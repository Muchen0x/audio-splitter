using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using StereoSplitter.Devices;

namespace StereoSplitter
{
    /// <summary>
    /// 一行输出设备 (高 72): 启用勾选 / 设备下拉 / 延迟补偿 / 用几个物理声道 / 承担几个声道 / 离线徽标 / 删除。
    ///
    /// 这一行只回答"这台设备怎么用"；某一路声音从哪来、送到哪个物理声道,
    /// 全部在声道卡片上编辑 —— 两个视角各管一件事, 不互相打架 (原 ChannelMapDialog 取消的原因)。
    /// </summary>
    sealed class SinkRowControl : Panel
    {
        public const int RowHeight = 72;

        static readonly Color Accent = Color.FromArgb(0, 120, 215);
        static readonly Color TextSub = Color.FromArgb(110, 110, 110);

        public SinkSpec Spec;

        readonly CheckBox chkEnabled;
        readonly NoWheelComboBox cmbDevice;
        readonly NumericUpDown nudDelay;
        readonly NoWheelComboBox cmbChannels;
        readonly Button btnRemove;
        readonly Label lblRole;
        readonly Label lblOffline;
        readonly Label lblDelay;
        readonly Label lblMs;

        bool syncing;
        bool offline;
        string roleText = "";
        List<string> deviceIds = new List<string>();

        public event Action<SinkRowControl> Changed;      // 勾选/换设备/延迟/声道数
        public event Action<SinkRowControl> RemoveRequested;

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

            lblRole = new Label
            {
                Text = "",
                AutoSize = false,
                Location = new Point(26, 38),
                Size = new Size(400, 18),
                ForeColor = TextSub
            };
            lblOffline = new Label
            {
                Text = "离线",
                AutoSize = true,
                Location = new Point(0, 38),
                ForeColor = Color.FromArgb(200, 120, 0),
                Visible = false
            };

            Controls.AddRange(new Control[]
            {
                chkEnabled, cmbDevice, lblDelay, nudDelay, lblMs, cmbChannels, btnRemove, lblRole, lblOffline
            });
            LayoutChildren();

            chkEnabled.CheckedChanged += (s, e) => { if (!syncing) Fire(); };
            cmbDevice.SelectedIndexChanged += (s, e) => { if (!syncing) Fire(); };
            nudDelay.ValueChanged += (s, e) => { if (!syncing) Fire(); };
            cmbChannels.SelectedIndexChanged += (s, e) => { if (!syncing) Fire(); };
            btnRemove.Click += (s, e) => { var h = RemoveRequested; if (h != null) h(this); };
        }

        /// <summary>
        /// 右侧一组控件 (延迟 / 声道数 / ×) 按当前宽度显式摆放。
        /// 不用 Anchor: 行是后加进 FlowLayoutPanel 的, 构造时宽度还是默认值,
        /// 那时算出来的"到右边缘的距离"是错的, 控件会被摆到屏幕外。
        /// </summary>
        void LayoutChildren()
        {
            // 构造期 Height= 就会触发 OnResize, 那时字段还没赋值
            if (chkEnabled == null || cmbDevice == null || lblDelay == null || nudDelay == null ||
                lblMs == null || cmbChannels == null || btnRemove == null || lblRole == null || lblOffline == null)
                return;
            int right = Width - 6;
            btnRemove.Left = right - btnRemove.Width;
            cmbChannels.Left = btnRemove.Left - 8 - cmbChannels.Width;
            lblMs.Left = cmbChannels.Left - lblMs.PreferredWidth - 4;
            nudDelay.Left = lblMs.Left - nudDelay.Width - 4;
            lblDelay.Left = nudDelay.Left - lblDelay.PreferredWidth - 6;

            int comboRight = lblDelay.Left - 10;
            cmbDevice.Width = Math.Max(120, comboRight - cmbDevice.Left);
            lblRole.Width = Math.Max(120, btnRemove.Left - lblRole.Left);
            lblOffline.Left = right - lblOffline.PreferredWidth;
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
                lblOffline.Visible = isOffline;
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

        /// <summary>第二行: "承担 2 声道 · 左上(0) 右下(1)" / 或"这台设备没有接到任何声道"。</summary>
        public void SetRole(string text)
        {
            roleText = text ?? "";
            lblRole.Text = roleText;
            lblRole.ForeColor = roleText.StartsWith("未接到", StringComparison.Ordinal)
                ? Color.FromArgb(190, 120, 0) : TextSub;
        }

        void RefreshRoleText()
        {
            if (roleText.Length > 0) lblRole.Text = roleText;
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
