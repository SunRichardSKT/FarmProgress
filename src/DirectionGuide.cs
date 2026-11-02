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
        readonly ConfigEntry<int> maxDistance;
        readonly ConfigEntry<bool> preferFar;
        string rangeInput, rangeError;
        readonly GuideTarget[] targets = new GuideTarget[3];
        readonly Rect[] drawnLabels = new Rect[3];
        readonly Vector2[] drawnAnchors = new Vector2[3];
        FarmData farm;
        GuideIndex index;
        Camera camera;
        Vector3 playerPosition;
        float nextPick, waterThreshold;
        bool visible;
        string focusGroup;
        GUIStyle label, distanceLabel;
        public Action<int> OpenDetails;
        public DirectionGuide(ConfigFile config)
        {
            enabled = config.Bind("Guidance", "Enabled", true, "Show nearest harvest, watering and feeding world-space/edge direction markers");
            mode = config.Bind("Guidance", "Mode", 0, "0: all three, 1: water only, 2: feed only, 3: harvest only");
            maxDistance = config.Bind("Guidance", "MaxDistanceTiles", 0, "Search radius in tiles, 0 means the whole loaded farm; custom values 1 to 10000");
            preferFar = config.Bind("Guidance", "PreferFarTargets", false, "Prefer farthest targets within the selected range");
            rangeInput = maxDistance.Value.ToString();
        }
        public void Update(FarmData data, GuideIndex current, float threshold)
        {
            bool newFarm = !ReferenceEquals(farm, data), newIndex = !ReferenceEquals(index, current);
            if (newFarm) { farm = data; focusGroup = null; Array.Clear(targets, 0, targets.Length); nextPick = 0; }
            index = current; waterThreshold = threshold; visible = false;
            var stage = StageScript.Instance; var player = stage == null ? null : stage.LocalPlayer;
            if (!enabled.Value || farm == null || index == null || player == null || player.IsInHouse || player.IsBusy && !ModInputCapture.Owns(player) || stage.PhotoMode) return;
            camera = player.Controller == null ? null : player.Controller.Camera;
            if (camera == null || !camera.enabled) return;
            visible = true; playerPosition = player.transform.position;
            if (!newIndex && Time.realtimeSinceStartup < nextPick) return;
            nextPick = Time.realtimeSinceStartup + 0.4f;
            if (focusGroup != null && !index.HasGroup(focusGroup)) focusGroup = null;
            var growth = new GrowthSnapshot();
            for (int kind = 0; kind < targets.Length; kind++)
            {
                if (mode.Value >= 1 && mode.Value <= 3 && kind != mode.Value - 1) { targets[kind] = null; continue; }
                string previous = targets[kind] == null ? null : targets[kind].Key;
                targets[kind] = null;
                // Bound work while a large tractor operation invalidates many indexed tiles.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    var target = index.Pick(kind, playerPosition.x, playerPosition.z, previous, focusGroup, Math.Max(0, Math.Min(10000, maxDistance.Value)), preferFar.Value);
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
            if (target.Kind == 2)
            {
                if ((string)row["category"] == "Pond" && target.Key != "harvest:pond:" + row["pondId"]) return false;
                bool harvestable = GuideIndex.IsHarvestable(row);
                if (!harvestable && (string)row["category"] == "Pond") index.Resolve(target.Key);
                return harvestable;
            }
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
            if (label == null)
            {
                label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false, clipping = TextClipping.Clip, richText = false };
                distanceLabel = new GUIStyle(label) { fontStyle = FontStyle.Normal };
                distanceLabel.normal.textColor = new Color(0.82f, 0.88f, 0.92f);
            }
            Rect pixels = camera.pixelRect;
            var view = new Rect(pixels.x, Screen.height - pixels.yMax, pixels.width, pixels.height);
            float s = Mathf.Min(scale, view.width / 360f, view.height / 240f);
            if (s <= 0) return;
            int drawn = 0;
            for (int kind = 0; kind < targets.Length; kind++)
            {
                var target = targets[kind]; if (target == null) continue;
                var point = camera.WorldToViewportPoint(new Vector3((float)target.X, (float)target.Y + 1.8f, (float)target.Z));
                var placement = GuideGeometry.Place(point.x, point.y, point.z, view.width, view.height, 28 * s);
                if (!placement.Valid) continue;
                var anchor = new Vector2(view.x + (float)placement.X, view.y + (float)placement.Y);
                var direction = new Vector2((float)placement.DirectionX, (float)placement.DirectionY);
                Vector2 originalAnchor = anchor;
                for (int attempt = 0; !placement.OnScreen && attempt < 5; attempt++)
                {
                    float offset = attempt == 0 ? 0 : (attempt + 1) / 2 * (attempt % 2 == 1 ? 1 : -1);
                    anchor = originalAnchor;
                    if (Mathf.Abs(direction.x) > Mathf.Abs(direction.y)) anchor.y += offset * 60 * s;
                    else anchor.x += offset * 170 * s;
                    if (anchor.x < view.x + 28 * s || anchor.x > view.xMax - 28 * s || anchor.y < view.y + 28 * s || anchor.y > view.yMax - 28 * s) continue;
                    bool crowded = false;
                    for (int previous = 0; previous < drawn; previous++) crowded |= (anchor - drawnAnchors[previous]).sqrMagnitude < 45 * s * 45 * s;
                    if (!crowded) break;
                }
                anchor.x = Mathf.Clamp(anchor.x, view.x + 28 * s, view.xMax - 28 * s);
                anchor.y = Mathf.Clamp(anchor.y, view.y + 28 * s, view.yMax - 28 * s);
                Color accent = kind == 2 ? new Color(0.42f, 0.91f, 0.57f) : kind == 0 ? new Color(0.38f, 0.77f, 1f) : target.Hungry ? new Color(1f, 0.40f, 0.36f) : new Color(1f, 0.78f, 0.30f);
                label.fontSize = Mathf.RoundToInt(16 * s); label.normal.textColor = accent;
                distanceLabel.fontSize = Mathf.RoundToInt(12 * s);
                string title = TargetText.GuideTitle(kind, target.Hungry, target.Name);
                float boxWidth = Mathf.Clamp(label.CalcSize(new GUIContent(title)).x + 24 * s, 166 * s, 280 * s);
                Vector2 center;
                if (placement.OnScreen)
                {
                    anchor.y += Mathf.Sin(Time.realtimeSinceStartup * 2.4f) * 3 * s;
                    direction = Vector2.down; center = anchor + new Vector2(0, -52 * s);
                }
                else center = anchor - new Vector2(direction.x * (boxWidth / 2 + 30 * s), direction.y * 65 * s);
                var box = new Rect(center.x - boxWidth / 2, center.y - 26 * s, boxWidth, 52 * s);
                box.x = Mathf.Clamp(box.x, view.x + 6 * s, view.xMax - box.width - 6 * s);
                box.y = Mathf.Clamp(box.y, view.y + 6 * s, view.yMax - box.height - 6 * s);
                Rect originalBox = box;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    float offset = attempt == 0 ? 0 : (attempt + 1) / 2 * (attempt % 2 == 1 ? 1 : -1);
                    box.y = Mathf.Clamp(originalBox.y + offset * 62 * s, view.y + 6 * s, view.yMax - box.height - 6 * s);
                    bool crowded = false;
                    for (int previous = 0; previous < drawn; previous++) crowded |= box.Overlaps(drawnLabels[previous]);
                    if (!crowded) break;
                }
                Fill(new Rect(box.x + 2, box.y + 3, box.width, box.height), new Color(0, 0, 0, 0.45f));
                Fill(box, new Color(0.035f, 0.05f, 0.08f, 0.96f));
                Fill(new Rect(box.x, box.y, 4 * s, box.height), accent);
                double distance = Math.Sqrt(target.DistanceSquared(playerPosition.x, playerPosition.z)) / 2;
                string distanceText = distance < 1 ? "就在附近" : "约 " + Math.Ceiling(distance) + " 格";
                string tooltip = target.Name + " (" + target.TileX + "," + target.TileY + ")；直线距离，不代表可通行路线";
                if (GUI.Button(box, new GUIContent("", tooltip), GUIStyle.none) && OpenDetails != null) OpenDetails(kind == 2 ? 1 : kind == 0 ? 2 : 3);
                GUI.Label(new Rect(box.x + 8 * s, box.y + 3 * s, box.width - 16 * s, 25 * s), title, label);
                GUI.Label(new Rect(box.x + 8 * s, box.y + 28 * s, box.width - 16 * s, 20 * s), distanceText, distanceLabel);
                DrawArrow(anchor, direction, accent, s);
                drawnLabels[drawn] = box; drawnAnchors[drawn] = anchor; drawn++;
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
        public string RangeDescription { get { return (maxDistance.Value <= 0 ? "全农场" : maxDistance.Value + " 格内") + " · " + (preferFar.Value ? "先看远处" : "就近处理"); } }
        public void ResetDefaults()
        { enabled.Value = true; mode.Value = 0; maxDistance.Value = 0; preferFar.Value = false; rangeInput = "0"; rangeError = null; focusGroup = null; nextPick = 0; }
        public void DrawSettings()
        {
            enabled.Value = GUILayout.Toggle(enabled.Value, "开启浮动方向指引");
            if (enabled.Value) mode.Value = GUILayout.SelectionGrid(Mathf.Clamp(mode.Value, 0, 3), new[] { "收获＋浇水＋喂食", "只指引浇水", "只指引喂食", "只指引收获" }, 2);
            GUILayout.Space(8);
            GUILayout.Label("搜索距离：" + RangeDescription);
            GUILayout.BeginHorizontal();
            foreach (int distance in new[] { 50, 100, 250, 500, 1000, 0 })
                if (GUILayout.Button(distance == 0 ? "全农场" : distance + " 格")) { maxDistance.Value = distance; rangeInput = distance.ToString(); rangeError = null; nextPick = 0; }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal(); GUILayout.Label("自定义", GUILayout.Width(62)); rangeInput = GUILayout.TextField(rangeInput, 5);
            if (GUILayout.Button("应用", GUILayout.Width(75)))
            {
                int parsed;
                if (int.TryParse(rangeInput, out parsed) && parsed >= 0 && parsed <= 10000) { maxDistance.Value = parsed; rangeError = null; nextPick = 0; }
                else rangeError = "请输入 0 至 10000，0 表示全农场。";
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("0 表示全农场；支持自定义 1–10000 格。", PanelTheme.Muted);
            if (rangeError != null) GUILayout.Label(rangeError);
            bool newFar = GUILayout.SelectionGrid(preferFar.Value ? 1 : 0, new[] { "就近处理", "先看远处" }, 2) == 1;
            if (newFar != preferFar.Value) { preferFar.Value = newFar; nextPick = 0; }
            if (focusGroup != null && GUILayout.Button("恢复指引最近的目标")) { focusGroup = null; nextPick = 0; }
        }
    }
}
