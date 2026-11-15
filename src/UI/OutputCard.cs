using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// 一路输出的模型: 接哪个声道、用哪台设备、延迟/音量/静音。
    /// 界面上的"输出卡片"是它的视图; ini 里的 Out{n}.* 是它的持久化。
    /// </summary>
    class OutputLane
    {
        public string ChannelKey = "FL";   // 规格里的声道键: M/FL/FR/C/LFE/SL/SR/BL/BR
        public string DeviceId = "";
        public int DelayMs;
        public int Volume = 100;           // 0..100, 写端点音量
        public bool Mute;
    }

    /// <summary>
    /// 输出卡片:
    ///   第一行 [设备▾] [输出声道▾] [延迟][n][ms]
    ///   第二行 音量 [滑条] n% [静音]
    ///   最右一列上下居中放 [×] (独立列, 行内控件不进入该列, 不再互相遮挡)。
    /// 取代旧界面的"左/右声道设备 + 左右延迟 + 左右音量"三组控件。
    /// </summary>
    class OutputCardControl : UserControl
    {
        public readonly OutputLane Lane;
        public readonly NoWheelComboBox cmbDevice;
        public readonly NoWheelComboBox cmbChannel;
        public readonly NumericUpDown nudDelay;
        public readonly TrackBar volTrack;
        public readonly Label lblVol;
        public readonly CheckBox chkMute;
        public readonly Button btnRemove;

        readonly Label lblDelay, lblMs, lblVolTitle;

        bool syncing;

        /// <summary>结构性/配置变化(换设备/换声道/延迟/静音/音量松手)。</summary>
        public event Action<OutputCardControl> Changed;
        /// <summary>音量拖动中(连续)。运行中热更新用。</summary>
        public event Action<OutputCardControl> VolumeScrolled;
        public event Action<OutputCardControl> RemoveRequested;

        public const int CardHeight = 78;
        public const int CardWidth = 500;

        public OutputCardControl(OutputLane lane)
        {
            Lane = lane;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            // 先定尺寸再摆子控件; 位置统一由 LayoutControls() 按实际宽度摆放, 不用 Anchor
            Size = new Size(CardWidth, CardHeight);

            cmbDevice = new NoWheelComboBox
            {
                DropDownWidth = 375,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbChannel = new NoWheelComboBox
            {
                Width = 92,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            lblDelay = new Label { Text = "延迟", AutoSize = true };
            nudDelay = new NumericUpDown
            {
                Width = 52,
                Minimum = 0,
                Maximum = 2000
            };
            lblMs = new Label { Text = "ms", AutoSize = true };
            btnRemove = new Button
            {
                Text = "×",
                Width = 26,
                Height = 24,
                TabStop = false
            };

            // 第二行独占: 音量 + 静音
            lblVolTitle = new Label { Text = "音量", AutoSize = true };
            volTrack = new TrackBar
            {
                Width = 250,
                Height = 45,
                AutoSize = false,
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                TickStyle = TickStyle.None,
                SmallChange = 5,
                LargeChange = 20
            };
            lblVol = new Label { Text = "100%", AutoSize = true };
            chkMute = new CheckBox { Text = "静音", AutoSize = true };

            Controls.AddRange(new Control[]
            {
                cmbDevice, cmbChannel, lblDelay, nudDelay, lblMs, btnRemove,
                lblVolTitle, volTrack, lblVol, chkMute
            });

            LayoutControls();
            Resize += (s, e) => LayoutControls();

            cmbDevice.SelectedIndexChanged += (s, e) =>
            {
                if (syncing) return;
                Fire();
            };
            cmbChannel.SelectedIndexChanged += (s, e) =>
            {
                if (syncing) return;
                Fire();
            };
            nudDelay.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                Fire();
            };
            // TrackBar.Scroll 只在鼠标拖动时触发, 键盘调整只发 ValueChanged
            volTrack.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                lblVol.Text = volTrack.Value + "%";
                // 音量到 0 自动勾静音, 拖离 0 取消 (与主音量一致)
                if (chkMute.Checked != (volTrack.Value == 0)) chkMute.Checked = volTrack.Value == 0;
                var h = VolumeScrolled;
                if (h != null) h(this);
            };
            volTrack.MouseUp += (s, e) => Fire();
            volTrack.KeyUp += (s, e) => Fire();
            lblVol.DoubleClick += (s, e) => { volTrack.Value = 100; Fire(); };
            chkMute.CheckedChanged += (s, e) =>
            {
                if (syncing) return;
                Fire();
            };
            btnRemove.Click += (s, e) =>
            {
                var h = RemoveRequested;
                if (h != null) h(this);
            };

            var tip = new ToolTip();
            tip.SetToolTip(cmbDevice, "这一路输出用哪台设备。");
            tip.SetToolTip(cmbChannel, "这一路输出规格里的哪个声道。\n角标: 规格行右侧的数字 = 有几路输出接到该声道。\n设备行上的物理声道由内核按规格分配。");
            tip.SetToolTip(nudDelay, "本路延迟补偿 (毫秒)。多台设备距离不同时, 给近的加延迟对齐声场。");
            tip.SetToolTip(volTrack, "本路音量 (写入该设备的端点音量)。双击百分比可复位 100%。");
            tip.SetToolTip(btnRemove, "移除这一路输出");

            SyncFromLane();
        }

        /// <summary>
        /// 按当前宽度摆放全部子控件。
        /// TrackBar(TickStyle.None) 的轨道画在控件顶部+10px 处而非垂直中心,
        /// 所以标签 Top=track.Top 时文字中心正好落在轨道线上 (标签高约20px)。
        /// </summary>
        void LayoutControls()
        {
            int w = Width;
            int xCol = w - 36;   // 右侧 × 专列左界 (×宽26, 右边距6)

            // × 放最右列, 上下居中 (卡片高78, 按钮高24)
            btnRemove.Location = new Point(w - 32, (CardHeight - 24) / 2);

            // 第一行右起排: ms → 延迟数字框 → 延迟标题 → 输出声道 → 设备
            lblMs.Location = new Point(xCol - 30, 8);
            nudDelay.Location = new Point(lblMs.Left - 4 - nudDelay.Width, 5);
            lblDelay.Location = new Point(nudDelay.Left - 4 - lblDelay.Width, 8);
            cmbChannel.Location = new Point(lblDelay.Left - 8 - cmbChannel.Width, 5);
            cmbDevice.Location = new Point(8, 5);
            cmbDevice.Width = Math.Max(90, cmbChannel.Left - 14);

            // 第二行: 音量标题 → 滑条 → 百分比 → 静音 (标签与轨道中心对齐)
            const int row2 = 30;
            lblVolTitle.Location = new Point(10, row2);
            volTrack.Location = new Point(lblVolTitle.Right + 6, row2);
            lblVol.Location = new Point(Math.Min(xCol - 118, volTrack.Right + 6), row2);
            chkMute.Location = new Point(Math.Min(xCol - 62, lblVol.Right + 12), row2 - 2);
        }

        /// <summary>用模型值回填控件 (载入/外部改动后调用)。</summary>
        public void SyncFromLane()
        {
            syncing = true;
            try
            {
                nudDelay.Value = Math.Max(nudDelay.Minimum, Math.Min(nudDelay.Maximum, Lane.DelayMs));
                volTrack.Value = Math.Max(volTrack.Minimum, Math.Min(volTrack.Maximum, Lane.Volume));
                lblVol.Text = Lane.Volume + "%";
                chkMute.Checked = Lane.Mute;
            }
            finally { syncing = false; }
        }

        /// <summary>填充设备下拉 (与设备目录同序)。</summary>
        public void SetDevices(IList<string> deviceTexts)
        {
            syncing = true;
            try
            {
                cmbDevice.Items.Clear();
                for (int i = 0; i < deviceTexts.Count; i++) cmbDevice.Items.Add(deviceTexts[i]);
                cmbDevice.SelectedIndex = -1;
            }
            finally { syncing = false; }
        }

        /// <summary>按设备下标选中 (下标与设备目录一致; -1 = 未选)。</summary>
        public void SetDeviceIndex(int idx)
        {
            syncing = true;
            try
            {
                if (idx >= 0 && idx < cmbDevice.Items.Count) cmbDevice.SelectedIndex = idx;
                else cmbDevice.SelectedIndex = -1;
            }
            finally { syncing = false; }
        }

        /// <summary>填充声道下拉 (键 + 显示名) 并选中当前声道。</summary>
        public void SetChannels(IList<KeyValuePair<string, string>> channels, string selectedKey)
        {
            syncing = true;
            try
            {
                cmbChannel.Items.Clear();
                for (int i = 0; i < channels.Count; i++) cmbChannel.Items.Add(channels[i].Value);
                int idx = -1;
                for (int i = 0; i < channels.Count; i++)
                    if (channels[i].Key == selectedKey) { idx = i; break; }
                cmbChannel.SelectedIndex = idx;
            }
            finally { syncing = false; }
        }

        /// <summary>当前选中的声道键 (未选返回 null)。</summary>
        public string SelectedChannelKey(IList<KeyValuePair<string, string>> channels)
        {
            int i = cmbChannel.SelectedIndex;
            if (i < 0 || i >= channels.Count) return null;
            return channels[i].Key;
        }

        public int SelectedDeviceIndex { get { return cmbDevice.SelectedIndex; } }

        /// <summary>把控件当前值写回模型。</summary>
        public void WriteBackToLane(IList<KeyValuePair<string, string>> channels)
        {
            string key = SelectedChannelKey(channels);
            if (key != null) Lane.ChannelKey = key;
            Lane.DelayMs = (int)nudDelay.Value;
            Lane.Volume = volTrack.Value;
            Lane.Mute = chkMute.Checked;
        }

        void Fire()
        {
            var h = Changed;
            if (h != null) h(this);
        }

        /// <summary>启动/运行中禁用结构控件, 音量仍可调。</summary>
        public void SetBusy(bool structuralLocked)
        {
            cmbDevice.Enabled = !structuralLocked;
            cmbChannel.Enabled = !structuralLocked;
            nudDelay.Enabled = !structuralLocked;
            btnRemove.Enabled = !structuralLocked;
        }
    }
}
