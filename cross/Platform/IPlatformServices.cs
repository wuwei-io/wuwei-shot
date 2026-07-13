using System;
using Avalonia;
using SkiaSharp;

namespace AltSnip.Platform;

/// <summary>
/// 每个操作系统各自实现的原生能力。UI 与标注逻辑是可移植的共享层，
/// 只通过这个接口访问系统：截屏、图片剪贴板、全局热键。
/// </summary>
public interface IPlatformServices
{
    string Name { get; }

    /// <summary>捕获指定屏幕区域（屏幕像素坐标）为位图。</summary>
    SKBitmap CaptureRegion(PixelRect region);

    /// <summary>把图片写入系统剪贴板。</summary>
    void CopyImageToClipboard(SKImage image);

    /// <summary>注册全局热键 Alt+A；不支持的平台返回 null。返回值 Dispose 时注销。</summary>
    IDisposable? RegisterHotkey(Action onAltA);

    /// <summary>当前鼠标位置（屏幕物理像素）；拿不到则返回 null。</summary>
    PixelPoint? CursorPosition();

    /// <summary>在指定屏幕点向下滚动 notches 格（长截图用）。</summary>
    void ScrollDown(PixelPoint at, int notches);
}
