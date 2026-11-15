using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// 单个逻辑声道的编辑浮层 (非模态): 名称 / Kind / 增益 / 静音 / 源抽头 / 送到哪些物理声道。
    ///
    /// 非模态是硬要求 —— 调声道时经常要一边听一边改, 模态框会把整条链路挡在后面,
    /// 想停止分流还得先关对话框。
    /// 所有改动即时写回模型并回调宿主 (宿主负责 Compile → 原子替换 → 卡片刷新),
    /// 因此运行中改绑定不会断音。
    /// </summary>
    sealed class ChannelEditPopup : Form
    {
        readonly LogicalChannel ch;
        readonly RoutingModel model;
        readonly List<SinkSpec> sinks;
        readonly Action onChanged;
        readonly Func<string, bool> sinkOffline;

        TextBox txtName;
        ComboBox cmbKind;
        NumericUpDown nudGain;
        CheckBox chkMute;
        Panel pnlTaps;
        FlowLayoutPanel pnlBinds;
        int srcChannels = 2;
        bool syncing;

        public ChannelEditPopup(LogicalChannel channel, RoutingModel routingModel, List<SinkSpec> sinkList,
            int srcCh, Action changed, Func<string, bool> offlineOf)
        {
            ch = channel;
            model = routingModel;
            sinks = sinkList;
            srcChannels = Math.Max(1, Math.Min(8, srcCh));
            onChanged = changed;
            sinkOffline = offlineOf;

            Text = "声道: " + ch.Name;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(352, 396);
            Font = new Font("Microsoft YaHei UI", 9F);
            MinimizeBox = false;
            MaximizeBox = false;

            BuildUi();
            Rebuild();
        }

        void BuildUi()
        {
            int y = 8;
            Controls.Add(new Label { Text = "名称", Location = new Point(12, y + 3), AutoSize = true });
            txtName = new TextBox { Location = new Point(58, y), Width = 130 };
            txtName.TextChanged += (s, e) => { if (syncing) return; ch.Name = txtName.Text; Text = "声道: " + ch.Name; Changed(); };

            Controls.Add(new Label { Text = "类型", Location = new Point(198, y + 3), AutoSize = true });
            cmbKind = new NoWheelComboBox
            {
                Location = new Point(234, y - 1),
                Width = 100,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbKind.Items.AddRange(new object[] { "FL", "FR", "C", "LFE", "SL", "SR", "BL", "BR", "通用" });
            cmbKind.SelectedIndexChanged += (s, e) => { if (syncing) return; ch.Kind = KindAt(cmbKind.SelectedIndex); Changed(); };
            y += 30;

            Controls.Add(new Label { Text = "增益", Location = new Point(12, y + 3), AutoSize = true });
            nudGain = new NoWheelNumeric
            {
                Location = new Point(58, y),
                Width = 60,
                Minimum = 0,
                Maximum = 200,
                Value = 100
            };
            nudGain.ValueChanged += (s, e) => { if (syncing) return; ch.Gain = (float)nudGain.Value / 100f; Changed(); };
            chkMute = new CheckBox { Text = "静音", Location = new Point(130, y + 2), AutoSize = true };
            chkMute.CheckedChanged += (s, e) => { if (syncing) return; ch.Mute = chkMute.Checked; Changed(); };
            y += 32;

            Controls.Add(new Label
            {
                Text = "源抽头（这个声道从捕获源的哪些声道取样）",
                Location = new Point(12, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100)
            });
            y += 20;

            pnlTaps = new Panel { Location = new Point(12, y), Size = new Size(322, 84), AutoScroll = true, BorderStyle = BorderStyle.FixedSingle };
            Controls.Add(pnlTaps);

            var btnAddTap = new Button { Text = "＋ 加抽头", Location = new Point(12, y + 88), Width = 78, Height = 24 };
            btnAddTap.Click += (s, e) =>
            {
                if (ch.Taps.Count >= 8) return;
                ch.Taps.Add(new SourceTap(0, 1f));
                Rebuild();
                Changed();
            };
            Controls.Add(btnAddTap);
            y += 120;

            Controls.Add(new Label
            {
                Text = "送到哪些物理声道（同一逻辑声道可以送多台设备）",
                Location = new Point(12, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 100, 100)
            });
            y += 20;

            pnlBinds = new FlowLayoutPanel
            {
                Location = new Point(12, y),
                Size = new Size(322, ClientSize.Height - y - 42),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
            };
            Controls.Add(pnlBinds);

            var btnClose = new Button { Text = "关闭", Width = 76, Height = 26, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
            btnClose.Location = new Point(ClientSize.Width - 88, ClientSize.Height - 32);
            btnClose.Click += (s, e) => Close();
            Controls.Add(btnClose);
            CancelButton = btnClose;
        }

        static ChannelKind KindAt(int i)
        {
            switch (i)
            {
                case 0: return ChannelKind.FL;
                case 1: return ChannelKind.FR;
                case 2: return ChannelKind.C;
                case 3: return ChannelKind.LFE;
                case 4: return ChannelKind.SL;
                case 5: return ChannelKind.SR;
                case 6: return ChannelKind.BL;
                case 7: return ChannelKind.BR;
                default: return ChannelKind.Generic;
            }
        }

        static int KindIndex(ChannelKind k)
        {
            switch (k)
            {
                case ChannelKind.FL: return 0;
                case ChannelKind.FR: return 1;
                case ChannelKind.C: return 2;
                case ChannelKind.LFE: return 3;
                case ChannelKind.SL: return 4;
                case ChannelKind.SR: return 5;
                case ChannelKind.BL: return 6;
                case ChannelKind.BR: return 7;
                default: return 8;
            }
        }

        void Rebuild()
        {
            syncing = true;
            try
            {
                txtName.Text = ch.Name;
                cmbKind.SelectedIndex = KindIndex(ch.Kind);
                nudGain.Value = Math.Max(0, Math.Min(200, (int)Math.Round(ch.Gain * 100)));
                chkMute.Checked = ch.Mute;
            }
            finally { syncing = false; }

            RebuildTaps();
            RebuildBinds();
        }

        void RebuildTaps()
        {
            pnlTaps.SuspendLayout();
            try
            {
                foreach (Control c in pnlTaps.Controls) c.Dispose();
                pnlTaps.Controls.Clear();

                if (ch.Taps.Count == 0)
                {
                    pnlTaps.Controls.Add(new Label
                    {
                        Text = "  没有抽头 = 这个声道静音",
                        Location = new Point(4, 6),
                        AutoSize = true,
                        ForeColor = Color.FromArgb(190, 120, 0)
                    });
                }

                for (int i = 0; i < ch.Taps.Count; i++)
                {
                    int idx = i;
                    var tap = ch.Taps[i];
                    int rowY = 4 + i * 28;

                    var cmbSrc = new NoWheelComboBox
                    {
                        Location = new Point(6, rowY),
                        Width = 120,
                        DropDownStyle = ComboBoxStyle.DropDownList
                    };
                    for (int s = 0; s < srcChannels; s++)
                        cmbSrc.Items.Add(ChannelStripControl.SrcName(s) + "  (源声道 " + s + ")");
                    cmbSrc.SelectedIndex = Math.Min(tap.Src, srcChannels - 1);
                    cmbSrc.SelectedIndexChanged += (s, e) =>
                    {
                        if (syncing) return;
                        var t = ch.Taps[idx];
                        t.Src = cmbSrc.SelectedIndex;
                        ch.Taps[idx] = t;
                        Changed();
                    };
                    pnlTaps.Controls.Add(cmbSrc);

                    var lblX = new Label { Text = "×", Location = new Point(130, rowY + 3), AutoSize = true };
                    pnlTaps.Controls.Add(lblX);

                    var nud = new NoWheelNumeric
                    {
                        Location = new Point(146, rowY),
                        Width = 62,
                        Minimum = 0,
                        Maximum = 200,
                        Value = (decimal)Math.Round(tap.Gain * 100)
                    };
                    nud.ValueChanged += (s, e) =>
                    {
                        if (syncing) return;
                        var t = ch.Taps[idx];
                        t.Gain = (float)nud.Value / 100f;
                        ch.Taps[idx] = t;
                        Changed();
                    };
                    pnlTaps.Controls.Add(nud);

                    var btnDel = new Button { Text = "−", Location = new Point(214, rowY), Width = 24, Height = 22 };
                    btnDel.Click += (s, e) =>
                    {
                        if (idx < ch.Taps.Count) ch.Taps.RemoveAt(idx);
                        Rebuild();
                        Changed();
                    };
                    pnlTaps.Controls.Add(btnDel);
                }
            }
            finally { pnlTaps.ResumeLayout(); }
        }

        void RebuildBinds()
        {
            pnlBinds.SuspendLayout();
            try
            {
                foreach (Control c in pnlBinds.Controls) c.Dispose();
                pnlBinds.Controls.Clear();

                for (int s = 0; s < sinks.Count; s++)
                {
                    var sink = sinks[s];
                    if (!sink.Enabled) continue;
                    for (int p = 0; p < Math.Max(1, sink.OutChannels); p++)
                    {
                        string sinkKey = sink.Key;
                        int phys = p;
                        bool off = sinkOffline != null && sinkOffline(sinkKey);
                        var cb = new CheckBox
                        {
                            Text = sink.Label + "  p" + phys + (off ? "  (离线)" : ""),
                            AutoSize = true,
                            Checked = HasBind(sinkKey, phys),
                            ForeColor = off ? Color.FromArgb(190, 120, 0) : SystemColors.ControlText
                        };
                        cb.CheckedChanged += (sender, e) =>
                        {
                            if (syncing) return;
                            SetBind(sinkKey, phys, cb.Checked);
                            Changed();
                        };
                        pnlBinds.Controls.Add(cb);
                    }
                }

                if (pnlBinds.Controls.Count == 0)
                {
                    pnlBinds.Controls.Add(new Label
                    {
                        Text = "没有启用的输出设备 —— 先在下面添加并勾选设备",
                        AutoSize = true,
                        ForeColor = Color.FromArgb(190, 120, 0)
                    });
                }
            }
            finally { pnlBinds.ResumeLayout(); }
        }

        bool HasBind(string sinkKey, int phys)
        {
            for (int i = 0; i < model.Bindings.Count; i++)
            {
                var b = model.Bindings[i];
                if (b.ChannelKey == ch.Key && b.SinkKey == sinkKey && b.Phys == phys) return true;
            }
            return false;
        }

        void SetBind(string sinkKey, int phys, bool on)
        {
            for (int i = model.Bindings.Count - 1; i >= 0; i--)
            {
                var b = model.Bindings[i];
                if (b.ChannelKey == ch.Key && b.SinkKey == sinkKey && b.Phys == phys)
                    model.Bindings.RemoveAt(i);
            }
            if (on) model.Bindings.Add(new ChannelBinding(ch.Key, sinkKey, phys, 1f));
        }

        void Changed()
        {
            var h = onChanged;
            if (h != null) h();
        }
    }
}
