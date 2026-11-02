using System;
using System.Collections.Generic;
using Logic.Farm;
using Milkstone.Utils;
using Newtonsoft.Json.Linq;

namespace FarmProgress
{
    public static class Snapshot
    {
        static string Name(ItemDefinition def)
        {
            if (def == null) return "";
            try { return Localization.Get(def.FullId); } catch { return def.FullId; }
        }
        static JObject Item(ItemDefinition def)
        { return new JObject { { "id", def.FullId }, { "name", Name(def) } }; }
        static JObject Money(FarmMoney m)
        {
            // Strings keep exact Int64 balances in browser reports, including values above 2^53.
            return new JObject { { "coins", m.Coins.ToString() }, { "diamonds", m.Bills.ToString() },
                { "medals", m.Medals.ToString() }, { "tickets", m.Tickets.ToString() } };
        }
        public static JObject Capture(FarmData farm)
        {
            var root = new JObject { { "schemaVersion", 3 }, { "capturedAt", DateTimeOffset.Now.ToString("o") },
                { "source", farm.IsLocal ? "local" : "multiplayer-client" },
                { "assemblyMvid", typeof(FarmData).Module.ModuleVersionId.ToString() },
                { "level", farm.Experience.Level }, { "levelProgress", farm.Experience.Progress },
                { "xpToNextLevel", farm.Experience.ExperienceToNextLevel }, { "money", Money(farm.Money) },
                { "style", farm.FarmStyle.FullId }, { "season", farm.CurrentSeason.ToString() },
                { "weather", farm.CurrentWeather.ToString() }, { "worldTicks", farm.WorldTicks },
                { "unlockedChunks", farm.UnlockedChunks }, { "totalChunks", farm.Chunks.Length },
                { "notes", "当前客户端快照；活动奖励资格不能证明房主账号的解锁。建筑按独立实例计数，占地格数另列。地图为地块示意图，不是游戏截图。" } };
            var resources = new JArray();
            for (int i = 1; i < (int)FarmResourceType.Count; i++)
            {
                var r = farm.GetResource((FarmResourceType)i);
                if (r != null) resources.Add(new JObject { { "id", r.Resource.ToString() }, { "amount", r.Amount.ToString() }, { "capacity", r.MaxValue.ToString() } });
            }
            root.Add("resources", resources);
            root.Add("nextSeasonInSeconds", Math.Max(0L, (long)farm.NextSeasonChange - farm.WorldTicks));
            var unlocks = new JArray();
            foreach (var def in ShopManager.ItemDictionary.Values)
            {
                if (def == null || !def.IsEnabled) continue;
                var row = Item(def);
                bool meets = farm.Experience.Level >= def.MinLevel;
                var requirements = new JArray();
                if (def.ItemLevelRequirements != null) foreach (var req in def.ItemLevelRequirements)
                {
                    uint actual = farm.GetItemLevel(req.Item);
                    meets &= actual >= req.Level;
                    var rr = Item(req.Item); rr.Add("required", req.Level); rr.Add("actual", actual); requirements.Add(rr);
                }
                row.Add("category", def.Category.ToString()); row.Add("minLevel", def.MinLevel);
                row.Add("farmRequirementsMet", meets); row.Add("requirements", requirements);
                row.Add("visitorPurchaseState", farm.IsUnlocked(def).ToString());
                row.Add("dlc", def.RequiredDLC.ToString());
                row.Add("ownerHasDlc", !def.RequiresDLC || farm.CheckOwnerDLC(def.RequiredDLC));
                row.Add("eventItem", def.IsEventItem || def.IsEventReward);
                row.Add("basePrice", Money(def.Price));
                if (def is CropDefinition)
                {
                    var crop = (CropDefinition)def; row.Add("baseGrowSeconds", crop.GrowTime);
                    row.Add("plantSeasons", crop.PlantSeasons.ToString()); row.Add("baseHarvestMoney", Money(crop.HarvestMoney)); row.Add("baseHarvestXp", crop.HarvestXp);
                }
                var harvest = def as HarvestItemDefinition;
                if (harvest != null) { row.Add("itemLevel", farm.GetItemLevel(harvest)); row.Add("itemProgress", farm.GetItemLevelProgress(harvest)); row.Add("resourceType", harvest.Resource.Type.ToString()); }
                unlocks.Add(row);
            }
            root.Add("unlocks", unlocks);
            var recipes = new JArray();
            foreach (var def in ShopManager.RecipeDictionary.Values)
            {
                if (def == null || !def.IsEnabled) continue;
                var row = Item(def); row.Add("level", farm.GetRecipeLevel(def)); row.Add("progress", farm.GetRecipeLevelProgress(def));
                row.Add("visitorPurchaseState", farm.IsUnlocked(def).ToString()); recipes.Add(row);
            }
            root.Add("recipes", recipes);
            var chunks = new JArray(); var tiles = new JArray(); var fences = new JArray(); var growthTiles = new JArray();
            var growth = new GrowthSnapshot();
            var counts = new Dictionary<string, JObject>();
            var buildings = new JArray();
            // Buildings occupy multiple tiles: count their authoritative instance list once.
            foreach (var b in farm.Buildings)
            {
                AddCount(counts, b.Definition, b.Definition.Category.ToString(), 1, 0);
                var row = Item(b.Definition); row.Add("instanceId", b.Id);
                row.Add("x", b.TopLeftTile._TileX); row.Add("y", b.TopLeftTile._TileY); row.Add("rotation", b.Rotation);
                row.Add("detail", b.ToJson()); buildings.Add(row);
            }
            foreach (var chunk in farm.Chunks)
            {
                if (chunk == null) continue;
                chunks.Add(new JObject { { "x", chunk.ChunkPosition.x }, { "y", chunk.ChunkPosition.y }, { "unlocked", chunk.IsUnlocked } });
                foreach (var tile in chunk.Tiles)
                {
                    if (tile == null) continue;
                    var c = tile.Contents; var def = c == null ? null : c.ShopItemDefinition;
                    var category = c == null ? "Empty" : c.Category.ToString();
                    // Compact row: x, y, category, definition ID, floor, unlocked, irrigated, height.
                    tiles.Add(new JArray(tile.TileId._TileX, tile.TileId._TileY, category, def == null ? "" : def.FullId,
                        tile.Floor.ToString(), chunk.IsUnlocked, tile.Irrigated, tile.FloorHeight));
                    if (def != null) AddCount(counts, def, category, c.IsBaseBuilding ? 0 : 1, 1);
                    var growthRow = growth.Capture(tile); if (growthRow != null) growthTiles.Add(growthRow);
                }
                foreach (var fence in chunk.Fences)
                {
                    if (fence == null) continue;
                    AddCount(counts, fence.Definition, "Fence", 1, 0);
                    var row = Item(fence.Definition); row.Add("x", fence.TileId._TileX); row.Add("y", fence.TileId._TileY);
                    row.Add("orientation", fence.Orientation.ToString()); fences.Add(row);
                }
            }
            root.Add("chunkSizeX", GameGlobals.ChunkSize.x); root.Add("chunkSizeY", GameGlobals.ChunkSize.y);
            root.Add("chunks", chunks); root.Add("tiles", tiles); root.Add("buildings", buildings); root.Add("fences", fences);
            root.Add("counts", new JArray(counts.Values));
            root.Add("growthTiles", growthTiles);
            var quests = new JArray();
            foreach (var q in farm.ActiveQuests) quests.Add(q.ToJson(q == farm.PinnedQuest));
            root.Add("quests", quests);
            var taskDetails = new JArray();
            foreach (var q in farm.ActiveQuests)
            {
                var requirements = new JArray();
                foreach (var target in q.QuestItems)
                {
                    var row = Item(target.ShopItem);
                    row.Add("required", target.RequiredAmount); row.Add("progress", target.Progress);
                    row.Add("remaining", target.Progress >= target.RequiredAmount ? 0u : target.RequiredAmount - target.Progress);
                    var resourceType = target.ShopItem.Resource.Type;
                    var stock = resourceType == FarmResourceType.Unused ? null : farm.GetResource(resourceType);
                    row.Add("resourceType", resourceType.ToString());
                    row.Add("stock", stock == null ? null : stock.Amount.ToString());
                    requirements.Add(row);
                }
                taskDetails.Add(new JObject {
                    { "id", q.QuestId }, { "title", q.Lore == null ? "任务 " + q.QuestId : q.Lore.Title },
                    { "length", q.Length.ToString() }, { "extra", q.IsExtraQuest }, { "pinned", q == farm.PinnedQuest },
                    { "complete", q.Complete }, { "reward", Money(q.RewardMoney) },
                    { "experience", q.RewardExperience.ToString() }, { "items", requirements }
                });
            }
            root.Add("taskDetails", taskDetails);
            return root;
        }
        static void AddCount(Dictionary<string, JObject> counts, FarmItemDefinition def, string category, int instances, int cells)
        {
            string key = category + ":" + def.FullId; JObject row;
            if (!counts.TryGetValue(key, out row))
            {
                row = Item(def); row.Add("category", category); row.Add("count", 0); row.Add("cells", 0); counts.Add(key, row);
            }
            row["count"] = (int)row["count"] + instances; row["cells"] = (int)row["cells"] + cells;
        }
    }
}
