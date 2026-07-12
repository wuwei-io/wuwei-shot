using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnipTool
{
    // 托盘常驻程序：用底层键盘钩子(WH_KEYBOARD_LL)抢在其它程序(如微信)之前拦截 Alt+A。
    // 系统会先把按键送到本钩子，命中 Alt+A 时触发截图并“吞掉”该键，
    // 因此谁用 RegisterHotKey 抢注 Alt+A 都无效，本工具始终优先。
    // 注意：钩子只判断“是不是 Alt+A”，不记录任何按键内容，非键盘记录器。
    public class TrayApp : Form
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_SYSKEYUP = 0x0105;
        const int VK_A = 0x41;
        const int VK_MENU = 0x12;   // Alt
        const int LLKHF_INJECTED = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        NotifyIcon _tray;
        bool _capturing = false;
        bool _altARepeat = false;     // 防止长按 Alt+A 连发
        OverlayForm _overlay;         // 当前遮罩，供再次 Alt+A 关闭
        IntPtr _hook = IntPtr.Zero;
        HookProc _proc;   // 保持引用，防止被 GC 回收导致钩子失效

        public TrayApp()
        {
            // 窗口本体隐藏，只做消息接收
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.FormBorderStyle = FormBorderStyle.FixedToolWindow;
            this.Opacity = 0;
            this.Load += (s, e) => { this.Visible = false; };

            // 用打进 exe 的 Logo 作为托盘/窗口图标
            Icon appIcon;
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { appIcon = SystemIcons.Application; }
            this.Icon = appIcon;

            _tray = new NotifyIcon();
            _tray.Icon = appIcon;
            _tray.Text = "AltSnip 截图 (Alt+A)";
            _tray.Visible = true;

            var menu = new ContextMenuStrip();
            menu.Items.Add("截图 (Alt+A)", null, (s, e) => StartCapture());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => { _tray.Visible = false; Application.Exit(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => StartCapture();

            // 安装底层键盘钩子
            _proc = HookCallback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
            {
                MessageBox.Show("键盘钩子安装失败，Alt+A 可能不生效。\n你仍可双击托盘图标截图。",
                    "截图工具", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                var kb = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if (kb.vkCode == VK_A && (kb.flags & LLKHF_INJECTED) == 0)
                {
                    // Alt+A 按下：切换（没开就截图，已开就取消）；长按只触发一次
                    if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
                    {
                        if (!_altARepeat) { _altARepeat = true; this.BeginInvoke((Action)ToggleCapture); }
                        return (IntPtr)1;   // 吞掉（微信等收不到）
                    }
                    // A 抬起：复位，并吞掉配对的抬起
                    if ((msg == WM_KEYUP || msg == WM_SYSKEYUP) && _altARepeat)
                    {
                        _altARepeat = false;
                        return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        void ToggleCapture()
        {
            if (_capturing) { if (_overlay != null) _overlay.Close(); }
            else StartCapture();
        }

        void StartCapture()
        {
            if (_capturing) return;
            _capturing = true;
            try
            {
                // 先把当前整个虚拟屏幕（含多显示器）冻结成一张图
                Rectangle vs = SystemInformation.VirtualScreen;
                Bitmap full = new Bitmap(vs.Width, vs.Height);
                using (Graphics g = Graphics.FromImage(full))
                {
                    g.CopyFromScreen(vs.Location, Point.Empty, vs.Size);
                }
                using (var overlay = new OverlayForm(full, vs))
                {
                    _overlay = overlay;
                    overlay.ShowDialog();
                    _overlay = null;
                }
                full.Dispose();
            }
            finally { _capturing = false; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            base.OnFormClosing(e);
        }

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--preview")
            {
                int hover = args.Length > 2 ? int.Parse(args[2]) : 0;
                OverlayForm.SavePreview(args.Length > 1 ? args[1] : "preview.png", hover);
                return;
            }
            Application.Run(new TrayApp());
        }
    }

    // 标注类型
    enum Tool { None, Arrow, Line, Rect, Text, Mosaic }

    // 一条标注
    class Anno
    {
        public Tool Type;
        public Point A;
        public Point B;
        public string Text;
        public Color Color;
        public float Width = 3f;
    }

    // 全屏选区遮罩：拖框 -> 可选标注(箭头/直线/方框) -> 打勾复制到剪贴板
    public class OverlayForm : Form
    {
        Bitmap _full;          // 冻结的屏幕图
        Rectangle _vs;         // 虚拟屏幕范围（含负坐标偏移）
        Point _start;
        Rectangle _sel = Rectangle.Empty;
        bool _dragging = false;   // 正在拖框选
        bool _hasSel = false;

        Tool _tool = Tool.None;
        readonly List<Anno> _annos = new List<Anno>();
        Anno _drawing = null;     // 正在画的标注
        bool _annotating = false;

        // 选框的移动/缩放（仅未选标注工具时）
        bool _moving = false, _resizing = false;
        int _handle = -1;         // 0 TL 1 TC 2 TR 3 RC 4 BR 5 BC 6 BL 7 LC
        Point _dragOrigin;
        Rectangle _selOrig;
        const int HS = 8;         // 控制点大小

        // 工具条按钮：id 1箭头 2直线 3方框 7文字 8马赛克 4撤销 9保存 5取消 6确认
        int _hover = 0;
        Rectangle _bar = Rectangle.Empty;
        readonly int[] _ids = { 1, 2, 3, 7, 8, 4, 9, 5, 6 };
        readonly Rectangle[] _rects = new Rectangle[9];
        const int BTN = 42;

        // 颜色 / 粗细（样式子条，选了可着色工具时出现）
        Color _color = Color.FromArgb(0xfa, 0x51, 0x51);
        float _width = 3f;
        static readonly Color[] PALETTE = {
            Color.FromArgb(0xfa,0x51,0x51), // 红
            Color.FromArgb(0xf4,0xb7,0x40), // 金
            Color.FromArgb(0xe0,0x76,0x2a), // 橙
            Color.FromArgb(0x2e,0xbe,0x6e), // 绿
            Color.FromArgb(0x3b,0x82,0xf6), // 蓝
            Color.White,                    // 白
            Color.FromArgb(0x22,0x22,0x22), // 黑
        };
        static readonly float[] WIDTHS = { 2f, 4f, 7f };
        bool _showStyle = false;
        int _widthCount = 0;
        Rectangle _styleBar = Rectangle.Empty;
        readonly Rectangle[] _colorRects = new Rectangle[7];
        readonly Rectangle[] _widthRects = new Rectangle[3];
        const int SUB = 30;

        // 文字标注：用真输入框（保证中文输入法可用）。底色取自点击处背后的原图，
        // 在纯色背景上看着就是透明；宽度随字数自动伸缩，不留长条。
        static readonly Font AnnoFont = new Font("Microsoft YaHei", 13.5f);
        TextBox _tb = null;

        // 暖琥珀 VI
        static readonly Color C_GOLD   = Color.FromArgb(0xf4, 0xb7, 0x40);
        static readonly Color C_TEXT   = Color.FromArgb(0xf3, 0xec, 0xe0);
        static readonly Color C_TEXT2  = Color.FromArgb(0xb0, 0x9b, 0x80);
        static readonly Color C_DEEP   = Color.FromArgb(0x0e, 0x0a, 0x06);
        static readonly Color C_ANNO   = Color.FromArgb(0xfa, 0x51, 0x51); // 标注红（最醒目，指哪打哪）
        const float ANNO_W = 3f;

        public OverlayForm(Bitmap full, Rectangle vs)
        {
            _full = full;
            _vs = vs;
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.Bounds = new Rectangle(0, 0, vs.Width, vs.Height);
            this.Location = vs.Location; // 覆盖整个虚拟屏幕
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.Cursor = Cursors.Cross;
            this.DoubleBuffered = true;
            this.KeyPreview = true;
            this.BackColor = Color.Black;

            this.MouseDown += OnDown;
            this.MouseMove += OnMove;
            this.MouseUp += OnUp;
            this.KeyDown += OnKey;
        }

        void OnKey(object s, KeyEventArgs e)
        {
            // 正在打字：Enter 提交、Esc 取消这段文字，其它键交给输入框
            if (_tb != null)
            {
                if (e.KeyCode == Keys.Escape) { CancelText(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Enter) { CommitText(); e.SuppressKeyPress = true; }
                return;
            }
            if (e.KeyCode == Keys.Escape) this.Close();
            else if (e.KeyCode == Keys.Enter && _hasSel) Confirm();
            else if (e.Control && e.KeyCode == Keys.Z && _annos.Count > 0)
            {
                _annos.RemoveAt(_annos.Count - 1);
                this.Invalidate();
            }
        }

        int HitButton(Point p)
        {
            if (!_hasSel) return 0;
            for (int i = 0; i < _rects.Length; i++)
                if (_rects[i].Contains(p)) return _ids[i];
            return 0;
        }

        // 8 个控制点（0 TL 1 TC 2 TR 3 RC 4 BR 5 BC 6 BL 7 LC）
        Point[] HandlePoints()
        {
            int l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom;
            int cx = l + _sel.Width / 2, cy = t + _sel.Height / 2;
            return new[]
            {
                new Point(l, t), new Point(cx, t), new Point(r, t),
                new Point(r, cy), new Point(r, b), new Point(cx, b),
                new Point(l, b), new Point(l, cy)
            };
        }

        Rectangle HandleRect(Point p) { return new Rectangle(p.X - HS / 2, p.Y - HS / 2, HS, HS); }

        int HitHandle(Point pt)
        {
            if (!_hasSel) return -1;
            var pts = HandlePoints();
            for (int i = 0; i < 8; i++)
            {
                var hr = HandleRect(pts[i]); hr.Inflate(3, 3);
                if (hr.Contains(pt)) return i;
            }
            return -1;
        }

        Cursor HandleCursor(int h)
        {
            switch (h)
            {
                case 0: case 4: return Cursors.SizeNWSE;
                case 2: case 6: return Cursors.SizeNESW;
                case 1: case 5: return Cursors.SizeNS;
                default: return Cursors.SizeWE; // 3, 7
            }
        }

        Rectangle ResizeRect(Rectangle o, int h, Point pt)
        {
            int l = o.Left, t = o.Top, r = o.Right, b = o.Bottom;
            switch (h)
            {
                case 0: l = pt.X; t = pt.Y; break;
                case 1: t = pt.Y; break;
                case 2: r = pt.X; t = pt.Y; break;
                case 3: r = pt.X; break;
                case 4: r = pt.X; b = pt.Y; break;
                case 5: b = pt.Y; break;
                case 6: l = pt.X; b = pt.Y; break;
                case 7: l = pt.X; break;
            }
            l = Math.Max(0, Math.Min(l, this.Width)); r = Math.Max(0, Math.Min(r, this.Width));
            t = Math.Max(0, Math.Min(t, this.Height)); b = Math.Max(0, Math.Min(b, this.Height));
            return new Rectangle(Math.Min(l, r), Math.Min(t, b), Math.Abs(r - l), Math.Abs(b - t));
        }

        void DrawHandles(Graphics g)
        {
            foreach (var p in HandlePoints())
            {
                var hr = HandleRect(p);
                using (var b = new SolidBrush(C_GOLD)) g.FillRectangle(b, hr);
                using (var pen = new Pen(Color.FromArgb(180, C_DEEP), 1f)) g.DrawRectangle(pen, hr);
            }
        }

        void OnDown(object s, MouseEventArgs e)
        {
            // 右键取消：正在打字→取消这段字；已框选→退回未框选可重画；未框选→关闭
            if (e.Button == MouseButtons.Right)
            {
                if (_tb != null) { CancelText(); return; }
                if (_hasSel || _dragging)
                {
                    _dragging = _moving = _resizing = _annotating = false;
                    _drawing = null; _handle = -1;
                    _hasSel = false; _sel = Rectangle.Empty;
                    _tool = Tool.None; _annos.Clear(); _hover = 0;
                    this.Cursor = Cursors.Cross;
                    this.Invalidate();
                    return;
                }
                this.Close();
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            // 点击别处先把正在输入的文字落定
            if (_tb != null) CommitText();

            if (_hasSel)
            {
                int id = HitButton(e.Location);
                if (id != 0) { HandleButton(id); return; }

                // 颜色 / 粗细子条
                if (HandleStyleClick(e.Location)) return;

                // 未选工具时：拖角缩放 / 框内移动
                if (_tool == Tool.None)
                {
                    int hd = HitHandle(e.Location);
                    if (hd >= 0)
                    {
                        _resizing = true; _handle = hd; _selOrig = _sel;
                        return;
                    }
                    if (_sel.Contains(e.Location))
                    {
                        _moving = true; _dragOrigin = e.Location; _selOrig = _sel;
                        return;
                    }
                }
                // 文字工具：在点击处开一个输入框
                if (_tool == Tool.Text && _sel.Contains(e.Location))
                {
                    BeginText(e.Location);
                    return;
                }
                // 其它标注工具（箭头/直线/方框/马赛克）：拖动画形状
                if (_tool != Tool.None && _tool != Tool.Text && _sel.Contains(e.Location))
                {
                    _annotating = true;
                    _drawing = new Anno { Type = _tool, A = e.Location, B = e.Location, Color = _color, Width = _width };
                    return;
                }
                // 否则重新框选（清掉旧标注）
            }

            _dragging = true;
            _hasSel = false;
            _tool = Tool.None;
            _annos.Clear();
            _hover = 0;
            this.Cursor = Cursors.Cross;
            _start = e.Location;
            _sel = new Rectangle(e.Location, Size.Empty);
            this.Invalidate();
        }

        void OnMove(object s, MouseEventArgs e)
        {
            if (_dragging)
            {
                int x = Math.Min(_start.X, e.X);
                int y = Math.Min(_start.Y, e.Y);
                _sel = new Rectangle(x, y, Math.Abs(_start.X - e.X), Math.Abs(_start.Y - e.Y));
                this.Invalidate();
                return;
            }
            if (_moving)
            {
                var r = _selOrig;
                r.X = Math.Max(0, Math.Min(_selOrig.X + (e.X - _dragOrigin.X), this.Width - r.Width));
                r.Y = Math.Max(0, Math.Min(_selOrig.Y + (e.Y - _dragOrigin.Y), this.Height - r.Height));
                _sel = r; LayoutButtons(); this.Invalidate();
                return;
            }
            if (_resizing)
            {
                _sel = ResizeRect(_selOrig, _handle, e.Location); LayoutButtons(); this.Invalidate();
                return;
            }
            if (_annotating)
            {
                _drawing.B = e.Location;
                this.Invalidate();
                return;
            }
            if (_hasSel)
            {
                int id = HitButton(e.Location);
                if (id != 0) this.Cursor = Cursors.Hand;
                else if (_showStyle && _styleBar.Contains(e.Location)) this.Cursor = Cursors.Hand;
                else if (_tool == Tool.None)
                {
                    int hd = HitHandle(e.Location);
                    if (hd >= 0) this.Cursor = HandleCursor(hd);
                    else if (_sel.Contains(e.Location)) this.Cursor = Cursors.SizeAll;
                    else this.Cursor = Cursors.Cross;
                }
                else if (_tool == Tool.Text && _sel.Contains(e.Location)) this.Cursor = Cursors.IBeam;
                else this.Cursor = Cursors.Cross;
                if (id != _hover) { _hover = id; this.Invalidate(); }
            }
        }

        void OnUp(object s, MouseEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                if (_sel.Width >= 3 && _sel.Height >= 3) { _hasSel = true; LayoutButtons(); }
                else { _sel = Rectangle.Empty; _hasSel = false; }
                this.Invalidate();
                return;
            }
            if (_moving || _resizing)
            {
                _moving = false; _resizing = false; _handle = -1;
                LayoutButtons();
                this.Invalidate();
                return;
            }
            if (_annotating)
            {
                _annotating = false;
                int dx = _drawing.B.X - _drawing.A.X, dy = _drawing.B.Y - _drawing.A.Y;
                if (dx * dx + dy * dy >= 16) _annos.Add(_drawing);   // 太短的忽略
                _drawing = null;
                this.Invalidate();
            }
        }

        void HandleButton(int id)
        {
            switch (id)
            {
                case 1: _tool = (_tool == Tool.Arrow) ? Tool.None : Tool.Arrow; break;
                case 2: _tool = (_tool == Tool.Line) ? Tool.None : Tool.Line; break;
                case 3: _tool = (_tool == Tool.Rect) ? Tool.None : Tool.Rect; break;
                case 7: _tool = (_tool == Tool.Text) ? Tool.None : Tool.Text; break;
                case 8: _tool = (_tool == Tool.Mosaic) ? Tool.None : Tool.Mosaic; break;
                case 4: if (_annos.Count > 0) _annos.RemoveAt(_annos.Count - 1); break;
                case 9: SavePng(); return;
                case 5: this.Close(); return;
                case 6: Confirm(); return;
            }
            LayoutStyle();
            this.Invalidate();
        }

        void LayoutButtons()
        {
            const int GAP = 8;
            int totalW = BTN * _rects.Length;
            int bx = _sel.Right - totalW;
            int by = _sel.Bottom + GAP;
            if (bx < _sel.Left) bx = _sel.Left;
            if (by + BTN > this.Height) by = _sel.Bottom - BTN - GAP; // 收进框内
            if (bx + totalW > this.Width) bx = this.Width - totalW - 2;
            if (bx < 0) bx = 2;
            if (by < 0) by = 2;

            _bar = new Rectangle(bx, by, totalW, BTN);
            for (int i = 0; i < _rects.Length; i++)
                _rects[i] = new Rectangle(bx + i * BTN, by, BTN, BTN);
            LayoutStyle();
        }

        // 样式子条：颜色 + 粗细（选了可着色工具时显示）
        void LayoutStyle()
        {
            _showStyle = (_tool == Tool.Arrow || _tool == Tool.Line || _tool == Tool.Rect || _tool == Tool.Text);
            if (!_showStyle) return;
            _widthCount = (_tool == Tool.Text) ? 0 : WIDTHS.Length;
            int nColor = PALETTE.Length;
            int gap = (_widthCount > 0) ? 12 : 0;
            int w = 8 + nColor * SUB + gap + _widthCount * SUB + 8;
            int x = _bar.X;
            int y = _bar.Bottom + 6;
            if (y + SUB > this.Height) y = _bar.Y - SUB - 6;   // 下方放不下就放上方
            if (y < 0) y = 2;
            if (x + w > this.Width) x = this.Width - w - 2;
            if (x < 0) x = 2;
            _styleBar = new Rectangle(x, y, w, SUB);

            int cx = x + 8;
            for (int i = 0; i < nColor; i++) { _colorRects[i] = new Rectangle(cx, y, SUB, SUB); cx += SUB; }
            cx += gap;
            for (int i = 0; i < _widthCount; i++) { _widthRects[i] = new Rectangle(cx, y, SUB, SUB); cx += SUB; }
        }

        // 点样式子条：命中就设置颜色/粗细
        bool HandleStyleClick(Point p)
        {
            if (!_showStyle) return false;
            for (int i = 0; i < PALETTE.Length; i++)
                if (_colorRects[i].Contains(p)) { _color = PALETTE[i]; this.Invalidate(); return true; }
            for (int i = 0; i < _widthCount; i++)
                if (_widthRects[i].Contains(p)) { _width = WIDTHS[i]; this.Invalidate(); return true; }
            return false;
        }

        // ---- 文字标注输入（真控件，底色贴合背景，宽度自适应）----
        void BeginText(Point p)
        {
            CommitText();
            var tb = new TextBox();
            tb.BorderStyle = BorderStyle.None;
            tb.Multiline = false;
            tb.Font = AnnoFont;
            tb.ForeColor = _color;
            tb.Location = p;
            tb.Width = 24;   // 起始很窄，随打字增长
            this.Controls.Add(tb);
            tb.BackColor = SampleBg(new Rectangle(tb.Left, tb.Top, tb.Width, tb.Height));
            tb.TextChanged += (s, e) =>
            {
                int w = TextRenderer.MeasureText(tb.Text, AnnoFont).Width + 14;
                w = Math.Max(24, Math.Min(_sel.Right - tb.Left - 2, w));
                tb.Width = w;
                tb.BackColor = SampleBg(new Rectangle(tb.Left, tb.Top, tb.Width, tb.Height));
            };
            _tb = tb;
            _tb.Focus();
        }

        // 取一小块区域的平均色（背景纯色时看起来就像透明）
        Color SampleBg(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, _full.Width, _full.Height));
            if (r.Width <= 0 || r.Height <= 0) return Color.White;
            long tr = 0, tg = 0, tb = 0; int n = 0;
            int sx = Math.Max(1, r.Width / 20), sy = Math.Max(1, r.Height / 6);
            for (int y = r.Top; y < r.Bottom; y += sy)
                for (int x = r.Left; x < r.Right; x += sx)
                {
                    Color c = _full.GetPixel(x, y);
                    tr += c.R; tg += c.G; tb += c.B; n++;
                }
            if (n == 0) return Color.White;
            return Color.FromArgb((int)(tr / n), (int)(tg / n), (int)(tb / n));
        }

        void CommitText()
        {
            if (_tb == null) return;
            var tb = _tb; _tb = null;               // 先置空，避免重入
            string txt = tb.Text;
            Point pos = tb.Location;
            this.Controls.Remove(tb);
            tb.Dispose();
            if (!string.IsNullOrEmpty(txt) && txt.Trim().Length > 0)
                _annos.Add(new Anno { Type = Tool.Text, A = pos, Text = txt, Color = _color });
            this.Focus();
            this.Invalidate();
        }

        void CancelText()
        {
            if (_tb == null) return;
            var tb = _tb; _tb = null;
            this.Controls.Remove(tb);
            tb.Dispose();
            this.Focus();
            this.Invalidate();
        }

        // 裁剪选区并把标注烧进去，返回最终图（含马赛克/箭头/文字等）
        Bitmap RenderResult()
        {
            Rectangle r = _sel;
            r.Intersect(new Rectangle(0, 0, _full.Width, _full.Height));
            if (r.Width <= 0 || r.Height <= 0) return null;
            var crop = new Bitmap(r.Width, r.Height);
            using (Graphics g = Graphics.FromImage(crop))
            {
                g.DrawImage(_full, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(-r.X, -r.Y);
                foreach (var a in _annos) DrawAnno(g, a);
            }
            return crop;
        }

        void Confirm()
        {
            CommitText();   // 落定未回车的文字
            try
            {
                using (var bmp = RenderResult())
                    if (bmp != null) Clipboard.SetImage(bmp);
            }
            catch (Exception ex)
            {
                MessageBox.Show("复制失败: " + ex.Message, "截图工具");
            }
            this.Close();
        }

        void SavePng()
        {
            CommitText();
            using (var bmp = RenderResult())
            {
                if (bmp == null) { this.Close(); return; }
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Filter = "PNG 图片|*.png";
                    dlg.FileName = "AltSnip_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                    try { dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures); }
                    catch { }
                    if (dlg.ShowDialog() == DialogResult.OK)
                    {
                        try { bmp.Save(dlg.FileName, ImageFormat.Png); }
                        catch (Exception ex) { MessageBox.Show("保存失败: " + ex.Message, "截图工具"); return; }
                        this.Close();
                    }
                    // 取消保存则留在界面继续编辑
                }
            }
        }

        // ================= 绘制 =================

        protected override void OnPaint(PaintEventArgs e)
        {
            Render(e.Graphics);
        }

        public void Render(Graphics g)
        {
            g.DrawImageUnscaled(_full, 0, 0);
            using (var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                g.FillRectangle(dim, this.ClientRectangle);

            if (_sel.Width > 0 && _sel.Height > 0)
            {
                // 选区内还原清晰原图
                g.DrawImage(_full, _sel, _sel, GraphicsUnit.Pixel);
                // 极简细边框（暖金 1px）
                g.SmoothingMode = SmoothingMode.None;
                using (var pen = new Pen(C_GOLD, 1f))
                    g.DrawRectangle(pen, _sel.X, _sel.Y, _sel.Width - 1, _sel.Height - 1);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // 标注（裁进选区内绘制）
                var oldClip = g.Clip;
                g.SetClip(_sel);
                foreach (var a in _annos) DrawAnno(g, a);
                if (_annotating && _drawing != null) DrawAnno(g, _drawing);
                g.Clip = oldClip;

                // 尺寸仅在拖动时显示（精简）
                if (_dragging)
                {
                    string info = _sel.Width + " × " + _sel.Height;
                    using (var f = new Font("Segoe UI", 9f))
                    using (var bg = new SolidBrush(Color.FromArgb(210, C_DEEP)))
                    using (var fg = new SolidBrush(C_GOLD))
                    {
                        SizeF sz = g.MeasureString(info, f);
                        float tx = _sel.Left, ty = _sel.Top - sz.Height - 5;
                        if (ty < 2) ty = _sel.Top + 4;
                        g.FillRectangle(bg, tx, ty, sz.Width + 10, sz.Height + 2);
                        g.DrawString(info, f, fg, tx + 5, ty + 1);
                    }
                }

                // 控制点：未选标注工具、且非拖框中时显示，提示可移动/缩放
                if (_hasSel && _tool == Tool.None && !_dragging) DrawHandles(g);

                if (_hasSel) DrawToolbar(g);
            }
            else if (!_dragging)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                string tip = "拖动框选区域        Enter 复制        Esc 取消";
                using (var f = new Font("Microsoft YaHei", 11f))
                using (var bg = new SolidBrush(Color.FromArgb(200, C_DEEP)))
                using (var fg = new SolidBrush(C_TEXT))
                {
                    SizeF sz = g.MeasureString(tip, f);
                    float tx = (this.Width - sz.Width) / 2, ty = 44;
                    using (var p = Rounded(new Rectangle((int)(tx - 18), (int)(ty - 9), (int)sz.Width + 36, (int)sz.Height + 18), 10))
                        g.FillPath(bg, p);
                    g.DrawString(tip, f, fg, tx, ty);
                }
            }
        }

        // 极简工具条：无边框，半透明暖墨底 + 细笔画图标
        void DrawToolbar(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 柔和投影
            using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            using (var p = Rounded(new Rectangle(_bar.X, _bar.Y + 3, _bar.Width, _bar.Height), 10))
                g.FillPath(sh, p);
            // 底板（无描边）
            using (var bg = new SolidBrush(Color.FromArgb(205, C_DEEP)))
            using (var p = Rounded(_bar, 10))
                g.FillPath(bg, p);

            for (int i = 0; i < _rects.Length; i++)
            {
                int id = _ids[i];
                Rectangle c = _rects[i];
                bool active = (id == 1 && _tool == Tool.Arrow) || (id == 2 && _tool == Tool.Line)
                            || (id == 3 && _tool == Tool.Rect) || (id == 7 && _tool == Tool.Text)
                            || (id == 8 && _tool == Tool.Mosaic);
                bool hover = (_hover == id);
                if (active) Chip(g, c, C_GOLD, 46);
                else if (hover) Chip(g, c, C_TEXT, 26);

                Color ic = active ? C_GOLD : (hover ? C_TEXT : C_TEXT2);
                switch (id)
                {
                    case 1: DrawArrowIcon(g, c, ic); break;
                    case 2: DrawLineIcon(g, c, ic); break;
                    case 3: DrawRectIcon(g, c, ic); break;
                    case 7: DrawTextIcon(g, c, ic); break;
                    case 8: DrawMosaicIcon(g, c, ic); break;
                    case 4: DrawUndoIcon(g, c, _annos.Count == 0 ? Color.FromArgb(90, C_TEXT2) : ic); break;
                    case 9: DrawSaveIcon(g, c, ic); break;
                    case 5: DrawCross(g, c, hover ? C_TEXT : C_TEXT2); break;
                    case 6: DrawCheck(g, c, C_GOLD); break;
                }
            }

            if (_showStyle) DrawStyleBar(g);
        }

        // 颜色 + 粗细 子条
        void DrawStyleBar(Graphics g)
        {
            using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            using (var p = Rounded(new Rectangle(_styleBar.X, _styleBar.Y + 3, _styleBar.Width, _styleBar.Height), 9))
                g.FillPath(sh, p);
            using (var bg = new SolidBrush(Color.FromArgb(205, C_DEEP)))
            using (var p = Rounded(_styleBar, 9))
                g.FillPath(bg, p);

            // 颜色圆点
            for (int i = 0; i < PALETTE.Length; i++)
            {
                Rectangle c = _colorRects[i];
                int d = 16, ox = c.Left + (c.Width - d) / 2, oy = c.Top + (c.Height - d) / 2;
                using (var b = new SolidBrush(PALETTE[i]))
                    g.FillEllipse(b, ox, oy, d, d);
                if (PALETTE[i].GetBrightness() > 0.85f)   // 白色描个边免得看不见
                    using (var pen = new Pen(Color.FromArgb(90, C_TEXT2), 1f))
                        g.DrawEllipse(pen, ox, oy, d, d);
                if (_color.ToArgb() == PALETTE[i].ToArgb())  // 选中：金色外圈
                    using (var pen = new Pen(C_GOLD, 2f))
                        g.DrawEllipse(pen, ox - 3, oy - 3, d + 6, d + 6);
            }
            // 粗细圆点（大小递增）
            for (int i = 0; i < _widthCount; i++)
            {
                Rectangle c = _widthRects[i];
                int d = 6 + i * 5, ox = c.Left + (c.Width - d) / 2, oy = c.Top + (c.Height - d) / 2;
                bool sel = Math.Abs(_width - WIDTHS[i]) < 0.01f;
                using (var b = new SolidBrush(sel ? C_GOLD : C_TEXT2))
                    g.FillEllipse(b, ox, oy, d, d);
            }
        }

        void Chip(Graphics g, Rectangle cell, Color c, int alpha)
        {
            Rectangle r = Rectangle.Inflate(cell, -4, -4);
            using (var b = new SolidBrush(Color.FromArgb(alpha, c)))
            using (var p = Rounded(r, 8))
                g.FillPath(b, p);
        }

        // ---- 标注绘制 ----
        void DrawAnno(Graphics g, Anno a)
        {
            Color col = (a.Color.A == 0) ? C_ANNO : a.Color;   // 兼容未设色
            float wd = a.Width > 0 ? a.Width : ANNO_W;

            if (a.Type == Tool.Mosaic)
            {
                DrawMosaic(g, Rectangle.FromLTRB(Math.Min(a.A.X, a.B.X), Math.Min(a.A.Y, a.B.Y),
                                                 Math.Max(a.A.X, a.B.X), Math.Max(a.A.Y, a.B.Y)));
                return;
            }
            if (a.Type == Tool.Text)
            {
                if (!string.IsNullOrEmpty(a.Text))
                    using (var br = new SolidBrush(col))
                        g.DrawString(a.Text, AnnoFont, br, a.A.X, a.A.Y, StringFormat.GenericTypographic);
                return;
            }
            using (var pen = new Pen(col, wd))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                if (a.Type == Tool.Rect)
                {
                    int x = Math.Min(a.A.X, a.B.X), y = Math.Min(a.A.Y, a.B.Y);
                    g.DrawRectangle(pen, x, y, Math.Abs(a.A.X - a.B.X), Math.Abs(a.A.Y - a.B.Y));
                }
                else if (a.Type == Tool.Line)
                {
                    g.DrawLine(pen, a.A, a.B);
                }
                else if (a.Type == Tool.Arrow)
                {
                    g.DrawLine(pen, a.A, a.B);
                    double ang = Math.Atan2(a.B.Y - a.A.Y, a.B.X - a.A.X);
                    float len = 10 + wd * 2.2f;
                    double a1 = ang + Math.PI - 0.5, a2 = ang + Math.PI + 0.5;
                    g.DrawLine(pen, a.B, new PointF(a.B.X + (float)(Math.Cos(a1) * len), a.B.Y + (float)(Math.Sin(a1) * len)));
                    g.DrawLine(pen, a.B, new PointF(a.B.X + (float)(Math.Cos(a2) * len), a.B.Y + (float)(Math.Sin(a2) * len)));
                }
            }
        }

        // 马赛克：把区域缩小再放大，得到打码块（读取冻结原图，源坐标为屏幕坐标）
        void DrawMosaic(Graphics g, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, _full.Width, _full.Height));
            if (r.Width < 2 || r.Height < 2) return;
            int block = 10;
            int sw = Math.Max(1, r.Width / block), sh = Math.Max(1, r.Height / block);
            using (var small = new Bitmap(sw, sh))
            {
                using (var sg = Graphics.FromImage(small))
                {
                    sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    sg.DrawImage(_full, new Rectangle(0, 0, sw, sh), r, GraphicsUnit.Pixel);
                }
                var oldI = g.InterpolationMode; var oldP = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(small, r);
                g.InterpolationMode = oldI; g.PixelOffsetMode = oldP;
            }
        }

        // ---- 图标 ----
        static Pen IconPen(Color c, float w)
        {
            var pen = new Pen(c, w);
            pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
            return pen;
        }

        void DrawArrowIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.2f))
            {
                var t = new PointF(c.Left + 13, c.Bottom - 13);
                var h = new PointF(c.Right - 13, c.Top + 13);
                g.DrawLine(pen, t, h);
                double ang = Math.Atan2(h.Y - t.Y, h.X - t.X);
                float len = 7f;
                double a1 = ang + Math.PI - 0.5, a2 = ang + Math.PI + 0.5;
                g.DrawLine(pen, h, new PointF(h.X + (float)(Math.Cos(a1) * len), h.Y + (float)(Math.Sin(a1) * len)));
                g.DrawLine(pen, h, new PointF(h.X + (float)(Math.Cos(a2) * len), h.Y + (float)(Math.Sin(a2) * len)));
            }
        }

        void DrawLineIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.2f))
                g.DrawLine(pen, c.Left + 13, c.Bottom - 13, c.Right - 13, c.Top + 13);
        }

        void DrawRectIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.2f))
            using (var p = Rounded(new Rectangle(c.Left + 12, c.Top + 13, c.Width - 24, c.Height - 26), 3))
                g.DrawPath(pen, p);
        }

        void DrawTextIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.2f))
            {
                float cx = c.Left + c.Width / 2f;
                g.DrawLine(pen, c.Left + 12, c.Top + 14, c.Right - 12, c.Top + 14); // 顶横
                g.DrawLine(pen, cx, c.Top + 14, cx, c.Bottom - 13);                 // 竖
            }
        }

        void DrawMosaicIcon(Graphics g, Rectangle c, Color col)
        {
            float x0 = c.Left + 12, y0 = c.Top + 12, s = (c.Width - 24) / 3f;
            bool[] fill = { true, false, true, false, true, false, true, false, true };
            using (var b = new SolidBrush(col))
                for (int i = 0; i < 9; i++)
                    if (fill[i])
                        g.FillRectangle(b, x0 + (i % 3) * s, y0 + (i / 3) * s, s - 1.5f, s - 1.5f);
        }

        void DrawSaveIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.2f))
            {
                float cx = c.Left + c.Width / 2f, ty = c.Top + 11, by = c.Bottom - 16;
                g.DrawLine(pen, cx, ty, cx, by);            // 竖杆
                g.DrawLine(pen, cx, by, cx - 5, by - 5);    // 箭头（朝下）
                g.DrawLine(pen, cx, by, cx + 5, by - 5);
                g.DrawLine(pen, c.Left + 11, c.Bottom - 11, c.Right - 11, c.Bottom - 11); // 底托
            }
        }

        void DrawUndoIcon(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.2f))
            {
                var box = new RectangleF(c.Left + 13, c.Top + 14, c.Width - 26, c.Height - 26);
                g.DrawArc(pen, box.X, box.Y, box.Width, box.Height, 30, 280);
                // 左上开口处画个小箭头（逆时针回退）
                float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
                double ang = 30 * Math.PI / 180;
                var tip = new PointF(cx + (float)(Math.Cos(ang) * box.Width / 2f), cy + (float)(Math.Sin(ang) * box.Height / 2f));
                g.DrawLine(pen, tip, new PointF(tip.X - 5, tip.Y - 2));
                g.DrawLine(pen, tip, new PointF(tip.X + 1, tip.Y - 6));
            }
        }

        void DrawCross(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.3f))
            {
                int p = 15;
                g.DrawLine(pen, c.Left + p, c.Top + p, c.Right - p, c.Bottom - p);
                g.DrawLine(pen, c.Right - p, c.Top + p, c.Left + p, c.Bottom - p);
            }
        }

        void DrawCheck(Graphics g, Rectangle c, Color col)
        {
            using (var pen = IconPen(col, 2.6f))
            {
                var p1 = new PointF(c.Left + 13f, c.Top + 22f);
                var p2 = new PointF(c.Left + 18f, c.Top + 27f);
                var p3 = new PointF(c.Left + 29f, c.Top + 15f);
                g.DrawLines(pen, new[] { p1, p2, p3 });
            }
        }

        GraphicsPath Rounded(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // 隐藏开发用：把真实绘制渲染成 PNG，便于核对视觉。用法 Snip.exe --preview out.png [hoverId]
        internal static void SavePreview(string path, int hover)
        {
            int W = 560, H = 380;
            var bg = new Bitmap(W, H);
            using (var g = Graphics.FromImage(bg))
            {
                using (var br = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(48, 76, 120), Color.FromArgb(18, 26, 44), 45f))
                    g.FillRectangle(br, 0, 0, W, H);
                using (var b = new SolidBrush(Color.FromArgb(238, 238, 240)))
                    g.FillRectangle(b, 70, 70, 230, 130);
                using (var b = new SolidBrush(Color.FromArgb(150, 170, 210)))
                    g.FillRectangle(b, 320, 90, 170, 110);
            }
            var f = new OverlayForm(bg, new Rectangle(0, 0, W, H));
            f.Bounds = new Rectangle(0, 0, W, H);
            f._sel = new Rectangle(120, 95, 300, 175);
            f._hasSel = true;
            f._tool = Tool.Rect;
            f._color = PALETTE[3]; f._width = WIDTHS[1];
            f._annos.Add(new Anno { Type = Tool.Arrow, A = new Point(400, 250), B = new Point(255, 160), Color = PALETTE[0], Width = 4 });
            f._annos.Add(new Anno { Type = Tool.Rect, A = new Point(150, 118), B = new Point(240, 172), Color = PALETTE[3], Width = 4 });
            f._annos.Add(new Anno { Type = Tool.Mosaic, A = new Point(330, 100), B = new Point(410, 150) });
            f._annos.Add(new Anno { Type = Tool.Text, A = new Point(285, 250), Text = "看这里", Color = PALETTE[1] });
            f.LayoutButtons();
            f._hover = hover;
            using (var outBmp = new Bitmap(W, H))
            {
                using (var g = Graphics.FromImage(outBmp)) f.Render(g);
                outBmp.Save(path, ImageFormat.Png);
            }
            f.Dispose();
        }

        // ---- 演示动画（DemoGen 用）：设置状态并抓一帧 ----
        internal void DemoConfig(Rectangle sel, bool hasSel, bool dragging, Tool tool)
        {
            _sel = sel; _hasSel = hasSel; _dragging = dragging; _tool = tool;
            if (hasSel && !dragging) LayoutButtons();
        }
        internal void DemoStyle(Color c, float w) { _color = c; _width = w; if (_hasSel) LayoutButtons(); }
        internal void DemoHover(int id) { _hover = id; }
        internal List<Anno> DemoAnnos { get { return _annos; } }
        internal Bitmap DemoFrame()
        {
            var b = new Bitmap(this.Width, this.Height);
            using (var g = Graphics.FromImage(b)) { g.SmoothingMode = SmoothingMode.AntiAlias; Render(g); }
            return b;
        }
    }

}
