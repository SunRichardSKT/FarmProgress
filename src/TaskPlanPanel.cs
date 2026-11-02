using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FarmProgress
{
    public sealed class TaskPlanPanel
    {
        int tab, filter, category;
        bool showRules;
        string search = "", expanded;
        GUIStyle numeric, itemStyle;
        static readonly string[] Categories = { "全部", "作物", "花卉", "树木", "动物", "鱼" };
        static readonly string[] CategoryIds = { "", "Crop", "Flower", "Tree", "Animal", "Pond" };
        public Action Refresh;
        public void Draw(TaskPlan plan, bool narrow, float age, string error)
        {
            GUILayout.Label("任务统计", PanelTheme.Heading);
            GUILayout.Label("同品种合并待收 = 各任务剩余数量的最大值；库存不能抵交。", PanelTheme.Muted);
            GUILayout.BeginHorizontal();
            tab = GUILayout.SelectionGrid(tab, new[] { "任务材料合并", "全农场种养数量" }, 2);
            if (GUILayout.Button("刷新", GUILayout.Width(56)) && Refresh != null) Refresh();
            GUILayout.EndHorizontal();
            if (plan == null) { GUILayout.Label("正在扫描农场；请先进入农场并开启实时监控。", PanelTheme.Muted); return; }
            GUILayout.Label("完整扫描更新于 " + (int)age + " 秒前" + (error == null ? "" : " · 刷新失败，当前显示上次统计，可点击刷新重试。"), PanelTheme.Muted);
            if (numeric == null)
            {
                numeric = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, richText = false, fontSize = 14 };
                itemStyle = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, richText = false };
            }
            GUILayout.Space(8);
            if (tab == 0) DrawTotals(plan);
            GUILayout.BeginHorizontal();
            GUILayout.Label("搜索", GUILayout.Width(36)); search = GUILayout.TextField(search, 80);
            GUILayout.EndHorizontal();
            category = GUILayout.SelectionGrid(category, Categories, narrow ? 3 : 6);
            if (tab == 0)
            {
                filter = GUILayout.SelectionGrid(filter, new[] { "全部待办", "现在可收", "一轮缺口", "循环可补齐", "包含已完成" }, narrow ? 3 : 5);
                GUILayout.Space(7); DrawMaterials(plan, narrow);
                GUILayout.Space(8);
                if (GUILayout.Button("复制任务统计")) GUIUtility.systemCopyBuffer = plan.ToText();
            }
            else { GUILayout.Space(7); DrawHoldings(plan, narrow); }
            GUILayout.Space(8);
            showRules = GUILayout.Toggle(showRules, "计算规则与限制");
            if (showRules)
            {
                GUILayout.Label("统计当前农场共享任务，不含个人活动。成熟格包含在已种/养中；同品种各任务进度分别保留。", PanelTheme.Muted);
                GUILayout.Label("花、树、鱼、动物可以多轮收获，一轮缺口是可选扩种。鱼的成熟格是捕获尝试，进度与扩养按捕获率估算；实际结果会波动，轮数不代表时间。", PanelTheme.Muted);
            }
            GUILayout.Label("统计覆盖已加载、已解锁区域，沿用实时扫描间隔；分批扫描期间数量可能短暂变化。", PanelTheme.Muted);
            if (plan.ReadErrors > 0) GUILayout.Label(plan.ReadErrors + " 格状态未读到，计入已种/养，成熟状态标为未知。", PanelTheme.Muted);
        }
        static string TypeName(string value)
        { for (int i = 1; i < CategoryIds.Length; i++) if (CategoryIds[i] == value) return Categories[i]; return value; }
        bool Matches(TaskHolding holding)
        {
            return (category == 0 || holding.Category == CategoryIds[category]) && (search.Length == 0 ||
                holding.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || holding.Id.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        void DrawTotals(TaskPlan plan)
        {
            int remainingKinds = 0; long cropPlant = 0, reusableExpand = 0, ready = 0;
            foreach (var material in plan.Materials.Values)
            {
                if (material.Remaining == 0) continue;
                remainingKinds++; ready += material.Holding.Ready;
                if (material.Holding.Reusable) reusableExpand += material.OneRoundAdditional ?? 0;
                else cropPlant += material.OneRoundAdditional ?? 0;
            }
            PanelTheme.BeginSection(plan.QuestCount + " 个任务 · " + remainingKinds + " 种材料未完成");
            GUILayout.Label("目标已成熟 " + ready + " 格   ·   作物需补种 " + cropPlant + " 格");
            GUILayout.Label("可持续品种一轮完成：预计可选加种/养 " + reusableExpand + " 格（也可循环收获；鱼为概率估计）。", PanelTheme.Muted);
            PanelTheme.EndSection();
        }
        void DrawMaterials(TaskPlan plan, bool narrow)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(14);
            GUILayout.Label("材料 / 展开任务", PanelTheme.Muted, GUILayout.MinWidth(105), GUILayout.ExpandWidth(true));
            Cell("合并待收", 64); Cell("已种/养", 64); Cell("成熟格", 64); Cell("一轮缺口", 78);
            GUILayout.Space(14);
            GUILayout.EndHorizontal();
            var ordered = new List<TaskMaterial>(plan.Materials.Values);
            ordered.Sort((a, b) =>
            {
                int result = b.Holding.Ready.CompareTo(a.Holding.Ready);
                return result != 0 ? result : StringComparer.Ordinal.Compare(a.Holding.Name, b.Holding.Name);
            });
            int shown = 0;
            foreach (var material in ordered)
            {
                var h = material.Holding;
                if (!Matches(h) || filter != 4 && material.Remaining == 0 || filter == 1 && h.Ready == 0 ||
                    filter == 2 && !(material.OneRoundAdditional > 0) || filter == 3 && (!h.Reusable || !(material.RoundsAtCurrentSize > 0))) continue;
                GUILayout.BeginVertical(PanelTheme.Card);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button((expanded == h.Key ? "▾ " : "▸ ") + h.Name + " · " + TypeName(h.Category), itemStyle, GUILayout.MinWidth(105), GUILayout.ExpandWidth(true)))
                    expanded = expanded == h.Key ? null : h.Key;
                Cell(material.Remaining.ToString(), 64); Cell(h.Planted.ToString(), 64); Cell(h.Ready.ToString(), 64);
                Cell((h.Category == "Pond" ? "≈" : "") + (material.OneRoundAdditional.HasValue ? material.OneRoundAdditional.ToString() : "未知"), 78);
                GUILayout.EndHorizontal();
                var progress = new StringBuilder("各任务进度：");
                for (int i = 0; i < material.Demands.Count; i++)
                { if (i > 0) progress.Append("  ·  "); var d = material.Demands[i]; progress.Append('#').Append(d.QuestId).Append(' ').Append(d.Progress).Append('/').Append(d.Required); }
                GUILayout.Label(progress.ToString(), PanelTheme.Muted);
                GUILayout.Label(material.ActionText, PanelTheme.Muted);
                if (expanded == h.Key)
                {
                    foreach (var d in material.Demands)
                        GUILayout.Label("#" + d.QuestId + " " + d.Title + "：" + d.Progress + "/" + d.Required + "，还需 " + d.Remaining);
                    GUILayout.Label("未成熟 " + h.Growing + " 格；状态未知 " + h.Unknown + " 格。", PanelTheme.Muted);
                    if (h.Category == "Pond") GUILayout.Label("捕获率 " + (h.SuccessChance.HasValue ? (h.SuccessChance.Value * 100).ToString("0.##") + "%" : "未知") +
                        "；成熟鱼格预计可补进度 " + (material.ExpectedReady.HasValue ? "≈" + material.ExpectedReady.Value.ToString("0.##") : "未知") + "。混养池按鱼种逐格统计。", PanelTheme.Muted);
                    else GUILayout.Label("现在收获可补进度 " + Math.Min(material.Remaining, h.Ready) + "；成熟格已包含在已种/养数量中。", PanelTheme.Muted);
                }
                GUILayout.EndVertical(); shown++;
            }
            if (shown == 0) GUILayout.Label(plan.Materials.Count == 0 ? "当前没有可统计的任务材料。" : "当前筛选下没有材料。", PanelTheme.Muted);
        }
        void DrawHoldings(TaskPlan plan, bool narrow)
        {
            GUILayout.Label("逐格统计实际品种数量，包含没有被当前任务需要的花、树、鱼和动物。", PanelTheme.Muted);
            GUILayout.BeginHorizontal();
            GUILayout.Space(14);
            GUILayout.Label("品种", PanelTheme.Muted, GUILayout.MinWidth(105), GUILayout.ExpandWidth(true));
            Cell("已种/养", 64); Cell("成熟格", 64); Cell("未成熟", 64); Cell("未知", 64); GUILayout.Space(14); GUILayout.EndHorizontal();
            var ordered = new List<TaskHolding>(plan.Holdings.Values);
            ordered.Sort((a, b) => StringComparer.Ordinal.Compare(a.Category + a.Name, b.Category + b.Name));
            int shown = 0;
            foreach (var h in ordered)
            {
                if (h.Planted == 0 || !Matches(h)) continue;
                GUILayout.BeginHorizontal(PanelTheme.Card);
                GUILayout.Label(h.Name + "\n" + TypeName(h.Category), GUILayout.MinWidth(105), GUILayout.ExpandWidth(true));
                Cell(h.Planted.ToString(), 64); Cell(h.Ready.ToString(), 64); Cell(h.Growing.ToString(), 64); Cell(h.Unknown.ToString(), 64);
                GUILayout.EndHorizontal(); shown++;
            }
            if (shown == 0) GUILayout.Label("当前筛选下没有已种养的品种。", PanelTheme.Muted);
        }
        void Cell(string text, float width) { GUILayout.Label(text, numeric, GUILayout.Width(width)); }
    }
}
