using System;
using System.IO;
using System.Threading.Tasks;
using AltSnip.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;

namespace AltSnip;

/// <summary>长截图：遮罩关闭后，自动向下滚动 + 连续抓取选区 + 拼接，最后弹出结果窗。</summary>
public static class LongShot
{
    public static async Task Run(PixelRect region, SKBitmap frame0)
    {
        var platform = PlatformServices.Current;
        await Task.Delay(300);   // 等遮罩关闭 + 目标重绘
        var acc = frame0;
        var pt = new PixelPoint(region.X + region.Width / 2, region.Y + region.Height / 2);
        int stable = 0;
        for (int i = 0; i < 60; i++)
        {
            platform.ScrollDown(pt, 3);
            await Task.Delay(380);   // 等滚动 + 内容渲染
            SKBitmap f;
            try { f = platform.CaptureRegion(region); } catch { break; }
            int startY = Stitcher.FindNewStart(acc, f);
            if (f.Height - startY < 4) { f.Dispose(); if (++stable >= 2) break; }  // 连续两次无新内容 = 到底
            else { stable = 0; acc = Stitcher.Append(acc, f, startY); f.Dispose(); }
            if (acc.Height > 20000) break;   // 安全上限
        }
        var win = new ResultWindow(acc);
        win.Show();
        win.Activate();
    }

    static Bitmap ToAvalonia(SKBitmap sk)
    {
        using var img = SKImage.FromBitmap(sk);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    private sealed class ResultWindow : Window
    {
        readonly SKBitmap _img;

        public ResultWindow(SKBitmap img)
        {
            _img = img;
            Title = $"长截图  {img.Width} × {img.Height}";
            Width = Math.Min(760, img.Width + 40);
            Height = 720;
            Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x23, 0x2b));

            var image = new Image { Source = ToAvalonia(img), Stretch = Stretch.None };
            var scroll = new ScrollViewer
            {
                Content = image,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            var copy = new Button { Content = "复制" };
            copy.Click += (_, _) => { try { PlatformServices.Current.CopyImageToClipboard(SKImage.FromBitmap(_img)); } catch { } };
            var save = new Button { Content = "保存 PNG" };
            save.Click += async (_, _) => await Save();
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10) };
            bar.Children.Add(copy);
            bar.Children.Add(save);

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(scroll);
            Content = root;
        }

        async Task Save()
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = "AltSnip-long.png",
                DefaultExtension = "png",
                FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } } },
            });
            if (file != null)
            {
                await using var s = await file.OpenWriteAsync();
                using var img = SKImage.FromBitmap(_img);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                data.SaveTo(s);
            }
        }
    }
}
