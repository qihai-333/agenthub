using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace AgentHub
{
    internal static class Icons
    {
        private static readonly Dictionary<string, Bitmap> _icons = new Dictionary<string, Bitmap>();

        private static readonly Color[] Palette = new Color[]
        {
            Color.FromArgb(255, 122, 26),
            Color.FromArgb(90, 140, 255),
            Color.FromArgb(70, 190, 150),
            Color.FromArgb(200, 110, 220),
            Color.FromArgb(230, 90, 110),
            Color.FromArgb(230, 180, 70),
            Color.FromArgb(110, 200, 230),
        };

        public static Bitmap Get(Agent a)
        {
            Bitmap bmp;
            if (a != null && _icons.TryGetValue(a.Id, out bmp)) return bmp;
            bmp = Extract(a);
            if (a != null) _icons[a.Id] = bmp;
            return bmp;
        }

        public static void Forget(string id)
        {
            if (id != null) _icons.Remove(id);
        }

        private static Bitmap Extract(Agent a)
        {
            if (a == null) return null;
            try
            {
                string path = a.Exe;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using (Icon ic = Icon.ExtractAssociatedIcon(path))
                {
                    if (ic == null) return null;
                    using (Bitmap src = ic.ToBitmap())
                    {
                        return Scale(src, 48);
                    }
                }
            }
            catch { return null; }
        }

        private static Bitmap Scale(Bitmap src, int size)
        {
            var dst = new Bitmap(size, size);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.DrawImage(src, new Rectangle(0, 0, size, size));
            }
            return dst;
        }

        private static readonly Dictionary<string, bool> _darkCache = new Dictionary<string, bool>();

        public static bool IsDark(Bitmap bmp, string key)
        {
            if (bmp == null) return false;
            bool v;
            if (key != null && _darkCache.TryGetValue(key, out v)) return v;

            double sum = 0;
            int n = 0;
            for (int y = 0; y < bmp.Height; y += 3)
            {
                for (int x = 0; x < bmp.Width; x += 3)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (c.A < 40) continue;
                    sum += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                    n++;
                }
            }
            bool dark = n > 0 && (sum / n) < 42;
            if (key != null) _darkCache[key] = dark;
            return dark;
        }

        public static Bitmap Letter(string name, int size)
        {
            string key = "L:" + (name ?? "") + ":" + size;
            Bitmap cached;
            if (_icons.TryGetValue(key, out cached)) return cached;

            string ch = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1);
            Color c = Palette[Math.Abs((name ?? "").GetHashCode()) % Palette.Length];

            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                float r = size * 0.28f;
                using (var path = new GraphicsPath())
                {
                    path.AddArc(0, 0, r * 2, r * 2, 180, 90);
                    path.AddArc(size - r * 2, 0, r * 2, r * 2, 270, 90);
                    path.AddArc(size - r * 2, size - r * 2, r * 2, r * 2, 0, 90);
                    path.AddArc(0, size - r * 2, r * 2, r * 2, 90, 90);
                    path.CloseFigure();
                    using (var b = new SolidBrush(c)) g.FillPath(b, path);
                }

                using (var f = new Font("Microsoft YaHei UI", size * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat())
                using (var tb = new SolidBrush(Color.FromArgb(11, 11, 13)))
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(ch, f, tb, new RectangleF(0, 0, size, size), sf);
                }
            }

            _icons[key] = bmp;
            return bmp;
        }
    }
}
