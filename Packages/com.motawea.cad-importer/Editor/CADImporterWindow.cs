using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CADImporter.Editor
{
    /// <summary>
    /// Batch CAD import window: pick or drop external CAD files, tune import settings once,
    /// and import them all into the project (optionally placing instances in the open scene).
    /// Files are copied into the project so the ScriptedImporters own them from then on —
    /// tweaking settings on the asset later just reimports.
    /// </summary>
    public class CADImporterWindow : EditorWindow
    {
        // ScriptableObject wrapper so the settings class renders through the standard
        // SerializedObject inspector UI (tooltips, ranges, arrays).
        sealed class SettingsHolder : ScriptableObject
        {
            public CADImportSettings settings = new CADImportSettings();
        }

        const string SettingsPrefsKey = "CADImporter.WindowSettings";
        const string FolderPrefsKey = "CADImporter.TargetFolder";

        static readonly string[] SupportedExtensions =
            { ".stl", ".ply", ".obj", ".step", ".stp", ".iges", ".igs", ".gltf", ".glb" };

        readonly List<string> files = new List<string>();
        SettingsHolder holder;
        SerializedObject serializedSettings;
        Vector2 scroll;
        string targetFolder = "Assets/CADModels";
        bool addToScene = true;
        string lastReport = "";

        [MenuItem("Tools/CAD Importer")]
        public static void Open()
        {
            var window = GetWindow<CADImporterWindow>("CAD Importer");
            window.minSize = new Vector2(380, 480);
        }

        void OnEnable()
        {
            holder = CreateInstance<SettingsHolder>();
            holder.hideFlags = HideFlags.DontSave;
            string json = EditorPrefs.GetString(SettingsPrefsKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { JsonUtility.FromJsonOverwrite(json, holder.settings); }
                catch { holder.settings = new CADImportSettings(); }
            }
            targetFolder = EditorPrefs.GetString(FolderPrefsKey, "Assets/CADModels");
            serializedSettings = new SerializedObject(holder);
        }

        void OnDisable()
        {
            PersistSettings();
            if (holder != null) DestroyImmediate(holder);
        }

        void PersistSettings()
        {
            if (holder != null)
                EditorPrefs.SetString(SettingsPrefsKey, JsonUtility.ToJson(holder.settings));
            EditorPrefs.SetString(FolderPrefsKey, targetFolder);
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);

            DrawFileList();
            EditorGUILayout.Space(6);
            DrawSettings();
            EditorGUILayout.Space(6);
            DrawStepConverterStatus();
            EditorGUILayout.Space(6);
            DrawOutputOptions();
            EditorGUILayout.Space(10);
            DrawImportButton();

            if (!string.IsNullOrEmpty(lastReport))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(lastReport, MessageType.Info);
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawFileList()
        {
            EditorGUILayout.LabelField("CAD Files", EditorStyles.boldLabel);

            var dropRect = GUILayoutUtility.GetRect(0, 48, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, files.Count == 0
                ? "Drop CAD files here (STL, PLY, OBJ, STEP, IGES)"
                : $"{files.Count} file(s) queued — drop more to add", EditorStyles.helpBox);
            HandleDragAndDrop(dropRect);

            for (int i = files.Count - 1; i >= 0; i--)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(Path.GetFileName(files[i]), GUILayout.MinWidth(120));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("✕", GUILayout.Width(22)))
                    files.RemoveAt(i);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Add Files…"))
            {
                string picked = EditorUtility.OpenFilePanel("Select CAD file",
                    "", "stl,ply,obj,step,stp,iges,igs,gltf,glb");
                if (!string.IsNullOrEmpty(picked) && !files.Contains(picked))
                    files.Add(picked);
            }
            using (new EditorGUI.DisabledScope(files.Count == 0))
            {
                if (GUILayout.Button("Clear"))
                    files.Clear();
            }
            EditorGUILayout.EndHorizontal();
        }

        void HandleDragAndDrop(Rect rect)
        {
            var evt = Event.current;
            if (!rect.Contains(evt.mousePosition)) return;

            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                bool any = DragAndDrop.paths.Any(p =>
                    SupportedExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
                DragAndDrop.visualMode = any ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;

                if (evt.type == EventType.DragPerform && any)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var p in DragAndDrop.paths)
                    {
                        if (SupportedExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()) &&
                            !files.Contains(p))
                            files.Add(p);
                    }
                }
                evt.Use();
            }
        }

        void DrawSettings()
        {
            EditorGUILayout.LabelField("Import Settings", EditorStyles.boldLabel);
            serializedSettings.Update();
            var prop = serializedSettings.FindProperty("settings");
            EditorGUI.BeginChangeCheck();
            foreach (SerializedProperty child in GetChildren(prop))
                EditorGUILayout.PropertyField(child, true);
            if (EditorGUI.EndChangeCheck())
            {
                serializedSettings.ApplyModifiedPropertiesWithoutUndo();
                PersistSettings();
            }
        }

        static IEnumerable<SerializedProperty> GetChildren(SerializedProperty prop)
        {
            var it = prop.Copy();
            var end = prop.GetEndProperty();
            if (!it.NextVisible(true)) yield break;
            while (!SerializedProperty.EqualContents(it, end))
            {
                yield return it;
                if (!it.NextVisible(false)) break;
            }
        }

        void DrawStepConverterStatus()
        {
            EditorGUILayout.LabelField("STEP / IGES Converter", EditorStyles.boldLabel);
            string path = StepConverter.ConverterPath;
            bool valid = !string.IsNullOrEmpty(path) && File.Exists(path);

            if (valid)
            {
                EditorGUILayout.HelpBox($"FreeCAD found:\n{path}", MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "FreeCAD not configured. STL/PLY/OBJ import works without it; STEP and IGES " +
                    "files need FreeCAD's Open CASCADE kernel for tessellation (free, freecad.org).",
                    MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Auto-Detect"))
            {
                var found = StepConverter.AutoDetect();
                if (found != null)
                {
                    StepConverter.ConverterPath = found;
                    ShowNotification(new GUIContent("FreeCAD found"));
                }
                else ShowNotification(new GUIContent("FreeCAD not found"));
            }
            if (GUILayout.Button("Browse…"))
            {
                string picked = EditorUtility.OpenFilePanel("Locate FreeCADCmd.exe", "", "exe");
                if (!string.IsNullOrEmpty(picked))
                    StepConverter.ConverterPath = picked;
            }
            EditorGUILayout.EndHorizontal();
        }

        void DrawOutputOptions()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            targetFolder = EditorGUILayout.TextField("Target Folder", targetFolder);
            if (GUILayout.Button("…", GUILayout.Width(24)))
            {
                string abs = EditorUtility.OpenFolderPanel("Target folder inside Assets",
                    Application.dataPath, "");
                if (!string.IsNullOrEmpty(abs))
                {
                    if (abs.StartsWith(Application.dataPath, StringComparison.OrdinalIgnoreCase))
                        targetFolder = "Assets" + abs.Substring(Application.dataPath.Length).Replace('\\', '/');
                    else
                        EditorUtility.DisplayDialog("CAD Importer",
                            "The target folder must be inside this project's Assets folder.", "OK");
                }
            }
            EditorGUILayout.EndHorizontal();
            addToScene = EditorGUILayout.ToggleLeft("Add imported model(s) to the open scene", addToScene);
        }

        void DrawImportButton()
        {
            using (new EditorGUI.DisabledScope(files.Count == 0))
            {
                if (GUILayout.Button($"Import {files.Count} File(s)", GUILayout.Height(32)))
                    ImportQueued();
            }
        }

        void ImportQueued()
        {
            PersistSettings();
            EnsureFolder(targetFolder);

            var imported = new List<string>();
            var failed = new List<string>();

            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    string src = files[i];
                    EditorUtility.DisplayProgressBar("CAD Importer",
                        $"Importing {Path.GetFileName(src)} ({i + 1}/{files.Count})",
                        (float)i / files.Count);
                    try
                    {
                        string assetPath = ImportOne(src);
                        imported.Add(assetPath);
                    }
                    catch (Exception e)
                    {
                        failed.Add($"{Path.GetFileName(src)}: {e.Message}");
                        Debug.LogError($"CAD Importer: {src} failed — {e}");
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (addToScene)
            {
                foreach (var assetPath in imported)
                {
                    var main = AssetDatabase.LoadMainAssetAtPath(assetPath) as GameObject;
                    if (main != null)
                    {
                        var instance = (GameObject)PrefabUtility.InstantiatePrefab(main);
                        Undo.RegisterCreatedObjectUndo(instance, "Import CAD Model");
                    }
                }
            }

            if (imported.Count > 0)
            {
                var lastAsset = AssetDatabase.LoadMainAssetAtPath(imported[imported.Count - 1]);
                if (lastAsset != null) EditorGUIUtility.PingObject(lastAsset);
            }

            lastReport = $"Imported {imported.Count}/{files.Count} file(s) into {targetFolder}."
                + (failed.Count > 0 ? "\nFailed:\n  " + string.Join("\n  ", failed) : "");
            files.Clear();
        }

        string ImportOne(string sourcePath)
        {
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{targetFolder}/{Path.GetFileName(sourcePath)}");

            File.Copy(sourcePath, assetPath, false);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            // Push the window's settings onto the new asset's importer, then reimport once.
            var importer = AssetImporter.GetAtPath(assetPath);
            bool applied = true;
            switch (importer)
            {
                case StlScriptedImporter stl: stl.settings = holder.settings.Clone(); break;
                case PlyScriptedImporter ply: ply.settings = holder.settings.Clone(); break;
                case StepScriptedImporter step: step.settings = holder.settings.Clone(); break;
                case GltfScriptedImporter gltf:
                    // glTF's unit and axis convention are fixed by the spec; keep them correct
                    // regardless of the window's shared (CAD-oriented) defaults.
                    var gs = holder.settings.Clone();
                    gs.sourceUnit = SourceUnit.Meters;
                    gs.sourceOrientation = SourceOrientation.YUpRightHanded;
                    gltf.settings = gs;
                    break;
                default:
                    applied = false; // e.g. OBJ → Unity's native model importer
                    break;
            }
            if (applied)
            {
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
            }
            else if (ext == ".obj")
            {
                Debug.Log("CAD Importer: .obj files are handled by Unity's native model importer; " +
                          "CAD Importer settings do not apply. Use the asset's own import settings.");
            }
            return assetPath;
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string[] parts = folder.Split('/');
            string current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
