using System;
using System.Collections.Generic;

namespace FarmProgress
{
    public sealed class ReminderEvent { public int Kind; }

    public sealed class ReminderCard
    {
        public int Kind, Filter;
        public string Title, Body;
        public bool Urgent { get { return Kind == 0; } }
    }

    public sealed class MonitorCounts
    {
        public int HarvestTiles, HarvestPonds, Water, Hungry, ShortFeed, Soon, TaskReady;
        public int Harvest { get { return HarvestTiles + HarvestPonds; } }
        public static MonitorCounts From(LiveRules rules)
        {
            var counts = new MonitorCounts();
            foreach (var group in rules.Groups.Values)
            {
                if (group.Category == "Pond") counts.HarvestPonds += group.Ready;
                else counts.HarvestTiles += group.Ready;
                counts.Water += group.LowWater;
                if (group.Hungry) counts.Hungry++;
                else if (group.FeedShort) counts.ShortFeed++;
                counts.Soon += group.Soon;
                if (group.TaskRemaining > 0) counts.TaskReady += group.Ready;
            }
            return counts;
        }
        public bool Needs(int kind)
        {
            switch (kind)
            {
                case 0: return Hungry > 0;
                case 1: return TaskReady > 0;
                case 2: return Harvest > 0;
                case 3: return Water > 0;
                case 4: return ShortFeed > 0;
                case 5: return Soon > 0;
                default: return false;
            }
        }
        public ReminderCard Card(int kind)
        {
            switch (kind)
            {
                case 0: return new ReminderCard { Kind = kind, Filter = 3, Title = "该喂食了", Body = Hungry + " 组动物已断粮" };
                case 1: return new ReminderCard { Kind = kind, Filter = 5, Title = "任务目标可以收获了", Body = "收获后可推进任务" };
                case 2: return new ReminderCard { Kind = kind, Filter = 1, Title = "可以收获了", Body = HarvestPonds == 0 ? HarvestTiles + " 格已成熟" : HarvestTiles == 0 ? HarvestPonds + " 个鱼塘可收获" : HarvestTiles + " 格成熟 · " + HarvestPonds + " 个鱼塘" };
                case 3: return new ReminderCard { Kind = kind, Filter = 2, Title = "该浇水了", Body = Water + " 格缺水" };
                case 4: return new ReminderCard { Kind = kind, Filter = 3, Title = "饲料快不够了", Body = ShortFeed + " 组动物需要补料" };
                default: return new ReminderCard { Kind = kind, Filter = 4, Title = "快成熟了", Body = Soon + " 格即将成熟" };
            }
        }
    }

    // Bounded to six categories. Recheck current totals before displaying an alert,
    // so resolved work is not replayed from an old notification queue.
    public sealed class ReminderQueue
    {
        readonly bool[] pending = new bool[6];
        double nextAllowed;
        public void Add(List<ReminderEvent> events, bool feedForecast, bool upcoming)
        {
            foreach (var e in events)
                if (e.Kind >= 0 && e.Kind < 6 && (e.Kind != 4 || feedForecast) && (e.Kind != 5 || upcoming)) pending[e.Kind] = true;
        }
        public ReminderCard Take(LiveRules rules, double now, double spacing, bool feedForecast, bool upcoming)
        {
            var counts = MonitorCounts.From(rules);
            for (int i = 0; i < pending.Length; i++)
                if (!counts.Needs(i) || i == 4 && !feedForecast || i == 5 && !upcoming) pending[i] = false;
            if (now < nextAllowed) return null;
            for (int i = 0; i < pending.Length; i++)
                if (pending[i])
                {
                    pending[i] = false;
                    if (i == 1) pending[2] = false;
                    nextAllowed = now + Math.Max(12, spacing);
                    return counts.Card(i);
                }
            return null;
        }
        public void Clear() { Array.Clear(pending, 0, pending.Length); }
    }
}
