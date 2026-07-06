using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace CADImporter
{
    /// <summary>
    /// Wavefront OBJ parser. Groups/objects become separate parts, usemtl statements
    /// become submeshes, and .mtl libraries next to the file are parsed for diffuse colors.
    /// Supports negative (relative) indices, v/vt/vn combinations and polygon fans.
    /// Unity has a native OBJ importer for assets; this parser exists for the runtime
    /// (digital-twin) loading path and for consistency with the CAD pipeline.
    /// </summary>
    public static class ObjParser
    {
        sealed class NodeBuilder
        {
            public string Name;
            public readonly List<Vector3> Positions = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Vector2> UVs = new List<Vector2>();
            public readonly List<Color32> Colors = new List<Color32>();
            public readonly Dictionary<(int, int, int), int> Map = new Dictionary<(int, int, int), int>();
            public readonly List<string> MaterialOrder = new List<string>();
            public readonly Dictionary<string, List<int>> SubmeshIndices = new Dictionary<string, List<int>>();
            public bool AnyNormals, AnyUVs, AnyColors;
        }

        public static CADModel Parse(string path)
        {
            var model = Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path),
                Path.GetDirectoryName(path));
            model.SourcePath = path;
            return model;
        }

        public static CADModel Parse(string text, string name, string mtlSearchDir = null)
        {
            var model = new CADModel { Name = name, Format = "OBJ" };

            var vPos = new List<Vector3>();
            var vNrm = new List<Vector3>();
            var vUv = new List<Vector2>();
            var vCol = new List<Color32>();
            bool anySourceColors = false;

            NodeBuilder current = null;
            string currentMaterial = "";
            var materialNames = new HashSet<string>();

            int pos = 0;
            while (pos < text.Length)
            {
                int lineEnd = text.IndexOf('\n', pos);
                if (lineEnd < 0) lineEnd = text.Length;
                int ls = pos, le = lineEnd;
                pos = lineEnd + 1;

                // trim
                while (ls < le && char.IsWhiteSpace(text[ls])) ls++;
                while (le > ls && char.IsWhiteSpace(text[le - 1])) le--;
                if (le - ls < 2 || text[ls] == '#') continue;

                char c0 = text[ls];
                char c1 = text[ls + 1];

                if (c0 == 'v' && char.IsWhiteSpace(c1))
                {
                    var t = Split(text, ls + 1, le);
                    vPos.Add(new Vector3(F(t, 0), F(t, 1), F(t, 2)));
                    if (t.Count >= 6)
                    {
                        anySourceColors = true;
                        vCol.Add(new Color32(
                            (byte)Mathf.Clamp(Mathf.RoundToInt(F(t, 3) * 255f), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(F(t, 4) * 255f), 0, 255),
                            (byte)Mathf.Clamp(Mathf.RoundToInt(F(t, 5) * 255f), 0, 255), 255));
                    }
                    else vCol.Add(new Color32(255, 255, 255, 255));
                }
                else if (c0 == 'v' && c1 == 'n')
                {
                    var t = Split(text, ls + 2, le);
                    vNrm.Add(new Vector3(F(t, 0), F(t, 1), F(t, 2)));
                }
                else if (c0 == 'v' && c1 == 't')
                {
                    var t = Split(text, ls + 2, le);
                    vUv.Add(new Vector2(F(t, 0), t.Count > 1 ? F(t, 1) : 0f));
                }
                else if (c0 == 'f' && char.IsWhiteSpace(c1))
                {
                    current ??= new NodeBuilder { Name = name };
                    var t = Split(text, ls + 1, le);
                    if (t.Count < 3) continue;

                    Span<int> tri = stackalloc int[3];
                    int first = -1, prev = -1;
                    for (int i = 0; i < t.Count; i++)
                    {
                        int local = ResolveCorner(current, t[i], vPos, vNrm, vUv, vCol, anySourceColors);
                        if (i == 0) first = local;
                        else if (i >= 2)
                        {
                            tri[0] = first; tri[1] = prev; tri[2] = local;
                            var list = GetSubmesh(current, currentMaterial);
                            list.Add(tri[0]); list.Add(tri[1]); list.Add(tri[2]);
                        }
                        prev = local;
                    }
                }
                else if ((c0 == 'g' || c0 == 'o') && char.IsWhiteSpace(c1))
                {
                    FlushNode(model, ref current);
                    string groupName = text.Substring(ls + 1, le - ls - 1).Trim();
                    current = new NodeBuilder { Name = string.IsNullOrEmpty(groupName) ? name : groupName };
                }
                else if (Matches(text, ls, le, "usemtl"))
                {
                    currentMaterial = text.Substring(ls + 6, le - ls - 6).Trim();
                    materialNames.Add(currentMaterial);
                }
                else if (Matches(text, ls, le, "mtllib") && mtlSearchDir != null)
                {
                    string lib = text.Substring(ls + 6, le - ls - 6).Trim();
                    TryParseMtl(Path.Combine(mtlSearchDir, lib), model);
                }
            }
            FlushNode(model, ref current);

            // Materials referenced but not defined in any .mtl get default entries.
            foreach (var m in materialNames)
                if (model.Materials.Find(x => x.Name == m) == null)
                    model.Materials.Add(new CADMaterialInfo { Name = m });

            if (model.Root.Children.Count == 0)
                throw new InvalidDataException("OBJ file contains no faces.");
            return model;
        }

        struct Token { public int Start, Length; }

        static readonly List<Token> s_tokens = new List<Token>(16);
        static string s_tokenSource;

        static List<Token> Split(string s, int start, int end)
        {
            s_tokens.Clear();
            s_tokenSource = s;
            int p = start;
            while (p < end)
            {
                while (p < end && char.IsWhiteSpace(s[p])) p++;
                int ts = p;
                while (p < end && !char.IsWhiteSpace(s[p])) p++;
                if (p > ts) s_tokens.Add(new Token { Start = ts, Length = p - ts });
            }
            return s_tokens;
        }

        static float F(List<Token> t, int i)
        {
            var tok = t[i];
            return float.Parse(s_tokenSource.AsSpan(tok.Start, tok.Length),
                NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static bool Matches(string s, int ls, int le, string keyword)
        {
            if (le - ls <= keyword.Length) return false;
            for (int i = 0; i < keyword.Length; i++)
                if (s[ls + i] != keyword[i]) return false;
            return char.IsWhiteSpace(s[ls + keyword.Length]);
        }

        static int ResolveCorner(NodeBuilder node, Token tok,
            List<Vector3> vPos, List<Vector3> vNrm, List<Vector2> vUv, List<Color32> vCol, bool anyColors)
        {
            // token forms: v | v/vt | v//vn | v/vt/vn ; indices are 1-based, negative = relative
            string s = s_tokenSource;
            int vi = 0, ti = 0, ni = 0, field = 0;
            bool neg = false;
            int val = 0;
            bool has = false;

            void Commit()
            {
                int r = neg ? -val : val;
                if (field == 0) vi = r; else if (field == 1) ti = r; else ni = r;
                val = 0; neg = false; has = false;
            }

            for (int i = 0; i < tok.Length; i++)
            {
                char c = s[tok.Start + i];
                if (c == '/')
                {
                    Commit();
                    field++;
                }
                else if (c == '-') neg = true;
                else if (c >= '0' && c <= '9') { val = val * 10 + (c - '0'); has = true; }
            }
            if (has || val != 0) Commit();

            int pi = vi > 0 ? vi - 1 : vPos.Count + vi;
            int uvI = ti == 0 ? -1 : (ti > 0 ? ti - 1 : vUv.Count + ti);
            int nI = ni == 0 ? -1 : (ni > 0 ? ni - 1 : vNrm.Count + ni);

            var key = (pi, uvI, nI);
            if (node.Map.TryGetValue(key, out int existing)) return existing;

            int idx = node.Positions.Count;
            node.Positions.Add(vPos[pi]);
            node.Normals.Add(nI >= 0 && nI < vNrm.Count ? vNrm[nI] : Vector3.zero);
            node.UVs.Add(uvI >= 0 && uvI < vUv.Count ? vUv[uvI] : Vector2.zero);
            node.Colors.Add(pi < vCol.Count ? vCol[pi] : new Color32(255, 255, 255, 255));
            if (nI >= 0) node.AnyNormals = true;
            if (uvI >= 0) node.AnyUVs = true;
            if (anyColors) node.AnyColors = true;
            node.Map.Add(key, idx);
            return idx;
        }

        static List<int> GetSubmesh(NodeBuilder node, string material)
        {
            if (!node.SubmeshIndices.TryGetValue(material, out var list))
            {
                list = new List<int>();
                node.SubmeshIndices.Add(material, list);
                node.MaterialOrder.Add(material);
            }
            return list;
        }

        static void FlushNode(CADModel model, ref NodeBuilder node)
        {
            if (node == null) return;
            bool any = false;
            foreach (var kv in node.SubmeshIndices)
                if (kv.Value.Count > 0) { any = true; break; }
            if (!any) { node = null; return; }

            var submeshes = new List<int[]>();
            var submeshMats = new List<string>();
            foreach (var mat in node.MaterialOrder)
            {
                var list = node.SubmeshIndices[mat];
                if (list.Count == 0) continue;
                submeshes.Add(list.ToArray());
                submeshMats.Add(mat);
            }

            var mesh = new CADMeshData
            {
                Name = node.Name,
                Positions = node.Positions.ToArray(),
                Normals = node.AnyNormals ? node.Normals.ToArray() : null,
                UV = node.AnyUVs ? node.UVs.ToArray() : null,
                Colors = node.AnyColors ? node.Colors.ToArray() : null,
                Submeshes = submeshes.ToArray(),
                SubmeshMaterials = submeshMats.ToArray()
            };
            model.Root.Children.Add(new CADNode { Name = node.Name, Mesh = mesh });
            node = null;
        }

        static void TryParseMtl(string path, CADModel model)
        {
            try
            {
                if (!File.Exists(path)) return;
                CADMaterialInfo current = null;
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    switch (tok[0])
                    {
                        case "newmtl":
                            current = new CADMaterialInfo { Name = tok.Length > 1 ? tok[1] : "" };
                            model.Materials.Add(current);
                            break;
                        case "Kd":
                            if (current != null && tok.Length >= 4)
                            {
                                current.Color = new Color(
                                    ParseF(tok[1]), ParseF(tok[2]), ParseF(tok[3]), current.Color.a);
                            }
                            break;
                        case "d":
                            if (current != null && tok.Length >= 2)
                            {
                                var c = current.Color;
                                c.a = ParseF(tok[1]);
                                current.Color = c;
                            }
                            break;
                        case "Ns":
                            if (current != null && tok.Length >= 2)
                                current.Smoothness = Mathf.Clamp01(ParseF(tok[1]) / 1000f);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"CAD Importer: failed to parse MTL '{path}': {e.Message}");
            }
        }

        static float ParseF(string s) => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
