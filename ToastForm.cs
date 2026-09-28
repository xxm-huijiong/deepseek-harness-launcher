using System;
using System.Drawing;
using System.Windows.Forms;

namespace DshLauncher
{
    /// <summary>
    /// 自绘提醒窗（屏幕右下角）：不依赖 Windows 通知设置 / 专注助手，保证「任务提醒」一定能被看到。
    /// 置顶但不抢焦点、不出现在任务栏与 Alt+Tab；约 8 秒后自动消失（鼠标移入暂停），点击可显示主窗口。
    /// 与系统托盘气泡并存：气泡用于在通知中心留记录，自绘窗用于保证可见。
    /// </summary>
    internal class ToastForm : Form
    {
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Action _onClick;
        private readonly Label _titleLabel;
        private readonly Label _textLabel;

        /// <param name="title">标题（如「任务已完成」）。</param>
        /// <param name="text">正文（多行会自动截断显示）。</param>
        /// <param name="onClick">点击提醒窗时的回调（通常用于把主窗口带到前台）。</param>
        public ToastForm(string title, string text, Action onClick)
        {
            _onClick = onClick;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 32, 32);
            ClientSize = new Size(348, 108);
            Font = new Font("Microsoft YaHei UI", 9f);

            _titleLabel = new Label
            {
                Text = title ?? "",
                ForeColor = Color.White,
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = false,
                AutoEllipsis = true,
                Location = new Point(14, 12),
                Size = new Size(320, 22),
                BackColor = Color.Transparent
            };
            _textLabel = new Label
            {
                Text = text ?? "",
                ForeColor = Color.FromArgb(214, 214, 214),
                AutoSize = false,
                AutoEllipsis = true,
                Location = new Point(14, 38),
                Size = new Size(320, 58),
                BackColor = Color.Transparent
            };
            Controls.Add(_titleLabel);
            Controls.Add(_textLabel);

            // 点击任意位置 → 关闭并把主窗口带到前台
            EventHandler onClickAnywhere = (s, e) =>
            {
                try { Close(); } catch { }
                try { _onClick?.Invoke(); } catch { }
            };
            Click += onClickAnywhere;
            _titleLabel.Click += onClickAnywhere;
            _textLabel.Click += onClickAnywhere;

            // 鼠标移入暂停自动关闭，移出后 2 秒关闭（方便看清长文本）
            EventHandler pause = (s, e) => { try { _timer.Stop(); } catch { } };
            EventHandler resume = (s, e) => { try { _timer.Interval = 2000; _timer.Start(); } catch { } };
            foreach (var c in new Control[] { this, _titleLabel, _textLabel })
            {
                c.MouseEnter += pause;
                c.MouseLeave += resume;
            }

            _timer = new System.Windows.Forms.Timer { Interval = 8000 };
            _timer.Tick += (s, e) => { _timer.Stop(); try { Close(); } catch { } };
            _timer.Start();
        }

        /// <summary>显示时不抢焦点（用户正在 dsh 里打字时不应被打断）。</summary>
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：不激活窗口
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不显示在 Alt+Tab
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                // 右下角贴边（避开任务栏）：优先用鼠标所在显示器的可用区域（多屏时更符合直觉）
                var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
            }
            catch
            {
                var wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Color.FromArgb(90, 90, 90));
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _timer?.Stop(); } catch { }
                try { _timer?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
