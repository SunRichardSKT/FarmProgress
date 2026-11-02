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
    [BepInPlugin("local.farmtogether.progress", "Farm Progress", "1.6.2")]
    [BepInProcess("FarmTogether.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        ConfigEntry<KeyCode> panelKey, exportKey;
        ConfigEntry<bool> autoExport, rawExport;
        FarmData observed;
        float readyAt;
        bool exported, visible, copying;
        string status = "加入农场后按 F9 导出报告。", lastPath;
        Rect window = new Rect(25, 70, 530, 385);
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
            if (Config.Bind("Multiplayer", "RecoveryEnabled", true, "Monitor guest divergence and show native rejoin recovery button").Value)
                syncRecovery = new SyncRecovery(Logger);
            guestFuel = new GuestFuelAssist(Config, Logger);
            monitor = new LiveMonitor(Config);
            monitor.OpenDetails = targetFilter => { visible = true; panelTab = 0; panelScroll = Vector2.zero; };
            admin = new AdminNetwork(Config, Logger);
            admin.SyncBlocked = () => syncRecovery != null && syncRecovery.NeedsAttention;
            adminPanel = new AdminPanel(admin);
            Logger.LogInfo("Farm Progress 1.6.2 loaded. Camera-relative watering and feeding direction guides enabled.");
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
            if (Input.GetKeyDown(panelKey.Value)) visible = !visible;
            var stage = StageScript.Instance;
            var farm = stage != null && stage.Loaded ? stage.FarmData : null;
            monitor.Update(farm);
            admin.Update(farm);
            adminPanel.Update(farm);
            if (!ReferenceEquals(farm, observed))
            {
                observed = farm; readyAt = Time.realtimeSinceStartup + 8f; exported = false;
                lastPath = null;
                status = farm == null ? "等待农场加载完成。" : "农场已加载，准备自动导出。";
            }
            if (farm == null) return;
            if (Input.GetKeyDown(exportKey.Value) || (autoExport.Value && !exported && Time.realtimeSinceStartup >= readyAt))
            { exported = true; Export(farm); }
        }
        void OnDestroy() { if (admin != null) admin.Dispose(); if (guestFuel != null) guestFuel.Dispose(); if (syncRecovery != null) syncRecovery.Dispose(); }
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
            adminPanel.Draw();
            GUI.skin.font = sharedFont;
            if (!visible && syncRecovery != null && syncRecovery.NeedsAttention)
            {
                var previousWarningFont = GUI.skin.font;
                if (font != null) GUI.skin.font = font;
                GUI.Box(new Rect(20, 20, Mathf.Min(650, Screen.width - 40), 30), "联机状态可能已分歧或停滞，按 F8 查看恢复选项。");
                GUI.skin.font = previousWarningFont;
            }
            if (!visible) return;
            var previous = GUI.skin.font;
            if (font != null) GUI.skin.font = font;
            window.width = Mathf.Min(530, Screen.width - 20);
            window.height = Mathf.Min(790, Screen.height - 90);
            window = GUILayout.Window(183746, window, DrawWindow, "农场进度 · Farm Progress");
            GUI.skin.font = previous;
        }
        void DrawWindow(int id)
        {
            panelScroll = GUILayout.BeginScrollView(panelScroll, GUILayout.Height(Mathf.Max(200, window.height - 40)));
            panelTab = GUILayout.SelectionGrid(panelTab, new[] { "实时监控", "报告与联机工具" }, 2);
            if (panelTab == 0)
            {
                monitor.DrawPanel();
                GUILayout.EndScrollView();
                GUI.DragWindow(new Rect(0, 0, 10000, 25));
                return;
            }
            GUILayout.Label("F8 显示/隐藏  ·  F9 导出  ·  加入后自动导出一次");
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
            if (lastPath != null && GUILayout.Button("打开离线报告")) Application.OpenURL(new Uri(lastPath).AbsoluteUri);
            if (GUILayout.Button("关闭面板")) visible = false;
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 25));
        }
    }
}
