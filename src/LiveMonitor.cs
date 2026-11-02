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
        readonly ConfigEntry<bool> includeGrowing;
        readonly ConfigEntry<float> noticeScale, noticeSpacing;
        FarmData farm;
        IEnumerator<FarmTile> scan;
        GrowthSnapshot growth;
        LiveRules building;
        TaskPlan buildingPlan;
        GuideIndex guidanceIndex, buildingGuidance;
        readonly DirectionGuide guides;
        readonly TaskPlanOverlay taskOverlay;
        ReminderRules rules;
        float nextScan, capturedAt, toastUntil;
        ReminderCard toast;
        ReminderQueue queue = new ReminderQueue();
        MonitorCounts currentCounts;
        float toastDuration;
        GUIStyle titleStyle, bodyStyle, targetStyle, hintStyle, hudNumberStyle, hudLabelStyle;
        Vector2 scroll;
        int filter;
        int settingsTab;
        LiveGroup selected;
        Rect hudRect = new Rect(20, 55, 350, 85);
        public Action<int> OpenDetails;
        public Action OpenTaskStatistics;
        public Action ReturnToGame;
        string search = "";
        public LiveRules Current { get; private set; }
        public TaskPlan Planning { get; private set; }
        public float PlanningAge { get { return Math.Max(0f, Time.realtimeSinceStartup - capturedAt); } }
        public void RefreshPlanning() { nextScan = 0; }
        public string Error { get; private set; }
        public LiveMonitor(ConfigFile config)
        {
            guides = new DirectionGuide(config);
            taskOverlay = new TaskPlanOverlay(config);
            taskOverlay.OpenDetails = () => { if (OpenTaskStatistics != null) OpenTaskStatistics(); };
            guides.OpenDetails = SelectDetails;
            enabled = config.Bind("Monitor", "Enabled", true, "Monitor loaded farm in batches");
            reminders = config.Bind("Monitor", "Reminders", true, "Show grouped in-game reminders");
            hud = config.Bind("Monitor", "Hud", true, "Show movable live summary");
            threshold = config.Bind("Monitor", "LowWaterFraction", 0.1f, "Water fraction threshold, 0 to 1");
            interval = config.Bind("Monitor", "RefreshSeconds", 2f, "Delay between completed scans");
            advance = config.Bind("Monitor", "AdvanceMinutes", 5f, "Upcoming harvest warning in minutes");
            cooldown = config.Bind("Monitor", "ReminderCooldownSeconds", 300f, "Reminder cooldown per group and state");
            feedForecast = config.Bind("Monitor", "FeedForecastNotifications", false, "Also notify before starvation, only when native feeding is currently available");
            includeGrowing = config.Bind("Monitor", "IncludeGrowingInTasks", false, "Include groups with no current work in the all-tasks list");
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
                Current = null; Planning = null; buildingPlan = null; currentCounts = null; guidanceIndex = null; buildingGuidance = null;
                rules = new ReminderRules(); queue = new ReminderQueue(); nextScan = 0; selected = null; toast = null; Error = null;
            }
            guides.Update(enabled.Value ? data : null, guidanceIndex, Mathf.Clamp01(threshold.Value));
            if (farm == null || !enabled.Value) return;
            if (scan == null)
            {
                if (Time.realtimeSinceStartup < nextScan) return;
                scan = Tiles(farm).GetEnumerator(); growth = new GrowthSnapshot(); building = new LiveRules();
                buildingGuidance = new GuideIndex(); buildingPlan = new TaskPlan();
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
                        buildingPlan.AddTile(row, name);
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
            TaskPlanAdapter.AddQuests(buildingPlan, farm);
            foreach (var group in building.Groups.Values)
            {
                TaskMaterial material;
                if (buildingPlan.Materials.TryGetValue(TaskPlan.Key(group.Category, group.ItemId), out material))
                    group.TaskRemaining = (int)Math.Min(int.MaxValue, material.Remaining);
            }
            Planning = buildingPlan;
            var notes = rules.Evaluate(building, Time.realtimeSinceStartup, Math.Max(0, cooldown.Value));
            bool first = Current == null; Current = building; Error = null; capturedAt = Time.realtimeSinceStartup;
            if (selected != null) { LiveGroup refreshed; selected = Current.Groups.TryGetValue(selected.Key, out refreshed) ? refreshed : null; }
            currentCounts = MonitorCounts.From(Current);
            guidanceIndex = buildingGuidance;
            guidanceIndex.CompleteNames(Current);
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
            taskOverlay.Draw(Planning, PlanningAge, Error);
            if (reminders.Value && Time.realtimeSinceStartup >= mutedUntil && Time.realtimeSinceStartup < toastUntil && toast != null) DrawCard(scale);
        }
        static readonly Color Green = new Color(0.42f, 0.91f, 0.57f), Blue = new Color(0.38f, 0.77f, 1f), Red = new Color(1f, 0.40f, 0.36f), Amber = new Color(1f, 0.78f, 0.30f);
        void EnsureStyles()
        {
            if (titleStyle != null) return;
            titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip };
            bodyStyle = new GUIStyle(titleStyle) { fontStyle = FontStyle.Normal };
            targetStyle = new GUIStyle(bodyStyle) { richText = false };
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
                    "当前可喂食：断粮 " + counts.Hungry + " 组；可补料 " + counts.ShortFeed + " 组", s);
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
            float width = 350 * s, height = 156 * s;
            var rect = new Rect((Screen.width - width) / 2, Mathf.Min(95 * s, Screen.height - height - 10), width, height);
            Color accent = toast.Kind == 0 ? Red : toast.Kind == 3 ? Blue : toast.Kind >= 4 ? Amber : Green;
            var old = GUI.color; GUI.color = new Color(old.r, old.g, old.b, old.a * Mathf.Clamp01((toastUntil - Time.realtimeSinceStartup) / 0.4f));
            Surface(rect, accent);
            titleStyle.fontSize = Mathf.RoundToInt(23 * s); titleStyle.normal.textColor = accent;
            bodyStyle.fontSize = Mathf.RoundToInt(19 * s); targetStyle.fontSize = Mathf.RoundToInt(16 * s); hintStyle.fontSize = Mathf.RoundToInt(12 * s);
            GUI.Label(new Rect(rect.x + 20 * s, rect.y + 12 * s, width - 50 * s, 34 * s), toast.Title, titleStyle);
            GUI.Label(new Rect(rect.x + 20 * s, rect.y + 48 * s, width - 35 * s, 25 * s), toast.Targets, targetStyle);
            GUI.Label(new Rect(rect.x + 20 * s, rect.y + 79 * s, width - 35 * s, 30 * s), toast.Body, bodyStyle);
            GUI.Label(new Rect(rect.x + 20 * s, rect.y + 121 * s, width - 35 * s, 24 * s), "F8 查看位置 · 按住 Alt 可点击", hintStyle);
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
        public void DrawOverview()
        {
            var counts = currentCounts;
            GUILayout.BeginHorizontal();
            DrawStat(counts == null ? "—" : counts.Harvest.ToString(), "可收获", Green, 1);
            DrawStat(counts == null ? "—" : counts.Water.ToString(), "需要浇水", Blue, 2);
            DrawStat(counts == null ? "—" : (counts.Hungry + counts.ShortFeed).ToString(), "需要喂食", Red, 3);
            GUILayout.EndHorizontal(); GUILayout.Space(10);
            PanelTheme.BeginSection("方向指引", guides.RangeDescription);
            GUILayout.Label("绿色收获  ·  蓝色浇水  ·  红色断粮 / 黄色补料");
            GUILayout.Label("F8 自动显示鼠标，关闭后恢复操作；按住 Alt 可临时点击浮动提示。", PanelTheme.Muted);
            PanelTheme.EndSection();
            if (Current != null) GUILayout.Label("更新于 " + (int)(Time.realtimeSinceStartup - capturedAt) + " 秒前 · 植物和动物按格、鱼按塘、喂食按圈内品种统计", PanelTheme.Muted);
            else GUILayout.Label("正在扫描农场，首次加载稍候即可。", PanelTheme.Muted);
        }
        void DrawStat(string value, string title, Color color, int targetFilter)
        {
            PanelTheme.Stat.normal.textColor = color;
            if (GUILayout.Button(value + "\n" + title, PanelTheme.Stat, GUILayout.ExpandWidth(true))) SelectDetails(targetFilter);
        }
        public void DrawSettings()
        {
            settingsTab = GUILayout.SelectionGrid(settingsTab, new[] { "方向指引", "弹窗提醒", "显示与监控" }, 3);
            GUILayout.Space(10);
            if (settingsTab == 0)
            {
            PanelTheme.BeginSection("方向指引", "按格设置搜索范围；全农场覆盖当前已加载、已解锁的地格。");
            guides.DrawSettings(); PanelTheme.EndSection();
                return;
            }
            if (settingsTab == 1)
            {
            PanelTheme.BeginSection("弹窗提醒", "同类合并，详细品种和坐标在待办页查看。");
            reminders.Value = GUILayout.Toggle(reminders.Value, "开启游戏内弹窗");
            if (GUILayout.Button(Time.realtimeSinceStartup < mutedUntil ? "恢复提醒" : "静音提醒 10 分钟"))
            { toast = null; queue.Clear(); mutedUntil = Time.realtimeSinceStartup < mutedUntil ? 0 : Time.realtimeSinceStartup + 600; }
            noticeSpacing.Value = Slider("两次弹窗间隔", noticeSpacing.Value, 12, 180, "秒", 0);
            upcoming.Value = GUILayout.Toggle(upcoming.Value, "提前成熟预告");
            if (upcoming.Value) advance.Value = Slider("提前提醒", advance.Value, 1, 30, "分钟", 0);
            feedForecast.Value = GUILayout.Toggle(feedForecast.Value, "可补料提醒（断粮且可喂食时始终提醒）");
            PanelTheme.EndSection();
                return;
            }
            PanelTheme.BeginSection("显示与监控", "修改后自动保存，下次进入游戏继续使用。");
            enabled.Value = GUILayout.Toggle(enabled.Value, "开启实时监控");
            hud.Value = GUILayout.Toggle(hud.Value, "显示常驻待办数字");
            taskOverlay.DrawSettings();
            noticeScale.Value = Slider("浮动提示大小", noticeScale.Value, 0.8f, 1.6f, "倍", 2);
            threshold.Value = Slider("低水分阈值", threshold.Value * 100, 1, 100, "%", 0) / 100f;
            interval.Value = Slider("扫描间隔", interval.Value, 0.5f, 15, "秒", 1);
            includeGrowing.Value = GUILayout.Toggle(includeGrowing.Value, "待办页也显示没有待办的成长中区域");
            if (GUILayout.Button("恢复推荐设置"))
            {
                enabled.Value = reminders.Value = hud.Value = true; upcoming.Value = feedForecast.Value = includeGrowing.Value = false;
                noticeScale.Value = 1.1f; noticeSpacing.Value = 30; threshold.Value = 0.1f; interval.Value = 2; advance.Value = 5;
                cooldown.Value = 300;
                guides.ResetDefaults();
                taskOverlay.ResetDefaults();
            }
            PanelTheme.EndSection();
        }
        static float Slider(string name, float value, float min, float max, string unit, int decimals)
        {
            GUILayout.Label(name + "    " + value.ToString("F" + decimals) + " " + unit);
            float next = (float)Math.Round(GUILayout.HorizontalSlider(value, min, max), decimals);
            return Math.Abs(next - value) > Math.Pow(10, -decimals) / 2 ? next : value;
        }
        public void DrawTasks()
        {
            if (Current == null) { GUILayout.Label(Error ?? "正在分批扫描农场…"); return; }
            GUILayout.Label("待办与地图", PanelTheme.Heading);
            GUILayout.Label(guides.RangeDescription + " · 列表统计覆盖全农场", PanelTheme.Muted);
            GUILayout.BeginHorizontal(); GUILayout.Label("搜索", GUILayout.Width(42)); search = GUILayout.TextField(search, 80); GUILayout.EndHorizontal();
            filter = GUILayout.SelectionGrid(filter, new[] { "全部", "收获", "浇水", "喂食", "即将成熟", "任务" }, 3);
            GUILayout.Space(8);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(230));
            var groups = new List<LiveGroup>(Current.Groups.Values);
            groups.Sort((a, b) => Priority(b).CompareTo(Priority(a)));
            int shown = 0;
            foreach (var group in groups)
            {
                if (filter == 1 && group.Ready == 0 || filter == 2 && group.LowWater == 0 ||
                    filter == 3 && !group.CanFeed || filter == 4 && group.Soon == 0 || filter == 5 && group.TaskRemaining == 0) continue;
                if (filter == 0 && !includeGrowing.Value && group.Ready == 0 && group.LowWater == 0 && !group.CanFeed && !group.Hungry) continue;
                if (search.Length > 0 && (group.Name ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 && (group.ItemId ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string state = group.CanFeed ? group.Hungry ? "已断粮，可喂食" : "可补料" : group.Hungry ? "已断粮，暂不可喂食" : group.Ready > 0 ? "可收获 " + group.Ready : group.LowWater > 0 ? "缺水 " + group.LowWater : "成长中";
                if (GUILayout.Button(group.Name + "    " + state + "\n坐标 " + group.X + ", " + group.Y + "    共 " + group.Count + " 格")) selected = group;
                shown++;
            }
            if (shown == 0) GUILayout.Label("当前筛选下暂无待办。", PanelTheme.Muted);
            GUILayout.EndScrollView();
            if (selected != null)
            {
                PanelTheme.BeginSection(selected.Name);
                GUILayout.Label("定位：" + selected.Name + "，坐标 (" + selected.X + "," + selected.Y + ")，区域 (" + selected.RegionX + "," + selected.RegionY + ")");
                GUILayout.Label(selected.Eta.HasValue ? "下一批预计 " + TimeSpan.FromSeconds(selected.Eta.Value).ToString() : "成熟时间需继续观察（动物供料不足时未知）");
                if (selected.Category == "Animal" && selected.FeedShort)
                    GUILayout.Label(selected.CanFeed ? "现有饲料不够本轮成熟，当前可补料。" : "本轮还需饲料（预测）；" + (selected.FeedBlockedReason ?? "当前无需补料") + "。");
                if ((selected.Ready > 0 || selected.LowWater > 0 || selected.CanFeed) && GUILayout.Button("指引这里并返回游戏", PanelTheme.Primary))
                { guides.Focus(selected.Key); if (ReturnToGame != null) ReturnToGame(); }
                PanelTheme.EndSection();
            }
            GUILayout.Space(8);
            DrawMap(groups);
            if (Error != null) GUILayout.Label("刷新错误：" + Error, PanelTheme.Muted);
        }
        float mutedUntil;
        static int Priority(LiveGroup g) { return (g.Hungry && g.CanFeed ? 100 : 0) + (g.TaskRemaining > 0 && g.Ready > 0 ? 80 : 0) + (g.Ready > 0 ? 40 : 0) + (g.LowWater > 0 ? 20 : 0) + (g.CanFeed ? 10 : 0); }
        void DrawMap(List<LiveGroup> groups)
        {
            var rect = GUILayoutUtility.GetRect(0, 200, GUILayout.ExpandWidth(true)); GUI.Box(rect, "待办示意图（点击定位）");
            int maxX = 1, maxY = 1;
            foreach (var g in groups) { maxX = Math.Max(maxX, g.X + 1); maxY = Math.Max(maxY, g.Y + 1); }
            foreach (var g in groups)
            {
                if (g.Ready == 0 && g.LowWater == 0 && !g.CanFeed) continue;
                var old = GUI.color; GUI.color = ReferenceEquals(selected, g) ? Color.white : g.Hungry ? Color.red : g.LowWater > 0 ? Color.cyan : Color.green;
                if (GUI.Button(new Rect(rect.x + 8 + g.X * (rect.width - 25) / maxX, rect.y + 25 + g.Y * (rect.height - 42) / maxY, 10, 10), "")) selected = g;
                GUI.color = old;
            }
        }
    }
}
