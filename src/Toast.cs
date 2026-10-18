using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgentHub
{
    internal class Toast : Form
    {
        private static Toast _current;

        private readonly Timer _life = new Timer();
        private readonly Timer _fade = new Timer();
        private readonly string _text;

        private Toast(string text)
        {
            _text = text;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(18, 18, 22);
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);

            using (var f = new Font("Microsoft YaHei UI", 9.5f))
            {
                Size sz = TextRenderer.MeasureText(text, f);
                Width = Math.Max(180, sz.Width + 48);
                Height = 44;
            }

            Rectangle scr = Screen.PrimaryScreen.WorkingArea;
            Bounds = new Rectangle(scr.X + (scr.Width - Width) / 2, scr.Bottom - 110, Width, Height);
            Opacity = 0.96;

            _life.Interval = 2000;
            _life.Tick += delegate
            {
                _life.Stop();
                _fade.Start();
            };
            _life.Start();

            _fade.Interval = 40;
            _fade.Tick += delegate
            {
                Opacity -= 0.12;
                if (Opacity <= 0.02) { _fade.Stop(); Close(); }
            };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            using (var p = new Pen(Color.FromArgb(255, 122, 26), 1.2f))
                g.DrawRectangle(p, 0.5f, 0.5f, Width - 1.5f, Height - 1.5f);

            using (var f = new Font("Microsoft YaHei UI", 9.5f))
                TextRenderer.DrawText(g, _text, f, ClientRectangle, Color.FromArgb(240, 240, 245),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        public static void Show(string text)
        {
            try
            {
                if (_current != null && !_current.IsDisposed) _current.Close();
            }
            catch { }
            _current = new Toast(text);
            _current.Show();
            _current.TopMost = true;
        }
    }
}
