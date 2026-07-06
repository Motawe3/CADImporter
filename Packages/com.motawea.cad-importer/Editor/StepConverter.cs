using System;
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
            float linearDeflection, float angularDeflectionDeg, out string error)
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
                process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(300000))
                {
                    try { process.Kill(); } catch { }
                    error = "FreeCAD conversion timed out after 5 minutes.";
                    return false;
                }

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

def collect(doc):
    items = []
    objs = [o for o in doc.Objects if hasattr(o, 'Shape') and o.Shape is not None and not o.Shape.isNull()]
    shaped = set(objs)
    # keep leaves only, so assembly containers don't duplicate their children
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
        Import.insert(SRC, doc.Name)
        items = collect(doc)
    except Exception:
        items = []
    if not items:
        shape = Part.Shape()
        shape.read(SRC)
        solids = shape.Solids if shape.Solids else [shape]
        base = os.path.splitext(os.path.basename(SRC))[0]
        items = [(base + ('_' + str(i) if len(solids) > 1 else ''), s) for i, s in enumerate(solids)]

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

    print('{OkMarker} %d' % count)
except Exception:
    traceback.print_exc()
    sys.exit(1)
";
        }
    }
}
