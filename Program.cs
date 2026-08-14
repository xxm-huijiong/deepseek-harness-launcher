using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace DshLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm.LoadConfig();   // 读取 config.json（可重定向 dsh 源码目录）
            Application.Run(new MainForm());
        }
    }

    internal class MainForm : Form
    {
        // ── 路径配置（以启动器所在目录为根，随 exe 位置，放任意盘/目录均可） ──
        private static readonly string LauncherDir = Path.GetDirectoryName(Environment.ProcessPath) ?? @"D:\dsh-launcher";
        private static string WorkDir = Path.Combine(LauncherDir, "dsh-src");          // dsh 源码目录（可由 config.json 的 workDir 重定向）
        private static readonly string ConfigFile = Path.Combine(LauncherDir, "config.json");
        private static readonly string DshHomeDir = Path.Combine(LauncherDir, "userdata");
        private const string LegacyHomeDir = @"C:\Users\wujiong\.dsh";                 // 旧版默认 home（仅本机迁移用，其他机器无此目录会自动跳过）
        private static readonly string LogFile = Path.Combine(LauncherDir, "launcher.log");
        private static readonly string WebViewDataDir = Path.Combine(LauncherDir, "webview-data-v3");
        private const string SourceRepoUrl = @"https://github.com/deepseek-ai/deepseek-harness";                    // dsh 官方仓库
        private const string SourceZipDirect = @"https://github.com/deepseek-ai/deepseek-harness/archive/refs/heads/main.zip";          // 直连下载
        private const string SourceZipMirror = @"https://ghproxy.net/https://github.com/deepseek-ai/deepseek-harness/archive/refs/heads/main.zip";  // 国内镜像下载

        private const int Port = 3080;
        private const string UiUrl = @"http://127.0.0.1:3080/web/";

        // 布局常量
        private const int StatusBarHeight = 22;   // 底部状态条
        private const int ActionsBarHeight = 48;  // 底部操作栏（展开时）
        private const int LogPanelHeight = 168;   // 底部日志面板（展开时）

        // Node / pnpm 候选（按优先级；PATH 优先，以下作为回退）
        private static readonly string[] NodeCandidates =
        {
            @"C:\Users\wujiong\.workbuddy\binaries\node\versions\22.22.2\node.exe",   // 已知满足 ^22.19
            @"D:\nodejs\node.exe"                                                      // 回退
        };
        private const string PnpmCjs = @"D:\npm-global\node_modules\pnpm\bin\pnpm.cjs"; // 本机回退路径

        // ── 控件 ──────────────────────────────────────────────────
        private Panel _statusBar;            // 底部状态条
        private Label _statusLabel;
        private Label _btnToggleActions;     // 展开/收起操作栏（Label 实现，无按钮边距裁剪）
        private Label _btnToggleLog;         // 展开/收起日志

        private Panel _actionsPanel;         // 底部操作栏（默认收起）
        private Button _btnStart;
        private Button _btnStop;
        private Button _btnRefresh;
        private Button _btnExternal;
        private Button _btnBackup;
        private Button _btnCheck;            // 检查更新
        private CheckBox _chkAutoStart;
        private CheckBox _chkAutoCheck;      // 启动时检查更新

        private Panel _logPanel;             // 底部日志面板（默认收起）
        private TextBox _logBox;

        private WebView2 _web;
        private PictureBox _splash;          // 启动画面（等待期随机图片）
        private NotifyIcon _trayIcon;        // 系统托盘图标
        private ToolStripMenuItem _menuStart; // 托盘菜单：启动服务
        private ToolStripMenuItem _menuStop;  // 托盘菜单：停止服务
        private bool _forceExit;             // 托盘「退出」触发真正退出
        private bool _trayNotified;          // 首次隐藏到托盘的气泡提示已显示

        // ── 运行状态 ──────────────────────────────────────────────
        private Process _managedServer;
        private bool _externalInstance;
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private readonly System.Windows.Forms.Timer _statusTimer;
        private readonly System.Windows.Forms.Timer _bootWatchTimer;
        private bool _uiLoaded;
        private int _bootFailCount;
        private bool _shuttingDown;
        private bool _startRequested;

        public MainForm()
        {
            Text = "dsh-launcher";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(1100, 720);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            LayoutPanels();

            _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _statusTimer.Tick += StatusTick;
            _statusTimer.Start();

            _bootWatchTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _bootWatchTimer.Tick += BootWatchTick;

            // 系统托盘：关闭窗口后缩到右下角，服务继续运行；右键可退出
            _trayIcon = new NotifyIcon
            {
                Icon = Icon,
                Visible = true,
                Text = "dsh-launcher"
            };
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("显示主窗口", null, (s, e) => ShowMainWindow());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(_menuStart = new ToolStripMenuItem("启动服务", null, (s, e) => _ = StartServerAsync()));
            trayMenu.Items.Add(_menuStop = new ToolStripMenuItem("停止服务", null, (s, e) => StopServer()));
            trayMenu.Items.Add("刷新页面", null, (s, e) => ReloadUi());
            trayMenu.Items.Add("浏览器打开", null, (s, e) => OpenExternalBrowser());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("备份数据", null, (s, e) => BackupData());
            trayMenu.Items.Add("检查更新", null, (s, e) => CheckForUpdates(interactive: true));
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出", null, (s, e) => ExitApp());
            _trayIcon.ContextMenuStrip = trayMenu;
            _trayIcon.DoubleClick += (s, e) => ShowMainWindow();

            Shown += OnShown;
            FormClosing += OnFormClosing;
            Resize += (s, e) => LayoutPanels();
        }

        // ── 界面：手动布局（WebView2 严格占据剩余空间，不与底部栏冲突） ──
        private void BuildUi()
        {
            // 底部状态条（浅色贴近系统）
            _statusBar = new Panel { BackColor = SystemColors.Control, Height = StatusBarHeight };
            _statusBar.Paint += (s, e) => e.Graphics.DrawLine(
                new Pen(SystemColors.ControlDark), 0, 0, _statusBar.Width, 0);
            _statusBar.Controls.Add(_statusLabel = new Label
            {
                Text = "● 检测中 ...",
                ForeColor = Color.FromArgb(60, 60, 60),
                AutoSize = false,
                Location = new Point(10, 0),
                Size = new Size(640, StatusBarHeight),
                Font = new Font(Font.FontFamily, 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            });
            _statusBar.Controls.Add(_btnToggleLog = MakeBarButton("日志", 44, (x) => ToggleLog()));
            _statusBar.Controls.Add(_btnToggleActions = MakeBarButton("≡ 操作", 56, (x) => ToggleActions()));
            _statusBar.Resize += (s, e) =>
            {
                int x = _statusBar.Width - 8;
                foreach (var b in new[] { _btnToggleLog, _btnToggleActions })
                {
                    x -= b.Width;
                    b.Location = new Point(x, (_statusBar.Height - b.Height) / 2);
                }
            };
            Controls.Add(_statusBar);

            // 底部操作栏（默认收起）
            _actionsPanel = new Panel { Visible = false, Height = ActionsBarHeight, BackColor = SystemColors.Control };
            _actionsPanel.Paint += (s, e) => e.Graphics.DrawLine(
                new Pen(SystemColors.ControlDark), 0, 0, _actionsPanel.Width, 0);
            _actionsPanel.Controls.Add(_btnStart = MakeActionButton("启动服务", 12, 10, (x) => _ = StartServerAsync()));
            _actionsPanel.Controls.Add(_btnStop = MakeActionButton("停止服务", 108, 10, (x) => StopServer()));
            _actionsPanel.Controls.Add(_btnRefresh = MakeActionButton("刷新", 204, 10, (x) => ReloadUi()));
            _actionsPanel.Controls.Add(_btnExternal = MakeActionButton("外部浏览器", 300, 10, (x) => OpenExternalBrowser()));
            _actionsPanel.Controls.Add(_btnBackup = MakeActionButton("备份数据", 396, 10, (x) => BackupData()));
            _actionsPanel.Controls.Add(_btnCheck = MakeActionButton("检查更新", 492, 10, (x) => CheckForUpdates(interactive: true)));
            _actionsPanel.Controls.Add(_chkAutoStart = new CheckBox
            {
                Text = "启动时自动运行",
                Location = new Point(596, 13),
                Size = new Size(130, 22),
                Checked = true,
                Font = new Font(Font.FontFamily, 9f)
            });
            _actionsPanel.Controls.Add(_chkAutoCheck = new CheckBox
            {
                Text = "启动时检查更新",
                Location = new Point(734, 13),
                Size = new Size(150, 22),
                Checked = true,
                Font = new Font(Font.FontFamily, 9f)
            });
            Controls.Add(_actionsPanel);

            // 内置浏览器（手动布局）
            _web = new WebView2();
            Controls.Add(_web);

            // 启动画面（服务就绪前显示随机图片；GIF 由 PictureBox 原生播放动画）
            _splash = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White,
                Visible = false
            };
            Controls.Add(_splash);

            // 底部日志面板（默认收起）
            _logPanel = new Panel { Visible = false, Height = LogPanelHeight, BackColor = SystemColors.Control };
            _logPanel.Controls.Add(new Label
            {
                Text = "运行日志",
                Location = new Point(10, 5),
                Size = new Size(120, 20),
                Font = new Font(Font.FontFamily, 9f, FontStyle.Bold)
            });
            _logPanel.Controls.Add(_logBox = new TextBox
            {
                Location = new Point(10, 26),
                Size = new Size(_logPanel.Width - 20, _logPanel.Height - 34),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                BackColor = Color.White,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom
            });
            Controls.Add(_logPanel);
        }

        /// <summary>手动布局：从下往上排 状态条/操作栏/日志，WebView2 占据全部剩余空间。</summary>
        private void LayoutPanels()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            int bottom = h;

            _statusBar.Bounds = new Rectangle(0, bottom - StatusBarHeight, w, StatusBarHeight);
            bottom -= StatusBarHeight;

            if (_actionsPanel.Visible)
            {
                _actionsPanel.Bounds = new Rectangle(0, bottom - ActionsBarHeight, w, ActionsBarHeight);
                bottom -= ActionsBarHeight;
            }

            if (_logPanel.Visible)
            {
                _logPanel.Bounds = new Rectangle(0, bottom - LogPanelHeight, w, LogPanelHeight);
                bottom -= LogPanelHeight;
            }

            _web.Bounds = new Rectangle(0, 0, w, Math.Max(120, bottom));

            // 启动画面与浏览器同区域，保持同步
            _splash.Bounds = _web.Bounds;
        }

        private Label MakeBarButton(string text, int width, Action<object> onClick)
        {
            var l = new Label
            {
                Text = text,
                Width = width,
                Height = 20,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(60, 60, 60),
                Font = new Font(Font.FontFamily, 9f),
                Cursor = Cursors.Hand,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            l.Click += (s, e) => onClick(s);
            return l;
        }

        private Button MakeActionButton(string text, int x, int y, Action<object> onClick)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(84, 28),
                Font = new Font(Font.FontFamily, 9f),
                Cursor = Cursors.Hand
            };
            b.Click += (s, e) => onClick(s);
            return b;
        }

        // ── 折叠面板切换（底部弹出，浏览器自动让位） ───────────────
        private void ToggleActions()
        {
            _actionsPanel.Visible = !_actionsPanel.Visible;
            _btnToggleActions.Text = _actionsPanel.Visible ? "≡ 收起" : "≡ 操作";
            LayoutPanels();
        }

        private void ToggleLog()
        {
            _logPanel.Visible = !_logPanel.Visible;
            _btnToggleLog.Text = _logPanel.Visible ? "日志 ✓" : "日志";
            LayoutPanels();
            if (_logPanel.Visible)
            {
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
        }

        // ── 生命周期 ──────────────────────────────────────────────
        private async void OnShown(object sender, EventArgs e)
        {
            LayoutPanels();
            Log("启动器就绪。");
            // 等待期内先隐藏浏览器并显示随机启动画面
            _web.Visible = false;
            ShowSplash();
            Diagnose("OnShown: 开始初始化内置浏览器");
            try
            {
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null, userDataFolder: WebViewDataDir);
                Diagnose("OnShown: env 创建成功");
                await _web.EnsureCoreWebView2Async(env);
                Diagnose("OnShown: EnsureCoreWebView2Async 成功");
                Log("内置浏览器已初始化。");
            }
            catch (Exception ex)
            {
                Diagnose("OnShown: 内置浏览器初始化失败 - " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
                Log("内置浏览器初始化失败（需安装 WebView2 Runtime）：" + ex.Message);
            }

            if (!HasDshSource())
            {
                // 首次使用：进入安装引导（自动下载 / 手动选择目录）
                Log("未找到 dsh 源码（" + WorkDir + "），进入首次安装引导 ...");
                await RunInstallFlowAsync();
                return;
            }

            if (_chkAutoStart.Checked)
                _ = StartServerAsync();

            // 启动时检查更新（静默模式：有更新才提示）
            if (_chkAutoCheck.Checked)
                _ = CheckForUpdatesAsync(interactive: false);
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_shuttingDown) return;

            // 点击「✕」关闭 → 隐藏到托盘，服务继续后台运行（不退出）
            if (!_forceExit)
            {
                e.Cancel = true;
                Hide();
                if (!_trayNotified)
                {
                    _trayNotified = true;
                    _trayIcon.ShowBalloonTip(2500, "dsh-launcher",
                        "已最小化到系统托盘，服务仍在后台运行。\n右键托盘图标可退出。",
                        ToolTipIcon.Info);
                }
                return;
            }

            // 托盘菜单「退出」→ 真正退出
            bool running = _managedServer != null && !_managedServer.HasExited;
            if (running)
            {
                var r = MessageBox.Show(
                    "服务正在运行中。\n退出将停止服务并关闭后台进程，确定吗？",
                    "确认退出",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    _forceExit = false;   // 取消退出：恢复正常「✕=缩到托盘」行为
                    e.Cancel = true;
                    return;
                }
            }
            _shuttingDown = true;
            _statusTimer.Stop();
            _bootWatchTimer.Stop();
            StopServer();
            try { _trayIcon.Visible = false; _trayIcon.Dispose(); } catch { }
            try { _web?.Dispose(); } catch { }
            try { _http.Dispose(); } catch { }
        }

        private void ShowMainWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitApp()
        {
            _forceExit = true;
            Close();
        }

        // ── 服务管理 ──────────────────────────────────────────────
        private async Task StartServerAsync()
        {
            if (_startRequested) return;
            _startRequested = true;
            try
            {
                if (_managedServer != null && !_managedServer.HasExited)
                {
                    Log("服务已由本启动器运行中。");
                    _uiLoaded = false;
                    LoadUi();
                    return;
                }

                // 1) 端口探测：已有 dsh 实例则直接使用，外部占用则报错
                var probe = await ProbeDshAsync();
                if (probe == ProbeState.DshReady)
                {
                    _externalInstance = true;
                    Log("检测到端口 3080 已有 dsh 实例在运行，直接连接。");
                    SetUiRunning(true, managed: false);
                    LoadUi();
                    return;
                }
                if (probe == ProbeState.Other)
                {
                    Log("错误：端口 3080 被其他程序占用，请先释放该端口。");
                    SetUiRunning(false, managed: false);
                    return;
                }

                // 2) 本启动器拉起服务
                string node = FindNode();
                if (node == null) { Log("错误：未找到可用的 node（需要 ≥ v22.19，请安装 Node.js）。"); return; }
                string pnpm = FindPnpm();
                if (pnpm == null) { Log("错误：未找到 pnpm（请先执行 npm install -g pnpm）。"); return; }

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = node,
                        Arguments = "\"" + pnpm + "\" dsh web",
                        WorkingDirectory = WorkDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    // 用户数据统一收敛到 D:\dsh-launcher\userdata（必要时从旧目录自动迁移）
                    EnsureHomeMigrated();
                    psi.Environment["DSH_HOME"] = DshHomeDir;
                    // 清掉外部注入的“安全删除”钩子，避免 pnpm 清理临时文件时报 trash 失败
                    psi.Environment["NODE_OPTIONS"] = "--use-system-ca";

                    _managedServer = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    _managedServer.OutputDataReceived += (s, ev) => { if (ev.Data != null) Log(ev.Data); };
                    _managedServer.ErrorDataReceived += (s, ev) => { if (ev.Data != null) Log("[err] " + ev.Data); };
                    _managedServer.Exited += (s, ev) => { if (!_shuttingDown) Log("服务进程已退出。"); };

                    _managedServer.Start();
                    _managedServer.BeginOutputReadLine();
                    _managedServer.BeginErrorReadLine();
                    _externalInstance = false;
                    Log("正在启动 dsh web（node " + NodeVersion(node) + "，DSH_HOME=" + DshHomeDir + "）...");
                    SetUiRunning(true, managed: true);

                    // 3) 等待 HTTP + WebSocket 双重就绪
                    bool ready = await WaitReadyAsync(90);
                    if (ready)
                    {
                        Log("服务已就绪。");
                        LoadUi();
                    }
                    else
                    {
                        Log("等待服务就绪超时，请点击「≡ 操作」展开后查看日志。");
                    }
                }
                catch (Exception ex)
                {
                    Log("启动失败：" + ex.Message);
                    _managedServer = null;
                    SetUiRunning(false, managed: true);
                }
            }
            finally
            {
                _startRequested = false;
            }
        }

        private void StopServer()
        {
            var p = _managedServer;
            _managedServer = null;
            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        Log("正在停止服务 ...");
                        var kill = Process.Start(new ProcessStartInfo
                        {
                            FileName = "taskkill.exe",
                            Arguments = "/PID " + p.Id + " /T /F",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        });
                        kill?.WaitForExit(8000);
                    }
                }
                catch (Exception ex)
                {
                    Log("停止服务异常：" + ex.Message);
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
            if (_externalInstance)
            {
                _externalInstance = false;
                Log("该实例由外部启动，不受本启动器管理。");
            }
            SetUiRunning(false, managed: p != null);
        }

        private void LoadUi()
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired) { Invoke((Action)LoadUi); return; }
                if (_uiLoaded) return;
                _uiLoaded = true;
                _bootFailCount = 0;
                if (_web.CoreWebView2 != null)
                {
                    // 隐藏启动画面，切回内置浏览器
                    if (_splash.Visible)
                    {
                        _splash.Visible = false;
                        try { _splash.Image?.Dispose(); } catch { }
                        _splash.Image = null;
                    }
                    _web.Visible = true;
                    _web.CoreWebView2.Navigate(UiUrl);
                    _bootWatchTimer.Start();
                    Log("已加载 Web UI。");
                    // 进入运行态：收起操作栏，界面只留底部细状态条
                    if (_actionsPanel.Visible) ToggleActions();
                }
                else
                {
                    Log("内置浏览器不可用，请展开操作栏点「外部浏览器」。");
                }
            }
            catch (Exception ex)
            {
                Log("加载 UI 异常：" + ex.Message);
            }
        }

        private void ReloadUi()
        {
            _uiLoaded = false;
            _bootFailCount = 0;
            try { _web?.CoreWebView2?.Reload(); } catch { }
            LoadUi();
        }

        // ── Boot 失败自动重试 ─────────────────────────────────────
        private async void BootWatchTick(object sender, EventArgs e)
        {
            try
            {
                if (_web?.CoreWebView2 == null) return;
                string result = await _web.CoreWebView2.ExecuteScriptAsync(
                    "(function(){var b=document.body;if(!b)return 'wait';" +
                    "if(b.innerText.indexOf('Failed to load plugins')>=0)return 'failed';" +
                    "if(b.innerText.indexOf('内测声明')>=0||b.innerText.indexOf('开始')>=0||b.innerText.indexOf('新建')>=0||b.innerText.length>200)return 'ok';" +
                    "return 'wait';})()");
                result = (result ?? "").Trim().Trim('"');
                if (result == "failed")
                {
                    _bootFailCount++;
                    if (_bootFailCount <= 3)
                    {
                        Log("检测到页面加载失败（第 " + _bootFailCount + " 次），自动刷新重试 ...");
                        _web.CoreWebView2.Reload();
                    }
                    else
                    {
                        Log("多次重试仍失败，请展开操作栏点「刷新」重试，或改用「外部浏览器」。");
                        _bootWatchTimer.Stop();
                    }
                }
                else if (result == "ok")
                {
                    _bootWatchTimer.Stop();
                    Log("Web UI 加载成功。");
                }
            }
            catch { }
        }

        // ── 状态轮询 ──────────────────────────────────────────────
        private async void StatusTick(object sender, EventArgs e)
        {
            bool managedRunning = _managedServer != null && !_managedServer.HasExited;
            bool ready = await IsHttpReadyAsync();
            bool hasServer = managedRunning || _externalInstance;

            if (ready)
            {
                _statusLabel.Text = "● 运行中  http://127.0.0.1:" + Port + "/web/";
                _statusLabel.ForeColor = Color.FromArgb(0, 140, 0);
            }
            else if (hasServer)
            {
                _statusLabel.Text = "● 启动中 ...";
                _statusLabel.ForeColor = Color.FromArgb(180, 120, 0);
            }
            else
            {
                _statusLabel.Text = "● 已停止";
                _statusLabel.ForeColor = Color.FromArgb(120, 120, 120);
                _uiLoaded = false;
            }
            try { _trayIcon.Text = "dsh-launcher - " + _statusLabel.Text.Replace("● ", ""); } catch { }
            try
            {
                if (!IsDisposed)
                    Invoke((Action)(() =>
                    {
                        bool canStart = !(managedRunning || _externalInstance);
                        _btnStart.Enabled = canStart;
                        _btnStop.Enabled = managedRunning;
                        if (_menuStart != null) _menuStart.Enabled = canStart;
                        if (_menuStop != null) _menuStop.Enabled = managedRunning;
                    }));
            }
            catch { }
        }

        // ── 探测与就绪 ────────────────────────────────────────────
        private enum ProbeState { Idle, DshReady, Other }

        private async Task<ProbeState> ProbeDshAsync()
        {
            try
            {
                using var resp = await _http.GetAsync(UiUrl);
                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync();
                    return body.Contains("__DSH_BOOT__") ? ProbeState.DshReady : ProbeState.Other;
                }
                return ProbeState.Idle;
            }
            catch
            {
                return ProbeState.Idle;
            }
        }

        private async Task<bool> IsHttpReadyAsync()
        {
            try
            {
                using var resp = await _http.GetAsync(UiUrl);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> IsWsReadyAsync()
        {
            try
            {
                using var ws = new ClientWebSocket();
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + Port + "/api/events.mux"), cts.Token);
                return ws.State == WebSocketState.Open;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> WaitReadyAsync(int timeoutSeconds)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < timeoutSeconds)
            {
                bool h = await IsHttpReadyAsync();
                bool w = await IsWsReadyAsync();
                if (h && w) return true;
                await Task.Delay(500);
            }
            return false;
        }

        // ── 配置与首次安装引导 ────────────────────────────────────
        internal static void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(ConfigFile));
                    if (doc.RootElement.TryGetProperty("workDir", out var wd))
                    {
                        string v = wd.GetString();
                        if (!string.IsNullOrEmpty(v) && Directory.Exists(v))
                        {
                            WorkDir = v;
                            return;
                        }
                    }
                }
            }
            catch { }

            // 无有效配置时回退到启动器目录下的默认位置（dsh-src）；不存在则首次引导让用户选择/下载
            WorkDir = Path.Combine(LauncherDir, "dsh-src");
        }

        private static bool HasDshSourceAt(string dir)
        {
            return Directory.Exists(dir)
                && File.Exists(Path.Combine(dir, "package.json"))
                && Directory.Exists(Path.Combine(dir, "apps", "cli"));
        }

        private static void SaveConfig()
        {
            try
            {
                File.WriteAllText(ConfigFile,
                    JsonSerializer.Serialize(new { workDir = WorkDir }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private static bool HasDshSource()
        {
            return HasDshSourceAt(WorkDir);
        }

        /// <summary>首次安装引导：自动下载 / 手动选择源码目录。</summary>
        private async Task RunInstallFlowAsync()
        {
            var choice = MessageBox.Show(
                "未找到 dsh-launcher 源码（" + WorkDir + "）。\n\n" +
                "【是】自动下载安装（国内网络可用镜像加速）\n" +
                "【否】手动选择已解压的源码目录\n" +
                "【取消】退出",
                "首次使用",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (choice == DialogResult.Cancel) { Close(); return; }

            if (choice == DialogResult.No)
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "请选择 dsh-launcher 源码目录（需包含 package.json）",
                    UseDescriptionForTitle = true
                };
                if (dlg.ShowDialog(this) == DialogResult.OK && Directory.Exists(dlg.SelectedPath))
                {
                    if (File.Exists(Path.Combine(dlg.SelectedPath, "package.json")))
                    {
                        WorkDir = dlg.SelectedPath;
                        SaveConfig();
                        Log("已选择源码目录：" + WorkDir);
                        await StartServerAsync();
                        return;
                    }
                    Log("所选目录不含 package.json，请选择 dsh 源码目录。");
                    MessageBox.Show("所选目录不是有效的 dsh 源码目录（缺少 package.json）。",
                        "目录无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            // 自动下载
            await DownloadAndInstallAsync();
        }

        private async Task DownloadAndInstallAsync()
        {
            string node = FindNode();
            if (node == null)
            {
                var r = MessageBox.Show(
                    "未找到 Node.js（≥ v22.19），自动安装需要 Node.js 来安装依赖。\n" +
                    "是否打开 Node.js 官网下载？",
                    "缺少 Node.js", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes)
                {
                    try { Process.Start(new ProcessStartInfo("https://nodejs.org") { UseShellExecute = true }); } catch { }
                }
                return;
            }
            if (FindPnpm() == null)
            {
                var r = MessageBox.Show(
                    "未找到 pnpm。\n是否现在用 npm 自动安装 pnpm？（npm install -g pnpm）",
                    "缺少 pnpm", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                Log("正在安装 pnpm ...");
                string npm = ResolveFromPath("npm.cmd") ?? "npm";
                if (!await RunProcessAsync(npm, "install -g pnpm", null, 300))
                {
                    MessageBox.Show("pnpm 安装失败，请手动执行 npm install -g pnpm 后重试。",
                        "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            var mirror = MessageBox.Show(
                "使用哪种方式下载源码？\n\n" +
                "【是】国内镜像（ghproxy，推荐国内网络）\n" +
                "【否】直连 GitHub",
                "下载方式", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            string url = mirror == DialogResult.Yes ? SourceZipMirror : SourceZipDirect;

            try
            {
                Log("开始下载 dsh 源码 ...（" + url + "）");
                string zipPath = Path.Combine(LauncherDir, "dsh-source.zip");
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    await resp.Content.CopyToAsync(fs);
                }
                Log("下载完成（" + new FileInfo(zipPath).Length / 1024 / 1024 + " MB），正在解压 ...");

                string extractDir = Path.Combine(LauncherDir, "dsh-extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(zipPath, extractDir);
                string inner = Directory.GetDirectories(extractDir).FirstOrDefault();
                if (inner == null) throw new Exception("压缩包内容异常");

                if (Directory.Exists(WorkDir)) throw new Exception("目标目录已存在：" + WorkDir);
                Directory.Move(inner, WorkDir);
                Log("源码已就绪：" + WorkDir);

                // 清理下载临时文件
                try { File.Delete(zipPath); Directory.Delete(extractDir, true); } catch { }

                SaveConfig();
                Log("正在安装依赖（pnpm install，可能需要几分钟）...");
                bool ok = await RunProcessAsync(node, "\"" + FindPnpm() + "\" install", WorkDir, 1800);
                if (!ok)
                {
                    Log("依赖安装失败，请手动在 " + WorkDir + " 执行 pnpm install。");
                    return;
                }
                Log("安装完成！正在启动服务 ...");
                await StartServerAsync();
            }
            catch (Exception ex)
            {
                Log("自动安装失败：" + ex.Message);
                MessageBox.Show("自动安装失败：" + ex.Message + "\n\n可尝试：\n1. 切换下载方式重试\n2. 手动从 " + SourceRepoUrl + " 下载后选择目录", 
                    "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>后台执行进程并把输出写入日志，返回是否成功退出（timeout 秒内）。</summary>
        private async Task<bool> RunProcessAsync(string fileName, string args, string workingDir, int timeoutSec)
        {
            var tcs = new TaskCompletionSource<bool>();
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                WorkingDirectory = workingDir ?? LauncherDir,
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

        // ── 工具 ──────────────────────────────────────────────────
        private void EnsureHomeMigrated()
        {
            try
            {
                // home 已有数据则跳过（正常路径）
                if (Directory.Exists(DshHomeDir) &&
                    Directory.GetFileSystemEntries(DshHomeDir).Length > 0)
                    return;
                // 旧目录不存在则无需迁移
                if (!Directory.Exists(LegacyHomeDir)) return;
                // 只迁移用户数据：sessions（聊天记录）、storages、以及根级配置文件；
                // profiles/ 由 dsh 首次启动时自动生成（含符号链接），避免复制导致链接损坏
                foreach (var dir in new[] { "sessions", "storages" })
                {
                    string src = Path.Combine(LegacyHomeDir, dir);
                    if (Directory.Exists(src))
                        CopyDirectoryContents(src, Path.Combine(DshHomeDir, dir));
                }
                foreach (var file in Directory.GetFiles(LegacyHomeDir))
                {
                    string name = Path.GetFileName(file);
                    File.Copy(file, Path.Combine(DshHomeDir, name), false);
                }
                Log("已迁移原有配置与聊天记录（profiles 由 dsh 自动重建）：" + LegacyHomeDir + " → " + DshHomeDir);
            }
            catch (Exception ex)
            {
                Log("迁移旧数据失败：" + ex.Message);
            }
        }

        private static void CopyDirectoryContents(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(src, dst));
            foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(src, dst), false);
        }

        private static string FindNode()
        {
            // 1) PATH 中查找 node（开源环境的主要途径）
            string pathNode = ResolveFromPath("node");
            if (pathNode != null)
            {
                string v = NodeVersion(pathNode);
                if (v != null && IsVersionOk(v)) return pathNode;
            }
            // 2) 硬编码候选（本机已知可用路径回退）
            string best = null, fallback = null;
            foreach (var c in NodeCandidates)
            {
                if (!File.Exists(c)) continue;
                string ver = NodeVersion(c);
                if (ver != null)
                {
                    if (IsVersionOk(ver)) return c;
                    fallback ??= c;
                }
                else best ??= c;
            }
            return best ?? fallback;
        }

        private static string FindPnpm()
        {
            // 1) PATH 中的 pnpm（npm 全局安装时 pnpm.cmd / pnpm 在 PATH，pnpm.cjs 在其同级 node_modules 下）
            string pnpmCmd = ResolveFromPath("pnpm.cmd") ?? ResolveFromPath("pnpm");
            if (pnpmCmd != null)
            {
                string dir = Path.GetDirectoryName(pnpmCmd);
                string candidate = Path.Combine(Path.GetDirectoryName(dir), "node_modules", "pnpm", "bin", "pnpm.cjs");
                if (File.Exists(candidate)) return candidate;
            }
            // 2) 本机已知路径回退
            if (File.Exists(PnpmCjs)) return PnpmCjs;
            return null;
        }

        private static string ResolveFromPath(string name)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(';'))
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    string full = Path.Combine(dir.Trim('"'), name + ".exe");
                    if (File.Exists(full)) return full;
                }
            }
            catch { }
            return null;
        }

        private static string NodeVersion(string nodeExe)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = nodeExe,
                    Arguments = "-v",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                });
                if (p == null) return null;
                string v = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000);
                return v;
            }
            catch { return null; }
        }

        private static bool IsVersionOk(string version)
        {
            // 接受 ^22.19.0 || >=24.0.0
            var m = Regex.Match(version, @"v(\d+)\.(\d+)");
            if (!m.Success) return false;
            int major = int.Parse(m.Groups[1].Value);
            int minor = int.Parse(m.Groups[2].Value);
            if (major == 22) return minor >= 19;
            return major >= 24;
        }

        private void BackupData()
        {
            try
            {
                string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string dest = Path.Combine(LauncherDir, "backups", "backup-" + ts);
                string userDataDest = Path.Combine(dest, "userdata");
                // 整体备份 userdata 用户数据（排除 profiles，由 dsh 自动重建、含符号链接不便备份）
                foreach (var dir in Directory.GetDirectories(DshHomeDir))
                {
                    string name = Path.GetFileName(dir);
                    if (name.Equals("profiles", StringComparison.OrdinalIgnoreCase)) continue;
                    CopyDirectoryContents(dir, Path.Combine(userDataDest, name));
                }
                foreach (var file in Directory.GetFiles(DshHomeDir))
                {
                    string name = Path.GetFileName(file);
                    File.Copy(file, Path.Combine(userDataDest, name), false);
                }
                Log("备份完成（userdata）：" + dest);
                MessageBox.Show("备份完成：\n" + dest + "\n\n恢复方式：把 userdata 文件夹的内容复制回\n" + DshHomeDir + "\n（删除其中的 profiles 目录）",
                    "备份成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log("备份失败：" + ex.Message);
                MessageBox.Show("备份失败：" + ex.Message, "备份失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ── 启动画面：从 D:\dsh-launcher\pics 随机抽一张显示 ─────
        private void ShowSplash()
        {
            try
            {
                string dir = Path.Combine(LauncherDir, "pics");
                if (!Directory.Exists(dir)) return;
                var files = Directory.GetFiles(dir)
                    .Where(f => IsImageExt(Path.GetExtension(f)))
                    .ToArray();
                if (files.Length == 0) return;

                string path = files[Random.Shared.Next(files.Length)];
                var img = Image.FromFile(path);
                _splash.Image?.Dispose();
                _splash.Image = img;
                _splash.Visible = true;
                _splash.BringToFront();
                Log("启动画面：" + Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                Log("启动画面加载失败：" + ex.Message);
            }
        }

        private static bool IsImageExt(string ext)
        {
            return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase);
        }

        // ── 更新检查（版本对比：本地 package.json vs GitHub 官方仓库） ──
        private async Task CheckForUpdatesAsync(bool interactive)
        {
            Log("正在检查更新 ...");
            var result = await Task.Run(() => CheckVersionRemote());

            if (result == null)
            {
                if (interactive)
                {
                    MessageBox.Show("无法检查更新（网络或 GitHub 仓库不可达）。",
                        "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                Log("检查更新失败（网络不可达）。");
                return;
            }

            if (result == "latest")
            {
                if (interactive)
                {
                    MessageBox.Show("当前已是最新版本（" + LocalVersion + "）。",
                        "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                Log("已是最新版本（" + LocalVersion + "）。");
                return;
            }

            Log("发现新版本：" + result + "（本地 " + LocalVersion + "）");
            var r = MessageBox.Show(
                "发现新版本 " + result + "（本地 " + LocalVersion + "）。\n\n" +
                "是否打开 GitHub 仓库查看更新说明并下载最新源码？",
                "发现更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://github.com/deepseek-ai/deepseek-harness") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Log("打开 GitHub 失败：" + ex.Message);
                }
            }
        }

        private void CheckForUpdates(bool interactive)
        {
            _ = CheckForUpdatesAsync(interactive);
        }

        private static string LocalVersion
        {
            get
            {
                try
                {
                    string json = File.ReadAllText(Path.Combine(WorkDir, "package.json"));
                    using var doc = JsonDocument.Parse(json);
                    return doc.RootElement.GetProperty("version").GetString() ?? "未知";
                }
                catch
                {
                    return "未知";
                }
            }
        }

        /// <summary>返回 "latest" 表示无更新；返回版本号表示远程新版本；null 表示检查失败。</summary>
        private static string CheckVersionRemote()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                string json = client.GetStringAsync(
                    "https://raw.githubusercontent.com/deepseek-ai/deepseek-harness/main/package.json").GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                string remote = doc.RootElement.GetProperty("version").GetString() ?? "未知";
                return remote == LocalVersion ? "latest" : remote;
            }
            catch
            {
                return null;
            }
        }

        private void OpenExternalBrowser()
        {
            try
            {
                Process.Start(new ProcessStartInfo(UiUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log("打开外部浏览器失败：" + ex.Message);
            }
        }

        private void SetUiRunning(bool running, bool managed)
        {
            try
            {
                if (IsDisposed) return;
                Invoke((Action)(() =>
                {
                    _btnStart.Enabled = !running;
                    _btnStop.Enabled = managed && running;
                }));
            }
            catch { }
        }

        private void Log(string message)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            try
            {
                if (!IsDisposed && IsHandleCreated)
                    Invoke((Action)(() =>
                    {
                        _logBox.AppendText(line + Environment.NewLine);
                        _logBox.SelectionStart = _logBox.TextLength;
                        _logBox.ScrollToCaret();
                    }));
            }
            catch { }
            try { File.AppendAllText(LogFile, line + Environment.NewLine); } catch { }
        }

        private static void Diagnose(string message)
        {
            try
            {
                string file = Path.Combine(LauncherDir, "diag-" + Environment.ProcessId + ".log");
                File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
