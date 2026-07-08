using UnityEngine;

namespace CADImporter
{
    /// <summary>
    /// Channel repacking between glTF's texture conventions and Unity's (URP Lit / Standard).
    /// glTF packs occlusion=R, roughness=G, metallic=B in one metallic-roughness image and
    /// occlusion=R separately; Unity's mask maps expect metallic=R, smoothness=A and
    /// occlusion=G. Kept free of Unity texture APIs so it can be unit-tested off-engine.
    /// </summary>
    public static class TextureRepack
    {
        /// <summary>glTF metallic-roughness (G=roughness, B=metallic) → Unity metallic-gloss
        /// (R=metallic, A=smoothness=1-roughness). Other channels are cleared.</summary>
        public static void MetallicRoughnessGltfToUnity(Color32[] px)
        {
            for (int i = 0; i < px.Length; i++)
            {
                byte metallic = px[i].b;
                byte roughness = px[i].g;
                px[i] = new Color32(metallic, 0, 0, (byte)(255 - roughness));
            }
        }

        /// <summary>glTF occlusion (R) → Unity occlusion map (sampled from G).</summary>
        public static void OcclusionGltfToUnity(Color32[] px)
        {
            for (int i = 0; i < px.Length; i++)
            {
                byte occ = px[i].r;
                px[i] = new Color32(occ, occ, occ, 255);
            }
        }
    }
}
