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
using StereoSplitter.Model;

namespace StereoSplitter
{
    partial class MainForm : Form
    {
        RoundedCombo cmbDefault, cmbSpec;
        NoWheelNumeric nudWaterInterval;
        RoundNumericShell nudWaterShell;    // 圆角外壳 (与卡片延迟框同一套)
        // 输出卡片: 每张卡片 = 一台设备 (声道 + 延迟 + 主音量 + 静音; 多声道时另有每行声道与音量)
        readonly List<OutputCardModel> models = new List<OutputCardModel>();   // UI/持久化的权威模型
        readonly List<OutputCardControl> cards = new List<OutputCardControl>();// 控件, 与 models 一一对应
        readonly List<OutputLane> lanes = new List<OutputLane>();              // 内核输入, 由 models 扁平化得到
        bool normalizing;        // 合并/行数校正期间屏蔽卡片事件重入
        FlowLayoutPanel cardHost;
        FlowLayoutPanel flwSpecChannels;
        RunNoteLabel lblRunState;   // 规格行的运行状态字 (启动中…/运行中/正在停止…/启动失败), 空闲态隐藏;
                                    // 超宽时占满空地滚动 (见 RunNoteLabel)
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
        System.Windows.Forms.Timer delayHotTimer; // 延迟热改的合并窗口 (数字框逐位输入只跳一次)
        System.Windows.Forms.Timer powerTimer;   // 唤醒后延迟重建 (端点恢复要几百毫秒)
        System.Windows.Forms.Timer refreshKickTimer;   // 刷新: 等图标转完一圈再开始同步枚举
        System.Windows.Forms.Timer runNoteTimer;       // 运行期事件插播状态字的保留时长 (到点回"运行中")
        /// <summary>
        /// 点击「刷新」到真正开始枚举的间隔 (ms) = 转一圈 + 40ms。
        /// <para><b>必须等一整圈转完</b>: 枚举实测要 0.5~1s 且是同步的, 期间消息泵停摆 ——
        /// 和动画并行的话, 图标会卡在半路, 恢复时那几帧已判超时直接归位, 用户只看到"闪一下"。
        /// 等它转完再枚举, 一圈就是完整、可预期的一圈。</para>
        /// </summary>
        const int RefreshKickMs = GearButton.SpinOnceMs + 40;
        VolumeSlider volMaster, volGain;
        Label lblVolMaster, lblVolGain;
        // 顶部固定行需要用到的控件 (自适应重排时要改它们的坐标/宽度)
        Label lblWater, lblWaterIv, lblWaterSec, lblLogTitle;
        StartButton btnStart;        // 自绘圆角 + 四态配色 + 动效 (换色渐变 / 过渡态转环 / 运行中呼吸点)
        GearButton btnRefresh;       // 刷新设备 (圆环箭头图标): 运行中置灰, 链路级
        ComboButtonShell captureShell;  // 捕获设备下拉 + 刷新按钮合成的一组 (一个圆角壳)
        RoundButton btnAddOutput;    // 「添加设备」文字按钮; 运行中置灰: 加卡片 = 加设备/改声道形状, 链路级
        Button btnClearLog;

        // ---- 设置页的入口与选项 ----
        // 这几个控件由主界面创建 (事件绑定 / ApplyUiLock / LoadSettings 读写全在这里), 但**不放进
        // 主面板**, 而是打开"更多"弹层时交给 SettingsFlyout 摆进去、关闭时搬回来 (见 SettingsFlyout)。
        // 声明成 internal 是为了让设置页能直接拿到它们, 不必再维护一份"控件清单"。
        internal Button btnCalibrate;      // 运行中可用: 校准期间临时静音分流, 不拆链路
        internal Label lblLatency;
        internal RoundedCombo cmbLatency;
        internal CheckBox chkMinimizeToTray;
        internal CheckBox chkDrift;
        internal CheckBox chkMuteCapture;
        // R-10: 三个滚轮/焦点路由器。原先直接 AddMessageFilter(new Xxx()) 丢弃引用,
        // 于是关窗时无从 Remove —— 过滤器链是进程级静态表, 会一直留着指向本窗体的委托。
        // 单实例进程退出即回收, 但"隐藏到托盘再真正退出"不该靠这个假设。
        readonly System.Collections.Generic.List<IMessageFilter> msgFilters =
            new System.Collections.Generic.List<IMessageFilter>();
        GearButton btnSettings;            // 标题栏里的齿轮 (在最小化左边)
        // 自绘标题栏的三个窗口键。系统标题栏已收掉 (见构造函数), 这三个是它唯一的替代品。
        CaptionButton btnMin, btnMax, btnClose;
        Icon titleBarIcon;                 // 标题栏左侧的小图标 (16px), 由窗体图标缩放而来
        /// <summary>标题栏高度 (设计像素)。实际高度用 <c>Padding.Top</c> —— 它已被 ScaleForm 缩放。</summary>
        const int TitleBarDesignHeight = 32;
        SettingsFlyout settingsFlyout;     // 打开中的"更多"弹层 (null = 未打开)
        ToolTip tipTop;                    // 顶部控件提示 (需要随状态刷新设置按钮的文案)
        Infrastructure.SessionLog sessionLog;
        SplitContainer splitContainer;
        TextBox txtLog;
        SpeakerButton btnMuteMaster;   // 主音量行的喇叭: 点一下 = 主静音 (原「静音」勾选框)
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

        // R-07: 状态与迁移表移到 src/Model/AppState.cs (StereoSplitter.Model) —— 状态机是纯逻辑,
        // 必须能被 --selftest 穷举; 放在 MainForm 的嵌套类型里, Infrastructure 层的自检够不到。
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

        // R-05: 端点声道数缓存。读一次要激活音频客户端 (COM, USB/蓝牙端点 10~100ms),
        // 而卡片事件会走"NormalizeModels → EnsureSlots → 每张多声道卡一次"。
        // 只缓存成功值 (>0); 失效点见 EndpointChannels 的注释。
        const int ChannelCacheMs = 30000;
        readonly Dictionary<string, int> chanCache = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, int> chanCacheTick = new Dictionary<string, int>(StringComparer.Ordinal);

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
        bool sinkProbeWarned;                   // 探测本身失败只提示一次 (R-13)
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

            // —— 自绘标题栏 ——
            // 系统标题栏是一条与内容区分色的横带 (深色主题下甚至是对比极强的黑条), 与下面浅灰的
            // 面板分成两截。用户要"融为一体": 于是把边框收掉、客户区盖满整窗, 标题栏自己画 ——
            // 底色与内容区同一支 SystemColors.Control, 不画分隔线, 看上去就是内容区往上多留了
            // 一条 32px 的顶边。
            // 窗口本身仍可拖动、可缩放、可最大化: 拖动与边缘缩放由 WndProc 的 WM_NCHITTEST 手工给出
            // (HTCAPTION / HTLEFT…), 最大化交给系统的 WindowState (窗口带了 WS_MAXIMIZEBOX)。
            FormBorderStyle = FormBorderStyle.None;
            // 用**设计像素**写, ScaleForm 的 Scale() 会再乘一次因子 —— 这里若也走 Dpi.S 会双倍。
            // 之后一律读 Padding.Top 当标题栏高度。
            Padding = new Padding(0, TitleBarDesignHeight, 0, 0);
            // 窗口尺寸变化时重画标题栏 (否则拉宽后标题栏右侧留一条旧底色)
            SetStyle(ControlStyles.ResizeRedraw, true);

            // 窗体本身双缓冲: 缩放/拖动分隔条时背景不会先刷成白的再画控件 (整窗闪一下的来源)
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;
            // DPI 适配: 设计基准 96 DPI。先关掉 WinForms 的自动缩放 (纯代码构建的窗体没有
            // AutoScaleDimensions 的"设计值"可依据, 实测它把两边的尺寸标注拉平后就不再缩放),
            // 改由 OnLoad 里显式 Scale(因子) 一次性放大构造期摆好的全部控件与字体。
            // 运行时重排 (LayoutXxx)、动态创建的卡片、自绘的线宽/圆角另由 Dpi.S() 换算。
            AutoScaleMode = AutoScaleMode.None;
            // 初始窗口尺寸; 不设 MinimumSize/MaximumSize, 长宽完全交给用户自由拖动
            Size = new Size(500, 500);
            Font = new Font("Microsoft YaHei UI", 9F);

            // 滚轮按鼠标位置派发: 系统会把 WM_MOUSEWHEEL 投给**焦点控件**, 于是"焦点留在卡片区、
            // 鼠标却在日志框上滚"时滚的是卡片区 (反之亦然) —— 表现为滚一个区域、另一个区域在动。
            // 这个过滤器只改写**跨面板**的目标; 必须加在 VolumeWheelRouter 之前
            // (MessageFilter 后加的先执行), 让滑条那套规则优先判断。
            AddMessageFilter(new WheelByCursorRouter());
            // 滚轮去向: 卡片列表正在滚的时候, 光标扫过音量条也只滚列表、不改音量
            // (拖着列表往下翻, 音量不能莫名其妙跳 5%); 列表没得滚时滚轮才调音量。
            AddMessageFilter(new VolumeWheelRouter());
            // 点到空白处 (标签/面板/卡片底色) 时把焦点交回整个窗口, 别让它赖在最后那个输入框里
            AddMessageFilter(new FocusParkRouter());

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
            // 分隔条位置变了, 左右那两截"补到边"的分隔线要跟着重画
            splitContainer.SplitterMoved += (s, e) => InvalidateSplitterBleed();
            Controls.Add(splitContainer);

