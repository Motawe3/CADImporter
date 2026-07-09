using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace CADImporter
{
    /// <summary>
    /// Deduplicates flat per-element colours (e.g. IFC surface styles) into a small set of shared
    /// <see cref="CADMaterialInfo"/> instances on a <see cref="CADModel"/>. Colours are quantised to
    /// 8 bits per channel so visually identical element colours collapse to one material — fewer
    /// materials let Unity batch the many small elements of a building together, which is the main
    /// realtime-rendering win for BIM-scale scenes.
    /// </summary>
    public static class CadColorPalette
    {
        /// <summary>
        /// Stable key for a colour + finish, quantised so visually identical materials collapse.
        /// The finish is part of the key so two categories that share a colour but differ in
        /// metallic/smoothness (e.g. matte vs. polished) stay distinct.
        /// </summary>
        public static string Key(Color c, float smoothness = 0.15f, float metallic = 0f) =>
            string.Format(CultureInfo.InvariantCulture, "Col_{0:X2}{1:X2}{2:X2}{3:X2}_{4:X2}{5:X2}",
                Q(c.r), Q(c.g), Q(c.b), Q(c.a), Q(smoothness), Q(metallic));

        /// <summary>
        /// Returns the name of a shared material for <paramref name="color"/> + finish, creating a
        /// <see cref="CADMaterialInfo"/> on <paramref name="model"/> the first time it is seen
        /// (tracked in <paramref name="seen"/>). Translucent colours (alpha &lt; 1) are marked
        /// <see cref="CADAlphaMode.Blend"/>.
        /// </summary>
        public static string GetOrAdd(CADModel model, HashSet<string> seen, Color color,
            float smoothness = 0.15f, float metallic = 0f)
        {
            string key = Key(color, smoothness, metallic);
            if (seen.Add(key))
            {
                model.Materials.Add(new CADMaterialInfo
                {
                    Name = key,
                    Color = color,
                    Metallic = metallic,
                    Smoothness = smoothness,
                    AlphaMode = color.a < 0.999f ? CADAlphaMode.Blend : CADAlphaMode.Opaque
                });
            }
            return key;
        }

        static int Q(float v) => Mathf.Clamp(Mathf.RoundToInt(v * 255f), 0, 255);
    }
}
