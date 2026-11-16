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
    ///   第一行 [设备▾] [输出声道▾] [延迟][n][ms] [×]
    ///   第二行 音量 [滑条] n% [静音]
    ///   × 紧挨延迟组右侧 (整组右对齐, 设备下拉吸收剩余宽度), 互不遮挡。
    /// 取代旧界面的"左/右声道设备 + 左右延迟 + 左右音量"三组控件。
    /// </summary>
    class OutputCardControl : UserControl
    {
        public readonly OutputLane Lane;
        public readonly NoWheelComboBox cmbDevice;
        public readonly NoWheelComboBox cmbChannel;
        public readonly NumericUpDown nudDelay;
        public readonly VolumeSlider volTrack;
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

        public const int CardHeight = 56;
        public const int CardWidth = 500;

        /// <summary>卡片之间的竖向间隙 (与 MainForm 里卡片的 Margin.Bottom 对应)。</summary>
        public const int CardGap = 4;

        static readonly Color BorderColor = Color.FromArgb(203, 208, 216);
        const int CornerRadius = 6;

        public OutputCardControl(OutputLane lane)
        {
            Lane = lane;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            SetStyle(ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            // 卡片底色用白, 边框由 OnPaint 画圆角矩形 (子控件透明背景也跟着变白)
            BackColor = Color.White;
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
            // NoWheelNumeric: 光标停在上面滚轮不会改延迟 (误滚一下就把声场拖偏, 很难发现)
            nudDelay = new NoWheelNumeric
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

            // 第二行独占: 音量 + 静音。
            // 标题/百分比/复选框都用固定宽度(AutoSize=false): 滑条宽度公式不再依赖
            // 字体测量值和当前文字("100%" vs "5%"), 所有卡片的滑条长度必然一致。
            lblVolTitle = new Label
            {
                Text = "音量",
                AutoSize = false,
                Width = 32,
                Height = 20,
                TextAlign = ContentAlignment.MiddleLeft
            };
            volTrack = new VolumeSlider
            {
                Width = 250,
                Height = 20,
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                SmallChange = 5,
                LargeChange = 5   // 点击每次 5%, 与主音量一致 (左侧已滑过部分填蓝)
            };
            lblVol = new Label
            {
                Text = "100%",
                AutoSize = false,
                Width = 42,
                Height = 20,
                TextAlign = ContentAlignment.MiddleLeft
            };
            chkMute = new CheckBox
            {
                Text = "静音",
                AutoSize = false,
                Width = 62,
                Height = 20
            };

            Controls.AddRange(new Control[]
            {
                cmbDevice, cmbChannel, lblDelay, nudDelay, lblMs, btnRemove,
                lblVolTitle, volTrack, lblVol, chkMute
            });

            LayoutControls();
            Resize += (s, e) => LayoutControls();
            // 字体要等挂到父容器后才继承到位; 就位后重算一次, 避免首张卡片
            // 按默认字体的测量值算布局、和后续重建的卡片不一致
            ParentChanged += (s, e) => LayoutControls();
            FontChanged += (s, e) => LayoutControls();

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
            // VolumeSlider 任何值变化都发 ValueChanged (拖动/点击/键盘/程序赋值),
            // 与原来的 TrackBar 一致; 拖动中的热更新走 VolumeScrolled。
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
            tip.SetToolTip(cmbDevice, "这一路输出用哪台设备。\n同一台设备可以承载多路（内核按设备合并成一条输出流，按卡片上下顺序分配物理声道）。");
            tip.SetToolTip(cmbChannel, "这一路输出规格里的哪个声道。\n角标: 规格行右侧的数字 = 有几路输出接到该声道。");
            tip.SetToolTip(nudDelay, "本路延迟补偿 (毫秒)。多台设备距离不同时, 给近的加延迟对齐声场。\n同一台设备只有一条延迟线: 该设备上的多张卡片取最小值, 不一致时日志会提示。");
            tip.SetToolTip(volTrack, "本路音量。左/右两张端点卡片写入设备端点音量(与系统音量双向同步),\n其余各路在混音层生效(与同设备的其它路互不干扰)。双击百分比可复位 100%。");
            tip.SetToolTip(btnRemove, "移除这一路输出");

            SyncFromLane();
        }

        /// <summary>
        /// 按当前宽度摆放全部子控件。
        /// VolumeSlider 的轨道画在控件垂直中心, 所以标签 Top=滑条.Top 时 (标签高 20)
        /// 文字中心正好落在轨道线上。
        /// </summary>
        void LayoutControls()
        {
            int w = Width;

            // 第一行右起排: [×] ← [ms] ← [延迟数字框] ← [延迟] ← [输出声道], 设备下拉占满左侧。
            // × 紧挨延迟组右侧 (距 ms 8px), 与单位文字不再互相遮挡。
            btnRemove.Location = new Point(w - 8 - btnRemove.Width, 4);
            lblMs.Location = new Point(btnRemove.Left - 8 - lblMs.Width, 8);
            nudDelay.Location = new Point(lblMs.Left - 4 - nudDelay.Width, 5);
            lblDelay.Location = new Point(nudDelay.Left - 4 - lblDelay.Width, 8);
            cmbChannel.Location = new Point(lblDelay.Left - 8 - cmbChannel.Width, 5);
            cmbDevice.Location = new Point(8, 5);
            cmbDevice.Width = Math.Max(90, cmbChannel.Left - 14);

            // 第二行: 音量标题 → 滑条(拉满可用宽度) → 百分比 → 静音 (标签与轨道中心对齐)
            // 卡片高 56 = 上边距4 + 第一行21 + 间隔5 + 第二行20 + 下边距6, 下方不再留空。
            const int row2 = 30;
            lblVolTitle.Location = new Point(10, row2);
            int volX = lblVolTitle.Right + 6;
            volTrack.Location = new Point(volX, row2);
            volTrack.Width = Math.Max(120, w - volX - (8 + lblVol.Width + 12 + chkMute.Width + 8));
            lblVol.Location = new Point(volTrack.Right + 8, row2);
            chkMute.Location = new Point(lblVol.Right + 12, row2 - 2);
        }

        /// <summary>
        /// 卡片外框: 圆角矩形描边, 圆角外的小方块用容器底色补齐 (否则四个角会露白)。
        /// 先铺容器底色, 再填圆角白底, 最后描边; 子控件在此之后自行绘制。
        /// </summary>
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
