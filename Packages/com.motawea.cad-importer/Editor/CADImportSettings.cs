using System;
using UnityEngine;

namespace CADImporter.Editor
{
    public enum ColliderMode
    {
        /// <summary>No colliders — attach your own.</summary>
        None,
        /// <summary>Axis-aligned box per part. Cheapest; good for coarse interaction.</summary>
        Box,
        /// <summary>Convex mesh collider per part (usable on dynamic rigidbodies, e.g. robot links).</summary>
        ConvexMesh,
        /// <summary>Non-convex mesh collider using a decimated collision mesh. Best accuracy/perf trade-off for static geometry.</summary>
        SimplifiedMesh,
        /// <summary>Non-convex mesh collider on the full-resolution mesh. Most accurate, most expensive.</summary>
        FullMesh
    }

    public enum MaterialMode
    {
        /// <summary>URP Lit (falls back to Standard). CAD colors from OBJ/MTL are applied when present.</summary>
        Lit,
        /// <summary>Unlit vertex-color shader — for PLY/scan data carrying per-vertex colors.</summary>
        VertexColorUnlit
    }

    /// <summary>
    /// Import settings shared by all CAD ScriptedImporters and the batch-import window.
    /// Serialized on each imported asset, so per-file overrides survive reimports.
    /// </summary>
    [Serializable]
    public class CADImportSettings
    {
        [Header("Units & Orientation")]
        [Tooltip("Unit the source file was authored in. Most CAD exports use millimeters.")]
        public SourceUnit sourceUnit = SourceUnit.Millimeters;
        [Tooltip("Source coordinate convention. Most CAD packages are Z-up right-handed.")]
        public SourceOrientation sourceOrientation = SourceOrientation.ZUpRightHanded;
        [Tooltip("Extra uniform scale applied on top of the unit conversion.")]
        public float additionalScale = 1f;

        [Header("Mesh Processing")]
        [Tooltip("Merge coincident vertices. Drastically reduces STL memory and enables smooth shading and LOD generation.")]
        public bool weldVertices = true;
        [Tooltip("Weld distance in meters (applied after unit scaling).")]
        public float weldTolerance = 1e-5f;
        [Tooltip("Recompute normals even when the source file provides them.")]
        public bool forceRecalculateNormals;
        [Tooltip("Edges sharper than this angle stay hard; flatter edges are smoothed."), Range(0f, 180f)]
        public float smoothingAngle = 30f;

        [Header("Level of Detail")]
        [Tooltip("Generate a LODGroup with decimated meshes per part.")]
        public bool generateLODs = true;
        [Tooltip("Triangle fraction kept at each additional LOD level.")]
        public float[] lodQualities = { 0.5f, 0.15f };
        [Tooltip("Parts below this triangle count skip LOD generation.")]
        public int lodMinTriangles = 512;
        [Tooltip("Screen height at which LOD0 hands over to LOD1."), Range(0.05f, 0.9f)]
        public float lodFirstTransition = 0.35f;
        [Tooltip("Screen height below which the part is culled entirely."), Range(0f, 0.2f)]
        public float lodCullHeight = 0.015f;

        [Header("Physics")]
        public ColliderMode colliderMode = ColliderMode.SimplifiedMesh;
        [Tooltip("Triangle fraction kept in generated collision meshes."), Range(0.02f, 1f)]
        public float colliderQuality = 0.25f;

        [Header("Rendering")]
        public MaterialMode materialMode = MaterialMode.Lit;
        public Color defaultColor = new Color(0.75f, 0.75f, 0.78f, 1f);
        [Tooltip("Keep mesh data readable by scripts at runtime (costs extra memory).")]
        public bool readWriteMeshes;
        [Tooltip("Mark imported objects static (batching, occlusion). Leave off for articulated robots.")]
        public bool markStatic;
        [Tooltip("Generate lightmap UVs for LOD0 meshes (slow on large models).")]
        public bool generateLightmapUVs;

        [Header("STEP / IGES (requires FreeCAD)")]
        [Tooltip("Tessellation chord tolerance, relative to shape size. Lower = finer mesh.")]
        public float stepLinearDeflection = 0.1f;
        [Tooltip("Tessellation angular tolerance in degrees. Lower = finer mesh."), Range(5f, 45f)]
        public float stepAngularDeflection = 20f;
        [Tooltip("Give up on FreeCAD conversion (STEP/IGES/IFC) after this many seconds. Large " +
                 "assemblies and buildings (hundreds of parts / 100+ MB) can take several minutes. " +
                 "Set 0 for no limit. Progress is logged to the Console so you can tell a slow job " +
                 "from a hung one.")]
        public int stepTimeoutSeconds = 900;

        [Header("IFC / BIM (requires FreeCAD's IfcOpenShell)")]
        [Tooltip("IFC tessellation chord tolerance in metres (absolute). Lower = finer curved " +
                 "surfaces and more triangles. 0.01 (1 cm) suits most building models.")]
        public float ifcLinearDeflection = 0.01f;
        [Tooltip("Attach an IfcElement component to every imported element, carrying its IFC " +
                 "property sets (Psets/quantities) for digital-twin and BIM tooling. The IFC type " +
                 "and GlobalId are always kept; this adds the full property data, which grows " +
                 "import time and asset size on very large models.")]
        public bool ifcImportProperties = true;
        [Tooltip("Import IfcSpace volumes (rooms/zones) as translucent geometry. Useful for " +
                 "space-analysis workflows; turn off to skip them entirely, as most viewers do.")]
        public bool ifcImportSpaces = true;

        public CADProcessOptions ToProcessOptions() => new CADProcessOptions
        {
            Scale = CADUnits.ToMeters(sourceUnit) * additionalScale,
            Orientation = sourceOrientation,
            Weld = weldVertices,
            WeldTolerance = weldTolerance,
            RecalculateNormals = forceRecalculateNormals,
            SmoothingAngleDeg = smoothingAngle
        };

        public CADImportSettings Clone() => JsonUtility.FromJson<CADImportSettings>(JsonUtility.ToJson(this));
    }
}
