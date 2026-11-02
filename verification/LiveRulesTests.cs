using System;
using System.Collections.Generic;
using System.Globalization;
using FarmProgress;
using Newtonsoft.Json.Linq;
class LiveRulesTests
{
    static int count;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); count++; }
    static JObject Row(string category, int x = 1)
    { return new JObject { { "category", category }, { "x", x }, { "y", 1 }, { "id", "A" }, { "unlocked", true }, { "irrigated", false }, { "ready", false }, { "canFeedNow", category == "Animal" }, { "waterFraction", null }, { "remainingSecondsEstimate", null } }; }
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
        group.Ready = 0; group.LowWater = 0; group.Soon = 0; animals.Hungry = false; animals.FeedShort = false; animals.CanFeed = false;
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
        TargetNameChecks();
        FeedingChecks();
        Console.WriteLine(count + " monitor/reminder/numeric checks passed");
    }
    static void FeedingChecks()
    {
        var row = Row("Animal"); row["animalFieldId"] = 5;
        row["foodAvailableForRace"] = 90; row["foodNeededToFinishRace"] = 900;
        row["canFeedNow"] = false; row["feedBlockedReason"] = "饲料槽未空出一份";
        var blocked = new LiveRules(); var group = blocked.Add(row, "奶牛", 0.1, 300);
        Check(group.FeedShort && !group.CanFeed && !group.Hungry, "future feed shortage remains information when trough cannot accept a load");
        var counts = MonitorCounts.From(blocked);
        Check(counts.Hungry == 0 && counts.ShortFeed == 0, "forecast-only shortage is absent from actionable HUD totals");
        var guide = new GuideIndex(); guide.Add(row, group.Key, group.Name, 0, 0, 0, 0.1);
        Check(guide.Pick(1, 0, 0, null, null) == null, "full trough never creates a feeding direction marker");
        var queue = new ReminderQueue(); queue.Add(new List<ReminderEvent> { new ReminderEvent { Kind = 4 } }, true, false);
        Check(queue.Take(blocked, 0, 30, true, false) == null, "enabling refill reminders does not display forecast-only shortages");
        var reminder = new ReminderRules(); reminder.Evaluate(blocked, 0, 0);
        row["canFeedNow"] = true; var allowed = new LiveRules(); allowed.Add(row, "奶牛", 0.1, 300);
        var events = reminder.Evaluate(allowed, 1, 0);
        Check(events.Count == 1 && events[0].Kind == 4, "refill reminder starts when native feeding becomes available");
        Check(MonitorCounts.From(allowed).ShortFeed == 1 && LiveRules.NeedsFeed(row), "available nonhungry refill counts once");
        guide.Add(row, group.Key, group.Name, 0, 0, 0, 0.1);
        Check(guide.Pick(1, 0, 0, null, null) != null, "available refill produces a direction target");
        queue.Add(events, true, false); Check(queue.Take(blocked, 1, 30, true, false) == null, "queued refill disappears when native eligibility is lost");
        row["foodAvailableForRace"] = 0; row["canFeedNow"] = false; row["feedBlockedReason"] = "当前角色没有喂食权限";
        var hungryBlocked = new LiveRules(); var hungry = hungryBlocked.Add(row, "奶牛", 0.1, 300);
        Check(hungry.Hungry && !hungry.CanFeed && hungry.FeedBlockedReason.Contains("权限"), "blocked starvation is visible as farm data with a reason");
        Check(MonitorCounts.From(hungryBlocked).Hungry == 0, "starvation without permission does not ask player to feed");
        reminder.Evaluate(hungryBlocked, 2, 0); row["canFeedNow"] = true;
        var hungryAllowed = new LiveRules(); hungryAllowed.Add(row, "奶牛", 0.1, 300);
        events = reminder.Evaluate(hungryAllowed, 3, 0);
        Check(events.Count == 1 && events[0].Kind == 0, "restoring feeding permission triggers urgent starvation reminder");
        row["foodAvailableForRace"] = 900;
        Check(!LiveRules.NeedsFeed(row), "sufficient feed including reserves does not request an unnecessary top-up");
        row["foodAvailableForRace"] = 0; row.Remove("canFeedNow");
        Check(!LiveRules.NeedsFeed(row), "unknown feeding eligibility is not assumed actionable");
        row["canFeedNow"] = true; row["ready"] = true;
        Check(!LiveRules.NeedsFeed(row), "ready animal remains a harvest rather than feeding target");
        row["ready"] = false; row["unlocked"] = false;
        Check(!LiveRules.NeedsFeed(row), "feeding eligibility cannot enable locked land");
    }
    static void PresentationChecks()
    {
        var rules = new LiveRules();
        var crop = new LiveGroup { Key = "a", Name = new string('名', 100), Category = "Crop", X = 123, Y = 456, Ready = 40, LowWater = 42, Soon = 30 };
        var pond = new LiveGroup { Key = "b", Category = "Pond", Ready = 2 };
        var animal = new LiveGroup { Key = "c", Category = "Animal", Hungry = true, CanFeed = true };
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
        Check(hungry.Hungry && hungry.TileX == 9, "nearest hungry animal preferred over nearby available refill");
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
        Check(index.Pick(1, 0, 0, null, null, 5).TileX == 3, "hungry animal outside radius does not block in-range available refill");
        row["x"] = 5; index.Add(row, "very-far", "Very far", 20000, 0, 0, 0.1);
        Check(index.Pick(0, 0, 0, null, null, 10000, true).TileX == 5, "custom radius supports ten thousand farm tiles");
        Check(index.Pick(0, 0, 0, null, null, 0).TileX == 1, "default nearest behavior is preserved");
    }
    static void TargetNameChecks()
    {
        var rules = new LiveRules();
        var carrot = new LiveGroup { Key = "carrot", Name = "胡萝卜", Category = "Crop", LowWater = 42, Ready = 10, TaskRemaining = 10 };
        var cow = new LiveGroup { Key = "cow", Name = "奶牛", Category = "Animal", Hungry = true, FeedShort = true, CanFeed = true };
        var goat = new LiveGroup { Key = "goat", Name = "山羊", Category = "Animal", FeedShort = true, CanFeed = true };
        rules.Groups.Add("carrot", carrot); rules.Groups.Add("cow", cow); rules.Groups.Add("goat", goat);
        var counts = MonitorCounts.From(rules);
        Check(counts.Card(3).Targets == "胡萝卜" && counts.Card(3).Body == "42 格缺水", "watering popup shows relevant target name and exact quantity");
        Check(counts.Card(0).Targets == "奶牛" && counts.Card(4).Targets == "山羊", "hungry and refill popups use their own animal names");
        Check(counts.Card(1).Targets == "胡萝卜" && !counts.Card(2).Targets.Contains("奶牛"), "task and harvest names exclude unrelated animals");
        rules.Groups.Add("carrot2", new LiveGroup { Name = "胡萝卜", Category = "Crop", LowWater = 2 });
        Check(MonitorCounts.From(rules).Card(3).Targets == "胡萝卜", "same variety across regions appears once");
        rules.Groups.Add("corn", new LiveGroup { Name = "玉米", Category = "Crop", LowWater = 20 });
        rules.Groups.Add("wheat", new LiveGroup { Name = "小麦", Category = "Crop", LowWater = 1 });
        string names = MonitorCounts.From(rules).Card(3).Targets;
        Check(names == "胡萝卜、玉米等3种", "merged reminder presents two representative names and variety count");
        Check(StringInfo.ParseCombiningCharacters(names).Length <= 18, "merged names stay within popup text budget");
        var queue = new ReminderQueue(); queue.Add(new List<ReminderEvent> { new ReminderEvent { Kind = 3 } }, false, false);
        carrot.LowWater = 0; rules.Groups["carrot2"].LowWater = 0; rules.Groups["wheat"].LowWater = 0;
        Check(queue.Take(rules, 0, 30, false, false).Targets == "玉米", "queued popup refreshes names after work is resolved");
        Check(TargetText.GuideTitle(0, false, "胡萝卜") == "浇水 · 胡萝卜" && TargetText.GuideTitle(1, true, "奶牛") == "喂食 · 奶牛", "direction markers include operation and target name");
        Check(TargetText.GuideTitle(2, false, "苹果树") == "收获 · 苹果树" && TargetText.GuideTitle(1, false, "山羊") == "补料 · 山羊", "harvest and refill labels identify actual target");
        string longName = new string('名', 60);
        Check(TargetText.Compact(longName, 12).EndsWith("…") && StringInfo.ParseCombiningCharacters(TargetText.Compact(longName, 12)).Length == 12, "long direction names truncate to a bounded label");
        string combined = "A\u0301\U0001F34E\U0001F34E胡萝卜";
        Check(StringInfo.ParseCombiningCharacters(TargetText.Compact(combined, 4)).Length == 4 && TargetText.Compact(combined, 4).EndsWith("…"), "truncation retains Unicode text-element boundaries");
        Check(TargetText.Name("  胡萝卜\n\t 玉米  ", "unknown") == "胡萝卜 玉米", "embedded whitespace cannot split a one-line target label");
        Check(TargetText.Name(" \n\t", "CropA") == "CropA" && TargetText.Name(null, null) == "未命名目标", "missing translated names have readable fallbacks");
        var pondRules = new LiveRules(); var pondIndex = new GuideIndex();
        var fish = Row("Pond", 1); fish["id"] = "waiting"; fish["pondId"] = 7; fish["pondHarvestable"] = true;
        var group = pondRules.Add(fish, "未成熟鲱鱼", 0.1, 300); pondIndex.Add(fish, group.Key, "未成熟鲱鱼", 1, 0, 0, 0.1);
        fish["id"] = "ready"; fish["x"] = 2; fish["ready"] = true;
        pondRules.Add(fish, "金鱼", 0.1, 300); pondIndex.Add(fish, group.Key, "金鱼", 10, 0, 0, 0.1); pondIndex.CompleteNames(pondRules);
        Check(MonitorCounts.From(pondRules).Card(2).Targets == "金鱼", "mixed pond popup names fish that are actually harvestable");
        var nearest = pondIndex.Pick(2, 0, 0, null, null);
        Check(nearest.TileX == 1 && nearest.Name.Contains("金鱼") && !nearest.Name.Contains("未成熟鲱鱼"), "nearest pond interaction marker names harvestable fish rather than waiting fish at that tile");
        var first = new Dictionary<string, int> { { "玉米", 3 }, { "胡萝卜", 3 } };
        var second = new Dictionary<string, int> { { "胡萝卜", 3 }, { "玉米", 3 } };
        Check(TargetText.Summary(first, "目标") == TargetText.Summary(second, "目标"), "equal-size groups yield stable name ordering");
    }
}
