using System;
using System.Collections.Generic;
using FarmProgress;
using Newtonsoft.Json.Linq;
class LiveRulesTests
{
    static int count;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); count++; }
    static JObject Row(string category, int x = 1)
    { return new JObject { { "category", category }, { "x", x }, { "y", 1 }, { "id", "A" }, { "unlocked", true }, { "irrigated", false }, { "ready", false }, { "waterFraction", null }, { "remainingSecondsEstimate", null } }; }
    static void Main()
    {
        var rules = new LiveRules(); var row = Row("Crop"); row["waterFraction"] = 0.05; row["remainingSecondsEstimate"] = 200;
        rules.Add(row, "Crop A", 0.1, 300); var group = new System.Collections.Generic.List<LiveGroup>(rules.Groups.Values)[0];
        Check(group.LowWater == 1 && group.Soon == 1, "low water and upcoming maturity");
        row["unlocked"] = false; rules.Add(row, "Crop A", 0.1, 300); Check(group.Count == 1, "locked land excluded");
        row["unlocked"] = true; row["irrigated"] = true; rules.Add(row, "Crop A", 0.1, 300); Check(group.LowWater == 1, "irrigated excluded from watering");
        row["ready"] = true; row["irrigated"] = false; rules.Add(row, "Crop A", 0.1, 300); Check(group.Ready == 1 && group.LowWater == 1, "mature crops excluded from watering");
        var pond = Row("Pond"); pond["pondId"] = 8; pond["pondHarvestable"] = true;
        rules.Add(pond, "Pond", 0.1, 300); pond["x"] = 51; rules.Add(pond, "Pond", 0.1, 300);
        var ponds = new System.Collections.Generic.List<LiveGroup>(rules.Groups.Values).Find(g => g.Category == "Pond");
        Check(ponds.Count == 2 && ponds.Ready == 1, "one pond work unit across regions");
        var animal = Row("Animal"); animal["animalFieldId"] = 7; animal["foodAvailableForRace"] = 0; animal["foodNeededToFinishRace"] = 20;
        rules.Add(animal, "Animal", 0.1, 300); animal["x"] = 52; rules.Add(animal, "Animal", 0.1, 300);
        var animals = new System.Collections.Generic.List<LiveGroup>(rules.Groups.Values).Find(g => g.Category == "Animal");
        Check(animals.Count == 2 && animals.Hungry && animals.FeedShort && !animals.Eta.HasValue, "shared hungry animal race grouped, ETA unknown");
        var bad = Row("Crop", 80); bad["readError"] = "missing property"; rules.Add(bad, "Broken", 0.1, 300);
        Check(rules.ReadErrors == 1 && rules.Groups.Count == 3, "read errors disclosed without fake counters");
        var reminder = new ReminderRules(); Check(reminder.Evaluate(rules, 0, 300).Count == 0, "join does not replay existing alerts");
        Check(reminder.Evaluate(rules, 1, 300).Count == 0, "persistent states do not spam");
        group.Ready = 0; group.LowWater = 0; group.Soon = 0; animals.Hungry = false; animals.FeedShort = false;
        reminder.Evaluate(rules, 2, 300); group.Ready = 1; Check(reminder.Evaluate(rules, 3, 300).Count == 1, "ready transition produces alert");
        group.Ready = 0; reminder.Evaluate(rules, 4, 300); group.Ready = 1;
        Check(reminder.Evaluate(rules, 5, 300).Count == 0, "cooldown suppresses re-entry spam");
        group.Ready = 0; reminder.Evaluate(rules, 304, 300); group.Ready = 1;
        Check(reminder.Evaluate(rules, 305, 300).Count == 1, "alert re-arms after recovery and cooldown");
        Check(AdminNumbers.Target(9, 1, true, 10) == 10, "capacity inclusive");
        Check(AdminNumbers.Target(9, -8, true, 10) == 1, "negative delta allowed");
        Check(AdminNumbers.Target(9007199254740993L, 1, true, long.MaxValue) == 9007199254740994L, "large integers remain exact");
        Throws(() => AdminNumbers.Target(long.MaxValue, 1, true, long.MaxValue), "overflow rejected");
        Throws(() => AdminNumbers.Target(0, -1, true, long.MaxValue), "negative balance rejected");
        Throws(() => AdminNumbers.Target(0, 11, false, 10), "capacity overflow rejected");
        PresentationChecks();
        Console.WriteLine(count + " monitor/reminder/numeric checks passed");
    }
    static void PresentationChecks()
    {
        var rules = new LiveRules();
        var crop = new LiveGroup { Key = "a", Name = new string('名', 100), Category = "Crop", X = 123, Y = 456, Ready = 40, LowWater = 42, Soon = 30 };
        var pond = new LiveGroup { Key = "b", Category = "Pond", Ready = 2 };
        var animal = new LiveGroup { Key = "c", Category = "Animal", Hungry = true };
        rules.Groups.Add("a", crop); rules.Groups.Add("b", pond); rules.Groups.Add("c", animal);
        var counts = MonitorCounts.From(rules);
        Check(counts.HarvestTiles == 40 && counts.HarvestPonds == 2 && counts.Water == 42 && counts.Hungry == 1, "HUD retains mixed harvest units and shared feed groups");
        var card = counts.Card(2);
        Check(card.Body == "40 格成熟 · 2 个鱼塘" && !card.Body.Contains("123") && card.Body.Length < 30, "toast omits names and coordinates and retains unit distinction");
        var queue = new ReminderQueue();
        var many = new List<ReminderEvent>(); for (int i = 0; i < 100; i++) many.Add(new ReminderEvent { Kind = 3 });
        queue.Add(many, false, false);
        var water = queue.Take(rules, 0, 30, false, false);
        Check(water.Kind == 3 && water.Body == "42 格缺水", "one concise card merges a hundred watering alerts");
        Check(queue.Take(rules, 31, 30, false, false) == null, "merged alerts do not replay");
        queue.Add(new List<ReminderEvent> { new ReminderEvent { Kind = 2 }, new ReminderEvent { Kind = 0 } }, false, false);
        Check(queue.Take(rules, 32, 30, false, false).Kind == 0, "feeding urgency wins independent of event insertion order");
        Check(queue.Take(rules, 33, 30, false, false) == null, "global spacing prevents rapid consecutive cards");
        crop.Ready = 0; pond.Ready = 0;
        Check(queue.Take(rules, 63, 30, false, false) == null, "resolved harvest discarded before showing queued card");
        queue.Add(many, false, false); crop.LowWater = 7;
        Check(queue.Take(rules, 64, 30, false, false).Body == "7 格缺水", "queued card uses refreshed totals");
        queue.Add(many, false, false); queue.Clear();
        Check(queue.Take(rules, 100, 30, false, false) == null, "mute clears pending alerts");
        queue.Add(new List<ReminderEvent> { new ReminderEvent { Kind = 5 }, new ReminderEvent { Kind = 4 } }, false, false);
        Check(queue.Take(rules, 101, 30, false, false) == null, "optional forecast alerts off by default");
        queue.Add(new List<ReminderEvent> { new ReminderEvent { Kind = 5 } }, false, true);
        Check(queue.Take(rules, 102, 30, false, true).Kind == 5, "upcoming alerts can be enabled explicitly");
        crop.Ready = 5; crop.TaskRemaining = 10;
        queue.Add(new List<ReminderEvent> { new ReminderEvent { Kind = 1 }, new ReminderEvent { Kind = 2 } }, false, false);
        Check(queue.Take(rules, 133, 30, false, false).Kind == 1 && queue.Take(rules, 164, 30, false, false) == null, "task harvest replaces duplicate general harvest card");
        var transition = new ReminderRules(); transition.Evaluate(rules, 0, 300); crop.Ready = 0; transition.Evaluate(rules, 1, 300); crop.Ready = 5;
        var events = transition.Evaluate(rules, 2, 300);
        Check(events.Count == 1 && events[0].Kind == 1, "one raw event per task harvest transition");
    }
    static void Throws(Action action, string label) { try { action(); } catch { count++; return; } throw new Exception(label); }
}
