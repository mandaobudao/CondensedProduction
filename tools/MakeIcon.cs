using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

// 图标工具：深蓝背景 + 三个小方块浓缩为一个发光大方块 + "xN" 字样
internal static class MakeIcon
{
    private static int Main(string[] args)
    {
        try
        {
            string pluginDir = args[0];
            string distIcon = args[1];
            Directory.CreateDirectory(pluginDir);
            Directory.CreateDirectory(Path.GetDirectoryName(distIcon));
            using (Bitmap bmp = new Bitmap(256, 256))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Draw(g);
                bmp.Save(distIcon, System.Drawing.Imaging.ImageFormat.Png);
            }
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static void Draw(Graphics g)
    {
        g.Clear(Color.FromArgb(14, 18, 34));
        // 三个小的暗色机器（左侧纵向）
        for (int i = 0; i < 3; i++)
        {
            var r = new Rectangle(28, 40 + i * 62, 52, 48);
            using (var b = new SolidBrush(Color.FromArgb(70, 90, 130)))
                g.FillRectangle(b, r);
            using (var p = new Pen(Color.FromArgb(120, 145, 190), 3))
                g.DrawRectangle(p, r);
        }
        // 箭头
        using (var p = new Pen(Color.FromArgb(255, 200, 80), 8))
        {
            p.EndCap = LineCap.ArrowAnchor;
            g.DrawLine(p, 92, 128, 130, 128);
        }
        // 一个大的发光机器（右侧）
        var big = new Rectangle(142, 52, 90, 152);
        using (var b = new SolidBrush(Color.FromArgb(46, 62, 110)))
            g.FillRectangle(b, big);
        using (var p = new Pen(Color.FromArgb(120, 190, 255), 4))
            g.DrawRectangle(p, big);
        using (var p = new Pen(Color.FromArgb(90, 150, 230, 120), 10))
            g.DrawRectangle(p, big.X - 6, big.Y - 6, big.Width + 12, big.Height + 12);
        // "xN" 字样
        using (var f = new Font("Arial", 44, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var b = new SolidBrush(Color.FromArgb(255, 214, 90)))
        {
            g.DrawString("xN", f, b, 152, 104);
        }
    }
}
