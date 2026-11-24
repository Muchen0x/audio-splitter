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
    ///   [🔊] [滑条] n%            ← 喇叭 = 该设备的静音开关 (点一下静音)
    ///   (多声道时) 每行: [声道▾] [滑条] n%
    /// 一张卡片对应一台设备; 声道下拉里的"多声道"把该设备的每个物理声道展开成一行。
    /// </summary>
    class OutputCardControl : UserControl
    {
        public readonly OutputCardModel Model;
        public readonly RoundedCombo cmbDevice;
        public readonly RoundedCombo cmbChannel;
        public readonly NoWheelNumeric nudDelay;
        /// <summary>延迟数字框的圆角外壳 (数字框是原生控件, 圆角边框画在它身上)。</summary>
        public readonly RoundNumericShell nudShell;
        public readonly VolumeSlider volTrack;
        public readonly Label lblVol;
        /// <summary>喇叭按钮: 该设备的静音开关 (原「静音」勾选框, 点图标即切换)。</summary>
        public readonly SpeakerButton btnMute;
        public readonly GearButton btnRemove;

        /// <summary>多声道行: 每个物理声道一行 (声道下拉 + 音量条 + 百分比)。</summary>
        sealed class SlotRow
        {
            public RoundedCombo Cmb;
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

        // 以下尺寸按 96 DPI 的设计值给出, 实际值由 ScaleForDpi 按系统缩放乘一次 (125% → ×1.25)。
        // 做成静态字段而非常量: 卡片宽度参与列数计算, 放大后若仍按设计值算, 卡片会比可视区宽
        // 或排成错误的列数; 常量没法在运行时改。
        public static int CardHeight { get; private set; } = 65;   // 单声道卡片高度 = 8+26+3+20+8
        public static int CardWidth { get; private set; } = 500;
        public static int RowHeight { get; private set; } = 24;    // 多声道每行高度 (行内下拉 22 + 2 行距)

        /// <summary>卡片之间的竖向间隙 (与 MainForm 里卡片的 Margin.Bottom 对应)。</summary>
        public static int CardGap { get; private set; } = 4;

        /// <summary>按 DPI 缩放卡片尺寸 (由 <see cref="Dpi.Init"/> 调一次)。100% 时保持原值。</summary>
        internal static void ScaleForDpi(float f)
        {
            // 高度按**同一批已经取整的零件**加出来, 而不是拿设计值再乘一次:
            // 音量行里的滑条/百分比是 20 物理像素的字面量 (不随 DPI 放大), 拿 20*f 去算卡高,
            // 底部就会凭空多出 5px 的空白 —— 四周留白立刻不等。这里由 Dpi.S 逐件取整再相加,
            // 与 LayoutControls 摆出来的位置逐像素一致 (Dpi.Init 在调本方法前已定好 factor)。
            CardHeight = CardPad + RowControlHeight + RowGapY + VolumeRowHeight + CardPad;
            RowTop = CardPad + RowControlHeight + RowGapY + VolumeRowHeight + RowGapY;
            CardWidth = (int)Math.Round(500 * f);
            RowHeight = (int)Math.Round(24 * f);
            CardGap = (int)Math.Round(4 * f);
        }

        /// <summary>音量行的高度: 滑条与百分比标签都是 20 物理像素 (字面量, 见 LayoutControls 的说明)。</summary>
        internal const int VolumeRowHeight = 20;

        /// <summary>卡片边框色。下拉框本体 / 弹层 / 延迟壳也用这一个, 全界面同一套框。</summary>
        internal static readonly Color BorderColor = Color.FromArgb(203, 208, 216);
        static int CornerRadius { get { return Dpi.S(6); } }
        static int RowTop = 60;                 // 多声道首行 y = 8+26+3+20+3 (由 ScaleForDpi 重算)

        /// <summary>
        /// 卡片四周的留白 (设计像素)。上下左右**同一个值** —— 用户要求"四周留白统一为相同宽度"。
        /// <para>此前各边不同: 左 8 / 上 5 / 下 4 / 右 6(靠 × 的内缩)。卡片一多、竖着排下来,
        /// 上下偏紧、左右偏宽的那点差别会连成一条不齐的"边", 很显眼。</para>
        /// <para>取 8 而不是取中间值: 左侧 8 已经被设备下拉、喇叭图标、行内下拉共用,
        /// 行内各列之间的间隙也是 8, 保持这个刻度最省事, 也不会让内容整体左移。</para>
        /// </summary>
        internal static int CardPad { get { return Dpi.S(8); } }

        /// <summary>第一行与第二行之间、以及音量行与多声道首行之间的竖向间距 (设计像素)。</summary>
        internal static int RowGapY { get { return Dpi.S(3); } }

        /// <summary>
        /// 卡片在给定多声道行数下的总高度。单/多声道共用一套：多声道行是接在"音量行"下方的，
        /// 所以底边留白同样等于 <see cref="CardPad"/>。
        /// <para>MainForm 推卡片高度时也调这里 (卡片还没建出来的时候要能算出高度), 两处公式必须同源。</para>
        /// </summary>
        public static int HeightFor(int rows)
        {
            if (rows <= 0) return CardHeight;
            return RowTop + (rows - 1) * RowHeight + RowComboHeight + CardPad;
        }

        /// <summary>
        /// 卡片第一行控件高度 (设计像素): 设备下拉 / 声道下拉 / 延迟壳三者同高、同顶同底。
        /// <para>一度是 30 —— 为了跟顶部「规格」下拉、「添加设备」按钮(95×38 物理像素)齐平。
        /// 后来用户要求"这三个的高度也缩小一点": 卡片里三块比行内的声道下拉(22)高出一大截,
        /// 显得头重; 收到 26 (125% 下 32 物理像素) 之后一行更紧凑, 与顶部按钮的差距也不再抢眼。</para>
        /// <para>注意这个值不随"与按钮同高"走: 按钮在窗体顶部, 卡片在滚动区里, 两者不并排。</para>
        /// </summary>
        internal static int RowControlHeight { get { return Dpi.S(26); } }

        /// <summary>
        /// 多声道行内下拉的高度 (设计像素)。比第一行的 30 矮一档 —— 用户要求"这两个下拉框高度缩小一点"。
        /// <para>多声道时一屏要竖排 N 个下拉 + 各自的滑条, 30 高的下拉把每行撑得很胖, 两三行就占掉
        /// 半张屏; 行内下拉只承载"这个物理声道接哪个逻辑声道", 一行里也只有几个字, 22 就够。
        /// 与第一行的设备/声道下拉不一致是刻意的, 两者不在同一行, 不会并排比较。</para>
        /// </summary>
        internal static int RowComboHeight { get { return Dpi.S(22); } }

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

            cmbDevice = new RoundedCombo
            {
                Height = RowControlHeight,
                ItemHeight = 24,
                MaxDropDownItems = 12
                // 不设 DropDownWidth: 弹层与本体等宽, 设备名过长时行内省略
            };
            cmbChannel = new RoundedCombo
            {
                Width = Dpi.S(92),
                Height = RowControlHeight,
                ItemHeight = 24,
                MaxDropDownItems = 10,
                // 运行中把「多声道」显示成灰色 (列表项本身没法单独禁用, 只能告诉绘制这行是灰的)。
                // 传闭包而不是快照: shapeLocked 随时在变, 读字段天然跟着变, 配合 SetShapeLock 里的
                // Invalidate 就能立刻反映。
                ItemGrayedFilter = idx => shapeLocked && idx >= MultiItemIndex
            };
            // NoWheelNumeric: 光标停在上面滚轮不会改延迟 (误滚一下就把声场拖偏, 很难发现),
            // 但滚轮会转给外层列表照常滚动; 上下按钮 / ↑↓ 每次 ±10ms
            nudDelay = new NoWheelNumeric
            {
                Width = 52,
                Minimum = 0,
                Maximum = 2000,
                Increment = 10
            };
            // 圆角外壳把「延迟」字样 + 数字框 + 上下箭头 + 「ms」单位包成同一个圆角矩形 (用户指定);
            // 描边画在壳身上, 数字框嵌 1px 内衬。壳的宽度在字体确定后会重算 (Recalc),
            // 布局按壳宽右起排, 所以壳变宽时要重跑一次布局。
            nudShell = new RoundNumericShell(nudDelay, "延迟", "ms");
            // 壳高与旁边的声道圆角下拉取齐 (同 RowControlHeight), 顶边/底边对齐 (用户指定)
            nudShell.FixedHeight = cmbChannel.Height;
            nudShell.SizeChanged += (s, e) => LayoutControls();
            // 移除按钮: "×" 图标 (用户指定), 无边框, 悬停铺浅底; 摆在卡片最右上角 (见 LayoutControls)
            btnRemove = new GearButton
            {
                Glyph = ButtonGlyph.Cross,
                Size = new Size(20, 20)
            };
            // 「×」贴死在卡片右上角 (位置见 LayoutControls), 会压住卡片自己那条顶边/右边和圆角弧。
            // 子控件在父容器之后绘制, 被压住的那一段不会自己回来 —— 让它在画完自己之后再补一遍。
            // 坐标换算: 按钮坐标 → 卡片坐标 (平移回去), 再把裁剪限制在按钮自己那块。
            btnRemove.OwnerFrame = g =>
            {
                var st = g.Save();
                g.TranslateTransform(-btnRemove.Left, -btnRemove.Top);
                g.SetClip(new Rectangle(btnRemove.Left, btnRemove.Top,
                    btnRemove.Width, btnRemove.Height), System.Drawing.Drawing2D.CombineMode.Intersect);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                PaintFrame(g, false);
                g.Restore(st);
            };

            // 第二行: 喇叭(静音开关) + 滑条 + 百分比。
            // 原来这里是「音量」文字 + 滑条 + 百分比 + 「静音」勾选框: 两个标签占掉 ~90px,
            // 滑条被挤到 120px 下限。改成图标后省下的宽度全部给滑条, 而且静音状态就在行首,
            // 不用眼睛跨到行尾去看。
            // 百分比用固定宽度(AutoSize=false): 滑条宽度公式不再依赖字体测量值和当前文字
            // ("100%" vs "5%"), 所有卡片的滑条长度必然一致。
            btnMute = new SpeakerButton();
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
                Width = Dpi.S(42),      // 125% 下 42 物理像素放不下「100%」, 会裁成「100」
                Height = 20,
                TextAlign = ContentAlignment.MiddleLeft
            };

            Controls.AddRange(new Control[]
            {
                cmbDevice, cmbChannel, nudShell, btnRemove,
                btnMute, volTrack, lblVol
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
                // 图标三态之一 (音量 0 = 裸喇叭) 跟着滑条走; 放在 syncing 判断之前,
                // RefreshFromModel 回填模型值时图标也要跟着变
                btnMute.VolumeValue = volTrack.Value;
                if (syncing) return;
                lblVol.Text = volTrack.Value + "%";
                // 音量到 0 自动静音, 拖离 0 取消 (与主音量一致)
                if (btnMute.Muted != (volTrack.Value == 0)) btnMute.Muted = volTrack.Value == 0;
                var h = VolumeScrolled;
                if (h != null) h(this, false);
            };
            volTrack.MouseUp += (s, e) => Fire();
            volTrack.KeyUp += (s, e) => Fire();
            lblVol.DoubleClick += (s, e) => { volTrack.Value = 100; Fire(); };
            btnMute.MutedChanged += (s, e) =>
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
            tip.SetToolTip(cmbDevice, "这张卡片输出到哪台设备。\n点开下拉框时会自动重新枚举设备。\n同一台设备只会有一张卡片: 两张卡片选到同一设备会自动合并成多声道卡片。\n运行中不可改（换设备要重建链路），需先停止分流。");
            tip.SetToolTip(cmbChannel, "这张卡片输出的声道。\n选「多声道」= 该设备的每个物理声道各占一行, 行内可分别选声道、分别调音量。\n角标: 规格行右侧的数字 = 有几个物理声道接到该声道。\n运行中可改（只换混音矩阵）；但「多声道」形态要改设备物理声道数，运行中不可切。");
            tip.SetToolTip(nudDelay, "这张卡片的延迟补偿 (毫秒)。多台设备距离不同时, 给近的加延迟对齐声场。\n上下按钮 / ↑↓ 每次 ±10ms, 也可直接输入数字。\n滚轮在这里不生效(避免误滚把声场拖偏), 但会照常滚动输出卡片列表。\n延迟是设备级: 合并后取原卡片里的最小值。\n运行中可改: 加延迟本路会停顿一下(约等于增量), 减延迟会跳过一小段, 其余不受影响。");
            tip.SetToolTip(volTrack, "主音量。\n单声道卡片: 写该设备的端点音量(与系统音量条双向同步, 停止时还原)。\n多声道卡片: 同样是设备端点音量; 各声道的微调在下方各行。\n双击百分比可复位 100%。\n运行中可调。");
            tip.SetToolTip(btnMute, "点击静音 / 取消静音这台设备 (写设备端点 Mute, 停止时还原)。\n音量拖到 0 会自动静音, 拖回去自动恢复。\n运行中可调。");
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

            int h = HeightFor(rows.Count);
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
            // 行首原来有个 "p0"/"p1" 的序号标签, 已去掉 (用户要求): 行的含义就是物理声道顺序,
            // 而下拉里显示的逻辑声道名已经足够定位, 序号只是噪声, 还占掉 30px 宽度。
            r.Cmb = new RoundedCombo
            {
                Width = Dpi.S(92),              // 初值; 下面开了 AutoFitWidth, 条目进来后会收到刚好
                MinFitWidth = 56,
                AutoFitWidth = true,            // 宽度只留到"刚好放得下最长条目"(用户要求), 让给滑条
                Height = RowComboHeight,
                ItemHeight = 24,
                MaxDropDownItems = 10,
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
                Width = Dpi.S(42),      // 同上: 字面量 42 在 125% 下会把「%」裁掉
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

            Controls.AddRange(new Control[] { r.Cmb, r.Vol, r.Lbl });
            rows.Add(r);
        }

        void RemoveRow(int index)
        {
            SlotRow r = rows[index];
            Controls.Remove(r.Cmb);
            Controls.Remove(r.Vol);
            Controls.Remove(r.Lbl);
            r.Cmb.Dispose();
            r.Vol.Dispose();
            r.Lbl.Dispose();
            rows.RemoveAt(index);
        }

        /// <summary>行内声道下拉: 规格声道 + 「不使用」。</summary>
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
                // 宽度不在这里算: 控件开了 AutoFitWidth, 条目一变它自己收到"刚好放得下最长的一条"。
                // 放在控件里是因为字体要挂上父容器才定下来, 在调用方量会偏窄 (见 AutoFitWidth 注释)。
            }
            finally { syncing = false; }
        }

        /// <summary>
        /// 行内下拉里表示「这个物理声道不接任何逻辑声道」的条目。
        /// <para>不带全角括号: 下拉本体窄, 「（不使用）」光括号就吃掉近 20px; 而且这一条在列表里
        /// 本来就和声道名长得不一样 (不是名字, 是一个状态), 不需要再靠括号区分。</para>
        /// </summary>
        public const string UnusedText = "不使用";

        /// <summary>
        /// 按当前宽度摆放全部子控件。
        /// VolumeSlider 的轨道画在控件垂直中心, 所以标签 Top=滑条.Top 时 (标签高 20)
        /// 文字中心正好落在轨道线上。
        /// </summary>
        void LayoutControls()
        {
            int w = Width;
            int pad = CardPad;                  // 四周统一留白
            // 第一行三个控件同高 (RowControlHeight): 设备下拉 / 声道下拉 / 延迟壳顶边都落在 pad 上,
            // 高度相同所以底边也齐。
            int row1 = pad;
            int ctrlH = RowControlHeight;

            // 第一行右起排: [×] ← [延迟圆角壳(字样+数字框+箭头+ms 单位)] ← [声道], 设备下拉占满左侧。
            // 「×」钉在卡片**最右上角**: (卡宽 - 键宽, 0), 两条边都直接贴卡片边缘, 不参与四周留白。
            // 它不受 pad 影响 —— 以后改留白、加内容都不会把这个键推离角上 (用户要求)。
            // 卡片那一段边框由它自己补画 (见构造函数里的 OwnerFrame)。
            btnRemove.Location = new Point(w - btnRemove.Width, 0);
            nudShell.Location = new Point(btnRemove.Left - Dpi.S(8) - nudShell.Width, row1);
            cmbChannel.Location = new Point(nudShell.Left - Dpi.S(8) - cmbChannel.Width, row1);
            cmbDevice.Location = new Point(pad, row1);
            cmbDevice.Width = Math.Max(Dpi.S(90), cmbChannel.Left - Dpi.S(14));

            // 第二行: 喇叭(静音开关) → 滑条(拉满可用宽度) → 百分比。
            // 喇叭 22 高放在 row2-1 处, 中心正好落在滑条轨道线上 (滑条 20 高, 轨道画在垂直中心);
            // 左缘与第一行的设备下拉同列。
            // 单声道卡片高 = 上 pad + 第一行 + 间距 + 音量行 + 下 pad (与 ScaleForDpi 同一套零件)。
            int row2 = row1 + ctrlH + RowGapY;
            btnMute.Location = new Point(pad, row2 - Dpi.S(1));
            int volX = btnMute.Right + Dpi.S(6);
            volTrack.Location = new Point(volX, row2);
            // 右侧只留 pad: 百分比右缘与第一行的「×」右缘落在同一条竖线上。
            // 公式里那个 8 是滑条与百分比之间的间隙 —— 百分比不是紧贴滑条, 少减它会直接顶出卡片右缘。
            volTrack.Width = Math.Max(Dpi.S(120), w - volX - (Dpi.S(8) + lblVol.Width + pad));
            lblVol.Location = new Point(volTrack.Right + Dpi.S(8), row2);

            // 多声道行: [声道▾] [滑条] n%
            // 行首的 "p0" 序号标签已去掉, 下拉直接贴到 8 (与第一行的设备下拉同列)。
            // 行内下拉 22 高而滑条/标签 20 高, 后两者按"在下拉里居中"偏移 1, 于是
            // 每行的文字中心、轨道线中心都落在下拉的垂直中线上。
            // 滑条起点跟着下拉的**实际**宽度走 (宽度由 FitWidth 按条目文字算, 不再写死 92),
            // 收窄后多出来的宽度全给滑条。
            int comboH = RowComboHeight;
            for (int i = 0; i < rows.Count; i++)
            {
                int y = RowTop + i * RowHeight;
                SlotRow r = rows[i];
                int mid = (comboH - r.Vol.Height) / 2;
                r.Cmb.Location = new Point(Dpi.S(8), y);
                int rowVolX = r.Cmb.Right + Dpi.S(8);
                r.Vol.Location = new Point(rowVolX, y + mid);
                r.Vol.Width = Math.Max(Dpi.S(100), w - rowVolX - (Dpi.S(8) + r.Lbl.Width + pad));
                r.Lbl.Location = new Point(r.Vol.Right + Dpi.S(8), y + mid);
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
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            PaintFrame(g, true);
        }

        /// <summary>
        /// 画卡片外框 (排版按本控件的客户区算)。
        /// <para><b>body = true</b>: 卡片自己的绘制 —— 铺容器底色 → 填圆角白底 → 描边。</para>
        /// <para><b>body = false</b>: 只补"圆角之外那点容器底色 + 一圈边框", 白底不动。
        /// 供贴死在右上角的「×」用: 子控件画在父容器之后, 按钮一压上去, 卡片的那段顶边/右边
        /// 连同圆角弧就被它的白底盖掉了, 只能由按钮在画完自己之后补画回来
        /// (见 GearButton.OwnerFrame)。用同一个方法画, 两处的圆角半径/边框色/半像素对齐才是同一套。</para>
        /// </summary>
        void PaintFrame(Graphics g, bool body)
        {
            Color outside = Parent != null ? Parent.BackColor : BackColor;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = UiDraw.RoundRect(rect, CornerRadius))
            {
                if (body)
                {
                    using (var outsideBrush = new SolidBrush(outside))
                        g.FillRectangle(outsideBrush, ClientRectangle);
                    using (var fill = new SolidBrush(BackColor))
                        g.FillPath(fill, path);
                }
                else
                {
                    // 只补路径之外那一圈: 按钮自己的白底、悬停底色和图标都要留着
                    var st = g.Save();
                    g.SetClip(path, System.Drawing.Drawing2D.CombineMode.Exclude);
                    using (var outsideBrush = new SolidBrush(outside))
                        g.FillRectangle(outsideBrush, ClientRectangle);
                    g.Restore(st);
                }

                // 描边走半像素对齐路径 (见 UiDraw.PenRect): 否则 1px 边被抗锯齿拆成两条灰线
                using (var edgePath = UiDraw.PenRect(this, CornerRadius))
                using (var pen = new Pen(BorderColor))
                    g.DrawPath(pen, edgePath);
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
                btnMute.Muted = Model.Mute;

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
            Model.Mute = btnMute.Muted;

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
            btnMute.Enabled = !locked;
            for (int i = 0; i < rows.Count; i++) rows[i].Vol.Enabled = !locked;
        }

        /// <summary>单/多声道形态: 会改设备物理声道数, 运行中把「多声道」画灰并拦下。</summary>
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

        /// <summary>「多声道」项的下标 (= 规格声道数, 它总在最后)。</summary>
        int MultiItemIndex { get { return channelsRef == null ? -1 : channelsRef.Count; } }
    }
}
