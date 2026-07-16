using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CADImporter.Editor
{
    /// <summary>How the IFC inspector colours the model.</summary>
    public enum IfcInspectorMode
    {
        /// <summary>Imported materials (clears every override and restores visibility).</summary>
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

    /// <summary>How a legend row's own hide rule stands, and whether other modes affect it.</summary>
    public enum IfcCategoryState
    {
        /// <summary>No rule here, and nothing in the category is hidden by another mode.</summary>
        Visible,
        /// <summary>This category's own hide rule is set.</summary>
        Hidden,
        /// <summary>No rule here, but another mode's rule hides part of the category.</summary>
        Partial
    }

    /// <summary>
    /// One renderable IFC element, classified for every mode up front. Scanning is the
    /// expensive part, so it happens once per model rather than once per mode switch.
    /// </summary>
    public sealed class IfcInspectorElement
    {
        public GameObject Go;
        public Renderer[] Renderers;
        /// <summary>Full-detail triangles (LOD0 only), summed over every submesh.</summary>
        public int Triangles;
        /// <summary>Category label per mode, indexed by <c>(int)IfcInspectorMode</c>.</summary>
        public string[] Labels;

        internal bool Hidden;   // wanted state, derived from the active hide rules
        internal bool Applied;  // last state actually pushed to SceneVisibilityManager
    }

    /// <summary>One legend row: a colour, its label and the elements it covers.</summary>
    public sealed class IfcInspectorGroup
    {
        public string Label;
        public Color Color;
        public readonly List<IfcInspectorElement> Elements = new List<IfcInspectorElement>();
        public int Triangles;
        /// <summary>Elements currently hidden by any rule, this category's or another mode's.</summary>
        public int HiddenCount;

        internal string RowText;
        public int Count => Elements.Count;
    }

    /// <summary>
    /// A scanned IFC model plus its category hide rules.
    ///
    /// Visibility is stored as rules — a set of hidden category labels per mode — and never
    /// read back out of the scene. An element is hidden when ANY mode's rule matches it, so
    /// hiding composes across categories: hide a storey, switch to By Type and toggle doors,
    /// and that storey's doors stay hidden because the storey rule still matches them. Reading
    /// the state back from <see cref="SceneVisibilityManager"/> instead cannot express this —
    /// the scene only knows a flat per-GameObject flag, with no record of which category asked.
    ///
    /// Rules also survive a rescan and a mode switch, because they are labels rather than
    /// object references.
    ///
    /// Colouring uses <see cref="MaterialPropertyBlock"/>s and visibility rides
    /// <see cref="SceneVisibilityManager"/> (the Hierarchy "eye"); both are editor-session
    /// state, so the scene, prefab and imported assets are never modified.
    /// </summary>
    public sealed class IfcInspectorModel
    {
        const int ModeCount = 5; // IfcInspectorMode members
        const string NoStorey = "(no storey)";
        const string Unknown = "(unknown)";

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor"); // URP Lit
        static readonly int ColorId = Shader.PropertyToID("_Color");         // Standard

        public readonly GameObject Root;
        public readonly IfcInspectorElement[] Elements;

        readonly HashSet<string>[] hiddenByMode;      // the rules, indexed by (int)mode
        readonly List<IfcInspectorGroup>[] groupsByMode; // built lazily, per mode

        /// <summary>Elements hidden by any rule in any mode — what "Show All" would restore.</summary>
        public int HiddenElements { get; private set; }

        IfcInspectorModel(GameObject root, IfcInspectorElement[] elements)
        {
            Root = root;
            Elements = elements;
            groupsByMode = new List<IfcInspectorGroup>[ModeCount];
            hiddenByMode = new HashSet<string>[ModeCount];
            for (int i = 0; i < ModeCount; i++)
                hiddenByMode[i] = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Scans every <see cref="IfcElement"/> under <paramref name="root"/>. Hide rules from
        /// <paramref name="previous"/> carry over when it describes the same root, so editing
        /// the scene does not silently drop what the user hid.
        /// </summary>
        public static IfcInspectorModel Scan(GameObject root, IfcInspectorModel previous = null)
        {
            var model = new IfcInspectorModel(root, ScanElements(root));

            if (previous != null && previous.Root == root)
                for (int i = 0; i < ModeCount; i++)
                    model.hiddenByMode[i].UnionWith(previous.hiddenByMode[i]);

            // Seed from the scene so we only push the visibility changes we actually need.
            var svm = SceneVisibilityManager.instance;
            foreach (var e in model.Elements)
                e.Applied = e.Go != null && svm.IsHidden(e.Go, false);

            model.Recompute();
            return model;
        }

        static IfcInspectorElement[] ScanElements(GameObject root)
        {
            var elements = root.GetComponentsInChildren<IfcElement>(true);

            // Nearest-IfcElement-ancestor per transform. Seeded with the elements themselves,
            // then memoised on the way up, so each transform is resolved once instead of once
            // per renderer (the old code walked the tree again for every element).
            var owner = new Dictionary<Transform, IfcElement>();
            foreach (var e in elements)
                owner[e.transform] = e;

            // Every LODGroup's lower levels, resolved once. GetLODs() allocates, so calling it
            // per renderer (as before) allocated an array per renderer on a 3,500-element model.
            var lowerLods = new HashSet<Renderer>();
            foreach (var lodGroup in root.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lodGroup.GetLODs();
                for (int i = 1; i < lods.Length; i++)
                {
                    var rs = lods[i].renderers;
                    if (rs == null) continue;
                    foreach (var r in rs)
                        if (r != null) lowerLods.Add(r);
                }
            }

            // One pass over renderers, each assigned to the element that owns it.
            var byElement = new Dictionary<IfcElement, List<Renderer>>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var e = NearestElement(r.transform, root.transform, owner);
                if (e == null) continue;
                if (!byElement.TryGetValue(e, out var list))
                    byElement[e] = list = new List<Renderer>();
                list.Add(r);
            }

            var storeys = new Dictionary<Transform, string>();
            var result = new List<IfcInspectorElement>(byElement.Count);
            foreach (var elem in elements)
            {
                // Spatial containers (site, storeys) own no geometry of their own.
                if (!byElement.TryGetValue(elem, out var renderers)) continue;

                var labels = new string[ModeCount];
                labels[(int)IfcInspectorMode.Original] = string.Empty;
                labels[(int)IfcInspectorMode.ByType] =
                    string.IsNullOrEmpty(elem.ifcType) ? Unknown : elem.ifcType;
                labels[(int)IfcInspectorMode.ByStorey] = StoreyOf(elem.transform.parent, storeys);
                labels[(int)IfcInspectorMode.ByLoadBearing] = Flag(elem, ".LoadBearing", "Load-bearing", "Non-bearing");
                labels[(int)IfcInspectorMode.ByExternal] = Flag(elem, ".IsExternal", "External", "Internal");

                result.Add(new IfcInspectorElement
                {
                    Go = elem.gameObject,
                    Renderers = renderers.ToArray(),
                    Triangles = Lod0Triangles(renderers, lowerLods),
                    Labels = labels
                });
            }
            return result.ToArray();
        }

        static IfcElement NearestElement(Transform t, Transform root, Dictionary<Transform, IfcElement> memo)
        {
            if (t == null) return null;
            if (memo.TryGetValue(t, out var found)) return found;

            // Stop at the model root: never attribute geometry to an element above it.
            var result = t == root ? null : NearestElement(t.parent, root, memo);
            memo[t] = result;
            return result;
        }

        static string StoreyOf(Transform t, Dictionary<Transform, string> memo)
        {
            if (t == null) return NoStorey;
            if (memo.TryGetValue(t, out var cached)) return cached;

            var elem = t.GetComponent<IfcElement>();
            var result = elem != null && elem.ifcType == "IfcBuildingStorey"
                ? t.gameObject.name
                : StoreyOf(t.parent, memo);
            memo[t] = result;
            return result;
        }

        /// <summary>Classifies a boolean pset flag; the pset prefix varies by element type
        /// (Pset_WallCommon, Pset_SlabCommon, ...), so match on the suffix.</summary>
        static string Flag(IfcElement elem, string suffix, string onLabel, string offLabel)
        {
            var props = elem.properties;
            if (props == null) return "Not specified";
            for (int i = 0; i < props.Count; i++)
            {
                if (!props[i].name.EndsWith(suffix, StringComparison.Ordinal)) continue;
                var v = props[i].value;
                if (v == "true") return onLabel;
                if (v == "false") return offLabel;
                return "Not specified";
            }
            return "Not specified";
        }

        /// <summary>Full-detail triangles: LOD1+ renderers are skipped, every submesh counted
        /// (multi-material parts carry one submesh each, so counting submesh 0 undercounts).</summary>
        static int Lod0Triangles(List<Renderer> renderers, HashSet<Renderer> lowerLods)
        {
            int tris = 0;
            foreach (var r in renderers)
            {
                if (lowerLods.Contains(r)) continue;
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                for (int i = 0; i < mesh.subMeshCount; i++)
                    if (mesh.GetTopology(i) == MeshTopology.Triangles)
                        tris += (int)(mesh.GetIndexCount(i) / 3);
            }
            return tris;
        }

        /// <summary>The legend for a mode, built once and reused across repaints.</summary>
        public List<IfcInspectorGroup> Groups(IfcInspectorMode mode)
        {
            int m = (int)mode;
            if (groupsByMode[m] != null) return groupsByMode[m];

            var list = new List<IfcInspectorGroup>();
            if (mode != IfcInspectorMode.Original)
            {
                var byLabel = new Dictionary<string, IfcInspectorGroup>(StringComparer.Ordinal);
                foreach (var e in Elements)
                {
                    var label = e.Labels[m];
                    if (!byLabel.TryGetValue(label, out var g))
                        byLabel[label] = g = new IfcInspectorGroup { Label = label };
                    g.Elements.Add(e);
                    g.Triangles += e.Triangles;
                }

                // Stable order (and therefore stable categorical colours) between refreshes.
                list = byLabel.Values
                    .OrderByDescending(g => g.Triangles)
                    .ThenBy(g => g.Label, StringComparer.Ordinal)
                    .ToList();

                AssignColors(list, mode);

                long total = 0;
                foreach (var g in list) total += g.Triangles;
                foreach (var g in list)
                {
                    float pct = total > 0 ? 100f * g.Triangles / total : 0f;
                    g.RowText = $"{g.Label}   —   {g.Count} elem, {g.Triangles:N0} tris ({pct:F1}%)";
                }
                RefreshHiddenCounts(list);
            }

            groupsByMode[m] = list;
            return list;
        }

        public bool IsCategoryHidden(IfcInspectorMode mode, string label) =>
            hiddenByMode[(int)mode].Contains(label);

        /// <summary>
        /// The eye reflects this category's OWN rule; <see cref="IfcCategoryState.Partial"/>
        /// means another mode's rule is hiding some of these elements.
        /// </summary>
        public IfcCategoryState StateOf(IfcInspectorGroup group, IfcInspectorMode mode)
        {
            if (IsCategoryHidden(mode, group.Label)) return IfcCategoryState.Hidden;
            return group.HiddenCount > 0 ? IfcCategoryState.Partial : IfcCategoryState.Visible;
        }

        public void SetCategoryHidden(IfcInspectorMode mode, string label, bool hidden)
        {
            var rules = hiddenByMode[(int)mode];
            bool changed = hidden ? rules.Add(label) : rules.Remove(label);
            if (changed) Recompute();
        }

        /// <summary>Shows only this category: drops every rule, then hides the other labels of
        /// this mode. Clearing the other modes is what makes a solo actually solo.</summary>
        public void Solo(IfcInspectorMode mode, string label)
        {
            foreach (var rules in hiddenByMode) rules.Clear();

            var own = hiddenByMode[(int)mode];
            foreach (var g in Groups(mode))
                if (!string.Equals(g.Label, label, StringComparison.Ordinal))
                    own.Add(g.Label);

            Recompute();
        }

        public void ShowAll()
        {
            bool any = false;
            foreach (var rules in hiddenByMode)
                any |= rules.Count > 0;
            if (!any) return;

            foreach (var rules in hiddenByMode) rules.Clear();
            Recompute();
        }

        public void ApplyColors(IfcInspectorMode mode)
        {
            ClearColors();
            if (mode == IfcInspectorMode.Original) return;

            var mpb = new MaterialPropertyBlock();
            foreach (var g in Groups(mode))
            {
                mpb.Clear();
                mpb.SetColor(BaseColorId, g.Color);
                mpb.SetColor(ColorId, g.Color);
                foreach (var e in g.Elements)
                    foreach (var r in e.Renderers)
                        if (r != null) r.SetPropertyBlock(mpb);
            }
        }

        public void ClearColors()
        {
            foreach (var e in Elements)
                foreach (var r in e.Renderers)
                    if (r != null) r.SetPropertyBlock(null);
        }

        /// <summary>Drops every override and every hide rule — the model as it was imported.</summary>
        public void Restore()
        {
            ClearColors();
            foreach (var rules in hiddenByMode) rules.Clear();

            if (Root != null) SceneVisibilityManager.instance.Show(Root, true);
            foreach (var e in Elements) e.Hidden = e.Applied = false;

            HiddenElements = 0;
            foreach (var list in groupsByMode)
                if (list != null) RefreshHiddenCounts(list);
        }

        /// <summary>Re-derives every element's visibility from the rules, then pushes only the
        /// elements whose state actually changed.</summary>
        void Recompute()
        {
            HiddenElements = 0;
            foreach (var e in Elements)
            {
                bool hidden = false;
                for (int m = 1; m < ModeCount; m++) // Original carries no rules
                {
                    var rules = hiddenByMode[m];
                    if (rules.Count > 0 && rules.Contains(e.Labels[m])) { hidden = true; break; }
                }
                e.Hidden = hidden;
                if (hidden) HiddenElements++;
            }

            foreach (var list in groupsByMode)
                if (list != null) RefreshHiddenCounts(list);

            ApplyVisibility();
        }

        static void RefreshHiddenCounts(List<IfcInspectorGroup> list)
        {
            foreach (var g in list)
            {
                int hidden = 0;
                foreach (var e in g.Elements)
                    if (e.Hidden) hidden++;
                g.HiddenCount = hidden;
            }
        }

        void ApplyVisibility()
        {
            var svm = SceneVisibilityManager.instance;
            foreach (var e in Elements)
            {
                if (e.Go == null || e.Hidden == e.Applied) continue;
                if (e.Hidden) svm.Hide(e.Go, true);
                else svm.Show(e.Go, true);
                e.Applied = e.Hidden;
            }
        }

        static void AssignColors(List<IfcInspectorGroup> groups, IfcInspectorMode mode)
        {
            // Semantic modes use fixed, meaningful colours; categorical modes get maximally
            // distinct hues via the golden ratio, in the (stable) group order.
            if (mode == IfcInspectorMode.ByLoadBearing || mode == IfcInspectorMode.ByExternal)
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
    /// Visual + statistical BIM inspection for imported IFC models: recolour a model in the
    /// scene by IFC type, storey or pset flags, with a colour-matched legend showing element
    /// and triangle statistics per category. Legend rows have visibility eyes (Alt-click to
    /// solo), click to select, double-click to frame. Hiding composes across categories and
    /// survives mode switches; *Original* clears everything. All colouring is a transient
    /// MaterialPropertyBlock override — nothing in the scene or assets is modified.
    /// </summary>
    public class IfcInspectorWindow : EditorWindow
    {
        CADModelInfo target;
        IfcInspectorMode mode = IfcInspectorMode.Original;
        IfcInspectorModel model;
        bool scanDirty;

        CADModelInfo[] roots = Array.Empty<CADModelInfo>();
        string[] rootNames = Array.Empty<string>();

        Vector2 scroll;
        string search = "";
        double lastRowClick;
        string lastRowLabel;

        static GUIContent visibleIcon, hiddenIcon;

        [MenuItem("Tools/CAD Importer IFC Inspector")]
        public static void Open()
        {
            var window = GetWindow<IfcInspectorWindow>("IFC Inspector");
            window.minSize = new Vector2(380, 360);
        }

        void OnEnable()
        {
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            RefreshRoots();
        }

        void OnDisable()
        {
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            model?.Restore();
            model = null;
        }

        void OnHierarchyChanged()
        {
            RefreshRoots();
            scanDirty = true; // rescanned lazily on the next repaint; hide rules survive it
            Repaint();
        }

        void OnFocus() => RefreshRoots();

        /// <summary>FindObjectsByType is far too expensive to run per repaint, so the picker's
        /// list is cached and refreshed only when the hierarchy or focus changes.</summary>
        void RefreshRoots()
        {
            roots = FindObjectsByType<CADModelInfo>(FindObjectsSortMode.None)
                .Where(m => m != null && m.gameObject.scene.IsValid())
                .ToArray();
            rootNames = roots.Select(m => m.gameObject.name).ToArray();

            if (target != null && !roots.Contains(target))
            {
                target = null;
                model = null;
            }
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

            EnsureModel();

            EditorGUILayout.Space(4);
            var newMode = (IfcInspectorMode)EditorGUILayout.EnumPopup("Draw mode", mode);
            if (newMode != mode)
            {
                mode = newMode;
                ApplyMode();
            }

            if (mode != IfcInspectorMode.Original && model != null)
                DrawLegend();
        }

        void DrawTargetPicker()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                int current = Array.IndexOf(roots, target);
                int picked = EditorGUILayout.Popup("Model", current, rootNames);
                if (picked != current && picked >= 0 && picked < roots.Length)
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
            if (target == next) return;
            model?.Restore();
            model = null;
            target = next;
            EnsureModel();
            ApplyMode();
        }

        void EnsureModel()
        {
            if (target == null) { model = null; return; }
            if (model != null && model.Root == target.gameObject && !scanDirty) return;

            model = IfcInspectorModel.Scan(target.gameObject, model);
            scanDirty = false;
            if (mode != IfcInspectorMode.Original)
                model.ApplyColors(mode);
        }

        void ApplyMode()
        {
            if (model == null) return;

            // Original is the reset: colours and every hide rule go. The other modes only swap
            // the colouring, so what you hid stays hidden across a mode switch.
            if (mode == IfcInspectorMode.Original) model.Restore();
            else model.ApplyColors(mode);

            SceneView.RepaintAll();
        }

        void DrawLegend()
        {
            var groups = model.Groups(mode);
            EditorGUILayout.Space(6);

            long totalTris = 0;
            int totalElems = 0;
            foreach (var g in groups)
            {
                totalTris += g.Triangles;
                totalElems += g.Count;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(
                    $"{totalElems} elements in {groups.Count} categories — " +
                    $"{totalTris:N0} triangles (LOD0)", EditorStyles.boldLabel);

                // Counts elements hidden by every mode's rules, not just this mode's, so the
                // button stays put when you switch category.
                int hidden = model.HiddenElements;
                if (hidden > 0 &&
                    GUILayout.Button($"Show All ({hidden} hidden)", GUILayout.Width(130)))
                {
                    model.ShowAll();
                    SceneView.RepaintAll();
                }
            }

            search = EditorGUILayout.TextField(GUIContent.none, search, EditorStyles.toolbarSearchField);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var g in groups)
            {
                if (!string.IsNullOrEmpty(search) &&
                    g.Label.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                DrawRow(g);
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.HelpBox(
                "Eye: hide/show a category (Alt-click to solo). Hiding is cumulative across " +
                "modes — a dimmed eye means another mode also hides part of that category. " +
                "Click a row to select its elements, double-click to frame them.",
                MessageType.None);
        }

        void DrawRow(IfcInspectorGroup g)
        {
            var row = EditorGUILayout.GetControlRect(false, 20);
            var state = model.StateOf(g, mode);
            bool isHidden = state == IfcCategoryState.Hidden;

            visibleIcon ??= EditorGUIUtility.IconContent("scenevis_visible_hover");
            hiddenIcon ??= EditorGUIUtility.IconContent("scenevis_hidden_hover");

            // Visibility eye, matching the Hierarchy's icons. Dimmed when another mode's rule
            // hides part of this category. Alt-click isolates the category.
            var eyeRect = new Rect(row.x, row.y + 1, 22, 18);
            var prevColor = GUI.color;
            if (state == IfcCategoryState.Partial)
                GUI.color = new Color(prevColor.r, prevColor.g, prevColor.b, prevColor.a * 0.45f);

            if (GUI.Button(eyeRect, isHidden ? hiddenIcon : visibleIcon, EditorStyles.iconButton))
            {
                if (Event.current.alt) model.Solo(mode, g.Label);
                else model.SetCategoryHidden(mode, g.Label, !isHidden);
                SceneView.RepaintAll();
            }
            GUI.color = prevColor;

            var swatch = new Rect(row.x + 26, row.y + 3, 14, 14);
            EditorGUI.DrawRect(swatch, g.Color);

            var label = new Rect(row.x + 46, row.y, row.width - 46, row.height);
            GUI.Label(label, g.RowText, isHidden ? EditorStyles.miniLabel : EditorStyles.label);

            if (state == IfcCategoryState.Partial)
            {
                var note = new Rect(label.x, label.y, label.width, label.height);
                GUI.Label(note, $"{g.HiddenCount}/{g.Count} hidden", RightAlignedMini);
            }

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                label.Contains(Event.current.mousePosition))
            {
                var objects = new UnityEngine.Object[g.Count];
                for (int i = 0; i < g.Count; i++) objects[i] = g.Elements[i].Go;
                Selection.objects = objects;

                bool doubleClick = g.Label == lastRowLabel &&
                                   EditorApplication.timeSinceStartup - lastRowClick < 0.4;
                lastRowClick = EditorApplication.timeSinceStartup;
                lastRowLabel = g.Label;
                if (doubleClick && SceneView.lastActiveSceneView != null)
                    SceneView.lastActiveSceneView.FrameSelected();
                Event.current.Use();
            }
        }

        static GUIStyle rightAlignedMini;
        static GUIStyle RightAlignedMini =>
            rightAlignedMini ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
    }
}
