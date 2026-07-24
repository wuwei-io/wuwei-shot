using System;
using SkiaSharp;

namespace WuweiShot;

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

    /// <summary>两帧是否几乎相同（= 没滚动）。采样对比灰度，恒定成本。
    /// 用于挡住"周期性重复内容"误匹配导致的静止时无限追加。</summary>
    public static bool NearlySame(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        int W = a.Width, H = a.Height, rb = a.RowBytes;
        byte[] pa = a.Bytes, pb = b.Bytes;
        long diff = 0, n = 0;
        for (int y = 0; y < H; y += 4)
        {
            int row = y * rb;
            for (int x = 0; x < W; x += 8)
            {
                int i = row + x * 4;
                int d = (pa[i] - pb[i]) + (pa[i + 1] - pb[i + 1]) + (pa[i + 2] - pb[i + 2]);
                diff += d < 0 ? -d : d;
                n++;
            }
        }
        return n > 0 && (double)diff / n < 6.0;   // 平均近乎为 0 = 静止
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

    /// <summary>增量拼接累加器：避免每帧重建整张大图(O(n²))。
    /// 每帧只裁出"新内容"存为一条切片，最后一次性合成(O(n))；匹配只对最近 tail 段做，成本恒定。</summary>
    public sealed class Accumulator : IDisposable
    {
        readonly System.Collections.Generic.List<SKBitmap> _slices = new();
        SKBitmap _tail;            // 已拼内容的尾部若干行，作为下一帧的匹配参照
        const int TAIL = 760;      // 尾段高度上限(≥FindNewStart 的 720 搜索窗)
        public int Width { get; }
        public int Height { get; private set; }

        public Accumulator(SKBitmap frame0)
        {
            Width = frame0.Width;
            _slices.Add(Copy(frame0));
            Height = frame0.Height;
            _tail = TailOf(frame0);
        }

        /// <summary>喂入一帧；有新内容则追加并返回 true。仅做恒定成本的 tail 匹配 + 一次小拷贝。</summary>
        public bool Feed(SKBitmap f)
        {
            if (f.Width != Width) return false;
            int start = FindNewStart(_tail, f);
            int add = f.Height - start;
            if (add < 4) return false;             // 无可靠新内容
            var slice = new SKBitmap(Width, add, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(slice))
                c.DrawBitmap(f, new SKRect(0, start, Width, f.Height), new SKRect(0, 0, Width, add));
            _slices.Add(slice);
            Height += add;
            var oldTail = _tail; _tail = TailOf(f); oldTail.Dispose();
            return true;
        }

        /// <summary>一次性合成最终长图(单遍 O(总高))。</summary>
        public SKBitmap Compose()
        {
            var res = new SKBitmap(Width, Math.Max(1, Height), SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(res))
            {
                int y = 0;
                foreach (var s in _slices) { c.DrawBitmap(s, 0, y); y += s.Height; }
            }
            return res;
        }

        static SKBitmap TailOf(SKBitmap f)
        {
            int h = Math.Min(TAIL, f.Height);
            var t = new SKBitmap(f.Width, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(t))
                c.DrawBitmap(f, new SKRect(0, f.Height - h, f.Width, f.Height), new SKRect(0, 0, f.Width, h));
            return t;
        }

        static SKBitmap Copy(SKBitmap f)
        {
            var b = new SKBitmap(f.Width, f.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(b)) c.DrawBitmap(f, 0, 0);
            return b;
        }

        public void Dispose()
        {
            foreach (var s in _slices) s.Dispose();
            _slices.Clear();
            _tail?.Dispose();
        }
    }
}
