using System;
using System.IO;
using System.Threading.Tasks;
using WuweiShot.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace WuweiShot;

/// <summary>长截图（微信式手动滚）：遮罩关闭后，用户自己向下滚真 App，
/// 后台定时抓选区 + 增量拼接，右侧单框实时预览，选区外右下角 ✓完成(复制)/✕取消。
/// 完成弹一个"无标题栏/无按钮"的纯长图预览窗。</summary>
public static class LongShot
{
    public static void Run(PixelRect region, SKBitmap frame0, double scaling, PixelRect screenBounds)
        => new LongSession(region, frame0, scaling <= 0 ? 1 : scaling, screenBounds).Start();

    // ---- SKBitmap → Avalonia Bitmap（可选降采样，预览用小图省开销）----
    internal static Bitmap ToAvalonia(SKBitmap sk, int maxW = 0, int maxH = 0)
    {
        SKBitmap use = sk; bool tmp = false;
        if (maxW > 0 && maxH > 0 && (sk.Width > maxW || sk.Height > maxH))
        {
            double k = Math.Min((double)maxW / sk.Width, (double)maxH / sk.Height);
            int nw = Math.Max(1, (int)(sk.Width * k)), nh = Math.Max(1, (int)(sk.Height * k));
            use = sk.Resize(new SKImageInfo(nw, nh), SKFilterQuality.Medium) ?? sk;
            tmp = use != sk;
        }
        using var img = SKImage.FromBitmap(use);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        using var ms = new MemoryStream();
        data.SaveTo(ms); ms.Position = 0;
        var bmp = new Bitmap(ms);
        if (tmp) use.Dispose();
        return bmp;
    }
}

internal sealed class LongSession
{
    readonly PixelRect _region;
    readonly PixelRect _screen;
    readonly double _scale;
    readonly Stitcher.Accumulator _acc;
    readonly IPlatformServices _plat = PlatformServices.Current;

    DimWindow? _dim;
    PreviewWindow? _preview;
    HintWindow? _hint;
    ControlWindow? _control;
    bool _running;
    bool _busyPreview;
    DateTime _lastPreview = DateTime.MinValue;
    double _prevW;      // 预览缩略图宽（DIP，固定）
    double _prevMaxH;   // 预览可用最大高（DIP，到屏底）

    public LongSession(PixelRect region, SKBitmap frame0, double scale, PixelRect screen)
    {
        _region = region; _scale = scale; _screen = screen;
        _acc = new Stitcher.Accumulator(frame0);
    }

