using System;
using System.IO;
using System.Reflection;
using FarmProgress;
using Newtonsoft.Json.Linq;
class AiExportTests
{
    static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    static int Main(string[] args)
    {
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) { return Assembly.LoadFrom(Path.Combine(args[1], new AssemblyName(e.Name).Name + ".dll")); };
            Run(args[0]); return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static void Run(string dir)
    {
        var snapshot = JObject.Parse(@"{ 'capturedAt':'2026-11-02T10:00:00+08:00','source':'synthetic-test','worldTicks':123,'level':42,'money':{'coins':'9223372036854775807'},'season':'Spring','weather':'None','nextSeasonInSeconds':200,'resources':[],'taskDetails':[],
        'unlocks':[{'id':'CropA','name':'测试作物','category':'Crop','visitorPurchaseState':'Unlocked'}],
        'growthTiles':[
        {'x':1,'y':1,'id':'CropA','category':'Crop','unlocked':true,'ready':true,'lowWater':false,'remainingSecondsEstimate':0},
        {'x':2,'y':1,'id':'CropA','category':'Crop','unlocked':true,'ready':false,'lowWater':true,'remainingSecondsEstimate':3600},
        {'x':3,'y':1,'id':'CropA','category':'Crop','unlocked':false,'ready':true,'lowWater':false,'remainingSecondsEstimate':0},
        {'x':4,'y':1,'id':'AnimalA','category':'Animal','unlocked':true,'ready':false,'lowWater':false,'remainingSecondsEstimate':null},
        {'x':5,'y':1,'id':'CropBroken','category':'Crop','unlocked':true,'readError':'missing field','remainingSecondsEstimate':null}] }");
        var summary = AiExport.Summarize(snapshot); var crop = (JObject)((JArray)summary["growthGroups"])[0];
        Check((int)crop["count"] == 3 && (int)crop["ready"] == 2, "all map crop counts");
        Check((int)crop["readyAccessible"] == 1 && (int)crop["accessibleCount"] == 2, "locked tiles excluded from actionable count");
        Check((int)crop["lowWaterAccessible"] == 1, "low-water count");
        Check((double)crop["minRemainingSeconds"] == 0 && (double)crop["maxRemainingSeconds"] == 3600, "ETA range includes ready cells");
        Check((int)crop["readyWithinOneHour"] == 3, "one-hour window inclusive");
        Check((int)((JArray)summary["growthGroups"])[1]["unknownEta"] == 1, "unfed animal has unknown ETA");
        Check((int)summary["tileReadErrors"] == 1, "read errors disclosed");
        Check((string)summary["money"]["coins"] == "9223372036854775807", "exact large balance");
        Check(((JArray)summary["taskPlan"]["holdings"]).Count == 3, "AI summary includes unlocked reusable and crop holdings even with no tasks");
        snapshot["taskDetails"] = JArray.Parse(@"[{'id':1,'title':'A','items':[{'id':'CropA','name':'测试作物','category':'Crop','required':10,'progress':6}]},
            {'id':2,'title':'B','items':[{'id':'CropA','name':'测试作物','category':'Crop','required':5,'progress':0}]}]");
        snapshot["taskPlan"] = TaskPlan.FromSnapshot(snapshot).ToJson();
        Directory.CreateDirectory(dir); AiExport.Write(dir, snapshot);
        string[] lines = File.ReadAllLines(Path.Combine(dir, "growth-tiles.jsonl"));
        Check(lines.Length == 5 && (int)JObject.Parse(lines[1])["x"] == 2, "JSONL one row per growth tile");
        var compact = JObject.Parse(File.ReadAllText(Path.Combine(dir, "ai-summary.json")));
        Check((long)compact["taskPlan"]["materials"][0]["remaining"] == 5 && (long)compact["taskPlan"]["materials"][0]["oneRoundAdditional"] == 3, "AI export retains merged task deficit and crop planting need");
        Check(((JArray)compact["taskPlan"]["materials"][0]["demands"]).Count == 2, "individual progress survives AI export");
        Check(compact["catalog"] == null && File.Exists(Path.Combine(dir, (string)compact["catalogFile"])), "catalog separated from quick summary");
        Check(File.ReadAllText(Path.Combine(dir, "AI-README.md")).Contains("不能库存抵交") || File.ReadAllText(Path.Combine(dir, "AI-README.md")).Contains("不假定库存可以提交"), "AI instructions preserve game rules");
        snapshot["growthTiles"] = new JArray(); Check(((JArray)AiExport.Summarize(snapshot)["growthGroups"]).Count == 0, "empty farm supported");
        Console.WriteLine("15 AI export checks passed (synthetic data)");
    }
}
