using System;
using BepInEx.Configuration;
using Logic.Farm;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FarmProgress
{
    public sealed class DirectionGuide
    {
        readonly ConfigEntry<bool> enabled;
        readonly ConfigEntry<int> mode;
        readonly GuideTarget[] targets = new GuideTarget[2];
        FarmData farm;
        GuideIndex index;
        Camera camera;
        Vector3 playerPosition;
        float nextPick, waterThreshold;
        bool visible;
        string focusGroup;
        GUIStyle label;
        public Action<int> OpenDetails;
        public DirectionGuide(ConfigFile config)
        {
            enabled = config.Bind("Guidance", "Enabled", true, "Show nearest watering and feeding world-space/edge direction markers");
            mode = config.Bind("Guidance", "Mode", 0, "0: water and feed, 1: water only, 2: feed only");
        }
        public void Update(FarmData data, GuideIndex current, float threshold)
        {
            bool newFarm = !ReferenceEquals(farm, data), newIndex = !ReferenceEquals(index, current);
            if (newFarm) { farm = data; focusGroup = null; targets[0] = targets[1] = null; nextPick = 0; }
            index = current; waterThreshold = threshold; visible = false;
            var stage = StageScript.Instance; var player = stage == null ? null : stage.LocalPlayer;
            if (!enabled.Value || farm == null || index == null || player == null || player.IsInHouse || player.IsBusy || stage.PhotoMode) return;
            camera = player.Controller == null ? null : player.Controller.Camera;
            if (camera == null || !camera.enabled) return;
            visible = true; playerPosition = player.transform.position;
            if (!newIndex && Time.realtimeSinceStartup < nextPick) return;
            nextPick = Time.realtimeSinceStartup + 0.4f;
            if (focusGroup != null && !index.HasGroup(focusGroup)) focusGroup = null;
            var growth = new GrowthSnapshot();
            for (int kind = 0; kind < 2; kind++)
            {
                if (kind == 0 && mode.Value == 2 || kind == 1 && mode.Value == 1) { targets[kind] = null; continue; }
                string previous = targets[kind] == null ? null : targets[kind].Key;
                targets[kind] = null;
                // Bound work while a large tractor operation invalidates many indexed tiles.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    var target = index.Pick(kind, playerPosition.x, playerPosition.z, previous, focusGroup);
                    if (target == null) break;
                    if (StillNeeded(target, growth)) { targets[kind] = target; break; }
                    index.ExcludeTile(target);
                }
            }
        }
        bool StillNeeded(GuideTarget target, GrowthSnapshot growth)
        {
            var tile = farm.GetTile(new FarmTileId { _TileX = (short)target.TileX, _TileY = (short)target.TileY });
            if (tile == null || !tile.Chunk.IsUnlocked || tile.Contents == null || tile.Contents.ShopItemDefinition == null || tile.Contents.ShopItemDefinition.FullId != target.ItemId) return false;
            var row = growth.Capture(tile);
            if (row == null || row["readError"] != null) return false;
            if (target.Kind == 0) return LiveRules.NeedsWater(row, waterThreshold);
            if ("feed:" + row["animalFieldId"] + ":" + row["id"] != target.Key) return false;
            long? available = (long?)row["foodAvailableForRace"], needed = (long?)row["foodNeededToFinishRace"];
            if (!available.HasValue || !needed.HasValue) return false;
            if (available >= needed) { index.Resolve(target.Key); return false; }
            target.Hungry = available == 0;
            return (bool?)row["ready"] != true;
        }
        public void Draw(float scale)
        {
            if (!visible || camera == null) return;
            if (label == null) label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip };
            Rect pixels = camera.pixelRect;
            var view = new Rect(pixels.x, Screen.height - pixels.yMax, pixels.width, pixels.height);
            float s = Mathf.Min(scale, view.width / 360f, view.height / 240f);
            if (s <= 0) return;
            Rect? previousLabel = null; Vector2? previousAnchor = null;
            for (int kind = 0; kind < targets.Length; kind++)
            {
                var target = targets[kind]; if (target == null) continue;
                var point = camera.WorldToViewportPoint(new Vector3((float)target.X, (float)target.Y + 1.8f, (float)target.Z));
                var placement = GuideGeometry.Place(point.x, point.y, point.z, view.width, view.height, 28 * s);
                if (!placement.Valid) continue;
                var anchor = new Vector2(view.x + (float)placement.X, view.y + (float)placement.Y);
                var direction = new Vector2((float)placement.DirectionX, (float)placement.DirectionY);
                if (!placement.OnScreen && previousAnchor.HasValue && (anchor - previousAnchor.Value).sqrMagnitude < 45 * s * 45 * s)
                {
                    if (Mathf.Abs(direction.x) > Mathf.Abs(direction.y)) anchor.y += anchor.y + 60 * s < view.yMax - 28 * s ? 60 * s : -60 * s;
                    else anchor.x += anchor.x + 170 * s < view.xMax - 28 * s ? 170 * s : -170 * s;
                }
                Color accent = kind == 0 ? new Color(0.38f, 0.77f, 1f) : target.Hungry ? new Color(1f, 0.40f, 0.36f) : new Color(1f, 0.78f, 0.30f);
                Vector2 center;
                if (placement.OnScreen)
                {
                    anchor.y += Mathf.Sin(Time.realtimeSinceStartup * 2.4f) * 3 * s;
                    direction = Vector2.down; center = anchor + new Vector2(0, -42 * s);
                }
                else center = anchor - new Vector2(direction.x * 108 * s, direction.y * 55 * s);
                var box = new Rect(center.x - 83 * s, center.y - 18 * s, 166 * s, 36 * s);
                box.x = Mathf.Clamp(box.x, view.x + 6 * s, view.xMax - box.width - 6 * s);
                box.y = Mathf.Clamp(box.y, view.y + 6 * s, view.yMax - box.height - 6 * s);
                if (previousLabel.HasValue && box.Overlaps(previousLabel.Value))
                    box.y = box.y - 46 * s > view.y + 6 * s ? box.y - 46 * s : Mathf.Min(view.yMax - box.height - 6 * s, box.y + 46 * s);
                Fill(new Rect(box.x + 2, box.y + 3, box.width, box.height), new Color(0, 0, 0, 0.45f));
                Fill(box, new Color(0.035f, 0.05f, 0.08f, 0.96f));
                Fill(new Rect(box.x, box.y, 4 * s, box.height), accent);
                label.fontSize = Mathf.RoundToInt(16 * s); label.normal.textColor = accent;
                double distance = Math.Sqrt(target.DistanceSquared(playerPosition.x, playerPosition.z)) / 2;
                string title = kind == 0 ? "浇水" : target.Hungry ? "喂食" : "补料";
                string text = title + " · " + (distance < 1 ? "就在附近" : "约 " + Math.Ceiling(distance) + " 格");
                string tooltip = target.Name + " (" + target.TileX + "," + target.TileY + ")；直线距离，不代表可通行路线";
                if (GUI.Button(box, new GUIContent(text, tooltip), label) && OpenDetails != null) OpenDetails(kind == 0 ? 2 : 3);
                DrawArrow(anchor, direction, accent, s);
                previousLabel = box; previousAnchor = anchor;
            }
        }
        static void Fill(Rect rect, Color color)
        { var old = GUI.color; GUI.color = old * color; GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = old; }
        static void Line(Vector2 from, Vector2 to, Color color, float width)
        {
            Matrix4x4 matrix = GUI.matrix; Vector2 delta = to - from;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, from);
            Fill(new Rect(from.x, from.y - width / 2, delta.magnitude, width), color); GUI.matrix = matrix;
        }
        static void DrawArrow(Vector2 at, Vector2 direction, Color accent, float s)
        {
            Vector2 side = new Vector2(-direction.y, direction.x);
            Vector2 tip = at + direction * 12 * s, tail = at - direction * 8 * s;
            Vector2 left = at - direction * 2 * s + side * 9 * s, right = at - direction * 2 * s - side * 9 * s;
            Line(tail, tip, Color.black, 7 * s); Line(left, tip, Color.black, 7 * s); Line(right, tip, Color.black, 7 * s);
            Line(tail, tip, accent, 3 * s); Line(left, tip, accent, 3 * s); Line(right, tip, accent, 3 * s);
        }
        public void Focus(string groupKey) { focusGroup = groupKey; nextPick = 0; }
        public void DrawSettings()
        {
            enabled.Value = GUILayout.Toggle(enabled.Value, "浮动方向指引（画面内标记，画面外边缘箭头）");
            if (enabled.Value) mode.Value = GUILayout.SelectionGrid(Mathf.Clamp(mode.Value, 0, 2), new[] { "浇水＋喂食", "只指引浇水", "只指引喂食" }, 3);
            if (focusGroup != null && GUILayout.Button("恢复指引最近的目标")) { focusGroup = null; nextPick = 0; }
        }
    }
}
