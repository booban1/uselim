using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Uselim
{
    // A small JSON reader and writer, so the exe carries zero libraries. Objects keep their keys in file order, so a
    // rewrite of Claude Code's settings changes only the entries Uselim owns. Numbers read as long when whole, else
    // double; arrays as List<object>; objects as JObject.

    public sealed class JObject : IEnumerable<KeyValuePair<string, object>>
    {
        private readonly List<string> keys = new List<string>();
        private readonly Dictionary<string, object> values = new Dictionary<string, object>();

        public int Count => keys.Count;
        public IEnumerable<string> Keys => keys;
        public bool Has(string key) => values.ContainsKey(key);

        public object this[string key]
        {
            get => values.TryGetValue(key, out var v) ? v : null;
            set
            {
                if (!values.ContainsKey(key)) keys.Add(key);
                values[key] = value;
            }
        }

        public void Remove(string key)
        {
            if (values.Remove(key)) keys.Remove(key);
        }

        public string Str(string key) => this[key] as string;
        public JObject Obj(string key) => this[key] as JObject;
        public List<object> Arr(string key) => this[key] as List<object>;

        public double? Num(string key)
        {
            switch (this[key])
            {
                case long l: return l;
                case double d: return d;
                default: return null;
            }
        }

        public bool? Bool(string key) => this[key] is bool b ? b : (bool?)null;

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            foreach (var k in keys) yield return new KeyValuePair<string, object>(k, values[k]);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public static class Json
    {
        // nil for anything that fails to parse
        public static object Parse(string text)
        {
            if (text == null) return null;
            try
            {
                var p = new Reader(text);
                p.Space();
                var value = p.Value();
                p.Space();
                return p.AtEnd ? value : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public static JObject ParseObject(string text) => Parse(text) as JObject;

        public static string Write(object value, bool pretty = false)
        {
            var sb = new StringBuilder();
            Emit(sb, value, pretty, 0);
            return sb.ToString();
        }

        private static void Emit(StringBuilder sb, object value, bool pretty, int depth)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: Quote(sb, s); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                    else if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                    else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JObject o:
                    if (o.Count == 0) { sb.Append("{}"); break; }
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in o)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        if (pretty) Line(sb, depth + 1);
                        Quote(sb, kv.Key);
                        sb.Append(pretty ? ": " : ":");
                        Emit(sb, kv.Value, pretty, depth + 1);
                    }
                    if (pretty) Line(sb, depth);
                    sb.Append('}');
                    break;
                case IList list:
                    if (list.Count == 0) { sb.Append("[]"); break; }
                    sb.Append('[');
                    for (var n = 0; n < list.Count; n++)
                    {
                        if (n > 0) sb.Append(',');
                        if (pretty) Line(sb, depth + 1);
                        Emit(sb, list[n], pretty, depth + 1);
                    }
                    if (pretty) Line(sb, depth);
                    sb.Append(']');
                    break;
                default:
                    Quote(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
        }

        private static void Line(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private sealed class Reader
        {
            private readonly string s;
            private int at;

            public Reader(string text) { s = text; }

            public bool AtEnd => at >= s.Length;

            public void Space()
            {
                while (at < s.Length && (s[at] == ' ' || s[at] == '\t' || s[at] == '\n' || s[at] == '\r' || s[at] == '﻿')) at++;
            }

            private char Peek() => at < s.Length ? s[at] : throw new FormatException("end of text");

            public object Value()
            {
                var c = Peek();
                switch (c)
                {
                    case '{': return Object();
                    case '[': return Array();
                    case '"': return String();
                    case 't': Word("true"); return true;
                    case 'f': Word("false"); return false;
                    case 'n': Word("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Number();
                        throw new FormatException("unexpected " + c);
                }
            }

            private void Word(string w)
            {
                if (string.CompareOrdinal(s, at, w, 0, w.Length) != 0) throw new FormatException("expected " + w);
                at += w.Length;
            }

            private JObject Object()
            {
                var o = new JObject();
                at++;
                Space();
                if (Peek() == '}') { at++; return o; }
                while (true)
                {
                    Space();
                    if (Peek() != '"') throw new FormatException("expected a key");
                    var key = String();
                    Space();
                    if (Peek() != ':') throw new FormatException("expected :");
                    at++;
                    Space();
                    o[key] = Value();
                    Space();
                    var c = Peek();
                    at++;
                    if (c == '}') return o;
                    if (c != ',') throw new FormatException("expected , or }");
                }
            }

            private List<object> Array()
            {
                var list = new List<object>();
                at++;
                Space();
                if (Peek() == ']') { at++; return list; }
                while (true)
                {
                    Space();
                    list.Add(Value());
                    Space();
                    var c = Peek();
                    at++;
                    if (c == ']') return list;
                    if (c != ',') throw new FormatException("expected , or ]");
                }
            }

            private string String()
            {
                at++;
                var sb = new StringBuilder();
                while (true)
                {
                    var c = Peek();
                    at++;
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    var e = Peek();
                    at++;
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
                            if (at + 4 > s.Length) throw new FormatException("short \\u");
                            sb.Append((char)int.Parse(s.Substring(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            at += 4;
                            break;
                        default: throw new FormatException("bad escape");
                    }
                }
            }

            private object Number()
            {
                var start = at;
                if (s[at] == '-') at++;
                var whole = true;
                while (at < s.Length)
                {
                    var c = s[at];
                    if (c >= '0' && c <= '9') { at++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { whole = false; at++; continue; }
                    break;
                }
                var text = s.Substring(start, at - start);
                if (whole && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                throw new FormatException("bad number " + text);
            }
        }
    }
}
