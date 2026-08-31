using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DshLauncher
{
    /// <summary>
    /// 首次使用的前置安装向导：检测 Node.js → 自动安装 dsh 全局包（npm install -g @deepseek-ai/dsh），
    /// 完成后进入主界面。npm 方式无需源码构建。
    /// </summary>
    internal class InstallForm : Form
    {
        // ── UI ──────────────────────────────────────────────────
        private readonly Label _lblNode = new Label();
        private readonly Label _lblDsh = new Label();
        private readonly Button _btnInstall = new Button { Text = "开始安装", Width = 110, Height = 30 };
        private readonly Button _btnSkip = new Button { Text = "跳过", Width = 80, Height = 30 };
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
                Text = "首次使用，需要先安装运行环境与 dsh",
                Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
                Location = new Point(14, 12), AutoSize = false, Size = new Size(392, 26)
            };
            Controls.Add(title);

            var envBox = new GroupBox { Text = "运行环境", Location = new Point(14, 44), Size = new Size(392, 70) };
            _lblNode.Location = new Point(12, 22); _lblNode.AutoSize = true; _lblNode.ForeColor = Color.FromArgb(120, 120, 120);
            _lblDsh.Location = new Point(12, 44); _lblDsh.AutoSize = true; _lblDsh.ForeColor = Color.FromArgb(120, 120, 120);
            envBox.Controls.Add(_lblNode);
            envBox.Controls.Add(_lblDsh);
            Controls.Add(envBox);

            var hint = new Label
            {
                Text = "将自动安装 dsh（npm install -g @deepseek-ai/dsh），无需源码构建。\n用户数据存放在 userdata 目录，更新/重装不受影响。",
                Location = new Point(14, 122), AutoSize = false, Size = new Size(392, 44),
                ForeColor = Color.FromArgb(100, 100, 100)
            };
            Controls.Add(hint);

            _btnInstall.Location = new Point(14, 206);
            _btnSkip.Location = new Point(134, 206);
            _btnCancel.Location = new Point(322, 206);
            _btnInstall.Click += async (s, e) => await StartInstallAsync();
            _btnSkip.Click += (s, e) => SkipInstall();
            _btnCancel.Click += (s, e) => Close();
            Controls.Add(_btnInstall);
            Controls.Add(_btnSkip);
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
            string node = await Task.Run(MainForm.FindManagedNode) ?? await Task.Run(MainForm.FindNode);
            if (node != null)
            {
                string ver = await Task.Run(() => MainForm.NodeVersion(node));
                _lblNode.Text = "✓ Node.js " + ver;
                _lblNode.ForeColor = Color.FromArgb(0, 140, 0);
            }
            else
            {
                _lblNode.Text = "✗ 未找到 Node.js（需 ≥ v22.19）";
                _lblNode.ForeColor = Color.FromArgb(200, 40, 40);
            }

            _lblDsh.Text = "● 检查 dsh ...";
            _lblDsh.ForeColor = Color.FromArgb(180, 120, 0);
            string dshCli = await Task.Run(MainForm.FindDshCli);
            if (dshCli != null)
            {
                _lblDsh.Text = "✓ dsh 已安装（" + MainForm.LocalVersion + "）";
                _lblDsh.ForeColor = Color.FromArgb(0, 140, 0);
            }
            else
            {
                _lblDsh.Text = "✗ 未安装 dsh（点「开始安装」自动安装）";
                _lblDsh.ForeColor = Color.FromArgb(200, 40, 40);
            }

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
                // 1) Node.js（缺失时提示安装）
                string node = MainForm.FindManagedNode() ?? MainForm.FindNode();
                if (node == null)
                {
                    var r = MessageBox.Show(
                        "未找到 Node.js（≥ v22.19）。\n\n是否自动下载安装 node v24（免安装版，不改系统环境变量）？",
                        "缺少 Node.js", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r == DialogResult.Yes)
                    {
                        // 复用主窗口的 node 24 自动升级
                        await MainForm.TryUpgradeNodeAsync(this, Log);
                        node = MainForm.FindManagedNode() ?? MainForm.FindNode();
                    }
                    if (node == null) return;
                }

                // 2) 确保全局 dsh 包已安装（npm install -g @deepseek-ai/dsh）
                SetStep("检查/安装 dsh 全局包 ...", 30);
                string dshCli = await MainForm.EnsureDshInstalledAsync(this, Log);
                if (dshCli == null) return;   // 用户取消或失败（已提示）

                SetStep("全部安装完成", 100);
                Log("安装完成！dsh 版本：" + MainForm.LocalVersion);
                MessageBox.Show("安装完成！即将进入主界面。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Completed = true;
                Close();
            }
            catch (Exception ex)
            {
                Log("安装失败：" + ex.Message);
                MessageBox.Show("自动安装失败：" + ex.Message,
                    "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _busy = false;
                SetButtons(true);
            }
        }

        /// <summary>跳过安装：仅当 dsh 已全局安装时可用（直接进入主界面）。</summary>
        private void SkipInstall()
        {
            if (_busy) return;
            if (MainForm.FindDshCli() != null)
            {
                Log("跳过安装（dsh 已可用）。");
                Completed = true;
                Close();
            }
            else
            {
                MessageBox.Show("尚未安装 dsh，无法跳过。\n请点「开始安装」自动安装。",
                    "无法跳过", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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
                _btnSkip.Enabled = enabled;
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
    }
}
