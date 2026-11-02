using System;
using System.Collections.Generic;
using System.Text;

namespace FarmProgress
{
    public static class TaskPlanDisplay
    {
        public static List<TaskHolding> InventoryRows(TaskPlan plan)
        {
            var rows = new List<TaskHolding>();
            if (plan != null) foreach (var holding in plan.Holdings.Values) if (holding.Planted > 0) rows.Add(holding);
            rows.Sort((a, b) => StringComparer.Ordinal.Compare(a.Category + a.Name, b.Category + b.Name));
            return rows;
        }
        public static string CategoryName(string category)
        { return category == "Crop" ? "作物" : category == "Flower" ? "花卉" : category == "Tree" ? "树木" : category == "Animal" ? "动物" : category == "Pond" ? "鱼" : category; }
        public static List<TaskMaterial> OverlayRows(TaskPlan plan, bool includeCompleted)
        {
            var rows = new List<TaskMaterial>();
            if (plan != null) foreach (var material in plan.Materials.Values)
                if (includeCompleted || material.Remaining > 0) rows.Add(material);
            rows.Sort((a, b) =>
            {
                int result = (b.Remaining > 0).CompareTo(a.Remaining > 0);
                if (result == 0) result = (b.Holding.Ready > 0).CompareTo(a.Holding.Ready > 0);
                return result != 0 ? result : StringComparer.Ordinal.Compare(a.Holding.Name, b.Holding.Name);
            });
            return rows;
        }
        public static string Progress(TaskMaterial material)
        {
            var text = new StringBuilder();
            foreach (var demand in material.Demands)
            {
                if (text.Length > 0) text.Append(" · ");
                text.Append('#').Append(demand.QuestId).Append(' ').Append(demand.Progress).Append('/').Append(demand.Required);
            }
            return text.ToString();
        }
        public static string Additional(TaskMaterial material)
        {
            if (material.Remaining == 0) return "已满足";
            if (!material.OneRoundAdditional.HasValue) return "未知";
            if (material.OneRoundAdditional == 0) return "无需";
            return (material.Holding.Reusable ? "可选" : "补种") + (material.Holding.Category == "Pond" ? "≈" : "") + material.OneRoundAdditional;
        }
        public static int PageCount(int rows, int pageSize) { return Math.Max(1, (rows + Math.Max(1, pageSize) - 1) / Math.Max(1, pageSize)); }
    }
}
