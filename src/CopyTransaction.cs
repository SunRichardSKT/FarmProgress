using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace FarmProgress
{
    // Runs inside StorageManager's storage lock; it never replaces a farm file.
    public interface ICopyStore
    {
        bool Exists(string path);
        Stream Open(string path, FileMode mode);
        void Delete(string path);
        void EnsureFarmDirectory();
    }
    public static class CopyTransaction
    {
        static readonly string[] IndexFiles = { "farms.data", "farms.bak", "farms.xml" };
        public static byte[] Read(ICopyStore store, string path)
        {
            using (var source = store.Open(path, FileMode.Open))
            using (var target = new MemoryStream())
            {
                var buffer = new byte[32768]; int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) != 0) target.Write(buffer, 0, count);
                return target.ToArray();
            }
        }
        public static byte[] Compress(byte[] json)
        {
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress)) gzip.Write(json, 0, json.Length);
                return output.ToArray();
            }
        }
        public static int Run(ICopyStore store, byte[] farm, string backupDirectory,
            Func<int, bool> occupied, Action<int> publish, Action unpublish, Action saveIndex, Action<int> verifyIndex)
        {
            int slot = 0;
            while (occupied(slot) || store.Exists("Farms/farm_" + slot + ".data") || store.Exists("Farms/farm_" + slot + ".bak")) slot++;
            var originals = new Dictionary<string, byte[]>();
            Directory.CreateDirectory(backupDirectory);
            foreach (string path in IndexFiles)
            {
                byte[] bytes = store.Exists(path) ? Read(store, path) : null;
                originals.Add(path, bytes);
                if (bytes != null) File.WriteAllBytes(Path.Combine(backupDirectory, path + ".before"), bytes);
            }
            File.WriteAllBytes(Path.Combine(backupDirectory, "farm-snapshot.data"), farm);
            string destination = "Farms/farm_" + slot + ".data";
            bool created = false, registered = false, indexAttempted = false;
            try
            {
                store.EnsureFarmDirectory();
                using (var output = store.Open(destination, FileMode.CreateNew))
                { created = true; output.Write(farm, 0, farm.Length); }
                byte[] readback = Read(store, destination);
                if (readback.Length != farm.Length) throw new IOException("Farm readback length differs");
                for (int i = 0; i < farm.Length; i++) if (readback[i] != farm[i]) throw new IOException("Farm readback differs");
                registered = true; publish(slot);
                indexAttempted = true; saveIndex(); verifyIndex(slot);
                return slot;
            }
            catch (Exception error)
            {
                var recovery = new List<Exception>(); recovery.Add(error);
                try { if (registered) unpublish(); } catch (Exception ex) { recovery.Add(ex); }
                if (indexAttempted) foreach (var entry in originals)
                {
                    try
                    {
                        if (entry.Value == null) { if (store.Exists(entry.Key)) store.Delete(entry.Key); }
                        else using (var output = store.Open(entry.Key, FileMode.Create)) output.Write(entry.Value, 0, entry.Value.Length);
                    }
                    catch (Exception ex) { recovery.Add(ex); }
                }
                try { if (created && store.Exists(destination)) store.Delete(destination); } catch (Exception ex) { recovery.Add(ex); }
                if (recovery.Count > 1) throw new IOException("Copy failed; recovery also failed. Original index is backed up in " + backupDirectory, error);
                throw;
            }
        }
    }
}