            // 捕获行: 原来行首有「捕获:」标签, 已删 (用户要求) —— 这一行只有"选哪台设备被抓取"
            // 一件事, 下拉里的设备名自己说明是什么, 再挂一个标签是重复标注。
            // 下拉与刷新合成一组: 同一个圆角壳里左边选设备、右边重新枚举, 中间一条分隔线
            // (见 Controls.cs 的 ComboButtonShell)。壳的尺寸/位置由 LayoutTopRows 定。
            cmbDefault = new RoundedCombo
            {
                Width = 250,
                Height = 28,
                // 不设 DropDownWidth: 弹层宽度与本体一致 (设备名过长靠行内省略号收尾)
                ItemHeight = 24,
                MaxDropDownItems = 12
            };
            // 「刷新设备」: 圆环箭头图标按钮 (用户指定图形), 无边框 —— 与设置齿轮同一套。
            // 点一下重新枚举设备 (插拔 USB/DAC 后用); 点开下拉不再自动刷新 (用户要求撤掉)。
            // 点击后图标转满一圈 (StartSpin): 枚举是同步跑的, 没有这个反馈就像点了没反应。
            btnRefresh = new GearButton
            {
                Glyph = ButtonGlyph.Refresh,
                Size = new Size(24, 24)
            };
            btnRefresh.Click += (s2, e2) => OnRefreshClick();
            captureShell = new ComboButtonShell(cmbDefault, btnRefresh);
            // 「更多」入口: 原设置齿轮换成三条横线图标 (用户指定), 点开弹出下拉式弹层,
            // 原设置页的选项全部移入弹层 (见 SettingsFlyout)。放在最小化左边。
            // GlyphScale 不再参与 (More 图形自带标题栏尺寸), 字段保留给螺母等其它实例用。
            btnSettings = new GearButton
            {
                Glyph = ButtonGlyph.More,
                Size = new Size(26, 26),
                TabStop = true
            };
            btnSettings.Click += (s2, e2) => OpenSettings();

            // 自绘标题栏的三个窗口键: 自右向左 [×][□][—] | [☰], 「更多」在最小化左边。
            // (系统标题栏里塞不进自定义控件, 所以这一组只能自己画)
            btnMin = new CaptionButton(CaptionGlyph.Minimize) { AccessibleName = "最小化" };
            btnMin.Click += (s2, e2) => WindowState = FormWindowState.Minimized;
            btnMax = new CaptionButton(CaptionGlyph.Maximize) { AccessibleName = "最大化" };
            btnMax.Click += (s2, e2) => ToggleMaximize();
            btnClose = new CaptionButton(CaptionGlyph.Close) { AccessibleName = "关闭" };
            btnClose.Click += (s2, e2) => Close();

            // 消音 / 缓冲档位 / 时钟校正 / 关闭行为 / 延迟校准 都已移入设置页:
            // 控件照旧在这里创建 (事件与持久化路径全留在 MainForm), 但不加入主面板,
            // 由 SettingsFlyout 打开时搬入、关闭时搬回。位置只是占位, 搬入时会被重设。
            chkMuteCapture = new NoCueCheckBox { Text = "消音", AutoSize = true };
            lblLatency = new Label { Text = "延迟档位:", AutoSize = true };
            cmbLatency = new RoundedCombo { Width = 200, Height = 24, ItemHeight = 24, MaxDropDownItems = 8 };
            chkMinimizeToTray = new NoCueCheckBox { Text = "关闭时最小化到托盘", AutoSize = true };
            chkDrift = new NoCueCheckBox { Text = "校正", AutoSize = true, Checked = true };
            btnCalibrate = new NoCueButton { Text = "延迟校准", Width = 76, Height = 26 };

            // 增益独占一行 (主音量上方)。自绘滑条轨道垂直居中, 图标 Top 比滑条低 1px 时
            // 中心正好落在轨道线上。滑条宽度不写死, 由 LayoutVolumeRows() 按面板宽度自适应
            // (和卡片里的音量条一样, 窗口拉宽就跟着变长)。
            // 行首用「推子」图标顶掉「增益:」文字: 与下面一行的喇叭看成同一列图标, 两条滑条
            // 靠"上面那个是推子、下面是喇叭"区分。增益没有开关语义, 所以这个图标是静态的。
            var icoGain = new GainGlyph { Location = new Point(15, 51) };
            volGain = new VolumeSlider { Location = new Point(78, 52), Width = 300, Minimum = 0, Maximum = 300, Value = 100, SmallChange = 5, LargeChange = 5 };
            lblVolGain = new Label { Text = "100%", AutoSize = false, Width = 46, Height = 20, TextAlign = ContentAlignment.MiddleLeft };

            // 主音量独占一行 (增益下方)。SmallChange/LargeChange=5: 点击/方向键/滚轮每次 5%, 与输出卡片一致
            // 行首放喇叭图标而不是「主音量:」文字: 图标本身就是静音开关, 原来右侧那个
            // 独立的「静音」勾选框因此删掉 (点喇叭 = 勾/取消), 滑条右端多出 ~66px。
            btnMuteMaster = new SpeakerButton { Location = new Point(15, 85) };
            volMaster = new VolumeSlider { Location = new Point(78, 86), Width = 300, Minimum = 0, Maximum = 100, Value = 100, SmallChange = 5, LargeChange = 5 };
            lblVolMaster = new Label { Text = "100%", AutoSize = false, Width = 46, Height = 20, TextAlign = ContentAlignment.MiddleLeft };

            // 主操作 + 规格行。
            // 「规格:」标签已删 —— 下拉里显示的就是规格, 再挂一个"规格:"是重复标注; 空出来的
            // 横向空间让给右对齐的下拉。
            // 行序 (上面还有捕获行 y6~32、增益 y52、主音量 y86 三行, 位置由各自的块硬编码):
            //   y=6:   [捕获下拉|刷新] …… [开始分流]   同一行 (用户指定):
            //          主按钮贴右缘, 壳吃掉它左边的剩余宽度; 两者同高同顶边, 对齐由 LayoutTopRows 定
            //   y=114: [规格下拉]   ……   [声道标注]   ……   [＋]
            //          三者同一行: 规格贴左 (当行首读), 加号贴右, 标注在两者之间居中
            //          (标注居中范围由 CenterSpecBadges 按两侧控件的位置收窄)
            //   y=148: 卡片区 (比主按钮独占一行时高出一截 —— 那一行让给了捕获行)
            //
            // 规格下拉与加号同为 76×30 圆角 6。宽度 76 不是随手取的: 下拉本体文字区 =
            // 宽 - 8 (左内缩) - 22 (箭头区), 而「立体声」在 9pt 下要 44px —— 60 宽只剩 30px,
            // 三个字会全被省略成"立…"。44 + 8 + 22 = 74 是下限, 取 76 留两像素。
            // 改小之前先跑 `uidump`, 看 COMBO[...] 行里的 selW / textRoom。
            // 主按钮走图标样式 (用户指定): 与刷新齿轮同一套无边框图标交互, 图形随状态切换
            // 并带动效 (见 StartButton) —— ▶ 可以开始 / 转环进行中 / ■+呼吸光环 运行中。
            // 本体 30 设计像素, 比行高 (26) 大一圈, LayoutTopRows 里垂直居中于行;
            // 语义由悬停提示与日志承载。
            btnStart = new StartButton
            {
                Text = "开始分流",
                Location = new Point(500, 4),
                Size = new Size(30, 30),
                TabStop = true
            };
            cmbSpec = new RoundedCombo
            {
                Location = new Point(15, 114),
                Width = 76,
                Height = 30,
                ItemHeight = 24,
                MaxDropDownItems = 6
            };
            cmbSpec.Items.AddRange(new object[] { "单声道", "立体声", "2.1", "四声道", "5.1" });
            cmbSpec.SelectedIndex = 1;
            // 运行状态字 (用户指定: 开始分流后状态显示在声道标注那一行): 规格下拉右侧,
            // 只有链路在动作 (启动中/运行中/正在停止/启动失败) 时才出现 —— 按钮图标化了,
            // "现在是什么状态"这半句话由这行字补上, 颜色与按钮图形同一支 (见 UpdateRunStateLabel)。
            // 自绘控件 (RunNoteLabel): 放得下居中, 放不下占满空地滚动 —— Label 只有截断。
            // 位置由 LayoutTopRows 重算 (跟规格下拉的右缘走), 标注的居中范围也让给它 (CenterSpecBadges)。
            lblRunState = new RunNoteLabel { Text = "运行中" };
            // 「添加设备」: 文字按钮, 大小与规格下拉一致 (76×30), 同一套描边样式 ——
            // 点开后选设备/负责声道再建卡片 (卡片构造即带默认选择), 按钮本身只负责加。
            // 贴右缘, 具体 Left 由 LayoutTopRows 按面板宽度重算 (这里只是构造期初值)。
            btnAddOutput = new RoundButton
            {
                Text = "添加设备",
                Location = new Point(482, 114),
                Size = new Size(cmbSpec.Width, cmbSpec.Height),
                Outline = true,
                FillColor = Color.White,
                BorderColor = OutputCardControl.BorderColor,     // 与下拉/延迟/卡片同一套浅灰框
                TextColor = Color.FromArgb(64, 64, 64)
            };

            // 声道名 + 输出路数上标: 与「规格」「＋」同一行, 在两者之间居中
            // (宽度由 CenterSpecBadges 按内容算; 高度必须容得下 Label 的字框 + 上边距 ——
            //  原来给 20 就比 9pt 的字框(17) 加 Margin.Top(5) 小, 文字底部被裁一条)
            flwSpecChannels = new FlowLayoutPanel
            {
                Location = new Point(166, 116),
                Size = new Size(310, 26),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = false
            };

