using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FarmProgressUpdater
{
    public sealed class OneClickForm : Form
    {
        readonly Label status = new Label(), location = new Label();
        readonly ProgressBar progress = new ProgressBar();
        readonly Button primary = new Button(), folder = new Button(), offline = new Button(), detail = new Button();
        readonly TextBox details = new TextBox();
        CancellationTokenSource cancellation;
        bool working, completed;
        string directory;
        readonly string remembered = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FarmProgressUpdater", "game-directory.txt");
        public OneClickForm(bool autoRun = true)
        {
            Text = "Farm Progress · 一键更新"; ClientSize = new Size(600, 245);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 10);
            Controls.Add(new Label { Text = "农场助手 · 自动安装与更新", AutoSize = true, Location = new Point(22, 18), Font = new Font(Font, FontStyle.Bold) });
            status.SetBounds(22, 57, 550, 45); status.Text = "准备检查最新版本…"; Controls.Add(status);
            progress.SetBounds(22, 110, 555, 20); progress.Style = ProgressBarStyle.Marquee; Controls.Add(progress);
            location.SetBounds(22, 143, 555, 25); location.AutoEllipsis = true; location.Text = "自动识别 Steam 游戏目录"; Controls.Add(location);
            primary.SetBounds(22, 190, 115, 32); primary.Text = "取消";
            primary.Click += async delegate
            {
                if (working) { cancellation.Cancel(); status.Text = "正在取消…"; }
                else if (completed) Close(); else await UpdateNow(false);
            };
            folder.SetBounds(150, 190, 115, 32); folder.Text = "选择目录";
            folder.Click += delegate { ChooseDirectory(); };
            offline.SetBounds(278, 190, 160, 32); offline.Text = "离线安装 " + UpdaterCore.BundledVersion;
            offline.Click += async delegate { await UpdateNow(true); };
            detail.SetBounds(451, 190, 126, 32); detail.Text = "查看详情";
            detail.Click += delegate { bool show = !details.Visible; details.Visible = show; ClientSize = new Size(600, show ? 440 : 245); detail.Text = show ? "收起详情" : "查看详情"; };
            Controls.Add(primary); Controls.Add(folder); Controls.Add(offline); Controls.Add(detail);
            details.SetBounds(22, 250, 555, 165); details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical; details.Visible = false; Controls.Add(details);
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            { if (working) { cancellation.Cancel(); e.Cancel = true; status.Text = "正在结束当前步骤，请稍候…"; } };
            Shown += async delegate { if (autoRun) { await Task.Delay(150); if (!IsDisposed && !Disposing) await UpdateNow(false); } };
        }
        void Report(string message)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Report), message); return; }
            details.AppendText(message + Environment.NewLine);
        }
        void SetProgress(int percent)
        {
            if (InvokeRequired) { BeginInvoke(new Action<int>(SetProgress), percent); return; }
            progress.Style = ProgressBarStyle.Continuous; progress.Value = Math.Max(0, Math.Min(100, percent));
        }
        bool ChooseDirectory()
        {
            using (var dialog = new FolderBrowserDialog { Description = "选择包含 FarmTogether.exe 的一代游戏目录", SelectedPath = directory ?? "" })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try { UpdaterCore.ValidateGameDirectory(dialog.SelectedPath); directory = dialog.SelectedPath; location.Text = directory; return true; }
                    catch (Exception ex) { status.Text = ex.Message; }
                }
            return false;
        }
        void FindDirectory()
        {
            if (directory != null) return;
            if (File.Exists(remembered))
            {
                try { string saved = File.ReadAllText(remembered).Trim(); UpdaterCore.ValidateGameDirectory(saved); directory = saved; return; }
                catch (Exception ex) { Report("已记录目录不可用：" + ex.Message); }
            }
            var games = UpdaterCore.FindGames();
            if (games.Count == 1) directory = games[0];
            else
            {
                status.Text = games.Count > 1 ? "检测到多个游戏目录，请选择要更新的安装。" : "未找到游戏，请选择一次安装目录。";
                if (!ChooseDirectory()) throw new OperationCanceledException("未选择游戏目录");
            }
        }
        async Task UpdateNow(bool bundled)
        {
            if (working) return;
            working = true; completed = false; cancellation = new CancellationTokenSource();
            primary.Text = "取消"; folder.Enabled = offline.Enabled = false;
            progress.Style = ProgressBarStyle.Marquee; var token = cancellation.Token;
            try
            {
                FindDirectory(); UpdaterCore.ValidateGameDirectory(directory); location.Text = directory;
                string target = directory, version; byte[] package;
                if (bundled)
                {
                    version = UpdaterCore.BundledVersion; status.Text = "正在准备离线内置版本 " + version + "…";
                    package = UpdaterCore.EmbeddedPackage();
                }
                else
                {
                    status.Text = "正在检查 GitHub 最新版本…";
                    var manifest = await Task.Run(() => UpdaterCore.FetchManifest(UpdaterCore.DefaultFeed, token));
                    version = manifest.version; status.Text = "正在下载最新版本 " + version + "…";
                    Report("最新版本：" + version);
                    package = await Task.Run(() => UpdaterCore.Download(manifest.url, 100 * 1024 * 1024, p => SetProgress(p * 85 / 100), token));
                    token.ThrowIfCancellationRequested();
                    if (!string.Equals(UpdaterCore.Hash(package), manifest.sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("下载校验失败，请重试。");
                    Report("下载 SHA-256 校验通过。");
                }
                token.ThrowIfCancellationRequested(); SetProgress(88);
                while (UpdaterCore.GameRunning())
                {
                    status.Text = "等待游戏退出…请暂时暂停自动重开脚本，退出后将自动继续。";
                    await Task.Delay(1000, token);
                }
                status.Text = "正在安装 " + version + "，请稍候…"; primary.Enabled = false;
                string backup = await Task.Run(() => UpdaterCore.Install(target, package, version, Report));
                completed = true; SetProgress(100);
                status.Text = bundled ? "离线版本 " + version + " 已安装，可以启动游戏。" : "最新版 " + version + " 已安装，可以启动游戏。";
                Report("更新前文件备份：" + backup);
                try { Directory.CreateDirectory(Path.GetDirectoryName(remembered)); File.WriteAllText(remembered, target); }
                catch (Exception ex) { Report("安装已成功，目录记录未保存：" + ex.Message); }
            }
            catch (Exception ex)
            {
                SetProgress(0);
                status.Text = token.IsCancellationRequested || ex is OperationCanceledException ? "已取消，可随时重新运行。" : "更新未完成：" + ex.Message;
                Report(status.Text);
                if (!bundled && !token.IsCancellationRequested) Report("网络不可用时可选择离线安装内置版本，不会自动降级。");
            }
            finally
            {
                working = false; primary.Enabled = true; primary.Text = completed ? "完成" : "重试更新";
                folder.Enabled = offline.Enabled = true; cancellation.Dispose();
            }
        }
    }
}
