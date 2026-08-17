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
    /// 首次使用的前置安装向导：检测环境 → 安装 pnpm → 下载 dsh 源码 → 安装依赖，
    /// 全部完成后才进入主界面。
    /// </summary>
    internal class InstallForm : Form
    {
        // ── UI ──────────────────────────────────────────────────
        private readonly Label _lblNode = new Label();
        private readonly Label _lblPnpm = new Label();
        private readonly RadioButton _rbDirect = new RadioButton { Text = "直连 GitHub", Checked = true, AutoSize = true };
        private readonly RadioButton _rbMirror = new RadioButton { Text = "国内镜像（ghproxy）", AutoSize = true };
        private readonly Button _btnInstall = new Button { Text = "开始安装", Width = 110, Height = 30 };
        private readonly Button _btnChoose = new Button { Text = "选择已有源码目录", Width = 150, Height = 30 };
        private readonly Button _btnCancel = new Button { Text = "取消", Width = 80, Height = 30 };
        private readonly ProgressBar _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 16 };
        private readonly TextBox _log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f), BackColor = Color.White
        };
        private readonly Label _step = new Label { Text = "就绪", AutoSize = false, Height = 22 };

        /// <summary>安装完成（或已选目录）后为 true，调用方据此进入主界面。</summary>
        public bool Completed { get; private set; }

        private bool _busy;
        private string _node;

        public InstallForm()
        {
            Text = "dsh-launcher 安装向导";
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(420, 460);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            BuildUi();
            Shown += async (s, e) => await DetectEnvironmentAsync();
        }

        private void BuildUi()
        {
            var title = new Label
            {
                Text = "首次使用，需要先安装运行环境与 dsh 源码",
                Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
                Location = new Point(14, 12), AutoSize = false, Size = new Size(392, 26)
            };
            Controls.Add(title);

            var envBox = new GroupBox { Text = "运行环境", Location = new Point(14, 44), Size = new Size(392, 70) };
            _lblNode.Location = new Point(12, 22); _lblNode.AutoSize = true; _lblNode.ForeColor = Color.FromArgb(120, 120, 120);
            _lblPnpm.Location = new Point(12, 44); _lblPnpm.AutoSize = true; _lblPnpm.ForeColor = Color.FromArgb(120, 120, 120);
            envBox.Controls.Add(_lblNode);
            envBox.Controls.Add(_lblPnpm);
            Controls.Add(envBox);

            var srcBox = new GroupBox { Text = "获取 dsh 源码", Location = new Point(14, 122), Size = new Size(392, 74) };
            _rbDirect.Location = new Point(12, 22);
            _rbMirror.Location = new Point(12, 46);
            srcBox.Controls.Add(_rbDirect);
            srcBox.Controls.Add(_rbMirror);
            Controls.Add(srcBox);

            _btnInstall.Location = new Point(14, 206);
            _btnChoose.Location = new Point(134, 206);
            _btnCancel.Location = new Point(322, 206);
            _btnInstall.Click += async (s, e) => await StartInstallAsync();
            _btnChoose.Click += (s, e) => ChooseExisting();
            _btnCancel.Click += (s, e) => Close();
            Controls.Add(_btnInstall);
            Controls.Add(_btnChoose);
            Controls.Add(_btnCancel);

            _step.Location = new Point(14, 246);
            Controls.Add(_step);

            _progress.Location = new Point(14, 272);
            _progress.Width = 392;
            Controls.Add(_progress);

            _log.Location = new Point(14, 294);
            _log.Size = new Size(392, 150);
            Controls.Add(_log);
        }

        // ── 环境检测 ─────────────────────────────────────────────
        private async Task DetectEnvironmentAsync()
        {
            _lblNode.Text = "● 检查 Node.js ...";
            _lblNode.ForeColor = Color.FromArgb(180, 120, 0);
            _node = await Task.Run(MainForm.FindNode);
            if (_node != null)
            {
                string ver = await Task.Run(() => MainForm.NodeVersion(_node));
                _lblNode.Text = "✓ Node.js " + ver;
                _lblNode.ForeColor = Color.FromArgb(0, 140, 0);
            }
            else
            {
                _lblNode.Text = "✗ 未找到 Node.js（需 ≥ v22.19）";
                _lblNode.ForeColor = Color.FromArgb(200, 40, 40);
            }

            _lblPnpm.Text = "● 检查 pnpm ...";
            _lblPnpm.ForeColor = Color.FromArgb(180, 120, 0);
            bool hasPnpm = await Task.Run(MainForm.FindPnpm) != null;
            _lblPnpm.Text = hasPnpm ? "✓ pnpm 已安装" : "✗ 未找到 pnpm";
            _lblPnpm.ForeColor = hasPnpm ? Color.FromArgb(0, 140, 0) : Color.FromArgb(200, 40, 40);

            Log("环境检测完成。");
        }

        // ── 安装流程 ─────────────────────────────────────────────
        private async Task StartInstallAsync()
        {
            if (_busy) return;
            _busy = true;
            SetButtons(false);
            try
            {
                // 1) Node.js
                if (_node == null)
                {
                    var r = MessageBox.Show(
                        "未找到 Node.js（≥ v22.19），安装依赖需要它。\n是否打开 Node.js 官网下载？",
                        "缺少 Node.js", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r == DialogResult.Yes)
                    {
                        try { Process.Start(new ProcessStartInfo("https://nodejs.org") { UseShellExecute = true }); } catch { }
                    }
                    return;
                }

                // 2) pnpm（缺失时用 npm 自动安装）
                if (MainForm.FindPnpm() == null)
                {
                    SetStep("安装 pnpm ...", 10);
                    var (npmExe, npmPrefix) = MainForm.FindNpmCommand(_node);
                    if (npmExe == null)
                    {
                        MessageBox.Show(
                            "未找到 npm（Node.js 安装包通常自带 npm，可能安装不完整）。\n\n" +
                            "请重新安装 Node.js ≥ 22.19（nodejs.org），或手动安装 pnpm 后重试。",
                            "缺少 npm", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    string args = npmPrefix != null ? npmPrefix + " install -g pnpm" : "install -g pnpm";
                    Log("运行：" + npmExe + " " + args);
                    if (!await RunProcessAsync(npmExe, args, null, 300))
                    {
                        MessageBox.Show("pnpm 安装失败，请手动执行 npm install -g pnpm 后重试。",
                            "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    SetStep("pnpm 安装完成，重新检测 ...", 20);
                    if (MainForm.FindPnpm() == null)
                    {
                        Log("安装后仍未检测到 pnpm（PATH 未刷新），需要重启启动器。");
                        MessageBox.Show(
                            "pnpm 已安装成功，但当前进程暂时无法识别（Windows PATH 需重启生效）。\n\n" +
                            "请关闭本窗口，重新打开启动器后再继续。",
                            "需要重启启动器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    Log("已检测到 pnpm。");
                }

                // 3) 下载源码（镜像候选按顺序尝试，失败自动切换；最后一项为直连）
                string[] urls = _rbMirror.Checked ? MainForm.SourceZipMirrors : new[] { MainForm.SourceZipDirect };
                string zipPath = Path.Combine(MainForm.LauncherDir, "dsh-source.zip");
                bool downloaded = false;
                foreach (var url in urls)
                {
                    SetStep("下载 dsh 源码 ...", 25);
                    Log("下载：" + url);
                    try
                    {
                        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                            if (!resp.IsSuccessStatusCode)
                            {
                                Log("下载失败（HTTP " + (int)resp.StatusCode + "），尝试下一个源 ...");
                                continue;
                            }
                            await resp.Content.CopyToAsync(fs);
                        }
                        downloaded = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log("下载异常：" + ex.Message + "，尝试下一个源 ...");
                    }
                }
                if (!downloaded)
                    throw new Exception("所有下载源均失败（网络不可达或地址无效），请稍后重试或更换网络。");
                SetStep("下载完成，正在解压 ...", 50);
                Log("下载完成：" + new FileInfo(zipPath).Length / 1024 / 1024 + " MB");

                string extractDir = Path.Combine(MainForm.LauncherDir, "dsh-extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                string inner = Directory.GetDirectories(extractDir).FirstOrDefault();
                if (inner == null) throw new Exception("压缩包内容异常");
                if (Directory.Exists(MainForm.WorkDir)) throw new Exception("目标目录已存在：" + MainForm.WorkDir);
                Directory.Move(inner, MainForm.WorkDir);
                try { File.Delete(zipPath); Directory.Delete(extractDir, true); } catch { }
                MainForm.SaveConfig();
                SetStep("源码就绪，安装依赖（pnpm install，可能需要几分钟）...", 60);

                // 4) 安装依赖
                string pnpm = MainForm.FindPnpm();
                bool ok = await RunProcessAsync(_node, "\"" + pnpm + "\" install", MainForm.WorkDir, 1800);
                if (!ok)
                {
                    MessageBox.Show("依赖安装失败，请手动在 " + MainForm.WorkDir + " 执行 pnpm install。",
                        "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 4.5) 补装 tsdown 的 optional peer 依赖 unrun（pnpm 默认不装 optional peer，
                //      干净环境 pnpm run build 会报 Failed to import module "unrun"）
                SetStep("补装构建依赖（unrun）...", 70);
                await RunProcessAsync(_node, "\"" + pnpm + "\" add -Dw unrun", MainForm.WorkDir, 600);

                // 5) 构建（官方流程 pnpm run build：生成 web 前端产物 lib/dist，缺失会导致 MissingClientBundleError）
                SetStep("构建前端产物（pnpm run build，可能需要几分钟）...", 80);
                Log("开始构建（pnpm run build）...");
                bool built = await RunProcessAsync(_node, "\"" + pnpm + "\" run build", MainForm.WorkDir, 1800);
                if (!built)
                {
                    MessageBox.Show(
                        "构建失败（pnpm run build），服务可能无法启动。\n" +
                        "可稍后手动在 " + MainForm.WorkDir + " 执行 pnpm run build。",
                        "构建失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                SetStep("全部安装完成", 100);
                Log("安装完成！");
                MessageBox.Show("安装完成！即将进入主界面。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Completed = true;
                Close();
            }
            catch (Exception ex)
            {
                Log("安装失败：" + ex.Message);
                MessageBox.Show("自动安装失败：" + ex.Message + "\n\n可尝试：\n1. 点击「开始安装」重试（会自动切换下载源）\n2. 手动从 " + MainForm.SourceRepoUrl + " 下载后选择已有目录",
                    "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _busy = false;
                SetButtons(true);
            }
        }

        private void ChooseExisting()
        {
            if (_busy) return;
            using var dlg = new FolderBrowserDialog
            {
                Description = "请选择 DeepSeek Harness 源码目录（需包含 package.json）",
                UseDescriptionForTitle = true
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (!MainForm.HasDshSourceAt(dlg.SelectedPath))
            {
                MessageBox.Show("所选目录不是有效的 dsh 源码目录（缺少 package.json 或 apps/cli）。",
                    "目录无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            MainForm.WorkDir = dlg.SelectedPath;
            MainForm.SaveConfig();
            Log("已选择源码目录：" + dlg.SelectedPath);
            Completed = true;
            Close();
        }

        // ── UI 辅助 ─────────────────────────────────────────────
        private void SetStep(string text, int percent)
        {
            if (IsDisposed) return;
            Invoke((Action)(() =>
            {
                _step.Text = text;
                _progress.Value = Math.Max(_progress.Minimum, Math.Min(_progress.Maximum, percent));
            }));
        }

        private void SetButtons(bool enabled)
        {
            if (IsDisposed) return;
            Invoke((Action)(() =>
            {
                _btnInstall.Enabled = enabled;
                _btnChoose.Enabled = enabled;
                _btnCancel.Enabled = enabled;
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

        /// <summary>后台执行进程并把输出写入日志，返回是否成功退出。</summary>
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
