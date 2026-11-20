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
    partial class MainForm : Form
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
        bool syncDictWarned;     // SyncSettingsDict 控件读取失败只提示一次
        int gridLogCols = -1;    // 上次记过日志的列数 (只在列数变化时打, 拖动窗口不刷屏)
        // 上次真正摆过的栅格参数: 完全一致时跳过重排 (缩放窗口时每帧都会被叫到好几次)
        int lastCardCols = -1, lastCardW = -1, lastRightGap = -1, lastCardCount = -1;
        Size lastLogPanelSize = Size.Empty;   // 日志面板上次的尺寸 (没变就不重摆日志框)
        int lastVolPanelW = -1;              // 音量行上次的面板宽度
        Size lastTopPanelSize = Size.Empty;   // 顶部三行 + 卡片区上次的面板尺寸 (宽或高变都要重排)
        int lastLogHeaderW = -1;             // 日志标题行上次的面板宽度
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
        // 顶部固定行需要用到的控件 (自适应重排时要改它们的坐标/宽度)
        Label lblD, lblLatency, lblSpec, lblWater, lblWaterIv, lblWaterSec, lblLogTitle;
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
        // F-03: 每代链路一个编号, 换引擎/拆引擎时递增。所有后台回调必须先比对编号再改 UI 状态,
        // 否则上一代迟到的单路重建结果会清掉新一代同下标的 sinkRestarting 标记。
        // 读写都走 Interlocked / Volatile, 故意不加 volatile 以免 Interlocked 触发 CS0420 警告。
        int engineGeneration;
        // 在途单路重建任务的完成信号 (Set = 没有在途任务); 关窗时等它们退出。
        readonly ManualResetEventSlim sinkRestartFinished = new ManualResetEventSlim(true);
        int sinkRestartInFlight;
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
        // 水位失衡判定 v2: 设备消费速率异常 (休眠抖动/驱动重置/端点僵死) 时 RenderedSamples
        // 仍在增长 (数据只是堆积), 停摆检测抓不到, 只有水位与校正暴露问题。三条判据:
        // ① 趋势: 校正触顶(|ppm|>9900) 持续 5 秒且水位比窗口起点净涨 ≥10ms —— 消费过慢的
        //    典型曲线。起播预缓冲放水阶段校正同样触顶, 但水位在下降 → 不会误触发。
        // ② 兜底: 调节水位超过目标水位 2.5 倍持续 10 秒 (正常 ≤1.3 倍), 不要求触顶。
        // ③ 削峰频发: provider 削峰 ≥8 次且每次间隔都在 60 秒内 —— 缓冲被反复顶到目标 2 倍。
        // 为什么要有 ③: USB/驱动的一次瞬间卡顿会把缓冲顶高 100~200ms, 而 ±1% 的校正
        // 每秒只能排 10ms, 5 秒内注定排不完。以前这种情况直接判"链路中断"→ 重建整条链路,
        // 两路一起静音约 1 秒, 用户听到的就是"总是突然没声"。现在 provider 先丢旧数据自愈,
        // 看门狗只在"自愈也压不住"时才动手。实测抖动多在 6~8 秒内自愈, 所以门限取 8 次
        // (持续不对时约 1.2 秒削一次 ≈ 10 秒), 只比自愈窗口多留一点余量。
        const int WatchdogImbalanceMs = 10000;  // 判据② 持续时长
        const int WatchdogTrendMs = 5000;       // 判据① 触顶持续时长
        const int ImbalanceGrowMs = 10;         // 判据① 窗口内最小净涨幅
        const int ClampDetectPpm = 9900;        // 触顶判定线 (随 ClockGroup.MaxRatio ±1% 联动, 留 1% 裕量)
        const int ImbalanceThresholdMs = 200;   // 判据② 水位兜底线 (下限; 实际按目标水位的 2.5 倍放宽)
        // 判据③: provider 自己削峰 (丢旧数据压回目标) 属于可自愈的瞬态 —— 单次不算故障。
        // 但 60 秒内反复削峰, 说明设备消费速率真的不够, 这时才值得拆链路重建。
        const int TrimBurstLimit = 8;           // 削峰次数上限 (持续不对时约 10 秒触达)
        const int TrimWindowMs = 60000;         // F-37: 判据③ 的真实滚动窗口跨度
        const int TrimGraceMs = 15000;          // 削峰后这段时间内不用"触顶+净涨"判死
        long[] watchTrims = new long[0];        // 判据③ 上次看到的削峰计数 (-1 = 未对齐)
        int[] watchTrimBurst = new int[0];      // 当前 60 秒窗口内削峰次数
        // F-37: 这是"窗口起点"而非"上次削峰时刻" —— 只有从起点算起超过 60 秒才滚动重置,
        // 否则 8 次分布在数分钟(相邻间隔都 <60s)也会被误判成"60 秒内 8 次"。
        int[] watchTrimLastTick = new int[0];   // 当前削峰窗口起点 (0 = 未开始)
        int[] watchImbalanceTick = new int[0];  // 判据① 首次触顶时刻
        int[] watchImbalanceReg = new int[0];   // 判据① 首次触顶时的水位
        int[] watchImbFloorTick = new int[0];   // 判据② 越线起始时刻

        /// <summary>
        /// F-37: 削峰滚动窗口判定 (纯函数, 供自检)。返回本次削峰后窗口内次数是否达到触发阈值。
        /// <paramref name="windowStart"/> 是窗口起点(0=未开始), 超时才整窗重置 —— 与"距上次削峰"
        /// 不同, 后者会让相隔数分钟但每次间隔都&lt;60s 的 8 次削峰误触发。unchecked 支持 TickCount 回绕。
        /// </summary>
        internal static bool BurstWindowTick(int now, ref int windowStart, ref int burst)
        {
            if (windowStart == 0 || unchecked(now - windowStart) > TrimWindowMs)
            {
                burst = 0;
                windowStart = now;   // 新窗口从本次削峰开始
            }
            burst++;
            return burst >= TrimBurstLimit;
        }
        int lastSinkProbeTick;                  // 输出端点存活探测 (30 秒一次)
        bool captureIdleLogged;      // "捕获源无数据"只提示一次
        int captureIdleSince;
        int lastCaptureCheckTick;    // 空闲期确认捕获设备是否还在 (1 分钟一次)
        int runSinceTick;            // 本次运行起点, 连续健康 5 分钟后清零重建计数
        // 自动重建: 设备真的没了时不能无限重启
        const int MaxAutoRestart = 3;
        int autoRestartCount;
        bool restarting;             // 重建进行中

        // 系统空闲待机抑制: 分流运行期间阻止 Modern Standby 挂起 USB 音频端点 (见 SetAppState)
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern uint SetThreadExecutionState(uint esFlags);
        const uint ES_CONTINUOUS = 0x80000000;
        const uint ES_SYSTEM_REQUIRED = 0x00000001;

        // 设备回归自动续播: 因端点消失放弃重建后, 每 5 秒探测一次, 设备全部回归即自动重新开始
        // (2026-11-18: 小米音箱 USB 链路瞬断 ~10 秒自行回归, 旧逻辑直接放弃要人工手点)
        System.Windows.Forms.Timer deviceReturnTimer;
        string returnCaptureId;
        System.Collections.Generic.List<string> returnSinkIds;

        // 单路端点掉线重试: 只等这一路回归 (其它路继续出声), 5 秒试一次
        System.Windows.Forms.Timer sinkRetryTimer;

        public MainForm()
        {
            Text = "Audio Splitter - 多路音频分流";
            // 图标: 取 exe 内嵌的 app.ico (标题栏/任务栏/托盘共用同一个)
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            // 窗体本身双缓冲: 缩放/拖动分隔条时背景不会先刷成白的再画控件 (整窗闪一下的来源)
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
            // 初始窗口尺寸; 不设 MinimumSize/MaximumSize, 长宽完全交给用户自由拖动
            Size = new Size(500, 500);
            Font = new Font("Microsoft YaHei UI", 9F);

            // 滚轮去向: 卡片列表正在滚的时候, 光标扫过音量条也只滚列表、不改音量
            // (拖着列表往下翻, 音量不能莫名其妙跳 5%); 列表没得滚时滚轮才调音量。
            Application.AddMessageFilter(new VolumeWheelRouter());
            // 点到空白处 (标签/面板/卡片底色) 时把焦点交回整个窗口, 别让它赖在最后那个输入框里
            Application.AddMessageFilter(new FocusParkRouter());

            // 上下可拖动面板: 内容区 | 6px灰分隔条 | 日志区 (参照 tk.PanedWindow)
            // FixedPanel=Panel2: 窗口变矮时先收缩上面的内容区, 日志区高度保持不变
            // (以前是 Panel1 固定 → 变矮时日志直接被压扁)。
            // Panel1MinSize=Panel2MinSize=0: **两个面板都不设下限**, 分隔条可以任意拖 ——
            // 日志区能一路拉到顶(只看内容)，也能一路压到底(只看日志)。
            // 窗口整体变矮的情况由 KeepContentOnShrink() 兜底, 不会把内容区缩没。
            splitContainer = new NoFocusSplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(0x88, 0x88, 0x88),
                FixedPanel = FixedPanel.Panel2,
                Panel1MinSize = 0,
                Panel2MinSize = 0,
                Size = new Size(500, 500),
                TabStop = false
            };
            // 显式恢复浅色面板, 阻断 BackColor(#888) 传播 → #888 只作用于分隔条
            splitContainer.Panel1.BackColor = SystemColors.Control;
            splitContainer.Panel2.BackColor = SystemColors.Control;
            // Panel1 自己不能滚: 否则窗口一缩, 整个内容区先冒出滚动条 (还带横向),
            // 而不是卡片区内部滚动。
            splitContainer.Panel1.AutoScroll = false;
            // 临时值, Load 里按客户区高度反算成"日志区 110 高"
            // (Panel2MinSize=0 之后 WinForms 不再帮着夹取, 硬编码值在 500 高窗口下会把日志区压到 69)
            splitContainer.SplitterDistance = 386;
            // 窗口整体变矮时把内容区拽回来 (分隔条拖动本身不受限, 见 KeepContentOnShrink)
            splitContainer.SizeChanged += (s, e) => KeepContentOnShrink();
            Controls.Add(splitContainer);

            lblD = new Label { Text = "捕获(虚拟):", Location = new Point(15, 15), AutoSize = true };
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
            lblSpec = new Label { Text = "规格:", Location = new Point(15, 189), AutoSize = true };
            cmbSpec = new NoWheelComboBox { Location = new Point(62, 185), Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbSpec.Items.AddRange(new object[] { "单声道", "立体声", "2.1", "四声道", "5.1" });
            cmbSpec.SelectedIndex = 1;
            flwSpecChannels = new FlowLayoutPanel
            {
                Location = new Point(166, 182),
                Size = new Size(310, 30),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = false
                // 宽度与右缘由 LayoutTopRows() 按面板实际宽度摆放 (Anchor 会按"构造时的负右距"把控件推出窗口)
            };
            btnAddOutput = new NoCueButton
            {
                Text = "添加输出",
                Location = new Point(482, 184),
                Width = 92,
                Height = 26
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
                // 宽度和高度都由 LayoutTopRows() 按面板实际尺寸算 (左 15 / 右 10 / 下 4)。
                // 不能用 Anchor.Right/Bottom: 初始 559x166 是按 600x600 窗口写的, 构造期父面板
                // 只有约 484x345, 算出的右边距/底边距是**负数**, 之后卡片区尺寸就锁死了,
                // 右边和下边都会切出可视区 (卡片右缘被切、最后一张卡被压掉一半)。
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };

            // 该下拉决定"预缓冲深度 + WASAPI 输出缓冲"整套组合, 直接影响稳态延迟, 不是启动耗时。
            // 选项里的总预估 = 捕获(约10ms) + 内部缓冲目标(driftTarget) + 输出缓冲, 不含 USB/DAC/声波;
            // 数字由 LatencyProfiles 现算, 改档位参数时文案自动跟随。
            lblLatency = new Label { Text = "延迟档位:", Location = new Point(15, 45), AutoSize = true };
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
            lblWater = new Label { Text = "水位监控:", Location = new Point(293, 8), AutoSize = true };
            lblWaterIv = new Label { Text = "间隔", Location = new Point(359, 8), AutoSize = true };
            // 间隔 0 = 关闭监控; 1 秒起, 不再卡 5 秒下限 (想看 1~4 秒的抖动就让人看)
            nudWaterInterval = new NoWheelNumeric { Location = new Point(397, 5), Width = 60, Minimum = 0, Maximum = 3600, Value = 30 };
            lblWaterSec = new Label { Text = "秒", Location = new Point(461, 8), AutoSize = true };

            lblLogTitle = new Label { Text = "日志", Location = new Point(15, 8), AutoSize = true, Font = new Font("Microsoft YaHei UI", 10F) };
            btnClearLog = new NoCueButton
            {
                Text = "清除日志",
                Location = new Point(489, 4),
                Width = 85,
                Height = 22
                // 右缘由 LayoutLogHeader() 按面板实际宽度摆放
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
            // 增益/主音量两行的滑条宽度跟面板宽度走 (窗口拉宽自动变长);
            // 顶部三行与日志标题行同理, 窗口变窄时右侧控件贴右缘, 不会越界截断
            splitContainer.Panel1.ClientSizeChanged += (s, e) => { LayoutTopRows(); LayoutVolumeRows(); };
            LayoutTopRows();
            LayoutVolumeRows();
            // 日志框按面板实时尺寸摆放 (拖分隔条/改窗口都要跟), 竖滚动条才会一直贴着右边
            splitContainer.Panel2.Resize += (s, e) => { LayoutLogPanel(); LayoutLogHeader(); };
            LayoutLogPanel();
            LayoutLogHeader();

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

            // 设备回归监听: 端点消失放弃重建后, 等设备全部回来再自动续播
            deviceReturnTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            deviceReturnTimer.Tick += (s, e) => DeviceReturnTick();

            // 单路端点掉线重试: 整条链路继续跑, 只反复尝试把掉线的那一路装回来
            sinkRetryTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            sinkRetryTimer.Tick += (s, e) => SinkRetryTick();

            Load += MainForm_Load;
            FormClosing += MainForm_FormClosing;
        }

        void SetAppState(AppState s)
        {
            state = s;

            // 分流运行期间阻止系统空闲待机 (Modern Standby / S0 低功耗):
            // 分流器在源静音时渲染的是静音, 系统不会因此阻止待机 → 整机挂起时 USB 音频端点
            // 一并挂起, 唤醒后端点病态 (水位堆积震荡/僵尸无声), 且 Modern Standby 的部分挂起
            // 不触发 PowerModes.Resume, "唤醒重建"逻辑抓不到。进程退出会自动清除该状态。
            if (s == AppState.Running) SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
            else SetThreadExecutionState(ES_CONTINUOUS);

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

            if (trayIcon != null) trayIcon.Text = "Audio Splitter - " + txt;

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
            btnAddOutput.Enabled = !busy && !run && models.Count < Infrastructure.LaneSettingsCodec.MaxOutputCards;  // F-24: 达上限禁用

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
                // 初始分割位置: 让日志区保持 110 高 (与去掉 Panel2MinSize 之前的默认观感一致)。
                // 现在两个面板都没有下限, WinForms 不会帮着夹取, 必须按真实客户区高度反算一次;
                // 之后完全由用户拖动决定, 不再有任何限制。
                splitContainer.SplitterDistance =
                    Math.Max(0, splitContainer.Height - 110 - splitContainer.SplitterWidth);
                // 高DPI下 AutoSize 宽度在构造后才最终确定 → Load 时强制重排一次顶部三行、卡片区与日志标题行
                // (重置缓存尺寸, 否则 LayoutTopRows/LayoutLogHeader 会以为尺寸没变而跳过)
                lastTopPanelSize = Size.Empty;
                lastLogHeaderW = -1;
                LayoutTopRows();
                LayoutLogHeader();
                // 日志框统一由 LayoutLogPanel() 按面板实际尺寸摆放 (以前这里还手工算一次高度,
                // 与 LayoutLogPanel 的边距不一致, 先设一个值又被覆盖)
                LayoutLogPanel();
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
            return catalog == null ? "" : catalog.NameOf(id);
        }

        // 端点绑定失效 (设备拔出/端点被拆) → 回退到混音增益: 该设备音量不再写端点,
        // 改由卡片音量乘进矩阵, 否则卡片滑条/静音全部失效 (端点对象已废, 写进去没反应)。
        // 不停止分流 (与原"仅警告"策略一致)。
        void OnBindingInvalidated(EndpointVolumeBinding b)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (IsDisposed) return;
                    SetVolumeBindVisual(false);
                    string devId = b == null ? null : b.DeviceId;
                    if (!string.IsNullOrEmpty(devId) && noEndpointVolumeDevices.Add(devId))
                    {
                        RebuildLanes();   // 该设备音量改走矩阵增益
                        if (running) ApplyRoutingHot("端点失效，回退混音增益");
                    }
                    Log("[警告] 「" + (b == null ? "?" : b.Role) + "」端点绑定已失效 (" +
                        (b == null ? "?" : b.DeviceName) +
                        ")，该设备音量已改由混音增益承载，请停止分流并刷新设备");
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
                {
                    int si = cards[i].cmbDevice.SelectedIndex;
                    if (si >= 0 && si < Devices.Count) usedIdx.Add(si);   // F-07: 占位项不算已选
                }

                int filled = 0;
                for (int i = 0; i < cards.Count && i < models.Count; i++)
                {
                    int si = cards[i].cmbDevice.SelectedIndex;
                    if (si >= 0 && si < Devices.Count) continue;   // F-07: 占位项(缺失设备)交给下面重选
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
        /// <summary>
        /// 端点写入失败 → 该设备音量改走混音增益 (F-06)。写失败通常由 binding 内部的 Fail() 顺带
        /// 触发 Invalidated → OnBindingInvalidated 已完成回退；这里再判一次返回值，覆盖
        /// "写入静默失败但事件没送到 UI" 的竞态，避免卡片音量/静音完全失效。
        /// </summary>
        void FallbackEndpointToMix(string deviceId, string what)
        {
            if (string.IsNullOrEmpty(deviceId) || !noEndpointVolumeDevices.Add(deviceId)) return;
            RebuildLanes();
            if (running) ApplyRoutingHot("端点" + what + "失败，回退混音增益");
            Log("[警告] " + DevNameFor(deviceId) + " 端点" + what + "失败，该设备音量已改由混音增益承载");
        }

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
                    if (b == null || !b.IsValid) { FallbackEndpointToMix(m.DeviceId, "未绑定"); parts.Add(role + "=(混音增益)"); continue; }
                    int vol = Math.Max(0, Math.Min(100, m.Volume));
                    bool volOk = b.WriteVolume(vol / 100f);
                    bool muteOk = !b.IsValid || b.ReadMuted() == m.Mute || b.WriteMute(m.Mute);
                    if (!volOk || !muteOk) { FallbackEndpointToMix(m.DeviceId, "写入"); parts.Add(role + "=" + vol + "%(混音增益)"); continue; }
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
                    int idx = catalog.IndexOf(models[ci].DeviceId);
                    if (idx < 0 && string.IsNullOrEmpty(models[ci].DeviceId) && devTexts.Count > 0)
                    {
                        // 仅"配置原本为空"才按旧版自动分配前两张卡 (明确的新建行为)
                        idx = ci < 2 ? ci : 0;
                        if (idx >= devTexts.Count) idx = 0;
                        models[ci].DeviceId = DeviceIdAt(idx);
                    }
                    else if (idx < 0)
                    {
                        // F-07: 保存的 ID 当前没枚举到 → 保留，不做破坏性替换
                        Log("[初始化] 输出 " + (ci + 1) + " 已保存的设备当前不可用: " + models[ci].DeviceId +
                            "（保留原配置，等该设备出现后刷新即可恢复）");
                    }
                    SetCardDeviceSelection(cards[ci], models[ci], devTexts);
                }
                // 刷新后设备列表变了 → 可能有两张卡片落到同一台设备, 合并掉。
                // 注意: 缺失设备的卡片保留了各自的原 ID, 不会落到同一台真实设备被误合并 (F-07)。
                if (cards.Count > 0 && NormalizeModels()) { RebuildCards(); SyncLanesFromCards(); }

                Log("[初始化] 发现 " + Devices.Count + " 个输出设备，默认输出: [" + defIdx + "] " + defName);
            }
            finally
            {
                loadingDevices = false;
            }
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
            // 单路重建状态随之作废: 引擎已拆, 在途重建会因 closed/引用不符自行退出
            sinkRestartCount = new int[0];
            sinkRestartTick = new int[0];
            sinkRestarting = new bool[0];
            DisarmSinkRetry();
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

            // F-03: 单路重建是线程池任务, 可能正卡在 RestartSink 的生命周期锁上。
            // TearDownEngine 已置取消标志并同步释放引擎, 这里再等所有任务真正退出,
            // 避免它们的回调在窗体销毁途中碰到已重置的 UI 状态。超时不阻塞关窗。
            if (!sinkRestartFinished.IsSet) sinkRestartFinished.Wait(2000);

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
            // 这一步失败 = 默认输出还挂在虚拟声卡上(整机无声), 必须留痕 (内层 TrySetDefault 的错误已有日志, 这里兜的是它之外的意外)
            catch (Exception ex) { Log("[错误] 恢复默认输出异常: " + ex.Message); }

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
