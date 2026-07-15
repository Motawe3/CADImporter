using System.Collections.Generic;
using UnityEngine;

namespace CADImporter
{
    /// <summary>Length unit the source CAD file was authored in.</summary>
    public enum SourceUnit
    {
        Millimeters,
        Centimeters,
        Meters,
        Inches,
        Feet
    }

    /// <summary>Coordinate convention of the source file.</summary>
    public enum SourceOrientation
    {
        /// <summary>Z-up, right-handed. Most CAD packages (SolidWorks, FreeCAD, Inventor, CATIA).</summary>
        ZUpRightHanded,
        /// <summary>Y-up, right-handed. Common for OBJ exports (Blender, Maya).</summary>
        YUpRightHanded,
        /// <summary>Y-up, left-handed. Data is already in Unity's convention; no conversion.</summary>
        YUpLeftHanded
    }

    public static class CADUnits
    {
        public static float ToMeters(SourceUnit unit)
        {
            switch (unit)
            {
                case SourceUnit.Millimeters: return 0.001f;
                case SourceUnit.Centimeters: return 0.01f;
                case SourceUnit.Inches:      return 0.0254f;
                case SourceUnit.Feet:        return 0.3048f;
                default:                     return 1f;
            }
        }
    }

    /// <summary>
    /// Intermediate triangle-mesh representation shared by all parsers.
    /// Attribute arrays other than <see cref="Positions"/> are optional (null when absent).
    /// </summary>
    public sealed class CADMeshData
    {
        public string Name = "Mesh";
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Color32[] Colors;
        public Vector2[] UV;

        /// <summary>Triangle index lists, one per submesh.</summary>
        public int[][] Submeshes;

        /// <summary>Material name per submesh; null when the format carries no materials.</summary>
        public string[] SubmeshMaterials;

        public int VertexCount => Positions?.Length ?? 0;

        public int TriangleCount
        {
            get
            {
                if (Submeshes == null) return 0;
                int n = 0;
                for (int i = 0; i < Submeshes.Length; i++)
                    n += Submeshes[i].Length / 3;
                return n;
            }
        }

        public int[] CombinedIndices()
        {
            if (Submeshes == null) return new int[0];
            if (Submeshes.Length == 1) return Submeshes[0];
            int total = 0;
            for (int i = 0; i < Submeshes.Length; i++) total += Submeshes[i].Length;
            var all = new int[total];
            int off = 0;
            for (int i = 0; i < Submeshes.Length; i++)
            {
                System.Array.Copy(Submeshes[i], 0, all, off, Submeshes[i].Length);
                off += Submeshes[i].Length;
            }
            return all;
        }

        public static int[] SequentialIndices(int count)
        {
            var idx = new int[count];
            for (int i = 0; i < count; i++) idx[i] = i;
            return idx;
        }
    }

    /// <summary>A node in the imported part hierarchy (assembly / part / body).</summary>
    public sealed class CADNode
    {
        public string Name;
        public CADMeshData Mesh;
        public readonly List<CADNode> Children = new List<CADNode>();

        /// <summary>
        /// Local transform relative to the parent, applied to the built GameObject when
        /// <see cref="HasLocalTransform"/> is true. Formats that bake placement into vertices
        /// (STL, STEP, OBJ) leave this identity; scene formats with a real node graph (glTF)
        /// set it so pivots and articulation joints are preserved. <see cref="LocalPosition"/>
        /// is in the source's length unit and is scaled with the geometry at build time.
        /// </summary>
        public Vector3 LocalPosition = Vector3.zero;
        public Quaternion LocalRotation = Quaternion.identity;
        public Vector3 LocalScale = Vector3.one;
        public bool HasLocalTransform;

        /// <summary>BIM identity (IFC type, GlobalId, property sets); null on non-BIM formats.</summary>
        public IfcElementData Ifc;
    }

    /// <summary>How a material's alpha is interpreted (glTF alphaMode).</summary>
    public enum CADAlphaMode { Opaque, Mask, Blend }

