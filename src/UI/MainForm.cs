using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using StereoSplitter.Devices;

namespace StereoSplitter
{
    class MainForm : Form
    {
        NoWheelComboBox cmbDefault, cmbLatency, cmbSpec;
        NumericUpDown nudWaterInterval;
        // 输出卡片: 每张卡片 = 一台设备 (声道 + 延迟 + 主音量 + 静音; 多声道时另有每行声道与音量)
        readonly List<OutputCardModel> models = new List<OutputCardModel>();   // UI/持久化的权威模型
        readonly List<OutputCardControl> cards = new List<OutputCardControl>();// 控件, 与 models 一一对应
        readonly List<OutputLane> lanes = new List<OutputLane>();              // 内核输入, 由 models 扁平化得到
        bool normalizing;        // 合并/行数校正期间屏蔽卡片事件重入
        FlowLayoutPanel cardHost;
        FlowLayoutPanel flwSpecChannels;
        bool fittingWidths;      // FitCardWidths 重入保护
        int gridLogCols = -1;    // 上次记过日志的列数 (只在列数变化时打, 拖动窗口不刷屏)
        // 上次真正摆过的栅格参数: 完全一致时跳过重排 (缩放窗口时每帧都会被叫到好几次)
        int lastCardCols = -1, lastCardW = -1, lastRightGap = -1, lastCardCount = -1;
        Size lastLogPanelSize = Size.Empty;   // 日志面板上次的尺寸 (没变就不重摆日志框)
        int lastVolPanelW = -1;              // 音量行上次的面板宽度
        int lastSplitH = -1;                 // 分隔容器上次的高度 (判断"窗口在变矮")
        static readonly int[][] LatencyProfiles = new int[][] {
            new int[] { 100, 50 },
            new int[] { 50, 30 },
            new int[] { 25, 20 },
            new int[] { 15, 10 },
            new int[] { 5, 5 },
        };
        System.Windows.Forms.Timer waterTimer;
        System.Windows.Forms.Timer saveDebounce;
        System.Windows.Forms.Timer watchTimer;   // 看门狗: 2 秒一看输出线程活性
        System.Windows.Forms.Timer powerTimer;   // 唤醒后延迟重建 (端点恢复要几百毫秒)
        VolumeSlider volMaster, volGain;
        Label lblVolMaster, lblVolGain;
        Label lblDot, lblStatus;
        Button btnStart;
        Button btnRefresh;
        Button btnAddOutput;      // 运行中置灰: 加卡片 = 加设备/改声道形状, 链路级
        Button btnClearLog;
        Infrastructure.SessionLog sessionLog;
        SplitContainer splitContainer;
        TextBox txtLog;
        CheckBox chkMuteMaster, chkMuteCapture;
        CheckBox chkMinimizeToTray;
        CheckBox chkDrift;
        NotifyIcon trayIcon;
        EventWaitHandle activateEvent;
        volatile bool exiting = false; // 后台启动线程读 → 需 volatile
        // 后台启动状态 (引擎 Start 含预缓冲等待, 移出UI线程防卡顿)
        volatile bool startAbort = false;
        // 启动完成信号: 替代关闭流程里的 Sleep(20)×100 忙等 (Set = 已结束/空闲)
        readonly ManualResetEventSlim startFinished = new ManualResetEventSlim(true);
        bool trayTipShown = false;
        bool loadingSettings = true;
        // ini 写盘失败告警限流: 拖动滑条会连续触发 FlushSettings, 不限流会刷屏
        int lastSaveWarnTick;
        float pendingGain = 1.0f;
        int driftMode = 1;      // 1=自适应 0=关闭 2=固定比例
        int fixedPpm = 600;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool BringWindowToTop(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SetFocus(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct SCROLLINFO
        {
            public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetScrollInfo(IntPtr hWnd, int nBar, ref SCROLLINFO si);
        const int SB_VERT = 1;
        const int SIF_RANGE = 0x1, SIF_PAGE = 0x2, SIF_POS = 0x4;

        /// <summary>日志框现在是不是贴在最后一行 (用户没往上翻看历史)。</summary>
        bool LogAtBottom()
        {
            if (txtLog == null || !txtLog.IsHandleCreated) return true;
            var si = new SCROLLINFO();
            si.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(SCROLLINFO));
            si.fMask = SIF_RANGE | SIF_PAGE | SIF_POS;
            if (!GetScrollInfo(txtLog.Handle, SB_VERT, ref si)) return true;
            return si.nPos + si.nPage >= si.nMax;
        }

        const int SW_RESTORE = 9;

        enum AppState { Ready, Starting, Running, Stopped, Failed }
        AppState state = AppState.Ready;

        volatile bool volSyncing = false;
        bool loadingDevices = false;
        string restoreDefaultId = null;
        // 点开始分流时的系统默认输出, 启动失败时回滚 (AutoConfigure 会改默认)
        string preStartDefaultId = null;
        // 系统默认输出监听: 切走→解绑还原, 切回→自动重绑
        DefaultChangeWatcher defaultWatcher;
        string expectCaptureId = null;
        // 运行期间生效的设备ID (拔出识别用: 默认输出切走时 bindings 会解绑但引擎仍在跑, 不能用 bindings 判断)
        // runSinkIds = 本次运行实际打开的全部输出端点 (N 路)
        string runCaptureId;
        readonly List<string> runSinkIds = new List<string>();
        // 本次运行实际下发给内核的路由模型 (热更新前比对用)
        RoutingModel runModel;
        // 已警告过的断开设备 (设备状态事件可能连发, 同一设备只警告一次)
        HashSet<string> warnedLostIds = new HashSet<string>();
        // 启动前 UI 快照 (主音量绑定会回读设备真值覆盖控件, 启动失败时恢复权威值)
        // 卡片音量不走端点回读 (端点由"独占设备"的那张卡负责, 写端点时 UI 本就是权威源), 故无需快照
        int snapM;
        bool snapMm, snapMc;
        bool hasSnap = false;

        // 设备目录: 内部实例仅供展示, 不参与端点操作 (见 DeviceCatalog 注释)
        DeviceCatalog catalog;
        // 捕获源端点 + 各输出设备端点: 各自持有独立 MMDevice, 可安全释放
        ChannelBindingSet bindings;
        // 端点音量不可用的设备 (端点没有音量接口/被独占): 这些设备的卡片退回混音增益
        readonly HashSet<string> noEndpointVolumeDevices = new HashSet<string>();

        static readonly IReadOnlyList<EndpointInfo> NoDevices = new List<EndpointInfo>();
        IReadOnlyList<EndpointInfo> Devices
        {
            // catalog 在 MainForm_Load 才建, 而 LoadSettings(含输出卡片)在构造期就要读
            get { return catalog == null ? NoDevices : catalog.Endpoints; }
        }

        AudioEngine engine;
        bool running = false;

        // ---- 长期运行看门狗状态 ----
        // 输出线程停摆判定: RenderedSamples 连续 10 秒不涨 → WasapiOut 已不再拉数据。
        // N 路: 下标 = 引擎 sink 下标, 路数变了按需扩容 (旧引擎事件不会写这里, 见 ReferenceEquals 校验)。
        const int WatchdogFreezeMs = 10000;
        long[] watchRendered = new long[0];
        int[] watchRenderedTick = new int[0];
        // 水位失衡判定: 一侧调节水位持续 >300ms 且校正触顶 → 设备消费速率严重低于捕获速率
        // (独占模式设备采样率失配、驱动异常)。RenderedSamples 还在涨 → 停摆检测抓不到。
        const int WatchdogImbalanceMs = 10000;
        const int ImbalanceThresholdMs = 300;   // 正常水位 ≤142ms (target 100 + 波动 42), 300 远超
        int[] watchImbalanceTick = new int[0];
        bool captureIdleLogged;      // "捕获源无数据"只提示一次
        int captureIdleSince;
        int lastCaptureCheckTick;    // 空闲期确认捕获设备是否还在 (1 分钟一次)
        int runSinceTick;            // 本次运行起点, 连续健康 5 分钟后清零重建计数
        // 自动重建: 设备真的没了时不能无限重启
        const int MaxAutoRestart = 3;
        int autoRestartCount;
        bool restarting;             // 重建进行中

        public MainForm()
        {
            Text = "Stereo Splitter - 双声道分流";
            // 窗体本身双缓冲: 缩放/拖动分隔条时背景不会先刷成白的再画控件 (整窗闪一下的来源)
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
            Size = new Size(600, 554);
            // 最小高度按布局反推: 内容区至少留到"规格行 + 一整张卡片"(约 284) +
            // 6px 分隔条 + 日志区最小 110 → 客户区需 ~400, 再加标题栏/边框。
            MinimumSize = new Size(600, 480);
            Font = new Font("Microsoft YaHei UI", 9F);

            // 滚轮去向: 卡片列表正在滚的时候, 光标扫过音量条也只滚列表、不改音量
            // (拖着列表往下翻, 音量不能莫名其妙跳 5%); 列表没得滚时滚轮才调音量。
            Application.AddMessageFilter(new VolumeWheelRouter());
            // 点到空白处 (标签/面板/卡片底色) 时把焦点交回整个窗口, 别让它赖在最后那个输入框里
            Application.AddMessageFilter(new FocusParkRouter());

            // 上下可拖动面板: 内容区 | 6px灰分隔条 | 日志区 (参照 tk.PanedWindow)
            // FixedPanel=Panel2: 窗口变矮时先收缩上面的内容区, 日志区高度保持不变
            // (以前是 Panel1 固定 → 变矮时日志直接被压扁)。
            // Panel1MinSize=0: **分隔条拖动不再设上限**, 日志区可以一路拉到顶 (想看长日志时不用受 284 限制);
            // 窗口整体变矮的情况由 KeepContentOnShrink() 兜底, 不会把内容区缩没。
            splitContainer = new NoFocusSplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(0x88, 0x88, 0x88),
                FixedPanel = FixedPanel.Panel2,
                Panel1MinSize = 0,
                Panel2MinSize = 110,
                Size = new Size(500, 500),
                TabStop = false
            };
            // 显式恢复浅色面板, 阻断 BackColor(#888) 传播 → #888 只作用于分隔条
            splitContainer.Panel1.BackColor = SystemColors.Control;
            splitContainer.Panel2.BackColor = SystemColors.Control;
            // Panel1 自己不能滚: 否则窗口一缩, 整个内容区先冒出滚动条 (还带横向),
            // 而不是卡片区内部滚动。
            splitContainer.Panel1.AutoScroll = false;
            // 初始340时日志区=111(客户457); 窗高减10 → 330 保持日志111不变
            // 2026-06 增益独立成行后内容下移34 → 386 保持日志区高度不变
            splitContainer.SplitterDistance = 386;
            // 窗口整体变矮时把内容区拽回来 (分隔条拖动本身不受限, 见 KeepContentOnShrink)
            splitContainer.SizeChanged += (s, e) => KeepContentOnShrink();
            Controls.Add(splitContainer);

            var lblD = new Label { Text = "捕获(虚拟):", Location = new Point(15, 15), AutoSize = true };
            cmbDefault = new NoWheelComboBox { Location = new Point(110, 12), Width = 250, DropDownWidth = 375, DropDownStyle = ComboBoxStyle.DropDownList };
            chkMuteCapture = new NoCueCheckBox { Text = "消音", Location = new Point(363, 14), AutoSize = true };
            btnRefresh = new NoCueButton { Text = "刷新设备", Location = new Point(417, 11), Width = 72, Height = 23 };

            // 增益独占一行 (主音量上方)。自绘滑条轨道垂直居中, 标签 Top = 滑条.Top 时
            // 文字中心落在轨道线上。滑条宽度不写死, 由 LayoutVolumeRows() 按面板宽度自适应
            // (和卡片里的音量条一样, 窗口拉宽就跟着变长)。
            var lblG = new Label { Text = "增益:", Location = new Point(15, 72), AutoSize = true };
            volGain = new VolumeSlider { Location = new Point(78, 72), Width = 300, Minimum = 0, Maximum = 300, Value = 100, SmallChange = 5, LargeChange = 5 };
            lblVolGain = new Label { Text = "100%", AutoSize = false, Width = 46, Height = 20, TextAlign = ContentAlignment.MiddleLeft };

            // 主音量独占一行 (增益下方)。SmallChange/LargeChange=5: 点击/方向键/滚轮每次 5%, 与输出卡片一致
            var lblVM = new Label { Text = "主音量:", Location = new Point(15, 106), AutoSize = true };
            volMaster = new VolumeSlider { Location = new Point(78, 106), Width = 300, Minimum = 0, Maximum = 100, Value = 100, SmallChange = 5, LargeChange = 5 };
            lblVolMaster = new Label { Text = "100%", AutoSize = false, Width = 46, Height = 20, TextAlign = ContentAlignment.MiddleLeft };
            chkMuteMaster = new NoCueCheckBox { Text = "静音", AutoSize = false, Width = 56, Height = 20 };

            // 开始分流
            btnStart = new NoCueButton { Text = "开始分流", Location = new Point(15, 142), Width = 130, Height = 35 };
            lblDot = new Label { Text = "●", Location = new Point(162, 147), AutoSize = true, ForeColor = Color.Gray, Font = new Font("Microsoft YaHei UI", 10F) };
            lblStatus = new Label { Text = "状态: 就绪", Location = new Point(182, 152), AutoSize = true, ForeColor = Color.Gray };

            // 规格行: 规格下拉 + 右侧各声道名称(带输出路数角标) + 最右「添加输出」
            var lblSpec = new Label { Text = "规格:", Location = new Point(15, 189), AutoSize = true };
            cmbSpec = new NoWheelComboBox { Location = new Point(62, 185), Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbSpec.Items.AddRange(new object[] { "单声道", "立体声", "2.1", "四声道", "5.1" });
            cmbSpec.SelectedIndex = 1;
            flwSpecChannels = new FlowLayoutPanel
            {
                Location = new Point(166, 182),
                Size = new Size(310, 30),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            btnAddOutput = new NoCueButton
            {
                Text = "添加输出",
                Location = new Point(482, 184),
                Width = 92,
                Height = 26,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            // 输出卡片区 (可增删, 内容超出时只有纵向滚动)
            // 宽度顶到面板右缘 (与「添加输出」右边缘对齐), 竖滚动条才会贴着右边显示。
            // 布局: 从左到右铺卡片, 铺满换行 —— 列数按「卡片基准宽度」的整数倍跳档
            // (见 ComputeCardGrid), 所以窗口变宽是"多一列", 而不是把单张卡片拉成一根长条。
            cardHost = new BufferedFlowPanel
            {
                Location = new Point(15, 216),
                Size = new Size(559, 166),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = true,
                Padding = new Padding(0, 4, 0, 6),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            // 该下拉决定"预缓冲深度 + WASAPI 输出缓冲"整套组合, 直接影响稳态延迟, 不是启动耗时。
            // 选项里的总预估 = 捕获(约10ms) + 内部缓冲目标(driftTarget) + 输出缓冲, 不含 USB/DAC/声波;
            // 数字由 LatencyProfiles 现算, 改档位参数时文案自动跟随。
            var lblLatency = new Label { Text = "延迟档位:", Location = new Point(15, 45), AutoSize = true };
            cmbLatency = new NoWheelComboBox { Location = new Point(85, 42), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            string[] latNames = { "标准", "低延迟", "极低", "超低", "极限" };
            string[] latWarn = { "", "", ", 易断音", ", 易断音", ", 频繁断音" };
            for (int i = 0; i < LatencyProfiles.Length; i++)
                cmbLatency.Items.Add(latNames[i] + " (总预估" +
                    AudioEngine.EstimatedTotalMs(LatencyProfiles[i][0], LatencyProfiles[i][1]) + "ms" + latWarn[i] + ")");
            cmbLatency.SelectedIndex = 0;

            chkMinimizeToTray = new NoCueCheckBox { Text = "关闭时最小化到托盘", Location = new Point(294, 44), AutoSize = true };
            chkDrift = new NoCueCheckBox { Text = "校正", AutoSize = true, Checked = true };
            chkDrift.Location = new Point(chkMinimizeToTray.Right + 8, chkMinimizeToTray.Top);

            // 卡片宽度跟随容器可视宽度。AutoScroll 的竖滚动条出现/消失只改
            // ClientSize 不改 Size (不触发 Resize), 所以两个事件都要挂:
            // 否则卡片的宽度会取决于创建那一刻滚动条在不在 (差 17px 对不齐)。
            cardHost.Resize += (s2, e2) => FitCardWidths();
            cardHost.ClientSizeChanged += (s2, e2) => FitCardWidths();
            btnAddOutput.Click += (s2, e2) => AddLaneCard(null);
            cmbSpec.SelectedIndexChanged += (s2, e2) =>
            {
                if (loadingSettings) return;
                ReassignLanesToSpec(true);
                RebuildCards();
                RefreshSpecBadges();
                SaveSettings();
                Log("[规格] " + SpecText() + " → " + SpecChannelNames());
            };

            // 水位监控组: 日志面板标题行右侧, 清除日志按钮左边 (间隔0=关闭监控)
            // 整组靠右: 右缘与「清除日志」按钮一致 (按钮右边 10px 距面板边), 右侧不留空条。
            var lblWater = new Label { Text = "水位监控:", Location = new Point(293, 8), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var lblWaterIv = new Label { Text = "间隔", Location = new Point(359, 8), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            // 间隔 0 = 关闭监控; 1 秒起, 不再卡 5 秒下限 (想看 1~4 秒的抖动就让人看)
            nudWaterInterval = new NoWheelNumeric { Location = new Point(397, 5), Width = 60, Minimum = 0, Maximum = 3600, Value = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var lblWaterSec = new Label { Text = "秒", Location = new Point(461, 8), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };

            var lblLogTitle = new Label { Text = "日志", Location = new Point(15, 8), AutoSize = true, Font = new Font("Microsoft YaHei UI", 10F) };
            btnClearLog = new NoCueButton
            {
                Text = "清除日志",
                Location = new Point(489, 4),
                Width = 85,
                Height = 22,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            txtLog = new TextBox
            {
                Location = new Point(15, 30),
                // 尺寸由 LayoutLogPanel() 按面板实际大小算: 右缘距面板边 10px (竖滚动条贴边),
                // 下缘留 8px。以前用固定 Size + Anchor, 初始面板高度和设计值不同, 底部被裁掉一截。
                Size = new Size(559, 90),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.Fixed3D,
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Consolas", 9F)
            };
            var cmsLog = new ContextMenuStrip();
            cmsLog.Items.Add("复制", null, (s, e) => txtLog.Copy());
            cmsLog.Items.Add(new ToolStripSeparator());
            cmsLog.Items.Add("全选", null, (s, e) => txtLog.SelectAll());
            txtLog.ContextMenuStrip = cmsLog;

            splitContainer.Panel1.Controls.AddRange(new Control[] { lblD, cmbDefault, chkMuteCapture, btnRefresh, lblLatency, cmbLatency, chkMinimizeToTray, chkDrift, lblVM, volMaster, lblVolMaster, chkMuteMaster, lblG, volGain, lblVolGain, lblDot, lblStatus, btnStart, lblSpec, cmbSpec, flwSpecChannels, btnAddOutput, cardHost });
            splitContainer.Panel2.Controls.AddRange(new Control[] { lblLogTitle, lblWater, lblWaterIv, nudWaterInterval, lblWaterSec, btnClearLog, txtLog });

            // 提示文案统一说明"运行中能不能改", 置灰时不至于让人以为坏了。
            // 必须放在所有控件都建好之后（水位监控那几个是后面才 new 的）。
            var tipTop = new ToolTip();
            tipTop.SetToolTip(cmbDefault, "把哪台虚拟声卡的输出抓过来。\n运行中不可改（要重开捕获端点），需先停止分流。");
            tipTop.SetToolTip(btnRefresh, "重新枚举输出设备（插拔 USB/DAC 后用）。\n运行中不可刷新（会改动卡片与链路），需先停止分流。");
            tipTop.SetToolTip(cmbLatency, "预缓冲深度 + 输出缓冲的整套预设，决定稳态延迟与抗断音能力。\n运行中不可改（缓冲在启动时按档位建好），需先停止分流。");
            tipTop.SetToolTip(cmbSpec, "可用的逻辑声道清单（单声道…5.1）。\n运行中不可改（会改变矩阵与每路声道数），需先停止分流。");
            tipTop.SetToolTip(btnAddOutput, "新增一张输出卡片（一台设备）。\n运行中不可加卡（会改变链路），需先停止分流。");
            tipTop.SetToolTip(volGain, "总增益，作用在采样层。\n运行中可调。");
            tipTop.SetToolTip(volMaster, "总音量，作用在采样层。\n运行中可调。");
            tipTop.SetToolTip(chkMuteMaster, "总静音。\n运行中可调。");
            tipTop.SetToolTip(chkMuteCapture, "锁定捕获端静音（外部取消静音会被恢复）。\n运行中可调。");
            tipTop.SetToolTip(chkDrift, "漂移闭环校正。\n运行中可切（立即生效）。");
            tipTop.SetToolTip(chkMinimizeToTray, "关闭窗口时最小化到托盘。\n与本机音频无关，随时可改。");
            tipTop.SetToolTip(nudWaterInterval, "水位监控的刷新间隔（秒，1~3600；0 = 关闭监控）。\n随时可改，回车提交。");
            // 增益/主音量两行的滑条宽度跟面板宽度走 (窗口拉宽自动变长)
            splitContainer.Panel1.ClientSizeChanged += (s, e) => LayoutVolumeRows();
            LayoutVolumeRows();
            // 日志框按面板实时尺寸摆放 (拖分隔条/改窗口都要跟), 竖滚动条才会一直贴着右边
            splitContainer.Panel2.Resize += (s, e) => LayoutLogPanel();
            LayoutLogPanel();

            btnStart.Click += BtnStart_Click;
            btnRefresh.Click += (s, e) => RefreshDevicesClick();
            btnClearLog.Click += (s, e) =>
            {
                txtLog.Clear();
                if (sessionLog != null) sessionLog.Clear();
            };
            chkMinimizeToTray.CheckedChanged += (s, e) => SaveSettings();
            chkDrift.CheckedChanged += (s, e) =>
            {
                driftMode = chkDrift.Checked ? 1 : 0;
                if (loadingSettings) return;
                SaveSettings();
                if (running && engine != null)
                {
                    try
                    {
                        engine.SetDriftMode(driftMode, fixedPpm);
                        Log("[设置] 时钟校正: " + (driftMode == 1 ? "开" : "关"));
                    }
                    catch (Exception ex) { Log("[错误] 切换时钟校正失败: " + ex.Message); }
                }
            };
            cmbLatency.SelectedIndexChanged += (s, e) => { if (!loadingSettings) SaveSettings(); };
            nudWaterInterval.ValueChanged += (s, e) =>
            {
                // 间隔只要 > 0 就照用: 1~4 秒也允许 (以前强制抬到 5 秒, 想看短间隔反而改不动)
                SyncWaterTimer();
                if (!loadingSettings) SaveSettings();
            };
            cmbDefault.SelectedIndexChanged += (s, e) => { if (!loadingDevices) SaveSettings(); };
            volMaster.Scroll += (s, e) =>
            {
                lblVolMaster.Text = volMaster.Value + "%";
                ApplyVolumes();
                // 音量到0自动勾选主静音; 拖离0取消 (捕获消音锁定不受拖动影响)
                if (chkMuteMaster.Checked != (volMaster.Value == 0)) chkMuteMaster.Checked = volMaster.Value == 0;
                // 双向同步: 写入当前默认输出设备的端点音量 (不改捕获消音状态)
                if (!volSyncing && bindings != null && bindings.Master != null)
                    bindings.Master.WriteVolume(volMaster.Value / 100f);
                SaveSettings();
            };
            chkMuteMaster.CheckedChanged += (s, e) =>
            {
                // 主静音仅采样层(左右分流), 不写捕获设备端点 Mute
                ApplyVolumes();
                SaveSettings();
            };
            chkMuteCapture.CheckedChanged += (s, e) =>
            {
                // 捕获消音仅端点 Mute(直出), 不碰采样层与主音量绑定
                if (!volSyncing && bindings != null && bindings.Master != null)
                    bindings.Master.WriteMute(chkMuteCapture.Checked);
                SaveSettings();
            };
            volGain.Scroll += (s, e) =>
            {
                lblVolGain.Text = volGain.Value + "%";
                pendingGain = volGain.Value / 100f;
                if (engine != null) engine.SetGain(pendingGain);
                SaveSettings();
            };

            settings = new Infrastructure.SettingsStore(IniPath);
            LoadSettings();
            InitTray();

            // 会话日志文件: 每会话一个 session 文件, UI + 文件双写
            try { sessionLog = new Infrastructure.SessionLog(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log")); }
            catch { sessionLog = null; }

            waterTimer = new System.Windows.Forms.Timer
            {
                Interval = Math.Max(1, (int)nudWaterInterval.Value) * 1000
            };
            waterTimer.Tick += (s, e) =>
            {
                if (running && engine != null && (int)nudWaterInterval.Value > 0) Log("[状态] 缓冲水位 " + engine.BufferStatus());
            };
            // 初始不启动: Ready 状态下不跑, 由 SetAppState 按状态启停

            // ini 写盘 debounce (SaveSettings 同步字典后排队, 300ms 无新变更才落盘)
            saveDebounce = new System.Windows.Forms.Timer { Interval = 300 };
            saveDebounce.Tick += (s, e) =>
            {
                saveDebounce.Stop();
                FlushSettings();
            };

            // 看门狗: 只在运行时判定 (引擎为空/未运行直接返回, 不空转)
            watchTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            watchTimer.Tick += (s, e) => WatchdogTick();

            // 休眠唤醒: 等端点恢复再重建, 太早重建大概率失败
            powerTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            powerTimer.Tick += (s, e) =>
            {
                powerTimer.Stop();
                if (!running || exiting) return;
                Log("[恢复] 系统已从休眠唤醒 → 重建音频流");
                RestartEngine();
            };

            Load += MainForm_Load;
            FormClosing += MainForm_FormClosing;
        }

        void SetAppState(AppState s)
        {
            state = s;

            string txt;
            Color c;
            switch (s)
            {
                case AppState.Starting:
                    c = Color.Orange; txt = "启动中..."; break;
                case AppState.Running:
                    c = Color.LimeGreen; txt = "运行中"; break;
                case AppState.Stopped:
                    c = Color.Gray; txt = "已停止"; break;
                case AppState.Failed:
                    c = Color.Red; txt = "启动失败(见日志)"; break;
                default:
                    c = Color.Gray; txt = "就绪"; break;
            }
            lblDot.ForeColor = c;
            lblStatus.ForeColor = c;
            lblStatus.Text = "状态: " + txt;

            running = (s == AppState.Running);
            btnStart.Text = running ? "停止分流" : "开始分流";
            ApplyUiLock();

            // 数值标签: 仅分流运行中(双向绑定生效)为黑, 其余灰色表示无效
            SetVolumeBindVisual(running);

            if (trayIcon != null) trayIcon.Text = "Stereo Splitter - " + txt;

            // 水位监控仅运行时按间隔跑, Ready/Stopped 或间隔0=不跑 (防空转)
            SyncWaterTimer();
        }

        /// <summary>
        /// 按当前状态统一切换控件的可用性。分三档，与内核能力严格对齐
        /// （<see cref="AudioEngine.SetRouting"/> 只能换矩阵，声道数/设备/延迟都换不了）：
        /// <list type="bullet">
        /// <item>链路级：捕获设备 / 刷新设备 / 延迟档位 / 规格 / 加卡 / 卡片设备 / 卡片延迟 / 移除卡片
        ///       —— 运行中置灰</item>
        /// <item>矩阵级：卡片声道 / 多声道行声道 —— 运行中可改（热替换矩阵）</item>
        /// <item>形态级：单 ↔ 多声道 —— 会改设备物理声道数，运行中把「多声道…」画灰并拦下</item>
        /// <item>采样级 / 监控级：音量、静音、消音、校正、水位间隔、最小化 —— 始终可动</item>
        /// </list>
        /// 卡片是动态建的，所以这里既在状态切换时调用，也在重建卡片后调用。
        /// </summary>
        void ApplyUiLock()
        {
            bool busy = (state == AppState.Starting);
            bool run = (state == AppState.Running);

            cmbDefault.Enabled = !busy && !run;
            btnRefresh.Enabled = !busy && !run;
            cmbLatency.Enabled = !busy && !run;
            cmbSpec.Enabled = !busy && !run;
            btnAddOutput.Enabled = !busy && !run;

            volMaster.Enabled = !busy;
            volGain.Enabled = !busy;
            chkMuteMaster.Enabled = !busy;
            chkMuteCapture.Enabled = !busy;
            chkMinimizeToTray.Enabled = !busy;
            chkDrift.Enabled = !busy;
            nudWaterInterval.Enabled = !busy;
            btnStart.Enabled = !busy;

            for (int ci = 0; ci < cards.Count; ci++)
            {
                cards[ci].SetStructuralLock(busy || run);   // 设备 / 延迟 / 移除
                cards[ci].SetChannelLock(busy);             // 声道 / 行声道
                cards[ci].SetShapeLock(busy || run);        // 单 ↔ 多声道形态
                cards[ci].SetVolumeLock(busy);              // 主音量(端点) / 静音 / 行音量
            }
        }

        // 水位 timer 启停与间隔统一入口 (SetAppState / 间隔变更时调用)
        void SyncWaterTimer()
        {
            if (waterTimer == null) return;
            if (running && (int)nudWaterInterval.Value > 0)
            {
                waterTimer.Interval = Math.Max(1, (int)nudWaterInterval.Value) * 1000;
                waterTimer.Start();
            }
            else
            {
                waterTimer.Stop();
            }
            // 看门狗独立于水位日志间隔: 只要在跑就 2 秒一看 (间隔设 0 也不能停看门狗)
            if (watchTimer != null)
            {
                if (running) watchTimer.Start();
                else watchTimer.Stop();
            }
        }

        void MainForm_Load(object sender, EventArgs e)
        {
            try
            {
                catalog = new DeviceCatalog();
                bindings = new ChannelBindingSet(catalog, Log);
                // 监听系统默认输出变化: 绑定期间被切走→解绑还原, 切回→自动重绑; 设备断开→日志警告
                defaultWatcher = new DefaultChangeWatcher(OnSystemDefaultChanged, OnDeviceLost);
                try { catalog.Enumerator.RegisterEndpointNotificationCallback(defaultWatcher); }
                catch (Exception ex) { Log("[警告] 注册默认输出监听失败: " + ex.Message); }
                RefreshDevices();
                RecordRestoreTarget();
                ApplyIniVolumeOverrides();
                // 高DPI下 AutoSize 宽度在构造后才最终确定，Load 时再贴到托盘选项右侧
                chkDrift.Location = new Point(chkMinimizeToTray.Right + 8, chkMinimizeToTray.Top);
                // 校准日志框高度, 此后由 Anchor 自动跟随分隔条拖动
                txtLog.Height = splitContainer.Panel2.Height - txtLog.Top - 15;
                SetAppState(AppState.Ready);
                RegisterActivateHandler();
                // 休眠唤醒后 WASAPI 端点已失效且不会自己回来 → 主动重建 (SystemEvents 需消息泵, 放 Load 里注册)
                Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
                Log("[初始化] 就绪。在「捕获(虚拟)」选择虚拟设备(如 CABLE Input)，点「开始分流」时自动完成配置。");
                Log("[初始化] 提示: 捕获输出不能与左右目标相同，否则会回环。");
            }
            catch (Exception ex)
            {
                SetAppState(AppState.Failed);
                Log("[错误] 初始化失败: " + ex.Message);
            }
            finally
            {
                loadingSettings = false;
            }
        }

        // 系统默认输出变化 (COM回调线程) → 回UI线程处理
        void OnSystemDefaultChanged(DataFlow flow, Role role, string newId)
        {
            if (flow != DataFlow.Render) return; // 只关心输出设备
            // 通信角色(eCommunications)与音乐/视频播放无关, 且语音软件/系统策略会自动改写它。
            // 不过滤的话会被误判成"默认输出被切走" → 解绑音量 → 一两秒后又重绑:
            // 实测 21:50/21:51/21:53 各抖动一次, 每次捕获源短暂无数据 → 缓冲抽空到 0ms → 断音。
            if (role == Role.Communications) return;
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(delegate { HandleDefaultChanged(newId, role); })); }
            catch { }
        }

        // 运行设备断开/停用 (COM回调线程) → 回UI线程记警告 (仅警告, 不停止分流)
        void OnDeviceLost(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(delegate { HandleDeviceLost(id); })); }
            catch { }
        }

        void HandleDeviceLost(string id)
        {
            if (state != AppState.Starting && state != AppState.Running) return;
            if (id != runCaptureId && !runSinkIds.Contains(id)) return;
            if (!warnedLostIds.Add(id)) return;
            string role = id == runCaptureId ? "捕获" : "输出设备";
            Log("[警告] " + role + "已断开: " + FindNameById(id) +
                " → 看门狗会在检测到断流后尝试自动重建；若设备已彻底移除则自动停止分流");
        }

        void HandleDefaultChanged(string newId, Role role)
        {
            if (newId == null || bindings == null) return;

            if (bindings.Master == null)
            {
                // 已解绑状态: 分流仍在跑 + 切回期望的捕获设备 → 自动重绑
                if ((state == AppState.Starting || state == AppState.Running)
                    && expectCaptureId != null && newId == expectCaptureId)
                    RebindAfterDefaultRestored();
                return; // 切到第三方设备 → 保持解绑, 不动作
            }

            if (newId == bindings.Master.DeviceId) return; // 我们自己切的(启动配置/退出恢复) → 忽略

            // 外部切走 → 先停双向绑定 + 还原原音量, 分流不停
            // 日志必须写清"切到哪个设备/哪个角色": 只有这样才能判断是谁在改默认设备
            Log("[恢复] 检测到默认输出被切换 → " + FindNameById(newId) + "（" + RoleName(role) + "）；" +
                "已解除双向音量绑定(含捕获消音锁定)并还原原音量，应用声音不再进入捕获源，输出将无声，切回原设备可自动恢复");
            expectCaptureId = bindings.Master.DeviceId; // 记住期望捕获设备, 供"切回"识别
            UnbindAllVolumeSync();        // 解绑通知 + 还原三端点原音量 + 还原已改静音
            SetVolumeBindVisual(false);   // 数值标签变灰(绑定已失效)
        }

        static string RoleName(Role role)
        {
            switch (role)
            {
                case Role.Console: return "控制台角色";
                case Role.Multimedia: return "多媒体角色";
                default: return "通信角色";
            }
        }

        // 默认输出切回捕获设备 → 自动重绑双向音量 (引擎未停, UI仍为权威源)
        void RebindAfterDefaultRestored()
        {
            try
            {
                Log("[恢复] 默认输出已切回捕获设备 → 自动重新绑定音量");

                // 捕获主音量 UI 当前值 (Bind 回读会用设备值覆盖)
                int uiM = volMaster.Value;
                bool uiMm = chkMuteMaster.Checked, uiMc = chkMuteCapture.Checked;

                BindDefaultVolumeSync("[恢复]");
                bool ok = BindEndpointVolumeSync("[恢复]");
                RestoreUIValues(uiM, uiMm, uiMc); // UI 覆盖回来
                SetVolumeBindVisual(bindings.Master != null && ok);
                WriteUIValuesToDevice(); // 恢复后的 UI 值写回设备
                Log("[恢复] 重绑完成: 主=" + DevInfo(bindings.Master) + SinkSummary());
            }
            catch (Exception ex)
            {
                Log("[错误] 自动重绑失败: " + ex.Message);
                SetVolumeBindVisual(false);
            }
        }

        // Bind 回读会用设备值覆盖控件 → 恢复 UI 权威值 (volSyncing 包裹防写端点)
        void RestoreUIValues(int uiM, bool uiMm, bool uiMc)
        {
            volSyncing = true;
            volMaster.Value = uiM;
            lblVolMaster.Text = uiM + "%";
            chkMuteMaster.Checked = uiMm;
            chkMuteCapture.Checked = uiMc;
            volSyncing = false;
        }

        void RecordRestoreTarget()
        {
            // 记录进入时的默认输出，退出时恢复
            try
            {
                string defId = catalog.DefaultRenderId();
                string defName = catalog.DefaultRenderName();
                restoreDefaultId = defId;

                if (DeviceCatalog.IsVirtualName(defName))
                {
                    // 默认是虚拟设备(如安装器切成 CABLE): 选真实扬声器作恢复目标
                    foreach (var d in Devices)
                    {
                        if (!d.IsVirtual)
                        {
                            restoreDefaultId = d.ID;
                            break;
                        }
                    }
                    Log("[初始化] 当前默认输出是虚拟设备，退出时将恢复到: " + FindNameById(restoreDefaultId));
                }
            }
            catch (Exception ex)
            {
                Log("[错误] 记录默认输出失败: " + ex.Message);
            }
        }

        void BindDefaultVolumeSync(string stage)
        {
            try
            {
                // 主音量绑定「默认输出」下拉选中项, 不是系统当前默认
                if (cmbDefault.SelectedIndex < 0 || cmbDefault.SelectedIndex >= Devices.Count) return;
                var def = Devices[cmbDefault.SelectedIndex];

                var b = bindings.BindMaster(def.ID, SyncVol_OnVolumeNotification);
                if (b == null)
                {
                    Log("[错误] " + stage + " 主音量绑定失败: 无法打开端点 " + def.FriendlyName + " (建议刷新设备)");
                    return;
                }
                b.Invalidated -= OnBindingInvalidated;
                b.Invalidated += OnBindingInvalidated;

                // 勾选跟人走: 换绑后的新设备立即消音 (旧设备的静音已在 BindMaster 内按 applied 恢复)
                if (chkMuteCapture.Checked && !b.ReadMuted()) b.WriteMute(true);

                volSyncing = true;
                int v = (int)Math.Round(b.ReadVolume() * 100);
                volMaster.Value = Math.Max(0, Math.Min(100, v));
                lblVolMaster.Text = volMaster.Value + "%";
                bool muted = b.ReadMuted();
                volSyncing = false;

                Log(stage + " 主音量绑定 → [" + FindIdxById(def.ID) + "] " + def.FriendlyName +
                    " (设备值 " + v + "%" + (muted ? ", 消音" : "") + "，启动时按 UI 值写入)");
            }
            catch (Exception ex)
            {
                volSyncing = false;
                Log("[错误] " + stage + " 主音量同步失败: " + AudioErrors.Describe(ex));
            }
        }

        int FindIdxById(string id)
        {
            return catalog.IndexOf(id);
        }

        string DevInfo(EndpointVolumeBinding b)
        {
            if (b == null || !b.IsValid) return "(未绑定)";
            return "[" + FindIdxById(b.DeviceId) + "] " + b.DeviceName;
        }

        string FindNameById(string id)
        {
            return catalog.NameOf(id);
        }

        // 端点绑定失效 (设备拔出/端点被拆) → 标记 UI 失效并提示, 不停止分流 (与原"仅警告"策略一致)
        void OnBindingInvalidated(EndpointVolumeBinding b)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    SetVolumeBindVisual(false);
                    Log("[警告] 「" + b.Role + "」端点绑定已失效 (" + b.DeviceName + ")，该路数字标签已置灰，请停止分流并刷新设备");
                }));
            }
            catch { }
        }

        void SyncVol_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            // Windows 侧音量变化 → 更新 UI (双向同步)
            try
            {
                BeginInvoke(new Action(() =>
                {
                    var b = bindings == null ? null : bindings.Master;
                    if (volSyncing || b == null || !b.IsValid) return;
                    volSyncing = true;
                    try
                    {
                        int v = Math.Max(0, Math.Min(100, (int)Math.Round(data.MasterVolume * 100)));
                        bool valueChanged = (volMaster.Value != v);
                        if (valueChanged)
                        {
                            volMaster.Value = v;
                            lblVolMaster.Text = v + "%";
                            ApplyVolumes();
                            SaveSettings();
                        }
                        // 端点静音基线: 与 data.Muted 不等 = 外部(系统声音设置)对捕获设备点静音/解除
                        bool muteExternal = (data.Muted != b.LastMuteWeWrote);
                        b.LastMuteWeWrote = data.Muted;
                        // 双向跟随仅运行中; 捕获消音锁开启时 mute 位归锁管, 主静音不跟
                        bool follow = running && !chkMuteCapture.Checked;
                        if (valueChanged || (follow && muteExternal))
                        {
                            // 不变式: 值0=静音; 外部静音→主静音跟勾, 外部解除→跟取消 (音量0时保持勾选)
                            bool want = (v == 0) || (follow && data.Muted);
                            if (chkMuteMaster.Checked != want) chkMuteMaster.Checked = want;
                        }
                        if (chkMuteCapture.Checked && !data.Muted)
                        {
                            // 消音锁定: 任何外部(系统音量条/快捷键/驱动)取消静音 → 静默恢复, 直到用户取消勾选
                            b.WriteMute(true);
                        }
                    }
                    finally { volSyncing = false; }
                }));
            }
            catch { }
        }

        /// <summary>
        /// 绑定各输出设备的端点音量 (音量/静音双向同步 + 停止时还原)。
        /// <para>一张卡片 = 一台设备 → 卡片主音量就是该设备的端点音量;
        /// 多声道卡片的各行音量是混音增益, 不碰端点 (端点音量是设备级单值, 分不了声道)。</para>
        /// </summary>
        bool BindEndpointVolumeSync(string stage)
        {
            SyncLanesFromCards();
            noEndpointVolumeDevices.Clear();

            // 设备集合可能已变 (换设备/删卡) → 先按旧基线还原并释放, 再按新集合重绑;
            // 同一设备仍在 → BindSink 复用原对象, 还原基线不会被冲掉
            bindings.UnbindSinks(true);

            for (int i = 0; i < models.Count; i++)
            {
                OutputCardModel m = models[i];
                if (string.IsNullOrEmpty(m.DeviceId)) continue;
                string role = CardRole(m);
                var b = bindings.BindSink(m.DeviceId, role, SinkNotify(m.DeviceId));
                if (b == null)
                {
                    // 端点打不开(端点无音量接口/被独占)不该拖垮整个启动: 这张卡片退回混音增益,
                    // 音量照常可调, 只是不写设备端点、停止时也无需还原
                    noEndpointVolumeDevices.Add(m.DeviceId);
                    Log("[警告] " + stage + " " + role + " 端点音量不可用: " + FindNameById(m.DeviceId) +
                        " (该卡片音量改在混音层生效，不写设备端点)");
                    continue;
                }
                b.Invalidated -= OnBindingInvalidated;
                b.Invalidated += OnBindingInvalidated;

                int v = (int)Math.Round(b.ReadVolume() * 100);
                Log(stage + " " + role + " 音量绑定 → [" + FindIdxById(m.DeviceId) + "] " + b.DeviceName +
                    " (设备值 " + v + "%" + (b.ReadMuted() ? ", 静音" : "") + "，启动时按 UI 值写入，停止时还原回此值)");
            }

            // 端点可用性可能刚变化 (有的绑不上) → 重新扁平化: 那些设备的音量改走矩阵增益
            RebuildLanes();
            return true;
        }

        /// <summary>给某设备端点挂通知: 设备音量变化 → 回填该设备那张卡片的滑条。</summary>
        Action<AudioVolumeNotificationData> SinkNotify(string deviceId)
        {
            return delegate(AudioVolumeNotificationData data)
            {
                try { BeginInvoke(new Action(delegate { HandleSinkVolumeNotification(deviceId, data); })); }
                catch { }
            };
        }

        // 设备值 → 卡片 UI (仅 UI, volSyncing 内不写端点)
        void HandleSinkVolumeNotification(string deviceId, AudioVolumeNotificationData data)
        {
            if (volSyncing || bindings == null) return;
            var b = bindings.SinkOf(deviceId);
            if (b == null || !b.IsValid) return;
            OutputCardControl card = CardOfDevice(deviceId);
            if (card == null) return;

            volSyncing = true;
            try
            {
                int v = Math.Max(0, Math.Min(100, (int)Math.Round(data.MasterVolume * 100)));
                if (card.volTrack.Value != v)
                {
                    card.volTrack.Value = v;
                    card.lblVol.Text = v + "%";
                }
                // 与主音量同构: 音量0=静音不变式 + 外部静音跟随 (仅 UI, volSyncing 内不写端点)
                bool want = (v == 0) || data.Muted;
                if (card.chkMute.Checked != want) card.chkMute.Checked = want;
                SyncLanesFromCards();
                SaveSettings();
            }
            finally { volSyncing = false; }
        }

        /// <summary>某设备上那张卡 (一张卡片一台设备 → 唯一; 未找到 → null)。</summary>
        OutputCardControl CardOfDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return null;
            for (int i = 0; i < cards.Count && i < models.Count; i++)
                if ((models[i].DeviceId ?? "") == deviceId) return cards[i];
            return null;
        }

        /// <summary>某卡片对应的模型 (控件与 models 一一对应)。</summary>
        OutputCardModel ModelOf(OutputCardControl card)
        {
            int i = cards.IndexOf(card);
            return (i >= 0 && i < models.Count) ? models[i] : null;
        }

        /// <summary>各输出设备端点绑定的摘要 (日志用)。</summary>
        string SinkSummary()
        {
            if (bindings == null) return "";
            var parts = new List<string>();
            foreach (var b in bindings.Sinks()) parts.Add(b.Role + "=" + DevInfo(b));
            return parts.Count == 0 ? " / 输出=(未绑定)" : " / " + string.Join(" / ", parts.ToArray());
        }

        bool AutoConfigure()
        {
            if (cmbDefault.SelectedIndex < 0) return false;

            var dev = Devices[cmbDefault.SelectedIndex];
            try
            {
                // 1. 设为系统默认输出 (已经是默认就跳过: 每次重设都会让系统音频重新协商一次)
                if (catalog.DefaultRenderId() == dev.ID)
                {
                    Log("[配置] 1/4 已是系统默认输出，跳过切换 → [" + cmbDefault.SelectedIndex + "] " + dev.FriendlyName);
                }
                else
                {
                    string setErr;
                    if (!SystemDefaultPolicy.TrySetDefault(dev.ID, out setErr))
                    {
                        Log("[错误] 配置 1/4 设置系统默认输出 → [" + cmbDefault.SelectedIndex + "] " +
                            dev.FriendlyName + " 失败: " + setErr);
                        int idx1 = catalog.IndexOf(catalog.DefaultRenderId());
                        if (idx1 >= 0) cmbDefault.SelectedIndex = idx1;
                        return false;
                    }
                    Log("[配置] 1/4 设置系统默认输出 → [" + cmbDefault.SelectedIndex + "] " + dev.FriendlyName + " 成功");
                }

                int defIdx = cmbDefault.SelectedIndex;

                // 2. 重绑主音量同步到新默认设备
                BindDefaultVolumeSync("[配置] 2/4");

                // 3. 输出目标: 只在"未选/失效"时补默认。
                //    用户已选的设备一律不动 —— 静默改选会让人以为"选好的设备自己变了"。
                //    设备不够时允许复用同一台: 复用后两张卡片会合并成一张多声道卡片
                //    (内核每台端点只开一条 WasapiOut, 这是唯一合法形态)。
                if (cards.Count == 0 || models.Count == 0)
                {
                    Log("[错误] 配置 3/4 没有输出卡片。");
                    return false;
                }
                var cand = new List<int>();
                for (int i = 0; i < Devices.Count; i++) if (i != defIdx && !Devices[i].IsVirtual) cand.Add(i);
                for (int i = 0; i < Devices.Count; i++) if (i != defIdx && Devices[i].IsVirtual) cand.Add(i);
                if (cand.Count == 0)
                {
                    Log("[错误] 配置 3/4 除捕获设备外没有任何可用输出端点。");
                    return false;
                }
                var usedIdx = new HashSet<int>();
                for (int i = 0; i < cards.Count; i++)
                    if (cards[i].cmbDevice.SelectedIndex >= 0) usedIdx.Add(cards[i].cmbDevice.SelectedIndex);

                int filled = 0;
                for (int i = 0; i < cards.Count && i < models.Count; i++)
                {
                    if (cards[i].cmbDevice.SelectedIndex >= 0) continue;
                    int chosen = -1;
                    for (int k = 0; k < cand.Count; k++)
                    {
                        int c = cand[(filled + k) % cand.Count];
                        if (!usedIdx.Contains(c)) { chosen = c; break; }
                    }
                    if (chosen < 0) chosen = cand[filled % cand.Count];   // 设备不够 → 复用 (随后合并)
                    cards[i].SetDeviceIndex(chosen);
                    models[i].DeviceId = DeviceIdAt(chosen);
                    usedIdx.Add(chosen);
                    filled++;
                    Log("[配置] 3/4 输出目标: 第 " + (i + 1) + " 张卡片（" + CardRole(models[i]) + "）未选择 → 自动补选 [" +
                        chosen + "] " + Devices[chosen].FriendlyName);
                }
                // 复用同一台设备会让卡片合并, 界面要跟着重建
                if (filled > 0 && NormalizeModels()) { RebuildCards(); SyncLanesFromCards(); }
                if (filled == 0)
                    Log("[配置] 3/4 输出目标: 保持手动选择 (" + LaneSummary() + ")");

                // 4. 重绑各输出设备的端点音量 (独占设备的卡片才绑, 见 BindEndpointVolumeSync)
                if (!BindEndpointVolumeSync("[配置] 4/4"))
                {
                    Log("[错误] 配置 4/4 输出音量绑定失败，中止启动。");
                    return false;
                }

                // 回环检查 (只报错, 不改用户的选择): 指向捕获设备都会采到自己的输出
                int badLane = -1;
                for (int i = 0; i < cards.Count; i++)
                    if (cards[i].cmbDevice.SelectedIndex == defIdx) { badLane = i; break; }
                if (badLane >= 0)
                {
                    Log("[错误] 回环检查未通过: 第 " + (badLane + 1) + " 张卡片（" +
                        (badLane < models.Count ? CardRole(models[badLane]) : "?") + "）与捕获设备相同 [" + defIdx + "] " +
                        dev.FriendlyName + "，请改选输出设备 (程序不会替你改)。");
                    return false;
                }
                Log("[配置] 回环检查通过 (捕获输出与 " + cards.Count + " 张输出卡片无冲突)");

                return true;
            }
            catch (Exception ex)
            {
                Log("[错误] 自动配置失败: " + ex.Message);
                return false;
            }
        }

        // ==================== 设置持久化 (exe 同目录 SplitterGUI.ini) ====================

        string IniPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "SplitterGUI.ini"); }
        }
        Infrastructure.SettingsStore settings;

        void LoadSettings()
        {
            settings.Load();

            chkMinimizeToTray.Checked = GetInt("MinimizeToTray", 0) != 0;
            cmbLatency.SelectedIndex = Math.Max(0, Math.Min(4, GetInt("LatencyProfile", 0)));
            int wi = GetInt("WaterIntervalSec", 30);
            if (wi != 0) wi = Math.Max(1, Math.Min(3600, wi)); // 0=关闭; 其余 1~3600 秒原样用
            nudWaterInterval.Value = wi;

            int mode = Math.Max(0, Math.Min(2, GetInt("DriftMode", 1)));
            chkDrift.Checked = mode != 0;
            driftMode = mode;
            fixedPpm = Math.Max(-5000, Math.Min(5000, GetInt("FixedRatioPpm", 600)));

            int gain = Math.Max(0, Math.Min(300, GetInt("Gain", 100)));
            volGain.Value = gain;
            lblVolGain.Text = gain + "%";
            pendingGain = gain / 100f;

            // 输出卡片 (规格 + 各路输出)
            LoadLanes();
        }


        // ==================== 规格与输出卡片 ====================

        /// <summary>当前规格的声道清单 (键, 显示名)。键与 Windows 扬声器位对应。
        /// 表在 LaneRouting 里 (内核翻译与界面共用同一张, 免得两边各写一套)。</summary>
        List<KeyValuePair<string, string>> CurrentSpecChannels()
        {
            return LaneRouting.SpecChannels(cmbSpec.SelectedIndex);
        }

        string SpecText()
        {
            string[] names = { "单声道", "立体声", "2.1", "四声道", "5.1" };
            int i = cmbSpec.SelectedIndex;
            return (i >= 0 && i < names.Length) ? names[i] : "立体声";
        }

        string SpecChannelNames()
        {
            var chans = CurrentSpecChannels();
            var names = new List<string>();
            for (int i = 0; i < chans.Count; i++) names.Add(chans[i].Value);
            return string.Join("/", names.ToArray());
        }

        string ChannelNameOf(string key)
        {
            var chans = CurrentSpecChannels();
            for (int i = 0; i < chans.Count; i++) if (chans[i].Key == key) return chans[i].Value;
            return key;
        }

        /// <summary>含该声道的卡片 (多声道卡片里任意一行算)</summary>
        OutputCardControl CardWithChannel(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            for (int i = 0; i < cards.Count && i < models.Count; i++)
                for (int s = 0; s < models[i].Slots.Count; s++)
                    if (models[i].Slots[s].ChannelKey == key) return cards[i];
            return null;
        }

        /// <summary>左输出卡片: 含 FL 的卡片 (单声道规格时 M)。</summary>
        OutputCardControl LeftCard()
        {
            return CardWithChannel("FL") ?? CardWithChannel("M") ?? (cards.Count > 0 ? cards[0] : null);
        }

        /// <summary>右输出卡片: 含 FR 的另一张; 都不行则取与左不同的第一张。</summary>
        OutputCardControl RightCard()
        {
            OutputCardControl l = LeftCard();
            OutputCardControl r = CardWithChannel("FR");
            if (r != null && r != l) return r;
            for (int i = 0; i < cards.Count; i++) if (cards[i] != l) return cards[i];
            return l;
        }

        string SafeDeviceId(int idx)
        {
            return (idx >= 0 && catalog != null) ? catalog.IdAt(idx) : "";
        }

        string LeftDeviceId()
        {
            OutputCardControl c = LeftCard();
            return c == null ? "" : SafeDeviceId(c.cmbDevice.SelectedIndex);
        }

        string RightDeviceId()
        {
            OutputCardControl c = RightCard();
            return c == null ? "" : SafeDeviceId(c.cmbDevice.SelectedIndex);
        }

        string LaneSummary()
        {
            var parts = new List<string>();
            for (int i = 0; i < cards.Count && i < models.Count; i++)
            {
                string name = "未选";
                int idx = cards[i].cmbDevice.SelectedIndex;
                if (idx >= 0 && idx < Devices.Count) name = Devices[idx].FriendlyName;
                parts.Add(CardRole(models[i]) + "->" + name);
            }
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>
        /// 控件 → 卡片模型 (控件是权威源) → 内核 lane 列表。
        /// <para>lane 是派生值: 一张卡片可能产生多行 (多声道), 也可能一行都不产生 (空声道)。</para>
        /// </summary>
        void SyncLanesFromCards()
        {
            var chans = CurrentSpecChannels();
            for (int i = 0; i < cards.Count && i < models.Count; i++)
            {
                cards[i].WriteBackToModel(chans);
                models[i].DeviceId = SafeDeviceId(cards[i].SelectedDeviceIndex);
            }
            RebuildLanes();
        }

        /// <summary>卡片模型 → 内核 lane 列表 (音量已折算成矩阵增益)。</summary>
        void RebuildLanes()
        {
            lanes.Clear();
            lanes.AddRange(LaneRouting.Flatten(models, EndpointVolumeAvailable));
        }

        /// <summary>该设备的端点音量是否可用 (绑不上时音量改走混音增益)。</summary>
        bool EndpointVolumeAvailable(string deviceId)
        {
            return !string.IsNullOrEmpty(deviceId) && !noEndpointVolumeDevices.Contains(deviceId);
        }

        /// <summary>卡片的显示角色 (日志/兼容键用): 单声道 → 声道名; 多声道 → "N 声道(…)";</summary>
        string CardRole(OutputCardModel m)
        {
            if (m == null) return "?";
            if (!m.Multi)
                return m.Slots.Count > 0 ? ChannelNameOf(m.Slots[0].ChannelKey) : "未选声道";
            var names = new List<string>();
            for (int i = 0; i < m.Slots.Count; i++)
                if (!string.IsNullOrEmpty(m.Slots[i].ChannelKey)) names.Add(ChannelNameOf(m.Slots[i].ChannelKey));
            return m.Slots.Count + " 声道" + (names.Count > 0 ? "(" + string.Join("/", names.ToArray()) + ")" : "");
        }

        string DevNameFor(string id)
        {
            string n = FindNameById(id);
            return string.IsNullOrEmpty(n) ? "(未选设备)" : n;
        }

        string DescribeCards()
        {
            var parts = new List<string>();
            for (int i = 0; i < models.Count; i++)
                parts.Add(CardRole(models[i]) + "→" + DevNameFor(models[i].DeviceId));
            return string.Join(" ", parts.ToArray());
        }

        // ==================== 合并与行数 ====================

        /// <summary>
        /// 模型规范化: 同设备的多张卡片合并成一张多声道卡片 + 校正多声道卡片的行数。
        /// <returns>true = 卡片集合/形态变了, 调用方需要重建界面。</returns>
        /// </summary>
        bool NormalizeModels()
        {
            if (normalizing) return false;
            normalizing = true;
            try
            {
                bool changed = false;

                // 1. 行数: 多声道卡片 = 该设备物理声道数, 单声道卡片 = 1 行
                for (int i = 0; i < models.Count; i++)
                    if (EnsureSlots(models[i])) changed = true;

                // 2. 合并同设备。合并前先记下哪几台设备是重复的 —— 只有它们的主音量需要重新取初值
                var dup = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < models.Count; i++)
                {
                    string d = models[i].DeviceId ?? "";
                    if (d.Length > 0 && !seen.Add(d) && !dup.Contains(d)) dup.Add(d);
                }

                int merged = LaneRouting.MergeSameDevice(models);
                if (merged > 0)
                {
                    changed = true;
                    for (int i = 0; i < models.Count; i++)
                    {
                        if (!models[i].Multi || !dup.Contains(models[i].DeviceId ?? "")) continue;
                        // 合并后主音量接管设备端点 → 取端点当前值, 保证"合并不改变响度"
                        int before = models[i].Volume;
                        models[i].Volume = ReadEndpointPercent(models[i].DeviceId, before);
                        models[i].Mute = false;
                        EnsureSlots(models[i]);
                        Log("[输出] " + DevNameFor(models[i].DeviceId) + " 的主音量取端点当前值 " +
                            models[i].Volume + "%（合并前 " + before + "%，设备端点音量从此由这张卡片控制）");
                    }
                    Log("[输出] 同设备的多张卡片已合并为多声道卡片: " + DescribeCards());
                }

                if (changed) RebuildLanes();
                return changed;
            }
            finally { normalizing = false; }
        }

        /// <summary>
        /// 行数对齐: 多声道卡片 = 该设备物理声道数 (读不到按规格声道数), 单声道卡片 = 1 行。
        /// <returns>true = 行数变了。</returns>
        /// </summary>
        bool EnsureSlots(OutputCardModel m)
        {
            int want = m.Multi ? DeviceChannelCount(m.DeviceId) : 1;
            bool changed = false;
            if (m.Slots.Count > want)
            {
                int dropped = 0;
                for (int i = want; i < m.Slots.Count; i++)
                    if (!string.IsNullOrEmpty(m.Slots[i].ChannelKey)) dropped++;
                m.Slots.RemoveRange(want, m.Slots.Count - want);
                if (dropped > 0)
                    Log("[输出] " + DevNameFor(m.DeviceId) + " 的可用声道减少，丢弃了 " + dropped + " 个多声道行");
                changed = true;
            }
            while (m.Slots.Count < want)
            {
                m.Slots.Add(new ChannelSlot { ChannelKey = NextFreeChannelKey(m), Volume = 100 });
                changed = true;
            }
            return changed;
        }

        /// <summary>该设备能用的物理声道数 (读不到就按当前规格的声道数)。</summary>
        int DeviceChannelCount(string deviceId)
        {
            int n = EndpointChannels(deviceId);
            if (n <= 0) n = CurrentSpecChannels().Count;
            return Math.Max(1, Math.Min(8, n));
        }

        /// <summary>卡片里下一个还没用到的规格声道; 用完了返回 "" (空行)。</summary>
        string NextFreeChannelKey(OutputCardModel m)
        {
            var chans = CurrentSpecChannels();
            for (int i = 0; i < chans.Count; i++)
            {
                bool used = false;
                for (int k = 0; k < m.Slots.Count; k++)
                    if (m.Slots[k].ChannelKey == chans[i].Key) { used = true; break; }
                if (!used) return chans[i].Key;
            }
            return "";
        }

        /// <summary>读某设备端点当前音量 (合并卡片时给主音量取初值)。失败返回 fallback。</summary>
        int ReadEndpointPercent(string deviceId, int fallback)
        {
            if (catalog == null || string.IsNullOrEmpty(deviceId)) return fallback;
            MMDevice d = null;
            EndpointVolumeBinding b = null;
            try
            {
                d = catalog.Open(deviceId);
                if (d == null) return fallback;
                b = new EndpointVolumeBinding(d, "读取", Log);
                return b.IsValid ? Math.Max(0, Math.Min(100, (int)Math.Round(b.ReadVolume() * 100))) : fallback;
            }
            catch { return fallback; }
            finally
            {
                // EndpointVolumeBinding 自己释放它持有的 MMDevice; 绑定没建起来才由这里释放
                if (b != null) { try { b.Dispose(); } catch { } }
                else if (d != null) { try { d.Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// 声道键必须落在当前规格里。force=true 时按顺序重排 (换规格用), 规格声道用完之后的行置空。
        /// </summary>
        void ReassignLanesToSpec(bool force)
        {
            var chans = CurrentSpecChannels();
            if (chans.Count == 0) return;
            var used = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < models.Count; i++)
            {
                OutputCardModel m = models[i];
                int n = Math.Max(1, m.Slots.Count);
                for (int s = 0; s < n; s++)
                {
                    if (s >= m.Slots.Count) m.Slots.Add(new ChannelSlot());
                    ChannelSlot slot = m.Slots[s];
                    bool ok = false;
                    for (int k = 0; k < chans.Count; k++)
                        if (chans[k].Key == slot.ChannelKey) { ok = true; break; }
                    if (!force && ok) { used.Add(slot.ChannelKey); continue; }

                    string next = "";
                    for (int k = 0; k < chans.Count; k++)
                        if (!used.Contains(chans[k].Key)) { next = chans[k].Key; break; }
                    // 单声道卡片不能没有声道 (否则整张卡不出声), 规格排完了就复用第一个
                    if (next.Length == 0 && !m.Multi) next = chans[0].Key;
                    slot.ChannelKey = next;
                    if (next.Length > 0) used.Add(next);
                }
            }
        }

        void RebuildCards()
        {
            for (int i = 0; i < cards.Count; i++)
            {
                cards[i].Changed -= OnCardChanged;
                cards[i].VolumeScrolled -= OnCardVolumeScrolled;
                cards[i].RemoveRequested -= OnCardRemoveRequested;
                cards[i].ShapeBlocked -= OnCardShapeBlocked;
                cardHost.Controls.Remove(cards[i]);
                cards[i].Dispose();
            }
            cards.Clear();

            var chans = CurrentSpecChannels();
            var devTexts = new List<string>();
            for (int i = 0; i < Devices.Count; i++) devTexts.Add("[" + i + "] " + Devices[i].FriendlyName);

            // 新建卡片先按"当前卡片数"该有的栅格摆好, 免得先按满宽建再被 FitCardWidths 挤一次
            int gridCols, gridW;
            ComputeCardGrid(models.Count, out gridCols, out gridW);
            var gridMargin = new Padding(0, 0, gridCols > 1 ? CardGapX : 0, OutputCardControl.CardGap);

            for (int i = 0; i < models.Count; i++)
            {
                OutputCardModel m = models[i];
                var card = new OutputCardControl(m);
                card.SetDevices(devTexts);
                card.SetDeviceIndex(catalog == null ? -1 : catalog.IndexOf(m.DeviceId));
                card.SetChannels(chans);
                // 行控件要先建出来, SyncFromModel 才填得进值
                card.SetMulti(m.Multi, m.Multi ? m.Slots.Count : 0, chans);
                card.SyncFromModel(chans);
                card.Changed += OnCardChanged;
                card.VolumeScrolled += OnCardVolumeScrolled;
                card.RemoveRequested += OnCardRemoveRequested;
                card.ShapeBlocked += OnCardShapeBlocked;
                // 卡片自带圆角外框: 竖向靠紧 (间隙 = Margin.Bottom), 横向留 CardGapX 供换列
                card.Margin = gridMargin;
                card.Width = gridW;
                cardHost.Controls.Add(card);
                cards.Add(card);
            }
            // 逐个加入过程中滚动条可能刚好切换, 统一再拉一次;
            // 滚动条的最终显隐要等布局跑完, 故再用 BeginInvoke 兜一次。
            lastCardCols = -1;                      // 卡片是新控件: 强制重排一次
            FitCardWidths();
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)FitCardWidths);
            ApplyUiLock();          // 新建的卡片要继承当前锁定状态(运行中/启动中)
            RefreshSpecBadges();
        }

        /// <summary>
        /// 分隔条拖动不设上限 (`Panel1MinSize = 0`), 日志区可以一路拉到顶;
        /// 但**窗口整体变矮**时不能把内容区缩没: 一旦内容区被压到 284 以下 (界面上只剩日志),
        /// 就把它拽回 284。只在"高度变小"时触发, 用户主动往上拖分隔条不受这条规则干扰。
        /// </summary>
        void KeepContentOnShrink()
        {
            if (splitContainer == null) return;
            int h = splitContainer.Height;
            bool shrinking = lastSplitH > 0 && h < lastSplitH;
            lastSplitH = h;
            if (!shrinking) return;

            const int minContent = 284;
            int dist = splitContainer.SplitterDistance;
            if (dist >= minContent) return;
            int maxDist = h - splitContainer.Panel2MinSize - splitContainer.SplitterWidth;
            if (maxDist <= dist) return;                 // 窗口矮到连内容区都放不下: 只能这样
            int want = Math.Min(minContent, maxDist);
            if (want <= dist) return;
            try { splitContainer.SplitterDistance = want; } catch { }
        }

        /// <summary>
        /// 日志框按日志面板的实时尺寸摆放: 左边距 15, 右边距 10 (文本框自己的竖滚动条
        /// 就贴在面板右缘), 下面距 8。拖分隔条改日志区高度时同步跟随。
        /// </summary>
        void LayoutLogPanel()
        {
            if (txtLog == null || splitContainer == null) return;
            Size cs = splitContainer.Panel2.ClientSize;
            // 尺寸没变就别再 SetBounds: 拖分隔条时这个回调每帧都来,
            // 每次都改一次 bounds 会连带整块日志框重排重画。
            if (cs == lastLogPanelSize) return;
            lastLogPanelSize = cs;
            int w = Math.Max(120, cs.Width - 15 - 10);
            int h = Math.Max(40, cs.Height - 30 - 8);
            txtLog.SetBounds(15, 30, w, h);
        }

        /// <summary>
        /// 增益/主音量两行的自适应布局 (与输出卡片里的音量条同理): 滑条右端跟着面板宽度走,
        /// 右起固定为 [静音][10][百分比][8][滑条]。
        /// 增益行没有静音框, 但预留同样的宽度 —— 两条滑条等长、百分比数字竖向对齐。
        /// </summary>
        void LayoutVolumeRows()
        {
            if (volMaster == null || chkMuteMaster == null) return;
            Control host = volMaster.Parent;
            if (host == null) return;

            int panelW = host.ClientSize.Width;
            // 宽度没变 (改高度/拖分隔条也会走到这里) → 摆出来的结果不会变, 直接跳过
            if (panelW == lastVolPanelW) return;
            lastVolPanelW = panelW;
            const int leftEdge = 78;    // 滑条左端 = "增益:"/"主音量:" 标签右缘
            const int rightEdge = 10;   // 右端留白, 与卡片区/日志框右缘一致
            int muteW = chkMuteMaster.Width;
            int pctW = lblVolMaster.Width;

            chkMuteMaster.Location = new Point(panelW - rightEdge - muteW, 106);
            int pctX = chkMuteMaster.Left - 10 - pctW;
            int volW = Math.Max(120, pctX - 8 - leftEdge);

            volGain.SetBounds(leftEdge, 72, volW, 20);
            lblVolGain.Location = new Point(pctX, 72);
            volMaster.SetBounds(leftEdge, 106, volW, 20);
            lblVolMaster.Location = new Point(pctX, 106);
        }

        /// <summary>卡片基准宽度: 换列的步长。取 490 = 卡片里固定控件(设备/声道/延迟/百分号/静音)
        /// 占掉约 273px 之后, 滑条还剩 200px 左右的最舒适宽度, 也是默认窗口下单列卡片的取值区。</summary>
        const int CardBaseW = 490;
        /// <summary>并排卡片之间的横向间距 (卡片外框到外框)。</summary>
        const int CardGapX = 10;
        /// <summary>卡片最窄宽度: 再窄第一行的设备下拉就压到「声道」下拉上了。</summary>
        const int CardMinW = 430;
        /// <summary>单列时的宽度上限: 再宽滑条就拉成一根看不见刻度的长线。</summary>
        const int CardMaxW = 1120;

        /// <summary>
        /// 算卡片栅格: 列数 + 每张卡片的宽度。
        /// <para>列数按"基准宽度的整数倍"跳档: 可视宽到 2×基准才铺第 2 列、到 3×基准才铺第 3 列。
        /// 因为余量摊进卡片宽度, 实际卡片宽度始终落在 [基准, 基准+间距] 这个窄区间里,
        /// 所以换列时滑条长度只变化几像素, 不会有"窗口宽一点、滑条就长一截"的拉扯感。</para>
        /// <para><b>竖滚动条要占宽度</b>: 窗口变矮 → 卡片区出现竖条 → 可视宽少了 17px。
        /// 如果卡片宽度还按"没竖条"的宽度算, 内容就比可视宽多出一条竖条的量,
        /// 横向滚动条立刻跟着冒出来。所以这里先算一遍列数, 用算出来的行数预测竖条会不会出现,
        /// 会出现就把它的宽度扣掉再算一遍 —— 卡片自己变窄, 永远不需要横向滚动。</para>
        /// </summary>
        void ComputeCardGrid(int count, out int cols, out int width)
        {
            if (cardHost == null)
            {
                cols = 1;
                width = OutputCardControl.CardWidth;
                return;
            }

            // cardHost.Width 是"外框宽", 竖条显隐不影响它 (ClientSize 会跟着少一条滚动条),
            // 用它当基准算出来的宽度才是稳定的; ClientSize 则用来看"此刻真实可用"多少。
            int full = Math.Max(0, cardHost.Width - cardHost.Padding.Horizontal);
            int avail = Math.Min(full, Math.Max(0, cardHost.ClientSize.Width - cardHost.Padding.Horizontal));
            if (avail <= 0) { cols = 1; width = OutputCardControl.CardWidth; return; }

            GridCols(avail, count, out cols, out width);

            // 这一版摆完会比可视区高 → 竖条会出现 → 扣掉竖条宽度重算, 卡片顺势缩短
            if (NeedVScroll(cols))
            {
                int narrower = Math.Max(0, full - SystemInformation.VerticalScrollBarWidth);
                if (narrower >= CardMinW && narrower < avail)
                    GridCols(narrower, count, out cols, out width);
            }
        }

        /// <summary>纯算术: 给定可用宽度与卡片数, 算出列数与统一卡片宽度。</summary>
        static void GridCols(int avail, int count, out int cols, out int width)
        {
            if (avail <= 0) { cols = 1; width = OutputCardControl.CardWidth; return; }

            cols = Math.Max(1, (avail + CardGapX) / (CardBaseW + CardGapX));
            cols = Math.Min(cols, Math.Max(1, count));

            if (cols <= 1)
            {
                // 单列: 卡片铺满可视宽 (与改造前完全一致), 只有窗口宽到离谱时才封顶
                width = Math.Min(Math.Max(avail, CardMinW), CardMaxW);
            }
            else
            {
                // 多列: 每列都带一个右侧间距(含最后一列), 这样 FlowLayoutPanel 的换行判断
                // (x + 卡宽 + Margin.Right > 可视宽) 正好在算好的列数处触发, 不会提前折行。
                width = (avail - cols * CardGapX) / cols;
                if (width < CardMinW)
                {
                    // 窗口窄到连最小宽度都排不下这么多列 → 主动减列, 宁可少一列也不挤成畸形
                    cols = Math.Max(1, (avail + CardGapX) / (CardMinW + CardGapX));
                    width = cols <= 1 ? Math.Min(Math.Max(avail, CardMinW), CardMaxW)
                                      : (avail - cols * CardGapX) / cols;
                }
            }
        }

        int CardCountForGrid()
        {
            if (cards != null && cards.Count > 0) return cards.Count;
            return models != null ? models.Count : 0;
        }

        /// <summary>第 i 张卡片的实际高度 (还没建出来时按模型推算, 与 SetMulti 的公式一致)。</summary>
        int CardHeightAt(int i)
        {
            if (cards != null && i < cards.Count) return cards[i].Height;
            if (models != null && i < models.Count)
                return OutputCardControl.CardHeight +
                    (models[i].Multi ? 4 + Math.Max(1, models[i].Slots.Count) * OutputCardControl.RowHeight : 0);
            return OutputCardControl.CardHeight;
        }

        /// <summary>
        /// 按这个列数摆完, 内容会不会比可视区高 (即竖滚动条会不会出现)。
        /// 行高取该行最高的一张卡片, 与 FlowLayoutPanel 的排法一致。
        /// </summary>
        bool NeedVScroll(int cols)
        {
            int n = CardCountForGrid();
            if (n == 0 || cardHost == null) return false;
            if (cols < 1) cols = 1;

            int rows = (n + cols - 1) / cols;
            int total = cardHost.Padding.Vertical;
            for (int r = 0; r < rows; r++)
            {
                int hMax = 0;
                for (int i = r * cols; i < Math.Min(n, (r + 1) * cols); i++)
                {
                    int h = CardHeightAt(i) + OutputCardControl.CardGap;   // + Margin.Bottom
                    if (h > hMax) hMax = h;
                }
                total += hMax;
            }
            return total > cardHost.ClientSize.Height;
        }

        bool fitAgain;           // 本轮摆完又触发布局 → 需要再收敛一次

        /// <summary>
        /// 按当前可视宽度摆卡片: 统一宽度 + 横向间距。
        /// 不逐张算宽度: 卡片是逐个加进 FlowLayoutPanel 的, 滚动条可能在中途出现/消失,
        /// 后建的卡片量到的可视宽度就比前面的大一个滚动条宽 (17px), 于是滑条长度对不齐。
        /// <para>改宽度本身会触发容器重新布局, 布局又会改滚动条 → 再发 ClientSizeChanged。
        /// 以前那一次回调被重入保护直接丢掉, 于是"竖条出现了、卡片还是旧宽度"这一版就留在屏幕上
        /// (横条就是这么来的)。这里改成: 被丢掉的那次记个待办, 本轮回手后再收敛一遍。</para>
        /// </summary>
        void FitCardWidths()
        {
            if (cards == null || cards.Count == 0) return;
            if (fittingWidths) { fitAgain = true; return; }
            fittingWidths = true;
            try
            {
                int guard = 0;
                do
                {
                    fitAgain = false;
                    ApplyCardWidths();
                }
                while (fitAgain && ++guard < 4);
            }
            finally { fittingWidths = false; }
        }

        void ApplyCardWidths()
        {
            int cols, w;
            ComputeCardGrid(cards.Count, out cols, out w);

            int avail = cardHost.ClientSize.Width - cardHost.Padding.Horizontal;
            // 兜底: 卡片绝不比可视区宽 (宽到连最小卡片宽都排不下时才听任横向滚动,
            // 宁可横滚也不能把第一行的设备/声道/延迟挤成重叠)
            if (avail >= CardMinW && w > avail) w = avail;

            // 单列不留右侧间距: 那段 10px 会被算进内容宽度, 宽度已 = 可视宽时
            // 正好顶出一根横向滚动条 (实测 600/1000/1200 宽都出现过)。
            // 多列必须留, 否则 FlowLayoutPanel 的换行判断会提前一张就折行。
            int rightGap = cols > 1 ? CardGapX : 0;

            // 缩放窗口时 Resize 与 ClientSizeChanged 会连着来好几次, 下面这轮
            // SuspendLayout/ResumeLayout 是**每个子控件全量重排**, 一帧里重复跑就是缩放卡顿的主因。
            // 列数/宽度/间距都没变(改高度、滚动条开关都会走到这儿) → 直接跳过重排。
            if (cols == lastCardCols && w == lastCardW && rightGap == lastRightGap
                && cards.Count == lastCardCount)
            {
                DropStaleHScroll();
                return;
            }
            lastCardCols = cols;
            lastCardW = w;
            lastRightGap = rightGap;
            lastCardCount = cards.Count;

            var margin = new Padding(0, 0, rightGap, OutputCardControl.CardGap);

            // 我们在 panel 自己的一次布局里改子控件尺寸 (ClientSizeChanged 就是在布局中发的),
            // 外面再套一层挂起/恢复, 让它在尺寸全部落定后统一重算换行与滚动条;
            // 不这么做的话它会把"内容宽"记成改之前的旧值。
            cardHost.SuspendLayout();
            try
            {
                for (int i = 0; i < cards.Count; i++)
                {
                    // 先摆间距再改宽: 换列发生在容器重新布局时, 间距必须已经就位
                    if (cards[i].Margin != margin) cards[i].Margin = margin;
                    if (cards[i].Width != w) cards[i].Width = w;
                }
            }
            finally { cardHost.ResumeLayout(true); }

            DropStaleHScroll();

            if (cols != gridLogCols)
            {
                gridLogCols = cols;
                Log("[界面] 卡片区 " + cols + " 列 × " + w + "px（可视 " + avail + "px，" +
                    cards.Count + " 张卡片）");
            }
        }

        /// <summary>
        /// 竖滚动条刚出现的那一版, FlowLayoutPanel 会把"内容宽"记成卡片收窄之前的旧宽度
        /// (实测: 卡片已 542, 它仍按 559 算), 横向滚动条就此赖着不走 —— 窗口变矮时冒出横条
        /// 就是这么来的。卡片确实已经放得下时, 开关一次 AutoScroll 让它按当前内容重算滚动条。
        /// </summary>
        void DropStaleHScroll()
        {
            if (cardHost == null || !cardHost.HorizontalScroll.Visible) return;
            if (cards == null || cards.Count == 0) return;

            // 内容真的比可视区宽 (窗口窄到连最小卡片宽都排不下) → 留着横条, 否则看不全
            int need = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                int r = cards[i].Width + cards[i].Margin.Horizontal;
                if (r > need) need = r;
            }
            if (need > cardHost.ClientSize.Width) return;

            Point keep = cardHost.AutoScrollPosition;
            cardHost.AutoScroll = false;
            cardHost.AutoScroll = true;
            cardHost.PerformLayout();
            cardHost.AutoScrollPosition = new Point(Math.Abs(keep.X), Math.Abs(keep.Y));
        }

        void AddLaneCard(OutputCardModel preset)
        {
            var chans = CurrentSpecChannels();
            var m = preset ?? new OutputCardModel();
            if (preset == null)
            {
                // 新卡片接第一个还没人接的声道 (都有人接则接第一个)
                string key = chans.Count > 0 ? chans[0].Key : "FL";
                var usedKeys = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < models.Count; i++)
                    for (int s = 0; s < models[i].Slots.Count; s++)
                        usedKeys.Add(models[i].Slots[s].ChannelKey);
                for (int i = 0; i < chans.Count; i++)
                    if (!usedKeys.Contains(chans[i].Key)) { key = chans[i].Key; break; }
                m.Slots.Add(new ChannelSlot { ChannelKey = key, Volume = 100 });

                // 新卡片默认接"还没被占用"的设备: 选到已被占用的设备会立刻并进那张多声道卡片,
                // 用户按"加一路"却看不到卡片变多, 容易以为坏了。设备都用完了就留空让用户自己选。
                m.DeviceId = FirstFreeDeviceId();
            }
            models.Add(m);
            NormalizeModels();
            RebuildCards();
            SaveSettings();
            Log("[输出] 新增卡片: " + (m.Slots.Count > 0 ? ChannelNameOf(m.Slots[0].ChannelKey) : "未选声道") +
                " → " + DevNameFor(m.DeviceId) + " (共 " + models.Count + " 张)");
            // 运行中加卡: 该设备可能刚接入端点音量, 归属要跟着改 (绑端点 ⇄ 混音增益)
            if (running) SyncEndpointBindingsWhileRunning();
        }

        /// <summary>还没被任何卡片占用的设备 ID (没有则返回 "")。</summary>
        string FirstFreeDeviceId()
        {
            if (catalog == null) return "";
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < models.Count; i++) used.Add(models[i].DeviceId ?? "");
            for (int i = 0; i < Devices.Count; i++)
            {
                string id = DeviceIdAt(i);
                if (!used.Contains(id)) return id;
            }
            return "";
        }

        void OnCardChanged(OutputCardControl card)
        {
            if (loadingSettings || loadingDevices || normalizing) return;
            // 形态切换前先记下旧形态: 单声道卡片的音量 == 设备端点音量, 多声道行音量 == 矩阵增益。
            // 两者的归属不同, 切换时要把衰减从一个容器搬到另一个, 否则会双份相乘 (12%×12%=1.44%)。
            bool wasMulti = card.Model.Multi;
            int oldRow0 = card.Model.Slots.Count > 0 ? card.Model.Slots[0].Volume : 100;

            SyncLanesFromCards();
            OutputCardModel m = card.Model;
            if (!wasMulti && m.Multi)
            {
                // 单 → 多: 主音量继续承载端点, 各行增益复位 (行是新出现的, 不该继承端点值)
                for (int i = 0; i < m.Slots.Count; i++) m.Slots[i].Volume = 100;
                RebuildLanes();
                Log("[输出] " + DevNameFor(m.DeviceId) + " 切到多声道: 主音量 " + m.Volume +
                    "% 仍由设备端点承载, 各行增益复位 100%");
            }
            else if (wasMulti && !m.Multi)
            {
                // 多 → 单: 把原 p0 行的增益折进主音量, 切回单声道不跳响度
                m.Volume = Math.Max(0, Math.Min(100, (int)Math.Round(m.Volume * oldRow0 / 100.0)));
                if (m.Slots.Count > 0) m.Slots[0].Volume = 100;
                RebuildLanes();
                Log("[输出] " + DevNameFor(m.DeviceId) + " 切回单声道: 原 p0 行增益 " + oldRow0 +
                    "% 已折进主音量 → " + m.Volume + "%");
            }

            // 声道下拉切到/切离「多声道」, 或换到已被占用的设备 → 形态变了, 必须先重建卡片再存盘
            // (models 已被合并/改行数, 旧 cards 再写回模型就会写到错误的卡片上)
            bool structural = NormalizeModels();
            if (structural) RebuildCards();
            SaveSettings();
            RefreshSpecBadges();
            if (structural) return;

            // 换设备: 先把被弃设备的音量还原, 再按新集合绑端点 (否则旧设备永久停在分流音量)
            if (running) SyncEndpointBindingsWhileRunning();
            // 卡片主音量 = 设备端点音量 → 静音也写端点 (与音量同一套双向绑定/还原基线)
            if (running && bindings != null && !volSyncing)
            {
                var b = bindings.SinkOf(card.Model.DeviceId);
                if (b != null && b.IsValid && b.ReadMuted() != card.chkMute.Checked)
                    b.WriteMute(card.chkMute.Checked);
            }
            if (running) ApplyRoutingHot("卡片改动", false);
        }

        /// <summary>
        /// 运行中试图切单/多声道形态 → 已被卡片拦下并回滚。形态改的是设备物理声道数
        /// (OutChannels)，WasapiOut 建好后换不了，只能重新开始。
        /// </summary>
        void OnCardShapeBlocked(OutputCardControl card)
        {
            Log("[提示] " + DevNameFor(card.Model.DeviceId) + " 单/多声道形态是链路级参数，" +
                "运行中不能切换（已还原）。要改请先「停止分流」。");
        }

        void OnCardVolumeScrolled(OutputCardControl card, bool isSlot)
        {
            if (volSyncing) return;
            // 主滑条 = 设备端点音量; 行滑条 = 混音增益 (端点音量是设备级单值, 分不了声道)
            var b = (bindings != null) ? bindings.SinkOf(card.Model.DeviceId) : null;
            if (!isSlot && b != null && b.IsValid)
            {
                b.WriteVolume(card.volTrack.Value / 100f);
                if (card.volTrack.Value > 0 && b.ReadMuted()) b.WriteMute(false);
            }
            // 拖动中的热更新: 只改增益, 不记日志 (一次拖动几十次事件, 记了就是刷屏)
            else if (running) ApplyRoutingHot("音量", true);
            SaveSettings();
        }

        void OnCardRemoveRequested(OutputCardControl card)
        {
            if (loadingSettings) return;
            int idx = cards.IndexOf(card);
            if (idx < 0 || idx >= models.Count) return;
            if (models.Count <= 1) { Log("[输出] 至少保留一张输出卡片"); return; }
            string who = CardRole(models[idx]) + " → " + DevNameFor(models[idx].DeviceId);
            models.RemoveAt(idx);
            RebuildCards();
            SaveSettings();
            Log("[输出] 已移除: " + who + " (剩 " + models.Count + " 张)");
            // 运行中删卡: 该设备的端点音量必须立刻还原
            if (running) SyncEndpointBindingsWhileRunning();
        }

        /// <summary>
        /// 规格行右侧的声道标注: 有输出的显示蓝色并在名字旁挂数字角标(= 路数), 没输出的灰色。
        /// </summary>
        void RefreshSpecBadges()
        {
            if (flwSpecChannels == null) return;
            flwSpecChannels.SuspendLayout();
            try
            {
                flwSpecChannels.Controls.Clear();
                var chans = CurrentSpecChannels();
                for (int i = 0; i < chans.Count; i++)
                {
                    // 角标 = 有几个物理声道接到该声道 (多声道卡片的每一行各算一个)
                    int n = 0;
                    for (int k = 0; k < models.Count; k++)
                        for (int s = 0; s < models[k].Slots.Count; s++)
                            if (models[k].Slots[s].ChannelKey == chans[i].Key) n++;
                    var name = new Label
                    {
                        Text = chans[i].Value,
                        AutoSize = true,
                        Margin = new Padding(1, 7, 0, 0),
                        ForeColor = n > 0 ? Color.SteelBlue : Color.Gray
                    };
                    flwSpecChannels.Controls.Add(name);
                    if (n > 0)
                    {
                        var badge = new Label
                        {
                            Text = n.ToString(),
                            AutoSize = false,
                            Size = new Size(15, 15),
                            TextAlign = ContentAlignment.MiddleCenter,
                            BackColor = Color.SteelBlue,
                            ForeColor = Color.White,
                            Margin = new Padding(1, 4, 3, 0),
                            Font = new Font("Microsoft YaHei UI", 7F, FontStyle.Bold)
                        };
                        flwSpecChannels.Controls.Add(badge);
                    }
                }
            }
            finally { flwSpecChannels.ResumeLayout(); }
        }

        /// <summary>从 ini 装载输出模型: 优先 Out* 新键, 没有则用旧的左右键合成两路。</summary>
        void LoadLanes()
        {
            models.Clear();
            cmbSpec.SelectedIndex = Math.Max(0, Math.Min(4, GetInt("Spec", 1)));

            int count = GetInt("OutCount", 0);
            if (count > 0)
            {
                for (int i = 0; i < count && i < 16; i++)
                {
                    string pk = "Out" + i + ".";
                    var m = new OutputCardModel
                    {
                        DeviceId = settings.GetStr(pk + "DeviceId") ?? "",
                        DelayMs = Math.Max(0, Math.Min(2000, GetInt(pk + "DelayMs", 0))),
                        Volume = Math.Max(0, Math.Min(100, GetInt(pk + "Volume", 100))),
                        Mute = GetInt(pk + "Mute", 0) != 0
                    };
                    string channel = StrOrDefault(settings.GetStr(pk + "Channel"), "FL");
                    bool multi = GetInt(pk + "Multi", 0) != 0 || channel == LaneRouting.MultiKey;

                    int n = Math.Max(0, Math.Min(8, GetInt(pk + "ChCount", 0)));
                    for (int s = 0; s < n; s++)
                    {
                        string ck = pk + "Ch" + s + ".";
                        m.Slots.Add(new ChannelSlot
                        {
                            ChannelKey = settings.GetStr(ck + "Channel") ?? "",
                            Volume = Math.Max(0, Math.Min(100, GetInt(ck + "Volume", 100)))
                        });
                    }

                    if (multi)
                    {
                        m.Multi = true;                 // 行数由 NormalizeModels 按设备声道数补齐
                        if (m.Slots.Count == 0) m.Slots.Add(new ChannelSlot { ChannelKey = "FL", Volume = 100 });
                    }
                    else
                    {
                        // 旧 ini (没有 Multi/Ch*): 一路 = 一张单声道卡片, 同设备的会在 Normalize 里合并
                        m.Slots.Add(new ChannelSlot
                        {
                            ChannelKey = channel,
                            Volume = Math.Max(0, Math.Min(100, GetInt(pk + "Volume", 100)))
                        });
                    }
                    models.Add(m);
                }
            }
            else
            {
                // 老配置 (DeviceLeft/DeviceRight 那套) → 左右两张卡片
                var l = new OutputCardModel
                {
                    DeviceId = GetStr("DeviceLeft") ?? "",
                    DelayMs = Math.Max(0, Math.Min(2000, GetInt("DelayLeft", 0))),
                    Volume = Math.Max(0, Math.Min(100, GetInt("VolumeLeft", 100))),
                    Mute = GetInt("MuteLeft", 0) != 0
                };
                l.Slots.Add(new ChannelSlot { ChannelKey = "FL", Volume = l.Volume });
                models.Add(l);

                var r = new OutputCardModel
                {
                    DeviceId = GetStr("DeviceRight") ?? "",
                    DelayMs = Math.Max(0, Math.Min(2000, GetInt("DelayRight", 0))),
                    Volume = Math.Max(0, Math.Min(100, GetInt("VolumeRight", 100))),
                    Mute = GetInt("MuteRight", 0) != 0
                };
                r.Slots.Add(new ChannelSlot { ChannelKey = "FR", Volume = r.Volume });
                models.Add(r);
            }

            ReassignLanesToSpec(false);   // 只修正落不进规格的声道键
            // 这里**不合并**: 构造期 catalog 还没建, 读不到端点音量, 合并出来的主音量会是 100%
            // (老 ini 里同设备两张卡各自是"混音增益", 主音量一旦按 100% 写端点就会突然变响)。
            // 合并放在 RefreshDevices 里做 —— 那时能读到端点真值, 合并不改响度。
            RebuildCards();
        }

        static string StrOrDefault(string s, string def)
        {
            return string.IsNullOrEmpty(s) ? def : s;
        }

        int GetInt(string key, int def)
        {
            return settings.GetInt(key, def);
        }

        string GetStr(string key)
        {
            return settings.GetStr(key);
        }

        string DeviceIdAt(int idx)
        {
            return catalog.IdAt(idx);
        }

        void SaveSettings()
        {
            // 初始化阶段(读ini/Bind回读/覆盖回写)不落盘, 防止把ini冲成端点回读值
            if (loadingSettings) return;
            // 字典立即同步: RefreshDevices 等按字典恢复选中, 不能等写盘
            SyncSettingsDict();
            // 文件写盘 debounce 300ms: 拖动滑条时避免每次移动都写盘
            if (saveDebounce != null)
            {
                saveDebounce.Stop();
                saveDebounce.Start();
            }
        }

        void FlushSettings()
        {
            if (loadingSettings) return;
            SyncSettingsDict();

            string err;
            if (settings.WriteAll(out err))
            {
                lastSaveWarnTick = 0;      // 恢复正常后允许下次重新告警
                return;
            }

            // 失败限流: 60 秒最多一条, 避免拖动滑条时刷屏
            int now = Environment.TickCount;
            if (lastSaveWarnTick != 0 && unchecked(now - lastSaveWarnTick) < 60000) return;
            lastSaveWarnTick = now;
            Log("[警告] 设置写入失败: " + err + "；本次运行的改动可能不会保留 (" + IniPath + ")");
        }

        void SyncSettingsDict()
        {
            try
            {
                settings.Put("MinimizeToTray", chkMinimizeToTray.Checked ? "1" : "0");
                settings.Put("LatencyProfile", Math.Max(0, cmbLatency.SelectedIndex).ToString());
                settings.Put("Spec", cmbSpec.SelectedIndex < 0 ? "1" : cmbSpec.SelectedIndex.ToString());
                SyncLanesFromCards();
                settings.RemovePrefix("Out");   // 先清干净, 删过的卡/行不留孤儿键
                settings.Put("OutCount", models.Count.ToString());
                for (int li = 0; li < models.Count && li < 16; li++)
                {
                    OutputCardModel m = models[li];
                    string pk = "Out" + li + ".";
                    // Channel 仍是"卡片声道": 单声道写声道键, 多声道写哨兵 (旧版本读到哨兵会退回第一行)
                    settings.Put(pk + "Channel",
                        m.Multi ? LaneRouting.MultiKey : (m.Slots.Count > 0 ? m.Slots[0].ChannelKey : "FL"));
                    settings.Put(pk + "DeviceId", m.DeviceId ?? "");
                    settings.Put(pk + "DelayMs", m.DelayMs.ToString());
                    settings.Put(pk + "Volume", m.Volume.ToString());
                    settings.Put(pk + "Mute", m.Mute ? "1" : "0");
                    settings.Put(pk + "Multi", m.Multi ? "1" : "0");
                    settings.Put(pk + "ChCount", m.Slots.Count.ToString());
                    for (int s = 0; s < m.Slots.Count && s < 8; s++)
                    {
                        string ck = pk + "Ch" + s + ".";
                        settings.Put(ck + "Channel", m.Slots[s].ChannelKey ?? "");
                        settings.Put(ck + "Volume", m.Slots[s].Volume.ToString());
                    }
                }
                settings.Put("WaterIntervalSec", ((int)nudWaterInterval.Value).ToString());
                settings.Put("Gain", volGain.Value.ToString());
                // 兼容键: 左右 = FL/FR 输出卡片的设备 (旧 ini 与外部工具仍按这两个键读)
                settings.Put("DeviceLeft", LeftDeviceId() ?? "");
                settings.Put("DeviceRight", RightDeviceId() ?? "");
                settings.Put("DeviceDefault", DeviceIdAt(cmbDefault.SelectedIndex));
                OutputCardControl vcl = LeftCard(), vcr = RightCard();
                settings.Put("VolumeLeft", (vcl != null ? vcl.volTrack.Value : 100).ToString());
                settings.Put("VolumeRight", (vcr != null ? vcr.volTrack.Value : 100).ToString());
                settings.Put("VolumeMaster", volMaster.Value.ToString());
                settings.Put("MuteLeft", (vcl != null && vcl.chkMute.Checked) ? "1" : "0");
                settings.Put("MuteRight", (vcr != null && vcr.chkMute.Checked) ? "1" : "0");
                settings.Put("MuteMaster", chkMuteMaster.Checked ? "1" : "0");
                settings.Put("MuteCapture", chkMuteCapture.Checked ? "1" : "0");
                settings.Put("DriftMode", driftMode.ToString());
                settings.Put("FixedRatioPpm", fixedPpm.ToString());
            }
            catch { }
        }

        void RestoreDeviceSelection(string key, NoWheelComboBox combo, string label)
        {
            string id = GetStr(key);
            if (string.IsNullOrEmpty(id)) return;
            int idx = catalog.IndexOf(id);
            if (idx >= 0) combo.SelectedIndex = idx;
            else Log("[初始化] 未找到已保存的" + label + "设备，使用默认选择");
        }

        // ini 音量在启动时由 WriteUIValuesToDevice 统一写入端点 (Load 期尚未绑定端点, 此处只回填 UI)
        void ApplyIniVolumeOverrides()
        {
            bool any = false;
            volSyncing = true;
            try
            {
                if (settings.Has("VolumeMaster"))
                {
                    int v = Math.Max(0, Math.Min(100, GetInt("VolumeMaster", 100)));
                    volMaster.Value = v;
                    lblVolMaster.Text = v + "%";
                    any = true;
                }
                if (settings.Has("MuteMaster"))
                {
                    bool m = GetInt("MuteMaster", 0) != 0;
                    chkMuteMaster.Checked = m;
                    any = true;
                }
                if (settings.Has("MuteCapture"))
                {
                    bool m = GetInt("MuteCapture", 0) != 0;
                    chkMuteCapture.Checked = m;
                    any = true;
                }
            }
            finally
            {
                volSyncing = false;
            }

            // 存量设置音量为0 → 补勾静音 (与拖到0自动静音规则一致)
            if (volMaster.Value == 0) chkMuteMaster.Checked = true;
            for (int i = 0; i < models.Count; i++)
                if (models[i].Volume == 0) models[i].Mute = true;

            if (any)
                Log("[初始化] 设置已加载: 主=" + volMaster.Value + "%/" + (chkMuteMaster.Checked ? "静音" : "开") +
                    " 输出 " + models.Count + " 张卡片 (" + LaneSummary() + ")" +
                    " 增益=" + volGain.Value + "%" +
                    (chkMuteCapture.Checked ? " 捕获消音=开" : ""));
        }

        // ==================== 托盘与单实例激活 ====================

        void InitTray()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (s, e) => RestoreFromTray());
            menu.Items.Add("退出", null, (s, e) => { exiting = true; Close(); });

            trayIcon = new NotifyIcon();
            trayIcon.Icon = this.Icon != null ? this.Icon : SystemIcons.Application;
            trayIcon.Text = "Stereo Splitter - 就绪";
            trayIcon.ContextMenuStrip = menu;
            trayIcon.Visible = true;
            trayIcon.MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
        }

        void HideToTray()
        {
            ShowInTaskbar = false;
            Hide();
            if (!trayTipShown)
            {
                trayTipShown = true;
                try { trayIcon.ShowBalloonTip(3000, "Stereo Splitter", "已最小化到托盘，双击图标恢复窗口", ToolTipIcon.Info); }
                catch { }
            }
        }

        void RestoreFromTray()
        {
            if (!ShowInTaskbar) ShowInTaskbar = true;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;

            IntPtr h = Handle;
            ShowWindow(h, SW_RESTORE);
            Activate();
            if (GetForegroundWindow() == h) return;

            // 前台锁定兜底: 正常路径由第二个实例 AllowSetForegroundWindow(ASFW_ANY) 转授许可,
            // 但从托盘菜单/任务栏点进来时没有第二实例, 只能靠把自己的输入队列临时挂到当前前台
            // 线程上 —— 共享输入队列后本线程就"继承"了前台资格, SetForegroundWindow 才会生效。
            uint fgPid;
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
            uint myThread = GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (fgThread != 0 && fgThread != myThread)
                    attached = AttachThreadInput(myThread, fgThread, true);
                BringWindowToTop(h);
                SetForegroundWindow(h);
                SetFocus(h);
            }
            catch { }
            finally
            {
                if (attached) { try { AttachThreadInput(myThread, fgThread, false); } catch { } }
            }

            if (GetForegroundWindow() != h)
            {
                // 最后兜底: 前台许可被彻底拒绝 (组策略/别的进程持续抢前台) 时短暂置顶再放开
                TopMost = true;
                TopMost = false;
                Activate();
            }
        }

        void RegisterActivateHandler()
        {
            try
            {
                activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ActivateEventName);
                // executeOnlyOnce=false: 每次第二个实例拉起都要能唤起主窗口 (true 只响应第一次)
                ThreadPool.RegisterWaitForSingleObject(activateEvent, delegate(object state, bool timedOut)
                {
                    try
                    {
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke(new Action(delegate { RestoreFromTray(); }));
                    }
                    catch { }
                }, null, -1, false);
            }
            catch { }
        }

        // 解除全部音量双向绑定 (停止分流/启动失败/退出/外部切默认时统一调用)
        // 还原端点音量与静音的逻辑收敛在 ChannelBindingSet.UnbindAll(restore:true)
        void UnbindAllVolumeSync()
        {
            if (bindings == null || !bindings.AnyBound) return;
            bindings.UnbindAll(true);
            // 中性措辞: 本方法被停止/启动失败/外部切默认三处共用, "[停止]" 会误导外部切换场景
            Log("[解绑] 双向音量绑定已解除");
            if (bindings.PendingRestoreCount > 0)
                Log("[警告] 有 " + bindings.PendingRestoreCount +
                    " 台设备的音量未能还原（设备拒绝写入），基线已保留，下次停止时会再试");
        }

        /// <summary>
        /// 运行中输出设备集合变了 (卡片换设备 / 加卡 / 删卡): 立即还原被弃设备的音量并绑定新设备。
        /// <para>不这么做的话, 换设备前那台设备会永久停在分流的音量上 —— 没人再还原它。</para>
        /// </summary>
        void SyncEndpointBindingsWhileRunning()
        {
            if (bindings == null || volSyncing) return;
            SyncLanesFromCards();

            // 期望集合 = 各卡片的设备; 已绑集合 = bindings 里的端点。双向比对
            var wanted = new List<string>();
            for (int i = 0; i < models.Count; i++)
            {
                string d = models[i].DeviceId ?? "";
                if (d.Length > 0 && !wanted.Contains(d)) wanted.Add(d);
            }
            bool same = true;
            int boundCount = 0;
            foreach (var b in bindings.Sinks())
            {
                boundCount++;
                if (!wanted.Contains(b.DeviceId)) { same = false; break; }
            }
            if (same && boundCount != wanted.Count) same = false;
            if (same) return;

            Log("[变更] 输出设备已变，同步端点绑定（被弃设备的音量立即还原）");
            BindEndpointVolumeSync("[变更]");
            WriteUIValuesToDevice();
            SetVolumeBindVisual(true);
        }

        // 数值标签有效/无效视觉: 深色 = 音量由设备端点承载(双向绑定生效), 灰色 = 走混音增益/未绑定
        void SetVolumeBindVisual(bool bound)
        {
            for (int i = 0; i < cards.Count && i < models.Count; i++)
            {
                if (cards[i].lblVol == null) continue;
                var b = bindings != null ? bindings.SinkOf(models[i].DeviceId) : null;
                bool ep = bound && b != null && b.IsValid;
                cards[i].lblVol.ForeColor = ep ? SystemColors.ControlText : SystemColors.GrayText;
            }
            if (lblVolMaster != null) lblVolMaster.ForeColor = bound ? SystemColors.ControlText : SystemColors.GrayText;
        }

        // 启动失败: 回滚系统默认输出 + 解绑(含恢复捕获静音) + Failed 状态
        void StartFailed()
        {
            // 启动失败 → 恢复启动前 UI 权威快照 (Bind 回填的设备真值不留在控件/ini 里)
            if (hasSnap)
            {
                RestoreUIValues(snapM, snapMm, snapMc);
                SaveSettings();
                hasSnap = false;
            }
            RollbackDefaultIfChanged();
            UnbindAllVolumeSync();
            ClearRunState();
            expectCaptureId = null;
            SetAppState(AppState.Failed);
        }

        // 启动流程中默认输出被 AutoConfigure 改走 → 失败时恢复到点开始时的默认
        void RollbackDefaultIfChanged()
        {
            RestoreDefaultOutput("启动失败");
        }

        /// <summary>把系统默认输出还原为本次启动前的设备 (停止/失败/启动失败共用)。
        /// 不还原的话默认仍挂在虚拟声卡上 → 应用声音全部无声, 直到下次启动才被纠正。
        /// 优先还原到 restoreDefaultId (程序初始化时检测到的"真正原始默认"):
        ///   启动时若默认已是虚拟设备(CABLE), preStartDefaultId=CABLE, 用它判"当前==preStart"会
        ///   误判为"无需还原" → 系统卡在 CABLE 无声。restoreDefaultId 才是真正该还原到的物理设备。</summary>
        void RestoreDefaultOutput(string reason)
        {
            string target = restoreDefaultId ?? preStartDefaultId;
            if (target == null || catalog == null) return;
            try
            {
                if (catalog.DefaultRenderId() != target)
                {
                    string err;
                    if (SystemDefaultPolicy.TrySetDefault(target, out err))
                        Log("[恢复] " + reason + "，默认输出 → " + FindNameById(target));
                    else
                        Log("[错误] 恢复默认输出失败(" + reason + "): " + err);
                }
            }
            catch (Exception ex) { Log("[错误] 恢复默认输出异常(" + reason + "): " + AudioErrors.Describe(ex)); }
        }

        // 把 UI 当前音量值写入已绑定端点 (点开始分流时, UI 为唯一权威源)
        void WriteUIValuesToDevice()
        {
            volSyncing = true;
            try
            {
                SyncLanesFromCards();
                bindings.WriteMaster(volMaster.Value, chkMuteCapture.Checked);

                var parts = new List<string>();
                parts.Add("主=" + volMaster.Value + "%");
                for (int i = 0; i < models.Count; i++)
                {
                    OutputCardModel m = models[i];
                    if (string.IsNullOrEmpty(m.DeviceId)) continue;
                    string role = CardRole(m);
                    var b = bindings.SinkOf(m.DeviceId);
                    if (b == null || !b.IsValid) { parts.Add(role + "=(未绑定)"); continue; }
                    int vol = Math.Max(0, Math.Min(100, m.Volume));
                    b.WriteVolume(vol / 100f);
                    if (b.ReadMuted() != m.Mute) b.WriteMute(m.Mute);
                    parts.Add(role + "=" + vol + "%" + (m.Mute ? "(静音)" : ""));
                }
                Log("[启动] 按UI值写入端点: " + string.Join(" ", parts.ToArray()) +
                    " (捕获消音=" + (chkMuteCapture.Checked ? "是" : "否") + ")");
            }
            finally { volSyncing = false; }
        }

        void RefreshDevicesClick()
        {
            Log("[刷新] 重新枚举设备...");

            // 释放旧 MMDevice/端点绑定 (未分流时通常未绑定, 幂等 no-op)
            UnbindAllVolumeSync();

            RefreshDevices();
            SaveSettings();
            Log("[刷新] 完成, 共 " + Devices.Count + " 个设备");
        }

        void RefreshDevices()
        {
            warnedLostIds.Clear(); // 列表重建, 断开警告去重一并复位
            loadingDevices = true;
            try
            {
                // 重建目录: 展示用实例由 catalog 自行释放 (端点绑定用的是独立实例, 不受影响)
                catalog.Refresh();
                cmbDefault.Items.Clear();

                var devTexts = new List<string>();
                for (int i = 0; i < Devices.Count; i++)
                {
                    string text = "[" + i + "] " + Devices[i].FriendlyName;
                    devTexts.Add(text);
                    cmbDefault.Items.Add(text);
                }

                int defIdx = catalog.IndexOf(catalog.DefaultRenderId());
                string defName = catalog.DefaultRenderName();
                if (cmbDefault.Items.Count > 0) cmbDefault.SelectedIndex = defIdx >= 0 ? defIdx : 0;

                // 恢复 ini 保存的设备选择 (按设备 ID, 找不到则保留上面的默认)
                RestoreDeviceSelection("DeviceDefault", cmbDefault, "捕获输出");

                // 输出卡片: 逐张填充设备下拉并按模型的 DeviceId 恢复
                for (int ci = 0; ci < cards.Count && ci < models.Count; ci++)
                {
                    cards[ci].SetDevices(devTexts);
                    int idx = catalog.IndexOf(models[ci].DeviceId);
                    if (idx < 0 && models[ci].DeviceId != "")
                    {
                        Log("[初始化] 输出 " + (ci + 1) + " 的设备已失效: " + models[ci].DeviceId);
                    }
                    if (idx < 0 && cards[ci].cmbDevice.Items.Count > 0)
                    {
                        // 未选/失效 → 默认接前两张卡用 0/1 号设备 (与旧版一致)
                        idx = ci < 2 ? ci : 0;
                        if (idx >= cards[ci].cmbDevice.Items.Count) idx = 0;
                        models[ci].DeviceId = DeviceIdAt(idx);
                    }
                    cards[ci].SetDeviceIndex(idx);
                }
                // 刷新后设备列表变了 → 可能有两张卡片落到同一台设备, 合并掉
                if (cards.Count > 0 && NormalizeModels()) { RebuildCards(); SyncLanesFromCards(); }

                Log("[初始化] 发现 " + Devices.Count + " 个输出设备，默认输出: [" + defIdx + "] " + defName);
            }
            finally
            {
                loadingDevices = false;
            }
        }

        void BtnStart_Click(object sender, EventArgs e)
        {
            if (running)
            {
                StopEngine();
                return;
            }

            if (cmbDefault.SelectedIndex < 0)
            {
                MessageBox.Show("请选择捕获输出设备。", "提示");
                return;
            }

            // 记录点开始时的系统默认输出 (启动失败时回滚 AutoConfigure 的修改)
            preStartDefaultId = catalog.DefaultRenderId();

            SetAppState(AppState.Starting);

            // 捕获主音量 UI 当前值 (AutoConfigure 的 Bind 会用设备真值回填覆盖, 须提前存下)
            snapM = volMaster.Value;
            snapMm = chkMuteMaster.Checked;
            snapMc = chkMuteCapture.Checked;
            hasSnap = true;

            // 点击开始时自动配置: 设默认输出 + 绑定音量 + 自动选左右 + 回环检查
            // 同步跑在 UI 线程, 含 3 次 SetDefaultEndpoint (100~500ms) → 先显示等待光标再执行
            var prevCursor = Cursor;
            bool cfgOk;
            Cursor = Cursors.WaitCursor;
            Update();                       // 只重绘, 不处理输入 (DoEvents 会有重入风险)
            try { cfgOk = AutoConfigure(); }
            finally { Cursor = prevCursor; }

            if (!cfgOk)
            {
                StartFailed();
                MessageBox.Show("自动配置失败，已中止启动。\n详见日志。", "启动中止");
                return;
            }

            if (models.Count == 0 || cards.Count == 0)
            {
                StartFailed();
                MessageBox.Show("没有可用的输出卡片，请先添加至少一张输出卡片。", "提示");
                return;
            }
            // 每张卡片（= 一台设备）都必须选好设备; 同设备多路已合并成一张多声道卡片
            for (int i = 0; i < cards.Count && i < models.Count; i++)
            {
                if (cards[i].cmbDevice.SelectedIndex < 0)
                {
                    StartFailed();
                    MessageBox.Show("第 " + (i + 1) + " 张卡片（" + CardRole(models[i]) +
                        "）还没有选择设备。", "提示");
                    return;
                }
            }

            int defIdx = catalog.IndexOf(catalog.DefaultRenderId());
            // 回环检查 (逐张): 指向捕获设备 → 采集自己的输出, 立刻啸叫
            for (int i = 0; i < cards.Count && i < models.Count; i++)
            {
                if (cards[i].cmbDevice.SelectedIndex != defIdx) continue;
                var others = new List<string>();
                for (int k = 0; k < Devices.Count; k++)
                    if (k != defIdx) others.Add("[" + k + "] " + Devices[k].FriendlyName);
                StartFailed();
                MessageBox.Show(
                    "第 " + (i + 1) + " 张卡片（" + CardRole(models[i]) + "）指向了捕获设备 [" + defIdx + "] " +
                    Devices[defIdx].FriendlyName + "，会造成声音回环！\n\n" +
                    "请把「捕获(虚拟)」改为虚拟设备(如 CABLE Input)，或给这张卡片换别的输出。\n其它可选:\n" +
                    string.Join("\n", others),
                    "设备冲突");
                return;
            }

            try
            {
                // 恢复被 Bind 回填覆盖的 UI 值, 以 UI 为唯一权威源写入端点
                RestoreUIValues(snapM, snapMm, snapMc);
                WriteUIValuesToDevice();

                // 预置运行期设备ID: 引擎在后台完成前 runCaptureId 还是空, Starting 期拔出会漏警
                // (AutoConfigure 已把系统默认设为 cmbDefault 项 → 捕获源即该设备; 引擎完成回调里再以 eng.CaptureId 校正)
                runCaptureId = DeviceIdAt(cmbDefault.SelectedIndex);
                FillRunSinkIds();

                autoRestartCount = 0;   // 手动启动 → 重新给自动重建机会
                StartParams params0 = SnapshotStartParams();
                if (!ValidateRouting(params0.Model))
                {
                    StartFailed();
                    MessageBox.Show("路由校验未通过（详见日志里的 [路由] 行），已中止启动。", "启动中止");
                    return;
                }
                LaunchEngine(runCaptureId, params0, false);
            }
            catch (Exception ex)
            {
                // 参数快照/入队阶段失败 (同步路径)
                Log("[错误] 启动失败: " + ex.Message);
                StartFailed();
                MessageBox.Show("启动失败: " + ex.Message, "错误");
            }
        }

        // 启动参数快照: 引擎 Start 在后台线程跑, UI 值必须先在本线程拷出来
        class StartParams
        {
            public int PreMs, OutMs, GainPct, Mode, FixedPpm;
            public float Gain, Master;
            /// <summary>界面 → 内核的翻译结果 (每路设备/延迟/绑定增益都在里面, 后台线程只读)。</summary>
            public RoutingModel Model;
        }

        StartParams SnapshotStartParams()
        {
            int prof = cmbLatency.SelectedIndex < 0 ? 0 : cmbLatency.SelectedIndex;
            return new StartParams
            {
                PreMs = LatencyProfiles[prof][0],
                OutMs = LatencyProfiles[prof][1],
                Gain = pendingGain,
                Master = CurrentMaster(),
                GainPct = (int)Math.Round(pendingGain * 100),
                Mode = driftMode,
                FixedPpm = fixedPpm,
                Model = BuildModelFromLanes(0, true)
            };
        }

        /// <summary>
        /// 界面 → 内核模型 (启动与热更新的唯一入口)。
        /// <para>srcCh: 捕获源声道数; 0 = 尚未启动, 按立体声假设建抽头。</para>
        /// </summary>
        RoutingModel BuildModelFromLanes(int srcCh, bool logDelayConflict)
        {
            SyncLanesFromCards();       // 控件是权威源
            bool conflict;
            var model = LaneRouting.Build(lanes, cmbSpec.SelectedIndex, srcCh,
                delegate (string id) { return catalog == null ? "" : catalog.NameOf(id); },
                out conflict);
            if (conflict && logDelayConflict)
                Log("[提示] 同一设备上的多路延迟不一致，已按最小值生效（内核每台设备只有一条延迟线）");
            return model;
        }

        /// <summary>
        /// 启动前的路由校验: Error 挡住启动, Warn/Info 只记日志。
        /// 校验两遍: 设备层 (空设备/指向捕获设备/端点声道数不足) + 声道层 (绑定越界/未分配/削波)。
        /// </summary>
        bool ValidateRouting(RoutingModel model)
        {
            var issues = model.ValidateSinks(DeviceIdAt(cmbDefault.SelectedIndex), EndpointChannels);
            // srcCh 传 0: 启动前拿不到捕获格式, 此时判抽头越界会把合法的 6ch 配置全误报
            issues.AddRange(model.Validate(0, model.EnabledSinks()));

            bool hasError = false;
            for (int i = 0; i < issues.Count; i++)
            {
                switch (issues[i].Severity)
                {
                    case IssueSev.Error: hasError = true; Log("[错误] [路由] " + issues[i].Message); break;
                    case IssueSev.Warn: Log("[警告] [路由] " + issues[i].Message); break;
                    default: Log("[提示] [路由] " + issues[i].Message); break;
                }
            }
            int en = model.EnabledSinks().Count;
            Log("[配置] 路由: " + model.DescribeConfig() + " / " + en + " 台设备");
            for (int i = 0; i < model.SinkSpecs.Count; i++)
            {
                var s = model.SinkSpecs[i];
                if (!s.Enabled) continue;
                Log("[配置] " + s.Label + " → " + FindNameById(s.DeviceId) + " (" + s.OutChannels +
                    "ch, 延迟 " + s.DelayMs + "ms)");
            }
            return !hasError;
        }

        /// <summary>端点实际输出声道数 (0 = 拿不到, 交由内核与端点协商时报错)。</summary>
        int EndpointChannels(string id)
        {
            if (catalog == null || string.IsNullOrEmpty(id)) return 0;
            MMDevice d = null;
            try
            {
                d = catalog.Open(id);
                return d == null ? 0 : d.AudioClient.MixFormat.Channels;
            }
            catch { return 0; }
            finally { if (d != null) { try { d.Dispose(); } catch { } } }
        }

        /// <summary>本次运行打开的全部输出端点 (= 启用卡片的设备)。</summary>
        void FillRunSinkIds()
        {
            runSinkIds.Clear();
            SyncLanesFromCards();
            for (int i = 0; i < models.Count; i++)
            {
                string id = models[i].DeviceId;
                if (!string.IsNullOrEmpty(id) && !runSinkIds.Contains(id)) runSinkIds.Add(id);
            }
        }

        /// <summary>
        /// 运行中热替换路由矩阵 (声道抽头/静音/音量增益): 重新 Compile 并逐路原子换引用,
        /// 不停链路、不断音。
        /// <para>设备与延迟是**链路级**参数 (每台设备一条 WasapiOut + 一条延迟线), 改它们只能
        /// 重新开始 —— 这里比对链路形状后给出明确提示, 而不是悄悄换一半。</para>
        /// </summary>
        void ApplyRoutingHot(string reason, bool quiet = false)
        {
            var eng = engine;
            if (!running || eng == null) return;
            int srcCh = 0;
            try { srcCh = eng.CaptureChannels; } catch { }

            var model = BuildModelFromLanes(srcCh, false);
            if (runModel != null && !SameLinkShape(runModel, model))
            {
                Log("[提示] 设备或延迟已改动，需「停止 → 开始」才会生效（当前链路保持不变）");
                return;
            }

            bool ok = false;
            try { ok = eng.SetRouting(model); }
            catch (Exception ex) { Log("[错误] 路由热更新失败: " + ex.Message); }

            if (ok)
            {
                runModel = model;
                if (!quiet)
                    Log("[设置] 路由已热更新 (" + reason + "): " + model.DescribeConfig() +
                        (srcCh > 0 ? " / 源 " + RoutingModel.DescribeSource(srcCh) : ""));
            }
            else if (!quiet)
            {
                Log("[提示] 路由热更新未生效（与当前链路的声道数不匹配），需停止后重新开始");
            }
        }

        /// <summary>链路形状是否一致: 路数、设备、每路声道数、延迟。</summary>
        static bool SameLinkShape(RoutingModel a, RoutingModel b)
        {
            var sa = a.EnabledSinks();
            var sb = b.EnabledSinks();
            if (sa.Count != sb.Count) return false;
            for (int i = 0; i < sa.Count; i++)
            {
                if (sa[i].DeviceId != sb[i].DeviceId) return false;
                if (sa[i].OutChannels != sb[i].OutChannels) return false;
                if (sa[i].DelayMs != sb[i].DelayMs) return false;
            }
            return true;
        }

        // 手动启动与自动重建共用同一条启动链 (差别只在失败处理: 自动重建不弹框、不自动回滚)
        void LaunchEngine(string expectCap, StartParams p, bool autoRestart)
        {
            startAbort = false;
            startFinished.Reset();
            ThreadPool.QueueUserWorkItem(delegate
            {
                AudioEngine eng = null;
                try
                {
                    if (startAbort) throw new OperationCanceledException("启动已取消");
                    // 设备全部写在 p.Model 里 (每台设备一路 sink), 引擎不再单独接收左右 ID
                    eng = new AudioEngine(Log, p.Mode, p.FixedPpm);
                    AudioEngine.StartOutcome outcome = eng.Start(p.Model, p.Gain, p.Master, p.PreMs, p.OutMs,
                        delegate { return startAbort; }, expectCap);

                    if (exiting || IsDisposed || !IsHandleCreated)
                    {   // 关窗等待期间完成 → 只释放引擎, 不碰UI
                        try { eng.Dispose(); } catch { }
                        return;
                    }
                    BeginInvoke(new Action(delegate
                    {
                        if (exiting) { try { eng.Dispose(); } catch { } return; }
                        engine = eng;
                        // 订阅放在赋值之后: 处理函数会按引用校验"还是不是当前引擎", 防止旧引擎的迟到事件触发二次重建
                        eng.Stalled += OnEngineStalledFromAudio;
                        runCaptureId = eng.CaptureId;
                        runModel = p.Model;
                        ResetWatchdog();
                        runSinceTick = Environment.TickCount;
                        SetAppState(AppState.Running);
                        ApplyVolumes();
                        Log("[启动] 端点绑定: 主=" + DevInfo(bindings.Master) + SinkSummary());
                        Log("[启动] 分流已启动 " + eng.SinkCount + " 路 (增益 " + p.GainPct + "%)" +
                            (outcome == AudioEngine.StartOutcome.NoAudioYet ? "，等待音频数据" : ""));
                        // 捕获格式只有启动后才知道: 拿到真实声道数就重建一次模型并热替换矩阵,
                        // 否则"按立体声建抽头"的假设会让真 5.1 源的中置/低音/环绕全部取错
                        ApplyRoutingHot("启动校源");
                        if (autoRestart)
                        {
                            restarting = false;
                            Log("[恢复] 自动重建完成，分流继续");
                        }
                    }));
                }
                catch (Exception ex)
                {
                    try { if (eng != null) eng.Dispose(); } catch { }
                    if (exiting || IsDisposed || !IsHandleCreated) return;
                    string reason = AudioErrors.Describe(ex);
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (exiting) return;
                            Log("[错误] 启动失败: " + reason);
                            if (autoRestart)
                            {
                                restarting = false;
                                GiveUp();   // 重建失败 → 别再假装运行, 明确停下来
                                Log("[错误] 自动重建失败，分流已停止；请检查设备后手动开始");
                                return;
                            }
                            StartFailed();
                            if (!(ex is OperationCanceledException))
                                MessageBox.Show("启动失败: " + reason, "错误");
                        }));
                    }
                    catch { }
                }
                finally { startFinished.Set(); }
            });
        }

        void StopEngine()
        {
            TearDownEngine();
            if (state == AppState.Running || state == AppState.Starting)
            {
                SetAppState(AppState.Stopped);
                Log("[停止] 分流已停止。");
            }
            UnbindAllVolumeSync();
            ClearRunState();
            expectCaptureId = null;
            warnedLostIds.Clear();
            autoRestartCount = 0;
            restarting = false;
            // 停止后还原系统默认输出: 不还原的话默认还挂在虚拟声卡上, 全系统无声
            // (退出路径有自己的 restoreDefaultId 还原, 这里跳过避免二次切换)
            if (!exiting) RestoreDefaultOutput("停止分流");
        }

        // 只拆引擎, 不动状态/绑定/设备ID (自动重建要沿用这些)
        void TearDownEngine()
        {
            var eng = engine;
            engine = null;
            if (eng == null) return;
            try { eng.Stop(); } catch { }
            try { eng.Dispose(); } catch { }
        }

        // 彻底放弃: 拆引擎 + 转失败态 + 解绑, 与 StopEngine 的区别是不打"用户主动停止"的日志
        void GiveUp()
        {
            TearDownEngine();
            SetAppState(AppState.Failed);
            UnbindAllVolumeSync();
            ClearRunState();
            expectCaptureId = null;
            warnedLostIds.Clear();
            autoRestartCount = 0;
            restarting = false;
            // 与 StopEngine 同理: 链路已死, 系统默认必须还给真实设备, 否则全系统无声
            if (!exiting) RestoreDefaultOutput("分流已中断");
        }

        // ---------------- 长期运行看门狗 ----------------

        void ResetWatchdog()
        {
            watchRendered = new long[0];
            watchRenderedTick = new int[0];
            watchImbalanceTick = new int[0];
            captureIdleLogged = false;
            captureIdleSince = 0;
            lastCaptureCheckTick = 0;
        }

        /// <summary>看门狗数组按当前路数扩容 (-1 = "还没取到第一个读数")。</summary>
        void EnsureWatchArrays(int n)
        {
            if (watchRendered.Length == n) return;
            watchRendered = new long[n];
            watchRenderedTick = new int[n];
            watchImbalanceTick = new int[n];
            for (int i = 0; i < n; i++) watchRendered[i] = -1;
        }

        static string SinkLabel(AudioEngine eng, int i)
        {
            var rt = eng == null ? null : eng.SinkAt(i);
            if (rt != null && rt.Spec != null && !string.IsNullOrEmpty(rt.Spec.Label)) return rt.Spec.Label;
            return "第" + (i + 1) + "路";
        }

        static string PpmText(double v)
        {
            return v.ToString("+0;-0;0") + "ppm";
        }

        /// <summary>
        /// 每 2 秒判定一次链路活性。核心判据是"输出线程有没有被调用"(RenderedSamples 是否增长),
        /// 而不是水位高低 —— 源静音时水位本来就该是 0, 用水位判会天天误报。
        /// 逐路判定: 总和会把某一路停摆掩盖掉 (其它路照常增长)。
        /// </summary>
        void WatchdogTick()
        {
            var eng = engine;
            if (!running || eng == null) return;
            int now = Environment.TickCount;

            // 连续健康 5 分钟 → 清空重建计数, 避免一天里偶发两三次后就被永久锁死在"不再重试"
            if (autoRestartCount > 0 && runSinceTick != 0 && unchecked(now - runSinceTick) >= 300000)
                autoRestartCount = 0;

            // 10 秒不被调用 → 该路输出端已停摆 (设备休眠/被独占/驱动挂起且没抛事件)
            int sinks = eng.SinkCount;
            EnsureWatchArrays(sinks);
            for (int i = 0; i < sinks; i++)
            {
                var rt = eng.SinkAt(i);
                if (rt == null || rt.Provider == null) continue;
                long r = rt.Provider.RenderedSamples;
                if (r != watchRendered[i]) { watchRendered[i] = r; watchRenderedTick[i] = now; }
                else if (watchRenderedTick[i] != 0 && unchecked(now - watchRenderedTick[i]) >= WatchdogFreezeMs)
                {
                    OnEngineStalled("输出「" + SinkLabel(eng, i) + "」线程已 " + (WatchdogFreezeMs / 1000) +
                        " 秒未取数据（水位 " + rt.Provider.BufferedMs + "ms）—— 该设备休眠/被独占的典型表现");
                    return;
                }
            }

            // 水位失衡检测: 设备实际采样率与标称偏差 10%+ 时, 远超校正环 ±0.5% 修正范围 →
            // 该路水位持续堆积到满。此时 RenderedSamples 仍在涨 (Read 还被调用, 只是消费速率
            // 不匹配), 停摆检测抓不到。判据: 调节水位 >300ms (正常 ≤142ms) 且该路校正触顶
            // (|ppm|>4900), 持续 10 秒。逐路判定: 总和会互相掩盖。
            for (int i = 0; i < sinks; i++)
            {
                var rt = eng.SinkAt(i);
                if (rt == null || rt.Provider == null) continue;
                double ppm = rt.Provider.RatioPpm;
                int reg = rt.Provider.RegulatedMs;
                bool clamped = ppm > 4900 || ppm < -4900;
                if (!clamped || reg <= ImbalanceThresholdMs) { watchImbalanceTick[i] = 0; continue; }
                if (watchImbalanceTick[i] == 0) { watchImbalanceTick[i] = now; continue; }
                if (unchecked(now - watchImbalanceTick[i]) < WatchdogImbalanceMs) continue;
                OnEngineStalled("「" + SinkLabel(eng, i) + "」水位持续堆积至 " + reg + "ms 且校正已触顶 " +
                    PpmText(ppm) + " 超过 10 秒——该设备消费速率严重低于捕获速率，校正环无法补偿");
                return;
            }

            int capTick = eng.LastCaptureTick;
            // 1.5s 阈值: 播放时回调约每 10ms 一次, 1.5s 无回调必为停流; 原 3s 会漏掉 2~3 秒的暂停
            bool captureActive = capTick != 0 && unchecked(now - capTick) < 1500;
            if (captureActive)
            {
                // 恢复补记: 此前若静默超过 2 秒, 记一条恢复事件 (检测粒度 2s, 时长为近似值)
                // —— 没有这条, "应用暂停→恢复"在日志里完全隐形, 会被误判成无故欠载
                if (captureIdleSince != 0 && unchecked(now - captureIdleSince) >= 2000)
                    Log("[状态] 捕获源恢复数据（此前静默约 " + (unchecked(now - captureIdleSince) / 1000) + " 秒）");
                captureIdleLogged = false;
                captureIdleSince = 0;
                lastCaptureCheckTick = 0;
                return;
            }

            // 捕获端没数据: 多数情况只是应用暂停/静音 (此时欠载计数会增长, 属预期而非故障), 只记一次
            // capTick==0 (从未收到数据) 只置位不打提示: 启动路径有自己的"等待音频数据"日志
            if (captureIdleSince == 0) captureIdleSince = now;
            if (!captureIdleLogged && unchecked(now - captureIdleSince) >= 2000)
            {
                captureIdleLogged = true;
                if (capTick != 0) Log("[状态] 捕获源暂无数据（应用暂停或静音），输出待命");
            }
            // 空闲期顺带确认捕获设备是否还在 (1 分钟一次): 设备被移除时引擎不会自杀, 只有这里能发现
            if (captureIdleLogged && (lastCaptureCheckTick == 0 || unchecked(now - lastCaptureCheckTick) >= 60000))
            {
                lastCaptureCheckTick = now;
                if (runCaptureId != null && FindMissingEndpoint(runCaptureId) != null)
                    OnEngineStalled("捕获设备已不可用");
            }
        }

        // 引擎在音频线程上报 → 回 UI 线程统一处理
        void OnEngineStalledFromAudio(AudioEngine src, string why)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    // 旧引擎的迟到事件不能触发二次重建
                    if (!ReferenceEquals(src, engine)) return;
                    OnEngineStalled(why);
                }));
            }
            catch { }
        }

        void OnEngineStalled(string why)
        {
            if (exiting || IsDisposed || restarting) return;
            if (state != AppState.Running && state != AppState.Starting) return;

            Log("[错误] " + why + " → 分流链路已中断，进入自动恢复流程");

            if (autoRestartCount >= MaxAutoRestart)
            {
                Log("[错误] 已连续自动重建 " + autoRestartCount + " 次仍未恢复，停止重试；请检查设备后手动开始");
                GiveUp();
                return;
            }
            autoRestartCount++;
            Log("[恢复] 自动重建输出流 (" + autoRestartCount + "/" + MaxAutoRestart + ")");
            RestartEngine();
        }

        /// <summary>按当前运行参数重建整条链路 (不断开音量绑定, 不重跑 AutoConfigure)。</summary>
        void RestartEngine()
        {
            if (exiting || IsDisposed) return;
            string capId = runCaptureId;

            if (string.IsNullOrEmpty(capId) || runSinkIds.Count == 0)
            {
                Log("[错误] 缺少运行期设备信息，无法自动重建；请手动重新开始");
                GiveUp();
                return;
            }
            string missing = FindMissingEndpoint(runSinkIds.ToArray());
            if (missing != null)
            {
                Log("[错误] 输出设备已不可用 (" + FindNameById(missing) + ")，放弃自动重建；请刷新设备后重新开始");
                GiveUp();
                return;
            }

            // 按当前 UI 重新翻译 (运行期卡片的结构控件是锁定的, 内容与上次启动一致;
            // 音量/静音可能被调过, 重建时一并带过去)
            StartParams p;
            try { p = SnapshotStartParams(); }
            catch (Exception ex)
            {
                Log("[错误] 重建参数生成失败: " + ex.Message);
                GiveUp();
                return;
            }

            restarting = true;
            TearDownEngine();                 // 起播水位/缓冲全部重来, 与首次启动一致
            SetAppState(AppState.Starting);   // 期间 running=false → 看门狗自然停手
            LaunchEngine(capId, p, true);
        }

        // 一次枚举校验多个端点是否仍活动; 返回第一个缺失的 ID, 全部在位返回 null。
        // 原实现每次只查一个 ID, 重建路径要枚举两遍; 枚举失败一律按"都在"处理, 避免误判触发重建。
        static string FindMissingEndpoint(params string[] ids)
        {
            if (ids == null || ids.Length == 0) return null;
            for (int i = 0; i < ids.Length; i++)
                if (string.IsNullOrEmpty(ids[i])) return ids[i] ?? "";   // 空 ID 与旧 IsEndpointActive 语义一致: 视为缺失

            var missing = new List<string>(ids);
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var coll = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    // MMDeviceCollection 本身无 Dispose; 索引器取出的 MMDevice 是新 RCW, 须逐个释放
                    for (int i = 0; i < coll.Count && missing.Count > 0; i++)
                    {
                        var d = coll[i];
                        try
                        {
                            string id = d.ID;
                            if (id != null) missing.Remove(id);
                        }
                        finally { try { d.Dispose(); } catch { } }
                    }
                }
            }
            catch { return null; }
            return missing.Count > 0 ? missing[0] : null;
        }

        void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (!running || exiting) return;
                    Log("[恢复] 系统已从休眠唤醒，等待设备就绪后重建音频流");
                    powerTimer.Stop();
                    powerTimer.Start();
                }));
            }
            catch { }
        }

        float CurrentMaster()
        {
            return chkMuteMaster.Checked ? 0f : volMaster.Value / 100f;
        }

        void ApplyVolumes()
        {
            if (engine == null) return;
            // 左右音量/静音走设备端点(已双向绑定)，主音量走采样层；
            // 其余各路音量已乘进混音矩阵 (LaneRouting 的绑定增益), 这里不用管
            engine.SetMaster(CurrentMaster());
        }

        /// <summary>清空"本次运行"的设备/模型记录 (停止、失败、放弃三条路径共用)。</summary>
        void ClearRunState()
        {
            runCaptureId = null;
            runSinkIds.Clear();
            runModel = null;
        }

        void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 用户点×且勾选「关闭时最小化到托盘」→ 藏到托盘, 分流继续 (关机等系统关闭不拦截)
            if (!exiting && chkMinimizeToTray.Checked && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            exiting = true; // 真正关闭: 阻止后台启动完成回调再碰UI

            // 后台启动进行中 → 请求中止并等它退出 (引擎等待循环每20ms检查一次中止位)
            if (!startFinished.IsSet)
            {
                startAbort = true;
                startFinished.Wait(2000); // 超时也不继续等: 后台回调自带 IsDisposed/exiting 保护
            }

            StopEngine();

            // 恢复默认输出
            try
            {
                if (restoreDefaultId != null && catalog != null)
                {
                    if (catalog.DefaultRenderId() != restoreDefaultId)
                    {
                        string err;
                        if (SystemDefaultPolicy.TrySetDefault(restoreDefaultId, out err))
                            Log("[恢复] 默认输出 → " + FindNameById(restoreDefaultId));
                        else
                            Log("[错误] 恢复默认输出失败: " + err);
                    }
                }
            }
            catch { }

            SaveSettings();
            FlushSettings(); // 退出前立即落最后一次 (debounce 不再等)
            if (waterTimer != null)
            {
                waterTimer.Stop();
                waterTimer.Dispose();
                waterTimer = null;
            }
            if (saveDebounce != null)
            {
                saveDebounce.Stop();
                saveDebounce.Dispose();
                saveDebounce = null;
            }
            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            if (watchTimer != null)
            {
                watchTimer.Stop();
                watchTimer.Dispose();
                watchTimer = null;
            }
            if (powerTimer != null)
            {
                powerTimer.Stop();
                powerTimer.Dispose();
                powerTimer = null;
            }
            if (activateEvent != null)
            {
                try { activateEvent.Close(); } catch { }
                activateEvent = null;
            }
            if (sessionLog != null)
            {
                try { sessionLog.Dispose(); } catch { }
                sessionLog = null;
            }
            // 注销默认输出监听 (须在枚举器释放前)
            if (defaultWatcher != null && catalog != null)
            {
                try { catalog.Enumerator.UnregisterEndpointNotificationCallback(defaultWatcher); } catch { }
                defaultWatcher = null;
            }
            // 释放端点绑定 + 设备目录 (引擎已停, 设备不再被引用)
            if (bindings != null)
            {
                try { bindings.Dispose(); } catch { }
                bindings = null;
            }
            if (catalog != null)
            {
                try { catalog.Dispose(); } catch { }
                catalog = null;
            }
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
        }

        delegate void LogDelegate(string msg);
        void Log(string msg)
        {
            // 卡片栅格的日志可能早于日志框建成 (或晚于它被释放) —— 那时只写文件
            if (txtLog == null || txtLog.IsDisposed)
            {
                if (sessionLog != null)
                    sessionLog.Write(DateTime.Now.ToString("HH:mm:ss.fff"), Infrastructure.SessionLog.Classify(msg), msg);
                return;
            }
            if (txtLog.InvokeRequired)
            {
                try { BeginInvoke(new LogDelegate(Log), msg); } catch { }
                return;
            }

            // 格式: HH:mm:ss.fff [LEVEL] [阶段] 内容 (毫秒便于排查启动时序)
            Infrastructure.LogLevel level = Infrastructure.SessionLog.Classify(msg);
            // 单次取时: UI 与文件同源, 否则同一行两处时间戳会差几毫秒, 排查时序时容易误判
            string stamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string line = stamp + " [" + Infrastructure.SessionLog.Tag(level) + "] " + msg;

            // UI: 单条 8192 截断 + 条目间空行 + 自动滚底
            string ui = line;
            if (ui.Length > 8192) ui = ui.Substring(0, 8192) + "\n...(日志过长已截断)";
            // 先看用户在不在底部: 正在往上翻历史时不要每次都把他拽回末尾
            // (ScrollToCaret 是整块重排 + 重画, 日志刷得快时也是主要开销之一)
            bool atBottom = LogAtBottom();
            txtLog.AppendText(ui + "\r\n\r\n");
            // 总量上限: 超 30 万字符截到 15 万, 防长时间运行文本无限增长 (文件不截断)
            int total = txtLog.TextLength;
            bool trimmed = false;
            if (total > 300000)
            {
                // 直接删除头部超量字符, 避免 txtLog.Text 整串复制+Substring 的两次大分配
                txtLog.Select(0, total - 150000);
                txtLog.SelectedText = "";
                trimmed = true;
            }
            if (atBottom || trimmed)
            {
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }

            // 文件持久化 (写原文, 不截断)
            if (sessionLog != null) sessionLog.Write(stamp, level, msg);
        }
    }
}
