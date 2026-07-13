using System;
using System.Runtime.InteropServices;

namespace AltSnip.Platform;

/// <summary>把一个无边框 NSWindow 抬到"遮罩层级"并铺满整屏（含菜单栏/Dock 区域），
/// 但仍留在当前 Space——避免 macOS 原生全屏把窗口滑进独立 Space 把别的窗口推走。
/// 这是截图取景窗覆盖全屏的标准做法。</summary>
public static class MacWindow
{
    const string Objc = "/usr/lib/libobjc.dylib";
    const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [StructLayout(LayoutKind.Sequential)]
    struct NSRect { public double X, Y, W, H; }

    [DllImport(Objc)] static extern IntPtr sel_registerName(string name);
    [DllImport(Objc)] static extern IntPtr objc_getClass(string name);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern IntPtr MsgSend_Ret(IntPtr r, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern void MsgSend_Long(IntPtr r, IntPtr sel, long a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern void MsgSend_ULong(IntPtr r, IntPtr sel, ulong a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] static extern void MsgSend_RectBool(IntPtr r, IntPtr sel, NSRect rect, [MarshalAs(UnmanagedType.I1)] bool b);

    // 屏保层级(1000)：高于菜单栏(24)/Dock(20)，足以铺满整屏；不用 shielding(极高)以免惊动其它窗口
    const long kScreenSaverLevel = 1000;
    // NSWindowCollectionBehavior：FullScreenAuxiliary(1<<8) | Stationary(1<<4)
    // 不设 CanJoinAllSpaces —— 只留在当前 Space，避免触发 Spaces 重排把别的窗口挪动
    const ulong kCollectionBehavior = (1UL << 8) | (1UL << 4);

    // NSApplicationActivationPolicy: Regular=0, Accessory=1, Prohibited=2
    /// <summary>把 app 设为 Accessory：不在 Dock 显示图标（保留菜单栏托盘图标 + 可弹窗），
    /// 后台常驻型。Avalonia 默认 Regular 会显示 Dock 图标，这里覆盖掉。</summary>
    public static void HideDockIcon()
    {
        try
        {
            var app = MsgSend_Ret(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app != IntPtr.Zero)
                MsgSend_Long(app, sel_registerName("setActivationPolicy:"), 1); // Accessory
        }
        catch { }
    }

    /// <param name="nsWindow">Avalonia TryGetPlatformHandle().Handle（macOS 下即 NSWindow*）</param>
    /// <param name="x">屏左下原点 X（点）</param><param name="y">屏左下原点 Y（点）</param>
    /// <param name="w">宽（点）</param><param name="h">高（点）</param>
    public static void CoverScreen(IntPtr nsWindow, double x, double y, double w, double h)
    {
        if (nsWindow == IntPtr.Zero) return;
        try
        {
            // 抬到屏保层级（高于菜单栏/Dock，但不至于惊动其它窗口）
            MsgSend_Long(nsWindow, sel_registerName("setLevel:"), kScreenSaverLevel);
            // 允许出现在所有 Space、且不参与原生全屏切换
            MsgSend_ULong(nsWindow, sel_registerName("setCollectionBehavior:"), kCollectionBehavior);
            // 铺满整屏（AppKit 左下原点；主屏 y=0）
            var frame = new NSRect { X = x, Y = y, W = w, H = h };
            MsgSend_RectBool(nsWindow, sel_registerName("setFrame:display:"), frame, true);
        }
        catch { /* interop 失败则退回普通置顶窗，不致命 */ }
    }
}
