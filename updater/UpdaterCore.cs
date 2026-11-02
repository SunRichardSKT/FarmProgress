using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace FarmProgressUpdater
{
    public sealed class UpdateManifest
    {
        public string version { get; set; }
        public string url { get; set; }
        public string sha256 { get; set; }
    }
    public static class UpdaterCore
    {
        public const string BundledVersion = "1.6.3";
        public const string DefaultFeed = "https://github.com/SunRichardSKT/FarmProgress/releases/latest/download/update.json";
        public static string Hash(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static void ValidateManifest(UpdateManifest manifest)
        {
            Version parsed;
            if (manifest == null || !Version.TryParse(manifest.version, out parsed) || parsed.Major < 1 ||
                manifest.sha256 == null || !Regex.IsMatch(manifest.sha256, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("更新清单格式无效");
            RequireHttps(manifest.url);
        }
        public static Uri RequireHttps(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidDataException("更新地址必须为 HTTPS");
            return uri;
        }
        public static byte[] Download(string url, int maxBytes, Action<int> progress = null, CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            RequireHttps(url); ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url); request.UserAgent = "FarmProgress-Updater/1.0";
            request.Timeout = 60000; request.ReadWriteTimeout = 60000;
            using (cancellation.Register(() => request.Abort()))
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.ResponseUri.Scheme != "https") throw new InvalidDataException("更新下载发生非 HTTPS 跳转");
                if (response.ContentLength > maxBytes) throw new InvalidDataException("下载文件过大");
                using (var stream = response.GetResponseStream()) using (var memory = new MemoryStream())
                {
                    var buffer = new byte[32768]; int n;
                    while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (memory.Length + n > maxBytes) throw new InvalidDataException("下载文件过大"); memory.Write(buffer, 0, n);
                        if (progress != null && response.ContentLength > 0) progress((int)Math.Min(100, memory.Length * 100 / response.ContentLength));
                    }
                    return memory.ToArray();
                }
            }
        }
        public static byte[] EmbeddedPackage()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FarmProgress.bundle.zip"))
            {
                if (stream == null) throw new FileNotFoundException("未包含离线安装包");
                using (var memory = new MemoryStream()) { stream.CopyTo(memory); return memory.ToArray(); }
            }
        }
        public static UpdateManifest FetchManifest(string url, CancellationToken cancellation = default(CancellationToken))
        {
            var result = new JavaScriptSerializer().Deserialize<UpdateManifest>(Encoding.UTF8.GetString(Download(url, 65536, null, cancellation)));
            ValidateManifest(result); return result;
        }
        public static bool GameRunning()
        {
            foreach (var process in Process.GetProcessesByName("FarmTogether")) { process.Dispose(); return true; }
            return false;
        }
        public static void ValidateGameDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "FarmTogether.exe")) ||
                !File.Exists(Path.Combine(directory, "FarmTogether_Data", "Managed", "Assembly-CSharp.dll")))
                throw new DirectoryNotFoundException("请选择包含 FarmTogether.exe 的一代游戏目录");
        }
        public static List<string> FindGames()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (key != null && key.GetValue("SteamPath") is string) roots.Add((string)key.GetValue("SteamPath"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                string file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (File.Exists(file)) foreach (Match match in Regex.Matches(File.ReadAllText(file), "\"path\"\\s*\"([^\"]+)\""))
                    libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
            var games = new List<string>();
            foreach (var library in libraries)
            {
                string apps = Path.Combine(library, "steamapps"), manifest = Path.Combine(apps, "appmanifest_673950.acf");
                if (File.Exists(manifest))
                {
                    var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s*\"([^\"]+)\"");
                    if (match.Success) AddGame(games, Path.Combine(apps, "common", match.Groups[1].Value));
                }
                AddGame(games, Path.Combine(apps, "common", "Farm Together"));
            }
            return games;
        }
        static void AddGame(List<string> games, string path)
        { if (File.Exists(Path.Combine(path, "FarmTogether.exe")) && !games.Contains(path)) games.Add(path); }
        public static string InstalledVersion(string directory)
        {
            string file = Path.Combine(directory, "BepInEx", "plugins", "FarmProgress", "FarmProgress.dll");
            if (!File.Exists(file)) return null;
            return AssemblyName.GetAssemblyName(file).Version.ToString();
        }
        public static string SafeTarget(string root, string relative)
        {
            if (string.IsNullOrEmpty(relative) || relative.Contains(":") || relative.Contains("\\") || relative.StartsWith("/"))
                throw new InvalidDataException("安装包路径无效");
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || relative.Split('/').Any(p => p == ".." || p == "."))
                throw new InvalidDataException("安装包包含越界路径");
            // Existing links could escape the game directory even when the text path is contained.
            string walk = Path.GetDirectoryName(target);
            while (walk != null && walk.Length >= fullRoot.TrimEnd(Path.DirectorySeparatorChar).Length)
            {
                if (Directory.Exists(walk) && (File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("安装路径包含目录链接，请选择普通游戏目录");
                walk = Path.GetDirectoryName(walk);
            }
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("安装目标是文件链接");
            return target;
        }
        static bool Allowed(string name)
        {
            if (name == "winhttp.dll" || name == "doorstop_config.ini" || name == ".doorstop_version" || name == "BepInEx/plugins/FarmProgress/FarmProgress.dll") return true;
            if (name.StartsWith("BepInEx/plugins/FarmProgress/licenses/", StringComparison.Ordinal) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                name.Substring("BepInEx/plugins/FarmProgress/licenses/".Length).IndexOf('/') < 0) return true;
            return name.StartsWith("BepInEx/core/", StringComparison.Ordinal) && name.Substring("BepInEx/core/".Length).IndexOf('/') < 0 &&
                (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        }
        public static string Install(string directory, byte[] package, string expectedVersion, Action<string> report, Func<bool> runningProbe = null)
        {
            ValidateGameDirectory(directory);
            var isRunning = runningProbe ?? GameRunning;
            if (isRunning()) throw new IOException("请先退出 Farm Together；自动重开房间脚本也请暂时停止，然后重新点击安装。");
            bool existingLoader = File.Exists(Path.Combine(directory, "winhttp.dll"));
            bool existingCore = File.Exists(Path.Combine(directory, "BepInEx", "core", "BepInEx.dll"));
            if (existingLoader && !existingCore) throw new IOException("现有 winhttp.dll 不属于可识别的 BepInEx，请先核对加载器。");
            if (existingCore && AssemblyName.GetAssemblyName(Path.Combine(directory, "BepInEx", "core", "BepInEx.dll")).Version.Major != 5)
                throw new IOException("当前安装的 BepInEx 不是受支持的 5.x 版本。");
            string installed = InstalledVersion(directory);
            if (installed != null && new Version(installed).Normalize() > new Version(expectedVersion).Normalize()) throw new IOException("本机 Mod 版本更高，已取消降级。");
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            using (var memory = new MemoryStream(package)) using (var archive = new ZipArchive(memory, ZipArchiveMode.Read))
            {
                long size = 0;
                foreach (var entry in archive.Entries)
                {
                    SafeTarget(directory, entry.FullName);
                    if (entry.FullName.EndsWith("/")) continue;
                    if (!Allowed(entry.FullName)) continue;
                    if (files.ContainsKey(entry.FullName)) throw new InvalidDataException("安装包包含重复文件");
                    size += entry.Length;
                    if (entry.Length > 32 * 1024 * 1024 || size > 100 * 1024 * 1024) throw new InvalidDataException("解压内容过大");
                    using (var stream = entry.Open()) using (var output = new MemoryStream())
                    { stream.CopyTo(output); files.Add(entry.FullName, output.ToArray()); }
                }
            }
            const string plugin = "BepInEx/plugins/FarmProgress/FarmProgress.dll";
            if (!files.ContainsKey(plugin)) throw new InvalidDataException("安装包缺少 Mod 文件");
            // Verify PE metadata without loading the plugin or its game dependencies.
            string temporary = Path.Combine(Path.GetTempPath(), "FarmProgress-" + Guid.NewGuid().ToString("N") + ".dll");
            try
            {
                File.WriteAllBytes(temporary, files[plugin]);
                if (AssemblyName.GetAssemblyName(temporary).Version != new Version(expectedVersion).Normalize())
                    throw new InvalidDataException("安装包内 Mod 版本与更新清单不一致");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            if (!existingCore && (!files.ContainsKey("BepInEx/core/BepInEx.dll") || !files.ContainsKey("winhttp.dll") || !files.ContainsKey("doorstop_config.ini")))
                throw new InvalidDataException("首次安装所需的加载器不完整");
            string backup = Path.Combine(directory, "BepInEx", "FarmProgressUpdateBackups", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
            SafeTarget(directory, "BepInEx/FarmProgressUpdateBackups/placeholder");
            Directory.CreateDirectory(backup);
            var changed = new List<string>(); var originals = new Dictionary<string, byte[]>();
            try
            {
                foreach (var pair in files)
                {
                    if (existingCore && pair.Key != plugin && !pair.Key.StartsWith("BepInEx/plugins/FarmProgress/licenses/", StringComparison.Ordinal)) continue;
                    if (isRunning()) throw new IOException("游戏已重新启动，已停止更新。请暂停自动启动脚本后重试。");
                    string target = SafeTarget(directory, pair.Key);
                    if (File.Exists(target))
                    {
                        originals[pair.Key] = File.ReadAllBytes(target);
                        string saved = SafeTarget(backup, pair.Key); Directory.CreateDirectory(Path.GetDirectoryName(saved)); File.WriteAllBytes(saved, originals[pair.Key]);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)); changed.Add(pair.Key);
                    File.WriteAllBytes(target, pair.Value);
                    if (Hash(File.ReadAllBytes(target)) != Hash(pair.Value)) throw new IOException("写入校验失败：" + pair.Key);
                    report("已安装 " + pair.Key);
                }
                File.WriteAllText(Path.Combine(backup, "installed.json"), new JavaScriptSerializer().Serialize(new { version = expectedVersion, files = changed }), Encoding.UTF8);
                return backup;
            }
            catch (Exception installError)
            {
                var errors = new List<string>();
                for (int i = changed.Count - 1; i >= 0; i--)
                    try
                    {
                        string target = SafeTarget(directory, changed[i]);
                        if (originals.ContainsKey(changed[i])) File.WriteAllBytes(target, originals[changed[i]]); else if (File.Exists(target)) File.Delete(target);
                    }
                    catch (Exception ex) { errors.Add(ex.Message); }
                if (errors.Count > 0) throw new IOException("安装失败且部分文件恢复失败。备份：" + backup + "；" + string.Join("；", errors), installError);
                throw new IOException("安装失败，已恢复原文件：" + installError.Message, installError);
            }
        }
        static Version Normalize(this Version version)
        { return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)); }
    }
}
