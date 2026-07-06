using System;
using UnityEditor.AssetImporters;

namespace CADImporter.Editor
{
    /// <summary>Imports .ply files (mesh or point data with vertex colors, e.g. scan data).</summary>
    [ScriptedImporter(2, "ply")]
    public class PlyScriptedImporter : ScriptedImporter
    {
        public CADImportSettings settings = new CADImportSettings();

        public override void OnImportAsset(AssetImportContext ctx)
        {
            try
            {
                var model = PlyParser.Parse(ctx.assetPath);
                CADAssetBuilder.Build(ctx, model, settings ?? new CADImportSettings());
            }
            catch (Exception e)
            {
                ctx.LogImportError($"CAD Importer: failed to import PLY '{ctx.assetPath}': {e.Message}");
                CADAssetBuilder.BuildPlaceholder(ctx, System.IO.Path.GetFileNameWithoutExtension(ctx.assetPath));
            }
        }
    }
}
