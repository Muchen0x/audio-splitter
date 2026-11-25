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
        /// <summary>
        /// 滚动位置/范围变了 (滚轮、拖原生滚动条、内容增删触发的布局)。卡片区壳
        /// (<see cref="CardScrollShell"/>) 拿它重绘自绘细条 —— ScrollableControl
        /// 没有现成的滚动通知事件。
        /// </summary>
        public event EventHandler ScrollChanged;

        public BufferedFlowPanel()
        {
            // DoubleBuffered 是 protected, 只能在子类里开
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // WM_VSCROLL (拖原生条/转发) / WM_HSCROLL (横向) / WM_MOUSEWHEEL (滚轮)
            // —— 三条路径都会变滚动位置 (批95: 横向滑条也要跟着刷新)
            if (m.Msg == 0x115 || m.Msg == 0x114 || m.Msg == 0x20A)
            {
                var h = ScrollChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
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
    enum ButtonGlyph { None, Plus, Nut, SquarePlus, Cross, Refresh, TriangleDown, TriangleRight, More, LogOn, LogOff }

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
        int radius = NoFocusSplitContainer.WindowRadiusPx;   // 与窗口圆角同一弧度 (批96)
        bool hot, pressed;
        ButtonGlyph glyph = ButtonGlyph.None;
        int glyphRadius = Dpi.S(8);
        float glyphThickness = Dpi.F(2.8f);
        bool outline;

        public RoundButton()
        {
            // UserPaint: 不走系统按钮绘制。少了它, 下面的 SetStyle 与 OnPaint 都不会生效
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            // 系统按钮类会在客户区最外一圈补自己的"主题边框"(125% 缩放下最明显, 深灰 #C8C8C8),
            // 圆角外那四个方角正好把它露出来 —— 看着就是"两个边框"。
            // Flat + BorderSize=0 让它别画, 再由 OnPaint 先铺满父容器底色, 那一圈就没东西可露。
            // 早期版本还额外用 Region 把窗口裁成圆角、并把描边内缩 2px —— 代价是按钮的**可见框
            // 比控件小一圈**(130×35 的按钮只有 126×31 的框), 与旁边"框就是控件边界"的下拉框
            // 并排时差 4px, 一眼就看出按钮缩在里头。现在改成填满 + 描边贴边(见 OnPaint),
            // 框与控件同尺寸, 两者的框可以逐像素重合。
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
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            // 圆角外那几个角要补成父容器底色, 否则会露出方角白块 (与 RoundedCombo.OnPaint 同一手法)。
            // 这一笔同时负责盖掉系统按钮类画在客户区最外一圈的主题边框 —— 整块铺满,
            // 后面画的圆角本体再压上去, 第 0 行/列不会露出 #C8C8C8。
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
            // 描边按钮的悬停/按下: 边框统一成卡片浅灰之后, 往边框色里混已经看不出来了
            // (浅灰 7% 的色差 ≈ 2), 所以改成把边框本身压深一档 —— 与 RoundedCombo 的
            // "悬停加深"同一套反馈, 白色底不脏。
            else if (outline && Enabled && (hot || pressed))
                edge = Blend(edge, Color.Black, pressed ? 0.28f : 0.18f);

            // 填充与描边走同一条圆角路径, 一并做抗锯齿。
            // <para><b>描边贴到最外圈</b>: 笔心落在 0.5, 1px 的边正好占满第 0 行/列与第
            // W-1/H-1 行/列, 于是按钮的**可见框 = 控件大小**, 与旁边的 RoundedCombo
            // (框就是控件边界) 能逐像素重合。此前描边内缩 2px, 按钮的框比下拉小一圈,
            // 同一行并排放着, 一眼看出按钮"缩在里头"。</para>
            // <para>贴边的前提是**不能再用 Region 裁圆角**: Region 是像素级硬裁, 圆角处
            // 会把描边外缘那圈抗锯齿像素按格切掉, 1px 的弧线立刻变成断续的虚线点
            // (蓝描边 8 倍放大时很明显)。不裁的代价是圆角外四个方角仍在窗口内, 由开头
            // 铺的父容器底色盖住 —— 与 RoundedCombo 同一手法, 同一个视觉结果。</para>
            if (Width > 4 && Height > 4)
            {
                int r = Math.Max(1, radius - 1);
                using (var bodyPath = UiDraw.RoundRect(new Rectangle(0, 0, Width, Height), r))
                using (var edgePath = UiDraw.RoundRect(
                    new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), r - 0.5f))
                {
                    using (var b = new SolidBrush(c))
                        g.FillPath(b, bodyPath);
                    using (var p = new Pen(edge))
                        g.DrawPath(p, edgePath);
                }
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

        /// <summary>按比例混色（不做 alpha 混合，免得底色透出来）。t=0 返回 a，t=1 返回 b。
        /// <para>internal: <see cref="StartButton"/> 的动效渐变与墨色对齐要用同一套混色算术。</para></summary>
        internal static Color Blend(Color a, Color b, float t)
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
        static Color BorderNormal { get { return OutputCardControl.BorderColor; } }   // 与下拉/卡片同一套浅灰
        static readonly Color BorderHot = Color.FromArgb(146, 153, 164);              // 悬停: 浅灰加深一档
        static readonly Color BorderDisabled = Color.FromArgb(226, 230, 235);         // 禁用: 比常态更淡
        static readonly Color BackDisabled = Color.FromArgb(244, 245, 247);
        static readonly Color TextDisabled = Color.FromArgb(150, 154, 160);
        // 设计值 (96 DPI), 访问时按系统缩放换算; SpinStrip 例外 —— 它是原生 UpDownButtons
        // 的物理宽度, 系统不随 DPI 放大, 保持 16 才能正好让开它。
        static int CornerRadius { get { return NoFocusSplitContainer.WindowRadiusPx; } }   // 与窗口圆角同一弧度 (批96)
        static int PadX { get { return Dpi.S(7); } }          // 文字距左缘 (与 RoundedCombo.TextPadX 对齐)
        static int GapCapInner { get { return Dpi.S(2); } }   // 文字与数字区的间隔 (与数字-单位间隔一致)
        static int GapNumUnit { get { return Dpi.S(2); } }    // 数字区与单位的间隔
        static int GapUnitArrow { get { return Dpi.S(4); } }  // 单位与箭头区的间隔
        static int NumPad { get { return Dpi.S(2); } }        // 数字文字两侧的余量 (光标/边距)
        static int ArrowW { get { return Dpi.S(20); } }       // 上下箭头区宽度
        const int SpinStrip = 16;    // 数字框内部给 (已藏) 原生按钮留的条宽, 见 Recalc

        public readonly NoWheelNumeric Inner;
        readonly Label caption;      // 框内的文字 (可空)
        readonly Label unit;         // 框内的单位后缀 (可空, 如 "ms")
        bool hot;
        int hoverDir, pressedDir;    // 箭头的悬停/按下状态 (1=上, -1=下, 0=不在箭头上)
        int arrowX;                  // 箭头区左缘 (客户区坐标, Recalc 时定)

        /// <summary>
        /// 外壳的固定高度 (0 = 自动, 数字框高 + 2)。数字框高度由字体决定 (9pt → 23, 壳 25),
        /// 旁边的圆角下拉 (如声道框) 是固定 24 —— 壳比它高 1px, 顶边/底边对不齐。
        /// 调用方设成与相邻下拉一致的高度 (OutputCard 设 cmbChannel.Height), Recalc 照它排,
        /// 数字框在壳内垂直居中。
        /// </summary>
        public int FixedHeight;

        public RoundNumericShell(NoWheelNumeric inner) : this(inner, null, null) { }

        public RoundNumericShell(NoWheelNumeric inner, string captionText) : this(inner, captionText, null) { }

        public RoundNumericShell(NoWheelNumeric inner, string captionText, string unitText)
        {
            Inner = inner;
            // 数字居中: 数字区按最大值固定宽度, 若右对齐则位数少时 (如 50) 左侧空出一大截,
            // 「延迟→数字」的间距远大于「数字→ms」; 居中让余白均分两侧, 两段间距天然相等
            // (用户指定「文字与数字的间距和数字与单位的一致」)。
            inner.TextAlign = HorizontalAlignment.Center;
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
            // 字体是环境继承的: 挂进父级后才定, caption 宽度跟着变, 定了之后要重排。
            // 数字区按最大值固定宽度 (用户指定), 不随当前值伸缩, 所以值变化不用重排
            FontChanged += (s, e) => { if (IsHandleCreated) BeginInvoke((Action)Recalc); };
        }

        /// <summary>单位后缀的常态色 (与 OutputCard 行标签同色, 禁用时换灰)。</summary>
        static readonly Color UnitColor = Color.FromArgb(120, 126, 136);

        /// <summary>
        /// 按内容重排: 外壳尺寸、文字/数字框/单位/箭头区的位置一次到位。
    /// <para>顺序 (用户指定): [文字][数字][单位][上下箭头], 彼此紧靠 ——
    /// 数字区宽度按最大值固定量出 (用户指定「固定长度」), 无论当前值是几位数都不伸缩;
    /// 数字在区域内居中, 余白均分两侧, 文字-数字与数字-单位两段间距保持一致。</para>
        /// </summary>
        void Recalc()
        {
            if (IsDisposed) return;
            int innerH = Inner.Height;
            // 外壳高度: 调用方指定 (与相邻圆角下拉对齐) 则照它, 否则数字框高 + 2
            int h = FixedHeight > 0 ? FixedHeight : innerH + 2;
            int x = 1;
            if (caption != null)
            {
                caption.Location = new Point(PadX, (h - caption.PreferredHeight) / 2);
                x = PadX + caption.PreferredWidth + GapCapInner;
            }
            // 固定宽度: 按最大值量 (如延迟 0..2000 → 按 "2000" 量), 当前值是几位数都一样宽
            int numberW = TextRenderer.MeasureText(Inner.Maximum.ToString("0"), Inner.Font).Width + NumPad;
            // 数字框窗口比文字区宽出一条 SpinStrip: 基类布局会把编辑框压到「客户区-按钮宽」,
            // 与它抢 bounds 是打不赢的 (非托管侧直接摆回来, 实测拉锯到 StackOverflow) ——
            // 干脆把这条宽度算进窗口: 编辑框正好铺到文字右缘; 剩下那条 SpinStrip 是被藏掉的
            // 原生按钮占的宽度, 由 PlaceInner 裁剪掉, 看不见也点不到
            PlaceInner(x, numberW + SpinStrip, h, innerH);
            int unitX = x + numberW + GapNumUnit;
            if (unit != null)
            {
                unit.Location = new Point(unitX, (h - unit.PreferredHeight) / 2);
                unitX += unit.PreferredWidth;
                // 数字框已裁到文字区, 与单位标签不再重叠; 这里仍把标签提到 z 序最上,
                // 万一将来裁剪条件变了也不会被数字框盖掉半截 (配合 NoWheelNumeric 的
                // WS_CLIPSIBLINGS)
                unit.BringToFront();
            }
            arrowX = unitX + GapUnitArrow;
            // 右侧多留一段给圆角: 箭头若压在圆角弧线上, 擦箭头的白底会把弧线一起擦掉 ——
            // 表现就是"底边框在箭头下方断一截、右边缘留一条游离竖线"。
            Size = new Size(arrowX + ArrowW + CornerRadius, h);
            Invalidate();
        }

        /// <summary>
        /// 数字框定位到数字区, 在外壳高度 <paramref name="shellH"/> 内垂直居中。
        /// 原生 UpDownButtons (数字框内部右侧那条立体按钮) 压成 0×0 藏掉:
        /// 它不藏, 就会一直占着数字框右缘, 箭头没法按「数字→单位→按钮」的顺序排到单位后面;
        /// 压成 0×0 后它收不到任何鼠标事件, 箭头改由外壳自绘 (见 OnPaint)。
        /// <para>NumericUpDown 在 Resize 时会重新摆它, 所以每次重排后都要再压一次。</para>
        /// </summary>
        void PlaceInner(int x, int w, int shellH, int innerH)
        {
            int y = (shellH - innerH) / 2;
            Inner.SetBounds(x, y, w, innerH);
            // 裁剪两条边:
            // 1) 上下各 1px —— 数字框是白底的原生窗口, 壳高 24 而数字框高 23 时 y=0 正好压住
            //    外壳的顶边框, 底边框也被压掉 1px, 边框在数字这一段"断"成缺口、旁边露半截线头;
            //    裁掉后两条边框完整, 文字 (居中, 约 12px 高) 不受影响。
            // 2) 右侧 SpinStrip —— 数字框窗口 = 文字区 + 16px (见 Recalc, 那条宽度是为绕开
            //    基类布局才留的), 16px 里本该是原生上下按钮, 现在被单位标签盖住大半, 只在
            //    数字与单位之间露出一条 2px 的缝。原生按钮偶尔被基类重新显示并画上主题
            //    高亮边框, 露出的那条缝就变成一块蓝色残影 (滚动卡片后最容易被看见)。
            //    把窗口裁到文字区为止, 那条宽度彻底不画, 露出的是外壳白底。
            int cropTop = y == 0 ? 1 : 0;
            Region old = Inner.Region;
            Inner.Region = new Region(new Rectangle(0, cropTop,
                Math.Max(1, w - SpinStrip), Math.Max(1, innerH - cropTop * 2)));
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
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            // 圆角外四个小角补成父容器底色 (与 SoftCard.OnPaint 同一手法)
            Color outside = Parent != null ? Parent.BackColor : SystemColors.Control;
            using (var b = new SolidBrush(outside)) g.FillRectangle(b, ClientRectangle);

            bool strong = Inner.Enabled && hot;
            var frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = UiDraw.RoundRect(frame, CornerRadius))
            using (var fill = new SolidBrush(Inner.Enabled ? Color.White : BackDisabled))
                g.FillPath(fill, path);

            // 原生上下按钮可能在句柄创建后被数字框自己摆回来, 每次绘制前压一道保险
            EnforceSpinHidden();

            // 箭头区白底: 整块清干净再画箭头 (RefreshArrow 只让这块失效)。
            // 它会连带擦掉右侧圆角的那段弧线 —— 所以**边框放到它之后再画**;
            // 顺序反了就是"底边在箭头下方断开、右缘留一条孤立竖线"。
            using (var b = new SolidBrush(Inner.Enabled ? Color.White : BackDisabled))
                g.FillRectangle(b, arrowX, 1, Math.Max(0, Width - arrowX - 1), Height - 2);

            // 边框: 与 RoundedCombo / 输出卡片同一套浅灰细边, 悬停时加深一档并加粗到 1.5px。
            // 走半像素对齐路径, 否则 1px 会被抗锯齿拆成两条灰线 (看着比 1px 粗、发灰)。
            if (Width > 4 && Height > 4)
            {
                float inset = strong ? 0.75f : 0.5f;
                Color edge = !Inner.Enabled ? BorderDisabled : (strong ? BorderHot : BorderNormal);
                using (var path = UiDraw.RoundRect(
                    new RectangleF(inset, inset, Width - inset * 2, Height - inset * 2), CornerRadius))
                using (var pen = new Pen(edge, strong ? 1.5f : 1f))
                    g.DrawPath(pen, path);
            }
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
            using (var p = new Pen(inkColor, Dpi.F(1.6f)))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(p, cx - Dpi.F(4f), cy + Dpi.F(2.2f) * dir, cx, cy - Dpi.F(2.2f) * dir);
                g.DrawLine(p, cx, cy - Dpi.F(2.2f) * dir, cx + Dpi.F(4f), cy + Dpi.F(2.2f) * dir);
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

    /// <summary>
    /// 日志文本框: 把会改变滚动位置的原生输入转成事件, 供圆角壳 (<see cref="RoundLogShell"/>)
    /// 重绘自绘滚动条 —— 原生 TextBox 不提供任何滚动通知。
    /// </summary>
    class LogTextBox : TextBox
    {
        public event EventHandler ScrollChanged;

        void Raise()
        {
            var h = ScrollChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // WM_VSCROLL (点滚动条/键盘翻页) / WM_MOUSEWHEEL (滚轮) / EM_SCROLLCARET
            // (追加日志后 ScrollToCaret 追到底) —— 三条路径都会改变滚动位置
            if (m.Msg == 0x115 || m.Msg == 0x20A || m.Msg == 0x00B7) Raise();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Raise();   // AppendText/Clear 后位置都可能变; ScrollToCaret 那条消息随后再触发一次, 幂等
        }
    }

    /// <summary>
    /// 日志圆角壳: TextBox 是原生控件, 自己画不了圆角描边 —— 外壳按 <see cref="RoundNumericShell"/>
    /// 同一套框色画圆角边框, 日志框嵌在 1px 内衬里并把窗口裁成圆角 (同一手法, 方角不会盖住弧线)。
    /// <para>右侧留一条竖向滚动条带: 原生滚动条被窗口区域裁掉 —— 看不见也点不到 (用户要求
    /// "滑动条无边框、去掉明显外框"), 滚动机制本身保留 (滚轮/键盘/ScrollInfo 全照旧);
    /// 滚动条改由外壳自绘成 4px 圆头细条 (与下拉列表的滚动条同一套), 拖拽滑块 / 点击翻页 /
    /// 条带上的滚轮都在外壳处理。</para>
    /// </summary>
    class RoundLogShell : Panel
    {
        static Color BorderCol { get { return OutputCardControl.BorderColor; } }   // 与延迟壳/卡片同一套浅灰框
        static readonly Color ThumbColor = Color.FromArgb(198, 203, 211);          // 与下拉列表滚动条同一支灰
        static readonly Color ThumbHotColor = Color.FromArgb(172, 178, 188);       // 悬停/拖拽加深一档
        static int CornerRadius { get { return NoFocusSplitContainer.WindowRadiusPx; } }   // 与窗口圆角同一弧度 (批96)
        static int BarW { get { return Dpi.S(4); } }      // 滑条粗细 (设计 px, 与下拉列表一致)
        static int StripW { get { return Dpi.S(10); } }   // 滑条带/命中区宽 (设计 px)
        static int PadX { get { return Dpi.S(10); } }     // 左右内衬留白 (设计 px, 对称): 正文不顶框,
                                                          // 右侧那条留白正是自绘滑条的位置 (用户要求)

        public readonly LogTextBox Inner;
        bool barHot;                 // 鼠标悬停在滚动条带上
        bool dragThumb;              // 正在拖拽滑块
        int dragGrabY;               // 按下点相对滑块顶边的偏移 (拖拽中保持不跳)
        int textEnd;                 // 正文右缘 (壳坐标, 不含): 右侧留白带从此到边框, 滑条在其中居中
        /// <summary>诊断: 按下滑块成功的次数 / 拖拽中实际发送滚动消息的次数 (探针读)。</summary>
        internal int DragStarts, DragMoves;

        const int SB_VERT = 1;
        const int SB_PAGEUP = 2, SB_PAGEDOWN = 3, SB_THUMBTRACK = 5;
        const int WM_VSCROLL = 0x115, WM_MOUSEWHEEL = 0x20A;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct SCROLLINFO
        {
            public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO si);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        public RoundLogShell(LogTextBox log)
        {
            Inner = log;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.ResizeRedraw, true);
            Controls.Add(log);
            // 滚动机制保留原生的 (滚轮/键盘/GetScrollInfo 都依赖它), 只把外观藏掉:
            // 3D 边框不要 (圆角描边由外壳画), 原生滚动条靠窗口区域裁剪 (见 Recalc)。
            log.BorderStyle = BorderStyle.None;
            log.ScrollBars = ScrollBars.Vertical;
            log.ScrollChanged += (s, e) => RefreshBar();
            log.TextChanged += (s, e) => RefreshBar();
            log.HandleCreated += OnInnerHandleCreated;   // 句柄重建时 Region 会丢, 兜底重裁
            Size = new Size(559, 90);
        }

        /// <summary>
        /// 摆日志框 + 裁区域。核心认知: WS_VSCROLL 是**非客户区**滚动条, 占据窗口右缘
        /// 一条固定宽度 (client 之外) —— Region 只要切在 client 右缘, 它就整条不可见、点不到。
        /// <para>布局分两步: 先把 Inner 窗口比可视区加宽一条滚动条宽 (初值取系统量度
        /// SM_CXVSCROLL, 再按实测 ClientSize **自校正一次**, 不依赖任何 DPI 假设), 让正文
        /// (client) 铺满到可视区右缘; 然后 Region 切在 min(client宽, 可视区-条带宽)。
        /// 被切掉的部分由外壳接管 (画自绘细条), 滚动机制 (滚轮/键盘/ScrollInfo) 原样保留。</para>
        /// </summary>
        void Recalc()
        {
            if (Inner == null || IsDisposed) return;
            int ih = Math.Max(1, Height - 2);
            // 左右内衬对称: 正文从 1+PadX 到 Width-1-PadX, 两边留白等宽 (用户要求"留白要对称")。
            // 右侧那条留白就是滑条的安放处 —— 滑条在其中居中, 不贴正文也不贴边框。
            int textW = Math.Max(StripW + 40, Width - 2 - 2 * PadX);
            if (Inner.IsHandleCreated)
            {
                // Inner 窗口宽 = 文本宽 + 原生滚动条宽 (初值取系统量度 SM_CXVSCROLL,
                // 再按实测 ClientSize 自校正一次, 不依赖 DPI 假设) —— client == textW 时正文铺满
                int w = textW + Math.Max(1, GetSystemMetrics(2));
                Inner.SetBounds(1 + PadX, 1, w, ih);
                int cw = Inner.ClientSize.Width;
                if (cw != textW)
                {
                    w = Math.Max(1, w + textW - cw);
                    Inner.SetBounds(1 + PadX, 1, w, ih);
                }
            }
            // Region 切在 client 右缘 (原生滚动条在 client 之外的非客户区, 整条被排除);
            // 兜底不超过 textW, 防止窗口加宽没生效时滚动条露进可视区。
            int cut = Math.Max(1, Math.Min(Inner.IsHandleCreated ? Inner.ClientSize.Width : textW, textW));
            textEnd = 1 + PadX + cut;
            // 区域在 Inner 自己的坐标里: 外壳内衬圆角平移 Inner 原点后与 [0, cut) 求交 ——
            // 右缘把原生滚动条整条排除 (看不见、鼠标事件也穿不过去, 全部落在壳上由外壳处理)。
            Region old = Inner.Region;
            using (var path = UiDraw.RoundRect(
                new System.Drawing.RectangleF(-(1 + PadX), -1f, Width - 2f, Height - 2f), CornerRadius - 1f))
            {
                var reg = new Region(path);
                reg.Intersect(new Rectangle(0, 0, cut, ih));
                Inner.Region = reg;
            }
            if (old != null) old.Dispose();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            BeginInvoke((Action)Recalc);   // 与 RoundNumericShell 同理: 等消息循环走起来再摆
        }

        void OnInnerHandleCreated(object sender, EventArgs e)
        {
            // TextBox 句柄重建 (字体/边框等属性变更) 会换新 HWND, Region 随旧句柄一起丢 ——
            // 原生滚动条会整条漏出来; 这里兜底重裁。BeginInvoke 等布局稳定后再做。
            if (IsHandleCreated) BeginInvoke((Action)Recalc);
        }

        protected override void OnResize(System.EventArgs eventargs)
        {
            base.OnResize(eventargs);
            Recalc();                      // 拖分隔条/缩窗时日志框每步跟随
        }

        /// <summary>只重画右侧留白带 (滚动位置变了, 正文由文本框自己重画)。</summary>
        void RefreshBar()
        {
            int from = textEnd > 0 ? textEnd : Width - StripW - 1;
            Invalidate(new Rectangle(from, 0, Width - from, Height));
        }

        /// <summary>当前竖向滚动信息 (nMax/nPage/nPos, 行单位); 句柄未建或读不到返回 false。</summary>
        bool GetVert(out SCROLLINFO si)
        {
            si = new SCROLLINFO();
            if (Inner == null || !Inner.IsHandleCreated) return false;
            si.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(SCROLLINFO));
            si.fMask = 0x1 | 0x2 | 0x4;    // SIF_RANGE | SIF_PAGE | SIF_POS
            return GetScrollInfo(Inner.Handle, SB_VERT, ref si) && si.nMax > 0;
        }

        /// <summary>算滑块几何 (条带内 trackTop..trackLen, 滑块顶 y 与高); 不需要滚动返回 false。</summary>
        bool ThumbGeometry(SCROLLINFO si, out int trackTop, out int trackLen, out int ty, out int th)
        {
            ty = th = 0;
            trackTop = Dpi.S(6);
            trackLen = Height - 1 - trackTop * 2;
            if (si.nPage >= si.nMax + 1 || trackLen < BarW * 4) return false;   // 全文可见/太矮 → 不画
            th = Math.Max(Dpi.S(18), (int)((long)trackLen * si.nPage / (si.nMax + 1)));
            if (th > trackLen) th = trackLen;
            int maxTop = si.nMax + 1 - si.nPage;
            ty = trackTop + (maxTop > 0 ? (int)((long)(trackLen - th) * si.nPos / maxTop) : 0);
            return true;
        }

        void DrawBar(Graphics g)
        {
            SCROLLINFO si;
            if (!GetVert(out si)) return;
            int trackTop, trackLen, ty, th;
            if (!ThumbGeometry(si, out trackTop, out trackLen, out ty, out th)) return;
            // 滑条在右侧留白带 (textEnd .. 边框) 内水平居中 —— 不贴正文也不贴边框
            int zoneL = textEnd > 0 ? textEnd : Width - 1 - StripW;
            int zoneW = (Width - 2) - zoneL + 1;
            int bx = zoneL + Math.Max(0, (zoneW - BarW) / 2);
            using (var path = UiDraw.RoundRect(new RectangleF(bx, ty, BarW, th), BarW / 2f))
            using (var b = new SolidBrush(barHot || dragThumb ? ThumbHotColor : ThumbColor))
                g.FillPath(b, path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            // 圆角外四个小角补成父容器底色 (与 RoundNumericShell.OnPaint 同一手法)
            Color outside = Parent != null ? Parent.BackColor : SystemColors.Control;
            using (var b = new SolidBrush(outside)) g.FillRectangle(b, ClientRectangle);

            using (var path = UiDraw.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), CornerRadius))
            using (var fill = new SolidBrush(Inner.BackColor))
                g.FillPath(fill, path);

            DrawBar(g);

            // 边框最后画: 与延迟壳同一套浅灰细边, 半像素对齐 (1px 不被抗锯齿拆粗)
            using (var path = UiDraw.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), CornerRadius))
            using (var pen = new Pen(BorderCol, 1f))
                g.DrawPath(pen, path);
        }

        // ---- 滚动条交互: 条带在 Inner 区域之外 (被 Inner 的窗口区域排除), 事件全落在外壳 ----

        bool InStrip(int x)
        {
            int from = textEnd > 0 ? textEnd : Width - 1 - StripW;
            return x >= from;
        }

        void SendScroll(int wParamLow, int pos)
        {
            if (!Inner.IsHandleCreated) return;
            SendMessage(Inner.Handle, WM_VSCROLL, (IntPtr)((pos << 16) | wParamLow), IntPtr.Zero);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !InStrip(e.X)) return;
            SCROLLINFO si;
            if (!GetVert(out si)) return;
            int trackTop, trackLen, ty, th;
            if (!ThumbGeometry(si, out trackTop, out trackLen, out ty, out th)) return;
            if (e.Y >= ty && e.Y < ty + th)
            {
                dragThumb = true;
                dragGrabY = e.Y - ty;
                DragStarts++;
                Capture = true;
            }
            else SendScroll(e.Y < ty ? SB_PAGEUP : SB_PAGEDOWN, 0);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool over = InStrip(e.X);
            if (over != barHot) { barHot = over; RefreshBar(); }
            if (!dragThumb) return;
            SCROLLINFO si;
            if (!GetVert(out si)) return;
            int trackTop, trackLen, ty, th;
            if (!ThumbGeometry(si, out trackTop, out trackLen, out ty, out th)) return;
            int maxTop = si.nMax + 1 - si.nPage;
            if (maxTop <= 0) return;
            int pos = (int)Math.Round((e.Y - trackTop - dragGrabY) * (double)maxTop / Math.Max(1, trackLen - th));
            pos = Math.Max(0, Math.Min(maxTop, pos));
            DragMoves++;
            SendScroll(SB_THUMBTRACK, pos);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragThumb) { dragThumb = false; Capture = false; RefreshBar(); }
        }

        /// <summary>滚轮落在条带上时转发给日志框 (正文区滚轮由文本框原生处理, 不经过这里)。</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Inner.IsHandleCreated || e.Delta == 0) return;
            var p = PointToScreen(e.Location);
            SendMessage(Inner.Handle, WM_MOUSEWHEEL,
                (IntPtr)((e.Delta << 16) & unchecked((int)0xFFFF0000)),
                (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF)));
        }
    }

    /// <summary>
    /// 输出卡片区滚动条覆盖层: 画在卡片区窗口**之上**的窄竖条控件 (宽 = 右侧留白带),
    /// 滑条的绘制与全部鼠标交互都在它身上。为什么要一层覆盖件而不直接在壳 (<see cref="CardScrollShell"/>,
    /// 卡片区的父窗口) 上画: 父窗口的绘制被 WS_CLIPCHILDREN 语义裁进子窗口矩形之外 ——
    /// 卡片区窗口加宽后矩形盖住了整条留白带, 父窗口对滑带的失效**永远落不出 WM_PAINT**
    /// (实测 PaintCount 卡死、强制 Invalidate 也不画); 顶层兄弟控件不受这层裁剪, 画得着、点得着。
    /// 视觉与日志圆角壳 (<see cref="RoundLogShell"/>) 同一套: 4px 圆头细条, 无边框无箭头。
    /// </summary>
    class CardBarOverlay : Control
    {
        static readonly Color ThumbColor = Color.FromArgb(198, 203, 211);      // 与日志壳/下拉列表同一支灰
        static readonly Color ThumbHotColor = Color.FromArgb(172, 178, 188);   // 悬停/拖拽加深一档
        static int BarW { get { return Dpi.S(4); } }      // 滑条粗细 (设计 px, 与日志壳一致)

        public readonly CardScrollShell Shell;
        bool barHot;
        bool dragThumb;
        int dragGrabY;
        /// <summary>诊断: OnPaint 次数 / 按下滑块次数 / 拖拽落位次数 / 最近 GetScrollInfo 读数 (探针读)。</summary>
        internal int PaintCount, DragStarts, DragMoves, DbgNMax, DbgNPage, DbgNPos;
        internal bool DbgGot, DbgGeo;

        const int SB_VERT = 1;
        const int WM_MOUSEWHEEL = 0x20A;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct SCROLLINFO
        {
            public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO si);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public CardBarOverlay(CardScrollShell shell)
        {
            Shell = shell;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        /// <summary>滚动信息/位置变了 → 重画滑条 (宿主转发卡片区的滚动通知)。</summary>
        public void RefreshBar()
        {
            Invalidate();
        }

        /// <summary>当前竖向滚动信息 (nMax/nPage/nPos, 像素单位); 没有滚动需求返回 false。</summary>
        bool GetVert(out SCROLLINFO si)
        {
            si = new SCROLLINFO();
            var inner = Shell.Inner;
            if (inner == null || !inner.IsHandleCreated) return false;
            // 内容放得下时不画 (用户批87): 原生条被 WinForms 藏起后, GetScrollInfo 的
            // nMax/nPage 仍保留旧范围值 (实测 nMax=647 不清零), 只认它会在"放得下"
            // 时照画 —— 托管 VerticalScroll.Visible 才是真实状态。
            if (!inner.VerticalScroll.Visible) return false;
            si.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(SCROLLINFO));
            si.fMask = 0x1 | 0x2 | 0x4;    // SIF_RANGE | SIF_PAGE | SIF_POS
            bool got = GetScrollInfo(inner.Handle, SB_VERT, ref si);
            DbgGot = got; DbgNMax = si.nMax; DbgNPage = si.nPage; DbgNPos = si.nPos;
            return got && si.nMax > 0;
        }

        bool ThumbGeometry(SCROLLINFO si, out int trackTop, out int trackLen, out int ty, out int th)
        {
            ty = th = 0;
            trackTop = Dpi.S(6);
            trackLen = Height - 1 - trackTop * 2;
            bool ok = si.nPage < si.nMax + 1 && trackLen >= BarW * 4;
            DbgGeo = ok;
            if (!ok) return false;
            th = Math.Max(Dpi.S(18), (int)((long)trackLen * si.nPage / (si.nMax + 1)));
            if (th > trackLen) th = trackLen;
            int maxTop = si.nMax + 1 - si.nPage;
            ty = trackTop + (maxTop > 0 ? (int)((long)(trackLen - th) * si.nPos / maxTop) : 0);
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintCount++;
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            using (var b = new SolidBrush(BackColor))
                g.FillRectangle(b, ClientRectangle);

            SCROLLINFO si;
            if (!GetVert(out si)) return;
            int trackTop, trackLen, ty, th;
            if (!ThumbGeometry(si, out trackTop, out trackLen, out ty, out th)) return;
            // 滑条贴合右框缘 (批89): 右缘离框线内缘 1 物理像素, 圆头贴着边框
            // (不再在留白带内居中 —— 用户要求"边框刚好贴合滑动条")
            int bx = Math.Max(0, Width - BarW - 1);
            using (var path = UiDraw.RoundRect(new RectangleF(bx, ty, BarW, th), BarW / 2f))
            using (var b = new SolidBrush(barHot || dragThumb ? ThumbHotColor : ThumbColor))
                g.FillPath(b, path);
        }

        // ---- 鼠标交互: 覆盖层盖在卡片区之上, 条带内的事件全落在这里 ----

        /// <summary>
        /// 落滚动位置。试过三条路 (探针 cardshell 模式实测): AutoScrollPosition 赋值
        /// 在这套布局下完全不生效 (读回恒 0)、外部 WM_VSCROLL 的 SB_THUMBTRACK/
        /// THUMBPOSITION 被忽略 —— 只有 <c>VerticalScroll.Value</c> 赋值能精确落位,
        /// 就用它; 滚动事件由 Inner.Scroll 回来刷新滑条。
        /// </summary>
        void SetScrollPos(int pos)
        {
            var inner = Shell.Inner;
            if (inner == null || !inner.IsHandleCreated) return;
            try { inner.VerticalScroll.Value = Math.Max(0, pos); }
            catch { }
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            SCROLLINFO si;
            if (!GetVert(out si)) return;
            int trackTop, trackLen, ty, th;
            if (!ThumbGeometry(si, out trackTop, out trackLen, out ty, out th)) return;
            if (e.Y >= ty && e.Y < ty + th)
            {
                dragThumb = true;
                dragGrabY = e.Y - ty;
                DragStarts++;
                Capture = true;
            }
            else SetScrollPos(si.nPos + (e.Y < ty ? -trackLen : trackLen));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!barHot) { barHot = true; Invalidate(); }
            if (!dragThumb) return;
            SCROLLINFO si;
            if (!GetVert(out si)) return;
            int trackTop, trackLen, ty, th;
            if (!ThumbGeometry(si, out trackTop, out trackLen, out ty, out th)) return;
            int maxTop = si.nMax + 1 - si.nPage;
            if (maxTop <= 0) return;
            int pos = (int)Math.Round((e.Y - trackTop - dragGrabY) * (double)maxTop / Math.Max(1, trackLen - th));
            pos = Math.Max(0, Math.Min(maxTop, pos));
            DragMoves++;
            SetScrollPos(pos);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!barHot) return;
            barHot = false;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragThumb) { dragThumb = false; Capture = false; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var inner = Shell.Inner;
            if (inner == null || !inner.IsHandleCreated || e.Delta == 0) return;
            var p = PointToScreen(e.Location);
            SendMessage(inner.Handle, WM_MOUSEWHEEL,
                (IntPtr)((e.Delta << 16) & unchecked((int)0xFFFF0000)),
                (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF)));
        }
    }

    /// <summary>
    /// 输出卡片区**横向**滑条 (批95): 窗口窄到卡片只剩最小宽还在挤 (窄过
    /// CardGrid.HardMinW, 内容确实放不下) 时 Inner 冒出横向滚动, 原生横条被
    /// Inner 的窗口区域裁掉看不见 —— 这里在壳的**底部留白带**画一条与竖向
    /// (<see cref="CardBarOverlay"/>) 完全同款的 4px 圆头灰条: 同一支色、同一粗细、
    /// 同一套拖拽/翻页/滚轮交互, 底缘贴壳底缘 (竖条贴右缘的镜像)。
    /// <para>内容放得下时一条都不画 (GetH 先查 HorizontalScroll.Visible, 批87
    /// 同款守卫 —— GetScrollInfo 的 nMax/nPage 旧缓存不可信)。</para>
    /// </summary>
    class CardHBarOverlay : Control
    {
        static readonly Color ThumbColor = Color.FromArgb(198, 203, 211);      // 与竖条同一支灰
        static readonly Color ThumbHotColor = Color.FromArgb(172, 178, 188);   // 悬停/拖拽加深一档
        static int BarW { get { return Dpi.S(4); } }

        public readonly CardScrollShell Shell;
        bool barHot;
        bool dragThumb;
        int dragGrabX;
        /// <summary>诊断 (探针读): OnPaint 次数 / 按下滑块次数 / 拖拽落位次数。</summary>
        internal int PaintCount, DragStarts, DragMoves;
        internal bool DbgGot;

        const int SB_HORZ = 0;
        const int WM_MOUSEHWHEEL = 0x20E;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct SCROLLINFO
        {
            public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO si);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public CardHBarOverlay(CardScrollShell shell)
        {
            Shell = shell;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        public void RefreshBar()
        {
            Invalidate();
        }

        /// <summary>当前横向滚动信息 (nMax/nPage/nPos, 像素单位); 没有滚动需求返回 false。</summary>
        bool GetH(out SCROLLINFO si)
        {
            si = new SCROLLINFO();
            var inner = Shell.Inner;
            if (inner == null || !inner.IsHandleCreated) return false;
            if (!inner.HorizontalScroll.Visible) return false;   // 放得下不画 (批87 同款)
            si.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(SCROLLINFO));
            si.fMask = 0x1 | 0x2 | 0x4;    // SIF_RANGE | SIF_PAGE | SIF_POS
            bool got = GetScrollInfo(inner.Handle, SB_HORZ, ref si);
            DbgGot = got;
            return got && si.nMax > 0;
        }

        /// <summary>滑块几何: tx..tx+tw 为滑块中心线行程, gx..gx+gw 为滑块矩形 (横条)。</summary>
        bool ThumbGeometryH(SCROLLINFO si, out int trackLeft, out int trackLen, out int gx, out int gw)
        {
            gx = gw = 0;
            trackLeft = Dpi.S(6);
            trackLen = Width - 1 - trackLeft * 2;
            bool ok = si.nPage < si.nMax + 1 && trackLen >= BarW * 4;
            if (!ok) return false;
            gw = Math.Max(Dpi.S(18), (int)((long)trackLen * si.nPage / (si.nMax + 1)));
            if (gw > trackLen) gw = trackLen;
            int maxLeft = si.nMax + 1 - si.nPage;
            gx = trackLeft + (maxLeft > 0 ? (int)((long)(trackLen - gw) * si.nPos / maxLeft) : 0);
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintCount++;
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            using (var b = new SolidBrush(BackColor))
                g.FillRectangle(b, ClientRectangle);

            SCROLLINFO si;
            if (!GetH(out si)) return;
            int trackLeft, trackLen, gx, gw;
            if (!ThumbGeometryH(si, out trackLeft, out trackLen, out gx, out gw)) return;
            // 滑条贴合壳底缘 (竖条贴右缘的镜像): 底缘离壳底 1 物理像素
            int by = Math.Max(0, Height - BarW - 1);
            using (var path = UiDraw.RoundRect(new RectangleF(gx, by, gw, BarW), BarW / 2f))
            using (var b = new SolidBrush(barHot || dragThumb ? ThumbHotColor : ThumbColor))
                g.FillPath(b, path);
        }

        void SetScrollPos(int pos)
        {
            var inner = Shell.Inner;
            if (inner == null || !inner.IsHandleCreated) return;
            try { inner.HorizontalScroll.Value = Math.Max(0, pos); }   // 竖条同款: 只有 Value 赋值能精确落位
            catch { }
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            SCROLLINFO si;
            if (!GetH(out si)) return;
            int trackLeft, trackLen, gx, gw;
            if (!ThumbGeometryH(si, out trackLeft, out trackLen, out gx, out gw)) return;
            if (e.X >= gx && e.X < gx + gw)
            {
                dragThumb = true;
                dragGrabX = e.X - gx;
                DragStarts++;
                Capture = true;
            }
            else SetScrollPos(si.nPos + (e.X < gx ? -trackLen : trackLen));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!barHot) { barHot = true; Invalidate(); }
            if (!dragThumb) return;
            SCROLLINFO si;
            if (!GetH(out si)) return;
            int trackLeft, trackLen, gx, gw;
            if (!ThumbGeometryH(si, out trackLeft, out trackLen, out gx, out gw)) return;
            int maxLeft = si.nMax + 1 - si.nPage;
            if (maxLeft <= 0) return;
            int pos = (int)Math.Round((e.X - trackLeft - dragGrabX) * (double)maxLeft / Math.Max(1, trackLen - gw));
            pos = Math.Max(0, Math.Min(maxLeft, pos));
            DragMoves++;
            SetScrollPos(pos);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!barHot) return;
            barHot = false;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragThumb) { dragThumb = false; Capture = false; Invalidate(); }
        }

        /// <summary>倾斜滚轮 / 触摸板横滑 → 转给 Inner 的横向滚动 (竖条转发 WM_MOUSEWHEEL 的镜像)。</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            var inner = Shell.Inner;
            if (inner == null || !inner.IsHandleCreated || e.Delta == 0) return;
            var p = PointToScreen(e.Location);
            SendMessage(inner.Handle, WM_MOUSEHWHEEL,
                (IntPtr)((e.Delta << 16) & unchecked((int)0xFFFF0000)),
                (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF)));
        }
    }

    /// <summary>
    /// 输出卡片区滚动壳: 把卡片区 (FlowLayoutPanel) 的原生竖向滚动条改成与日志圆角壳
    /// (<see cref="RoundLogShell"/>) **同一套**的样子 (用户批85: "输出卡片的滑动条也和日志
    /// 内容区域的做一样")。两件事:
    /// <para>① 藏原生条: Inner 窗口比可视区加宽一条滚动条宽 (SM_CXVSCROLL 初估 + ClientSize
    /// 实测自校正), 窗口区域切在 client 右缘 —— 原生条整条落在可见区域之外, 看不见点不到。</para>
    /// <para>② 自绘细条: <see cref="CardBarOverlay"/> 盖在卡片区窗口之上 (父窗口被
    /// WS_CLIPCHILDREN 裁进子矩形, 画不到留白带, 必须用顶层兄弟控件画), 4px 圆头灰条,
    /// 拖拽 / 点击翻页 / 滚轮全在覆盖层处理。</para>
    /// <para>留白 (批89/91): 左右下各 PadX, 上 PadX/2 (用户要求上留白缩一半), 滑条算在
    /// 右侧留白里 (不占内容宽), 滑条右缘贴壳右缘。不加边框 (批90: 用户看过圆角框后要求撤掉)。
    /// 批95: 挤压出横滚 (窄过 CardGrid.HardMinW) 时, 底部留白带画与竖条同款的横向滑条
    /// (<see cref="CardHBarOverlay"/>), 原生横条同样被窗口区域裁掉。</para>
    /// </summary>
    class CardScrollShell : Panel
    {
        static int StripW { get { return Dpi.S(10); } }   // 滑条带/命中区宽 (设计 px)
        static int PadX { get { return Dpi.S(10); } }     // 四边内衬留白 (设计 px, 一致) ——
                                                          // 批89: 上下左右留白一致, 滑条算在留白里

        public readonly BufferedFlowPanel Inner;
        public readonly CardBarOverlay Bar;
        public readonly CardHBarOverlay HBar;   // 横向滑条 (批95): 挤压出横滚时画在底部留白带
        int textEnd;                 // 卡片可视区右缘 (壳坐标, 不含): 右侧留白带从此到框线

        public CardScrollShell(BufferedFlowPanel cards)
        {
            Inner = cards;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.ResizeRedraw, true);
            Controls.Add(cards);
            cards.ScrollChanged += (s, e) => RefreshBar();
            cards.Scroll += (s, e) => RefreshBar();     // AutoScrollPosition 程序赋值不发 WM_VSCROLL
            // 原生条显隐 (内容增删 / 竖向放不下转横滚) 会**直接改 Inner 的 ClientSize**,
            // 不走壳的 Resize 也不换句柄 —— 原 Region 没跟着重切, 原生条就露在
            // "client 右缘 .. 壳右缘"的留白带里, 与自绘条并排 (用户批98: 两个滚动条)。
            // client 一变就重排 (Recalc 里有 IsDisposed 守卫, 构造期不触发)。
            cards.ClientSizeChanged += (s, e) => Recalc();
            // 句柄重建 (AutoScroll 开关等会换新 HWND) 会丢窗口 Region —— 原生条整条漏出来;
            // 与 RoundLogShell 同一套兜底: 句柄重建后重摆+重裁
            cards.HandleCreated += OnInnerHandleCreated;
            Bar = new CardBarOverlay(this);
            Controls.Add(Bar);
            Bar.BringToFront();                         // 必须在卡片区窗口之上 (画滑条、接条带鼠标)
            HBar = new CardHBarOverlay(this);
            Controls.Add(HBar);
            HBar.BringToFront();
            Size = new Size(559, 166);
        }

        void OnInnerHandleCreated(object sender, EventArgs e)
        {
            if (IsHandleCreated) BeginInvoke((Action)Recalc);   // 等布局稳定后再裁
        }

        /// <summary>滚动位置/范围变了 → 重画滑条 (宿主在卡片增删/栅格重排后也调它)。</summary>
        public void RefreshBar()
        {
            if (Bar != null && Bar.IsHandleCreated) Bar.RefreshBar();
            if (HBar != null && HBar.IsHandleCreated) HBar.RefreshBar();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            BeginInvoke((Action)Recalc);
        }

        protected override void OnResize(System.EventArgs eventargs)
        {
            base.OnResize(eventargs);
            Recalc();
        }

        bool recalcBusy;            // 防重入: Recalc 里 SetBounds 又触发 ClientSizeChanged

        void Recalc()
        {
            if (Inner == null || IsDisposed || recalcBusy) return;
            recalcBusy = true;
            try
            {
            // 四边留白 (批89), 批91 调整: 上留白缩一半 (用户要求) —— 上 PadX/2,
            // 左/右/下仍 PadX。滑条带依旧算在右侧留白里。
            int padTop = PadX / 2;
            int ih = Math.Max(1, Height - padTop - PadX);
            int textW = Math.Max(StripW + 40, Width - 2 * PadX);
            if (Inner.IsHandleCreated)
            {
                // Inner 窗口宽 = 可视宽 + 原生竖条宽, 高 = 可视高 + 原生横条宽 (批95,
                // 竖条同款数学: 挤压出横滚时 Inner 窗口加高一条横条高, client 实测
                // 自校正) —— client == 可视区时内容铺满可视区, 原生条 (竖右横下) 都
                // 落在 client 之外的非客户区, 由下面的窗口区域整体裁掉
                int cyH = Inner.HorizontalScroll.Visible ? Math.Max(1, GetSystemMetrics(3)) : 0;   // SM_CYHSCROLL
                int w = textW + Math.Max(1, GetSystemMetrics(2));                                   // SM_CXVSCROLL
                int h = ih + cyH;
                Inner.SetBounds(PadX, padTop, w, h);
                int cw = Inner.ClientSize.Width;
                if (cw != textW)
                {
                    w = Math.Max(1, w + textW - cw);
                    Inner.SetBounds(PadX, padTop, w, h);
                }
                int ch = Inner.ClientSize.Height;
                if (ch != ih)
                {
                    // 横条显隐与预估差一档 (SetBounds 前读到的是上一状态): client 高 !=
                    // 可视高 → 按差值再修一次, 目标是 client 高恰好 = ih (批85 同款)
                    h = Math.Max(1, h + ih - ch);
                    Inner.SetBounds(PadX, padTop, w, h);
                }
            }
            // 窗口区域: 宽切在 client 右缘, 高切在 client 底缘 (min clientH/可视高) ——
            // 竖条在 client 右侧、横条在 client 底部, 整条都在区域之外, 看不见点不到
            int cut = Math.Max(1, Math.Min(Inner.IsHandleCreated ? Inner.ClientSize.Width : textW, textW));
            int cutH = Math.Max(1, Math.Min(Inner.IsHandleCreated ? Inner.ClientSize.Height : ih, ih));
            textEnd = PadX + cut;
            Region old = Inner.Region;
            Inner.Region = new Region(new Rectangle(0, 0, cut, cutH));
            if (old != null) old.Dispose();
            // 竖条覆盖层恰好盖住右侧留白带 (卡片可视区右缘 .. 壳右缘): 画滑条、接条带鼠标事件
            int barW = Math.Max(1, Width - textEnd);
            if (Bar.Left != textEnd || Bar.Top != padTop || Bar.Width != barW || Bar.Height != ih)
                Bar.SetBounds(textEnd, padTop, barW, ih);
            // 横条覆盖层 (批95) 盖住底部留白带 (padTop+ih .. 壳底), 宽 = 卡片可视宽:
            // 有横滚需求时画 4px 圆头灰条 (与竖条同款), 没有就只铺底色 (隐形)
            int hbarH = Math.Max(1, Height - padTop - ih);
            if (HBar.Left != PadX || HBar.Top != padTop + ih || HBar.Width != textEnd - PadX || HBar.Height != hbarH)
                HBar.SetBounds(PadX, padTop + ih, Math.Max(1, textEnd - PadX), hbarH);
            }
            finally { recalcBusy = false; }
        }

        /// <summary>
        /// 卡片可视宽度 (像素): 宿主用它替代 cardHost.ClientSize.Width 算栅格 ——
        /// 后者会随原生滚动条显隐跳动 (差一条滚动条宽), 这里永远是裁剪后的稳定值。
        /// </summary>
        public int ContentWidth
        {
            get { return Math.Max(1, Math.Min(Width - 2 * PadX, textEnd - PadX)); }
        }
        // 批89 曾给壳画过圆角边框 (与日志壳同一套), 用户看后改主意"还是不要那个框"
        // —— 框撤了 (批90), 四边留白一致与滑条贴右缘保留。

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);
    }
    // 不接受焦点的 SplitContainer: 点击/拖动分隔条不产生焦点虚线框
    class NoFocusSplitContainer : SplitContainer
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetFocus();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SetFocus(IntPtr hWnd);

        // ---- 批78 拖动防闪 ----
        // 实时拖动每步都重排面板 + 整面重画滑带; 容器/面板/原生文本框各自擦背景与
        // 重绘交错时会闪。容器自身滑带绘制走双缓冲 (见 ctor), 拖动期间再给整棵子树
        // 开 WS_EX_COMPOSITED: 系统把子控件 (面板/文本框等) 以自底向上顺序合成进一
        // 层缓冲再统一上屏, 消除子控件逐个擦背景的闪帧 (文本框是原生控件, 托管双缓冲
        // 帮不到它, 只有这条覆盖得到)。
        const int GWL_EXSTYLE = -20;
        const int WS_EX_COMPOSITED = 0x02000000;

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
        static int GetWindowLong(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8 ? (int)GetWindowLongPtrW(hWnd, nIndex) : GetWindowLongW(hWnd, nIndex);
        }
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);
        static void SetWindowLong(IntPtr hWnd, int nIndex, int value)
        {
            if (IntPtr.Size == 8) SetWindowLongPtrW(hWnd, nIndex, (IntPtr)value);
            else SetWindowLongW(hWnd, nIndex, value);
        }

        /// <summary>拖动期间给容器子树加/去 <see cref="WS_EX_COMPOSITED"/> (探针可读回验证)。</summary>
        void SetSubtreeComposited(bool on)
        {
            if (!IsHandleCreated) return;
            int style = GetWindowLong(Handle, GWL_EXSTYLE);
            int want = on ? (style | WS_EX_COMPOSITED) : (style & ~WS_EX_COMPOSITED);
            if (want != style) SetWindowLong(Handle, GWL_EXSTYLE, want);
        }

        internal bool SubtreeComposited
        {
            get { return IsHandleCreated && (GetWindowLong(Handle, GWL_EXSTYLE) & WS_EX_COMPOSITED) != 0; }
        }

        public NoFocusSplitContainer()
        {
            // 滑带绘制双缓冲: 实时拖动时每步整面重画滑带 (先铺浅底再画深带),
            // 不缓冲的话两步之间会闪出浅色帧 (批78 用户报告"拖动时会闪烁")。
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

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

        /// <summary>
        /// 拖动结束 (WM_LBUTTONUP, 落位之后)。历史上 SplitContainer 的拖动用 **XOR
        /// 反色预览条**: 拖动中把滑带位置的屏幕条带按位反转、松手再反转一次"擦除",
        /// 擦除与重绘交错时会留残迹 (深带 25 → 反色 230, 把手条 140 → 115)。批 77 起
        /// 拖动已改为 <see cref="LiveDrag"/> 实时落位, 原生 XOR 预览不再参与; 本事件
        /// 保留为松手后的**兜底重画通知** —— 宿主把滑带区整条重画一遍, 任何旧机制
        /// 残留的痕迹都在这里被盖掉。
        /// </summary>
        public event EventHandler DragFeedbackEnd;

        /// <summary>诊断: DragFeedbackEnd 触发次数 (探针读)。</summary>
        internal int DragEndCount;

        /// <summary>诊断: 实时拖动中应用距离的次数 (探针读, &gt;0 = 实时拖动生效)。</summary>
        internal int LiveDragMoves;

        bool _liveDrag;                     // 接管式实时拖动进行中
        int _dragStartY;                    // 按下时的鼠标 y (容器坐标)
        int _dragOrigDist;                  // 按下时的 SplitterDistance (位移基准, 避免逐帧累计误差)

        /// <summary>
        /// 实时拖动 (替代原生 XOR 反色预览条): 鼠标按下滑带后, 每次移动直接把
        /// 分隔距离落到新位置 —— 面板、滑带、喇叭口走**正常重绘**整体跟着走。
        /// <para>为什么要接管: 原生预览矩形只覆盖容器界内的中段直带, 而滑带两端的
        /// 喇叭口画在左右让边里 (MainForm.PaintSplitterBleed), 预览延伸不到那里 ——
        /// 拖动时看起来"只有中间的在动" (用户截图)。实时落位后预览与静止形态完全一致,
        /// XOR 棋盘花纹一类的问题也从根上消失。</para>
        /// <para>夹取: 上限留 1px 余量 (面板高度不归零), 整体收起留到松手时落位 ——
        /// 与原生"松手才落位"的语义一致 (拖到最底 = 松手后整体收起日志面板)。</para>
        /// </summary>
        void ApplyLiveDrag(int y)
        {
            int min = Panel1MinSize;
            int max = Height - SplitterWidth - Panel2MinSize - 1;
            if (max < min) max = min;
            int dist = _dragOrigDist + (y - _dragStartY);
            if (dist < min) dist = min;
            if (dist > max) dist = max;
            if (dist != SplitterDistance)
            {
                try { SplitterDistance = dist; } catch { }   // 触发 SplitterMoved → 宿主重画
                LiveDragMoves++;
                // 两窗同步冲刷 (批79): 滑带/把手画在容器 (子窗口) 表面, 两端喇叭口画在
                // 窗体表面 —— 分属两个 HWND、各自双缓冲上屏。快速拖动时两者的 WM_PAINT
                // 若落入不同帧, 圆口就会相对滑带抖动 (用户报告"拖动时圆角会抖动")。
                // 每步落位后立刻把两窗的脏区都刷上屏, 拖动的每一帧都是对齐的。
                if (IsHandleCreated) Update();
                Form f = Parent as Form;
                if (f != null && f.IsHandleCreated) f.Update();
            }
        }

        /// <summary>WM_XXX 的 LPARAM → 客户区坐标 (有符号 16 位两半)。</summary>
        static Point LParamPt(IntPtr lp)
        {
            int v = lp.ToInt32();
            return new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0201)                       // WM_LBUTTONDOWN: child 能收到, 此刻记录旧焦点 (Focus 尚未发生)
            {
                restoreFocus = GetFocus();
                // 命中滑带 → 接管拖动 (不调 base, 原生 SplitBegin/XOR 预览不再启动)。
                // 点在面板上的消息走面板自己的窗口, 到不了容器 —— 这里收到的就是滑带上的点击。
                Rectangle sr = SplitterRectangle;
                if (sr.Width > 0 && sr.Height > 0 && !Panel1Collapsed && !Panel2Collapsed
                    && ptInStrip(m.LParam, sr))
                {
                    _liveDrag = true;
                    _dragStartY = LParamPt(m.LParam).Y;
                    _dragOrigDist = SplitterDistance;
                    Capture = true;                    // 原生拖动也会设捕获, 这里自己接管
                    SetSubtreeComposited(true);        // 拖动期间子树合成绘制防闪 (见顶部注释)
                    return;
                }
                base.WndProc(ref m);
                return;
            }
            if (m.Msg == 0x0200 && _liveDrag)          // WM_MOUSEMOVE: 实时落位。跳过 base ——
            {                                          // 原生移动分支会画 XOR 反色预览条
                ApplyLiveDrag(LParamPt(m.LParam).Y);
                return;
            }
            if (m.Msg == 0x0202)                       // WM_LBUTTONUP: 结束拖动
            {
                if (_liveDrag)
                {
                    _liveDrag = false;
                    Capture = false;
                    SetSubtreeComposited(false);
                    // 拖到最底 (拖动中被夹在 max-1, 面板高度还剩 1px) → 松手落到真正的
                    // max, Panel2 高度归零 → SplitterMoved → OnSplitterMoved 整体收起
                    int min = Panel1MinSize;
                    int max = Height - SplitterWidth - Panel2MinSize;
                    if (max < min) max = min;
                    if (max > min && SplitterDistance >= max - 1)
                    {
                        try { SplitterDistance = max; } catch { }
                    }
                    DragEndCount++;
                    if (DragFeedbackEnd != null) DragFeedbackEnd(this, EventArgs.Empty);
                    return;
                }
                base.WndProc(ref m);
                DragEndCount++;
                if (DragFeedbackEnd != null) DragFeedbackEnd(this, EventArgs.Empty);
                return;
            }
            if (m.Msg == 0x0215 && _liveDrag)          // WM_CAPTURECHANGED: 捕获被意外抢走
            {                                          // (弹窗等) → 放弃拖动, 停在最后落位
                _liveDrag = false;
                SetSubtreeComposited(false);
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

        /// <summary>滑带命中: 点落在滑带矩形内 (容器只在自己裸露的滑带上收到点击)。</summary>
        bool ptInStrip(IntPtr lp, Rectangle sr)
        {
            Point pt = LParamPt(lp);
            return pt.Y >= sr.Top && pt.Y < sr.Bottom;
        }

        /// <summary>
        /// 分隔条 (用户参考图): 上下两块面板是两个圆角矩形, 深色滑带 = 它们之间的**缝** ——
        /// 中段上下直边, 两端沿窗口边缘**外扩**成圆角喇叭口 (弧线就是上下面板朝缝那侧的
        /// 圆角, 半径跟随窗口系统圆角 <see cref="SeamRadiusPx"/>)。
        /// <para>BackColor = 浅色 (与面板同支)。整个窗口内区于是读作一个完整的浅色圆角
        /// 矩形被这条缝分成上下两块。路径由 <see cref="BuildSeamPath"/> 统一描述:
        /// 容器画自己界内的一段 (局部坐标, 窗口边缘在 x=-Left / Parent宽-Left),
        /// 左右让边 (ResizeBorder) 里的两段由 MainForm.PaintSplitterBleed 用**同一条**
        /// 路径补齐 —— 两边同一套数学, 接缝才对得上。线色 <see cref="SeamLine"/>。</para>
        /// <para>日志面板整体收起 (Panel2Collapsed) 时不画 (OnPaint 显式守卫 ——
        /// SplitterRectangle 在收起态并不为空, 不能靠它自然跳过)。</para>
        /// </summary>
        /// <summary>缝线色 (批95): 与窗口边框同一支 —— DWMWA_BORDER_COLOR 用的就是
        /// <see cref="OutputCardControl.BorderColor"/> (203,208,216), 分隔条不再是
        /// 黑线, 整窗只有一套线条色 (原来 25,25,25 的黑线是批93 之前"深色滑带"的遗产)。</summary>
        internal static readonly Color SeamLine = OutputCardControl.BorderColor;

        /// <summary>
        /// 喇叭口的圆角半径 (物理像素): 跟随窗口的 Win11 系统圆角 —— 固定 8 物理像素、
        /// 不随 DPI 缩放 (用户要求"圆角弧度跟随窗口")。这里的绘制坐标全是设备像素
        /// (bounds 都是 Dpi.S 过的值), 直接用物理值即可, 不要再过 Dpi.S。
        /// </summary>
        internal const float SeamRadiusPx = 8f;

        /// <summary>
        /// 窗口圆角半径 (物理像素整数版, 不随 DPI): 批96 起所有圆角矩形 (输出卡片/
        /// 延迟壳/日志壳/下拉弹层/圆角按钮) 的圆角弧度统一用它 —— 与窗口系统圆角
        /// (<see cref="SeamRadiusPx"/>) 同一支, 不再各写各的 Dpi.S(6) (125% 下碰巧
        /// 也是 8, 但 100%/150% 就分叉了)。
        /// </summary>
        internal const int WindowRadiusPx = (int)SeamRadiusPx;

        /// <summary>
        /// 滑带路径: 上下直边 + 四个角沿左右窗口边缘外扩的四分之一圆弧 (半径 r)。
        /// 坐标: xl/xr = 左/右窗口边缘的 x, y1/y2 = 滑带上下缘。弧的圆心分别在
        /// (xl±r, y1∓r) 与 (xl±r, y2±r) —— 弧线即上下面板朝缝那侧的圆角轮廓。
        /// </summary>
        internal static System.Drawing.Drawing2D.GraphicsPath BuildSeamPath(float xl, float xr, float y1, float y2, float r)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(xl, y1 - 2f * r, 2f * r, 2f * r, 90f, 90f);           // 左上: (xl+r,y1) → (xl,y1-r)
            p.AddLine(xl, y1 - r, xl, y2 + r);                             // 左窗口边缘
            p.AddArc(xl, y2, 2f * r, 2f * r, 180f, 90f);                   // 左下: (xl,y2+r) → (xl+r,y2)
            p.AddLine(xl + r, y2, xr - r, y2);                             // 底边
            p.AddArc(xr - 2f * r, y2, 2f * r, 2f * r, 270f, 90f);          // 右下: (xr-r,y2) → (xr,y2+r)
            p.AddLine(xr, y2 + r, xr, y1 - r);                             // 右窗口边缘
            p.AddArc(xr - 2f * r, y1 - 2f * r, 2f * r, 2f * r, 0f, 90f);   // 右上: (xr,y1-r) → (xr-r,y1)
            p.CloseFigure();                                               // 顶边闭回起点
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            // 面板整体收起时滑带必须跟着消失 (批84): Panel2Collapsed 下 SplitterRectangle
            // **并不为空** —— 它仍按 SplitterDistance 取带区, 若距离还落在容器界内 (恢复
            // 流程或钳位残留), 这里会画出一段没有面板接续的悬空滑带, 表现为"隐藏了日志
            // 面板, 分隔条却还在"。
            if (Panel1Collapsed || Panel2Collapsed) return;
            Rectangle sr = SplitterRectangle;
            if (sr.Height <= 0 || sr.Width <= 0) return;
            // **无视增量裁剪, 整面重画**: 快速拖动时 WinForms 给的失效区只是增量条,
            // 若某次绘制的裁剪漏了几行, 旧位置的把手条/弧缘 AA 残迹会与新内容交错
            // 永久留在屏上 (缩放窗口才消失, 用户报告的"小幅度拖动显示异常")。
            // 这里强制按控件整面重画 —— GDI 会把子控件 (上下两块面板) 裁出去,
            // 不会画到它们身上; 滑带区只有 20 来行像素, 全画的开销可忽略。
            e.Graphics.SetClip(ClientRectangle);
            // 缝线: 整条路径用窗体客户区坐标描述、平移成本控件局部坐标 ——
            // 界内画得到的部分 (中段直边 + 喇叭口靠内一截) 在这里出来;
            // 落在左右让边里的部分 (x&lt;0 / x&gt;Width) 被控件边界裁掉, 由
            // MainForm.PaintSplitterBleed 补。同一条路径两处各画一段, 弧线无缝相接。
            float xl = -Left;
            float xr = (Parent != null ? Parent.ClientSize.Width : Left + Width) - Left;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            // 滑带经过的整条横带先铺底色 (盖掉任何旧位置的残迹), 再画路径
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : BackColor))
                e.Graphics.FillRectangle(b, 0, sr.Top - (int)SeamRadiusPx - 2, Width,
                    sr.Height + 2 * (int)SeamRadiusPx + 4);
            using (var path = BuildSeamPath(xl, xr,
                sr.Top + (sr.Height - 2) / 2, sr.Bottom - (sr.Height - 2) / 2, SeamRadiusPx))
            using (var b = new SolidBrush(SeamLine))
                e.Graphics.FillPath(b, path);
            // 缝带缩成一条细线 (批93): 视觉线只有中间 2 物理px, 上下留浅色与面板
            // 连成一体 ("缩成一条线, 只保留圆角和中间把手条"); 批95 线色改为与窗口
            // 边框同一支 (OutputCardControl.BorderColor); 端部仍沿窗口边缘外扩
            // 成喇叭口弧。中央把手圆头条改为**覆盖层控件** (SeamThumbOverlay) 画 ——
            // 条比线"突出一点" (上下各凸 2 物理px), 凸出部分落在上下面板的子窗口
            // 区域, 容器绘制被 WS_CLIPCHILDREN 裁掉画不到 (批85 CardBarOverlay 同款
            // 教训), 由 MainForm 创建并跟随分隔条移动。
        }
    }

    /// <summary>
    /// 分隔条中央把手条 (批93): 缝带视觉缩成一条 2 物理px 细线后, 把手条"突出一点"
    /// (比线粗, 上下各凸出 2 物理px) 且宽缩半 (30%→15% 缝带可视宽, 用户指定)。
    /// <para>批94 (用户: "不够圆滑"): 原来 Region 裁成胶囊 + 填充同一路径 —— Region
    /// 是**硬裁** (逐像素), 把填充路径的 抗锯齿边缘像素裁掉, 端部呈锯齿方角。
    /// 改为覆盖层矩形 = **整条缝带段** (高 = 缝带宽), 自己复刻底下的像素
    /// (缝带底色 + 细线段, 与容器 OnPaint 同一套公式逐像素对齐), 再用抗锯齿
    /// 胶囊画把手条 —— 所有 AA 都落进自己的缓冲区, 不再需要 Region; 条外的鼠标
    /// 命中仍靠 <see cref="WndProc"/> 回 HTTRANSPARENT 穿透到下层缝带 (条上照样
    /// 能按住拖分隔条)。</para>
    /// <para>为什么是**窗体级覆盖层**: 把手条比细线凸出的部分落在上下面板的子窗口
    /// 区域, 容器与窗体的绘制都被 WS_CLIPCHILDREN 裁进子窗口矩形之外画不到
    /// (批85 CardBarOverlay 同款教训)。</para>
    /// </summary>
    class SeamThumbOverlay : Control
    {
        static readonly Color ThumbColor = Color.FromArgb(140, 140, 140);   // 原容器把手条同色
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;
        /// <summary>胶囊左右各让 2 物理px 给抗锯齿渐变 (端部圆滑的关键)。</summary>
        internal const int AaMargin = 2;

        public SeamThumbOverlay()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // 1) 整面铺缝带底色 —— 与容器 OnPaint 同源 (容器画的是它 Parent.BackColor =
            //    窗体 BackColor), 逐像素同色, 与条两侧的容器绘制无缝相接
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : BackColor))
                g.FillRectangle(b, 0, 0, Width, Height);
            // 2) 细线段: 与容器同一公式 (y1=(H-2)/2, y2=H-(H-2)/2), 容器在条两侧
            //    画的是同一条线, 这里接上中间被条盖住的一段 (平头矩形, 与容器的
            //    直线段完全同行 —— 容器画线边缘恰在整像素, 无 AA); 批95 线色 =
            //    窗口边框色 (SeamLine)
            int ly1 = (Height - 2) / 2, ly2 = Height - (Height - 2) / 2;
            using (var b = new SolidBrush(NoFocusSplitContainer.SeamLine))
                g.FillRectangle(b, 0, ly1, Width, ly2 - ly1);
            // 3) 圆头条: 高 = 缝带宽 - 2 (比线上下各凸出 2 物理px), 左右让 AaMargin 给
            //    AA 渐变 —— 端部圆滑 (批94)。批97: 端部改**椭圆角** —— 垂直半径仍钳到
            //    半高 (几何必然), 水平半径用窗口圆角 (WindowRadiusPx=8): 细长条装不下
            //    8px 的正圆弧, 但端部弧的**横向跨度**与窗口四角一致 (用户 "所有圆角
            //    包括分隔条的") —— 半圆头 (rx=3) 换平缓椭圆头 (rx=8), 弧线更舒展。
            float th = Height - 2f;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            using (var path = UiDraw.RoundRectEx(new RectangleF(AaMargin, 1f, Width - 2 * AaMargin, th),
                NoFocusSplitContainer.WindowRadiusPx, th / 2f))
            using (var b = new SolidBrush(ThumbColor))
                g.FillPath(b, path);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
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
        /// <summary>焦点提示是否该显示 (键盘进来才显示, 鼠标点进来不显示)。与 RoundedCombo 同一套判定。</summary>
        bool keyboardFocusCues = true;
        ButtonGlyph glyph = ButtonGlyph.Nut;
        /// <summary>图标相对按钮边长的缩放 (默认 1)。给"按钮热区要够大、图案却要小"的场合用 ——
        /// 标题栏的齿轮就是: 26px 的热区配 10px 级的图案, 才和旁边的系统窗口键一个重量。</summary>
        public float GlyphScale = 1f;

        // —— 刷新图标的旋转: 点一次转满一圈, 回到原位 ——
        System.Windows.Forms.Timer spinTimer;
        float spinAngle;
        long spinStartMs;
        /// <summary>转满一圈用多久 (ms)。约 1.7 圈/秒 —— 再快看不清在转, 再慢像卡住。
        /// <para>MainForm 要按它算"等转完再开始枚举"的时机, 所以对外可见。</para></summary>
        internal const int SpinOnceMs = 560;

        /// <summary>图标此刻是否在转。</summary>
        public bool IsSpinning { get { return spinTimer != null && spinTimer.Enabled; } }

        /// <summary>鼠标是否停在按钮上 (壳据此把整组的框一起加深)。</summary>
        internal bool IsHot { get { return hot; } }

        /// <summary>
        /// 悬停/按下状态变了。曾经给 ComboButtonShell 组合壳用 (壳已删), 事件保留:
        /// 想让外部跟着按钮的深浅联动重画时订阅它。
        /// </summary>
        internal event Action VisualStateChanged;
        void NotifyVisual()
        {
            var h = VisualStateChanged;
            if (h != null) h();
        }

        /// <summary>
        /// 让 Refresh 图标转满一圈回到原位 (点一次 = 一圈)。
        /// <para>用 Timer 而不是在点击处理里循环: 设备枚举是**同步**跑的, 循环里界面画不出来。</para>
        /// <para>一圈, 不是"一直转等人喊停": 转圈表达的是"这次点击生效了", 该有始有终。
        /// 连续转要靠外部停它, 停在哪一帧图标就歪着停在哪 —— 只有转满 360° 才能保证
        /// 停下来时正好是原位。</para>
        /// </summary>
        public void StartSpin()
        {
            if (spinTimer == null)
            {
                spinTimer = new System.Windows.Forms.Timer { Interval = 16 };
                spinTimer.Tick += (s, e) =>
                {
                    // 角度由**流逝时间**算, 不是每帧累加: 枚举卡住掉十几帧也照样 560ms 转完,
                    // 不会因为丢帧而少转几十度、停在歪角度上。
                    float t = (NowMs() - spinStartMs) / (float)SpinOnceMs;
                    if (t >= 1f) { EndSpin(); return; }
                    // 先快后慢 (ease-out quad): 点下去当场就在动, 末尾平滑落到原位。
                    // 缓入的头 100ms 几乎不动, 会被当成"点了没反应"。
                    float eased = 1f - (1f - t) * (1f - t);
                    spinAngle = 360f * eased;
                    Invalidate();
                };
            }
            if (spinTimer.Enabled) return;   // 已在转: 让它转完这一圈, 不从头来 (重来会看到图标瞬间跳回 0°)
            spinStartMs = NowMs();
            spinAngle = 0f;
            spinTimer.Start();
        }

        void EndSpin()
        {
            spinAngle = 0f;      // 360° 与 0° 是同一位置, 归零没有跳变
            if (spinTimer != null) spinTimer.Stop();
            Invalidate();
        }

        static long NowMs()
        {
            // 与 RoundedCombo.NowMs 同一套: Stopwatch 的基准是性能计数器, 不受系统校时影响
            return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && spinTimer != null)
            {
                spinTimer.Stop();
                spinTimer.Dispose();
                spinTimer = null;
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// 贴到父容器边角时, 由父容器把自己的外框补画一遍。
        /// <para>子控件在父容器**之后**绘制: 按钮一旦压住父容器的边框, 那一段顶边/右边连同圆角弧
        /// 就没了, 圆角外侧还会露出按钮的方角白块。父容器才直到自己的圆角半径和边框色, 所以让它
        /// 用**按钮坐标**画一遍 —— 按钮只负责在画完自己的底/悬停/图标之后调这个回调。</para>
        /// <para>卡片右上角的「×」用它把卡片外框补回来 (见 OutputCardControl)。</para>
        /// </summary>
        public Action<Graphics> OwnerFrame { get; set; }

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

        // 自绘 Control 默认把空格当"滚动/默认按钮"处理, 拿不到 KeyDown; 声明成输入键才行。
        // 只对 TabStop 的实例有意义 (齿轮/× 都是 TabStop=false, 不受影响)。
        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Space || k == Keys.Enter) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && Enabled)
            {
                down = true;                 // 与鼠标按下同一套按下态, 反馈一致
                Invalidate();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                if (down)
                {
                    down = false;
                    Invalidate();
                    OnClick(EventArgs.Empty);
                }
                e.Handled = true;
            }
            base.OnKeyUp(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); NotifyVisual(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; down = false; Invalidate(); NotifyVisual(); base.OnMouseLeave(e); }

        protected override void OnGotFocus(EventArgs e)
        {
            // 到这一刻左键还按着 → 是"点进来"的, 别画焦点框; 键已松开 → Tab 进来的, 要画
            keyboardFocusCues = Control.MouseButtons == MouseButtons.None;
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                down = true;
                // 焦点已经在本控件上时 (Tab 进来再点鼠标) 不会再有 GotFocus, 这里必须自己关掉焦点框
                keyboardFocusCues = false;
                Invalidate();
            }
            base.OnMouseDown(e);
        }
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
                using (var path = UiDraw.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1),
                    NoFocusSplitContainer.WindowRadiusPx))
                using (var fill = new SolidBrush(down ? PressBack : HoverBack))
                    g.FillPath(fill, path);
            }

            float cx = (Width - 1) / 2f;
            float cy = (Height - 1) / 2f + (down ? 1f : 0f);
            Color icon = Enabled ? ((hot || down) ? IconActive : IconColor) : Color.FromArgb(170, 170, 170);

            // 刷新时整圈绕着按钮中心转: 只转 Refresh 一个图形 (螺母/× 转起来没有语义)
            bool spun = glyph == ButtonGlyph.Refresh && spinAngle != 0f;
            System.Drawing.Drawing2D.GraphicsState spinState = null;
            if (spun)
            {
                spinState = g.Save();
                g.TranslateTransform(cx, cy);
                g.RotateTransform(spinAngle);
                g.TranslateTransform(-cx, -cy);
            }

            switch (glyph)
            {
                case ButtonGlyph.Nut:
                    // 设置: 六角螺母 —— 六边形一圈墙 + 中心一个圆环 (照用户给的 SVG 描的)。
                    // 原图坐标: 六角对宽 824×903 (k = 24/903), 墙 85 (≈2.3px), 圆环外径 170.7 内径 85.3。
                    // GlyphScale: 标题栏里的齿轮要与旁边 10px 的系统窗口键同一视觉重量,
                    // 按默认比例 (≈0.85 倍按钮边长) 画出来会大出一圈, 抢掉标题的注意力。
                    DrawNut(g, cx, cy, Math.Min(Width, Height) * 0.0266f * GlyphScale, icon,
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

                case ButtonGlyph.TriangleDown:
                case ButtonGlyph.TriangleRight:
                    // 多声道行的折叠标记。用**实心**三角而不是描边钩:
                    // 延迟框右缘就是它自带的上下微调箭头 (细描边), 旁边再来一个描边的 "v"
                    // 会被读成那个微调器的第三个箭头 (试过, 3 倍放大下确实分不清)。
                    // 实心块在整排描边图标里一眼就是"另一种东西"。
                    {
                        bool rightward = glyph == ButtonGlyph.TriangleRight;
                        PointF[] tri = rightward
                            ? new[] { new PointF(cx - 2.2f, cy - 4.2f), new PointF(cx - 2.2f, cy + 4.2f), new PointF(cx + 3.4f, cy) }
                            : new[] { new PointF(cx - 4.2f, cy - 2.2f), new PointF(cx + 4.2f, cy - 2.2f), new PointF(cx, cy + 3.4f) };
                        using (var b = new SolidBrush(icon))
                            g.FillPolygon(b, tri);
                    }
                    break;

                case ButtonGlyph.Refresh:
                    // 「刷新设备」: 圆环 + 右上角实心直角旗标 (照用户给的 SVG 描的)。
                    // 原图 1024 视图: 圆环外径 382 内径 287 (中线 334.5, 笔宽 95.6),
                    // 旗标是被斜边(过圆心)切开的直角三角形, 直角顶在 (889,459)-(889,125)。
                    // 22px 下: 半径 7.18、笔宽 2.05、旗标顶点见 DrawRefresh。
                    DrawRefresh(g, cx, cy, Math.Min(Width, Height) / 22f, icon);
                    break;

                case ButtonGlyph.More:
                    // 「更多」: 三条横线 (用户指定, 替换原设置齿轮)。线条几何**不过 GlyphScale**:
                    // 这枚图形只在标题栏用 (26px 热区), 直接按"与旁边 10px 级窗口键同一视觉重量"
                    // 定尺寸 —— 线长 ±6.5、行距 4.5、笔宽 1.8, 与 SquarePlus 的线条语言一致。
                    // 不走 GlyphScale 是因为 0.62 的系数是给螺母的原图比例配的, 套上来只剩 8px。
                    using (var p = new Pen(icon, 1.8f))
                    {
                        p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        for (int i = -1; i <= 1; i++)
                        {
                            float y = cy + i * 4.5f;   // 按下下沉已含在 cy 里, 这里不再加
                            g.DrawLine(p, cx - 6.5f, y, cx + 6.5f, y);
                        }
                    }
                    break;
                case ButtonGlyph.LogOn:
                case ButtonGlyph.LogOff:
                    // 「日志面板」开关 (标题栏, 更多按钮左边): 圆角小窗 + 两条文字行 = 显示;
                    // 同一扇窗加一道斜杠 = 隐藏 (与捕获消音的 MicSlash 同一套斜杠语言)。
                    // 与 More 三条线同一笔画 (1.7~1.9px 圆头), 不过 GlyphScale (只在标题栏用)。
                    using (var p = new Pen(icon, 1.7f))
                    {
                        p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                        using (var path = UiDraw.RoundRect(new RectangleF(cx - 7f, cy - 6f, 14f, 12f), 3f))
                            g.DrawPath(p, path);
                        if (glyph == ButtonGlyph.LogOn)
                        {
                            g.DrawLine(p, cx - 3.5f, cy - 2f, cx + 3.5f, cy - 2f);
                            g.DrawLine(p, cx - 3.5f, cy + 2f, cx + 1.5f, cy + 2f);
                        }
                        else
                        {
                            using (var ps = new Pen(icon, 1.9f)
                            {
                                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                                EndCap = System.Drawing.Drawing2D.LineCap.Round
                            })
                                g.DrawLine(ps, cx - 6.5f, cy - 7f, cx + 6.5f, cy + 7f);
                        }
                    }
                    break;

            }

            if (spun) g.Restore(spinState);

            // 键盘焦点: 一圈蓝色细框 (与下拉框的焦点色同一支蓝)。只在键盘进来时画 ——
            // 鼠标点一下就把蓝框挂在图标上, 看着像"没取消的选中态"
            if (Focused && keyboardFocusCues && Enabled)
            {
                using (var path = UiDraw.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f),
                    NoFocusSplitContainer.WindowRadiusPx))
                using (var pen = new Pen(Color.FromArgb(0, 120, 215), 1.4f))
                    g.DrawPath(pen, path);
            }

            // 最后补画父容器外框: 既盖住按钮方角露到父容器圆角外的那点白, 也把被压住的
            // 那一段边框重新描一遍 (顺序必须在图标之后, 否则边框会被悬停底盖掉)
            if (OwnerFrame != null) OwnerFrame(g);
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
                using (var path = UiDraw.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1),
                    NoFocusSplitContainer.WindowRadiusPx))
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
        /// <summary>
        /// 圆角矩形路径 (浮点版)。**1px 描边必须用它**: 传 (0.5, 0.5, W-1, H-1) 让笔心落在像素中心,
        /// 线正好覆盖一整列像素, 是纯黑; 用整数矩形时笔心在像素边界上, 抗锯齿把 1px 拆成
        /// 两条 50% 灰 —— 放大看就是"边框发灰、比 1px 粗"。
        /// </summary>
        public static System.Drawing.Drawing2D.GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            float max = Math.Min(r.Width, r.Height) / 2f;
            float d = Math.Min(radius, max) * 2f;
            if (d <= 0f)
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

        /// <summary>
        /// 椭圆圆角矩形路径: 水平半径 rx 与垂直半径 ry 各自独立 (CSS border-radius:
        /// rx/ry 的等价物)。给**高度不足**的细长条用 —— 端部圆头的垂直半径被高度
        /// 钳到半高是几何必然, 但水平半径可以拉到窗口圆角那么大, 端部弧的横向
        /// 跨度便与窗口四角一致 (用户批97: "所有圆角包括分隔条的")。
        /// </summary>
        public static System.Drawing.Drawing2D.GraphicsPath RoundRectEx(RectangleF r, float rx, float ry)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            float mx = Math.Min(rx, r.Width / 2f), my = Math.Min(ry, r.Height / 2f);
            float dx = mx * 2f, dy = my * 2f;
            if (dx <= 0f || dy <= 0f)
            {
                path.AddRectangle(r);
            }
            else
            {
                path.AddArc(r.X, r.Y, dx, dy, 180, 90);
                path.AddArc(r.Right - dx, r.Y, dx, dy, 270, 90);
                path.AddArc(r.Right - dx, r.Bottom - dy, dx, dy, 0, 90);
                path.AddArc(r.X, r.Bottom - dy, dx, dy, 90, 90);
                path.CloseFigure();
            }
            return path;
        }

        /// <summary>1px 描边用的路径: 客户区四边各内缩半像素。</summary>
        public static System.Drawing.Drawing2D.GraphicsPath PenRect(Control c, int radius)
        {            return RoundRect(new RectangleF(0.5f, 0.5f, c.Width - 1f, c.Height - 1f), radius);
        }

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
