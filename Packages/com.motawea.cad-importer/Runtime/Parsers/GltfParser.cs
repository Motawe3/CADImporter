using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace CADImporter
{
    /// <summary>
    /// Pure-C# glTF 2.0 / GLB parser producing a <see cref="CADModel"/>. Reads the binary GLB
    /// container and text .gltf (external, embedded and base64 data-URI buffers/images), decodes
    /// accessors (every component type, normalized integers, interleaved byteStride and sparse
    /// substitution), bakes the node-graph transforms into vertex data, and extracts metallic-
    /// roughness PBR materials with their textures as raw bytes.
    ///
    /// Geometry is emitted in glTF's native space (right-handed, Y-up, metres); the shared
    /// <see cref="MeshProcessor"/> converts it to Unity's convention via
    /// <see cref="SourceOrientation.YUpRightHanded"/>, so no coordinate maths lives here.
    ///
    /// Geometry-compression extensions (KHR_draco_mesh_compression, EXT_meshopt_compression) and
    /// KTX2/basisu textures are not decoded; a file that *requires* them fails with a clear
    /// message, and optional KTX2 textures are skipped (the material keeps its colour factors).
    /// </summary>
    public static class GltfParser
    {
        // glTF component types.
        const int BYTE = 5120, UBYTE = 5121, SHORT = 5122, USHORT = 5123, UINT = 5125, FLOAT = 5126;
        // Primitive modes.
        const int MODE_TRIANGLES = 4, MODE_TRIANGLE_STRIP = 5, MODE_TRIANGLE_FAN = 6;

        public static CADModel Parse(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var model = Parse(bytes, Path.GetFileNameWithoutExtension(path), Path.GetDirectoryName(path));
            model.SourcePath = path;
            return model;
        }

        /// <summary>
        /// Returns the external file URIs (relative to the .gltf) that a text glTF references
        /// for its buffers and images — i.e. the sidecar files that must travel with it (a
        /// typical "scene.gltf + scene.bin + textures/*" export). Data-URI resources and GLB
        /// files (which embed everything) return an empty list. Used by the batch importer to
        /// copy dependencies into the project alongside the .gltf.
        /// </summary>
        public static List<string> GetExternalResources(string gltfPath)
        {
            var result = new List<string>();
            byte[] bytes = File.ReadAllBytes(gltfPath);
            // GLB embeds its buffers/images; nothing external to copy.
            if (bytes.Length >= 4 && bytes[0] == 0x67 && bytes[1] == 0x6C && bytes[2] == 0x54 && bytes[3] == 0x46)
                return result;

            int off = (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            JNode root;
            try { root = JNode.Parse(Encoding.UTF8.GetString(bytes, off, bytes.Length - off)); }
            catch { return result; }

            void Collect(JNode arr)
            {
                foreach (var item in arr.Items)
                {
                    string uri = item["uri"].AsString();
                    if (string.IsNullOrEmpty(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string rel = Uri.UnescapeDataString(uri);
                    if (!result.Contains(rel)) result.Add(rel);
                }
            }
            Collect(root["buffers"]);
            Collect(root["images"]);
            return result;
        }

        public static CADModel Parse(byte[] fileBytes, string name, string baseDir)
        {
            var impl = new Impl(baseDir);
            return impl.Run(fileBytes, name);
        }

        sealed class Impl
        {
            readonly string _baseDir;
            JNode _root;
            byte[][] _buffers;         // resolved buffer bytes by index
            byte[] _glbBin;            // GLB binary chunk (buffer 0 when it has no uri)
            readonly Dictionary<int, CADMaterialInfo> _materials = new Dictionary<int, CADMaterialInfo>();
            readonly Dictionary<int, CADTextureImage> _imageCache = new Dictionary<int, CADTextureImage>();
            readonly HashSet<int> _visiting = new HashSet<int>();
            CADModel _model;
            bool _isGlb;

            public Impl(string baseDir) { _baseDir = baseDir; }

            public CADModel Run(byte[] fileBytes, string name)
            {
                string jsonText = ExtractJson(fileBytes);
                _root = JNode.Parse(jsonText);

                string version = _root["asset"]["version"].AsString("");
                if (!version.StartsWith("2"))
                    throw new InvalidDataException(
                        $"Unsupported glTF asset version '{version}'. This importer supports glTF 2.0.");

                RejectUnsupportedRequiredExtensions();

                _model = new CADModel { Name = name, Format = _isGlb ? "GLB" : "glTF" };

                ResolveBuffers();

                // Choose the scene to instantiate: the document default, else scene 0, else all nodes.
                var sceneNodes = new List<int>();
                int defaultScene = _root.Has("scene") ? _root["scene"].AsInt() : 0;
                var scenes = _root["scenes"];
                if (scenes.IsArray && scenes.Count > 0)
                {
                    int si = Mathf.Clamp(defaultScene, 0, scenes.Count - 1);
                    foreach (var n in scenes[si]["nodes"].Items) sceneNodes.Add(n.AsInt());
                }
                else
                {
                    for (int i = 0; i < _root["nodes"].Count; i++) sceneNodes.Add(i);
                }

                var identity = Mat4.Identity();
                foreach (int ni in sceneNodes)
                {
                    var child = VisitNode(ni, identity);
                    if (child != null) _model.Root.Children.Add(child);
                }

                if (_model.Root.Children.Count == 0)
                    throw new InvalidDataException("glTF file contains no renderable geometry.");
                return _model;
            }

            // --- container -----------------------------------------------------------------

            string ExtractJson(byte[] b)
            {
                // GLB: 12-byte header (magic 'glTF', version, length) then length-prefixed chunks.
                if (b.Length >= 12 && b[0] == 0x67 && b[1] == 0x6C && b[2] == 0x54 && b[3] == 0x46)
                {
                    _isGlb = true;
                    uint glbVersion = ReadU32(b, 4);
                    if (glbVersion != 2)
                        throw new InvalidDataException($"Unsupported GLB container version {glbVersion}.");
                    uint total = ReadU32(b, 8);
                    int len = (int)Math.Min(total, (uint)b.Length);

                    string json = null;
                    int p = 12;
                    while (p + 8 <= len)
                    {
                        uint chunkLen = ReadU32(b, p);
                        uint chunkType = ReadU32(b, p + 4);
                        int dataStart = p + 8;
                        if (dataStart + (int)chunkLen > b.Length)
                            throw new InvalidDataException("Corrupt GLB: chunk exceeds file length.");
                        if (chunkType == 0x4E4F534A)        // 'JSON'
                            json = Encoding.UTF8.GetString(b, dataStart, (int)chunkLen);
                        else if (chunkType == 0x004E4942)   // 'BIN\0'
                        {
                            _glbBin = new byte[chunkLen];
                            Array.Copy(b, dataStart, _glbBin, 0, (int)chunkLen);
                        }
                        p = dataStart + (int)chunkLen;
                        p = (p + 3) & ~3;                    // chunks are 4-byte aligned
                    }
                    if (json == null) throw new InvalidDataException("GLB file has no JSON chunk.");
                    return json;
                }

                // Text .gltf — strip a UTF-8 BOM if present.
                int off = (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) ? 3 : 0;
                return Encoding.UTF8.GetString(b, off, b.Length - off);
            }

            void RejectUnsupportedRequiredExtensions()
            {
                var known = new HashSet<string> { "KHR_texture_transform" }; // tolerated (ignored)
                foreach (var e in _root["extensionsRequired"].Items)
                {
                    string ext = e.AsString("");
                    if (ext == "KHR_draco_mesh_compression" || ext == "EXT_meshopt_compression")
                        throw new InvalidDataException(
                            $"This glTF requires '{ext}' (mesh compression), which is not supported. " +
                            "Re-export without Draco/meshopt compression.");
                    if (ext == "KHR_texture_basisu")
                        throw new InvalidDataException(
                            "This glTF requires KTX2/basisu textures, which are not supported. " +
                            "Re-export with PNG/JPEG textures.");
                    // Other required extensions we don't recognise: fail rather than mis-render.
                    if (!known.Contains(ext) && ext != "KHR_materials_unlit" &&
                        ext != "KHR_materials_emissive_strength")
                        throw new InvalidDataException(
                            $"This glTF requires unsupported extension '{ext}'.");
                }
            }

            void ResolveBuffers()
            {
                var buffers = _root["buffers"];
                _buffers = new byte[buffers.Count][];
                for (int i = 0; i < buffers.Count; i++)
                {
                    var buf = buffers[i];
                    string uri = buf["uri"].AsString();
                    if (string.IsNullOrEmpty(uri))
                    {
                        // No URI => the GLB binary chunk (only valid for buffer 0).
                        _buffers[i] = _glbBin ?? throw new InvalidDataException(
                            $"Buffer {i} has no URI and there is no GLB binary chunk.");
                    }
                    else if (IsDataUri(uri))
                    {
                        _buffers[i] = DecodeDataUri(uri);
                    }
                    else
                    {
                        string file = Path.Combine(_baseDir ?? "", Uri.UnescapeDataString(uri));
                        if (!File.Exists(file))
                            throw new FileNotFoundException($"glTF external buffer not found: {uri}", file);
                        _buffers[i] = File.ReadAllBytes(file);
                    }
                }
            }

            // --- node graph ----------------------------------------------------------------

            CADNode VisitNode(int nodeIndex, Mat4 parentWorld)
            {
                var nodes = _root["nodes"];
                if (nodeIndex < 0 || nodeIndex >= nodes.Count) return null;
                if (!_visiting.Add(nodeIndex)) return null; // guard against malformed cycles

                try
                {
                    var node = nodes[nodeIndex];
                    Mat4 world = parentWorld.Mul(LocalMatrix(node));
                    string nodeName = node["name"].AsString($"node{nodeIndex}");
                    var cad = new CADNode { Name = nodeName };

                    if (node.Has("mesh"))
                    {
                        var prims = BuildMesh(node["mesh"].AsInt(), world);
                        if (prims.Count == 1)
                            cad.Mesh = prims[0];
                        else
                            for (int k = 0; k < prims.Count; k++)
                                cad.Children.Add(new CADNode { Name = $"{nodeName}_prim{k}", Mesh = prims[k] });
                    }

                    foreach (var childIdx in node["children"].Items)
                    {
                        var childNode = VisitNode(childIdx.AsInt(), world);
                        if (childNode != null) cad.Children.Add(childNode);
                    }

                    // Prune empty leaves that carry neither geometry nor descendants.
                    if (cad.Mesh == null && cad.Children.Count == 0) return null;
                    return cad;
                }
                finally { _visiting.Remove(nodeIndex); }
            }

            Mat4 LocalMatrix(JNode node)
            {
                if (node.Has("matrix"))
                {
                    var m = node["matrix"].AsFloatArray();
                    if (m != null && m.Length == 16) return new Mat4(m);
                }
                var t = node["translation"].AsFloatArray();
                var r = node["rotation"].AsFloatArray();
                var s = node["scale"].AsFloatArray();
                return Mat4.TRS(
                    t != null && t.Length == 3 ? t : new[] { 0f, 0f, 0f },
                    r != null && r.Length == 4 ? r : new[] { 0f, 0f, 0f, 1f },
                    s != null && s.Length == 3 ? s : new[] { 1f, 1f, 1f });
            }

            // --- meshes / primitives -------------------------------------------------------

            List<CADMeshData> BuildMesh(int meshIndex, Mat4 world)
            {
                var result = new List<CADMeshData>();
                var mesh = _root["meshes"][meshIndex];
                string meshName = mesh["name"].AsString($"mesh{meshIndex}");

                Mat4 normalMat = world.NormalMatrix();
                bool flipWinding = world.Determinant3() < 0f;

                int primIndex = 0;
                foreach (var prim in mesh["primitives"].Items)
                {
                    var data = BuildPrimitive(prim, meshName, world, normalMat, flipWinding);
                    if (data != null) result.Add(data);
                    primIndex++;
                }
                return result;
            }

            CADMeshData BuildPrimitive(JNode prim, string meshName, Mat4 world, Mat4 normalMat, bool flipWinding)
            {
                int mode = prim.Has("mode") ? prim["mode"].AsInt() : MODE_TRIANGLES;
                if (mode != MODE_TRIANGLES && mode != MODE_TRIANGLE_STRIP && mode != MODE_TRIANGLE_FAN)
                    return null; // points/lines have no surface to import

                var attributes = prim["attributes"];
                if (!attributes.Has("POSITION")) return null;

                float[] pos = ReadAccessorFloats(attributes["POSITION"].AsInt(), out int posComps);
                if (pos == null || posComps < 3) return null;
                int vcount = pos.Length / posComps;

                var positions = new Vector3[vcount];
                for (int i = 0; i < vcount; i++)
                    positions[i] = world.TransformPoint(pos[i * posComps], pos[i * posComps + 1], pos[i * posComps + 2]);

                Vector3[] normals = null;
                if (attributes.Has("NORMAL"))
                {
                    float[] nrm = ReadAccessorFloats(attributes["NORMAL"].AsInt(), out int nc);
                    if (nrm != null && nc >= 3 && nrm.Length / nc == vcount)
                    {
                        normals = new Vector3[vcount];
                        for (int i = 0; i < vcount; i++)
                            normals[i] = normalMat.TransformDirection(
                                nrm[i * nc], nrm[i * nc + 1], nrm[i * nc + 2]).normalized;
                    }
                }

                Vector2[] uv = null;
                if (attributes.Has("TEXCOORD_0"))
                {
                    float[] t = ReadAccessorFloats(attributes["TEXCOORD_0"].AsInt(), out int tc);
                    if (t != null && tc >= 2 && t.Length / tc == vcount)
                    {
                        uv = new Vector2[vcount];
                        for (int i = 0; i < vcount; i++)
                            uv[i] = new Vector2(t[i * tc], 1f - t[i * tc + 1]); // glTF V is top-down
                    }
                }

                Color32[] colors = null;
                if (attributes.Has("COLOR_0"))
                {
                    float[] c = ReadAccessorFloats(attributes["COLOR_0"].AsInt(), out int cc);
                    if (c != null && cc >= 3 && c.Length / cc == vcount)
                    {
                        colors = new Color32[vcount];
                        for (int i = 0; i < vcount; i++)
                            colors[i] = new Color(c[i * cc], c[i * cc + 1], c[i * cc + 2],
                                cc >= 4 ? c[i * cc + 3] : 1f);
                    }
                }

                // Index buffer -> triangle list (expanding strips/fans, applying winding flip).
                int[] tris = BuildTriangleList(prim, vcount, mode, flipWinding);
                if (tris.Length < 3) return null;

                string materialName = ResolveMaterial(prim);

                return new CADMeshData
                {
                    Name = meshName,
                    Positions = positions,
                    Normals = normals,
                    UV = uv,
                    Colors = colors,
                    Submeshes = new[] { tris },
                    SubmeshMaterials = new[] { materialName }
                };
            }

            int[] BuildTriangleList(JNode prim, int vcount, int mode, bool flipWinding)
            {
                int[] src;
                if (prim.Has("indices"))
                    src = ReadIndices(prim["indices"].AsInt());
                else
                {
                    src = new int[vcount];
                    for (int i = 0; i < vcount; i++) src[i] = i;
                }

                List<int> tri;
                if (mode == MODE_TRIANGLES)
                {
                    tri = new List<int>(src.Length);
                    for (int i = 0; i + 2 < src.Length; i += 3)
                    { tri.Add(src[i]); tri.Add(src[i + 1]); tri.Add(src[i + 2]); }
                }
                else if (mode == MODE_TRIANGLE_STRIP)
                {
                    tri = new List<int>();
                    for (int i = 0; i + 2 < src.Length; i++)
                    {
                        if ((i & 1) == 0) { tri.Add(src[i]); tri.Add(src[i + 1]); tri.Add(src[i + 2]); }
                        else { tri.Add(src[i]); tri.Add(src[i + 2]); tri.Add(src[i + 1]); }
                    }
                }
                else // MODE_TRIANGLE_FAN
                {
                    tri = new List<int>();
                    for (int i = 1; i + 1 < src.Length; i++)
                    { tri.Add(src[0]); tri.Add(src[i]); tri.Add(src[i + 1]); }
                }

                if (flipWinding)
                    for (int i = 0; i + 2 < tri.Count; i += 3)
                    { int t = tri[i + 1]; tri[i + 1] = tri[i + 2]; tri[i + 2] = t; }

                return tri.ToArray();
            }

            // --- accessors -----------------------------------------------------------------

            static int ComponentSize(int componentType)
            {
                switch (componentType)
                {
                    case BYTE: case UBYTE: return 1;
                    case SHORT: case USHORT: return 2;
                    case UINT: case FLOAT: return 4;
                    default: throw new InvalidDataException($"Unknown accessor componentType {componentType}.");
                }
            }

            static int TypeComponentCount(string type)
            {
                switch (type)
                {
                    case "SCALAR": return 1;
                    case "VEC2": return 2;
                    case "VEC3": return 3;
                    case "VEC4": return 4;
                    case "MAT2": return 4;
                    case "MAT3": return 9;
                    case "MAT4": return 16;
                    default: throw new InvalidDataException($"Unknown accessor type '{type}'.");
                }
            }

            /// <summary>Reads an accessor as a flat float array of length count*components (row-major
            /// per element), applying integer normalization and sparse substitution.</summary>
            float[] ReadAccessorFloats(int accessorIndex, out int components)
            {
                var acc = _root["accessors"][accessorIndex];
                if (!acc.Exists) { components = 0; return null; }

                int componentType = acc["componentType"].AsInt();
                int count = acc["count"].AsInt();
                string type = acc["type"].AsString("SCALAR");
                bool normalized = acc["normalized"].AsBool();
                components = TypeComponentCount(type);
                int compSize = ComponentSize(componentType);

                var result = new float[count * components];

                if (acc.Has("bufferView"))
                    ReadInto(result, acc["bufferView"].AsInt(), acc["byteOffset"].AsInt(),
                        count, components, componentType, compSize, normalized);
                // else: accessor is implicitly all-zero unless overridden by sparse.

                ApplySparse(acc, result, components, normalized);
                return result;
            }

            void ReadInto(float[] dst, int bufferViewIndex, int accByteOffset, int count, int components,
                int componentType, int compSize, bool normalized)
            {
                var bv = _root["bufferViews"][bufferViewIndex];
                int buffer = bv["buffer"].AsInt();
                int bvOffset = bv["byteOffset"].AsInt();
                int stride = bv.Has("byteStride") ? bv["byteStride"].AsInt() : components * compSize;
                byte[] data = _buffers[buffer];
                int baseOffset = bvOffset + accByteOffset;

                for (int e = 0; e < count; e++)
                {
                    int elem = baseOffset + e * stride;
                    for (int c = 0; c < components; c++)
                        dst[e * components + c] =
                            ReadComponent(data, elem + c * compSize, componentType, normalized);
                }
            }

            void ApplySparse(JNode acc, float[] dst, int components, bool normalized)
            {
                var sparse = acc["sparse"];
                if (!sparse.Exists) return;

                int sCount = sparse["count"].AsInt();
                var idxNode = sparse["indices"];
                var valNode = sparse["values"];
                int idxCompType = idxNode["componentType"].AsInt();

                int[] indices = ReadRawIndices(idxNode["bufferView"].AsInt(), idxNode["byteOffset"].AsInt(),
                    sCount, idxCompType);

                var valBv = valNode["bufferView"];
                int valBvIndex = valBv.AsInt();
                int valByteOffset = valNode["byteOffset"].AsInt();
                int valComponentType = acc["componentType"].AsInt();
                int valCompSize = ComponentSize(valComponentType);

                var bv = _root["bufferViews"][valBvIndex];
                byte[] data = _buffers[bv["buffer"].AsInt()];
                int valBase = bv["byteOffset"].AsInt() + valByteOffset;
                int valStride = components * valCompSize; // sparse values are tightly packed

                for (int i = 0; i < sCount; i++)
                {
                    int target = indices[i];
                    for (int c = 0; c < components; c++)
                        dst[target * components + c] = ReadComponent(
                            data, valBase + i * valStride + c * valCompSize, valComponentType, normalized);
                }
            }

            /// <summary>Reads an index accessor's contents as a plain int array (component types
            /// UBYTE/USHORT/UINT), used for primitive indices.</summary>
            int[] ReadIndices(int accessorIndex)
            {
                var acc = _root["accessors"][accessorIndex];
                int count = acc["count"].AsInt();
                int componentType = acc["componentType"].AsInt();
                return ReadRawIndices(acc["bufferView"].AsInt(), acc["byteOffset"].AsInt(), count, componentType);
            }

            int[] ReadRawIndices(int bufferViewIndex, int accByteOffset, int count, int componentType)
            {
                var bv = _root["bufferViews"][bufferViewIndex];
                byte[] data = _buffers[bv["buffer"].AsInt()];
                int compSize = ComponentSize(componentType);
                int stride = bv.Has("byteStride") ? bv["byteStride"].AsInt() : compSize;
                int baseOffset = bv["byteOffset"].AsInt() + accByteOffset;

                var result = new int[count];
                for (int i = 0; i < count; i++)
                {
                    int off = baseOffset + i * stride;
                    switch (componentType)
                    {
                        case UBYTE: result[i] = data[off]; break;
                        case USHORT: result[i] = data[off] | (data[off + 1] << 8); break;
                        case UINT: result[i] = (int)ReadU32(data, off); break;
                        default: throw new InvalidDataException($"Invalid index componentType {componentType}.");
                    }
                }
                return result;
            }

            static float ReadComponent(byte[] data, int offset, int componentType, bool normalized)
            {
                switch (componentType)
                {
                    case FLOAT:
                        return BitConverter.ToSingle(data, offset);
                    case UBYTE:
                    {
                        byte v = data[offset];
                        return normalized ? v / 255f : v;
                    }
                    case BYTE:
                    {
                        sbyte v = (sbyte)data[offset];
                        return normalized ? Mathf.Max(v / 127f, -1f) : v;
                    }
                    case USHORT:
                    {
                        ushort v = (ushort)(data[offset] | (data[offset + 1] << 8));
                        return normalized ? v / 65535f : v;
                    }
                    case SHORT:
                    {
                        short v = (short)(data[offset] | (data[offset + 1] << 8));
                        return normalized ? Mathf.Max(v / 32767f, -1f) : v;
                    }
                    case UINT:
                        return ReadU32(data, offset);
                    default:
                        throw new InvalidDataException($"Unknown componentType {componentType}.");
                }
            }

            // --- materials & textures ------------------------------------------------------

            string ResolveMaterial(JNode prim)
            {
                if (!prim.Has("material")) return null; // builder uses its default
                int mi = prim["material"].AsInt();
                if (!_materials.TryGetValue(mi, out var info))
                {
                    info = BuildMaterial(mi);
                    _materials[mi] = info;
                    _model.Materials.Add(info);
                }
                return info.Name;
            }

            CADMaterialInfo BuildMaterial(int matIndex)
            {
                var mat = _root["materials"][matIndex];
                string rawName = mat["name"].AsString($"Material_{matIndex}");
                var info = new CADMaterialInfo { Name = UniqueMaterialName(rawName, matIndex) };

                var pbr = mat["pbrMetallicRoughness"];
                var baseColor = pbr["baseColorFactor"].AsFloatArray();
                info.Color = baseColor != null && baseColor.Length == 4
                    ? new Color(baseColor[0], baseColor[1], baseColor[2], baseColor[3])
                    : Color.white;
                info.Metallic = pbr.Has("metallicFactor") ? pbr["metallicFactor"].AsFloat() : 1f;
                float roughness = pbr.Has("roughnessFactor") ? pbr["roughnessFactor"].AsFloat() : 1f;
                info.Smoothness = 1f - Mathf.Clamp01(roughness);

                info.BaseColorTex = LoadTextureRef(pbr["baseColorTexture"], linear: false);
                info.MetallicRoughnessTex = LoadTextureRef(pbr["metallicRoughnessTexture"], linear: true);
                info.NormalTex = LoadTextureRef(mat["normalTexture"], linear: true);
                if (mat["normalTexture"].Has("scale")) info.NormalScale = mat["normalTexture"]["scale"].AsFloat(1f);
                info.OcclusionTex = LoadTextureRef(mat["occlusionTexture"], linear: true);
                if (mat["occlusionTexture"].Has("strength"))
                    info.OcclusionStrength = mat["occlusionTexture"]["strength"].AsFloat(1f);

                var emissive = mat["emissiveFactor"].AsFloatArray();
                Color emissiveColor = emissive != null && emissive.Length == 3
                    ? new Color(emissive[0], emissive[1], emissive[2], 1f)
                    : Color.black;
                float emissiveStrength = mat["extensions"]["KHR_materials_emissive_strength"]
                    ["emissiveStrength"].AsFloat(1f);
                info.EmissiveColor = emissiveColor * emissiveStrength;
                info.EmissiveTex = LoadTextureRef(mat["emissiveTexture"], linear: false);

                switch (mat["alphaMode"].AsString("OPAQUE"))
                {
                    case "MASK": info.AlphaMode = CADAlphaMode.Mask; break;
                    case "BLEND": info.AlphaMode = CADAlphaMode.Blend; break;
                    default: info.AlphaMode = CADAlphaMode.Opaque; break;
                }
                info.AlphaCutoff = mat.Has("alphaCutoff") ? mat["alphaCutoff"].AsFloat() : 0.5f;
                info.DoubleSided = mat["doubleSided"].AsBool();
                info.Unlit = mat["extensions"]["KHR_materials_unlit"].Exists;
                return info;
            }

            readonly HashSet<string> _usedMaterialNames = new HashSet<string>();
            string UniqueMaterialName(string raw, int index)
            {
                string name = string.IsNullOrEmpty(raw) ? $"Material_{index}" : raw;
                if (_usedMaterialNames.Add(name)) return name;
                string candidate = $"{name}_{index}";
                while (!_usedMaterialNames.Add(candidate)) candidate += "_";
                return candidate;
            }

            CADTextureImage LoadTextureRef(JNode textureRef, bool linear)
            {
                if (!textureRef.Exists || !textureRef.Has("index")) return null;
                int texIndex = textureRef["index"].AsInt();
                int texCoord = textureRef["texCoord"].AsInt();

                var texture = _root["textures"][texIndex];
                if (!texture.Exists) return null;

                // basisu (KTX2) textures aren't decodable here — skip, keep colour factors.
                if (texture["extensions"]["KHR_texture_basisu"].Exists)
                {
                    Debug.LogWarning($"CAD Importer: glTF texture {texIndex} uses KTX2/basisu " +
                                     "(unsupported); using material colour factors only.");
                    return null;
                }
                if (!texture.Has("source")) return null;
                int source = texture["source"].AsInt();

                int cacheKey = source * 2 + (linear ? 1 : 0);
                if (_imageCache.TryGetValue(cacheKey, out var cached))
                {
                    cached.TexCoord = texCoord;
                    return cached;
                }

                byte[] bytes = LoadImageBytes(source, out string mime);
                if (bytes == null) return null;
                if (mime == "image/ktx2")
                {
                    Debug.LogWarning($"CAD Importer: glTF image {source} is KTX2 (unsupported); skipped.");
                    return null;
                }

                var img = new CADTextureImage
                {
                    Name = _root["images"][source]["name"].AsString($"image{source}"),
                    EncodedBytes = bytes,
                    TexCoord = texCoord,
                    Linear = linear,
                    CacheKey = $"img{source}:{(linear ? 1 : 0)}"
                };
                _imageCache[cacheKey] = img;
                return img;
            }

            byte[] LoadImageBytes(int imageIndex, out string mimeType)
            {
                var image = _root["images"][imageIndex];
                mimeType = image["mimeType"].AsString("");

                string uri = image["uri"].AsString();
                if (!string.IsNullOrEmpty(uri))
                {
                    if (IsDataUri(uri))
                    {
                        if (string.IsNullOrEmpty(mimeType)) mimeType = DataUriMime(uri);
                        return DecodeDataUri(uri);
                    }
                    string file = Path.Combine(_baseDir ?? "", Uri.UnescapeDataString(uri));
                    if (!File.Exists(file))
                    {
                        Debug.LogWarning($"CAD Importer: glTF image not found: {uri}");
                        return null;
                    }
                    if (string.IsNullOrEmpty(mimeType))
                        mimeType = uri.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                   uri.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                                   ? "image/jpeg" : "image/png";
                    return File.ReadAllBytes(file);
                }

                if (image.Has("bufferView"))
                {
                    var bv = _root["bufferViews"][image["bufferView"].AsInt()];
                    byte[] data = _buffers[bv["buffer"].AsInt()];
                    int off = bv["byteOffset"].AsInt();
                    int len = bv["byteLength"].AsInt();
                    var slice = new byte[len];
                    Array.Copy(data, off, slice, 0, len);
                    return slice;
                }
                return null;
            }

            // --- data URIs & little-endian helpers -----------------------------------------

            static bool IsDataUri(string uri) => uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

            static byte[] DecodeDataUri(string uri)
            {
                int comma = uri.IndexOf(',');
                if (comma < 0) throw new InvalidDataException("Malformed data URI.");
                string meta = uri.Substring(5, comma - 5);
                string payload = uri.Substring(comma + 1);
                if (meta.IndexOf("base64", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Convert.FromBase64String(payload);
                return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
            }

            static string DataUriMime(string uri)
            {
                int comma = uri.IndexOf(',');
                string meta = comma > 5 ? uri.Substring(5, comma - 5) : "";
                int semi = meta.IndexOf(';');
                return semi >= 0 ? meta.Substring(0, semi) : meta;
            }

            static uint ReadU32(byte[] b, int off) =>
                (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        }

        /// <summary>
        /// Column-major 4x4 matrix (glTF convention) using plain floats, so transform baking is
        /// independent of any Unity maths and can be validated off-engine.
        /// </summary>
        readonly struct Mat4
        {
            readonly float[] m; // 16 elements, column-major: m[col*4 + row]

            public Mat4(float[] cm) { m = cm; }

            public static Mat4 Identity() =>
                new Mat4(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 });

            public static Mat4 TRS(float[] t, float[] q, float[] s)
            {
                // Rotation matrix from quaternion (x, y, z, w).
                float x = q[0], y = q[1], z = q[2], w = q[3];
                float xx = x * x, yy = y * y, zz = z * z;
                float xy = x * y, xz = x * z, yz = y * z;
                float wx = w * x, wy = w * y, wz = w * z;
                float sx = s[0], sy = s[1], sz = s[2];

                var r = new float[16];
                // Column 0 (scaled by sx)
                r[0] = (1 - 2 * (yy + zz)) * sx;
                r[1] = (2 * (xy + wz)) * sx;
                r[2] = (2 * (xz - wy)) * sx;
                r[3] = 0;
                // Column 1 (scaled by sy)
                r[4] = (2 * (xy - wz)) * sy;
                r[5] = (1 - 2 * (xx + zz)) * sy;
                r[6] = (2 * (yz + wx)) * sy;
                r[7] = 0;
                // Column 2 (scaled by sz)
                r[8] = (2 * (xz + wy)) * sz;
                r[9] = (2 * (yz - wx)) * sz;
                r[10] = (1 - 2 * (xx + yy)) * sz;
                r[11] = 0;
                // Column 3 (translation)
                r[12] = t[0]; r[13] = t[1]; r[14] = t[2]; r[15] = 1;
                return new Mat4(r);
            }

            public Mat4 Mul(Mat4 b)
            {
                var a = m; var bb = b.m;
                var r = new float[16];
                for (int col = 0; col < 4; col++)
                    for (int row = 0; row < 4; row++)
                    {
                        float sum = 0;
                        for (int k = 0; k < 4; k++) sum += a[k * 4 + row] * bb[col * 4 + k];
                        r[col * 4 + row] = sum;
                    }
                return new Mat4(r);
            }

            public Vector3 TransformPoint(float x, float y, float z) => new Vector3(
                m[0] * x + m[4] * y + m[8] * z + m[12],
                m[1] * x + m[5] * y + m[9] * z + m[13],
                m[2] * x + m[6] * y + m[10] * z + m[14]);

            public Vector3 TransformDirection(float x, float y, float z) => new Vector3(
                m[0] * x + m[4] * y + m[8] * z,
                m[1] * x + m[5] * y + m[9] * z,
                m[2] * x + m[6] * y + m[10] * z);

            public float Determinant3()
            {
                float m00 = m[0], m01 = m[4], m02 = m[8];
                float m10 = m[1], m11 = m[5], m12 = m[9];
                float m20 = m[2], m21 = m[6], m22 = m[10];
                return m00 * (m11 * m22 - m12 * m21)
                     - m01 * (m10 * m22 - m12 * m20)
                     + m02 * (m10 * m21 - m11 * m20);
            }

            /// <summary>Inverse-transpose of the upper-left 3x3, for correct normals under
            /// non-uniform scale. Falls back to the plain 3x3 when near-singular.</summary>
            public Mat4 NormalMatrix()
            {
                float m00 = m[0], m01 = m[4], m02 = m[8];
                float m10 = m[1], m11 = m[5], m12 = m[9];
                float m20 = m[2], m21 = m[6], m22 = m[10];

                float c00 = m11 * m22 - m12 * m21;
                float c01 = m12 * m20 - m10 * m22;
                float c02 = m10 * m21 - m11 * m20;
                float det = m00 * c00 + m01 * c01 + m02 * c02;

                if (Mathf.Abs(det) < 1e-12f)
                    return new Mat4(new float[] { m00, m10, m20, 0, m01, m11, m21, 0, m02, m12, m22, 0, 0, 0, 0, 1 });

                float c10 = m02 * m21 - m01 * m22;
                float c11 = m00 * m22 - m02 * m20;
                float c12 = m01 * m20 - m00 * m21;
                float c20 = m01 * m12 - m02 * m11;
                float c21 = m02 * m10 - m00 * m12;
                float c22 = m00 * m11 - m01 * m10;
                float inv = 1f / det;

                // Normal matrix = inverse-transpose(M3) = cofactor(M3)/det. TransformDirection
                // needs element(row,col) at column-major index col*4+row, and element(r,c) = C_rc/det,
                // so column c holds (C_0c, C_1c, C_2c)/det. (For a pure rotation this reduces to R.)
                var r = new float[16];
                r[0] = c00 * inv; r[1] = c10 * inv; r[2] = c20 * inv; r[3] = 0;  // column 0
                r[4] = c01 * inv; r[5] = c11 * inv; r[6] = c21 * inv; r[7] = 0;  // column 1
                r[8] = c02 * inv; r[9] = c12 * inv; r[10] = c22 * inv; r[11] = 0; // column 2
                r[12] = 0; r[13] = 0; r[14] = 0; r[15] = 1;
                return new Mat4(r);
            }
        }
    }
}