            // 输出卡片区 (可增删, 内容超出时只有纵向滚动)
            // 宽度顶到面板右缘 (与「添加输出」右边缘对齐), 竖滚动条才会贴着右边显示。
            // 布局: 从左到右铺卡片, 铺满换行 —— 列数按「卡片基准宽度」的整数倍跳档
            // (见 ComputeCardGrid), 所以窗口变宽是"多一列", 而不是把单张卡片拉成一根长条。
            cardHost = new BufferedFlowPanel
            {
                Location = new Point(15, 148),
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
            // 控件本身摆在"更多"弹层 (见 SettingsFlyout), 这里只把选项填进去。
            string[] latNames = { "标准", "低延迟", "极低", "超低", "极限" };
            string[] latWarn = { "", "", ", 易断音", ", 易断音", ", 频繁断音" };
            for (int i = 0; i < LatencyProfiles.Length; i++)
                cmbLatency.Items.Add(latNames[i] + " (总预估" +
                    AudioEngine.EstimatedTotalMs(LatencyProfiles[i][0], LatencyProfiles[i][1]) + "ms" + latWarn[i] + ")");
            cmbLatency.SelectedIndex = 0;

            // 卡片宽度跟随容器可视宽度。AutoScroll 的竖滚动条出现/消失只改
            // ClientSize 不改 Size (不触发 Resize), 所以两个事件都要挂:
            // 否则卡片的宽度会取决于创建那一刻滚动条在不在 (差 17px 对不齐)。
            cardHost.Resize += (s2, e2) => FitCardWidths();
            cardHost.ClientSizeChanged += (s2, e2) => FitCardWidths();
            btnAddOutput.Click += (s2, e2) => AddOutputViaDialog();
            btnCalibrate.Click += (s2, e2) => OpenLatencyCalibration();
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
            nudWaterShell = new RoundNumericShell(nudWaterInterval) { Location = new Point(397, 5) };
            lblWaterSec = new Label { Text = "秒", Location = new Point(461, 8), AutoSize = true };

            lblLogTitle = new Label { Text = "日志", Location = new Point(15, 8), AutoSize = true, Font = new Font("Microsoft YaHei UI", 10F) };
            // 「清除日志」: 原先用系统 Button (系统灰边 + 系统圆角), 与旁边的下拉/添加设备
            // 并排时框色明显不是一套; 换成自绘描边按钮, 同一套浅灰框 (与输出卡片一致)。
            btnClearLog = new RoundButton
            {
                Text = "清除日志",
                Location = new Point(489, 4),
                Width = 85,
                Height = 22,
                Outline = true,
                FillColor = Color.White,
                BorderColor = OutputCardControl.BorderColor,
                TextColor = Color.FromArgb(64, 64, 64)
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

            // 主面板只放高频控件; 缓冲档位 / 时钟校正 / 捕获消音 / 关闭行为 / 延迟校准
            // 都在"更多"弹层里 (控件由本类持有, 由 SettingsFlyout 搬入搬出)。
            splitContainer.Panel1.Controls.AddRange(new Control[] { captureShell, btnMuteMaster, volMaster, lblVolMaster, icoGain, volGain, lblVolGain, btnStart, cmbSpec, lblRunState, flwSpecChannels, btnAddOutput, cardHost });
            splitContainer.Panel2.Controls.AddRange(new Control[] { lblLogTitle, lblWater, lblWaterIv, nudWaterShell, lblWaterSec, btnClearLog, txtLog });

            // 提示文案统一说明"运行中能不能改", 置灰时不至于让人以为坏了。
            // 必须放在所有控件都建好之后（水位监控那几个是后面才 new 的）。
            // 已迁入设置页的选项由设置页自己的说明文字负责解释, 这里不再挂提示。
            tipTop = new ToolTip();
            tipTop.SetToolTip(cmbDefault, "把哪台虚拟声卡的输出抓过来。\n运行中不可改（要重开捕获端点），需先停止分流。");
            tipTop.SetToolTip(btnRefresh, "重新枚举输出设备（插拔 USB/DAC 后用）。\n运行中不可刷新（会改动卡片与链路），需先停止分流。");
            tipTop.SetToolTip(btnStart, "开始分流（运行中点击 = 停止）。\n图形即状态: ▶ 可以开始 · 转环 进行中 · ■ 呼吸 运行中。");
            tipTop.SetToolTip(cmbSpec, "可用的逻辑声道清单（单声道…5.1）。\n运行中不可改（会改变矩阵与每路声道数），需先停止分流。");
            tipTop.SetToolTip(btnAddOutput, "新增一张输出卡片（一台设备）：先选设备和负责声道，确认后添加。\n运行中不可加卡（会改变链路），需先停止分流。");
            tipTop.SetToolTip(icoGain, "总增益（100% = 原样，>100% 放大）。\n作用在采样层，与「主音量」是两条独立的滑条。\n运行中可调。");
            tipTop.SetToolTip(volGain, "总增益，作用在采样层。\n运行中可调。");
            tipTop.SetToolTip(volMaster, "总音量，作用在采样层。\n运行中可调。");
            tipTop.SetToolTip(btnMuteMaster, "点击静音 / 取消静音（总静音，作用在采样层，不写设备端点）。\n音量拖到 0 会自动静音，拖回去自动恢复。\n运行中可调。");
            tipTop.SetToolTip(nudWaterInterval, "水位监控的刷新间隔（秒，1~3600；0 = 关闭监控）。\n随时可改，回车提交。");
            RefreshSettingsTip();
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
            btnClearLog.Click += (s, e) =>
            {
                txtLog.Clear();
                if (sessionLog != null) sessionLog.Clear();
            };
            chkMinimizeToTray.CheckedChanged += (s, e) => { SaveSettings(); RefreshSettingsTip(); };
            chkDrift.CheckedChanged += (s, e) =>
            {
                driftMode = chkDrift.Checked ? 1 : 0;
                if (loadingSettings) return;
                SaveSettings();
                RefreshSettingsTip();   // 齿轮提示里要跟着显示"自动时钟校正: 开/关"
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
            // 这两项的状态只反映在齿轮提示里, 改完顺手刷新 (挂载期间 loadingSettings 为真, 不会刷)
            cmbLatency.SelectedIndexChanged += (s, e) =>
            {
                if (loadingSettings) return;
                SaveSettings();
                RefreshSettingsTip();
            };
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
                // 音量到0自动静音; 拖离0取消 (捕获消音锁定不受拖动影响)
                if (btnMuteMaster.Muted != (volMaster.Value == 0)) btnMuteMaster.Muted = volMaster.Value == 0;
                // 双向同步: 写入当前默认输出设备的端点音量 (不改捕获消音状态)
                if (!volSyncing && bindings != null && bindings.Master != null)
                    bindings.Master.WriteVolume(volMaster.Value / 100f);
                SaveSettings();
            };
            // 图标三态之一 (音量 0 = 裸喇叭) 跟着滑条走; ValueChanged (含程序赋值) 比
            // Scroll (仅用户拖动) 全 —— 设置回填 / 设备同步改音量时图标同样要变
            volMaster.ValueChanged += (s, e) => btnMuteMaster.VolumeValue = volMaster.Value;
            btnMuteMaster.MutedChanged += (s, e) =>
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
            RefreshSettingsTip();   // 读盘后把"缓冲档位/校正状态"写进齿轮提示

            // 会话日志文件: 每会话一个 session 文件, UI + 文件双写
            // R-04: 目录由 AppPaths 决定 (便携优先, exe 目录不可写则退 LOCALAPPDATA)
            try { sessionLog = new Infrastructure.SessionLog(Infrastructure.AppPaths.LogDir); }
            catch { sessionLog = null; }

            // 落位说明: 便携模式下为空 (老行为, 不刷日志); 发生回退/迁移时提示一次,
            // 并明确写出 ini 与 log 的真实位置 —— 否则用户找不到自己的配置,
            // 也说不清"为什么设置没保住"。
            string pathNote = Infrastructure.AppPaths.ResolveNote;
            if (!string.IsNullOrEmpty(pathNote)) Log("[提示] " + pathNote);
            Log("[环境] 配置: " + IniPath + " / 日志: " + Infrastructure.AppPaths.LogDir);
            // R-04(b): 同目录 NAudio dll 与内嵌副本不一致 → 明确告警 (CLR 会优先用同目录那份)
            string dllNote = Program.AudioDllWarning;
            if (!string.IsNullOrEmpty(dllNote)) Log("[警告] " + dllNote);

            waterTimer = new System.Windows.Forms.Timer
            {
                Interval = Math.Max(1, (int)nudWaterInterval.Value) * 1000
            };
            waterTimer.Tick += (s, e) =>
            {
                if (running && engine != null && (int)nudWaterInterval.Value > 0) Log("[状态] 缓冲水位 " + engine.BufferStatus());
            };
            // 初始不启动: Ready 状态下不跑, 由 SetAppState 按状态启停

            // 运行期事件在规格行状态字上的保留时长: 到点回到"运行中" (见 ShowRunStateNote)。
            // 6s: 水位监控默认 30s 一条, 事件类 (捕获源静默/恢复) 都是偶发 —— 足够读完。
            runNoteTimer = new System.Windows.Forms.Timer { Interval = 6000 };
            runNoteTimer.Tick += (s, e) =>
            {
                runNoteTimer.Stop();
                // 只有仍然运行中才回"运行中": 期间状态若已迁移 (停止/失败), UpdateRunStateLabel 自己会摆
                if (state == AppState.Running) UpdateRunStateLabel(AppState.Running);
            };

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

            // 延迟热改的合并窗口: 第一下立即生效 (点箭头要马上听到), 窗口内后续改动攒到
            // 到点用最终值再应用一次 —— 数字框每敲一位都会 ValueChanged, 逐位跳会连成一串"咔"。
            delayHotTimer = new System.Windows.Forms.Timer { Interval = 180 };
            delayHotTimer.Tick += (s, e) =>
            {
                delayHotTimer.Stop();
                // 值已经和链路一致就不必再跑一遍 (前沿那次已经应用过); 这样只点一下箭头
                // 只产生一条日志, 不会多出一条"路由已热更新"。
                if (running && DelayDiffersFromRunning()) ApplyRoutingHot("延迟", false);
            };

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

            // —— 标题栏的四个键挂在窗体上 (不在任何面板里): 它们的坐标系是窗体客户区 ——
            // 最后加 = z-order 最上, 不会被 Dock Fill 的 splitContainer 盖住。
            Controls.AddRange(new Control[] { btnMin, btnMax, btnClose, btnSettings });

            // 标题栏左侧的小图标: 由窗体图标缩到 16 设计像素 (DrawIcon 不缩放, 得自己生成一个尺寸)
            try
            {
                if (Icon != null) titleBarIcon = new Icon(Icon, Dpi.S(16), Dpi.S(16));
            }
            catch { }

            Load += MainForm_Load;
            FormClosing += MainForm_FormClosing;
        }

        /// <summary>
        /// DPI 放大: 构造期所有控件都按 96 DPI 的设计值摆好, 这里一次性乘上系统缩放系数
        /// (125% → 1.25), 之后的首次布局就按放大后的尺寸算。
        /// <para><b>为什么不用 AutoScaleMode.Dpi</b>: 纯代码构建的窗体没有设计器写入的
        /// AutoScaleDimensions, WinForms 首次布局时会把"设计尺寸"与"当前 DPI"标成同一个值,
        /// 结果一像素都不缩放 (实测: AutoScaleDimensions 被改成 120, 控件仍是 96 的像素值)。</para>
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            Dpi.ScaleForm(this);
            base.OnLoad(e);
            // 缩放之后才知道标题栏的实际高度与按钮尺寸 —— 位置按当前客户区排一次
            // 顺便把最外一圈让给窗体自己 (见下), 两件事都要用缩放后的数值。
            ReserveResizeBorder();
            LayoutCaption();
            SyncCaptionState();
        }

