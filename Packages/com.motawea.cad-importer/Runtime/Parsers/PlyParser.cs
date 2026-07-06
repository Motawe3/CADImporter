using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace CADImporter
{
    /// <summary>
    /// PLY parser supporting ascii, binary_little_endian and binary_big_endian formats.
    /// Reads positions, normals, vertex colors and UVs; polygon faces are fan-triangulated.
    /// Unknown elements and properties are skipped structurally.
    /// </summary>
    public static class PlyParser
    {
        enum PType { I8, U8, I16, U16, I32, U32, F32, F64 }

        sealed class PProp
        {
            public string Name;
            public PType Type;
            public bool IsList;
            public PType CountType;
            public int Role = -1; // index into vertex-role table, -1 = ignored
        }

        sealed class PElem
        {
            public string Name;
            public int Count;
            public readonly List<PProp> Props = new List<PProp>();
        }

        // Vertex property roles
        const int RX = 0, RY = 1, RZ = 2, RNX = 3, RNY = 4, RNZ = 5,
                  RR = 6, RG = 7, RB = 8, RA = 9, RU = 10, RV = 11;

        public static CADModel Parse(string path)
        {
            var model = Parse(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));
            model.SourcePath = path;
            return model;
        }

        public static CADModel Parse(byte[] data, string name)
        {
            int bodyStart = ParseHeader(data, out var elements, out bool binary, out bool bigEndian);

            Vector3[] positions = null;
            Vector3[] normals = null;
            Color32[] colors = null;
            Vector2[] uvs = null;
            var indices = new List<int>();
            bool hasNormals = false, hasColors = false, hasUvs = false;

            int off = bodyStart;
            string text = binary ? null : Encoding.ASCII.GetString(data);
            int textPos = binary ? 0 : bodyStart;

            foreach (var elem in elements)
            {
                bool isVertex = elem.Name == "vertex";
                bool isFace = elem.Name == "face";

                if (isVertex)
                {
                    positions = new Vector3[elem.Count];
                    foreach (var p in elem.Props)
                    {
                        if (p.Role >= RNX && p.Role <= RNZ) hasNormals = true;
                        else if (p.Role >= RR && p.Role <= RA) hasColors = true;
                        else if (p.Role == RU || p.Role == RV) hasUvs = true;
                    }
                    if (hasNormals) normals = new Vector3[elem.Count];
                    if (hasColors)
                    {
                        colors = new Color32[elem.Count];
                        for (int i = 0; i < colors.Length; i++) colors[i] = new Color32(255, 255, 255, 255);
                    }
                    if (hasUvs) uvs = new Vector2[elem.Count];
                }

                for (int row = 0; row < elem.Count; row++)
                {
                    for (int pi = 0; pi < elem.Props.Count; pi++)
                    {
                        var prop = elem.Props[pi];
                        if (prop.IsList)
                        {
                            int n = (int)ReadValue(data, ref off, text, ref textPos, binary, bigEndian, prop.CountType);
                            if (isFace && (prop.Name == "vertex_indices" || prop.Name == "vertex_index"))
                            {
                                int i0 = 0, prev = 0;
                                for (int k = 0; k < n; k++)
                                {
                                    int v = (int)ReadValue(data, ref off, text, ref textPos, binary, bigEndian, prop.Type);
                                    if (k == 0) i0 = v;
                                    else if (k >= 2)
                                    {
                                        indices.Add(i0);
                                        indices.Add(prev);
                                        indices.Add(v);
                                    }
                                    prev = v;
                                }
                            }
                            else
                            {
                                for (int k = 0; k < n; k++)
                                    ReadValue(data, ref off, text, ref textPos, binary, bigEndian, prop.Type);
                            }
                        }
                        else
                        {
                            double v = ReadValue(data, ref off, text, ref textPos, binary, bigEndian, prop.Type);
                            if (isVertex && prop.Role >= 0)
                                AssignVertexValue(prop, v, row, positions, normals, colors, uvs);
                        }
                    }
                }
            }

            if (positions == null || positions.Length == 0)
                throw new InvalidDataException("PLY file contains no vertices.");

            var mesh = new CADMeshData
            {
                Name = name,
                Positions = positions,
                Normals = hasNormals ? normals : null,
                Colors = hasColors ? colors : null,
                UV = hasUvs ? uvs : null,
                Submeshes = new[]
                {
                    indices.Count > 0 ? indices.ToArray() : CADMeshData.SequentialIndices(positions.Length - positions.Length % 3)
                }
            };

            var model = new CADModel { Name = name, Format = "PLY" };
            model.Root.Children.Add(new CADNode { Name = name, Mesh = mesh });
            return model;
        }

        static void AssignVertexValue(PProp prop, double v, int row,
            Vector3[] pos, Vector3[] nrm, Color32[] col, Vector2[] uv)
        {
            switch (prop.Role)
            {
                case RX: pos[row].x = (float)v; break;
                case RY: pos[row].y = (float)v; break;
                case RZ: pos[row].z = (float)v; break;
                case RNX: nrm[row].x = (float)v; break;
                case RNY: nrm[row].y = (float)v; break;
                case RNZ: nrm[row].z = (float)v; break;
                case RR: col[row].r = ColorByte(prop, v); break;
                case RG: col[row].g = ColorByte(prop, v); break;
                case RB: col[row].b = ColorByte(prop, v); break;
                case RA: col[row].a = ColorByte(prop, v); break;
                case RU: uv[row].x = (float)v; break;
                case RV: uv[row].y = (float)v; break;
            }
        }

        static byte ColorByte(PProp prop, double v)
        {
            if (prop.Type == PType.F32 || prop.Type == PType.F64)
                return (byte)Mathf.Clamp(Mathf.RoundToInt((float)(v * 255.0)), 0, 255);
            return (byte)Math.Min(Math.Max(v, 0), 255);
        }

        // --- header ----------------------------------------------------------------------

        static int ParseHeader(byte[] data, out List<PElem> elements, out bool binary, out bool bigEndian)
        {
            int headerEnd = FindHeaderEnd(data, out int bodyStart);
            string header = Encoding.ASCII.GetString(data, 0, headerEnd);
            var lines = header.Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "ply")
                throw new InvalidDataException("Not a PLY file (missing 'ply' magic).");

            elements = new List<PElem>();
            binary = false;
            bigEndian = false;
            PElem current = null;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var tok = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                switch (tok[0])
                {
                    case "format":
                        if (tok.Length < 2) throw new InvalidDataException("Malformed PLY format line.");
                        binary = tok[1] != "ascii";
                        bigEndian = tok[1] == "binary_big_endian";
                        break;
                    case "element":
                        current = new PElem
                        {
                            Name = tok[1],
                            Count = int.Parse(tok[2], CultureInfo.InvariantCulture)
                        };
                        elements.Add(current);
                        break;
                    case "property":
                        if (current == null) break;
                        var prop = new PProp();
                        if (tok[1] == "list")
                        {
                            prop.IsList = true;
                            prop.CountType = ParseType(tok[2]);
                            prop.Type = ParseType(tok[3]);
                            prop.Name = tok[4];
                        }
                        else
                        {
                            prop.Type = ParseType(tok[1]);
                            prop.Name = tok[2];
                        }
                        if (current.Name == "vertex")
                            prop.Role = RoleOf(prop.Name);
                        current.Props.Add(prop);
                        break;
                }
            }
            return bodyStart;
        }

        static int RoleOf(string name)
        {
            switch (name)
            {
                case "x": return RX;
                case "y": return RY;
                case "z": return RZ;
                case "nx": return RNX;
                case "ny": return RNY;
                case "nz": return RNZ;
                case "red": case "r": case "diffuse_red": return RR;
                case "green": case "g": case "diffuse_green": return RG;
                case "blue": case "b": case "diffuse_blue": return RB;
                case "alpha": return RA;
                case "u": case "s": case "texture_u": return RU;
                case "v": case "t": case "texture_v": return RV;
                default: return -1;
            }
        }

        static PType ParseType(string s)
        {
            switch (s)
            {
                case "char": case "int8": return PType.I8;
                case "uchar": case "uint8": return PType.U8;
                case "short": case "int16": return PType.I16;
                case "ushort": case "uint16": return PType.U16;
                case "int": case "int32": return PType.I32;
                case "uint": case "uint32": return PType.U32;
                case "float": case "float32": return PType.F32;
                case "double": case "float64": return PType.F64;
                default: throw new InvalidDataException($"Unknown PLY property type '{s}'.");
            }
        }

        static int FindHeaderEnd(byte[] data, out int bodyStart)
        {
            var marker = Encoding.ASCII.GetBytes("end_header");
            int limit = Math.Min(data.Length, 65536) - marker.Length;
            for (int i = 0; i <= limit; i++)
            {
                bool hit = true;
                for (int j = 0; j < marker.Length; j++)
                    if (data[i + j] != marker[j]) { hit = false; break; }
                if (!hit) continue;

                int p = i + marker.Length;
                while (p < data.Length && data[p] != '\n') p++;
                bodyStart = p + 1;
                return i;
            }
            throw new InvalidDataException("PLY header has no 'end_header' line.");
        }

        // --- value reading -----------------------------------------------------------------

        static double ReadValue(byte[] data, ref int off, string text, ref int textPos,
            bool binary, bool bigEndian, PType type)
        {
            if (!binary)
                return ReadAsciiValue(text, ref textPos);

            switch (type)
            {
                case PType.U8: return data[off++];
                case PType.I8: return (sbyte)data[off++];
                case PType.I16: return (short)ReadU16(data, ref off, bigEndian);
                case PType.U16: return ReadU16(data, ref off, bigEndian);
                case PType.I32: return (int)ReadU32(data, ref off, bigEndian);
                case PType.U32: return ReadU32(data, ref off, bigEndian);
                case PType.F32:
                {
                    uint bits = ReadU32(data, ref off, bigEndian);
                    return BitConverter.Int32BitsToSingle((int)bits);
                }
                case PType.F64:
                {
                    ulong bits = ReadU64(data, ref off, bigEndian);
                    return BitConverter.Int64BitsToDouble((long)bits);
                }
                default: throw new InvalidDataException("Unhandled PLY type.");
            }
        }

        static ushort ReadU16(byte[] d, ref int off, bool be)
        {
            ushort v = be ? (ushort)((d[off] << 8) | d[off + 1])
                          : (ushort)(d[off] | (d[off + 1] << 8));
            off += 2;
            return v;
        }

        static uint ReadU32(byte[] d, ref int off, bool be)
        {
            uint v = be
                ? ((uint)d[off] << 24) | ((uint)d[off + 1] << 16) | ((uint)d[off + 2] << 8) | d[off + 3]
                : d[off] | ((uint)d[off + 1] << 8) | ((uint)d[off + 2] << 16) | ((uint)d[off + 3] << 24);
            off += 4;
            return v;
        }

        static ulong ReadU64(byte[] d, ref int off, bool be)
        {
            ulong lo, hi;
            if (be)
            {
                hi = ReadU32(d, ref off, true);
                lo = ReadU32(d, ref off, true);
            }
            else
            {
                lo = ReadU32(d, ref off, false);
                hi = ReadU32(d, ref off, false);
            }
            return (hi << 32) | lo;
        }

        static double ReadAsciiValue(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
            int start = pos;
            while (pos < s.Length && !char.IsWhiteSpace(s[pos])) pos++;
            if (pos == start)
                throw new InvalidDataException("Unexpected end of ASCII PLY data.");
            return double.Parse(s.AsSpan(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
