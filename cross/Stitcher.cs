using System;
using SkiaSharp;

namespace AltSnip;

/// <summary>长截图拼接：相邻帧顶部与已拼图底部滑动匹配，找重叠后接上新内容。（算法已用合成数据验证）</summary>
public static class Stitcher
{
    static byte[] Gray(SKBitmap b)
    {
        int w = b.Width, h = b.Height, rb = b.RowBytes;
        byte[] px = b.Bytes;
        var g = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * rb;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;             // Bgra8888
                g[y * w + x] = (byte)((px[i] + px[i + 1] + px[i + 2]) / 3);
            }
        }
        return g;
    }

    /// <summary>在 f 中找"新内容起始行"；返回 f.Height 表示没有可靠的新内容（到底/失配）。</summary>
    public static int FindNewStart(SKBitmap acc, SKBitmap f)
    {
        if (acc.Width != f.Width) return f.Height;
        int W = acc.Width, accH = acc.Height, fH = f.Height;
        byte[] accG = Gray(acc), fG = Gray(f);

        const int band = 48, topM = 8, scrollbar = 24, colStep = 3;
        if (fH < band + topM + 2) return fH;

        long best = long.MaxValue; int bestAccY = -1;
        int searchTop = Math.Max(0, accH - 720);
        int searchBot = accH - band;
        for (int accY = searchTop; accY <= searchBot; accY++)
        {
            long diff = 0;
            for (int r = 0; r < band; r++)
            {
                int fr = (topM + r) * W, ar = (accY + r) * W;
                for (int c = 0; c < W - scrollbar; c += colStep)
                {
                    int d = fG[fr + c] - accG[ar + c];
                    diff += d < 0 ? -d : d;
                }
                if (diff >= best) break;
            }
            if (diff < best) { best = diff; bestAccY = accY; }
        }
        if (bestAccY < 0) return fH;

        long samples = (long)band * ((W - scrollbar + colStep - 1) / colStep);
        double avg = (double)best / samples;
        if (avg > 26) return fH;                    // 没找到可靠重叠 → 停
        int startY = topM + (accH - bestAccY);      // f 中与 acc 底边对齐处 = 新内容起点
        return Math.Max(0, Math.Min(fH, startY));
    }

    public static SKBitmap Append(SKBitmap acc, SKBitmap f, int fromY)
    {
        int add = f.Height - fromY;
        if (add <= 0) return acc;
        var res = new SKBitmap(acc.Width, acc.Height + add, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(res))
        {
            c.DrawBitmap(acc, 0, 0);
            c.DrawBitmap(f, new SKRect(0, fromY, f.Width, f.Height),
                         new SKRect(0, acc.Height, f.Width, acc.Height + add));
        }
        acc.Dispose();
        return res;
    }
}
