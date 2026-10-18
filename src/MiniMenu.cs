using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AgentHub
{
    internal class MiniMenu : Form
    {
        private static MiniMenu _current;

        private readonly string[] _labels;
        private readonly Action[] _actions;
        private int _hot = -1;

        private MiniMenu(string[] labels, Action[] actions)
        {
            _labels = labels;
            _actions = actions;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(16, 16, 20);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);

            int w = 150;
            using (var f = new Font("Microsoft YaHei UI", 9.5f))
            {
                foreach (string s in labels)
                {
                    Size sz = TextRenderer.MeasureText(s, f);
                    if (sz.Width + 40 > w) w = sz.Width + 40;
                }
            }
            Width = w;
            Height = labels.Length * 34 + 8;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;
                return cp;
            }
        }

        private int RowAt(int y)
        {
            int i = (y - 4) / 34;
            return (i >= 0 && i < _labels.Length) ? i : -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            using (var p = new Pen(Color.FromArgb(46, 46, 54)))
                g.DrawRectangle(p, 0.5f, 0.5f, Width - 1.5f, Height - 1.5f);

            using (var f = new Font("Microsoft YaHei UI", 9.5f))
            {
                for (int i = 0; i < _labels.Length; i++)
                {
                    Rectangle r = new Rectangle(3, 4 + i * 34, Width - 6, 34);
                    if (i == _hot)
                        using (var b = new SolidBrush(Color.FromArgb(255, 122, 26)))
                            g.FillRectangle(b, r);
                    TextRenderer.DrawText(g, _labels[i], f, r,
                        i == _hot ? Color.FromArgb(11, 11, 13) : Color.FromArgb(234, 234, 238),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = RowAt(e.Y);
            if (i != _hot) { _hot = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot != -1) { _hot = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int i = RowAt(e.Y);
            if (i < 0) return;
            Action act = _actions[i];

            // 不要在鼠标消息的处理栈里 Close() + 执行动作：
            // 动作可能把主窗口从 92px 撑满屏幕，而此时本窗口正在销毁，
            // WinForms 收尾时会碰到已释放的对象。先让这条消息处理完再说。
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    Close();
                    if (act != null) act();
                });
            }
            catch
            {
                Close();
                if (act != null) act();
            }
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public static void ShowAt(Point screen, string[] labels, Action[] actions)
        {
            try
            {
                if (_current != null && !_current.IsDisposed) _current.Close();
            }
            catch { }

            var m = new MiniMenu(labels, actions);
            Rectangle scr = Screen.PrimaryScreen.WorkingArea;
            int x = Math.Min(screen.X, scr.Right - m.Width - 4);
            int y = Math.Min(screen.Y, scr.Bottom - m.Height - 4);
            m.Location = new Point(Math.Max(scr.X + 4, x), Math.Max(scr.Y + 4, y));
            _current = m;
            m.Show();
            m.Activate();
        }

        public static void ShowAt(Point screen, string label, Action action)
        {
            ShowAt(screen, new string[] { label }, new Action[] { action });
        }
    }
}
