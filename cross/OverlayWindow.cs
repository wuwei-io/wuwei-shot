using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SkiaSharp;

namespace AltSnip;

/// <summary>无边框全屏取景窗：先落到目标屏，再 FullScreen 铺满该屏（可靠置顶+铺满）。</summary>
public sealed class OverlayWindow : Window
{
    readonly OverlayControl _control;
    readonly PixelRect _bounds;

    public OverlayWindow(SKBitmap shot, PixelRect bounds)
    {
        _bounds = bounds;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = bounds.Position;   // 先落到目标屏

        var textLayer = new Canvas();
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
        Log.W($"opened pos={Position} bounds={Bounds} client={ClientSize} state={WindowState}");
        Position = _bounds.Position;
        WindowState = WindowState.FullScreen;   // 铺满该屏
        Activate();
        _control.Focus();
        Log.W($"after fullscreen bounds={Bounds} client={ClientSize} state={WindowState}");
    }
}
