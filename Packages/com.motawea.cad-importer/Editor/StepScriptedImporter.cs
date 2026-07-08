using System;
using System.IO;
using System.Linq;
using UnityEditor.AssetImporters;

namespace CADImporter.Editor
{
    /// <summary>
    /// Imports STEP/IGES B-rep files by tessellating them through a headless FreeCAD install
    /// (auto-detected; configurable in Tools → CAD Importer). Each solid/part in the file
    /// becomes a child GameObject, preserving assembly structure and part names.
    /// </summary>
    [ScriptedImporter(3, new[] { "step", "stp", "iges", "igs" })]
    public class StepScriptedImporter : ScriptedImporter
    {
        public CADImportSettings settings = new CADImportSettings();

        public override void OnImportAsset(AssetImportContext ctx)
        {
            string name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            var s = settings ?? new CADImportSettings();

            string converter = StepConverter.ResolveConverter();
            if (converter == null)
            {
                ctx.LogImportWarning(
                    "CAD Importer: STEP/IGES import requires FreeCAD (its bundled Open CASCADE kernel " +
                    "does the B-rep tessellation). Install FreeCAD from https://www.freecad.org and, if " +
                    "it is not auto-detected, set the FreeCADCmd.exe path in Tools → CAD Importer. " +
                    "Then reimport this asset.");
                CADAssetBuilder.BuildPlaceholder(ctx, name);
                return;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "UnityCADImporter", Guid.NewGuid().ToString("N"));
            try
            {
                string src = Path.GetFullPath(ctx.assetPath);
                if (!StepConverter.ConvertToStl(converter, src, tempDir,
                        s.stepLinearDeflection, s.stepAngularDeflection, s.stepTimeoutSeconds,
                        Path.GetFileName(ctx.assetPath), out string error))
                {
                    ctx.LogImportError($"CAD Importer: STEP conversion of '{ctx.assetPath}' failed. {error}");
                    CADAssetBuilder.BuildPlaceholder(ctx, name);
                    return;
                }

                var model = new CADModel { Name = name, Format = FormatOf(ctx.assetPath), SourcePath = ctx.assetPath };
                foreach (var stl in Directory.GetFiles(tempDir, "*.stl").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var part = StlParser.Parse(stl);
                    string partName = PartNameOf(stl);
                    foreach (var child in part.Root.Children)
                    {
                        child.Name = part.Root.Children.Count > 1 ? $"{partName}_{child.Name}" : partName;
                        model.Root.Children.Add(child);
                    }
                }

                if (model.Root.Children.Count == 0)
                {
                    ctx.LogImportError($"CAD Importer: '{ctx.assetPath}' produced no geometry.");
                    CADAssetBuilder.BuildPlaceholder(ctx, name);
                    return;
                }

                CADAssetBuilder.Build(ctx, model, s);
            }
            catch (Exception e)
            {
                ctx.LogImportError($"CAD Importer: failed to import '{ctx.assetPath}': {e.Message}");
                CADAssetBuilder.BuildPlaceholder(ctx, name);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        static string FormatOf(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".iges":
                case ".igs":
                    return "IGES";
                default:
                    return "STEP";
            }
        }

        static string PartNameOf(string stlPath)
        {
            string name = Path.GetFileNameWithoutExtension(stlPath);
            int underscore = name.IndexOf('_');
            return underscore >= 0 && underscore < name.Length - 1 ? name.Substring(underscore + 1) : name;
        }
    }
}
