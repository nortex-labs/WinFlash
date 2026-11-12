using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WinFlash.Core
{
    /// <summary>
    /// Небольшой JSON-парсер и сериализатор без внешних зависимостей
    /// (в .NET Framework 4.8 и WinPE-NetFx нет System.Text.Json).
    /// Объекты — Dictionary&lt;string, object&gt;, массивы — List&lt;object&gt;,
    /// числа — double, плюс string, bool и null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var p = new Parser(text);
            p.SkipWs();
            var value = p.ReadValue();
            p.SkipWs();
            if (!p.End) throw p.Error("лишние символы после конца JSON");
            return value;
        }

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        // ------------------------------------------------------------ удобные геттеры

        public static Dictionary<string, object> Obj(this object o) => o as Dictionary<string, object>;

        public static List<object> Arr(this object o, string key)
            => (o.Obj() != null && o.Obj().TryGetValue(key, out var v) ? v as List<object> : null) ?? new List<object>();

        public static string Str(this object o, string key, string def = "")
            => o.Obj() != null && o.Obj().TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;

        // ------------------------------------------------------------ запись

        static void WriteValue(StringBuilder sb, object v, int indent)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> obj:
                    if (obj.Count == 0) { sb.Append("{}"); break; }
                    sb.Append("{\n");
                    int n = 0;
                    foreach (var kv in obj)
                    {
                        sb.Append(' ', (indent + 1) * 2);
                        WriteString(sb, kv.Key);
                        sb.Append(": ");
                        WriteValue(sb, kv.Value, indent + 1);
                        if (++n < obj.Count) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append(' ', indent * 2).Append('}');
                    break;
                case System.Collections.IEnumerable list:
                    var items = new List<object>();
                    foreach (var item in list) items.Add(item);
                    if (items.Count == 0) { sb.Append("[]"); break; }
                    bool simple = items.TrueForAll(x => !(x is IDictionary<string, object>) && !(x is List<object>));
                    if (simple)
                    {
                        sb.Append("[ ");
                        for (int k = 0; k < items.Count; k++)
                        {
                            if (k > 0) sb.Append(", ");
                            WriteValue(sb, items[k], indent + 1);
                        }
                        sb.Append(" ]");
                    }
                    else
                    {
                        sb.Append("[\n");
                        for (int k = 0; k < items.Count; k++)
                        {
                            sb.Append(' ', (indent + 1) * 2);
                            WriteValue(sb, items[k], indent + 1);
                            if (k < items.Count - 1) sb.Append(',');
                            sb.Append('\n');
                        }
                        sb.Append(' ', indent * 2).Append(']');
                    }
                    break;
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
                    break;
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------ чтение

        sealed class Parser
        {
            readonly string s;
            int i;
            public Parser(string text) { s = text ?? ""; if (s.Length > 0 && s[0] == '﻿') i = 1; }
            public bool End => i >= s.Length;

            public FormatException Error(string msg)
            {
                int line = 1;
                for (int k = 0; k < i && k < s.Length; k++) if (s[k] == '\n') line++;
                return new FormatException($"Ошибка JSON (строка {line}): {msg}");
            }

            public void SkipWs()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            public object ReadValue()
            {
                SkipWs();
                if (End) throw Error("неожиданный конец");
                char c = s[i];
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (c == '"') return ReadString();
                if (c == 't' && Match("true")) return true;
                if (c == 'f' && Match("false")) return false;
                if (c == 'n' && Match("null")) return null;
                if (c == '-' || char.IsDigit(c)) return ReadNumber();
                throw Error($"неожиданный символ «{c}»");
            }

            bool Match(string word)
            {
                if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
                i += word.Length;
                return true;
            }

            Dictionary<string, object> ReadObject()
            {
                var obj = new Dictionary<string, object>(StringComparer.Ordinal);
                i++; SkipWs();
                if (!End && s[i] == '}') { i++; return obj; }
                while (true)
                {
                    SkipWs();
                    if (End || s[i] != '"') throw Error("ожидалось имя поля");
                    string key = ReadString();
                    SkipWs();
                    if (End || s[i] != ':') throw Error("ожидалось «:»");
                    i++;
                    obj[key] = ReadValue();
                    SkipWs();
                    if (End) throw Error("объект не закрыт");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return obj; }
                    throw Error("ожидалось «,» или «}»");
                }
            }

            List<object> ReadArray()
            {
                var list = new List<object>();
                i++; SkipWs();
                if (!End && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ReadValue());
                    SkipWs();
                    if (End) throw Error("массив не закрыт");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return list; }
                    throw Error("ожидалось «,» или «]»");
                }
            }

            string ReadString()
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (End) throw Error("строка не закрыта");
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw Error("строка не закрыта");
                    char e = s[i++];
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
                            if (i + 4 > s.Length) throw Error("неполный \\u");
                            sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                            i += 4;
                            break;
                        default: throw Error($"неизвестная escape-последовательность \\{e}");
                    }
                }
            }

            double ReadNumber()
            {
                int start = i;
                if (s[i] == '-') i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
                return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
