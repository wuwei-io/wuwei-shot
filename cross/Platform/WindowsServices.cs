using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Threading;
using SkiaSharp;

namespace AltSnip.Platform;

/// <summary>Windows 原生实现：BitBlt 截屏 / CF_DIB 图片剪贴板 / WH_KEYBOARD_LL 全局热键。</summary>
public sealed class WindowsServices : IPlatformServices
{
    public string Name => "windows";

    // ---------------- 截屏 ----------------
    public SKBitmap CaptureRegion(PixelRect region)
    {
        int w = Math.Max(1, region.Width), h = Math.Max(1, region.Height);
        IntPtr hScreen = GetDC(IntPtr.Zero);
        IntPtr hDC = CreateCompatibleDC(hScreen);
        IntPtr hBmp = CreateCompatibleBitmap(hScreen, w, h);
        IntPtr old = SelectObject(hDC, hBmp);
        BitBlt(hDC, 0, 0, w, h, hScreen, region.X, region.Y, SRCCOPY | CAPTUREBLT);

        var bmi = new BITMAPINFO();
        bmi.biSize = Marshal.SizeOf<BITMAPINFO>();
        bmi.biWidth = w;
        bmi.biHeight = -h;          // 自上而下
        bmi.biPlanes = 1;
        bmi.biBitCount = 32;
        bmi.biCompression = 0;      // BI_RGB

        var skbmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        GetDIBits(hDC, hBmp, 0, (uint)h, skbmp.GetPixels(), ref bmi, 0);

        SelectObject(hDC, old);
        DeleteObject(hBmp);
        DeleteDC(hDC);
        ReleaseDC(IntPtr.Zero, hScreen);
        return skbmp;
    }

    // ---------------- 图片剪贴板 ----------------
    public void CopyImageToClipboard(SKImage image)
    {
        int w = image.Width, h = image.Height;
        using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var c = new SKCanvas(bmp)) c.DrawImage(image, 0, 0);
        byte[] src = bmp.Bytes; // BGRA, 自上而下

        int stride = w * 4;
        int headerSize = 40;
        int dibSize = headerSize + stride * h;
        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)dibSize);
        IntPtr ptr = GlobalLock(hMem);
        try
        {
            // BITMAPINFOHEADER
            var hdr = new byte[headerSize];
            BitConverter.GetBytes(headerSize).CopyTo(hdr, 0);
            BitConverter.GetBytes(w).CopyTo(hdr, 4);
            BitConverter.GetBytes(h).CopyTo(hdr, 8);          // 正数 = 自下而上
            BitConverter.GetBytes((short)1).CopyTo(hdr, 12);  // planes
            BitConverter.GetBytes((short)32).CopyTo(hdr, 14); // bpp
            BitConverter.GetBytes(0).CopyTo(hdr, 16);         // BI_RGB
            Marshal.Copy(hdr, 0, ptr, headerSize);

            // 像素：翻转成自下而上，alpha 补 255
            var row = new byte[stride];
            for (int y = 0; y < h; y++)
            {
                Array.Copy(src, (h - 1 - y) * stride, row, 0, stride);
                for (int x = 3; x < stride; x += 4) row[x] = 255;
                Marshal.Copy(row, 0, ptr + headerSize + y * stride, stride);
            }
        }
        finally { GlobalUnlock(hMem); }

        if (OpenClipboard(IntPtr.Zero))
        {
            EmptyClipboard();
            SetClipboardData(CF_DIB, hMem); // 所有权转移，勿释放
            CloseClipboard();
        }
        else
        {
            GlobalFree(hMem);
        }
    }

    // ---------------- 全局热键 Alt+A（底层键盘钩子）----------------
    public IDisposable? RegisterHotkey(Action onAltA)
        => new HotkeyHook(onAltA);

    public PixelPoint? CursorPosition()
        => GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : (PixelPoint?)null;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    private sealed class HotkeyHook : IDisposable
    {
        const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104,
                  WM_KEYUP = 0x101, WM_SYSKEYUP = 0x105, VK_A = 0x41, VK_MENU = 0x12, LLKHF_INJECTED = 0x10;
        readonly LowLevelKeyboardProc _proc;
        IntPtr _hook;
        bool _repeat;
        readonly Action _onAltA;

        public HotkeyHook(Action onAltA)
        {
            _onAltA = onAltA;
            _proc = Callback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (kb.vkCode == VK_A && (kb.flags & LLKHF_INJECTED) == 0)
                {
                    if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
                    {
                        if (!_repeat) { _repeat = true; Dispatcher.UIThread.Post(_onAltA); }
                        return (IntPtr)1;
                    }
                    if ((msg == WM_KEYUP || msg == WM_SYSKEYUP) && _repeat) { _repeat = false; return (IntPtr)1; }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }
    }

    // ---------------- P/Invoke ----------------
    const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000, CF_DIB = 8;
    const uint GMEM_MOVEABLE = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public int biSize; public int biWidth; public int biHeight;
        public short biPlanes; public short biBitCount; public int biCompression;
        public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
        public int biClrUsed; public int biClrImportant;
        public int colors; // 占位，避免调色板越界
    }

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, IntPtr bits, ref BITMAPINFO bmi, uint usage);

    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint fmt, IntPtr h);

    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr h);

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc proc, IntPtr hMod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] static extern IntPtr GetModuleHandle(string? name);
}
