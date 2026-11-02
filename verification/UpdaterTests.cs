using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using FarmProgressUpdater;
class UpdaterTests
{
    static int count;
    static string root;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); count++; }
    static void Reject(Action action, string label) { try { action(); } catch { count++; return; } throw new Exception(label); }
    static byte[] Package(Dictionary<string, byte[]> files)
    {
        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                foreach (var pair in files) using (var output = zip.CreateEntry(pair.Key).Open()) output.Write(pair.Value, 0, pair.Value.Length);
            return memory.ToArray();
        }
    }
    static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().FullName + ": " + ex.Message); return 1; }
    }
    static void Run(string[] args)
    {
        root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
        Check(UpdaterCore.Hash(new byte[0]) == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "SHA256 known vector");
        UpdaterCore.ValidateManifest(new UpdateManifest { version = "1.6.0", sha256 = new string('a', 64), url = "https://example.com/mod.zip" }); count++;
        Reject(() => UpdaterCore.ValidateManifest(new UpdateManifest { version = "garbage", sha256 = new string('a', 64), url = "https://example.com" }), "bad version");
        Reject(() => UpdaterCore.ValidateManifest(new UpdateManifest { version = "1.6.0", sha256 = "bad", url = "https://example.com" }), "bad checksum");
        Reject(() => UpdaterCore.RequireHttps("http://example.com"), "HTTP rejected");
        Reject(() => UpdaterCore.RequireHttps("file:///C:/test"), "file URL rejected");
        Reject(() => UpdaterCore.SafeTarget(root, "../escaped.dll"), "parent traversal rejected");
        Reject(() => UpdaterCore.SafeTarget(root, "C:/escaped.dll"), "drive path rejected");
        Reject(() => UpdaterCore.SafeTarget(root, "BepInEx/../escaped.dll"), "interior traversal rejected");
        Reject(() => UpdaterCore.SafeTarget(root, @"BepInEx\bad.dll"), "alternate separator rejected");
        string game = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(game, "FarmTogether_Data", "Managed"));
        File.WriteAllBytes(Path.Combine(game, "FarmTogether.exe"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(game, "FarmTogether_Data", "Managed", "Assembly-CSharp.dll"), new byte[] { 0 });
        string plugin = "BepInEx/plugins/FarmProgress/FarmProgress.dll";
        var files = new Dictionary<string, byte[]> { { plugin, File.ReadAllBytes(args[1]) },
            { "BepInEx/core/BepInEx.dll", File.ReadAllBytes(args[2]) }, { "winhttp.dll", new byte[] { 1 } },
            { "doorstop_config.ini", new byte[] { 2 } }, { "ignored.txt", new byte[] { 3 } } };
        var bytes = Package(files);
        Reject(() => UpdaterCore.Install(game, bytes, "1.6.0", s => {}, () => true), "running game blocks installation");
        Reject(() => UpdaterCore.Install(game, bytes, "9.9.9", s => {}, () => false), "embedded version mismatch rejected before install");
        Check(!File.Exists(Path.Combine(game, "winhttp.dll")), "mismatch does not mutate game files");
        string backup = UpdaterCore.Install(game, bytes, "1.6.0", s => {}, () => false);
        Check(File.Exists(Path.Combine(game, plugin)) && Directory.Exists(backup), "first install and backup directory");
        Check(!File.Exists(Path.Combine(game, "ignored.txt")), "unlisted files not installed");
        File.WriteAllText(Path.Combine(game, "doorstop_config.ini"), "custom-loader-settings");
        Directory.CreateDirectory(Path.Combine(game, "BepInEx", "config"));
        File.WriteAllText(Path.Combine(game, "BepInEx", "config", "user.cfg"), "keep-me");
        UpdaterCore.Install(game, bytes, "1.6.0", s => {}, () => false);
        Check(File.ReadAllText(Path.Combine(game, "doorstop_config.ini")) == "custom-loader-settings", "existing loader configuration retained");
        Check(File.ReadAllText(Path.Combine(game, "BepInEx", "config", "user.cfg")) == "keep-me", "user configuration retained");
        string before = UpdaterCore.Hash(File.ReadAllBytes(Path.Combine(game, plugin)));
        Reject(() => UpdaterCore.Install(game, bytes, "1.6.0", s => { throw new IOException("simulated write callback failure"); }, () => false), "interrupted install fails");
        Check(UpdaterCore.Hash(File.ReadAllBytes(Path.Combine(game, plugin))) == before, "original plugin restored on failure");
        files["../../escaped.dll"] = new byte[] { 1 };
        Reject(() => UpdaterCore.Install(game, Package(files), "1.6.0", s => {}, () => false), "malicious zip path rejected");
        files.Remove("../../escaped.dll"); files.Remove("BepInEx/core/BepInEx.dll");
        string second = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(second, "FarmTogether_Data", "Managed"));
        File.WriteAllBytes(Path.Combine(second, "FarmTogether.exe"), new byte[] { 0 }); File.WriteAllBytes(Path.Combine(second, "FarmTogether_Data", "Managed", "Assembly-CSharp.dll"), new byte[] { 0 });
        Reject(() => UpdaterCore.Install(second, Package(files), "1.6.0", s => {}, () => false), "incomplete first-install loader rejected");
        Console.WriteLine(count + " updater checks passed (isolated fake game directories)");
    }
}
