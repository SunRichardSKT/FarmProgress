using Logic.Farm;
using Milkstone.Utils;

namespace FarmProgress
{
    public static class TaskPlanAdapter
    {
        public static void AddQuests(TaskPlan plan, FarmData farm)
        {
            foreach (var quest in farm.ActiveQuests)
                foreach (var target in quest.QuestItems)
                {
                    var def = target.ShopItem; var pond = def as PondDefinition;
                    plan.AddRequirement(quest.QuestId.ToString(), quest.Lore == null ? "任务 " + quest.QuestId : quest.Lore.Title,
                        def.Category.ToString(), def.FullId, Localization.Get(def.FullId), target.RequiredAmount, target.Progress,
                        pond == null ? (decimal?)null : (decimal)pond.SuccessChance / 100m);
                }
        }
    }
}