    public void Start()
    {
        // 选区(物理像素) → DIP 位置/尺寸
        double s = _scale;
        var pos = _region.Position;
        double wDip = _region.Width / s, hDip = _region.Height / s;
        int cxp = _region.X + _region.Width / 2, cyp = _region.Y + _region.Height / 2;

        // 控件条：复用短截图 Skia 工具栏——右对齐选区右边缘、置于选区正下方（3 格 = 42×3 宽，+6 给投影）
        const int CtlW = 126, CtlH = 48;
        int ctlX = pos.X + _region.Width - (int)(CtlW * s);            // 右对齐选区右边
        ctlX = Math.Max(_screen.X + 4, ctlX);
        int ctlY = pos.Y + _region.Height + 8;
        if (ctlY + (int)(CtlH * s) > _screen.Y + _screen.Height)
            ctlY = pos.Y + _region.Height - (int)(CtlH * s) - 8;       // 贴底则收进选区内右下
        var ctlPos = new PixelPoint(ctlX, ctlY);

        // 右侧实时预览：缩略图宽固定，高度随内容增长。物理区域先算好——蒙层在这挖洞不压暗。
        double prevW = Math.Min(150, Math.Max(96, wDip * 0.4));
        double prevInitH = prevW * hDip / wDip;                             // 起始一帧缩略高（无留白）
        double prevMaxH = (_screen.Y + _screen.Height - pos.Y) / s - 8;     // 到屏底的可用最大高
        _prevW = prevW; _prevMaxH = prevMaxH;
        var prevPos = new PixelPoint(pos.X + _region.Width + 12, pos.Y);

        // "滚动截取更多内容"提示文字：左对齐选区左边、悬于选区正上方（无背景框，纯文字浮在暗蒙层上）
        const int HintW = 240, HintH = 28;
        int hintX = pos.X;                                            // 左对齐选区左边
        int hintY = Math.Max(_screen.Y + 4, pos.Y - (int)(HintH * s) - 8);
        var hintPos = new PixelPoint(hintX, hintY);

        // 选区外暗色蒙层（点击穿透；只挖选区洞。预览/提示/按钮都自己置顶浮在蒙层上，不再挖洞）
        _dim = new DimWindow(_screen, _region, s);
        _dim.Show();
        // 选区不画边框，越简洁越好

        // 此刻：上一层全屏取景窗(实心黑、非穿透)已关闭；我方 dim/border 是 WS_EX_TRANSPARENT
        // (WindowFromPoint 会跳过穿透窗) → 取选区中心下方拿到的正是要截的真 App，而非残留浮层。
        IntPtr target = WinScroll.WindowUnder(cxp, cyp);

        _preview = new PreviewWindow(prevPos, prevW, prevInitH, s);
        _preview.Show();
        _hint = new HintWindow(hintPos, HintW, HintH);
        _hint.Show();
        _control = new ControlWindow(ctlPos, s, Finish, Save, Cancel);
        _control.OnCancelKey = Cancel;
        _control.Show();

        // 浮层都摆好后，把目标窗口(顶层)顶到前台取得焦点 → 用户原生滚轮直接滚它
        WinScroll.FocusTarget(target);
        // 提示/按钮无洞、浮在蒙层上：需确保其 z 序在蒙层之上（否则被 50% 黑压暗）。
        // 蒙层 OnOpened 有一次延迟置顶，这里补一次更晚的置顶把浮层顶回最上。
        Dispatcher.UIThread.Post(() => { _hint?.Bump(); _control?.Bump(); _preview?.Bump(); },
            DispatcherPriority.Background);
        _running = true;
        UpdatePreview();   // 立即显示首帧(选区当前内容)，"未滚动=全黑"不再误判为坏
        _ = Loop();
    }

    async Task Loop()
    {
        await Task.Delay(320);   // 等遮罩消失 + 目标重绘
        while (_running)
        {
            bool changed = await Task.Run(() =>
            {
                SKBitmap f;
                try { f = _plat.CaptureRegion(_region); } catch { return false; }
                bool c;
                try { c = _acc.Feed(f); } catch { c = false; }
                f.Dispose();
                return c;
            });
            if (!_running) break;
            if (changed) UpdatePreview();
            if (_acc.Height > 30000) { Finish(); return; }   // 安全上限
            await Task.Delay(140);
        }
    }

    void UpdatePreview()
    {
        if (_busyPreview || (DateTime.UtcNow - _lastPreview).TotalMilliseconds < 260) return;
        _busyPreview = true; _lastPreview = DateTime.UtcNow;
        _ = Task.Run(() =>
        {
            Bitmap? bmp = null; double winH = 0;
            try
            {
                using var full = _acc.Compose();
                double fullHdip = _prevW * full.Height / full.Width;   // 缩略全高
                if (fullHdip <= _prevMaxH)
                {
                    // 未封顶：窗口 = 内容真实缩略高，整图铺满
                    winH = fullHdip;
                    bmp = LongShot.ToAvalonia(full, 300, 4000);
                }
                else
                {
                    // 已封顶：只取底部尾巴（最新内容），窗口 = 屏底可用高
                    winH = _prevMaxH;
                    int tailH = Math.Min(full.Height, Math.Max(1, (int)(_prevMaxH / _prevW * full.Width)));
                    using var tail = new SKBitmap(full.Width, tailH);
                    using (var cv = new SKCanvas(tail))
                        cv.DrawBitmap(full,
                            new SKRect(0, full.Height - tailH, full.Width, full.Height),
                            new SKRect(0, 0, full.Width, tailH));
                    bmp = LongShot.ToAvalonia(tail, 300, 4000);
                }
            }
            catch { }
            Dispatcher.UIThread.Post(() =>
            {
                if (_running && bmp != null) _preview?.Update(bmp, winH);
                else bmp?.Dispose();
                _busyPreview = false;
            });
        });
    }

