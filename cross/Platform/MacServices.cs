using System;
using System.IO;
using Avalonia;
using SkiaSharp;

namespace AltSnip.Platform;

/// <summary>macOS 实现：screencapture 截屏 + osascript 写图片剪贴板。全局热键待 M2。</summary>
public sealed class MacServices : IPlatformServices
{
    public string Name => "macos";

    public SKBitmap CaptureRegion(PixelRect region)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "altsnip_capture.png");
        // -x 静音, -R x,y,w,h 区域
        Proc.Run("/usr/sbin/screencapture", "-x", "-t", "png",
                 $"-R{region.X},{region.Y},{region.Width},{region.Height}", tmp);
        var bmp = File.Exists(tmp) ? SKBitmap.Decode(tmp) : null;
        return bmp ?? new SKBitmap(Math.Max(1, region.Width), Math.Max(1, region.Height));
    }

    public void CopyImageToClipboard(SKImage image)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "altsnip_copy.png");
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var fs = File.Create(tmp))
            data.SaveTo(fs);
        Proc.Run("/usr/bin/osascript", "-e",
                 $"set the clipboard to (read (POSIX file \"{tmp}\") as «class PNGf»)");
    }

    public IDisposable? RegisterHotkey(Action onAltA) => null;

    public Avalonia.PixelPoint? CursorPosition() => null; // M2: Carbon RegisterEventHotKey
}
