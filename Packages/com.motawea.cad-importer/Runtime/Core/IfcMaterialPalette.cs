using System.Collections.Generic;
using UnityEngine;

namespace CADImporter
{
    /// <summary>Rendering finish for one IFC category: colour plus PBR metallic/smoothness.</summary>
    public readonly struct IfcFinish
    {
        public readonly Color Color;
        public readonly float Metallic;
        public readonly float Smoothness;

        public IfcFinish(Color color, float metallic, float smoothness)
        {
            Color = color;
            Metallic = metallic;
            Smoothness = smoothness;
        }
    }

    /// <summary>
    /// A professional "colour by category / material" palette for IFC elements that carry no
    /// authored surface style — the muted, desaturated scheme modern BIM viewers (Solibri,
    /// BIMcollab, Speckle, Autodesk viewers) default to, so an unstyled building reads clearly.
    /// The element's associated IFC <b>material</b> is consulted first (so a glazed wall, a
    /// curtain-wall plate, a steel column etc. are coloured by what they are made of — glazing is
    /// automatically translucent), then its IFC <b>type</b>. Files that define their own styles
    /// keep those colours; this is only the fallback. Colours are sRGB, with metallic/smoothness
    /// chosen so glass looks like glass, steel like metal and concrete stays matte.
    /// </summary>
    public static class IfcMaterialPalette
    {
        static Color RGB(int r, int g, int b, float a = 1f) =>
            new Color(r / 255f, g / 255f, b / 255f, a);

        // Helper so the finishes below can be declared from 0..255 sRGB ints.
        static IfcFinish F(int r, int g, int b, float metallic, float smoothness, float a = 1f) =>
            new IfcFinish(new Color(r / 255f, g / 255f, b / 255f, a), metallic, smoothness);

        // --- named finishes, shared by the type map and the material-keyword map ---
        static readonly IfcFinish PlasterF     = F(230, 226, 218, 0f, 0.12f);        // off-white
        static readonly IfcFinish ConcreteF    = F(190, 185, 175, 0f, 0.08f);
        static readonly IfcFinish CharcoalF    = F( 76,  81,  87, 0f, 0.25f);
        static readonly IfcFinish SteelF       = F(126, 131, 136, 0.55f, 0.55f);
        static readonly IfcFinish LightSteelF  = F(140, 145, 150, 0.55f, 0.50f);
        static readonly IfcFinish DarkMetalF   = F( 95, 100, 105, 0.60f, 0.60f);
        static readonly IfcFinish GlassF       = F(175, 205, 230, 0f, 0.90f, 0.25f); // translucent
        static readonly IfcFinish WoodF        = F(155, 106,  68, 0f, 0.20f);
        static readonly IfcFinish BrickF       = F(160,  92,  74, 0f, 0.10f);
        static readonly IfcFinish InsulationF  = F(219, 199, 128, 0f, 0.20f);
        static readonly IfcFinish CoveringF    = F(210, 204, 196, 0f, 0.10f);
        static readonly IfcFinish CirculationF = F(156, 160, 164, 0.10f, 0.30f);
        static readonly IfcFinish FurnitureF   = F(178, 167, 148, 0f, 0.20f);
        static readonly IfcFinish SpaceF       = F(150, 180, 205, 0f, 0.20f, 0.10f);
        static readonly IfcFinish PipeF        = F( 79, 163, 158, 0.30f, 0.50f);     // teal
        static readonly IfcFinish DuctF        = F(211, 177,  92, 0.20f, 0.40f);     // tan

        /// <summary>Neutral warm grey for proxies / unknown categories.</summary>
        public static readonly IfcFinish Default = F(195, 191, 183, 0f, 0.15f);

        // Exact IFC type -> finish. Keys are the entity names ifcopenshell reports (elem.is_a()).
        static readonly Dictionary<string, IfcFinish> ByType = new Dictionary<string, IfcFinish>
        {
            { "IfcWall", PlasterF }, { "IfcWallStandardCase", PlasterF },
            { "IfcSlab", ConcreteF }, { "IfcCovering", CoveringF }, { "IfcRoof", CharcoalF },
            { "IfcSpace", SpaceF },
            { "IfcColumn", SteelF }, { "IfcBeam", LightSteelF }, { "IfcMember", LightSteelF },
            { "IfcPlate", LightSteelF }, { "IfcRailing", DarkMetalF },
            { "IfcWindow", GlassF }, { "IfcCurtainWall", GlassF },
            { "IfcDoor", WoodF },
            { "IfcStair", CirculationF }, { "IfcStairFlight", CirculationF },
            { "IfcRamp", CirculationF }, { "IfcRampFlight", CirculationF },
            { "IfcFurnishingElement", FurnitureF }, { "IfcFurniture", FurnitureF },
        };

        /// <summary>
        /// Finish for an element, preferring its associated IFC material (<paramref name="materialHint"/>,
        /// a lower-cased material name/category) over its IFC <paramref name="ifcType"/>. This is how a
        /// glazed wall or a steel brace gets the right look — and how glass is auto-detected and made
        /// translucent regardless of element type. Pass null <paramref name="materialHint"/> to colour
        /// purely by type.
        /// </summary>
        public static IfcFinish ForElement(string ifcType, string materialHint)
        {
            var byMat = ByMaterialKeyword(materialHint);
            return byMat ?? ForType(ifcType);
        }

        /// <summary>Finish by IFC entity type, with family heuristics and a neutral default.</summary>
        public static IfcFinish ForType(string ifcType)
        {
            if (string.IsNullOrEmpty(ifcType)) return Default;
            if (ByType.TryGetValue(ifcType, out var f)) return f;

            if (ifcType.StartsWith("IfcPipe") || (ifcType.StartsWith("IfcFlow") && ifcType.Contains("Segment")))
                return PipeF;
            if (ifcType.StartsWith("IfcDuct")) return DuctF;
            if (ifcType.StartsWith("IfcWall")) return PlasterF;
            if (ifcType.StartsWith("IfcSlab")) return ConcreteF;
            if (ifcType.StartsWith("IfcColumn")) return SteelF;
            if (ifcType.StartsWith("IfcBeam") || ifcType.StartsWith("IfcMember")) return LightSteelF;

            return Default;
        }

        /// <summary>
        /// Maps an IFC material name/category to a finish. Insulation ("glass wool", "glass fibre")
        /// is matched before glazing so it isn't mistaken for transparent glass. Returns null when
        /// no keyword matches, so the caller falls back to the type palette.
        /// </summary>
        static IfcFinish? ByMaterialKeyword(string hint)
        {
            if (string.IsNullOrEmpty(hint)) return null;
            hint = hint.ToLowerInvariant();

            if (Has(hint, "insulation", "mineral wool", "rockwool", "glass wool", "glass fib", "glasswool"))
                return InsulationF;
            if (Has(hint, "glass", "glazing", "glazed", "glaz"))
                return GlassF;                                   // <-- auto-translucent
            if (Has(hint, "steel", "metal", "alumin", "iron", "zinc", "copper"))
                return SteelF;
            if (Has(hint, "concrete", "cement", "cast-in"))
                return ConcreteF;
            if (Has(hint, "timber", "wood", "oak", "pine", "plywood", "mdf"))
                return WoodF;
            if (Has(hint, "brick", "masonry", "blockwork", "clay"))
                return BrickF;
            if (Has(hint, "gypsum", "plaster", "drywall", "plasterboard"))
                return PlasterF;
            return null;
        }

        static bool Has(string s, params string[] keys)
        {
            foreach (var k in keys) if (s.Contains(k)) return true;
            return false;
        }
    }
}
