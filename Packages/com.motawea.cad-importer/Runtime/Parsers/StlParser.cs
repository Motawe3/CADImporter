using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace CADImporter
{
    /// <summary>
    /// STL parser supporting binary and ASCII files (including multi-solid ASCII files,
    /// which become separate parts). Stored facet normals are discarded — the pipeline
    /// recomputes smooth normals with a configurable hard-edge angle, which gives far
    /// better shading than STL's per-facet normals.
    /// </summary>
    public static class StlParser
    {
        public static CADModel Parse(string path)
        {
            var model = Parse(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
            model.SourcePath = path;
            return model;
        }

        public static CADModel Parse(byte[] data, string name)
        {
            if (data == null || data.Length < 15)
                throw new InvalidDataException("STL file is too small to be valid.");

            var model = new CADModel { Name = name, Format = "STL" };
            if (IsBinary(data))
                ParseBinary(data, model);
            else
                ParseAscii(data, model);

            if (model.Root.Children.Count == 0)
                throw new InvalidDataException("STL file contains no triangles.");
            return model;
        }

        /// <summary>
        /// Binary detection by structural size check — the "solid" prefix is unreliable
        /// because many binary exporters write headers starting with that word.
        /// </summary>
        static bool IsBinary(byte[] data)
        {
            if (data.Length < 84) return false;
            uint triCount = BitConverter.ToUInt32(data, 80);
            long expected = 84L + triCount * 50L;
            if (expected == data.Length) return true;
            // Tolerate exporters that pad or append a few trailing bytes.
            return triCount > 0 && expected < data.Length && data.Length - expected <= 512;
        }

        static void ParseBinary(byte[] data, CADModel model)
        {
            int triCount = (int)BitConverter.ToUInt32(data, 80);
            var positions = new Vector3[triCount * 3];
            int off = 84;
            for (int t = 0; t < triCount; t++)
            {
                off += 12; // stored facet normal — recomputed later
                int v = t * 3;
                positions[v] = new Vector3(
                    BitConverter.ToSingle(data, off),
                    BitConverter.ToSingle(data, off + 4),
                    BitConverter.ToSingle(data, off + 8));
                positions[v + 1] = new Vector3(
                    BitConverter.ToSingle(data, off + 12),
                    BitConverter.ToSingle(data, off + 16),
                    BitConverter.ToSingle(data, off + 20));
                positions[v + 2] = new Vector3(
                    BitConverter.ToSingle(data, off + 24),
                    BitConverter.ToSingle(data, off + 28),
                    BitConverter.ToSingle(data, off + 32));
                off += 38; // 36 bytes of vertices + 2 bytes attribute count
            }
            AddSolid(model, model.Name, positions);
        }

        static void ParseAscii(byte[] data, CADModel model)
        {
            string text = Encoding.ASCII.GetString(data);
            int pos = 0;
            string solidName = null;
            var verts = new List<Vector3>(4096);

            while (NextToken(text, ref pos, out int ts, out int len))
            {
                if (TokenIs(text, ts, len, "vertex"))
                {
                    float x = NextFloat(text, ref pos);
                    float y = NextFloat(text, ref pos);
                    float z = NextFloat(text, ref pos);
                    verts.Add(new Vector3(x, y, z));
                }
                else if (TokenIs(text, ts, len, "solid"))
                {
                    solidName = RestOfLine(text, ref pos).Trim();
                }
                else if (TokenIs(text, ts, len, "endsolid"))
                {
                    Flush(model, solidName, verts);
                    verts.Clear();
                    solidName = null;
                    RestOfLine(text, ref pos);
                }
                // facet / normal / outer / loop / endloop / endfacet — no data needed
            }
            Flush(model, solidName, verts); // tolerate a missing endsolid
        }

        static void Flush(CADModel model, string solidName, List<Vector3> verts)
        {
            int count = verts.Count - verts.Count % 3;
            if (count <= 0) return;
            var positions = new Vector3[count];
            verts.CopyTo(0, positions, 0, count);
            string name = string.IsNullOrEmpty(solidName)
                ? (model.Root.Children.Count == 0 ? model.Name : $"{model.Name}_{model.Root.Children.Count}")
                : solidName;
            AddSolid(model, name, positions);
        }

        static void AddSolid(CADModel model, string name, Vector3[] positions)
        {
            if (positions.Length == 0) return;
            var mesh = new CADMeshData
            {
                Name = name,
                Positions = positions,
                Submeshes = new[] { CADMeshData.SequentialIndices(positions.Length) }
            };
            model.Root.Children.Add(new CADNode { Name = name, Mesh = mesh });
        }

        // --- lightweight ASCII tokenizer -------------------------------------------------

        static bool NextToken(string s, ref int pos, out int start, out int length)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
            start = pos;
            while (pos < s.Length && !char.IsWhiteSpace(s[pos])) pos++;
            length = pos - start;
            return length > 0;
        }

        static bool TokenIs(string s, int start, int length, string keyword)
        {
            if (length != keyword.Length) return false;
            for (int i = 0; i < length; i++)
                if (char.ToLowerInvariant(s[start + i]) != keyword[i]) return false;
            return true;
        }

        static float NextFloat(string s, ref int pos)
        {
            if (!NextToken(s, ref pos, out int ts, out int len))
                throw new InvalidDataException("Unexpected end of ASCII STL while reading a vertex.");
            return float.Parse(s.AsSpan(ts, len), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string RestOfLine(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && s[pos] != '\n' && s[pos] != '\r') pos++;
            return s.Substring(start, pos - start);
        }
    }
}