        /// <summary>缩放边框的宽度 (设计像素)。与 <see cref="HitTestCaption"/> 里的 grip 同一支数。</summary>
        const int ResizeBorderDesign = 6;

        /// <summary>
        /// 把窗口最外一圈让给窗体自己: 拖边缘缩放、Aero Snap、缩放光标全靠它。
        ///
        /// <para><b>为什么必须让出来</b>: 鼠标落在哪个窗口上是由系统按"最深的那层子窗口"判定的。
        /// 内容区 (SplitContainer) 一旦铺满客户区, 边缘那几像素上就是它在接鼠标 —— 它回 HTCLIENT,
        /// 点击便落在内容上, 窗体那套 HitTestCaption 从头到尾没被问过一次 (实测: 边缘按下滑 80px,
        /// 宽度一个像素都不动; 内容内缩 8px 后同一位置立刻拉宽 77px)。</para>
        ///
        /// <para>所以 Padding 不只留标题栏, 左/右/下各再让出一圈。这圈底色与两个面板同为
        /// SystemColors.Control, 看不出接缝; 只有横贯的分隔条会短一截, 由
        /// <see cref="PaintSplitterBleed"/> 补到边。</para>
        /// </summary>
        void ReserveResizeBorder()
        {
            int grip = Dpi.S(ResizeBorderDesign);
            Padding = new Padding(grip, Padding.Top, grip, grip);
        }

        /// <summary>
        /// 把 6px 灰分隔条补进左右那一圈让出去的空隙, 让分隔线仍然通到窗口边缘。
        /// <para>颜色取容器自己的 BackColor, 与它画的分隔条是同一支; 位置取 Panel1 的下缘,
        /// 所以用户上下拖分隔条时补丁跟着走 (SplitterMoved 里重画)。</para>
        /// </summary>
        void PaintSplitterBleed(Graphics g)
        {
            if (splitContainer == null) return;
            int grip = Padding.Left;
            if (grip <= 0) return;

            int y = splitContainer.Top + splitContainer.Panel1.Bottom;
            int h = splitContainer.SplitterWidth;
            using (var b = new SolidBrush(splitContainer.BackColor))
            {
                g.FillRectangle(b, 0, y, grip, h);
                g.FillRectangle(b, splitContainer.Right, y, grip, h);
            }
        }

        void InvalidateSplitterBleed()
        {
            if (splitContainer == null || !IsHandleCreated) return;
            int grip = Padding.Left;
            if (grip <= 0) return;
            Invalidate(new Rectangle(0, 0, grip, ClientSize.Height));
            Invalidate(new Rectangle(ClientSize.Width - grip, 0, grip, ClientSize.Height));
        }

        // ================= 自绘标题栏 =================
        //
        // 系统标题栏已收掉 (FormBorderStyle.None + WM_NCCALCSIZE 返回 0), 下面这组方法接管了它:
        // 画底与标题、摆四个键、把"拖窗口 / 双击最大化 / 边缘缩放"还给系统。

        /// <summary>
        /// 按客户区宽度摆标题栏的键, 自右向左: [×] [□] [—] | [☰]。
        /// <para>「更多」图标 (原设置齿轮, 用户指定替换) 在最小化**左边**, 它与窗口键组之间
        /// 画一条竖线分隔 (用户指定) —— 功能键与系统键是两类东西。系统标题栏里塞不进
        /// 自定义控件, 这也是必须自绘标题栏的原因之一。</para>
        /// </summary>
        void LayoutCaption()
        {
            if (btnClose == null || btnMin == null || btnMax == null || btnSettings == null) return;

            int h = Padding.Top;                 // 已缩放过的真实高度
            if (h <= 0) return;
            int w = ClientSize.Width;
            int bw = Dpi.S(46);                  // 与系统窗口键同宽 (46@96dpi)
            int sw = Dpi.S(26);                  // 「更多」图标
            int gap = Dpi.S(14);                 // 图标与最小化之间的缝, 正中画竖线分隔 (原 6px 无分隔线)

            // 整组从右往左排, 左端用 max(0) 兜底: 窗口被拖到比这组键还窄时, 键退成贴左,
            // 至少图标与最小化还在窗内 (拖动、缩放、关闭都还能用), 而不是整组飘到窗口外。
            int need = sw + gap + bw * 3;
            int left = Math.Max(0, w - need);

            btnSettings.SetBounds(left, (h - sw) / 2, sw, sw);
            btnMin.SetBounds(left + sw + gap, 0, bw, h);
            btnMax.SetBounds(left + sw + gap + bw, 0, bw, h);
            btnClose.SetBounds(left + sw + gap + bw * 2, 0, bw, h);
        }

