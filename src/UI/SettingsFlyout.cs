using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// "更多"弹层 (用户指定): 标题栏的三条横线图标点开一个类似下拉框的弹窗。
    /// <para><b>第二版</b> (用户指定"改成菜单列表那样, 鼠标悬停再显示说明"): 不再摆分组卡片,
    /// 改为纯菜单行 —— 图标 + 标题 + 右侧当前值, 说明文字全部挪进悬停 ToolTip。
    /// 行的可用性 / 取值实时镜像主界面借来的那几个控件; 点击行就是去驱动原控件
    /// (勾选翻转 / 缓冲档位子菜单 / 校准按钮 PerformClick), 所以原有事件绑定、
    /// ini 落盘、引擎调用路径<b>零改动</b> —— "点菜单行"和"以前在设置页点一下"等价。</para>
    ///
    /// <para><b>控件是"借"来的, 不是新建的</b>: 主界面持有那几个控件实例, 打开弹层时搬进来
    /// 隐藏托管 (Visible=false, 只是状态载体), 关闭时搬回去并把快照刷成最新值 —— 用户在菜单里
    /// 改过的选择<b>保留</b>而不是还原 (弹层本来就是"改动立即生效"的语义, 快照只用来兜
    /// 换父容器句柄重建时的状态丢失)。</para>
    /// </summary>
    sealed class SettingsFlyout : ToolStripDropDown
    {
        /// <summary>借来的一个控件 + 它的状态快照 (跟随用户改动刷新, 见 ReturnControls)。</summary>
        sealed class Borrowed
        {
            public Control C;
            public bool IsCheck;
            public int ComboIndex = int.MinValue;
            public bool Checked;

            /// <summary>把控件当前值记进快照 (用户改完之后调用, 让"归还时校正"变成保留最新值)。</summary>
            public void Refresh()
            {
                if (C is RoundedCombo) { ComboIndex = ((RoundedCombo)C).SelectedIndex; return; }
                if (C is ComboBox) { ComboIndex = ((ComboBox)C).SelectedIndex; return; }
                CheckBox chk = C as CheckBox;
                if (chk != null) { Checked = chk.Checked; IsCheck = true; }
            }
        }

        readonly List<Borrowed> borrowed = new List<Borrowed>();
        bool returned;

        readonly Font rowFont = new Font("Microsoft YaHei UI", 9F);
        readonly ToolTip tips = new ToolTip { ShowAlways = true, InitialDelay = 350, ReshowDelay = 120 };

        /// <summary>借来的缓冲档位下拉: 菜单行只镜像它的值与可用性, 自己的弹窗不再用 (隐藏托管)。</summary>
        RoundedCombo cmbBuf;
        ProfileMenu profileMenu;

        // —— 布局常量 = 96 设计像素 (面板按 96 摆好后整体乘一次缩放系数) ——
        const int DesignW = 300;
        const int RowH = 34;
        const int PadY = 6;
        static readonly int DesignH = PadY + 5 * RowH + PadY;

        /// <summary>弹层收起 (点外面/Esc/程序收起) 的时刻 (NowMs 基准)。MainForm 用它做
        /// "刚收起的那次点击别再打开" 的闸 —— 与 RoundedCombo 的 AutoCloseGuardMs 同一套竞态。</summary>
        internal long LastAutoCloseMs { get; private set; }

        /// <summary>承载菜单行的面板 (探针用来检查边界/重叠)。</summary>
        internal Control Content { get { return content; } }
        readonly Panel content;

        /// <param name="controls">顺序与 <see cref="MainForm.SettingsControls"/> 严格一致:
        /// 0 缓冲档位标签 (不再使用) / 1 缓冲档位下拉 / 2 自动时钟校正 / 3 捕获消音
        /// / 4 关闭进托盘 / 5 延迟校准。</param>
        public SettingsFlyout(Control[] controls)
        {
            AutoSize = false;
            AutoClose = true;
            DropShadowEnabled = false;       // 与 ComboPopup 同: 系统投影跟方角窗口对不上, 一并去掉
            BackColor = Color.White;
            GripStyle = ToolStripGripStyle.Hidden;
            Padding = Padding.Empty;         // Padding 非 0 会缩 ToolStrip 的绘制 clip, 边框铺不全 (实测教训)
            Renderer = new ComboPopup.PopupRenderer();

            content = new Panel { Size = new Size(DesignW, DesignH), BackColor = Color.White };
            BuildUi(controls);

            // DPI: 面板按 96 设计像素摆好, 这里整体乘一次缩放系数 (字体是 pt 单位, 不用缩;
            // MenuRow 自己按 Height/RowH 推缩放系数画图标)
            if (Dpi.Factor != 1f) content.Scale(new SizeF(Dpi.Factor, Dpi.Factor));
            int m = Dpi.S(2);                // host 四边各让 2px, 弹层自绘的 1px 边框才不会被面板压住
            var host = new ToolStripControlHost(content)
            {
                Margin = new Padding(m),
                Padding = Padding.Empty,
                AutoSize = false,
                Size = content.Size
            };
            Items.Add(host);
            Size = new Size(content.Width + m * 2, content.Height + m * 2);

            Closed += (s, e) =>
            {
                LastAutoCloseMs = NowMs();
                if (profileMenu != null) { try { profileMenu.Close(); } catch { } }
                if (!AutoClose) AutoClose = true;    // 收起时必回常态, 下一次打开才是干净状态
            };
        }

        void BuildUi(Control[] c)
        {
            cmbBuf = c != null && c.Length > 1 ? c[1] as RoundedCombo : null;
            Control chkDrift = c != null && c.Length > 2 ? c[2] : null;   // 自动时钟校正
            Control chkMuteCap = c != null && c.Length > 3 ? c[3] : null; // 锁定捕获端静音
            Control chkTray = c != null && c.Length > 4 ? c[4] : null;    // 关闭进托盘
            Control btnCalib = c != null && c.Length > 5 ? c[5] : null;   // 延迟校准

            MenuRow rowBuf = null;
            rowBuf = NewRow(MenuRow.IconKind.Mixer, "缓冲档位",
                () => cmbBuf != null ? ProfileShort(cmbBuf.SelectedIndex) : "",
                null, true, cmbBuf,
                "越低延迟越小, 但对设备抖动越敏感; 运行中不可改, 需先停止分流。",
                () => OpenProfileMenu(rowBuf));
            NewRow(MenuRow.IconKind.Clock, "自动时钟校正",
                null, () => IsChecked(chkDrift), false, chkDrift,
                "微调每路的重采样比例抵消晶振差异: 防止长时间播放后延迟漂移、或水位耗尽断音。",
                () => FlipCheck(chkDrift));
            NewRow(MenuRow.IconKind.MicSlash, "锁定捕获端静音",
                null, () => IsChecked(chkMuteCap), false, chkMuteCap,
                "开启后被外部取消静音会自动恢复, 避免声音从原设备漏出。",
                () => FlipCheck(chkMuteCap));
            NewRow(MenuRow.IconKind.Tray, "关闭时收进托盘",
                null, () => IsChecked(chkTray), false, chkTray,
                "开启后点关闭只隐藏窗口、分流继续; 双击托盘图标恢复。",
                () => FlipCheck(chkTray));
            NewRow(MenuRow.IconKind.Stopwatch, "延迟校准…",
                null, null, false, btnCalib,
                "用麦克风实测各路声音到达耳朵的时间差并自动补齐, 适合有线 + 无线混用对不齐; 需要一支可用麦克风。",
                () => RaiseClick(btnCalib));  // 原按钮 Click → OpenLatencyCalibration (运行中可点, 启动中行已被镜像置灰)
        }

        /// <summary>造一行菜单: 借来的控件隐藏托管进面板 (只当状态载体), 行的可用性镜像它。</summary>
        MenuRow NewRow(MenuRow.IconKind icon, string title, Func<string> value, Func<bool> toggle,
            bool arrow, Control src, string tip, Action activate)
        {
            // 行 y 位置: 只数已经摆了的菜单行 (借来的隐藏控件也占 Controls 序号, 不能拿来当 y)
            int rowIdx = 0;
            foreach (Control cc in content.Controls) if (cc is MenuRow) rowIdx++;

            var row = new MenuRow(icon, title, value, toggle, arrow)
            {
                Bounds = new Rectangle(0, PadY + rowIdx * RowH, DesignW, RowH),
                Font = rowFont
            };

            if (activate != null) row.Click += (s, e) => { if (row.Enabled) activate(); };
            tips.SetToolTip(row, tip);
            if (src != null)
            {
                Park(src);
                row.Enabled = src.Enabled;                 // 镜像: 运行中档位置灰等规则全由 ApplyUiLock 决定
                src.EnabledChanged += (s, e) => { row.Enabled = src.Enabled; };
            }
            content.Controls.Add(row);
            return row;
        }

        /// <summary>把借来的控件搬进弹层隐藏托管, 记下初始快照。</summary>
        void Park(Control control)
        {
            var b = new Borrowed { C = control };
            b.Refresh();
            if (control.Parent != null) control.Parent.Controls.Remove(control);
            control.Location = new Point(0, 0);
            control.Visible = false;                     // 状态载体, 不再直接显示
            content.Controls.Add(control);
            borrowed.Add(b);
        }

        void FlipCheck(Control chk)
        {
            CheckBox cb = chk as CheckBox;
            if (cb == null) return;
            cb.Checked = !cb.Checked;                    // CheckedChanged → 原有落盘/引擎路径
            NoteValue(cb);
            foreach (Control cc in content.Controls) if (cc is MenuRow) cc.Invalidate();
        }

        /// <summary>用户在菜单里改了值 → 快照跟着刷成最新 (归还时按"保留"语义校正)。</summary>
        void NoteValue(Control c)
        {
            foreach (Borrowed b in borrowed) if (b.C == c) b.Refresh();
        }

        /// <summary>点"缓冲档位"行: 弹档位子菜单 (白底圆角, 当前档打勾)。</summary>
        void OpenProfileMenu(MenuRow row)
        {
            if (cmbBuf == null || cmbBuf.Items.Count == 0) return;

            // 子菜单是另一个顶层窗口, 鼠标会离开本弹层 —— 挂起 AutoClose, 关掉后恢复;
            // 若不是点菜单项收的 (点了别处/Esc), 本弹层也跟着收, 别留一个悬空的父层。
            if (profileMenu != null) { try { profileMenu.Dispose(); } catch { } profileMenu = null; }
            ProfileMenu menu = new ProfileMenu(cmbBuf, rowFont, idx =>
            {
                cmbBuf.SelectedIndex = idx;              // SelectedIndexChanged → 原有落盘/引擎路径
                NoteValue(cmbBuf);
                row.Invalidate();
            });
            profileMenu = menu;
            AutoClose = false;
            menu.Closed += (s, e) =>
            {
                AutoClose = true;
                bool picked = e.CloseReason == ToolStripDropDownCloseReason.ItemClicked;
                if (!picked && Visible) Close();
            };

            // 锚在行的右侧偏下 (级联菜单观感), 顶出屏幕工作区时往回收
            Point anchor = row.PointToScreen(new Point(row.Width - Dpi.S(26), Dpi.S(30)));
            Rectangle wa = Screen.FromControl(row).WorkingArea;
            if (anchor.X + menu.Width > wa.Right) anchor.X = wa.Right - menu.Width;
            if (anchor.Y + menu.Height > wa.Bottom) anchor.Y = Math.Max(wa.Top, wa.Bottom - menu.Height);
            menu.Show(anchor);
        }

        /// <summary>缓冲档位的短名 ("标准"…"极限"), 与 MainForm.LatencyProfileShort 同表。</summary>
        static string ProfileShort(int i)
        {
            string[] names = { "标准", "低延迟", "极低", "超低", "极限" };
            if (i < 0 || i >= names.Length) i = 0;
            return names[i];
        }

        static bool IsChecked(Control chk)
        {
            CheckBox cb = chk as CheckBox;
            return cb != null && cb.Checked;
        }

        /// <summary>触发原控件的 Click (校准按钮是 Control 引用, PerformClick 在 Button 上)。
        /// OnClick 里没有 Enabled 检查 —— 启动中行已被镜像置灰, 这里再拦一道兜底。</summary>
        static void RaiseClick(Control c)
        {
            if (c == null || !c.Enabled || c.IsDisposed) return;
            typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(c, new object[] { EventArgs.Empty });
        }

        // ================= 归还 (与原 SettingsForm 同一套语义, 快照=最新值) =================

        /// <summary>
        /// 把借来的控件原样还给主界面并校正状态。幂等; **必须在 Dispose 之前完成**,
        /// 否则这些控件会跟着弹层一起销毁, 主界面再引用就是访问已释放对象。
        /// <para>快照在用户每次改动后都会刷新 (NoteValue), 所以"校正"只兜换父容器
        /// 句柄重建时的状态丢失, 不会把用户的选择退回去。</para>
        /// </summary>
        public void ReturnControls()
        {
            if (returned) return;
            returned = true;

            foreach (Borrowed b in borrowed)
            {
                Control c = b.C;
                if (c == null || c.IsDisposed) continue;
                if (c.Parent != null) c.Parent.Controls.Remove(c);

                if (b.ComboIndex != int.MinValue && ComboIndexOf(c) != b.ComboIndex)
                    SetComboIndex(c, b.ComboIndex);
                CheckBox chk = c as CheckBox;
                if (chk != null && b.IsCheck && chk.Checked != b.Checked) chk.Checked = b.Checked;
            }
            borrowed.Clear();
        }

        static int ComboIndexOf(Control c)
        {
            if (c is RoundedCombo) return ((RoundedCombo)c).SelectedIndex;
            if (c is ComboBox) return ((ComboBox)c).SelectedIndex;
            return int.MinValue;
        }

        static void SetComboIndex(Control c, int index)
        {
            if (c is RoundedCombo) { ((RoundedCombo)c).SelectedIndex = index; return; }
            if (c is ComboBox) { ((ComboBox)c).SelectedIndex = index; }
        }

        // ================= 弹层窗口形状 (与 ComboPopup 同一套) =================

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FlyoutChrome.Apply(this);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            FlyoutChrome.Apply(this);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            FlyoutChrome.RoundRegion(this);
        }

        static long NowMs()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0
                / System.Diagnostics.Stopwatch.Frequency);
        }

        protected override void Dispose(bool disposing)
        {
            // 兜底: 即使调用方没来得及归还, 也不能让主界面的控件跟着弹层一起销毁
            if (disposing)
            {
                ReturnControls();
                if (profileMenu != null) { try { profileMenu.Dispose(); } catch { } profileMenu = null; }
                try { tips.Dispose(); } catch { }
            }
            base.Dispose(disposing);
            if (!disposing) return;
            try { rowFont.Dispose(); } catch { }
        }
    }

    /// <summary>顶层浮层的窗口外观: 去系统圆角 + Region 裁圆角。SettingsFlyout / ProfileMenu 共用。</summary>
    internal static class FlyoutChrome
    {
        public static void Apply(ToolStripDropDown dd)
        {
            try
            {
                int noRound = 1;                                        // DWMWCP_DONOTROUND
                DwmSetWindowAttribute(dd.Handle, DwmCornerPreference, ref noRound, sizeof(int));
                SetWindowPos(dd.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            RoundRegion(dd);
        }

        /// <summary>把窗口裁成圆角; Region 比描边外缘外扩 1px, 圆弧的抗锯齿边不被硬裁断。</summary>
        public static void RoundRegion(ToolStripDropDown dd)
        {
            if (dd.Width <= 0 || dd.Height <= 0) return;
            const int Bleed = 1;
            using (var path = UiDraw.RoundRect(
                new Rectangle(-Bleed, -Bleed, dd.Width + Bleed * 2, dd.Height + Bleed * 2),
                RoundedCombo.PopupRadius + Bleed))
            {
                Region old = dd.Region;
                dd.Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }

        const int DwmCornerPreference = 33;                             // DWMWA_WINDOW_CORNER_PREFERENCE

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const int SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004,
                  SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, int flags);
    }

    /// <summary>
    /// "更多"弹层里的缓冲档位子菜单: 白底圆角 + 勾选标记, 与规格下拉同一套观感。
    /// 选项文本直接取缓冲档位下拉的 Items (含"总预估xxms"提示), 点选后由回调写回下拉。
    /// </summary>
    internal sealed class ProfileMenu : ToolStripDropDown
    {
        public ProfileMenu(RoundedCombo cmb, Font font, Action<int> pick)
        {
            AutoSize = false;
            AutoClose = true;
            DropShadowEnabled = false;
            BackColor = Color.White;
            GripStyle = ToolStripGripStyle.Hidden;
            Padding = Padding.Empty;
            Renderer = new ComboPopup.PopupRenderer();

            int cur = cmb.SelectedIndex;
            for (int i = 0; i < cmb.Items.Count; i++)
            {
                int idx = i;
                var it = new ToolStripMenuItem(cmb.ItemTextAt(idx))
                {
                    Checked = idx == cur,
                    Font = font,
                    BackColor = Color.White
                };
                it.Click += (s, e) => { if (pick != null) pick(idx); Close(); };
                // 当前档的标记: PopupRenderer 不画勾, 给当前项挂一个左侧勾选图标 ——
                // 只要有一项带 Image, 整个下拉就会留出统一的图标栏, 对勾不会再压到文字。
                if (idx == cur)
                {
                    it.Image = MakeCheckGlyph();
                    it.ImageScaling = ToolStripItemImageScaling.None;
                }
                Items.Add(it);
            }
            // 宽度交给 AutoSize (有图标栏时 ToolStrip 自己会留), 只保证不小于最小宽
            Size = new Size(Math.Max(PreferredSize.Width, Dpi.S(160)), PreferredSize.Height);
        }

        /// <summary>勾选标记图标: 18×18 白底上画一支蓝对勾 (与启动钮/开关同一支蓝)。</summary>
        static Bitmap MakeCheckGlyph()
        {
            var bmp = new Bitmap(Dpi.S(18), Dpi.S(18));
            using (Graphics g = Graphics.FromImage(bmp))
            using (var p = new Pen(Color.FromArgb(26, 116, 192), Dpi.F(1.8f))
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float s = Dpi.S(18);
                g.DrawLines(p, new[]
                {
                    new PointF(s * 0.24f, s * 0.52f),
                    new PointF(s * 0.42f, s * 0.70f),
                    new PointF(s * 0.78f, s * 0.28f)
                });
            }
            return bmp;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FlyoutChrome.Apply(this);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            FlyoutChrome.Apply(this);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            FlyoutChrome.RoundRegion(this);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            foreach (ToolStripItem it in Items)
                if (it.Image != null) { it.Image.Dispose(); it.Image = null; }
        }
    }

    /// <summary>
    /// 菜单行: 图标 + 标题 + 右侧当前值 (+ 可选展开箭头), 悬停垫灰 —— 观感对齐
    /// 用户给的菜单列表截图 (安全锁定/帮助与反馈那种)。说明文字由宿主用 ToolTip 挂上来。
    /// 绘制按 Height/RowH 推缩放系数 (面板整体 Scale 过, 宽高已是真实像素)。
    /// </summary>
    internal sealed class MenuRow : Control
    {
        internal enum IconKind { Mixer, Clock, MicSlash, Tray, Stopwatch }

        const int DesignRowH = 34;
        static readonly Color Ink = Color.FromArgb(70, 74, 82);           // 图标
        static readonly Color InkDisabled = Color.FromArgb(176, 180, 186);
        static readonly Color TitleC = Color.FromArgb(44, 44, 42);
        static readonly Color TitleDisabled = Color.FromArgb(150, 154, 160);
        static readonly Color ValueC = Color.FromArgb(124, 130, 138);
        static readonly Color HoverBg = Color.FromArgb(242, 243, 245);    // RoundedCombo.RowHover 同值

        readonly IconKind icon;
        readonly Func<string> valueText;
        readonly Func<bool> toggleState;        // 非 null = 行尾画胶囊开关 (替代 开/关 文字)
        readonly bool hasArrow;
        bool hot;

        public MenuRow(IconKind icon, string title, Func<string> value, Func<bool> toggle, bool arrow)
        {
            this.icon = icon;
            this.valueText = value;
            this.toggleState = toggle;
            this.hasArrow = arrow;
            Text = title;
            TabStop = false;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.Opaque, false);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float k = Height / (float)DesignRowH;      // 缩放系数 = 真实行高 / 设计行高

            if (hot && Enabled)
                using (var bg = UiDraw.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 6 * k))
                    using (var b = new SolidBrush(HoverBg)) g.FillPath(b, bg);

            bool en = Enabled;
            Color ink = en ? Ink : InkDisabled;

            // 图标: 16 设计px 盒子, 圆心 (22,17)
            DrawIcon(g, icon, 22 * k, 17 * k, k, ink);

            // 标题
            Color fore = en ? TitleC : TitleDisabled;
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle((int)(40 * k), 0, Width - (int)(40 * k) - (int)(14 * k), Height),
                fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                | TextFormatFlags.EndEllipsis);

            // 行尾: 开关行画胶囊滑块, 其余画文字值 / 展开箭头
            if (toggleState != null)
                DrawToggle(g, toggleState() && en, Width - 12 * k, Height / 2f, k, en);
            else if (valueText != null)
            {
                string v = valueText();
                if (!string.IsNullOrEmpty(v))
                {
                    int rightEdge = Width - (int)((hasArrow ? 22 : 12) * k);
                    var vr = new Rectangle(0, 0, rightEdge, Height);
                    Size vs = TextRenderer.MeasureText(g, v, Font, vr.Size, TextFormatFlags.Default);
                    TextRenderer.DrawText(g, v, Font,
                        new Rectangle(rightEdge - vs.Width, 0, vs.Width + 2, Height),
                        en ? ValueC : InkDisabled,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }

            // 展开箭头 (子菜单)
            if (hasArrow)
            {
                float ax = Width - 14 * k, ay = Height / 2f, s = 3.4f * k;
                using (var p = new Pen(en ? ValueC : InkDisabled, 1.6f * k)
                { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(p, new[]
                    {
                        new PointF(ax - s * 0.55f, ay - s * 0.8f),
                        new PointF(ax + s * 0.55f, ay),
                        new PointF(ax - s * 0.55f, ay + s * 0.8f)
                    });
            }
        }

        /// <summary>胶囊开关 (用户给的样式): 关 = 灰胶囊 + 左侧白圆钮, 开 = 蓝胶囊 + 右侧白圆钮。
        /// 尺寸 30×16 设计px, 圆钮 r6.5, 右缘贴 right。</summary>
        static void DrawToggle(Graphics g, bool on, float right, float cy, float k, bool enabled)
        {
            float w = 30 * k, h = 16 * k, r = h / 2f;
            float x = right - w;
            Color track = !enabled ? Color.FromArgb(206, 209, 213)
                : on ? Color.FromArgb(26, 116, 192)      // 开: 与档位对勾/启动钮同一支蓝
                     : Color.FromArgb(201, 205, 211);    // 关: 用户截图的浅灰胶囊
            using (var path = UiDraw.RoundRect(new RectangleF(x, cy - r, w, h), r))
            using (var b = new SolidBrush(track))
                g.FillPath(b, path);
            float knobX = on ? x + w - r : x + r;        // 圆心贴端部, 各留 r 内缩
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, knobX - 6.5f * k, cy - 6.5f * k, 13f * k, 13f * k);
            if (!enabled)
                using (var p = new Pen(Color.FromArgb(226, 228, 232), 1f))
                    g.DrawEllipse(p, knobX - 6.5f * k, cy - 6.5f * k, 13f * k, 13f * k);
        }

        static void DrawIcon(Graphics g, IconKind kind, float cx, float cy, float k, Color c)
        {
            using (var p = new Pen(c, 1.7f * k)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (kind)
                {
                    case IconKind.Mixer:            // 三条滑杆 (推子台): 越低延迟越小
                        for (int i = -1; i <= 1; i++)
                        {
                            float y = cy + i * 4.6f * k;
                            g.DrawLine(p, cx - 6.6f * k, y, cx + 6.6f * k, y);
                        }
                        using (var b = new SolidBrush(c))
                            foreach (var kn in new[] { new PointF(-2.6f, -1), new PointF(2.6f, 0), new PointF(-1.2f, 1) })
                                g.FillEllipse(b, cx + kn.X * k - 2.3f * k, cy + kn.Y * 4.6f * k - 2.3f * k,
                                    4.6f * k, 4.6f * k);
                        break;

                    case IconKind.Clock:            // 时钟: 校正 = 对时
                        g.DrawEllipse(p, cx - 6f * k, cy - 6f * k, 12f * k, 12f * k);
                        g.DrawLine(p, cx, cy, cx, cy - 3.8f * k);
                        g.DrawLine(p, cx, cy, cx + 3f * k, cy + 0.6f * k);
                        break;

                    case IconKind.MicSlash:         // 麦克风 + 斜杠: 捕获端静音
                        using (var path = UiDraw.RoundRect(
                            new RectangleF(cx - 2.2f * k, cy - 6.4f * k, 4.4f * k, 7.6f * k), 2.2f * k))
                            g.DrawPath(p, path);
                        g.DrawLine(p, cx, cy + 1.2f * k, cx, cy + 4f * k);
                        g.DrawLine(p, cx - 3.4f * k, cy + 4f * k, cx + 3.4f * k, cy + 4f * k);
                        using (var ps = new Pen(c, 1.9f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLine(ps, cx - 6f * k, cy - 6.6f * k, cx + 6f * k, cy + 6.6f * k);
                        break;

                    case IconKind.Tray:             // 箭头落进托盘: 关闭只隐藏
                        g.DrawLines(p, new[]
                        {
                            new PointF(cx - 6.2f * k, cy - 3.4f * k),
                            new PointF(cx - 6.2f * k, cy + 4.6f * k),
                            new PointF(cx + 6.2f * k, cy + 4.6f * k),
                            new PointF(cx + 6.2f * k, cy - 3.4f * k)
                        });
                        g.DrawLine(p, cx, cy - 6.6f * k, cx, cy + 1.6f * k);
                        g.DrawLines(p, new[]
                        {
                            new PointF(cx - 2.6f * k, cy - 1f * k),
                            new PointF(cx, cy + 1.6f * k),
                            new PointF(cx + 2.6f * k, cy - 1f * k)
                        });
                        break;

                    case IconKind.Stopwatch:        // 秒表: 延迟校准
                        g.DrawEllipse(p, cx - 5.4f * k, cy - 4.2f * k, 10.8f * k, 10.8f * k);
                        g.DrawLine(p, cx - 2.6f * k, cy - 7.6f * k, cx + 2.6f * k, cy - 7.6f * k);
                        g.DrawLine(p, cx, cy - 7.6f * k, cx, cy - 4.6f * k);
                        g.DrawLine(p, cx, cy + 1.2f * k, cx + 3f * k, cy - 1.4f * k);
                        break;
                }
            }
        }
    }
}
