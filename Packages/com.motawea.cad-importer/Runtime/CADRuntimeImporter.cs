using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace CADImporter
{
    /// <summary>Options for runtime CAD loading.</summary>
    [Serializable]
    public class CADRuntimeImportSettings
    {
        public SourceUnit sourceUnit = SourceUnit.Millimeters;
        public SourceOrientation sourceOrientation = SourceOrientation.ZUpRightHanded;
        public float additionalScale = 1f;

        public bool weldVertices = true;
        public float weldTolerance = 1e-5f;
        public bool forceRecalculateNormals;
        [Range(0f, 180f)] public float smoothingAngle = 30f;

        public bool generateColliders = true;
        /// <summary>Fraction of source triangles kept in collision meshes (1 = full mesh).</summary>
        [Range(0.02f, 1f)] public float colliderQuality = 0.25f;
        public bool convexColliders;

        /// <summary>Optional material override; a default lit material is created when null.</summary>
        public Material material;

        public CADProcessOptions ToProcessOptions() => new CADProcessOptions
        {
            Scale = CADUnits.ToMeters(sourceUnit) * additionalScale,
            Orientation = sourceOrientation,
            Weld = weldVertices,
            WeldTolerance = weldTolerance,
            RecalculateNormals = forceRecalculateNormals,
            SmoothingAngleDeg = smoothingAngle
        };
    }

    /// <summary>
    /// Runtime CAD import for digital twins: load STL / PLY / OBJ from disk in builds.
    /// <see cref="ImportAsync"/> parses and processes geometry on a worker thread and only
    /// touches the Unity API on the main thread, keeping the simulation responsive.
    /// </summary>
    public static class CADRuntimeImporter
    {
        static Material s_defaultMaterial;

        public static bool IsSupported(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".stl":
                case ".ply":
                case ".obj":
                case ".gltf":
                case ".glb":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Synchronous import. Blocks the calling thread; prefer ImportAsync in play mode.</summary>
        public static GameObject Import(string path, CADRuntimeImportSettings settings = null, Transform parent = null)
        {
            settings ??= new CADRuntimeImportSettings();
            var model = ParseAndProcess(path, settings);
            return Build(model, settings, parent);
        }

        /// <summary>Parses and processes off the main thread, then builds the GameObject hierarchy.</summary>
        public static async Task<GameObject> ImportAsync(string path, CADRuntimeImportSettings settings = null, Transform parent = null)
        {
            settings ??= new CADRuntimeImportSettings();
            var model = await Task.Run(() => ParseAndProcess(path, settings));
            return Build(model, settings, parent);
        }

        static CADModel ParseAndProcess(string path, CADRuntimeImportSettings settings)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("CAD file not found.", path);

            CADModel model;
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".stl": model = StlParser.Parse(path); break;
                case ".ply": model = PlyParser.Parse(path); break;
                case ".obj": model = ObjParser.Parse(path); break;
                case ".gltf":
                case ".glb": model = GltfParser.Parse(path); break;
                default:
                    throw new NotSupportedException(
                        $"Runtime import supports .stl/.ply/.obj/.gltf/.glb — got '{Path.GetExtension(path)}'. " +
                        "Convert STEP/IGES in the editor (or offline) first.");
            }

            MeshProcessor.Process(model, settings.ToProcessOptions());
            return model;
        }

        static GameObject Build(CADModel model, CADRuntimeImportSettings settings, Transform parent)
        {
            var root = new GameObject(model.Name);
            if (parent != null) root.transform.SetParent(parent, false);

            var texCache = new Dictionary<string, Texture2D>();
            var materialLookup = new Dictionary<string, Material>();
            foreach (var m in model.Materials)
                if (!string.IsNullOrEmpty(m.Name) && !materialLookup.ContainsKey(m.Name))
                    materialLookup[m.Name] = CadMaterialFactory.Create(m, false, DefaultColor, texCache).Material;

            int totalV = 0, totalT = 0, parts = 0;
            foreach (var child in model.Root.Children)
                BuildNode(child, root.transform, settings, materialLookup, ref totalV, ref totalT, ref parts);

            var info = root.AddComponent<CADModelInfo>();
            info.sourceFile = model.SourcePath ?? model.Name;
            info.sourceFormat = model.Format;
            info.sourceUnit = settings.sourceUnit;
            info.appliedScale = CADUnits.ToMeters(settings.sourceUnit) * settings.additionalScale;
            info.totalVertices = totalV;
            info.totalTriangles = totalT;
            info.partCount = parts;
            info.importedAt = DateTime.UtcNow.ToString("u");
            return root;
        }

        static void BuildNode(CADNode node, Transform parent, CADRuntimeImportSettings settings,
            Dictionary<string, Material> materials, ref int totalV, ref int totalT, ref int parts)
        {
            var go = new GameObject(string.IsNullOrEmpty(node.Name) ? "Part" : node.Name);
            go.transform.SetParent(parent, false);

            var data = node.Mesh;
            if (data != null && data.TriangleCount > 0)
            {
                parts++;
                var mesh = UnityMeshBuilder.Build(data);
                totalV += mesh.vertexCount;
                totalT += data.TriangleCount;

                var filter = go.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = ResolveMaterials(data, settings, materials);

                if (settings.generateColliders)
                {
                    CADMeshData colData = data;
                    if (settings.colliderQuality < 0.999f && data.TriangleCount > 128)
                    {
                        // Position-only weld: merges shading splits back into connected
                        // topology so decimation doesn't erode hard edges into holes.
                        var welded = MeshProcessor.PositionWeldedCopy(
                            data, Mathf.Max(settings.weldTolerance, 1e-9f));
                        colData = MeshDecimator.Decimate(welded, settings.colliderQuality);
                    }
                    if (colData.TriangleCount > 0)
                    {
                        var colMesh = UnityMeshBuilder.BuildCollision(colData, data.Name + "_collision");
                        var mc = go.AddComponent<MeshCollider>();
                        mc.convex = settings.convexColliders;
                        mc.sharedMesh = colMesh;
                    }
                }
            }

            foreach (var child in node.Children)
                BuildNode(child, go.transform, settings, materials, ref totalV, ref totalT, ref parts);
        }

        static Material[] ResolveMaterials(CADMeshData data, CADRuntimeImportSettings settings,
            Dictionary<string, Material> materials)
        {
            var result = new Material[data.Submeshes.Length];
            for (int i = 0; i < result.Length; i++)
            {
                Material m = settings.material;
                if (m == null && data.SubmeshMaterials != null && i < data.SubmeshMaterials.Length &&
                    !string.IsNullOrEmpty(data.SubmeshMaterials[i]) &&
                    materials.TryGetValue(data.SubmeshMaterials[i], out var found))
                    m = found;
                result[i] = m != null ? m : DefaultMaterial();
            }
            return result;
        }

        // Note: for builds, ensure the URP Lit shader is referenced by some asset or listed in
        // "Always Included Shaders", otherwise the factory's Shader.Find returns null.
        static readonly Color DefaultColor = new Color(0.75f, 0.75f, 0.78f);

        static Material DefaultMaterial()
        {
            if (s_defaultMaterial == null)
                s_defaultMaterial = CadMaterialFactory.Create(null, false, DefaultColor, null).Material;
            return s_defaultMaterial;
        }
    }
}
