using System;
using System.IO;
using System.Text;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Logic.Farm;
using Newtonsoft.Json;
using UnityEngine;

namespace FarmProgress
{
    [BepInPlugin("local.farmtogether.progress", "Farm Progress", "1.6.5")]
    [BepInProcess("FarmTogether.exe")]
    [DefaultExecutionOrder(-10000)]
    public sealed class Plugin : BaseUnityPlugin
    {
        ConfigEntry<KeyCode> panelKey, exportKey;
        ConfigEntry<bool> autoExport, rawExport;
        FarmData observed;
        float readyAt;
        bool exported, visible, copying;
        string status = "加入农场后按 F9 导出报告。", lastPath;
        Rect window = new Rect(50, 50, 900, 690);
        bool windowPlaced;
        PanelTheme theme;
        ModInputCapture inputCapture;
        Font font;
        SyncRecovery syncRecovery;
        GuestFuelAssist guestFuel;
        LiveMonitor monitor;
        AdminNetwork admin;
        AdminPanel adminPanel;
        int panelTab;
        Vector2 panelScroll;
        void Awake()
        {
            panelKey = Config.Bind("Keys", "Panel", KeyCode.F8, "Toggle progress panel");
            exportKey = Config.Bind("Keys", "Export", KeyCode.F9, "Export current farm report");
            autoExport = Config.Bind("Export", "OnJoin", true, "Export once, 8 seconds after the farm finishes loading");
            rawExport = Config.Bind("Export", "DetailedJson", true, "Include full read-only farm JSON snapshot");
            font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 16);
            theme = new PanelTheme(); inputCapture = new ModInputCapture();
            if (Config.Bind("Multiplayer", "RecoveryEnabled", true, "Monitor guest divergence and show native rejoin recovery button").Value)
                syncRecovery = new SyncRecovery(Logger);
            guestFuel = new GuestFuelAssist(Config, Logger);
            monitor = new LiveMonitor(Config);
            monitor.OpenDetails = targetFilter => { visible = true; panelTab = 1; panelScroll = Vector2.zero; };
            monitor.ReturnToGame = () => visible = false;
            admin = new AdminNetwork(Config, Logger);
            admin.SyncBlocked = () => syncRecovery != null && syncRecovery.NeedsAttention;
            adminPanel = new AdminPanel(admin);
            Logger.LogInfo("Farm Progress 1.6.5 loaded. Target names included in floating guidance and grouped notifications.");
        }
        void CopyFarm()
        {
            if (copying) return;
            copying = true;
            status = "正在复制当前农场，请等待保存完成。";
            try
            {
                FarmCopy.Start(observed,
                    message => { copying = false; status = message; Logger.LogInfo(message); },
                    error => { copying = false; status = "复制失败，详情见 BepInEx 日志；已有农场不会被选为复制目标。"; Logger.LogError(error); });
            }
            catch (Exception ex) { copying = false; status = "复制失败：" + ex.Message; Logger.LogError(ex); }
        }
        void Update()
        {
            if (syncRecovery != null) syncRecovery.Update();
            if (guestFuel != null) guestFuel.Update();
            if (Input.GetKeyDown(panelKey.Value)) { visible = !visible; if (visible) adminPanel.Close(); }
            if (Input.GetKeyDown(KeyCode.Escape) && (visible || adminPanel.Visible))
            { visible = false; adminPanel.Close(); Input.ResetInputAxes(); }
            var stage = StageScript.Instance;
            var farm = stage != null && stage.Loaded ? stage.FarmData : null;
            adminPanel.Update(farm);
            if (adminPanel.Visible) visible = false;
            CaptureInput();
            monitor.Update(farm);
            admin.Update(farm);
            if (!ReferenceEquals(farm, observed))
            {
                observed = farm; readyAt = Time.realtimeSinceStartup + 8f; exported = false;
                if (farm == null) { visible = false; adminPanel.Close(); }
                lastPath = null;
                status = farm == null ? "等待农场加载完成。" : "农场已加载，准备自动导出。";
            }
            if (farm == null) return;
            if (Input.GetKeyDown(exportKey.Value) || (autoExport.Value && !exported && Time.realtimeSinceStartup >= readyAt))
            { exported = true; Export(farm); }
        }
        void CaptureInput()
        {
            var stage = StageScript.Instance;
            var player = stage != null && stage.Loaded ? stage.LocalPlayer : null;
            inputCapture.Tick(visible || adminPanel.Visible || Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt), player);
        }
        void LateUpdate() { if (inputCapture != null) CaptureInput(); }
        void OnDestroy() { if (inputCapture != null) inputCapture.Dispose(); if (theme != null) theme.Dispose(); if (admin != null) admin.Dispose(); if (guestFuel != null) guestFuel.Dispose(); if (syncRecovery != null) syncRecovery.Dispose(); }
        void Export(FarmData farm)
        {
            try
            {
                var snapshot = Snapshot.Capture(farm);
                var dir = Path.Combine(Path.Combine(Paths.BepInExRootPath, "FarmProgressReports"), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "summary.json"), snapshot.ToString(Formatting.Indented), Encoding.UTF8);
                AiExport.Write(dir, snapshot);
                if (rawExport.Value)
                {
                    // Verified serializer writes JSON only. Never call Save, Load, or network actions.
                    var method = typeof(FarmData).GetMethod("ToJson2", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (method != null)
                    {
                        using (var sw = new StringWriter())
                        using (var jw = new JsonTextWriter(sw))
                        {
                            method.Invoke(farm, new object[] { jw }); jw.Flush();
                            File.WriteAllText(Path.Combine(dir, "farm-detail.json"), sw.ToString(), Encoding.UTF8);
                        }
                    }
                }
                string template;
                using (var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("FarmProgress.report.html"))) template = reader.ReadToEnd();
                // Base64 prevents any farm/item text from becoming executable HTML.
                var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(snapshot.ToString(Formatting.None)));
                lastPath = Path.Combine(dir, "report.html");
                File.WriteAllText(lastPath, template.Replace("__SNAPSHOT_BASE64__", payload), Encoding.UTF8);
                status = "报告已导出：" + dir;
                Logger.LogInfo(status);
            }
            catch (Exception ex) { status = "导出失败，按 F9 重试；详情见 BepInEx 日志。"; Logger.LogError(ex); }
        }
        void OnGUI()
        {
            var sharedFont = GUI.skin.font;
            if (font != null) GUI.skin.font = font;
            monitor.DrawHud();
            var originalSkin = GUI.skin;
            theme.Ensure(font); GUI.skin = theme.Skin;
            adminPanel.Draw();
            GUI.skin = originalSkin;
            GUI.skin.font = sharedFont;
            if (!visible && syncRecovery != null && syncRecovery.NeedsAttention)
            {
                var previousWarningFont = GUI.skin.font;
                if (font != null) GUI.skin.font = font;
                GUI.Box(new Rect(20, 20, Mathf.Min(650, Screen.width - 40), 30), "联机状态可能已分歧或停滞，按 F8 查看恢复选项。");
                GUI.skin.font = previousWarningFont;
            }
            if (!visible) return;
            GUI.skin = theme.Skin;
            window.width = Mathf.Min(900, Screen.width - 32);
            window.height = Mathf.Min(690, Screen.height - 32);
            if (!windowPlaced) { window.x = (Screen.width - window.width) / 2; window.y = (Screen.height - window.height) / 2; windowPlaced = true; }
            window.x = Mathf.Clamp(window.x, 0, Screen.width - window.width); window.y = Mathf.Clamp(window.y, 0, Screen.height - window.height);
            window = GUILayout.Window(183746, window, DrawWindow, "", theme.MainWindow);
            GUI.skin = originalSkin;
        }
        void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(); GUILayout.Label("农场助手", PanelTheme.Title);
            GUILayout.Label("FARM PROGRESS  ·  实时监控与方向指引", PanelTheme.Muted); GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("×", GUILayout.Width(36), GUILayout.Height(34))) visible = false;
            GUILayout.EndHorizontal(); GUILayout.Space(12);
            bool narrow = window.width < 720;
            string[] tabs = { "农场概览", "待办与地图", "提醒设置", "报告与工具" };
            if (narrow) panelTab = GUILayout.SelectionGrid(panelTab, tabs, 4);
            GUILayout.BeginHorizontal();
            if (!narrow)
            {
                GUILayout.BeginVertical(GUILayout.Width(130));
                for (int tab = 0; tab < tabs.Length; tab++)
                    if (GUILayout.Button(tabs[tab], panelTab == tab ? PanelTheme.NavSelected : PanelTheme.Nav)) { panelTab = tab; panelScroll = Vector2.zero; }
                GUILayout.Space(16); GUILayout.Label("按住 Alt\n可点击屏幕上的\n浮动提示", PanelTheme.Muted);
                GUILayout.EndVertical(); GUILayout.Space(16);
            }
            panelScroll = GUILayout.BeginScrollView(panelScroll, GUILayout.Height(Mathf.Max(180, window.height - (narrow ? 165 : 126))));
            if (panelTab == 0) DrawOverview();
            else if (panelTab == 1) monitor.DrawTasks();
            else if (panelTab == 2) monitor.DrawSettings();
            else DrawTools();
            GUILayout.EndScrollView(); GUILayout.EndHorizontal();
            GUILayout.Space(6); GUILayout.Label("F8 / Esc 返回游戏   ·   设置自动保存   ·   操作面板时暂时停止角色控制", PanelTheme.Muted);
            GUI.DragWindow(new Rect(0, 0, window.width - 65, 66));
        }
        void DrawOverview()
        {
            monitor.DrawOverview();
            if (observed == null) { GUILayout.Label("进入农场后自动加载实时数据。", PanelTheme.Muted); return; }
            PanelTheme.BeginSection("农场状态", observed.IsLocal ? "房主农场" : "访客观测数据");
            GUILayout.Label("等级 " + observed.Experience.Level + "    已解锁地块 " + observed.UnlockedChunks + " / " + observed.Chunks.Length);
            var m = observed.Money;
            GUILayout.Label(string.Format("金币  {0:N0}     钻石  {1:N0}\n奖牌  {2:N0}     票券  {3:N0}", m.Coins, m.Bills, m.Medals, m.Tickets));
            GUILayout.Label(syncRecovery == null ? "" : syncRecovery.Description, PanelTheme.Muted);
            PanelTheme.EndSection();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("查看待办", PanelTheme.Primary)) { panelTab = 1; panelScroll = Vector2.zero; }
            if (GUILayout.Button("调整提醒")) { panelTab = 2; panelScroll = Vector2.zero; }
            if (GUILayout.Button("导出报告")) Export(observed);
            GUILayout.EndHorizontal();
        }
        void DrawTools()
        {
            PanelTheme.BeginSection("报告与农场副本", "F9 可直接导出最新报告；复制操作会新建独立的本地农场。");
            if (observed != null)
            {
                var m = observed.Money;
                GUILayout.Label(string.Format("农场 Lv.{0}  ·  {1}  ·  地块 {2}/{3}", observed.Experience.Level,
                    observed.IsLocal ? "本地农场" : "联机农场", observed.UnlockedChunks, observed.Chunks.Length));
                GUILayout.Label(string.Format("金币 {0:N0}    钻石 {1:N0}\n勋章/缎带 {2:N0}    票券 {3:N0}", m.Coins, m.Bills, m.Medals, m.Tickets));
                if (GUILayout.Button("导出最新进度与地图")) Export(observed);
                bool enabled = GUI.enabled;
                GUI.enabled = enabled && !copying && !observed.IsLocal && StageScript.Instance != null && StageScript.Instance.Loaded;
                if (GUILayout.Button(copying ? "正在保存本地副本…" : "同步当前农场到本地（新建副本）")) CopyFarm();
                GUI.enabled = enabled;
                GUILayout.Label("每次复制新建一个本地农场；不包含房主个人账号解锁。" );
            }
            GUILayout.Label(status);
            if (lastPath != null && GUILayout.Button("打开离线报告")) Application.OpenURL(new Uri(lastPath).AbsoluteUri);
            PanelTheme.EndSection();
            PanelTheme.BeginSection("联机工具");
            if (guestFuel != null)
            {
                bool toggle = GUILayout.Toggle(guestFuel.Enabled, "访客拖拉机免耗油（实验性）");
                if (toggle != guestFuel.Enabled) guestFuel.Enabled = toggle;
                GUILayout.Label(guestFuel.Description);
                GUILayout.Label("外部修改器可能导致分歧；内置数据管理要求全房同版 Mod。" );
            }
            if (syncRecovery != null)
            {
                GUILayout.Space(8);
                GUILayout.Label("联机同步：" + syncRecovery.Description);
                bool enabled = GUI.enabled;
                GUI.enabled = enabled && syncRecovery.CanRejoin && !copying;
                if (GUILayout.Button("退出并重新加入房主（重新下载农场数据）")) syncRecovery.Rejoin();
                GUI.enabled = enabled;
                GUILayout.Label("访客恢复会短暂离开房间；房主须在线。不会覆盖房主存档。" );
                if (syncRecovery.DiagnosticPath != null && GUILayout.Button("打开同步诊断目录"))
                    Application.OpenURL(new Uri(Path.GetDirectoryName(syncRecovery.DiagnosticPath)).AbsoluteUri);
            }
            PanelTheme.EndSection();
        }
    }
}
