// AltSnip 演示动画生成器（不打进正式 exe，只用于生成 docs/demo.gif）。
// 用工具自己的渲染引擎，在一个虚构的“订单详情”界面上自动演示
// 框选 -> 箭头 -> 打字 -> 马赛克 -> 复制 的全过程，导出为循环 GIF。
// 编译： csc /target:winexe /out:DemoGen.exe /main:SnipTool.DemoGen \
//        /reference:System.Drawing.dll /reference:System.Windows.Forms.dll src\Snip.cs tools\DemoGen.cs
// 运行： DemoGen.exe <输出gif路径>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace SnipTool
{
    static class DemoGen
    {
        const int W = 820, H = 520;
        static readonly Color RED = Color.FromArgb(0xfa, 0x51, 0x51);
        static readonly Color GOLD = Color.FromArgb(0xf4, 0xb7, 0x40);
        static readonly Color GREEN = Color.FromArgb(0x2e, 0xbe, 0x6e);

        [STAThread]
        static void Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : "demo.gif";
            var frames = new List<Bitmap>();
            var delays = new List<int>();   // 单位：厘秒(1/100s)

            Bitmap bg = MockScreen();
            var f = new OverlayForm(bg, new Rectangle(0, 0, W, H));
            f.Bounds = new Rectangle(0, 0, W, H);

            Action<int> hold = cs => { frames.Add(f.DemoFrame()); delays.Add(cs); };

            // 目标坐标
            Rectangle finalSel = new Rectangle(140, 118, 545, 320);
            Rectangle phone = new Rectangle(236, 185, 156, 30);   // 手机号（打码目标）
            Point btn = new Point(588, 392);                       // 按钮左上（箭头指向）

            // 1) 提示语
            f.DemoConfig(Rectangle.Empty, false, false, Tool.None);
            hold(110);

            // 2) 拖动框选
            for (int i = 1; i <= 8; i++)
            {
                int w = finalSel.Width * i / 8, h = finalSel.Height * i / 8;
                f.DemoConfig(new Rectangle(finalSel.X, finalSel.Y, w, h), false, true, Tool.None);
                hold(5);
            }
            f.DemoConfig(finalSel, true, false, Tool.None);
            hold(90);   // 出现控制点，停一下

            // 3) 选“方框红”颜色 + 箭头工具
            f.DemoStyle(RED, 4f);
            f.DemoConfig(finalSel, true, false, Tool.Arrow);
            f.DemoHover(1);
            hold(70);

            // 4) 画箭头，指向按钮（走下方空白，不压数据行）
            var arrow = new Anno { Type = Tool.Arrow, A = new Point(330, 360), B = new Point(330, 360), Color = RED, Width = 4 };
            f.DemoAnnos.Add(arrow);
            Point aEnd = new Point(btn.X - 4, btn.Y + 2);
            for (int i = 1; i <= 6; i++)
            {
                arrow.B = new Point(arrow.A.X + (aEnd.X - arrow.A.X) * i / 6, arrow.A.Y + (aEnd.Y - arrow.A.Y) * i / 6);
                hold(5);
            }
            hold(60);

            // 5) 文字工具，打一段说明（放在箭头左侧空白处）
            f.DemoConfig(finalSel, true, false, Tool.Text);
            f.DemoHover(7);
            var text = new Anno { Type = Tool.Text, A = new Point(196, 346), Text = "", Color = RED };
            f.DemoAnnos.Add(text);
            string full = "点这里发货";
            for (int i = 1; i <= full.Length; i++) { text.Text = full.Substring(0, i); hold(16); }
            hold(70);

            // 6) 马赛克，遮住手机号
            f.DemoStyle(RED, 4f);
            f.DemoConfig(finalSel, true, false, Tool.Mosaic);
            f.DemoHover(8);
            var mo = new Anno { Type = Tool.Mosaic, A = new Point(phone.Left, phone.Top), B = new Point(phone.Left, phone.Bottom) };
            f.DemoAnnos.Add(mo);
            for (int i = 1; i <= 6; i++) { mo.B = new Point(phone.Left + phone.Width * i / 6, phone.Bottom); hold(6); }
            hold(80);

            // 7) 点确认 -> “已复制”提示
            f.DemoConfig(finalSel, true, false, Tool.None);
            f.DemoHover(6);
            using (var frame = f.DemoFrame())
            {
                var toastFrame = (Bitmap)frame.Clone();
                DrawToast(toastFrame, "✓  已复制到剪贴板");
                frames.Add(toastFrame); delays.Add(220);
            }

            SaveGif(outPath, frames, delays);
            Console.WriteLine("Wrote " + outPath + "  (" + frames.Count + " frames)");
            foreach (var b in frames) b.Dispose();
            f.Dispose(); bg.Dispose();
        }

        // 虚构一个干净的“订单详情”窗口，纯色为主，GIF 友好
        static Bitmap MockScreen()
        {
            var b = new Bitmap(W, H);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // 纯色底（GIF 量化更干净，无噪点）
                using (var bgb = new SolidBrush(Color.FromArgb(0x1b, 0x20, 0x2b)))
                    g.FillRectangle(bgb, 0, 0, W, H);

                Rectangle win = new Rectangle(105, 60, 610, 400);
                using (var sh = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                using (var p = Round(new Rectangle(win.X + 4, win.Y + 8, win.Width, win.Height), 14)) g.FillPath(sh, p);
                using (var wb = new SolidBrush(Color.White))
                using (var p = Round(win, 14)) g.FillPath(wb, p);

                // 标题栏
                using (var tb = new SolidBrush(Color.FromArgb(0xf4, 0xf5, 0xf7)))
                using (var p = RoundTop(new Rectangle(win.X, win.Y, win.Width, 48), 14)) g.FillPath(tb, p);
                int cy = win.Y + 24;
                DrawDot(g, win.X + 22, cy, Color.FromArgb(0xff, 0x5f, 0x57));
                DrawDot(g, win.X + 40, cy, Color.FromArgb(0xfe, 0xbc, 0x2e));
                DrawDot(g, win.X + 58, cy, Color.FromArgb(0x28, 0xc8, 0x40));
                using (var tf = new Font("Microsoft YaHei", 11f, FontStyle.Bold))
                using (var tc = new SolidBrush(Color.FromArgb(0x33, 0x37, 0x40)))
                    g.DrawString("订单详情", tf, tc, win.X + 88, win.Y + 15);

                using (var lab = new Font("Microsoft YaHei", 11f))
                using (var val = new Font("Microsoft YaHei", 11.5f, FontStyle.Bold))
                using (var labc = new SolidBrush(Color.FromArgb(0x8a, 0x90, 0x99)))
                using (var valc = new SolidBrush(Color.FromArgb(0x23, 0x28, 0x30)))
                {
                    int x = win.X + 40, y = win.Y + 84;
                    g.DrawString("收货人", lab, labc, x, y); g.DrawString("张大成", val, valc, x + 96, y);
                    y += 44;
                    g.DrawString("手机号", lab, labc, x, y); g.DrawString("138 8888 6666", val, valc, x + 96, y);
                    y += 44;
                    g.DrawString("收货地址", lab, labc, x, y); g.DrawString("杭州市 · 科研者之家 12 栋", val, valc, x + 96, y);
                    y += 44;
                    g.DrawString("商品", lab, labc, x, y); g.DrawString("AltSnip 授权 × 1", val, valc, x + 96, y);
                }

                // 主按钮
                Rectangle button = new Rectangle(590, 388, 96, 40);
                using (var bb = new LinearGradientBrush(button, Color.FromArgb(0xff, 0x8a, 0x3d), Color.FromArgb(0xf0, 0x6a, 0x1e), 90f))
                using (var p = Round(button, 8)) g.FillPath(bb, p);
                using (var bf = new Font("Microsoft YaHei", 10.5f, FontStyle.Bold))
                using (var bc = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("确认发货", bf, bc, button, sf);
                }
            }
            return b;
        }

        static void DrawDot(Graphics g, int cx, int cy, Color c)
        { using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - 6, cy - 6, 12, 12); }

        static void DrawToast(Bitmap bmp, string text)
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var f = new Font("Microsoft YaHei", 12.5f, FontStyle.Bold))
                {
                    SizeF sz = g.MeasureString(text, f);
                    int w = (int)sz.Width + 44, h = 46;
                    int x = (W - w) / 2, y = (H - h) / 2;
                    using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                    using (var p = Round(new Rectangle(x, y + 3, w, h), 12)) g.FillPath(sh, p);
                    using (var bg = new SolidBrush(Color.FromArgb(235, 0x0e, 0x0a, 0x06)))
                    using (var p = Round(new Rectangle(x, y, w, h), 12)) g.FillPath(bg, p);
                    using (var fg = new SolidBrush(GOLD))
                    {
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString(text, f, fg, new Rectangle(x, y, w, h), sf);
                    }
                }
            }
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
        static GraphicsPath RoundTop(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddLine(r.Right, r.Bottom, r.X, r.Bottom); p.CloseFigure(); return p;
        }

        // ---- 动画 GIF（GDI+ 多帧 + 反射设置每帧延时/循环）----
        static void SaveGif(string path, List<Bitmap> frames, List<int> delaysCs)
        {
            ImageCodecInfo enc = null;
            foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Gif.Guid) enc = c;

            var epMulti = new EncoderParameters(1);
            epMulti.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
            var epNext = new EncoderParameters(1);
            epNext.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.FrameDimensionTime);
            var epFlush = new EncoderParameters(1);
            epFlush.Param[0] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.Flush);

            var first = frames[0];
            byte[] delayBytes = new byte[4 * frames.Count];
            for (int i = 0; i < frames.Count; i++)
                BitConverter.GetBytes(delaysCs[i]).CopyTo(delayBytes, i * 4);
            first.SetPropertyItem(NewProp(0x5100, 4, delayBytes));      // PropertyTagFrameDelay
            first.SetPropertyItem(NewProp(0x5101, 3, new byte[] { 0, 0 })); // PropertyTagLoopCount = 无限

            using (var fs = File.Create(path))
            {
                first.Save(fs, enc, epMulti);
                for (int i = 1; i < frames.Count; i++) first.SaveAdd(frames[i], epNext);
                first.SaveAdd(epFlush);
            }
        }

        static PropertyItem NewProp(int id, short type, byte[] value)
        {
            var pi = (PropertyItem)FormatterServices.GetUninitializedObject(typeof(PropertyItem));
            pi.Id = id; pi.Type = type; pi.Len = value.Length; pi.Value = value;
            return pi;
        }
    }
}
