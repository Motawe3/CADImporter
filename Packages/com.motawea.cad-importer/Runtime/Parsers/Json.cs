using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CADImporter
{
    /// <summary>
    /// Minimal, allocation-conscious JSON reader used by the glTF parser. Deliberately
    /// dependency-free (no Newtonsoft / JsonUtility) to keep the package self-contained and
    /// portable. Parses into <see cref="JNode"/> trees; missing lookups return a non-existent
    /// node so callers can chain <c>root["a"]["b"][0]</c> without null checks.
    /// </summary>
    internal sealed class JNode
    {
        // value is one of: Dictionary<string, JNode>, List<JNode>, string, double, bool, null.
        readonly object _value;

        /// <summary>True when this node came from actual JSON (vs. a missing lookup result).</summary>
        public readonly bool Exists;

        static readonly JNode Missing = new JNode(null, false);

        JNode(object value, bool exists)
        {
            _value = value;
            Exists = exists;
        }

        public bool IsNull => Exists && _value == null;
        public bool IsObject => _value is Dictionary<string, JNode>;
        public bool IsArray => _value is List<JNode>;

        public JNode this[string key]
        {
            get
            {
                if (_value is Dictionary<string, JNode> o && o.TryGetValue(key, out var n)) return n;
                return Missing;
            }
        }

        public JNode this[int index]
        {
            get
            {
                if (_value is List<JNode> a && index >= 0 && index < a.Count) return a[index];
                return Missing;
            }
        }

        public bool Has(string key) => _value is Dictionary<string, JNode> o && o.ContainsKey(key);

        /// <summary>Element count for arrays and objects; 0 otherwise.</summary>
        public int Count =>
            _value is List<JNode> a ? a.Count :
            _value is Dictionary<string, JNode> o ? o.Count : 0;

        /// <summary>Enumerates array elements (empty when this is not an array).</summary>
        public IEnumerable<JNode> Items
        {
            get
            {
                if (_value is List<JNode> a)
                    foreach (var n in a) yield return n;
            }
        }

        /// <summary>Enumerates object members (empty when this is not an object).</summary>
        public IEnumerable<KeyValuePair<string, JNode>> Members
        {
            get
            {
                if (_value is Dictionary<string, JNode> o)
                    foreach (var kv in o) yield return kv;
            }
        }

        public string AsString(string fallback = null) => _value as string ?? fallback;
        public double AsDouble(double fallback = 0) => _value is double d ? d : fallback;
        public float AsFloat(float fallback = 0) => _value is double d ? (float)d : fallback;
        public bool AsBool(bool fallback = false) => _value is bool b ? b : fallback;

        public int AsInt(int fallback = 0) => _value is double d ? (int)d : fallback;
        public long AsLong(long fallback = 0) => _value is double d ? (long)d : fallback;

        public float[] AsFloatArray()
        {
            if (!(_value is List<JNode> a)) return null;
            var r = new float[a.Count];
            for (int i = 0; i < a.Count; i++) r[i] = a[i].AsFloat();
            return r;
        }

        // --- parsing ----------------------------------------------------------------------

        public static JNode Parse(string text)
        {
            int pos = 0;
            var root = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length)
                throw new FormatException($"Trailing characters in JSON at offset {pos}.");
            return root;
        }

        static JNode ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("Unexpected end of JSON.");
            char c = s[pos];
            switch (c)
            {
                case '{': return new JNode(ParseObject(s, ref pos), true);
                case '[': return new JNode(ParseArray(s, ref pos), true);
                case '"': return new JNode(ParseString(s, ref pos), true);
                case 't': Expect(s, ref pos, "true"); return new JNode(true, true);
                case 'f': Expect(s, ref pos, "false"); return new JNode(false, true);
                case 'n': Expect(s, ref pos, "null"); return new JNode(null, true);
                default: return new JNode(ParseNumber(s, ref pos), true);
            }
        }

        static Dictionary<string, JNode> ParseObject(string s, ref int pos)
        {
            var o = new Dictionary<string, JNode>();
            pos++; // '{'
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return o; }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"')
                    throw new FormatException($"Expected object key at offset {pos}.");
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':')
                    throw new FormatException($"Expected ':' at offset {pos}.");
                pos++;
                o[key] = ParseValue(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unterminated object.");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; break; }
                throw new FormatException($"Expected ',' or '}}' at offset {pos}.");
            }
            return o;
        }

        static List<JNode> ParseArray(string s, ref int pos)
        {
            var a = new List<JNode>();
            pos++; // '['
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return a; }
            while (true)
            {
                a.Add(ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unterminated array.");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; break; }
                throw new FormatException($"Expected ',' or ']' at offset {pos}.");
            }
            return a;
        }

        static string ParseString(string s, ref int pos)
        {
            pos++; // opening quote
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (pos >= s.Length) break;
                    char e = s[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 4 > s.Length) throw new FormatException("Bad \\u escape.");
                            sb.Append((char)ushort.Parse(s.Substring(pos, 4), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture));
                            pos += 4;
                            break;
                        default: throw new FormatException($"Invalid escape '\\{e}'.");
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("Unterminated string.");
        }

        static double ParseNumber(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length)
            {
                char c = s[pos];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                    pos++;
                else break;
            }
            if (pos == start) throw new FormatException($"Invalid number at offset {pos}.");
            return double.Parse(s.Substring(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || s.Substring(pos, literal.Length) != literal)
                throw new FormatException($"Expected '{literal}' at offset {pos}.");
            pos += literal.Length;
        }

        static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') pos++;
                else break;
            }
        }
    }
}
