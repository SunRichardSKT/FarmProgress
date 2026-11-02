using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Logic.Farm;
using UnityEngine;

namespace FarmProgress
{
    public sealed class GuestFuelAssist : IDisposable
    {
        static GuestFuelAssist active;
        readonly ConfigEntry<bool> enabled;
        readonly ManualLogSource logger;
        readonly Harmony patches;
        LocalPlayerScript player;
        uint shadowFuel;
        bool filling, sending, supported, failed;
        public bool Enabled { get { return enabled.Value; } set { enabled.Value = value; Update(); } }
        public string Description { get { return !supported ? "当前版本不支持" : failed ? "发送发生错误，已停用；请重进房间" : enabled.Value ? "访客拖拉机免耗油：多格操作按原顺序拆分发送（待双人验证）" : "访客拖拉机免耗油已关闭"; } }
        public GuestFuelAssist(ConfigFile config, ManualLogSource logger)
        {
            this.logger = logger; active = this;
            enabled = config.Bind("GuestTractor", "NoFuelCost", false, "Guest-only experimental tractor fuel assist; splits multi-tile commands into vanilla single-tile commands. Disable external trainer first.");
            supported = typeof(FarmData).Module.ModuleVersionId == new Guid("db6d818f-4e71-4ce2-8125-59818f68fab0");
            if (!supported) return;
            patches = new Harmony("local.farmtogether.progress.guestfuel");
            try
            {
                patches.Patch(AccessTools.Method(typeof(FWFNetworkLogicBehaviour), "SendPerformWork"), prefix: new HarmonyMethod(typeof(GuestFuelAssist), "BeforeSend"));
                patches.Patch(AccessTools.Method(typeof(PlayerScript), "PayFuel"), prefix: new HarmonyMethod(typeof(GuestFuelAssist), "BeforePay"));
                patches.Patch(AccessTools.Method(typeof(PlayerScript), "FillFuel"), prefix: new HarmonyMethod(typeof(GuestFuelAssist), "BeforeFill"));
            }
            catch (Exception ex) { supported = false; patches.UnpatchSelf(); logger.LogError(ex); }
        }
        static LocalPlayerScript Guest()
        {
            var stage = StageScript.Instance;
            return stage != null && stage.Loaded && stage.IsOnline && !stage.IsHost && stage.FarmData != null && !stage.FarmData.IsLocal ? stage.LocalPlayer : null;
        }
        bool IsAssisted(PlayerScript candidate) { return supported && enabled.Value && !failed && player != null && ReferenceEquals(player, candidate) && ReferenceEquals(Guest(), player); }
        public void Update()
        {
            var current = Guest();
            if (player != null && (!ReferenceEquals(current, player) || !enabled.Value || !supported || failed)) Detach();
            if (!supported || !enabled.Value || failed || current == null) return;
            if (player == null) { player = current; shadowFuel = current.Fuel; }
            if (player.Fuel < player.MaxFuel)
            {
                filling = true;
                try { player.FillFuel(player.MaxFuel - player.Fuel); } finally { filling = false; }
            }
        }
        void Detach()
        {
            // Fuel differences are kept out of farm simulation by the single-tile command path.
            if (player != null)
            {
                AccessTools.Field(typeof(PlayerScript), "fuel").SetValue(player, shadowFuel);
                filling = true;
                try { player.FillFuel(0); } finally { filling = false; }
            }
            player = null;
        }
        static void BeforePay(PlayerScript __instance, uint value)
        { if (active != null && active.IsAssisted(__instance)) active.shadowFuel = GuestFuelPlan.Pay(active.shadowFuel, value); }
        static void BeforeFill(PlayerScript __instance, uint amount)
        { if (active != null && !active.filling && active.IsAssisted(__instance)) active.shadowFuel = GuestFuelPlan.Refill(active.shadowFuel, amount, __instance.MaxFuel); }
        static bool BeforeSend(FWFNetworkLogicBehaviour __instance, PlayerScript player, WorkType workType, FarmItemDefinition definition, List<FarmTile> workTiles, int rotation, bool usingVehicle)
        {
            var assist = active;
            if (assist == null || assist.sending || !usingVehicle || !assist.IsAssisted(player) || workTiles.Count <= 1) return true;
            bool structural = workType == WorkType.MoveFence || workType == WorkType.MassRecycle;
            var ranges = GuestFuelPlan.Ranges(workTiles.Count, workType == WorkType.Move, structural);
            if (ranges.Count <= 1) return true;
            var tiles = new List<FarmTile>(workTiles);
            assist.sending = true;
            try
            {
                foreach (var range in ranges)
                    __instance.SendPerformWork(player, workType, definition, tiles.GetRange(range[0], range[1]), rotation, usingVehicle);
            }
            catch (Exception ex)
            {
                // Do not replay an already partially sent batch or fall back and duplicate actions.
                assist.failed = true; assist.logger.LogError(ex);
            }
            finally { assist.sending = false; }
            return false;
        }
        public void Dispose() { Detach(); if (patches != null) patches.UnpatchSelf(); if (active == this) active = null; }
    }
}
