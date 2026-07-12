using System;
using AltSnip.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace AltSnip;

public partial class App : Application
{
    private Window? _host;          // 隐形宿主：提供 Screens 信息 + 保活
    private IDisposable? _hotkey;
    private bool _capturing;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Log.Clear();
        Log.W("app init");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _host = new Window
            {
                Width = 1,
                Height = 1,
                SystemDecorations = SystemDecorations.None,
                ShowInTaskbar = false,
                Topmost = false,
                Opacity = 0,
                Position = new PixelPoint(-32000, -32000),
            };
            _host.Show();

            SetupTray();
            try { _hotkey = PlatformServices.Current.RegisterHotkey(Capture); } catch { }

            // 隐藏自测：启动即触发一次截图，便于开发时验证覆盖层
            foreach (var a in Environment.GetCommandLineArgs())
                if (a == "--test-capture")
                    Avalonia.Threading.Dispatcher.UIThread.Post(Capture,
                        Avalonia.Threading.DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static WindowIcon LoadIcon()
        => new WindowIcon(AssetLoader.Open(new Uri("avares://AltSnip/Assets/logo.png")));

    private void SetupTray()
    {
        var tray = new TrayIcon { ToolTipText = "AltSnip — Alt+A to capture", Icon = LoadIcon() };
        tray.Clicked += (_, _) => Capture();

        var menu = new NativeMenu();
        var cap = new NativeMenuItem("Capture (Alt+A)");
        cap.Click += (_, _) => Capture();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Shutdown();
        menu.Items.Add(cap);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);
        tray.Menu = menu;

        TrayIcon.SetIcons(this, new TrayIcons { tray });
    }

    private void Shutdown()
    {
        _hotkey?.Dispose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            d.Shutdown();
    }

    private void Capture()
    {
        if (_capturing || _host == null) return;
        _capturing = true;
        try
        {
            var all = _host.Screens.All;
            if (all == null || all.Count == 0) { _capturing = false; return; }

            // 覆盖鼠标所在的那个显示器（单窗口最稳，且弹在你正操作的屏上）
            var cur = PlatformServices.Current.CursorPosition();
            var screen = (cur.HasValue ? _host.Screens.ScreenFromPoint(cur.Value) : null)
                         ?? _host.Screens.Primary ?? all[0];
            var bounds = screen.Bounds;
            Log.W($"capture screen bounds={bounds} scaling={screen.Scaling} cursor={cur}");

            var shot = PlatformServices.Current.CaptureRegion(bounds);
            Log.W($"shot {shot.Width}x{shot.Height}");
            var win = new OverlayWindow(shot, bounds);
            win.Closed += (_, _) => _capturing = false;
            win.Show();
            win.Activate();
            Log.W("win.Show done");
        }
        catch { _capturing = false; }
    }
}