    void Finish()
    {
        if (!_running) return;
        _running = false;
        SKBitmap result;
        try { result = _acc.Compose(); } catch { CloseChrome(); _acc.Dispose(); return; }
        // 直接复制到剪贴板，不再弹预览窗
        try { using var img = SKImage.FromBitmap(result); _plat.CopyImageToClipboard(img); } catch { }
        CloseChrome();
        result.Dispose();
        _acc.Dispose();
    }

    // 保存：停止拼接 → 直接弹系统"另存为"选位置写 PNG（不弹预览窗）
    void Save()
    {
        if (!_running) return;
        _running = false;
        SKBitmap result;
        try { result = _acc.Compose(); } catch { CloseChrome(); _acc.Dispose(); return; }
        CloseChrome();
        _ = SaveToFileAsync(result);
    }

    async Task SaveToFileAsync(SKBitmap img)
    {
        // 用一个 1×1 透明宿主窗口承载系统"另存为"对话框（对话框需要窗口宿主），全程不显示预览。
        var host = new Window
        {
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            Width = 1, Height = 1,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(_screen.X + _screen.Width / 2, _screen.Y + _screen.Height / 2),
            Background = Brushes.Transparent,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
        };
        try
        {
            host.Show();
            var file = await host.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "保存长截图",
                SuggestedFileName = "长截图.png",
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("PNG 图片") { Patterns = new[] { "*.png" } },
                },
            });
            if (file != null)
            {
                await using var stream = await file.OpenWriteAsync();
                using var im = SKImage.FromBitmap(img);
                using var d = im.Encode(SKEncodedImageFormat.Png, 95);
                d.SaveTo(stream);
            }
        }
        catch { }
        finally
        {
            try { host.Close(); } catch { }
            img.Dispose();
            _acc.Dispose();
        }
    }

    void Cancel()
    {
        _running = false;
        CloseChrome();
        _acc.Dispose();
    }

    void CloseChrome()
    {
        try { _dim?.Close(); } catch { }
        try { _preview?.Close(); } catch { }
        try { _hint?.Close(); } catch { }
        try { _control?.Close(); } catch { }
        _dim = null; _preview = null; _hint = null; _control = null;
    }
}

// ---- 选区外暗色蒙层：铺满目标屏、选区处挖空；透明+点击穿透（滚轮穿过去滚真 App）----
internal sealed class DimWindow : Window
{
    readonly PixelPoint _origin;   // 目标屏物理原点，OnOpened 时再校正（PerMonitorV2 下 ctor 落点不可靠）
    readonly PixelRect _screenPx;  // 目标屏物理矩形：OnOpened 用它强制原生窗口矩形，DPI-proof
    const double Bleed = 8;        // 外沿溢出(DIP)：整体越过窗口边一点，任何取整误差都不会在屏边留缝

    // 用"外框铺满 + EvenOdd 挖洞"画蒙层：外框(含 bleed)算 1 层，洞区再被内矩形覆盖成 2 层(偶数)=不填充=透明。
    // 天然无拼接缝(治 #1 顶/左盖不满)，且能同时挖多个洞(选区洞 + 按钮洞，治 #2 按钮被压暗)。
    public DimWindow(PixelRect screen, PixelRect region, double scale)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        _origin = screen.Position;
        _screenPx = screen;
        Position = _origin;
        double fw = screen.Width / scale, fh = screen.Height / scale;
        Width = fw; Height = fh;

