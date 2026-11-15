using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using StereoSplitter.Devices;

namespace StereoSplitter
{
    /// <summary>
    /// 一行输出设备 (高 72), 两行布局:
    ///   第一行  ☑ 设备下拉 .......... 延迟 [0] ms  [1ch▾]  [×]
    ///   第二行  占用 1 个物理声道: 左 → 声道1 ...... 音量 [====] 100%
    ///
    /// 这一行只回答"这台设备怎么用"——用几个声道、每个声道放什么、延迟多少、音量多大;
    /// 某一路声音从哪来、系数多少, 全部在声道卡片上编辑 —— 两个视角各管一件事, 不互相打架。
    ///
    /// 音量是**软件增益** (进编译矩阵), 不是 Windows 端点音量: 端点音量是整台设备共享的,
    /// 两路同设备时调一个会带着另一个一起变, 而且第三方驱动未必支持。
    /// </summary>
    sealed class SinkRowControl : Panel
    {
        public const int RowHeight = 72;

        static readonly Color Accent = Color.FromArgb(0, 120, 215);
        static readonly Color TextSub = Color.FromArgb(110, 110, 110);
        static readonly Color TextWarn = Color.FromArgb(190, 120, 0);

        public SinkSpec Spec;

        readonly CheckBox chkEnabled;
        readonly NoWheelComboBox cmbDevice;
        readonly NumericUpDown nudDelay;
        readonly NoWheelComboBox cmbChannels;
        readonly Button btnRemove;
        readonly Label lblRole;
        readonly Label lblDelay;
        readonly Label lblMs;
        readonly Label lblVol;
        readonly Label lblVolValue;
        readonly TrackBar trkVolume;
        readonly ToolTip tip;

        bool syncing;
        bool offline;
        // 用户真的动过音量滑条才把 trackbar 的值写回模型:
        // TrackBar 是像素量化的 (110px 跑 0~200, 有些值落不回原位, 100 会变 99),
        // 载入时若照着 trackbar 回写, 用户设的 100% 会莫名变成 99%。
        bool volumeDirty;
        string roleText = "";

        List<string> deviceIds = new List<string>();

        public event Action<SinkRowControl> Changed;          // 勾选/换设备/延迟/声道数, 或音量拖完松手
        public event Action<SinkRowControl> VolumeChanged;    // 拖动中的连续事件 (热应用, 不存盘)
        public event Action<SinkRowControl> RemoveRequested;
        public event Action<SinkRowControl> RoleClicked;      // 点第二行 → 主窗体弹"这台设备的声道分配"

