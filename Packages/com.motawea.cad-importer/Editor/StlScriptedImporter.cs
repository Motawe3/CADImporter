using System;
using UnityEditor.AssetImporters;

namespace CADImporter.Editor
{
    /// <summary>Makes .stl files first-class Unity assets: drag into the project and get a prefab.</summary>
    [ScriptedImporter(3, "stl")]
    public class StlScriptedImporter : ScriptedImporter
    {
        public CADImportSettings settings = new CADImportSettings();

        public override void OnImportAsset(AssetImportContext ctx)
        {
            try
            {
                var model = StlParser.Parse(ctx.assetPath);
                CADAssetBuilder.Build(ctx, model, settings ?? new CADImportSettings());
            }
            catch (Exception e)
            {
                ctx.LogImportError($"CAD Importer: failed to import STL '{ctx.assetPath}': {e.Message}");
                CADAssetBuilder.BuildPlaceholder(ctx, System.IO.Path.GetFileNameWithoutExtension(ctx.assetPath));
            }
        }
    }
}
