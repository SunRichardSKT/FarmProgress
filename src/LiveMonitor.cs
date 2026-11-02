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
        readonly ConfigEntry<bool> feedForecast, upcoming;
        readonly ConfigEntry<float> noticeScale, noticeSpacing;
        FarmData farm;
        IEnumerator<FarmTile> scan;
        GrowthSnapshot growth;
        LiveRules building;
        GuideIndex guidanceIndex, buildingGuidance;
        readonly DirectionGuide guides;
        ReminderRules rules;
        float nextScan, capturedAt, toastUntil;
        ReminderCard toast;
        ReminderQueue queue = new ReminderQueue();
        MonitorCounts currentCounts;
        float toastDuration;
        GUIStyle titleStyle, bodyStyle, hintStyle, hudNumberStyle, hudLabelStyle;
        Vector2 scroll;
        int filter;
        LiveGroup selected;
        Rect hudRect = new Rect(20, 55, 350, 85);
        public Action<int> OpenDetails;
        public LiveRules Current { get; private set; }
        public string Error { get; private set; }
        public LiveMonitor(ConfigFile config)
        {
            guides = new DirectionGuide(config);
            guides.OpenDetails = SelectDetails;
            enabled = config.Bind("Monitor", "Enabled", true, "Monitor loaded farm in batches");
            reminders = config.Bind("Monitor", "Reminders", true, "Show grouped in-game reminders");
            hud = config.Bind("Monitor", "Hud", true, "Show movable live summary");
            threshold = config.Bind("Monitor", "LowWaterFraction", 0.1f, "Water fraction threshold, 0 to 1");
            interval = config.Bind("Monitor", "RefreshSeconds", 2f, "Delay between completed scans");
            advance = config.Bind("Monitor", "AdvanceMinutes", 5f, "Upcoming harvest warning in minutes");
            cooldown = config.Bind("Monitor", "ReminderCooldownSeconds", 300f, "Reminder cooldown per group and state");
            feedForecast = config.Bind("Monitor", "FeedForecastNotifications", false, "Also pop up forecast warnings before feed runs out");
            upcoming = config.Bind("Monitor", "UpcomingNotifications", false, "Also pop up upcoming maturity reminders");
            noticeScale = config.Bind("Monitor", "NotificationScale", 1.1f, "Size of the compact HUD and notification cards, 0.8 to 1.6");
            noticeSpacing = config.Bind("Monitor", "NotificationSpacingSeconds", 30f, "Minimum interval between grouped cards");
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
                Current = null; currentCounts = null; guidanceIndex = null; buildingGuidance = null;
                rules = new ReminderRules(); queue = new ReminderQueue(); nextScan = 0; selected = null; toast = null; Error = null;
            }
            guides.Update(enabled.Value ? data : null, guidanceIndex, Mathf.Clamp01(threshold.Value));
            if (farm == null || !enabled.Value) return;
            if (scan == null)
            {
                if (Time.realtimeSinceStartup < nextScan) return;
                scan = Tiles(farm).GetEnumerator(); growth = new GrowthSnapshot(); building = new LiveRules();
                buildingGuidance = new GuideIndex();
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
                        string name = Localization.Get(tile.Contents.ShopItemDefinition.FullId);
                        var group = building.Add(row, name,
                            Mathf.Clamp01(threshold.Value), Math.Max(0, advance.Value) * 60d);
                        if (group != null)
                        {
                            var position = tile.WorldPosition;
                            buildingGuidance.Add(row, group.Key, name, position.x, position.y, position.z, Mathf.Clamp01(threshold.Value));
                        }
                    }
                }
            }
            catch (Exception ex)
            { if (scan != null) scan.Dispose(); scan = null; Error = ex.Message; nextScan = Time.realtimeSinceStartup + 10; }
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
            currentCounts = MonitorCounts.From(Current);
            guidanceIndex = buildingGuidance;
            guides.Update(farm, guidanceIndex, Mathf.Clamp01(threshold.Value));
            nextScan = capturedAt + Mathf.Clamp(interval.Value, 0.5f, 60f);
            if (reminders.Value && Time.realtimeSinceStartup >= mutedUntil)
            {
                var counts = currentCounts;
                if (first && counts.Hungry > 0) notes.Add(new ReminderEvent { Kind = 0 });
                queue.Add(notes, feedForecast.Value, upcoming.Value);
                if (toast != null && !counts.Needs(toast.Kind)) toast = null;
                if (toast == null || capturedAt >= toastUntil)
                {
                    toast = queue.Take(Current, capturedAt, noticeSpacing.Value, feedForecast.Value, upcoming.Value);
                    if (toast != null) { toastDuration = toast.Urgent ? 12 : 8; toastUntil = capturedAt + toastDuration; }
                }
                else toast = counts.Card(toast.Kind);
            }
            else { queue.Clear(); toast = null; }
        }
        public void DrawHud()
        {
            if (farm == null || !enabled.Value) return;
            EnsureStyles();
            float scale = Mathf.Min(Mathf.Clamp(noticeScale.Value, 0.8f, 1.6f), (Screen.width - 24) / 350f);
            guides.Draw(scale);
            if (hud.Value)
            {
                hudRect.width = 320 * scale; hudRect.height = 78 * scale;
                hudRect.x = Mathf.Clamp(hudRect.x, 0, Mathf.Max(0, Screen.width - hudRect.width));
                hudRect.y = Mathf.Clamp(hudRect.y, 0, Mathf.Max(0, Screen.height - hudRect.height));
                hudRect = GUI.Window(183748, hudRect, DrawCompactHud, "", GUIStyle.none);
            }
            if (reminders.Value && Time.realtimeSinceStartup >= mutedUntil && Time.realtimeSinceStartup < toastUntil && toast != null) DrawCard(scale);
        }
        static readonly Color Green = new Color(0.42f, 0.91f, 0.57f), Blue = new Color(0.38f, 0.77f, 1f), Red = new Color(1f, 0.40f, 0.36f), Amber = new Color(1f, 0.78f, 0.30f);
        void EnsureStyles()
        {
            if (titleStyle != null) return;
            titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip };
            bodyStyle = new GUIStyle(titleStyle) { fontStyle = FontStyle.Normal };
            hintStyle = new GUIStyle(bodyStyle);
            hudNumberStyle = new GUIStyle(titleStyle) { alignment = TextAnchor.MiddleCenter };
            hudLabelStyle = new GUIStyle(bodyStyle) { alignment = TextAnchor.MiddleCenter };
            titleStyle.normal.textColor = bodyStyle.normal.textColor = hudNumberStyle.normal.textColor = Color.white;
            hintStyle.normal.textColor = hudLabelStyle.normal.textColor = new Color(0.72f, 0.77f, 0.83f);
        }
        static void Fill(Rect rect, Color color)
        {
            var old = GUI.color; GUI.color = old * color; GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = old;
        }
        static void Surface(Rect rect, Color accent)
        {
            Fill(new Rect(rect.x + 3, rect.y + 4, rect.width, rect.height), new Color(0, 0, 0, 0.45f));
            Fill(rect, new Color(0.04f, 0.06f, 0.09f, 0.97f));
            Fill(new Rect(rect.x, rect.y, 5, rect.height), accent);
        }
        void DrawCompactHud(int id)
        {
            float s = hudRect.width / 320f;
            var counts = currentCounts;
            Surface(new Rect(0, 0, hudRect.width, hudRect.height), counts != null && counts.Hungry > 0 ? Red : Blue);
            hintStyle.fontSize = Mathf.RoundToInt(11 * s);
            GUI.Label(new Rect(13 * s, 4 * s, 295 * s, 20 * s), "农场待办                         拖动 · F8", hintStyle);
            bodyStyle.fontSize = Mathf.RoundToInt(18 * s);
            if (counts == null) GUI.Label(new Rect(15 * s, 30 * s, 290 * s, 35 * s), "正在扫描…", bodyStyle);
            else if (counts.Harvest == 0 && counts.Water == 0 && counts.Hungry == 0 && counts.ShortFeed == 0)
            {
                bodyStyle.fontSize = Mathf.RoundToInt(18 * s);
                GUI.Label(new Rect(15 * s, 30 * s, 290 * s, 35 * s), "暂无待办", bodyStyle);
            }
            else
            {
                DrawHudCount(0, "收获", counts.Harvest, Green, 1, "植物/动物按格，鱼按整塘", s);
                DrawHudCount(1, "浇水", counts.Water, Blue, 2, "未成熟且缺水的地格", s);
                DrawHudCount(2, "喂食", counts.Hungry + counts.ShortFeed, counts.Hungry > 0 ? Red : Amber, 3,
                    "已断粮 " + counts.Hungry + " 组；本轮饲料不足 " + counts.ShortFeed + " 组", s);
            }
            GUI.DragWindow(new Rect(0, 0, hudRect.width, 24 * s));
        }
        void DrawHudCount(int index, string label, int count, Color accent, int targetFilter, string tooltip, float s)
        {
            var rect = new Rect((12 + index * 102) * s, 24 * s, 98 * s, 50 * s);
            if (GUI.Button(rect, new GUIContent("", tooltip), GUIStyle.none)) SelectDetails(targetFilter);
            hudNumberStyle.fontSize = Mathf.RoundToInt(24 * s); hudNumberStyle.normal.textColor = count > 0 ? accent : new Color(0.45f, 0.49f, 0.55f);
            hudLabelStyle.fontSize = Mathf.RoundToInt(12 * s);
            GUI.Label(new Rect(rect.x, rect.y - 2 * s, rect.width, 31 * s), count.ToString(), hudNumberStyle);
            GUI.Label(new Rect(rect.x, rect.y + 28 * s, rect.width, 20 * s), label, hudLabelStyle);
        }
        void DrawCard(float s)
        {
            float width = 350 * s, height = 126 * s;
            var rect = new Rect((Screen.width - width) / 2, Mathf.Min(95 * s, Screen.height - height - 10), width, height);
            Color accent = toast.Kind == 0 ? Red : toast.Kind == 3 ? Blue : toast.Kind >= 4 ? Amber : Green;
            var old = GUI.color; GUI.color = new Color(old.r, old.g, old.b, old.a * Mathf.Clamp01((toastUntil - Time.realtimeSinceStartup) / 0.4f));
            Surface(rect, accent);
            titleStyle.fontSize = Mathf.RoundToInt(23 * s); titleStyle.normal.textColor = accent;
            bodyStyle.fontSize = Mathf.RoundToInt(19 * s); hintStyle.fontSize = Mathf.RoundToInt(12 * s);
            GUI.Label(new Rect(rect.x + 20 * s, rect.y + 12 * s, width - 50 * s, 34 * s), toast.Title, titleStyle);
            GUI.Label(new Rect(rect.x + 20 * s, rect.y + 50 * s, width - 35 * s, 30 * s), toast.Body, bodyStyle);
            GUI.Label(new Rect(rect.x + 20 * s, rect.y + 91 * s, width - 35 * s, 24 * s), "点击或按 F8 查看位置", hintStyle);
            float remaining = Mathf.Clamp01((toastUntil - Time.realtimeSinceStartup) / toastDuration);
            Fill(new Rect(rect.x + 5, rect.yMax - 3, (width - 5) * remaining, 3), accent);
            if (GUI.Button(new Rect(rect.xMax - 29 * s, rect.y + 5 * s, 24 * s, 24 * s), "×", hintStyle)) toast = null;
            else if (GUI.Button(rect, "", GUIStyle.none)) { SelectDetails(toast.Filter); toast = null; }
            GUI.color = old;
        }
        void SelectDetails(int targetFilter)
        {
            filter = targetFilter;
            if (OpenDetails != null) OpenDetails(targetFilter);
        }
        public void DrawPanel()
        {
            enabled.Value = GUILayout.Toggle(enabled.Value, "实时监控");
            hud.Value = GUILayout.Toggle(hud.Value, "显示悬浮待办");
            reminders.Value = GUILayout.Toggle(reminders.Value, "游戏内提醒");
            if (GUILayout.Button(Time.realtimeSinceStartup < mutedUntil ? "恢复提醒" : "静音提醒 10 分钟"))
            { toast = null; queue.Clear(); mutedUntil = Time.realtimeSinceStartup < mutedUntil ? 0 : Time.realtimeSinceStartup + 600; }
            float newScale = GUILayout.HorizontalSlider(noticeScale.Value, 0.8f, 1.6f);
            if (Math.Abs(newScale - noticeScale.Value) >= 0.01f) noticeScale.Value = (float)Math.Round(newScale, 2);
            GUILayout.Label("提示大小 " + Mathf.RoundToInt(noticeScale.Value * 100) + "% · 同类合并，间隔 " + Math.Max(12, noticeSpacing.Value).ToString("0") + " 秒");
            upcoming.Value = GUILayout.Toggle(upcoming.Value, "提前成熟弹窗（默认关闭）");
            feedForecast.Value = GUILayout.Toggle(feedForecast.Value, "饲料不足预警弹窗（默认关闭，断粮仍会提醒）");
            guides.DrawSettings();
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
                if ((selected.Ready > 0 || selected.LowWater > 0 || selected.FeedShort) && GUILayout.Button("将这个区域设为方向指引目标")) guides.Focus(selected.Key);
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
