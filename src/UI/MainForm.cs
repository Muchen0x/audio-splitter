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
        // 输出卡片: 每张卡片 = 一路输出 (输出声道 + 设备 + 延迟 + 音量 + 静音)
        readonly List<OutputLane> lanes = new List<OutputLane>();
        readonly List<OutputCardControl> cards = new List<OutputCardControl>();
        FlowLayoutPanel cardHost;
        FlowLayoutPanel flwSpecChannels;
        bool fittingWidths;      // FitCardWidths 重入保护
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
        // 启动前 UI 快照 (AutoConfigure 的 Bind 会用设备真值回填覆盖控件, 启动失败时恢复权威值)
        int snapL, snapR, snapM;
        bool snapMl, snapMr, snapMm, snapMc;
        bool hasSnap = false;

        // 设备目录: 内部实例仅供展示, 不参与端点操作 (见 DeviceCatalog 注释)
        DeviceCatalog catalog;
        // 主/左/右端点绑定: 各自持有独立 MMDevice, 可安全释放
        ChannelBindingSet bindings;

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
            Size = new Size(600, 554);
            // 最小高度按布局反推: 内容区至少留到"规格行 + 一整张卡片"(约 284) +
            // 6px 分隔条 + 日志区最小 110 → 客户区需 ~400, 再加标题栏/边框。
            MinimumSize = new Size(600, 480);
            Font = new Font("Microsoft YaHei UI", 9F);

            // 滚轮路由: 光标停在音量条上就调音量 (每格 5%), 而不是滚卡片列表。
            // 卡片里的滑条不抢焦点, 收不到 WM_MOUSEWHEEL, 只能按光标位置转过去。
            Application.AddMessageFilter(new VolumeWheelRouter());

            // 上下可拖动面板: 内容区 | 6px灰分隔条 | 日志区 (参照 tk.PanedWindow)
            // FixedPanel=Panel2: 窗口变矮时先收缩上面的内容区, 日志区高度保持不变
            // (以前是 Panel1 固定 → 变矮时日志直接被压扁)。
            // Panel1MinSize: 至少留住"规格行 + 一整张卡片", 再小就没有意义了;
            // 卡片区自己带纵向滚动, 所以 Panel1 不需要 AutoScroll。
            splitContainer = new NoFocusSplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(0x88, 0x88, 0x88),
                FixedPanel = FixedPanel.Panel2,
                Panel1MinSize = 284,
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
            Controls.Add(splitContainer);

            var lblD = new Label { Text = "捕获(虚拟):", Location = new Point(15, 15), AutoSize = true };
            cmbDefault = new NoWheelComboBox { Location = new Point(110, 12), Width = 250, DropDownWidth = 375, DropDownStyle = ComboBoxStyle.DropDownList };
            chkMuteCapture = new CheckBox { Text = "消音", Location = new Point(363, 14), AutoSize = true };
            btnRefresh = new Button { Text = "刷新设备", Location = new Point(417, 11), Width = 72, Height = 23 };

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
            chkMuteMaster = new CheckBox { Text = "静音", AutoSize = false, Width = 56, Height = 20 };

            // 开始分流
            btnStart = new Button { Text = "开始分流", Location = new Point(15, 142), Width = 130, Height = 35 };
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
            var btnAddOutput = new Button
            {
                Text = "添加输出",
                Location = new Point(482, 184),
                Width = 92,
                Height = 26,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            // 输出卡片区 (可增删, 内容超出时只有纵向滚动)
            // 宽度顶到面板右缘 (与「添加输出」右边缘对齐), 竖滚动条才会贴着右边显示;
            // 左右不留 Padding: 卡片宽度 = 可视宽度, 数学上不可能超出 → 不会出现横向滚动条。
            cardHost = new FlowLayoutPanel
            {
                Location = new Point(15, 216),
                Size = new Size(559, 166),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
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

            chkMinimizeToTray = new CheckBox { Text = "关闭时最小化到托盘", Location = new Point(294, 44), AutoSize = true };
            chkDrift = new CheckBox { Text = "校正", AutoSize = true, Checked = true };
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
            nudWaterInterval = new NoWheelNumeric { Location = new Point(397, 5), Width = 60, Minimum = 0, Maximum = 3600, Value = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var lblWaterSec = new Label { Text = "秒", Location = new Point(461, 8), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };

            var lblLogTitle = new Label { Text = "日志", Location = new Point(15, 8), AutoSize = true, Font = new Font("Microsoft YaHei UI", 10F) };
            btnClearLog = new Button
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
                int v = (int)nudWaterInterval.Value;
                if (v >= 1 && v <= 4) { nudWaterInterval.Value = 5; return; } // 下限5秒: 显示与实际一致
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
                Interval = Math.Max(5, (int)nudWaterInterval.Value) * 1000
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

            bool busy = (s == AppState.Starting);
            bool run = (s == AppState.Running);
            cmbDefault.Enabled = !busy && !run;
            btnRefresh.Enabled = !busy && !run;
            volMaster.Enabled = !busy;
            volGain.Enabled = !busy;
            chkMuteMaster.Enabled = !busy;
            chkMuteCapture.Enabled = !busy;
            chkMinimizeToTray.Enabled = !busy;
            chkDrift.Enabled = !busy;
            cmbLatency.Enabled = !busy && !run;
            cmbSpec.Enabled = !busy && !run;
            nudWaterInterval.Enabled = !busy;
            btnStart.Enabled = !busy;
            for (int ci = 0; ci < cards.Count; ci++)
            {
                cards[ci].SetBusy(busy || run);   // 设备/声道/延迟/移除锁定
                cards[ci].volTrack.Enabled = !busy;
                cards[ci].chkMute.Enabled = !busy;
            }

            // 数值标签: 仅分流运行中(双向绑定生效)为黑, 其余灰色表示无效
            SetVolumeBindVisual(run);

            if (trayIcon != null) trayIcon.Text = "Stereo Splitter - " + txt;

            // 水位监控仅运行时按间隔跑, Ready/Stopped 或间隔0=不跑 (防空转)
            SyncWaterTimer();
        }

        // 水位 timer 启停与间隔统一入口 (SetAppState / 间隔变更时调用)
        void SyncWaterTimer()
        {
            if (waterTimer == null) return;
            if (running && (int)nudWaterInterval.Value > 0)
            {
                waterTimer.Interval = Math.Max(5, (int)nudWaterInterval.Value) * 1000;
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

                // 捕获 UI 当前值 (Bind 回读会用设备值覆盖, 与启动流程同理)
                OutputCardControl cl0 = LeftCard(), cr0 = RightCard();
                int uiL = cl0 != null ? cl0.volTrack.Value : 100, uiR = cr0 != null ? cr0.volTrack.Value : 100, uiM = volMaster.Value;
                bool uiMl = cl0 != null && cl0.chkMute.Checked, uiMr = cr0 != null && cr0.chkMute.Checked,
                     uiMm = chkMuteMaster.Checked, uiMc = chkMuteCapture.Checked;

                BindDefaultVolumeSync("[恢复]");
                bool ok = BindChannelVolumeSync("[恢复]");
                RestoreUIValues(uiL, uiR, uiM, uiMl, uiMr, uiMm, uiMc); // UI 覆盖回来
                SetVolumeBindVisual(bindings.Master != null && ok);
                WriteUIValuesToDevice(); // 恢复后的 UI 值写回设备
                Log("[恢复] 重绑完成: 主=" + DevInfo(bindings.Master) + " / 左=" + DevInfo(bindings.Left) + " / 右=" + DevInfo(bindings.Right));
            }
            catch (Exception ex)
            {
                Log("[错误] 自动重绑失败: " + ex.Message);
                SetVolumeBindVisual(false);
            }
        }

        // Bind 回读会用设备值覆盖控件 → 恢复 UI 权威值 (volSyncing 包裹防写端点)
        void RestoreUIValues(int uiL, int uiR, int uiM, bool uiMl, bool uiMr, bool uiMm, bool uiMc)
        {
            volSyncing = true;
            OutputCardControl cl = LeftCard(), cr = RightCard();
            if (cl != null) { cl.volTrack.Value = uiL; cl.lblVol.Text = uiL + "%"; cl.chkMute.Checked = uiMl; }
            if (cr != null && cr != cl) { cr.volTrack.Value = uiR; cr.lblVol.Text = uiR + "%"; cr.chkMute.Checked = uiMr; }
            volMaster.Value = uiM;
            lblVolMaster.Text = uiM + "%";
            chkMuteMaster.Checked = uiMm;
            chkMuteCapture.Checked = uiMc;
            volSyncing = false;
        }

        // 左右声道滑条共用: UI 标签/自动静音 + 写入端点音量 (逐行等价于原两份重复实现)
        void OnChannelScroll(VolumeSlider slider, Label lbl, CheckBox chkMute, EndpointVolumeBinding b, string channel)
        {
            lbl.Text = slider.Value + "%";
            // 音量到0自动勾选静音; 拖离0自动取消 (主动操作优先于勾选状态)
            if (chkMute.Checked != (slider.Value == 0)) chkMute.Checked = slider.Value == 0;
            // 双向同步: 写入设备端点音量
            if (!volSyncing && b != null && b.IsValid)
            {
                b.WriteVolume(slider.Value / 100f);
                if (slider.Value > 0 && b.ReadMuted()) b.WriteMute(false);
            }
            SaveSettings();
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
                    Log("[警告] " + b.Role + "声道端点绑定已失效 (" + b.DeviceName + ")，数字标签已置灰，请停止分流并刷新设备");
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

        bool BindChannelVolumeSync(string stage)
        {
            OutputCardControl cl = LeftCard(), cr = RightCard();
            if (cl == null || cr == null)
            {
                Log("[错误] " + stage + " 输出卡片不足, 无法绑定左右音量");
                return false;
            }
            bool okL = BindOneChannel(stage, "左", cl.cmbDevice.SelectedIndex, bindings.BindLeft,
                LeftVol_OnVolumeNotification, cl.volTrack, cl.lblVol, cl.chkMute);
            bool okR = BindOneChannel(stage, "右", cr.cmbDevice.SelectedIndex, bindings.BindRight,
                RightVol_OnVolumeNotification, cr.volTrack, cr.lblVol, cr.chkMute);

            if (cl.cmbDevice.SelectedIndex >= 0 && cl.cmbDevice.SelectedIndex == cr.cmbDevice.SelectedIndex)
                Log("[提示] 左右输出选择了同一设备 " + DevInfo(bindings.Left) + "，两滑条联动同一端点");
            return okL && okR;
        }

        bool BindOneChannel(string stage, string channel, int idx,
            Func<string, Action<AudioVolumeNotificationData>, EndpointVolumeBinding> bind,
            Action<AudioVolumeNotificationData> handler,
            VolumeSlider slider, Label lbl, CheckBox chkMute)
        {
            if (idx < 0 || idx >= Devices.Count)
            {
                bind(null, null);
                return true;
            }

            var d = Devices[idx];
            var b = bind(d.ID, handler);
            if (b == null)
            {
                Log("[错误] " + stage + " " + channel + "声道音量绑定失败: 无法打开端点 " + d.FriendlyName);
                return false;
            }
            b.Invalidated -= OnBindingInvalidated;
            b.Invalidated += OnBindingInvalidated;

            volSyncing = true;
            int v = (int)Math.Round(b.ReadVolume() * 100);
            slider.Value = Math.Max(0, Math.Min(100, v));
            lbl.Text = v + "%";
            bool muted = b.ReadMuted();
            chkMute.Checked = muted;
            volSyncing = false;

            Log(stage + " " + channel + "声道音量绑定 → [" + idx + "] " + d.FriendlyName +
                " (设备值 " + v + "%" + (muted ? ", 静音" : "") + "，启动时按 UI 值写入)");
            return true;
        }

        void LeftVol_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            try { BeginInvoke(new Action(() => { var c = LeftCard(); if (c != null) HandleChannelVolumeNotification(data, bindings == null ? null : bindings.Left, c.volTrack, c.lblVol, c.chkMute); })); }
            catch { }
        }

        void RightVol_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            try { BeginInvoke(new Action(() => { var c = RightCard(); if (c != null) HandleChannelVolumeNotification(data, bindings == null ? null : bindings.Right, c.volTrack, c.lblVol, c.chkMute); })); }
            catch { }
        }

        // 左右声道通知共用: 设备值 → UI (仅 UI, volSyncing 内不写端点)
        void HandleChannelVolumeNotification(AudioVolumeNotificationData data, EndpointVolumeBinding b,
            VolumeSlider slider, Label lbl, CheckBox chkMute)
        {
            if (volSyncing || b == null || !b.IsValid) return;
            volSyncing = true;
            try
            {
                int v = Math.Max(0, Math.Min(100, (int)Math.Round(data.MasterVolume * 100)));
                if (slider.Value != v)
                {
                    slider.Value = v;
                    lbl.Text = v + "%";
                    SaveSettings();
                }
                // 与主音量同构: 音量0=静音不变式 + 外部静音跟随 (仅 UI, volSyncing 内不写端点)
                bool want = (v == 0) || data.Muted;
                if (chkMute.Checked != want) chkMute.Checked = want;
            }
            finally { volSyncing = false; }
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
                //    N 路: 先登记已选设备, 再从候选池轮流补。设备不够时允许复用同一台 ——
                //    内核按设备分组, 每台端点只开一条 WasapiOut, 多张卡片共用一台设备是合法用法。
                if (cards.Count == 0 || lanes.Count == 0)
                {
                    Log("[错误] 配置 3/4 没有输出卡片。");
                    return false;
                }
                if (LeftCard() == null || RightCard() == null)
                {
                    Log("[错误] 配置 3/4 输出卡片不足, 无法确定左右目标 (需要 FL/FR 或两张单声道卡片)。");
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
                for (int i = 0; i < cards.Count; i++)
                {
                    if (cards[i].cmbDevice.SelectedIndex >= 0) continue;
                    int chosen = -1;
                    for (int k = 0; k < cand.Count; k++)
                    {
                        int c = cand[(filled + k) % cand.Count];
                        if (!usedIdx.Contains(c)) { chosen = c; break; }
                    }
                    if (chosen < 0) chosen = cand[filled % cand.Count];   // 设备不够 → 复用
                    cards[i].SetDeviceIndex(chosen);
                    if (i < lanes.Count) lanes[i].DeviceId = DeviceIdAt(chosen);
                    usedIdx.Add(chosen);
                    filled++;
                    Log("[配置] 3/4 输出目标: 第 " + (i + 1) + " 路（" +
                        (i < lanes.Count ? ChannelNameOf(lanes[i].ChannelKey) : "?") + "）未选择 → 自动补选 [" +
                        chosen + "] " + Devices[chosen].FriendlyName);
                }
                if (filled == 0)
                    Log("[配置] 3/4 输出目标: 保持手动选择 (" + LaneSummary() + ")");

                // 4. 重绑左右音量同步到左右目标设备
                if (!BindChannelVolumeSync("[配置] 4/4"))
                {
                    Log("[错误] 配置 4/4 左右音量绑定失败，中止启动。");
                    return false;
                }

                // 回环检查 (只报错, 不改用户的选择): 任一路指向捕获设备都会采到自己的输出
                int badLane = -1;
                for (int i = 0; i < cards.Count; i++)
                    if (cards[i].cmbDevice.SelectedIndex == defIdx) { badLane = i; break; }
                if (badLane >= 0)
                {
                    Log("[错误] 回环检查未通过: 第 " + (badLane + 1) + " 路与捕获设备相同 [" + defIdx + "] " +
                        dev.FriendlyName + "，请改选输出设备 (程序不会替你改)。");
                    return false;
                }
                Log("[配置] 回环检查通过 (捕获输出与 " + cards.Count + " 路输出无冲突)");

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
            if (wi != 0) wi = Math.Max(5, Math.Min(3600, wi)); // 0=关闭, 1-4 按 5 秒
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

        /// <summary>左输出卡片: 规格里的 FL (单声道规格时 M)。</summary>
        OutputCardControl LeftCard()
        {
            for (int i = 0; i < lanes.Count && i < cards.Count; i++) if (lanes[i].ChannelKey == "FL") return cards[i];
            for (int i = 0; i < lanes.Count && i < cards.Count; i++) if (lanes[i].ChannelKey == "M") return cards[i];
            return cards.Count > 0 ? cards[0] : null;
        }

        /// <summary>右输出卡片: FR; 单声道规格时第二张 M 卡; 都没有则取与左不同的第一张。</summary>
        OutputCardControl RightCard()
        {
            // 左卡片先定下来 —— 右卡片绝不能和它是同一张:
            // 两张"卡片"指向同一对象时, 左右设备必然相同, 会被误判成
            // "左右选同一设备" 而触发自动改选, 设备就被悄悄换掉了。
            OutputCardControl l = LeftCard();
            for (int i = 0; i < lanes.Count && i < cards.Count; i++)
                if (lanes[i].ChannelKey == "FR" && cards[i] != l) return cards[i];
            int seen = 0;
            for (int i = 0; i < lanes.Count && i < cards.Count; i++)
                if (lanes[i].ChannelKey == "M" && cards[i] != l) { seen++; if (seen == 2) return cards[i]; }
            for (int i = 0; i < cards.Count; i++) if (cards[i] != l) return cards[i];
            return l ?? (cards.Count > 1 ? cards[1] : null);
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
            for (int i = 0; i < lanes.Count && i < cards.Count; i++)
            {
                string name = "未选";
                int idx = cards[i].cmbDevice.SelectedIndex;
                if (idx >= 0 && idx < Devices.Count) name = Devices[idx].FriendlyName;
                parts.Add(ChannelNameOf(lanes[i].ChannelKey) + "->" + name);
            }
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>把控件当前值写回模型 (存盘/启动前调用)。</summary>
        void SyncLanesFromCards()
        {
            var chans = CurrentSpecChannels();
            for (int i = 0; i < cards.Count && i < lanes.Count; i++)
            {
                cards[i].WriteBackToLane(chans);
                lanes[i].DeviceId = SafeDeviceId(cards[i].cmbDevice.SelectedIndex);
            }
        }

        /// <summary>声道键必须落在当前规格里。force=true 时按顺序重排 (换规格用)。</summary>
        void ReassignLanesToSpec(bool force)
        {
            var chans = CurrentSpecChannels();
            if (chans.Count == 0) return;
            for (int i = 0; i < lanes.Count; i++)
            {
                bool ok = false;
                for (int k = 0; k < chans.Count; k++) if (chans[k].Key == lanes[i].ChannelKey) { ok = true; break; }
                if (force || !ok) lanes[i].ChannelKey = chans[i % chans.Count].Key;
            }
        }

        void RebuildCards()
        {
            for (int i = 0; i < cards.Count; i++)
            {
                cards[i].Changed -= OnCardChanged;
                cards[i].VolumeScrolled -= OnCardVolumeScrolled;
                cards[i].RemoveRequested -= OnCardRemoveRequested;
                cardHost.Controls.Remove(cards[i]);
                cards[i].Dispose();
            }
            cards.Clear();

            var chans = CurrentSpecChannels();
            var devTexts = new List<string>();
            for (int i = 0; i < Devices.Count; i++) devTexts.Add("[" + i + "] " + Devices[i].FriendlyName);

            for (int i = 0; i < lanes.Count; i++)
            {
                var card = new OutputCardControl(lanes[i]);
                card.Changed += OnCardChanged;
                card.VolumeScrolled += OnCardVolumeScrolled;
                card.RemoveRequested += OnCardRemoveRequested;
                card.SetDevices(devTexts);
                card.SetDeviceIndex(catalog == null ? -1 : catalog.IndexOf(lanes[i].DeviceId));
                card.SetChannels(chans, lanes[i].ChannelKey);
                // 卡片自带圆角外框, 竖向靠紧 (间隙 = Margin.Bottom), 左右由 cardHost.Padding 撑开
                card.Margin = new Padding(0, 0, 0, OutputCardControl.CardGap);
                card.Width = CardWidth();
                cardHost.Controls.Add(card);
                cards.Add(card);
            }
            // 逐个加入过程中滚动条可能刚好切换, 统一再拉一次;
            // 滚动条的最终显隐要等布局跑完, 故再用 BeginInvoke 兜一次。
            FitCardWidths();
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)FitCardWidths);
            RefreshSpecBadges();
        }

        /// <summary>
        /// 日志框按日志面板的实时尺寸摆放: 左边距 15, 右边距 10 (文本框自己的竖滚动条
        /// 就贴在面板右缘), 下面距 8。拖分隔条改日志区高度时同步跟随。
        /// </summary>
        void LayoutLogPanel()
        {
            if (txtLog == null || splitContainer == null) return;
            Size cs = splitContainer.Panel2.ClientSize;
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

        /// <summary>
        /// 卡片宽度 = 卡片区可视宽度 (ClientSize 已扣掉竖滚动条)。
        /// 不再给下限: 一旦卡片比可视区宽就会冒出横向滚动条, 宁可挤也不横滚。
        /// </summary>
        int CardWidth()
        {
            if (cardHost == null) return OutputCardControl.CardWidth;
            return cardHost.ClientSize.Width;
        }

        /// <summary>
        /// 把所有卡片拉成同一宽度。
        /// 单张卡片各自算宽度不可靠: 卡片是逐个加进 FlowLayoutPanel 的,
        /// 滚动条可能在中途出现/消失, 后建的卡片量到的可视宽度就比前面的大
        /// 一个滚动条宽 (17px), 于是滑条长度对不齐。
        /// </summary>
        void FitCardWidths()
        {
            if (cards == null || cards.Count == 0) return;
            if (fittingWidths) return;      // 改宽度会触发容器重新布局, 防递归
            fittingWidths = true;
            try
            {
                int w = CardWidth();
                for (int i = 0; i < cards.Count; i++)
                    if (cards[i].Width != w) cards[i].Width = w;
            }
            finally { fittingWidths = false; }
        }

        void AddLaneCard(OutputLane preset)
        {
            var chans = CurrentSpecChannels();
            var lane = preset ?? new OutputLane();
            if (preset == null)
            {
                // 新卡片接第一个还没人接的声道; 都有人接则接第一个声道
                string key = chans.Count > 0 ? chans[0].Key : "FL";
                for (int i = 0; i < chans.Count; i++)
                {
                    bool used = false;
                    for (int k = 0; k < lanes.Count; k++) if (lanes[k].ChannelKey == chans[i].Key) { used = true; break; }
                    if (!used) { key = chans[i].Key; break; }
                }
                lane.ChannelKey = key;
                // 新卡片默认沿用上一路的设备: 一块 6ch 声卡接 5.1 时不必重复选六次
                if (lanes.Count > 0) lane.DeviceId = lanes[lanes.Count - 1].DeviceId;
            }
            lanes.Add(lane);
            RebuildCards();
            SaveSettings();
            Log("[输出] 新增一路: " + ChannelNameOf(lane.ChannelKey) + " (共 " + lanes.Count + " 路)");
        }

        void OnCardChanged(OutputCardControl card)
        {
            if (loadingSettings || loadingDevices) return;
            SyncLanesFromCards();
            SaveSettings();
            RefreshSpecBadges();
            // 端点卡片的静音写在设备端点上 (与音量同一套双向绑定/还原基线); 其余卡片走混音增益
            if (running && bindings != null && !volSyncing)
            {
                OutputCardControl cl = LeftCard(), cr = RightCard();
                if (card == cl && bindings.Left != null && bindings.Left.IsValid)
                    bindings.Left.WriteMute(card.chkMute.Checked);
                else if (card == cr && card != cl && bindings.Right != null && bindings.Right.IsValid)
                    bindings.Right.WriteMute(card.chkMute.Checked);
            }
            if (running) ApplyRoutingHot("卡片改动", false);
        }

        void OnCardVolumeScrolled(OutputCardControl card)
        {
            if (volSyncing) return;
            // 端点卡片(左/右)的音量/静音走设备端点双向绑定; 其余卡片的音量进混音增益
            OutputCardControl cl = LeftCard(), cr = RightCard();
            bool endpoint = false;
            if (card == cl && bindings != null && bindings.Left != null && bindings.Left.IsValid)
            {
                endpoint = true;
                bindings.Left.WriteVolume(card.volTrack.Value / 100f);
                if (card.volTrack.Value > 0 && bindings.Left.ReadMuted()) bindings.Left.WriteMute(false);
            }
            else if (card == cr && card != cl && bindings != null && bindings.Right != null && bindings.Right.IsValid)
            {
                endpoint = true;
                bindings.Right.WriteVolume(card.volTrack.Value / 100f);
                if (card.volTrack.Value > 0 && bindings.Right.ReadMuted()) bindings.Right.WriteMute(false);
            }
            // 拖动中的热更新: 只改增益, 不记日志 (一次拖动几十次事件, 记了就是刷屏)
            if (running && !endpoint) ApplyRoutingHot("音量", true);
            SaveSettings();
        }

        void OnCardRemoveRequested(OutputCardControl card)
        {
            if (loadingSettings) return;
            int idx = cards.IndexOf(card);
            if (idx < 0) return;
            if (lanes.Count <= 1) { Log("[输出] 至少保留一路输出"); return; }
            string who = ChannelNameOf(lanes[idx].ChannelKey);
            lanes.RemoveAt(idx);
            RebuildCards();
            SaveSettings();
            Log("[输出] 已移除: " + who + " (剩 " + lanes.Count + " 路)");
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
                    int n = 0;
                    for (int k = 0; k < lanes.Count; k++) if (lanes[k].ChannelKey == chans[i].Key) n++;
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
            lanes.Clear();
            cmbSpec.SelectedIndex = Math.Max(0, Math.Min(4, GetInt("Spec", 1)));

            int count = GetInt("OutCount", 0);
            if (count > 0)
            {
                for (int i = 0; i < count && i < 16; i++)
                {
                    string pk = "Out" + i + ".";
                    lanes.Add(new OutputLane
                    {
                        ChannelKey = StrOrDefault(settings.GetStr(pk + "Channel"), "FL"),
                        DeviceId = settings.GetStr(pk + "DeviceId") ?? "",
                        DelayMs = Math.Max(0, Math.Min(2000, GetInt(pk + "DelayMs", 0))),
                        Volume = Math.Max(0, Math.Min(100, GetInt(pk + "Volume", 100))),
                        Mute = GetInt(pk + "Mute", 0) != 0
                    });
                }
            }
            else
            {
                // 老配置 (DeviceLeft/DeviceRight 那套) → 左右两路
                lanes.Add(new OutputLane
                {
                    ChannelKey = "FL",
                    DeviceId = GetStr("DeviceLeft") ?? "",
                    DelayMs = Math.Max(0, Math.Min(2000, GetInt("DelayLeft", 0))),
                    Volume = Math.Max(0, Math.Min(100, GetInt("VolumeLeft", 100))),
                    Mute = GetInt("MuteLeft", 0) != 0
                });
                lanes.Add(new OutputLane
                {
                    ChannelKey = "FR",
                    DeviceId = GetStr("DeviceRight") ?? "",
                    DelayMs = Math.Max(0, Math.Min(2000, GetInt("DelayRight", 0))),
                    Volume = Math.Max(0, Math.Min(100, GetInt("VolumeRight", 100))),
                    Mute = GetInt("MuteRight", 0) != 0
                });
            }

            ReassignLanesToSpec(false);   // 只修正落不进规格的声道键
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
                settings.RemovePrefix("Out");   // 先清干净, 删过的路不留孤儿键
                settings.Put("OutCount", lanes.Count.ToString());
                for (int li = 0; li < lanes.Count && li < 16; li++)
                {
                    string pk = "Out" + li + ".";
                    settings.Put(pk + "Channel", lanes[li].ChannelKey);
                    settings.Put(pk + "DeviceId", lanes[li].DeviceId ?? "");
                    settings.Put(pk + "DelayMs", lanes[li].DelayMs.ToString());
                    settings.Put(pk + "Volume", lanes[li].Volume.ToString());
                    settings.Put(pk + "Mute", lanes[li].Mute ? "1" : "0");
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
            for (int i = 0; i < lanes.Count; i++)
                if (lanes[i].Volume == 0) lanes[i].Mute = true;

            if (any)
                Log("[初始化] 设置已加载: 主=" + volMaster.Value + "%/" + (chkMuteMaster.Checked ? "静音" : "开") +
                    " 输出 " + lanes.Count + " 路 (" + LaneSummary() + ")" +
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
        }

        // 数值标签有效/无效视觉 (仅 Running 即绑定生效时为黑)
        void SetVolumeBindVisual(bool bound)
        {
            Color c = bound ? SystemColors.ControlText : SystemColors.GrayText;
            OutputCardControl cl = LeftCard(), cr = RightCard();
            if (cl != null && cl.lblVol != null) cl.lblVol.ForeColor = c;
            if (cr != null && cr != cl && cr.lblVol != null) cr.lblVol.ForeColor = c;
            if (lblVolMaster != null) lblVolMaster.ForeColor = c;
        }

        // 启动失败: 回滚系统默认输出 + 解绑(含恢复捕获静音) + Failed 状态
        void StartFailed()
        {
            // 启动失败 → 恢复启动前 UI 权威快照 (Bind 回填的设备真值不留在控件/ini 里)
            if (hasSnap)
            {
                RestoreUIValues(snapL, snapR, snapM, snapMl, snapMr, snapMm, snapMc);
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
                OutputCardControl wcl = LeftCard(), wcr = RightCard();
                int wl = wcl != null ? wcl.volTrack.Value : 100, wr = wcr != null ? wcr.volTrack.Value : 100;
                bool wml = wcl != null && wcl.chkMute.Checked, wmr = wcr != null && wcr.chkMute.Checked;
                bindings.WriteValues(volMaster.Value, wl, wr, chkMuteCapture.Checked, wml, wmr);
                Log("[启动] 按UI值写入端点: 主=" + volMaster.Value + "% 左=" + wl +
                    "% 右=" + wr + "% (捕获消音=" + (chkMuteCapture.Checked ? "是" : "否") + ")");
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

                // 输出卡片: 逐张填充设备下拉并按 Lane.DeviceId 恢复
                for (int ci = 0; ci < cards.Count && ci < lanes.Count; ci++)
                {
                    cards[ci].SetDevices(devTexts);
                    int idx = catalog.IndexOf(lanes[ci].DeviceId);
                    if (idx < 0 && lanes[ci].DeviceId != "")
                    {
                        Log("[初始化] 输出 " + (ci + 1) + " 的设备已失效: " + lanes[ci].DeviceId);
                    }
                    if (idx < 0 && cards[ci].cmbDevice.Items.Count > 0)
                    {
                        // 未选/失效 → 默认接前两张卡用 0/1 号设备 (与旧版一致)
                        idx = ci < 2 ? ci : 0;
                        if (idx >= cards[ci].cmbDevice.Items.Count) idx = 0;
                        lanes[ci].DeviceId = DeviceIdAt(idx);
                    }
                    cards[ci].SetDeviceIndex(idx);
                }

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

            // 捕获 UI 当前值 (AutoConfigure 的 Bind 会用设备真值回填覆盖, 须提前存下)
            OutputCardControl scl = LeftCard(), scr = RightCard();
            int uiL = scl != null ? scl.volTrack.Value : 100, uiR = scr != null ? scr.volTrack.Value : 100, uiM = volMaster.Value;
            bool uiMl = scl != null && scl.chkMute.Checked, uiMr = scr != null && scr.chkMute.Checked,
                 uiMm = chkMuteMaster.Checked, uiMc = chkMuteCapture.Checked;
            snapL = uiL; snapR = uiR; snapM = uiM;
            snapMl = uiMl; snapMr = uiMr; snapMm = uiMm; snapMc = uiMc;
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

            if (scl == null || scr == null)
            {
                StartFailed();
                MessageBox.Show("没有可用的输出卡片，请先添加至少一路输出。", "提示");
                return;
            }
            // N 路: 每一路都必须选好设备; 同一设备承载多路是合法用法 (内核按设备分组,
            // 每台端点只开一条 WasapiOut), 因此不再禁止"多张卡片选中同一设备"。
            for (int i = 0; i < lanes.Count && i < cards.Count; i++)
            {
                if (cards[i].cmbDevice.SelectedIndex < 0)
                {
                    StartFailed();
                    MessageBox.Show("第 " + (i + 1) + " 路输出（" + ChannelNameOf(lanes[i].ChannelKey) +
                        "）还没有选择设备。", "提示");
                    return;
                }
            }

            int defIdx = catalog.IndexOf(catalog.DefaultRenderId());
            // 回环检查 (逐路): 任一路指向捕获设备 → 采集自己的输出, 立刻啸叫
            for (int i = 0; i < lanes.Count && i < cards.Count; i++)
            {
                if (cards[i].cmbDevice.SelectedIndex != defIdx) continue;
                var others = new List<string>();
                for (int k = 0; k < Devices.Count; k++)
                    if (k != defIdx) others.Add("[" + k + "] " + Devices[k].FriendlyName);
                StartFailed();
                MessageBox.Show(
                    "第 " + (i + 1) + " 路（" + ChannelNameOf(lanes[i].ChannelKey) + "）指向了捕获设备 [" + defIdx + "] " +
                    Devices[defIdx].FriendlyName + "，会造成声音回环！\n\n" +
                    "请把「捕获(虚拟)」改为虚拟设备(如 CABLE Input)，或给这一路换别的输出。\n其它可选:\n" +
                    string.Join("\n", others),
                    "设备冲突");
                return;
            }

            try
            {
                // 恢复被 Bind 回填覆盖的 UI 值, 以 UI 为唯一权威源写入端点
                RestoreUIValues(uiL, uiR, uiM, uiMl, uiMr, uiMm, uiMc);
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
                LeftCard() == null ? null : LeftCard().Lane,
                RightCard() == null ? null : RightCard().Lane,
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

        /// <summary>本次运行打开的全部输出端点 (= 路由模型里启用路的设备)。</summary>
        void FillRunSinkIds()
        {
            runSinkIds.Clear();
            SyncLanesFromCards();
            for (int i = 0; i < lanes.Count; i++)
            {
                string id = lanes[i].DeviceId;
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
                        Log("[启动] 端点绑定: 主=" + DevInfo(bindings.Master) + " / 左=" + DevInfo(bindings.Left) + " / 右=" + DevInfo(bindings.Right));
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
            txtLog.AppendText(ui + "\r\n\r\n");
            // 总量上限: 超 30 万字符截到 15 万, 防长时间运行文本无限增长 (文件不截断)
            int total = txtLog.TextLength;
            if (total > 300000)
            {
                // 直接删除头部超量字符, 避免 txtLog.Text 整串复制+Substring 的两次大分配
                txtLog.Select(0, total - 150000);
                txtLog.SelectedText = "";
            }
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();

            // 文件持久化 (写原文, 不截断)
            if (sessionLog != null) sessionLog.Write(stamp, level, msg);
        }
    }
}
