using System;
using UnityEditor.AssetImporters;

namespace CADImporter.Editor
{
    /// <summary>
    /// Imports glTF 2.0 (.gltf) and binary GLB (.glb) files as prefabs, running the same CAD
    /// pipeline (welding, LODs, colliders) as the other formats. glTF geometry is metres and
    /// Y-up right-handed by specification, so the defaults differ from the CAD formats.
    /// </summary>
    [ScriptedImporter(1, new[] { "gltf", "glb" })]
    public class GltfScriptedImporter : ScriptedImporter
    {
        public CADImportSettings settings = CreateDefaults();

        /// <summary>glTF is defined in metres with a Y-up right-handed axis convention.</summary>
        internal static CADImportSettings CreateDefaults()
        {
            var s = new CADImportSettings
            {
                sourceUnit = SourceUnit.Meters,
                sourceOrientation = SourceOrientation.YUpRightHanded
            };
            return s;
        }

        public override void OnImportAsset(AssetImportContext ctx)
        {
            try
            {
                var model = GltfParser.Parse(ctx.assetPath);
                CADAssetBuilder.Build(ctx, model, settings ?? CreateDefaults());
            }
            catch (Exception e)
            {
                ctx.LogImportError($"CAD Importer: failed to import glTF '{ctx.assetPath}': {e.Message}");
                CADAssetBuilder.BuildPlaceholder(ctx, System.IO.Path.GetFileNameWithoutExtension(ctx.assetPath));
            }
        }
    }
}