        public SinkRowControl(SinkSpec spec)
        {
            Spec = spec;
            Height = RowHeight;
            BackColor = Color.FromArgb(250, 250, 250);
            Margin = new Padding(0, 0, 0, 2);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            chkEnabled = new CheckBox { Text = "", Location = new Point(6, 6), Width = 18, AutoSize = false, Height = 20 };

            cmbDevice = new NoWheelComboBox
            {
                Location = new Point(26, 4),
                Width = 200,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DropDownWidth = 420
            };

            lblDelay = new Label { Text = "延迟", AutoSize = true, Location = new Point(0, 8) };
            nudDelay = new NoWheelNumeric { Location = new Point(0, 5), Width = 54, Maximum = 2000 };
            lblMs = new Label { Text = "ms", AutoSize = true, Location = new Point(0, 8) };
            cmbChannels = new NoWheelComboBox
            {
                Location = new Point(0, 4),
                Width = 64,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            btnRemove = new Button
            {
                Text = "×",
                Location = new Point(0, 4),
                Width = 22,
                Height = 22,
                FlatStyle = FlatStyle.Flat
            };
            btnRemove.FlatAppearance.BorderSize = 0;

            // 第二行左侧: 这台设备的每个物理声道放什么。可点击 → 弹菜单直接改分配,
            // 不必为了换个声道去开声道浮层 (那是改抽头和系数的地方)。
            lblRole = new Label
            {
                Text = "",
                AutoSize = false,
                Location = new Point(26, 33),
                Size = new Size(400, 18),
                ForeColor = TextSub,
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };
            lblRole.Click += (s, e) => { var h = RoleClicked; if (h != null) h(this); };

            // 第二行右侧: 本路音量 (软件增益 0~200%)
            // 注: WinForms 的 TrackBar 有 45px 最小高度, 设 24 会被拉回 45, 所以按 45 布局
            // (位置放在 26, 底 71, 正好在 72 的行高内, 不会压到下一行)。
            lblVol = new Label { Text = "音量", AutoSize = true, Location = new Point(0, 33), ForeColor = TextSub };
            trkVolume = new TrackBar
            {
                Location = new Point(0, 26),
                Width = 110,
                AutoSize = false,
                Minimum = 0,
                Maximum = 200,
                TickFrequency = 50,
                TickStyle = TickStyle.None,
                SmallChange = 5,
                LargeChange = 20,
                Value = 100
            };
            lblVolValue = new Label
            {
                Text = "100%",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(0, 32),
                Size = new Size(42, 18),
                ForeColor = TextSub
            };

            tip = new ToolTip();
            tip.SetToolTip(trkVolume, "本路音量 (软件增益, 100% = 原始电平)。拖动即时生效, 松手后保存。");
            tip.SetToolTip(lblVolValue, "双击恢复 100%");
            tip.SetToolTip(cmbChannels, "这一路用几个物理声道: 左右分离用 1ch, 立体声/多声道用 2ch 以上。");
            tip.SetToolTip(nudDelay, "本路延迟补偿 (毫秒)。多台设备距离不同时, 给近的那台加延迟对齐声场。");
            tip.SetToolTip(lblRole, "「声道1」= 这台设备端点上的第 1 个物理声道 (Windows 声音面板里的第 1 声道)。\n" +
                                    "点一下可以直接改: 哪个声道放左边、哪个放右边、哪个放中置。");

            Controls.AddRange(new Control[]
            {
                chkEnabled, cmbDevice, lblDelay, nudDelay, lblMs, cmbChannels, btnRemove,
                lblRole, lblVol, trkVolume, lblVolValue
            });
            LayoutChildren();

            chkEnabled.CheckedChanged += (s, e) => { if (!syncing) Fire(); };
            cmbDevice.SelectedIndexChanged += (s, e) => { if (!syncing) Fire(); };
            nudDelay.ValueChanged += (s, e) => { if (!syncing) Fire(); };
            cmbChannels.SelectedIndexChanged += (s, e) => { if (!syncing) Fire(); };
            btnRemove.Click += (s, e) => { var h = RemoveRequested; if (h != null) h(this); };

            // 拖动/键盘调整: 值一变就热应用 (每像素写一次 ini 太费, 所以只热应用)
            // 用 ValueChanged 而不是 Scroll: Scroll 只在鼠标拖动时发, 键盘方向键只发 ValueChanged。
            trkVolume.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                volumeDirty = true;
                if (Spec != null) Spec.Volume = trkVolume.Value / 100f;
                lblVolValue.Text = trkVolume.Value + "%";
                var h = VolumeChanged;
                if (h != null) h(this);
            };
            // 松手 / 键盘调整结束: 存盘 + 刷新角色文字
            trkVolume.MouseUp += (s, e) => Fire();
            trkVolume.KeyUp += (s, e) => Fire();
            lblVolValue.DoubleClick += (s, e) => { trkVolume.Value = 100; Fire(); };
        }

