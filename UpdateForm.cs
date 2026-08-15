using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshLauncher
{
    /// <summary>
    /// 自动更新窗口（前置）：检测到新版后询问用户，立即更新（git pull 或下载替换）→
    /// 重新安装依赖并构建 → 完成后进入主界面。
    /// </summary>
    internal class UpdateForm : Form
    {
        private readonly Label _lblInfo = new Label { AutoSize = false, Height = 48 };
        private readonly Button _btnUpdate = new Button { Text = "立即更新", Width = 110, Height = 30 };
        private readonly Button _btnSkip = new Button { Text = "跳过（继续使用旧版）", Width = 170, Height = 30 };
        private readonly ProgressBar _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 16 };
        private readonly TextBox _log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f), BackColor = Color.White
        };
        private readonly Label _step = new Label { AutoSize = false, Height = 22 };

        private readonly string _localVersion;
        private readonly string _remoteVersion;
        private bool _busy;
        private string _node;

        /// <summary>更新完成并成功为 true。</summary>
        public bool Completed { get; private set; }

        public UpdateForm(string localVersion, string remoteVersion)
        {
            _localVersion = localVersion;
            _remoteVersion = remoteVersion;
            Text = "dsh-launcher 更新";
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(460, 440);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            _lblInfo.Location = new Point(14, 12);
            _lblInfo.Size = new Size(432, 52);
            _lblInfo.Text = "发现新版本 " + _remoteVersion + "（本地 " + _localVersion + "）\n" +
                "更新将替换 dsh 源码并重新构建，userdata 数据不受影响。";

            _btnUpdate.Location = new Point(14, 70);
            _btnSkip.Location = new Point(134, 70);
            _btnUpdate.Click += async (s, e) => await StartUpdateAsync();
            _btnSkip.Click += (s, e) => Close();

            _step.Location = new Point(14, 108);
            _progress.Location = new Point(14, 134);
            _progress.Width = 432;
            _log.Location = new Point(14, 156);
            _log.Size = new Size(432, 268);

            Controls.Add(_lblInfo);
            Controls.Add(_btnUpdate);
            Controls.Add(_btnSkip);
            Controls.Add(_step);
            Controls.Add(_progress);
            Controls.Add(_log);
        }

        private async Task StartUpdateAsync()
        {
            if (_busy) return;
            _busy = true;
            _btnUpdate.Enabled = false;
            _btnSkip.Enabled = false;
            try
            {
                _node = MainForm.FindNode();
                if (_node == null)
                {
                    MessageBox.Show("未找到 Node.js（≥ v22.19），无法重新安装依赖。\n请安装 Node.js 后重试更新。",
                        "缺少 Node.js", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string pnpm = MainForm.FindPnpm();
                if (pnpm == null)
                {
                    MessageBox.Show("未找到 pnpm。请先手动执行 npm install -g pnpm，或重新安装 Node.js。",
                        "缺少 pnpm", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 1) 更新源码：git 仓库 → git pull；非 git → 下载 zip 替换
                if (Directory.Exists(Path.Combine(MainForm.WorkDir, ".git")))
                {
                    SetStep("从 git 拉取最新源码 ...", 15);
                    string git = MainForm.ResolveFromPath("git.exe") ?? "git";
                    Log("运行：" + git + " fetch origin");
                    if (!await RunProcessAsync(git, "fetch origin", MainForm.WorkDir, 300))
                        throw new Exception("git fetch 失败（虚拟机需先安装 Git）");
                    Log("运行：" + git + " reset --hard origin/master");
                    if (!await RunProcessAsync(git, "reset --hard origin/master", MainForm.WorkDir, 300))
                        throw new Exception("git reset 失败");
                    Log("源码已更新到最新。");
                }
                else
                {
                    SetStep("下载最新源码 ...", 15);
                    string zipPath = Path.Combine(MainForm.LauncherDir, "dsh-update.zip");
                    bool downloaded = false;
                    foreach (var url in MainForm.SourceZipMirrors)
                    {
                        Log("下载：" + url);
                        try
                        {
                            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                                if (!resp.IsSuccessStatusCode) { Log("HTTP " + (int)resp.StatusCode + "，尝试下一源 ..."); continue; }
                                await resp.Content.CopyToAsync(fs);
                            }
                            downloaded = true;
                            break;
                        }
                        catch (Exception ex) { Log("下载异常：" + ex.Message + "，尝试下一源 ..."); }
                    }
                    if (!downloaded) throw new Exception("下载最新源码失败（所有下载源不可用）");

                    SetStep("备份旧源码并替换 ...", 45);
                    string backup = MainForm.WorkDir + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    Directory.Move(MainForm.WorkDir, backup);
                    Log("已备份旧源码到：" + backup);
                    string extractDir = Path.Combine(MainForm.LauncherDir, "dsh-update-extract");
                    if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                    ZipFile.ExtractToDirectory(zipPath, extractDir);
                    string inner = Directory.GetDirectories(extractDir).FirstOrDefault();
                    if (inner == null) throw new Exception("压缩包内容异常");
                    Directory.Move(inner, MainForm.WorkDir);
                    try { File.Delete(zipPath); Directory.Delete(extractDir, true); } catch { }
                    Log("新源码已就位。");
                }

                // 2) 重新安装依赖 + 构建
                SetStep("安装依赖（pnpm install）...", 55);
                if (!await RunProcessAsync(_node, "\"" + pnpm + "\" install", MainForm.WorkDir, 1800))
                    throw new Exception("依赖安装失败（pnpm install）");
                SetStep("构建（pnpm run build）...", 80);
                if (!await RunProcessAsync(_node, "\"" + pnpm + "\" run build", MainForm.WorkDir, 1800))
                    throw new Exception("构建失败（pnpm run build）");

                SetStep("更新完成", 100);
                Log("更新完成！");
                MessageBox.Show("更新完成！即将进入主界面。", "更新成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Completed = true;
                Close();
            }
            catch (Exception ex)
            {
                Log("更新失败：" + ex.Message);
                MessageBox.Show("更新失败：" + ex.Message + "\n\n可手动从 " + MainForm.SourceRepoUrl + " 下载最新源码替换，或跳过本次更新。",
                    "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _busy = false;
                if (!IsDisposed) { _btnUpdate.Enabled = true; _btnSkip.Enabled = true; }
            }
        }

        private void SetStep(string text, int percent)
        {
            if (IsDisposed) return;
            Invoke((Action)(() =>
            {
                _step.Text = text;
                _progress.Value = Math.Max(_progress.Minimum, Math.Min(_progress.Maximum, percent));
            }));
        }

        private void Log(string message)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            try
            {
                if (!IsDisposed && IsHandleCreated)
                    Invoke((Action)(() =>
                    {
                        _log.AppendText(line + Environment.NewLine);
                        _log.SelectionStart = _log.TextLength;
                        _log.ScrollToCaret();
                    }));
            }
            catch { }
            try { File.AppendAllText(MainForm.LogFile, line + Environment.NewLine); } catch { }
        }

        private async Task<bool> RunProcessAsync(string fileName, string args, string workingDir, int timeoutSec)
        {
            var tcs = new TaskCompletionSource<bool>();
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = workingDir ?? MainForm.LauncherDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) Log("[err] " + e.Data); };
            p.Exited += (s, e) => tcs.TrySetResult(p.ExitCode == 0);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            bool completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutSec * 1000)) == tcs.Task;
            if (!completed)
            {
                try { p.Kill(true); } catch { }
                return false;
            }
            return await tcs.Task;
        }
    }
}
