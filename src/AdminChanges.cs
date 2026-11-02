using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Logic.Farm;
using Logic.Farm.Contents;
using Newtonsoft.Json.Linq;

namespace FarmProgress
{
    public static class AdminNumbers
    {
        public static long Target(long current, long input, bool add, long capacity)
        {
            long target = add ? checked(current + input) : input;
            if (target < 0 || target > capacity) throw new ArgumentOutOfRangeException("value", "数值超出允许范围：0 至 " + capacity);
            return target;
        }
    }

    public static class AdminChanges
    {
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static FieldInfo Field(object o, string name)
        {
            var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) throw new MissingFieldException(o.GetType().Name, name); return f;
        }
        static uint UInt(object o, string name) { return Convert.ToUInt32(Field(o, name).GetValue(o)); }
        static FarmTile Tile(FarmData farm, string[] parts)
        {
            int x = int.Parse(parts[1], Invariant), y = int.Parse(parts[2], Invariant);
            if (x < 0 || y < 0 || x > short.MaxValue || y > short.MaxValue) throw new ArgumentException("地格坐标无效");
            var t = farm.GetTile(new FarmTileId { _TileX = (short)x, _TileY = (short)y });
            if (t == null || !t.Chunk.IsUnlocked || t.Contents == null) throw new ArgumentException("目标地格不存在或未解锁"); return t;
        }
        static MoneyTypes MoneyType(string value)
        {
            if (value != "Coins" && value != "Bills" && value != "Medals" && value != "Tickets") throw new ArgumentException("货币类型无效");
            return (MoneyTypes)Enum.Parse(typeof(MoneyTypes), value);
        }
        static FarmResourceStorage Resource(FarmData farm, string value)
        {
            FarmResourceType type = (FarmResourceType)Enum.Parse(typeof(FarmResourceType), value);
            if (type <= FarmResourceType.Unused || type >= FarmResourceType.Count) throw new ArgumentException("库存类型无效");
            return farm.GetResource(type);
        }
        static AnimalField.FieldRaceData Race(FarmData farm, string[] parts)
        {
            uint id = uint.Parse(parts[1], Invariant);
            foreach (var field in farm.AnimalFields) if (field.Id == id)
                foreach (var race in field.Races) if (race.Definition.FullId == parts[2]) return race;
            throw new ArgumentException("动物圈或品种不存在");
        }
        public static string Read(FarmData farm, string key)
        {
            string[] p = key.Split('|');
            if (p.Length == 2 && p[0] == "money") return farm.Money.Get(MoneyType(p[1])).ToString(Invariant);
            if (p.Length == 2 && p[0] == "resource") return Resource(farm, p[1]).Amount.ToString(Invariant);
            if (p.Length == 3 && p[0] == "feed")
            { var r = Race(farm, p); return r.Food + "," + r.ExtraFood; }
            if (p.Length == 3 && (p[0] == "water" || p[0] == "mature"))
            {
                var tile = Tile(farm, p); var c = tile.Contents;
                string identity = c.Category + ";" + c.ShopItemDefinition.FullId + ";";
                if (p[0] == "water")
                {
                    if (c.Category != FarmTileContentsType.Crop && c.Category != FarmTileContentsType.Flower) throw new ArgumentException("该地格不能补水");
                    return identity + UInt(c, "waterLevel");
                }
                if (c.Category == FarmTileContentsType.Crop || c.Category == FarmTileContentsType.Flower)
                    return identity + UInt(c, "growthPoints") + "," + Convert.ToInt32(Field(c, "state").GetValue(c));
                if (c.Category == FarmTileContentsType.Animal)
                    return identity + UInt(c, "foodConsumed") + "," + Convert.ToInt32(Field(c, "state").GetValue(c));
                if (c.Category == FarmTileContentsType.Pond)
                    return identity + UInt(c, "waitedTime") + "," + Convert.ToInt32(Field(c, "state").GetValue(c));
                throw new ArgumentException("首版成熟调整支持作物、花、动物和鱼");
            }
            throw new ArgumentException("不支持的修改目标");
        }
        public static JObject Plan(FarmData farm, string key, long value, bool add)
        {
            string before = Read(farm, key); string[] p = key.Split('|'); string after;
            if (p[0] == "money" || p[0] == "resource")
            {
                long capacity = p[0] == "resource" ? Resource(farm, p[1]).MaxValue : long.MaxValue;
                after = AdminNumbers.Target(long.Parse(before, Invariant), value, add, capacity).ToString(Invariant);
            }
            else if (p[0] == "feed")
            { var race = Race(farm, p); after = race.MaxFood + "," + race.ExtraFood; }
            else
            {
                var c = Tile(farm, p).Contents; uint amount;
                if (p[0] == "water") amount = c.Category == FarmTileContentsType.Crop ? ((CropDefinition)c.ShopItemDefinition).MaxWaterLevel : ((FlowerDefinition)c.ShopItemDefinition).MaxWaterLevel;
                else if (c.Category == FarmTileContentsType.Crop) amount = ((CropDefinition)c.ShopItemDefinition).GrowTime;
                else if (c.Category == FarmTileContentsType.Flower) amount = ((FlowerDefinition)c.ShopItemDefinition).MaxGrowth;
                else if (c is AnimalFieldContents) amount = ((AnimalFieldContents)c).HarvestInterval;
                else amount = ((PondContents)c).Definition.FishInterval;
                after = before.Substring(0, before.LastIndexOf(';') + 1) + amount + (p[0] == "mature" ? ",1" : "");
            }
            return new JObject { { "key", key }, { "before", before }, { "after", after } };
        }
        public static void Apply(FarmData farm, JObject change)
        {
            string key = (string)change["key"], before = (string)change["before"], after = (string)change["after"];
            if (Read(farm, key) != before) throw new InvalidOperationException("目标已变化或客户端存在差异，请刷新后重试");
            string[] p = key.Split('|');
            if (p[0] == "money")
            {
                long old = long.Parse(before, Invariant), target = long.Parse(after, Invariant);
                if (target < 0) throw new ArgumentOutOfRangeException("after");
                farm.EarnMoney(new FarmMoney(MoneyType(p[1]), checked(target - old)), null);
            }
            else if (p[0] == "resource")
            {
                var r = Resource(farm, p[1]); long target = AdminNumbers.Target(r.Amount, long.Parse(after, Invariant), false, r.MaxValue);
                if (target >= r.Amount) r.IncreaseAmount(target - r.Amount); else r.DecreaseAmount(r.Amount - target);
            }
            else if (p[0] == "feed")
            {
                var r = Race(farm, p); string[] parts = after.Split(','); r.Food = uint.Parse(parts[0], Invariant); r.ExtraFood = uint.Parse(parts[1], Invariant);
                farm.AnimalFieldUpdated(farm.GetAnimalField(uint.Parse(p[1], Invariant)));
            }
            else
            {
                var c = Tile(farm, p).Contents;
                string[] parts = after.Substring(after.LastIndexOf(';') + 1).Split(','); uint amount = uint.Parse(parts[0], Invariant);
                if (p[0] == "water") Field(c, "waterLevel").SetValue(c, amount);
                else if (c is AnimalFieldContents)
                {
                    Field(c, "foodConsumed").SetValue(c, amount);
                    c.GetType().GetProperty("State").GetSetMethod(true).Invoke(c, new object[] { AnimalFieldContents.AnimalState.ReadyToHarvest });
                }
                else if (c is PondContents)
                { ((PondContents)c).SetWaitedTime(amount); c.Tick(0); }
                else
                {
                    Field(c, "growthPoints").SetValue(c, amount);
                    var state = Field(c, "state"); state.SetValue(c, Enum.ToObject(state.FieldType, 1));
                }
                c.Tile.StateChanged(false);
            }
            if (Read(farm, key) != after) throw new InvalidOperationException("修改后校验失败");
        }
    }
}
