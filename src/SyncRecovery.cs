using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Logic.Farm;
using Logic.Mode;
using Milkstone.Game;
using Milkstone.Game.Network;
using Milkstone.Game.Steam.Network;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace FarmProgress
{
    public sealed class SyncRecovery : IDisposable
    {
        static SyncRecovery active;
        readonly ManualLogSource logger;
        Harmony patches;
        FarmData farm;
        SyncHealth health;
        bool repairPending, warned, supported;
        float repairStarted;
        string diagnosticPath, failure;
        public bool NeedsAttention { get { return failure != null || health != null && (health.Mismatches > 0 || health.Stalled(Time.realtimeSinceStartup)); } }
        public bool CanRejoin { get { return supported && IsGuest() && !repairPending; } }
        public string Description { get { return failure ?? (repairPending ? "正在通过游戏原生流程重新加入房主…" : health == null ? "联机恢复仅用于访客。" : health.Describe(Time.realtimeSinceStartup)); } }
        public string DiagnosticPath { get { return diagnosticPath; } }
        static bool IsGuest()
        {
            var stage = StageScript.Instance;
            return stage != null && stage.Loaded && stage.IsOnline && !stage.IsHost && stage.FarmData != null && !stage.FarmData.IsLocal;
        }
        public SyncRecovery(ManualLogSource logger)
        {
            this.logger = logger; active = this;
            // All private entry points are verified against this installed build.
            supported = typeof(FarmData).Module.ModuleVersionId == new Guid("db6d818f-4e71-4ce2-8125-59818f68fab0");
            if (!supported) { failure = "游戏版本改变，自动校验监测与重新加入入口未启用。"; return; }
            try
            {
                patches = new Harmony("local.farmtogether.progress.sync");
                patches.Patch(AccessTools.Method(typeof(FarmScript), "SanityCheck", new[] { typeof(long) }),
                    prefix: new HarmonyMethod(typeof(SyncRecovery), "BeforeSanityCheck"));
                patches.Patch(AccessTools.Method(typeof(FWFNetworkLogicBehaviour), "_RpcTick"),
                    postfix: new HarmonyMethod(typeof(SyncRecovery), "AfterTick"));
                logger.LogInfo("Sync recovery monitor enabled; original sanity checks and simulation remain active.");
            }
            catch (Exception ex) { failure = "同步监测初始化失败，请查看日志。"; supported = false; if (patches != null) patches.UnpatchSelf(); logger.LogError(ex); }
        }
        public void Update()
        {
            var current = IsGuest() ? StageScript.Instance.FarmData : null;
            if (!ReferenceEquals(current, farm))
            {
                farm = current; health = current == null ? null : new SyncHealth(Time.realtimeSinceStartup); warned = false;
                if (current != null)
                {
                    repairPending = false;
                    diagnosticPath = Path.Combine(Path.Combine(Paths.BepInExRootPath, "SyncDiagnostics"), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".jsonl");
                    Write("session-start", null);
                }
            }
            if (repairPending && Time.realtimeSinceStartup - repairStarted > 60f)
            { repairPending = false; failure = "重新加入尚未完成；请使用游戏好友列表手动加入。"; }
            if (health != null && health.Stalled(Time.realtimeSinceStartup) && !warned)
            { warned = true; Write("clock-stalled", null); logger.LogWarning("Farm clock stalled; use F8 recovery button or rejoin from friends list."); }
        }
        static void BeforeSanityCheck(FarmScript __instance, long randSeed)
        {
            if (active == null || !IsGuest()) return;
            // Observe before vanilla reseeds the RNG; never suppress or rewrite its result.
            try
            {
                active.Update();
                long local = __instance.FarmData.FarmRandom.CurrentSeed;
                if (active.health.Check(local, randSeed))
                {
                    active.Write("rng-mismatch", new JObject { { "clientSeed", local.ToString() }, { "hostSeed", randSeed.ToString() } });
                    active.logger.LogWarning("Multiplayer state diverged before vanilla correction. F8: rejoin host for full farm reload.");
                }
            }
            catch (Exception ex) { active.logger.LogError("Sync observation failed: " + ex.Message); }
        }
        static void AfterTick()
        {
            if (active == null || !IsGuest()) return;
            try { active.Update(); if (active.health != null) active.health.Tick(Time.realtimeSinceStartup); }
            catch (Exception ex) { active.logger.LogError("Tick observation failed: " + ex.Message); }
        }
        void Write(string kind, JObject extra)
        {
            if (diagnosticPath == null || farm == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(diagnosticPath));
                var m = farm.Money;
                var row = new JObject { { "at", DateTimeOffset.Now.ToString("o") }, { "event", kind },
                    { "worldTicks", farm.WorldTicks }, { "level", farm.Experience.Level },
                    { "coins", m.Coins.ToString() }, { "diamonds", m.Bills.ToString() },
                    { "medals", m.Medals.ToString() }, { "tickets", m.Tickets.ToString() },
                    { "buildings", farm.Buildings.Count }, { "quests", farm.ActiveQuests.Count },
                    { "mismatches", health == null ? 0 : health.Mismatches }, { "detail", extra } };
                File.AppendAllText(diagnosticPath, row.ToString(Formatting.None) + Environment.NewLine);
            }
            catch (Exception ex) { logger.LogWarning("Sync diagnostic write failed: " + ex.Message); }
        }
        public void Rejoin()
        {
            if (!CanRejoin) return;
            try
            {
                var session = MilkNetworkManager.singleton == null ? null : MilkNetworkManager.singleton.Session as SteamSession;
                var invite = typeof(BaseGameInit).GetField("inviteAccepted", BindingFlags.Instance | BindingFlags.NonPublic);
                var gamer = PlatformInterface.Instance.GetGamer(FarmManager.Instance.PlayerIndex);
                if (session == null || invite == null || gamer == null) throw new InvalidOperationException("无法识别当前 Steam 房间，请通过好友列表手动重进。");
                Write("manual-rejoin", null);
                // Same invite payload and entry point used by the game's Steam join handler.
                invite.SetValue(GameInit.Instance, new InviteAcceptedArgs(gamer, session.Lobby));
                repairPending = true; repairStarted = Time.realtimeSinceStartup; failure = null;
                GameInit.Instance.JoinInvited();
            }
            catch (Exception ex) { repairPending = false; failure = "重新加入失败：" + ex.Message; logger.LogError(ex); }
        }
        public void Dispose() { if (patches != null) patches.UnpatchSelf(); if (active == this) active = null; }
    }
}
