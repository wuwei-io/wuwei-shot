using System;
using System.Runtime.InteropServices;

namespace WuweiShot.Platform;

/// <summary>Windows 窗口扩展样式助手：把长截图的浮层设为
/// 置顶 + 不抢焦点(NOACTIVATE，点了也不夺走被截 App 的键盘焦点/滚动)
/// + 可选点击穿透(TRANSPARENT，滚轮穿过浮层滚到底下真 App)
/// + 工具窗(不进任务栏/Alt-Tab)。非 Windows 平台为 no-op。</summary>
public static class WinOverlay
{
    public static void Apply(IntPtr hwnd, bool clickThrough)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        SetEx(hwnd, clickThrough);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>同上，但用物理像素强制窗口矩形——绕开 Avalonia 在 PerMonitorV2/多屏下
    /// Position/Size 的 DPI 误差(治蒙层顶/左漏盖)。x/y/w/h 均为屏幕物理像素。</summary>
    public static void ApplyRect(IntPtr hwnd, bool clickThrough, int x, int y, int w, int h)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        SetEx(hwnd, clickThrough);
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE);
    }

    static void SetEx(IntPtr hwnd, bool clickThrough)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
        if (clickThrough) ex |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOPMOST = 0x00000008, WS_EX_TRANSPARENT = 0x00000020,
              WS_EX_TOOLWINDOW = 0x00000080, WS_EX_LAYERED = 0x00080000,
              WS_EX_NOACTIVATE = 0x08000000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // 读原生窗口真实矩形（物理像素）——诊断用
    public static (int w, int h) NativeSize(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return (-1, -1);
        return GetWindowRect(hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (-2, -2);
    }
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
