using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CADImporter
{
    /// <summary>
    /// Builds Unity materials (URP Lit, falling back to Standard) from parsed
    /// <see cref="CADMaterialInfo"/>. Handles both the simple colour-only case (OBJ/PLY/STEP)
    /// and full glTF metallic-roughness PBR: base colour, metallic-roughness, normal,
    /// occlusion and emissive maps, alpha mode and double-sided.
    ///
    /// Must run on the main thread — it creates <see cref="Texture2D"/>s and decodes image
    /// bytes (<see cref="ImageConversion"/> is not thread-safe). Created textures are returned
    /// so the editor importer can register them as sub-assets; the texture cache de-duplicates
    /// images shared by several materials within one import.
    /// </summary>
    public static class CadMaterialFactory
    {
        public sealed class Result
        {
            public Material Material;
            /// <summary>Textures newly created by this call (already de-duplicated via the cache).</summary>
            public readonly List<Texture2D> CreatedTextures = new List<Texture2D>();
        }

        public static Result Create(CADMaterialInfo info, bool vertexColorUnlit,
            Color fallbackColor, Dictionary<string, Texture2D> texCache)
        {
            var result = new Result();
            Color baseColor = info != null ? info.Color : fallbackColor;
            float smoothness = info != null ? info.Smoothness : 0.4f;
            float metallic = info != null ? info.Metallic : 0f;
            bool unlit = info != null && info.Unlit;

            var mat = new Material(ResolveShader(vertexColorUnlit, unlit))
            {
                name = Sanitize(info != null && !string.IsNullOrEmpty(info.Name) ? info.Name : "CAD_Default")
            };
            SetColor(mat, "_BaseColor", baseColor);
            SetColor(mat, "_Color", baseColor);
            SetFloat(mat, "_Metallic", metallic);
            SetFloat(mat, "_Smoothness", smoothness);
            SetFloat(mat, "_Glossiness", smoothness); // Standard shader's smoothness property

            if (info != null)
                ApplyPbr(mat, info, result, texCache, unlit || vertexColorUnlit);

            result.Material = mat;
            return result;
        }

        static void ApplyPbr(Material mat, CADMaterialInfo info, Result result,
            Dictionary<string, Texture2D> texCache, bool colorOnlyShader)
        {
            var baseMap = MakeTexture(info.BaseColorTex, "", texCache, result.CreatedTextures, null);
            if (baseMap != null)
            {
                SetTexture(mat, "_BaseMap", baseMap);
                SetTexture(mat, "_MainTex", baseMap);
            }

            // Unlit / vertex-colour shaders only carry a base map.
            if (!colorOnlyShader)
            {
                var mr = MakeTexture(info.MetallicRoughnessTex, "_mr", texCache, result.CreatedTextures,
                    TextureRepack.MetallicRoughnessGltfToUnity);
                if (mr != null && mat.HasProperty("_MetallicGlossMap"))
                {
                    mat.SetTexture("_MetallicGlossMap", mr);
                    mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                    SetFloat(mat, "_Metallic", 1f);    // let the map drive metallic/smoothness
                    SetFloat(mat, "_Smoothness", 1f);
                    SetFloat(mat, "_GlossMapScale", 1f);
                }

                var normal = MakeTexture(info.NormalTex, "_n", texCache, result.CreatedTextures, null);
                if (normal != null && mat.HasProperty("_BumpMap"))
                {
                    mat.SetTexture("_BumpMap", normal);
                    mat.EnableKeyword("_NORMALMAP");
                    SetFloat(mat, "_BumpScale", info.NormalScale);
                }

                var occlusion = MakeTexture(info.OcclusionTex, "_occ", texCache, result.CreatedTextures,
                    TextureRepack.OcclusionGltfToUnity);
                if (occlusion != null && mat.HasProperty("_OcclusionMap"))
                {
                    mat.SetTexture("_OcclusionMap", occlusion);
                    mat.EnableKeyword("_OCCLUSIONMAP");
                    SetFloat(mat, "_OcclusionStrength", info.OcclusionStrength);
                }

                if (!ColorsApproximatelyEqual(info.EmissiveColor, Color.black) || info.EmissiveTex != null)
                {
                    var em = MakeTexture(info.EmissiveTex, "", texCache, result.CreatedTextures, null);
                    if (em != null) SetTexture(mat, "_EmissionMap", em);
                    SetColor(mat, "_EmissionColor", info.EmissiveColor);
                    mat.EnableKeyword("_EMISSION");
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                }
            }

            SetupSurface(mat, info);
        }

        static void SetupSurface(Material mat, CADMaterialInfo info)
        {
            switch (info.AlphaMode)
            {
                case CADAlphaMode.Mask:
                    SetFloat(mat, "_AlphaClip", 1f);
                    SetFloat(mat, "_Cutoff", info.AlphaCutoff);
                    mat.EnableKeyword("_ALPHATEST_ON");
                    mat.renderQueue = (int)RenderQueue.AlphaTest;
                    break;
                case CADAlphaMode.Blend:
                    SetFloat(mat, "_Surface", 1f);        // URP: 0 opaque, 1 transparent
                    SetFloat(mat, "_Mode", 3f);           // Standard: transparent
                    SetFloat(mat, "_SrcBlend", (float)BlendMode.SrcAlpha);
                    SetFloat(mat, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    SetFloat(mat, "_ZWrite", 0f);
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.SetOverrideTag("RenderType", "Transparent");
                    mat.renderQueue = (int)RenderQueue.Transparent;
                    break;
            }
            if (info.DoubleSided) SetFloat(mat, "_Cull", (float)CullMode.Off);
        }

        static Texture2D MakeTexture(CADTextureImage img, string suffix,
            Dictionary<string, Texture2D> cache, List<Texture2D> created, System.Action<Color32[]> repack)
        {
            if (img == null || img.EncodedBytes == null || img.EncodedBytes.Length == 0) return null;
            string key = (img.CacheKey ?? img.Name) + suffix;
            if (cache != null && cache.TryGetValue(key, out var existing)) return existing;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true, linear: img.Linear);
            if (!tex.LoadImage(img.EncodedBytes, markNonReadable: false))
            {
                Debug.LogWarning($"CAD Importer: failed to decode texture '{img.Name}'.");
                return null;
            }
            tex.name = Sanitize(img.Name) + suffix;
            if (repack != null)
            {
                var px = tex.GetPixels32();
                repack(px);
                tex.SetPixels32(px);
                tex.Apply(updateMipmaps: true);
            }
            tex.wrapMode = TextureWrapMode.Repeat;

            cache?.Add(key, tex);
            created.Add(tex);
            return tex;
        }

        static Shader ResolveShader(bool vertexColorUnlit, bool unlit)
        {
            Shader s = null;
            if (vertexColorUnlit) s = Shader.Find("CAD Importer/Vertex Color Unlit");
            if (s == null && unlit) s = Shader.Find("Universal Render Pipeline/Unlit");
            if (s == null) s = Shader.Find("Universal Render Pipeline/Lit");
            if (s == null) s = Shader.Find("Standard");
            return s;
        }

        static void SetColor(Material m, string prop, Color c) { if (m.HasProperty(prop)) m.SetColor(prop, c); }
        static void SetFloat(Material m, string prop, float f) { if (m.HasProperty(prop)) m.SetFloat(prop, f); }
        static void SetTexture(Material m, string prop, Texture t) { if (m.HasProperty(prop)) m.SetTexture(prop, t); }

        static bool ColorsApproximatelyEqual(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 1e-4f && Mathf.Abs(a.g - b.g) < 1e-4f &&
            Mathf.Abs(a.b - b.b) < 1e-4f;

        static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Material";
            foreach (char bad in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');
            return name;
        }
    }
}
