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
        const string OkMarker = "CADIMPORTER_OK";
        const string ProgressMarker = "CADIMPORTER_PROGRESS";

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
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                // FreeCAD emits ProgressMarker lines as it works; surface them live so a slow
                // large-assembly conversion is visibly making progress rather than looking hung.
                var progress = new ConcurrentQueue<string>();
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    stdout.AppendLine(e.Data);
                    int i = e.Data.IndexOf(ProgressMarker, StringComparison.Ordinal);
                    if (i >= 0) progress.Enqueue(e.Data.Substring(i + ProgressMarker.Length).Trim());
                };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Poll instead of one blocking wait, so we can drain progress and honour a
                // configurable (optionally unlimited) timeout. timeoutSeconds <= 0 means no limit.
                string prefix = string.IsNullOrEmpty(label) ? "" : label + ": ";
                var sw = Stopwatch.StartNew();
                long budgetMs = timeoutSeconds <= 0 ? long.MaxValue : (long)timeoutSeconds * 1000;
                while (!process.WaitForExit(250))
                {
                    while (progress.TryDequeue(out var msg))
                        UnityEngine.Debug.Log($"CAD Importer: {prefix}{msg}");
                    if (sw.ElapsedMilliseconds >= budgetMs)
                    {
                        try { process.Kill(); } catch { }
                        error = $"FreeCAD conversion timed out after {timeoutSeconds} seconds. " +
                                "Large assemblies can legitimately take longer — raise " +
                                "\"Step Timeout Seconds\" in the import settings (0 = no limit).";
                        return false;
                    }
                }
                process.WaitForExit(); // let the async output readers flush
                while (progress.TryDequeue(out var msg))
                    UnityEngine.Debug.Log($"CAD Importer: {prefix}{msg}");

                if (!stdout.ToString().Contains(OkMarker))
                {
                    error = $"FreeCAD conversion failed.\nstdout: {Tail(stdout.ToString())}\nstderr: {Tail(stderr.ToString())}";
                    return false;
                }

                if (Directory.GetFiles(outputDir, "*.stl").Length == 0)
                {
                    error = "FreeCAD reported success but produced no STL files (empty model?).";
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

def prog(msg):
    # Surfaced live in the Unity Console by StepConverter; keep messages terse.
    print('{ProgressMarker} ' + msg, flush=True)

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

def collect(doc):
    # Walk the document tree the way the FreeCAD GUI does: through getSubObjects /
    # getSubObject, which resolve App::Link instances and accumulate the placements
    # of App::Part containers. Reading .Shape off doc.Objects misses link instances
    # entirely and loses container placements.
    items = []

    def visit(root, subname, obj):
        r = resolved(obj)
        tid = r.TypeId
        if tid.startswith('App::Origin') or tid in ('App::Line', 'App::Plane', 'App::Point', 'App::Annotation'):
            return
        # Recurse into tree children first: containers (App::Part) aggregate their
        # children into a compound Shape since FreeCAD 1.0, so a shape check alone
        # would swallow whole sub-assemblies into a single part.
        try:
            subs = obj.getSubObjects()
        except Exception:
            subs = ()
        if subs:
            for s in subs:
                try:
                    child = root.getSubObject(subname + s, retType=1)
                except Exception:
                    child = None
                if child is not None:
                    visit(root, subname + s, child)
            return
        if has_shape(r):
            try:
                # retType=0 returns the shape with the full placement chain applied.
                shape = root.getSubObject(subname, retType=0) if subname else r.Shape
            except Exception:
                shape = None
            if shape is not None and not shape.isNull() and len(shape.Faces) > 0:
                items.append((obj.Label or r.Label, shape))

    for root in doc.RootObjects:
        visit(root, '', root)
    if items:
        return items

    # Fallback for documents without tree structure: flat leaf collection.
    objs = [o for o in doc.Objects if has_shape(o)]
    shaped = set(objs)
    for o in objs:
        if any((child in shaped) for child in o.OutList):
            continue
        items.append((o.Label, o.Shape))
    return items

try:
    items = []
    try:
        import Import
        doc = FreeCAD.newDocument('cadimport')
        prog('loading B-rep (large files can take a few minutes)...')
        Import.insert(SRC, doc.Name)
        prog('loaded; walking assembly tree...')
        items = collect(doc)
    except Exception:
        items = []
    if not items:
        prog('reading solids directly...')
        shape = Part.Shape()
        shape.read(SRC)
        solids = shape.Solids if shape.Solids else [shape]
        base = os.path.splitext(os.path.basename(SRC))[0]
        items = [(base + ('_' + str(i) if len(solids) > 1 else ''), s) for i, s in enumerate(solids)]

    total = len(items)
    prog('tessellating %d part(s)...' % total)
    count = 0
    for label, shape in items:
        try:
            m = mesh_shape(shape)
            if m.CountFacets == 0:
                continue
            m.write(os.path.join(OUT, '%03d_%s.stl' % (count, sanitize(label))))
            count += 1
        except Exception:
            traceback.print_exc()
        if count % 25 == 0 and count > 0:
            prog('tessellated %d/%d part(s)...' % (count, total))

    prog('done: %d part(s)' % count)
    print('{OkMarker} %d' % count)
except Exception:
    traceback.print_exc()
    sys.exit(1)
";
        }
    }
}
