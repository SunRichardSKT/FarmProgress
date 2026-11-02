using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Logic.Farm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace FarmProgress
{
    public sealed class AdminWireMessage : MessageBase
    {
        public string Json;
        public override void Serialize(NetworkWriter writer) { writer.Write(Json); }
        public override void Deserialize(NetworkReader reader) { Json = reader.ReadString(); }
    }

    public sealed class AdminNetwork : IDisposable
    {
        const short MessageId = 19751;
        public const string Version = "1.7.0";
        static AdminNetwork active;
        readonly ConfigEntry<bool> autoAuthorize;
        readonly ManualLogSource log;
        readonly Harmony harmony;
        readonly Dictionary<NetworkConnection, float> peers = new Dictionary<NetworkConnection, float>();
        readonly Dictionary<string, string> results = new Dictionary<string, string>();
        readonly HashSet<NetworkConnection> awaiting = new HashSet<NetworkConnection>();
        readonly HashSet<string> applied = new HashSet<string>();
        FarmData farm;
        string session, pendingId, pendingActor, clientRequest, auditPath;
        JObject pendingChange;
        NetworkConnection requester;
        float nextHello, pendingAt, requestedAt;
        bool fault, backedUp, disposed;
        NetworkClient registeredClient;
        public string Status { get; private set; }
        public bool Supported { get; private set; }
        public bool Host { get { return StageScript.Instance != null && StageScript.Instance.IsHost; } }
        public bool Busy { get { return pendingId != null || clientRequest != null; } }
        public bool AutoAuthorize { get { return autoAuthorize.Value; } set { autoAuthorize.Value = value; } }
        public Func<bool> SyncBlocked;
        public AdminNetwork(ConfigFile config, ManualLogSource logger)
        {
            active = this; log = logger;
            autoAuthorize = config.Bind("Administration", "AutoAuthorizeAllVisitors", true, "Automatically authorize every visitor; persists across game and room restarts");
            Supported = typeof(FarmData).Module.ModuleVersionId == new Guid("db6d818f-4e71-4ce2-8125-59818f68fab0");
            Status = "等待农场加载";
            harmony = new Harmony("local.farmtogether.progress.admin");
            try
            {
                harmony.Patch(AccessTools.Method(typeof(FWFNetworkLogic), "Client_OnStartClient"), postfix: new HarmonyMethod(typeof(AdminNetwork), "ClientStarted"));
                harmony.Patch(AccessTools.Method(typeof(FWFNetworkLogic), "Server_OnServerReady"), postfix: new HarmonyMethod(typeof(AdminNetwork), "ServerReady"));
            }
            catch (Exception ex) { Supported = false; harmony.UnpatchSelf(); log.LogWarning("Administration unavailable: " + ex.Message); }
        }
        static void ClientStarted(NetworkClient client) { if (active != null) active.RegisterClient(client); }
        static void ServerReady() { if (active != null) NetworkServer.RegisterHandler(MessageId, active.ReceiveServer); }
        void RegisterClient(NetworkClient client)
        {
            if (client == null) return;
            client.RegisterHandler(MessageId, ReceiveClient); registeredClient = client;
        }
        static void Send(NetworkConnection connection, JObject body)
        {
            if (!connection.Send(MessageId, new AdminWireMessage { Json = body.ToString(Formatting.None) }))
                throw new IOException("联机消息发送失败");
        }
        void SendHost(JObject body)
        {
            var manager = MilkNetworkManager.singleton;
            if (manager == null || manager.client == null || !manager.client.isConnected) throw new IOException("房主连接不可用");
            Send(manager.client.connection, body);
        }
        public void Update(FarmData current)
        {
            if (disposed) return;
            if (!ReferenceEquals(farm, current))
            {
                farm = current; peers.Clear(); results.Clear(); awaiting.Clear(); applied.Clear(); pendingId = null;
                pendingChange = null; clientRequest = null; fault = false; backedUp = false; session = current != null && Host ? Guid.NewGuid().ToString("N") : null;
                nextHello = 0; Status = current == null ? "等待农场加载" : Host ? "所有访客自动授权：" + (AutoAuthorize ? "已开启" : "已关闭") : "正在连接房主管理服务";
                auditPath = null;
            }
            if (farm == null) return;
            var stage = StageScript.Instance; var manager = MilkNetworkManager.singleton;
            if (stage.IsOnline && manager != null)
            {
                if (!ReferenceEquals(registeredClient, manager.client)) RegisterClient(manager.client);
                if (Host) NetworkServer.RegisterHandler(MessageId, ReceiveServer);
                else if (Time.realtimeSinceStartup >= nextHello)
                {
                    nextHello = Time.realtimeSinceStartup + 3;
                    try { SendHost(new JObject { { "kind", "hello" }, { "version", Version }, { "supported", Supported }, { "session", session }, { "healthy", !fault && !(SyncBlocked != null && SyncBlocked()) } }); }
                    catch (Exception ex) { Status = ex.Message; }
                }
            }
            if (pendingId != null && Time.realtimeSinceStartup - pendingAt > 12)
                Fail("客户端确认超时；修改可能已执行，请核对数据并重新加入后再修改");
            if (clientRequest != null && Time.realtimeSinceStartup - requestedAt > 15)
            { Status = "请求结果待核实：请核对当前数值，不要重复提交。重新加入后恢复修改。"; fault = true; clientRequest = null; }
        }
        List<NetworkConnection> CompatiblePeers()
        {
            var list = new List<NetworkConnection>();
            var manager = MilkNetworkManager.singleton;
            if (!StageScript.Instance.IsOnline) return list;
            if (manager == null || !NetworkServer.active) throw new InvalidOperationException("房间尚未就绪");
            foreach (var connection in NetworkServer.connections)
            {
                if (connection == null) continue;
                bool local = false, identified = false;
                foreach (var p in manager.PlayerList)
                    if (p != null && ReferenceEquals(p.connectionToClient, connection)) { local |= p.isLocalPlayer; identified = true; }
                if (local) continue;
                float last;
                if (!identified || !connection.isReady || !peers.TryGetValue(connection, out last) || Time.realtimeSinceStartup - last > 10)
                    throw new InvalidOperationException("有玩家未安装兼容 Mod、正在加载或尚未完成握手，修改暂不可用");
                list.Add(connection);
            }
            return list;
        }
        string Identity(NetworkConnection connection)
        {
            if (connection == null) return "host";
            var manager = MilkNetworkManager.singleton;
            if (manager != null) foreach (var player in manager.PlayerList)
                if (player != null && ReferenceEquals(player.connectionToClient, connection) && player.Gamer != null) return player.Gamer.UniqueId;
            throw new InvalidOperationException("玩家身份尚未就绪");
        }
        JObject Decode(NetworkMessage message)
        {
            var body = message.ReadMessage<AdminWireMessage>().Json;
            if (body == null || body.Length > 8192) throw new ArgumentException("管理消息长度无效");
            return JObject.Parse(body);
        }
        void ReceiveServer(NetworkMessage message)
        {
            try
            {
                if (farm == null || !Host || !Supported) return;
                var body = Decode(message); string kind = (string)body["kind"];
                if (kind == "hello")
                {
                    Identity(message.conn);
                    if ((string)body["session"] == session && (string)body["version"] == Version && (bool?)body["supported"] == true && (bool?)body["healthy"] == true)
                        peers[message.conn] = Time.realtimeSinceStartup;
                    else peers.Remove(message.conn);
                    Send(message.conn, new JObject { { "kind", "welcome" }, { "session", session }, { "version", Version }, { "allowed", AutoAuthorize }, { "healthy", !fault } });
                }
                else if ((string)body["session"] == session && kind == "request") ProcessRequest(body, message.conn);
                else if ((string)body["session"] == session && kind == "ack" && (string)body["id"] == pendingId && awaiting.Contains(message.conn))
                {
                    if ((bool?)body["ok"] != true) { Fail("客户端状态校验失败，已暂停修改，请重新加入房间"); return; }
                    awaiting.Remove(message.conn); if (awaiting.Count == 0) Finish();
                }
                else if (kind == "request") Reply(message.conn, (string)body["id"], "房间已重启，请等待重新连接后重试", false);
            }
            catch (Exception ex) { log.LogWarning("Admin receive: " + ex.Message); }
        }
        void ReceiveClient(NetworkMessage message)
        {
            try
            {
                if (farm == null || Host) return;
                var body = Decode(message); string kind = (string)body["kind"];
                if (kind == "welcome")
                {
                    string newSession = (string)body["session"];
                    if (session != null && session != newSession && clientRequest != null)
                    { fault = true; clientRequest = null; Status = "房间发生变化，上一请求结果待核实"; }
                    session = newSession;
                    if (!fault && clientRequest == null) Status = (string)body["version"] != Version ? "房主版本不同，请更新 Mod" :
                        (bool?)body["healthy"] != true ? "房主管理服务已暂停，请重新加入房间" :
                        (bool?)body["allowed"] == true ? "房主已自动授权所有访客" : "房主已关闭访客修改";
                }
                else if ((string)body["session"] == session && kind == "apply")
                {
                    string id = (string)body["id"]; bool ok = !fault;
                    try
                    {
                        if (ok && !applied.Contains(id))
                        {
                            AdminChanges.Apply(farm, (JObject)body["change"]); applied.Add(id);
                        }
                    }
                    catch (Exception ex) { ok = false; fault = true; Status = "修改同步失败：" + ex.Message; }
                    SendHost(new JObject { { "kind", "ack" }, { "session", session }, { "id", id }, { "ok", ok } });
                }
                else if ((string)body["session"] == session && kind == "result" && (string)body["id"] == clientRequest)
                { Status = (string)body["message"]; clientRequest = null; }
            }
            catch (Exception ex) { fault = true; Status = "管理消息异常，需重新加入：" + ex.Message; log.LogWarning(Status); }
        }
        public void Submit(string key, long value, bool add, string expected)
        {
            try
            {
                if (farm == null || !Supported || fault || SyncBlocked != null && SyncBlocked()) throw new InvalidOperationException("当前无法修改：农场未就绪、版本不支持或需要恢复同步");
                if (Busy) throw new InvalidOperationException("上一请求尚未结束");
                if (!Host && session == null) throw new InvalidOperationException("尚未连接房主管理服务");
                var request = new JObject { { "kind", "request" }, { "session", session }, { "id", Guid.NewGuid().ToString("N") },
                    { "key", key }, { "value", value.ToString() }, { "add", add }, { "expected", expected } };
                if (Host) ProcessRequest(request, null);
                else { SendHost(request); clientRequest = (string)request["id"]; requestedAt = Time.realtimeSinceStartup; Status = "等待房主执行与客户端确认…"; }
            }
            catch (Exception ex) { Status = ex.Message; }
        }
        void ProcessRequest(JObject body, NetworkConnection connection)
        {
            string id = (string)body["id"];
            try
            {
                if (id == null || id.Length != 32) throw new ArgumentException("请求编号无效");
                new Guid(id);
                string actor = Identity(connection), cached;
                if (results.TryGetValue(actor + ":" + id, out cached)) { Reply(connection, id, cached, true); return; }
                if (id == pendingId) return;
                if (pendingId != null) throw new InvalidOperationException("其他修改正在同步，请稍后重试");
                if (fault || SyncBlocked != null && SyncBlocked()) throw new InvalidOperationException("检测到同步问题，已暂停修改");
                if (connection != null && !AutoAuthorize) throw new InvalidOperationException("房主已关闭访客修改");
                var recipients = CompatiblePeers();
                if (connection != null && !recipients.Contains(connection)) throw new InvalidOperationException("尚未完成兼容性检查");
                string key = (string)body["key"];
                if (AdminChanges.Read(farm, key) != (string)body["expected"]) throw new InvalidOperationException("预览数值已变化，请重新读取后提交");
                var change = AdminChanges.Plan(farm, key, long.Parse((string)body["value"]), (bool)body["add"]);
                Backup();
                Audit("prepared", actor, id, change);
                pendingId = id; pendingActor = actor; pendingChange = change; requester = connection; pendingAt = Time.realtimeSinceStartup;
                awaiting.Clear(); foreach (var peer in recipients) awaiting.Add(peer);
                // Same reliable channel 0 as native work/tick messages: mutation stays ordered with game actions.
                AdminChanges.Apply(farm, change);
                foreach (var peer in recipients) Send(peer, new JObject { { "kind", "apply" }, { "session", session }, { "id", id }, { "change", change.DeepClone() } });
                Status = "已执行，正在等待 " + awaiting.Count + " 个客户端确认";
                if (awaiting.Count == 0) Finish();
            }
            catch (Exception ex)
            {
                if (pendingId == id) Fail("修改结果待核实：" + ex.Message);
                else Reply(connection, id, ex.Message, false);
            }
        }
        void Backup()
        {
            if (backedUp) return;
            string directory = Path.Combine(Paths.BepInExRootPath, "FarmProgressAdminBackups"); Directory.CreateDirectory(directory);
            string stem = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + session;
            File.WriteAllBytes(Path.Combine(directory, stem + ".data"), farm.ToBinary());
            auditPath = Path.Combine(directory, stem + ".jsonl"); backedUp = true;
        }
        void Audit(string state, string actor, string id, JObject change)
        {
            File.AppendAllText(auditPath, new JObject { { "time", DateTime.Now.ToString("o") }, { "state", state }, { "actor", actor },
                { "requestId", id }, { "session", session }, { "change", change.DeepClone() } }.ToString(Formatting.None) + Environment.NewLine, Encoding.UTF8);
        }
        void Finish()
        {
            Audit("confirmed", pendingActor, pendingId, pendingChange);
            string text = "已完成：" + (string)pendingChange["key"] + " → " + (string)pendingChange["after"];
            results[pendingActor + ":" + pendingId] = text;
            Reply(requester, pendingId, text, true); pendingId = null; pendingChange = null; awaiting.Clear();
        }
        void Fail(string reason)
        {
            fault = true;
            try { if (pendingChange != null) Audit("uncertain", pendingActor, pendingId, pendingChange); }
            catch (Exception ex) { log.LogWarning(ex.Message); }
            try { Reply(requester, pendingId, reason, false); } catch (Exception ex) { log.LogWarning(ex.Message); }
            Status = reason; pendingId = null; pendingChange = null; awaiting.Clear();
        }
        void Reply(NetworkConnection connection, string id, string message, bool ok)
        {
            Status = message;
            if (connection != null) Send(connection, new JObject { { "kind", "result" }, { "session", session }, { "id", id }, { "message", message }, { "ok", ok } });
        }
        public void Dispose()
        {
            disposed = true; harmony.UnpatchSelf();
            if (registeredClient != null) registeredClient.UnregisterHandler(MessageId);
            if (NetworkServer.active) NetworkServer.UnregisterHandler(MessageId);
            if (ReferenceEquals(active, this)) active = null;
        }
    }
}
