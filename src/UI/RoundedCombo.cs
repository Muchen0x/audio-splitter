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
        // ---------- 配色: 与 OutputCard / SoftCard 同一套边框色, 强调色与 VolumeSlider 同值 ----------
        static readonly Color BorderNormal = Color.FromArgb(203, 208, 216);
        static readonly Color BorderHot = Color.FromArgb(168, 176, 188);
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

        /// <summary>本体圆角。比卡片的 8 小一号 —— 控件矮, 同半径会显得过圆。</summary>
        const int CornerRadius = 6;
        /// <summary>弹层圆角, 与输出卡片/设置分组卡一致。</summary>
        internal const int PopupRadius = 8;
        /// <summary>文字左内缩。与卡片里其它控件的 8px 对齐。</summary>
        internal const int TextPadX = 8;
        /// <summary>右侧给箭头留的宽度。</summary>
        const int ArrowZone = 22;
        /// <summary>弹层上下留白 (视觉上让第一行不贴边)。</summary>
        const int PopupPadY = 4;
        /// <summary>自动收起后多久内不允许再展开 (毫秒)。见 <see cref="OpenDropDown"/> 的说明。</summary>
        const int AutoCloseGuardMs = 300;

        ComboPopup popup;
        bool open;
        bool hot;
        int selected = -1;
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

        /// <summary>弹层最小宽度。0 表示与本体同宽 (原生 ComboBox 的默认行为)。</summary>
        public int DropDownWidth { get; set; }

        /// <summary>弹层最多显示多少行, 超过就用滚动条。</summary>
        public int MaxDropDownItems { get; set; }

        /// <summary>弹层行高。本体高度由 <see cref="Control.Height"/> 单独决定, 两者互不影响。</summary>
        public int ItemHeight { get; set; }

        /// <summary>
        /// 哪些行显示成灰色 (不可选的样子)。
        /// <para>卡片里的「多声道…」在运行中就是这样: 列表项本身没法单独禁用, 只能靠这一层告诉绘制
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

            // 圆角之外的那几个小角要补成父容器底色, 否则会露出本控件的白底方块
            if (Parent != null)
            {
                using (var b = new SolidBrush(Parent.BackColor))
                    g.FillRectangle(b, ClientRectangle);
            }

            Color border = !Enabled ? BorderNormal
                         : (open || Focused) ? BorderActive
                         : hot ? BorderHot
                         : BorderNormal;

            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = UiDraw.RoundRect(rect, CornerRadius))
            {
                using (var fill = new SolidBrush(Enabled ? Color.White : DisabledBack))
                    g.FillPath(fill, path);
                using (var pen = new Pen(border))
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

            DrawChevron(g, !Enabled ? DisabledText : (hot || open || Focused) ? ArrowHot : ArrowNormal);
        }

        /// <summary>
        /// 右侧那个「﹀」: 两条带圆头的线段, 没有任何外框。
        /// <para>不用字体字符 (U+FE40 之类的竖排标点在不同字体里宽度/基线差很多, 缺字还会画成方块),
        /// 直接用线画, 尺寸与位置完全可控。展开时翻转成「﹀」朝上, 与原生 ComboBox 的反馈一致。</para>
        /// </summary>
        void DrawChevron(Graphics g, Color color)
        {
            float cx = Width - 13f;
            float cy = Height / 2f;
            const float halfW = 4f;
            const float halfH = 2.2f;
            float dir = open ? -1f : 1f;
            using (var pen = new Pen(color, 1.6f))
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
            if (!Focused) Focus();
            ToggleDropDown();
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
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
            if (open || !Enabled || Items.Count == 0 || !IsHandleCreated) return;

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

        /// <summary>弹层尺寸: 高 = 行数×行高 + 上下留白, 宽取本体与 DropDownWidth 的较大者。</summary>
        Size PopupSize()
        {
            int rows = Math.Max(1, Math.Min(Items.Count, MaxDropDownItems));
            bool scrolled = Items.Count > rows;

            int w = Math.Max(Width, DropDownWidth);
            if (scrolled) w += SystemInformation.VerticalScrollBarWidth;  // 别让滚动条盖住文字

            int h = rows * ItemHeight + PopupPadY * 2 + 2;                 // +2 = 左右各 1px 边框
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
            int y = (below >= size.Height || below >= above) ? Height + 2 : -size.Height - 1;

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
        static readonly Color BorderColor = Color.FromArgb(203, 208, 216);

        internal readonly ComboRowList List;

        public ComboPopup(RoundedCombo owner, Size size)
        {
            AutoSize = false;
            AutoClose = true;
            DropShadowEnabled = true;
            BackColor = Color.White;
            GripStyle = ToolStripGripStyle.Hidden;
            // 左右各 1px 给边框, 上下再留 4px 让首末行不贴边
            Padding = new Padding(1, 4, 1, 4);
            Size = size;
            Renderer = new PopupRenderer();

            List = new ComboRowList(owner)
            {
                Size = new Size(size.Width - 2, size.Height - 8 - 2)
            };
            // 列表里的"条目"只用来撑出行数与做命中计算, 真正的文字由 ComboRowList.OnDrawItem
            // 回查 owner 拿 —— 但必须真的放进等量条目, 否则 IndexFromPoint 永远返回 -1 (点不动)。
            if (owner.Items.Count > 0)
            {
                var texts = new object[owner.Items.Count];
                for (int i = 0; i < texts.Length; i++) texts[i] = owner.ItemTextAt(i);
                List.Items.AddRange(texts);
            }
            var host = new ToolStripControlHost(List)
            {
                Margin = Padding.Empty,
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
            if (List.Items.Count == 0) return;
            int i = Math.Max(0, Math.Min(List.Items.Count - 1, index));
            List.SelectedIndex = i;

            int rowH = Math.Max(1, List.ItemHeight);
            int visible = Math.Max(1, List.ClientSize.Height / rowH);
            if (i < List.TopIndex) List.TopIndex = i;
            else if (i >= List.TopIndex + visible) List.TopIndex = i - visible + 1;
        }

        /// <summary>把滚轮消息转给列表 (下拉展开时滚的是选项, 不是卡片区)。</summary>
        internal void DeliverWheel(Message m)
        {
            if (List.IsHandleCreated) WheelRouting.Deliver(List, m);
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

        /// <summary>把窗口本身裁成圆角: 不这么做的话浮层的四个角会露出方角。</summary>
        void ShapeRound()
        {
            if (Width <= 0 || Height <= 0) return;
            using (var path = UiDraw.RoundRect(new Rectangle(0, 0, Width, Height), RoundedCombo.PopupRadius))
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

        /// <summary>白底 + 圆角描边, 顶掉 ToolStripProfessionalRenderer 的渐变与方框边框。</summary>
        sealed class PopupRenderer : ToolStripProfessionalRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (var b = new SolidBrush(Color.White))
                    e.Graphics.FillRectangle(b, e.ToolStrip.ClientRectangle);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                ToolStrip ts = e.ToolStrip;
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = UiDraw.RoundRect(new Rectangle(0, 0, ts.Width - 1, ts.Height - 1), RoundedCombo.PopupRadius))
                using (var pen = new Pen(BorderColor))
                    g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>
    /// 弹层里的自绘列表。
    /// <para>用 ListBox 是为了白拿滚动条与"点哪行选哪行"的命中计算; 但它的背景/文字全部自己画,
    /// 因为系统那套蓝底高亮与界面配色对不上。</para>
    /// </summary>
    sealed class ComboRowList : ListBox
    {
        internal readonly RoundedCombo Owner;
        int hot = -1;

        public ComboRowList(RoundedCombo owner)
        {
            Owner = owner;
            BorderStyle = BorderStyle.None;
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = Math.Max(12, owner.ItemHeight);
            IntegralHeight = false;
            BackColor = Color.White;
            ForeColor = RoundedCombo.RowSelected;
            TabStop = false;
            // 不参与焦点: 焦点留在下拉框本体上, 键盘才有一条统一的处理链 (见 ComboPopup.ProcessCmdKey)
            SetStyle(ControlStyles.Selectable, false);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            Graphics g = e.Graphics;
            bool sel = e.Index == SelectedIndex;
            Color back = sel ? RoundedCombo.RowSelected : (e.Index == hot ? RoundedCombo.RowHover : Color.White);
            using (var b = new SolidBrush(back))
                g.FillRectangle(b, e.Bounds);

            bool gray = !Owner.Enabled
                || (Owner.ItemGrayedFilter != null && Owner.ItemGrayedFilter(e.Index));

            // 选中项用灰底 + 正常深色文字: 灰底上放白字看不清, 而加粗会让行高显得不匀
            var r = new Rectangle(e.Bounds.X + RoundedCombo.TextPadX, e.Bounds.Y,
                Math.Max(0, e.Bounds.Width - RoundedCombo.TextPadX - 6), e.Bounds.Height);
            TextRenderer.DrawText(g, Owner.ItemTextAt(e.Index), Font, r,
                gray ? RoundedCombo.DisabledText : Color.FromArgb(32, 32, 32),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                | TextFormatFlags.SingleLine);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexFromPoint(e.Location);
            if (i != hot) { hot = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hot != -1) { hot = -1; Invalidate(); }
            base.OnMouseLeave(e);
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
            int i = IndexFromPoint(e.Location);
            if (i >= 0 && i < Items.Count) Owner.CommitSelection(i);
        }
    }
}
