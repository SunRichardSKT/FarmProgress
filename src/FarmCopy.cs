using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using BepInEx;
using Logic.Farm;
using Logic.Mode;
using Milkstone.Game;
using Milkstone.Game.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FarmProgress
{
    public static class FarmCopy
    {
        sealed class Store : ICopyStore
        {
            readonly IStorageFile storage;
            public Store(IStorageFile storage) { this.storage = storage; }
            public bool Exists(string path) { return storage.FileExists(path); }
            public Stream Open(string path, FileMode mode) { return storage.OpenFile(path, mode); }
            public void Delete(string path) { storage.DeleteFile(path); }
            public void EnsureFarmDirectory() { if (!storage.DirectoryExists("Farms/")) storage.CreateDirectory("Farms/"); }
        }
        public static string Serialize(FarmData source)
        {
            var serializer = typeof(FarmData).GetMethod("ToJson2", BindingFlags.Instance | BindingFlags.NonPublic);
            if (serializer == null) throw new NotSupportedException("Farm serializer not found in this game version");
            using (var text = new StringWriter())
            using (var writer = new JsonTextWriter(text))
            { serializer.Invoke(source, new object[] { writer }); writer.Flush(); return text.ToString(); }
        }
        public static void Start(FarmData source, Action<string> success, Action<Exception> failure)
        {
            if (source == null || source.IsLocal) throw new InvalidOperationException("请先加入朋友的农场。");
            var manager = FarmManager.Instance;
            if (manager == null || !manager.Initialized || StorageManager.Instance == null) throw new InvalidOperationException("本地存档尚未准备完成。");
            var slotsField = typeof(FarmManager).GetField("slots", BindingFlags.Instance | BindingFlags.NonPublic);
            var slotField = typeof(LocalFarmHeader).GetField("slot", BindingFlags.Instance | BindingFlags.NonPublic);
            if (slotsField == null || slotField == null) throw new NotSupportedException("Local farm registration changed in this game version");
            var slots = (Dictionary<int, LocalFarmHeader>)slotsField.GetValue(manager);
            string json = Serialize(source);
            var body = JObject.Parse(json);
            byte[] compressed = CopyTransaction.Compress(Encoding.UTF8.GetBytes(json));
            var now = PlatformInterface.Instance.CurrentTime;
            string name = "联机副本 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            // ParseJson builds a detached header without the default-farm creation/save side effects.
            var headerJson = new JObject {
                { "name", name }, { "slot", 0 }, { "version", (int)body["Version"] },
                { "style", source.FarmStyle.FullId }, { "level", source.Experience.Level },
                { "levelProgress", source.Experience.Progress }, { "money", source.Money.ToJson() },
                { "ticks", source.WorldTicks }, { "nextSeason", source.NextSeasonChange },
                { "season", (int)source.CurrentSeason }, { "playMode", (int)PlayMode.Local },
                { "simulationSpeed", (int)SimulationSpeeds.Normal },
                { "tutorialStep", (int)FarmTutorial.TutorialSteps.TutorialSkipped },
                { "last_save", now.Ticks }, { "creation_time", now.Ticks }, { "likes", 0 }, { "fuel", 0 }
            };
            var header = LocalFarmHeader.ParseJson(headerJson, manager);
            string backupDir = Path.Combine(Path.Combine(Paths.BepInExRootPath, "FarmProgressCopies"), DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            int savedSlot = -1;
            StorageManager.Instance.Post(manager.PlayerIndex, delegate(IPlayerStorageFile container, object unused)
            {
                if (container == null) throw new IOException("No local player storage container");
                var store = new Store(container);
                lock (manager.Farms)
                {
                    savedSlot = CopyTransaction.Run(store, compressed, backupDir, slot => slots.ContainsKey(slot),
                        slot => {
                            slotField.SetValue(header, slot); slots.Add(slot, header); manager.Farms.Add(header); manager.SortFarms();
                            File.WriteAllText(Path.Combine(backupDir, "copy-info.json"), new JObject {
                                { "name", name }, { "slot", slot }, { "farmPath", "Farms/" + FarmManager.GetSlotPath(slot) },
                                { "copiedAt", DateTimeOffset.Now.ToString("o") }, { "source", "multiplayer-client" }
                            }.ToString(Formatting.Indented), Encoding.UTF8);
                        },
                        () => {
                            LocalFarmHeader current;
                            if (slots.TryGetValue(header.Slot, out current) && ReferenceEquals(current, header)) slots.Remove(header.Slot);
                            manager.Farms.Remove(header);
                        },
                        () => manager.Save(container, false),
                        slot => {
                            using (var raw = container.OpenFile("farms.data", FileMode.Open))
                            using (var gzip = new GZipStream(raw, CompressionMode.Decompress))
                            using (var reader = new StreamReader(gzip, Encoding.UTF8))
                            {
                                var savedIndex = JObject.Parse(reader.ReadToEnd()); bool found = false;
                                foreach (var item in (JArray)savedIndex["Farms"]) if ((int)item["slot"] == slot) found = true;
                                if (!found) throw new IOException("New farm missing from saved index");
                            }
                        });
                }
            }, null, delegate(object unused, Exception error)
            {
                if (error != null) failure(error);
                else success("已保存为本地农场：“" + name + "”。退出房间后在农场列表选择它。备份：" + backupDir);
            });
        }
    }
}
