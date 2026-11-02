using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace FarmProgress
{
    public sealed class GuideTarget
    {
        public string Key, GroupKey, Name, ItemId;
        public int Kind, TileX, TileY;
        public bool Hungry;
        public bool IsPond;
        public double X, Y, Z;
        public double DistanceSquared(double playerX, double playerZ)
        { double dx = X - playerX, dz = Z - playerZ; return dx * dx + dz * dz; }
    }

    public sealed class GuideIndex
    {
        readonly List<GuideTarget> targets = new List<GuideTarget>();
        readonly HashSet<string> resolved = new HashSet<string>();
        readonly HashSet<GuideTarget> excluded = new HashSet<GuideTarget>();
        bool Available(GuideTarget target) { return !resolved.Contains(target.Key) && !excluded.Contains(target); }
        public static bool IsHarvestable(JObject row)
        {
            return (bool?)row["unlocked"] == true && row["readError"] == null &&
                ((string)row["category"] == "Pond" ? (bool?)row["pondHarvestable"] == true : (bool?)row["ready"] == true);
        }
        public void Add(JObject row, string groupKey, string name, double worldX, double worldY, double worldZ, double threshold)
        {
            if ((bool?)row["unlocked"] != true || row["readError"] != null) return;
            bool harvest = IsHarvestable(row), water = !harvest && LiveRules.NeedsWater(row, threshold);
            long? available = (long?)row["foodAvailableForRace"], needed = (long?)row["foodNeededToFinishRace"];
            bool feed = !harvest && (bool?)row["ready"] != true && (string)row["category"] == "Animal" && available.HasValue && needed.HasValue && available < needed;
            if (!harvest && !water && !feed) return;
            int x = (int)row["x"], y = (int)row["y"];
            targets.Add(new GuideTarget { Kind = harvest ? 2 : water ? 0 : 1, Hungry = feed && available == 0,
                Key = harvest ? ((string)row["category"] == "Pond" ? "harvest:pond:" + row["pondId"] : "harvest:" + x + ":" + y) :
                    water ? "water:" + x + ":" + y : "feed:" + row["animalFieldId"] + ":" + row["id"],
                GroupKey = groupKey, Name = TargetText.Name(name, (string)row["id"]), IsPond = (string)row["category"] == "Pond", ItemId = (string)row["id"], TileX = x, TileY = y, X = worldX, Y = worldY, Z = worldZ });
        }
        public void CompleteNames(LiveRules rules)
        {
            var summaries = new Dictionary<string, string>();
            foreach (var target in targets)
                if (target.Kind == 2 && target.IsPond)
                {
                    string summary;
                    if (!summaries.TryGetValue(target.GroupKey, out summary))
                    {
                        LiveGroup group;
                        summary = rules.Groups.TryGetValue(target.GroupKey, out group) && group.HarvestNames.Count > 0 ?
                            "鱼塘 · " + TargetText.Summary(group.HarvestNames, "鱼塘") : "鱼塘";
                        summaries[target.GroupKey] = summary;
                    }
                    target.Name = summary;
                }
        }
        public GuideTarget Pick(int kind, double playerX, double playerZ, string previousKey, string focusGroup, double maxDistanceTiles = 0, bool preferFar = false)
        {
            GuideTarget best = null, previous = null;
            double bestDistance = preferFar ? -1 : double.MaxValue, previousDistance = preferFar ? -1 : double.MaxValue;
            double limitSquared = maxDistanceTiles > 0 ? maxDistanceTiles * maxDistanceTiles * 4 : double.MaxValue;
            bool hasFocused = false, hasHungry = false;
            foreach (var target in targets)
                if (target.Kind == kind && Available(target) && target.DistanceSquared(playerX, playerZ) <= limitSquared && (focusGroup == null || target.GroupKey == focusGroup))
                { hasFocused |= focusGroup != null; hasHungry |= target.Hungry; }
            if (!hasFocused) foreach (var target in targets)
                if (target.Kind == kind && Available(target) && target.DistanceSquared(playerX, playerZ) <= limitSquared) hasHungry |= target.Hungry;
            foreach (var target in targets)
            {
                if (target.Kind != kind || !Available(target) || hasFocused && target.GroupKey != focusGroup || hasHungry && !target.Hungry) continue;
                double distance = target.DistanceSquared(playerX, playerZ);
                if (distance > limitSquared) continue;
                if (best == null || (preferFar ? distance > bestDistance : distance < bestDistance) || distance == bestDistance && Earlier(target, best)) { best = target; bestDistance = distance; }
                if (target.Key == previousKey && (previous == null || (preferFar ? distance > previousDistance : distance < previousDistance))) { previous = target; previousDistance = distance; }
            }
            // Keep a nearby valid target while walking across equal-distance boundaries.
            if (previous != null && (preferFar ? Math.Sqrt(previousDistance) >= Math.Sqrt(bestDistance) / 1.2 - 2 : Math.Sqrt(previousDistance) <= Math.Sqrt(bestDistance) * 1.2 + 2)) return previous;
            return best;
        }
        static bool Earlier(GuideTarget a, GuideTarget b) { return a.TileX < b.TileX || a.TileX == b.TileX && a.TileY < b.TileY; }
        public void Resolve(string key) { resolved.Add(key); }
        public void ExcludeTile(GuideTarget target) { excluded.Add(target); }
        public bool HasGroup(string key)
        {
            foreach (var target in targets) if (target.GroupKey == key && Available(target)) return true;
            return false;
        }
    }

    public sealed class GuidePlacement
    {
        public double X, Y, DirectionX, DirectionY;
        public bool OnScreen, Valid;
    }

    public static class GuideGeometry
    {
        // Unity viewport coordinates have their origin at bottom left. GUI uses top left.
        public static GuidePlacement Place(double viewportX, double viewportY, double depth, double width, double height, double margin)
        {
            var result = new GuidePlacement();
            if (!Finite(viewportX) || !Finite(viewportY) || !Finite(depth) || width <= 0 || height <= 0) return result;
            double halfWidth = Math.Max(1, width / 2 - Math.Min(margin, width / 3));
            double halfHeight = Math.Max(1, height / 2 - Math.Min(margin, height / 3));
            double dx = (viewportX - 0.5) * width, dy = (0.5 - viewportY) * height;
            result.OnScreen = depth > 0 && Math.Abs(dx) <= halfWidth && Math.Abs(dy) <= halfHeight;
            if (depth <= 0) { dx = -dx; dy = -dy; }
            if (!result.OnScreen)
            {
                if (Math.Abs(dx) + Math.Abs(dy) < 0.001) dy = 1; // Directly behind the camera.
                double factor = Math.Min(Math.Abs(dx) < 0.001 ? double.MaxValue : halfWidth / Math.Abs(dx),
                    Math.Abs(dy) < 0.001 ? double.MaxValue : halfHeight / Math.Abs(dy));
                dx *= factor; dy *= factor;
            }
            result.X = width / 2 + dx; result.Y = height / 2 + dy;
            double length = Math.Sqrt(dx * dx + dy * dy);
            result.DirectionX = length > 0.001 ? dx / length : 0;
            result.DirectionY = length > 0.001 ? dy / length : 1;
            result.Valid = true; return result;
        }
        static bool Finite(double n) { return !double.IsNaN(n) && !double.IsInfinity(n); }
    }
}
