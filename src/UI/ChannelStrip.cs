using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace StereoSplitter
{
    enum CardState { Ok, Unbound, Conflict, Offline }

    /// <summary>
    /// 一张声道卡 (108×88): 状态点+名称+Kind / 源抽头 / 承接设备。
    /// 三行信息固定, 任何改动都落在这三行里 —— 不用点开就知道这一路现在怎么回事。
    /// </summary>
    sealed class ChannelCardControl : Control
    {
        public const int CardWidth = 108;
        public const int CardHeight = 82;

        static readonly Color Accent = Color.FromArgb(0, 120, 215);
        static readonly Color Border = Color.FromArgb(208, 208, 208);
        static readonly Color FillNormal = Color.FromArgb(252, 252, 252);
        static readonly Color FillSelected = Color.FromArgb(232, 242, 252);
        static readonly Color TextMain = Color.FromArgb(32, 32, 32);
        static readonly Color TextSub = Color.FromArgb(112, 112, 112);

        public LogicalChannel Ch;
        public string TapText = "";
        public string SinkText = "";
        public CardState State = CardState.Unbound;
        public bool Selected;

        Rectangle closeRect;
        bool hoverClose;

        public event Action<ChannelCardControl> Activate;
        public event Action<ChannelCardControl> Remove;

        public ChannelCardControl(LogicalChannel ch)
        {
            Ch = ch;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(CardWidth, CardHeight);
            Margin = new Padding(0, 0, 6, 0);
            // 整块自己画满, 不用透明背景 (Control 默认不支持 Transparent, 会抛 ArgumentException)
            BackColor = SystemColors.Control;
            Cursor = Cursors.Hand;
        }

        static Color StateColor(CardState s)
        {
            switch (s)
            {
                case CardState.Ok: return Color.FromArgb(46, 125, 50);
                case CardState.Conflict: return Color.FromArgb(211, 47, 47);
                case CardState.Offline: return Color.FromArgb(249, 168, 37);
                default: return Color.FromArgb(158, 158, 158);
            }
        }

        static string KindText(ChannelKind k)
        {
            switch (k)
            {
                case ChannelKind.FL: return "FL";
                case ChannelKind.FR: return "FR";
                case ChannelKind.C: return "C";
                case ChannelKind.LFE: return "LFE";
                case ChannelKind.SL: return "SL";
                case ChannelKind.SR: return "SR";
                case ChannelKind.BL: return "BL";
                case ChannelKind.BR: return "BR";
                default: return "";
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var fill = new SolidBrush(Selected ? FillSelected : FillNormal))
            using (var pen = new Pen(State == CardState.Conflict ? StateColor(CardState.Conflict) : (Selected ? Accent : Border),
                                     Selected || State == CardState.Conflict ? 2f : 1f))
            {
                g.FillRectangle(fill, r);
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }

            // 第 1 行: 状态点 + 名称 + Kind
            using (var dot = new SolidBrush(StateColor(State)))
                g.FillEllipse(dot, 7, 9, 7, 7);
            using (var br = new SolidBrush(Ch.Mute ? TextSub : TextMain))
            using (var f = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold))
                g.DrawString(Clip(g, Ch.Name, f, Width - 44), f, br, 18, 4);
            using (var br = new SolidBrush(TextSub))
            using (var f = new Font("Microsoft YaHei UI", 7.5F))
            {
                string kind = KindText(Ch.Kind);
                if (kind.Length > 0)
                {
                    var sz = g.MeasureString(kind, f);
                    g.DrawString(kind, f, br, Width - sz.Width - 6, 7);
                }
            }

            // 第 2 行: 源抽头
            using (var br = new SolidBrush(TextSub))
            using (var f = new Font("Consolas", 8.5F))
                g.DrawString(Clip(g, TapText, f, Width - 14), f, br, 7, 30);

            // 第 3 行: 承接设备
            using (var br = new SolidBrush(State == CardState.Unbound ? Color.FromArgb(190, 120, 0) : TextMain))
            using (var f = new Font("Microsoft YaHei UI", 8F))
                g.DrawString(Clip(g, SinkText, f, Width - 14), f, br, 7, 54);

            if (Ch.Mute)
            {
                using (var br = new SolidBrush(Color.FromArgb(150, 150, 150)))
                using (var f = new Font("Microsoft YaHei UI", 7.5F))
                    g.DrawString("静音", f, br, Width - 30, 70);
            }

            // 右上角 ×
            closeRect = new Rectangle(Width - 18, 2, 16, 16);
            using (var pen = new Pen(hoverClose ? Color.FromArgb(211, 47, 47) : Color.FromArgb(190, 190, 190), 1.4f))
            {
                g.DrawLine(pen, closeRect.X + 4, closeRect.Y + 4, closeRect.Right - 4, closeRect.Bottom - 4);
                g.DrawLine(pen, closeRect.Right - 4, closeRect.Y + 4, closeRect.X + 4, closeRect.Bottom - 4);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = closeRect.Contains(e.Location);
            if (h != hoverClose) { hoverClose = h; Cursor = h ? Cursors.Hand : Cursors.Hand; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverClose) { hoverClose = false; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (closeRect.Contains(e.Location))
            {
                var rm = Remove;
                if (rm != null) rm(this);
                return;
            }
            var ac = Activate;
            if (ac != null) ac(this);
            base.OnMouseClick(e);
        }

        internal static string Clip(Graphics g, string s, Font f, int maxWidth)
        {
            if (string.IsNullOrEmpty(s) || maxWidth <= 8) return s ?? "";
            if (g.MeasureString(s, f).Width <= maxWidth) return s;
            int lo = 0, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (g.MeasureString(s.Substring(0, mid) + "…", f).Width <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return s.Substring(0, lo) + "…";
        }
    }

    /// <summary>
    /// 声道清单: 工具条 (声道数 / 布局 / 加一个 / 自动绑定 / 已分配) + 横向滚动卡片带。
    /// 卡片带用 FlowLayoutPanel(LeftToRight, WrapContents=false, AutoScroll) 实现,
    /// 数量超出可视为宽度时自动出横向滚动条, 由 WheelRouter 把滚轮转进来。
    /// </summary>
    sealed class ChannelStripControl : UserControl
    {
        // 32(工具条) + 82(卡片) + 17(横向滚动条, 卡片多了才出现) + 余量
        public const int StripHeight = 128;

        readonly NumericUpDown nudCount;
        readonly ComboBox cmbLayout;
        readonly Button btnAdd;
        readonly Button btnAuto;
        readonly Label lblAssigned;
        public readonly FlowLayoutPanel Band;

        readonly List<ChannelCardControl> cards = new List<ChannelCardControl>();
        Control hint;

        RoutingModel model;
        bool syncing;

        /// <summary>模型被本控件改动后触发 (增删声道 / 改声道数 / 改布局 / 自动绑定)。</summary>
        public event Action ModelChanged;
        /// <summary>点卡片: 请求打开编辑浮层 (参数为声道下标)。</summary>
        public event Action<int> EditRequested;
        /// <summary>sinkKey → 显示名 (由主窗体提供, 避免控件知道设备列表)。</summary>
        public Func<string, string> SinkLabelOf;
        /// <summary>sinkKey → 该路可用物理声道数。</summary>
        public Func<string, int> SinkChannelsOf;
        /// <summary>sinkKey → 该路是否离线。</summary>
        public Func<string, bool> SinkOffline;

        public int SelectedIndex = -1;

        public ChannelStripControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            Height = StripHeight;
            BackColor = SystemColors.Control;

            var lblTitle = new Label { Text = "声道清单", Location = new Point(12, 8), AutoSize = true, Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };

            nudCount = new NoWheelNumeric
            {
                Location = new Point(80, 5),
                Width = 52,
                Minimum = 1,
                Maximum = RoutingModel.MaxChannels,
                Value = 2
            };
            var lblLayout = new Label { Text = "布局", Location = new Point(142, 8), AutoSize = true };
            cmbLayout = new NoWheelComboBox
            {
                Location = new Point(176, 3),
                Width = 88,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbLayout.Items.AddRange(new object[] { "单声道", "立体声", "四声道", "5.1", "7.1" });

            btnAdd = new Button { Text = "＋ 加一个", Location = new Point(274, 2), Width = 78, Height = 24 };
            btnAuto = new Button { Text = "自动绑定", Location = new Point(358, 2), Width = 78, Height = 24 };
            lblAssigned = new Label
            {
                Text = "已分配 0/0",
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                Location = new Point(440, 8),
                Width = 130
            };

            Band = new FlowLayoutPanel
            {
                Location = new Point(0, 32),
                Size = new Size(Width, StripHeight - 32),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(10, 0, 0, 0),
                BackColor = SystemColors.Control
            };

            Controls.AddRange(new Control[] { lblTitle, nudCount, lblLayout, cmbLayout, btnAdd, btnAuto, lblAssigned, Band });

            nudCount.ValueChanged += (s, e) => { if (syncing) return; ApplyCount((int)nudCount.Value); };
            cmbLayout.SelectedIndexChanged += (s, e) => { if (syncing) return; ApplyLayout(); };
            btnAdd.Click += (s, e) => AddChannel();
            btnAuto.Click += (s, e) =>
            {
                if (model == null) return;
                model.AutoBind(EnabledSinks());
                Notify();
            };
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayOut();
        }

        /// <summary>工具条右侧的"已分配"和卡片带都按当前宽度摆放 (不靠 Anchor, 理由同 SinkRow)。</summary>
        void LayOut()
        {
            if (lblAssigned == null || Band == null || btnAuto == null || btnAdd == null) return;
            int w = ClientSize.Width;
            lblAssigned.Left = Math.Max(200, w - lblAssigned.Width - 14);
            Band.Left = 0;
            Band.Top = 32;
            Band.Width = w;
            Band.Height = Math.Max(60, ClientSize.Height - 32);
            btnAuto.Left = lblAssigned.Left - btnAuto.Width - 10;
            btnAdd.Left = btnAuto.Left - btnAdd.Width - 6;
        }

        public RoutingModel Model
        {
            get { return model; }
            set { model = value; Rebuild(); }
        }

        List<SinkSpec> EnabledSinks()
        {
            return model == null ? new List<SinkSpec>() : model.EnabledSinks();
        }

        /// <summary>整表刷新 (模型变了/设备变了都调这个)。保留横向滚动位置。</summary>
        public void Rebuild()
        {
            if (model == null) return;

            int scrollX = -Band.AutoScrollPosition.X;
            Band.SuspendLayout();
            try
            {
                for (int i = 0; i < cards.Count; i++)
                {
                    cards[i].Activate -= OnCardActivate;
                    cards[i].Remove -= OnCardRemove;
                    Band.Controls.Remove(cards[i]);
                    cards[i].Dispose();
                }
                cards.Clear();
                if (hint != null) { Band.Controls.Remove(hint); hint.Dispose(); hint = null; }

                for (int i = 0; i < model.Channels.Count; i++)
                {
                    var card = new ChannelCardControl(model.Channels[i]);
                    card.Selected = (i == SelectedIndex);
                    card.Activate += OnCardActivate;
                    card.Remove += OnCardRemove;
                    RefreshCardText(card);
                    cards.Add(card);
                    Band.Controls.Add(card);
                }
            }
            finally { Band.ResumeLayout(); }

            Band.AutoScrollPosition = new Point(Math.Max(0, scrollX), 0);

            syncing = true;
            try
            {
                int n = model.Channels.Count;
                nudCount.Value = Math.Max(nudCount.Minimum, Math.Min(nudCount.Maximum, n));
                cmbLayout.SelectedIndex = LayoutIndex(model.Layout);
                int assigned = 0;
                for (int i = 0; i < n; i++) if (HasBinding(model.Channels[i].Key)) assigned++;
                lblAssigned.Text = "已分配 " + assigned + "/" + n;
                lblAssigned.ForeColor = assigned == n ? Color.FromArgb(46, 125, 50) : Color.FromArgb(190, 120, 0);
            }
            finally { syncing = false; }

            if (model.Channels.Count == 0) ShowEmptyHint();
        }

        void ShowEmptyHint()
        {
            // 空清单不是错误状态, 而是"还没开始": 给一句下一步提示, 别只留一片空白
            var hintLabel = new Label
            {
                Text = "还没有声道 —— 在上面选一个工作模式，或点「＋ 加一个」手动添加",
                AutoSize = true,
                ForeColor = Color.FromArgb(130, 130, 130),
                Location = new Point(12, 8)
            };
            hint = hintLabel;
            Band.Controls.Add(hintLabel);
        }

        void RefreshCardText(ChannelCardControl card)
        {
            card.TapText = DescribeTaps(card.Ch);
            card.SinkText = DescribeSinks(card.Ch);
            card.State = ComputeState(card.Ch);
            card.Invalidate();
        }

        CardState ComputeState(LogicalChannel ch)
        {
            if (model == null) return CardState.Unbound;
            var binds = new List<ChannelBinding>();
            for (int i = 0; i < model.Bindings.Count; i++)
                if (model.Bindings[i].ChannelKey == ch.Key) binds.Add(model.Bindings[i]);

            if (binds.Count == 0) return CardState.Unbound;
            if (ch.Mute) return CardState.Unbound;

            bool anyOnline = false;
            bool conflict = false;
            for (int i = 0; i < binds.Count; i++)
            {
                var b = binds[i];
                var sink = FindSink(b.SinkKey);
                if (sink == null) continue;
                if (SinkOffline != null && SinkOffline(b.SinkKey)) continue;
                anyOnline = true;
                int chans = SinkChannelsOf != null ? SinkChannelsOf(b.SinkKey) : sink.OutChannels;
                if (b.Phys < 0 || b.Phys >= chans) conflict = true;
                if (b.Phys >= sink.OutChannels) conflict = true;
            }
            if (conflict) return CardState.Conflict;
            return anyOnline ? CardState.Ok : CardState.Offline;
        }

        SinkSpec FindSink(string key)
        {
            if (model == null) return null;
            for (int i = 0; i < model.SinkSpecs.Count; i++)
                if (model.SinkSpecs[i].Key == key) return model.SinkSpecs[i];
            return null;
        }

        bool HasBinding(string chKey)
        {
            if (model == null) return false;
            for (int i = 0; i < model.Bindings.Count; i++)
                if (model.Bindings[i].ChannelKey == chKey) return true;
            return false;
        }

        string DescribeSinks(LogicalChannel ch)
        {
            if (model == null) return "→ 未分配";
            var parts = new List<string>();
            for (int i = 0; i < model.Bindings.Count; i++)
            {
                var b = model.Bindings[i];
                if (b.ChannelKey != ch.Key) continue;
                string label = SinkLabelOf != null ? SinkLabelOf(b.SinkKey) : b.SinkKey;
                parts.Add(label + b.Phys);
            }
            if (parts.Count == 0) return "→ 未分配";
            if (parts.Count == 1) return "→ " + parts[0];
            return "→ " + parts.Count + " 台";
        }

        static string DescribeTaps(LogicalChannel ch)
        {
            if (ch == null || ch.Taps.Count == 0) return "静音";
            bool sameGain = true;
            float g0 = ch.Taps[0].Gain;
            for (int i = 1; i < ch.Taps.Count; i++)
                if (Math.Abs(ch.Taps[i].Gain - g0) > 0.001f) { sameGain = false; break; }

            var sb = new StringBuilder();
            for (int i = 0; i < ch.Taps.Count; i++)
            {
                if (i > 0) sb.Append('+');
                sb.Append(SrcName(ch.Taps[i].Src));
            }
            if (ch.Taps.Count > 3) return "混合×" + ch.Taps.Count;

            if (sameGain)
            {
                sb.Append(" ×").Append(g0.ToString("0.##"));
            }
            else
            {
                sb.Length = 0;
                for (int i = 0; i < ch.Taps.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(SrcName(ch.Taps[i].Src)).Append('×').Append(ch.Taps[i].Gain.ToString("0.##"));
                }
            }
            return sb.ToString();
        }

        public static string SrcName(int src)
        {
            switch (src)
            {
                case 0: return "L";
                case 1: return "R";
                case 2: return "C";
                case 3: return "LFE";
                case 4: return "SL";
                case 5: return "SR";
                default: return "s" + src;
            }
        }

        static int LayoutIndex(ChannelLayout l)
        {
            switch (l)
            {
                case ChannelLayout.Mono: return 0;
                case ChannelLayout.Stereo: return 1;
                case ChannelLayout.Quad: return 2;
                case ChannelLayout.Surround51: return 3;
                case ChannelLayout.Surround71: return 4;
                default: return 1;
            }
        }

        static ChannelLayout LayoutAt(int i)
        {
            switch (i)
            {
                case 0: return ChannelLayout.Mono;
                case 1: return ChannelLayout.Stereo;
                case 2: return ChannelLayout.Quad;
                case 3: return ChannelLayout.Surround51;
                case 4: return ChannelLayout.Surround71;
                default: return ChannelLayout.Stereo;
            }
        }

        void ApplyCount(int n)
        {
            if (model == null) return;
            n = Math.Max(1, Math.Min(RoutingModel.MaxChannels, n));
            while (model.Channels.Count > n)
            {
                var last = model.Channels[model.Channels.Count - 1];
                model.Channels.RemoveAt(model.Channels.Count - 1);
                model.Bindings.RemoveAll(delegate(ChannelBinding b) { return b.ChannelKey == last.Key; });
            }
            while (model.Channels.Count < n)
                model.Channels.Add(MakeChannel(model.Channels.Count));
            model.Mode = WorkMode.Custom;   // 手动改了声道数 = 用户接管, 模式不再代表清单
            Notify();
        }

        void ApplyLayout()
        {
            if (model == null) return;
            model.Layout = LayoutAt(cmbLayout.SelectedIndex);
            Notify();
        }

        void AddChannel()
        {
            if (model == null) return;
            if (model.Channels.Count >= RoutingModel.MaxChannels) return;
            model.Channels.Add(MakeChannel(model.Channels.Count));
            model.Mode = WorkMode.Custom;
            Notify();
        }

        LogicalChannel MakeChannel(int index)
        {
            // Key 必须唯一: 删过中间声道后 Channels.Count 会指向已存在的 key,
            // 绑定按 key 查声道, 撞了就会把两条绑定算成同一个声道
            int n = index;
            while (model != null && model.FindChannel("ch" + n) != null) n++;
            var c = new LogicalChannel("ch" + n, "声道" + (n + 1), ChannelKind.Generic);
            c.Order = index;
            c.Taps.Add(new SourceTap(0, 1f));
            return c;
        }

        void OnCardActivate(ChannelCardControl card)
        {
            int idx = cards.IndexOf(card);
            if (idx < 0) return;
            SelectedIndex = idx;
            for (int i = 0; i < cards.Count; i++) cards[i].Selected = (i == idx);
            for (int i = 0; i < cards.Count; i++) cards[i].Invalidate();
            var h = EditRequested;
            if (h != null) h(idx);
        }

        void OnCardRemove(ChannelCardControl card)
        {
            int idx = cards.IndexOf(card);
            if (idx < 0 || model == null) return;
            if (model.Channels.Count <= 1) return;   // 至少留一个声道
            var ch = model.Channels[idx];
            model.Channels.RemoveAt(idx);
            model.Bindings.RemoveAll(delegate(ChannelBinding b) { return b.ChannelKey == ch.Key; });
            model.Mode = WorkMode.Custom;
            SelectedIndex = -1;
            Notify();
        }

        void Notify()
        {
            Rebuild();
            var h = ModelChanged;
            if (h != null) h();
        }

        /// <summary>只刷新卡片文字与统计 (改绑定/增益后不必重建控件)。</summary>
        public void RefreshCards()
        {
            for (int i = 0; i < cards.Count; i++) RefreshCardText(cards[i]);
            if (model == null) return;
            int n = model.Channels.Count, assigned = 0;
            for (int i = 0; i < n; i++) if (HasBinding(model.Channels[i].Key)) assigned++;
            lblAssigned.Text = "已分配 " + assigned + "/" + n;
            lblAssigned.ForeColor = assigned == n ? Color.FromArgb(46, 125, 50) : Color.FromArgb(190, 120, 0);
        }

        public void SelectChannel(int idx)
        {
            SelectedIndex = idx;
            for (int i = 0; i < cards.Count; i++) { cards[i].Selected = (i == idx); cards[i].Invalidate(); }
        }
    }
}
