using System;
using System.Collections.Generic;
using System.Reflection;
using Logic.Farm;
using Logic.Farm.Contents;
using Newtonsoft.Json.Linq;

namespace FarmProgress
{
    public sealed class GrowthSnapshot
    {
        readonly Dictionary<Type, Dictionary<string, PropertyInfo>> properties = new Dictionary<Type, Dictionary<string, PropertyInfo>>();
        readonly Dictionary<AnimalField.FieldRaceData, long> feedNeeds = new Dictionary<AnimalField.FieldRaceData, long>();
        object Read(object obj, string name)
        {
            Dictionary<string, PropertyInfo> members;
            if (!properties.TryGetValue(obj.GetType(), out members))
            { members = new Dictionary<string, PropertyInfo>(); foreach (var p in obj.GetType().GetProperties()) members[p.Name] = p; properties.Add(obj.GetType(), members); }
            PropertyInfo property;
            if (!members.TryGetValue(name, out property)) throw new MissingMemberException(obj.GetType().FullName, name);
            return property.GetValue(obj, null);
        }
        public JObject Capture(FarmTile tile)
        {
            var c = tile.Contents; if (c == null) return null;
            var category = c.Category;
            if (category != FarmTileContentsType.Crop && category != FarmTileContentsType.Flower && category != FarmTileContentsType.Tree && category != FarmTileContentsType.Animal && category != FarmTileContentsType.Pond) return null;
            var def = c.ShopItemDefinition;
            var row = new JObject { { "x", tile.TileId._TileX }, { "y", tile.TileId._TileY }, { "id", def == null ? "" : def.FullId },
                { "category", category.ToString() }, { "unlocked", tile.Chunk.IsUnlocked }, { "irrigated", tile.Irrigated },
                { "waterLevel", null }, { "waterCapacity", null }, { "waterFraction", null }, { "lowWater", false },
                { "remainingSecondsEstimate", null }, { "remainingHoursEstimate", null } };
            try
            {
                string state = Convert.ToString(Read(c, "State")); row.Add("state", state);
                bool ready = state == "ReadyToHarvest" || state == "ReadyToGather";
                row.Add("ready", ready);
                double remaining = 0; bool timeKnown = true;
                string basis;
                if (category == FarmTileContentsType.Crop || category == FarmTileContentsType.Flower)
                {
                    long water = Convert.ToInt64(Read(c, "WaterLevel"));
                    long capacity = category == FarmTileContentsType.Crop ? ((CropDefinition)def).MaxWaterLevel : ((FlowerDefinition)def).MaxWaterLevel;
                    row["waterLevel"] = water; row["waterCapacity"] = capacity;
                    row["waterFraction"] = capacity > 0 ? (JToken)Math.Min(1d, Math.Max(0d, (double)water / capacity)) : JValue.CreateNull();
                    row["lowWater"] = !ready && !tile.Irrigated && capacity > 0 && water <= capacity * 0.1d;
                    row.Add("growthPoints", Convert.ToInt64(Read(c, "GrowthPoints")));
                    row.Add("growthProgress", Convert.ToDouble(Read(c, "GrowthProgress")));
                    if (category == FarmTileContentsType.Flower)
                    { row.Add("wateredPoints", Convert.ToInt64(Read(c, "WateredPoints"))); row.Add("qualityProgress", Convert.ToDouble(Read(c, "QualityProgress"))); }
                    remaining = Convert.ToDouble(Read(c, "RemainingTime")); basis = "game_remaining_time_estimate";
                }
                else if (category == FarmTileContentsType.Tree)
                {
                    remaining = ready ? 0 : ((FarmTree)c).TimeUntilNextFruit;
                    row.Add("harvestSeasons", ((TreeDefinition)def).HarvestSeasons.ToString()); basis = "next_harvest_season";
                }
                else if (category == FarmTileContentsType.Animal)
                {
                    var animal = (AnimalFieldContents)c; long needed = 0; uint available = 0; bool found = false;
                    foreach (var race in animal.Field.Races) if (race.Definition == animal.Definition)
                    {
                        found = true; available = race.FoodAvailable;
                        if (!feedNeeds.TryGetValue(race, out needed))
                        {
                            foreach (var member in race.Tiles) needed += Math.Max(0L, (long)member.HarvestInterval - member.FoodConsumed);
                            feedNeeds.Add(race, needed);
                        }
                        break;
                    }
                    remaining = Math.Max(0L, (long)animal.HarvestInterval - animal.FoodConsumed);
                    row.Add("animalFieldId", animal.Field.Id); row.Add("foodConsumed", animal.FoodConsumed);
                    row.Add("foodAvailableForRace", found ? (JToken)available : JValue.CreateNull());
                    row.Add("foodNeededToFinishRace", found ? (JToken)needed : JValue.CreateNull());
                    row.Add("minimumRemainingSecondsWithContinuousFeed", ready ? 0 : remaining);
                    timeKnown = ready || found && available >= needed;
                    basis = timeKnown ? "sufficient_current_feed_for_race" : "requires_more_feed_eta_unknown";
                }
                else
                {
                    var pond = (PondContents)c; remaining = Math.Max(0L, (long)pond.Definition.FishInterval - pond.WaitedTime);
                    row.Add("pondId", pond.Pond.Id); row.Add("pondHarvestable", pond.Pond.State == Pond.PondState.ReadyToHarvest);
                    row.Add("waitedSeconds", pond.WaitedTime); basis = "fish_interval";
                }
                row.Add("etaBasis", basis);
                if (ready) remaining = 0;
                if (timeKnown)
                { row["remainingSecondsEstimate"] = remaining; row["remainingHoursEstimate"] = Math.Round(remaining / 3600d, 6); }
            }
            catch (Exception ex) { row.Add("readError", ex.GetType().Name + ": " + ex.Message); }
            return row;
        }
    }
}
