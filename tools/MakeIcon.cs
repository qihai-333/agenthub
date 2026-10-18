using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class MakeIcon
{
    private static readonly int[] Sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static Bitmap Render(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float r = s * 0.26f;
            var rect = new RectangleF(0, 0, s, s);
            using (var path = new GraphicsPath())
            {
                path.AddArc(rect.X, rect.Y, r * 2, r * 2, 180, 90);
                path.AddArc(rect.Right - r * 2, rect.Y, r * 2, r * 2, 270, 90);
                path.AddArc(rect.Right - r * 2, rect.Bottom - r * 2, r * 2, r * 2, 0, 90);
                path.AddArc(rect.X, rect.Bottom - r * 2, r * 2, r * 2, 90, 90);
                path.CloseFigure();
                using (var b = new SolidBrush(Color.FromArgb(255, 122, 26)))
                    g.FillPath(b, path);
            }

            using (var f = new Font("Segoe UI", s * 0.64f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat())
            using (var tb = new SolidBrush(Color.FromArgb(11, 11, 13)))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                var rf = new RectangleF(0, s * 0.03f, s, s);
                g.DrawString("A", f, tb, rf, sf);
            }
        }
        return bmp;
    }

    private static byte[] ToDib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        int rowXor = w * 4;
        int xorSize = rowXor * h;
        int rowAnd = ((w + 31) / 32) * 4;
        int andSize = rowAnd * h;
        int total = 40 + xorSize + andSize;

        var data = new byte[total];
        int p = 0;

        BitConverter.GetBytes(40).CopyTo(data, p); p += 4;
        BitConverter.GetBytes(w).CopyTo(data, p); p += 4;
        BitConverter.GetBytes(h * 2).CopyTo(data, p); p += 4;
        BitConverter.GetBytes((short)1).CopyTo(data, p); p += 2;
        BitConverter.GetBytes((short)32).CopyTo(data, p); p += 2;
        BitConverter.GetBytes(0).CopyTo(data, p); p += 4;
        BitConverter.GetBytes(xorSize + andSize).CopyTo(data, p); p += 4;
        BitConverter.GetBytes(0).CopyTo(data, p); p += 4;
        BitConverter.GetBytes(0).CopyTo(data, p); p += 4;
        BitConverter.GetBytes(0).CopyTo(data, p); p += 4;
        BitConverter.GetBytes(0).CopyTo(data, p); p += 4;

        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = 0; x < w; x++)
            {
                Color c = bmp.GetPixel(x, y);
                data[p++] = c.B;
                data[p++] = c.G;
                data[p++] = c.R;
                data[p++] = c.A;
            }
        }
        // AND mask 全 0：透明度交给 alpha 通道
        p += andSize;
        return data;
    }

    private static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "icon.ico";
        var images = new byte[Sizes.Length][];
        for (int i = 0; i < Sizes.Length; i++)
        {
            using (Bitmap bmp = Render(Sizes[i]))
            {
                images[i] = ToDib(bmp);
            }
        }

        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)Sizes.Length);

            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int s = Sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(images[i].Length);
                bw.Write(offset);
                offset += images[i].Length;
            }
            for (int i = 0; i < Sizes.Length; i++) bw.Write(images[i]);
        }

        using (var ic = new Icon(outPath))
        {
            Console.WriteLine("icon ok " + ic.Width + "x" + ic.Height + " bytes=" + new FileInfo(outPath).Length);
        }
    }
}