        /// <summary>
        /// 右侧一组控件 (延迟 / 声道数 / ×) 与第二行的音量区按当前宽度显式摆放。
        /// 不用 Anchor: 行是后加进 FlowLayoutPanel 的, 构造时宽度还是默认值,
        /// 那时算出来的"到右边缘的距离"是错的, 控件会被摆到屏幕外。
        /// </summary>
        void LayoutChildren()
        {
            // 构造期 Height= 就会触发 OnResize, 那时字段还没赋值
            if (chkEnabled == null || cmbDevice == null || lblDelay == null || nudDelay == null ||
                lblMs == null || cmbChannels == null || btnRemove == null || lblRole == null ||
                lblVol == null || trkVolume == null || lblVolValue == null)
                return;
            int right = Width - 6;
            btnRemove.Left = right - btnRemove.Width;
            cmbChannels.Left = btnRemove.Left - 8 - cmbChannels.Width;
            lblMs.Left = cmbChannels.Left - lblMs.PreferredWidth - 4;
            nudDelay.Left = lblMs.Left - nudDelay.Width - 4;
            lblDelay.Left = nudDelay.Left - lblDelay.PreferredWidth - 6;

            int comboRight = lblDelay.Left - 10;
            cmbDevice.Width = Math.Max(120, comboRight - cmbDevice.Left);

            // 第二行右侧: 音量 (数值 → 滑条 → 标题, 从右往左摆)
            lblVolValue.Left = right - lblVolValue.Width;
            trkVolume.Left = lblVolValue.Left - trkVolume.Width - 6;
            lblVol.Left = trkVolume.Left - lblVol.PreferredWidth - 6;

            int roleRight = lblVol.Left - 12;
            lblRole.Width = Math.Max(120, roleRight - lblRole.Left);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        void Fire()
        {
            if (Spec != null)
            {
                Spec.Enabled = chkEnabled.Checked;
                Spec.DelayMs = (int)nudDelay.Value;
                if (volumeDirty)
                {
                    Spec.Volume = trkVolume.Value / 100f;
                    volumeDirty = false;
                }
                if (cmbDevice.SelectedIndex >= 0 && cmbDevice.SelectedIndex < deviceIds.Count)
                    Spec.DeviceId = deviceIds[cmbDevice.SelectedIndex];
                if (cmbChannels.SelectedIndex >= 0)
                    Spec.OutChannels = cmbChannels.SelectedIndex + 1;
            }
            RefreshRoleText();
            var h = Changed;
            if (h != null) h(this);
        }

        /// <summary>把模型值回填到控件 (装载/解析设备后调)。</summary>
        public void SyncFromSpec()
        {
            syncing = true;
            try
            {
                chkEnabled.Checked = Spec.Enabled;
                nudDelay.Value = Math.Max(0, Math.Min(2000, Spec.DelayMs));
                int vol = (int)Math.Round(Spec.EffectiveVolume * 100);
                trkVolume.Value = Math.Max(trkVolume.Minimum, Math.Min(trkVolume.Maximum, vol));
                // 显示跟模型走: TrackBar 可能把 100 量化成 99, 但用户设的是 100
                lblVolValue.Text = vol + "%";
                volumeDirty = false;
                RefreshChannelChoices();
                RefreshRoleText();
            }
            finally { syncing = false; }
        }

        /// <summary>填入设备列表并选中 spec 里的设备 (找不到则留空, 由主窗体决定是否报错)。</summary>
        public void SetDevices(IReadOnlyList<EndpointInfo> devices, string selectedId, bool isOffline)
        {
            syncing = true;
            try
            {
                deviceIds = new List<string>();
                cmbDevice.Items.Clear();
                for (int i = 0; i < devices.Count; i++)
                {
                    deviceIds.Add(devices[i].ID);
                    cmbDevice.Items.Add("[" + i + "] " + devices[i].FriendlyName);
                }
                int sel = -1;
                for (int i = 0; i < deviceIds.Count; i++)
                    if (deviceIds[i] == selectedId) { sel = i; break; }
                cmbDevice.SelectedIndex = sel;
                offline = isOffline;
                RefreshRoleText();
            }
            finally { syncing = false; }
        }

        void RefreshChannelChoices()
        {
            int want = Math.Max(1, Math.Min(8, Spec.OutChannels));
            cmbChannels.Items.Clear();
            for (int i = 1; i <= want; i++)
                cmbChannels.Items.Add(i + "ch");
            cmbChannels.SelectedIndex = want - 1;
        }

        /// <summary>声道数下拉的候选上限随端点实际声道数变化 (P2 校验会保证不超限)。</summary>
        public void SetChannelLimit(int endpointChannels)
        {
            int want = Math.Max(1, Math.Min(8, Spec.OutChannels));
            int limit = Math.Max(want, Math.Max(1, Math.Min(8, endpointChannels)));
            syncing = true;
            try
            {
                if (cmbChannels.Items.Count == limit) return;
                cmbChannels.Items.Clear();
                for (int i = 1; i <= limit; i++)
                    cmbChannels.Items.Add(i + "ch");
                cmbChannels.SelectedIndex = want - 1;
            }
            finally { syncing = false; }
        }

        /// <summary>第二行文案 (主窗体按绑定算好): "占用 2 个物理声道: 左 → 声道1, 右 → 声道2  ▾"。</summary>
        public void SetRole(string text)
        {
            roleText = text ?? "";
            RefreshRoleText();
        }

        /// <summary>角色文字的屏幕位置, 供"声道分配"菜单贴着它弹出。</summary>
        public Point RoleScreenAnchor
        {
            get { return PointToScreen(new Point(lblRole.Left, lblRole.Top + lblRole.Height + 2)); }
        }

        void RefreshRoleText()
        {
            string t = roleText;
            if (offline && t.Length > 0) t = "设备离线 · " + t;
            lblRole.Text = t;
            lblRole.ForeColor = offline ? TextWarn
                : (t.StartsWith("未接到", StringComparison.Ordinal) ? TextWarn : TextSub);
        }

        /// <summary>音量数值被外部改动后回填 (例如"全部恢复 100%")。</summary>
        public void SetVolumePercent(int percent)
        {
            int v = Math.Max(trkVolume.Minimum, Math.Min(trkVolume.Maximum, percent));
            syncing = true;
            try
            {
                trkVolume.Value = v;
                lblVolValue.Text = v + "%";
                if (Spec != null) Spec.Volume = v / 100f;
            }
            finally { syncing = false; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(224, 224, 224)))
                e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            if (!Spec.Enabled)
            {
                using (var br = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
                    e.Graphics.FillRectangle(br, ClientRectangle);
            }
        }
    }
}
