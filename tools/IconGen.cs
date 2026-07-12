// AltSnip 图标/Logo 生成器 —— 用「无为」品牌 VI 的设计语言，但是截图工具的专属图标。
// 造型：玄墨/靛青渐变方圆底 + 月白取景框四角标(一眼=截图/选区) + 中心一颗朱赭"一念"火种(快门/对焦点)。
// 无为主标「一念之门·圆相」是品牌主标，不做产品图标（否则各产品雷同）；产品图标沿用 VI 色板+圆头线性语言。
// 色板：玄墨黑 #16191E / 靛青 #274A63 / 月白 #F4F6F8 / 银灰 #B7C0C7 / 朱赭 #C05F3C。
// 编译： csc /target:exe /out:IconGen.exe /reference:System.Drawing.dll tools\IconGen.cs
// 运行： IconGen.exe <输出目录>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

class IconGen
{
    // 无为 VI
    static readonly Color INK    = Hex("#16191E"); // 玄墨黑
    static readonly Color INK2   = Hex("#22384A"); // 玄墨→靛青（压深）
    static readonly Color MOON   = Hex("#F4F6F8"); // 月白
    static readonly Color SILVER = Hex("#AEB8C0"); // 银灰（圆环渐隐）
    static readonly Color SPARK  = Hex("#C05F3C"); // 朱赭·一念火种

    static Color Hex(string h)
    {
        h = h.TrimStart('#');
        return Color.FromArgb(
            Convert.ToInt32(h.Substring(0, 2), 16),
            Convert.ToInt32(h.Substring(2, 2), 16),
            Convert.ToInt32(h.Substring(4, 2), 16));
    }

    static Bitmap DrawLogo(int S)
    {
        var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 方圆底：玄墨黑 → 靛青 对角渐变
            float radius = 58 * S / 256f;
            var tile = new RectangleF(0.5f, 0.5f, S - 1f, S - 1f);
            using (var path = Rounded(tile, radius))
            using (var br = new LinearGradientBrush(new PointF(0, 0), new PointF(S, S), INK, INK2))
                g.FillPath(br, path);

            // 取景框四角标（月白，圆头线性）——一眼=截图/选区
            float cx = S / 2f, cy = S / 2f;
            float sw = S * 0.072f;
            float inset = S * 0.265f, arm = S * 0.155f;
            float lo = inset, hi = S - inset;
            using (var rb = new LinearGradientBrush(new PointF(0, 0), new PointF(S, S), MOON, SILVER))
            using (var pen = new Pen(rb, sw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                Bracket(g, pen, lo, lo, +1, +1, arm); // 左上
                Bracket(g, pen, hi, lo, -1, +1, arm); // 右上
                Bracket(g, pen, lo, hi, +1, -1, arm); // 左下
                Bracket(g, pen, hi, hi, -1, -1, arm); // 右下
            }

            // 中心一念火种（朱赭 + 柔光）——快门/对焦点，也是品牌"一念"
            float dotR = S * 0.056f;
            using (var glow = new GraphicsPath())
            {
                glow.AddEllipse(cx - dotR * 2.8f, cy - dotR * 2.8f, dotR * 5.6f, dotR * 5.6f);
                using (var pgb = new PathGradientBrush(glow)
                {
                    CenterColor = Color.FromArgb(130, SPARK),
                    SurroundColors = new[] { Color.FromArgb(0, SPARK) },
                    CenterPoint = new PointF(cx, cy),
                })
                    g.FillPath(pgb, glow);
            }
            using (var db = new SolidBrush(SPARK))
                g.FillEllipse(db, cx - dotR, cy - dotR, dotR * 2, dotR * 2);
        }
        return bmp;
    }

    // 一个 L 形角标：肘点 (ex,ey)，两臂沿 dirX/dirY 方向伸出 arm
    static void Bracket(Graphics g, Pen pen, float ex, float ey, int dirX, int dirY, float arm)
    {
        using (var p = new GraphicsPath())
        {
            p.AddLine(ex + dirX * arm, ey, ex, ey);
            p.AddLine(ex, ey, ex, ey + dirY * arm);
            g.DrawPath(pen, p);
        }
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        int[] sizes = { 256, 128, 64, 48, 32, 16 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (var bmp = DrawLogo(s))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
                if (s == 256) bmp.Save(Path.Combine(outDir, "logo_256.png"), ImageFormat.Png);
            }
        }

        string icoPath = Path.Combine(outDir, "app.ico");
        using (var fs = File.Create(icoPath))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0);
            w.Write((short)1);
            w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte wh = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(wh); w.Write(wh);
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var png in pngs) w.Write(png);
        }
        Console.WriteLine("Wrote " + icoPath + " and logo_256.png");
    }
}
