// Runs the production host/client protocol with an in-memory UNet substitute.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FarmProgress;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;
namespace BepInEx { public static class Paths { public static string BepInExRootPath; } }
namespace BepInEx.Configuration
{
    public class ConfigEntry<T> { public T Value; }
    public class ConfigFile { public ConfigEntry<T> Bind<T>(string a, string b, T c, string d) { return new ConfigEntry<T> { Value = c }; } }
}
namespace BepInEx.Logging { public class ManualLogSource { public void LogWarning(object s) {} } }
namespace HarmonyLib
{
    public class Harmony { public Harmony(string s) {} public void Patch(MethodInfo m, HarmonyMethod postfix = null) {} public void UnpatchSelf() {} }
    public class HarmonyMethod { public HarmonyMethod(Type t, string n) {} }
    public static class AccessTools { public static MethodInfo Method(Type t, string s) { return null; } }
}
namespace UnityEngine { public static class Time { public static float realtimeSinceStartup; } }
namespace UnityEngine.Networking
{
    public class MessageBase { public virtual void Serialize(NetworkWriter w) {} public virtual void Deserialize(NetworkReader r) {} }
    public class NetworkWriter { public void Write(string s) {} }
    public class NetworkReader { public string ReadString() { return ""; } }
    public class NetworkMessage
    {
        public NetworkConnection conn; public string Json;
        public T ReadMessage<T>() where T : MessageBase, new() { return (T)(object)new AdminWireMessage { Json = Json }; }
    }
    public class NetworkConnection
    {
        public bool isReady = true;
        public readonly List<JObject> Sent = new List<JObject>();
        public bool Send(short id, MessageBase m) { Sent.Add(JObject.Parse(((AdminWireMessage)m).Json)); return true; }
    }
    public class NetworkClient
    {
        public bool isConnected = true; public NetworkConnection connection = new NetworkConnection();
        public void RegisterHandler(short n, Action<NetworkMessage> a) {} public void UnregisterHandler(short n) {}
    }
    public static class NetworkServer
    {
        public static bool active = true; public static List<NetworkConnection> connections = new List<NetworkConnection>();
        public static void RegisterHandler(short n, Action<NetworkMessage> a) {} public static void UnregisterHandler(short n) {}
    }
}
namespace Logic.Farm { public class FarmData { public long Balance = 100; public byte[] ToBinary() { return BitConverter.GetBytes(Balance); } } }
public class StageScript { public static StageScript Instance; public bool IsHost = true, IsOnline = true; }
public class Gamer { public string UniqueId; }
public class MilkNetworkPlayerBehaviour { public NetworkConnection connectionToClient; public bool isLocalPlayer; public Gamer Gamer; }
public class MilkNetworkManager
{
    public static MilkNetworkManager singleton;
    public NetworkClient client = new NetworkClient();
    public List<MilkNetworkPlayerBehaviour> PlayerList = new List<MilkNetworkPlayerBehaviour>();
}
public class FWFNetworkLogic {}
namespace FarmProgress
{
    public static class AdminChanges
    {
        public static string Read(Logic.Farm.FarmData f, string key) { return f.Balance.ToString(); }
        public static JObject Plan(Logic.Farm.FarmData f, string k, long v, bool add)
        { return new JObject { { "key", k }, { "before", f.Balance.ToString() }, { "after", (add ? checked(f.Balance + v) : v).ToString() } }; }
        public static void Apply(Logic.Farm.FarmData f, JObject p)
        { if (f.Balance.ToString() != (string)p["before"]) throw new Exception("precondition mismatch"); f.Balance = long.Parse((string)p["after"]); }
    }
}
class AdminNetworkTests
{
    static int checks;
    static void Check(bool b, string s) { if (!b) throw new Exception(s); checks++; }
    static void Receive(AdminNetwork service, NetworkConnection connection, JObject body, bool server = true)
    { typeof(AdminNetwork).GetMethod(server ? "ReceiveServer" : "ReceiveClient", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(service, new object[] { new NetworkMessage { conn = connection, Json = body.ToString(Formatting.None) } }); }
    static JObject Hello(string session) { return new JObject { { "kind", "hello" }, { "version", AdminNetwork.Version }, { "supported", true }, { "healthy", true }, { "session", session } }; }
    static string Session(AdminNetwork service) { return (string)typeof(AdminNetwork).GetField("session", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service); }
    static JObject Request(string session, string id, string expected = "100")
    { return new JObject { { "kind", "request" }, { "session", session }, { "id", id }, { "key", "money|Coins" }, { "value", "5" }, { "add", true }, { "expected", expected } }; }
    static JObject Ack(string session, string id, bool ok) { return new JObject { { "kind", "ack" }, { "session", session }, { "id", id }, { "ok", ok } }; }
    static AdminNetwork New()
    {
        var n = new AdminNetwork(new BepInEx.Configuration.ConfigFile(), new BepInEx.Logging.ManualLogSource());
        typeof(AdminNetwork).GetProperty("Supported").GetSetMethod(true).Invoke(n, new object[] { true }); return n;
    }
    static int Main(string[] args)
    { try { Run(args); return 0; } catch (Exception e) { Console.WriteLine("FAIL: " + e.Message); return 1; } }
    static void Run(string[] args)
    {
        BepInEx.Paths.BepInExRootPath = args[0]; Directory.CreateDirectory(args[0]);
        StageScript.Instance = new StageScript(); MilkNetworkManager.singleton = new MilkNetworkManager();
        var a = new NetworkConnection(); var b = new NetworkConnection();
        NetworkServer.connections.Add(a); NetworkServer.connections.Add(b);
        MilkNetworkManager.singleton.PlayerList.Add(new MilkNetworkPlayerBehaviour { connectionToClient = a, Gamer = new Gamer { UniqueId = "visitor-a" } });
        MilkNetworkManager.singleton.PlayerList.Add(new MilkNetworkPlayerBehaviour { connectionToClient = b, Gamer = new Gamer { UniqueId = "visitor-b" } });
        var farm = new Logic.Farm.FarmData(); var host = New(); host.Update(farm); string session = Session(host);
        Check(host.AutoAuthorize, "all visitors authorized by default");
        Receive(host, a, Hello(null)); Receive(host, b, Hello(null));
        string rejectedId = Guid.NewGuid().ToString("N"); Receive(host, a, Request(session, rejectedId));
        Check(farm.Balance == 100 && !host.Busy, "session handshake required before mutation");
        Receive(host, a, Hello(session)); Receive(host, b, Hello(session));
        string id = Guid.NewGuid().ToString("N"); Receive(host, a, Request(session, id));
        Check(farm.Balance == 105 && host.Busy, "host applies request and waits for both peers");
        Receive(host, a, Request(session, id)); Check(farm.Balance == 105, "pending duplicate not reapplied");
        Receive(host, a, Ack(session, id, true)); Check(host.Busy, "one ack is insufficient");
        Receive(host, b, Ack(session, id, true)); Check(!host.Busy, "all acks complete transaction");
        Receive(host, a, Request(session, id)); Check(farm.Balance == 105, "completed request replay not reapplied");
        Receive(host, a, Request("old-session", Guid.NewGuid().ToString("N"), "105")); Check(farm.Balance == 105, "old room request rejected");
        Receive(host, a, Request(session, Guid.NewGuid().ToString("N"), "100")); Check(farm.Balance == 105, "stale preview rejected");
        host.AutoAuthorize = false; Receive(host, a, Request(session, Guid.NewGuid().ToString("N"), "105")); Check(farm.Balance == 105, "disabled authorization enforced by host"); host.AutoAuthorize = true;
        var native = new NetworkConnection(); NetworkServer.connections.Add(native);
        Receive(host, a, Request(session, Guid.NewGuid().ToString("N"), "105")); Check(farm.Balance == 105, "unmodded/loading third peer blocks editing"); NetworkServer.connections.Remove(native);
        string second = Guid.NewGuid().ToString("N"); Receive(host, a, Request(session, second, "105"));
        Check(farm.Balance == 110, "subsequent transaction applies");
        Receive(host, b, Ack(session, second, false));
        Receive(host, a, Request(session, Guid.NewGuid().ToString("N"), "110")); Check(farm.Balance == 110 && !host.Busy, "negative ack pauses future modifications");
        var loaded = new Logic.Farm.FarmData(); host.Update(loaded); string newSession = Session(host);
        Check(newSession != session, "reload rotates session token");
        Receive(host, a, Hello(newSession)); Receive(host, b, Hello(newSession));
        Receive(host, a, Request(newSession, Guid.NewGuid().ToString("N"))); UnityEngine.Time.realtimeSinceStartup = 13; host.Update(loaded);
        Check(!host.Busy && host.Status.Contains("待核实") || !host.Busy && host.Status.Contains("超时"), "ack timeout leaves uncertain result without replay");
        Check(Directory.GetFiles(args[0], "*.data", SearchOption.AllDirectories).Length == 2, "one backup per loaded farm session");
        StageScript.Instance.IsHost = false;
        var guest = New(); var guestFarm = new Logic.Farm.FarmData(); guest.Update(guestFarm);
        Receive(guest, a, new JObject { { "kind", "welcome" }, { "session", newSession }, { "version", AdminNetwork.Version }, { "allowed", true }, { "healthy", true } }, false);
        var change = AdminChanges.Plan(guestFarm, "money|Coins", 5, true);
        var apply = new JObject { { "kind", "apply" }, { "session", newSession }, { "id", "single-apply" }, { "change", change } };
        Receive(guest, a, apply, false); Receive(guest, a, apply, false);
        Check(guestFarm.Balance == 105, "guest apply is idempotent");
        apply["id"] = "mismatch"; Receive(guest, a, apply, false);
        Check(guestFarm.Balance == 105 && guest.Status.Contains("失败"), "guest precondition mismatch fails without overwriting state");
        Console.WriteLine(checks + " administration protocol checks passed (simulated transport, not live Steam)");
    }
}
