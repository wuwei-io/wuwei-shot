using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;

namespace AltSnip;

public enum Tool { None, Arrow, Line, Rect, Text, Mosaic }

public sealed class Anno
{
    public Tool Type;
    public SKPoint A, B;
    public string? Text;
    public SKColor Color;
    public float Width = 3f;
}

/// <summary>
/// 全屏取景 + 标注层（SkiaSharp 绘制，与最终成图共用同一套绘制代码）。
/// 坐标统一用 DIP；画到物理像素帧缓冲时对 canvas 做 Scale(_scale)。
/// </summary>
public sealed class OverlayControl : Control
{
    readonly SKBitmap _src;              // 物理像素原图
    readonly Action _onClose;
    readonly Action<SKImage> _onCopy;

    /// <summary>承载文字输入框的图层（由窗口提供，绝对定位）。</summary>
    public Canvas? TextLayer { get; set; }

    readonly SKBitmap _frame;             // 物理像素帧缓冲（尺寸=截图，只建一次）
    readonly SKBitmap _dimmed;            // 预烘焙「截图+压暗蒙版」，画框时每帧直接铺，省全屏混合
    Bitmap? _ava;                         // 每次重绘换一张 Avalonia 位图（旧的立即释放）
    bool _dirty = true;                   // 脏标记：把光栅化合并到每帧渲染只做一次，避免每个指针事件都重绘
    double _scale = 1;
    double _dipW, _dipH;
    double DipW => _dipW;
    double DipH => _dipH;

    // 选区（DIP）
    bool _dragging, _hasSel;
    SKPoint _start;
    SKRect _sel;

    // 鼠标准星（初始框选阶段）
    SKPoint _mouse;
    bool _mouseIn;
    // 透明 1×1 位图光标：可靠隐藏系统鼠标（比 StandardCursorType.None 更稳）
    internal static readonly Cursor CurHidden = MakeInvisibleCursor();

    static Cursor MakeInvisibleCursor()
    {
        try
        {
            using var sk = new SKBitmap(1, 1);            // 默认全透明
            using var img = SKImage.FromBitmap(sk);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new System.IO.MemoryStream();
            data.SaveTo(ms);
            ms.Position = 0;
            return new Cursor(new Bitmap(ms), new PixelPoint(0, 0));
        }
        catch { return new Cursor(StandardCursorType.None); }
    }

    // 移动/缩放
    bool _moving, _resizing;
    int _handle = -1;
    SKPoint _dragOrigin;
    SKRect _selOrig;

    // 标注
    Tool _tool = Tool.None;
    readonly List<Anno> _annos = new();
    Anno? _drawing;
    bool _annotating;

    // 样式
    SKColor _color = new(0xfa, 0x51, 0x51);
    float _width = 3f;
    static readonly SKColor[] PALETTE =
    {
        new(0xfa,0x51,0x51), new(0xf4,0xb7,0x40), new(0xe0,0x76,0x2a),
        new(0x2e,0xbe,0x6e), new(0x3b,0x82,0xf6), new(0xff,0xff,0xff), new(0x22,0x22,0x22),
    };
    static readonly float[] WIDTHS = { 2f, 4f, 7f };

    // 工具条（DIP）：id 1箭头 2直线 3方框 7文字 8马赛克 10长截图 4撤销 9保存 5取消 6确认
    const float BTN = 42, HS = 8, SUB = 30;
    readonly int[] _ids = { 1, 2, 3, 7, 8, 10, 4, 9, 5, 6 };
    readonly SKRect[] _rects = new SKRect[10];

    /// <summary>长截图回调：给出选区首帧(物理像素) + 选区(DIP)，由窗口接管滚动截取。</summary>
    public Action<SKBitmap, SKRect>? OnLongShot;
    SKRect _bar;
    int _hover;
    bool _showStyle;
    int _widthCount;
    SKRect _styleBar;
    readonly SKRect[] _colorRects = new SKRect[7];
    readonly SKRect[] _widthRects = new SKRect[3];

    // 文字输入
    TextBox? _tb;

    // 主题色
    static readonly SKColor C_GOLD = new(0xf4, 0xb7, 0x40);
    static readonly SKColor C_TEXT = new(0xf3, 0xec, 0xe0);
    static readonly SKColor C_TEXT2 = new(0xb0, 0x9b, 0x80);
    static readonly SKColor C_DEEP = new(0x0e, 0x0a, 0x06);
    static readonly SKColor C_CARD = new(0x1d, 0x15, 0x0b);

    static readonly SKTypeface CjkFace = ResolveCjk();
    static readonly SKTypeface UiFace = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;

