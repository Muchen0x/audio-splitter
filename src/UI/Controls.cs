﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using StereoSplitter.Devices;

namespace StereoSplitter
{
    /// <summary>
    /// 滚轮路由: 下拉框/数字框自己不吃滚轮, 而是把消息转给外层的 AutoScroll 容器。
    /// <para>以前这里是直接 return —— 值确实没被改, 但滚动链也在控件上断了: 鼠标滑过卡片上的
    /// 下拉或延迟框时, 输出卡片列表就滚不动, 得先把鼠标挪到空白处。这里改成转发,
    /// 控件自身依旧不改值, 滚动却照常发生。</para>
    /// </summary>
    static class WheelRouting
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public const int WM_MOUSEWHEEL = 0x020A;

        /// <summary>
        /// 把滚轮转给最近一个「开了 AutoScroll 且当前真的能滚」的容器。
        /// <para>找到就滚它并返回 true; 找不到返回 false (调用方自行决定怎么处理)。</para>
        /// </summary>
        public static bool ForwardToScroller(Control self, Message m)
        {
            for (Control p = self.Parent; p != null; p = p.Parent)
            {
                if (!p.IsHandleCreated) continue;
                ScrollableControl sc = p as ScrollableControl;
                if (sc != null && sc.AutoScroll && sc.VerticalScroll.Visible)
                {
                    SendMessage(p.Handle, m.Msg, m.WParam, m.LParam);
                    return true;
                }
            }
            return false;
        }

