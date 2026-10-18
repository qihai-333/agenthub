using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

class FontPreview
{
    static void Main()
    {
        string[] names = { "等线" };
        int w = 1500, rowH = 620;
        using (var bmp = new Bitmap(w, rowH * names.Length))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.FromArgb(11, 11, 13));

            for (int r = 0; r < names.Length; r++)
            {
                float cx = w / 2f, cy = rowH * r + rowH / 2f;
                using (var f = new Font(names[r], 18, FontStyle.Regular))
                using (var sb = new SolidBrush(Color.FromArgb(120, 120, 130)))
                    g.DrawString(names[r], f, sb, 16, rowH * r + 10);

                DrawTitle(g, "开始工作", names[r], 228, cx, cy);
            }

            bmp.Save(@"D:\dsh\agenthub\shots\fonts.png");
        }
        Console.WriteLine("ok");
    }

    static void DrawTitle(Graphics g, string text, string family, float em, float cx, float cy)
    {
        using (var fam = new FontFamily(family))
        using (var path = new GraphicsPath())
        {
            path.AddString(text, fam, (int)FontStyle.Bold, em, new PointF(0, 0), StringFormat.GenericTypographic);
            RectangleF b = path.GetBounds();
            using (var mtx = new Matrix())
            {
                mtx.Translate(cx - (b.X + b.Width / 2f), cy - (b.Y + b.Height / 2f));
                path.Transform(mtx);
            }

            for (int s = 3; s >= 1; s--)
            {
                using (var gl = (GraphicsPath)path.Clone())
                using (var m = new Matrix())
                {
                    m.Translate(cx, cy);
                    m.Scale(1f + s * 0.022f, 1f + s * 0.022f);
                    m.Translate(-cx, -cy);
                    gl.Transform(m);
                    using (var br = new SolidBrush(Color.FromArgb(18, 255, 122, 26)))
                        g.FillPath(br, gl);
                }
            }

            for (int i = 4; i >= 1; i--)
            {
                using (var ex = (GraphicsPath)path.Clone())
                {
                    using (var m = new Matrix()) { m.Translate(0, i * 1.9f); ex.Transform(m); }
                    using (var br = new SolidBrush(Color.FromArgb(255, 104, 40, 2)))
                        g.FillPath(br, ex);
                }
            }

            RectangleF gb = path.GetBounds();
            var rect = new RectangleF(gb.X, gb.Y, gb.Width, gb.Height);
            using (var lg = new LinearGradientBrush(rect, Color.White, Color.Black, LinearGradientMode.Vertical))
            {
                lg.InterpolationColors = new ColorBlend
                {
                    Colors = new[]
                    {
                        Color.FromArgb(255, 205, 138),
                        Color.FromArgb(255, 126, 28),
                        Color.FromArgb(206, 78, 6)
                    },
                    Positions = new[] { 0f, 0.5f, 1f }
                };
                g.FillPath(lg, path);
            }

            Region old = g.Clip;
            g.SetClip(path, CombineMode.Intersect);
            var hl = new RectangleF(gb.X, gb.Y, gb.Width, gb.Height * 0.46f);
            using (var hb = new LinearGradientBrush(hl, Color.FromArgb(130, 255, 244, 220),
                       Color.FromArgb(0, 255, 244, 220), LinearGradientMode.Vertical))
                g.FillRectangle(hb, hl);
            g.Clip = old;

            using (var pen = new Pen(Color.FromArgb(95, 255, 202, 146), 1f))
                g.DrawPath(pen, path);
        }
    }
}
