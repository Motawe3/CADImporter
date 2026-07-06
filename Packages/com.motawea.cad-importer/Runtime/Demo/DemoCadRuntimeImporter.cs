using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace CADImporter
{
    /// <summary>
    /// Demonstrates runtime CAD import for digital twins. Drop this on any GameObject and
    /// press Play: an on-screen panel lets you type a path to an .stl/.ply/.obj file and
    /// import it while the scene is running. Leave the path empty to import a generated
    /// sample part, so the demo works without any external files.
    /// Parsing and geometry processing run on a worker thread (see
    /// <see cref="CADRuntimeImporter.ImportAsync"/>), so the scene never hitches.
    /// </summary>
    public class DemoCadRuntimeImporter : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Absolute path to an .stl, .ply or .obj file. Leave empty to import a generated sample part.")]
        public string filePath = "";

        [Tooltip("Import automatically when the scene starts.")]
        public bool importOnStart = true;

        [Header("Import Options")]
        public CADRuntimeImportSettings settings = new CADRuntimeImportSettings();

        [Header("Placement")]
        [Tooltip("Imported models are parented here. Defaults to this transform.")]
        public Transform spawnPoint;

        [Tooltip("Destroy the previously imported model before importing the next one.")]
        public bool replacePrevious = true;

        [Tooltip("Move the main camera to frame the imported model.")]
        public bool frameWithMainCamera = true;

        [Tooltip("Slowly rotate the imported model for presentation.")]
        public bool turntable = true;
        [Range(0f, 90f)] public float turntableDegreesPerSecond = 20f;

        GameObject _imported;
        bool _importing;
        string _status = "Ready.";
        string _pathField;
        string _scaleField;

        static readonly string[] UnitLabels = { "mm", "cm", "m", "inch", "ft" };
        static readonly SourceUnit[] UnitValues =
        {
            SourceUnit.Millimeters, SourceUnit.Centimeters, SourceUnit.Meters,
            SourceUnit.Inches, SourceUnit.Feet
        };

        void Start()
        {
            _pathField = filePath;
            if (importOnStart)
                _ = ImportNow();
        }

        void Update()
        {
            if (turntable && _imported != null)
                _imported.transform.Rotate(0f, turntableDegreesPerSecond * Time.deltaTime, 0f, Space.World);
        }

        /// <summary>Imports the file at <see cref="filePath"/> (or the generated sample). Safe to call from UI.</summary>
        public async System.Threading.Tasks.Task ImportNow()
        {
            if (_importing) return;
            _importing = true;
            try
            {
                string path = string.IsNullOrWhiteSpace(filePath)
                    ? WriteSamplePart()
                    : filePath.Trim();

                if (!File.Exists(path))
                {
                    _status = $"File not found:\n{path}";
                    return;
                }
                if (!CADRuntimeImporter.IsSupported(path))
                {
                    _status = $"Unsupported extension '{Path.GetExtension(path)}'. Runtime import handles .stl, .ply and .obj.";
                    return;
                }

                _status = $"Importing {Path.GetFileName(path)}…";
                var stopwatch = Stopwatch.StartNew();

                GameObject model = await CADRuntimeImporter.ImportAsync(path, settings);
                stopwatch.Stop();

                // The scene (or this component) may have been torn down during the await.
                if (this == null)
                {
                    if (model != null) Destroy(model);
                    return;
                }

                if (replacePrevious && _imported != null)
                    Destroy(_imported);

                var parent = spawnPoint != null ? spawnPoint : transform;
                model.transform.SetParent(parent, false);
                _imported = model;

                var info = model.GetComponent<CADModelInfo>();
                _status = $"Imported {Path.GetFileName(path)} in {stopwatch.ElapsedMilliseconds} ms\n" +
                          $"{info.partCount} part(s), {info.totalTriangles:N0} triangles, {info.totalVertices:N0} vertices";
                UnityEngine.Debug.Log($"CAD Importer demo: {_status.Replace('\n', ' ')}");

                if (frameWithMainCamera)
                    FrameModel(model);
            }
            catch (Exception e)
            {
                _status = $"Import failed: {e.Message}";
                UnityEngine.Debug.LogException(e);
            }
            finally
            {
                _importing = false;
            }
        }

        public void ClearImported()
        {
            if (_imported != null) Destroy(_imported);
            _imported = null;
            _status = "Cleared.";
        }

        void OnGUI()
        {
            const int width = 420;
            GUILayout.BeginArea(new Rect(12, 12, width, 250), GUI.skin.box);
            GUILayout.Label("<b>CAD Runtime Import Demo</b>",
                new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 });

            GUILayout.Label("CAD file (.stl / .ply / .obj) — empty = generated sample:");
            _pathField = GUILayout.TextField(_pathField ?? "");
            filePath = _pathField;

            DrawScaleControls();

            GUILayout.BeginHorizontal();
            GUI.enabled = !_importing;
            if (GUILayout.Button(_importing ? "Importing…" : "Import", GUILayout.Height(26)))
                _ = ImportNow();
            GUI.enabled = _imported != null;
            if (GUILayout.Button("Clear", GUILayout.Height(26), GUILayout.Width(80)))
                ClearImported();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Label(_status, new GUIStyle(GUI.skin.label) { wordWrap = true });
            GUILayout.EndArea();
        }

        // Source-unit buttons and an extra multiplier; both apply to the next import.
        void DrawScaleControls()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Source unit:", GUILayout.Width(84));
            int current = Array.IndexOf(UnitValues, settings.sourceUnit);
            int picked = GUILayout.SelectionGrid(current, UnitLabels, UnitLabels.Length);
            if (picked != current && picked >= 0)
                settings.sourceUnit = UnitValues[picked];
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Extra scale:", GUILayout.Width(84));
            _scaleField ??= settings.additionalScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _scaleField = GUILayout.TextField(_scaleField, GUILayout.Width(64));
            if (float.TryParse(_scaleField, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float extra) && extra > 0f)
                settings.additionalScale = extra;
            float metersPerUnit = CADUnits.ToMeters(settings.sourceUnit) * settings.additionalScale;
            int unitIndex = Mathf.Max(0, Array.IndexOf(UnitValues, settings.sourceUnit));
            GUILayout.Label($"  1 {UnitLabels[unitIndex]} → {metersPerUnit:0.######} m");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        void FrameModel(GameObject model)
        {
            var cam = Camera.main;
            if (cam == null) return;

            var renderers = model.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers) bounds.Encapsulate(r.bounds);

            float radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
            float distance = radius / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            cam.transform.position = bounds.center + new Vector3(1f, 0.7f, -1f).normalized * distance * 1.2f;
            cam.transform.LookAt(bounds.center);
        }

        /// <summary>
        /// Writes a small generated part (a flanged cylinder, authored like a CAD export:
        /// millimeters, Z-up, binary STL triangle soup) so the demo needs no external files.
        /// </summary>
        static string WriteSamplePart()
        {
            string path = Path.Combine(Application.temporaryCachePath, "CADImporterSample.stl");
            if (File.Exists(path)) return path;

            const int segments = 96;
            const float shaftRadius = 30f, shaftHeight = 120f;   // mm
            const float flangeRadius = 55f, flangeHeight = 14f;

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(new byte[80]);
            long countPos = ms.Position;
            w.Write(0u); // patched below
            uint triangles = 0;

            void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                for (int i = 0; i < 3; i++) w.Write(0f);
                w.Write(a.x); w.Write(a.y); w.Write(a.z);
                w.Write(b.x); w.Write(b.y); w.Write(b.z);
                w.Write(c.x); w.Write(c.y); w.Write(c.z);
                w.Write((ushort)0);
                triangles++;
            }

            Vector3 P(int s, float radius, float z)
            {
                float t = 2f * Mathf.PI * s / segments;
                return new Vector3(radius * Mathf.Cos(t), radius * Mathf.Sin(t), z);
            }

            // Rings from bottom to top: flange bottom, flange top, shaft top (Z-up, CCW outward).
            (float radius, float z0, float z1)[] walls =
            {
                (flangeRadius, 0f, flangeHeight),
                (shaftRadius, flangeHeight, shaftHeight)
            };
            for (int s = 0; s < segments; s++)
            {
                foreach (var (radius, z0, z1) in walls)
                {
                    Tri(P(s, radius, z0), P(s + 1, radius, z0), P(s + 1, radius, z1));
                    Tri(P(s, radius, z0), P(s + 1, radius, z1), P(s, radius, z1));
                }
                // bottom cap (facing -Z)
                Tri(Vector3.zero, P(s + 1, flangeRadius, 0f), P(s, flangeRadius, 0f));
                // flange shoulder ring (facing +Z)
                Tri(P(s, flangeRadius, flangeHeight), P(s + 1, flangeRadius, flangeHeight), P(s + 1, shaftRadius, flangeHeight));
                Tri(P(s, flangeRadius, flangeHeight), P(s + 1, shaftRadius, flangeHeight), P(s, shaftRadius, flangeHeight));
                // top cap (facing +Z)
                Tri(new Vector3(0f, 0f, shaftHeight), P(s, shaftRadius, shaftHeight), P(s + 1, shaftRadius, shaftHeight));
            }

            ms.Position = countPos;
            w.Write(triangles);
            w.Flush();
            File.WriteAllBytes(path, ms.ToArray());
            return path;
        }
    }
}
