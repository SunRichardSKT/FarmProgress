using System;
using FarmProgress;
namespace UnityEngine
{
    public enum CursorLockMode { None, Locked, Confined }
    public static class Cursor { public static bool visible; public static CursorLockMode lockState; }
    public static class Time { public static float realtimeSinceStartup; }
    public static class Input { public static int Resets; public static void ResetInputAxes() { Resets++; } }
}
public class MilkThirdPersonCamera { public bool EnableInput = true; }
public class MilkThirdPersonController { public bool EnableInput = true; public MilkThirdPersonCamera ThirdPersonCamera = new MilkThirdPersonCamera(); }
public class GameInit { public static GameInit Instance = new GameInit(); public bool ShowCursor; public UnityEngine.CursorLockMode CursorLockMode; }
public class StageScript { public static StageScript Instance; public LocalPlayerScript LocalPlayer; }
public class LocalPlayerScript
{
    public enum BusyStates { Pause, Shop, Chat, Dialog }
    public bool IsBusy; public BusyStates BusyState; public int BusyCalls, ResumeCalls;
    public MilkThirdPersonController Controller = new MilkThirdPersonController();
    public event Action<bool, BusyStates> OnBusyStateChanged;
    public void Busy(BusyStates state) { IsBusy = true; BusyState = state; BusyCalls++; if (OnBusyStateChanged != null) OnBusyStateChanged(true, state); }
    public void Resume() { IsBusy = false; ResumeCalls++; if (OnBusyStateChanged != null) OnBusyStateChanged(false, BusyState); }
}
class InputCaptureTests
{
    static int count;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
    static LocalPlayerScript Player()
    {
        var player = new LocalPlayerScript(); StageScript.Instance = new StageScript { LocalPlayer = player };
        GameInit.Instance = new GameInit { ShowCursor = false, CursorLockMode = UnityEngine.CursorLockMode.Locked };
        UnityEngine.Cursor.visible = false; UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.Locked;
        UnityEngine.Time.realtimeSinceStartup = 0; return player;
    }
    static void Close(ModInputCapture capture, LocalPlayerScript player)
    { capture.Tick(false, player); UnityEngine.Time.realtimeSinceStartup += 0.2f; capture.Tick(false, player); }
    static int Main()
    {
        try { Run(); Console.WriteLine(count + " cursor and modal-input checks passed (simulated game state)"); return 0; }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static void Run()
    {
        var player = Player();
        using (var capture = new ModInputCapture())
        {
            capture.Tick(true, player);
            Check(player.IsBusy && ModInputCapture.Owns(player), "opening F8 captures native gameplay actions");
            Check(!player.Controller.EnableInput && !player.Controller.ThirdPersonCamera.EnableInput, "movement and camera input blocked while editing");
            Check(UnityEngine.Cursor.visible && UnityEngine.Cursor.lockState == UnityEngine.CursorLockMode.None && GameInit.Instance.ShowCursor, "cursor actually released and native cursor policy updated");
            capture.Tick(true, player); Check(player.BusyCalls == 1, "holding menu open does not repeatedly raise busy event");
            capture.Tick(false, player); UnityEngine.Time.realtimeSinceStartup = 0.05f; capture.Tick(false, player);
            Check(player.IsBusy, "close-click is absorbed before gameplay resumes");
            UnityEngine.Time.realtimeSinceStartup = 0.2f; capture.Tick(false, player);
            Check(!player.IsBusy && player.ResumeCalls == 1 && !ModInputCapture.Owns(player), "closing restores player actions once");
            Check(player.Controller.EnableInput && player.Controller.ThirdPersonCamera.EnableInput, "closing restores movement and camera");
            Check(!UnityEngine.Cursor.visible && UnityEngine.Cursor.lockState == UnityEngine.CursorLockMode.Locked && !GameInit.Instance.ShowCursor, "closing restores original cursor visibility and lock");
            capture.Tick(true, player); capture.Tick(false, player); capture.Tick(true, player);
            UnityEngine.Time.realtimeSinceStartup += 1; capture.Tick(true, player);
            Check(player.IsBusy && ModInputCapture.Owns(player), "reopening panel or holding Alt cancels pending release");
        }
        Check(!player.IsBusy && player.Controller.EnableInput, "disposal immediately releases owned controls");
        player = Player(); player.Busy(LocalPlayerScript.BusyStates.Shop);
        player.Controller.EnableInput = player.Controller.ThirdPersonCamera.EnableInput = false;
        GameInit.Instance.ShowCursor = UnityEngine.Cursor.visible = true; GameInit.Instance.CursorLockMode = UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.Confined;
        using (var capture = new ModInputCapture())
        {
            capture.Tick(true, player); Check(!ModInputCapture.Owns(player) && player.BusyState == LocalPlayerScript.BusyStates.Shop, "existing shop state not overwritten");
            Close(capture, player);
            Check(player.IsBusy && player.ResumeCalls == 0 && UnityEngine.Cursor.lockState == UnityEngine.CursorLockMode.Confined, "closing F8 leaves preexisting shop open");
        }
        player = Player();
        using (var capture = new ModInputCapture())
        {
            capture.Tick(true, player); player.Busy(LocalPlayerScript.BusyStates.Dialog); Close(capture, player);
            Check(player.IsBusy && player.ResumeCalls == 0, "external dialog taking ownership is not accidentally resumed");
        }
        player = Player();
        using (var capture = new ModInputCapture())
        {
            capture.Tick(true, player);
            StageScript.Instance = null; GameInit.Instance.ShowCursor = true; GameInit.Instance.CursorLockMode = UnityEngine.CursorLockMode.Confined;
            UnityEngine.Cursor.visible = true; UnityEngine.Cursor.lockState = UnityEngine.CursorLockMode.Confined;
            capture.Tick(false, null);
            Check(GameInit.Instance.ShowCursor && UnityEngine.Cursor.lockState == UnityEngine.CursorLockMode.Confined, "leaving farm preserves main-menu cursor state");
        }
        player = Player(); player.Controller.EnableInput = player.Controller.ThirdPersonCamera.EnableInput = false;
        using (var capture = new ModInputCapture()) { capture.Tick(true, player); Close(capture, player); }
        Check(!player.Controller.EnableInput && !player.Controller.ThirdPersonCamera.EnableInput, "preexisting disabled input is not enabled by the mod");
        player = Player(); player.Busy(LocalPlayerScript.BusyStates.Shop);
        player.Controller.EnableInput = player.Controller.ThirdPersonCamera.EnableInput = false;
        using (var capture = new ModInputCapture())
        {
            capture.Tick(true, player); player.Resume();
            player.Controller.EnableInput = player.Controller.ThirdPersonCamera.EnableInput = true;
            GameInit.Instance.ShowCursor = false; GameInit.Instance.CursorLockMode = UnityEngine.CursorLockMode.Locked;
            capture.Tick(true, player); Close(capture, player);
            Check(!player.IsBusy && player.Controller.EnableInput && player.Controller.ThirdPersonCamera.EnableInput, "native menu ending while F8 is open restores fresh gameplay baseline");
        }
    }
}