    public OverlayControl(SKBitmap src, Action onClose, Action<SKImage> onCopy)
    {
        _src = src;
        _onClose = onClose;
        _onCopy = onCopy;
        _frame = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        // 预烘焙「截图 + 压暗蒙版」一次：画框时每帧只铺这张不透明图，省掉每帧的全屏截图重绘+蒙版混合
        _dimmed = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var dc = new SKCanvas(_dimmed))
        {
            dc.DrawBitmap(_src, 0, 0);
            using var p = new SKPaint { Color = new SKColor(0, 0, 0, 120) };
            dc.DrawRect(new SKRect(0, 0, src.Width, src.Height), p);
        }
        Focusable = true;
        Cursor = CurHidden;   // 隐藏系统灰十字，改用自绘金色准星
    }

    static SKTypeface ResolveCjk()
    {
        string[] fams = OperatingSystem.IsWindows()
            ? new[] { "Microsoft YaHei", "SimSun" }
            : OperatingSystem.IsMacOS()
                ? new[] { "PingFang SC", "Hiragino Sans GB" }
                : new[] { "Noto Sans CJK SC", "WenQuanYi Micro Hei" };
        foreach (var f in fams)
        {
            var t = SKTypeface.FromFamilyName(f);
            if (t != null && t.FamilyName.Contains(f.Split(' ')[0], StringComparison.OrdinalIgnoreCase)) return t;
        }
        return SKFontManager.Default.MatchCharacter('中') ?? SKTypeface.Default;
    }

    // ---------- 帧缓冲 / 显示 ----------
    protected override Size ArrangeOverride(Size finalSize)
    {
        var r = base.ArrangeOverride(finalSize);
        // 仅在尺寸真的变化时重绘（防死循环）；帧位图尺寸固定=截图尺寸，从不重建（防泄漏）
        if (finalSize.Width > 0 &&
            (Math.Abs(finalSize.Width - _dipW) > 0.5 || Math.Abs(finalSize.Height - _dipH) > 0.5))
        {
            _dipW = finalSize.Width;
            _dipH = finalSize.Height;
            _scale = _src.Width / _dipW;
            Repaint();
        }
        return r;
    }

    // 只置脏标记并请求渲染；真正的光栅化推迟到 Render()，同一帧内多次调用只光栅化一次
    void Repaint()
    {
        _dirty = true;
        InvalidateVisual();
    }

    void RebuildFrame()
    {
        if (_dipW <= 0) return;
        using (var c = new SKCanvas(_frame))
        {
            // 不需要 Clear：_dimmed 不透明且铺满整帧
            c.Scale((float)_scale);
            DrawScene(c);
        }
        var old = _ava;
        _ava = new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul,
            _frame.GetPixels(), new PixelSize(_frame.Width, _frame.Height), new Vector(96, 96), _frame.RowBytes);
        old?.Dispose();   // 释放上一张，杜绝泄漏
    }

    public override void Render(DrawingContext ctx)
    {
        if (_dirty) { RebuildFrame(); _dirty = false; }
        if (_ava != null) ctx.DrawImage(_ava, new Rect(0, 0, _dipW, _dipH));
    }

    // ---------- 场景绘制（DIP 坐标）----------
    void DrawScene(SKCanvas c)
    {
        var full = new SKRect(0, 0, (float)DipW, (float)DipH);
        c.DrawBitmap(_dimmed, full);   // 预烘焙的「截图+压暗」，一次不透明铺满（替代 截图+全屏蒙版 两步）

        if (_sel.Width > 0 && _sel.Height > 0)
        {
            c.Save();
            c.ClipRect(_sel);
            c.DrawBitmap(_src, full);
            DrawAnnos(c);
            if (_annotating && _drawing != null) DrawAnno(c, _drawing);
            c.Restore();

            using (var pen = new SKPaint { Color = C_GOLD, IsStroke = true, StrokeWidth = 1.4f, IsAntialias = false })
                c.DrawRect(_sel, pen);

            if (_dragging) DrawSizeLabel(c);
            if (_tool == Tool.None && !_dragging && !_moving && !_resizing) DrawHandles(c);
            DrawToolbar(c);
        }
        else if (!_dragging)
        {
            if (_mouseIn)
            {
                // 鼠标处的小十字准星：实线连续，金色（系统鼠标已隐藏，等于把鼠标变成十字）
                using var gp = new SKPaint { Color = C_GOLD, StrokeWidth = 1.5f, IsAntialias = true };
                float x = _mouse.X, y = _mouse.Y, arm = 11;
                c.DrawLine(x - arm, y, x + arm, y, gp);
                c.DrawLine(x, y - arm, x, y + arm, gp);
            }
            DrawTip(c);
        }
    }

    void DrawAnnos(SKCanvas c) { foreach (var a in _annos) DrawAnno(c, a); }

    void DrawAnno(SKCanvas c, Anno a)
    {
        if (a.Type == Tool.Mosaic) { DrawMosaic(c, Norm(a.A, a.B)); return; }
        if (a.Type == Tool.Text)
        {
            if (!string.IsNullOrEmpty(a.Text))
                using (var p = new SKPaint { Color = a.Color, IsAntialias = true, Typeface = CjkFace, TextSize = 18 })
                    c.DrawText(a.Text, a.A.X, a.A.Y + 16, p);
            return;
        }
        using var paint = new SKPaint
        {
            Color = a.Color,
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = a.Width,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        if (a.Type == Tool.Rect) c.DrawRect(Norm(a.A, a.B), paint);
        else if (a.Type == Tool.Line) c.DrawLine(a.A, a.B, paint);
        else if (a.Type == Tool.Arrow)
        {
            c.DrawLine(a.A, a.B, paint);
            double ang = Math.Atan2(a.B.Y - a.A.Y, a.B.X - a.A.X);
            float len = 10 + a.Width * 2.2f;
            for (int s = -1; s <= 1; s += 2)
            {
                double t = ang + Math.PI + s * 0.5;
                c.DrawLine(a.B, new SKPoint(a.B.X + (float)(Math.Cos(t) * len), a.B.Y + (float)(Math.Sin(t) * len)), paint);
            }
        }
    }

    void DrawMosaic(SKCanvas c, SKRect r)
    {
        if (r.Width < 2 || r.Height < 2) return;
        var srcRect = new SKRect(r.Left * (float)_scale, r.Top * (float)_scale, r.Right * (float)_scale, r.Bottom * (float)_scale);
        int bw = Math.Max(1, (int)(r.Width / 10)), bh = Math.Max(1, (int)(r.Height / 10));
        using var small = new SKBitmap(bw, bh);
        using (var sc = new SKCanvas(small))
        using (var hp = new SKPaint { FilterQuality = SKFilterQuality.Medium })
            sc.DrawBitmap(_src, srcRect, new SKRect(0, 0, bw, bh), hp);
        using var np = new SKPaint { FilterQuality = SKFilterQuality.None };
        c.DrawBitmap(small, r, np);
    }

    static SKRect Norm(SKPoint a, SKPoint b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    void DrawSizeLabel(SKCanvas c)
    {
        int w = (int)(_sel.Width * _scale), h = (int)(_sel.Height * _scale);
        string s = $"{w} × {h}";
        using var fp = new SKPaint { Color = C_GOLD, IsAntialias = true, Typeface = UiFace, TextSize = 12 };
        float tw = fp.MeasureText(s);
        float x = _sel.Left, y = _sel.Top - 6;
        if (y - 14 < 0) y = _sel.Top + 16;
        using (var bg = new SKPaint { Color = new SKColor(0x0e, 0x0a, 0x06, 235) })
            c.DrawRect(new SKRect(x, y - 14, x + tw + 12, y + 4), bg);
        c.DrawText(s, x + 6, y, fp);
    }

    void DrawTip(SKCanvas c)
    {
        string s = "拖动框选区域    ·    Enter 复制    ·    Esc 取消";
        using var fp = new SKPaint { Color = C_TEXT, IsAntialias = true, Typeface = CjkFace, TextSize = 15 };
        float tw = fp.MeasureText(s);
        float x = (float)(DipW - tw) / 2, y = 54;
        using (var bg = new SKPaint { Color = new SKColor(0x0e, 0x0a, 0x06, 200) })
            c.DrawRoundRect(new SKRect(x - 18, y - 22, x + tw + 18, y + 10), 10, 10, bg);
        c.DrawText(s, x, y, fp);
    }

    // ---------- 工具条 ----------
    void DrawToolbar(SKCanvas c)
    {
        using (var sh = new SKPaint { Color = new SKColor(0, 0, 0, 70), IsAntialias = true })
            c.DrawRoundRect(new SKRect(_bar.Left, _bar.Top + 3, _bar.Right, _bar.Bottom + 3), 10, 10, sh);
        using (var bg = new SKPaint { Color = new SKColor(0x0e, 0x0a, 0x06, 205), IsAntialias = true })
            c.DrawRoundRect(_bar, 10, 10, bg);

        for (int i = 0; i < _rects.Length; i++)
        {
            int id = _ids[i];
            var cell = _rects[i];
            bool active = (id == 1 && _tool == Tool.Arrow) || (id == 2 && _tool == Tool.Line) ||
                          (id == 3 && _tool == Tool.Rect) || (id == 7 && _tool == Tool.Text) || (id == 8 && _tool == Tool.Mosaic);
            bool hover = _hover == id;
            if (active) Chip(c, cell, C_GOLD, 46);
            else if (hover) Chip(c, cell, C_TEXT, 26);
            var ic = active ? C_GOLD : (hover ? C_TEXT : C_TEXT2);
            switch (id)
            {
                case 1: IconArrow(c, cell, ic); break;
                case 2: IconLine(c, cell, ic); break;
                case 3: IconRect(c, cell, ic); break;
                case 7: IconText(c, cell, ic); break;
                case 8: IconMosaic(c, cell, ic); break;
                case 10: IconLongShot(c, cell, ic); break;
                case 4: IconUndo(c, cell, _annos.Count == 0 ? new SKColor(0xb0, 0x9b, 0x80, 90) : ic); break;
                case 9: IconSave(c, cell, ic); break;
                case 5: IconCross(c, cell, hover ? C_TEXT : C_TEXT2); break;
                case 6: IconCheck(c, cell, C_GOLD); break;
            }
        }
        if (_showStyle) DrawStyleBar(c);
    }

    void DrawStyleBar(SKCanvas c)
    {
        using (var bg = new SKPaint { Color = new SKColor(0x0e, 0x0a, 0x06, 205), IsAntialias = true })
            c.DrawRoundRect(_styleBar, 9, 9, bg);
        for (int i = 0; i < PALETTE.Length; i++)
        {
            var cell = _colorRects[i];
            float cx = cell.MidX, cy = cell.MidY;
            using (var b = new SKPaint { Color = PALETTE[i], IsAntialias = true }) c.DrawCircle(cx, cy, 8, b);
            if (_color == PALETTE[i])
                using (var pen = new SKPaint { Color = C_GOLD, IsStroke = true, StrokeWidth = 2, IsAntialias = true })
                    c.DrawCircle(cx, cy, 11, pen);
        }
        for (int i = 0; i < _widthCount; i++)
        {
            var cell = _widthRects[i];
            float r = 3 + i * 2.5f;
            bool sel = Math.Abs(_width - WIDTHS[i]) < 0.01f;
            using var b = new SKPaint { Color = sel ? C_GOLD : C_TEXT2, IsAntialias = true };
            c.DrawCircle(cell.MidX, cell.MidY, r, b);
        }
    }

    void Chip(SKCanvas c, SKRect cell, SKColor col, byte a)
    {
        var r = new SKRect(cell.Left + 4, cell.Top + 4, cell.Right - 4, cell.Bottom - 4);
        using var b = new SKPaint { Color = col.WithAlpha(a), IsAntialias = true };
        c.DrawRoundRect(r, 8, 8, b);
    }

    static SKPaint Stroke(SKColor col, float w) => new()
    { Color = col, IsAntialias = true, IsStroke = true, StrokeWidth = w, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

    void IconArrow(SKCanvas c, SKRect r, SKColor col)
    {
        using var p = Stroke(col, 2.2f);
        var t = new SKPoint(r.Left + 13, r.Bottom - 13);
        var h = new SKPoint(r.Right - 13, r.Top + 13);
        c.DrawLine(t, h, p);
        double ang = Math.Atan2(h.Y - t.Y, h.X - t.X);
        for (int s = -1; s <= 1; s += 2)
        {
            double a = ang + Math.PI + s * 0.5;
            c.DrawLine(h, new SKPoint(h.X + (float)(Math.Cos(a) * 7), h.Y + (float)(Math.Sin(a) * 7)), p);
        }
    }
    void IconLine(SKCanvas c, SKRect r, SKColor col)
    { using var p = Stroke(col, 2.2f); c.DrawLine(new SKPoint(r.Left + 13, r.Bottom - 13), new SKPoint(r.Right - 13, r.Top + 13), p); }
    void IconRect(SKCanvas c, SKRect r, SKColor col)
    { using var p = Stroke(col, 2.2f); c.DrawRoundRect(new SKRect(r.Left + 12, r.Top + 13, r.Right - 12, r.Bottom - 13), 3, 3, p); }
    void IconText(SKCanvas c, SKRect r, SKColor col)
    {
        using var p = Stroke(col, 2.2f);
        float cx = r.MidX;
        c.DrawLine(new SKPoint(r.Left + 12, r.Top + 14), new SKPoint(r.Right - 12, r.Top + 14), p);
        c.DrawLine(new SKPoint(cx, r.Top + 14), new SKPoint(cx, r.Bottom - 13), p);
    }
    void IconMosaic(SKCanvas c, SKRect r, SKColor col)
    {
        float x0 = r.Left + 12, y0 = r.Top + 12, s = (r.Width - 24) / 3f;
        bool[] fill = { true, false, true, false, true, false, true, false, true };
        using var b = new SKPaint { Color = col, IsAntialias = true };
        for (int i = 0; i < 9; i++) if (fill[i]) c.DrawRect(new SKRect(x0 + i % 3 * s, y0 + i / 3 * s, x0 + i % 3 * s + s - 1.5f, y0 + i / 3 * s + s - 1.5f), b);
    }
    void IconUndo(SKCanvas c, SKRect r, SKColor col)
    {
        using var p = Stroke(col, 2.2f);
        var box = new SKRect(r.Left + 13, r.Top + 14, r.Right - 13, r.Bottom - 12);
        using var path = new SKPath();
        path.AddArc(box, 30, 280);
        c.DrawPath(path, p);
        double ang = 30 * Math.PI / 180;
        float tx = box.MidX + (float)(Math.Cos(ang) * box.Width / 2), ty = box.MidY + (float)(Math.Sin(ang) * box.Height / 2);
        c.DrawLine(new SKPoint(tx, ty), new SKPoint(tx - 5, ty - 2), p);
        c.DrawLine(new SKPoint(tx, ty), new SKPoint(tx + 1, ty - 6), p);
    }
    void IconSave(SKCanvas c, SKRect r, SKColor col)
    {
        using var p = Stroke(col, 2.2f);
        float cx = r.MidX, ty = r.Top + 11, by = r.Bottom - 16;
        c.DrawLine(new SKPoint(cx, ty), new SKPoint(cx, by), p);
        c.DrawLine(new SKPoint(cx, by), new SKPoint(cx - 5, by - 5), p);
        c.DrawLine(new SKPoint(cx, by), new SKPoint(cx + 5, by - 5), p);
        c.DrawLine(new SKPoint(r.Left + 11, r.Bottom - 11), new SKPoint(r.Right - 11, r.Bottom - 11), p);
    }

    // 长截图：细页框 + 两个向下箭头（滚动取长图）
    void IconLongShot(SKCanvas c, SKRect r, SKColor col)
    {
        using var p = Stroke(col, 2.0f);
        using (var rp = new SKPaint { Color = col, IsAntialias = true, IsStroke = true, StrokeWidth = 1.6f })
            c.DrawRoundRect(new SKRect(r.Left + 13, r.Top + 11, r.Right - 13, r.Bottom - 11), 2, 2, rp);
        float cx = r.MidX;
        c.DrawLine(new SKPoint(cx - 4, r.MidY - 4), new SKPoint(cx, r.MidY), p);
        c.DrawLine(new SKPoint(cx, r.MidY), new SKPoint(cx + 4, r.MidY - 4), p);
        c.DrawLine(new SKPoint(cx - 4, r.MidY + 1), new SKPoint(cx, r.MidY + 5), p);
        c.DrawLine(new SKPoint(cx, r.MidY + 5), new SKPoint(cx + 4, r.MidY + 1), p);
    }
    void IconCross(SKCanvas c, SKRect r, SKColor col)
    {
        using var p = Stroke(col, 2.3f);
        c.DrawLine(new SKPoint(r.Left + 15, r.Top + 15), new SKPoint(r.Right - 15, r.Bottom - 15), p);
        c.DrawLine(new SKPoint(r.Right - 15, r.Top + 15), new SKPoint(r.Left + 15, r.Bottom - 15), p);
    }
    void IconCheck(SKCanvas c, SKRect r, SKColor col)
    {
        using var p = Stroke(col, 2.6f);
        using var path = new SKPath();
        path.MoveTo(r.Left + 13, r.Top + 22);
        path.LineTo(r.Left + 18, r.Top + 27);
        path.LineTo(r.Left + 29, r.Top + 15);
        c.DrawPath(path, p);
    }

    // ---------- 手柄 ----------
    SKPoint[] HandlePts()
    {
        float l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom, cx = _sel.MidX, cy = _sel.MidY;
        return new[]
        {
            new SKPoint(l,t), new SKPoint(cx,t), new SKPoint(r,t), new SKPoint(r,cy),
            new SKPoint(r,b), new SKPoint(cx,b), new SKPoint(l,b), new SKPoint(l,cy),
        };
    }
    void DrawHandles(SKCanvas c)
    {
        using var b = new SKPaint { Color = C_GOLD, IsAntialias = true };
        using var pen = new SKPaint { Color = new SKColor(0x0e, 0x0a, 0x06, 180), IsStroke = true, StrokeWidth = 1 };
        foreach (var p in HandlePts())
        {
            var r = new SKRect(p.X - HS / 2, p.Y - HS / 2, p.X + HS / 2, p.Y + HS / 2);
            c.DrawRect(r, b); c.DrawRect(r, pen);
        }
    }
    int HitHandle(SKPoint p)
    {
        if (!_hasSel) return -1;
        var pts = HandlePts();
        for (int i = 0; i < 8; i++)
            if (Math.Abs(p.X - pts[i].X) <= 6 && Math.Abs(p.Y - pts[i].Y) <= 6) return i;
        return -1;
    }
    StandardCursorType HandleCursor(int h) => h switch
    {
        0 or 4 => StandardCursorType.TopLeftCorner,
        2 or 6 => StandardCursorType.TopRightCorner,
        1 or 5 => StandardCursorType.SizeNorthSouth,
        _ => StandardCursorType.SizeWestEast,
    };
    SKRect ResizeRect(SKRect o, int h, SKPoint p)
    {
        float l = o.Left, t = o.Top, r = o.Right, b = o.Bottom;
        switch (h)
        {
            case 0: l = p.X; t = p.Y; break;
            case 1: t = p.Y; break;
            case 2: r = p.X; t = p.Y; break;
            case 3: r = p.X; break;
            case 4: r = p.X; b = p.Y; break;
            case 5: b = p.Y; break;
            case 6: l = p.X; b = p.Y; break;
            case 7: l = p.X; break;
        }
        l = Clamp(l, 0, (float)DipW); r = Clamp(r, 0, (float)DipW);
        t = Clamp(t, 0, (float)DipH); b = Clamp(b, 0, (float)DipH);
        return new SKRect(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
    }
    static float Clamp(float v, float lo, float hi) => Math.Max(lo, Math.Min(hi, v));

    // ---------- 布局 ----------
    void Layout()
    {
        var sc = new SKRect(0, 0, (float)DipW, (float)DipH);
        bool style = _tool is Tool.Arrow or Tool.Line or Tool.Rect or Tool.Text;
        float reserve = style ? SUB + 6 : 0;
        float totalW = BTN * _rects.Length;
        const float GAP = 8;

        float bx = _sel.Right - totalW;
        if (bx + totalW > sc.Right) bx = sc.Right - totalW;
        if (bx < sc.Left) bx = sc.Left;

        float by;
        if (_sel.Bottom + GAP + BTN + reserve <= sc.Bottom) by = _sel.Bottom + GAP;
        else if (_sel.Top - GAP - BTN - reserve >= sc.Top) by = _sel.Top - GAP - BTN - reserve;
        else by = Math.Min(_sel.Bottom - BTN - GAP, sc.Bottom - BTN - reserve - 2);
        if (by < sc.Top + 2) by = sc.Top + 2;

        _bar = new SKRect(bx, by, bx + totalW, by + BTN);
        for (int i = 0; i < _rects.Length; i++) _rects[i] = new SKRect(bx + i * BTN, by, bx + i * BTN + BTN, by + BTN);

        _showStyle = style;
        if (!style) return;
        _widthCount = _tool == Tool.Text ? 0 : WIDTHS.Length;
        int nColor = PALETTE.Length;
        float gap = _widthCount > 0 ? 12 : 0;
        float w = 8 + nColor * SUB + gap + _widthCount * SUB + 8;
        float x = _bar.Left;
        if (x + w > sc.Right) x = sc.Right - w;
        if (x < sc.Left) x = sc.Left;
        float y = _bar.Bottom + 6;
        if (y + SUB > sc.Bottom) y = _bar.Top - SUB - 6;
        if (y < sc.Top) y = sc.Top + 2;
        _styleBar = new SKRect(x, y, x + w, y + SUB);
        float px = x + 8;
        for (int i = 0; i < nColor; i++) { _colorRects[i] = new SKRect(px, y, px + SUB, y + SUB); px += SUB; }
        px += gap;
        for (int i = 0; i < _widthCount; i++) { _widthRects[i] = new SKRect(px, y, px + SUB, y + SUB); px += SUB; }
    }

    int HitButton(SKPoint p)
    {
        if (!_hasSel) return 0;
        for (int i = 0; i < _rects.Length; i++) if (_rects[i].Contains(p)) return _ids[i];
        return 0;
    }
    bool HitStyle(SKPoint p)
    {
        if (!_showStyle) return false;
        for (int i = 0; i < PALETTE.Length; i++) if (_colorRects[i].Contains(p)) { _color = PALETTE[i]; if (_tb != null) _tb.Foreground = ToBrush(_color); Repaint(); return true; }
        for (int i = 0; i < _widthCount; i++) if (_widthRects[i].Contains(p)) { _width = WIDTHS[i]; Repaint(); return true; }
        return false;
    }

    // ---------- 交互 ----------
    static SKPoint P(Point p) => new((float)p.X, (float)p.Y);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        var p = P(e.GetPosition(this));

        if (props.IsRightButtonPressed)
        {
            if (_tb != null) { CancelText(); return; }
            if (_hasSel || _dragging) { ResetSelection(); return; }
            _onClose(); return;
        }
        if (!props.IsLeftButtonPressed) return;
        if (_tb != null) CommitText();

        if (_hasSel)
        {
            int id = HitButton(p);
            if (id != 0) { HandleButton(id); return; }
            if (HitStyle(p)) return;
            if (_tool == Tool.None)
            {
                int hd = HitHandle(p);
                if (hd >= 0) { _resizing = true; _handle = hd; _selOrig = _sel; return; }
                if (_sel.Contains(p)) { _moving = true; _dragOrigin = p; _selOrig = _sel; return; }
            }
            if (_tool == Tool.Text && _sel.Contains(p)) { BeginText(p); return; }
            if (_tool != Tool.None && _tool != Tool.Text && _sel.Contains(p))
            {
                _annotating = true;
                _drawing = new Anno { Type = _tool, A = p, B = p, Color = _color, Width = _width };
                return;
            }
        }
        _dragging = true; _hasSel = false; _tool = Tool.None; _annos.Clear(); _hover = 0;
        Cursor = CurHidden;
        _start = p; _sel = new SKRect(p.X, p.Y, p.X, p.Y);
        Repaint();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _mouseIn = false;
        if (!_hasSel && !_dragging) Repaint();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = P(e.GetPosition(this));
        _mouse = p; _mouseIn = true;
        if (_dragging) { _sel = Norm(_start, p); Repaint(); return; }
        if (_moving)
        {
            float dx = p.X - _dragOrigin.X, dy = p.Y - _dragOrigin.Y;
            float nx = Clamp(_selOrig.Left + dx, 0, (float)DipW - _selOrig.Width);
            float ny = Clamp(_selOrig.Top + dy, 0, (float)DipH - _selOrig.Height);
            _sel = new SKRect(nx, ny, nx + _selOrig.Width, ny + _selOrig.Height);
            Layout(); Repaint(); return;
        }
        if (_resizing) { _sel = ResizeRect(_selOrig, _handle, p); Layout(); Repaint(); return; }
        if (_annotating && _drawing != null) { _drawing.B = p; Repaint(); return; }
        if (_hasSel)
        {
            int id = HitButton(p);
            if (id != 0) Cursor = new Cursor(StandardCursorType.Hand);
            else if (_showStyle && _styleBar.Contains(p)) Cursor = new Cursor(StandardCursorType.Hand);
            else if (_tool == Tool.None)
            {
                int hd = HitHandle(p);
                Cursor = new Cursor(hd >= 0 ? HandleCursor(hd) : _sel.Contains(p) ? StandardCursorType.SizeAll : StandardCursorType.Cross);
            }
            else if (_tool == Tool.Text && _sel.Contains(p)) Cursor = new Cursor(StandardCursorType.Ibeam);
            else Cursor = new Cursor(StandardCursorType.Cross);
            if (id != _hover) { _hover = id; Repaint(); }
        }
        else Repaint();   // 初始框选阶段：重绘让金色准星跟随
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            if (_sel.Width >= 3 && _sel.Height >= 3) { _hasSel = true; Layout(); }
            else { _sel = default; _hasSel = false; }
            Repaint(); return;
        }
        if (_moving || _resizing) { _moving = _resizing = false; _handle = -1; Layout(); Repaint(); return; }
        if (_annotating && _drawing != null)
        {
            _annotating = false;
            float dx = _drawing.B.X - _drawing.A.X, dy = _drawing.B.Y - _drawing.A.Y;
            if (dx * dx + dy * dy >= 16) _annos.Add(_drawing);
            _drawing = null; Repaint();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_tb != null)
        {
            if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
            else if (e.Key is Key.Enter or Key.Return) { CommitText(); e.Handled = true; }
            return;
        }
        if (e.Key == Key.Escape) { if (_hasSel) ResetSelection(); else _onClose(); e.Handled = true; }
        else if (e.Key is Key.Enter or Key.Return) { Confirm(); e.Handled = true; }
        else if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _annos.Count > 0)
        { _annos.RemoveAt(_annos.Count - 1); Repaint(); e.Handled = true; }
    }

    void ResetSelection()
    {
        _dragging = _moving = _resizing = _annotating = false;
        _drawing = null; _handle = -1; _hasSel = false; _sel = default;
        _tool = Tool.None; _annos.Clear(); _hover = 0;
        Cursor = new Cursor(StandardCursorType.Cross);
        Repaint();
    }

    void HandleButton(int id)
    {
        switch (id)
        {
            case 1: _tool = _tool == Tool.Arrow ? Tool.None : Tool.Arrow; break;
            case 2: _tool = _tool == Tool.Line ? Tool.None : Tool.Line; break;
            case 3: _tool = _tool == Tool.Rect ? Tool.None : Tool.Rect; break;
            case 7: _tool = _tool == Tool.Text ? Tool.None : Tool.Text; break;
            case 8: _tool = _tool == Tool.Mosaic ? Tool.None : Tool.Mosaic; break;
            case 10: StartLongShot(); return;
            case 4: if (_annos.Count > 0) _annos.RemoveAt(_annos.Count - 1); break;
            case 9: _ = SavePng(); return;
            case 5: _onClose(); return;
            case 6: Confirm(); return;
        }
        Layout(); Repaint();
    }

    void StartLongShot()
    {
        if (!_hasSel || OnLongShot == null) return;
        if (_tb != null) CommitText();
        // 从冻结原图裁出首帧（物理像素）
        int l = Math.Max(0, (int)(_sel.Left * _scale)), t = Math.Max(0, (int)(_sel.Top * _scale));
        int r = Math.Min(_src.Width, (int)(_sel.Right * _scale)), b = Math.Min(_src.Height, (int)(_sel.Bottom * _scale));
        if (r <= l || b <= t) return;
        var f0 = new SKBitmap(r - l, b - t, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (!_src.ExtractSubset(f0, new SKRectI(l, t, r, b))) return;
        OnLongShot(f0, _sel);
    }

    // ---------- 文字 ----------
    static IBrush ToBrush(SKColor c) => new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));

    void BeginText(SKPoint p)
    {
        CommitText();
        _tb = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = ToBrush(_color),
            FontSize = 18,
            Padding = new Thickness(0),
            MinWidth = 20,
            Tag = p,
        };
        Canvas.SetLeft(_tb, p.X);
        Canvas.SetTop(_tb, p.Y);
        TextLayer?.Children.Add(_tb);
        _tb.Focus();
    }

    void CommitText()
    {
        if (_tb == null) return;
        var tb = _tb; _tb = null;
        var p = (SKPoint)tb.Tag!;
        string txt = tb.Text ?? "";
        TextLayer?.Children.Remove(tb);
        if (txt.Trim().Length > 0)
            _annos.Add(new Anno { Type = Tool.Text, A = p, Text = txt, Color = _color });
        Focus();
        Repaint();
    }

    void CancelText()
    {
        if (_tb == null) return;
        TextLayer?.Children.Remove(_tb);
        _tb = null;
        Focus();
        Repaint();
    }

    // ---------- 输出 ----------
    SKImage RenderResult()
    {
        if (_tb != null) CommitText();
        int pw = Math.Max(1, (int)(_sel.Width * _scale)), ph = Math.Max(1, (int)(_sel.Height * _scale));
        var bmp = new SKBitmap(pw, ph);
        using (var c = new SKCanvas(bmp))
        {
            c.Scale((float)_scale);
            c.Translate(-_sel.Left, -_sel.Top);
            c.ClipRect(_sel);
            c.DrawBitmap(_src, new SKRect(0, 0, (float)DipW, (float)DipH));
            DrawAnnos(c);
        }
        return SKImage.FromBitmap(bmp);
    }

    void Confirm()
    {
        if (!_hasSel) return;
        _onCopy(RenderResult());
        _onClose();
    }

    async Task SavePng()
    {
        if (!_hasSel) return;
        using var img = RenderResult();
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "AltSnip.png",
            DefaultExtension = "png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } } },
        });
        if (file != null)
        {
            await using var s = await file.OpenWriteAsync();
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            data.SaveTo(s);
        }
        _onClose();
    }
}