        /// <summary>把滚轮转给最近的「AutoScroll 且能滚」的容器; 找不到就交给直接父窗口继续冒泡。</summary>
        public static void Forward(Control self, Message m)
        {
            if (ForwardToScroller(self, m)) return;

            Control nearest = null;
            for (Control p = self.Parent; p != null; p = p.Parent)
                if (p.IsHandleCreated) { nearest = p; break; }

            if (nearest != null)
                SendMessage(nearest.Handle, m.Msg, m.WParam, m.LParam);   // 交给父窗口按默认规则冒泡
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(System.Drawing.Point p);

        /// <summary>
        /// 这条滚轮消息是否落在一个打开的下拉弹层上 (弹层自己的窗口, 或光标压在弹层上)。
        /// <para><b>为什么需要它</b>: VolumeWheelRouter / WheelByCursorRouter 两个全局过滤器按
        /// 「光标下的控件」找滚轮的去向, 而弹层 (<see cref="ComboPopup"/>) 是独立窗口,
        /// 不在主窗体控件树里 —— 弹层开着时, 过滤器会拿消息坐标在弹层<b>底下</b>找到音量滑条 /
        /// 卡片区, 把滚轮改投出去并吞掉: 弹层列表收不到, 底下的卡片却滚走了 ("滚轮穿透")。
        /// 两个过滤器开头都先查这一条, 命中就放行 —— 消息会按系统路由到达弹层或下拉本体,
        /// 那两条路各自都把滚轮交给弹层列表。</para>
        /// </summary>
        public static bool OverOpenComboPopup(Message m)
        {
            // 消息的目标窗口本身就是弹层/弹层列表 (Win10+ 默认按光标位置投递)
            if (IsPopupWindow(m.HWnd)) return true;
            // 消息被投给了焦点窗口 —— 用消息自带的屏幕坐标判断光标是否压在弹层上
            long lp = m.LParam.ToInt64();
            var at = new System.Drawing.Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            return IsPopupWindow(WindowFromPoint(at));
        }

        static bool IsPopupWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            for (Control c = Control.FromChildHandle(hwnd); c != null; c = c.Parent)
                if (c is ComboPopup || c is ComboRowList) return true;
            return false;
        }
    }

    /// <summary>
    /// 焦点虚线框抑制: WinForms 的按钮 / 复选框 / 下拉框拿到焦点就画一圈虚线焦点框,
    /// 鼠标点一下也留一个, 满屏虚线框很难看。
    /// <para>做法是截掉系统"显示焦点框"的 UI 状态请求 (WM_UPDATEUISTATE / UISF_HIDEFOCUS),
    /// 让 ShowFocusCues 永远返回 false。键盘用户看的是控件自己的高亮/边框, 不需要这圈虚线。</para>
    /// </summary>
    static class FocusCue
    {
        public const int WM_UPDATEUISTATE = 0x0129;
        const int UISF_HIDEFOCUS = 0x0001;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>把"要显示焦点框"的请求改写成"继续隐藏", 再交给基类处理。</summary>
        public static void Suppress(ref Message m)
        {
            long wp = m.WParam.ToInt64();
            int action = (int)(wp & 0xFFFF);          // 低 16 位 = 要改哪一位
            int state = (int)((wp >> 16) & 0xFFFF);   // 高 16 位 = 改成的值
            if (action == UISF_HIDEFOCUS && (state & UISF_HIDEFOCUS) == 0)
                m.WParam = new IntPtr((UISF_HIDEFOCUS << 16) | UISF_HIDEFOCUS);
        }

        /// <summary>句柄刚建好时先置成隐藏, 免得第一次点击前就带着焦点框。</summary>
        public static void HideNow(Control c)
        {
            if (c == null || !c.IsHandleCreated) return;
            SendMessage(c.Handle, WM_UPDATEUISTATE, new IntPtr((UISF_HIDEFOCUS << 16) | UISF_HIDEFOCUS), IntPtr.Zero);
        }
    }

    /// <summary>
    /// 双缓冲的 FlowLayoutPanel (卡片区)。
    /// <para>原生 FlowLayoutPanel 的 AutoScroll 在滚动/缩放时是一格一格擦背景再画子控件,
    /// 卡片一多就能看到整片闪白; 开双缓冲后整帧一次性贴上去, 滚动和缩放明显顺。</para>
    /// </summary>
    class BufferedFlowPanel : FlowLayoutPanel
    {
        public BufferedFlowPanel()
        {
            // DoubleBuffered 是 protected, 只能在子类里开
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
    }

    /// <summary>点完不留虚线焦点框的按钮。</summary>
    class NoCueButton : Button
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FocusCue.HideNow(this);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == FocusCue.WM_UPDATEUISTATE) FocusCue.Suppress(ref m);
            base.WndProc(ref m);
        }
    }

    /// <summary><see cref="RoundButton"/> 可以画的自绘符号 (不依赖字体)。</summary>
    enum ButtonGlyph { None, Plus, Nut, SquarePlus, Cross, Refresh }

    /// <summary>
    /// 自绘圆角矩形按钮。
    ///
    /// <para><b>为什么另起一个类而不给 NoCueButton 加个开关</b>：原生 Button 的外框与圆角由系统
    /// 窗口类绘制（FlatStyle 只有 System/Standard/Flat 三档，圆角做不了，底色也只能取系统色）。
    /// 要"圆角 + 按状态换底色"就必须接管整块绘制（<c>UserPaint</c>）。做成开关的话，
    /// "哪些按钮是圆角"会散落在各处调用点上；分成两个类，看类型就知道。</para>
    ///
    /// <para><b>只接管绘制</b>：Click、DialogResult、Enabled、键盘 Space/Enter 这些语义全部沿用
    /// <see cref="Button"/>，所以替换原生按钮不会改变任何交互行为。</para>
    /// </summary>
    class RoundButton : Button
    {
        static readonly Color DefaultFill = Color.FromArgb(0, 120, 215);

        Color fill = DefaultFill;
        Color fillDisabled = Color.Empty;
        Color borderColor = Color.Empty;
        Color textColor = Color.White;
        int radius = 6;
        bool hot, pressed;
        ButtonGlyph glyph = ButtonGlyph.None;
        int glyphRadius = 8;
        float glyphThickness = 2.8f;
        bool outline;

        public RoundButton()
        {
            // UserPaint: 不走系统按钮绘制。少了它, 下面的 SetStyle 与 OnPaint 都不会生效
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            // 系统按钮类仍会在自绘内容之外补一圈"主题边框"(125% 缩放下最明显, 深灰 1px),
            // 圆角外那四个方角正好把它露出来 —— 看着就是"两个边框"。
            // Flat + BorderSize=0 是唯一能让 native 尽量不画边框的组合 (FlatStyle 只有三档,
            // System/Standard 都自带边框)。但光靠它不够 (实测仍在), 真正的解法是下面的
            // ShapeRound: 把窗口本身裁成圆角, 方角不在窗口内, 谁画都露不出来。
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
        }

        /// <summary>常态底色。悬停/按下的派生色由它算出来，不用再给两套。</summary>
        public Color FillColor
        {
            get { return fill; }
            set { if (fill != value) { fill = value; Invalidate(); } }
        }

        /// <summary>
        /// 禁用态底色。<c>Color.Empty</c> (默认) = 按 <see cref="FillColor"/> 与白混合，即普通的
        /// "不可点"观感；过渡态（启动中 / 正在停止）需要显式给色，否则会被混成接近白底、文字发虚。
        /// </summary>
        public Color FillDisabled
        {
            get { return fillDisabled; }
            set { if (fillDisabled != value) { fillDisabled = value; Invalidate(); } }
        }

        public Color TextColor
        {
            get { return textColor; }
            set { if (textColor != value) { textColor = value; Invalidate(); } }
        }

        /// <summary>
        /// 描边色。<c>Color.Empty</c> (默认) = 按底色自动加深一档；需要与旁边的控件（比如
        /// <see cref="RoundedCombo"/> 的 #CBD0D8 边框）看起来是一对时，显式给同一个值。
        /// </summary>
        public Color BorderColor
        {
            get { return borderColor; }
            set { if (borderColor != value) { borderColor = value; Invalidate(); } }
        }

        /// <summary>圆角半径。默认 6，与 <see cref="RoundedCombo"/> 本体同一个值。</summary>
        public int CornerRadius
        {
            get { return radius; }
            set { if (radius != value) { radius = value; Invalidate(); } }
        }

        /// <summary>
        /// 自绘符号。<c>None</c> 时照常画 <see cref="Control.Text"/>。
        /// <para>图标不走字体: 字体画出的「+」粗细是字号的函数, 想加粗只能放大字号 —— 而 30px 高的
        /// 按钮里放到 20pt 就已经顶到上下边缘被裁。自绘两条线则尺寸与线宽各自独立。</para>
        /// </summary>
        public ButtonGlyph Glyph
        {
            get { return glyph; }
            set { if (glyph != value) { glyph = value; Invalidate(); } }
        }

        /// <summary>符号的臂长 (从中心到端点, 像素)。默认 8 → 16×16 的十字。</summary>
        public int GlyphRadius
        {
            get { return glyphRadius; }
            set { if (glyphRadius != value) { glyphRadius = value; Invalidate(); } }
        }

        /// <summary>符号线宽 (像素)。默认 2.8。</summary>
        public float GlyphThickness
        {
            get { return glyphThickness; }
            set { if (glyphThickness != value) { glyphThickness = value; Invalidate(); } }
        }

        /// <summary>
        /// 描边样式：底色留白，只让边框和文字着色。
        /// <para>悬停/按下的着色也换成往边框色里混（而不是混黑）—— 白底混黑会发灰，
        /// 与蓝色或红色的描边放在一起像"脏了"。</para>
        /// </summary>
        public bool Outline
        {
            get { return outline; }
            set { if (outline != value) { outline = value; Invalidate(); } }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FocusCue.HideNow(this);          // 自绘本来就不画虚线框, 这句是防系统在别处补一刀
            ShapeRound();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ShapeRound();
        }

        /// <summary>
        /// 把窗口本身裁成圆角，与 <see cref="RoundedCombo"/> 同一手法。
        /// <para>为什么非裁不可：系统按钮类会在客户区最外一圈画自己的边框（主题色，约 #C8C8C8），
        /// 我们的圆角在四角向内收，那一小段就露出来了 —— 表现就是"按钮有两个边框"。
        /// 只靠 <c>FlatStyle.Flat + BorderSize=0</c> 压不住（实测仍在），矩形填充盖它也盖不住
        /// （它画在我们之后）。裁了之后方角不属于这个窗口，谁画都露不出来。</para>
        /// <para><b>Region 要内缩 1px</b>：那圈 native 边框正好在客户区第 0 列上，Region 与客户区
        /// 边界重合的话它仍在裁剪区内。代价是按钮的可见尺寸小 2px，描边相应画在 (2,2) 处。</para>
        /// </summary>
        void ShapeRound()
        {
            if (Width <= 4 || Height <= 4) return;
            using (var path = UiDraw.RoundRect(new Rectangle(1, 1, Width - 2, Height - 2), radius))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == FocusCue.WM_UPDATEUISTATE) FocusCue.Suppress(ref m);
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // 圆角外那几个角要补成父容器底色, 否则会露出方角白块 (与 RoundedCombo.OnPaint 同一手法)
            if (Parent != null)
                using (var b = new SolidBrush(Parent.BackColor))
                    g.FillRectangle(b, ClientRectangle);

            Color c = Enabled
                ? fill
                : (fillDisabled.IsEmpty ? Blend(fill, Color.White, 0.55f) : fillDisabled);
            if (Enabled && (pressed || hot))
            {
                // 描边按钮往边框色里混 (浅蓝/浅红底), 实心按钮照旧混黑压暗
                Color tint = (outline && !borderColor.IsEmpty) ? borderColor : Color.Black;
                c = Blend(c, tint, pressed ? 0.16f : 0.07f);
            }

            // 描边: 显式给了就跟它走 (白底按钮靠这条与下拉框对齐), 否则按底色加深一档 ——
            // 纯色块在白底上边界发糊, 浅色的「+」尤其明显
            Color edge = borderColor.IsEmpty ? Blend(c, Color.Black, 0.12f) : borderColor;
            float edgeFade = outline ? 0.30f : 0.45f;   // 描边时边框就是主体, 别淡到看不见
            if (!Enabled && !borderColor.IsEmpty) edge = Blend(edge, Color.White, edgeFade);

            // 底色铺满矩形, 圆角交给 Region (见 ShapeRound) —— 不能再用抗锯齿的圆角填充:
            // 边缘那圈半透明像素被 Region 硬裁之后会变成暗边 (实测 #C8C8C8), 比原来的问题更显眼。
            // 圆角处的台阶由紧随其后的描边盖住, 看不出锯齿。
            if (Width > 4 && Height > 4)
            {
                using (var b = new SolidBrush(c))
                    g.FillRectangle(b, ClientRectangle);

                // 描边再往里 1px (即 (2,2)): Region 已经把客户区第 0 列裁掉了, 描边贴着 (1,1) 画的话
                // 外侧半个像素会被裁掉, 线又细又淡
                using (var edgePath = UiDraw.RoundRect(new Rectangle(2, 2, Width - 4, Height - 4), Math.Max(1, radius - 1)))
                using (var p = new Pen(edge))
                    g.DrawPath(p, edgePath);
            }

            // 文字色: 只有在"自动淡化成的禁用态"里才跟着变浅。显式给了 FillDisabled 的
            // (实心过渡态 / 图标按钮) 说明配色是特意定的, 再淡化会把深色字也洗白。
            // 描边样式没有"禁用底色", 淡化幅度要跟边框一致 (0.30), 否则边淡字深看着别扭。
            Color ink = (Enabled || !fillDisabled.IsEmpty)
                ? textColor
                : Blend(textColor, Color.White, outline ? 0.30f : 0.20f);

            if (glyph != ButtonGlyph.None)
            {
                DrawGlyph(g, ink);
                return;
            }

            TextRenderer.DrawText(g, Text, Font, ClientRectangle,
                ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis);
        }

        /// <summary>
        /// 画图标符号。中心取 (W-1)/2 —— 像素坐标里 0..W-1 的中点是 (W-1)/2，
        /// 用 W/2 会让奇数宽的线整体右下偏半像素。
        /// </summary>
        void DrawGlyph(Graphics g, Color ink)
        {
            float cx = (Width - 1) / 2f;
            float cy = (Height - 1) / 2f;

            using (var p = new Pen(ink, glyphThickness))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                if (glyph == ButtonGlyph.Plus)
                {
                    g.DrawLine(p, cx - glyphRadius, cy, cx + glyphRadius, cy);
                    g.DrawLine(p, cx, cy - glyphRadius, cx, cy + glyphRadius);
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = false; pressed = false; Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (pressed) { pressed = false; Invalidate(); }
            base.OnMouseUp(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            hot = false; pressed = false; Invalidate();
            base.OnEnabledChanged(e);
        }

        /// <summary>按比例混色（不做 alpha 混合，免得底色透出来）。t=0 返回 a，t=1 返回 b。</summary>
        static Color Blend(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }
    }

    /// <summary>点完不留虚线焦点框的复选框 (原生 CheckBox 点一下就画一圈虚线)。</summary>
    class NoCueCheckBox : CheckBox
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FocusCue.HideNow(this);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == FocusCue.WM_UPDATEUISTATE) FocusCue.Suppress(ref m);
            base.WndProc(ref m);
        }
    }

    /// <summary>焦点交还窗体 / 输入框回车的公共动作。</summary>
    static class UiFocus
    {
        /// <summary>
        /// 把焦点交还给整个窗体。
        /// <para>数字框/文本框失去焦点时才会把编辑中的文字提交成值, 所以"回车提交并失焦"
        /// 和"点空白处焦点回到窗口"都走这一条路。</para>
        /// </summary>
        public static void ParkOnForm(Control c)
        {
            Form f = (c != null && !c.IsDisposed) ? c.FindForm() : null;
            if (f == null || f.IsDisposed) return;
            try
            {
                f.ActiveControl = null;                  // 当前控件失焦 → 编辑值提交
                if (f.CanFocus && !f.Focused) f.Focus(); // 焦点落到窗体本身
            }
            catch { }
        }
    }

    /// <summary>
    /// 点到空白处 (标签 / 面板 / 卡片底色 / 窗体背景) 时把焦点交回整个窗口。
    /// <para>不这么做的话: 在延迟框里输完数字, 点一下卡片空白, 那个框还带着焦点,
    /// 之后随便敲个方向键就又把值改回去了。点到真控件 (输入框/下拉框/按钮/滑条) 则不动,
    /// 它们自己会抢焦点。</para>
    /// </summary>
    class FocusParkRouter : IMessageFilter
    {
        const int WM_LBUTTONDOWN = 0x0201;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_LBUTTONDOWN) return false;
            Control hit = Control.FromChildHandle(m.HWnd);
            if (hit == null) return false;              // 下拉列表等非控件窗口: 不介入

            for (Control c = hit; c != null; c = c.Parent)
                if (IsInputLike(c)) return false;

            Form f = hit.FindForm();
            if (f == null || f.IsDisposed || !f.IsHandleCreated) return false;
            // 延后一拍: 先让这次点击按正常规则落地 (该抢焦点的抢完), 再把焦点收回窗体
            try { f.BeginInvoke((Action)(() => UiFocus.ParkOnForm(f))); }
            catch { }
            return false;                               // 消息照常派发, 点击本身不受影响
        }

        /// <summary>真正需要键盘焦点的控件 (点它们不抢焦点, 否则没法输入/选字)。</summary>
        static bool IsInputLike(Control c)
        {
            if (c is TextBoxBase) return true;   // 日志框: 点进去要能选文字/复制
            if (c is RoundedCombo) return true;  // 下拉框: 展开/键盘导航要用焦点, 点完自己还焦点
            if (c is ComboBox) return true;      // 原生下拉框 (当前未使用, 留作防御)
            if (c is UpDownBase) return true;
            if (c is ButtonBase) return true;
            if (c is ListBox) return true;
            return false;
        }
    }

    // 鼠标滚轮不改值的 NumericUpDown (只允许手动输入/上下按钮, 每次 ±Increment)
    class NoWheelNumeric : NumericUpDown
    {
        SpinButtonsHook spin;

        public NoWheelNumeric()
        {
            // 原生的凹陷立体边框不再要: 圆角描边由外壳 <see cref="RoundNumericShell"/> 画
            // (与 RoundedCombo 同一套框色), 箭头区的立体框由 SpinButtonsHook 自绘顶掉。
            BorderStyle = BorderStyle.None;
        }

        /// <summary>
        /// WS_CLIPSIBLINGS: 单位标签与数字框的窗口有一角重叠 (见 <see cref="RoundNumericShell.Recalc"/>) ——
        /// 没有这个样式, 数字框每次重绘都会把重叠区里的「ms」左半截抹掉。
        /// 有了它, 本窗口重绘时自动避开 z 序在上的兄弟窗口。
        /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x04000000;   // WS_CLIPSIBLINGS
                return cp;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // 不调 base: 滚轮对数字框一律无效 (误滚一下就把延迟拖偏, 很难发现)
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FocusCue.HideNow(this);
            AttachSpinHook();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            DetachSpinHook();
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DetachSpinHook();
                if (repeatTimer != null)
                {
                    repeatTimer.Stop();
                    repeatTimer.Dispose();
                    repeatTimer = null;
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// 接管上下按钮的点击 (见 <see cref="SpinButtonsHook"/>)。
        /// <para>UpDownBase 内部有两个子控件: UpDownEdit (文本框) 与 UpDownButtons (箭头区)。
        /// 原生逻辑是"点箭头 → 先把焦点交给文本框再改值", 于是文本框会亮起插入符、整段
        /// 内容被选中, 看着像进了编辑态。我们改为自己改值、把点击吞掉, 焦点和插入符都不动。</para>
        /// <para>挂载时机: OnHandleCreated 里箭头区的句柄往往还没建好 (UpDownBase 在
        /// 自己的 OnHandleCreated 里才去建), 所以这里既接 HandleCreated, 也留一次
        /// 延后重试, 双保险。</para>
        /// </summary>
        void AttachSpinHook()
        {
            if (spin != null || IsDisposed) return;
            foreach (Control c in Controls)
            {
                if (c is TextBox) continue;          // 跳过 UpDownEdit
                if (c.IsHandleCreated) { HookSpin(c); return; }
                c.HandleCreated += OnSpinHandleCreated;
                return;
            }
            // 子控件还没进 Controls: 本轮消息处理完再试一次 (只重试一次, 免得自投递打转)
            if (spinRetried || !IsHandleCreated || IsDisposed) return;
            spinRetried = true;
            try { BeginInvoke(new Action(AttachSpinHook)); } catch { }
        }

        bool spinRetried;

        void OnSpinHandleCreated(object sender, EventArgs e)
        {
            Control c = sender as Control;
            if (c == null) return;
            c.HandleCreated -= OnSpinHandleCreated;
            if (spin == null && !IsDisposed) HookSpin(c);
        }

        void HookSpin(Control buttons)
        {
            spin = new SpinButtonsHook(this, buttons);
            spin.AssignHandle(buttons.Handle);
        }

        void DetachSpinHook()
        {
            if (spin == null) return;
            try { spin.ReleaseHandle(); } catch { }
            spin = null;
        }

        /// <summary>
        /// 按箭头方向走一步 (点数由 <see cref="NumericUpDown.Increment"/> 决定)。
        /// <para>先把正在输入的文字落成值, 否则"输入 18 还没提交就点箭头"会从旧值算起。</para>
        /// </summary>
        internal void StepArrow(int dir)
        {
            if (!IsHandleCreated || !Enabled) return;
            ValidateEditText();
            decimal v = Value + dir * Increment;
            if (v < Minimum) v = Minimum;
            else if (v > Maximum) v = Maximum;
            Value = v;
        }

        /// <summary>按住箭头连发: 首拍等 400ms, 之后每 120ms 一步 (与原生手感接近)。</summary>
        internal void BeginRepeat(int dir)
        {
            if (repeatTimer == null)
            {
                repeatTimer = new System.Windows.Forms.Timer { Interval = 120 };
                repeatTimer.Tick += (s, e) =>
                {
                    // 松开按钮就收尾 (鼠标移出后按键状态仍然可查, 不依赖 WM_MOUSELEAVE)
                    if (Control.MouseButtons != MouseButtons.Left) { EndRepeat(); return; }
                    if (repeatTimer.Interval != 120)
                    {
                        repeatTimer.Interval = 120;   // 首拍之后转为连发节奏
                        return;
                    }
                    StepArrow(repeatDir);
                };
            }
            repeatDir = dir;
            repeatTimer.Interval = 400;   // 首拍慢: 点一下只走一步, 不会因为抖动走两步
            repeatTimer.Start();
        }

        internal void EndRepeat()
        {
            if (repeatTimer == null) return;
            repeatTimer.Stop();
            repeatTimer.Interval = 400;   // 下一轮重新从"首拍"开始
        }

        System.Windows.Forms.Timer repeatTimer;
        int repeatDir;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // 回车 = 提交 + 失焦: 数字框是"失去焦点才把编辑中的文字变成值",
            // 所以这里直接把焦点交回窗体, 值顺带落定 (再敲键盘也不会改到它)。
            if (e.KeyCode == Keys.Enter && !e.Alt && !e.Control)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                UiFocus.ParkOnForm(this);
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WheelRouting.WM_MOUSEWHEEL)
            {
                WheelRouting.Forward(this, m);
                return;
            }
            if (m.Msg == FocusCue.WM_UPDATEUISTATE) FocusCue.Suppress(ref m);
            base.WndProc(ref m);
        }

        /// <summary>
        /// 箭头区的窗口过程: 吞掉左键按下/抬起, 自己走一步, 不让基类把焦点挪进文本框。
        /// <para>代价是原生那点"按下去变暗"的视觉反馈没了 —— 值当场变了, 反馈其实更直接。
        /// 连发由 <see cref="BeginRepeat"/> 补上, 所以按住箭头仍然能连续走。</para>
        /// </summary>
        sealed class SpinButtonsHook : NativeWindow
        {
            const int WM_PAINT = 0x000F;
            const int WM_ERASEBKGND = 0x0014;
            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_MOUSEMOVE = 0x0200;
            const int WM_LBUTTONUP = 0x0202;
            const int WM_LBUTTONDBLCLK = 0x0203;
            const int WM_RBUTTONDOWN = 0x0204;
            const int WM_RBUTTONDBLCLK = 0x0206;
            const int WM_CAPTURECHANGED = 0x0215;
            const int WM_MOUSELEAVE = 0x02A3;

            readonly NoWheelNumeric owner;
            readonly Control host;
            int hoverDir, pressedDir;
            bool leaveTracked;

            public SpinButtonsHook(NoWheelNumeric owner, Control host)
            {
                this.owner = owner;
                this.host = host;
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_PAINT)
                {
                    PaintArrows(ref m);
                    return;
                }
                if (m.Msg == WM_ERASEBKGND)
                {
                    // 底色在 PaintArrows 里自己铺, 不要系统再擦一遍 (防闪)
                    m.Result = (IntPtr)1;
                    return;
                }
                if (m.Msg == WM_MOUSEMOVE)
                {
                    int d = DirOf(m.LParam);
                    if (d != hoverDir) { hoverDir = d; host.Invalidate(); }
                    TrackLeave(m.HWnd);
                }
                else if (m.Msg == WM_MOUSELEAVE)
                {
                    leaveTracked = false;
                    if (hoverDir != 0) { hoverDir = 0; host.Invalidate(); }
                }
                else if (m.Msg == WM_LBUTTONDOWN)
                {
                    int dir = DirOf(m.LParam);
                    if (dir != 0)
                    {
                        owner.StepArrow(dir);
                        owner.BeginRepeat(dir);
                        pressedDir = dir;
                        host.Invalidate();
                        // 若焦点原本就在文本框 (例如刚手动输完一个数), 点箭头时也收走:
                        // 点箭头只该改数字, 不该留着插入符/选区在闪。
                        if (owner.ContainsFocus) UiFocus.ParkOnForm(owner);
                        return;      // 吞掉: 基类不会再 SetFocus 到文本框
                    }
                }
                else if (m.Msg == WM_LBUTTONDBLCLK)
                {
                    // 快速连点时, 每一对点击的**第二击**系统发的是 DBLCLK 而不是 DOWN
                    // (序列: DOWN,UP,DBLCLK,UP)。所以它代表一次真实点击, 必须照样走一步 ——
                    // 上一轮把它整个吞掉, 结果偶数次点击全丢, 数字只走一半, 明显不跟手。
                    // 仍然不调 base: 那正是当初把焦点塞进 UpDownEdit 的元凶。
                    int dir = DirOf(m.LParam);
                    if (dir != 0) owner.StepArrow(dir);
                    return;
                }
                else if (m.Msg == WM_RBUTTONDBLCLK)
                {
                    return;          // 右键双击同样别把焦点挪进文本框
                }
                else if (m.Msg == WM_LBUTTONUP || m.Msg == WM_CAPTURECHANGED || m.Msg == WM_RBUTTONDOWN)
                {
                    if (pressedDir != 0) { pressedDir = 0; host.Invalidate(); }
                    owner.EndRepeat();
                }
                base.WndProc(ref m);
            }

            /// <summary>
            /// 箭头自绘: 底色 + 上下两条圆头短线, 与 RoundedCombo 的下拉箭头同一笔画。
            /// <para>原生 UpDownButtons 自带一圈立体边框和 3D 箭头, 与界面其余扁平控件放不到一起;
            /// 悬停/按下的反馈由本类自己的 hoverDir/pressedDir 状态补上 (原生那套按下去变暗
            /// 被吞掉点击后就没了)。</para>
            /// </summary>
            void PaintArrows(ref Message m)
            {
                var ps = new PAINTSTRUCT();
                IntPtr hdc = BeginPaint(m.HWnd, ref ps);
                if (hdc == IntPtr.Zero) return;
                try
                {
                    using (var g = Graphics.FromHdc(hdc))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        // 底色跟外壳走: 禁用时外壳铺浅灰, 箭头区必须一致, 不能留一块白
                        Color back = owner.Enabled ? Color.White : Color.FromArgb(244, 245, 247);
                        using (var b = new SolidBrush(back))
                            g.FillRectangle(b, 0, 0, host.Width, host.Height);

                        float cx = host.Width / 2f;
                        Color ink = Color.FromArgb(95, 94, 90);      // 与喇叭/下拉箭头同一墨色
                        Color act = Color.FromArgb(44, 44, 42);
                        Color off = Color.FromArgb(178, 178, 178);
                        float topY = host.Height * 0.25f, botY = host.Height * 0.75f;
                        DrawChevron(g, cx, topY, 1f, !owner.Enabled ? off
                            : (pressedDir == 1 || hoverDir == 1) ? act : ink);
                        DrawChevron(g, cx, botY, -1f, !owner.Enabled ? off
                            : (pressedDir == -1 || hoverDir == -1) ? act : ink);
                    }
                }
                finally { EndPaint(m.HWnd, ref ps); }
                m.Result = IntPtr.Zero;
            }

            /// <summary>「^」/「v」两条圆头短线 (dir=1 朝上, -1 朝下)。</summary>
            static void DrawChevron(Graphics g, float cx, float cy, float dir, Color ink)
            {
                using (var p = new Pen(ink, 1.6f))
                {
                    p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    g.DrawLine(p, cx - 4f, cy + 2.2f * dir, cx, cy - 2.2f * dir);
                    g.DrawLine(p, cx, cy - 2.2f * dir, cx + 4f, cy + 2.2f * dir);
                }
            }

            /// <summary>悬停出了箭头区要退色 —— 系统只发一次 WM_MOUSELEAVE, 得先申请跟踪。</summary>
            void TrackLeave(IntPtr hwnd)
            {
                if (leaveTracked) return;
                leaveTracked = true;
                var tme = new TRACKMOUSEEVENT
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(TRACKMOUSEEVENT)),
                    dwFlags = 2,        // TME_LEAVE
                    hwndTrack = hwnd
                };
                TrackMouseEvent(ref tme);
            }

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT ps);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            struct PAINTSTRUCT
            {
                public IntPtr hdc;
                public int fErase;
                public int rcLeft, rcTop, rcRight, rcBottom;
                public int fRestore;
                public int fIncUpdate;
                [System.Runtime.InteropServices.MarshalAs(
                    System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)]
                public byte[] rgbReserved;
            }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            struct TRACKMOUSEEVENT
            {
                public int cbSize;
                public int dwFlags;
                public IntPtr hwndTrack;
                public int dwHoverTime;
            }

            /// <summary>上半是 +1, 下半是 −1; 落在边界外返回 0 (不处理)。</summary>
            int DirOf(IntPtr lParam)
            {
                int y = (short)((long)lParam >> 16);
                int h = host.ClientSize.Height;
                if (y < 0 || y >= h || h <= 0) return 0;
                return y < h / 2 ? 1 : -1;
            }
        }
    }

    /// <summary>
    /// 圆角数字框外壳: <see cref="NoWheelNumeric"/> 是原生控件, 自己画不了圆角描边 ——
    /// 外壳负责按 <see cref="RoundedCombo"/> 同一套框色画圆角边框, 数字框嵌在 1px 内衬里,
    /// 并把它的窗口裁成圆角 (与 RoundButton.ShapeRound 同一手法), 方角才不会盖住边框弧线。
    /// <para>可以带文字 (<c>new RoundNumericShell(nud, "延迟")</c>) 和单位后缀
    /// (<c>new RoundNumericShell(nud, "延迟", "ms")</c>): 文字、数字框、单位、上下箭头
    /// 按 [文字][数字][单位][按钮] 的顺序紧靠着包进同一个圆角矩形 ——
    /// 整组控件看起来是一个完整的框, 而不是几件散件。</para>
    /// <para>上下箭头由外壳自绘成两条圆头短线 (原生 UpDownButtons 压成 0×0), 交互也在
    /// 外壳的鼠标事件里: 悬停/按下变深, 只重画箭头矩形 —— 之前原生按钮自绘 hover 与
    /// 钩子的自绘争抢重绘, 鼠标在按钮上一扫就闪; 现在外壳是双缓冲的, 闪不了。</para>
    /// </summary>
    class RoundNumericShell : Panel
    {
        static readonly Color BorderNormal = Color.FromArgb(203, 208, 216);   // 与 RoundedCombo/SoftCard 同框色
        static readonly Color BorderHot = Color.FromArgb(168, 176, 188);
        static readonly Color BackDisabled = Color.FromArgb(244, 245, 247);
        static readonly Color TextDisabled = Color.FromArgb(150, 154, 160);
        const int CornerRadius = 6;
        const int PadX = 7;          // 文字距左缘 (与 RoundedCombo.TextPadX 对齐)
        const int GapCapInner = 5;   // 文字与数字框的间隔
        const int GapNumUnit = 4;    // 数字与单位的间隔
        const int GapUnitArrow = 5;  // 单位与箭头区的间隔
        const int ArrowW = 20;       // 上下箭头区宽度
        const int SpinStrip = 16;    // 数字框内部给 (已藏) 原生按钮留的条宽, 见 Recalc

        public readonly NoWheelNumeric Inner;
        readonly Label caption;      // 框内的文字 (可空)
        readonly Label unit;         // 框内的单位后缀 (可空, 如 "ms")
        bool hot;
        int hoverDir, pressedDir;    // 箭头的悬停/按下状态 (1=上, -1=下, 0=不在箭头上)
        int arrowX;                  // 箭头区左缘 (客户区坐标, Recalc 时定)

        public RoundNumericShell(NoWheelNumeric inner) : this(inner, null, null) { }

        public RoundNumericShell(NoWheelNumeric inner, string captionText) : this(inner, captionText, null) { }

        public RoundNumericShell(NoWheelNumeric inner, string captionText, string unitText)
        {
            Inner = inner;
            // 数字右对齐: 默认 Left 时数字贴左缘, 与右边的上下箭头/单位隔出一大段空白,
            // 单位看起来不像跟数字是一组。贴右之后整组读作 [延迟 …… 0⇅ms]。
            inner.TextAlign = HorizontalAlignment.Right;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.ResizeRedraw, true);
            inner.BackColor = Color.White;
            Controls.Add(inner);
            if (captionText != null)
            {
                caption = new Label
                {
                    Text = captionText,
                    AutoSize = true,
                    BackColor = Color.Transparent   // 让外壳的圆角填充透出来
                };
                Controls.Add(caption);
            }
            if (unitText != null)
            {
                unit = new Label
                {
                    Text = unitText,
                    AutoSize = true,
                    BackColor = Color.Transparent,
                    ForeColor = Color.FromArgb(120, 126, 136)   // 单位弱化成辅色 (与卡片行标签同色)
                };
                Controls.Add(unit);
            }
            Size = new Size(150, Inner.Height + 2);
            // 鼠标几乎全落在 Inner 上 (外壳只剩箭头区和内衬), 边框悬停跟踪得挂在它身上;
            // 移进数字框时外壳收不到 MouseMove, 顺手把箭头的悬停态清掉
            inner.MouseEnter += (s, e) => { hot = true; Invalidate(); };
            inner.MouseLeave += (s, e) => { hot = false; Invalidate(); };
            inner.MouseMove += (s, e) => { if (hoverDir != 0) { hoverDir = 0; RefreshArrow(); } };
            inner.EnabledChanged += (s, e) =>
            {
                inner.BackColor = inner.Enabled ? Color.White : BackDisabled;
                if (caption != null) caption.ForeColor = inner.Enabled ? SystemColors.ControlText : TextDisabled;
                if (unit != null) unit.ForeColor = inner.Enabled ? UnitColor : TextDisabled;
                Invalidate();
            };
            // 字体是环境继承的: 挂进父级后才定, caption 宽度跟着变, 定了之后要重排;
            // 数值位数变化 (0→10→100→1000) 时数字区跟着伸缩, 用 BeginInvoke 避开
            // ValueChanged 处理中途改布局的重入
            FontChanged += (s, e) => { if (IsHandleCreated) BeginInvoke((Action)Recalc); };
            Inner.ValueChanged += (s, e) => { if (IsHandleCreated) BeginInvoke((Action)Recalc); };
        }

        /// <summary>单位后缀的常态色 (与 OutputCard 行标签同色, 禁用时换灰)。</summary>
        static readonly Color UnitColor = Color.FromArgb(120, 126, 136);

        /// <summary>
        /// 按内容重排: 外壳尺寸、文字/数字框/单位/箭头区的位置一次到位。
    /// <para>顺序 (用户指定): [文字][数字][单位][上下箭头], 彼此紧靠 ——
    /// 数字区宽度按当前值量出 (位数变了才伸缩), 数字右对齐后正好贴住单位;
    /// 没有按最大值预留的空位, 也就没有「延迟 …… 0」那种大空隙。</para>
        /// </summary>
        void Recalc()
        {
            if (IsDisposed) return;
            int innerH = Inner.Height;
            int x = 1;
            if (caption != null)
            {
                caption.Location = new Point(PadX, (Height - caption.PreferredHeight) / 2);
                x = PadX + caption.PreferredWidth + GapCapInner;
            }
            int numberW = TextRenderer.MeasureText(Inner.Value.ToString("0"), Inner.Font).Width + 6;
            // 数字框窗口比文字区宽出一条 SpinStrip: 基类布局会把编辑框压到「客户区-按钮宽」,
            // 与它抢 bounds 是打不赢的 (非托管侧直接摆回来, 实测拉锯到 StackOverflow) ——
            // 干脆把这条宽度算进窗口: 编辑框正好铺到文字右缘, 数字右对齐后紧贴单位;
            // 剩下那条 SpinStrip 是被藏掉的原生按钮占的空白, 看不见也点不到
            PlaceInner(x, numberW + SpinStrip, innerH);
            int unitX = x + numberW + GapNumUnit;
            if (unit != null)
            {
                unit.Location = new Point(unitX, (Height - unit.PreferredHeight) / 2);
                unitX += unit.PreferredWidth;
                // 单位标签与数字框的窗口有一角重叠 (数字框窗口比文字区宽出一条按钮条),
                // 必须让标签在 z 序之上, 数字框重绘才盖不到它 (配合 NoWheelNumeric
                // 的 WS_CLIPSIBLINGS)
                unit.BringToFront();
            }
            arrowX = unitX + GapUnitArrow;
            Size = new Size(arrowX + ArrowW + 1, innerH + 2);
            Invalidate();
        }

        /// <summary>
        /// 数字框定位到数字区。原生 UpDownButtons (数字框内部右侧那条立体按钮) 压成 0×0 藏掉:
        /// 它不藏, 就会一直占着数字框右缘, 箭头没法按「数字→单位→按钮」的顺序排到单位后面;
        /// 压成 0×0 后它收不到任何鼠标事件, 箭头改由外壳自绘 (见 OnPaint)。
        /// <para>NumericUpDown 在 Resize 时会重新摆它, 所以每次重排后都要再压一次。</para>
        /// </summary>
        void PlaceInner(int x, int w, int h)
        {
            Inner.SetBounds(x, 1, w, h);
            Region old = Inner.Region;
            Inner.Region = null;
            if (old != null) old.Dispose();
            EnforceSpinHidden();
        }

        /// <summary>
        /// 把原生 UpDownButtons (数字框内部右侧那条立体按钮) 藏起来 —— <b>只藏可见性</b>。
        /// <para><b>为什么不动它的 bounds</b>: 数字框基类会用非托管侧的排布把编辑框压回
        /// 「客户区-按钮宽」、把按钮摆回原位, 我们改 bounds 它就摆回去, 无穷拉锯
        /// (实测 StackOverflow / 不停重绘)。Visible 却没人碰 —— 藏一次就稳住。
        /// 按钮条占用的那条宽度的处理见 <see cref="Recalc"/> 的注释。</para>
        /// </summary>
        void EnforceSpinHidden()
        {
            if (IsDisposed) return;
            foreach (Control c in Inner.Controls)
                if (c.GetType().Name == "UpDownButtons" && c.Visible) c.Visible = false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 不能在句柄刚建时就重排: 此时数字框/编辑框/原生按钮的句柄多半还没建,
            // 摆下去的位置会被数字框随后 OnHandleCreated 里的直接摆位冲掉。
            // 推迟到消息循环 (所有句柄就绪、数字框摆完) 之后再重排一次。
            BeginInvoke((Action)Recalc);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // 圆角外四个小角补成父容器底色 (与 SoftCard.OnPaint 同一手法)
            Color outside = Parent != null ? Parent.BackColor : SystemColors.Control;
            using (var b = new SolidBrush(outside)) g.FillRectangle(b, ClientRectangle);

            using (var path = UiDraw.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius))
            {
                using (var fill = new SolidBrush(Inner.Enabled ? Color.White : BackDisabled))
                    g.FillPath(fill, path);
                using (var pen = new Pen(Inner.Enabled && hot ? BorderHot : BorderNormal))
                    g.DrawPath(pen, path);
            }

            // 原生上下按钮可能在句柄创建后被数字框自己摆回来, 每次绘制前压一道保险
            EnforceSpinHidden();

            // 箭头区: 两条圆头短线, 与 RoundedCombo 下拉箭头同一笔画; 悬停/按下变深。
            // 这里只管画, 状态变化时 RefreshArrow() 只让箭头矩形失效 —— 外壳是双缓冲的,
            // 整框不会跟着重绘, 原生按钮那种"hover 一闪一闪"不存在了。
            using (var b = new SolidBrush(Inner.Enabled ? Color.White : BackDisabled))
                g.FillRectangle(b, arrowX, 1, Width - arrowX - 1, Height - 2);
            Color off = Color.FromArgb(178, 178, 178);
            Color ink = Color.FromArgb(95, 94, 90);
            Color act = Color.FromArgb(44, 44, 42);
            float cx = arrowX + ArrowW / 2f;
            float topY = Height * 0.25f, botY = Height * 0.75f;
            DrawChevron(g, cx, topY, 1f, !Inner.Enabled ? off
                : (pressedDir == 1 || hoverDir == 1) ? act : ink);
            DrawChevron(g, cx, botY, -1f, !Inner.Enabled ? off
                : (pressedDir == -1 || hoverDir == -1) ? act : ink);
        }

        /// <summary>「^」/「v」两条圆头短线 (dir=1 朝上, -1 朝下), 与旧钩子画的同一笔画。</summary>
        static void DrawChevron(Graphics g, float cx, float cy, float dir, Color inkColor)
        {
            using (var p = new Pen(inkColor, 1.6f))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(p, cx - 4f, cy + 2.2f * dir, cx, cy - 2.2f * dir);
                g.DrawLine(p, cx, cy - 2.2f * dir, cx + 4f, cy + 2.2f * dir);
            }
        }

        /// <summary>(x, y) 是否落在箭头区, 及其半区方向; 区外返回 0 (不处理)。</summary>
        int DirAt(int x, int y)
        {
            if (x < arrowX || x >= Width - 1 || y < 1 || y > Height - 2) return 0;
            return y < Height / 2 ? 1 : -1;
        }

        /// <summary>只让箭头矩形失效 —— 整框 (数字/单位) 不参与, 这是防闪的关键。</summary>
        void RefreshArrow()
        {
            Invalidate(new Rectangle(arrowX, 0, Width - arrowX, Height));
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hot = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hot = false;
            if (hoverDir != 0) hoverDir = 0;
            Invalidate();          // 边框退热 + 箭头退色, 一次整框重绘足够 (离开是低频事件)
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int d = DirAt(e.X, e.Y);
            if (d != hoverDir) { hoverDir = d; RefreshArrow(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Inner.Enabled) return;
            int d = DirAt(e.X, e.Y);
            if (d == 0) return;
            hoverDir = d;
            pressedDir = d;
            Inner.StepArrow(d);
            Inner.BeginRepeat(d);
            RefreshArrow();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (pressedDir != 0) { pressedDir = 0; Inner.EndRepeat(); RefreshArrow(); }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (pressedDir != 0) { pressedDir = 0; Inner.EndRepeat(); RefreshArrow(); }
        }
    }

    // 不接受焦点的 SplitContainer: 点击/拖动分隔条不产生焦点虚线框
    class NoFocusSplitContainer : SplitContainer
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetFocus();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SetFocus(IntPtr hWnd);

        IntPtr restoreFocus = IntPtr.Zero;

        protected override void Select(bool directed, bool forward)
        {
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style &= ~0x00010000; // WS_TABSTOP: 去掉样式, 焦点虚线框无从绘制
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0201)                       // WM_LBUTTONDOWN: child 能收到, 此刻记录旧焦点 (Focus 尚未发生)
            {
                restoreFocus = GetFocus();
                base.WndProc(ref m);
                return;
            }
            if (m.Msg == 0x0007)                       // WM_SETFOCUS: 焦点被抢到分隔条 → 立即还回
            {
                IntPtr target = (restoreFocus != IntPtr.Zero && restoreFocus != Handle) ? restoreFocus : IntPtr.Zero;
                if (target == IntPtr.Zero)
                {
                    Form f = FindForm();
                    if (f != null) target = f.Handle;  // 兜底还给窗体
                }
                if (target != IntPtr.Zero && target != Handle) SetFocus(target);
                return;
            }
            if (m.Msg == 0x0021)                       // WM_MOUSEACTIVATE (顶层才收得到, 保留无害)
            {
                m.Result = (IntPtr)3;                  // MA_NOACTIVATE
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// 滚轮按「鼠标位置」重定向。
    /// <para>Windows 把 WM_MOUSEWHEEL 投递到**焦点窗口**, 不是鼠标下的窗口: 点过卡片上的
    /// 下拉框/滑条后焦点留在卡片区, 这时把鼠标移到日志框上滚轮, 滚的却是卡片区;
    /// 反过来(焦点在日志框、鼠标在卡片区)也一样 —— 表现为"滚一个区域, 另一个区域在动"。</para>
    /// <para>这里只在「鼠标下控件」与「消息目标控件」**分属不同面板**(SplitterPanel)时改写目标,
    /// 同一面板内一律不干预, 免得打乱滑条/数字框自己往卡片区转发的既有规则
    /// (<see cref="WheelRouting"/>)。</para>
    /// </summary>
    class WheelByCursorRouter : IMessageFilter
    {
        const int WM_MOUSEWHEEL = 0x020A;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(Point p);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            // 下拉弹层开着时滚轮归弹层列表, 过滤器一律放行 (不然会按消息坐标在弹层
            // 底下找到滑条/列表, 把滚轮改投出去 —— 弹层收不到, 底下却滚了)
            if (WheelRouting.OverOpenComboPopup(m)) return false;

            // 消息自带屏幕坐标 (投递那一刻的位置), 比读 Cursor.Position 可靠
            long lp = m.LParam.ToInt64();
            Point at = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));

            Control under = Control.FromChildHandle(WindowFromPoint(at));
            if (under == null || under.IsDisposed || !under.Visible) return false;
            if (under.Handle == m.HWnd) return false;          // 目标本来就是鼠标下这个控件

            // 同一面板 → 不干预 (滑条该滚列表还是该调音量, 交给 VolumeWheelRouter 判断)
            if (PanelOf(under) == PanelOf(Control.FromChildHandle(m.HWnd))) return false;

            SendMessage(under.Handle, m.Msg, m.WParam, m.LParam);
            return true;                                       // 已改投, 原消息不再派发
        }

        static Control PanelOf(Control c)
        {
            for (Control p = c; p != null; p = p.Parent)
                if (p is SplitterPanel) return p;
            return null;
        }
    }

    /// <summary>
    /// 光标停在音量滑条上时的滚轮去向。
    /// <para>滑条刻意不抢焦点 (一抢焦点 AutoScroll 容器就会滚一下, 卡片列表会抖),
    /// 所以它收不到 WM_MOUSEWHEEL —— 这里按光标位置自己找控件。</para>
    /// <para>规则: **卡片列表正在滚动时, 滚轮只滚列表, 绝不改音量** —— 拖着列表往下划,
    /// 鼠标扫过某张卡片的音量条时音量不会莫名其妙跳 5%; 列表没东西可滚时(不显示竖滚动条)
    /// 才回落到原来的滚轮调音量, 免得白滚一场。</para>
    /// </summary>
    class VolumeWheelRouter : IMessageFilter
    {
        const int WM_MOUSEWHEEL = 0x020A;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            // 下拉弹层开着时滚轮归弹层列表, 过滤器一律放行 (否则会按消息坐标在弹层底下
            // 找到音量滑条, 把滚轮改投给卡片区 —— 弹层收不到, 底下的卡片却滚走了)
            if (WheelRouting.OverOpenComboPopup(m)) return false;

            // 命中判定用消息自带的屏幕坐标 (就是滚轮真正发生的位置), 比读光标更可靠:
            // 光标可能被别的程序挪走, 而这条消息的坐标是投递那一刻定下的。
            long lp = m.LParam.ToInt64();
            Point at = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            VolumeSlider slider = VolumeSlider.HitTestScreen(at);
            if (slider == null) slider = VolumeSlider.HitTestScreen(Cursor.Position);
            if (slider == null) return false;

            // 卡片列表能滚 → 这一格滚列表, 滑条值不动 (吞掉消息, 免得又滚一次)
            if (WheelRouting.ForwardToScroller(slider, m)) return true;

            // 列表没得滚 → 保持原行为: 滚轮调这一格音量
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            return slider.ApplyWheelDelta(delta);
        }
    }

    /// <summary>
    /// 设置页的分组卡片: 白底 + 圆角描边。
    /// <para>刻意照抄 <see cref="OutputCardControl"/> 的外框画法 (同一边框色、同一圆角半径),
    /// 让设置页与主界面看起来是同一套控件, 而不是另开一套风格。圆角外的小方块用窗体底色补齐,
    /// 否则四个角会露出白边。</para>
    /// </summary>
    class SoftCard : Panel
    {
        static readonly Color BorderColor = Color.FromArgb(203, 208, 216);
        const int CornerRadius = 8;

        public SoftCard()
        {
            BackColor = Color.White;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            Color outside = Parent != null ? Parent.BackColor : BackColor;
            using (var outsideBrush = new SolidBrush(outside))
                g.FillRectangle(outsideBrush, ClientRectangle);

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = UiDraw.RoundRect(rect, CornerRadius))
            {
                using (var fill = new SolidBrush(BackColor))
                    g.FillPath(fill, path);
                using (var pen = new Pen(BorderColor))
                    g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>
    /// 图标按钮 (无边框、透明底、悬停铺浅底): 设置=六角螺母, 添加设备=方框加号, 移除卡片=×。
    /// <para>不用字体图标 (Segoe MDL2 在精简版/老系统上未必存在, 缺字时会画成方块),
    /// 也不用文字 "⚙" (不同字体渲染差异大, 有的字体画成黑白两色 emoji)。图形全部照
    /// 用户给的 SVG 用路径直接画, 线条粗细折算成像素, 20~30px 下都按"能读清"取整。</para>
    /// <para>没有边框: 这些按钮周围都是内容控件 (下拉/滑条/卡片), 再带一圈边框会让
    /// 行里"框"比内容还多; 悬停浅底已经足够提示可点。</para>
    /// <para>已知怪癖 (旧齿轮时代就有): DrawToBitmap (WM_PRINT) 拍不到这个控件 ——
    /// OnPaint 正常执行、裁剪区完整, 但输出不落在目标位图上, uidump 的 icons 模式里
    /// 这一格是空的。实屏 (CopyFromScreen) 与真实运行不受影响; 截它只能用实屏。</para>
    /// </summary>
    class GearButton : Control
    {
        static readonly Color IconColor = Color.FromArgb(95, 94, 90);
        static readonly Color IconActive = Color.FromArgb(44, 44, 42);
        static readonly Color HoverBack = Color.FromArgb(234, 234, 234);
        static readonly Color PressBack = Color.FromArgb(216, 216, 216);

        bool hot, down;
        ButtonGlyph glyph = ButtonGlyph.Nut;

        /// <summary>按钮上的图形 (默认螺母 = 设置)。</summary>
        public ButtonGlyph Glyph
        {
            get { return glyph; }
            set { if (glyph == value) return; glyph = value; Invalidate(); }
        }

        public GearButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Size = new Size(28, 28);
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { if (down) { down = false; Invalidate(); } base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) { hot = false; down = false; } Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            Color back = Parent != null ? Parent.BackColor : SystemColors.Control;
            using (var b = new SolidBrush(back)) g.FillRectangle(b, ClientRectangle);

            if (Enabled && (hot || down))
            {
                using (var path = UiDraw.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 6))
                using (var fill = new SolidBrush(down ? PressBack : HoverBack))
                    g.FillPath(fill, path);
            }

            float cx = (Width - 1) / 2f;
            float cy = (Height - 1) / 2f + (down ? 1f : 0f);
            Color icon = Enabled ? ((hot || down) ? IconActive : IconColor) : Color.FromArgb(170, 170, 170);

            switch (glyph)
            {
                case ButtonGlyph.Nut:
                    // 设置: 六角螺母 —— 六边形一圈墙 + 中心一个圆环 (照用户给的 SVG 描的)。
                    // 原图坐标: 六角对宽 824×903 (k = 24/903), 墙 85 (≈2.3px), 圆环外径 170.7 内径 85.3。
                    DrawNut(g, cx, cy, Math.Min(Width, Height) * 0.0266f, icon,
                        (hot || down) ? (down ? PressBack : HoverBack) : back);
                    break;

                case ButtonGlyph.SquarePlus:
                    // 「添加设备」: 方框 + 加号。原图线条都是方框的 ~11%: 30×30 按钮里
                    // 框画成 16×16、线宽 1.8px, 加号臂长 ±5.5。
                    using (var p = new Pen(icon, 1.8f))
                    {
                        g.DrawRectangle(p, cx - 8f, cy - 8f, 16f, 16f);
                        g.DrawLine(p, cx - 5.5f, cy, cx + 5.5f, cy);
                        g.DrawLine(p, cx, cy - 5.5f, cx, cy + 5.5f);
                    }
                    break;

                case ButtonGlyph.Cross:
                    // 移除卡片: 一个干净的 "×" (用户给的 SVG 就是两条 45° 短线)。
                    // 20×20 按钮里臂长 ±4.5、线宽 1.7px, 圆头。
                    using (var p = new Pen(icon, 1.7f))
                    {
                        p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        g.DrawLine(p, cx - 4.5f, cy - 4.5f, cx + 4.5f, cy + 4.5f);
                        g.DrawLine(p, cx - 4.5f, cy + 4.5f, cx + 4.5f, cy - 4.5f);
                    }
                    break;

                case ButtonGlyph.Refresh:
                    // 「刷新设备」: 圆环 + 右上角实心直角旗标 (照用户给的 SVG 描的)。
                    // 原图 1024 视图: 圆环外径 382 内径 287 (中线 334.5, 笔宽 95.6),
                    // 旗标是被斜边(过圆心)切开的直角三角形, 直角顶在 (889,459)-(889,125)。
                    // 22px 下: 半径 7.18、笔宽 2.05、旗标顶点见 DrawRefresh。
                    DrawRefresh(g, cx, cy, Math.Min(Width, Height) / 22f, icon);
                    break;
            }
        }

        /// <summary>
        /// 六角螺母: 与喇叭同一套"外轮廓填墨色 / 内轮廓填底色"的画法, 墙厚不靠笔宽,
        /// 靠两个多边形之间的距离。上下是平的、左右是尖的 —— 螺母躺倒的样子。
        /// </summary>
        static void DrawNut(Graphics g, float cx, float cy, float k, Color icon, Color back)
        {
            var outer = new PointF[]
            {
                new PointF(1.23f, -13.0f), new PointF(11.87f, -7.56f),
                new PointF(11.87f,  7.56f), new PointF(0f,     13.0f),
                new PointF(-11.87f,  7.56f), new PointF(-11.87f, -7.56f),
            };
            var inner = new PointF[]
            {
                new PointF(1.23f, -10.87f), new PointF(9.41f, -5.43f),
                new PointF(9.41f,   5.43f), new PointF(0f,    10.87f),
                new PointF(-9.41f,   5.43f), new PointF(-9.41f, -5.43f),
            };
            using (var fill = new SolidBrush(icon))
            {
                for (int i = 0; i < outer.Length; i++)
                    outer[i] = new PointF(cx + outer[i].X * k, cy + outer[i].Y * k);
                for (int i = 0; i < inner.Length; i++)
                    inner[i] = new PointF(cx + inner[i].X * k, cy + inner[i].Y * k);
                g.FillPolygon(fill, outer);
            }
            using (var fill = new SolidBrush(back))
                g.FillPolygon(fill, inner);

            // 中心圆环: 外圆墨色, 内圆底色。原图外径 170.7/内径 85.3 是 1024 视图的原始值,
            // 要按六角高 903 → 本坐标系高 26 的比例归一 (170.67*26/903 ≈ 4.91) ——
            // 直接乘 k 会得到 127px 的巨圆, 墨色圆再被底色圆盖住, 整个按钮等于被刷白。
            float rOut = 4.91f * k, rIn = 2.46f * k;
            using (var fill = new SolidBrush(icon))
                g.FillEllipse(fill, cx - rOut, cy - rOut, rOut * 2, rOut * 2);
            using (var fill = new SolidBrush(back))
                g.FillEllipse(fill, cx - rIn, cy - rIn, rIn * 2, rIn * 2);
        }

        /// <summary>
        /// 刷新图标: 圆环弧 + 右上角实心直角旗标。坐标按 22×22 折算 (s = 按钮短边 / 22)。
        /// <para>原图环形从 -45°(右上对角) 逆时针绕过左/下/右, 在 17° 收口 —— 缺口正好被旗标
        /// 填满, 弧的 -45° 端与旗标斜边(过圆心的 45° 线)无缝相接。GDI 的角度顺时针为正,
        /// 所以这段弧 = <c>DrawArc(start=17°, sweep=298°)</c>。</para>
        /// </summary>
        static void DrawRefresh(Graphics g, float cx, float cy, float s, Color icon)
        {
            const float r = 7.18f, penW = 2.05f;
            var rect = new RectangleF(cx - r * s, cy - r * s, r * 2 * s, r * 2 * s);
            using (var pen = new Pen(icon, penW * s))
                g.DrawArc(pen, rect, 17f, 298f);

            // 旗标: 直角三角形, 斜边过圆心 (原图顶点 (889,459)(889,125)(775,240) — 斜边与
            // 内圆 -45° 交点共线, 这里按 22px 折算后省略与斜边共线的中间点)。
            var flag = new PointF[]
            {
                new PointF(cx + 0.91f * s,  cy - 1.13f * s),   // 近圆心 (斜边下端)
                new PointF(cx + 8.08f * s,  cy - 1.13f * s),   // 右下 (直角)
                new PointF(cx + 8.08f * s,  cy - 8.32f * s),   // 右上 (直角)
            };
            using (var fill = new SolidBrush(icon))
                g.FillPolygon(fill, flag);
        }
    }


    /// <summary>
    /// 喇叭图标按钮: 顶替「音量」文字标签 + 独立静音勾选框。点一下静音, 再点恢复。
    /// <para>图标有三种样子 (照用户给的 SVG 描的, 描边风格):</para>
    /// <para>· 带两道声波 = 正常出声;  · 喇叭 + × = 已静音 (点的);  · 裸喇叭 = 音量为 0。
    /// "音量为 0"和"被静音"是两件不同的事: 前者是滑条拖到底的自然结果, 后者是明确的开关 ——
    /// 音量 0 时优先画裸喇叭 (没声就是没声, 不管是不是手动关的), 拖离 0 后若仍是静音再画 ×。</para>
    /// <para>比原来「文字标签 + 勾选框」省下 ~84px 横向空间 (卡片第二行), 主音量行省 ~66px,
    /// 空出来的宽度全给滑条。</para>
    /// <para>与 <see cref="GearButton"/> 同一套画法 (自绘 / 透明底 / 只在悬停按下时铺浅底),
    /// 也不用字体图标: Segoe MDL2 在精简版或老系统上缺字会画成方块。</para>
    /// <para><b>语义与勾选框一致</b>: <see cref="Muted"/> 赋值会触发 <see cref="MutedChanged"/>,
    /// 所以原来挂在 CheckedChanged 上的「同步/落盘/重算增益」逻辑可以原样搬过来, 不必改调用顺序。</para>
    /// </summary>
    class SpeakerButton : Control
    {
        static readonly Color IconColor = Color.FromArgb(95, 94, 90);
        static readonly Color IconActive = Color.FromArgb(44, 44, 42);
        // 关掉的两态 (静音 / 音量 0) 都用灰蓝而不是红。红在本程序里已经等于「停止/失败」,
        // 而且同一行里滑条变灰、百分比照旧, 用红色会读成"出错了"。
        static readonly Color IconMuted = Color.FromArgb(0x8A, 0x90, 0x99);
        static readonly Color HoverBack = Color.FromArgb(234, 234, 234);
        static readonly Color PressBack = Color.FromArgb(216, 216, 216);
        static readonly Color DisabledInk = Color.FromArgb(178, 178, 178);

        /// <summary>图标的三种样子 (选哪个由 Muted / VolumeValue 一起决定, 见 OnPaint)。</summary>
        enum SpeakerLook { Waves, Cross, Bare }

        bool hot, down, muted;
        int volumeValue = 100;

        /// <summary>静音状态 (等价于原来勾选框的 Checked)。</summary>
        public bool Muted
        {
            get { return muted; }
            set
            {
                if (muted == value) return;
                muted = value;
                Invalidate();
                var h = MutedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 音量滑条的当前值, 只用来选图标 (0 → 裸喇叭)。与 <see cref="Muted"/> 各走各的:
        /// 这里不写任何声音逻辑, 音量到 0 自动静音那条规则仍在滑条事件里。
        /// </summary>
        public int VolumeValue
        {
            get { return volumeValue; }
            set
            {
                if (volumeValue == value) return;
                volumeValue = value;
                Invalidate();
            }
        }

        /// <summary>静音状态变化 (鼠标点 / 代码赋值都会触发, 与 CheckedChanged 一致)。</summary>
        public event EventHandler MutedChanged;

        public SpeakerButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Size = new Size(22, 22);
            Cursor = Cursors.Hand;
            // 不进 Tab 序列: 自绘控件不画焦点框, 有焦点也看不见, 反而会在点过之后留下
            // 一个"按空格会静音"的隐形按钮。静音始终可以用滑条拖到 0 达成。
            TabStop = false;
        }

        protected override void OnClick(EventArgs e)
        {
            if (Enabled) Muted = !muted;
            base.OnClick(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = false; down = false; Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { down = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (down) { down = false; Invalidate(); }
            base.OnMouseUp(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) { hot = false; down = false; }
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            Color back = Parent != null ? Parent.BackColor : SystemColors.Control;
            using (var b = new SolidBrush(back)) g.FillRectangle(b, ClientRectangle);

            if (Enabled && (hot || down))
            {
                back = down ? PressBack : HoverBack;
                using (var path = UiDraw.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 5))
                using (var fill = new SolidBrush(back))
                    g.FillPath(fill, path);
            }

            // 选哪张脸: 音量 0 优先 (没声就是没声), 其次静音 ×, 最后正常声波。
            Color ink = !Enabled ? DisabledInk
                : (muted || volumeValue == 0) ? IconMuted
                : ((hot || down) ? IconActive : IconColor);
            var look = volumeValue == 0 ? SpeakerLook.Bare
                : muted ? SpeakerLook.Cross
                : SpeakerLook.Waves;

            // 几何按 22×22 设计, 换尺寸时整体等比缩放 (卡片/主界面都用 22, 这里只是兜底)
            float s = Math.Min(Width, Height) / 22f;
            DrawSpeaker(g, Width / 2f, Height / 2f, s, ink, back, look);
        }

        /// <summary>
        /// 画喇叭 (描边风格, 照用户给的 SVG 描的)。坐标是 1024 视图等比折算到 22×22 的结果:
        /// 内容 866×660 装进 20px 宽 (k=20/866), 中心 (497,522.8) 对准控件中心 ——
        /// 所以喇叭占左半边, 声波/×占右半边, 和原图一样。
        /// 本体画法: 外轮廓填墨色, 内轮廓填底色, 两圈之间就是 1.5px 的"描边墙"
        /// (原图内外轮廓相距 64, 折算正好 1.48px, 不用再调)。
        /// </summary>
        static void DrawSpeaker(Graphics g, float cx, float cy, float s, Color ink, Color back, SpeakerLook look)
        {
            // 外轮廓 (圆角在 22px 下只剩 0.74px, 直接当直角画)
            var outer = new[]
            {
                new PointF(-9.26f, -3.84f), new PointF(-5.46f, -3.84f),   // 方箱顶边
                new PointF(-0.75f, -7.62f), new PointF(0.45f, -7.04f),    // 锥形上斜边 + 锥口上角
                new PointF(0.45f,  7.05f),  new PointF(-0.75f,  7.63f),   // 锥口下角 + 下斜边
                new PointF(-5.59f,  3.75f), new PointF(-9.26f,  3.75f),   // 方箱底边
                new PointF(-10.0f,  3.01f), new PointF(-10.0f, -3.10f),   // 右侧圆角
            };
            // 内轮廓: 与外轮廓相距恒 64 (原图如此), 这 64 就是描边墙的厚度
            var inner = new[]
            {
                new PointF(-8.53f, -2.36f), new PointF(-8.53f,  2.28f),
                new PointF(-5.09f,  2.28f), new PointF(-1.03f,  5.51f),
                new PointF(-1.03f, -5.50f), new PointF(-4.95f, -2.36f),
            };

            using (var fill = new SolidBrush(ink))
                g.FillPolygon(fill, Scale(outer, cx, cy, s));
            using (var fill = new SolidBrush(back))
                g.FillPolygon(fill, Scale(inner, cx, cy, s));

            if (look == SpeakerLook.Bare) return;   // 音量 0: 只剩喇叭, 波和 × 都不画

            if (look == SpeakerLook.Waves)
            {
                // 两道同心圆弧, 圆心在控件中心 (原图如此), 只取右侧 ~91°。
                // 原图线宽 32 折算 0.74px —— 22px 下会断成虚线, 放到 1.1px。
                using (var p = new Pen(ink, Math.Max(1f, 1.1f * s)))
                {
                    p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    for (int i = 0; i < 2; i++)
                    {
                        float r = (230f + i * 205f) * 20f / 866f * s;   // 原图半径 230 / 435
                        g.DrawArc(p, cx - 0.5f * s - r, cy - r, r * 2, r * 2, -45.5f, 91f);
                    }
                }
                return;
            }

            // 静音 ×: 原图是一个高瘦的叉 (半宽 2.7 / 半高 3.6), 中心在控件中心右侧 6.9px。
            using (var p = new Pen(ink, Math.Max(1f, 1.1f * s)))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                float mx = cx + 6.9f * s, my = cy, hw = 2.7f * s, hh = 3.6f * s;
                g.DrawLine(p, mx - hw, my - hh, mx + hw, my + hh);
                g.DrawLine(p, mx - hw, my + hh, mx + hw, my - hh);
            }
        }

        static PointF[] Scale(PointF[] pts, float cx, float cy, float s)
        {
            var r = new PointF[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                r[i] = new PointF(cx + pts[i].X * s, cy + pts[i].Y * s);
            return r;
        }
    }


    /// <summary>
    /// 增益图标: 圆圈加号 (照用户给的 SVG 描的) —— 圆环一圈 + 中心十字, 线宽全部同宽。
    /// <para>1024 视图: 圆环外径 512 / 内径 409.6 (笔宽 102.4), 加号臂半长 307.2、臂厚 102.4;
    /// 22px 下笔宽 2.2、环中线半径 9.9、臂半长 6.6。</para>
    /// <para>静态、不可点: 增益没有开关语义, 也没有可切换的状态。做成按钮会让人去点,
    /// 点了却什么都不会发生 —— 那是比"少一个功能"更糟的体验。</para>
    /// </summary>
    class GainGlyph : Control
    {
        /// <summary>与 <see cref="SpeakerButton"/> 同一个墨色, 两个图标在同一列里才像一家。</summary>
        static readonly Color Ink = Color.FromArgb(95, 94, 90);

        public GainGlyph()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Size = new Size(22, 22);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            Color back = Parent != null ? Parent.BackColor : SystemColors.Control;
            using (var b = new SolidBrush(back)) g.FillRectangle(b, ClientRectangle);

            // 圆圈加号 (照用户给的 SVG 描的): 1024 视图圆环外径 512 内径 409.6 (笔宽 102.4),
            // 加号臂半长 307.2、臂厚 102.4, 全部同宽。22px 下笔宽 2.2、环中线半径 9.9、臂 ±6.6。
            // 圆环外缘会顶到 11 (=控件半宽), 缩一档基准 (s 用 /23) 给抗锯齿留 0.5px 余量。
            float s = Math.Min(Width, Height) / 23f;
            float cx = Width / 2f, cy = Height / 2f;
            const float ringR = 9.9f, stroke = 2.2f, arm = 6.6f;

            using (var pen = new Pen(Ink, stroke * s))
                g.DrawEllipse(pen, cx - ringR * s, cy - ringR * s, ringR * 2 * s, ringR * 2 * s);
            using (var pen = new Pen(Ink, stroke * s))
            {
                g.DrawLine(pen, cx - arm * s, cy, cx + arm * s, cy);
                g.DrawLine(pen, cx, cy - arm * s, cx, cy + arm * s);
            }
        }
    }


    /// <summary>自绘用的公共绘制工具。</summary>
    static class UiDraw
    {
        /// <summary>圆角矩形路径 (radius 会被夹到不超过半高/半宽)。</summary>
        public static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int max = Math.Min(r.Width, r.Height) / 2;
            int d = Math.Min(radius, max) * 2;
            if (d <= 0)
            {
                path.AddRectangle(r);
            }
            else
            {
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
            }
            return path;
        }
    }
}
