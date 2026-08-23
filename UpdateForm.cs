using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshLauncher
{
    /// <summary>
    /// 自动更新窗口（前置）：检测到新版后询问用户，立即更新。
    /// npm 方式：更新 = npm install -g @deepseek-ai/dsh（拉取最新全局包），无需下载源码、无需本地构建。
    /// </summary>
    internal class UpdateForm : Form
    {
        private readonly Label _lblInfo = new Label { AutoSize = false, Height = 48 };
        private readonly Button _btnUpdate = new Button { Text = "立即更新", Width = 96, Height = 30 };
        private readonly Button _btnSkip = new Button { Text = "跳过", Width = 96, Height = 30 };
        private readonly Button _btnChoose = new Button { Text = "选择目录", Width = 104, Height = 30 };
        private readonly Button _btnExit = new Button { Text = "退出", Width = 104, Height = 30 };
        private readonly ProgressBar _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 16 };
        private readonly TextBox _log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f), BackColor = Color.White
        };
        private readonly Label _step = new Label { AutoSize = false, Height = 22, Width = 432 };

        private readonly string _localVersion;
        private string _remoteVersion;
        private readonly string _knownRemote;   // 已知的新版号（主窗口手动检查传入）；null 表示需自行检查
        private bool _busy;
        private string _node;

        /// <summary>更新完成并成功为 true。</summary>
        public bool Completed { get; private set; }

        public UpdateForm(string localVersion) : this(localVersion, null) { }

        /// <summary>
        /// localVersion 当前版本；remoteVersion 非 null 表示已确认存在新版（主窗口手动检查场景），
        /// 直接进入「发现新版」界面；为 null 时窗口内自行联网检查（启动时场景，立即显示检查状态）。
        /// </summary>
        public UpdateForm(string localVersion, string remoteVersion)
        {
            _localVersion = localVersion;
            _knownRemote = remoteVersion;
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
            _lblInfo.Text = "正在检查更新 ...\n本地版本：" + _localVersion;

            _btnUpdate.Location = new Point(14, 70);
            _btnSkip.Location = new Point(116, 70);
            _btnChoose.Location = new Point(218, 70);
            _btnExit.Location = new Point(328, 70);
            _btnUpdate.Click += async (s, e) => await StartUpdateAsync();
            _btnSkip.Click += (s, e) => Close();
            _btnChoose.Click += (s, e) => ChooseDshDirectory();
            // 「退出」：直接退出程序，不进入主界面/不启动服务（用于更新失败时快速退出）
            _btnExit.Click += (s, e) => Application.Exit();

            _step.Location = new Point(14, 108);
            _progress.Location = new Point(14, 134);
            _progress.Width = 432;
            _log.Location = new Point(14, 156);
            _log.Size = new Size(432, 268);

            Controls.Add(_lblInfo);
            Controls.Add(_btnUpdate);
            Controls.Add(_btnSkip);
            Controls.Add(_btnChoose);
            Controls.Add(_btnExit);
            Controls.Add(_step);
            Controls.Add(_progress);
            Controls.Add(_log);

            // 初始状态：若已传入已知新版则直接进入「发现新版」；否则进入「检查中」状态。
            // 检查阶段「跳过」始终可用：用户可随时跳过检查直接进入主窗口（检查仅发 HTTP 读版本号，
            // 不动源码不启服务，不存在进程互相干扰；窗口关闭后异步检查结果会被 IsDisposed 丢弃）。
            if (_knownRemote != null)
            {
                ApplyCheckResult(_knownRemote);
            }
            else
            {
                _btnUpdate.Enabled = false;   // 检查未完成前不知道是否有新版，禁用「立即更新」
                // 检查阶段进度条保持 0（进度条仅用于实际更新过程）
                SetStep("正在检查更新 ...", _progress.Minimum);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_knownRemote == null)
                _ = CheckForUpdatesAsync();   // 后台异步检查，UI 即时响应
        }

        /// <summary>后台检查是否有新版；完成后更新界面状态。
        /// 带 15s 总超时：无网络/慢网络时不至于让更新窗口长时间卡住（按检查失败处理，进入主界面）。</summary>
        private async Task CheckForUpdatesAsync()
        {
            string result = null;
            try
            {
                var check = Task.Run(MainForm.CheckVersionRemote);
                // 若网络不通，多个候选源逐个超时会拖很久；这里限制总等待时长
                var done = await Task.WhenAny(check, Task.Delay(15000)) == check;
                if (done) result = await check;
            }
            catch
            {
                result = null;
            }
            if (IsDisposed) return;
            Invoke((Action)(() => ApplyCheckResult(result)));
        }

        /// <summary>根据检查结果切换界面：有新版→可更新；无/失败→短暂显示后自动关闭。
        /// 检查阶段结束即把进度条归零（进度条仅用于实际更新过程），避免“检查时走进度条”的突兀感。</summary>
        private void ApplyCheckResult(string result)
        {
            _progress.Value = _progress.Minimum;   // 检测结束，进度条重置
            if (result != null && result != "latest")
            {
                _remoteVersion = result;
                _lblInfo.Text = "发现新版本 " + _remoteVersion + "（本地 " + _localVersion + "）\n" +
                    "更新将执行 npm install -g @deepseek-ai/dsh；你的聊天记录与 userdata 数据不受影响。";
                _btnUpdate.Enabled = true;
                _btnSkip.Enabled = true;
                SetStep("发现新版本，可点击「立即更新」", _progress.Minimum);
                Log("发现新版本：" + result + "（本地 " + _localVersion + "）");
                return;
            }
            // 无新版或检查失败：简短展示结果后自动进入主界面
            if (result == "latest")
            {
                _lblInfo.Text = "已是最新版本（" + _localVersion + "）。";
                SetStep("已是最新版本，即将进入主界面 ...", _progress.Minimum);
            }
            else
            {
                _lblInfo.Text = "无法检查更新（网络或仓库不可达）。\n可稍后展开操作栏「检查更新」重试。";
                SetStep("检查失败，即将进入主界面 ...", _progress.Minimum);
            }
            Log(_lblInfo.Text);
            // 短暂停留，让用户看到检查结果，再自动关闭进入主界面
            var t = new System.Windows.Forms.Timer { Interval = 1200 };
            t.Tick += (s2, e2) => { t.Stop(); Close(); };
            t.Start();
        }

        /// <summary>选择并切换 dsh 源码目录（更新窗口内手动指定，供用户自行更新后指向新目录）。</summary>
        private void ChooseDshDirectory()
        {
            string path = MainForm.SelectAndSetDshDirectory(this);
            if (path == null) return;   // 用户取消或目录无效
            _lblInfo.Text = "已选择 dsh 源码目录：\n" + path;
            SetStep("本地版本：" + MainForm.LocalVersion, 5);
            Log("已选择 dsh 源码目录：" + path);
            // 重新检查更新（指向新目录后版本可能不同）
            _ = CheckForUpdatesAsync();
        }

        private async Task StartUpdateAsync()
        {
            if (_busy) return;
            _busy = true;
            _btnUpdate.Enabled = false;
            _btnSkip.Enabled = false;
            try
            {
                bool finished = false;   // true = 更新完成；false = 用户中途取消
                while (!finished)
                {
                    try
                    {
                        finished = await RunUpdateCoreAsync();
                        if (!finished) break;          // 用户取消，静默退出
                    }
                    catch (Exception ex)
                    {
                        Log("更新失败：" + ex.Message);
                        // 失败时进度条复位，避免停在中间值造成“仍在尝试”的错觉
                        _progress.Value = _progress.Minimum;
                        var r = MessageBox.Show(
                            "更新失败：" + ex.Message + "\n\n" +
                            "【重试】重新执行整个更新流程\n【取消】放弃本次更新\n\n" +
                            "也可在终端手动执行：npm install -g @deepseek-ai/dsh",
                            "更新失败", MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                        if (r != DialogResult.Retry) break;
                    }
                }
            }
            finally
            {
                _busy = false;
                if (!IsDisposed) { _btnUpdate.Enabled = true; _btnSkip.Enabled = true; }
            }
        }

        /// <summary>返回 true 表示更新完成；false 表示用户中途取消（静默返回，不视为失败）。
        /// npm 方式：更新 = npm install -g @deepseek-ai/dsh（拉取最新全局包），无需源码构建。</summary>
        private async Task<bool> RunUpdateCoreAsync()
        {
            _node = MainForm.FindManagedNode() ?? MainForm.FindNode();
            if (_node == null)
            {
                MessageBox.Show("未找到 Node.js（≥ v22.19），无法更新 dsh。\n请先在托盘菜单「升级 Node.js 到 v24」安装。",
                    "缺少 Node.js", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            string npmCli = Path.Combine(Path.GetDirectoryName(_node), "node_modules", "npm", "bin", "npm-cli.js");
            string prefix = MainForm.FindNpmGlobalPrefix(_node);

            // 停止 dsh 服务（避免更新后旧进程使用旧包）
            if (!await StopDshServiceIfRunningAsync()) return false;

            SetStep("正在更新 dsh（npm install -g @deepseek-ai/dsh）...", 30);
            Log("运行：" + _node + " " + npmCli + " install -g @deepseek-ai/dsh");
            bool ok = await RunNpmGlobalAsync(_node, npmCli, prefix,
                "install -g @deepseek-ai/dsh", 600);
            if (!ok) throw new Exception("npm install -g @deepseek-ai/dsh 失败（请查看上方日志或检查网络）");

            string newVer = MainForm.LocalVersion;   // 重新读取全局包版本
            SetStep("更新完成", 100);
            Log("更新完成！当前 dsh 版本：" + newVer);
            MessageBox.Show("dsh 更新完成（版本 " + newVer + "）！\n即将进入主界面。", "更新成功",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            Completed = true;
            Close();
            return true;
        }

        /// <summary>用自管理 node 执行 npm 全局命令（install -g / update -g），传 npm_config_prefix，事件异步读取输出避免死锁。</summary>
        private async Task<bool> RunNpmGlobalAsync(string node, string npmCli, string prefix, string args, int timeoutSec)
        {
            var tcs = new TaskCompletionSource<bool>();
            var psi = new ProcessStartInfo
            {
                FileName = node,
                Arguments = "\"" + npmCli + "\" " + args,
                WorkingDirectory = MainForm.LauncherDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (!string.IsNullOrEmpty(prefix)) psi.Environment["npm_config_prefix"] = prefix;
            psi.Environment["NODE_OPTIONS"] = "--use-system-ca";
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
                Log("npm 命令超时（" + timeoutSec + " 秒），已终止。");
                return false;
            }
            return await tcs.Task;
        }

        // ── 占用进程清理 ──────────────────────────────────────────

        /// <summary>检测并停止占用端口/服务的进程；用户拒绝则返回 false（中止更新）。</summary>
        private async Task<bool> StopDshServiceIfRunningAsync()
        {
            var pids = new List<int>();
            var names = new List<string>();

            // 1) 3080 端口监听进程（正在运行/已就绪的 dsh 服务）
            try
            {
                var psi = new ProcessStartInfo("netstat.exe", "-ano")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                foreach (var line in output.Split('\n'))
                {
                    if (!line.Contains(":3080") || !line.Contains("LISTENING")) continue;
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string pid = parts.Length > 0 ? parts[parts.Length - 1] : null;
                    if (int.TryParse(pid, out int pidNum) && pidNum > 0 && !pids.Contains(pidNum))
                    {
                        pids.Add(pidNum);
                        names.Add("端口 3080 监听进程（PID " + pidNum + "）");
                    }
                }
            }
            catch (Exception ex) { Log("检查端口占用失败：" + ex.Message); }

            if (pids.Count == 0) return true;

            var r = MessageBox.Show(
                "检测到以下进程正在占用端口，更新前需要先停止它们：\n\n" +
                string.Join("\n", names) + "\n\n是否继续？",
                "需要停止占用进程", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes)
            {
                Log("用户取消更新（未停止占用进程）。");
                return false;
            }
            foreach (int pid in pids)
            {
                try
                {
                    var kill = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var kp = Process.Start(kill);
                    kp?.WaitForExit(8000);
                    Log("已停止进程（PID " + pid + "）。");
                }
                catch (Exception ex) { Log("停止进程 " + pid + " 失败：" + ex.Message); }
            }
            await Task.Delay(1500);   // 等待文件句柄释放
            return true;
        }

        private void SetStep(string text, int percent)
        {
            if (IsDisposed) return;
            int v = Math.Max(_progress.Minimum, Math.Min(_progress.Maximum, percent));
            if (!IsHandleCreated)
            {
                // 窗口句柄尚未创建（如构造函数阶段）：直接赋值，避免 Invoke 抛异常
                _step.Text = text;
                _progress.Value = v;
                return;
            }
            Invoke((Action)(() =>
            {
                _step.Text = text;
                _progress.Value = v;
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
    }
}
