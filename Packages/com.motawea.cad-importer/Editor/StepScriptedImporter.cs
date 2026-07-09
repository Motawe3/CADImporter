using System;
using System.IO;
using System.Linq;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace CADImporter.Editor
{
    /// <summary>
    /// Imports STEP/IGES B-rep files by tessellating them through a headless FreeCAD install
    /// (auto-detected; configurable in Tools → CAD Importer). The assembly tree is preserved:
    /// each container/part becomes a child GameObject with its own local pivot, so sub-assembly
    /// placements (e.g. robot joints) survive rather than being flattened to the origin.
    /// </summary>
    [ScriptedImporter(4, new[] { "step", "stp", "iges", "igs" })]
    public class StepScriptedImporter : ScriptedImporter
    {
        public CADImportSettings settings = new CADImportSettings();

        public override void OnImportAsset(AssetImportContext ctx)
        {
            string name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            var s = settings ?? new CADImportSettings();

            string converter = StepConverter.ResolveConverter();
            if (converter == null)
            {
                ctx.LogImportWarning(
                    "CAD Importer: STEP/IGES import requires FreeCAD (its bundled Open CASCADE kernel " +
                    "does the B-rep tessellation). Install FreeCAD from https://www.freecad.org and, if " +
                    "it is not auto-detected, set the FreeCADCmd.exe path in Tools → CAD Importer. " +
                    "Then reimport this asset.");
                CADAssetBuilder.BuildPlaceholder(ctx, name);
                return;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "UnityCADImporter", Guid.NewGuid().ToString("N"));
            try
            {
                string src = Path.GetFullPath(ctx.assetPath);
                if (!StepConverter.ConvertToStl(converter, src, tempDir,
                        s.stepLinearDeflection, s.stepAngularDeflection, s.stepTimeoutSeconds,
                        Path.GetFileName(ctx.assetPath), out string error))
                {
                    ctx.LogImportError($"CAD Importer: STEP conversion of '{ctx.assetPath}' failed. {error}");
                    CADAssetBuilder.BuildPlaceholder(ctx, name);
                    return;
                }

                var model = new CADModel { Name = name, Format = FormatOf(ctx.assetPath), SourcePath = ctx.assetPath };
                string manifestPath = Path.Combine(tempDir, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    var root = JNode.Parse(File.ReadAllText(manifestPath));
                    foreach (var mn in root["nodes"].Items)
                    {
                        var node = BuildStepNode(mn, tempDir, s.sourceOrientation);
                        if (node != null) model.Root.Children.Add(node);
                    }
                }
                else
                {
                    // Fallback (older converter output): flat, unnamed-index STL reassembly.
                    foreach (var stl in Directory.GetFiles(tempDir, "*.stl").OrderBy(f => f, StringComparer.Ordinal))
                    {
                        var part = StlParser.Parse(stl);
                        foreach (var child in part.Root.Children)
                            model.Root.Children.Add(child);
                    }
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
                ctx.LogImportError($"CAD Importer: failed to import '{ctx.assetPath}': {e.Message}");
                CADAssetBuilder.BuildPlaceholder(ctx, name);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        static string FormatOf(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".iges":
                case ".igs":
                    return "IGES";
                default:
                    return "STEP";
            }
        }

        /// <summary>
        /// Rebuilds a <see cref="CADNode"/> from a manifest node. The FreeCAD local placement is
        /// Z-up right-handed (millimetres); it is converted to Unity with the same reflection the
        /// mesh pipeline applies for <paramref name="orientation"/> (via
        /// <see cref="MeshProcessor.ConvertPlacement"/>), so transforms and geometry always agree
        /// regardless of the chosen orientation. Position is scaled with the geometry by the
        /// builder. Returns null for empty branches.
        /// </summary>
        static CADNode BuildStepNode(JNode mn, string dir, SourceOrientation orientation)
        {
            var node = new CADNode { Name = mn["name"].AsString("Part") };

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
                string stl = Path.Combine(dir, mesh.ToString("D3") + ".stl");
                if (File.Exists(stl))
                {
                    var part = StlParser.Parse(stl);
                    var kids = part.Root.Children;
                    if (kids.Count == 1)
                        node.Mesh = kids[0].Mesh;
                    else
                        foreach (var k in kids)
                            node.Children.Add(new CADNode { Name = $"{node.Name}_{k.Name}", Mesh = k.Mesh });
                }
            }

            foreach (var child in mn["children"].Items)
            {
                var cn = BuildStepNode(child, dir, orientation);
                if (cn != null) node.Children.Add(cn);
            }

            return node.Mesh == null && node.Children.Count == 0 ? null : node;
        }
    }
}
