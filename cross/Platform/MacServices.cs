using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;
using SkiaSharp;

namespace AltSnip.Platform;

/// <summary>macOS 实现：CoreGraphics 进程内截屏 + osascript 写图片剪贴板 + Carbon 全局热键 Alt+A。</summary>
public sealed class MacServices : IPlatformServices
{
    public string Name => "macos";

    const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [StructLayout(LayoutKind.Sequential)]
    struct CGRect { public double X, Y, W, H; }

    [DllImport(CG)] static extern IntPtr CGWindowListCreateImage(CGRect bounds, uint listOption, uint windowID, uint imageOption);
    [DllImport(CG)] static extern nint CGImageGetWidth(IntPtr img);
    [DllImport(CG)] static extern nint CGImageGetHeight(IntPtr img);
    [DllImport(CG)] static extern IntPtr CGColorSpaceCreateDeviceRGB();
    [DllImport(CG)] static extern IntPtr CGBitmapContextCreate(IntPtr data, nint w, nint h, nint bitsPerComp, nint bytesPerRow, IntPtr cs, uint bitmapInfo);
    [DllImport(CG)] static extern void CGContextDrawImage(IntPtr ctx, CGRect rect, IntPtr img);
    [DllImport(CG)] static extern void CGContextRelease(IntPtr ctx);
    [DllImport(CG)] static extern void CGColorSpaceRelease(IntPtr cs);
    [DllImport(CG)] static extern void CGImageRelease(IntPtr img);

    const uint kCGWindowListOptionOnScreenOnly = 1;
    const uint kCGNullWindowID = 0;
    const uint kCGWindowImageDefault = 0;
    // kCGImageAlphaPremultipliedFirst(2) | kCGBitmapByteOrder32Little(2<<12) → 内存序 BGRA，匹配 Skia Bgra8888
    const uint kBitmapInfoBGRA = 2u | (2u << 12);

    /// <summary>进程内截屏（CGWindowListCreateImage）——权限直接归属本 app，授权一次不再反复弹窗；
    /// 也无需启动 screencapture 子进程，更快更无缝。region 为该屏点坐标(非 Retina 即像素)。</summary>
    public SKBitmap CaptureRegion(PixelRect region)
    {
        var bounds = new CGRect { X = region.X, Y = region.Y, W = region.Width, H = region.Height };
        IntPtr img = CGWindowListCreateImage(bounds, kCGWindowListOptionOnScreenOnly, kCGNullWindowID, kCGWindowImageDefault);
        if (img == IntPtr.Zero)
            return new SKBitmap(Math.Max(1, region.Width), Math.Max(1, region.Height)); // 无权限/失败：空图

        int w = (int)CGImageGetWidth(img), h = (int)CGImageGetHeight(img);   // 物理像素（Retina 为 2x）
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        IntPtr cs = CGColorSpaceCreateDeviceRGB();
        IntPtr ctx = CGBitmapContextCreate(bmp.GetPixels(), w, h, 8, bmp.RowBytes, cs, kBitmapInfoBGRA);
        if (ctx != IntPtr.Zero)
        {
            CGContextDrawImage(ctx, new CGRect { X = 0, Y = 0, W = w, H = h }, img);
            CGContextRelease(ctx);
        }
        CGColorSpaceRelease(cs);
        CGImageRelease(img);
        return bmp;
    }

    public void CopyImageToClipboard(SKImage image)
    {
        // 进程内 NSPasteboard 写图，替代 osascript 子进程——消除首次点对号时 osascript 冷启动的卡顿
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        MacClipboard.SetPng(data.ToArray());
    }

    public IDisposable? RegisterHotkey(Action onAltA)
    {
        try { return MacHotKey.Register(onAltA); }
        catch { return null; } // 注册失败不致命：托盘图标仍可触发
    }

    public Avalonia.PixelPoint? CursorPosition() => null;

    public void ScrollDown(Avalonia.PixelPoint at, int notches)
    {
        try
        {
            CGWarpMouseCursorPosition(new CGPoint { x = at.X, y = at.Y });
            var e = CGEventCreateScrollWheelEvent(IntPtr.Zero, 1, 1, -notches); // line units
            if (e != IntPtr.Zero) { CGEventPost(0, e); CFRelease(e); }
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CGPoint { public double x, y; }
    const string AS = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    [DllImport(AS)] static extern IntPtr CGEventCreateScrollWheelEvent(IntPtr src, int units, uint count, int wheel1);
    [DllImport(AS)] static extern void CGEventPost(uint tap, IntPtr evt);
    [DllImport(AS)] static extern int CGWarpMouseCursorPosition(CGPoint p);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] static extern void CFRelease(IntPtr o);
}

/// <summary>用 Carbon RegisterEventHotKey 注册全局热键 Option+A(=Alt+A)。
/// 纯热键注册无需「辅助功能」授权、简单可靠。前提：Option+A 未被别的 app 占用
/// （微信默认占用它 → 需在微信里把其截图快捷键改掉，让出 Option+A）。</summary>
internal static class MacHotKey
{
    const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    // 'keyb'；kEventHotKeyPressed = 5；optionKey = 1<<11；kVK_ANSI_A = 0
    const uint kEventClassKeyboard = 0x6B657962;
    const uint kEventHotKeyPressed = 5;
    const uint optionKey = 0x0800;
    const uint kVK_ANSI_A = 0x00;
    const uint kHotKeyMods = optionKey;   // Option+A = Alt+A

