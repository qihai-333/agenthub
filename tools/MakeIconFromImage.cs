using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class MakeIconFromImage
{
    private static readonly int[] Sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static Bitmap Render(Image src, Rectangle crop, int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(src, new Rectangle(0, 0, s, s), crop, GraphicsUnit.Pixel);
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
        p += andSize;
        return data;
    }

    private static void Main(string[] args)
    {
        string src = args.Length > 0 ? args[0] : @"brand\logo.jpg";
        string outPath = args.Length > 1 ? args[1] : "icon.ico";
        string previewPath = args.Length > 2 ? args[2] : @"shots\icon-preview2.png";

        using (var img = Image.FromFile(src))
        {
            int side = Math.Min(img.Width, img.Height);
            var crop = new Rectangle((img.Width - side) / 2, (img.Height - side) / 2, side, side);

            var images = new byte[Sizes.Length][];
            for (int i = 0; i < Sizes.Length; i++)
            {
                using (Bitmap bmp = Render(img, crop, Sizes[i]))
                    images[i] = ToDib(bmp);
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

            using (Bitmap big = Render(img, crop, 256))
                big.Save(previewPath, ImageFormat.Png);
        }

        using (var ic = new Icon(outPath))
        {
            Console.WriteLine("icon ok " + ic.Width + "x" + ic.Height + " bytes=" + new FileInfo(outPath).Length);
        }
    }
}
