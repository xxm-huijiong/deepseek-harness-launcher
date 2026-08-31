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
            try
            {
                MainInternal();
            }
            catch (Exception ex)
            {
                // 启动即崩溃时写入诊断日志，便于定位
                try
                {
                    System.IO.File.AppendAllText(
                        MainForm.LogFile,
                        "[" + DateTime.Now.ToString("HH:mm:ss") + "] [FATAL] " +
                        ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace + "\n");
                }
                catch { }
                throw;
            }
        }

        private static void MainInternal()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm.LoadConfig();   // 读取 config.json（可重定向 dsh 源码目录）

            // 首次使用：dsh 全局包未安装时跑安装向导（npm install -g @deepseek-ai/dsh）
            if (MainForm.FindDshCli() == null)
            {
                using var installer = new InstallForm();
                Application.Run(installer);
                if (!installer.Completed) return;   // 用户取消 → 退出
            }

            // 启动时检查更新（前置更新窗口）：立即弹出「检查更新」窗口并显示检查状态，
            // 检查完成后窗口内给出结果（有新版→立即更新；无新版→自动关闭进入主界面）。
            // 时序保证：更新窗口（含更新执行）→ 窗口关闭 → 进入主界面 → OnShown 里才自动启动服务。
            // 千万不要在更新窗口之前/期间启动 dsh 服务，否则更新覆盖源码时会报“文件被另一进程使用”。
            if (MainForm.CheckUpdateOnStart)
            {
                using var updater = new UpdateForm(MainForm.LocalVersion);
                Application.Run(updater);
                // 更新成功后直接进入主界面（新版源码已就绪）
            }

            Application.Run(new MainForm());
        }
    }

    internal class MainForm : Form
    {
        // ── 路径配置（以启动器所在目录为根，随 exe 位置，放任意盘/目录均可） ──
        internal static readonly string LauncherDir = Path.GetDirectoryName(Environment.ProcessPath) ?? @"D:\dsh-launcher";
        internal static string WorkDir = Path.Combine(LauncherDir, "dsh-src");          // dsh 源码目录（可由 config.json 的 workDir 重定向）
        private static readonly string ConfigFile = Path.Combine(LauncherDir, "config.json");
        private static readonly string DshHomeDir = Path.Combine(LauncherDir, "userdata");
        private const string LegacyHomeDir = @"C:\Users\wujiong\.dsh";                 // 旧版默认 home（仅本机迁移用，其他机器无此目录会自动跳过）
        internal static readonly string LogFile = Path.Combine(LauncherDir, "launcher.log");
        private static readonly string WebViewDataDir = Path.Combine(LauncherDir, "webview-data-v3");
        internal const string SourceRepoUrl = @"https://github.com/deepseek-ai/deepseek-harness";                    // dsh 官方仓库
        internal const string SourceZipDirect = @"https://github.com/deepseek-ai/deepseek-harness/archive/refs/heads/master.zip";          // 直连下载（官方默认分支为 master）
        internal const string SourceZipMirror = @"https://ghproxy.net/https://github.com/deepseek-ai/deepseek-harness/archive/refs/heads/master.zip";  // 国内镜像下载
        internal static readonly string[] SourceZipMirrors = {                          // 镜像候选（按顺序尝试，失败自动切换）
            SourceZipDirect,                                                             // 直连优先：走系统/环境代理通常最快且最稳定
            @"https://ghproxy.net/https://github.com/deepseek-ai/deepseek-harness/archive/refs/heads/master.zip",   // 多个国内镜像（GitHub 会限流，作保底）
            @"https://gh-proxy.com/https://github.com/deepseek-ai/deepseek-harness/archive/refs/heads/master.zip",
            @"https://ghfast.top/https://github.com/deepseek-ai/deepseek-harness/archive/refs/heads/master.zip",
            @"https://gh.llkk.cc/https://github.com/deepseek-ai/deepseek-harness/archive/refs/heads/master.zip",
            @"https://github.moeyy.xyz/https://github.com/deepseek-ai/deepseek-harness/archive/refs/heads/master.zip"
        };

        private const int Port = 3080;
        // dsh 的 Web UI 路径随版本变化：旧版在 /web/，新版（0.1.1+）在根路径 /。
        // 启动时通过 ProbeUiPath 探测，兼容新旧版本；此字段后续会被探测结果覆盖。
        private static string UiUrl = @"http://127.0.0.1:3080/";

        // 布局常量
        private const int StatusBarHeight = 22;   // 底部状态条
        private const int ActionsBarHeight = 70;  // 底部操作栏（展开时：第一行按钮 + 第二行复选框）
        private const int LogPanelHeight = 168;   // 底部日志面板（展开时）

        // Node / pnpm 候选（按优先级；PATH 优先，以下作为回退）
        private static readonly string[] NodeCandidates =
        {
            @"C:\Users\wujiong\.workbuddy\binaries\node\versions\22.22.2\node.exe",   // 已知满足 ^22.19
            @"D:\nodejs\node.exe"                                                      // 回退
        };
        private const string PnpmCjs = @"D:\npm-global\node_modules\pnpm\bin\pnpm.cjs"; // 本机回退路径
        // 启动器自管理的 node（免安装 zip 解压到 LauncherDir\node），优先使用以规避 node22 的 import.meta.main 问题
        internal static readonly string ManagedNodeDir = Path.Combine(LauncherDir, "node");

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
        private Button _btnWebView;          // 安装/修复内置浏览器（WebView2 Runtime）
        private Button _btnChooseDir;        // 选择 dsh 源码目录
        private Button _btnExit;             // 退出程序
        private CheckBox _chkAutoStart;
        private CheckBox _chkAutoCheck;      // 启动时检查更新
        private CheckBox _chkNotify;         // 任务提醒（等待确认/任务完成时气泡+提示音）
        private CheckBox _chkBackOnly;       // 后台才提醒（窗口在前端时不弹通知，需勾选任务提醒才生效）

        // 事件监听（events.mux）：审批提醒 + 回合级任务完成提醒（只提醒主会话）
        private System.Threading.CancellationTokenSource _eventCts;
        private readonly Dictionary<string, string> _lastAssistantText = new();   // sessionId → 最近一条模型答复文本（回合完成摘要）
        private string _mainSession;                                               // 主会话（最近有用户输入 user/message 的会话）；只提醒它的回合完成
        private readonly HashSet<string> _notifiedApprovals = new();              // approvalId 去重
        private bool _eventMonitorStarted;

        private Panel _logPanel;             // 底部日志面板（默认收起）
        private TextBox _logBox;

        private WebView2 _web;
        private PictureBox _splash;          // 启动画面（等待期随机图片）
        private NotifyIcon _trayIcon;        // 系统托盘图标
        private ToolStripMenuItem _menuStart;   // 托盘菜单：启动服务
        private ToolStripMenuItem _menuStop;    // 托盘菜单：停止服务
        private ToolStripMenuItem _menuAutoStart; // 托盘菜单选项：启动时自动运行
        private ToolStripMenuItem _menuAutoCheck; // 托盘菜单选项：启动时检查更新
        private ToolStripMenuItem _menuNotify;    // 托盘菜单选项：任务提醒
        private ToolStripMenuItem _menuBackOnly;  // 托盘菜单选项：后台才提醒
        private bool _forceExit;               // 托盘「退出」触发真正退出
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
            Text = "Deepseek Harness";
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

            // ── 操作组 ────────────────────────────────────────────
            trayMenu.Items.Add(_menuStart = new ToolStripMenuItem("启动服务", null, (s, e) => _ = StartServerAsync()));
            trayMenu.Items.Add(_menuStop = new ToolStripMenuItem("停止服务", null, (s, e) => StopServer()));
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("刷新页面", null, (s, e) => ReloadUi());
            trayMenu.Items.Add("浏览器打开", null, (s, e) => OpenExternalBrowser());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("备份数据", null, (s, e) => BackupData());
            trayMenu.Items.Add("检查更新", null, (s, e) => CheckForUpdates(interactive: true));
            trayMenu.Items.Add("修复浏览器", null, (s, e) => _ = TryInstallWebView2Async());
            trayMenu.Items.Add("选择 dsh 目录", null, (s, e) => ChooseDshDirectoryFromMain());

            // ── 选项组（勾选，与主窗口操作栏复选框同步） ──────────
            trayMenu.Items.Add(new ToolStripSeparator());
            _menuAutoStart = new ToolStripMenuItem("启动时自动运行") { CheckOnClick = true, Checked = true };
            _menuAutoStart.CheckedChanged += (s, e) => SyncCheckbox(_chkAutoStart, _menuAutoStart.Checked);
            trayMenu.Items.Add(_menuAutoStart);

            _menuAutoCheck = new ToolStripMenuItem("启动时检查更新") { CheckOnClick = true, Checked = CheckUpdateOnStart };
            // 持久化由复选框自己的 CheckedChanged 处理（SyncCheckbox 设置后自动触发）
            _menuAutoCheck.CheckedChanged += (s, e) => SyncCheckbox(_chkAutoCheck, _menuAutoCheck.Checked);
            trayMenu.Items.Add(_menuAutoCheck);

            _menuNotify = new ToolStripMenuItem("任务提醒") { CheckOnClick = true, Checked = true };
            _menuNotify.CheckedChanged += (s, e) => SyncCheckbox(_chkNotify, _menuNotify.Checked);
            trayMenu.Items.Add(_menuNotify);

            _menuBackOnly = new ToolStripMenuItem("后台才提醒") { CheckOnClick = true, Checked = true };
            _menuBackOnly.CheckedChanged += (s, e) => SyncCheckbox(_chkBackOnly, _menuBackOnly.Checked);
            trayMenu.Items.Add(_menuBackOnly);

            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("升级 Node.js 到 v24", null, (s, e) => _ = TryUpgradeNodeAsync(this, Log));
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
            _actionsPanel.Controls.Add(_btnWebView = MakeActionButton("修复浏览器", 588, 10, (x) => _ = TryInstallWebView2Async()));
            _actionsPanel.Controls.Add(_btnChooseDir = MakeActionButton("选择 dsh 目录", 684, 10, (x) => ChooseDshDirectoryFromMain(), 104));
            _actionsPanel.Controls.Add(_btnExit = MakeActionButton("退出", 800, 10, (x) => ExitApp()));
            // 第二行：复选框（独立一行，文字空间充裕）
            _actionsPanel.Controls.Add(_chkAutoStart = new CheckBox
            {
                Text = "启动时自动运行",
                Location = new Point(12, 42),
                Size = new Size(130, 22),
                Checked = true,
                Font = new Font(Font.FontFamily, 9f)
            });
            _chkAutoStart.CheckedChanged += (s, e) => { if (_menuAutoStart != null) _menuAutoStart.Checked = _chkAutoStart.Checked; };
            _actionsPanel.Controls.Add(_chkAutoCheck = new CheckBox
            {
                Text = "启动时检查更新",
                Location = new Point(150, 42),
                Size = new Size(140, 22),
                Checked = CheckUpdateOnStart,
                Font = new Font(Font.FontFamily, 9f)
            });
            _chkAutoCheck.CheckedChanged += (s, e) =>
            {
                if (_menuAutoCheck != null) _menuAutoCheck.Checked = _chkAutoCheck.Checked;
                CheckUpdateOnStart = _chkAutoCheck.Checked; SaveConfig();
            };
            _actionsPanel.Controls.Add(_chkNotify = new CheckBox
            {
                Text = "任务提醒",
                Location = new Point(300, 42),
                Size = new Size(80, 22),
                Checked = true,
                Font = new Font(Font.FontFamily, 9f)
            });
            _chkNotify.CheckedChanged += (s, e) => { if (_menuNotify != null) _menuNotify.Checked = _chkNotify.Checked; };
            _actionsPanel.Controls.Add(_chkBackOnly = new CheckBox
            {
                Text = "后台才提醒",
                Location = new Point(390, 42),
                Size = new Size(110, 22),
                Checked = true,
                Font = new Font(Font.FontFamily, 9f)
            });
            _chkBackOnly.CheckedChanged += (s, e) => { if (_menuBackOnly != null) _menuBackOnly.Checked = _chkBackOnly.Checked; };
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

        private Button MakeActionButton(string text, int x, int y, Action<object> onClick, int width = 84)
        {
            var b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, 28),
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
                // 自动下载并静默安装 WebView2 Runtime，装好后重试内置浏览器
                _ = TryInstallWebView2Async();
            }

            if (_chkAutoStart.Checked)
                _ = StartServerAsync();

            // 启动时检查更新已移至主程序入口（前置更新窗口）处理

            // 检测 node 版本：若 < v24，提示可自动升级（dsh 0.1.1+ 构建依赖 node24 的 import.meta.main）
            CheckNodeVersionAndSuggest();
        }

        /// <summary>后台检测当前 node 版本；若 &lt; v24 则提示用户可自动升级（不影响现有启动流程）。</summary>
        private async void CheckNodeVersionAndSuggest()
        {
            try
            {
                string node = await Task.Run(FindNode);
                if (node == null) return;   // 无 node，不打扰（安装向导会处理）
                string ver = await Task.Run(() => NodeVersion(node));
                if (ver == null) return;
                if (MajorOf(ver) >= 24) return;   // 已是 node24+，无需提示
                // node22 等：提示可升级（后台延迟，避免挤占启动流程）
                await Task.Delay(1500);
                if (IsDisposed) return;
                var r = MessageBox.Show(
                    "当前 Node.js：" + ver + "（&lt; v24）。\n\n" +
                    "dsh 0.1.1+ 的构建在 node v22 下可能静默失败（import.meta.main 不生效）。\n" +
                    "是否自动升级到 node v24（免安装版，不改系统环境变量）？",
                    "建议升级 Node.js", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes)
                    await TryUpgradeNodeAsync(this, Log);
            }
            catch { }
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
                string node = FindManagedNode() ?? FindNode();
                if (node == null) { Log("错误：未找到可用的 node（需要 ≥ v22.19，请安装 Node.js）。"); return; }

                try
                {
                    // npm 包方式运行 dsh：优先用自管理 node 24 执行全局 dsh 包的 bin.js（无需源码构建）
                    string dshCli = await EnsureDshInstalledAsync(this, Log);
                    if (string.IsNullOrEmpty(dshCli))
                    {
                        Log("未找到全局 dsh 包，无法启动。请先在托盘菜单「升级 Node.js」后启动，或自动安装 dsh。");
                        SetUiRunning(false, managed: true);
                        return;
                    }
                    node = FindManagedNode() ?? node;   // 优先 node 24 运行
                    var psi = new ProcessStartInfo
                    {
                        FileName = node,
                        // --no-open：禁止 dsh 自动打开系统默认浏览器（启动器内置 WebView2 会加载页面，避免重复打开）
                        Arguments = "\"" + dshCli + "\" web --no-open",
                        WorkingDirectory = LauncherDir,
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
                    // 清掉外部注入的“安全删除”钩子
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

                    // 3) 探测 Web UI 实际路径（新/旧版 dsh 不同），再等待 HTTP + WebSocket 双重就绪
                    await ProbeUiPath();
                    bool ready = await WaitReadyAsync(90);
                    if (ready)
                    {
                        Log("服务已就绪。");
                        LoadUi();
                        StartEventMonitor();   // 监听审批/任务事件（任务提醒）
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
            StopEventMonitor();   // 服务停止，事件监听一并停止
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
                StartEventMonitor();   // 任务提醒（含外部实例就绪的场景）
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

        /// <summary>探测 dsh Web UI 实际路径（新版在根路径 /，旧版在 /web/），更新 UiUrl 以便加载正确页面。</summary>
        private async Task ProbeUiPath()
        {
            string[] candidates = {
                @"http://127.0.0.1:3080/",
                @"http://127.0.0.1:3080/web/"
            };
            foreach (var url in candidates)
            {
                try
                {
                    using var resp = await _http.GetAsync(url);
                    if (resp.IsSuccessStatusCode) { UiUrl = url; return; }
                }
                catch { /* 服务未就绪，尝试下一个 */ }
            }
            // 都不可达：保持默认（新版根路径）
            UiUrl = candidates[0];
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
        internal static bool CheckUpdateOnStart = true;   // 启动时检查更新（config.json 持久化）

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
                        }
                    }
                    if (doc.RootElement.TryGetProperty("checkUpdate", out var cu) && cu.ValueKind == JsonValueKind.False)
                        CheckUpdateOnStart = false;
                }
            }
            catch { }

            // 无有效 workDir 时回退到启动器目录下的默认位置（dsh-src）；不存在则首次引导让用户选择/下载
            if (!HasDshSourceAt(WorkDir))
                WorkDir = Path.Combine(LauncherDir, "dsh-src");
        }

        internal static bool HasDshSourceAt(string dir)
        {
            return Directory.Exists(dir)
                && File.Exists(Path.Combine(dir, "package.json"))
                && Directory.Exists(Path.Combine(dir, "apps", "cli"));
        }

        internal static void SaveConfig()
        {
            try
            {
                File.WriteAllText(ConfigFile,
                    JsonSerializer.Serialize(new { workDir = WorkDir, checkUpdate = CheckUpdateOnStart }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        internal static bool HasDshSource()
        {
            return HasDshSourceAt(WorkDir);
        }

        /// <summary>
        /// 弹出目录选择框，让用户手动指定 dsh 源码目录并保存到 config。
        /// 校验所选目录必须是有效 dsh 源码目录（含 package.json 与 apps/cli）。
        /// 返回新目录路径；用户取消或目录无效返回 null。
        /// 供更新窗口「选择 dsh 目录」与主界面操作栏按钮共用。
        /// </summary>
        internal static string SelectAndSetDshDirectory(IWin32Window owner)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "请选择 DeepSeek Harness 源码目录（需包含 package.json）",
                UseDescriptionForTitle = true
            };
            if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
            if (!HasDshSourceAt(dlg.SelectedPath))
            {
                MessageBox.Show(owner,
                    "所选目录不是有效的 dsh 源码目录（缺少 package.json 或 apps/cli）。",
                    "目录无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            WorkDir = dlg.SelectedPath;
            SaveConfig();
            return dlg.SelectedPath;
        }

        /// <summary>主窗口操作栏「选择 dsh 目录」：切换源码目录后提示重启服务以生效。</summary>
        private void ChooseDshDirectoryFromMain()
        {
            string path = SelectAndSetDshDirectory(this);
            if (path == null) return;   // 用户取消或目录无效
            Log("已切换 dsh 源码目录：" + path + "（本地版本 " + LocalVersion + "）");
            MessageBox.Show(this,
                "已选择 dsh 源码目录：\n" + path + "\n\n" +
                "若服务正在运行，请先「停止服务」再「启动服务」以使用新目录。",
                "选择 dsh 目录", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>把托盘菜单勾选项的状态同步到主窗口复选框。
        /// 仅当复选框存在时设置（菜单在 BuildUi 之前创建，复选框可能尚未生成）。
        /// 设置 Checked 会触发复选框的 CheckedChanged，持久化等逻辑由它负责。</summary>
        private void SyncCheckbox(CheckBox box, bool value)
        {
            if (box != null && box.Checked != value) box.Checked = value;
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

        internal static string FindNode()
        {
            // 0) 启动器自管理的 node（LauncherDir\node\node-v*/node.exe，优先 node 24+；无则取版本 OK 的）
            string managed = FindManagedNode();
            if (managed != null) return managed;
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

        /// <summary>在自管理 node 目录里找 node.exe；优先版本 ≥24（规避 node22 的 import.meta.main 问题），否则取版本 OK 的。</summary>
        internal static string FindManagedNode()
        {
            try
            {
                if (!Directory.Exists(ManagedNodeDir)) return null;
                string candidate24 = null, fallback = null;
                foreach (var dir in Directory.GetDirectories(ManagedNodeDir, "node-v*"))
                {
                    string exe = Path.Combine(dir, "node.exe");
                    if (!File.Exists(exe)) continue;
                    string ver = NodeVersion(exe);
                    if (ver == null) continue;
                    if (IsVersionOk(ver))
                    {
                        if (MajorOf(ver) >= 24) return exe;      // node 24+ 最优
                        fallback ??= exe;
                    }
                    else candidate24 ??= exe;
                }
                return fallback ?? candidate24;
            }
            catch { return null; }
        }

        private static int MajorOf(string version)
        {
            var m = Regex.Match(version ?? "", @"v(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        internal static string FindPnpm()
        {
            // 1) PATH 中的 pnpm（npm 全局安装时 pnpm.cmd / pnpm 在 PATH，pnpm.cjs 在其同级 node_modules 下）
            string pnpmCmd = ResolveFromPath("pnpm.cmd") ?? ResolveFromPath("pnpm");
            if (pnpmCmd != null)
            {
                string dir = Path.GetDirectoryName(pnpmCmd);
                string candidate = Path.Combine(Path.GetDirectoryName(dir), "node_modules", "pnpm", "bin", "pnpm.cjs");
                if (File.Exists(candidate)) return candidate;
            }
            // 2) npm 默认全局目录（%APPDATA%\npm；刚用 npm -g 安装、PATH 未刷新时也能找到）
            string npmGlobal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
            string candidate2 = Path.Combine(npmGlobal, "node_modules", "pnpm", "bin", "pnpm.cjs");
            if (File.Exists(candidate2)) return candidate2;
            // 3) 本机已知路径回退
            if (File.Exists(PnpmCjs)) return PnpmCjs;
            return null;
        }

        /// <summary>
        /// 定位 npm：优先 node 同目录的 npm.cmd（Node 官方 Windows 安装自带）；
        /// 其次 node 同目录 node_modules/npm/bin/npm-cli.js（由 node 直接运行，不依赖 cmd shim）；
        /// 最后 PATH 中的 npm.cmd。
        /// 返回 (可执行文件, 需要前置的参数前缀)；npm-cli.js 情形 exe=node、prefix=带引号的 cli 路径。
        /// </summary>
        internal static (string exe, string argsPrefix) FindNpmCommand(string node)
        {
            if (!string.IsNullOrEmpty(node))
            {
                string dir = Path.GetDirectoryName(node);
                string npmCmd = Path.Combine(dir, "npm.cmd");
                if (File.Exists(npmCmd)) return (npmCmd, null);
                string npmCli = Path.Combine(dir, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(npmCli)) return (node, "\"" + npmCli + "\"");
            }
            string fromPath = ResolveFromPath("npm.cmd");
            if (fromPath != null) return (fromPath, null);
            return (null, null);
        }

        internal static string ResolveFromPath(string name)
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

        /// <summary>获取 npm 全局前缀目录（dsh 全局包安装位置）；失败返回 null。</summary>
        internal static string FindNpmGlobalPrefix(string nodeExe)
        {
            try
            {
                string npmCli = Path.Combine(Path.GetDirectoryName(nodeExe), "node_modules", "npm", "bin", "npm-cli.js");
                if (!File.Exists(npmCli)) return null;
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = nodeExe,
                    Arguments = "\"" + npmCli + "\" prefix -g",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (p == null) return null;
                string o = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(8000);
                return string.IsNullOrEmpty(o) ? null : o;
            }
            catch { return null; }
        }

        /// <summary>定位全局安装的 dsh 包运行入口（node_modules/@deepseek-ai/dsh/lib/bin.js）；未安装返回 null。</summary>
        internal static string FindDshCli()
        {
            try
            {
                // 优先从自管理 node 的全局前缀定位
                foreach (var node in new[] { FindManagedNode(), FindNode() })
                {
                    if (node == null) continue;
                    string prefix = FindNpmGlobalPrefix(node);
                    if (string.IsNullOrEmpty(prefix)) continue;
                    string p = Path.Combine(prefix, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                    if (File.Exists(p)) return p;
                }
                // 回退：从 PATH 的 dsh.cmd 所在目录的 node_modules 定位
                string dshCmd = ResolveFromPath("dsh.cmd");
                if (dshCmd != null)
                {
                    string p = Path.Combine(Path.GetDirectoryName(dshCmd), "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        /// <summary>确保全局已安装 @deepseek-ai/dsh（用自管理 node 执行 npm install -g）；返回 dsh cli 入口，失败返回 null。</summary>
        internal static async Task<string> EnsureDshInstalledAsync(IWin32Window owner, Action<string> log)
        {
            string dsh = FindDshCli();
            if (dsh != null) return dsh;
            string node = FindManagedNode() ?? FindNode();
            if (node == null)
            {
                MessageBox.Show(owner, "未找到可用的 Node.js，请先在托盘菜单「升级 Node.js 到 v24」安装。",
                    "缺少 Node.js", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            var r = MessageBox.Show(owner,
                "未检测到全局 dsh 包，是否自动安装？\n\n" +
                "将执行：npm install -g @deepseek-ai/dsh\n（安装到全局目录，联网下载，耗时约 1-2 分钟）",
                "安装 dsh", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return null;

            string npmCli = Path.Combine(Path.GetDirectoryName(node), "node_modules", "npm", "bin", "npm-cli.js");
            string prefix = FindNpmGlobalPrefix(node);
            try
            {
                log?.Invoke("正在安装 dsh（npm install -g @deepseek-ai/dsh）...");
                // 用当前进程的 node（而非临时 pnpm）执行 npm，避免全局目录被沙箱/旧 node 干扰
                var psi = new ProcessStartInfo
                {
                    FileName = node,
                    Arguments = "\"" + npmCli + "\" install -g @deepseek-ai/dsh",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                if (prefix != null) psi.Environment["npm_config_prefix"] = prefix;
                psi.Environment["NODE_OPTIONS"] = "--use-system-ca";
                var proc = Process.Start(psi);
                if (proc == null) return null;
                // 事件异步读取 stdout/stderr：避免 ReadToEndAsync 与管道缓冲导致死锁，且能实时输出进度
                proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) log?.Invoke("[err] " + e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                // 超时保护：npm install 最长等 5 分钟，超时判失败，避免无限卡住
                bool exited = await Task.Run(() => proc.WaitForExit(5 * 60 * 1000));
                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    throw new Exception("npm install -g @deepseek-ai/dsh 超时（5 分钟未完成）。可能是网络问题，请重试或手动安装。");
                }
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    throw new Exception("npm install -g @deepseek-ai/dsh 失败（退出码 " + proc.ExitCode + "）。请查看上方日志或手动执行该命令。");
                }
                dsh = FindDshCli();
                if (dsh == null)
                {
                    throw new Exception("dsh 已安装但未找到运行入口，请检查全局 npm 目录。");
                }
                log?.Invoke("dsh 安装完成。");
                return dsh;
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "安装 dsh 失败：\n" + ex.Message,
                    "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
        }

        internal static string NodeVersion(string nodeExe)
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
                "【是】立即自动更新（停服 → 更新源码 → 重装依赖并构建 → 重启服务）\n" +
                "【否】只打开 GitHub 仓库手动处理",
                "发现更新", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
            if (r == DialogResult.Cancel) return;
            if (r == DialogResult.Yes)
            {
                // 自动更新：先停服务，避免源码被占用
                StopEventMonitor();
                bool wasRunning = _managedServer != null && !_managedServer.HasExited;
                if (wasRunning) StopServer();
                await Task.Delay(1500);   // 等待进程退出/文件句柄释放
                using var updater = new UpdateForm(LocalVersion, result);
                updater.ShowDialog(this);
                if (updater.Completed && wasRunning)
                {
                    Log("更新完成，正在重启服务 ...");
                    _ = StartServerAsync();
                }
                return;
            }
            if (r == DialogResult.No)
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

        internal static string LocalVersion
        {
            get
            {
                // npm 方式：优先读全局安装的 @deepseek-ai/dsh 版本
                try
                {
                    string dshCli = FindDshCli();
                    if (dshCli != null)
                    {
                        string pkg = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dshCli)), "package.json");
                        string json = File.ReadAllText(pkg);
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("version", out var v))
                        {
                            string ver = v.GetString();
                            if (!string.IsNullOrEmpty(ver)) return ver;
                        }
                    }
                }
                catch { }
                // 回退：从源码目录 package.json 读（兼容旧方式）
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

        // ── 事件监听（任务提醒：等待确认 / 回合级任务完成） ──────────
        private void StartEventMonitor()
        {
            if (_eventMonitorStarted || _eventCts != null) return;
            _eventCts = new System.Threading.CancellationTokenSource();
            _eventMonitorStarted = true;
            // 单流 events.mux：审批 + 回合事件。子代理过滤不再依赖 events.host 会话列表，
            // 改为「只提醒主会话」：通过 user/message 识别用户正在交互的会话，兼容 dsh 未来格式变化。
            _ = Task.Run(() => EventStreamLoopAsync("/api/events.mux", "事件监听", ProcessEvent, _eventCts.Token));
        }

        private void StopEventMonitor()
        {
            _eventMonitorStarted = false;
            try { _eventCts?.Cancel(); } catch { }
            try { _eventCts?.Dispose(); } catch { }
            _eventCts = null;
        }

        /// <summary>单条事件流循环：连接 → 逐条消息回调 → 断开 5 秒重连，直到取消。</summary>
        private async Task EventStreamLoopAsync(string path, string name, Action<string> onMessage, System.Threading.CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var ws = new System.Net.WebSockets.ClientWebSocket();
                    await ws.ConnectAsync(new Uri("ws://127.0.0.1:" + Port + path), ct);
                    Log(name + "已连接。");
                    var buf = new byte[65536];
                    while (ws.State == System.Net.WebSockets.WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
                        string text = System.Text.Encoding.UTF8.GetString(buf, 0, result.Count);
                        onMessage(text);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Log(name + "断开（" + ex.GetType().Name + "），5 秒后重连。");
                }
                try { await Task.Delay(5000, ct); } catch { break; }
            }
        }

        private void ProcessEvent(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("method", out var m)) return;
                string method = m.GetString();
                if (!root.TryGetProperty("payload", out var payload)) return;

                if (method == "approval/requested")
                {
                    // 需要用户确认：气泡 + 提示音（approvalId 去重）
                    string id = payload.TryGetProperty("approvalId", out var a) ? a.GetString() : "";
                    if (!string.IsNullOrEmpty(id) && !_notifiedApprovals.Add(id)) return;
                    if (_notifiedApprovals.Count > 200) _notifiedApprovals.Clear();
                    string tool = payload.TryGetProperty("toolName", out var t) ? t.GetString() : "?";
                    string reason = payload.TryGetProperty("reason", out var r) ? r.GetString() : "";
                    Notify("需要你的确认", "工具：" + tool + "\n" + Shorten(reason, 80));
                }
                else if (method == "session/event")
                {
                    // 回合事件：任务完成监听（turn/end）+ 最终答复摘要（assistant/message）
                    ProcessSessionEvent(payload);
                }
            }
            catch { }
        }

        /// <summary>
        /// 回合级任务完成监听：以「回合结束（turn/end）」为准，不再按 job 状态转变弹通知。
        /// 原因：session/jobs 快照包含所有工具调用/子代理 job（线缆无父子关系字段），
        /// 且 job 在子进程退出时即结算、早于模型最终答复，两者都会造成误报与提前。
        /// </summary>
        private void ProcessSessionEvent(JsonElement payload)
        {
            string sessionId = payload.TryGetProperty("sessionId", out var sidEl) ? sidEl.GetString() ?? "" : "";
            if (!payload.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object) return;
            string etype = ev.TryGetProperty("type", out var t) ? t.GetString() : "";
            if (!ev.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;

            if (etype == "user/message")
            {
                // 主会话识别：用户主动输入消息的会话即主会话（子代理由 dsh 内部驱动，无 user/message）。
                // 只提醒主会话的回合完成，从而自然过滤掉子代理（兼容 dsh 未来格式变化，不依赖会话列表）。
                if (!string.IsNullOrEmpty(sessionId)) _mainSession = sessionId;
            }
            else if (etype == "assistant/message")
            {
                // 记录该会话最近一条模型答复，回合结束时作为「任务已完成」摘要
                if (data.TryGetProperty("message", out var msg)
                    && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var block in content.EnumerateArray())
                    {
                        if (block.ValueKind == JsonValueKind.Object
                            && block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                            sb.Append(txt.GetString());
                    }
                    string text = sb.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (_lastAssistantText.Count > 100) _lastAssistantText.Clear();
                        _lastAssistantText[sessionId] = text;
                    }
                }
            }
            else if (etype == "turn/start")
            {
                // 新回合开始，清掉上一回合的旧摘要，避免串台
                _lastAssistantText.Remove(sessionId);
            }
            else if (etype == "turn/end")
            {
                // 只提醒主会话（最近有用户输入的会话）的回合完成；子代理/其他会话不打扰。
                // 尚未识别到主会话（用户还没在当前会话发过消息）时，暂时不提醒，避免子代理误报。
                if (_mainSession == null || sessionId != _mainSession) return;

                string reasonKind = "";
                string errorMessage = "";
                if (data.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.Object)
                {
                    reasonKind = reason.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                    if (reasonKind == "error"
                        && reason.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object
                        && err.TryGetProperty("message", out var em))
                        errorMessage = em.GetString() ?? "";
                }
                string summary = _lastAssistantText.TryGetValue(sessionId, out var s) ? s : "";
                switch (reasonKind)
                {
                    case "completed":
                        Notify("任务已完成", string.IsNullOrEmpty(summary) ? "本轮任务已完成。" : Shorten(summary, 80));
                        break;
                    case "error":
                        Notify("任务失败", string.IsNullOrEmpty(errorMessage) ? "本轮任务出错。" : Shorten(errorMessage, 80));
                        break;
                    case "aborted":
                        Notify("任务已取消", "本轮任务被取消。");
                        break;
                    // blocked / max-tokens / interrupted 等不弹通知
                }
            }
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private void Notify(string title, string text)
        {
            try
            {
                if (!IsDisposed)
                    Invoke((Action)(() =>
                    {
                        if (!_chkNotify.Checked) return;
                        // 「后台才提醒」：窗口在前端（用户正看着界面）时不弹通知
                        if (_chkBackOnly.Checked && Form.ActiveForm == this) return;
                        try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
                        try { _trayIcon.ShowBalloonTip(5000, "dsh-launcher - " + title, text, ToolTipIcon.Info); } catch { }
                    }));
            }
            catch { }
        }

        /// <summary>
        /// 返回 "latest" 表示无更新；返回版本号表示远程新版本；null 表示检查失败。
        /// 多候选源依次尝试：raw GitHub → ghproxy 镜像 → npm registry → 国内 npm 镜像；
        /// 自动读取 HTTPS_PROXY/HTTP_PROXY 环境变量（兼容代理环境，不影响系统设置）。
        /// </summary>
        internal static string CheckVersionRemote()
        {
            var candidates = new[]
            {
                "https://raw.githubusercontent.com/deepseek-ai/deepseek-harness/main/package.json",
                "https://ghproxy.net/https://raw.githubusercontent.com/deepseek-ai/deepseek-harness/main/package.json",
                "https://registry.npmjs.org/@deepseek-ai/dsh/latest",
                "https://registry.npmmirror.com/@deepseek-ai/dsh/latest"
            };
            foreach (var url in candidates)
            {
                string json = TryFetchText(url, 8);
                if (json == null) continue;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("version", out var v))
                    {
                        string remote = v.GetString();
                        if (!string.IsNullOrEmpty(remote))
                            return IsRemoteNewer(LocalVersion, remote) ? remote : "latest";
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 语义化版本比较：判断 remote 是否比 local 新。
        /// 支持 MAJOR.MINOR.PATCH 及 prerelease（如 0.1.0-rc.5）。无 prerelease 视为最稳定（比任何带 prerelease 的新）。
        /// </summary>
        private static bool IsRemoteNewer(string local, string remote)
        {
            return CompareVersions(local, remote) < 0;
        }

        /// <summary>比较两个版本号：返回 &lt;0 表示 a 旧于 b，0 相同，&gt;0 表示 a 新于 b。</summary>
        private static int CompareVersions(string a, string b)
        {
            // 拆出主次补丁数字段
            var ra = Regex.Match(a ?? "", @"^(\d+)\.(\d+)\.(\d+)");
            var rb = Regex.Match(b ?? "", @"^(\d+)\.(\d+)\.(\d+)");
            if (!ra.Success || !rb.Success) return string.CompareOrdinal(a, b);
            for (int i = 1; i <= 3; i++)
            {
                int na = int.Parse(ra.Groups[i].Value);
                int nb = int.Parse(rb.Groups[i].Value);
                if (na != nb) return na.CompareTo(nb);
            }
            // 主次补丁相同，比较 prerelease；无 prerelease 的最新
            int pa = GetPreReleaseNum(a);
            int pb = GetPreReleaseNum(b);
            return pa.CompareTo(pb);
        }

        /// <summary>取 prerelease 段里的数字（如 -rc.6 → 6）；无 prerelease 返回 int.MaxValue（视为最稳定/最新）。</summary>
        private static int GetPreReleaseNum(string v)
        {
            var pm = Regex.Match(v ?? "", @"-([^.\s]+)(?:\.(\d+))?");
            if (!pm.Success) return int.MaxValue;
            // 形如 rc.6 或 beta.3：取最后一段数字
            var num = Regex.Match(pm.Groups[0].Value, @"(\d+)\s*$");
            return num.Success ? int.Parse(num.Groups[1].Value) : 0;
        }

        /// <summary>
        /// 创建带合理 User-Agent 的 HttpClient（供下载/抓取共用）。
        /// useProxy=true：走代理（HTTPS_PROXY/HTTP_PROXY 环境变量优先，其次系统代理），没有则直连；
        /// useProxy=false：强制直连（绕过代理）。
        /// 注意：某些代理出口 IP 会被 GitHub codeload 限流（429），而直连反而正常，故下载需直连/代理都尝试。
        /// </summary>
        internal static HttpClient CreateHttpClient(int timeoutSec, bool useProxy = true)
        {
            var handler = new HttpClientHandler { UseProxy = useProxy, AllowAutoRedirect = true };
            if (useProxy)
            {
                string proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                            ?? Environment.GetEnvironmentVariable("https_proxy")
                            ?? Environment.GetEnvironmentVariable("HTTP_PROXY");
                if (!string.IsNullOrEmpty(proxy))
                {
                    handler.Proxy = new System.Net.WebProxy(proxy);
                    handler.UseProxy = true;
                }
            }
            else
            {
                handler.UseProxy = false;   // 强制直连
            }
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
            return client;
        }

        /// <summary>带代理感知的短超时抓取；失败返回 null。</summary>
        private static string TryFetchText(string url, int timeoutSec)
        {
            try
            {
                using var client = CreateHttpClient(timeoutSec);
                return client.GetStringAsync(url).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        // ── 外部浏览器：自动探测现代浏览器（Edge/Chrome/Firefox），不再依赖系统默认关联 ──
        private static readonly string[] BrowserCandidates =
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files\Mozilla Firefox\firefox.exe",
            @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe"
        };

        private string FindModernBrowser()
        {
            foreach (var c in BrowserCandidates)
                if (File.Exists(c)) return c;
            // 注册表 App Paths（用户自定义安装位置）
            foreach (var name in new[] { "msedge.exe", "chrome.exe", "firefox.exe" })
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                    if (key != null)
                    {
                        string p = key.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                    }
                }
                catch { }
            }
            return null;
        }

        private void OpenExternalBrowser()
        {
            try
            {
                string browser = FindModernBrowser();
                if (browser != null)
                {
                    string name = Path.GetFileNameWithoutExtension(browser).ToLowerInvariant();
                    // Edge/Chrome 用 --app 模式（无地址栏更像内置浏览器）；Firefox 不支持，直接开 URL
                    string args = name == "firefox" ? UiUrl : "--app=" + UiUrl;
                    Process.Start(new ProcessStartInfo(browser, args) { UseShellExecute = false });
                    Log("已用 " + Path.GetFileName(browser) + " 打开外部浏览器。");
                    return;
                }
                Process.Start(new ProcessStartInfo(UiUrl) { UseShellExecute = true });
                Log("未找到 Edge/Chrome/Firefox，已通过系统默认浏览器打开。");
            }
            catch (Exception ex)
            {
                Log("打开外部浏览器失败：" + ex.Message);
                MessageBox.Show("打开外部浏览器失败：\n" + ex.Message +
                    "\n\n请安装 Edge 或 Chrome，或使用内置浏览器（操作栏「修复浏览器」可自动安装 WebView2 Runtime）。",
                    "打开浏览器失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ── WebView2 Runtime 自动安装（内置浏览器） ──────────────
        private const string WebView2BootstrapperUrl = @"https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// <summary>
        /// 下载并静默安装 WebView2 Runtime（Evergreen 引导安装器，约 2MB，可装到当前用户、无需管理员），
        /// 装好后自动重试初始化内置浏览器。供 OnShown 失败时自动调用，也可由操作栏「修复浏览器」按钮手动触发。
        /// </summary>
        private async Task TryInstallWebView2Async()
        {
            try
            {
                var r = MessageBox.Show(
                    "内置浏览器需要 WebView2 Runtime。\n\n" +
                    "是否自动下载并静默安装？（约 2MB，安装到当前用户，无需管理员权限）",
                    "安装/修复内置浏览器", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;

                string exe = Path.Combine(LauncherDir, "MicrosoftEdgeWebview2Setup.exe");
                Log("正在下载 WebView2 Runtime 安装器 ...");
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
                {
                    byte[] data = await client.GetByteArrayAsync(WebView2BootstrapperUrl);
                    File.WriteAllBytes(exe, data);
                }
                Log("安装器已下载（" + new FileInfo(exe).Length / 1024 + " KB），正在静默安装 ...");

                using var installer = Process.Start(new ProcessStartInfo(exe, "/silent /install")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (installer == null) throw new Exception("无法启动 WebView2 安装器");
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5)))
                {
                    await installer.WaitForExitAsync(cts.Token);
                }
                try { File.Delete(exe); } catch { }
                Log("WebView2 安装进程已退出（代码 " + installer.ExitCode + "），重新初始化内置浏览器 ...");

                try
                {
                    var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                        browserExecutableFolder: null, userDataFolder: WebViewDataDir);
                    await _web.EnsureCoreWebView2Async(env);
                    Log("内置浏览器已初始化（WebView2 安装后）。");
                }
                catch (Exception ex2)
                {
                    Diagnose("WebView2 安装后初始化仍失败 - " + ex2.GetType().Name + ": " + ex2.Message);
                    MessageBox.Show("内置浏览器仍然不可用：\n" + ex2.Message +
                        "\n\n请手动安装 WebView2 Runtime：\nhttps://developer.microsoft.com/microsoft-edge/webview2/",
                        "内置浏览器不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Log("WebView2 自动安装失败：" + ex.Message);
                MessageBox.Show("自动安装 WebView2 失败：" + ex.Message +
                    "\n\n请手动到以下地址安装：\nhttps://developer.microsoft.com/microsoft-edge/webview2/",
                    "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 自动升级 node 到 v24（免安装 zip 解压到 LauncherDir\node）。
        /// 背景：dsh 0.1.1+ 的 scripts/build.ts 用 import.meta.main 判断入口，该特性在 node22+tsx 下为
        /// undefined 导致构建静默失败；node 24 稳定支持。升级后 FindNode 优先使用自管理 node 24。
        /// </summary>
        internal static async Task TryUpgradeNodeAsync(IWin32Window owner, Action<string> log)
        {
            try
            {
                string current = FindNode();
                string curVer = current != null ? NodeVersion(current) : null;
                if (curVer != null && MajorOf(curVer) >= 24)
                {
                    log("当前 node 已为 v24+（" + curVer + "），无需升级。");
                    MessageBox.Show(owner, "当前 Node.js 版本：" + curVer + "（≥ v24），已满足要求，无需升级。",
                        "Node.js 版本", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var r = MessageBox.Show(owner,
                    "当前 Node.js：" + (curVer ?? "未找到") + "\n\n" +
                    "dsh 0.1.1+ 的构建需要 node v24（v22 下 import.meta.main 不生效，可能导致构建失败）。\n" +
                    "是否自动下载并安装 node v24.12.0（免安装版，解压到启动器目录，不改系统环境变量）？",
                    "升级 Node.js 到 v24", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;

                const string ver = "24.12.0";
                string zipPath = Path.Combine(LauncherDir, "node-upgrade.zip");
                string destDir = Path.Combine(ManagedNodeDir, "node-v" + ver + "-win-x64");
                string[] urls =
                {
                    @"https://nodejs.org/dist/v" + ver + @"/node-v" + ver + @"-win-x64.zip",          // 官方
                    @"https://npmmirror.com/mirrors/node/v" + ver + @"/node-v" + ver + @"-win-x64.zip", // 国内镜像
                };

                log("正在下载 node v" + ver + " ...");
                bool downloaded = false;
                foreach (var url in urls)
                {
                    foreach (bool useProxy in new[] { false, true })
                    {
                        try
                        {
                            using (var client = MainForm.CreateHttpClient(600, useProxy))
                            {
                                var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                                if (!resp.IsSuccessStatusCode) { log("HTTP " + (int)resp.StatusCode + "，尝试下一源..."); continue; }
                                using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                                await resp.Content.CopyToAsync(fs);
                            }
                            downloaded = true; break;
                        }
                        catch (Exception ex) { log("下载 node 失败：" + ex.Message + "，尝试下一源..."); }
                    }
                    if (downloaded) break;
                }
                if (!downloaded) throw new Exception("node v" + ver + " 下载失败（所有源均不可用）");

                log("node 下载完成（" + new FileInfo(zipPath).Length / 1024 / 1024 + " MB），正在解压 ...");
                Directory.CreateDirectory(ManagedNodeDir);
                if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
                ZipFile.ExtractToDirectory(zipPath, ManagedNodeDir);
                try { File.Delete(zipPath); } catch { }

                // 验证解压出的 node 可用
                string newExe = Path.Combine(destDir, "node.exe");
                string newVer = File.Exists(newExe) ? NodeVersion(newExe) : null;
                if (newVer == null) throw new Exception("解压后未找到可用的 node.exe");
                log("node v" + ver + " 安装完成，位于 " + destDir + "（" + newVer + "）。重启启动器后生效。");
                MessageBox.Show(owner,
                    "Node.js 已升级到 " + newVer + "（免安装版）。\n\n" +
                    "请关闭并重新打开启动器，之后将优先使用 node v24 进行构建，可解决 dsh 构建失败问题。",
                    "升级完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                log("升级 node 失败：" + ex.Message);
                MessageBox.Show(owner, "自动升级 Node.js 失败：\n" + ex.Message +
                    "\n\n可手动到 https://nodejs.org/en/download 下载 v24 win-x64 版本。",
                    "升级失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
