using System;
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

        /// <summary>把滚轮消息原样投给指定控件 (下拉展开时用它滚选项列表)。</summary>
        public static void Deliver(Control target, Message m)
        {
            if (target == null || !target.IsHandleCreated) return;
            SendMessage(target.Handle, m.Msg, m.WParam, m.LParam);
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
    enum ButtonGlyph { None, Plus }

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
            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_LBUTTONUP = 0x0202;
            const int WM_LBUTTONDBLCLK = 0x0203;
            const int WM_RBUTTONDOWN = 0x0204;
            const int WM_RBUTTONDBLCLK = 0x0206;
            const int WM_CAPTURECHANGED = 0x0215;

            readonly NoWheelNumeric owner;
            readonly Control host;

            public SpinButtonsHook(NoWheelNumeric owner, Control host)
            {
                this.owner = owner;
                this.host = host;
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_LBUTTONDOWN)
                {
                    int dir = DirOf(m.LParam);
                    if (dir != 0)
                    {
                        owner.StepArrow(dir);
                        owner.BeginRepeat(dir);
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
                    owner.EndRepeat();
                }
                base.WndProc(ref m);
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
    /// 设置入口按钮: 自绘齿轮。
    /// <para>不用字体图标 (Segoe MDL2 在精简版/老系统上未必存在, 缺字时会画成方块),
    /// 也不用文字 "⚙" (不同字体渲染差异大, 有的字体画成黑白两色 emoji)。
    /// 路径直接画: 8 个齿 + 圆盘 + 挖孔, 只在悬停/按下时给一层浅底, 与主界面其余扁平控件一致。</para>
    /// </summary>
    class GearButton : Control
    {
        static readonly Color IconColor = Color.FromArgb(95, 94, 90);
        static readonly Color IconActive = Color.FromArgb(44, 44, 42);
        static readonly Color HoverBack = Color.FromArgb(234, 234, 234);
        static readonly Color PressBack = Color.FromArgb(216, 216, 216);

        bool hot, down;

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

            if (hot || down)
            {
                using (var path = UiDraw.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 6))
                using (var fill = new SolidBrush(down ? PressBack : HoverBack))
                    g.FillPath(fill, path);
            }

            float cx = Width / 2f;
            float cy = Height / 2f + (down ? 1f : 0f);
            float rOut = Math.Min(Width, Height) * 0.36f;
            Color icon = Enabled ? ((hot || down) ? IconActive : IconColor) : Color.FromArgb(170, 170, 170);
            DrawGear(g, cx, cy, rOut, rOut * 0.70f, rOut * 0.28f, icon, (hot || down) ? (down ? PressBack : HoverBack) : back);
        }

        /// <summary>齿轮: 8 个齿绕中心旋转铺开 + 实心圆盘 + 用底色挖出中心孔。</summary>
        static void DrawGear(Graphics g, float cx, float cy, float rOut, float rBody, float rHole, Color icon, Color holeBack)
        {
            const int teeth = 8;
            using (var brush = new SolidBrush(icon))
            {
                float tw = Math.Max(2f, rOut * 0.40f);
                float len = rOut - rBody + 1.4f;
                System.Drawing.Drawing2D.GraphicsState st = g.Save();
                g.TranslateTransform(cx, cy);
                for (int i = 0; i < teeth; i++)
                {
                    g.RotateTransform(360f / teeth);
                    g.FillRectangle(brush, -tw / 2f, -rOut, tw, len);
                }
                g.Restore(st);
                g.FillEllipse(brush, cx - rBody, cy - rBody, rBody * 2, rBody * 2);
            }
            using (var bg = new SolidBrush(holeBack))
                g.FillEllipse(bg, cx - rHole, cy - rHole, rHole * 2, rHole * 2);
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
