using System;
using System.Collections.Generic;
using System.Globalization;

namespace FarmProgress
{
    public static class TargetText
    {
        public static string Name(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value)) value = fallback;
            if (string.IsNullOrEmpty(value)) value = "未命名目标";
            string clean = string.Join(" ", value.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            return clean.Length == 0 ? Name(fallback == value ? null : fallback, "未命名目标") : clean;
        }
        public static string Compact(string value, int maxElements)
        {
            value = Name(value, "未命名目标");
            int[] positions = StringInfo.ParseCombiningCharacters(value);
            if (positions.Length <= maxElements) return value;
            if (maxElements <= 1) return "…";
            return value.Substring(0, positions[maxElements - 1]) + "…";
        }
        public static string Summary(Dictionary<string, int> names, string fallback)
        {
            if (names.Count == 0) return Compact(Name(null, fallback), 18);
            var sorted = new List<KeyValuePair<string, int>>(names);
            sorted.Sort((a, b) => a.Value == b.Value ? StringComparer.Ordinal.Compare(a.Key, b.Key) : b.Value.CompareTo(a.Value));
            if (sorted.Count == 1) return Compact(sorted[0].Key, 18);
            string suffix = sorted.Count > 2 ? "等" + sorted.Count + "种" : "";
            int budget = 18 - suffix.Length, first = Math.Min(7, (budget - 1) / 2), second = Math.Min(7, budget - first - 1);
            return Compact(sorted[0].Key, first) + "、" + Compact(sorted[1].Key, second) + suffix;
        }
        public static string Summary(IEnumerable<string> names, string fallback)
        {
            var counts = new Dictionary<string, int>();
            foreach (var name in names) Add(counts, name, 1);
            return Summary(counts, fallback);
        }
        public static void Add(Dictionary<string, int> names, string name, int amount)
        {
            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0 || amount <= 0) return;
            name = Name(name, "未命名目标"); int previous;
            names.TryGetValue(name, out previous); names[name] = previous + amount;
        }
        public static string GuideTitle(int kind, bool hungry, string name)
        { return (kind == 2 ? "收获" : kind == 0 ? "浇水" : hungry ? "喂食" : "补料") + " · " + Compact(name, 12); }
    }
}
