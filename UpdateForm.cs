using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshLauncher
{
    /// <summary>
    /// 自动更新窗口（前置）：检测到新版后询问用户，立即更新（git pull 或下载替换）→
    /// 重新安装依赖并构建 → 完成后进入主界面。
    ///
    /// v5.3.2 起，非 git 目录改为「文件级合并覆盖」：
    ///   - 不再整目录改名（Windows 下目录被任意进程当工作目录/持有句柄时改名必失败，
    ///     这是此前「文件被另一进程使用」导致更新卡住的根因）；
    ///   - 新源码逐个覆盖同名文件，用户自行添加的文件（新包里没有的）原样保留；
    ///   - 替换前弹确认警告，提示用户自有文件建议先手动备份；
    ///   - 更新前彻底清理占用进程（3080 监听 + 命令行指向源码目录的 node/git 进程，
    ///     覆盖「服务仍在启动、尚未监听 3080」的漏网情形）；
    ///   - 文件被占用时自动重试并指明具体路径，失败可整流程重试。
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
                    "更新将覆盖 dsh 源码并重新构建；你自己添加的文件会保留，userdata 数据不受影响。";
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
                            "也可手动从 " + MainForm.SourceRepoUrl + " 下载最新源码替换。",
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

        /// <summary>返回 true 表示更新完成；false 表示用户中途取消（静默返回，不视为失败）。</summary>
        private async Task<bool> RunUpdateCoreAsync()
        {
            // 0) 确保没有进程占用源码目录（否则覆盖会报文件被占用）
            if (!await StopDshServiceIfRunningAsync()) return false;

            _node = MainForm.FindNode();
            if (_node == null)
            {
                MessageBox.Show("未找到 Node.js（≥ v22.19），无法重新安装依赖。\n请安装 Node.js 后重试更新。",
                    "缺少 Node.js", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            string pnpm = MainForm.FindPnpm();
            if (pnpm == null)
            {
                MessageBox.Show("未找到 pnpm。请先手动执行 npm install -g pnpm，或重新安装 Node.js。",
                    "缺少 pnpm", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // 1) 更新源码：git 仓库 → git pull；非 git → 下载 zip 后文件级合并覆盖
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
                // 单轮尝试所有源（直连 → 走代理）。GitHub 限流（429/502）失败后由用户决定是否重试，
                // 通过外层 StartUpdateAsync 的「重试/取消」弹窗触发，不自动循环。
                foreach (var url in MainForm.SourceZipMirrors)
                {
                    foreach (bool useProxy in new[] { false, true })
                    {
                        Log("下载：" + url + (useProxy ? "（走代理）" : "（直连）"));
                        try
                        {
                            using (var client = MainForm.CreateHttpClient(900, useProxy))
                            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                                if (!resp.IsSuccessStatusCode)
                                {
                                    Log("HTTP " + (int)resp.StatusCode + "，尝试下一方式/源 ...");
                                    continue;
                                }
                                await resp.Content.CopyToAsync(fs);
                            }
                            downloaded = true;
                            Log("下载成功：" + url);
                            break;
                        }
                        catch (Exception ex) { Log("下载异常：" + ex.Message + "，尝试下一方式/源 ..."); }
                    }
                    if (downloaded) break;
                }
                if (!downloaded) throw new Exception("下载最新源码失败（所有下载源均不可用）");

                // 替换前确认：告知会覆盖同名文件，用户自有文件建议先手动备份
                var warn = MessageBox.Show(
                    "即将用新版源码覆盖 " + MainForm.WorkDir + " 中的同名文件（包括你自行修改过的仓库文件）。\n\n" +
                    "你自己添加的文件（新源码包中没有的，如 _launcher_build*、.agents 等）会自动保留，\n" +
                    "但建议先手动备份以防万一。\n\n是否继续更新？",
                    "更新前确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (warn != DialogResult.OK)
                {
                    Log("用户取消更新（替换前确认）。");
                    return false;
                }

                SetStep("解压新源码 ...", 35);
                string extractDir = Path.Combine(MainForm.LauncherDir, "dsh-update-extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                string inner = Directory.GetDirectories(extractDir).FirstOrDefault();
                if (inner == null) throw new Exception("压缩包内容异常");

                SetStep("覆盖新源码（你的自有文件会保留）...", 45);
                if (!await MergeNewSourceAsync(inner)) return false;
                try { File.Delete(zipPath); Directory.Delete(extractDir, true); } catch { }
                Log("新源码已就位。");
            }

            // 2) 重新安装依赖 + 构建
            SetStep("安装依赖（pnpm install）...", 60);
            if (!await RunProcessAsync(_node, "\"" + pnpm + "\" install", MainForm.WorkDir, 1800))
                throw new Exception("依赖安装失败（pnpm install）");

            // 补装 tsdown 的 optional peer 依赖 unrun（dsh 的 pnpm run build 依赖它，
            // 但 pnpm 默认不装 optional peer，干净环境直接 build 会报 Failed to import module "unrun"）。
            // 幂等：已装则跳过；失败不阻断（若 build 确需它，下面 build 会给出明确错误）。
            SetStep("补装构建依赖（unrun）...", 70);
            if (!await RunProcessAsync(_node, "\"" + pnpm + "\" add -Dw unrun", MainForm.WorkDir, 600))
                Log("补装 unrun 失败（忽略，继续构建）。");

            SetStep("构建（pnpm run build）...", 85);
            if (!await RunProcessAsync(_node, "\"" + pnpm + "\" run build", MainForm.WorkDir, 1800))
                throw new Exception("构建失败（pnpm run build）");

            SetStep("更新完成", 100);
            Log("更新完成！");
            MessageBox.Show("更新完成！即将进入主界面。", "更新成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Completed = true;
            Close();
            return true;
        }

        // ── 占用进程清理 ──────────────────────────────────────────

        /// <summary>检测并停止占用源码目录的进程；用户拒绝则返回 false（中止更新）。</summary>
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

            // 2) 命令行指向源码目录的 node/git 进程（覆盖“服务仍在启动、尚未监听 3080”的漏网情形）
            try
            {
                string wd = MainForm.WorkDir.Replace("'", "''");
                string script =
                    "Get-CimInstance Win32_Process | Where-Object { " +
                    "($_.CommandLine -match [regex]::Escape('" + wd + "')) -or " +
                    "($_.CommandLine -match 'apps[\\/]cli[\\/]src[\\/]bin\\.ts') " +
                    "} | ForEach-Object { [string]$_.ProcessId + '|' + $_.Name }";
                foreach (var line in RunPowershellCapture(script, out int psPid))
                {
                    var kv = line.Split('|');
                    if (kv.Length >= 2 && int.TryParse(kv[0], out int pidNum) && pidNum > 0
                        && pidNum != Environment.ProcessId && pidNum != psPid && !pids.Contains(pidNum))
                    {
                        pids.Add(pidNum);
                        names.Add(kv[1] + "（PID " + pidNum + "，指向 " + MainForm.WorkDir + "）");
                    }
                }
            }
            catch (Exception ex) { Log("检查进程占用失败：" + ex.Message); }

            if (pids.Count == 0) return true;

            var r = MessageBox.Show(
                "检测到以下进程正在占用源码目录，更新前需要先停止它们：\n\n" +
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

        /// <summary>执行一段 PowerShell 脚本并返回逐行输出；失败返回空列表。selfPid 返回本次启动的 powershell 进程 ID（查询本身会匹配到自己，需在结果中排除）。</summary>
        private List<string> RunPowershellCapture(string script, out int selfPid)
        {
            selfPid = 0;
            var result = new List<string>();
            try
            {
                string args = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" +
                              script.Replace("\"", "\\\"") + "\"";
                var psi = new ProcessStartInfo("powershell.exe", args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    // stderr 不重定向：避免管道缓冲填满导致与 stdout 读取互相等待（死锁）
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) return result;
                selfPid = p.Id;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(20000);
                foreach (var line in output.Split('\n'))
                {
                    string s = line.Trim();
                    if (s.Length > 0) result.Add(s);
                }
            }
            catch (Exception ex) { Log("枚举占用进程失败：" + ex.Message); }
            return result;
        }

        // ── 文件级合并覆盖 ────────────────────────────────────────

        /// <summary>把 newRoot 下的源码文件级合并覆盖到 WorkDir；返回 false = 用户取消。
        /// 合并（清理残留 + 逐个覆盖）在后台线程执行，避免更新窗口卡成「无响应」。</summary>
        private async Task<bool> MergeNewSourceAsync(string newRoot)
        {
            string workDir = MainForm.WorkDir;
            Directory.CreateDirectory(workDir);

            // 耗时操作放后台线程：本机源码目录体量很大（node_modules 内含大量 pnpm 联接），
            // 且逐文件覆盖受杀软实时扫描影响，同步执行会把更新窗口卡成「无响应」。
            var failed = await Task.Run(() => MergeNewSourceCore(newRoot, workDir));

            if (failed.Count > 0)
            {
                string list = string.Join("\n", failed.Take(6));
                var r = MessageBox.Show(
                    "以下文件被其他进程占用，重试后仍无法更新：\n\n" + list +
                    (failed.Count > 6 ? "\n…共 " + failed.Count + " 个" : "") +
                    "\n\n【是】跳过这些文件继续\n【否】取消更新",
                    "文件被占用", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes)
                {
                    Log("用户取消更新（文件被占用）。");
                    return false;
                }
                Log("已跳过 " + failed.Count + " 个被占用文件。");
            }
            return true;
        }

        /// <summary>后台线程执行的合并：先清理残留（剪枝 node_modules/.git），再逐文件覆盖（占用自动重试）。</summary>
        private List<string> MergeNewSourceCore(string newRoot, string workDir)
        {
            var failed = new List<string>();

            // 1) 同步清理：仅新包顶层存在的目录内，删除新包中不存在的残留文件。
            //    手动递归并在 node_modules/.git 处剪枝——这些目录含 pnpm 联接（符号链接），
            //    SearchOption.AllDirectories 会跟随联接遍历整个依赖图，导致卡死甚至死循环。
            try { DeleteStaleFiles(newRoot, workDir); }
            catch (Exception ex) { Log("清理残留文件失败（忽略）：" + ex.Message); }

            // 2) 逐个文件覆盖；被占用文件自动重试，仍失败则报告具体路径
            var files = Directory.GetFiles(newRoot, "*", SearchOption.AllDirectories);
            var queue = new Queue<string>(files);
            int attempt = 0;
            while (queue.Count > 0 && attempt < 5)
            {
                attempt++;
                int round = queue.Count;
                for (int i = 0; i < round; i++)
                {
                    string src = queue.Dequeue();
                    string rel = Path.GetRelativePath(newRoot, src);
                    string dst = Path.Combine(workDir, rel);
                    try
                    {
                        string dir = Path.GetDirectoryName(dst);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        if (File.Exists(dst))
                        {
                            try { File.SetAttributes(dst, FileAttributes.Normal); } catch { }
                        }
                        File.Copy(src, dst, true);
                        if (i % 500 == 0)
                            SetStep("覆盖新源码 " + (files.Length - queue.Count) + "/" + files.Length + " ...", 45);
                    }
                    catch (Exception ex)
                    {
                        if (attempt >= 5) failed.Add(dst + "（" + ex.Message + "）");
                        else queue.Enqueue(src);   // 稍后重试（可能是杀软/索引器瞬时占用）
                    }
                }
                if (queue.Count > 0)
                {
                    Log("仍有 " + queue.Count + " 个文件被占用，2 秒后重试（第 " + attempt + " 次）...");
                    System.Threading.Thread.Sleep(2000);
                }
            }
            return failed;
        }

        /// <summary>删除旧目录中「新包顶层目录内且新包已不存在的」残留文件。
        /// 手动递归并在 node_modules/.git 处剪枝（pnpm 联接/符号链接不可遍历）。</summary>
        private static void DeleteStaleFiles(string newRoot, string workDir)
        {
            foreach (var newTop in Directory.GetDirectories(newRoot))
            {
                string topName = Path.GetFileName(newTop);
                string oldTop = Path.Combine(workDir, topName);
                if (!Directory.Exists(oldTop)) continue;
                var newFiles = Directory.GetFiles(newTop, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(newTop, f))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                DeleteStaleRecursive(oldTop, oldTop, newFiles);
            }
        }

        private static void DeleteStaleRecursive(string oldTop, string current, HashSet<string> keepRel)
        {
            foreach (var file in Directory.GetFiles(current))
            {
                string rel = Path.GetRelativePath(oldTop, file);
                if (keepRel.Contains(rel)) continue;
                try { File.Delete(file); }
                catch { /* 被占用/只读则跳过，不影响整体 */ }
            }
            foreach (var sub in Directory.GetDirectories(current))
            {
                string name = Path.GetFileName(sub);
                if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
                DeleteStaleRecursive(oldTop, sub, keepRel);
            }
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
