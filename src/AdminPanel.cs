using System;
using Logic.Farm;
using Logic.Farm.Contents;
using UnityEngine;

namespace FarmProgress
{
    public sealed class AdminPanel
    {
        readonly AdminNetwork service;
        Rect rect = new Rect(80, 90, 650, 510);
        bool visible, confirm;
        int tab, currency, resource = 1, mode;
        string amount = "10000", x = "0", y = "0", preview, key, expected;
        long pendingValue;
        bool pendingAdd;
        Vector2 scroll;
        FarmData farm;
        public bool Visible { get { return visible && farm != null; } }
        public void Close() { visible = false; confirm = false; }
        public AdminPanel(AdminNetwork service) { this.service = service; }
        public void Update(FarmData data)
        {
            if (!ReferenceEquals(data, farm)) { farm = data; visible = false; confirm = false; }
            var player = StageScript.Instance == null ? null : StageScript.Instance.LocalPlayer;
            if (data != null && (player == null || !player.IsBusy || ModInputCapture.Owns(player)) && Input.GetKeyDown(KeyCode.F10) &&
                (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) &&
                (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))) visible = !visible;
            if (visible && Input.GetKeyDown(KeyCode.Escape)) { visible = false; confirm = false; }
        }
        public void Draw()
        {
            if (!visible || farm == null) return;
            rect.width = Mathf.Min(650, Screen.width - 20); rect.height = Mathf.Min(560, Screen.height - 40);
            rect.x = Mathf.Clamp(rect.x, 0, Screen.width - rect.width); rect.y = Mathf.Clamp(rect.y, 0, Screen.height - rect.height);
            rect = GUILayout.Window(183749, rect, DrawWindow, "农场数据管理 · Ctrl + Shift + F10");
        }
        void DrawWindow(int id)
        {
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Max(200, rect.height - 55)));
            GUILayout.Label(service.Host ? "房主管理" : "访客远程修改（由房主执行）");
            GUILayout.Label(service.Status);
            if (!service.Supported) GUILayout.Label("当前游戏构建尚未验证，数据修改未启用。");
            if (service.Host) service.AutoAuthorize = GUILayout.Toggle(service.AutoAuthorize, "自动授权所有进入房间的访客（重启后保留）");
            GUILayout.Label("联机修改要求房间内所有客户端安装同版 Mod。首次修改前自动备份农场。");
            tab = GUILayout.SelectionGrid(tab, new[] { "资产", "库存", "补水 / 补料 / 成熟" }, 3);
            if (tab == 0)
            {
                currency = GUILayout.SelectionGrid(currency, new[] { "金币", "钻石", "奖牌", "票券" }, 4);
                string[] names = { "Coins", "Bills", "Medals", "Tickets" };
                GUILayout.Label("当前值：" + farm.Money.Get((MoneyTypes)Enum.Parse(typeof(MoneyTypes), names[currency])));
            }
            else if (tab == 1)
            {
                string[] labels = { "未使用", "水果", "蔬菜", "葡萄", "花", "谷物", "坚果", "牛奶", "鸡蛋", "鱼", "蜂蜜", "毛", "奶酪", "果酱", "蘑菇", "金块", "香料" };
                resource = GUILayout.SelectionGrid(resource - 1, Subset(labels), 4) + 1;
                var stock = farm.GetResource((FarmResourceType)resource); GUILayout.Label("当前库存：" + stock.Amount + " / " + stock.MaxValue);
            }
            if (tab < 2)
            {
                mode = GUILayout.SelectionGrid(mode, new[] { "设置目标值", "增加 / 减少" }, 2);
                GUILayout.Label(mode == 0 ? "目标数量" : "变化数量（负数表示减少）"); amount = GUILayout.TextField(amount, 22);
                if (GUILayout.Button("预览修改"))
                {
                    string[] names = { "Coins", "Bills", "Medals", "Tickets" };
                    Preview(tab == 0 ? "money|" + names[currency] : "resource|" + (FarmResourceType)resource);
                }
            }
            else
            {
                GUILayout.Label("目标地格坐标（F8 待办列表可查看坐标）");
                GUILayout.BeginHorizontal(); GUILayout.Label("X", GUILayout.Width(25)); x = GUILayout.TextField(x, 5);
                GUILayout.Label("Y", GUILayout.Width(25)); y = GUILayout.TextField(y, 5); GUILayout.EndHorizontal();
                GUILayout.Label("补水与补料为管理员直接补满，不扣费用。成熟不直接发放收获收益。");
                if (GUILayout.Button("预览：补满此格水分")) Preview("water|" + x + "|" + y);
                if (GUILayout.Button("预览：补满此格动物所属圈的同品种饲料"))
                {
                    try
                    {
                        var tile = farm.GetTile(new FarmTileId { _TileX = short.Parse(x), _TileY = short.Parse(y) });
                        var animal = tile == null ? null : tile.Contents as AnimalFieldContents;
                        if (animal == null) throw new ArgumentException("此坐标不是动物");
                        Preview("feed|" + animal.Field.Id + "|" + animal.Definition.FullId);
                    }
                    catch (Exception ex) { confirm = false; preview = ex.Message; }
                }
                if (GUILayout.Button("预览：使此格作物 / 花 / 动物 / 鱼成熟")) Preview("mature|" + x + "|" + y);
            }
            if (preview != null) GUILayout.Label(preview);
            bool previous = GUI.enabled; GUI.enabled = previous && confirm && !service.Busy && service.Supported;
            if (GUILayout.Button(service.Busy ? "正在等待联机确认…" : "确认提交修改"))
            { service.Submit(key, pendingValue, pendingAdd, expected); confirm = false; }
            GUI.enabled = previous;
            GUILayout.Label("备份与操作记录：BepInEx/FarmProgressAdminBackups。成熟调整首版为单格操作。");
            if (GUILayout.Button("关闭")) visible = false;
            GUILayout.EndScrollView(); GUI.DragWindow(new Rect(0, 0, 10000, 25));
        }
        void Preview(string target)
        {
            try
            {
                pendingValue = tab < 2 ? long.Parse(amount) : 0; pendingAdd = tab < 2 && mode == 1; key = target;
                var plan = AdminChanges.Plan(farm, key, pendingValue, pendingAdd);
                expected = (string)plan["before"];
                preview = "目标：" + key + "\n修改前：" + plan["before"] + "\n修改后：" + plan["after"];
                confirm = true;
            }
            catch (Exception ex) { confirm = false; preview = ex.Message; }
        }
        static string[] Subset(string[] source) { var a = new string[source.Length - 1]; Array.Copy(source, 1, a, 0, a.Length); return a; }
    }
}
