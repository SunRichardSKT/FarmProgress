using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FarmProgress
{
    public sealed class TaskPlanOverlay
    {
        readonly ConfigEntry<bool> enabled, collapsed, includeCompleted, inventory;
        readonly ConfigEntry<int> rowCount;
        readonly ConfigEntry<float> size, positionX, positionY;
        Rect window;
        GUIStyle title, text, muted, header, number, button;
        TaskPlan plan, previousPlan;
        List<TaskMaterial> rows = new List<TaskMaterial>();
        List<TaskHolding> holdings = new List<TaskHolding>();
        bool previousInclude, showInventory;
        int RowTotal { get { return showInventory ? holdings.Count : rows.Count; } }
        float scale, age;
        int page, pages, count;
        string error;
        public Action OpenDetails;
        public TaskPlanOverlay(ConfigFile config)
        {
            enabled = config.Bind("TaskOverlay", "Enabled", true, "Show a persistent shared-quest material table in game");
            collapsed = config.Bind("TaskOverlay", "Collapsed", false, "Keep only the task overlay title visible");
            includeCompleted = config.Bind("TaskOverlay", "IncludeCompleted", false, "Include materials already satisfied by current quests");
            inventory = config.Bind("TaskOverlay", "InventoryView", false, "Show all planted varieties rather than quest requirements in the overlay");
            rowCount = config.Bind("TaskOverlay", "RowsPerPage", 5, "Task materials per page, 3 to 10");
            size = config.Bind("TaskOverlay", "Scale", 1f, "Task overlay size, 0.8 to 1.4");
            positionX = config.Bind("TaskOverlay", "X", 20f, "Task overlay position in screen pixels");
            positionY = config.Bind("TaskOverlay", "Y", 155f, "Task overlay position in screen pixels");
            window = new Rect(positionX.Value, positionY.Value, 400, 100);
        }
        public void Draw(TaskPlan current, float seconds, string failure)
        {
            if (!enabled.Value) return;
            plan = current; age = seconds; error = failure;
            showInventory = inventory.Value;
            if (!ReferenceEquals(plan, previousPlan) || previousInclude != includeCompleted.Value)
            { rows = TaskPlanDisplay.OverlayRows(plan, includeCompleted.Value); holdings = TaskPlanDisplay.InventoryRows(plan); previousPlan = plan; previousInclude = includeCompleted.Value; }
            count = Math.Max(3, Math.Min(10, rowCount.Value));
            scale = Mathf.Min(Mathf.Clamp(size.Value, 0.8f, 1.4f), (Screen.width - 20) / 400f);
            // Keep the title and at least one row available on small screens.
            count = Math.Max(1, Math.Min(count, (int)((Screen.height - 24) / Math.Max(0.1f, scale) - 104) / 43));
            pages = TaskPlanDisplay.PageCount(RowTotal, count); page = Math.Max(0, Math.Min(pages - 1, page));
            float height = collapsed.Value ? 35 : 104 + Math.Max(1, Math.Min(count, RowTotal)) * 43;
            window.width = 400 * scale; window.height = height * scale;
            window.x = Mathf.Clamp(window.x, 0, Mathf.Max(0, Screen.width - window.width));
            window.y = Mathf.Clamp(window.y, 0, Mathf.Max(0, Screen.height - window.height));
            EnsureStyles();
            window = GUI.Window(183750, window, DrawWindow, "", GUIStyle.none);
            if (GUIUtility.hotControl == 0 && Event.current.type == EventType.Repaint)
            {
                if (Math.Abs(positionX.Value - window.x) > 0.5f) positionX.Value = window.x;
                if (Math.Abs(positionY.Value - window.y) > 0.5f) positionY.Value = window.y;
            }
        }
        void EnsureStyles()
        {
            if (text == null)
            {
                text = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false, clipping = TextClipping.Clip };
                title = new GUIStyle(text) { fontStyle = FontStyle.Bold };
                muted = new GUIStyle(text); muted.normal.textColor = new Color(0.65f, 0.75f, 0.78f);
                header = new GUIStyle(muted) { alignment = TextAnchor.MiddleRight };
                text.normal.textColor = Color.white; title.normal.textColor = new Color(0.55f, 0.93f, 0.75f);
                number = new GUIStyle(text) { alignment = TextAnchor.MiddleRight };
                button = new GUIStyle(GUI.skin.button) { padding = new RectOffset(), richText = false };
            }
            title.fontSize = Mathf.RoundToInt(14 * scale); text.fontSize = number.fontSize = button.fontSize = Mathf.RoundToInt(12 * scale);
            header.fontSize = muted.fontSize = Mathf.RoundToInt(11 * scale);
        }
        Rect R(float x, float y, float width, float height) { return new Rect(x * scale, y * scale, width * scale, height * scale); }
        void Label(float x, float y, float width, string value, GUIStyle style) { GUI.Label(R(x, y, width, 21), value, style); }
        static void Fill(Rect rect, Color color)
        { var old = GUI.color; GUI.color = color; GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = old; }
        void DrawWindow(int id)
        {
            Fill(new Rect(0, 0, window.width, window.height), new Color(0.035f, 0.06f, 0.075f, 0.95f));
            Fill(new Rect(0, 0, 3 * scale, window.height), new Color(0.43f, 0.88f, 0.70f));
            Label(12, 7, 194, (showInventory ? "全农场种养 · " : "任务材料 · ") + (plan == null ? "扫描中" : showInventory ? holdings.Count + "种" : plan.QuestCount + "任务 / " + rows.Count + "种"), title);
            if (GUI.Button(R(211, 6, 54, 23), showInventory ? "看任务" : "看种养", button)) { inventory.Value = !showInventory; page = 0; }
            if (GUI.Button(R(271, 6, 62, 23), "完整统计", button) && OpenDetails != null) OpenDetails();
            if (GUI.Button(R(340, 6, 48, 23), collapsed.Value ? "展开" : "折叠", button)) collapsed.Value = !collapsed.Value;
            if (!collapsed.Value)
            {
                Label(12, 36, 99, showInventory ? "品种" : "材料", muted); Label(113, 36, 50, showInventory ? "已种/养" : "待收", header);
                Label(170, 36, 50, showInventory ? "成熟格" : "已种/养", header); Label(225, 36, 50, showInventory ? "未成熟" : "成熟格", header); Label(282, 36, 106, showInventory ? "未知" : "补种 / 扩种", header);
                if (RowTotal == 0)
                    Label(12, 66, 376, plan == null ? "正在扫描任务与种养数量…" : showInventory ? "当前没有已种养的品种" : plan.Materials.Count == 0 ? "当前没有农场任务材料" : "当前任务材料均已满足", text);
                for (int i = 0, start = page * count; i < count && start + i < RowTotal; i++)
                {
                    var material = showInventory ? null : rows[start + i]; var h = showInventory ? holdings[start + i] : material.Holding; float y = 59 + i * 43;
                    if (i % 2 == 0) Fill(R(8, y, 384, 43), new Color(0.10f, 0.17f, 0.19f, 0.8f));
                    Label(12, y, 99, TargetText.Compact(h.Name, 7), text);
                    Label(113, y, 50, (showInventory ? h.Planted : material.Remaining).ToString(), number);
                    var old = number.normal.textColor;
                    var readyColor = new Color(0.55f, 0.93f, 0.75f);
                    if (showInventory && h.Ready > 0) number.normal.textColor = readyColor;
                    Label(170, y, 50, (showInventory ? h.Ready : h.Planted).ToString(), number);
                    number.normal.textColor = !showInventory && h.Ready > 0 ? readyColor : old;
                    Label(225, y, 50, (showInventory ? h.Growing : h.Ready).ToString(), number); number.normal.textColor = old;
                    Label(282, y, 106, showInventory ? h.Unknown.ToString() : TaskPlanDisplay.Additional(material), number);
                    Label(12, y + 20, 376, showInventory ? TaskPlanDisplay.CategoryName(h.Category) + (h.Reusable ? " · 可重复收获" : " · 收获后需重新种植") : "进度 " + TargetText.Compact(TaskPlanDisplay.Progress(material), 48), muted);
                }
                float bottom = 62 + Math.Max(1, Math.Min(count, RowTotal)) * 43;
                Label(12, bottom, 264, error != null ? "刷新失败，显示上次统计" : "Alt 操作 / 拖动 · " + (int)age + "秒前更新", muted);
                bool oldEnabled = GUI.enabled;
                GUI.enabled = oldEnabled && page > 0; if (GUI.Button(R(284, bottom, 24, 21), "‹", button)) page--;
                GUI.enabled = oldEnabled; Label(311, bottom, 45, (page + 1) + "/" + pages, muted);
                GUI.enabled = oldEnabled && page < pages - 1; if (GUI.Button(R(362, bottom, 24, 21), "›", button)) page++;
                GUI.enabled = oldEnabled;
                Label(12, bottom + 20, 376, showInventory ? "包括非任务品种 · 已种/养包含成熟格 · 仅已解锁区域" : "可选 = 多轮可补齐，按需扩种 · 鱼的数量建议为估计", muted);
            }
            GUI.DragWindow(R(0, 0, 205, 34));
        }
        public void DrawSettings()
        {
            enabled.Value = GUILayout.Toggle(enabled.Value, "显示任务材料常驻浮窗");
            if (!enabled.Value) return;
            includeCompleted.Value = GUILayout.Toggle(includeCompleted.Value, "任务浮窗也显示已满足的材料");
            GUILayout.Label("浮窗每页 " + rowCount.Value + " 种材料");
            rowCount.Value = Mathf.RoundToInt(GUILayout.HorizontalSlider(rowCount.Value, 3, 10));
            GUILayout.Label("任务浮窗大小 " + size.Value.ToString("0.00") + " 倍");
            float next = (float)Math.Round(GUILayout.HorizontalSlider(size.Value, 0.8f, 1.4f), 2);
            if (Math.Abs(next - size.Value) > 0.005f) size.Value = next;
            if (GUILayout.Button("重置任务浮窗位置并展开"))
            { window.x = positionX.Value = 20; window.y = positionY.Value = 155; collapsed.Value = false; }
        }
        public void ResetDefaults()
        { enabled.Value = true; collapsed.Value = includeCompleted.Value = inventory.Value = false; rowCount.Value = 5; size.Value = 1; window.x = positionX.Value = 20; window.y = positionY.Value = 155; }
    }
}
