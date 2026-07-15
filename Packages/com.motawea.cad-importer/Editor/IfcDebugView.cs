using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CADImporter.Editor
{
    /// <summary>How the IFC debug view colours the model.</summary>
    public enum IfcDebugMode
    {
        /// <summary>Imported materials (clears every override).</summary>
        Original,
        /// <summary>One categorical colour per IFC entity type (IfcWall, IfcSlab, ...).</summary>
        ByType,
        /// <summary>One categorical colour per containing building storey.</summary>
        ByStorey,
        /// <summary>Pset *.LoadBearing: bearing / non-bearing / not specified.</summary>
        ByLoadBearing,
        /// <summary>Pset *.IsExternal: external / internal / not specified.</summary>
        ByExternal
    }

    /// <summary>One legend row: a colour, its label and the elements/geometry it covers.</summary>
    public sealed class IfcDebugGroup
    {
        public string Label;
        public Color Color;
        public readonly List<GameObject> Elements = new List<GameObject>();
        public readonly List<Renderer> Renderers = new List<Renderer>();
        public int Triangles;
    }

    /// <summary>
    /// Non-destructive debug colouring of an imported IFC model, driven by the
    /// <see cref="IfcElement"/> data the importer attaches. Solid colouring uses
    /// <see cref="MaterialPropertyBlock"/>s (never serialized); X-ray mode temporarily swaps in
    /// shared transparent ghost materials and always restores the originals — on mode change,
    /// window close, scene save, play mode entry and domain reload. Category visibility rides
    /// on <see cref="SceneVisibilityManager"/> (the Hierarchy "eye"), which is editor-session
    /// state only. The scene, prefab and imported assets are never modified. Separated from
    /// the window so it is testable headlessly.
    /// </summary>
    [InitializeOnLoad]
    public static class IfcDebugPainter
    {
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"); // URP Lit
        static readonly int ColorId = Shader.PropertyToID("_Color");         // Standard

        // X-ray state: original materials per renderer, and one ghost material per category
        // colour. Ghost materials are HideAndDontSave instances — restored/destroyed via the
        // lifecycle hooks below so they can never leak into a saved scene.
        static readonly Dictionary<Renderer, Material[]> Backup = new Dictionary<Renderer, Material[]>();
        static readonly Dictionary<Color, Material> Ghosts = new Dictionary<Color, Material>();
        static float ghostAlpha = 0.3f;

        static IfcDebugPainter()
        {
            AssemblyReloadEvents.beforeAssemblyReload += RestoreMaterials;
            EditorApplication.playModeStateChanged += _ => RestoreMaterials();
            // A scene must never be saved with ghost materials assigned — they are not assets.
            EditorSceneManager.sceneSaving += (_, _2) => RestoreMaterials();
            EditorApplication.quitting += RestoreMaterials;
        }

        /// <summary>True while X-ray ghost materials are assigned somewhere.</summary>
        public static bool XrayActive => Backup.Count > 0;

        /// <summary>Classifies every IfcElement under <paramref name="root"/> for a mode.</summary>
        public static List<IfcDebugGroup> CollectGroups(GameObject root, IfcDebugMode mode)
        {
            var byLabel = new Dictionary<string, IfcDebugGroup>();
            foreach (var elem in root.GetComponentsInChildren<IfcElement>(true))
            {
                var renderers = OwnedRenderers(elem);
                if (renderers.Count == 0) continue; // spatial containers (storeys, site, ...)

                string label = Classify(elem, mode);
                if (!byLabel.TryGetValue(label, out var group))
                    byLabel[label] = group = new IfcDebugGroup { Label = label };

                group.Elements.Add(elem.gameObject);
                group.Renderers.AddRange(renderers);
                group.Triangles += Lod0Triangles(renderers);
            }

            // Stable order (and therefore stable categorical colours) between refreshes.
            var groups = byLabel.Values.OrderByDescending(g => g.Triangles)
                .ThenBy(g => g.Label).ToList();
            AssignColors(groups, mode);
            return groups;
        }

        /// <summary>Applies each group's colour to all its renderers (every LOD level).</summary>
        public static void Apply(List<IfcDebugGroup> groups)
        {
            var mpb = new MaterialPropertyBlock();
            foreach (var g in groups)
            {
                mpb.Clear();
                mpb.SetColor(BaseColorId, g.Color);
                mpb.SetColor(ColorId, g.Color);
                foreach (var r in g.Renderers)
                    if (r != null) r.SetPropertyBlock(mpb);
            }
        }

        /// <summary>
        /// Ghost view: swaps every group renderer to a shared transparent material in the
        /// group's colour so occluded parts show through. Originals are backed up and come
        /// back via <see cref="RestoreMaterials"/>. Colour overrides are cleared first —
        /// a property block's opaque alpha would defeat the transparency.
        /// </summary>
        public static void ApplyXray(List<IfcDebugGroup> groups, float alpha)
        {
            ghostAlpha = Mathf.Clamp(alpha, 0.02f, 1f);
            foreach (var g in groups)
            {
                var ghost = GhostMaterial(g.Color);
                foreach (var r in g.Renderers)
                {
                    if (r == null) continue;
                    r.SetPropertyBlock(null);
                    if (!Backup.ContainsKey(r))
                        Backup[r] = r.sharedMaterials;
                    var mats = new Material[r.sharedMaterials.Length];
                    for (int i = 0; i < mats.Length; i++) mats[i] = ghost;
                    r.sharedMaterials = mats;
                }
            }
        }

        /// <summary>Live-updates the transparency of every ghost material.</summary>
        public static void SetXrayAlpha(float alpha)
        {
            ghostAlpha = Mathf.Clamp(alpha, 0.02f, 1f);
            foreach (var kv in Ghosts)
            {
                var c = kv.Key;
                c.a = ghostAlpha;
                if (kv.Value != null)
                {
                    if (kv.Value.HasProperty(BaseColorId)) kv.Value.SetColor(BaseColorId, c);
                    if (kv.Value.HasProperty(ColorId)) kv.Value.SetColor(ColorId, c);
                }
            }
        }

        /// <summary>Puts every original material back and destroys the ghost materials.</summary>
        public static void RestoreMaterials()
        {
            foreach (var kv in Backup)
                if (kv.Key != null)
                    kv.Key.sharedMaterials = kv.Value;
            Backup.Clear();
            foreach (var m in Ghosts.Values)
                if (m != null) Object.DestroyImmediate(m);
            Ghosts.Clear();
        }

        /// <summary>Removes every colour override under <paramref name="root"/> and restores
        /// any X-ray materials and hidden categories.</summary>
        public static void Clear(GameObject root)
        {
            RestoreMaterials();
            if (root == null) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                r.SetPropertyBlock(null);
            SceneVisibilityManager.instance.Show(root, true);
        }

        /// <summary>Shows or hides one category via the Hierarchy's scene-visibility "eye".</summary>
        public static void SetGroupVisible(IfcDebugGroup group, bool visible)
        {
            var svm = SceneVisibilityManager.instance;
            foreach (var go in group.Elements)
            {
                if (go == null) continue;
                if (visible) svm.Show(go, true);
                else svm.Hide(go, true);
            }
        }

        public static bool IsGroupHidden(IfcDebugGroup group)
        {
            var svm = SceneVisibilityManager.instance;
            foreach (var go in group.Elements)
                if (go != null) return svm.IsHidden(go, false);
            return false;
        }

        static Material GhostMaterial(Color color)
        {
            if (Ghosts.TryGetValue(color, out var cached) && cached != null) return cached;
            var c = color;
            c.a = ghostAlpha;
            // The factory configures a correct transparent surface for the active pipeline
            // (URP Lit or Standard) — the same path imported translucent glazing uses.
            var mat = CadMaterialFactory.Create(new CADMaterialInfo
            {
                Name = "IfcDebug_Ghost",
                Color = c,
                Smoothness = 0.1f,
                AlphaMode = CADAlphaMode.Blend,
                DoubleSided = true // hidden back faces read better while ghosted
            }, false, c, null).Material;
            mat.hideFlags = HideFlags.HideAndDontSave;
            Ghosts[color] = mat;
            return mat;
        }

        static string Classify(IfcElement elem, IfcDebugMode mode)
        {
            switch (mode)
            {
                case IfcDebugMode.ByType:
                    return string.IsNullOrEmpty(elem.ifcType) ? "(unknown)" : elem.ifcType;

                case IfcDebugMode.ByStorey:
                {
                    for (var t = elem.transform.parent; t != null; t = t.parent)
                    {
                        var anc = t.GetComponent<IfcElement>();
                        if (anc != null && anc.ifcType == "IfcBuildingStorey")
                            return anc.gameObject.name;
                    }
                    return "(no storey)";
                }

                case IfcDebugMode.ByLoadBearing:
                    switch (PropBySuffix(elem, ".LoadBearing"))
                    {
                        case "true": return "Load-bearing";
                        case "false": return "Non-bearing";
                        default: return "Not specified";
                    }

                case IfcDebugMode.ByExternal:
                    switch (PropBySuffix(elem, ".IsExternal"))
                    {
                        case "true": return "External";
                        case "false": return "Internal";
                        default: return "Not specified";
                    }

                default:
                    return "";
            }
        }

        /// <summary>First property whose flattened name ends with <paramref name="suffix"/> —
        /// the pset prefix varies by element type (Pset_WallCommon, Pset_SlabCommon, ...).</summary>
        static string PropBySuffix(IfcElement elem, string suffix)
        {
            if (elem.properties == null) return null;
            for (int i = 0; i < elem.properties.Count; i++)
                if (elem.properties[i].name.EndsWith(suffix, System.StringComparison.Ordinal))
                    return elem.properties[i].value;
            return null;
        }

        /// <summary>
        /// Renderers belonging to this element itself: those in its subtree whose nearest
        /// IfcElement ancestor is this element (LOD children and multi-mesh splits are plain
        /// GameObjects; nested elements own their own renderers).
        /// </summary>
        static List<Renderer> OwnedRenderers(IfcElement elem)
        {
            var owned = new List<Renderer>();
            foreach (var r in elem.GetComponentsInChildren<Renderer>(true))
            {
                var t = r.transform;
                while (t != null && t.GetComponent<IfcElement>() == null)
                    t = t.parent;
                if (t == elem.transform) owned.Add(r);
            }
            return owned;
        }

        /// <summary>Triangle count of the full-detail geometry only (skips LOD1+ renderers).</summary>
        static int Lod0Triangles(List<Renderer> renderers)
        {
            var lowerLods = new HashSet<Renderer>();
            foreach (var r in renderers)
            {
                var group = r.GetComponentInParent<LODGroup>();
                if (group == null) continue;
                var lods = group.GetLODs();
                for (int i = 1; i < lods.Length; i++)
                    foreach (var lr in lods[i].renderers)
                        lowerLods.Add(lr);
            }

            int tris = 0;
            foreach (var r in renderers)
            {
                if (lowerLods.Contains(r)) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                    tris += (int)(mf.sharedMesh.GetIndexCount(0) / 3);
            }
            return tris;
        }

        static void AssignColors(List<IfcDebugGroup> groups, IfcDebugMode mode)
        {
            // Semantic modes use fixed, meaningful colours; categorical modes get maximally
            // distinct hues via the golden ratio, in the (stable) group order.
            if (mode == IfcDebugMode.ByLoadBearing || mode == IfcDebugMode.ByExternal)
            {
                foreach (var g in groups)
                {
                    switch (g.Label)
                    {
                        case "Load-bearing": g.Color = new Color(0.85f, 0.33f, 0.25f); break;
                        case "Non-bearing":  g.Color = new Color(0.45f, 0.65f, 0.85f); break;
                        case "External":     g.Color = new Color(0.90f, 0.62f, 0.20f); break;
                        case "Internal":     g.Color = new Color(0.40f, 0.70f, 0.55f); break;
                        default:             g.Color = new Color(0.62f, 0.62f, 0.62f); break;
                    }
                }
                return;
            }

            const float golden = 0.6180339887f;
            for (int i = 0; i < groups.Count; i++)
                groups[i].Color = Color.HSVToRGB((i * golden) % 1f, 0.65f, 0.95f);
        }
    }

    /// <summary>
    /// Visual + statistical BIM debugging for imported IFC models: recolour a model in the
    /// scene by IFC type, storey or pset flags — solid or X-ray (see-through) — with a
    /// colour-matched legend showing element and triangle statistics per category. Legend
    /// rows have visibility eyes (Alt-click to solo), click to select, double-click to frame.
    /// All colouring is transient — nothing in the scene or assets is modified.
    /// </summary>
    public class IfcDebugWindow : EditorWindow
    {
        const string AlphaPrefsKey = "CADImporter.IfcDebug.XrayAlpha";

        CADModelInfo target;
        IfcDebugMode mode = IfcDebugMode.Original;
        List<IfcDebugGroup> groups;
        Vector2 scroll;
        bool xray;
        float xrayAlpha = 0.3f;
        string search = "";
        double lastRowClick;
        string lastRowLabel;

        [MenuItem("Tools/CAD Importer IFC Debug")]
        public static void Open()
        {
            var window = GetWindow<IfcDebugWindow>("IFC Debug");
            window.minSize = new Vector2(380, 360);
        }

        void OnEnable()
        {
            xrayAlpha = EditorPrefs.GetFloat(AlphaPrefsKey, 0.3f);
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        void OnDisable()
        {
            EditorPrefs.SetFloat(AlphaPrefsKey, xrayAlpha);
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            if (target != null) IfcDebugPainter.Clear(target.gameObject);
            else IfcDebugPainter.RestoreMaterials();
        }

        // The painter strips ghost materials right before a scene save (they must never be
        // serialized); put the current view back once the save is done.
        void OnSceneSaved(UnityEngine.SceneManagement.Scene _)
        {
            if (target != null && mode != IfcDebugMode.Original)
                ApplyMode();
        }

        void OnGUI()
        {
            DrawTargetPicker();
            if (target == null)
            {
                EditorGUILayout.HelpBox(
                    "Pick an imported CAD model instance in the open scene. IFC models expose " +
                    "their BIM data (type, storey, property sets) for the modes below.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.Space(4);
            var newMode = (IfcDebugMode)EditorGUILayout.EnumPopup("Draw mode", mode);
            if (newMode != mode)
            {
                mode = newMode;
                ApplyMode();
            }

            using (new EditorGUI.DisabledScope(mode == IfcDebugMode.Original))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool newXray = EditorGUILayout.ToggleLeft(
                        new GUIContent("X-Ray", "See-through ghost materials so hidden parts " +
                                                "show. Originals restore automatically."),
                        xray, GUILayout.Width(70));
                    if (newXray != xray)
                    {
                        xray = newXray;
                        ApplyMode();
                    }

                    using (new EditorGUI.DisabledScope(!xray))
                    {
                        EditorGUI.BeginChangeCheck();
                        xrayAlpha = EditorGUILayout.Slider(xrayAlpha, 0.05f, 0.8f);
                        if (EditorGUI.EndChangeCheck())
                            IfcDebugPainter.SetXrayAlpha(xrayAlpha);
                    }
                }
            }

            if (mode != IfcDebugMode.Original && groups != null)
                DrawLegend();
        }

        void DrawTargetPicker()
        {
            var roots = FindObjectsByType<CADModelInfo>(FindObjectsSortMode.None)
                .Where(m => m.gameObject.scene.IsValid()).ToArray();

            using (new EditorGUILayout.HorizontalScope())
            {
                int current = System.Array.IndexOf(roots, target);
                var names = roots.Select(m => m.gameObject.name).ToArray();
                int picked = EditorGUILayout.Popup("Model", current, names);
                if (picked != current && picked >= 0)
                    SetTarget(roots[picked]);

                using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
                {
                    if (GUILayout.Button("From Selection", GUILayout.Width(110)))
                    {
                        var info = Selection.activeGameObject != null
                            ? Selection.activeGameObject.GetComponentInParent<CADModelInfo>()
                            : null;
                        if (info != null) SetTarget(info);
                    }
                }
            }

            if (target == null && roots.Length == 1)
                SetTarget(roots[0]);
        }

        void SetTarget(CADModelInfo next)
        {
            if (target != null && target != next)
                IfcDebugPainter.Clear(target.gameObject);
            target = next;
            ApplyMode();
        }

        void ApplyMode()
        {
            if (target == null) return;
            IfcDebugPainter.RestoreMaterials();
            foreach (var r in target.GetComponentsInChildren<Renderer>(true))
                r.SetPropertyBlock(null);

            if (mode == IfcDebugMode.Original)
            {
                groups = null;
                SceneVisibilityManager.instance.Show(target.gameObject, true);
                SceneView.RepaintAll();
                return;
            }

            groups = IfcDebugPainter.CollectGroups(target.gameObject, mode);
            if (xray) IfcDebugPainter.ApplyXray(groups, xrayAlpha);
            else IfcDebugPainter.Apply(groups);
            SceneView.RepaintAll();
        }

        void DrawLegend()
        {
            EditorGUILayout.Space(6);

            long totalTris = groups.Sum(g => (long)g.Triangles);
            int totalElems = groups.Sum(g => g.Elements.Count);
            int hidden = groups.Count(IfcDebugPainter.IsGroupHidden);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    $"{totalElems} elements in {groups.Count} categories — " +
                    $"{totalTris:N0} triangles (LOD0)", EditorStyles.boldLabel);
                if (hidden > 0 && GUILayout.Button($"Show All ({hidden} hidden)", GUILayout.Width(130)))
                {
                    SceneVisibilityManager.instance.Show(target.gameObject, true);
                    SceneView.RepaintAll();
                }
            }

            search = EditorGUILayout.TextField(GUIContent.none, search, EditorStyles.toolbarSearchField);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var g in groups)
            {
                if (!string.IsNullOrEmpty(search) &&
                    g.Label.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                DrawRow(g, totalTris);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.HelpBox(
                "Eye: hide/show a category (Alt-click to solo). Click a row to select its " +
                "elements, double-click to frame them. Colours and X-ray are transient " +
                "overrides — the scene, prefab and imported assets are untouched.",
                MessageType.None);
        }

        void DrawRow(IfcDebugGroup g, long totalTris)
        {
            var row = EditorGUILayout.GetControlRect(false, 20);
            bool isHidden = IfcDebugPainter.IsGroupHidden(g);

            // Visibility eye, matching the Hierarchy's icons. Alt-click isolates the category.
            var eyeRect = new Rect(row.x, row.y + 1, 22, 18);
            var eyeIcon = EditorGUIUtility.IconContent(
                isHidden ? "scenevis_hidden_hover" : "scenevis_visible_hover");
            if (GUI.Button(eyeRect, eyeIcon, EditorStyles.iconButton))
            {
                if (Event.current.alt)
                {
                    foreach (var other in groups)
                        IfcDebugPainter.SetGroupVisible(other, other == g);
                }
                else
                {
                    IfcDebugPainter.SetGroupVisible(g, isHidden);
                }
                SceneView.RepaintAll();
            }

            var swatch = new Rect(row.x + 26, row.y + 3, 14, 14);
            EditorGUI.DrawRect(swatch, g.Color);

            float pct = totalTris > 0 ? 100f * g.Triangles / totalTris : 0f;
            var label = new Rect(row.x + 46, row.y, row.width - 46, row.height);
            GUI.Label(label,
                $"{g.Label}   —   {g.Elements.Count} elem, {g.Triangles:N0} tris ({pct:F1}%)",
                isHidden ? EditorStyles.miniLabel : EditorStyles.label);

            if (Event.current.type == EventType.MouseDown && label.Contains(Event.current.mousePosition))
            {
                Selection.objects = g.Elements.ToArray();
                bool doubleClick = g.Label == lastRowLabel &&
                                   EditorApplication.timeSinceStartup - lastRowClick < 0.4;
                lastRowClick = EditorApplication.timeSinceStartup;
                lastRowLabel = g.Label;
                if (doubleClick && SceneView.lastActiveSceneView != null)
                    SceneView.lastActiveSceneView.FrameSelected();
                Event.current.Use();
            }
        }
    }
}
