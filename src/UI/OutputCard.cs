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
    /// 输出卡片: [设备▾] [输出声道▾] [×] / 延迟[n]ms 音量[滑条]n% [静音]。
    /// 取代旧界面的"左声道设备/右声道设备 + 左右延迟 + 左右音量"三组控件。
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

            cmbDevice = new NoWheelComboBox
            {
                Location = new Point(8, 6),
                Width = 250,
                DropDownWidth = 375,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbChannel = new NoWheelComboBox
            {
                Location = new Point(264, 6),
                Width = 112,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            btnRemove = new Button
            {
                Text = "×",
                Location = new Point(466, 4),
                Width = 26,
                Height = 24,
                TabStop = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            var lblDelay = new Label { Text = "延迟", Location = new Point(10, 38), AutoSize = true };
            nudDelay = new NumericUpDown
            {
                Location = new Point(44, 35),
                Width = 56,
                Minimum = 0,
                Maximum = 2000
            };
            var lblMs = new Label { Text = "ms", Location = new Point(103, 38), AutoSize = true };
            var lblVolTitle = new Label { Text = "音量", Location = new Point(136, 38), AutoSize = true };
            volTrack = new TrackBar
            {
                Location = new Point(174, 27),
                Width = 150,
                Height = 45,
                AutoSize = false,
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                TickStyle = TickStyle.None,
                SmallChange = 5,
                LargeChange = 20
            };
            lblVol = new Label
            {
                Text = "100%",
                Location = new Point(328, 38),
                AutoSize = true
            };
            chkMute = new CheckBox { Text = "静音", Location = new Point(372, 36), AutoSize = true };

            var lblRole = new Label
            {
                Text = "输出",
                Location = new Point(418, 38),
                AutoSize = true,
                ForeColor = SystemColors.GrayText
            };

            Controls.AddRange(new Control[]
            {
                cmbDevice, cmbChannel, btnRemove,
                lblDelay, nudDelay, lblMs, lblVolTitle, volTrack, lblVol, chkMute, lblRole
            });

            Size = new Size(500, CardHeight);

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
            tip.SetToolTip(cmbChannel, "这一路输出规格里的哪个声道。\n角标: 规格行右侧的数字 = 有几路输出接到该声道。");
            tip.SetToolTip(nudDelay, "本路延迟补偿 (毫秒)。多台设备距离不同时, 给近的加延迟对齐声场。");
            tip.SetToolTip(volTrack, "本路音量 (写入该设备的端点音量)。双击百分比可复位 100%。");
            tip.SetToolTip(btnRemove, "移除这一路输出");
            tip.SetToolTip(lblRole, "设备行上的物理声道由内核按规格分配");

            SyncFromLane();
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
