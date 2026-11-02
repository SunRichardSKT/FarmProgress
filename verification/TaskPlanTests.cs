using System;
using System.IO;
using FarmProgress;
using Newtonsoft.Json.Linq;

class TaskPlanTests
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static JObject Tile(string category, string id, bool ready = false)
    { return new JObject { { "category", category }, { "id", id }, { "unlocked", true }, { "ready", ready } }; }
    static void Tiles(TaskPlan plan, string category, string id, int count, int ready)
    { for (int i = 0; i < count; i++) plan.AddTile(Tile(category, id, i < ready), null); }
    static TaskMaterial Material(TaskPlan plan, string category, string id) { return plan.Materials[TaskPlan.Key(category, id)]; }
    static int Main(string[] args)
    {
        try { Run(args[0]); Console.WriteLine(checks + " task planning checks passed"); return 0; }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static void Run(string directory)
    {
        var plan = new TaskPlan();
        plan.AddRequirement("1", "旧任务", "Crop", "corn", "玉米", 100, 90);
        plan.AddRequirement("2", "新任务", "Crop", "corn", "玉米", 60, 0);
        var corn = Material(plan, "Crop", "corn");
        Check(corn.Remaining == 60, "shared progress takes max remaining, neither sum nor max required minus max progress");
        Check(plan.Materials.Count == 1 && plan.QuestCount == 2 && corn.Demands.Count == 2, "same variety merges without losing individual progress");
        Tiles(plan, "Crop", "corn", 40, 20);
        Check(corn.Holding.Planted == 40 && corn.Holding.Ready == 20 && corn.Holding.Growing == 20, "ready crop remains included in planted count");
        Check(corn.OneRoundAdditional == 20 && corn.ExpectedReady == 20, "crop deficit does not subtract ready tiles twice");
        Check(corn.RoundsAtCurrentSize == null && corn.ActionText.Contains("需补种 20"), "one-use crops cannot be treated as infinitely reusable stock");
        plan.AddRequirement("3", "另一个目标", "Crop", "redCorn", "玉米", 20, 0);
        Check(plan.Materials.Count == 2 && Material(plan, "Crop", "redCorn").Holding.Planted == 0, "same display names do not combine different definitions");
        plan.AddRequirement("4", "已完成", "Crop", "done", "已完成作物", 10, 15);
        var done = Material(plan, "Crop", "done");
        Check(done.Remaining == 0 && done.OneRoundAdditional == 0 && done.ExpectedReady == 0 && done.RoundsAtCurrentSize == 0, "overcompleted progress never creates negative work");
        plan.AddRequirement("4", "已完成", "Flower", "zero", "空目标", 0, 0);
        Check(Material(plan, "Flower", "zero").Remaining == 0 && plan.QuestCount == 4, "zero requirement and multiple targets in one quest");
        foreach (var category in new[] { "Flower", "Tree", "Animal" })
        {
            plan.AddRequirement("5", "循环收获", category, category, category, 45, 5);
            Tiles(plan, category, category, 15, 7);
            var m = Material(plan, category, category);
            Check(m.Holding.Reusable && m.RoundsAtCurrentSize == 3 && m.OneRoundAdditional == 25, category + " reuses actual stock across rounds");
            Check(m.ExpectedReady == 7 && m.ActionText.Contains("可选"), category + " current harvest and optional expansion stay separate");
        }
        plan.AddRequirement("5", "循环收获", "Tree", "none", "尚未种树", 30, 0);
        var none = Material(plan, "Tree", "none");
        Check(none.RoundsAtCurrentSize == null && none.OneRoundAdditional == 30 && none.ActionText.Contains("尚未种养"), "no reusable stock does not claim a finite existing harvest cycle");
        var locked = Tile("Tree", "Tree", true); locked["unlocked"] = false; plan.AddTile(locked, "Tree");
        Check(Material(plan, "Tree", "Tree").Holding.Planted == 15, "locked-region trees excluded");
        var broken = Tile("Flower", "Flower", true); broken["readError"] = "unknown growth state"; plan.AddTile(broken, "Flower");
        var flower = Material(plan, "Flower", "Flower");
        Check(flower.Holding.Planted == 16 && flower.Holding.Ready == 7 && flower.Holding.Unknown == 1 && flower.Holding.Growing == 8 && plan.ReadErrors == 1, "known occupancy survives a state error without inventing mature flowers");
        Tiles(plan, "Flower", "notATask", 9, 3);
        Check(plan.Holdings["Flower:notATask"].Planted == 9 && !plan.Materials.ContainsKey("Flower:notATask"), "farm inventory includes flowers not requested by active quests");
        plan.AddTile(Tile("Building", "shed", true), "shed");
        Check(!plan.Holdings.ContainsKey("Building:shed"), "building footprint cannot inflate harvest inventory");
        plan.AddRequirement("6", "钓鱼", "Pond", "fish", "金鱼", 10, 0, 0.5m);
        for (int i = 0; i < 10; i++)
        {
            var row = Tile("Pond", "fish", i < 4); row["pondId"] = 7; row["pondHarvestable"] = true; row["questSuccessChance"] = 0.5m;
            plan.AddTile(row, "金鱼");
        }
        var fish = Material(plan, "Pond", "fish");
        Check(fish.Holding.Planted == 10 && fish.Holding.Ready == 4, "fish count uses actual fish tiles instead of pond count or pond-wide readiness");
        Check(fish.ExpectedReady == 2 && fish.RoundsAtCurrentSize == 2 && fish.OneRoundAdditional == 10, "fish planning accounts for failed catches");
        Check(fish.ActionText.Contains("预计"), "probabilistic fish output is labeled as estimated");
        plan.AddRequirement("6", "钓鱼", "Pond", "otherFish", "另一鱼种", 4, 0, 1m);
        var other = Tile("Pond", "otherFish", true); other["pondId"] = 7; other["pondHarvestable"] = true; plan.AddTile(other, "另一鱼种");
        Check(Material(plan, "Pond", "otherFish").Holding.Planted == 1 && fish.Holding.Planted == 10, "mixed fish varieties in one pond remain distinct");
        plan.AddRequirement("6", "钓鱼", "Pond", "certainFish", "确定捕获", 2, 0, 1m);
        Tiles(plan, "Pond", "certainFish", 3, 3);
        Check(Material(plan, "Pond", "certainFish").ExpectedReady == 2 && Material(plan, "Pond", "certainFish").OneRoundAdditional == 0, "expected task progress is capped at remaining demand");
        plan.AddRequirement("6", "钓鱼", "Pond", "zeroFish", "零捕获", 2, 0, 0m); Tiles(plan, "Pond", "zeroFish", 2, 2);
        var zeroFish = Material(plan, "Pond", "zeroFish");
        Check(zeroFish.ExpectedReady == 0 && zeroFish.OneRoundAdditional == null && zeroFish.RoundsAtCurrentSize == null, "zero catch probability produces no fictional progress or divide-by-zero");
        plan.AddRequirement("6", "钓鱼", "Pond", "unknownFish", "未知捕获", 2, 0); Tiles(plan, "Pond", "unknownFish", 2, 2);
        Check(Material(plan, "Pond", "unknownFish").ExpectedReady == null && Material(plan, "Pond", "unknownFish").OneRoundAdditional == null, "missing fish probability stays unknown");
        plan.AddRequirement("6", "钓鱼", "Pond", "decimalFish", "精确比例", 29, 0, 0.29m);
        Check(Material(plan, "Pond", "decimalFish").OneRoundAdditional == 100, "decimal catch percentages avoid a spurious extra tile from floating-point rounding");
        plan.AddRequirement("7", "大数量", "Crop", "huge", "大数量", uint.MaxValue, 1);
        Check(Material(plan, "Crop", "huge").Remaining == 4294967294L && Material(plan, "Crop", "huge").OneRoundAdditional == 4294967294L, "task quantities stay exact above Int32");
        var json = JObject.Parse(plan.ToJson().ToString());
        Check((string)json["scope"] == "loaded_unlocked_tiles" && (int)json["activeQuestCount"] == 7, "export states scope and unique active quest count");
        Check(((JArray)json["materials"]).Count == plan.Materials.Count && ((JArray)json["holdings"]).Count == plan.Holdings.Count, "export preserves task material and non-task inventory lists");
        Check(plan.ToText().Contains("#1 旧任务 90/100") && plan.ToText().Contains("玉米：合并待收 60"), "copy text carries both shared deficit and individual task progress");
        var snapshot = JObject.Parse(@"{'unlocks':[{'id':'TreeX','name':'苹果树','category':'Tree'}], 'growthTiles':[{'category':'Tree','id':'TreeX','ready':true,'unlocked':true}],
          'taskDetails':[{'id':1,'title':'摘苹果','items':[{'id':'TreeX','name':'苹果树','required':8,'progress':3}]}]}");
        var restored = TaskPlan.FromSnapshot(snapshot); var tree = Material(restored, "Tree", "TreeX");
        Check(tree.Remaining == 5 && tree.Holding.Planted == 1 && tree.Holding.Ready == 1, "offline snapshot uses the same planner and can infer legacy target category from catalog");
        Check(new TaskPlan().ToJson()["materials"] is JArray && TaskPlan.FromSnapshot(new JObject()).QuestCount == 0, "empty farm and no active quests remain usable");
        var overlay = TaskPlanDisplay.OverlayRows(plan, false);
        Check(overlay.Count < plan.Materials.Count && overlay.TrueForAll(m => m.Remaining > 0), "floating table defaults to current unfinished materials");
        Check(TaskPlanDisplay.OverlayRows(plan, true).Count == plan.Materials.Count, "floating table can include already satisfied materials");
        Check(overlay[0].Holding.Ready > 0, "floating table prioritizes currently mature material targets");
        Check(TaskPlanDisplay.Progress(corn) == "#1 90/100 · #2 0/60", "floating rows display independent shared task progress");
        Check(TaskPlanDisplay.Additional(corn) == "补种20", "floating crop deficit is labeled mandatory planting");
        Check(TaskPlanDisplay.Additional(Material(plan, "Tree", "Tree")) == "可选25", "floating reusable deficit is labeled optional expansion");
        Check(TaskPlanDisplay.Additional(fish) == "可选≈10", "floating fish expansion retains probability estimate marker");
        Check(TaskPlanDisplay.Additional(done) == "已满足" && TaskPlanDisplay.Additional(zeroFish) == "未知", "completed and unestimable floating rows stay distinct");
        Check(TaskPlanDisplay.PageCount(0, 5) == 1 && TaskPlanDisplay.PageCount(5, 5) == 1 && TaskPlanDisplay.PageCount(12, 5) == 3, "floating pagination handles empty, full and partial pages");
        var inventoryRows = TaskPlanDisplay.InventoryRows(plan);
        Check(inventoryRows.Exists(h => h.Id == "notATask") && inventoryRows.TrueForAll(h => h.Planted > 0), "floating inventory includes non-task flowers and omits unplanted requirements");
        Check(TaskPlanDisplay.CategoryName("Tree") == "树木" && TaskPlanDisplay.CategoryName("Pond") == "鱼", "floating inventory identifies reusable categories in Chinese");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "task-plan.json"), plan.ToJson().ToString());
        var demo = new TaskPlan();
        demo.AddRequirement("21", "丰收时节", "Crop", "corn", "玉米", 120, 40);
        demo.AddRequirement("22", "农场订单", "Crop", "corn", "玉米", 100, 70); Tiles(demo, "Crop", "corn", 50, 20);
        demo.AddRequirement("21", "丰收时节", "Tree", "apple", "苹果树", 100, 40); Tiles(demo, "Tree", "apple", 20, 5);
        demo.AddRequirement("23", "花园计划", "Flower", "daisy", "雏菊", 50, 20); Tiles(demo, "Flower", "daisy", 15, 0);
        demo.AddRequirement("22", "农场订单", "Pond", "goldfish", "金鱼", 100, 60, 0.8m); Tiles(demo, "Pond", "goldfish", 24, 8);
        demo.AddTile(Tile("Flower", "rose", true), "玫瑰");
        File.WriteAllText(Path.Combine(directory, "demo-plan.json"), demo.ToJson().ToString());
    }
}