        // 物理坐标 → 本窗 DIP（洞的四边保持精确）
        Rect Hole(PixelRect r) => new Rect(
            (r.X - screen.X) / scale, (r.Y - screen.Y) / scale, r.Width / scale, r.Height / scale);
        var sel = Hole(region);

        double m = Bleed;
        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(new RectangleGeometry(new Rect(-m, -m, fw + 2 * m, fh + 2 * m))); // 外框(bleed)
        geo.Children.Add(new RectangleGeometry(sel));                                       // 选区洞（预览窗自己置顶，不再挖预览洞）
        var path = new Avalonia.Controls.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),   // 半透明黑 ~50%
            Data = geo,
        };
        var cv = new Canvas();
        Canvas.SetLeft(path, 0); Canvas.SetTop(path, 0);
        cv.Children.Add(path);
        Content = cv;
    }
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // 用物理像素强制窗口 = 目标屏矩形，绕开 Avalonia 的 DPI 定位误差 →
        // 蒙层严丝合缝盖满整屏，不再顶部/左侧漏一条。
        var h = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        WinOverlay.ApplyRect(h, clickThrough: true,
            _screenPx.X, _screenPx.Y, _screenPx.Width, _screenPx.Height);
        // 蒙层铺满后，把洞区精确裁回目标尺寸，避免 bleed 外沿被强设物理矩形后变成黑边
        Dispatcher.UIThread.Post(() =>
        {
            if (h != IntPtr.Zero)
            {
                WinOverlay.ApplyRect(h, clickThrough: true,
                    _screenPx.X, _screenPx.Y, _screenPx.Width, _screenPx.Height);
            }
        }, DispatcherPriority.Background);
    }
}

// ---- 实时预览：右侧单框，只显示正在拼的长图（底部对齐，越长越往上滚）----
internal sealed class PreviewWindow : Window
{
    readonly Image _img;
    readonly PixelPoint _origin;
    readonly double _wDip;
    readonly double _s;
    public PreviewWindow(PixelPoint pos, double wDip, double initHDip, double scale)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = true; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        MinHeight = 1; MinWidth = 1;
        // 全透明：去掉深色底/深色边框，预览只留截图本身，与选区一样亮
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        _origin = pos; Position = pos; _wDip = wDip; _s = scale <= 0 ? 1 : scale;
        // 起始只有一帧缩略高；之后窗口高随内容变。图片 Fill 铺满窗口（Skia 侧已裁好尾巴）。
        Width = wDip; Height = initHDip;
        _img = new Image { Stretch = Stretch.Fill };
        Content = _img;
    }
    // bmp = 已裁好的"该显示内容"，winHDip = 窗口应有高度（= 内容真实缩略高，封顶时=屏底可用高）
    public void Update(Bitmap bmp, double winHDip)
    {
        double h = Math.Max(1, winHDip);      // 窗口高 = 内容缩略高（Skia 已裁好尾巴）
        Height = h;                           // Avalonia 逻辑高
        var old = _img.Source as Bitmap;
        _img.Source = bmp;
        old?.Dispose();
        // 关键：Avalonia 对透明置顶浮层的 resize 不一定传到原生窗口，这里用 Win32 强制原生矩形 = 期望大小 + 置顶。
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
            WinOverlay.ApplyRect(hwnd, clickThrough: false,
                _origin.X, _origin.Y, (int)Math.Round(_wDip * _s), (int)Math.Round(h * _s));
    }
    public void Bump() => WinOverlay.Apply(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough: false);
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Position = _origin;
        WinOverlay.Apply(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough: false);
    }
}

// ---- 复用短截图（OverlayControl）的 SkiaSharp 工具栏画法：像素级一致 ----
// 配色即无为 VI：玄墨底 #16191e、月白 #f4f6f8、银灰 #9aa7b2、淡青 #6f9fad（激活/确认）。
internal static class ShotToolbar
{
    public enum Ico { Save, Cross, Check, Copy }

