using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace CADImporter.Editor
{
    /// <summary>
    /// Turns a parsed <see cref="CADModel"/> into an imported prefab: GameObject hierarchy,
    /// mesh/material sub-assets, LODGroups, physics colliders and metadata. Shared by all
    /// CAD ScriptedImporters.
    /// </summary>
    internal static class CADAssetBuilder
    {
        sealed class Context
        {
            public AssetImportContext Ctx;
            public CADImportSettings Settings;
            public Dictionary<string, Material> Materials;
            public Dictionary<string, Texture2D> TexCache;
            public Material DefaultMaterial;
            public float Scale;
            public int AssetId;
            public int TotalVertices, TotalTriangles, PartCount;
        }

        public static void Build(AssetImportContext ctx, CADModel model, CADImportSettings settings)
        {
            MeshProcessor.Process(model, settings.ToProcessOptions());

            var c = new Context
            {
                Ctx = ctx,
                Settings = settings,
                Materials = new Dictionary<string, Material>(),
                TexCache = new Dictionary<string, Texture2D>(),
                Scale = CADUnits.ToMeters(settings.sourceUnit) * settings.additionalScale
            };

            bool vertexColorUnlit = settings.materialMode == MaterialMode.VertexColorUnlit;
            c.DefaultMaterial = BuildMaterial(c, null, vertexColorUnlit, settings.defaultColor);
            foreach (var m in model.Materials)
                if (!string.IsNullOrEmpty(m.Name) && !c.Materials.ContainsKey(m.Name))
                    c.Materials[m.Name] = BuildMaterial(c, m, vertexColorUnlit, settings.defaultColor);

            var root = new GameObject(model.Name);
            foreach (var child in model.Root.Children)
                BuildNode(c, child, root.transform);

            var info = root.AddComponent<CADModelInfo>();
            info.sourceFile = System.IO.Path.GetFileName(ctx.assetPath);
            info.sourceFormat = model.Format;
            info.sourceUnit = settings.sourceUnit;
            info.appliedScale = CADUnits.ToMeters(settings.sourceUnit) * settings.additionalScale;
            info.totalVertices = c.TotalVertices;
            info.totalTriangles = c.TotalTriangles;
            info.partCount = c.PartCount;
            info.importedAt = DateTime.UtcNow.ToString("u");

            if (settings.markStatic)
                SetStaticRecursively(root);

            ctx.AddObjectToAsset("root", root);
            ctx.SetMainObject(root);
        }

        /// <summary>Creates a valid (empty) asset when import fails, so the error is visible in-project.</summary>
        public static void BuildPlaceholder(AssetImportContext ctx, string name)
        {
            var root = new GameObject(name);
            root.AddComponent<CADModelInfo>().sourceFile = System.IO.Path.GetFileName(ctx.assetPath);
            ctx.AddObjectToAsset("root", root);
            ctx.SetMainObject(root);
        }

        static void BuildNode(Context c, CADNode node, Transform parent)
        {
            // Sibling names must be unique: Unity derives prefab-internal file IDs from the
            // hierarchy path, and repeated CAD part labels (e.g. four identical screws)
            // would otherwise trigger "Identifier uniqueness violation" and break re-linking.
            var go = new GameObject(UniqueSiblingName(parent,
                string.IsNullOrEmpty(node.Name) ? "Part" : Sanitize(node.Name)));
            go.transform.SetParent(parent, false);

            // Scene formats (glTF) carry a real node graph; apply each node's local transform so
            // pivots and articulation joints are preserved. LocalPosition is scaled with the
            // geometry (which MeshProcessor already scaled) to keep the model uniformly sized.
            if (node.HasLocalTransform)
            {
                go.transform.localPosition = node.LocalPosition * c.Scale;
                go.transform.localRotation = node.LocalRotation;
                go.transform.localScale = node.LocalScale;
            }

            var data = node.Mesh;
            if (data != null && data.TriangleCount > 0)
            {
                c.PartCount++;
                var s = c.Settings;

                var mesh0 = UnityMeshBuilder.Build(data);
                mesh0.name = go.name;
                if (s.generateLightmapUVs)
                    Unwrapping.GenerateSecondaryUVSet(mesh0);
                AddAsset(c, mesh0);
                c.TotalVertices += mesh0.vertexCount;
                c.TotalTriangles += data.TriangleCount;

                var materials = ResolveMaterials(c, data);

                // Decimation and collision need connected topology. The render mesh has
                // vertices split along hard edges for shading, which the decimator would
                // see as open borders (tearing LODs apart) — so rebuild a position-only
                // welded copy and decimate that instead.
                CADMeshData welded = null;
                CADMeshData Welded() =>
                    welded ??= MeshProcessor.PositionWeldedCopy(data, Mathf.Max(s.weldTolerance, 1e-9f));

                bool useLods = s.generateLODs
                    && s.lodQualities != null && s.lodQualities.Length > 0
                    && data.TriangleCount >= s.lodMinTriangles;

                if (useLods)
                    BuildLodChain(c, go, data, mesh0, materials, Welded());
                else
                    AttachRenderer(go, mesh0, materials);

                BuildCollider(c, go, data, mesh0, Welded);

                // FullMesh collision reuses mesh0, which must then stay readable for cooking.
                if (!s.readWriteMeshes && s.colliderMode != ColliderMode.FullMesh)
                    mesh0.UploadMeshData(true);
            }

            foreach (var child in node.Children)
                BuildNode(c, child, go.transform);
        }

        static void BuildLodChain(Context c, GameObject go, CADMeshData data, Mesh mesh0,
            Material[] materials, CADMeshData welded)
        {
            var s = c.Settings;
            var lods = new List<LOD>();

            var r0 = CreateRendererChild(go, go.name + "_LOD0", mesh0, materials);
            lods.Add(new LOD(0f, new[] { r0 }));

            int previousTris = data.TriangleCount;
            for (int i = 0; i < s.lodQualities.Length; i++)
            {
                float quality = Mathf.Clamp(s.lodQualities[i], 0.01f, 0.95f);
                var decimated = MeshDecimator.Decimate(welded, quality);
                int tris = decimated.TriangleCount;
                if (tris < 8 || tris >= previousTris) break;
                previousTris = tris;

                MeshProcessor.RecalculateSmoothNormals(decimated,
                    Mathf.Max(s.weldTolerance, 1e-9f), s.smoothingAngle);
                var lodMesh = UnityMeshBuilder.Build(decimated);
                lodMesh.name = $"{go.name}_LOD{i + 1}";
                AddAsset(c, lodMesh);
                if (!s.readWriteMeshes)
                    lodMesh.UploadMeshData(true);

                var r = CreateRendererChild(go, lodMesh.name, lodMesh, ResolveMaterials(c, decimated));
                lods.Add(new LOD(0f, new[] { r }));
            }

            // Screen-relative transition heights, geometric from first transition down to cull.
            int levels = lods.Count;
            var arr = lods.ToArray();
            float cull = Mathf.Max(s.lodCullHeight, 1e-4f);
            float first = Mathf.Max(s.lodFirstTransition, cull * 2f); // keep heights descending
            for (int i = 0; i < levels; i++)
            {
                float t = levels > 1 ? (float)i / (levels - 1) : 1f;
                float h = first * Mathf.Pow(cull / first, t);
                arr[i].screenRelativeTransitionHeight = i == levels - 1 ? s.lodCullHeight : h;
            }

            var lodGroup = go.AddComponent<LODGroup>();
            lodGroup.SetLODs(arr);
            lodGroup.RecalculateBounds();
        }

        static void BuildCollider(Context c, GameObject go, CADMeshData data, Mesh mesh0,
            Func<CADMeshData> welded)
        {
            var s = c.Settings;
            switch (s.colliderMode)
            {
                case ColliderMode.None:
                    return;

                case ColliderMode.Box:
                {
                    var box = go.AddComponent<BoxCollider>();
                    box.center = mesh0.bounds.center;
                    box.size = mesh0.bounds.size;
                    return;
                }

                case ColliderMode.FullMesh:
                {
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = mesh0;
                    return;
                }

                case ColliderMode.ConvexMesh:
                case ColliderMode.SimplifiedMesh:
                {
                    CADMeshData colData = welded();
                    if (s.colliderQuality < 0.999f && colData.TriangleCount > 128)
                        colData = MeshDecimator.Decimate(colData, s.colliderQuality);
                    if (colData.TriangleCount < 4) colData = welded();

                    var colMesh = UnityMeshBuilder.BuildCollision(colData, go.name + "_collision");
                    AddAsset(c, colMesh);

                    var mc = go.AddComponent<MeshCollider>();
                    mc.convex = s.colliderMode == ColliderMode.ConvexMesh;
                    mc.sharedMesh = colMesh;
                    return;
                }
            }
        }

        static Renderer CreateRendererChild(GameObject parent, string name, Mesh mesh, Material[] materials)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            return renderer;
        }

        static void AttachRenderer(GameObject go, Mesh mesh, Material[] materials)
        {
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
        }

        static Material[] ResolveMaterials(Context c, CADMeshData data)
        {
            var result = new Material[Mathf.Max(1, data.Submeshes.Length)];
            for (int i = 0; i < result.Length; i++)
            {
                Material m = null;
                if (data.SubmeshMaterials != null && i < data.SubmeshMaterials.Length &&
                    !string.IsNullOrEmpty(data.SubmeshMaterials[i]))
                    c.Materials.TryGetValue(data.SubmeshMaterials[i], out m);
                result[i] = m != null ? m : c.DefaultMaterial;
            }
            return result;
        }

        static Material BuildMaterial(Context c, CADMaterialInfo info, bool vertexColorUnlit, Color fallback)
        {
            var res = CadMaterialFactory.Create(info, vertexColorUnlit, fallback, c.TexCache);
            foreach (var tex in res.CreatedTextures) AddAsset(c, tex);
            AddAsset(c, res.Material);
            return res.Material;
        }

        static void AddAsset(Context c, UnityEngine.Object obj)
        {
            c.Ctx.AddObjectToAsset($"{obj.GetType().Name}_{c.AssetId++}_{obj.name}", obj);
        }

        static void SetStaticRecursively(GameObject root)
        {
            const StaticEditorFlags flags =
                StaticEditorFlags.BatchingStatic |
                StaticEditorFlags.OccluderStatic |
                StaticEditorFlags.OccludeeStatic |
                StaticEditorFlags.ReflectionProbeStatic;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);
        }

        static string UniqueSiblingName(Transform parent, string baseName)
        {
            if (parent.Find(baseName) == null) return baseName;
            for (int i = 1; ; i++)
            {
                string candidate = $"{baseName} ({i})";
                if (parent.Find(candidate) == null) return candidate;
            }
        }

        static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Part";
            foreach (char bad in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');
            return name;
        }
    }
}
