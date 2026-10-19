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

        internal RightDock(Shell shell)
        {
            _shell = shell;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(250, 250, 251);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);
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
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            using (var p = new Pen(Color.FromArgb(232, 232, 236))) g.DrawLine(p, 0, 0, 0, Height);

            using (var f = new Font("微软雅黑", 13f, FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, "当前模式", f, new Rectangle(20, 28, Width - 40, 22),
                    Color.FromArgb(150, 150, 158), TextFormatFlags.Left);

            using (var f = new Font("微软雅黑", 24f, FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, _shell.ModeTitle(), f, new Rectangle(20, 58, Width - 40, 38),
                    Color.FromArgb(20, 20, 24), TextFormatFlags.Left);

            Rectangle add = AddRect();
            bool off = _shell.AgentCount >= 18;
            Color addCol = off ? Color.FromArgb(205, 205, 210)
                          : (_hot == 0 ? ControlPaint.Light(Color.Orange, 0.10f) : Color.Orange);
            using (var path = Round(add, 14))
            using (var b = new SolidBrush(addCol))
                g.FillPath(b, path);
            using (var f = new Font("微软雅黑", 18f, FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, off ? "已达上限" : "＋  添加进程", f, add, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            Rectangle sw = SwitchRect();
            using (var path = Round(sw, 12))
            using (var b = new SolidBrush(_hot == 1 ? Color.FromArgb(228, 228, 233) : Color.FromArgb(240, 240, 243)))
                g.FillPath(b, path);
            using (var f = new Font("微软雅黑", 16f, FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, "切换模式", f, sw, Color.FromArgb(70, 70, 80),
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
            if (AddRect().Contains(e.Location)) { _shell.DockAddProcess(); return; }
            if (SwitchRect().Contains(e.Location)) { _shell.SwitchModeFromPanel(); return; }
        }

        // 面板自己不该被关闭
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }
    }
}
