using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// 全自绘下拉框: 圆角矩形本体 + 右侧「﹀」箭头 + 圆角弹层 + 灰色选中高亮。
    ///
    /// <para><b>为什么不用 ComboBox</b>: 外框和右侧箭头由 ComboBox 的系统窗口类绘制, FlatStyle 只有
    /// System/Standard/Flat 三档, 边框颜色改不了, 圆角也做不了; 而 DropDownList 的 Height 是无效属性
    /// (高度由 ItemHeight 推出来, 实测 高 = ItemHeight + 6)。要"圆角 + 不带小框的箭头 + 圆角弹层",
    /// 只能整个重写控件。</para>
    ///
    /// <para><b>换来了什么</b>: 高度可以任意指定, 本体与弹层都能圆角, 选中项用界面自己的灰而不是
    /// 系统 Highlight 蓝。代价是弹层、键盘、滚轮、定位全部要自己实现 —— 下面每一段都为此存在,
    /// 改动时不要只看"画得好不好看"。</para>
    ///
    /// <para><b>交互对齐原生 ComboBox</b>: 点击展开、再点收起; ↑↓ 收起时直接改值、展开时移动高亮;
    /// Home/End/PageUp/PageDown; Enter/Space 展开或提交; Esc/F4 收起; Alt+↓ 展开;
    /// 展开时滚轮滚选项, 收起时滚轮转给外层可滚容器 (卡片区)。</para>
    /// </summary>
    class RoundedCombo : Control
    {
        // ---------- 配色 ----------
        /// <summary>常态边框: 黑色细边 (用户指定)。黑色已是最深, 悬停/展开的反馈只能靠笔宽。</summary>
        static readonly Color BorderNormal = Color.Black;
        /// <summary>禁用态边框: 淡灰 —— 禁用控件用黑边会看着仍可点。</summary>
        static readonly Color BorderDisabled = Color.FromArgb(203, 208, 216);
        static readonly Color BorderActive = Color.FromArgb(0, 120, 215);
        static readonly Color TextNormal = Color.FromArgb(32, 32, 32);
        static readonly Color DisabledBack = Color.FromArgb(244, 245, 247);
        internal static readonly Color DisabledText = Color.FromArgb(150, 154, 160);
        static readonly Color ArrowNormal = Color.FromArgb(95, 94, 90);
        static readonly Color ArrowHot = Color.FromArgb(44, 44, 42);
        /// <summary>弹层里鼠标悬停的行。</summary>
        internal static readonly Color RowHover = Color.FromArgb(242, 243, 245);
        /// <summary>弹层里已选中项的高亮 (灰, 不是系统蓝)。</summary>
        internal static readonly Color RowSelected = Color.FromArgb(226, 228, 232);

        // 以下都是 96 DPI 的设计值, 访问时按系统缩放换算 (见 Dpi)。
        /// <summary>本体圆角。比卡片的 8 小一号 —— 控件矮, 同半径会显得过圆。</summary>
        static int CornerRadius { get { return Dpi.S(6); } }
        /// <summary>弹层圆角, 与输出卡片/设置分组卡一致。</summary>
        internal static int PopupRadius { get { return Dpi.S(8); } }
        /// <summary>文字左内缩。与卡片里其它控件的 8px 对齐。</summary>
        internal static int TextPadX { get { return Dpi.S(8); } }
        /// <summary>右侧给箭头留的宽度 (internal: 量测工具要靠它算文字可用宽度)。</summary>
        internal static int ArrowZone { get { return Dpi.S(22); } }
        /// <summary>弹层上下留白 (视觉上让第一行不贴边)。</summary>
        internal static int PopupPadY { get { return Dpi.S(4); } }
        /// <summary>自动收起后多久内不允许再展开 (毫秒)。见 <see cref="OpenDropDown"/> 的说明。</summary>
        const int AutoCloseGuardMs = 300;

        ComboPopup popup;
        bool open;
        bool hot;
        int selected = -1;

        /// <summary>
        /// 当前是否该画焦点描边。
        /// <para>只在"键盘操作"时画: 鼠标点一下就把蓝框挂在那儿, 用户会当成没擦掉的残留。
        /// 判据取 <see cref="Control.MouseButtons"/> 而不是自己维护标志位 —— 进入焦点那一刻鼠标键是否按下,
        /// 正好区分了"点进来"和"Tab 进来"两种来源, 不必在两条路径上各记一笔。</para>
        /// </summary>
        bool keyboardFocusCues = true;
        /// <summary>
        /// 单调毫秒时钟。
        /// <para>不用 DateTime: 它会被系统校时改动; 也不用 Environment.TickCount64 —— net48 上没这个成员
        /// (那是 .NET Core 3.0 才加的)。Stopwatch 的基准是性能计数器, 只用来算两个时刻的短差值。</para>
        /// </summary>
        static long NowMs()
        {
            return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        }

        /// <summary>上一次"非主动"收起弹层的时刻 (见 <see cref="NowMs"/>)。初值 0 = 从未收起过。</summary>
        long lastAutoClose;

        internal readonly ComboItemList Items;

        public RoundedCombo()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable, true);
            BackColor = Color.White;
            Size = new Size(120, 24);
            // 给默认值: 少设一处也不会变成"弹层只有一行"或"行高 0"
            ItemHeight = 24;
            MaxDropDownItems = 10;
            Items = new ComboItemList(this);
        }

        // ================= 对外接口 (尽量贴近 ComboBox 的常用子集) =================

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                int v = (value >= 0 && value < Items.Count) ? value : -1;
                if (v == selected) return;
                selected = v;
                RefreshText();
                Invalidate();
                // 弹层开着时高亮要跟着走 (程序化改值时, 键盘那条路是反过来由高亮驱动选中)
                if (popup != null) popup.Highlight(v);
                OnSelectedIndexChanged(EventArgs.Empty);
            }
        }

        public object SelectedItem
        {
            get { return Items.At(selected); }
        }

        /// <summary>收起状态下显示的文本 (即选中项的 ToString)。</summary>
        public string SelectedText { get { return Text; } }

        public event EventHandler SelectedIndexChanged;

        /// <summary>
        /// 弹层即将展开 (点击本体 / Enter / Space / F4 / Alt+↓ 都会走这一条路)。
        /// <para>触发时机在展开条件检查之前 —— 外部可以在这里重建条目 (比如设备下拉
        /// 「点开时刷新设备」), 刷出来的 Items 当场就能进弹层。</para>
        /// </summary>
        public event EventHandler DropDownOpening;

        /// <summary>
        /// 弹层最小宽度。0 (默认) = 与本控件同宽。
        /// <para>界面里所有下拉都不设这个值: 弹层比本体宽多少, 视觉上就是"两个错开的框",
        /// 而滚动条也不再撑宽弹层 (改成右侧悬浮细条), 所以宽度天然一致。</para>
        /// <para>留着这个开关只为将来真有"必须显示超长名字"的场合: 那时加宽是刻意的选择,
        /// 代价是弹层会突出本体右缘。</para>
        /// </summary>
        public int DropDownWidth { get; set; }

        /// <summary>弹层最多显示多少行, 超过就用滚动条。</summary>
        public int MaxDropDownItems { get; set; }

        int itemHeight = Dpi.S(24);

        /// <summary>
        /// 弹层行高。本体高度由 <see cref="Control.Height"/> 单独决定, 两者互不影响。
        /// <para>赋值按设计像素处理 (调用点一律写 24), 取值拿到的是按当前 DPI 换算后的实际像素 ——
        /// 它不参与 WinForms 的 Bounds 缩放, 不换算的话 125% 下行高还是 24, 15px 的字会顶到行边。</para>
        /// </summary>
        public int ItemHeight
        {
            get { return itemHeight; }
            set { itemHeight = Dpi.S(value); }
        }

        /// <summary>
        /// 哪些行显示成灰色 (不可选的样子)。
        /// <para>卡片里的「多声道」在运行中就是这样: 列表项本身没法单独禁用, 只能靠这一层告诉绘制
        /// "这行是灰的"。传委托而不是传集合, 是因为判据随时在变 (shapeLocked), 闭包读字段天然跟着变。</para>
        /// </summary>
        public Func<int, bool> ItemGrayedFilter { get; set; }

        /// <summary>批量改动期间抑制重绘 (对应原生 ComboBox 的 BeginUpdate/EndUpdate)。</summary>
        public void BeginUpdate() { updating++; }

        public void EndUpdate()
        {
            if (updating > 0) updating--;
            if (updating == 0) Invalidate();
        }

        int updating;

        /// <summary>本体的文字由"选中项"决定, 不接受外部赋值 (否则与 SelectedIndex 打架)。</summary>
        void RefreshText()
        {
            base.Text = selected < 0 ? "" : ItemTextAt(selected);
        }

        internal string ItemTextAt(int index)
        {
            object o = Items.At(index);
            return o == null ? "" : o.ToString();
        }

        internal void OnItemsChanged()
        {
            // 条目变少时把越界的选中项拉回来; 条目变了索引就不再可靠, 弹层直接收掉
            if (selected >= Items.Count) selected = Items.Count - 1;
            RefreshText();
            if (popup != null) CloseDropDown();
            Invalidate();
        }

        internal void OnItemsCleared()
        {
            if (popup != null) CloseDropDown();
            bool had = selected >= 0;
            selected = -1;
            RefreshText();
            Invalidate();
            // 与原生一致: 清空导致选中项消失时, 事件要发出去
            if (had) OnSelectedIndexChanged(EventArgs.Empty);
        }

        void OnSelectedIndexChanged(EventArgs e)
        {
            EventHandler h = SelectedIndexChanged;
            if (h != null) h(this, e);
        }

        // ================= 绘制 =================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // 圆角之外的那几个小角要补成父容器底色, 否则会露出本控件的白底方块
            if (Parent != null)
            {
                using (var b = new SolidBrush(Parent.BackColor))
                    g.FillRectangle(b, ClientRectangle);
            }

            // 边框: 黑色细边 (用户指定)。展开/hover 用"同一黑色加粗到 1.5px"做反馈 ——
            // 黑色已经是最深, 没法像浅灰那样靠加深区分; 蓝色只留给键盘焦点
            // (鼠标点一下就变蓝、收起后蓝还挂在框上, 看起来就是"焦点框残留",
            //  见 keyboardFocusCues 的说明)。
            Color border = !Enabled ? BorderDisabled
                         : (Focused && keyboardFocusCues) ? BorderActive
                         : BorderNormal;
            bool strong = Enabled && (open || hot);

            // 填充始终铺满: 若边框态用内缩矩形, hover 时四周会露出 1px 父容器底色
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = UiDraw.RoundRect(rect, CornerRadius))
            {
                using (var fill = new SolidBrush(Enabled ? Color.White : DisabledBack))
                    g.FillPath(fill, path);
            }

            // 描边走**半像素对齐**路径: 笔心落在像素中心时 1px 正好盖住一整列, 是纯黑;
            // 用整数边界会把 1px 拆成两条 50% 灰 (放大看就是"灰边、比 1px 粗")。
            // 加粗时再多留一点余量, 免得 1.5px 的一半被客户区裁掉。
            if (Width > 4 && Height > 4)
            {
                float inset = strong ? 0.75f : 0.5f;
                using (var path = UiDraw.RoundRect(
                    new RectangleF(inset, inset, Width - inset * 2, Height - inset * 2), CornerRadius))
                using (var pen = new Pen(border, strong ? 1.5f : 1f))
                    g.DrawPath(pen, path);
            }

            int tw = Math.Max(0, Width - TextPadX - ArrowZone);
            string text = Text;
            if (tw > 0 && text.Length > 0)
            {
                TextRenderer.DrawText(g, text, Font,
                    new Rectangle(TextPadX, 0, tw, Height),
                    Enabled ? TextNormal : DisabledText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                    | TextFormatFlags.SingleLine);
            }

            DrawChevron(g, !Enabled ? DisabledText
                             : (hot || open || (Focused && keyboardFocusCues)) ? ArrowHot
                             : ArrowNormal);
        }

        /// <summary>
        /// 右侧那个「﹀」: 两条带圆头的线段, 没有任何外框。
        /// <para>不用字体字符 (U+FE40 之类的竖排标点在不同字体里宽度/基线差很多, 缺字还会画成方块),
        /// 直接用线画, 尺寸与位置完全可控。展开时翻转成「﹀」朝上, 与原生 ComboBox 的反馈一致。</para>
        /// </summary>
        void DrawChevron(Graphics g, Color color)
        {
            float cx = Width - Dpi.F(13f);
            float cy = Height / 2f;
            float halfW = Dpi.F(4f);
            float halfH = Dpi.F(2.2f);
            float dir = open ? -1f : 1f;
            using (var pen = new Pen(color, Dpi.F(1.6f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx - halfW, cy - halfH * dir, cx, cy + halfH * dir);
                g.DrawLine(pen, cx, cy + halfH * dir, cx + halfW, cy - halfH * dir);
            }
        }

        // ================= 鼠标 / 悬停 =================

        protected override void OnMouseEnter(EventArgs e)
        {
            hot = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hot = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || !Enabled) return;
            // 这里不能只靠 OnGotFocus: 焦点已经在本控件上时 (先 Tab 进来再点鼠标) Focus() 是空操作,
            // 蓝框会一直挂着。鼠标路径一律把焦点提示关掉。
            if (keyboardFocusCues) { keyboardFocusCues = false; Invalidate(); }
            if (!Focused) Focus();
            ToggleDropDown();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            // 到这一刻左键还按着 → 是"点进来"的; 键已松开 → 是 Tab / 程序化进来的, 该给焦点提示
            keyboardFocusCues = Control.MouseButtons == MouseButtons.None;
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) { hot = false; CloseDropDown(); }
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (!Visible) CloseDropDown();
            base.OnVisibleChanged(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ComboPopup p = popup;
                popup = null;
                open = false;
                if (p != null)
                {
                    try { p.Close(); } catch { }
                    if (!p.IsDisposed) { try { p.Dispose(); } catch { } }
                }
            }
            base.Dispose(disposing);
        }

        // ================= 滚轮 =================

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WheelRouting.WM_MOUSEWHEEL)
            {
                // 展开时滚轮归列表 (滚选项), 收起时转给外层可滚容器 —— 与原来那条下拉框规则一致
                if (open && popup != null) { popup.DeliverWheel(m); return; }
                WheelRouting.Forward(this, m);
                return;
            }
            base.WndProc(ref m);
        }

        // ================= 键盘 =================

        /// <summary>
        /// 把这些键抢过来当"输入键"。不抢的话方向键会被父容器当成焦点导航, 下拉框永远收不到。
        /// Tab 故意不在列表里 —— 那是正常的焦点顺序, 拦下来就走不出去了。
        /// </summary>
        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Left:
                case Keys.Right:
                case Keys.Home:
                case Keys.End:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Enter:
                case Keys.Space:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (HandleKey(e.KeyData))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        /// <summary>
        /// 所有键盘动作的唯一入口。
        /// <para>弹层里的按键也会转到这里 (见 <see cref="ComboPopup.ProcessCmdKey"/>), 因为焦点可能
        /// 跟着弹层走了 —— 两处各写一套迟早会不一致。</para>
        /// </summary>
        internal bool HandleKey(Keys keyData)
        {
            if (!Enabled) return false;

            // 鼠标点开之后接着用键盘 → 把焦点提示补回来 (否则键盘用户看不到焦点落在哪)
            if (!keyboardFocusCues) { keyboardFocusCues = true; Invalidate(); }

            Keys k = keyData & Keys.KeyCode;
            bool alt = (keyData & Keys.Alt) != 0;

            if (!open)
            {
                if (k == Keys.Down && alt) { OpenDropDown(); return true; }
                switch (k)
                {
                    case Keys.Down: MoveSelection(1); return true;
                    case Keys.Up: MoveSelection(-1); return true;
                    case Keys.Home: SelectedIndex = 0; return true;
                    case Keys.End: SelectedIndex = Items.Count - 1; return true;
                    case Keys.Enter:
                    case Keys.Space:
                    case Keys.F4:
                        OpenDropDown(); return true;
                }
                return false;
            }

            // —— 展开中: ↑↓ 只移动高亮, 不落地 ——
            int hl = popup == null ? 0 : popup.List.SelectedIndex;
            switch (k)
            {
                case Keys.Down: Highlight(hl + 1); return true;
                case Keys.Up: Highlight(hl - 1); return true;
                case Keys.PageDown: Highlight(hl + 5); return true;
                case Keys.PageUp: Highlight(hl - 5); return true;
                case Keys.Home: Highlight(0); return true;
                case Keys.End: Highlight(Items.Count - 1); return true;
                case Keys.Enter:
                case Keys.Space:
                    if (popup != null) CommitSelection(popup.List.SelectedIndex);
                    return true;
                case Keys.Escape:
                case Keys.F4:
                    CloseDropDown();
                    return true;
                case Keys.Tab:
                    CloseDropDown();     // 收起但把 Tab 让给系统, 焦点正常往后走
                    return false;
            }
            return false;
        }

        void MoveSelection(int delta)
        {
            if (Items.Count == 0) return;
            int i = selected + delta;
            if (i < 0) i = 0;
            if (i > Items.Count - 1) i = Items.Count - 1;
            SelectedIndex = i;
        }

        /// <summary>Tab 走出这个控件时把弹层收掉 (否则焦点已经去了下一个控件, 浮层还挂在屏幕上)。</summary>
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (open && (keyData & Keys.KeyCode) == Keys.Tab) CloseDropDown();
            return base.ProcessDialogKey(keyData);
        }

        internal void Highlight(int index)
        {
            if (popup == null) return;
            popup.Highlight(index);
        }

        // ================= 弹层 =================

        void ToggleDropDown()
        {
            if (open) CloseDropDown();
            else OpenDropDown();
        }

        void OpenDropDown()
        {
            if (open || !Enabled || !IsHandleCreated) return;

            // 展开前通知外部 (可能在这里刷新条目), 之后再看条目数决定要不要真的展开
            var opening = DropDownOpening;
            if (opening != null) opening(this, EventArgs.Empty);
            // 外部刷新可能重建了宿主 (设备变化 → 卡片合并 → 本控件被 Dispose), 别再往下走
            if (IsDisposed || !IsHandleCreated) return;
            if (Items.Count == 0) return;

            // 刚刚被"点击外部"自动收起的话, 本次点击不能再打开。
            // 场景: 弹层开着, 用户点下拉框本体想收起 —— ToolStripDropDown 的 AutoClose 会先把弹层
            // 收掉, 紧接着本体的 MouseDown 又调到这里; 没有这道闸就是"关掉又立刻打开", 看着像没反应。
            if (NowMs() - lastAutoClose < AutoCloseGuardMs) return;

            Size size = PopupSize();
            var p = new ComboPopup(this, size);
            p.Closed += OnPopupClosed;
            popup = p;
            open = true;
            Invalidate();

            // 展开时把高亮落在当前选中项上; 没选中过就落在第一行
            p.Highlight(selected >= 0 && selected < Items.Count ? selected : 0);
            p.Show(this, DropLocation(size));
        }

        void OnPopupClosed(object sender, ToolStripDropDownClosedEventArgs e)
        {
            ComboPopup p = sender as ComboPopup;
            if (p == null) return;
            p.Closed -= OnPopupClosed;

            // 自己主动关的时候 popup 已经被置空, 这里不该再改状态
            if (popup == p)
            {
                popup = null;
                open = false;
                Invalidate();
                // 立刻重画: 边框要从"展开色"退回常态, 拖到下一拍才画的话, 收起瞬间会看到旧描边一闪
                Update();
                // 这次是"外部原因"收起的 (点别处 / 点本体 / Esc): 记下时刻, 让紧随其后的点击别再打开
                lastAutoClose = NowMs();
            }

            // 释放要延后一拍: 此刻还在 Closed 事件里, 立刻 Dispose 有可能把消息循环打乱
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke((Action)(() => { if (!p.IsDisposed) p.Dispose(); }));
                else
                    p.Dispose();
            }
            catch { try { p.Dispose(); } catch { } }
        }

        internal void CloseDropDown()
        {
            ComboPopup p = popup;
            if (p == null) return;
            popup = null;
            open = false;
            Invalidate();
            Update();          // 同 OnPopupClosed: 描边当场退回常态, 不留一帧旧色
            try { p.Close(); } catch { }
        }

        /// <summary>选中并收起 (鼠标点选与 Enter 共用)。</summary>
        internal void CommitSelection(int index)
        {
            if (index < 0 || index >= Items.Count)
            {
                CloseDropDown();
                return;
            }
            CloseDropDown();
            SelectedIndex = index;
            if (IsHandleCreated && !IsDisposed)
            {
                try { Focus(); } catch { }
            }
        }

        /// <summary>
        /// 弹层窗口尺寸。窗口即内容: 不再外扩阴影边距 (见 <see cref="ComboPopup"/> 关于"无阴影"的说明),
        /// 所以落点与量测都可以直接用窗口尺寸。
        /// <para>内容宽 = 本体宽度 (或显式指定的 <see cref="DropDownWidth"/>), 内容高 = 行数×行高 +
        /// 上下留白 + 2px 边框。滚动条画在列表右侧的留白里, 不额外加宽。</para>
        /// </summary>
        Size PopupSize()
        {
            int rows = Math.Max(1, Math.Min(Items.Count, MaxDropDownItems));
            int w = Math.Max(Width, DropDownWidth);

            int h = rows * ItemHeight + PopupPadY * 2;
            return new Size(w, h);
        }

        /// <summary>
        /// 弹层的落点 (相对本控件客户区)。下方放不下且上方更宽敞时向上翻。
        /// <para>横向也要夹: 卡片贴右缘时 375px 宽的弹层会伸出屏幕。</para>
        /// </summary>
        Point DropLocation(Size size)
        {
            Screen scr = Screen.FromControl(this);
            Rectangle wa = scr.WorkingArea;
            Rectangle here = new Rectangle(PointToScreen(Point.Empty), Size);

            int below = wa.Bottom - here.Bottom - 2;
            int above = here.Top - wa.Top - 2;
            // 向下展开时内容上缘贴本体下缘 2px; 向上翻时内容下缘贴本体上缘 1px
            int y = (below >= size.Height || below >= above) ? Height + 2 : -1 - size.Height;

            int x = 0;
            if (here.Left + size.Width > wa.Right) x = wa.Right - size.Width - here.Left;
            if (here.Left + x < wa.Left) x = wa.Left - here.Left;
            return new Point(x, y);
        }

        // ================= 条目集合 =================

        /// <summary>条目集合。只实现界面用得到的几个方法, 不做 IList 那一整套。</summary>
        public sealed class ComboItemList
        {
            readonly List<object> items = new List<object>();
            readonly RoundedCombo owner;

            internal ComboItemList(RoundedCombo owner) { this.owner = owner; }

            public int Count { get { return items.Count; } }

            public object this[int index] { get { return items[index]; } }

            public void Add(object item)
            {
                items.Add(item);
                owner.OnItemsChanged();
            }

            public void AddRange(object[] range)
            {
                if (range == null || range.Length == 0) return;
                items.AddRange(range);
                owner.OnItemsChanged();
            }

            public void Clear()
            {
                if (items.Count == 0) return;
                items.Clear();
                owner.OnItemsCleared();
            }

            /// <summary>越界返回 null, 免得调用方每次都要先判范围。</summary>
            internal object At(int index)
            {
                return (index >= 0 && index < items.Count) ? items[index] : null;
            }
        }
    }

    /// <summary>
    /// 下拉弹层: 圆角无边框的浮层, 里面只有一个自绘列表。
    /// <para>用 ToolStripDropDown 而不是无边框 Form, 是因为它已经把"点外面关闭 / 按 Esc 关闭 /
    /// 跟随 owner 窗口"这套做完了; 换成 Form 这些都得自己处理, 而且无激活窗口的鼠标捕获很容易做错。</para>
    /// </summary>
    sealed class ComboPopup : ToolStripDropDown
    {
        /// <summary>弹层边框: 与下拉本体同一套黑色细边 (用户指定), 白底 + 圆角投影衬托着不显重。</summary>
        static readonly Color BorderColor = Color.Black;

        internal readonly ComboRowList List;

        public ComboPopup(RoundedCombo owner, Size size)
        {
            AutoSize = false;
            AutoClose = true;
            // 系统投影按窗口**矩形**投, 与弹层的圆角 Region 对不上: 四个角外侧会露出方角灰影。
            // 曾用"窗口外扩 4px + 自绘圆角投影"绕开这个问题, 现在按用户要求整体去掉阴影 ——
            // 弹层就是"白底 + 圆角黑边", 边界由 1px 黑线交代, 不再有窗体外扩量。
            DropShadowEnabled = false;
            BackColor = Color.White;
            GripStyle = ToolStripGripStyle.Hidden;
            // 内缩交给 host 的 Margin, 不用 ToolStrip.Padding: 有 Padding 时 ToolStrip 的绘制
            // clip 会收缩到内边距里, 铺白铺不到最外那几行, 描边又恰好画在那里 —— 表现是
            // 边框"画了却看不见", 四角只剩几点残影 (实测)。Padding 为 0, clip 才等于整个客户区。
            Padding = Padding.Empty;
            Size = size;
            Renderer = new PopupRenderer();

            int padX = Dpi.S(2), padY = RoundedCombo.PopupPadY;

            // 列表直接回查 owner.Items, 不再保存一份文字副本: 自绘列表用 "行号 = (y / 行高) + 首行"
            // 定位, 不依赖 ListBox 的 IndexFromPoint (那份副本一旦忘填, 表现就是"能展开但点哪行都没反应")。
            List = new ComboRowList(owner)
            {
                // 左右各让出 2px: 只让 1px 的话列表面板会压住边框内侧那半个像素,
                // 左右两条边就只剩 0.5px 宽 (实测), 而上下留了 4px 所以看着更粗 —— 四边不齐。
                Size = new Size(size.Width - padX * 2, size.Height - padY * 2)
            };
            var host = new ToolStripControlHost(List)
            {
                Margin = new Padding(padX, padY, padX, padY),
                Padding = Padding.Empty,
                AutoSize = false,
                Size = List.Size
            };
            Items.Add(host);

            // 滚动卡片区时下拉必须收起来, 否则浮层会停在原地与卡片错位
            ScrollableControl sc = FindScroller(owner);
            if (sc != null) sc.Scroll += OnHostScroll;
        }

        internal void Highlight(int index)
        {
            List.Highlight(index);
        }

        /// <summary>
        /// 关掉 Windows 11 给顶层窗口自动套的系统圆角。
        /// <para><b>为什么要关</b>: 系统的圆角是在 DWM 合成阶段另做的一层裁剪, 叠在我们自绘的
        /// Region 之上。Region 里特意留了给描边当余量的白边, 若被系统再裁一次, 圆角外侧就会
        /// 直接露出父窗口底色 —— 1 倍下看正是"圆角上有裂缝/缺口"。弹层的形状本来就由我们自己的
        /// Region 决定, 系统这一层只会让结果不可控。</para>
        /// <para>Win10 及更早没有这个属性, 调用会失败, 忽略即可。</para>
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyCornerPreference();
        }

        /// <summary>窗口真正显示后再设一次: handle 刚创建时 DWM 还没接管这个窗口, 那一次是空转。</summary>
        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            ApplyCornerPreference();
        }

        void ApplyCornerPreference()
        {
            try
            {
                int noRound = 1;                                        // DWMWCP_DONOTROUND
                DwmSetWindowAttribute(Handle, DwmCornerPreference, ref noRound, sizeof(int));
                // 让 DWM 立刻按新属性重算窗口框 (不改位置/尺寸/层序)
                SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        const int DwmCornerPreference = 33;                             // DWMWA_WINDOW_CORNER_PREFERENCE

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const int SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004,
                  SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, int flags);

        /// <summary>
        /// 滚轮只滚弹层列表, 绝不放它冒泡: ToolStripDropDown 不处理 WM_MOUSEWHEEL,
        /// 未处理的消息会被 DefWindowProc 转给 owner (主窗体), 再经焦点控件链落到卡片区 ——
        /// 表现就是"滚轮在弹层上滚, 底下的卡片却动了"。截下来直接滚列表。
        /// <para><b>为什么不能 SendMessage 投给列表</b>: 列表处理完滚轮消息会落到 DefWindowProc,
        /// Win32 会把未处理的 WM_MOUSEWHEEL 冒泡给父窗口 —— 父窗口正是本弹层, 弹层又截住
        /// 再投给列表, 无限递归, 程序直接卡死 (实测)。所以这里只调用列表的滚动入口,
        /// 消息本身到此为止。</para>
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WheelRouting.WM_MOUSEWHEEL)
            {
                long wp = m.WParam.ToInt64();
                List.HandleWheel((short)((wp >> 16) & 0xFFFF));
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>把滚轮转给列表 (下拉展开时滚的是选项, 不是卡片区)。调用方必须传真实的 delta。</summary>
        internal void DeliverWheel(Message m)
        {
            long wp = m.WParam.ToInt64();
            List.HandleWheel((short)((wp >> 16) & 0xFFFF));
        }

        /// <summary>
        /// 键盘统一交给下拉框本体处理。
        /// <para>必须在这里截: ToolStrip 自己有一套菜单导航 (方向键在条目间移动), 它会把键吞掉,
        /// 于是焦点在弹层上时 ↓ 就变成了"什么都没有发生"。</para>
        /// </summary>
        protected override bool ProcessCmdKey(ref Message m, Keys keyData)
        {
            ComboRowList list = List;
            RoundedCombo owner = list == null ? null : list.Owner;
            if (owner != null && !owner.IsDisposed && owner.HandleKey(keyData)) return true;
            return base.ProcessCmdKey(ref m, keyData);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ShapeRound();
        }

        /// <summary>
        /// 把窗口本身裁成圆角: 不这么做的话浮层的四个角会露出方角。
        /// <para><b>Region 要比可见内容外扩 2px</b>: Region 是像素级硬裁, 没有抗锯齿, 贴着边界
        /// 的像素还可能整格被丢掉。外扩 1px 只够盖住描边本身, 描边外侧那条白底反而被吃掉,
        /// 圆角处于是露出下层窗口、像裂了一道缝; 外扩 2px 才留得下一圈完整白边, 裁切的台阶
        /// 落在白底之外 (见 <see cref="PopupRenderer"/> 的铺白)。</para>
        /// </summary>
        void ShapeRound()
        {
            if (Width <= 0 || Height <= 0) return;
            const int Bleed = 1;
            using (var path = UiDraw.RoundRect(
                new Rectangle(-Bleed, -Bleed, Width + Bleed * 2, Height + Bleed * 2),
                RoundedCombo.PopupRadius + Bleed))
            {
                Region old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        void OnHostScroll(object sender, ScrollEventArgs e) { Close(); }

        static ScrollableControl FindScroller(Control c)
        {
            for (Control p = c; p != null; p = p.Parent)
            {
                ScrollableControl sc = p as ScrollableControl;
                if (sc != null && sc.AutoScroll) return sc;
            }
            return null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ScrollableControl sc = List == null ? null : FindScroller(List.Owner);
                if (sc != null) sc.Scroll -= OnHostScroll;
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// 纯白底 + 细黑描边, 顶掉 ToolStripProfessionalRenderer 的渐变与方框边框。
        ///
        /// <para><b>为什么要 4 倍超采样</b>: 直接以 1px 画圆角, 弧线是斜着切过像素栅格的 ——
        /// 45° 附近一格只被覆盖一半, 相邻格还会整格漏空, 于是弧线上出现一串深浅跳变的点:
        /// 看起来既"不顺滑"又"比直边细"。这不是笔宽没调好, 是 1 倍分辨率下圆弧采样点不够。
        /// 放到 4 倍上画同一条弧, 采样点变成 16 倍, 缩回 1 倍时覆盖率被平摊到相邻像素上,
        /// 弧线与直边就得到同样的视觉厚度, 过渡也连续了。</para>
        ///
        /// <para>背景与边框都在这里画 (边框覆写留空): 超采样必须一次成型, 分成两次绘制会
        /// 缩两张图再叠, 接缝处反而更脏。</para>
        ///
        /// <para>这里刻意不画投影 (用户要求): 窗口 Region 就是圆角, 任何"往外一层"的阴影
        /// 都落在裁剪区外、根本看不见。</para>
        /// </summary>
        sealed class PopupRenderer : ToolStripProfessionalRenderer
        {
            /// <summary>超采样倍率。4 倍已经能把 45° 处的覆盖率差压到看不见, 再高只是变慢。</summary>
            const int Ss = 4;

            /// <summary>描边宽度 (设计像素)。1.2 在 125% 下约 1.5 物理像素, 是"细边"与"能抗锯齿"的折中。</summary>
            const float PenW = 1.2f;

            // 边框半径与线宽都是物理像素: Graphics 不吃 DPI 缩放, 只有形状需要自己算。
            static float PenPhysical { get { return Dpi.F(PenW); } }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                ToolStrip ts = e.ToolStrip;
                Graphics g = e.Graphics;
                int w = ts.Width, h = ts.Height;
                if (w <= 0 || h <= 0) return;

                using (var bmp = new Bitmap(w * Ss, h * Ss,
                    System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                using (var big = Graphics.FromImage(bmp))
                {
                    big.SmoothingMode = SmoothingMode.AntiAlias;
                    big.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // 白底铺满整张图: Region 已是圆角, 圆角外的白会被裁掉, 不留方角
                    using (var b = new SolidBrush(Color.White))
                        big.FillRectangle(b, 0, 0, w * Ss, h * Ss);

                    // 半像素对齐: 笔心落在像素中心时, 直边才是纯黑而不是两条 50% 灰。
                    // 半径同步减掉 0.5, 圆心才与 Region 的圆心重合, 弧线离裁剪边处处等距。
                    const float Inset = 0.5f;
                    float radius = (RoundedCombo.PopupRadius - Inset) * Ss;
                    // 笔宽取到超采样网格的整数倍: 非整数时一条边会摊到 3 格、另一条只摊 2 格,
                    // 同一条路径的四条边立刻粗细不一 (这正是 100%/150% 缩放下的老问题)。
                    float pw = Math.Max(1f, (float)Math.Round(PenPhysical * Ss));
                    float left = Inset * Ss, top = Inset * Ss;
                    float rw = w * Ss - Inset * 2 * Ss, rh = h * Ss - Inset * 2 * Ss;
                    // 描边用"两条路径相减再填充"而不是 DrawPath: 实测 DrawPath 在横竖两个方向
                    // 上的实际覆盖不同 (同一支 1.5px 的笔, 横线量 1.25px、竖线只量到 1.00px),
                    // 四条边于是粗细不一。填充走的是扫描线覆盖, 内缘外缘都是精确的几何边界,
                    // 两个方向用同一套算法。
                    using (var b = new SolidBrush(BorderColor))
                    {
                        if (rw > pw * 2 && rh > pw * 2)
                        {
                            using (var ring = new GraphicsPath(FillMode.Alternate))
                            {
                                using (var outer = UiDraw.RoundRect(
                                    new RectangleF(left, top, rw, rh), radius))
                                using (var inner = UiDraw.RoundRect(
                                    new RectangleF(left + pw, top + pw, rw - pw * 2, rh - pw * 2),
                                    radius - pw))
                                {
                                    ring.AddPath(outer, false);
                                    ring.AddPath(inner, false);
                                }
                                big.FillPath(b, ring);
                            }
                        }
                        else
                        {
                            // 弹层窄到放不下两条边时退化成实心块 (正常尺寸走不到这里)
                            using (var solid = UiDraw.RoundRect(
                                new RectangleF(left, top, rw, rh), radius))
                                big.FillPath(b, solid);
                        }
                    }

                    // 缩图不用 GDI+ 的 DrawImage: 双三次在横竖两个方向上的滤波不对称,
                    // 实测同一根线横着量 1.26px、竖着量 1.00px —— 四边粗细立刻不齐。
                    // 自己按 4×4 做盒式平均, 四个方向用的是同一套权重, 像素覆盖严格守恒。
                    using (var small = Downsample(bmp, w, h))
                    {
                        // 1:1 贴图, 不做任何重采样。改用 DrawImage(rect) 时 GDI+ 会按插值模式
                        // 重采样一遍, 边缘那一列被"取邻居"取掉, 左右两条边凭空少 1 像素 ——
                        // 表现为横向只有纵向一半粗。DrawImageUnscaled 不经过取样器。
                        // SourceCopy: 连 alpha 一起写成不透明。分层窗口里用默认的 SourceOver 画,
                        // 目标表面的 alpha 抬不满, 边缘合成后会发灰。
                        CompositingMode old = g.CompositingMode;
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImageUnscaled(small, 0, 0);
                        g.CompositingMode = old;
                    }
                }
            }

            /// <summary>
            /// 把 <see cref="Ss"/> 倍图按 4×4 盒式平均缩回 1 倍。
            /// <para>这是"抗锯齿"的定义式实现: 一个输出像素的灰度 = 它覆盖的 16 个采样点的平均,
            /// 所以线条的视觉厚度严格等于它的几何宽度, 任何角度都一样 —— 直边 1.5px, 圆角 45° 也
            /// 是 1.5px。用 GDI+ 缩放拿不到这个保证 (它对横竖方向的滤波不同)。</para>
            /// </summary>
            static Bitmap Downsample(Bitmap big, int w, int h)
            {
                const System.Drawing.Imaging.PixelFormat Fmt = System.Drawing.Imaging.PixelFormat.Format24bppRgb;
                var small = new Bitmap(w, h, Fmt);
                System.Drawing.Imaging.BitmapData sd = null, dd = null;
                try
                {
                    sd = big.LockBits(new Rectangle(0, 0, big.Width, big.Height),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly, Fmt);
                    dd = small.LockBits(new Rectangle(0, 0, w, h),
                        System.Drawing.Imaging.ImageLockMode.WriteOnly, Fmt);
                    var src = new byte[sd.Stride * sd.Height];
                    var dst = new byte[dd.Stride * h];
                    System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, src, 0, src.Length);

                    int n = Ss * Ss;
                    for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int b = 0, g2 = 0, r = 0;
                        for (int j = 0; j < Ss; j++)
                        {
                            int row = (y * Ss + j) * sd.Stride + x * Ss * 3;
                            for (int i = 0; i < Ss; i++, row += 3)
                            {
                                b += src[row]; g2 += src[row + 1]; r += src[row + 2];
                            }
                        }
                        int o = y * dd.Stride + x * 3;
                        dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g2 / n); dst[o + 2] = (byte)(r / n);
                    }
                    System.Runtime.InteropServices.Marshal.Copy(dst, 0, dd.Scan0, dst.Length);
                }
                finally
                {
                    if (sd != null) big.UnlockBits(sd);
                    if (dd != null) small.UnlockBits(dd);
                }
                return small;
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
        }
    }

    /// <summary>
    /// 弹层里的自绘列表。
    ///
    /// <para><b>为什么不是 ListBox</b>: ListBox 是原生控件, WinForms 的
    /// <c>SetStyle(OptimizedDoubleBuffer)</c> 管不到它的重绘路径 —— 鼠标每换一行都要"擦背景再逐行画",
    /// 双缓冲完全失效, 于是悬停时整块列表肉眼可见地闪。自己画十行以内的文字, 代码更短、完全可控,
    /// 顺带能把滚动条换成右侧悬浮细条 (不再撑宽弹层)。</para>
    ///
    /// <para><b>数据源只有一个</b>: 行数与文字都直接读 <see cref="RoundedCombo.Items"/>, 命中按
    /// "行号 = y / 行高 + 首行"算, 不另存副本 —— ListBox 那套 IndexFromPoint 依赖列表里真有等量条目,
    /// 忘填时表现为"能展开但点哪行都没反应", 查起来很费劲。</para>
    /// </summary>
    sealed class ComboRowList : Control
    {
        internal readonly RoundedCombo Owner;

        /// <summary>鼠标下的行 (-1 = 不在任何行上)。</summary>
        int hot = -1;
        /// <summary>高亮行: 展开期间 ↑↓ 移动的是它, 回车/点击才落地。</summary>
        int sel = -1;
        /// <summary>首行对应的条目索引, 也就是滚动位置。</summary>
        int top;

        /// <summary>右侧留给滚动条的宽度。文字区固定让出这么多, 免得滚动条一出现文字就重排跳动。</summary>
        const int ScrollZone = 8;
        const int ScrollBarW = 4;
        const int ScrollBarMinH = 18;
        static readonly Color ScrollBarColor = Color.FromArgb(198, 203, 211);

        public ComboRowList(RoundedCombo owner)
        {
            Owner = owner;
            BackColor = Color.White;
            TabStop = false;
            // UserPaint 必须开: 只有它能让 WinForms 走自己的双缓冲绘制路径。
            // 少了这一位, 后面两个 SetStyle 对原生控件等于没写 (见类注释)。
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // 不参与焦点: 焦点留在下拉框本体上, 键盘只有一条处理链 (见 ComboPopup.ProcessCmdKey)
            SetStyle(ControlStyles.Selectable, false);
        }

        int RowH { get { return Math.Max(12, Owner.ItemHeight); } }
        int Count { get { return Owner.Items.Count; } }
        int VisibleRows { get { return Math.Max(1, Height / RowH); } }
        bool NeedsScroll { get { return Count > VisibleRows; } }

        /// <summary>当前高亮行 (提交选中项时读它)。</summary>
        internal int SelectedIndex { get { return sel; } }

        // ================= 滚动 / 高亮 =================

        internal void Highlight(int index)
        {
            if (Count == 0) return;
            sel = Math.Max(0, Math.Min(Count - 1, index));
            EnsureVisible(sel);
            Invalidate();
        }

        void EnsureVisible(int i)
        {
            if (i < 0) return;
            if (i < top) top = i;
            else if (i >= top + VisibleRows) top = i - VisibleRows + 1;
            ClampTop();
        }

        void ClampTop()
        {
            int max = Math.Max(0, Count - VisibleRows);
            if (top > max) top = max;
            if (top < 0) top = 0;
        }

        void ScrollBy(int rows)
        {
            if (!NeedsScroll || rows == 0) return;
            int before = top;
            top += rows;
            ClampTop();
            if (top != before) Invalidate();
        }

        /// <summary>鼠标位置对应的条目索引; 不在行上、或压在滚动条上都返回 -1。</summary>
        int HitTest(Point p)
        {
            if (p.Y < 0 || p.Y >= Height) return -1;
            if (NeedsScroll && p.X >= Width - ScrollZone) return -1;   // 滚动条区不当作选行
            int i = top + p.Y / RowH;
            return (i >= 0 && i < Count) ? i : -1;
        }

        // ================= 绘制 =================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int rowH = RowH;
            int count = Count;
            int visible = VisibleRows;

            using (var b = new SolidBrush(Color.White))
                g.FillRectangle(b, ClientRectangle);

            // 文字右界固定在滚动条左边: 有滚动条时也不跟着缩, 行宽不会有"多出/少掉一截"的观感
            int textW = Math.Max(0, Width - RoundedCombo.TextPadX - ScrollZone);
            for (int r = 0; r < visible; r++)
            {
                int i = top + r;
                if (i >= count) break;

                var row = new Rectangle(0, r * rowH, Width, rowH);
                Color back = i == sel ? RoundedCombo.RowSelected
                            : i == hot ? RoundedCombo.RowHover
                            : Color.White;
                if (back != Color.White)
                    using (var b2 = new SolidBrush(back))
                        g.FillRectangle(b2, row);

                bool gray = !Owner.Enabled
                    || (Owner.ItemGrayedFilter != null && Owner.ItemGrayedFilter(i));
                // 选中项用灰底 + 正常深色文字: 灰底上放白字看不清, 而加粗会让行高显得不匀
                TextRenderer.DrawText(g, Owner.ItemTextAt(i), Font,
                    new Rectangle(RoundedCombo.TextPadX, row.Y, textW, rowH),
                    gray ? RoundedCombo.DisabledText : Color.FromArgb(32, 32, 32),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                    | TextFormatFlags.SingleLine);
            }

            DrawScrollBar(g, count, visible);
        }

        /// <summary>
        /// 右侧滚动条: 4px 圆角细条, 悬浮在留白里。
        /// <para>不占布局宽度, 所以弹层宽度永远等于下拉框本体宽度 (原生滚动条会把弹层撑宽 17px)。</para>
        /// </summary>
        void DrawScrollBar(Graphics g, int count, int visible)
        {
            if (count <= visible) return;
            int h = Height;
            int thumbH = Math.Max(ScrollBarMinH, (int)((long)h * visible / count));
            if (thumbH > h) thumbH = h;
            int maxTop = Math.Max(1, count - visible);
            int y = (int)((long)(h - thumbH) * top / maxTop);

            var r = new Rectangle(Width - ScrollBarW - 2, y, ScrollBarW, thumbH);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = UiDraw.RoundRect(r, ScrollBarW / 2))
            using (var b = new SolidBrush(ScrollBarColor))
                g.FillPath(b, path);
        }

        // ================= 鼠标 =================

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = HitTest(e.Location);
            if (i != hot) SetHot(i);
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hot != -1) SetHot(-1);
            base.OnMouseLeave(e);
        }

        /// <summary>只失效变化的那两行 —— 整块重绘虽然不闪, 但没必要。</summary>
        void SetHot(int i)
        {
            int rowH = RowH;
            int old = hot;
            hot = i;
            InvalidateRow(old, rowH);
            InvalidateRow(i, rowH);
        }

        void InvalidateRow(int i, int rowH)
        {
            if (i < 0 || i < top) return;
            int y = (i - top) * rowH;
            if (y >= Height) return;
            Invalidate(new Rectangle(0, y, Width, rowH));
        }

        /// <summary>
        /// 按下即选中并收起 (与 Windows ComboBox 的下拉列表一致)。
        /// <para>刻意不用 MouseUp: 弹层显示后鼠标被 ToolStripDropDown 捕获, 抬起事件有可能先被
        /// 它吃掉 —— 那条路一旦不通, 整个下拉就点不动了, 而按下事件几乎不可能收不到。</para>
        /// </summary>
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int i = HitTest(e.Location);
            if (i >= 0) Owner.CommitSelection(i);
        }

        /// <summary>
        /// 滚轮就地处理并吞掉。<b>绝不调 base</b>: Control.DefWndProc 会把未处理的 WM_MOUSEWHEEL
        /// 冒泡给父窗口, 父窗口正是弹层 —— 弹层再滚一次就是一格走两行; 若弹层又把消息投回来,
        /// 就是无限递归、程序卡死 (两个坑都实际踩过, 见 ComboPopup.WndProc 的注释)。
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WheelRouting.WM_MOUSEWHEEL)
            {
                long wp = m.WParam.ToInt64();
                HandleWheel((short)((wp >> 16) & 0xFFFF));
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>滚轮滚动列表。delta > 0 (向上) 滚回去, 与鼠标滚动的直觉一致。</summary>
        internal void HandleWheel(int delta)
        {
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 1;
            ScrollBy(delta > 0 ? -lines : lines);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // 尺寸定下来之后可见行数才准, 重新夹一次滚动位置并让高亮行留在视野里
            ClampTop();
            EnsureVisible(sel);
        }
    }
}
