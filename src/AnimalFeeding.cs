using Logic.Farm;
using Logic.Farm.Contents;

namespace FarmProgress
{
    public sealed class AnimalFeedStatus
    {
        public bool CanFeed;
        public string BlockedReason;
    }

    public static class AnimalFeeding
    {
        public static AnimalFeedStatus Read(AnimalFieldContents animal, AnimalField.FieldRaceData race, PlayerScript player)
        {
            if (player == null) return Blocked("等待本地角色");
            // Native Refill buys whole FoodCapacity loads, using Food rather than FoodAvailable (which includes feeder reserves).
            if (race.Definition.FoodCapacity == 0 || race.RemainingFoodCapacity < race.Definition.FoodCapacity)
                return Blocked("饲料槽未空出一份");
            FailedActionReason failure;
            // performWork=false checks native permissions/availability without buying feed or changing the farm.
            if (!animal.Work(player, false, WorkType.Refill, out failure))
                return Blocked(failure == FailedActionReason.NotPermitted ? "当前角色没有喂食权限" :
                    failure == FailedActionReason.NotEnoughMoney ? "当前资产不足以喂食" : "游戏暂不允许喂食");
            // Native Work considers every race in a mixed pen. Check this race too, so another cheap race cannot validate this target.
            if (!animal.Tile.FarmData.CanPay(race.Definition.FeedMoney, out failure))
                return Blocked("当前资产不足以喂食");
            return new AnimalFeedStatus { CanFeed = true };
        }

        static AnimalFeedStatus Blocked(string reason) { return new AnimalFeedStatus { BlockedReason = reason }; }
    }
}
