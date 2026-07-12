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
        const int WM_SYSKEYDOWN = 0x0104;
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
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                var kb = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                // 命中 A 键 + 当前 Alt 处于按下状态；忽略程序注入的假按键
                if (kb.vkCode == VK_A && (kb.flags & LLKHF_INJECTED) == 0
                    && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
                {
                    // 交给 UI 线程截图，并吞掉这个按键（微信等收不到）
                    if (!_capturing)
                        this.BeginInvoke((Action)StartCapture);
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
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
                    overlay.ShowDialog();
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
    enum Tool { None, Arrow, Line, Rect, Text }

    // 一条标注
    class Anno
    {
        public Tool Type;
        public Point A;
        public Point B;
        public string Text;
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

        // 工具条按钮：id 1箭头 2直线 3方框 7文字 4撤销 5取消 6确认
        int _hover = 0;
        Rectangle _bar = Rectangle.Empty;
        readonly int[] _ids = { 1, 2, 3, 7, 4, 5, 6 };
        readonly Rectangle[] _rects = new Rectangle[7];
        const int BTN = 42;

        // 文字标注：用真输入框（保证中文输入法可用），但把它背后的冻结原图
        // 画成自己的背景 => 看起来完全透明；配合红字、红光标。
        static readonly Font AnnoFont = new Font("Microsoft YaHei", 13.5f);
        ClearTextBox _tb = null;

        const int WM_CTLCOLOREDIT = 0x0133;
        [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr hdc, int color);
        [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int i);

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

        // 让文字输入框的字背景透明（不盖住我们画上去的原图背景），并把字设成红色
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CTLCOLOREDIT)
            {
                SetBkMode(m.WParam, 1);                 // TRANSPARENT
                int bgr = C_ANNO.R | (C_ANNO.G << 8) | (C_ANNO.B << 16);
                SetTextColor(m.WParam, bgr);
                m.Result = GetStockObject(5);           // NULL_BRUSH
                return;
            }
            base.WndProc(ref m);
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

        void OnDown(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            // 点击别处先把正在输入的文字落定
            if (_tb != null) CommitText();

            if (_hasSel)
            {
                int id = HitButton(e.Location);
                if (id != 0) { HandleButton(id); return; }

                // 文字工具：在点击处开一个输入框
                if (_tool == Tool.Text && _sel.Contains(e.Location))
                {
                    BeginText(e.Location);
                    return;
                }
                // 其它标注工具：拖动画形状
                if (_tool != Tool.None && _tool != Tool.Text && _sel.Contains(e.Location))
                {
                    _annotating = true;
                    _drawing = new Anno { Type = _tool, A = e.Location, B = e.Location };
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
                case 4: if (_annos.Count > 0) _annos.RemoveAt(_annos.Count - 1); break;
                case 5: this.Close(); return;
                case 6: Confirm(); return;
            }
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
        }

        // ---- 文字标注输入（真控件 + 透明背景 + 红光标）----
        void BeginText(Point p)
        {
            CommitText();
            _tb = new ClearTextBox();
            _tb.BorderStyle = BorderStyle.None;
            _tb.Multiline = false;
            _tb.Font = AnnoFont;
            _tb.ForeColor = C_ANNO;
            _tb.Location = p;
            _tb.Width = Math.Min(320, Math.Max(60, _sel.Right - p.X - 4));
            this.Controls.Add(_tb);

            // 把输入框背后的冻结原图截下来当它的背景 => 视觉上完全透明
            int h = _tb.Height;
            Rectangle rb = new Rectangle(p.X, p.Y, _tb.Width, h);
            rb.Intersect(new Rectangle(0, 0, _full.Width, _full.Height));
            if (rb.Width > 0 && rb.Height > 0)
            {
                var bg = new Bitmap(_tb.Width, h);
                using (var g = Graphics.FromImage(bg))
                    g.DrawImage(_full, new Rectangle(0, 0, rb.Width, rb.Height), rb, GraphicsUnit.Pixel);
                _tb.Bg = bg;
            }
            _tb.Focus();
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
                _annos.Add(new Anno { Type = Tool.Text, A = pos, Text = txt });
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

        void Confirm()
        {
            CommitText();   // 落定未回车的文字
            try
            {
                Rectangle r = _sel;
                r.Intersect(new Rectangle(0, 0, _full.Width, _full.Height));
                if (r.Width <= 0 || r.Height <= 0) { this.Close(); return; }
                using (Bitmap crop = new Bitmap(r.Width, r.Height))
                {
                    using (Graphics g = Graphics.FromImage(crop))
                    {
                        g.DrawImage(_full, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
                        // 把标注烧进图里（平移到裁剪坐标系）
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TranslateTransform(-r.X, -r.Y);
                        foreach (var a in _annos) DrawAnno(g, a);
                    }
                    Clipboard.SetImage(crop);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("复制失败: " + ex.Message, "截图工具");
            }
            this.Close();
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
                            || (id == 3 && _tool == Tool.Rect) || (id == 7 && _tool == Tool.Text);
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
                    case 4: DrawUndoIcon(g, c, _annos.Count == 0 ? Color.FromArgb(90, C_TEXT2) : ic); break;
                    case 5: DrawCross(g, c, hover ? C_TEXT : C_TEXT2); break;
                    case 6: DrawCheck(g, c, C_GOLD); break;
                }
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
            using (var pen = new Pen(C_ANNO, ANNO_W))
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
                    float len = 10 + ANNO_W * 2.2f;
                    double a1 = ang + Math.PI - 0.5, a2 = ang + Math.PI + 0.5;
                    g.DrawLine(pen, a.B, new PointF(a.B.X + (float)(Math.Cos(a1) * len), a.B.Y + (float)(Math.Sin(a1) * len)));
                    g.DrawLine(pen, a.B, new PointF(a.B.X + (float)(Math.Cos(a2) * len), a.B.Y + (float)(Math.Sin(a2) * len)));
                }
            }
            if (a.Type == Tool.Text && !string.IsNullOrEmpty(a.Text))
            {
                // 纯红字，无阴影，越简洁越好
                using (var br = new SolidBrush(C_ANNO))
                    g.DrawString(a.Text, AnnoFont, br, a.A.X, a.A.Y, StringFormat.GenericTypographic);
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
            f._tool = Tool.Text;
            f._annos.Add(new Anno { Type = Tool.Arrow, A = new Point(390, 250), B = new Point(250, 160) });
            f._annos.Add(new Anno { Type = Tool.Rect, A = new Point(150, 120), B = new Point(240, 175) });
            f._annos.Add(new Anno { Type = Tool.Text, A = new Point(300, 255), Text = "看这里" });
            f.LayoutButtons();
            f._hover = hover;
            using (var outBmp = new Bitmap(W, H))
            {
                using (var g = Graphics.FromImage(outBmp)) f.Render(g);
                outBmp.Save(path, ImageFormat.Png);
            }
            f.Dispose();
        }
    }

    // 透明输入框：因为截图是冻结静止的，把它背后那块原图画成自己的背景，
    // 看起来就完全透明；再配一个红色光标。真控件 => 中文输入法照常工作。
    class ClearTextBox : TextBox
    {
        public Bitmap Bg;

        [DllImport("user32.dll")] static extern bool CreateCaret(IntPtr hWnd, IntPtr hBitmap, int w, int h);
        [DllImport("user32.dll")] static extern bool ShowCaret(IntPtr hWnd);
        [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int color);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

        const int WM_ERASEBKGND = 0x0014;
        const int WM_SETFOCUS = 0x0007;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND && Bg != null)
            {
                using (var g = Graphics.FromHdc(m.WParam))
                    g.DrawImageUnscaled(Bg, 0, 0);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == WM_SETFOCUS)
            {
                // 红色光标：用一个 2px 宽的纯红位图当 caret
                var bmp = new Bitmap(2, Math.Max(2, this.Font.Height));
                using (var g = Graphics.FromImage(bmp))
                    g.Clear(Color.FromArgb(0xfa, 0x51, 0x51));
                IntPtr hb = bmp.GetHbitmap();
                CreateCaret(this.Handle, hb, 0, 0);
                ShowCaret(this.Handle);
                DeleteObject(hb);
                bmp.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Bg != null) { Bg.Dispose(); Bg = null; }
            base.Dispose(disposing);
        }
    }
}
