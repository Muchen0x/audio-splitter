using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using StereoSplitter.Model;

namespace StereoSplitter
{
    /// <summary>
    /// 输出卡片:
    ///   [设备▾] [声道▾/多声道] [延迟][n][ms] [×]
    ///   音量 [滑条] n% [静音]
    ///   (多声道时) 每行: p0 [声道▾] [滑条] n%
    /// 一张卡片对应一台设备; 声道下拉里的"多声道"把该设备的每个物理声道展开成一行。
    /// </summary>
    class OutputCardControl : UserControl
    {
        public readonly OutputCardModel Model;
        public readonly NoWheelComboBox cmbDevice;
        public readonly NoWheelComboBox cmbChannel;
        public readonly NumericUpDown nudDelay;
        public readonly VolumeSlider volTrack;
        public readonly Label lblVol;
        public readonly CheckBox chkMute;
        public readonly Button btnRemove;

        readonly Label lblDelay, lblMs, lblVolTitle;

        /// <summary>多声道行: 每个物理声道一行 (声道下拉 + 音量条 + 百分比)。</summary>
        sealed class SlotRow
        {
            public Label Tag;
            public NoWheelComboBox Cmb;
            public VolumeSlider Vol;
            public Label Lbl;
        }

        readonly List<SlotRow> rows = new List<SlotRow>();

        // R-02: ToolTip 是独立的 TOOLTIPS_CLASS 原生窗口, 不是本控件的子控件 ——
        // 每张卡片一个, 而 RebuildCards 只 Dispose 卡片本身, 连带不到它。
        // 收进字段, 由 Dispose(bool) 显式释放。
        readonly ToolTip tip;

        bool syncing;
        /// <summary>运行中: 单/多声道形态不可切（会改设备的物理声道数 = 链路级）。</summary>
        bool shapeLocked;
        /// <summary>启动中: 声道下拉整体禁用（新建的行要跟上这个状态）。</summary>
        bool channelLocked;
        /// <summary>启动中: 音量条整体禁用（新建的行要跟上这个状态）。</summary>
        bool volLocked;
        /// <summary>最近一次拿到的规格声道清单（回滚下拉选择时按它把值还原成模型里的）。</summary>
        IList<KeyValuePair<string, string>> channelsRef = new List<KeyValuePair<string, string>>();

        /// <summary>结构性/配置变化(换设备/换声道/延迟/静音/音量松手)。</summary>
        public event Action<OutputCardControl> Changed;
        /// <summary>音量拖动中(连续)。运行中热更新用; 第二参数 true = 拖的是多声道行(增益)。</summary>
        public event Action<OutputCardControl, bool> VolumeScrolled;
        public event Action<OutputCardControl> RemoveRequested;
        /// <summary>运行中试图切换单/多声道形态 → 已被拦下并回滚（调用方负责提示）。</summary>
        public event Action<OutputCardControl> ShapeBlocked;

        public const int CardHeight = 56;       // 单声道卡片高度
        public const int CardWidth = 500;
        public const int RowHeight = 26;        // 多声道每行高度

        /// <summary>卡片之间的竖向间隙 (与 MainForm 里卡片的 Margin.Bottom 对应)。</summary>
        public const int CardGap = 4;

        static readonly Color BorderColor = Color.FromArgb(203, 208, 216);
        static readonly Color RowLabelColor = Color.FromArgb(120, 126, 136);
        const int CornerRadius = 6;
        const int RowTop = 58;                  // 多声道首行 y

        public OutputCardControl(OutputCardModel model)
        {
            Model = model;
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
                DropDownStyle = ComboBoxStyle.DropDownList,
                // 自绘: 运行中要把「多声道…」这一项画成灰色（列表项没法单独禁用）
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 18
            };
            cmbChannel.DrawItem += DrawChannelItem;
            lblDelay = new Label { Text = "延迟", AutoSize = true };
            // NoWheelNumeric: 光标停在上面滚轮不会改延迟 (误滚一下就把声场拖偏, 很难发现),
            // 但滚轮会转给外层列表照常滚动; 上下按钮 / ↑↓ 每次 ±10ms
            nudDelay = new NoWheelNumeric
            {
                Width = 52,
                Minimum = 0,
                Maximum = 2000,
                Increment = 10
            };
            lblMs = new Label { Text = "ms", AutoSize = true };
            btnRemove = new NoCueButton
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
            chkMute = new NoCueCheckBox
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
                // 运行中切单/多声道 = 改设备物理声道数（OutChannels），WasapiOut 已经建好，热换不了
                if (shapeLocked && IsShapeChange())
                {
                    RevertChannel();
                    var b = ShapeBlocked;
                    if (b != null) b(this);
                    return;
                }
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
                if (h != null) h(this, false);
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

