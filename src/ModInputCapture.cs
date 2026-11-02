using System;
using UnityEngine;

namespace FarmProgress
{
    // Uses native busy state so opening a mod panel does not pause farm simulation.
    public sealed class ModInputCapture : IDisposable
    {
        static ModInputCapture current;
        LocalPlayerScript player;
        MilkThirdPersonController controller;
        MilkThirdPersonCamera camera;
        bool ownedBusy, writingBusy, initiallyBusy, refreshBaseline, oldInput, oldCamera, oldShowCursor, oldVisible;
        CursorLockMode oldLock, oldActualLock;
        float releaseAt = -1;
        public ModInputCapture() { current = this; }
        public static bool Owns(LocalPlayerScript candidate) { return current != null && current.ownedBusy && ReferenceEquals(current.player, candidate); }
        public void Tick(bool wanted, LocalPlayerScript candidate)
        {
            if (!ReferenceEquals(player, candidate)) Release();
            if (!wanted || candidate == null)
            {
                if (player == null) return;
                if (candidate == null) { Release(); return; }
                if (releaseAt < 0) releaseAt = Time.realtimeSinceStartup + 0.12f;
                if (Time.realtimeSinceStartup >= releaseAt) Release();
                return;
            }
            releaseAt = -1;
            if (player == null)
            {
                player = candidate; initiallyBusy = player.IsBusy;
                controller = player.Controller; camera = controller == null ? null : controller.ThirdPersonCamera;
                oldInput = controller != null && controller.EnableInput; oldCamera = camera != null && camera.EnableInput;
                oldVisible = Cursor.visible; oldActualLock = Cursor.lockState;
                var game = GameInit.Instance;
                oldShowCursor = game == null ? oldVisible : game.ShowCursor;
                oldLock = game == null ? oldActualLock : game.CursorLockMode;
                player.OnBusyStateChanged += BusyChanged;
            }
            if (!player.IsBusy)
            {
                if (refreshBaseline)
                {
                    initiallyBusy = false; refreshBaseline = false;
                    oldInput = controller != null && controller.EnableInput; oldCamera = camera != null && camera.EnableInput;
                    if (GameInit.Instance != null) { oldShowCursor = GameInit.Instance.ShowCursor; oldLock = GameInit.Instance.CursorLockMode; }
                    oldVisible = oldShowCursor; oldActualLock = oldLock;
                }
                ownedBusy = true; writingBusy = true;
                try { player.Busy(LocalPlayerScript.BusyStates.Dialog); }
                finally { writingBusy = false; }
                Input.ResetInputAxes();
            }
            if (controller != null) controller.EnableInput = false;
            if (camera != null) camera.EnableInput = false;
            if (GameInit.Instance != null) { GameInit.Instance.ShowCursor = true; GameInit.Instance.CursorLockMode = CursorLockMode.None; }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        void BusyChanged(bool busy, LocalPlayerScript.BusyStates state)
        {
            if (!writingBusy && busy) ownedBusy = false;
            if (!busy && initiallyBusy) refreshBaseline = true;
        }
        void Release()
        {
            if (player == null) { player = null; ownedBusy = false; refreshBaseline = false; controller = null; camera = null; releaseAt = -1; return; }
            var stage = StageScript.Instance;
            bool samePlayer = stage != null && ReferenceEquals(stage.LocalPlayer, player);
            bool otherMenu = player.IsBusy && !ownedBusy && !initiallyBusy;
            player.OnBusyStateChanged -= BusyChanged;
            if (ownedBusy && player.IsBusy && player.BusyState == LocalPlayerScript.BusyStates.Dialog) player.Resume();
            if (samePlayer && !otherMenu)
            {
                if (controller != null) controller.EnableInput = oldInput;
                if (camera != null) camera.EnableInput = oldCamera;
                if (GameInit.Instance != null) { GameInit.Instance.ShowCursor = oldShowCursor; GameInit.Instance.CursorLockMode = oldLock; }
                Cursor.lockState = oldActualLock; Cursor.visible = oldVisible;
            }
            player = null; controller = null; camera = null; ownedBusy = false; refreshBaseline = false; releaseAt = -1;
        }
        public void Dispose() { Release(); if (ReferenceEquals(current, this)) current = null; }
    }
}
