using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SkiaSharp;

namespace AltSnip;

/// <summary>无边框置顶取景窗：一个盖住目标屏的普通置顶窗（不进 macOS 原生全屏，
/// 避免滑入独立 Space 把其它窗口推走）。底下窗口保持不动，在冻结的截图上画框，无缝。</summary>
public sealed class OverlayWindow : Window
{
    readonly OverlayControl _control;
    readonly PixelRect _bounds;
    readonly double _scaling;

    public OverlayWindow(SKBitmap shot, PixelRect bounds, double scaling)
    {
        _bounds = bounds;
        _scaling = scaling <= 0 ? 1 : scaling;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = OverlayControl.CurHidden;   // 全局隐藏系统鼠标（改用自绘十字）
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = bounds.Position;   // 先落到目标屏
        // 用逻辑尺寸铺满该屏（Bounds 是物理像素，Width/Height 是 DIP）
        if (scaling <= 0) scaling = 1;
        Width = bounds.Width / scaling;
        Height = bounds.Height / scaling;

        var textLayer = new Canvas { Cursor = OverlayControl.CurHidden };
        _control = new OverlayControl(shot, Close, Copy) { TextLayer = textLayer };
        var grid = new Grid();
        grid.Children.Add(_control);
        grid.Children.Add(textLayer);
        Content = grid;
    }

    void Copy(SKImage image)
    {
        try { Platform.PlatformServices.Current.CopyImageToClipboard(image); } catch { }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Position = _bounds.Position;   // 再次校正到目标屏原点（不进原生全屏）
        // macOS：抬到遮罩层级并铺满整屏（含菜单栏/Dock），留在当前 Space
        if (OperatingSystem.IsMacOS())
        {
            var h = TryGetPlatformHandle();
            if (h != null)
                Platform.MacWindow.CoverScreen(h.Handle,
                    _bounds.X / _scaling, _bounds.Y / _scaling,
                    _bounds.Width / _scaling, _bounds.Height / _scaling);
        }
        Activate();
        _control.Focus();
    }
}
