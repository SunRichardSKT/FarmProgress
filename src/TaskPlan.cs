using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace FarmProgress
{
    public sealed class TaskDemand
    {
        public string QuestId, Title;
        public long Required, Progress;
        public long Remaining { get { return Math.Max(0L, Required - Progress); } }
    }

    public sealed class TaskHolding
    {
        public string Key, Id, Name, Category;
        public long Planted, Ready, Unknown;
        public decimal? SuccessChance;
        public bool Reusable { get { return Category != "Crop"; } }
        public long Growing { get { return Math.Max(0L, Planted - Ready - Unknown); } }
    }

    public sealed class TaskMaterial
    {
        public TaskHolding Holding;
        public readonly List<TaskDemand> Demands = new List<TaskDemand>();
        public long Remaining
        {
            get { long result = 0; foreach (var demand in Demands) result = Math.Max(result, demand.Remaining); return result; }
        }
        public decimal? ExpectedReady
        { get { return Remaining == 0 ? 0m : Holding.SuccessChance.HasValue ? (decimal?)Math.Min(Remaining, Holding.Ready * Holding.SuccessChance.Value) : null; } }
        public long? OneRoundAdditional
        {
            get
            {
                if (Remaining == 0) return 0;
                if (!Holding.SuccessChance.HasValue || Holding.SuccessChance.Value <= 0) return null;
                return Math.Max(0L, (long)decimal.Ceiling(Remaining / Holding.SuccessChance.Value) - Holding.Planted);
            }
        }
        public long? RoundsAtCurrentSize
        {
            get
            {
                if (Remaining == 0) return 0;
                if (!Holding.Reusable && Remaining > Holding.Planted) return null;
                if (Holding.Planted == 0 || !Holding.SuccessChance.HasValue || Holding.SuccessChance.Value <= 0) return null;
                return (long)decimal.Ceiling(Remaining / (Holding.Planted * Holding.SuccessChance.Value));
            }
        }
        public string ActionText
        {
            get
            {
                if (Remaining == 0) return "此材料的当前任务已满足";
                if (!Holding.Reusable) return OneRoundAdditional > 0 ? "作物需补种 " + OneRoundAdditional + " 格" : "已种数量足够，收获即可";
                if (!Holding.SuccessChance.HasValue) return "捕获率未知，暂不估算扩养数量";
                if (Holding.SuccessChance == 0) return "当前捕获率为 0，无法推进此材料任务";
                if (Holding.Planted == 0) return "尚未种养；可先种养，再循环收获";
                return (Holding.Category == "Pond" ? "按捕获率预计 " : "按现有规模需收 ") + RoundsAtCurrentSize + " 轮" +
                    (OneRoundAdditional > 0 ? "；一轮完成可选加种/养 " + OneRoundAdditional + " 格" : "；无需扩种");
            }
        }
    }

    // Uses the same definition ID as native Quest.CheckProgress; resource inventory is not task progress.
    public sealed class TaskPlan
    {
        public readonly Dictionary<string, TaskHolding> Holdings = new Dictionary<string, TaskHolding>();
        public readonly Dictionary<string, TaskMaterial> Materials = new Dictionary<string, TaskMaterial>();
        readonly HashSet<string> quests = new HashSet<string>();
        public int QuestCount { get { return quests.Count; } }
        public int ReadErrors;
        public static string Key(string category, string id) { return category + ":" + id; }
        public static bool Supported(string category)
        { return category == "Crop" || category == "Flower" || category == "Tree" || category == "Animal" || category == "Pond"; }
        TaskHolding Holding(string category, string id, string name)
        {
            string key = Key(category, id); TaskHolding holding;
            if (!Holdings.TryGetValue(key, out holding))
            {
                holding = new TaskHolding { Key = key, Id = id, Category = category, Name = TargetText.Name(name, id), SuccessChance = category == "Pond" ? (decimal?)null : 1m };
                Holdings.Add(key, holding);
            }
            if (!string.IsNullOrEmpty(name)) holding.Name = name;
            return holding;
        }
        public void AddTile(JObject row, string name)
        {
            string category = (string)row["category"], id = (string)row["id"];
            if ((bool?)row["unlocked"] != true || !Supported(category) || string.IsNullOrEmpty(id)) return;
            var holding = Holding(category, id, name); holding.Planted++;
            if (row["readError"] != null) { holding.Unknown++; ReadErrors++; return; }
            if (category == "Pond")
            {
                decimal? chance = (decimal?)row["questSuccessChance"];
                if (chance.HasValue && chance >= 0 && chance <= 1) holding.SuccessChance = chance;
            }
            // A mixed pond being harvestable does not make every fish tile ready.
            if ((bool?)row["ready"] == true) holding.Ready++;
        }
        public void AddRequirement(string questId, string title, string category, string id, string name, long required, long progress, decimal? successChance = null)
        {
            if (!Supported(category) || string.IsNullOrEmpty(id)) return;
            var holding = Holding(category, id, name);
            if (category == "Pond" && successChance.HasValue && successChance >= 0 && successChance <= 1) holding.SuccessChance = successChance;
            quests.Add(questId);
            TaskMaterial material;
            if (!Materials.TryGetValue(holding.Key, out material))
            { material = new TaskMaterial { Holding = holding }; Materials.Add(holding.Key, material); }
            material.Demands.Add(new TaskDemand { QuestId = questId, Title = title, Required = Math.Max(0L, required), Progress = Math.Max(0L, progress) });
        }
        public static TaskPlan FromSnapshot(JObject snapshot)
        {
            var result = new TaskPlan();
            var names = new Dictionary<string, string>();
            var categories = new Dictionary<string, string>();
            var catalog = snapshot["unlocks"] as JArray;
            if (catalog != null) foreach (JObject def in catalog)
            { names[(string)def["id"]] = (string)def["name"]; categories[(string)def["id"]] = (string)def["category"]; }
            var tiles = snapshot["growthTiles"] as JArray;
            if (tiles != null) foreach (JObject tile in tiles)
            { string name; names.TryGetValue((string)tile["id"], out name); categories[(string)tile["id"]] = (string)tile["category"]; result.AddTile(tile, name); }
            var tasks = snapshot["taskDetails"] as JArray;
            if (tasks != null) foreach (JObject task in tasks)
                foreach (JObject item in (JArray)task["items"])
                {
                    string category = (string)item["category"]; if (category == null) categories.TryGetValue((string)item["id"], out category);
                    result.AddRequirement((string)task["id"], (string)task["title"], category, (string)item["id"], (string)item["name"],
                        (long)item["required"], (long)item["progress"], (decimal?)item["questSuccessChance"]);
                }
            return result;
        }
        public JObject ToJson()
        {
            var holdings = new JArray();
            var ordered = new List<TaskHolding>(Holdings.Values); ordered.Sort((a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
            foreach (var holding in ordered) holdings.Add(HoldingJson(holding));
            var materials = new JArray();
            foreach (var holding in ordered)
            {
                TaskMaterial material; if (!Materials.TryGetValue(holding.Key, out material)) continue;
                var row = HoldingJson(holding); var demands = new JArray();
                foreach (var demand in material.Demands) demands.Add(new JObject { { "questId", demand.QuestId }, { "title", demand.Title },
                    { "required", demand.Required }, { "progress", demand.Progress }, { "remaining", demand.Remaining } });
                row.Add("remaining", material.Remaining); row.Add("expectedReadyProgress", material.ExpectedReady);
                row.Add("oneRoundAdditional", material.OneRoundAdditional); row.Add("roundsAtCurrentSize", material.RoundsAtCurrentSize);
                row.Add("action", material.ActionText); row.Add("demands", demands); materials.Add(row);
            }
            return new JObject { { "scope", "loaded_unlocked_tiles" }, { "activeQuestCount", QuestCount }, { "tileReadErrors", ReadErrors },
                { "mergeRule", "max(max(0, required-progress)) by exact item definition across current active quests" },
                { "countModel", "One task point per harvest for crops/flowers/trees/animals; fish points require successful catches. Reusable oneRoundAdditional is optional expansion, not mandatory planting. Rounds are harvest batches, not time estimates." },
                { "materials", materials }, { "holdings", holdings } };
        }
        static JObject HoldingJson(TaskHolding holding)
        {
            return new JObject { { "id", holding.Id }, { "name", holding.Name }, { "category", holding.Category }, { "planted", holding.Planted },
                { "ready", holding.Ready }, { "growing", holding.Growing }, { "unknown", holding.Unknown }, { "reusable", holding.Reusable }, { "successChance", holding.SuccessChance } };
        }
        public string ToText()
        {
            var text = new StringBuilder("任务材料合并（各任务剩余数量取最大；仅已加载、已解锁区域）\n");
            var ordered = new List<TaskMaterial>(Materials.Values); ordered.Sort((a, b) => StringComparer.Ordinal.Compare(a.Holding.Key, b.Holding.Key));
            foreach (var material in ordered)
            {
                var h = material.Holding;
                text.Append(h.Name).Append("：合并待收 ").Append(material.Remaining).Append("；已种/养 ").Append(h.Planted).Append("；成熟格 ").Append(h.Ready)
                    .Append("；").Append(material.ActionText).AppendLine();
                foreach (var d in material.Demands) text.Append("  #").Append(d.QuestId).Append(" ").Append(d.Title).Append(" ").Append(d.Progress).Append('/').Append(d.Required).AppendLine();
            }
            return text.ToString();
        }
    }
}
