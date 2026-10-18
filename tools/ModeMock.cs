using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

class ModeMock
{
    static readonly Color Bg = Color.FromArgb(255, 255, 255);
    static readonly Color Ink = Color.FromArgb(18, 18, 20);
    static readonly Color Ink2 = Color.FromArgb(110, 110, 118);
    static readonly Color Line = Color.FromArgb(228, 228, 232);
    static readonly Color Accent = Color.FromArgb(255, 122, 26);

    static void Main()
    {
        int w = 1000, h = 600;
        Render("A").Save(@"D:\dsh\agenthub\shots\mode-a.png");
        Render("B").Save(@"D:\dsh\agenthub\shots\mode-b.png");
        Render("C").Save(@"D:\dsh\agenthub\shots\mode-c.png");
        Console.WriteLine("ok");
    }

    static Bitmap Render(string variant)
    {
        int w = 1000, h = 600;
        var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Bg);

            // 侧栏占位
            using (var b = new SolidBrush(Color.FromArgb(11, 11, 13)))
                g.FillRectangle(b, 0, 0, 36, h);
            using (var b = new SolidBrush(Color.FromArgb(255, 255, 255)))
                g.FillRectangle(b, 36, 0, w - 36, h);

            using (var f = new Font("Microsoft YaHei UI", 7f))
            using (var b = new SolidBrush(Color.FromArgb(150, 150, 155)))
                g.DrawString("方案 " + variant, f, b, 46, 12);

            if (variant == "A") LayoutA(g, w, h);
            else if (variant == "B") LayoutB(g, w, h);
            else LayoutC(g, w, h);
        }
        return bmp;
    }

    // 标题 + 两张横排卡片，整体居中
    static void LayoutA(Graphics g, int w, int h)
    {
        float cx = 36 + (w - 36) / 2f;
        Title(g, "选择你的工作模式", cx, 190, 44, true);

        float cardW = 300, cardH = 190, gap = 28;
        float x0 = cx - cardW - gap / 2f;
        Card(g, new RectangleF(x0, 300, cardW, cardH), "多 agent 协同模式", "三个应用同屏分栏，一起干活", true);
        Card(g, new RectangleF(x0 + cardW + gap, 300, cardW, cardH), "普通工作站模式", "一次专注一个，切换即换", false);
    }

    // 标题左上 + 纵向堆叠卡片
    static void LayoutB(Graphics g, int w, int h)
    {
        Title(g, "选择你的工作模式", 70, 90, 38, false);
        Card(g, new RectangleF(70, 180, 520, 130), "多 agent 协同模式", "三个应用同屏分栏，一起干活", true);
        Card(g, new RectangleF(70, 330, 520, 130), "普通工作站模式", "一次专注一个，切换即换", false);

        // 右侧留白里放个示意图
        using (var p = new Pen(Line, 1.4f))
        {
            g.DrawRectangle(p, 640, 180, 300, 280);
            g.DrawLine(p, 740, 180, 740, 460);
            g.DrawLine(p, 840, 180, 840, 460);
        }
        using (var f = new Font("Microsoft YaHei UI", 8f))
        using (var b = new SolidBrush(Ink2))
            g.DrawString("协同：三栏", f, b, 650, 470);
    }

    // 左右两块平铺，标题压在中线上
    static void LayoutC(Graphics g, int w, int h)
    {
        float mid = 36 + (w - 36) / 2f;
        using (var b = new SolidBrush(Color.FromArgb(250, 250, 251)))
            g.FillRectangle(b, 36, 0, mid - 36, h);

        Title(g, "选择你的工作模式", mid, 120, 34, true);

        Card(g, new RectangleF(80, 200, mid - 160, 300), "多 agent 协同模式", "三个应用同屏分栏，一起干活", true);
        Card(g, new RectangleF(mid + 40, 200, mid - 160, 300), "普通工作站模式", "一次专注一个，切换即换", false);
    }

    static void Title(Graphics g, string text, float cx, float y, float size, bool centered)
    {
        using (var path = new GraphicsPath())
        using (var fam = new FontFamily("等线"))
        {
            path.AddString(text, fam, (int)FontStyle.Bold, size, new PointF(0, 0), StringFormat.GenericTypographic);
            RectangleF b = path.GetBounds();
            float x = centered ? cx - b.Width / 2f : cx;
            using (var m = new Matrix()) { m.Translate(x - b.X, y); path.Transform(m); }

            // 阴影
            for (int i = 6; i >= 1; i--)
            {
                using (var sh = (GraphicsPath)path.Clone())
                {
                    using (var m2 = new Matrix()) { m2.Translate(0, i * 1.1f); sh.Transform(m2); }
                    using (var br = new SolidBrush(Color.FromArgb(9, 0, 0, 0)))
                        g.FillPath(br, sh);
                }
            }
            // 主体：上浅下深
            RectangleF gb = path.GetBounds();
            using (var lg = new LinearGradientBrush(new RectangleF(gb.X, gb.Y, gb.Width, gb.Height),
                       Color.FromArgb(70, 70, 78), Ink, LinearGradientMode.Vertical))
                g.FillPath(lg, path);
        }
    }

    static void Card(Graphics g, RectangleF r, string title, string sub, bool accent)
    {
        using (var p = new Pen(accent ? Accent : Line, accent ? 2f : 1.2f))
        using (var path = Round(r, 18))
            g.DrawPath(p, path);

        using (var f = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold))
        using (var b = new SolidBrush(Ink))
            g.DrawString(title, f, b, r.X + 24, r.Y + 26);

        using (var f = new Font("Microsoft YaHei UI", 9.5f))
        using (var b = new SolidBrush(Ink2))
            g.DrawString(sub, f, b, r.X + 24, r.Y + 62);

        using (var f = new Font("Microsoft YaHei UI", 8.5f))
        using (var b = new SolidBrush(accent ? Accent : Color.FromArgb(170, 170, 176)))
            g.DrawString(accent ? "→ 进入协同" : "→ 进入工作台", f, b, r.X + 24, r.Bottom - 34);
    }

    static GraphicsPath Round(RectangleF r, int rad)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, rad, rad, 180, 90);
        p.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
        p.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
        p.AddArc(r.X, r.Bottom - rad, rad, rad, 90, 90);
        p.CloseFigure();
        return p;
    }
}
