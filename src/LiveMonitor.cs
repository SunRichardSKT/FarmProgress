using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Logic.Farm;
using Milkstone.Utils;
using UnityEngine;

namespace FarmProgress
{
    public sealed class LiveMonitor
    {
        readonly ConfigEntry<bool> enabled, reminders, hud;
        readonly ConfigEntry<float> threshold, interval, advance, cooldown;
        FarmData farm;
        IEnumerator<FarmTile> scan;
        GrowthSnapshot growth;
        LiveRules building;
        ReminderRules rules;
        float nextScan, capturedAt, toastUntil;
        string toast;
        Vector2 scroll;
        int filter;
        LiveGroup selected;
        Rect hudRect = new Rect(20, 55, 690, 55);
        public LiveRules Current { get; private set; }
        public string Error { get; private set; }
        public LiveMonitor(ConfigFile config)
        {
            enabled = config.Bind("Monitor", "Enabled", true, "Monitor loaded farm in batches");
            reminders = config.Bind("Monitor", "Reminders", true, "Show grouped in-game reminders");
            hud = config.Bind("Monitor", "Hud", true, "Show movable live summary");
            threshold = config.Bind("Monitor", "LowWaterFraction", 0.1f, "Water fraction threshold, 0 to 1");
            interval = config.Bind("Monitor", "RefreshSeconds", 2f, "Delay between completed scans");
            advance = config.Bind("Monitor", "AdvanceMinutes", 5f, "Upcoming harvest warning in minutes");
            cooldown = config.Bind("Monitor", "ReminderCooldownSeconds", 300f, "Reminder cooldown per group and state");
        }
        static IEnumerable<FarmTile> Tiles(FarmData data)
        {
            foreach (var chunk in data.Chunks)
                if (chunk != null && chunk.IsUnlocked)
                    foreach (var tile in chunk.Tiles) if (tile != null) yield return tile;
        }
        public void Update(FarmData data)
        {
            if (!ReferenceEquals(farm, data))
            {
                if (scan != null) scan.Dispose(); scan = null; farm = data;
                Current = null; rules = new ReminderRules(); nextScan = 0; selected = null; toast = null; Error = null;
            }
            if (farm == null || !enabled.Value) return;
            if (scan == null)
            {
                if (Time.realtimeSinceStartup < nextScan) return;
                scan = Tiles(farm).GetEnumerator(); growth = new GrowthSnapshot(); building = new LiveRules();
            }
            try
            {
                for (int i = 0; i < 512; i++)
                {
                    if (!scan.MoveNext()) { Complete(); return; }
                    var tile = scan.Current; var row = growth.Capture(tile);
                    if (row != null)
                    {
                        row["regionX"] = tile.Chunk.ChunkPosition.x; row["regionY"] = tile.Chunk.ChunkPosition.y;
                        building.Add(row, Localization.Get(tile.Contents.ShopItemDefinition.FullId),
                            Mathf.Clamp01(threshold.Value), Math.Max(0, advance.Value) * 60d);
                    }
                }
            }
            catch (Exception ex)
            { scan.Dispose(); scan = null; Error = ex.Message; nextScan = Time.realtimeSinceStartup + 10; }
        }
        void Complete()
        {
            scan.Dispose(); scan = null;
            foreach (var quest in farm.ActiveQuests)
                foreach (var item in quest.QuestItems)
                    foreach (var group in building.Groups.Values)
                        if (group.ItemId == item.ShopItem.FullId)
                            group.TaskRemaining += (int)Math.Min(int.MaxValue, Math.Max(0L, (long)item.RequiredAmount - item.Progress));
            var notes = rules.Evaluate(building, Time.realtimeSinceStartup, Math.Max(0, cooldown.Value));
            bool first = Current == null; Current = building; Error = null; capturedAt = Time.realtimeSinceStartup;
            nextScan = capturedAt + Mathf.Clamp(interval.Value, 0.5f, 60f);
            if (reminders.Value && Time.realtimeSinceStartup >= mutedUntil && (first || notes.Count > 0))
            {
                toast = first ? "农场待办：" + Current.Totals() : notes[0] + (notes.Count > 1 ? "（另有 " + (notes.Count - 1) + " 条，按 F8 查看）" : "");
                toastUntil = capturedAt + 12;
            }
        }
        public void DrawHud()
        {
            if (farm == null || !enabled.Value) return;
            if (hud.Value) hudRect = GUILayout.Window(183748, hudRect, id => {
                GUILayout.Label(Current == null ? "正在扫描农场…" : Current.Totals()); GUI.DragWindow();
            }, "农场实时待办 · F8 查看");
            if (reminders.Value && Time.realtimeSinceStartup < toastUntil && toast != null)
                GUI.Box(new Rect(20, Screen.height - 90, Mathf.Min(920, Screen.width - 40), 65), toast);
        }
        public void DrawPanel()
        {
            enabled.Value = GUILayout.Toggle(enabled.Value, "实时监控");
            hud.Value = GUILayout.Toggle(hud.Value, "显示悬浮待办");
            reminders.Value = GUILayout.Toggle(reminders.Value, "游戏内提醒");
            if (GUILayout.Button("静音提醒 10 分钟")) { toastUntil = 0; mutedUntil = Time.realtimeSinceStartup + 600; }
            if (Current == null) { GUILayout.Label(Error ?? "正在分批扫描农场…"); return; }
            GUILayout.Label(Current.Totals());
            GUILayout.Label((farm.IsLocal ? "房主数据" : "当前客户端观测") + " · " + (int)(Time.realtimeSinceStartup - capturedAt) + "秒前 · 读取失败 " + Current.ReadErrors);
            filter = GUILayout.SelectionGrid(filter, new[] { "全部", "收获", "浇水", "喂食", "即将成熟", "任务" }, 3);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(200));
            var groups = new List<LiveGroup>(Current.Groups.Values);
            groups.Sort((a, b) => Priority(b).CompareTo(Priority(a)));
            foreach (var group in groups)
            {
                if (filter == 1 && group.Ready == 0 || filter == 2 && group.LowWater == 0 ||
                    filter == 3 && !group.FeedShort || filter == 4 && group.Soon == 0 || filter == 5 && group.TaskRemaining == 0) continue;
                if (GUILayout.Button(group.Summary)) selected = group;
            }
            GUILayout.EndScrollView();
            if (selected != null)
            {
                GUILayout.Label("定位：" + selected.Name + "，坐标 (" + selected.X + "," + selected.Y + ")，区域 (" + selected.RegionX + "," + selected.RegionY + ")");
                GUILayout.Label(selected.Eta.HasValue ? "下一批预计 " + TimeSpan.FromSeconds(selected.Eta.Value).ToString() : "成熟时间需继续观察（动物供料不足时未知）");
            }
            DrawMap(groups);
            if (Error != null) GUILayout.Label("刷新错误：" + Error);
        }
        float mutedUntil;
        static int Priority(LiveGroup g) { return (g.Hungry ? 100 : 0) + (g.TaskRemaining > 0 && g.Ready > 0 ? 80 : 0) + (g.Ready > 0 ? 40 : 0) + (g.LowWater > 0 ? 20 : 0) + (g.FeedShort ? 10 : 0); }
        void DrawMap(List<LiveGroup> groups)
        {
            var rect = GUILayoutUtility.GetRect(420, 160); GUI.Box(rect, "待办示意图（点击定位）");
            int maxX = 1, maxY = 1;
            foreach (var g in groups) { maxX = Math.Max(maxX, g.X + 1); maxY = Math.Max(maxY, g.Y + 1); }
            foreach (var g in groups)
            {
                if (g.Ready == 0 && g.LowWater == 0 && !g.FeedShort) continue;
                var old = GUI.color; GUI.color = ReferenceEquals(selected, g) ? Color.white : g.Hungry ? Color.red : g.LowWater > 0 ? Color.cyan : Color.green;
                if (GUI.Button(new Rect(rect.x + 8 + g.X * (rect.width - 25) / maxX, rect.y + 25 + g.Y * (rect.height - 42) / maxY, 10, 10), "")) selected = g;
                GUI.color = old;
            }
        }
    }
}