    /// <summary>
    /// An encoded (PNG/JPEG) texture image carried through the parser as raw bytes.
    /// Decoding to a <c>Texture2D</c> is deferred to the builders, which run on the main
    /// thread — <see cref="UnityEngine.ImageConversion"/> is not thread-safe.
    /// </summary>
    public sealed class CADTextureImage
    {
        public string Name = "Texture";
        public byte[] EncodedBytes;
        /// <summary>UV set index the material samples this image with (only 0 is supported).</summary>
        public int TexCoord;
        /// <summary>True for non-color data (normal / metallic-roughness / occlusion maps).</summary>
        public bool Linear;
        /// <summary>Stable key for de-duplicating identical source images into one Texture2D.</summary>
        public string CacheKey;
    }

    /// <summary>
    /// Material description parsed from the source file. Colour + smoothness/metallic cover
    /// simple formats (OBJ .mtl, STEP); the texture and factor fields carry full glTF
    /// metallic-roughness PBR. Texture fields are null when the format has no maps.
    /// </summary>
    public sealed class CADMaterialInfo
    {
        public string Name;
        /// <summary>Base colour factor (RGBA). Multiplies <see cref="BaseColorTex"/> when present.</summary>
        public Color Color = new Color(0.75f, 0.75f, 0.78f, 1f);
        public float Smoothness = 0.4f;
        public float Metallic;

        // --- glTF metallic-roughness PBR (all optional) ---
        public CADTextureImage BaseColorTex;
        /// <summary>Packed map, glTF convention: G = roughness, B = metallic.</summary>
        public CADTextureImage MetallicRoughnessTex;
        public CADTextureImage NormalTex;
        public float NormalScale = 1f;
        /// <summary>Packed map, glTF convention: R = occlusion.</summary>
        public CADTextureImage OcclusionTex;
        public float OcclusionStrength = 1f;
        public Color EmissiveColor = Color.black;
        public CADTextureImage EmissiveTex;
        public CADAlphaMode AlphaMode = CADAlphaMode.Opaque;
        public float AlphaCutoff = 0.5f;
        public bool DoubleSided;
        /// <summary>KHR_materials_unlit — shade as flat base colour, ignore lighting.</summary>
        public bool Unlit;

        public bool HasTextures =>
            BaseColorTex != null || MetallicRoughnessTex != null || NormalTex != null ||
            OcclusionTex != null || EmissiveTex != null;
    }

    /// <summary>Root object produced by every parser.</summary>
    public sealed class CADModel
    {
        public string Name;
        public string SourcePath;
        public string Format;
        public readonly CADNode Root = new CADNode { Name = "Root" };
        public readonly List<CADMaterialInfo> Materials = new List<CADMaterialInfo>();

        public IEnumerable<CADNode> EnumerateNodes()
        {
            var stack = new Stack<CADNode>();
            stack.Push(Root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                for (int i = n.Children.Count - 1; i >= 0; i--)
                    stack.Push(n.Children[i]);
            }
        }

        public int TotalTriangles
        {
            get
            {
                int n = 0;
                foreach (var node in EnumerateNodes())
                    if (node.Mesh != null) n += node.Mesh.TriangleCount;
                return n;
            }
        }

        public int TotalVertices
        {
            get
            {
                int n = 0;
                foreach (var node in EnumerateNodes())
                    if (node.Mesh != null) n += node.Mesh.VertexCount;
                return n;
            }
        }
    }

    /// <summary>Geometry-processing options applied after parsing, before Unity meshes are built.</summary>
    public struct CADProcessOptions
    {
        public float Scale;
        public SourceOrientation Orientation;
        public bool Weld;
        public float WeldTolerance;
        public bool RecalculateNormals;
        public float SmoothingAngleDeg;

        public static CADProcessOptions Default => new CADProcessOptions
        {
            Scale = 0.001f,
            Orientation = SourceOrientation.ZUpRightHanded,
            Weld = true,
            WeldTolerance = 1e-5f,
            RecalculateNormals = false,
            SmoothingAngleDeg = 30f
        };
    }
}
