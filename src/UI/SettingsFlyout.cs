using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// "更多"弹层 (用户指定): 标题栏的三条横线图标点开一个类似下拉框的弹窗,
    /// 原设置页的选项全部移入其中。它取代了模态的 <c>SettingsForm</c> ——
    /// 四张分组卡片 (音频链路 / 捕获监听 / 窗口行为 / 校准工具) 原样搬进弹层面板,
    /// 「关闭」按钮不再需要 (点弹层外 / 按 Esc 收起), 布局与原设置页一致。
    ///
    /// <para><b>控件是"借"来的, 不是新建的</b> (与原 SettingsForm 同一套机制): 主界面仍持有
    /// 那几个控件实例, 事件绑定、ApplyUiLock 的启停规则、Load/SaveSettings 的读写路径全部
    /// 原样保留; 这里只改父容器, 关闭时再搬回去并还原搬出时的状态快照 —— 所以"在弹层里改
    /// 一下"和"以前在设置页点一下"走的是同一条代码路径。</para>
    ///
    /// <para><b>弹层里嵌着一个会弹弹层的下拉</b>: 「缓冲档位」是自绘 <see cref="RoundedCombo"/>,
    /// 它展开自己的 ComboPopup 时鼠标会离开本弹层窗口 —— ToolStripDropDown 的 AutoClose
    /// 会把本弹层一起收掉, 表现是"选个档位整个菜单消失"。对策: 它 DropDownOpening 时挂起
    /// AutoClose, 关掉后由一个轻量时钟看 IsOpen 恢复。</para>
    /// </summary>
    sealed class SettingsFlyout : ToolStripDropDown
    {
        /// <summary>借来的一个控件 + 它搬出前的状态快照 (与原 SettingsForm.Borrowed 相同)。</summary>
        sealed class Borrowed
        {
            public Control C;
            public bool IsCheck;
            public int ComboIndex = int.MinValue;
            public bool Checked;
        }

        readonly List<Borrowed> borrowed = new List<Borrowed>();
        bool returned;

        readonly Font titleFont = new Font("Microsoft YaHei UI", 10F);
        readonly Font hintFont = new Font("Microsoft YaHei UI", 8.25F);

        /// <summary>借来的缓冲档位下拉 (null = 没借到): 跟踪它自己的弹窗开合, 挂起/恢复 AutoClose。</summary>
        RoundedCombo cmbBuf;
        System.Windows.Forms.Timer resumeTimer;

        // —— 布局常量 = 原设置页的设计像素 (面板按 96 DPI 摆好后整体乘一次缩放系数) ——
        const int PadX = 16;
        const int CardW = 448;
        const int DesignW = 480;
        const int DesignH = 516;

        /// <summary>弹层收起 (点外面/Esc/程序收起) 的时刻 (NowMs 基准)。MainForm 用它做
        /// "刚收起的那次点击别再打开" 的闸 —— 与 RoundedCombo 的 AutoCloseGuardMs 同一套竞态。</para></summary>
        internal long LastAutoCloseMs { get; private set; }

        /// <summary>承载全部选项的面板 (探针用来检查边界/重叠)。</summary>
        internal Control Content { get { return content; } }
        readonly Panel content;

        /// <param name="controls">顺序与 <see cref="MainForm.SettingsControls"/> 严格对应:
        /// 0 缓冲档位标签 / 1 缓冲档位下拉 / 2 自动时钟校正 / 3 捕获消音 / 4 关闭进托盘 / 5 延迟校准。</param>
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

            // DPI: 面板按 96 设计像素摆好, 这里整体乘一次缩放系数 (字体是 pt 单位, 不用缩)
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

            // 内嵌下拉开着 → 挂起自动收起; 关掉 → 恢复。轮询而不是订阅关闭事件,
            // 因为 ComboPopup 没有对外暴露"关了"的通知, IsOpen 是现成的可靠状态。
            resumeTimer = new System.Windows.Forms.Timer { Interval = 100 };
            resumeTimer.Tick += (s, e) =>
            {
                if (cmbBuf == null || cmbBuf.IsDisposed) { resumeTimer.Stop(); return; }
                if (cmbBuf.IsOpen) return;
                if (!AutoClose) AutoClose = true;    // 只在真的挂起过时翻回去, 避免无谓的属性抖动
            };

            Closed += (s, e) =>
            {
                LastAutoCloseMs = NowMs();
                resumeTimer.Stop();
                if (!AutoClose) AutoClose = true;    // 收起时必回常态, 下一次打开才是干净状态
            };
        }

        void BuildUi(Control[] c)
        {
            Control lblBuf = c != null && c.Length > 0 ? c[0] : null;   // "缓冲档位:" 标签
            cmbBuf = c != null && c.Length > 1 ? c[1] as RoundedCombo : null;
            Control chkDrift = c != null && c.Length > 2 ? c[2] : null; // 自动时钟校正
            Control chkMuteCap = c != null && c.Length > 3 ? c[3] : null;
            Control chkTray = c != null && c.Length > 4 ? c[4] : null;
            Control btnCalib = c != null && c.Length > 5 ? c[5] : null;

            // —— 1) 音频链路 ——
            SoftCard card1 = NewCard(14, 158);
            AddTitle(card1, "音频链路");
            if (lblBuf != null)
            {
                lblBuf.Text = "缓冲档位:";
                Borrow(lblBuf, card1, 14, 40, 0, 0);
            }
            else AddLabel(card1, "缓冲档位:", 14, 40);
            if (cmbBuf != null)
            {
                Borrow(cmbBuf, card1, 88, 36, 252, 0);
                // 弹层里点开档位下拉时, 本弹层不能跟着"点外面"收掉
                cmbBuf.DropDownOpening += (s, e) => { if (Visible) AutoClose = false; };
            }
            AddHint(card1, "越低延迟越小, 但对设备抖动越敏感; 运行中不可改, 需先停止分流。", 14, 66, 34);

            if (chkDrift != null)
            {
                chkDrift.Text = "自动时钟校正";
                Borrow(chkDrift, card1, 14, 100, 0, 0);
            }
            AddHint(card1, "微调每路的重采样比例抵消晶振差异: 防止长时间播放后延迟漂移、或水位耗尽断音。",
                14, 124, 30);

            // —— 2) 捕获监听 ——
            SoftCard card2 = NewCard(184, 88);
            AddTitle(card2, "捕获监听");
            if (chkMuteCap != null)
            {
                chkMuteCap.Text = "锁定捕获端静音";
                Borrow(chkMuteCap, card2, 14, 36, 0, 0);
            }
            AddHint(card2, "开启后被外部取消静音会自动恢复, 避免声音从原设备漏出。", 14, 58, 20);

            // —— 3) 窗口行为 ——
            SoftCard card3 = NewCard(284, 88);
            AddTitle(card3, "窗口行为");
            if (chkTray != null)
            {
                chkTray.Text = "关闭窗口时收进系统托盘";
                Borrow(chkTray, card3, 14, 36, 0, 0);
            }
            AddHint(card3, "开启后点关闭只隐藏窗口、分流继续; 双击托盘图标恢复。", 14, 58, 20);

            // —— 4) 校准工具 ——
            SoftCard card4 = NewCard(384, 104);
            AddTitle(card4, "校准工具");
            if (btnCalib != null)
            {
                btnCalib.Text = "延迟校准…";
                Borrow(btnCalib, card4, 14, 34, 152, 30);
            }
            AddHint(card4, "用麦克风实测各路声音到达耳朵的时间差并自动补齐, 适合有线 + 无线混用对不齐。\r\n" +
                "需要一支可用麦克风; 校准会独占输出端点, 请先停止分流。", 14, 72, 30);

            var lblFoot = new Label
            {
                Text = "改动立即生效并自动保存 · 点弹层外或按 Esc 收起",
                Location = new Point(PadX, 496),
                AutoSize = true,
                ForeColor = Color.FromArgb(120, 126, 136),
                Font = hintFont,
                BackColor = Color.White
            };
            content.Controls.Add(lblFoot);
        }

        SoftCard NewCard(int y, int h)
        {
            var card = new SoftCard { Location = new Point(PadX, y), Size = new Size(CardW, h) };
            content.Controls.Add(card);
            return card;
        }

        void AddTitle(Control card, string text)
        {
            card.Controls.Add(new Label
            {
                Text = text,
                Location = new Point(14, 10),
                AutoSize = true,
                Font = titleFont,
                ForeColor = Color.FromArgb(44, 44, 42),
                BackColor = Color.White
            });
        }

        void AddLabel(Control card, string text, int x, int y)
        {
            card.Controls.Add(new Label
            {
                Text = text,
                Location = new Point(x, y + 3),
                AutoSize = true,
                BackColor = Color.White
            });
        }

        void AddHint(Control card, string text, int x, int y, int h)
        {
            card.Controls.Add(new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(CardW - x - 14, h),
                Font = hintFont,
                ForeColor = Color.FromArgb(120, 126, 136),
                BackColor = Color.White
            });
        }

        // ================= 借用 / 归还 (与原 SettingsForm 同一套语义) =================

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

        /// <summary>把主界面持有的控件搬进弹层, 并记下搬出前的状态。</summary>
        void Borrow(Control control, Control host, int x, int y, int w, int h)
        {
            var b = new Borrowed { C = control, IsCheck = control is CheckBox };
            b.ComboIndex = ComboIndexOf(control);
            CheckBox chk = control as CheckBox;
            if (chk != null) b.Checked = chk.Checked;

            if (control.Parent != null) control.Parent.Controls.Remove(control);
            control.Location = new Point(x, y);
            if (w > 0) control.Width = w;
            if (h > 0) control.Height = h;
            host.Controls.Add(control);

            borrowed.Add(b);
        }

        /// <summary>
        /// 把借来的控件原样还给主界面并校正状态。幂等; **必须在 Dispose 之前完成**,
        /// 否则这些控件会跟着弹层一起销毁, 主界面再引用就是访问已释放对象。
        /// </summary>
        public void ReturnControls()
        {
            if (returned) return;
            returned = true;

            if (resumeTimer != null) resumeTimer.Stop();
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

        // ================= 弹层窗口形状 (与 ComboPopup 同一套) =================

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyCornerPreference();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            ApplyCornerPreference();
            if (resumeTimer != null && cmbBuf != null) resumeTimer.Start();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ShapeRound();
        }

        /// <summary>把窗口裁成圆角; Region 比描边外缘外扩 1px, 圆弧的抗锯齿边不被硬裁断。</summary>
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

        /// <summary>关掉 Win11 给顶层窗口自动套的系统圆角 (会叠在自绘 Region 之上, 裁出裂缝)。</summary>
        void ApplyCornerPreference()
        {
            try
            {
                int noRound = 1;                                        // DWMWCP_DONOTROUND
                DwmSetWindowAttribute(Handle, DwmCornerPreference, ref noRound, sizeof(int));
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
                if (resumeTimer != null) { resumeTimer.Dispose(); resumeTimer = null; }
            }
            base.Dispose(disposing);
            if (!disposing) return;
            try { titleFont.Dispose(); } catch { }
            try { hintFont.Dispose(); } catch { }
        }
    }
}