    static readonly SKColor Text  = new(0xf4, 0xf6, 0xf8);
    static readonly SKColor Text2 = new(0x9a, 0xa7, 0xb2);
    static readonly SKColor Gold  = new(0x6f, 0x9f, 0xad);   // 淡青（激活/确认）

    static SKPaint Stroke(SKColor col, float w) => new()
    { Color = col, IsAntialias = true, IsStroke = true, StrokeWidth = w, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };

    static void Chip(SKCanvas c, SKRect cell, SKColor col, byte a)
    {
        var r = new SKRect(cell.Left + 4, cell.Top + 4, cell.Right - 4, cell.Bottom - 4);
        using var b = new SKPaint { Color = col.WithAlpha(a), IsAntialias = true };
        c.DrawRoundRect(r, 8, 8, b);
    }

    // 迷你工具条：深色圆角底 + 柔和投影 + 若干格图标（画法搬自短截图 DrawToolbar）
    public static void DrawBar(SKCanvas c, float btn, Ico[] icons, int hover)
    {
        var bar = new SKRect(0, 0, btn * icons.Length, btn);
        using (var sh = new SKPaint { Color = new SKColor(0, 0, 0, 70), IsAntialias = true })
            c.DrawRoundRect(new SKRect(bar.Left, bar.Top + 3, bar.Right, bar.Bottom + 3), 10, 10, sh);
        using (var bg = new SKPaint { Color = new SKColor(0x16, 0x19, 0x1e, 205), IsAntialias = true })
            c.DrawRoundRect(bar, 10, 10, bg);

        for (int i = 0; i < icons.Length; i++)
        {
            var cell = new SKRect(i * btn, 0, i * btn + btn, btn);
            bool hov = hover == i;
            if (hov) Chip(c, cell, Text, 26);
            var ico = icons[i];
            bool accent = ico == Ico.Check || ico == Ico.Copy;   // 主操作用淡青，与短截图确认键一致
            var col = accent ? Gold : (hov ? Text : Text2);
            switch (ico)
            {
                case Ico.Save: IconSave(c, cell, col); break;
                case Ico.Cross: IconCross(c, cell, col); break;
                case Ico.Check: IconCheck(c, cell, col); break;
                case Ico.Copy: IconCopy(c, cell, col); break;
            }
        }
    }

    // 以下图标画法逐字搬自 OverlayControl（BTN=42 格），保证与短截图一模一样
    static void IconSave(SKCanvas c, SKRect r, SKColor col)
    {
        using var p = Stroke(col, 2.2f);
        float cx = r.MidX, ty = r.Top + 11, by = r.Bottom - 16;
        c.DrawLine(new SKPoint(cx, ty), new SKPoint(cx, by), p);
        c.DrawLine(new SKPoint(cx, by), new SKPoint(cx - 5, by - 5), p);
        c.DrawLine(new SKPoint(cx, by), new SKPoint(cx + 5, by - 5), p);
        c.DrawLine(new SKPoint(r.Left + 11, r.Bottom - 11), new SKPoint(r.Right - 11, r.Bottom - 11), p);
    }
    static void IconCross(SKCanvas c, SKRect r, SKColor col)
    {
        using var p = Stroke(col, 2.3f);
        c.DrawLine(new SKPoint(r.Left + 15, r.Top + 15), new SKPoint(r.Right - 15, r.Bottom - 15), p);
        c.DrawLine(new SKPoint(r.Right - 15, r.Top + 15), new SKPoint(r.Left + 15, r.Bottom - 15), p);
    }
    static void IconCheck(SKCanvas c, SKRect r, SKColor col)
    {
        using var p = Stroke(col, 2.6f);
        using var path = new SKPath();
        path.MoveTo(r.Left + 13, r.Top + 22);
        path.LineTo(r.Left + 18, r.Top + 27);
        path.LineTo(r.Left + 29, r.Top + 15);
        c.DrawPath(path, p);
    }
    static void IconCopy(SKCanvas c, SKRect r, SKColor col)
    {
        using var p = Stroke(col, 2.0f);
        c.DrawRoundRect(new SKRect(r.Left + 12, r.Top + 15, r.Right - 15, r.Bottom - 11), 2.5f, 2.5f, p);
        c.DrawRoundRect(new SKRect(r.Left + 15, r.Top + 11, r.Right - 12, r.Bottom - 15), 2.5f, 2.5f, p);
    }
}

