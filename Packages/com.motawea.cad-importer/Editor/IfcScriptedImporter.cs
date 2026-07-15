using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace CADImporter.Editor
{
    /// <summary>
    /// Imports IFC (BIM) files by tessellating them through the IfcOpenShell library bundled with a
    /// headless FreeCAD install (auto-detected; configurable in Tools → CAD Importer). The IFC
    /// spatial and aggregation tree is preserved: project → site → building → storey → element each
    /// becomes a child GameObject with its own local pivot, and every element keeps its IFC surface
    /// colour, so a building imports as a navigable, coloured hierarchy ready for realtime rendering.
    /// </summary>
    // .ifcxml is deliberately NOT registered: the IfcOpenShell build FreeCAD currently bundles
    // (0.8.x) stubs out its ifcXML parser ("IFC-XML import temporarily disabled"), so claiming
    // the extension would only manufacture guaranteed import errors.
    [ScriptedImporter(3, new[] { "ifc", "ifczip" })]
    public class IfcScriptedImporter : ScriptedImporter
    {
        public CADImportSettings settings = CreateDefaults();

        /// <summary>IFC geometry is emitted in metres, Z-up right-handed (IFC's convention).</summary>
        internal static CADImportSettings CreateDefaults()
        {
            return new CADImportSettings
            {
                sourceUnit = SourceUnit.Meters,
                sourceOrientation = SourceOrientation.ZUpRightHanded,
                // Buildings are mostly flat surfaces; a matte, non-metallic default reads best and
                // static batching is a big win for the many small elements a model contains.
                markStatic = true
            };
        }

        public override void OnImportAsset(AssetImportContext ctx)
        {
            string name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            var s = settings ?? CreateDefaults();

            string converter = StepConverter.ResolveConverter();
            if (converter == null)
            {
                ctx.LogImportWarning(
                    "CAD Importer: IFC import requires FreeCAD (its bundled IfcOpenShell does the " +
                    "tessellation). Install FreeCAD from https://www.freecad.org and, if it is not " +
                    "auto-detected, set the FreeCADCmd.exe path in Tools → CAD Importer. Then reimport.");
                CADAssetBuilder.BuildPlaceholder(ctx, name);
                return;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "UnityCADImporter", Guid.NewGuid().ToString("N"));
            try
            {
                string src = Path.GetFullPath(ctx.assetPath);
                if (!IfcConverter.ConvertToStl(converter, src, tempDir,
                        s.ifcLinearDeflection, s.ifcImportProperties, s.stepTimeoutSeconds,
                        Path.GetFileName(ctx.assetPath), out string error))
                {
                    ctx.LogImportError($"CAD Importer: IFC conversion of '{ctx.assetPath}' failed. {error}");
                    CADAssetBuilder.BuildPlaceholder(ctx, name);
                    return;
                }

                var parts = StepScriptedImporter.ParseConvertedParts(tempDir, Path.GetFileName(ctx.assetPath));

                var model = new CADModel { Name = name, Format = "IFC", SourcePath = ctx.assetPath };
                var palette = new HashSet<string>();

                string manifestPath = Path.Combine(tempDir, "manifest.json");
                var root = JNode.Parse(File.ReadAllText(manifestPath));

                // Surface the schema (IFC2X3 / IFC4 / IFC4X3...) — files of different vintages
                // tessellate and colour differently, so make the version easy to see when
                // comparing imports.
                string schema = root["schema"].AsString(null);
                if (!string.IsNullOrEmpty(schema))
                {
                    model.Format = $"IFC ({schema})";
                    Debug.Log($"CAD Importer: '{Path.GetFileName(ctx.assetPath)}' schema {schema}.");
                }

                foreach (var mn in root["nodes"].Items)
                {
                    var node = BuildIfcNode(mn, tempDir, parts, s.sourceOrientation, model, palette);
                    if (node != null) model.Root.Children.Add(node);
                }

                if (model.Root.Children.Count == 0)
                {
                    ctx.LogImportError($"CAD Importer: '{ctx.assetPath}' produced no geometry.");
                    CADAssetBuilder.BuildPlaceholder(ctx, name);
                    return;
                }

                CADAssetBuilder.Build(ctx, model, s);
            }
            catch (Exception e)
            {
                ctx.LogImportError($"CAD Importer: failed to import IFC '{ctx.assetPath}': {e.Message}");
                CADAssetBuilder.BuildPlaceholder(ctx, name);
            }
            finally
            {
                UnityEditor.EditorUtility.ClearProgressBar();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// Rebuilds a <see cref="CADNode"/> from a manifest node. IfcOpenShell placements are Z-up
        /// right-handed metres; they are converted to Unity with the same reflection the mesh
        /// pipeline applies (via <see cref="MeshProcessor.ConvertPlacement"/>) so transforms and
        /// geometry always agree. Each geometric element's IFC surface colour becomes a shared
        /// material (deduplicated by colour, so batching stays effective). Returns null for empty
        /// branches.
        /// </summary>
        static CADNode BuildIfcNode(JNode mn, string dir, Dictionary<string, CADModel> parts,
            SourceOrientation orientation, CADModel model, HashSet<string> palette)
        {
            var node = new CADNode { Name = mn["name"].AsString("Element"), Ifc = ParseIdentity(mn) };

            var pos = mn["pos"].AsFloatArray();
            var quat = mn["quat"].AsFloatArray();
            if (pos != null && pos.Length == 3 && quat != null && quat.Length == 4)
            {
                var p = new Vector3(pos[0], pos[1], pos[2]);
                var r = new Quaternion(quat[0], quat[1], quat[2], quat[3]);
                var sc = Vector3.one;
                MeshProcessor.ConvertPlacement(orientation, ref p, ref r, ref sc);
                node.LocalPosition = p;
                node.LocalRotation = r;
                node.LocalScale = sc;
                node.HasLocalTransform = true;
            }

            int mesh = mn["mesh"].AsInt(-1);
            if (mesh >= 0)
            {
                // Authored IFC surface style wins; elements with no style are coloured by category
                // from the professional default palette (so an unstyled model reads clearly).
                string matName;
                var col = mn["color"].AsFloatArray();
                if (col != null && col.Length >= 3)
                    matName = CadColorPalette.GetOrAdd(model, palette,
                        new Color(col[0], col[1], col[2], col.Length >= 4 ? col[3] : 1f));
                else
                {
                    // Colour by associated IFC material (glass -> translucent, steel -> metallic, …),
                    // falling back to the element's IFC category.
                    var fin = IfcMaterialPalette.ForElement(mn["ifcType"].AsString(null),
                        mn["material"].AsString(null));
                    matName = CadColorPalette.GetOrAdd(model, palette, fin.Color, fin.Smoothness, fin.Metallic);
                }

                string stl = Path.Combine(dir, mesh.ToString("D3") + ".stl");
                if (parts.TryGetValue(stl, out var part))
                {
                    var kids = part.Root.Children;
                    if (kids.Count == 1)
                    {
                        node.Mesh = kids[0].Mesh;
                        AssignMaterial(node.Mesh, matName);
                    }
                    else
                    {
                        foreach (var k in kids)
                        {
                            AssignMaterial(k.Mesh, matName);
                            node.Children.Add(new CADNode { Name = $"{node.Name}_{k.Name}", Mesh = k.Mesh });
                        }
                    }
                }
            }

            foreach (var child in mn["children"].Items)
            {
                var cn = BuildIfcNode(child, dir, parts, orientation, model, palette);
                if (cn != null) node.Children.Add(cn);
            }

            return node.Mesh == null && node.Children.Count == 0 ? null : node;
        }

        /// <summary>
        /// BIM identity from a manifest node — IFC type, GlobalId and (when exported) the
        /// flattened property sets. The asset builder turns this into an <see cref="IfcElement"/>
        /// component on the element's GameObject. Null when the node carries no identity.
        /// </summary>
        static IfcElementData ParseIdentity(JNode mn)
        {
            string ifcType = mn["ifcType"].AsString(null);
            string guid = mn["guid"].AsString(null);
            if (ifcType == null && guid == null) return null;

            var data = new IfcElementData { IfcType = ifcType, GlobalId = guid };
            var props = mn["props"];
            if (props.IsObject)
            {
                data.Properties = new List<IfcProperty>(props.Count);
                foreach (var kv in props.Members)
                {
                    string v = kv.Value.AsString(null);
                    if (v != null) data.Properties.Add(new IfcProperty(kv.Key, v));
                }
            }
            return data;
        }

        static void AssignMaterial(CADMeshData mesh, string matName)
        {
            if (mesh == null || matName == null || mesh.Submeshes == null) return;
            var mats = new string[mesh.Submeshes.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = matName;
            mesh.SubmeshMaterials = mats;
        }
    }
}