        /// <summary>画标题栏: 底色与内容区同色、无分隔线, 左边图标 + 标题文字。</summary>
        void PaintCaption(Graphics g)
        {
            int h = Padding.Top;
            if (h <= 0) return;

            // 用窗体自己的底色 (默认 SystemColors.Control, 与两个面板一致): 系统换主题/高对比度时
            // 标题栏与内容区仍是一块颜色, 不会一边 Control 一边 Window。
            using (var b = new SolidBrush(BackColor))
                g.FillRectangle(b, 0, 0, ClientSize.Width, h);

            int x = Dpi.S(12);
            if (titleBarIcon != null)
            {
                int isz = Dpi.S(16);
                g.DrawIcon(titleBarIcon, new Rectangle(x, (h - isz) / 2, isz, isz));
                x += isz + Dpi.S(8);
            }

            // 文字止于最左边的那个键 (更多图标) 之前: 窗口拉窄时标题先省略, 不会钻到按钮底下
            int limit = btnSettings != null ? btnSettings.Left - Dpi.S(8) : ClientSize.Width - Dpi.S(8);
            var rect = new Rectangle(x, 0, Math.Max(0, limit - x), h);
            TextRenderer.DrawText(g, Text, Font, rect, Color.FromArgb(32, 32, 32),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // 「更多」图标与窗口键组之间的竖线分隔 (用户指定): 画在两者之间那道缝的正中。
            // 高度取标题栏的约四成, 上下都不顶到边 —— 是"分隔符"不是"分栏线"。
            if (btnSettings != null && btnMin != null && btnSettings.Right < btnMin.Left)
            {
                int dx = (btnSettings.Right + btnMin.Left) / 2;
                int dh = Dpi.S(14);
                using (var pen = new Pen(Color.FromArgb(203, 203, 200), 1f))
                    g.DrawLine(pen, dx, (h - dh) / 2, dx, (h + dh) / 2);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            PaintCaption(e.Graphics);
            PaintSplitterBleed(e.Graphics);
        }

        // —— 还原尺寸的一次性钳制 ——
        // 系统在"最大化 → 还原"时按"窗口带边框"的假设反算窗口矩形, 每次多加 16px (125% 下),
        // 最大化还原两轮就能看出窗口一圈圈变大。这里记住离开正常态时的矩形, 还原时摆回去。
        Rectangle normalBounds = Rectangle.Empty;
        FormWindowState lastWindowState = FormWindowState.Normal;
        bool clampingBounds;            // 防重入: 自己在 OnResize 里改尺寸会再触发一次 OnResize

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampRestoredBounds();
            LayoutCaption();
            SyncCaptionState();
        }

        /// <summary>
        /// 从最大化/最小化回到正常态时, 把窗口尺寸按记下的矩形摆正。
        /// <para>时序是这里唯一要注意的: 还原时<b>先</b>按正确尺寸给我们一次 WM_SIZE (875x1000),
        /// <b>随后</b> WinForms 又照"窗口带边框"反算了一遍, 再加 16px (125% 下) 补一次
        /// —— 那一跳在我们的 OnResize 之后。所以校正必须排到下一轮消息里做, 否则改了也被盖掉。</para>
        /// <para>用户后来自己拖边缘时不会触发这段: 那时 <c>lastWindowState</c> 早已是 Normal。</para>
        /// </summary>
        void ClampRestoredBounds()
        {
            FormWindowState st = WindowState;
            if (st == FormWindowState.Normal && !clampingBounds)
            {
                if (lastWindowState != FormWindowState.Normal && !normalBounds.IsEmpty)
                {
                    Size target = normalBounds.Size;
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (WindowState != FormWindowState.Normal || clampingBounds) return;
                            if (Bounds.Size == target) return;
                            clampingBounds = true;
                            try { Bounds = new Rectangle(Bounds.Location, target); }
                            finally { clampingBounds = false; }
                            normalBounds = Bounds;
                        }));
                    }
                    catch (InvalidOperationException) { }   // 句柄已销毁 (正在关窗): 无所谓
                }
                normalBounds = Bounds;      // 连续记录: 用户拖动边缘时这里一直在更新
            }
            lastWindowState = st;
        }

        /// <summary>最大化/还原键的字形与可访问名跟着窗口状态走; 顺带更新圆角偏好。</summary>
        void SyncCaptionState()
        {
            if (btnMax == null) return;
            bool max = WindowState == FormWindowState.Maximized;
            btnMax.Glyph = max ? CaptionGlyph.Restore : CaptionGlyph.Maximize;
            btnMax.AccessibleName = max ? "向下还原" : "最大化";
            if (tipTop != null)
                tipTop.SetToolTip(btnMax, max ? "向下还原" : "最大化");
            ApplyWindowFrame();
        }

        void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
        }

        /// <summary>
        /// 最大化边界与 Win11 圆角。
        /// <para><b>最大化边界</b>由 WM_GETMINMAXINFO 直接接管 (见 WndProc): 那里拿到的是
        /// "系统打算给多大"的第一手数值, 不再走 MaximizedBounds 这条 WinForms 的中间层。</para>
        /// <para>圆角: 常态圆角、最大化方角 —— 最大化时若还圆角, 四角会透出桌面。</para>
        /// </summary>
        void ApplyWindowFrame()
        {
            if (!IsHandleCreated) return;

            // DWMWA_WINDOW_CORNER_PREFERENCE = 33; 1 = 不要圆角, 2 = 圆角
            try
            {
                int pref = WindowState == FormWindowState.Maximized ? 1 : 2;
                DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int));
            }
            catch { }   // Windows 10 没有这个属性: 调用失败即可, 窗口保持方角

            // 保留 WS_THICKFRAME 之后, 系统会给窗口描一圈 1px 的边线 (颜色随主题/强调色变,
            // 深浅还不一致)。它与卡片描边不是同一支色, 看着像"窗口外框是别人画的"。
            // DWMWA_BORDER_COLOR = 34: 按设计里的卡片描边 (203,208,216) 指定, 边缘才同一套。
            try
            {
                int line = unchecked((int)0x00D8D0CB);   // COLORREF = 0x00BBGGRR
                DwmSetWindowAttribute(Handle, 34, ref line, sizeof(int));
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // —— 第 998 行起: 窗口几何的三处接管 ——
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct POINT { public int x, y; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MINMAXINFO
        {
            public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public int dwFlags;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        const int MONITOR_DEFAULTTONEAREST = 2;

        // 窗口样式位 (Win32 常量, 只在本文件用)
        const int WS_CAPTION = 0x00C00000;
        const int WS_THICKFRAME = 0x00040000;
        const int WS_MINIMIZEBOX = 0x00020000;
        const int WS_MAXIMIZEBOX = 0x00010000;
        const int WS_SYSMENU = 0x00080000;

        const int WM_NCCALCSIZE = 0x0083;
        const int WM_GETMINMAXINFO = 0x0024;
        const int WM_NCHITTEST = 0x0084;
        const int WM_NCACTIVATE = 0x0086;
        const int WM_NCPAINT = 0x0085;

        const int HTCLIENT = 1;
        const int HTCAPTION = 2;
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTBOTTOM = 15;
        const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // 边框样式位**必须留着** (WS_CAPTION | WS_THICKFRAME): 它们不只是"画一条框",
                // 还是系统计算窗口几何的依据 —— 最大化矩形 = 工作区 + 两侧边框、还原尺寸同一条公式、
                // 边缘缩放与 Aero Snap 的热区。以前把这两个删掉, 系统那些算法就全部失配
                // (最大化多 18px、还原大一圈), 只能靠手写 hit-test 与尺寸补偿去追 —— 追不平。
                //
                // 现在的分工是 QQ 音乐/Chrome 那套: **边框留着, 只是不画**。客户区照旧盖满整窗
                // (见 WM_NCCALCSIZE), 那条 8px 边框被我们的绘制吃掉, 用户看不到; 但系统算几何时
                // 它依然存在, 于是最大化的客户区正好等于工作区, 边框落在屏幕外。
                // 只留 WS_THICKFRAME, 不要 WS_CAPTION:
                // THICKFRAME 是"窗口几何按边框算"的那一位 (最大化矩形 = 工作区 + 边框、Snap 热区、
                // 边缘缩放), 必须留着; CAPTION 则会让系统在最大化时**多减一条标题栏高度** ——
                // 实测客户区比工作区矮 29px。标题栏由我们自己画, 不需要系统那一位。
                cp.Style |= WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // 客户区盖满整个窗口, 标题栏与边框一条都不留 —— 全部由 PaintCaption 自己画。
            // wParam=0 的那次是"算窗口矩形"的询问, 必须照常走 (返回 0 会破坏坐标)。
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                m.Result = IntPtr.Zero;
                return;
            }

            // NC 区的绘制一律不让系统动手。
            //
            // <para><b>为什么必须拦</b>: 窗口一旦失去激活 (打开设置页这类模态框、切到别的程序),
            // 系统会照"这窗口有标题栏"的老规矩把标题条画一遍 —— Aero 的浅蓝渐变 + 三个带方框的
            // 系统键, 直接盖在我们的自绘标题栏上, 而且**不会自己消失** (它画在窗口表面, 我们这边
            // 没人再重绘那一条)。用户看到的就是"打开设置后标题栏变了"。</para>
            //
            // 自绘边框的窗口都这么处理: WM_NCACTIVATE 回 TRUE 表示"激活状态我收到了, 但别画",
            // WM_NCPAINT 回 0 表示"NC 区没有要画的东西"。标题栏仍由 PaintCaption 画 —— 顺便
            // 重绘一次, 把上次可能残留的痕迹盖掉。
            if (m.Msg == WM_NCACTIVATE)
            {
                m.Result = (IntPtr)1;
                Invalidate(new Rectangle(0, 0, ClientSize.Width, Padding.Top));
                return;
            }
            if (m.Msg == WM_NCPAINT)
            {
                m.Result = IntPtr.Zero;
                return;
            }

            // "最大化能占多大"由我们说了算: 系统默认给的是**工作区 + 两侧边框**
            // (那是留给不可见边框的位置), 而我们的边框已经被绘制吃掉, 照单全收就会
            // 比屏幕大一圈、底边压住任务栏。这里直接把它按到工作区上。
            //
            // 这一句是"保留 WS_THICKFRAME"的全部代价 —— 换成上一版删掉边框的做法,
            // 这笔账确实不用付, 但 Snap / Win+方向键 / 系统阴影也就一起没了。
            if (m.Msg == WM_GETMINMAXINFO)
            {
                base.WndProc(ref m);
                var mmi = (MINMAXINFO)System.Runtime.InteropServices.Marshal.PtrToStructure(
                    m.LParam, typeof(MINMAXINFO));
                var mi = new MONITORINFO
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFO))
                };
                IntPtr mon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
                if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
                {
                    mmi.ptMaxPosition.x = mi.rcWork.left;
                    mmi.ptMaxPosition.y = mi.rcWork.top;
                    mmi.ptMaxSize.x = mi.rcWork.right - mi.rcWork.left;
                    mmi.ptMaxSize.y = mi.rcWork.bottom - mi.rcWork.top;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, m.LParam, false);
                }
                return;
            }

            base.WndProc(ref m);

            // 系统在 NC 区缺失时把所有点都当客户区。这里把它掰回来:
            // 边缘 → 缩放热区, 标题栏空白 → 可拖动 (双击自动最大化、右键系统菜单一并回来)。
            if (m.Msg == WM_NCHITTEST && (int)m.Result == HTCLIENT)
            {
                var cursor = new Point((short)(m.LParam.ToInt64() & 0xFFFF),
                                       (short)((m.LParam.ToInt64() >> 16) & 0xFFFF));
                m.Result = (IntPtr)HitTestCaption(PointToClient(cursor));
            }
        }

        /// <summary>窗口左上角起算的点 → 命中码。按钮区域返回 HTCLIENT, 让控件自己收点击。</summary>
        int HitTestCaption(Point p)
        {
            int w = ClientSize.Width, h = ClientSize.Height;

            // 边缘缩放: 最大化时不该有 (那时没有"边框"可拖)
            if (WindowState != FormWindowState.Maximized)
            {
                int grip = Dpi.S(6);
                bool left = p.X < grip, right = p.X >= w - grip;
                bool top = p.Y < grip, bottom = p.Y >= h - grip;
                if (top && left) return HTTOPLEFT;
                if (top && right) return HTTOPRIGHT;
                if (bottom && left) return HTBOTTOMLEFT;
                if (bottom && right) return HTBOTTOMRIGHT;
                if (left) return HTLEFT;
                if (right) return HTRIGHT;
                if (top) return HTTOP;
                if (bottom) return HTBOTTOM;
            }

            if (p.Y < Padding.Top)
            {
                // 落在键上就交还给控件 (否则 HTCAPTION 会把点击吞掉, 键按不动)
                foreach (var c in new Control[] { btnMin, btnMax, btnClose, btnSettings })
                    if (c != null && c.Visible && c.Bounds.Contains(p)) return HTCLIENT;
                return HTCAPTION;
            }

            return HTCLIENT;
        }

        void SetAppState(AppState s)
        {
            // R-07: 四条恢复路径 (整链路重建 / 单路重建 / 端点回归轮询 / 看门狗) 都从这里改状态,
            // 但"哪些迁移允许"从来没有集中表达过。这里只判不拦 —— 越界说明两套恢复逻辑同时对同一条
            // 链路动手, 记下来供排查; 真去拦的代价 (恢复路径被冻住、界面卡在"启动中") 比记错大得多。
            AppState prev = state;
            if (!AppStateMachine.CanTransition(prev, s))
                Log("[警告] 状态迁移越界: " + AppStateMachine.Text(prev) + " → " + AppStateMachine.Text(s) +
                    "（不在允许集合内；通常意味着两条恢复路径同时对同一条链路动手）");
            state = s;

            // 分流运行期间阻止系统空闲待机 (Modern Standby / S0 低功耗):
            // 分流器在源静音时渲染的是静音, 系统不会因此阻止待机 → 整机挂起时 USB 音频端点
            // 一并挂起, 唤醒后端点病态 (水位堆积震荡/僵尸无声), 且 Modern Standby 的部分挂起
            // 不触发 PowerModes.Resume, "唤醒重建"逻辑抓不到。进程退出会自动清除该状态。
            if (s == AppState.Running) SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
            else SetThreadExecutionState(ES_CONTINUOUS);

            string txt;
            switch (s)
            {
                case AppState.Starting: txt = "启动中..."; break;
                case AppState.Running: txt = "运行中"; break;
                case AppState.Stopping: txt = "正在停止..."; break;
                case AppState.Stopped: txt = "已停止"; break;
                case AppState.Failed: txt = "启动失败(见日志)"; break;
                default: txt = "就绪"; break;
            }
            // 状态不再常驻在按钮旁 (那一行已让给规格行)。改为"变化时写一条日志":
            // 只在真正的迁移时记 —— 四条恢复路径会反复 SetAppState 同一个状态, 无条件写会刷屏。
            // 点击「开始分流」后日志第一行就是 [状态] 启动中..., 信息量与原标签相同且可回溯。
            if (prev != s) Log("[状态] " + txt);

            running = (s == AppState.Running);
            UpdateStartButton(s);
            UpdateRunStateLabel(s);
            ApplyUiLock();

            // 数值标签: 仅分流运行中(双向绑定生效)为黑, 其余灰色表示无效
            SetVolumeBindVisual(running);

            if (trayIcon != null) trayIcon.Text = "Audio Splitter - " + txt;

            // 水位监控仅运行时按间隔跑, Ready/Stopped 或间隔0=不跑 (防空转)
            SyncWaterTimer();
        }

        /// <summary>
        /// 「开始分流」图标按钮的图形与配色。四个状态各一套 (图形与文字色一起读出链路在干什么)：
        /// <list type="bullet">
        /// <item>默认 (Ready/Stopped/Failed): 蓝 ▶ —— 可以开始</item>
        /// <item>启动中 (Starting): 蓝灰转环 —— 正在建链路，不可点</item>
        /// <item>运行中 (Running): 红 ■ + 呼吸光环 —— 点击即停止</item>
        /// <item>正在停止 (Stopping): 浅红转环 —— 正在拆链路，不可点</item>
        /// </list>
        /// <para>Text 仍随状态更新: 控件不再画文字, 但屏幕阅读器 / 探针 / 无障碍语义靠它。
        /// 颜色过渡由 <see cref="StartButton.BeginTint"/> 做 220ms 渐变; 过渡态被
        /// <see cref="ApplyUiLock"/> 置为不可点, 图标自动混白 30% —— "变淡 = 不可点"。</para>
        /// </summary>
        void UpdateStartButton(AppState s)
        {
            switch (s)
            {
                case AppState.Starting:
                    btnStart.Text = "启动中…";
                    // 比默认蓝更深更灰: 过渡态会被自动淡化 30%, 基准色太浅的话淡完就快看不见了
                    SetStartTint(Color.FromArgb(0x47, 0x68, 0x8C));
                    btnStart.Mode = StartButtonMode.Working;
                    break;
                case AppState.Stopping:
                    btnStart.Text = "正在停止…";
                    // 比运行中的红浅一档: 运行中全饱和 + 可点, 停止中淡化 + 不可点, 两者叠加后
                    // 明度差已经够大, 不必再换图形语言
                    SetStartTint(Color.FromArgb(0xB4, 0x50, 0x4A));
                    btnStart.Mode = StartButtonMode.Working;
                    break;
                case AppState.Running:
                    btnStart.Text = "停止分流";
                    SetStartTint(Color.FromArgb(0xC4, 0x2B, 0x1C));
                    btnStart.Mode = StartButtonMode.Pulse;
                    break;
                default:
                    btnStart.Text = "开始分流";
                    SetStartTint(Color.FromArgb(0x00, 0x78, 0xD7));
                    btnStart.Mode = StartButtonMode.Idle;
                    break;
            }
            btnStart.Invalidate();
        }

        /// <summary>图标按钮的唯一着色入口: 图标色与悬停垫色同源 (<see cref="StartButton"/>),
        /// 由 BeginTint 渐变过去 (220ms) —— 状态切换的反馈多一层"颜色在流动"的动效。</summary>
        void SetStartTint(Color c)
        {
            btnStart.BeginTint(c);
        }

        /// <summary>
        /// 规格行的运行状态字 (用户指定: 开始分流后**替换掉**声道标注)。链路在途时声道标注
        /// 整个让位 —— 那一行的信息重点变成了"链路现在在干什么", 标注在停止后原样回来
        /// (FlowLayoutPanel 只是隐藏, 内容不重建)。四个在途状态各一行, 颜色与按钮图形同一支:
        /// <list type="bullet">
        /// <item>启动中… / 正在停止…: 蓝灰 / 浅红 (与图标同色)</item>
        /// <item>运行中: 红 —— 与 ■ 图标呼应</item>
        /// <item>启动失败: 浅红 + 指路日志 (按钮此刻已回到蓝色可点态)</item>
        /// </list>
        /// 空闲态 (就绪/已停止) 隐藏状态字、恢复标注。
        /// <para>这是状态字的**唯一权威入口**: 状态迁移时它收回 <see cref="ShowRunStateNote"/>
        /// 的插播 (停掉保留计时器), 事件插播只允许改文字/颜色, 不允许改"谁在场"。</para>
        /// </summary>
        void UpdateRunStateLabel(AppState s)
        {
            if (lblRunState == null) return;
            if (runNoteTimer != null) runNoteTimer.Stop();   // 新的权威文案到场, 插播作废
            string txt; Color c; bool show = true;
            switch (s)
            {
                case AppState.Starting: txt = "启动中…"; c = Color.FromArgb(0x47, 0x68, 0x8C); break;
                case AppState.Running: txt = "运行中"; c = Color.FromArgb(0xC4, 0x2B, 0x1C); break;
                case AppState.Stopping: txt = "正在停止…"; c = Color.FromArgb(0xB4, 0x50, 0x4A); break;
                case AppState.Failed: txt = "启动失败（见日志）"; c = Color.FromArgb(0xB4, 0x50, 0x4A); break;
                default: show = false; txt = ""; c = Color.Gray; break;
            }
            if (!show)
            {
                if (lblRunState.Visible)
                {
                    lblRunState.Visible = false;
                    if (flwSpecChannels != null) flwSpecChannels.Visible = true;   // 标注原样回来 (内容一直都在, 只是藏了)
                }
                return;
            }
            lblRunState.Text = txt;
            lblRunState.ForeColor = c;
            if (flwSpecChannels != null && flwSpecChannels.Visible) flwSpecChannels.Visible = false;
            if (!lblRunState.Visible) lblRunState.Visible = true;
            PlaceRunStateLabel();   // 与声道标注同一个居中规则: 换的只是内容, 位置语言不变
        }

        /// <summary>状态字落位: 交给 <see cref="RunNoteLabel.PlaceIn"/> —— 放得下按文字宽
        /// 居中于空地 (与声道标注同一条居中语言), 放不下占满整块空地滚动。窗口缩放时
        /// LayoutTopRows 也会调它 —— 标注和状态字用的是同一块空地, 谁在场谁占。</summary>
        void PlaceRunStateLabel()
        {
            if (lblRunState == null) return;
            int lb = cmbSpec.Right + Dpi.S(8);
            int rb = btnAddOutput.Left - Dpi.S(8);
            lblRunState.PlaceIn(new Rectangle(lb, cmbSpec.Top, Math.Max(0, rb - lb), cmbSpec.Height));
        }

        // 运行期事件插播的两档配色: 事件是"过程信息"不是"状态", 都比"运行中"的红退一档,
        // 眼睛读到的是"运行中, 但有件事值得知道"。
        static readonly Color RunNoteInfo = Color.FromArgb(0x47, 0x68, 0x8C);   // 蓝灰: 与"启动中"同支
        static readonly Color RunNoteWarn = Color.FromArgb(0xB4, 0x7A, 0x28);   // 琥珀: 注意但不报警

        /// <summary>
        /// 运行期事件插播 (用户指定: 日志里的状态也简单显示到声道标注处)。只改状态字的
        /// 文字/颜色, "谁在场"的规则不动 —— 插播时声道标注本来就因 Running 而隐藏;
        /// <see cref="runNoteTimer"/> 到点把它收回"运行中", 状态迁移时 UpdateRunStateLabel
        /// 会立即停掉这个计时器 (权威文案到场, 插播作废)。
        /// </summary>
        void ShowRunStateNote(string txt, Color c)
        {
            if (lblRunState == null || !running || lblRunState.IsDisposed) return;
            lblRunState.Text = txt;
            lblRunState.ForeColor = c;
            if (flwSpecChannels != null && flwSpecChannels.Visible) flwSpecChannels.Visible = false;
            if (!lblRunState.Visible) lblRunState.Visible = true;
            PlaceRunStateLabel();
            runNoteTimer.Stop();
            runNoteTimer.Start();
        }

        /// <summary>
        /// 日志 → 状态字的插播桥 (在 <see cref="Log"/> 里对 <c>[状态]</c> 条目调用)。
        /// 状态机迁移的那六条不插 —— UpdateRunStateLabel 是它们的唯一入口, 这里再插一遍
        /// 只会跟权威文案打架; 只补**运行期事件**: 捕获源静默/恢复、水位恢复、周期水位。
        /// 缓冲水位的全文有一百多字符 (调节/瞬时/校正/最低/欠载/电平/时序), 状态字只有
        /// 一格文字宽, 挑最有用的调节与最低两组, 其余留给日志框。
        /// </summary>
        void MirrorRunStateEvent(string body)
        {
            string txt; Color c;
            if (body.StartsWith("捕获源暂无数据"))
            {
                txt = "捕获源暂无数据，输出待命"; c = RunNoteWarn;
            }
            else if (body.StartsWith("捕获源恢复数据"))
            {
                // "捕获源恢复数据（此前静默约 12 秒）" → 保住时长, 它是唯一有信息量的部分
                txt = "捕获源已恢复（静默" + After(body, "约 ", " ") + "秒）";
                c = RunNoteInfo;
            }
            else if (body.StartsWith("缓冲水位已恢复"))
            {
                // "缓冲水位已恢复: 62ms / 目标 120ms" → "水位已恢复 62ms"
                txt = "水位已恢复 " + FirstMs(body); c = RunNoteInfo;
            }
            else if (body.StartsWith("缓冲水位 "))
            {
                txt = "水位 " + BriefWater(body.Substring(5)); c = RunNoteInfo;
            }
            else return;
            ShowRunStateNote(txt, c);
        }

        /// <summary>缓冲水位单行简版: 双输出取「调节 L/R」「最低 L/R」两组; 多输出
        /// (设备编号格式) 每台都列出 调节/最低 —— 用户要求"全显示, 放不下靠滚动",
        /// 不再把第三台往后的折成 "+N台"。欠载时补一笔。解析失败退回截断 ——
        /// 日志里永远有全文。</summary>
        static string BriefWater(string body)
        {
            try
            {
                var ms = System.Text.RegularExpressions.Regex.Matches(body, @"[LR]=(\d+)ms");
                if (ms.Count >= 6)   // 双输出顺序: 调节(0,1) 瞬时(2,3) 最低(4,5)
                {
                    string s = "调节 " + ms[0].Groups[1].Value + "/" + ms[1].Groups[1].Value + "ms"
                             + " 最低 " + ms[4].Groups[1].Value + "/" + ms[5].Groups[1].Value + "ms";
                    // 欠载计数为 0 时不显示 (引擎哪怕没欠过载也会带 "欠载 L=0 R=0" 字段)
                    var u = System.Text.RegularExpressions.Regex.Match(body, @"欠载 L=(\d+) R=(\d+)");
                    if (u.Success && (u.Groups[1].Value != "0" || u.Groups[2].Value != "0"))
                        s += " 欠载" + u.Groups[1].Value + "/" + u.Groups[2].Value;
                    return s;
                }
                // 多输出: "[4] 66/29ms +496ppm 电平0.6% | [5] …" → 每台都列 调节/最低,
                // 超宽交给 RunNoteLabel 跑马灯 (不折台数)
                var dv = System.Text.RegularExpressions.Regex.Matches(body, @"(\S+) (\d+)/(\d+)ms");
                if (dv.Count >= 1)
                {
                    var parts = new string[dv.Count];
                    for (int i = 0; i < dv.Count; i++)
                        parts[i] = dv[i].Groups[1].Value + " " + dv[i].Groups[2].Value
                                 + "/" + dv[i].Groups[3].Value + "ms";
                    return string.Join(" ", parts);
                }
                if (ms.Count >= 2)
                    return "调节 " + ms[0].Groups[1].Value + "/" + ms[1].Groups[1].Value + "ms";
            }
            catch { }
            return body.Length > 30 ? body.Substring(0, 30) + "…" : body;
        }

        /// <summary>取字符串里第一个 "Nms"。</summary>
        static string FirstMs(string s)
        {
            int i = s.IndexOf("ms", StringComparison.Ordinal);
            while (i > 0 && (i < 1 || !char.IsDigit(s[i - 1]))) 
            {
                i = s.IndexOf("ms", i + 1, StringComparison.Ordinal);
                if (i < 0) break;
            }
            if (i <= 0) return "";
            int j = i - 1;
            while (j > 0 && (char.IsDigit(s[j - 1]) || s[j - 1] == '.')) j--;
            return s.Substring(j, i - j) + "ms";
        }

        /// <summary>取 a 与 b 之间的子串 (找不到返回空串)。</summary>
        static string After(string s, string a, string b)
        {
            int i = s.IndexOf(a, StringComparison.Ordinal);
            if (i < 0) return "";
            i += a.Length;
            int j = s.IndexOf(b, i, StringComparison.Ordinal);
            return j < 0 ? s.Substring(i) : s.Substring(i, j - i);
        }

        /// <summary>
        /// 按当前状态统一切换控件的可用性。分三档，与内核能力严格对齐
        /// （<see cref="AudioEngine.SetRouting"/> 只能换矩阵，声道数/设备/延迟都换不了）：
        /// <list type="bullet">
        /// <item>链路级：捕获设备 / 刷新设备 / 延迟档位 / 规格 / 加卡 / 卡片设备 / 移除卡片
        ///       —— 运行中置灰</item>
        /// <item>延迟级：卡片延迟 —— 只有启动中置灰；运行中改的是本路延迟线深度，就地生效
        ///       （设备与声道数定死，但延迟只是一个 FIFO 深度）</item>
        /// <item>矩阵级：卡片声道 / 多声道行声道 —— 运行中可改（热替换矩阵）</item>
        /// <item>形态级：单 ↔ 多声道 —— 会改设备物理声道数，运行中把「多声道」画灰并拦下</item>
        /// <item>采样级 / 监控级：音量、静音、消音、校正、水位间隔、最小化 —— 始终可动</item>
        /// </list>
        /// 卡片是动态建的，所以这里既在状态切换时调用，也在重建卡片后调用。
        /// </summary>
        void ApplyUiLock()
        {
            // 过渡态 (启动中 / 正在停止) 一律锁死: 链路正在建或正在拆, 这时改任何链路级参数
            // 都是在跟后台线程抢设备。顺带也挡住了重复点击 —— 按钮不可点, 点击事件根本不会发。
            bool busy = (state == AppState.Starting || state == AppState.Stopping);
            bool run = (state == AppState.Running);

            cmbDefault.Enabled = !busy && !run;
            btnRefresh.Enabled = !busy && !run;
            cmbLatency.Enabled = !busy && !run;
            cmbSpec.Enabled = !busy && !run;
            btnAddOutput.Enabled = !busy && !run && models.Count < Infrastructure.LaneSettingsCodec.MaxOutputCards;  // F-24: 达上限禁用
            // 校准运行中可用: 测试音叠在正在播放的音频上, 不必停止再开始, 也不静音。
            // 启动中仍置灰 —— 链路正在建, 端点还没稳, 这时放扫频没有意义。
            btnCalibrate.Enabled = !busy;

            volMaster.Enabled = !busy;
            volGain.Enabled = !busy;
            btnMuteMaster.Enabled = !busy;
            chkMuteCapture.Enabled = !busy;
            chkMinimizeToTray.Enabled = !busy;
            chkDrift.Enabled = !busy;
            nudWaterInterval.Enabled = !busy;
            btnStart.Enabled = !busy;

            for (int ci = 0; ci < cards.Count; ci++)
            {
                cards[ci].SetStructuralLock(busy || run);   // 设备 / 移除
                cards[ci].SetDelayLock(busy);               // 延迟: 运行中可改 (只换延迟线深度)
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
                defaultWatcher = new DefaultChangeWatcher(OnSystemDefaultChanged, OnDeviceLost, InvalidateChannelCache);
                try { catalog.Enumerator.RegisterEndpointNotificationCallback(defaultWatcher); }
                catch (Exception ex) { Log("[警告] 注册默认输出监听失败: " + ex.Message); }
                RefreshDevices();
                RecordRestoreTarget();
                ApplyIniVolumeOverrides();
                // 初始分割位置: 让日志区保持 110 高 (与去掉 Panel2MinSize 之前的默认观感一致)。
                // 现在两个面板都没有下限, WinForms 不会帮着夹取, 必须按真实客户区高度反算一次;
                // 之后完全由用户拖动决定, 不再有任何限制。
                splitContainer.SplitterDistance =
                    Math.Max(0, splitContainer.Height - Dpi.S(110) - splitContainer.SplitterWidth);
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
            // R-05: 先失效, 且要放在下面三个提前 return 之前 —— 设备断开与"是否在本次运行
            // 的输出集合里"无关 (未运行时用户也可能拔设备), 缓存该丢就得丢。
            InvalidateChannelCache(id);
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
                bool uiMm = btnMuteMaster.Muted, uiMc = chkMuteCapture.Checked;

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
            btnMuteMaster.Muted = uiMm;
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
                            if (btnMuteMaster.Muted != want) btnMuteMaster.Muted = want;
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
        /// <param name="incremental">
        /// true = 运行中设备集合刚变过, **只还原并释放离开集合的端点**, 留下的原样保留。
        /// false (默认) = 启动 / 从"外部切默认输出"恢复 / 睡眠唤醒后重绑, 端点可能已整体失效,
        /// 必须全量重来。
        /// <para>为什么不让所有调用方都走 incremental: 睡眠唤醒与切默认输出之后,
        /// <c>EndpointVolumeBinding</c> 里的 COM 对象可能"看起来还有效"但指向的已不是同一个端点,
        /// 只有全量 Dispose + 重新 <c>catalog.Open</c> 才能保证拿到当前的真实端点。</para>
        /// </param>
        bool BindEndpointVolumeSync(string stage, bool incremental = false)
        {
            SyncLanesFromCards();
            noEndpointVolumeDevices.Clear();

            // 设备集合可能已变 (换设备/删卡) → 先按旧基线还原并释放, 再按新集合重绑;
            // 同一设备仍在 → BindSink 复用原对象, 还原基线不会被冲掉
            if (incremental)
            {
                var wanted = new List<string>(models.Count);
                for (int i = 0; i < models.Count; i++)
                {
                    string d = models[i].DeviceId;
                    if (!string.IsNullOrEmpty(d) && !wanted.Contains(d)) wanted.Add(d);
                }
                bindings.RetainSinks(wanted, true);
            }
            else
            {
                bindings.UnbindSinks(true);
            }

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
                if (card.btnMute.Muted != want) card.btnMute.Muted = want;
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
                    bool commSwitched;
                    if (!SystemDefaultPolicy.TrySetDefault(dev.ID, out setErr, out commSwitched))
                    {
                        Log("[错误] 配置 1/4 设置系统默认输出 → [" + cmbDefault.SelectedIndex + "] " +
                            dev.FriendlyName + " 失败: " + setErr);
                        int idx1 = catalog.IndexOf(catalog.DefaultRenderId());
                        if (idx1 >= 0) cmbDefault.SelectedIndex = idx1;
                        return false;
                    }
                    // R-12: 通信角色是系统级副作用 (通话/会议走哪台), 必须让用户在日志里看见
                    Log("[配置] 1/4 设置系统默认输出 → [" + cmbDefault.SelectedIndex + "] " + dev.FriendlyName + " 成功" +
                        (commSwitched ? "（通信设备一并切换：通话/会议声音也会走这台）" : "（通信设备未跟着切换）"));
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
            // incremental: 只动离开集合的那几台, 留下的端点不还原、不 Dispose、不重新订阅
            BindEndpointVolumeSync("[变更]", true);
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
                    if (SystemDefaultPolicy.TrySetDefault(target, out err, out _))
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

        /// <summary>
        /// 「刷新设备」按钮: 重新枚举设备并重建下拉/卡片 (设备被拔掉等场景)。
        /// 与下拉展开解耦 —— 点开下拉不再自动刷新 (用户要求撤掉), 刷新只走这个按钮。
        /// </summary>
        /// <summary>
        /// 刷新按钮的点击: 图标先转满一圈, 转完再跑枚举。
        /// <para>枚举 (<c>catalog.Refresh</c> + 重建卡片) 是**同步**的, 一进来就把消息泵堵死 ——
        /// 实测要 0.5~1 秒, 期间动画一帧都画不出来。所以不能在 Click 里直接跑, 也不能和动画并行:
        /// 并行的话图标转到一半就被冻住, 等枚举结束早已过了 560ms, 那几帧直接归位 ——
        /// 看到的是"卡住然后突然归位", 比不转还怪。</para>
        /// <para>顺序改成: 转一圈 (560ms) → 枚举。转完再干, 用户看到的就是完整的一圈。</para>
        /// </summary>
        void OnRefreshClick()
        {
            if (btnRefresh.IsSpinning) return;      // 这一圈还没转完: 忽略连点, 免得动画被打断重来
            btnRefresh.StartSpin();

            if (refreshKickTimer == null)
            {
                refreshKickTimer = new System.Windows.Forms.Timer { Interval = RefreshKickMs };
                refreshKickTimer.Tick += (s, e) =>
                {
                    refreshKickTimer.Stop();
                    RefreshDevicesClick();
                };
            }
            refreshKickTimer.Start();
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
            // R-05: 设备集合真的重新枚举了, 端点声道数全部视为陈旧
            InvalidateChannelCache(null);
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

        /// <summary>注册进程级消息过滤器并留住引用 (R-10: 关窗时按它逐个摘除)。</summary>
        void AddMessageFilter(IMessageFilter f)
        {
            if (f == null) return;
            Application.AddMessageFilter(f);
            msgFilters.Add(f);
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
                        if (SystemDefaultPolicy.TrySetDefault(restoreDefaultId, out err, out _))
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
            ReleaseRuntimeResources();
        }

        /// <summary>
        /// 关窗时的运行时资源释放 (R-10)。
        /// <para>原先这一段散在 <see cref="MainForm_FormClosing"/> 里, 逐个 if-null 手写, 漏了
        /// `deviceReturnTimer` / `sinkRetryTimer` / 两个 `ManualResetEventSlim` / 设置页 /
        /// 顶部 ToolTip / 三个消息过滤器。收成一处, 以后加东西只有这里要改。</para>
        /// <para>幂等: 每个字段释放后置 null, 重复调用安全。调用前提见调用点 ——
        /// 两个 ManualResetEventSlim 在这里**释放**, 所以调用前必须已经 Wait 完在途任务
        /// (否则 Wait 会抛 ObjectDisposedException)。</para>
        /// </summary>
        void ReleaseRuntimeResources()
        {
            // 消息过滤器先摘: 它们会在任意控件的消息循环里跑, 留着就可能碰到已经 Dispose 的控件
            for (int i = 0; i < msgFilters.Count; i++)
            {
                try { Application.RemoveMessageFilter(msgFilters[i]); } catch { }
            }
            msgFilters.Clear();

            // 定时器: 一律先 Stop 再 Dispose —— 只 Dispose 的话, 已排进消息队列的 Tick
            // 仍可能在下一个消息循环里回调到已释放的窗体上。
            DisposeTimer(ref waterTimer);
            DisposeTimer(ref saveDebounce);
            DisposeTimer(ref watchTimer);
            DisposeTimer(ref powerTimer);
            DisposeTimer(ref delayHotTimer);
            DisposeTimer(ref deviceReturnTimer);   // R-10 补
            DisposeTimer(ref sinkRetryTimer);      // R-10 补
            DisposeTimer(ref refreshKickTimer);    // 刷新动画的延迟启动
            DisposeTimer(ref runNoteTimer);        // 运行期事件插播状态字的保留时长

            // 标题栏图标: 由窗体图标缩放生成的一份副本 (Icon 每 new 一次都要自己释放)
            if (titleBarIcon != null)
            {
                try { titleBarIcon.Dispose(); } catch { }
                titleBarIcon = null;
            }

            try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }

            if (activateEvent != null)
            {
                try { activateEvent.Close(); } catch { }
                activateEvent = null;
            }
            // 注销默认输出监听 (须在枚举器释放前)
            if (defaultWatcher != null && catalog != null)
            {
                try { catalog.Enumerator.UnregisterEndpointNotificationCallback(defaultWatcher); } catch { }
                defaultWatcher = null;
            }
            if (sessionLog != null)
            {
                try { sessionLog.Dispose(); } catch { }
                sessionLog = null;
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
            // R-10 补: "更多"弹层正常路径上已被关闭; 但 AppState 切换失败等
            // 异常路径可能留下一个未释放的实例 (Dispose 里会先归还借去的控件)。
            if (settingsFlyout != null)
            {
                try { if (!settingsFlyout.IsDisposed) settingsFlyout.Dispose(); } catch { }
                settingsFlyout = null;
            }
            // R-10 补: 顶部 ToolTip 持有一个 TOOLTIPS_CLASS 原生窗口 (与 R-02 同一类资源)
            if (tipTop != null)
            {
                try { tipTop.RemoveAll(); } catch { }
                try { tipTop.Dispose(); } catch { }
                tipTop = null;
            }
            // R-10 补: 两个 ManualResetEventSlim 内部各持一个 WaitHandle, 但**只在确认没有
            // 在途任务时才释放**。释放是有代价的: 上面 Wait(2000) 超时后, 后台线程仍可能走到
            // finally { startFinished.Set() } (MainForm.Engine.cs:405) 或
            // sinkRestartFinished.Set() (:864), 那会抛 ObjectDisposedException —— 而且是在
            // finally 里抛, 线程池上没人接 = 崩进程。IsSet=true 的语义就是"已结束/空闲",
            // 此时不会有在途的 Set; 没等到就让进程退出时由 OS 回收。
            // 外面再包一层 try: 万一本方法被重复调用, 对已释放的对象读 IsSet 也不该抛。
            try { if (startFinished.IsSet) startFinished.Dispose(); } catch { }
            try { if (sinkRestartFinished.IsSet) sinkRestartFinished.Dispose(); } catch { }
        }

        /// <summary>
        /// R-10 补: 把 <see cref="ReleaseRuntimeResources"/> 也接到 <c>Dispose</c> 上。
        ///
        /// <para>原先清理**只**挂在 <see cref="MainForm_FormClosing"/> 里, 等于假设"窗体一定是被用户
        /// 点 X 关掉的"。但 <c>Dispose</c> 是 IDisposable 契约里的正规退出路径: 程序里任何一处
        /// <c>using</c>/显式 Dispose、以及以后若加"切换主窗口"之类的逻辑, 都会绕过 FormClosing 直接
        /// 走这里, 那时 <c>bindings</c> / <c>catalog</c> / <c>activateEvent</c> 一个都放不掉 ——
        /// 分析器把这几项逐条报出来 (CA2213) 是有道理的, 只是它看不到 FormClosing 这条间接路径。</para>
        ///
        /// <para>幂等性由 <see cref="ReleaseRuntimeResources"/> 保证 (每项释放后置 null), 所以
        /// "FormClosing 已调过、Dispose 再调一次"是安全的。顺序上必须先于 <c>base.Dispose</c>:
        /// 那一步会销毁子控件, 而定时器 Tick / 消息过滤器可能还在碰它们。</para>
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseRuntimeResources();
            base.Dispose(disposing);
        }

        /// <summary>Stop + Dispose + 置 null; 重复调用安全。</summary>
        static void DisposeTimer(ref System.Windows.Forms.Timer t)
        {
            if (t == null) return;
            try { t.Stop(); } catch { }
            try { t.Dispose(); } catch { }
            t = null;
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

            // 运行期事件插播到规格行状态字 (用户指定: 日志里的状态也显示到声道标注处)。
            // 站在 Log 里搭桥的好处: 四类来源 (水位定时器 / 引擎看门狗 / 校正 provider 的
            // 音频线程 Report) 走的都是这一个漏斗, 一处接管全覆盖; 到这里必在 UI 线程。
            if (running && msg.StartsWith("[状态] ", StringComparison.Ordinal))
                MirrorRunStateEvent(msg.Substring(5).TrimEnd());

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
