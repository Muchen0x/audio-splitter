using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// 「添加设备」的选择弹窗: 点「添加设备」不再直接加卡片, 而是先在这里选好
    /// **输出到哪台设备** 和 **负责哪个声道**, 确认后按所选建卡片。
    /// <para>设备下拉里已被其它卡片占用的设备显示成灰色 (选了会立刻并进那张卡片,
    /// 用户按"加一路"却看不到卡片变多, 容易以为坏了) —— 默认落在第一台没人用的设备上。</para>
    /// <para>样式与主窗口同一套 (批110): 自绘标题栏 + Win11 圆角边框 + 圆角下拉/圆角按钮;
    /// 「添加/取消」按钮组在窗口内**左右留白相等** (整组居中)。</para>
    /// </summary>
    class AddOutputDialog : Form
    {
        // 批110: 与校准窗同一套自绘框架 (LatencyCalibrationForm): 标题栏高 32 设计px,
        // 内容整体让出这一条; 关闭键与主窗口窗口键同宽。几何全部用设计像素,
        // Load 里的 Dpi.ScaleForm 统一乘 DPI。
        const int TitleBarDesignHeight = 32;

        CaptionButton titleClose;               // 标题栏右端关闭键 (中性灰悬停, 批115)

        // 批115: 标题栏不再画应用图标 (用户要求), 也没有 titleBarIcon 字段了。

        // 批115: 按钮组「留白-取消-留白-添加-留白」三处留白严格等宽 (取消在左、添加在右),
        // 客户区 330 = 3×58 + 2×78, 按钮宽 78, 中缝与两端留白同为 58。两按钮样式一致 (批113)。
        const int BtnW = 78;
        const int BtnH = 28;
        const int BtnGap = 58;

        readonly RoundedCombo cmbDevice = new RoundedCombo
        {
            ItemHeight = 24,
            MaxDropDownItems = 12
        };
        readonly RoundedCombo cmbChannel = new RoundedCombo
        {
            ItemHeight = 24,
            MaxDropDownItems = 12
        };
        readonly RoundButton btnOk;
        readonly RoundButton btnCancel;

        readonly string[] deviceIds;
        readonly List<KeyValuePair<string, string>> channels;

        /// <summary>确认后的选择: 设备 ID。取消时为 null。</summary>
        public string DeviceId { get; private set; }
        /// <summary>确认后的选择: 是否「多声道」形态。</summary>
        public bool IsMulti { get; private set; }
        /// <summary>确认后的选择: 单声道形态的逻辑声道键。</summary>
        public string ChannelKey { get; private set; }

        /// <param name="deviceTexts">设备下拉的显示文本 (与卡片设备下拉同一格式, 平行于 deviceIds)。</param>
        /// <param name="deviceIds">与 deviceTexts 平行的设备 ID。</param>
        /// <param name="usedDeviceIds">已被卡片占用的设备 ID (显示成灰色)。</param>
        /// <param name="defaultDeviceIdx">默认选中的设备下标 (第一台没人用的; -1 = 全被占用, 不预选)。</param>
        /// <param name="specChannels">规格声道清单 (Key → 显示名)。</param>
        /// <param name="defaultChannelKey">默认声道 (第一路没人接的)。</param>
        public AddOutputDialog(string[] deviceTexts, string[] deviceIds, HashSet<string> usedDeviceIds,
            int defaultDeviceIdx, List<KeyValuePair<string, string>> specChannels, string defaultChannelKey)
        {
            this.deviceIds = deviceIds;
            channels = specChannels;

            Text = "添加设备";
            StartPosition = FormStartPosition.CenterParent;
            // 批110: 与校准窗同一手法 —— 收掉系统标题栏, 自己画同色标题栏 + Win11 圆角边框;
            // 保留 WS_THICKFRAME 让 DWM 给出圆角与边框几何 (见 CreateParams/WndProc)。
            FormBorderStyle = FormBorderStyle.None;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
            // 背景与主窗口同支 SystemColors.Control, 不再是纯白 (白底会和主窗口分出两截)。
            BackColor = SystemColors.Control;
            // 用设计像素写: 内容高 144 + 标题栏 32; Load 里 Dpi.ScaleForm 统一乘 DPI。
            Padding = new Padding(0, TitleBarDesignHeight, 0, 0);
            ClientSize = new Size(330, 144 + TitleBarDesignHeight);
            Font = new Font("Microsoft YaHei UI", 9F);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            titleClose = new CaptionButton(CaptionGlyph.Close)
            {
                AccessibleName = "关闭",
                // 批115: 弹窗关闭键不用红色悬停, 对齐输出卡片上的 × (中性灰)
                NeutralClose = true
            };
            titleClose.Click += (s, e) => { DialogResult = DialogResult.Cancel; };

            // —— 布局 (设计像素, 相对内容区) ——
            // 左右对称: 标签距左 16, 下拉右缘距右 16; 两行行距一致 (16/54, 均高 26)。
            const int M = 16;               // 窗口左右留白
            const int ComboX = 104;         // 下拉与标签的分栏线 (标签 AutoSize, 右端约 98)
            const int ComboW = 330 - ComboX - M;
            const int Row1Y = 16, Row2Y = 54;
            int top = TitleBarDesignHeight;

            var lblDev = new Label { Text = "输出到设备:", Location = new Point(M, Row1Y + 5 + top), AutoSize = true };
            var lblChan = new Label { Text = "负责声道:", Location = new Point(M, Row2Y + 5 + top), AutoSize = true };
            cmbDevice.Location = new Point(ComboX, Row1Y + top);
            cmbDevice.Size = new Size(ComboW, 26);
            cmbChannel.Location = new Point(ComboX, Row2Y + top);
            cmbChannel.Size = new Size(ComboW, 26);
            // 批115: 弹窗里的下拉不画蓝色焦点框 (用户要求; 弹层展开中本来也不画)
            cmbDevice.FocusCueEnabled = false;
            cmbChannel.FocusCueEnabled = false;

            cmbDevice.Items.AddRange(deviceTexts);
            cmbDevice.ItemGrayedFilter = idx =>
            {
                string id = idx >= 0 && idx < deviceIds.Length ? deviceIds[idx] : "";
                return usedDeviceIds.Contains(id ?? "");
            };
            if (defaultDeviceIdx >= 0 && defaultDeviceIdx < deviceTexts.Length)
                cmbDevice.SelectedIndex = defaultDeviceIdx;

            var chanItems = new List<object>();
            int multiIdx = -1, defaultIdx = -1;
            for (int i = 0; i < specChannels.Count; i++)
            {
                chanItems.Add(specChannels[i].Value);
                if (specChannels[i].Key == defaultChannelKey) defaultIdx = i;
            }
            chanItems.Add(LaneRouting.MultiText);
            multiIdx = chanItems.Count - 1;
            cmbChannel.Items.AddRange(chanItems.ToArray());
            cmbChannel.SelectedIndex = defaultIdx >= 0 ? defaultIdx : 0;

            // —— 按钮组: 取消在左、添加在右 (批115 用户要求互换), 三处留白 (左端/中缝/右端)
            //    严格等宽 58; 两按钮字体/边框/文字色完全一致 (批113) ——
            int btnY = 144 - M - BtnH;      // 100, 底部留白 = 左右留白 = 16
            int btnLeft = (330 - (BtnW * 2 + BtnGap)) / 2;   // = 58
            btnCancel = new RoundButton
            {
                Text = "取消",
                Location = new Point(btnLeft, btnY + top),
                Size = new Size(BtnW, BtnH),
                Outline = true,
                FillColor = Color.White,
                BorderColor = OutputCardControl.BorderColor,   // 与主界面下拉/卡片同一套浅灰框
                TextColor = Color.FromArgb(64, 64, 64)
            };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; };

            btnOk = new RoundButton
            {
                Text = "添加",
                Location = new Point(btnLeft + BtnW + BtnGap, btnY + top),
                Size = new Size(BtnW, BtnH),
                Outline = true,
                FillColor = Color.White,
                BorderColor = OutputCardControl.BorderColor,   // 与取消完全一致 (批113)
                TextColor = Color.FromArgb(64, 64, 64)
            };
            btnOk.Click += (s, e) => Confirm();

            Controls.AddRange(new Control[] { lblDev, cmbDevice, lblChan, cmbChannel, btnOk, btnCancel, titleClose });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            // 与主窗口同一套 DPI 放大 + 圆角边框 (控件都按 96 DPI 摆好后, Load 里整体乘一次)
            Load += (s2, e2) =>
            {
                Dpi.ScaleForm(this);
                LayoutTitleBar();
                ApplyWindowFrame();
            };
        }

        /// <summary>标题栏右端关闭键: 与主窗口窗口键同宽 (46 设计px), 高 = 标题栏高。</summary>
        void LayoutTitleBar()
        {
            if (titleClose == null) return;
            int bw = Dpi.S(46);
            titleClose.SetBounds(ClientSize.Width - bw, 0, bw, Padding.Top);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int h = Padding.Top;
            if (h <= 0) return;
            // 与主窗口 PaintCaption 同一支底色: 标题栏与内容区融为一体, 不画分隔线。
            using (var b = new SolidBrush(BackColor))
                e.Graphics.FillRectangle(b, 0, 0, ClientSize.Width, h);

            // 批115: 标题栏不画应用图标, 标题文字直接从左留白起
            int x = Dpi.S(12);
            int limit = titleClose != null ? titleClose.Left - Dpi.S(8) : ClientSize.Width - Dpi.S(8);
            TextRenderer.DrawText(e.Graphics, Text, Font,
                new Rectangle(x, 0, Math.Max(0, limit - x), h), Color.FromArgb(32, 32, 32),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        /// <summary>Win11 圆角 + 边框色, 与主窗口/校准窗完全一致 (批105/110)。</summary>
        void ApplyWindowFrame()
        {
            if (!IsHandleCreated) return;
            try
            {
                int round = 2;   // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
                int line = unchecked((int)0x00D8D0CB);
                DwmSetWindowAttribute(Handle, 34, ref line, sizeof(int));
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const int WS_THICKFRAME = 0x00040000;
        const int WS_SYSMENU = 0x00080000;
        const int WM_NCCALCSIZE = 0x0083;
        const int WM_NCHITTEST = 0x0084;
        const int WM_NCACTIVATE = 0x0086;
        const int WM_NCPAINT = 0x0085;
        const int HTCLIENT = 1;
        const int HTCAPTION = 2;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // 与主窗口同一手法: 保留 THICKFRAME 让 DWM 给圆角/边框几何, 但客户区盖满整窗,
                // 那条框由 DWM 按指定颜色画, 系统标题栏一律不画 (下面拦 NC 消息)。
                cp.Style |= WS_THICKFRAME | WS_SYSMENU;
                return cp;
            }
        }

        // 批131: 缩放防闪 (与主窗/校准窗同款) —— 诊断计数供探针读。
        internal int resizeGuardBegins, resizeGuardEnds;
        internal bool ResizeComposited { get { return IsHandleCreated && ResizeFlickerGuard.IsComposited(Handle); } }

        protected override void WndProc(ref Message m)
        {
            // 批131: 拖边缩放期间整窗合成绘制, 与其他两个自绘窗口一致。
            bool enterResize = m.Msg == 0x0231, exitResize = m.Msg == 0x0232;
            if (enterResize)
            {
                ResizeFlickerGuard.Begin(Handle);
                resizeGuardBegins++;
            }
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_NCACTIVATE)
            {
                m.Result = (IntPtr)1;
                Invalidate(new Rectangle(0, 0, ClientSize.Width, Padding.Top));
                return;
            }
            if (m.Msg == WM_NCPAINT)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
            // 批131: EXIT 的合成位必须在 base 之后清 (WinForms 会重读/恢复 ExStyle)。
            if (exitResize)
            {
                resizeGuardEnds++;
                if (IsHandleCreated)
                {
                    ResizeFlickerGuard.End(Handle);
                    Invalidate(true);
                }
            }
            // 系统没有标题栏可拖: 标题区空白交回 HTCAPTION (关闭键是子控件, 自己收点击)。
            if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT)
            {
                var cursor = new Point((short)(m.LParam.ToInt64() & 0xFFFF),
                                       (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
                Point p = PointToClient(cursor);
                if (p.Y >= 0 && p.Y < Padding.Top)
                {
                    if (titleClose != null && titleClose.Visible && titleClose.Bounds.Contains(p))
                        m.Result = (IntPtr)HTCLIENT;
                    else
                        m.Result = (IntPtr)HTCAPTION;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            // 批115: 标题栏不再画应用图标, 没有需要释放的 titleBarIcon 了。
            base.Dispose(disposing);
        }

        void Confirm()
        {
            int di = cmbDevice.SelectedIndex;
            if (di < 0 || di >= deviceIds.Length)
            {
                MessageBox.Show(this, "请选择一台输出设备。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DeviceId = deviceIds[di];
            int ci = cmbChannel.SelectedIndex;
            if (ci >= 0 && ci < cmbChannel.Items.Count &&
                (string)cmbChannel.Items[ci] == LaneRouting.MultiText)
            {
                IsMulti = true;
                ChannelKey = null;
            }
            else
            {
                IsMulti = false;
                ChannelKey = ci >= 0 && ci < channels.Count ? channels[ci].Key : null;
            }
            DialogResult = DialogResult.OK;
        }
    }
}