    [StructLayout(LayoutKind.Sequential)]
    struct EventTypeSpec { public uint eventClass; public uint eventKind; }

    [StructLayout(LayoutKind.Sequential)]
    struct EventHotKeyID { public uint signature; public uint id; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int EventHandlerProc(IntPtr callRef, IntPtr evt, IntPtr userData);

    [DllImport(Carbon)] static extern IntPtr GetApplicationEventTarget();
    [DllImport(Carbon)] static extern int InstallEventHandler(
        IntPtr target, EventHandlerProc handler, uint numTypes,
        EventTypeSpec[] typeList, IntPtr userData, out IntPtr outRef);
    [DllImport(Carbon)] static extern int RegisterEventHotKey(
        uint hotKeyCode, uint hotKeyModifiers, EventHotKeyID hotKeyID,
        IntPtr target, uint options, out IntPtr outRef);
    [DllImport(Carbon)] static extern int UnregisterEventHotKey(IntPtr hotKeyRef);
    [DllImport(Carbon)] static extern int RemoveEventHandler(IntPtr handlerRef);

    // 保持委托与回调存活，避免被 GC 回收后 native 侧悬空
    static EventHandlerProc? _proc;
    static Action? _cb;
    static IntPtr _hotKeyRef, _handlerRef;

    static int OnHotKey(IntPtr callRef, IntPtr evt, IntPtr userData)
    {
        var cb = _cb;
        if (cb != null)
            Dispatcher.UIThread.Post(() => { try { cb(); } catch { } });
        return 0; // noErr
    }

    public static IDisposable Register(Action onAltA)
    {
        _cb = onAltA;
        _proc = OnHotKey;
        var target = GetApplicationEventTarget();
        var spec = new EventTypeSpec { eventClass = kEventClassKeyboard, eventKind = kEventHotKeyPressed };
        InstallEventHandler(target, _proc, 1, new[] { spec }, IntPtr.Zero, out _handlerRef);
        var id = new EventHotKeyID { signature = 0x41534E50 /*'ASNP'*/, id = 1 };
        int st = RegisterEventHotKey(kVK_ANSI_A, kHotKeyMods, id, target, 0, out _hotKeyRef);
        if (st != 0) throw new InvalidOperationException($"RegisterEventHotKey failed: {st}");
        return new Unregister();
    }

    sealed class Unregister : IDisposable
    {
        public void Dispose()
        {
            if (_hotKeyRef != IntPtr.Zero) { UnregisterEventHotKey(_hotKeyRef); _hotKeyRef = IntPtr.Zero; }
            if (_handlerRef != IntPtr.Zero) { RemoveEventHandler(_handlerRef); _handlerRef = IntPtr.Zero; }
            _cb = null; _proc = null;
        }
    }
}

/// <summary>进程内写图片到剪贴板（NSPasteboard）——避免 osascript 子进程冷启动卡顿。</summary>
internal static class MacClipboard
{
    const string Objc = "/usr/lib/libobjc.dylib";
    const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const uint kCFStringEncodingUTF8 = 0x08000100;

    [DllImport(Objc)] static extern IntPtr objc_getClass(string s);
    [DllImport(Objc)] static extern IntPtr sel_registerName(string s);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr Msg(IntPtr r, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr Msg_PL(IntPtr r, IntPtr sel, IntPtr p, ulong len);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr Msg_PP(IntPtr r, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(CF)] static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string s, uint enc);

    public static void SetPng(byte[] png)
    {
        try
        {
            IntPtr nsdata;
            var h = GCHandle.Alloc(png, GCHandleType.Pinned);
            try
            {
                // [NSData dataWithBytes:length:] —— 复制一份，autoreleased
                nsdata = Msg_PL(objc_getClass("NSData"), sel_registerName("dataWithBytes:length:"),
                                h.AddrOfPinnedObject(), (ulong)png.Length);
            }
            finally { h.Free(); }
            if (nsdata == IntPtr.Zero) return;
            var pb = Msg(objc_getClass("NSPasteboard"), sel_registerName("generalPasteboard"));
            Msg(pb, sel_registerName("clearContents"));
            var type = CFStringCreateWithCString(IntPtr.Zero, "public.png", kCFStringEncodingUTF8); // 与 NSString 免费桥接
            Msg_PP(pb, sel_registerName("setData:forType:"), nsdata, type);
        }
        catch { }
    }
}
