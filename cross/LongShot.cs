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
    BorderWindow? _border;
    PreviewWindow? _preview;
    ControlWindow? _control;
    bool _running;
    bool _busyPreview;
    DateTime _lastPreview = DateTime.MinValue;

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

        // ✓/✕ 控件条：选区右下角外侧。物理区域先算好——蒙层要在这块挖洞，避免按钮被压暗。
        var ctlPos = new PixelPoint(pos.X + _region.Width - (int)(84 * s), pos.Y + _region.Height + 8);
        var ctlPhys = new PixelRect(ctlPos.X, ctlPos.Y, (int)(88 * s), (int)(34 * s));

        // 选区外暗色蒙层（点击穿透；选区处 + 按钮区都挖空，按钮不被压暗）
        _dim = new DimWindow(_screen, _region, ctlPhys, s);
        _dim.Show();
        _border = new BorderWindow(new PixelPoint(pos.X - 3, pos.Y - 3), wDip + 6, hDip + 6);
        _border.Show();

        // 此刻：上一层全屏取景窗(实心黑、非穿透)已关闭；我方 dim/border 是 WS_EX_TRANSPARENT
        // (WindowFromPoint 会跳过穿透窗) → 取选区中心下方拿到的正是要截的真 App，而非残留浮层。
        IntPtr target = WinScroll.WindowUnder(cxp, cyp);

        _preview = new PreviewWindow(new PixelPoint(pos.X + _region.Width + 12, pos.Y),
                                     Math.Min(150, Math.Max(96, wDip * 0.4)), hDip);
        _preview.Show();
        _control = new ControlWindow(ctlPos, Finish, Cancel);
        _control.OnCancelKey = Cancel;
        _control.Show();

        // 浮层都摆好后，把目标窗口(顶层)顶到前台取得焦点 → 用户原生滚轮直接滚它
        WinScroll.FocusTarget(target);
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
            Bitmap? bmp = null;
            try { using var full = _acc.Compose(); bmp = LongShot.ToAvalonia(full, 300, 4000); } catch { }
            Dispatcher.UIThread.Post(() =>
            {
                if (_running && bmp != null) _preview?.Update(bmp);
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
        try { result = _acc.Compose(); } catch { CloseChrome(); return; }
        try { _plat.CopyImageToClipboard(SKImage.FromBitmap(result)); } catch { }
        CloseChrome();
        try { new ResultWindow(result).Show(); } catch { }
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
        try { _border?.Close(); } catch { }
        try { _preview?.Close(); } catch { }
        try { _control?.Close(); } catch { }
        _dim = null; _border = null; _preview = null; _control = null;
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
    public DimWindow(PixelRect screen, PixelRect region, PixelRect control, double scale)
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
        var btn = Hole(control).Inflate(2);   // 按钮洞比控件条略大 2 DIP，四周不贴暗边

        double m = Bleed;
        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(new RectangleGeometry(new Rect(-m, -m, fw + 2 * m, fh + 2 * m))); // 外框(bleed)
        geo.Children.Add(new RectangleGeometry(sel));                                       // 选区洞
        geo.Children.Add(new RectangleGeometry(btn));                                       // 按钮洞
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

// ---- 选区外框：透明、点击穿透（滚轮穿过去滚真 App），只画一圈竹青边 ----
internal sealed class BorderWindow : Window
{
    readonly PixelPoint _origin;
    public BorderWindow(PixelPoint pos, double wDip, double hDip)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        _origin = pos; Position = pos; Width = wDip; Height = hDip;
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0x8A, 0x73)),
            BorderThickness = new Thickness(2),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(3),
        };
    }
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Position = _origin;
        WinOverlay.Apply(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough: true);
    }
}

// ---- 实时预览：右侧单框，只显示正在拼的长图（底部对齐，越长越往上滚）----
internal sealed class PreviewWindow : Window
{
    readonly Image _img;
    readonly PixelPoint _origin;
    public PreviewWindow(PixelPoint pos, double wDip, double hDip)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x14));
        WindowStartupLocation = WindowStartupLocation.Manual;
        _origin = pos; Position = pos; Width = wDip; Height = hDip;
        _img = new Image { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Bottom };
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2C, 0x37, 0x42)),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(4),
            Child = _img,
        };
    }
    public void Update(Bitmap bmp)
    {
        var old = _img.Source as Bitmap;
        _img.Source = bmp;
        old?.Dispose();
    }
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Position = _origin;
        WinOverlay.Apply(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough: false);
    }
}

// ---- 选区外右下角：两个小图标 ✓完成(复制) / ✕取消 ----
internal sealed class ControlWindow : Window
{
    public Action? OnCancelKey;
    readonly PixelPoint _origin;
    public ControlWindow(PixelPoint pos, Action onOk, Action onCancel)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        WindowStartupLocation = WindowStartupLocation.Manual;
        _origin = pos; Position = pos; Width = 88; Height = 34;

        var no = IconButton("✕", Color.FromRgb(0xF4, 0xF6, 0xF8), Color.FromRgb(0x3A, 0x46, 0x53));
        no.Click += (_, _) => onCancel();
        var ok = IconButton("✓", Color.FromRgb(0x0E, 0x11, 0x14), Color.FromRgb(0x5C, 0x8A, 0x73));
        ok.Click += (_, _) => onOk();

        // 控件条：不透明深色底 + 固定尺寸，避免 DPI/字体度量差异导致布局抖动
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x1E)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(4, 3),
            Width = 88,
            Height = 34,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { no, ok },
            },
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) OnCancelKey?.Invoke(); };
    }

    static Button IconButton(string glyph, Color fg, Color bg) => new()
    {
        Content = glyph,
        Width = 36, Height = 26,
        Foreground = new SolidColorBrush(fg),
        Background = new SolidColorBrush(bg),
        FontSize = 13,
        FontWeight = FontWeight.Bold,
        CornerRadius = new CornerRadius(5),
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
    };

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Position = _origin;
        WinOverlay.Apply(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero, clickThrough: false);
    }
}

// ---- 纯结果框：无标题栏/无尺寸/无按钮，只有长图；Esc 关闭 ----
internal sealed class ResultWindow : Window
{
    public ResultWindow(SKBitmap img)
    {
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = true; Topmost = false;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x11, 0x14));
        Width = Math.Min(780, img.Width + 24);
        Height = 760;
        var image = new Image { Source = LongShot.ToAvalonia(img), Stretch = Stretch.None };
        Content = new ScrollViewer
        {
            Content = image,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Padding = new Thickness(8),
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