            tip = new ToolTip();
            tip.SetToolTip(cmbDevice, "这张卡片输出到哪台设备。\n同一台设备只会有一张卡片: 两张卡片选到同一设备会自动合并成多声道卡片。\n运行中不可改（换设备要重建链路），需先停止分流。");
            tip.SetToolTip(cmbChannel, "这张卡片输出的声道。\n选「多声道」= 该设备的每个物理声道各占一行, 行内可分别选声道、分别调音量。\n角标: 规格行右侧的数字 = 有几个物理声道接到该声道。\n运行中可改（只换混音矩阵）；但「多声道…」形态要改设备物理声道数，运行中不可切。");
            tip.SetToolTip(nudDelay, "这张卡片的延迟补偿 (毫秒)。多台设备距离不同时, 给近的加延迟对齐声场。\n上下按钮 / ↑↓ 每次 ±10ms, 也可直接输入数字。\n滚轮在这里不生效(避免误滚把声场拖偏), 但会照常滚动输出卡片列表。\n延迟是设备级: 合并后取原卡片里的最小值。\n运行中可改: 加延迟本路会停顿一下(约等于增量), 减延迟会跳过一小段, 其余不受影响。");
            tip.SetToolTip(volTrack, "主音量。\n单声道卡片: 写该设备的端点音量(与系统音量条双向同步, 停止时还原)。\n多声道卡片: 同样是设备端点音量; 各声道的微调在下方各行。\n双击百分比可复位 100%。\n运行中可调。");
            tip.SetToolTip(btnRemove, "移除这张输出卡片 (整台设备)。\n运行中不可移除（会改变链路），需先停止分流。");
        }

