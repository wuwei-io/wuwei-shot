using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SkiaSharp;

namespace AltSnip;

/// <summary>覆盖单个屏幕的无边框全屏取景窗。</summary>
public sealed class OverlayWindow : Window
{
    readonly OverlayControl _control;

    public OverlayWindow(SKBitmap shot, PixelRect bounds, double scaling)
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Position = bounds.Position;
        Width = bounds.Width / scaling;
        Height = bounds.Height / scaling;

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
        Activate();
        _control.Focus();
    }
}
