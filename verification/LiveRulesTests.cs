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
    static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static void Run()
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
        GuideChecks();
        HarvestGuideChecks();
        RangeChecks();
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
    static void GuideChecks()
    {
        var index = new GuideIndex(); var a = Row("Crop", 1); a["waterFraction"] = 0.01;
        var b = Row("Crop", 2); b["waterFraction"] = 0.03;
        index.Add(a, "near", "Crop", 10, 3, 0, 0.1); index.Add(b, "far", "Crop", 14, 4, 0, 0.1);
        var nearest = index.Pick(0, 0, 0, null, null);
        Check(nearest.TileX == 1 && nearest.Y == 3, "guide targets actual needy tile and retains terrain height");
        var farther = index.Pick(0, 0, 0, null, "far");
        Check(farther.TileX == 2, "explicit region focus overrides nearest choice");
        Check(index.Pick(0, 0, 0, farther.Key, null).TileX == 2, "near-tie target held stable while walking");
        Check(index.Pick(0, 10, 0, farther.Key, null).TileX == 1, "materially closer target replaces previous one");
        Check(index.Pick(0, 0, 0, null, "missing").TileX == 1, "missing focused group falls back to nearest target");
        index.Resolve(nearest.Key); Check(index.Pick(0, 0, 0, null, null).TileX == 2, "resolved watering tile switches to next target");
        index.ExcludeTile(farther); Check(index.Pick(0, 0, 0, null, null) == null, "no marker after all candidates resolved");
        var irrigation = Row("Flower", 3); irrigation["waterFraction"] = 0; irrigation["irrigated"] = true;
        index.Add(irrigation, "irrigated", "Flower", 0, 0, 0, 0.1);
        var mature = Row("Crop", 4); mature["waterFraction"] = 0; mature["ready"] = true;
        index.Add(mature, "mature", "Crop", 0, 0, 0, 0.1);
        var locked = Row("Crop", 5); locked["waterFraction"] = 0; locked["unlocked"] = false;
        index.Add(locked, "locked", "Crop", 0, 0, 0, 0.1);
        var broken = Row("Crop", 6); broken["waterFraction"] = 0; broken["readError"] = "failure";
        index.Add(broken, "broken", "Crop", 0, 0, 0, 0.1);
        Check(index.Pick(0, 0, 0, null, null) == null, "irrigated, mature, locked and unreadable tiles excluded");
        var feeds = new GuideIndex(); var feed = Row("Animal", 7);
        feed["animalFieldId"] = 7; feed["foodAvailableForRace"] = 5; feed["foodNeededToFinishRace"] = 100;
        feeds.Add(feed, "feed-near", "Animal", 2, 0, 0, 0.1);
        feed["x"] = 8; feed["animalFieldId"] = 8; feed["foodAvailableForRace"] = 0;
        feeds.Add(feed, "feed-hungry", "Animal", 30, 0, 0, 0.1);
        feed["x"] = 9; feeds.Add(feed, "feed-hungry", "Animal", 24, 0, 0, 0.1);
        var hungry = feeds.Pick(1, 0, 0, null, null);
        Check(hungry.Hungry && hungry.TileX == 9, "nearest hungry animal preferred over nearby feed forecast");
        feeds.ExcludeTile(hungry);
        Check(feeds.Pick(1, 0, 0, null, null).TileX == 8, "one mature animal does not invalidate entire race");
        feeds.Resolve(hungry.Key);
        Check(feeds.Pick(1, 0, 0, null, null).TileX == 7, "refilled race removes all its candidate tiles");
        Check(feeds.Pick(0, 0, 0, null, null) == null, "water and feeding target categories remain separate");
        Check(!feeds.HasGroup("feed-hungry") && feeds.HasGroup("feed-near"), "focus detects resolved groups");
        feed["ready"] = true; feed["animalFieldId"] = 9; feed["x"] = 10;
        feeds.Add(feed, "ready-animal", "Animal", 0, 0, 0, 0.1);
        Check(feeds.Pick(1, 0, 0, null, null).TileX == 7, "mature animal not chosen for feed interaction");
        var center = GuideGeometry.Place(0.5, 0.5, 1, 1000, 500, 30);
        Check(center.Valid && center.OnScreen && center.X == 500 && center.Y == 250, "visible world target projects to screen center");
        var right = GuideGeometry.Place(2, 0.5, 1, 1000, 500, 30);
        Check(!right.OnScreen && Math.Abs(right.X - 970) < 0.001 && right.Y == 250 && right.DirectionX > 0, "offscreen right arrow stays at right inset");
        var left = GuideGeometry.Place(-1, 0.5, 1, 1000, 500, 30);
        Check(Math.Abs(left.X - 30) < 0.001 && left.DirectionX < 0, "offscreen left arrow");
        var top = GuideGeometry.Place(0.5, 2, 1, 1000, 500, 30);
        Check(Math.Abs(top.Y - 30) < 0.001 && top.DirectionY < 0, "viewport up maps to GUI top edge");
        var bottom = GuideGeometry.Place(0.5, -1, 1, 1000, 500, 30);
        Check(Math.Abs(bottom.Y - 470) < 0.001 && bottom.DirectionY > 0, "viewport down maps to GUI bottom edge");
        var diagonal = GuideGeometry.Place(2, 2, 1, 1000, 500, 30);
        Check(Math.Abs(diagonal.Y - 30) < 0.001 && Math.Abs(diagonal.X - 940) < 0.001, "diagonal arrows preserve direction on a widescreen viewport");
        var behind = GuideGeometry.Place(0.8, 0.5, -1, 1000, 500, 30);
        Check(!behind.OnScreen && Math.Abs(behind.X - 30) < 0.001, "behind-camera projection reverses its mirrored direction");
        var directlyBehind = GuideGeometry.Place(0.5, 0.5, -1, 1000, 500, 30);
        Check(directlyBehind.Valid && Math.Abs(directlyBehind.Y - 470) < 0.001, "directly behind target has a stable bottom indicator");
        var frontLeft = GuideGeometry.Place(0.2, 0.5, 1, 1000, 500, 30);
        var frontRight = GuideGeometry.Place(0.8, 0.5, 1, 1000, 500, 30);
        Check(frontLeft.X < center.X && frontRight.X > center.X, "camera-relative left and right follow projected view");
        Check(!GuideGeometry.Place(double.NaN, 0, 1, 1000, 500, 30).Valid && !GuideGeometry.Place(0, double.PositiveInfinity, 1, 1000, 500, 30).Valid, "unprojectable targets are not rendered");
        Check(!GuideGeometry.Place(0.5, 0.5, 1, 0, 500, 30).Valid, "inactive zero-width viewport excluded");
        var tiny = GuideGeometry.Place(2, -1, 1, 80, 60, 100);
        Check(tiny.Valid && tiny.X >= 0 && tiny.X <= 80 && tiny.Y >= 0 && tiny.Y <= 60, "small viewport retains contained edge marker");
    }
    static void HarvestGuideChecks()
    {
        var index = new GuideIndex();
        var crop = Row("Crop", 1); crop["ready"] = true; crop["waterFraction"] = 0;
        index.Add(crop, "crop", "Crop", 12, 0, 0, 0.1);
        var tree = Row("Tree", 2); tree["ready"] = true;
        index.Add(tree, "tree", "Tree", 6, 0, 0, 0.1);
        var flower = Row("Flower", 3); flower["ready"] = true;
        index.Add(flower, "flower", "Flower", 9, 0, 0, 0.1);
        Check(index.Pick(2, 0, 0, null, null).TileX == 2, "harvest chooses nearest ready crop, tree or flower");
        Check(index.Pick(0, 0, 0, null, null) == null, "ready crop belongs to harvest rather than watering guide");
        Check(index.Pick(2, 0, 0, null, "crop").TileX == 1, "harvest can focus a selected region");
        index.Resolve(index.Pick(2, 0, 0, null, null).Key);
        Check(index.Pick(2, 0, 0, null, null).TileX == 3, "harvesting nearest target advances to next one");
        var animal = Row("Animal", 4); animal["ready"] = true; animal["animalFieldId"] = 7;
        animal["foodAvailableForRace"] = 0; animal["foodNeededToFinishRace"] = 100;
        index.Add(animal, "animal", "Animal", 3, 0, 0, 0.1);
        Check(index.Pick(2, 0, 0, null, null).TileX == 4 && index.Pick(1, 0, 0, null, null) == null, "mature animal gets a harvest marker instead of a feeding marker");
        var ponds = new GuideIndex(); var pond = Row("Pond", 5); pond["ready"] = false; pond["pondHarvestable"] = true; pond["pondId"] = 9;
        ponds.Add(pond, "pond", "Fish", 8, 0, 0, 0.1);
        pond["x"] = 6; ponds.Add(pond, "pond", "Fish", 4, 0, 0, 0.1);
        var nearest = ponds.Pick(2, 0, 0, null, null);
        Check(nearest.TileX == 6, "harvestable pond can guide to nearest pond tile even when that tile is waiting");
        ponds.Resolve(nearest.Key);
        Check(ponds.Pick(2, 0, 0, null, null) == null, "harvested pond invalidates all its tile candidates");
        pond["ready"] = true; pond["pondHarvestable"] = false;
        Check(!GuideIndex.IsHarvestable(pond), "pond-wide harvest state takes precedence over individual fish timer");
        crop["ready"] = false; Check(!GuideIndex.IsHarvestable(crop), "harvest recheck drops a harvested crop");
        crop["ready"] = true; crop["unlocked"] = false; Check(!GuideIndex.IsHarvestable(crop), "locked mature crops excluded from guidance");
        crop["unlocked"] = true; crop["readError"] = "failed"; Check(!GuideIndex.IsHarvestable(crop), "unreadable mature targets excluded from guidance");
    }
    static void RangeChecks()
    {
        var index = new GuideIndex(); var row = Row("Crop", 1); row["waterFraction"] = 0;
        index.Add(row, "near", "Near", 10, 0, 0, 0.1);
        row["x"] = 2; index.Add(row, "far", "Far", 1000, 0, 0, 0.1);
        Check(index.Pick(0, 0, 0, null, null, 0, true).TileX == 2, "whole-farm far preference includes targets 500 tiles away");
        Check(index.Pick(0, 0, 0, null, null, 500, true).TileX == 2, "custom radius includes exact boundary");
        Check(index.Pick(0, 0, 0, null, null, 499, true).TileX == 1, "far preference respects custom maximum distance");
        Check(index.Pick(0, 0, 0, null, null, 5).TileX == 1, "distance expressed in farm tiles rather than world units");
        Check(index.Pick(0, 0, 0, null, null, 4) == null, "no marker when all candidates outside radius");
        Check(index.Pick(0, 0, 0, "water:2:1", "far", 5).TileX == 1, "out-of-range focus and previous target do not escape radius");
        var feed = Row("Animal", 3); feed["animalFieldId"] = 1; feed["foodAvailableForRace"] = 5; feed["foodNeededToFinishRace"] = 20;
        index.Add(feed, "feed-near", "Animal", 2, 0, 0, 0.1);
        feed["animalFieldId"] = 2; feed["x"] = 4; feed["foodAvailableForRace"] = 0;
        index.Add(feed, "feed-far", "Animal", 1000, 0, 0, 0.1);
        Check(index.Pick(1, 0, 0, null, null, 5).TileX == 3, "hungry animal outside radius does not block in-range feeding forecast");
        row["x"] = 5; index.Add(row, "very-far", "Very far", 20000, 0, 0, 0.1);
        Check(index.Pick(0, 0, 0, null, null, 10000, true).TileX == 5, "custom radius supports ten thousand farm tiles");
        Check(index.Pick(0, 0, 0, null, null, 0).TileX == 1, "default nearest behavior is preserved");
    }
}