// ---- Avalonia 宿主：把 Skia 工具条渲染成图片 + 手动命中格子（点击 / hover）----
internal sealed class SkiaToolbar : Image
{
    readonly ShotToolbar.Ico[] _icons;
    readonly double _s;
    readonly Action<int> _onClick;
    int _hover = -1;
    const float BTN = 42;

    public double BarWidthDip => BTN * _icons.Length;
    public double BarHeightDip => BTN + 6;   // 底部留 6 给投影

    public SkiaToolbar(ShotToolbar.Ico[] icons, double scale, Action<int> onClick)
    {
        _icons = icons; _s = scale <= 0 ? 1 : scale; _onClick = onClick;
        Stretch = Stretch.Fill;
        Width = BarWidthDip; Height = BarHeightDip;
        Render();
        PointerMoved += (_, e) => SetHover(CellAt(e.GetPosition(this)));
        PointerExited += (_, _) => SetHover(-1);
        PointerPressed += (_, e) => { int i = CellAt(e.GetPosition(this)); if (i >= 0) _onClick(i); };
    }

    int CellAt(Point p)
    {
        if (p.Y > BTN) return -1;
        int i = (int)(p.X / BTN);
        return (i < 0 || i >= _icons.Length) ? -1 : i;
    }
    void SetHover(int h) { if (h != _hover) { _hover = h; Render(); } }

    void Render()
    {
        int w = Math.Max(1, (int)Math.Round(BarWidthDip * _s));
        int h = Math.Max(1, (int)Math.Round(BarHeightDip * _s));
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(SKColors.Transparent);
            c.Scale((float)_s);
            ShotToolbar.DrawBar(c, BTN, _icons, _hover);
        }
        var old = Source as Bitmap;
        Source = LongShot.ToAvalonia(bmp);
        old?.Dispose();
    }
}

// ---- "滚动截取更多内容"提示气泡：悬于选区上方居中，点击穿透 ----
internal sealed class HintWindow : Window
{
    readonly PixelPoint _origin;
    public HintWindow(PixelPoint pos, double wDip, double hDip)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        _origin = pos; Position = pos; Width = wDip; Height = hDip;
        // 无背景框：纯文字左对齐浮在暗蒙层上（加一点半透明黑描边阴影保证在浅色区也读得清）
        Content = new TextBlock
        {
            Text = "滚动页面截取更多内容",
            Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8)),
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
    public void Bump() => WinOverlay.Apply(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough: true);
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Position = _origin;
        WinOverlay.Apply(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough: true);
    }
}

// ---- 选区右下控件条：保存 / 取消 / 完成（复用短截图 Skia 工具栏画法，无外框浮在暗蒙层上）----
internal sealed class ControlWindow : Window
{
    public Action? OnCancelKey;
    readonly PixelPoint _origin;
    public ControlWindow(PixelPoint pos, double scale, Action onOk, Action onSave, Action onCancel)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        _origin = pos; Position = pos;

        var tb = new SkiaToolbar(
            new[] { ShotToolbar.Ico.Save, ShotToolbar.Ico.Cross, ShotToolbar.Ico.Check },
            scale,
            i => { if (i == 0) onSave(); else if (i == 1) onCancel(); else onOk(); });
        Width = tb.BarWidthDip; Height = tb.BarHeightDip;
        Content = tb;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) OnCancelKey?.Invoke(); };
    }

    public void Bump() => WinOverlay.Apply(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough: false);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Position = _origin;
        WinOverlay.Apply(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough: false);
    }
}