        /// <summary>
        /// R-02: 卡片被重建时 (RebuildCards / 切换规格 / 合并同设备) 释放 ToolTip。
        /// 原生 TOOLTIPS_CLASS 窗口有 finalizer, 但 Gen0/Gen1 在低内存压力机器上很少触发,
        /// 句柄会长期滞留; 反复增删卡片时累积最明显。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing && tip != null)
            {
                try { tip.RemoveAll(); } catch { }
                try { tip.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        /// <summary>行数 (多声道行; 单声道为 0)。</summary>
        public int RowCount { get { return rows.Count; } }

        /// <summary>
        /// 设置卡片形态: 多声道则按 <paramref name="rowCount"/> 建行。
        /// <para>行是设备物理声道, 顺序固定 p0..pN-1; 每行的逻辑声道由行内下拉决定。</para>
        /// </summary>
        public void SetMulti(bool multi, int rowCount, IList<KeyValuePair<string, string>> channels)
        {
            if (channels != null) channelsRef = channels;
            int want = multi ? Math.Max(1, rowCount) : 0;
            // 行数不变就只刷新下拉选项 (避免重建控件导致焦点/滚动位置跳动)
            while (rows.Count > want) RemoveRow(rows.Count - 1);
            while (rows.Count < want) AddRow(rows.Count);
            foreach (var r in rows) FillSlotChannels(r, channels);

            int h = CardHeight + (rows.Count > 0 ? 4 + rows.Count * RowHeight : 0);
            if (Height != h)
            {
                // FlowLayoutPanel 按控件高度排列, 改高度后必须重排
                SuspendLayout();
                Height = h;
                LayoutControls();
                ResumeLayout();
            }
            LayoutControls();
        }

        void AddRow(int index)
        {
            var r = new SlotRow();
            r.Tag = new Label
            {
                Text = "p" + index,
                AutoSize = false,
                Width = 24,
                Height = 20,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = RowLabelColor
            };
            r.Cmb = new NoWheelComboBox
            {
                Width = 92,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Enabled = !channelLocked        // 启动中新建的行也要是禁用状态
            };
            r.Vol = new VolumeSlider
            {
                Width = 200,
                Height = 20,
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                SmallChange = 5,
                LargeChange = 5,
                Enabled = !volLocked          // 启动中新建的行也一样
            };
            r.Lbl = new Label
            {
                Text = "100%",
                AutoSize = false,
                Width = 42,
                Height = 20,
                TextAlign = ContentAlignment.MiddleLeft
            };

            r.Cmb.SelectedIndexChanged += (s, e) => { if (!syncing) Fire(); };
            r.Vol.ValueChanged += (s, e) =>
            {
                if (syncing) return;
                r.Lbl.Text = r.Vol.Value + "%";
                var h = VolumeScrolled;
                if (h != null) h(this, true);
            };
            r.Vol.MouseUp += (s, e) => Fire();
            r.Vol.KeyUp += (s, e) => Fire();
            r.Lbl.DoubleClick += (s, e) => { r.Vol.Value = 100; Fire(); };

            Controls.AddRange(new Control[] { r.Tag, r.Cmb, r.Vol, r.Lbl });
            rows.Add(r);
        }

        void RemoveRow(int index)
        {
            SlotRow r = rows[index];
            Controls.Remove(r.Tag);
            Controls.Remove(r.Cmb);
            Controls.Remove(r.Vol);
            Controls.Remove(r.Lbl);
            r.Tag.Dispose();
            r.Cmb.Dispose();
            r.Vol.Dispose();
            r.Lbl.Dispose();
            rows.RemoveAt(index);
        }

        /// <summary>行内声道下拉: 规格声道 + 「(不使用)」。</summary>
        void FillSlotChannels(SlotRow r, IList<KeyValuePair<string, string>> channels)
        {
            syncing = true;
            try
            {
                object keep = r.Cmb.SelectedItem;
                r.Cmb.Items.Clear();
                r.Cmb.Items.Add(UnusedText);
                for (int i = 0; i < channels.Count; i++) r.Cmb.Items.Add(channels[i].Value);
                // 尽量保住原来选中的文字 (控件重建/换规格时不跳回第一项)
                int idx = 0;
                if (keep != null)
                {
                    for (int i = 0; i < r.Cmb.Items.Count; i++)
                        if ((string)r.Cmb.Items[i] == (string)keep) { idx = i; break; }
                }
                if (r.Cmb.SelectedIndex != idx) r.Cmb.SelectedIndex = idx;
            }
            finally { syncing = false; }
        }

        public const string UnusedText = "（不使用）";

        /// <summary>
        /// 按当前宽度摆放全部子控件。
        /// VolumeSlider 的轨道画在控件垂直中心, 所以标签 Top=滑条.Top 时 (标签高 20)
        /// 文字中心正好落在轨道线上。
        /// </summary>
        void LayoutControls()
        {
            int w = Width;

            // 第一行右起排: [×] ← [ms] ← [延迟数字框] ← [延迟] ← [声道], 设备下拉占满左侧。
            // × 紧挨延迟组右侧 (距 ms 8px), 与单位文字不再互相遮挡。
            btnRemove.Location = new Point(w - 8 - btnRemove.Width, 4);
            lblMs.Location = new Point(btnRemove.Left - 8 - lblMs.Width, 8);
            nudDelay.Location = new Point(lblMs.Left - 4 - nudDelay.Width, 5);
            lblDelay.Location = new Point(nudDelay.Left - 4 - lblDelay.Width, 8);
            cmbChannel.Location = new Point(lblDelay.Left - 8 - cmbChannel.Width, 5);
            cmbDevice.Location = new Point(8, 5);
            cmbDevice.Width = Math.Max(90, cmbChannel.Left - 14);

            // 第二行: 音量标题 → 滑条(拉满可用宽度) → 百分比 → 静音 (标签与轨道中心对齐)
            // 单声道卡片高 56 = 上边距4 + 第一行21 + 间隔5 + 第二行20 + 下边距6, 下方不再留空。
            const int row2 = 30;
            lblVolTitle.Location = new Point(10, row2);
            int volX = lblVolTitle.Right + 6;
            volTrack.Location = new Point(volX, row2);
            volTrack.Width = Math.Max(120, w - volX - (8 + lblVol.Width + 12 + chkMute.Width + 8));
            lblVol.Location = new Point(volTrack.Right + 8, row2);
            chkMute.Location = new Point(lblVol.Right + 12, row2 - 2);

            // 多声道行: p0 [声道▾] [滑条] n%
            for (int i = 0; i < rows.Count; i++)
            {
                int y = RowTop + i * RowHeight;
                SlotRow r = rows[i];
                r.Tag.Location = new Point(10, y + 3);
                r.Cmb.Location = new Point(38, y);
                r.Vol.Location = new Point(140, y + 3);
                r.Vol.Width = Math.Max(100, w - 140 - (8 + r.Lbl.Width + 10));
                r.Lbl.Location = new Point(r.Vol.Right + 8, y + 3);
            }
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

        /// <summary>用模型值回填控件 (载入/外部改动后调用)。行数须先由 SetMulti 定好。</summary>
        public void SyncFromModel(IList<KeyValuePair<string, string>> channels)
        {
            if (channels != null) channelsRef = channels;
            syncing = true;
            try
            {
                nudDelay.Value = Math.Max(nudDelay.Minimum, Math.Min(nudDelay.Maximum, Model.DelayMs));
                volTrack.Value = Math.Max(volTrack.Minimum, Math.Min(volTrack.Maximum, Model.Volume));
                lblVol.Text = Model.Volume + "%";
                chkMute.Checked = Model.Mute;

                // 声道: 多声道选哨兵项, 单声道选 Slots[0]
                int idx = -1;
                if (Model.Multi)
                {
                    for (int i = 0; i < cmbChannel.Items.Count; i++)
                        if ((string)cmbChannel.Items[i] == LaneRouting.MultiText) { idx = i; break; }
                }
                else
                {
                    string key = Model.Slots.Count > 0 ? Model.Slots[0].ChannelKey : "";
                    for (int i = 0; i < channels.Count; i++)
                        if (channels[i].Key == key) { idx = i; break; }
                }
                cmbChannel.SelectedIndex = idx;

                for (int i = 0; i < rows.Count; i++)
                {
                    string key = i < Model.Slots.Count ? Model.Slots[i].ChannelKey : "";
                    int vi = 0;
                    if (!string.IsNullOrEmpty(key))
                        for (int k = 0; k < channels.Count; k++)
                            if (channels[k].Key == key) { vi = k + 1; break; }
                    rows[i].Cmb.SelectedIndex = vi;
                    int vol = i < Model.Slots.Count ? Model.Slots[i].Volume : 100;
                    rows[i].Vol.Value = Math.Max(0, Math.Min(100, vol));
                    rows[i].Lbl.Text = vol + "%";
                }
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

        /// <summary>
        /// 在下拉末尾追加"已保存设备当前不可用"占位项 (F-07)。该项下标 ≥ 真实设备数，
        /// 上层据此保留模型里保存的设备 ID，不自动换设备、不覆盖保存。
        /// </summary>
        public void AppendMissingDeviceItem(string text)
        {
            cmbDevice.Items.Add(text);
        }

        /// <summary>填充声道下拉 (规格声道 + 「多声道」)。</summary>
        public void SetChannels(IList<KeyValuePair<string, string>> channels)
        {
            if (channels != null) channelsRef = channels;
            syncing = true;
            try
            {
                string keep = cmbChannel.SelectedItem as string;
                cmbChannel.Items.Clear();
                for (int i = 0; i < channels.Count; i++) cmbChannel.Items.Add(channels[i].Value);
                cmbChannel.Items.Add(LaneRouting.MultiText);
                int idx = -1;
                if (keep != null)
                    for (int i = 0; i < cmbChannel.Items.Count; i++)
                        if ((string)cmbChannel.Items[i] == keep) { idx = i; break; }
                cmbChannel.SelectedIndex = idx;
            }
            finally { syncing = false; }
        }

        /// <summary>当前选中的声道键; 选中「多声道」时返回 <see cref="LaneRouting.MultiKey"/>。</summary>
        public string SelectedChannelKey(IList<KeyValuePair<string, string>> channels)
        {
            int i = cmbChannel.SelectedIndex;
            if (i < 0) return null;
            if (i >= channels.Count) return LaneRouting.MultiKey;
            return channels[i].Key;
        }

        public int SelectedDeviceIndex { get { return cmbDevice.SelectedIndex; } }

        /// <summary>把控件当前值写回模型。</summary>
        public void WriteBackToModel(IList<KeyValuePair<string, string>> channels)
        {
            if (channels != null) channelsRef = channels;
            string key = SelectedChannelKey(channels);
            Model.Multi = key == LaneRouting.MultiKey;

            Model.DelayMs = (int)nudDelay.Value;
            Model.Volume = volTrack.Value;
            Model.Mute = chkMute.Checked;

            if (!Model.Multi)
            {
                if (Model.Slots.Count == 0) Model.Slots.Add(new ChannelSlot());
                if (key != null) Model.Slots[0].ChannelKey = key;
                Model.Slots[0].Volume = volTrack.Value;
                return;   // 多声道行隐藏时不读 (行值仍留在模型里, 切回来即恢复)
            }

            // 多声道: 行 → Slots (行数由外部按设备声道数维护, 这里只对齐已有行)
            for (int i = 0; i < rows.Count; i++)
            {
                if (i >= Model.Slots.Count) Model.Slots.Add(new ChannelSlot());
                int ci = rows[i].Cmb.SelectedIndex;
                Model.Slots[i].ChannelKey = ci <= 0 ? "" : channels[ci - 1].Key;
                Model.Slots[i].Volume = rows[i].Vol.Value;
            }
        }

        void Fire()
        {
            var h = Changed;
            if (h != null) h(this);
        }

        // ==================== 运行中锁定 ====================
        //
        // 分四档, 与内核能力严格对齐:
        //   链路级 (设备 / 移除卡片 / 单多声道形态)  → 运行中置灰 (换端点或换缓冲必须重建)
        //   延迟级 (延迟)                            → 只有启动中置灰; 运行中改的是延迟线深度,
        //                                             不碰端点/矩阵, 就地生效
        //   矩阵级 (卡片声道 / 多声道行声道)          → 运行中可改, 热替换矩阵
        //   采样级 (音量 / 静音)                      → 运行中可改

        /// <summary>链路级: 运行中禁用换设备 / 移除卡片 (换端点必须重建链路)。</summary>
        public void SetStructuralLock(bool locked)
        {
            cmbDevice.Enabled = !locked;
            btnRemove.Enabled = !locked;
        }

        /// <summary>延迟: 只在"启动中"禁用。运行中改延迟只动本路的延迟线深度,
        /// 不换端点也不换矩阵, 引擎能就地应用 (见 SinkRuntime.SetDelayMs)。</summary>
        public void SetDelayLock(bool locked)
        {
            nudDelay.Enabled = !locked;
        }

        /// <summary>声道下拉(含多声道各行): 只在启动过程中禁用; 运行中是矩阵级, 可以改。</summary>
        public void SetChannelLock(bool locked)
        {
            channelLocked = locked;
            cmbChannel.Enabled = !locked;
            for (int i = 0; i < rows.Count; i++) rows[i].Cmb.Enabled = !locked;
        }

        /// <summary>采样级: 卡片主音量 / 静音 / 多声道各行音量。只在启动过程中禁用
        /// （启动用的是点下去那一刻的模型快照，中途改值不会进这一趟）。</summary>
        public void SetVolumeLock(bool locked)
        {
            volLocked = locked;
            volTrack.Enabled = !locked;
            chkMute.Enabled = !locked;
            for (int i = 0; i < rows.Count; i++) rows[i].Vol.Enabled = !locked;
        }

        /// <summary>单/多声道形态: 会改设备物理声道数, 运行中把「多声道…」画灰并拦下。</summary>
        public void SetShapeLock(bool locked)
        {
            bool changed = shapeLocked != locked;
            shapeLocked = locked;
            if (changed && cmbChannel.IsHandleCreated) cmbChannel.Invalidate();
        }

        /// <summary>当前下拉选择是否改变单/多声道形态 (与模型里的形态不一致)。</summary>
        bool IsShapeChange()
        {
            string key = SelectedChannelKey(channelsRef);
            if (key == null) return false;      // 还没选 → 交给上层补设备
            return (key == LaneRouting.MultiKey) != Model.Multi;
        }

        /// <summary>把下拉还原成模型里的值 (拦下非法切换后调用, 全程不发事件)。</summary>
        void RevertChannel()
        {
            if (channelsRef == null) return;
            SyncFromModel(channelsRef);
        }

        /// <summary>「多声道…」项的下标 (= 规格声道数, 它总在最后)。</summary>
        int MultiItemIndex { get { return channelsRef == null ? -1 : channelsRef.Count; } }

        /// <summary>
        /// 声道下拉自绘: 运行中把「多声道…」画成灰色。ComboBox 没有"禁用单个列表项"的能力，
        /// 只能自绘 + 在选择事件里回滚。
        /// </summary>
        void DrawChannelItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            int idx = e.Index >= 0 ? e.Index : cmbChannel.SelectedIndex;
            if (idx < 0 || idx >= cmbChannel.Items.Count) return;

            bool dim = !cmbChannel.Enabled || (shapeLocked && idx >= MultiItemIndex);
            Color fg;
            if (dim) fg = SystemColors.GrayText;
            else if ((e.State & DrawItemState.Selected) != 0) fg = SystemColors.HighlightText;
            else fg = cmbChannel.ForeColor;

            // 外框那条 (e.Index < 0) 的 Bounds 含边框, 留 3px 内缩, 与默认绘制的文字位置对齐
            Rectangle r = e.Bounds;
            if (e.Index < 0) r = new Rectangle(r.X + 3, r.Y, r.Width - 6, r.Height);
            TextRenderer.DrawText(e.Graphics, cmbChannel.Items[idx].ToString(), cmbChannel.Font, r, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            // 不再 DrawFocusRectangle: 下拉项上的虚线焦点框也是"点一下留一圈虚线"的来源
        }
    }
}
