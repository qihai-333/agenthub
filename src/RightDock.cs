using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgentHub
{
    // 右侧栏独立小窗：主窗口在显示应用时会缩到 92px，画不下 260px 的面板，
    // 所以这条栏用独立窗口贴在屏幕右边，和主窗口、被接管的应用窗口并列。
    internal class RightDock : Form
    {
        private readonly Shell _shell;
        private int _hot = -1;          // 0 = 添加进程，1 = 切换模式
        private int _press = -1;        // 按住的按钮（动画期间保留）：0=添加进程 1=切换模式
        private bool _held;             // 左键是否仍按住
        private float _relT = 1f;       // 松开回弹进度
        private readonly Timer _anim = new Timer();

        internal RightDock(Shell shell)
        {
            _shell = shell;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // 不加 TopMost：和左侧栏一样待在普通窗口层，不会盖住用户自己的窗口
            BackColor = Color.FromArgb(14, 16, 22);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
            _anim.Interval = 16;
            _anim.Tick += AnimTick;
        }

        // 不抢焦点：点面板不应该让当前应用失去焦点
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt+Tab
                return cp;
            }
        }

        internal void AttachToScreen(int width)
        {
            Rectangle scr = Screen.PrimaryScreen.Bounds;
            Bounds = new Rectangle(scr.Right - width, scr.Top, width, scr.Height);
        }

        private Rectangle AddRect()
        {
            return new Rectangle(20, 150, Width - 40, 64);
        }

        private Rectangle SwitchRect()
        {
            return new Rectangle(20, 240, Width - 40, 52);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // 深空岩板底：纵向明暗 + 顶部高光棱
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(ClientRectangle,
                Color.FromArgb(24, 26, 36), Color.FromArgb(12, 14, 20),
                System.Drawing.Drawing2D.LinearGradientMode.Vertical))
                g.FillRectangle(lg, ClientRectangle);
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, Width, Math.Max(2, 30)),
                Color.FromArgb(60, 255, 255, 255), Color.Transparent,
                System.Drawing.Drawing2D.LinearGradientMode.Vertical))
                g.FillRectangle(lg, new Rectangle(0, 0, Width, Math.Max(2, 30)));
            using (var p = new Pen(Color.FromArgb(40, 40, 52)))
                g.DrawLine(p, 0, 0, 0, Height);

            using (var f = new Font("微软雅黑", 13f, FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, "当前模式", f, new Rectangle(20, 28, Width - 40, 22),
                    Color.FromArgb(154, 154, 163), TextFormatFlags.Left);

            using (var f = new Font("微软雅黑", 24f, FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, _shell.ModeTitle(), f, new Rectangle(20, 58, Width - 40, 38),
                    Color.FromArgb(232, 232, 238), TextFormatFlags.Left);

            Rectangle add = AddRect();
            if (_press == 0) add = ScaleRect(add, _held ? 0.86f : ReleaseSpring(_relT));   // 按压缩放/回弹
            bool off = _shell.AgentCount >= 18;
            Color addCol = off ? Color.FromArgb(60, 60, 70)
                          : (_hot == 0 ? ControlPaint.Light(Shell.AccentDefault, 0.10f) : Shell.AccentDefault);
            using (var path = Round(add, 14))
            using (var b = new SolidBrush(addCol))
                g.FillPath(b, path);
            using (var f = new Font("微软雅黑", 18f, FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, off ? "已达上限" : "＋  添加进程", f, add, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            Rectangle sw = SwitchRect();
            if (_press == 1) sw = ScaleRect(sw, _held ? 0.86f : ReleaseSpring(_relT));   // 按压缩放/回弹
            using (var path = Round(sw, 12))
            {
                using (var b = new SolidBrush(_hot == 1 ? Color.FromArgb(38, 40, 52) : Color.FromArgb(24, 26, 36)))
                    g.FillPath(b, path);
                using (var p2 = new Pen(Color.FromArgb(44, 46, 58)))
                    g.DrawPath(p2, path);
            }
            using (var f = new Font("微软雅黑", 16f, FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, "切换模式", f, sw, Color.FromArgb(200, 201, 210),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private static System.Drawing.Drawing2D.GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = Math.Max(1, radius * 2);
            if (r.Width <= d || r.Height <= d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = AddRect().Contains(e.Location) ? 0 : (SwitchRect().Contains(e.Location) ? 1 : -1);
            if (h != _hot) { _hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot != -1) { _hot = -1; Cursor = Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (AddRect().Contains(e.Location))
            {
                _press = 0; _held = true; _relT = 1f;
                if (!_anim.Enabled) _anim.Start();
                Invalidate();
                _shell.DockAddProcess();
                return;
            }
            if (SwitchRect().Contains(e.Location))
            {
                _press = 1; _held = true; _relT = 1f;
                if (!_anim.Enabled) _anim.Start();
                Invalidate();
                _shell.SwitchModeFromPanel();
                return;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            _held = false;
            if (_relT >= 1f) { _relT = 0f; if (!_anim.Enabled) _anim.Start(); }
            Invalidate();
        }

        private void AnimTick(object sender, EventArgs e)
        {
            bool active = false;
            if (_relT < 1f) { _relT = Approach(_relT, 1f, 0.13f); active = true; }
            if (!_held && _relT >= 1f && _press != -1) { _press = -1; active = true; }
            if (active) Invalidate();
            else _anim.Stop();
        }

        private static float Approach(float v, float target, float rate)
        {
            float d = target - v;
            if (Math.Abs(d) < 0.002f) return target;
            return v + d * rate;
        }

        // 松开回弹：0.86 起，带一次轻微过冲回 1.0
        private static float ReleaseSpring(float t)
        {
            if (t <= 0f) return 0.86f;
            if (t >= 1f) return 1f;
            return 1f + (float)(-0.14 * Math.Exp(-5.0 * t) * Math.Cos(9.0 * t));
        }

        private static Rectangle ScaleRect(Rectangle r, float s)
        {
            if (s > 0.995f && s < 1.005f) return r;
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            int nw = (int)Math.Round(r.Width * s), nh = (int)Math.Round(r.Height * s);
            return new Rectangle(cx - nw / 2, cy - nh / 2, nw, nh);
        }

        // 面板自己不该被关闭
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }
    }
}
