using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;

namespace CADImporter.Editor
{
    /// <summary>
    /// Converts STEP/IGES B-rep files to per-part STL meshes using a headless FreeCAD
    /// (FreeCADCmd.exe), which ships the Open CASCADE kernel. Pure-C# tessellation of
    /// B-rep solids is not practical, so this is the pragmatic bridge; the converter is
    /// auto-detected and the path can be overridden in the CAD Importer window.
    /// </summary>
    public static class StepConverter
    {
        const string PrefsKey = "CADImporter.FreeCADPath";
        internal const string OkMarker = "CADIMPORTER_OK";
        internal const string ProgressMarker = "CADIMPORTER_PROGRESS";

        public static string ConverterPath
        {
            get => EditorPrefs.GetString(PrefsKey, "");
            set => EditorPrefs.SetString(PrefsKey, value ?? "");
        }

        /// <summary>Returns the path to a working FreeCADCmd.exe, or null when none is found.</summary>
        public static string ResolveConverter()
        {
            var configured = ConverterPath;
            if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
                return configured;

            var detected = AutoDetect();
            if (detected != null)
                ConverterPath = detected;
            return detected;
        }

        public static string AutoDetect()
        {
            // PATH lookup
            try
            {
                var psi = new ProcessStartInfo("where.exe", "FreeCADCmd.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (p.ExitCode == 0)
                {
                    var first = output.Split('\n')[0].Trim();
                    if (File.Exists(first)) return first;
                }
            }
            catch { /* where.exe unavailable */ }

            // Common install locations
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
            };
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root, "FreeCAD*"))
                {
                    var exe = Path.Combine(dir, "bin", "FreeCADCmd.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
            return null;
        }

        /// <summary>
        /// Tessellates every solid in <paramref name="sourceFile"/> into numbered STL files
        /// ("000_PartLabel.stl", ...) inside <paramref name="outputDir"/>.
        /// </summary>
        public static bool ConvertToStl(string converterExe, string sourceFile, string outputDir,
            float linearDeflection, float angularDeflectionDeg, int timeoutSeconds, string label,
            out string error)
        {
            error = null;
            Directory.CreateDirectory(outputDir);
            string scriptPath = Path.Combine(outputDir, "_convert.py");
            File.WriteAllText(scriptPath, BuildScript(sourceFile, outputDir, linearDeflection, angularDeflectionDeg));

            if (!RunFreeCadScript(converterExe, scriptPath, timeoutSeconds, label, out _, out error))
                return false;

            if (Directory.GetFiles(outputDir, "*.stl").Length == 0)
            {
                error = "FreeCAD reported success but produced no STL files (empty model?).";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Runs <paramref name="scriptPath"/> through a headless FreeCAD (<paramref name="converterExe"/>),
        /// forwarding <c>CADIMPORTER_PROGRESS</c> lines to the Unity Console and honouring a configurable
        /// (optionally unlimited) timeout. Returns true only when the script prints <c>CADIMPORTER_OK</c>.
        /// Shared by the STEP/IGES and IFC converters (both drive FreeCAD's bundled Python).
        /// </summary>
        internal static bool RunFreeCadScript(string converterExe, string scriptPath, int timeoutSeconds,
            string label, out string stdout, out string error)
        {
            error = null;
            stdout = null;

            var psi = new ProcessStartInfo(converterExe, $"\"{scriptPath}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using var process = Process.Start(psi);
                var outBuf = new StringBuilder();
                var stderr = new StringBuilder();
                // FreeCAD emits ProgressMarker lines as it works; surface them live so a slow
                // large-assembly conversion is visibly making progress rather than looking hung.
                var progress = new ConcurrentQueue<string>();
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    outBuf.AppendLine(e.Data);
                    int i = e.Data.IndexOf(ProgressMarker, StringComparison.Ordinal);
                    if (i >= 0) progress.Enqueue(e.Data.Substring(i + ProgressMarker.Length).Trim());
                };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Poll instead of one blocking wait, so we can drain progress and honour a
                // configurable (optionally unlimited) timeout. timeoutSeconds <= 0 means no limit.
                string prefix = string.IsNullOrEmpty(label) ? "" : label + ": ";
                string title = $"CAD Importer — {label}";
                float frac = 0f;
                string phase = "starting…";
                var sw = Stopwatch.StartNew();
                long budgetMs = timeoutSeconds <= 0 ? long.MaxValue : (long)timeoutSeconds * 1000;
                while (!process.WaitForExit(250))
                {
                    while (progress.TryDequeue(out var raw))
                    {
                        ParseProgress(raw, ref frac, out phase);
                        UnityEngine.Debug.Log($"CAD Importer: {prefix}{phase}");
                    }
                    // Refresh every poll (even with no new line) so the elapsed clock keeps
                    // ticking — a multi-minute B-rep load never looks frozen.
                    EditorUtility.DisplayProgressBar(title,
                        $"{phase}   ({FormatElapsed(sw.Elapsed)})", UnityEngine.Mathf.Clamp01(frac));
                    if (sw.ElapsedMilliseconds >= budgetMs)
                    {
                        try { process.Kill(); } catch { }
                        error = $"FreeCAD conversion timed out after {timeoutSeconds} seconds. " +
                                "Large models can legitimately take longer — raise " +
                                "\"Step Timeout Seconds\" in the import settings (0 = no limit).";
                        return false;
                    }
                }
                process.WaitForExit(); // let the async output readers flush
                while (progress.TryDequeue(out var raw))
                {
                    ParseProgress(raw, ref frac, out phase);
                    UnityEngine.Debug.Log($"CAD Importer: {prefix}{phase}");
                }

                stdout = outBuf.ToString();
                if (!stdout.Contains(OkMarker))
                {
                    error = $"FreeCAD conversion failed.\nstdout: {Tail(stdout)}\nstderr: {Tail(stderr.ToString())}";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                error = $"Failed to run FreeCAD converter: {e.Message}";
                return false;
            }
        }

        static string Tail(string s, int max = 2000) =>
            s.Length <= max ? s : "..." + s.Substring(s.Length - max);

        /// <summary>
        /// Parses a script progress line of the form "&lt;frac&gt; &lt;message&gt;". A fraction in
        /// [0,1] advances the bar; a negative fraction (indeterminate step) leaves it unchanged.
        /// Lines with no leading number are treated as plain messages.
        /// </summary>
        static void ParseProgress(string raw, ref float frac, out string message)
        {
            message = raw;
            int sp = raw.IndexOf(' ');
            if (sp > 0 && float.TryParse(raw.Substring(0, sp), System.Globalization.NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float f))
            {
                if (f >= 0f) frac = f;
                message = raw.Substring(sp + 1);
            }
        }

        static string FormatElapsed(TimeSpan t) =>
            t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{t.Seconds}s";

        static string BuildScript(string src, string outDir, float linDef, float angDefDeg)
        {
            var inv = CultureInfo.InvariantCulture;
            string lin = Math.Max(linDef, 0.001f).ToString(inv);
            string ang = (Math.Max(angDefDeg, 1f) * Math.PI / 180.0).ToString(inv);

            // Paths embedded as Python raw string literals — avoids argv quoting issues.
            return $@"# Auto-generated by Unity CAD Importer
import os, sys, traceback
import FreeCAD
import Part
import Mesh
import MeshPart

SRC = r'''{src}'''
OUT = r'''{outDir}'''
LIN = {lin}
ANG = {ang}

TOTAL = 0  # total tessellatable leaf parts, filled in after the B-rep loads

def prog(frac, msg):
    # Surfaced live in the Unity Console + progress bar by StepConverter; keep messages terse.
    print('{ProgressMarker} %.4f %s' % (frac, msg), flush=True)

def sanitize(label):
    out = ''.join(ch if (ch.isalnum() or ch in '-_ .') else '_' for ch in label)
    return out.strip()[:60] or 'Part'

def mesh_shape(shape):
    try:
        return MeshPart.meshFromShape(Shape=shape, LinearDeflection=LIN, AngularDeflection=ANG, Relative=True)
    except Exception:
        m = Mesh.Mesh()
        m.addFacets(shape.tessellate(LIN))
        return m

def has_shape(o):
    try:
        return hasattr(o, 'Shape') and o.Shape is not None and not o.Shape.isNull() and len(o.Shape.Faces) > 0
    except Exception:
        return False

def resolved(o):
    # Follow App::Link chains to the object that owns the geometry.
    try:
        linked = o.getLinkedObject(True)
        return linked if linked is not None else o
    except Exception:
        return o

def placement_of(pl):
    # Local placement relative to the parent container: position (mm) + quaternion (x,y,z,w).
    b = pl.Base
    q = pl.Rotation.Q
    return [b.x, b.y, b.z], [q[0], q[1], q[2], q[3]]

SKIP_TYPES = ('App::Line', 'App::Plane', 'App::Point', 'App::Annotation')

def count_parts(obj):
    # Mirror build_tree's traversal but only count tessellatable leaves (no meshing),
    # so the importer can show a determinate 'part i of N' progress bar.
    r = resolved(obj)
    tid = r.TypeId
    if tid.startswith('App::Origin') or tid in SKIP_TYPES:
        return 0
    try:
        subs = obj.getSubObjects()
    except Exception:
        subs = ()
    if subs:
        n = 0
        for s in subs:
            try:
                child = obj.getSubObject(s, retType=1)
            except Exception:
                child = None
            if child is not None:
                n += count_parts(child)
        return n
    return 1 if has_shape(r) else 0

def build_tree(obj, counter):
    # Preserve the assembly tree: each object becomes a node carrying its LOCAL placement.
    # Containers (App::Part / links, which have getSubObjects) become group nodes; leaf parts
    # are tessellated in their own local frame (Shape.Placement stripped) so pivots survive.
    r = resolved(obj)
    tid = r.TypeId
    if tid.startswith('App::Origin') or tid in SKIP_TYPES:
        return None
    name = obj.Label or r.Label
    pos, quat = placement_of(obj.Placement)
    try:
        subs = obj.getSubObjects()
    except Exception:
        subs = ()
    if subs:
        children = []
        for s in subs:
            try:
                child = obj.getSubObject(s, retType=1)
            except Exception:
                child = None
            if child is not None:
                cn = build_tree(child, counter)
                if cn is not None:
                    children.append(cn)
        if not children:
            return None
        return {{'name': name, 'pos': pos, 'quat': quat, 'mesh': -1, 'children': children}}
    if has_shape(r):
        try:
            local = r.Shape.copy()
            local.Placement = FreeCAD.Placement()  # strip -> pivot-at-origin local geometry
            m = mesh_shape(local)
        except Exception:
            traceback.print_exc()
            return None
        if m.CountFacets == 0:
            return None
        idx = counter[0]
        m.write(os.path.join(OUT, '%03d.stl' % idx))
        counter[0] += 1
        if counter[0] % 10 == 0:
            prog(0.1 + 0.85 * (counter[0] / max(TOTAL, 1)),
                 'tessellated %d/%d part(s)...' % (counter[0], TOTAL))
        return {{'name': name, 'pos': pos, 'quat': quat, 'mesh': idx, 'children': []}}
    return None

def count_leaves(n):
    c = 1 if n.get('mesh', -1) >= 0 else 0
    for ch in n.get('children', []):
        c += count_leaves(ch)
    return c

try:
    import json
    roots = []
    world_bbox = None
    used_tree = False
    try:
        import Import
        doc = FreeCAD.newDocument('cadimport')
        prog(0.02, 'loading B-rep (large files can take a few minutes)...')
        Import.insert(SRC, doc.Name)
        prog(0.06, 'loaded; counting parts...')
        TOTAL = 0
        for ro in doc.RootObjects:
            TOTAL += count_parts(ro)
        prog(0.1, 'walking assembly tree (%d part(s))...' % TOTAL)
        counter = [0]
        for ro in doc.RootObjects:
            n = build_tree(ro, counter)
            if n is not None:
                roots.append(n)
        try:
            bb = None
            for ro in doc.RootObjects:
                if hasattr(ro, 'Shape') and ro.Shape is not None and not ro.Shape.isNull():
                    x = ro.Shape.BoundBox
                    vals = [x.XMin, x.YMin, x.ZMin, x.XMax, x.YMax, x.ZMax]
                    if bb is None:
                        bb = vals
                    else:
                        bb = [min(bb[0], vals[0]), min(bb[1], vals[1]), min(bb[2], vals[2]),
                              max(bb[3], vals[3]), max(bb[4], vals[4]), max(bb[5], vals[5])]
            world_bbox = bb
        except Exception:
            world_bbox = None
        used_tree = len(roots) > 0
    except Exception:
        traceback.print_exc()
        roots = []

    if not used_tree:
        prog(0.1, 'reading solids directly...')
        shape = Part.Shape()
        shape.read(SRC)
        solids = shape.Solids if shape.Solids else [shape]
        base = os.path.splitext(os.path.basename(SRC))[0]
        counter = [0]
        for i, s in enumerate(solids):
            m = mesh_shape(s)
            if m.CountFacets == 0:
                continue
            idx = counter[0]
            m.write(os.path.join(OUT, '%03d.stl' % idx))
            counter[0] += 1
            prog(0.1 + 0.85 * ((i + 1) / max(len(solids), 1)),
                 'tessellated %d/%d solid(s)...' % (i + 1, len(solids)))
            nm = base + ('_' + str(i) if len(solids) > 1 else '')
            roots.append({{'name': nm, 'pos': [0, 0, 0], 'quat': [0, 0, 0, 1], 'mesh': idx, 'children': []}})

    manifest = {{'unit': 'mm', 'worldBBox': world_bbox, 'nodes': roots}}
    with open(os.path.join(OUT, 'manifest.json'), 'w') as mf:
        json.dump(manifest, mf)

    total = sum(count_leaves(n) for n in roots)
    prog(1.0, 'done: %d part(s)' % total)
    print('{OkMarker} %d' % total)
except Exception:
    traceback.print_exc()
    sys.exit(1)
";
        }
    }
}
