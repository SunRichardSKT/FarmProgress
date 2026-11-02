using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace FarmProgress
{
    public sealed class LiveGroup
    {
        public string Key, Name, Category, ItemId;
        public int X, Y, RegionX, RegionY, Count, Ready, LowWater, Soon, TaskRemaining;
        public bool Hungry, FeedShort;
        public double? Eta;
        public string Summary { get { return Name + " · " + Count + "格 · 可收获 " + Ready + " · 缺水 " + LowWater +
            (Hungry ? " · 已断粮" : FeedShort ? " · 饲料不足" : "") + (TaskRemaining > 0 ? " · 任务还需 " + TaskRemaining : "") +
            " · (" + X + "," + Y + ")"; } }
    }

    public sealed class LiveRules
    {
        public readonly Dictionary<string, LiveGroup> Groups = new Dictionary<string, LiveGroup>();
        public int ReadErrors;
        public static bool NeedsWater(JObject row, double threshold)
        {
            var water = (double?)row["waterFraction"];
            return (bool?)row["ready"] != true && (bool?)row["irrigated"] != true && water.HasValue && water.Value <= threshold;
        }
        public LiveGroup Add(JObject row, string name, double waterThreshold, double soonSeconds)
        {
            if (!(bool)row["unlocked"]) return null;
            if (row["readError"] != null) { ReadErrors++; return null; }
            string category = (string)row["category"], id = (string)row["id"];
            int x = (int)row["x"], y = (int)row["y"];
            int regionX = (int?)row["regionX"] ?? x / 20, regionY = (int?)row["regionY"] ?? y / 20;
            string area = category == "Animal" ? "field:" + row["animalFieldId"] + ":" + id :
                category == "Pond" ? "pond:" + row["pondId"] : regionX + ":" + regionY + ":" + id;
            string key = category + ":" + area;
            LiveGroup group;
            if (!Groups.TryGetValue(key, out group))
            {
                group = new LiveGroup { Key = key, Name = name, ItemId = id, Category = category, X = x, Y = y, RegionX = regionX, RegionY = regionY };
                Groups.Add(key, group);
            }
            group.Count++;
            bool ready = category == "Pond" ? (bool?)row["pondHarvestable"] == true : (bool?)row["ready"] == true;
            if (ready) group.Ready = category == "Pond" ? 1 : group.Ready + 1;
            if (!ready && NeedsWater(row, waterThreshold)) group.LowWater++;
            var eta = (double?)row["remainingSecondsEstimate"];
            if (!ready && eta.HasValue)
            {
                if (eta.Value <= soonSeconds) group.Soon++;
                if (!group.Eta.HasValue || eta.Value < group.Eta.Value) group.Eta = eta;
            }
            if (category == "Animal")
            {
                long? available = (long?)row["foodAvailableForRace"], needed = (long?)row["foodNeededToFinishRace"];
                group.Hungry |= needed > 0 && available == 0;
                group.FeedShort |= available.HasValue && needed.HasValue && available < needed;
            }
            return group;
        }

        public string Totals()
        {
            int ready = 0, water = 0, hungry = 0, shortFeed = 0;
            foreach (var group in Groups.Values)
            { ready += group.Ready; water += group.LowWater; if (group.Hungry) hungry++; else if (group.FeedShort) shortFeed++; }
            return "可收获 " + ready + "（鱼按塘） · 缺水 " + water + "格 · 断粮 " + hungry + "组 · 饲料不足 " + shortFeed + "组";
        }
    }

    public sealed class ReminderRules
    {
        Dictionary<string, bool> states = new Dictionary<string, bool>();
        readonly Dictionary<string, double> last = new Dictionary<string, double>();
        bool initialized;
        public List<ReminderEvent> Evaluate(LiveRules rules, double now, double cooldown)
        {
            var next = new Dictionary<string, bool>(); var messages = new List<ReminderEvent>();
            foreach (var group in rules.Groups.Values)
            {
                bool[] values = { group.Hungry, group.Ready > 0 && group.TaskRemaining > 0,
                    group.Ready > 0, group.LowWater > 0, group.FeedShort && !group.Hungry, group.Soon > 0 };
                for (int i = 0; i < values.Length; i++)
                {
                    string key = group.Key + ":" + i; next[key] = values[i]; bool previous; double previousTime;
                    if (values[i] && initialized && (!states.TryGetValue(key, out previous) || !previous) &&
                        (!last.TryGetValue(key, out previousTime) || now - previousTime >= cooldown))
                    {
                        // A task harvest already covers the same ready group.
                        if (i != 2 || group.TaskRemaining == 0) messages.Add(new ReminderEvent { Kind = i });
                        last[key] = now;
                    }
                }
            }
            states = next; initialized = true; return messages;
        }
    }
}
