using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace ComfyLauncher
{
    // JSON as dictionaries and lists, through the serializer that ships with
    // .NET Framework, so nothing is added to the download. Every read is
    // forgiving: a missing key is null or empty, never an exception, because
    // the manifest and the state file are both older than this code.
    static class Json
    {
        static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static Dictionary<string, object> Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return Serializer.DeserializeObject(text.TrimStart('﻿')) as Dictionary<string, object>;
        }

        // Any JSON value: a list comes back as object[].
        public static object ParseValue(string text) =>
            string.IsNullOrWhiteSpace(text) ? null : Serializer.DeserializeObject(text.TrimStart('﻿'));

        public static object Get(this Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            foreach (var kv in d) if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        public static string Str(this Dictionary<string, object> d, string key) => d.Get(key) as string ?? "";

        public static long Long(this Dictionary<string, object> d, string key)
        {
            var v = d.Get(key);
            try { return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; }
        }

        public static bool Bool(this Dictionary<string, object> d, string key, bool fallback = false) =>
            d.Get(key) is bool b ? b : fallback;

        public static Dictionary<string, object> Obj(this Dictionary<string, object> d, string key) =>
            d.Get(key) as Dictionary<string, object>;

        public static List<Dictionary<string, object>> Objs(this Dictionary<string, object> d, string key) =>
            (d.Get(key) as IEnumerable)?.OfType<Dictionary<string, object>>().ToList()
            ?? new List<Dictionary<string, object>>();

        public static List<string> Strs(this Dictionary<string, object> d, string key) =>
            (d.Get(key) as IEnumerable)?.OfType<string>().ToList() ?? new List<string>();

        // An array of numbers. A string is IEnumerable too, of its characters, so it is no array here.
        public static List<long> Longs(this Dictionary<string, object> d, string key)
        {
            var list = new List<long>();
            if (!(d.Get(key) is IEnumerable items) || items is string) return list;
            foreach (var v in items)
                if (v != null && !(v is string)) try { list.Add(Convert.ToInt64(v)); } catch { }
            return list;
        }

        // Written indented, as the .ps1 launcher writes comfylauncher.json,
        // because a person opens that file to edit their realms.
        public static string Write(object value)
        {
            var sb = new System.Text.StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        static void Write(System.Text.StringBuilder sb, object v, int depth)
        {
            string pad = new string(' ', depth * 2), inner = new string(' ', (depth + 1) * 2);
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: sb.Append(Serializer.Serialize(s)); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case IDictionary<string, object> d:
                    if (d.Count == 0) { sb.Append("{}"); break; }
                    sb.Append("{\n");
                    int i = 0;
                    foreach (var kv in d)
                    {
                        sb.Append(inner).Append(Serializer.Serialize(kv.Key)).Append(": ");
                        Write(sb, kv.Value, depth + 1);
                        sb.Append(++i < d.Count ? ",\n" : "\n");
                    }
                    sb.Append(pad).Append('}');
                    break;
                case IEnumerable e:
                    var items = e.Cast<object>().ToList();
                    if (items.Count == 0) { sb.Append("[]"); break; }
                    sb.Append("[\n");
                    for (int j = 0; j < items.Count; j++)
                    {
                        sb.Append(inner);
                        Write(sb, items[j], depth + 1);
                        sb.Append(j + 1 < items.Count ? ",\n" : "\n");
                    }
                    sb.Append(pad).Append(']');
                    break;
                default: sb.Append(Serializer.Serialize(v)); break;
            }
        }
    }
}
