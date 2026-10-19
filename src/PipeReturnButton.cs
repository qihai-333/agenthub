using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgentHub
{
    // 协同模式聚焦应用时的悬浮返回按钮。
    // 聚焦时应用的真实窗口盖在主窗上面，主窗上画的任何东西都被挡住，
    // 所以返回按钮必须是自己的置顶小窗（WS_EX_NOACTIVATE，点击不抢焦点）。
    internal class PipeReturnButton : Form
    {
        private static PipeReturnButton _current;

        private bool _hot;

        private PipeReturnButton(Action onClick)
        {
            Click2 = onClick;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(255, 122, 26);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);

            Rectangle scr = Screen.PrimaryScreen.WorkingArea;
            Bounds = new Rectangle(scr.X + 108, scr.Y + 14, 124, 38);
        }

        private readonly Action Click2;

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE：点了不把前台从应用手里抢走
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(_hot ? Color.FromArgb(255, 150, 70) : Color.FromArgb(255, 122, 26));

            using (var f = new Font("微软雅黑", 10.5f, FontStyle.Bold))
                TextRenderer.DrawText(g, "← 返回流程", f, ClientRectangle, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hot = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = false;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Action a = Click2;
            Close();
            if (a != null) a();
        }

        // 聚焦状态下显示；离开聚焦（回平铺/退出协同）由调用方关闭
        public static void Show(Action onClick)
        {
            Hide();
            _current = new PipeReturnButton(onClick);
            _current.Show();
        }

        // new：与 Control.Hide() 同名但是静态方法，语义不同
        public static new void Hide()
        {
            try { if (_current != null && !_current.IsDisposed) _current.Close(); }
            catch { }
            _current = null;
        }
    }
}
