using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AgentHub
{
    // 开屏窗口：逐个显示预加载进度，第一个就绪后由外部关掉。
    internal class Splash : Form
    {
        private static readonly Color Bg = Color.FromArgb(13, 13, 16);
        private static readonly Color Fg = Color.FromArgb(236, 236, 240);
        private static readonly Color Dim = Color.FromArgb(132, 132, 142);
        private static readonly Color Orange = Color.FromArgb(255, 122, 26);
        private static readonly Color Bad = Color.FromArgb(255, 92, 92);
        private static readonly Color Good = Color.FromArgb(96, 210, 150);

        private readonly List<Agent> _items = new List<Agent>();
        private readonly Dictionary<string, string> _status = new Dictionary<string, string>();
        private readonly Dictionary<string, Color> _color = new Dictionary<string, Color>();
        private readonly Bitmap _logo;

        public Splash(List<Agent> items, Bitmap logo)
        {
            _logo = logo;
            foreach (Agent a in items)
            {
                _items.Add(a);
                _status[a.Id] = "等待中";
                _color[a.Id] = Dim;
            }

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Bg;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint, true);

            int rows = Math.Max(1, _items.Count);
            ClientSize = new Size(440, 130 + rows * 34 + 26);

            var close = new Label();
            close.Text = "×";
            close.Font = new Font("Segoe UI", 12f);
            close.ForeColor = Dim;
            close.AutoSize = false;
            close.TextAlign = ContentAlignment.MiddleCenter;
            close.SetBounds(ClientSize.Width - 34, 8, 26, 26);
            close.Cursor = Cursors.Hand;
            close.Click += delegate { Close(); };
            Controls.Add(close);
        }

        public void Set(string id, string text, int kind)   // 0 等待 1 加载中 2 就绪 3 失败
        {
            if (IsDisposed) return;
            _status[id] = text;
            _color[id] = kind == 2 ? Good : (kind == 3 ? Bad : (kind == 1 ? Orange : Dim));
            Invalidate();
            Update();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Bg);

            using (var p = new Pen(Color.FromArgb(44, 44, 52)))
                g.DrawRectangle(p, 0.5f, 0.5f, Width - 1.5f, Height - 1.5f);

            if (_logo != null)
                g.DrawImage(_logo, new Rectangle(20, 20, 44, 44));

            using (var f = new Font("等线", 15f, FontStyle.Bold))
            using (var b = new SolidBrush(Fg))
                g.DrawString("AGENT HUB", f, b, 76, 22);

            using (var f = new Font("Microsoft YaHei UI", 8.5f))
            using (var b = new SolidBrush(Dim))
                g.DrawString("正在预加载上次打开的应用", f, b, 78, 48);

            int y = 96;
            using (var f = new Font("Microsoft YaHei UI", 9f))
            using (var sfRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            {
                foreach (Agent a in _items)
                {
                    using (var b = new SolidBrush(Fg))
                        g.DrawString(a.Name, f, b, new RectangleF(22, y, 250, 26));
                    using (var b = new SolidBrush(_color[a.Id]))
                        g.DrawString(_status[a.Id], f, b, new RectangleF(150, y, 266, 26), sfRight);
                    y += 34;
                }
            }

            using (var f = new Font("Microsoft YaHei UI", 7.5f))
            using (var b = new SolidBrush(Color.FromArgb(90, 90, 100)))
                g.DrawString("右下角 × 可跳过，剩下的等你点击时再启动", f, b, 22, Height - 26);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
