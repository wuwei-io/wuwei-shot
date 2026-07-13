using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Threading;
using SkiaSharp;

namespace AltSnip.Platform;

/// <summary>Linux 实现：grim/scrot/ImageMagick 截屏 + xclip/wl-copy 图片剪贴板。全局热键待 M2。</summary>
public sealed class LinuxServices : IPlatformServices
{
    public string Name => "linux";

    public SKBitmap CaptureRegion(PixelRect region)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "altsnip_capture.png");
        int w = Math.Max(1, region.Width), h = Math.Max(1, region.Height);

        // Wayland：grim 支持直接抠区域
        if (Proc.Exists("grim") &&
            Proc.Run("grim", "-g", $"{region.X},{region.Y} {w}x{h}", tmp) == 0 && File.Exists(tmp))
            return SKBitmap.Decode(tmp) ?? new SKBitmap(w, h);

        // X11：整屏抓下来再裁
        bool full = false;
        if (Proc.Exists("scrot")) full = Proc.Run("scrot", "-o", tmp) == 0;
        if (!full && Proc.Exists("import")) full = Proc.Run("import", "-window", "root", tmp) == 0;
        if (!full && Proc.Exists("gnome-screenshot")) full = Proc.Run("gnome-screenshot", "-f", tmp) == 0;

        var shot = File.Exists(tmp) ? SKBitmap.Decode(tmp) : null;
        if (shot == null) return new SKBitmap(w, h);
        if (region.X == 0 && region.Y == 0 && shot.Width == w && shot.Height == h) return shot;

        var crop = new SKBitmap(w, h, shot.ColorType, shot.AlphaType);
        using (var canvas = new SKCanvas(crop))
            canvas.DrawBitmap(shot, new SKRect(region.X, region.Y, region.X + w, region.Y + h),
                              new SKRect(0, 0, w, h));
        shot.Dispose();
        return crop;
    }

    public void CopyImageToClipboard(SKImage image)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "altsnip_copy.png");
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var fs = File.Create(tmp))
            data.SaveTo(fs);

        if (Proc.Exists("wl-copy")) { Proc.RunWithStdin("wl-copy", tmp, "--type", "image/png"); return; }
        if (Proc.Exists("xclip")) { Proc.Run("xclip", "-selection", "clipboard", "-t", "image/png", "-i", tmp); return; }
    }

    // 全局热键：X11 XGrabKey（best-effort，X11/XWayland 有效；失败则返回 null 走托盘）
    public IDisposable? RegisterHotkey(Action onAltA)
    {
        try { return new X11Hotkey(onAltA); }
        catch { return null; }
    }

    public PixelPoint? CursorPosition() => null;

    public void ScrollDown(PixelPoint at, int notches)
    {
        if (Proc.Exists("xdotool"))
        {
            Proc.Run("xdotool", "mousemove", at.X.ToString(), at.Y.ToString());
            for (int i = 0; i < notches; i++) Proc.Run("xdotool", "click", "5"); // button 5 = 向下滚
        }
    }

    private sealed class X11Hotkey : IDisposable
    {
        const int GrabModeAsync = 1, KeyPress = 2;
        const uint Mod1 = 0x08, Lock = 0x02, Mod2 = 0x10;
        readonly IntPtr _display;
        readonly IntPtr _root;
        readonly int _keycode;
        readonly Thread _thread;
        readonly Action _onAltA;
        volatile bool _stop;

        public X11Hotkey(Action onAltA)
        {
            _onAltA = onAltA;
            _display = XOpenDisplay(null);
            if (_display == IntPtr.Zero) throw new InvalidOperationException("no X display");
            _root = XDefaultRootWindow(_display);
            _keycode = XKeysymToKeycode(_display, (IntPtr)0x61); // XK_a
            foreach (uint extra in new uint[] { 0, Lock, Mod2, Lock | Mod2 })
                XGrabKey(_display, _keycode, Mod1 | extra, _root, true, GrabModeAsync, GrabModeAsync);
            _thread = new Thread(Loop) { IsBackground = true, Name = "AltSnip-x11-hotkey" };
            _thread.Start();
        }

        void Loop()
        {
            var buf = new byte[192]; // XEvent 联合体
            while (!_stop)
            {
                if (XPending(_display) > 0)
                {
                    XNextEvent(_display, buf);
                    if (BitConverter.ToInt32(buf, 0) == KeyPress)
                        Dispatcher.UIThread.Post(_onAltA);
                }
                else Thread.Sleep(20);
            }
            try
            {
                foreach (uint extra in new uint[] { 0, Lock, Mod2, Lock | Mod2 })
                    XUngrabKey(_display, _keycode, Mod1 | extra, _root);
                XCloseDisplay(_display);
            }
            catch { }
        }

        public void Dispose() { _stop = true; }

        [DllImport("libX11.so.6")] static extern IntPtr XOpenDisplay(string? d);
        [DllImport("libX11.so.6")] static extern int XCloseDisplay(IntPtr d);
        [DllImport("libX11.so.6")] static extern IntPtr XDefaultRootWindow(IntPtr d);
        [DllImport("libX11.so.6")] static extern int XKeysymToKeycode(IntPtr d, IntPtr keysym);
        [DllImport("libX11.so.6")] static extern int XGrabKey(IntPtr d, int keycode, uint mods, IntPtr win, bool owner, int pMode, int kMode);
        [DllImport("libX11.so.6")] static extern int XUngrabKey(IntPtr d, int keycode, uint mods, IntPtr win);
        [DllImport("libX11.so.6")] static extern int XNextEvent(IntPtr d, byte[] ev);
        [DllImport("libX11.so.6")] static extern int XPending(IntPtr d);
    }
}
