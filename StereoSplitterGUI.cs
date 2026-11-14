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
        NoWheelComboBox cmbLeft, cmbRight, cmbDefault, cmbLatency;
        NumericUpDown nudDelayL, nudDelayR;
        NumericUpDown nudWaterInterval;
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
        TrackBar volLeft, volRight, volMaster, volGain;
        Label lblVolLeft, lblVolRight, lblVolMaster, lblVolGain;
        Label lblDot, lblStatus;
        Button btnStart;
        Button btnRefresh;
        Button btnClearLog;
        Infrastructure.SessionLog sessionLog;
        SplitContainer splitContainer;
        TextBox txtLog;
        CheckBox chkMuteL, chkMuteR, chkMuteMaster, chkMuteCapture;
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
        string runCaptureId, runLeftId, runRightId;
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

        IReadOnlyList<EndpointInfo> Devices
        {
            get { return catalog.Endpoints; }
        }

        AudioEngine engine;
        bool running = false;

        // ---- 长期运行看门狗状态 ----
        // 输出线程停摆判定: RenderedSamples 连续 10 秒不涨 → WasapiOut 已不再拉数据
        const int WatchdogFreezeMs = 10000;
        long watchRenderedL = -1, watchRenderedR = -1;
        int watchRenderedTickL, watchRenderedTickR;
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
            Size = new Size(530, 486);
            MinimumSize = new Size(530, 486);
            Font = new Font("Microsoft YaHei UI", 9F);

            // 上下可拖动面板: 内容区 | 6px灰分隔条 | 日志区 (参照 tk.PanedWindow)
            splitContainer = new NoFocusSplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(0x88, 0x88, 0x88),
                FixedPanel = FixedPanel.Panel1,
                Panel1MinSize = 0,
                Panel2MinSize = 110,
                Size = new Size(500, 500),
                TabStop = false
            };
            // 显式恢复浅色面板, 阻断 BackColor(#888) 传播 → #888 只作用于分隔条
            splitContainer.Panel1.BackColor = SystemColors.Control;
            splitContainer.Panel2.BackColor = SystemColors.Control;
            splitContainer.Panel1.AutoScroll = true;
            // 初始340时日志区=111(客户457); 窗高减10 → 330 保持日志111不变
            splitContainer.SplitterDistance = 330;
            Controls.Add(splitContainer);

            var lblD = new Label { Text = "捕获(虚拟):", Location = new Point(15, 15), AutoSize = true };
            cmbDefault = new NoWheelComboBox { Location = new Point(110, 12), Width = 250, DropDownWidth = 375, DropDownStyle = ComboBoxStyle.DropDownList };
            chkMuteCapture = new CheckBox { Text = "消音", Location = new Point(363, 14), AutoSize = true };
            btnRefresh = new Button { Text = "刷新设备", Location = new Point(417, 11), Width = 72, Height = 23 };

            var lblL = new Label { Text = "左声道设备:", Location = new Point(15, 45), AutoSize = true };
            cmbLeft = new NoWheelComboBox { Location = new Point(110, 42), Width = 250, DropDownWidth = 375, DropDownStyle = ComboBoxStyle.DropDownList };
            var lblDL = new Label { Text = "延迟", Location = new Point(370, 45), AutoSize = true };
            nudDelayL = new NumericUpDown { Location = new Point(402, 42), Width = 60, Minimum = 0, Maximum = 2000 };
            var lblMsL = new Label { Text = "ms", Location = new Point(465, 45), AutoSize = true };

            var lblR = new Label { Text = "右声道设备:", Location = new Point(15, 75), AutoSize = true };
            cmbRight = new NoWheelComboBox { Location = new Point(110, 72), Width = 250, DropDownWidth = 375, DropDownStyle = ComboBoxStyle.DropDownList };
            var lblDR = new Label { Text = "延迟", Location = new Point(370, 75), AutoSize = true };
            nudDelayR = new NumericUpDown { Location = new Point(402, 72), Width = 60, Minimum = 0, Maximum = 2000 };
            var lblMsR = new Label { Text = "ms", Location = new Point(465, 75), AutoSize = true };

            var lblVL = new Label { Text = "左音量:", Location = new Point(15, 145), AutoSize = true };
            volLeft = new TrackBar { Location = new Point(80, 140), Width = 300, Height = 34, AutoSize = false, Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10 };
            lblVolLeft = new Label { Text = "100%", Location = new Point(390, 145), AutoSize = true };
            chkMuteL = new CheckBox { Text = "静音", Location = new Point(435, 144), AutoSize = true };

            var lblVR = new Label { Text = "右音量:", Location = new Point(15, 180), AutoSize = true };
            volRight = new TrackBar { Location = new Point(80, 175), Width = 300, Height = 34, AutoSize = false, Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10 };
            lblVolRight = new Label { Text = "100%", Location = new Point(390, 180), AutoSize = true };
            chkMuteR = new CheckBox { Text = "静音", Location = new Point(435, 179), AutoSize = true };

            var lblVM = new Label { Text = "主音量:", Location = new Point(15, 215), AutoSize = true };
            volMaster = new TrackBar { Location = new Point(80, 210), Width = 300, Height = 34, AutoSize = false, Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10 };
            lblVolMaster = new Label { Text = "100%", Location = new Point(390, 215), AutoSize = true };
            chkMuteMaster = new CheckBox { Text = "静音", Location = new Point(435, 214), AutoSize = true };

            var lblG = new Label { Text = "增益:", Location = new Point(15, 250), AutoSize = true };
            volGain = new TrackBar { Location = new Point(80, 245), Width = 300, Height = 34, AutoSize = false, Minimum = 0, Maximum = 300, Value = 100, TickFrequency = 10 };
            lblVolGain = new Label { Text = "100%", Location = new Point(390, 250), AutoSize = true };

            btnStart = new Button { Text = "开始分流", Location = new Point(190, 285), Width = 130, Height = 35 };

            lblDot = new Label { Text = "●", Location = new Point(17, 290), AutoSize = true, ForeColor = Color.Gray, Font = new Font("Microsoft YaHei UI", 10F) };
            lblStatus = new Label { Text = "状态: 就绪", Location = new Point(37, 295), AutoSize = true, ForeColor = Color.Gray };

            var lblLatency = new Label { Text = "启动延迟:", Location = new Point(15, 108), AutoSize = true };
            cmbLatency = new NoWheelComboBox { Location = new Point(85, 105), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbLatency.Items.AddRange(new object[] { "标准 (预缓冲100ms)", "低延迟 (50ms)", "极低 (25ms, 可能断音)", "超低 (15ms, 断音增多)", "极限 (5ms, 频繁断音)" });
            cmbLatency.SelectedIndex = 0;

            chkMinimizeToTray = new CheckBox { Text = "关闭时最小化到托盘", Location = new Point(290, 107), AutoSize = true };
            chkDrift = new CheckBox { Text = "校正", AutoSize = true, Checked = true };
            chkDrift.Location = new Point(chkMinimizeToTray.Right + 8, chkMinimizeToTray.Top);

            // 水位监控组: 日志面板标题行右侧, 清除日志按钮左边 (间隔0=关闭监控)
            var lblWater = new Label { Text = "水位监控:", Location = new Point(209, 8), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var lblWaterIv = new Label { Text = "间隔", Location = new Point(275, 8), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            nudWaterInterval = new NoWheelNumeric { Location = new Point(313, 5), Width = 60, Minimum = 0, Maximum = 3600, Value = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            var lblWaterSec = new Label { Text = "秒", Location = new Point(377, 8), AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right };

            var lblLogTitle = new Label { Text = "日志", Location = new Point(15, 8), AutoSize = true, Font = new Font("Microsoft YaHei UI", 10F) };
            btnClearLog = new Button
            {
                Text = "清除日志",
                Location = new Point(405, 4),
                Width = 85,
                Height = 22,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            txtLog = new TextBox
            {
                Location = new Point(15, 30),
                Size = new Size(475, 202),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.Fixed3D,
                BackColor = Color.FromArgb(250, 250, 250),
                Font = new Font("Consolas", 9F),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            var cmsLog = new ContextMenuStrip();
            cmsLog.Items.Add("复制", null, (s, e) => txtLog.Copy());
            cmsLog.Items.Add(new ToolStripSeparator());
            cmsLog.Items.Add("全选", null, (s, e) => txtLog.SelectAll());
            txtLog.ContextMenuStrip = cmsLog;

            splitContainer.Panel1.Controls.AddRange(new Control[] { lblD, cmbDefault, chkMuteCapture, btnRefresh, lblL, cmbLeft, lblDL, nudDelayL, lblMsL, lblR, cmbRight, lblDR, nudDelayR, lblMsR, lblLatency, cmbLatency, chkMinimizeToTray, chkDrift, lblVL, volLeft, lblVolLeft, chkMuteL, lblVR, volRight, lblVolRight, chkMuteR, lblVM, volMaster, lblVolMaster, chkMuteMaster, lblG, volGain, lblVolGain, lblDot, lblStatus, btnStart });
            splitContainer.Panel2.Controls.AddRange(new Control[] { lblLogTitle, lblWater, lblWaterIv, nudWaterInterval, lblWaterSec, btnClearLog, txtLog });

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
            nudDelayL.ValueChanged += (s, e) => { if (!loadingSettings) SaveSettings(); };
            nudDelayR.ValueChanged += (s, e) => { if (!loadingSettings) SaveSettings(); };
            nudWaterInterval.ValueChanged += (s, e) =>
            {
                int v = (int)nudWaterInterval.Value;
                if (v >= 1 && v <= 4) { nudWaterInterval.Value = 5; return; } // 下限5秒: 显示与实际一致
                SyncWaterTimer();
                if (!loadingSettings) SaveSettings();
            };
            cmbLeft.SelectedIndexChanged += (s, e) => { if (!loadingDevices) SaveSettings(); };
            cmbRight.SelectedIndexChanged += (s, e) => { if (!loadingDevices) SaveSettings(); };
            cmbDefault.SelectedIndexChanged += (s, e) => { if (!loadingDevices) SaveSettings(); };
            volLeft.Scroll += (s, e) => OnChannelScroll(volLeft, lblVolLeft, chkMuteL, bindings == null ? null : bindings.Left, "左声道");
            volRight.Scroll += (s, e) => OnChannelScroll(volRight, lblVolRight, chkMuteR, bindings == null ? null : bindings.Right, "右声道");
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
            chkMuteL.CheckedChanged += (s, e) =>
            {
                if (!volSyncing && bindings != null && bindings.Left != null)
                    bindings.Left.WriteMute(chkMuteL.Checked);
                SaveSettings();
            };
            chkMuteR.CheckedChanged += (s, e) =>
            {
                if (!volSyncing && bindings != null && bindings.Right != null)
                    bindings.Right.WriteMute(chkMuteR.Checked);
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
            cmbLeft.Enabled = !busy && !run;
            cmbRight.Enabled = !busy && !run;
            cmbDefault.Enabled = !busy && !run;
            btnRefresh.Enabled = !busy && !run;
            volLeft.Enabled = !busy;
            volRight.Enabled = !busy;
            volMaster.Enabled = !busy;
            volGain.Enabled = !busy;
            chkMuteL.Enabled = !busy;
            chkMuteR.Enabled = !busy;
            chkMuteMaster.Enabled = !busy;
            chkMuteCapture.Enabled = !busy;
            chkMinimizeToTray.Enabled = !busy;
            chkDrift.Enabled = !busy;
            cmbLatency.Enabled = !busy && !run;
            nudDelayL.Enabled = !busy && !run;
            nudDelayR.Enabled = !busy && !run;
            nudWaterInterval.Enabled = !busy;
            btnStart.Enabled = !busy;

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
            if (id != runCaptureId && id != runLeftId && id != runRightId) return;
            if (!warnedLostIds.Add(id)) return;
            string role = id == runCaptureId ? "捕获" : (id == runLeftId ? "左声道" : "右声道");
            Log("[警告] " + role + "设备已断开: " + FindNameById(id) +
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
                int uiL = volLeft.Value, uiR = volRight.Value, uiM = volMaster.Value;
                bool uiMl = chkMuteL.Checked, uiMr = chkMuteR.Checked,
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
            volLeft.Value = uiL;
            volRight.Value = uiR;
            volMaster.Value = uiM;
            lblVolLeft.Text = uiL + "%";
            lblVolRight.Text = uiR + "%";
            lblVolMaster.Text = uiM + "%";
            chkMuteL.Checked = uiMl;
            chkMuteR.Checked = uiMr;
            chkMuteMaster.Checked = uiMm;
            chkMuteCapture.Checked = uiMc;
            volSyncing = false;
        }

        // 左右声道滑条共用: UI 标签/自动静音 + 写入端点音量 (逐行等价于原两份重复实现)
        void OnChannelScroll(TrackBar slider, Label lbl, CheckBox chkMute, EndpointVolumeBinding b, string channel)
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
            bool okL = BindOneChannel(stage, "左", cmbLeft.SelectedIndex, bindings.BindLeft,
                LeftVol_OnVolumeNotification, volLeft, lblVolLeft, chkMuteL);
            bool okR = BindOneChannel(stage, "右", cmbRight.SelectedIndex, bindings.BindRight,
                RightVol_OnVolumeNotification, volRight, lblVolRight, chkMuteR);

            if (cmbLeft.SelectedIndex >= 0 && cmbLeft.SelectedIndex == cmbRight.SelectedIndex)
                Log("[提示] 左右声道选择了同一设备 " + DevInfo(bindings.Left) + "，两滑条联动同一端点");
            return okL && okR;
        }

        bool BindOneChannel(string stage, string channel, int idx,
            Func<string, Action<AudioVolumeNotificationData>, EndpointVolumeBinding> bind,
            Action<AudioVolumeNotificationData> handler,
            TrackBar slider, Label lbl, CheckBox chkMute)
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
            try { BeginInvoke(new Action(() => HandleChannelVolumeNotification(data, bindings == null ? null : bindings.Left, volLeft, lblVolLeft, chkMuteL))); }
            catch { }
        }

        void RightVol_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            try { BeginInvoke(new Action(() => HandleChannelVolumeNotification(data, bindings == null ? null : bindings.Right, volRight, lblVolRight, chkMuteR))); }
            catch { }
        }

        // 左右声道通知共用: 设备值 → UI (仅 UI, volSyncing 内不写端点)
        void HandleChannelVolumeNotification(AudioVolumeNotificationData data, EndpointVolumeBinding b,
            TrackBar slider, Label lbl, CheckBox chkMute)
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

                // 3. 左右目标若与默认冲突/无效 → 自动选前两个非默认非虚拟设备
                bool conflict =
                    cmbLeft.SelectedIndex < 0 || cmbRight.SelectedIndex < 0 ||
                    cmbLeft.SelectedIndex == defIdx || cmbRight.SelectedIndex == defIdx ||
                    cmbLeft.SelectedIndex == cmbRight.SelectedIndex;

                if (conflict)
                {
                    int l = -1, r = -1;
                    // 优先真实设备
                    for (int i = 0; i < Devices.Count; i++)
                    {
                        if (i == defIdx) continue;
                        if (Devices[i].IsVirtual) continue;
                        if (l < 0) l = i;
                        else { r = i; break; }
                    }
                    // 不够则任意非默认
                    if (l < 0 || r < 0)
                    {
                        for (int i = 0; i < Devices.Count; i++)
                        {
                            if (i == defIdx) continue;
                            if (l < 0) l = i;
                            else if (i != l) { r = i; break; }
                        }
                    }
                    if (l >= 0 && r >= 0 && l != r)
                    {
                        cmbLeft.SelectedIndex = l;
                        cmbRight.SelectedIndex = r;
                        Log("[配置] 3/4 左右目标: 自动改选(冲突/无效) L=[" + l + "] " + Devices[l].FriendlyName +
                            " / R=[" + r + "] " + Devices[r].FriendlyName);
                    }
                    else
                    {
                        Log("[错误] 配置 3/4 无法自动选择左右目标设备，可用设备不足。");
                        return false;
                    }
                }
                else
                {
                    Log("[配置] 3/4 左右目标: 保持手动选择 L=[" + cmbLeft.SelectedIndex + "] " +
                        Devices[cmbLeft.SelectedIndex].FriendlyName + " / R=[" + cmbRight.SelectedIndex + "] " +
                        Devices[cmbRight.SelectedIndex].FriendlyName);
                }

                // 4. 重绑左右音量同步到左右目标设备
                if (!BindChannelVolumeSync("[配置] 4/4"))
                {
                    Log("[错误] 配置 4/4 左右音量绑定失败，中止启动。");
                    return false;
                }

                // 回环检查
                if (cmbLeft.SelectedIndex == defIdx || cmbRight.SelectedIndex == defIdx)
                {
                    Log("[错误] 回环检查未通过: 捕获输出与左右目标相同。");
                    return false;
                }
                Log("[配置] 回环检查通过 (捕获输出与左右目标无冲突)");

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
            nudDelayL.Value = Math.Max(0, Math.Min(2000, GetInt("DelayLeft", 0)));
            nudDelayR.Value = Math.Max(0, Math.Min(2000, GetInt("DelayRight", 0)));
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
                settings.Put("DelayLeft", ((int)nudDelayL.Value).ToString());
                settings.Put("DelayRight", ((int)nudDelayR.Value).ToString());
                settings.Put("WaterIntervalSec", ((int)nudWaterInterval.Value).ToString());
                settings.Put("Gain", volGain.Value.ToString());
                settings.Put("DeviceLeft", DeviceIdAt(cmbLeft.SelectedIndex));
                settings.Put("DeviceRight", DeviceIdAt(cmbRight.SelectedIndex));
                settings.Put("DeviceDefault", DeviceIdAt(cmbDefault.SelectedIndex));
                settings.Put("VolumeLeft", volLeft.Value.ToString());
                settings.Put("VolumeRight", volRight.Value.ToString());
                settings.Put("VolumeMaster", volMaster.Value.ToString());
                settings.Put("MuteLeft", chkMuteL.Checked ? "1" : "0");
                settings.Put("MuteRight", chkMuteR.Checked ? "1" : "0");
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
                if (settings.Has("VolumeLeft"))
                {
                    int v = Math.Max(0, Math.Min(100, GetInt("VolumeLeft", 100)));
                    volLeft.Value = v;
                    lblVolLeft.Text = v + "%";
                    any = true;
                }
                if (settings.Has("MuteLeft"))
                {
                    bool m = GetInt("MuteLeft", 0) != 0;
                    chkMuteL.Checked = m;
                    any = true;
                }
                if (settings.Has("VolumeRight"))
                {
                    int v = Math.Max(0, Math.Min(100, GetInt("VolumeRight", 100)));
                    volRight.Value = v;
                    lblVolRight.Text = v + "%";
                    any = true;
                }
                if (settings.Has("MuteRight"))
                {
                    bool m = GetInt("MuteRight", 0) != 0;
                    chkMuteR.Checked = m;
                    any = true;
                }
            }
            finally
            {
                volSyncing = false;
            }

            // 存量设置音量为0 → 补勾静音 (与拖到0自动静音规则一致)
            if (volLeft.Value == 0) chkMuteL.Checked = true;
            if (volRight.Value == 0) chkMuteR.Checked = true;
            if (volMaster.Value == 0) chkMuteMaster.Checked = true;

            if (any)
                Log("[初始化] 设置已加载: 主=" + volMaster.Value + "%/" + (chkMuteMaster.Checked ? "静音" : "开") +
                    " 左=" + volLeft.Value + "%/" + (chkMuteL.Checked ? "静音" : "开") +
                    " 右=" + volRight.Value + "%/" + (chkMuteR.Checked ? "静音" : "开") +
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
            Activate();
            SetForegroundWindow(Handle);
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
            if (lblVolLeft != null) lblVolLeft.ForeColor = c;
            if (lblVolRight != null) lblVolRight.ForeColor = c;
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
            runCaptureId = runLeftId = runRightId = null;
            expectCaptureId = null;
            SetAppState(AppState.Failed);
        }

        // 启动流程中默认输出被 AutoConfigure 改走 → 失败时恢复到点开始时的默认
        void RollbackDefaultIfChanged()
        {
            RestoreDefaultOutput("启动失败");
        }

        /// <summary>把系统默认输出还原为本次启动前的设备 (停止/失败/启动失败共用)。
        /// 不还原的话默认仍挂在虚拟声卡上 → 应用声音全部无声, 直到下次启动才被纠正。</summary>
        void RestoreDefaultOutput(string reason)
        {
            if (preStartDefaultId == null || catalog == null) return;
            try
            {
                if (catalog.DefaultRenderId() != preStartDefaultId)
                {
                    string err;
                    if (SystemDefaultPolicy.TrySetDefault(preStartDefaultId, out err))
                        Log("[恢复] " + reason + "，默认输出 → " + FindNameById(preStartDefaultId));
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
                bindings.WriteValues(volMaster.Value, volLeft.Value, volRight.Value,
                    chkMuteCapture.Checked, chkMuteL.Checked, chkMuteR.Checked);
                Log("[启动] 按UI值写入端点: 主=" + volMaster.Value + "% 左=" + volLeft.Value +
                    "% 右=" + volRight.Value + "% (捕获消音=" + (chkMuteCapture.Checked ? "是" : "否") + ")");
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
                cmbLeft.Items.Clear();
                cmbRight.Items.Clear();
                cmbDefault.Items.Clear();

                for (int i = 0; i < Devices.Count; i++)
                {
                    string text = "[" + i + "] " + Devices[i].FriendlyName;
                    cmbLeft.Items.Add(text);
                    cmbRight.Items.Add(text);
                    cmbDefault.Items.Add(text);
                }

                int defIdx = catalog.IndexOf(catalog.DefaultRenderId());
                string defName = catalog.DefaultRenderName();
                if (cmbLeft.Items.Count > 0) cmbLeft.SelectedIndex = 0;
                if (cmbRight.Items.Count > 1) cmbRight.SelectedIndex = 1;
                if (cmbDefault.Items.Count > 0) cmbDefault.SelectedIndex = defIdx >= 0 ? defIdx : 0;

                // 恢复 ini 保存的设备选择 (按设备 ID, 找不到则保留上面的默认)
                RestoreDeviceSelection("DeviceLeft", cmbLeft, "左");
                RestoreDeviceSelection("DeviceRight", cmbRight, "右");
                RestoreDeviceSelection("DeviceDefault", cmbDefault, "捕获输出");

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
            int uiL = volLeft.Value, uiR = volRight.Value, uiM = volMaster.Value;
            bool uiMl = chkMuteL.Checked, uiMr = chkMuteR.Checked,
                 uiMm = chkMuteMaster.Checked, uiMc = chkMuteCapture.Checked;
            snapL = uiL; snapR = uiR; snapM = uiM;
            snapMl = uiMl; snapMr = uiMr; snapMm = uiMm; snapMc = uiMc;
            hasSnap = true;

            // 点击开始时自动配置: 设默认输出 + 绑定音量 + 自动选左右 + 回环检查
            if (!AutoConfigure())
            {
                StartFailed();
                MessageBox.Show("自动配置失败，已中止启动。\n详见日志。", "启动中止");
                return;
            }

            if (cmbLeft.SelectedIndex < 0 || cmbRight.SelectedIndex < 0)
            {
                StartFailed();
                MessageBox.Show("请选择左右设备。", "提示");
                return;
            }
            if (cmbLeft.SelectedIndex == cmbRight.SelectedIndex)
            {
                StartFailed();
                MessageBox.Show("左右设备不能相同。", "提示");
                return;
            }

            int defIdx = catalog.IndexOf(catalog.DefaultRenderId());
            if (defIdx == cmbLeft.SelectedIndex || defIdx == cmbRight.SelectedIndex)
            {
                var others = new List<string>();
                for (int i = 0; i < Devices.Count; i++)
                    if (i != cmbLeft.SelectedIndex && i != cmbRight.SelectedIndex)
                        others.Add("[" + i + "] " + Devices[i].FriendlyName);
                StartFailed();
                MessageBox.Show(
                    "捕获输出与左右目标相同，会造成声音回环！\n\n" +
                    "请把「捕获(虚拟)」改为虚拟设备(如 CABLE Input)。\n其它可选:\n" + string.Join("\n", others),
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
                runLeftId = DeviceIdAt(cmbLeft.SelectedIndex);
                runRightId = DeviceIdAt(cmbRight.SelectedIndex);
                runCaptureId = DeviceIdAt(cmbDefault.SelectedIndex);

                autoRestartCount = 0;   // 手动启动 → 重新给自动重建机会
                LaunchEngine(runLeftId, runRightId, runCaptureId, SnapshotStartParams(), false);
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
            public int PreMs, OutMs, DlyL, DlyR, GainPct, Mode, FixedPpm;
            public float Gain, Master;
        }

        StartParams SnapshotStartParams()
        {
            int prof = cmbLatency.SelectedIndex < 0 ? 0 : cmbLatency.SelectedIndex;
            return new StartParams
            {
                PreMs = LatencyProfiles[prof][0],
                OutMs = LatencyProfiles[prof][1],
                DlyL = (int)nudDelayL.Value,
                DlyR = (int)nudDelayR.Value,
                Gain = pendingGain,
                Master = CurrentMaster(),
                GainPct = (int)Math.Round(pendingGain * 100),
                Mode = driftMode,
                FixedPpm = fixedPpm
            };
        }

        // 手动启动与自动重建共用同一条启动链 (差别只在失败处理: 自动重建不弹框、不自动回滚)
        void LaunchEngine(string leftId, string rightId, string expectCap, StartParams p, bool autoRestart)
        {
            startAbort = false;
            startFinished.Reset();
            ThreadPool.QueueUserWorkItem(delegate
            {
                AudioEngine eng = null;
                try
                {
                    if (startAbort) throw new OperationCanceledException("启动已取消");
                    eng = new AudioEngine(leftId, rightId, Log, p.Mode, p.FixedPpm);
                    AudioEngine.StartOutcome outcome = eng.Start(p.Gain, p.Master, p.PreMs, p.OutMs, p.DlyL, p.DlyR,
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
                        runLeftId = leftId;
                        runRightId = rightId;
                        ResetWatchdog();
                        runSinceTick = Environment.TickCount;
                        SetAppState(AppState.Running);
                        ApplyVolumes();
                        Log("[启动] 端点绑定: 主=" + DevInfo(bindings.Master) + " / 左=" + DevInfo(bindings.Left) + " / 右=" + DevInfo(bindings.Right));
                        Log("[启动] 分流已启动 (增益 " + p.GainPct + "%)" +
                            (outcome == AudioEngine.StartOutcome.NoAudioYet ? "，等待音频数据" : ""));
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
            runCaptureId = runLeftId = runRightId = null;
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
            runCaptureId = runLeftId = runRightId = null;
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
            watchRenderedL = -1;
            watchRenderedR = -1;
            watchRenderedTickL = Environment.TickCount;
            watchRenderedTickR = Environment.TickCount;
            captureIdleLogged = false;
            captureIdleSince = 0;
            lastCaptureCheckTick = 0;
        }

        /// <summary>
        /// 每 2 秒判定一次链路活性。核心判据是"输出线程有没有被调用"(RenderedSamples 是否增长),
        /// 而不是水位高低 —— 源静音时水位本来就该是 0, 用水位判会天天误报。
        /// 左右分开判定: 总和会把单侧停摆掩盖掉 (另一侧照常增长)。
        /// </summary>
        void WatchdogTick()
        {
            var eng = engine;
            if (!running || eng == null) return;
            int now = Environment.TickCount;

            // 连续健康 5 分钟 → 清空重建计数, 避免一天里偶发两三次后就被永久锁死在"不再重试"
            if (autoRestartCount > 0 && runSinceTick != 0 && unchecked(now - runSinceTick) >= 300000)
                autoRestartCount = 0;

            // 10 秒不被调用 → 该侧输出端已停摆 (设备休眠/被独占/驱动挂起且没抛事件)
            long rl = eng.LeftRenderedSamples;
            if (rl != watchRenderedL) { watchRenderedL = rl; watchRenderedTickL = now; }
            else if (watchRenderedTickL != 0 && unchecked(now - watchRenderedTickL) >= WatchdogFreezeMs)
            {
                OnEngineStalled("输出左声道线程已 " + (WatchdogFreezeMs / 1000) +
                    " 秒未取数据（水位 " + eng.LeftBufferMs + "ms）—— 单只音箱休眠/被独占的典型表现");
                return;
            }
            long rr = eng.RightRenderedSamples;
            if (rr != watchRenderedR) { watchRenderedR = rr; watchRenderedTickR = now; }
            else if (watchRenderedTickR != 0 && unchecked(now - watchRenderedTickR) >= WatchdogFreezeMs)
            {
                OnEngineStalled("输出右声道线程已 " + (WatchdogFreezeMs / 1000) +
                    " 秒未取数据（水位 " + eng.RightBufferMs + "ms）—— 单只音箱休眠/被独占的典型表现");
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
                if (runCaptureId != null && !IsEndpointActive(runCaptureId))
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
            string leftId = runLeftId, rightId = runRightId, capId = runCaptureId;

            if (string.IsNullOrEmpty(leftId) || string.IsNullOrEmpty(rightId) || string.IsNullOrEmpty(capId))
            {
                Log("[错误] 缺少运行期设备信息，无法自动重建；请手动重新开始");
                GiveUp();
                return;
            }
            if (!IsEndpointActive(leftId) || !IsEndpointActive(rightId))
            {
                Log("[错误] 左右目标设备已不可用，放弃自动重建；请刷新设备后重新开始");
                GiveUp();
                return;
            }

            restarting = true;
            TearDownEngine();                 // 起播水位/缓冲全部重来, 与首次启动一致
            SetAppState(AppState.Starting);   // 期间 running=false → 看门狗自然停手
            LaunchEngine(leftId, rightId, capId, SnapshotStartParams(), true);
        }

        // 端点是否仍在"活动输出"列表里 (枚举失败一律按"还在"处理, 避免误判触发重建)
        static bool IsEndpointActive(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var coll = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    // MMDeviceCollection 本身无 Dispose; 索引器取出的 MMDevice 是新 RCW, 须逐个释放
                    for (int i = 0; i < coll.Count; i++)
                    {
                        var d = coll[i];
                        try { if (d.ID == id) return true; }
                        finally { try { d.Dispose(); } catch { } }
                    }
                }
                return false;
            }
            catch { return true; }
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
            // 左右音量/静音走设备端点(已双向绑定)，主音量走采样层
            engine.SetMaster(CurrentMaster());
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

    // 系统默认输出变化/设备断开监听 (COM回调线程 → 转发给主窗体处理)
    class DefaultChangeWatcher : IMMNotificationClient
    {
        readonly Action<DataFlow, Role, string> onDefaultChanged;
        readonly Action<string> onDeviceLost;

        public DefaultChangeWatcher(Action<DataFlow, Role, string> cb, Action<string> deviceLost)
        {
            onDefaultChanged = cb;
            onDeviceLost = deviceLost;
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            try { onDefaultChanged(flow, role, defaultDeviceId); } catch { }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState != DeviceState.Active) NotifyLost(deviceId);
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { NotifyLost(deviceId); }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        void NotifyLost(string deviceId)
        {
            try { if (onDeviceLost != null) onDeviceLost(deviceId); } catch { }
        }
    }

    // 捕获时钟域内的固定延迟线: 数据攒到目标水位后按到达顺序放出
    // 稳态 FIFO 恒等于 target 字节 → 延迟精确恒定, 不引入新时钟漂移
    class DelayLine
    {
        class Block
        {
            public byte[] Data;
            public int Len;
            public int Offset;
        }

        readonly Queue<Block> blocks = new Queue<Block>();
        // 块缓冲复用: 延迟开启时每块原本都要 new byte[len], 稳态后可从池中取回
        readonly Stack<byte[]> pool = new Stack<byte[]>();
        int pooledBytes;
        const int PoolMaxBytes = 1 << 20; // 池上限 1MB, 防止大块数据长期驻留
        readonly int targetBytes;
        readonly Action<byte[], int, int> emit;
        readonly object sync = new object(); // Write(采集线程) 与 Clear(UI线程) 并发保护
        long buffered;

        public DelayLine(int targetBytes, Action<byte[], int, int> emit)
        {
            this.targetBytes = Math.Max(0, targetBytes);
            this.emit = emit;
        }

        public void Write(byte[] data, int len)
        {
            if (len <= 0) return;
            lock (sync)
            {
                // 快路径 (默认延迟0的常态): 不复制不入队直接投递
                // emit 同步消费 (AddSamples 立即拷入缓冲), e.Buffer 在回调返回前用完 → 生命周期安全
                if (targetBytes == 0 && buffered == 0)
                {
                    emit(data, 0, len);
                    return;
                }

                byte[] copy = null;
                while (pool.Count > 0)
                {
                    byte[] candidate = pool.Pop();
                    pooledBytes -= candidate.Length;
                    if (candidate.Length >= len) { copy = candidate; break; }
                }
                if (copy == null) copy = new byte[len];
                Buffer.BlockCopy(data, 0, copy, 0, len);
                blocks.Enqueue(new Block { Data = copy, Len = len });
                buffered += len;

                while (buffered > targetBytes && blocks.Count > 0)
                {
                    var b = blocks.Peek();
                    int avail = b.Len - b.Offset;
                    int take = (int)Math.Min(avail, buffered - targetBytes);
                    // emit 抛异常时整块丢弃: 否则该块永远卡在队头, 之后每次 Write 都在同一处失败,
                    // 队列只进不出 → 长时间运行内存无界增长
                    if (EmitSafe(b.Data, b.Offset, take)) b.Offset += take;
                    else b.Offset = b.Len;
                    buffered -= take;
                    if (b.Offset >= b.Len)
                    {
                        blocks.Dequeue();
                        if (pooledBytes + b.Data.Length <= PoolMaxBytes)
                        {
                            pool.Push(b.Data);
                            pooledBytes += b.Data.Length;
                        }
                    }
                }
            }
        }

        bool EmitSafe(byte[] data, int offset, int len)
        {
            try { emit(data, offset, len); return true; }
            catch { return false; }
        }

        public void Clear()
        {
            lock (sync)
            {
                while (blocks.Count > 0)
                {
                    var b = blocks.Dequeue();
                    if (pooledBytes + b.Data.Length <= PoolMaxBytes)
                    {
                        pool.Push(b.Data);
                        pooledBytes += b.Data.Length;
                    }
                }
                buffered = 0;
            }
        }
    }

    class AudioEngine : IDisposable
    {
        WasapiLoopbackCapture loopback;
        WasapiOut leftOut, rightOut;
        BufferedWaveProvider leftBuf, rightBuf;
        DriftCorrectingProvider leftProvider, rightProvider;
        MMDevice leftDev, rightDev, captureDev;
        // 设备 ID: MMDevice 在 Start 所在线程(后台)自建, 不跨线程传递 (NAudio #214)
        readonly string leftDevId, rightDevId;
        readonly int driftMode, fixedPpm;
        Action<string> log;
        volatile int sampleRate;
        DelayLine leftLine, rightLine;
        bool splitErrLogged = false;
        // 起播时捕获源无数据 → 首帧到达时补一条"就绪"日志 (NoAudioYet 场景)
        volatile bool silentStartup = false;
        int firstDataLogged = 0;
        // 采集回调置位, 预缓冲等待用它替代 20ms 忙等轮询
        AutoResetEvent dataReady;
        // 输出缓冲溢出告警限流 (DiscardOnBufferOverflow 会静默丢块, 不记就等于丢音无迹可查)
        int lastOverflowWarnTick;

        // ---- 告警去重: 左右两路 provider 各自限流, 同一现象必然打两条 ----
        readonly object logGate = new object();
        readonly Dictionary<string, int> logCategoryTick = new Dictionary<string, int>();

        /// <summary>按告警类别 1.5s 去重: 左右两路同时触发同一类告警时只留第一条。
        /// 只用于 provider, 启动/停止等一次性日志仍走原 log, 避免被吞。</summary>
        void LogThrottled(string msg)
        {
            if (msg == null || log == null) return;
            string key = CategoryKey(msg);
            int now = Environment.TickCount;
            lock (logGate)
            {
                int last;
                if (logCategoryTick.TryGetValue(key, out last) && unchecked(now - last) < 1500) return;
                logCategoryTick[key] = now;
                if (logCategoryTick.Count > 64) logCategoryTick.Clear();   // 防无界增长
            }
            try { log(msg); } catch { }
        }

        /// <summary>"[左] [警告] 缓冲水位偏低: 调节水位 28ms …" → "[警告] 缓冲水位偏低"。
        /// 开头的声道标记必须跳过 —— 左右标签不同, 计入类别键会让去重完全失效。</summary>
        static string CategoryKey(string msg)
        {
            int start = 0;
            if (msg.Length > 3 && msg[0] == '[' && msg[2] == ']') start = 3;   // "[左]" / "[右]"
            int n = start;
            while (n < msg.Length && n < start + 40)
            {
                char c = msg[n];
                if (c == ':' || c == '(' || c == '+' || c == '-' || (c >= '0' && c <= '9')) break;
                n++;
            }
            return msg.Substring(start, n - start).TrimEnd();
        }

        // 长期运行看门狗: 输出/捕获线程会因设备休眠、被独占、拔插而"悄悄停止",
        // 不订阅这些事件时界面会一直显示"运行中"却早已无声 (实测有过 71 分钟)
        volatile bool stopping;             // 主动 Stop 期间这些事件也会触发 → 屏蔽
        bool disposed;                      // Dispose 幂等守卫
        int stallNotified;                  // Stalled 只上报一次 (左右常同时停)
        int lastCaptureTick;                // 最近一次收到捕获数据的 TickCount (0 = 尚未收到)
        /// <summary>链路意外中断 (参数为人话原因)。在音频线程触发, 订阅方须自行回 UI 线程。</summary>
        public event Action<AudioEngine, string> Stalled;

        // 起播余量: 覆盖输出端起播时对缓冲的预拉取。
        // 实测(蓝牙音箱) 95ms 目标起播后 5 秒内掉到 14ms, 峰值抽走约 80ms → 取 100ms 留裕度。
        // 多填的这部分由起播瞬态消耗, 稳态由时钟环回落到 driftTargetMs, 不增加稳态延迟。
        const int PrePullGuardMs = 100;

        public AudioEngine(string leftDevId, string rightDevId, Action<string> log)
            : this(leftDevId, rightDevId, log, 1, 0)
        { }

        public AudioEngine(string leftDevId, string rightDevId, Action<string> log, int driftMode, int fixedPpm)
        {
            this.leftDevId = leftDevId;
            this.rightDevId = rightDevId;
            this.log = log;
            this.driftMode = driftMode;
            this.fixedPpm = fixedPpm;
        }

        // Start 实际捕获的设备 ID (取启动时的系统默认输出, 可能与点开始时的选择不同)
        public string CaptureId
        {
            get
            {
                try { return captureDev != null ? captureDev.ID : null; } catch { return null; }
            }
        }

        /// <summary>最近一次收到捕获数据的时刻 (Environment.TickCount), 0 = 起播后尚未收到。</summary>
        public int LastCaptureTick
        {
            get { return lastCaptureTick; }
        }

        /// <summary>输出线程累计渲染样本数。看门狗用它判断输出线程是否还在被调用:
        /// 比看水位可靠 —— 水位在连通时也可能短暂不动, 而渲染计数冻结必然是停摆。
        /// 必须左右分开看: 只看总和的话, 一侧停摆另一侧照常涨, 总和仍在增长 → 单只音箱
        /// 休眠/被独占这种"无声但界面正常"的故障永远检不出来。</summary>
        public long LeftRenderedSamples
        {
            get { return leftProvider == null ? 0 : leftProvider.RenderedSamples; }
        }

        public long RightRenderedSamples
        {
            get { return rightProvider == null ? 0 : rightProvider.RenderedSamples; }
        }

        /// <summary>单侧当前瞬时水位 (ms), 仅供看门狗日志描述现场。</summary>
        public int LeftBufferMs
        {
            get { return leftProvider == null ? 0 : leftProvider.BufferedMs; }
        }

        public int RightBufferMs
        {
            get { return rightProvider == null ? 0 : rightProvider.BufferedMs; }
        }

        /// <summary>启动结果: 预缓冲是否真的拿到了数据 (旧实现超时也当成功, 导致空缓冲起播)。</summary>
        public enum StartOutcome
        {
            /// <summary>左右缓冲均达到目标水位。</summary>
            Ready,
            /// <summary>超时但已有部分数据, 开头可能欠载。</summary>
            Insufficient,
            /// <summary>超时且完全没有数据 (捕获源当前静音/无声源)。</summary>
            NoAudioYet
        }

        public StartOutcome Start(float gain, float master, int prebufferMs, int outLatencyMs, int leftDelayMs, int rightDelayMs,
            Func<bool> aborted)
        {
            return Start(gain, master, prebufferMs, outLatencyMs, leftDelayMs, rightDelayMs, aborted, null);
        }

        public StartOutcome Start(float gain, float master, int prebufferMs, int outLatencyMs, int leftDelayMs, int rightDelayMs,
            Func<bool> aborted, string expectedCaptureId)
        {
            // 三个 MMDevice 都在本线程创建: 跨线程使用 UI 线程创建的 MMDevice 会 QI IMMDevice 失败 E_NOINTERFACE (NAudio #214)
            var enumerator = new MMDeviceEnumerator();
            try
            {
                // 启动前校验: 设备可能在这几秒内被拔出, 用陈旧 ID 建 WasapiOut 只会抛 "尚未实现" 这类鬼话
                // 一次枚举同时校验左右 (原实现对左/右各枚举了一遍全量设备)
                string missing = FindMissingChannel(enumerator, leftDevId, rightDevId);
                if (missing != null)
                    throw new AudioDeviceException(missing + "不可用（可能已拔出或被禁用），请刷新设备后重试");

                leftDev = enumerator.GetDevice(leftDevId);
                rightDev = enumerator.GetDevice(rightDevId);
                captureDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            finally { try { enumerator.Dispose(); } catch { } }

            string captureName = DeviceCatalog.SafeName(captureDev);
            log("[启动] 捕获源: " + captureName);
            if (expectedCaptureId != null && CaptureId != null && CaptureId != expectedCaptureId)
                log("[警告] 当前系统默认输出与预期捕获设备不一致 (预期 " + expectedCaptureId + ")，已按当前默认输出捕获");

            loopback = new WasapiLoopbackCapture(captureDev);
            var wf = loopback.WaveFormat;
            sampleRate = wf.SampleRate;
            log("[启动] 格式: " + wf.SampleRate + "Hz / " + wf.Encoding + " / " + wf.BitsPerSample + "bit / " + wf.Channels + "ch");

            // 端点传输类型: 蓝牙 A2DP 自成一条链路, 偶发"咔哒"多出在链路层而不是缓冲层,
            // 写进日志才能在排查时立刻区分责任方
            string lt = DeviceCatalog.TransportName(leftDev), rt = DeviceCatalog.TransportName(rightDev);
            log("[启动] 端点传输: 左=" + DeviceCatalog.TransportText(lt) + " / 右=" + DeviceCatalog.TransportText(rt));
            if ((DeviceCatalog.IsBluetooth(lt) || DeviceCatalog.IsBluetooth(rt)) && outLatencyMs < 20)
                log("[提示] 蓝牙端点使用 " + outLatencyMs + "ms 输出缓冲偏紧；若链路上偶发\"咔哒\"，把「延迟」档位调到 25ms/20ms 或更高");

            var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(wf.SampleRate, 1);
            // 时钟环稳态目标: 档位越小目标越低, 上限 150ms
            int driftTargetMs = Math.Min(150, Math.Max(prebufferMs, outLatencyMs) + 80);
            int driftTargetSamples = (int)((long)wf.SampleRate * driftTargetMs / 1000);
            // 起播填充目标 = 稳态目标 + 余量 (见 PrePullGuardMs 注释)
            int fillTargetMs = driftTargetMs + PrePullGuardMs;
            int fillTargetSamples = (int)((long)wf.SampleRate * fillTargetMs / 1000);
            leftBuf = new BufferedWaveProvider(outFormat) { BufferDuration = TimeSpan.FromMilliseconds(1000), DiscardOnBufferOverflow = true };
            rightBuf = new BufferedWaveProvider(outFormat) { BufferDuration = TimeSpan.FromMilliseconds(1000), DiscardOnBufferOverflow = true };

            leftOut = new WasapiOut(leftDev, AudioClientShareMode.Shared, true, outLatencyMs);
            rightOut = new WasapiOut(rightDev, AudioClientShareMode.Shared, true, outLatencyMs);
            leftProvider = new DriftCorrectingProvider(leftBuf, driftTargetSamples, driftMode, fixedPpm, LogThrottled, "左");
            rightProvider = new DriftCorrectingProvider(rightBuf, driftTargetSamples, driftMode, fixedPpm, LogThrottled, "右");
            // Play 之前设好增益/主音量, 否则首帧以满音量(1.0)出声造成跳变
            leftProvider.Gain = gain;
            rightProvider.Gain = gain;
            leftProvider.Master = master;
            rightProvider.Master = master;
            leftOut.Init(leftProvider);
            rightOut.Init(rightProvider);

            // 输出/捕获线程意外退出 → 看门狗接管 (设备休眠、被独占、被移除都会走到这里)。
            // 主动 Stop 时也会触发, 由 stopping 标记屏蔽, 否则每次停止都会被当成故障。
            leftOut.PlaybackStopped += delegate(object s, StoppedEventArgs e) { OnOutputStopped("左", e); };
            rightOut.PlaybackStopped += delegate(object s, StoppedEventArgs e) { OnOutputStopped("右", e); };
            loopback.RecordingStopped += delegate(object s, StoppedEventArgs e) { OnCaptureStopped(e); };

            // 左右独立延迟线: 输入是交错多声道原始数据 → 按 blockAlign(帧) 算目标字节并天然对齐
            // (旧算法按 mono float 4字节算 → 延迟减半; &~3 未对齐帧 → 44100Hz 等场景字节错位出破音)
            int blockAlign = Math.Max(1, wf.BlockAlign);
            leftLine = new DelayLine((int)Math.Min(int.MaxValue, (long)wf.SampleRate * leftDelayMs / 1000) * blockAlign,
                (buf, off, len) => Split(buf, off, len, 0, leftBuf));
            rightLine = new DelayLine((int)Math.Min(int.MaxValue, (long)wf.SampleRate * rightDelayMs / 1000) * blockAlign,
                (buf, off, len) => Split(buf, off, len, 1, rightBuf));

            dataReady = new AutoResetEvent(false);
            loopback.DataAvailable += (s, e) =>
            {
                // 采样线程裸异常会直接杀进程 → 捕获并去重记一条
                try
                {
                    leftLine.Write(e.Buffer, e.BytesRecorded);
                    rightLine.Write(e.Buffer, e.BytesRecorded);
                    // 捕获活性时间戳: 看门狗用它区分"源本来就静音"和"捕获线程已死"
                    if (e.BytesRecorded > 0) lastCaptureTick = Environment.TickCount;
                    if (silentStartup && e.BytesRecorded > 0 && Interlocked.Exchange(ref firstDataLogged, 1) == 0)
                        log("[启动] 音频流已就绪 (捕获源开始有数据)");
                }
                catch (Exception ex)
                {
                    if (!splitErrLogged)
                    {
                        splitErrLogged = true;
                        log("[错误] 采样数据处理异常: " + ex.Message);
                    }
                }
                // 唤醒预缓冲等待 (事件在 Dispose 里等采集线程退出后才释放, 此处无释放竞态)
                try
                {
                    var ev = dataReady;
                    if (ev != null) ev.Set();
                }
                catch (ObjectDisposedException) { }
                catch { }
            };

            loopback.StartRecording();

            int fillBytes = fillTargetSamples * 4;
            int targetMs = driftTargetSamples * 1000 / wf.SampleRate;
            // 等待上限随填充目标与延迟补偿动态放宽 (延迟 2000ms 时固定 2s 会超时)
            int maxWaitMs = fillTargetMs + Math.Max(leftDelayMs, rightDelayMs) + 1000;
            int waitStart = Environment.TickCount;
            while (leftBuf.BufferedBytes < fillBytes || rightBuf.BufferedBytes < fillBytes)
            {
                // 关窗中止检查: FormClosing 置 startAbort 后最多 20ms 即退出
                if (aborted != null && aborted()) throw new OperationCanceledException("启动已取消");
                if (Environment.TickCount - waitStart >= maxWaitMs) break;
                var ev = dataReady;
                if (ev != null) ev.WaitOne(20); else Thread.Sleep(20);
            }

            // 先取水位再修剪: 修剪会把快路压回目标值, 之后再判断就永远"达标"
            int gotL = leftBuf.BufferedBytes;
            int gotR = rightBuf.BufferedBytes;

            // 修剪快路多攒的水位: 等待以慢路为终点时, 无延迟的快路会多攒数据, 抵消延迟线的时差
            // 两 buf 统一修剪到填充阈值 → 净声道差 = 延迟线的 (L - R)
            TrimTo(leftBuf, fillBytes);
            TrimTo(rightBuf, fillBytes);

            StartOutcome outcome;
            if (gotL >= fillBytes && gotR >= fillBytes)
            {
                outcome = StartOutcome.Ready;
                log("[启动] 预缓冲完成 (L=" + gotL + " R=" + gotR + " 字节, 填充目标 " + fillTargetMs +
                    "ms / 时钟目标 " + targetMs + "ms / 档位 " + prebufferMs + "ms / 输出 " + outLatencyMs + "ms)");
            }
            else if (gotL == 0 || gotR == 0)
            {
                outcome = StartOutcome.NoAudioYet;
                log("[警告] 捕获源 " + captureName + " 在 " + (maxWaitMs / 1000.0).ToString("0.0") +
                    " 秒内没有音频数据 (L=" + gotL + " R=" + gotR + " 字节)，已按静音启动，播放声音后会自动补齐");
            }
            else
            {
                outcome = StartOutcome.Insufficient;
                log("[警告] 预缓冲不足 (L=" + gotL + " R=" + gotR + " / 目标 " + fillBytes +
                    " 字节)，开头可能出现欠载");
            }

            silentStartup = (outcome == StartOutcome.NoAudioYet);
            log("[启动] 时钟漂移补偿: " + (driftMode == 0 ? "关闭" : driftMode == 2 ? "固定 " + fixedPpm + "ppm" : "自适应") + ", 比例上限 ±0.50%");
            if (leftDelayMs > 0 || rightDelayMs > 0)
                log("[启动] 延迟补偿: 左=" + leftDelayMs + "ms 右=" + rightDelayMs + "ms");

            if (aborted != null && aborted()) throw new OperationCanceledException("启动已取消");

            leftOut.Play();
            rightOut.Play();
            log("[启动] 输出 L=" + DeviceCatalog.SafeName(leftDev));
            log("[启动] 输出 R=" + DeviceCatalog.SafeName(rightDev));
            return outcome;
        }

        // 一次枚举同时校验左右声道设备是否仍在活动输出列表中; 返回缺失的声道名, 都正常返回 null
        static string FindMissingChannel(MMDeviceEnumerator enumerator, string leftId, string rightId)
        {
            if (string.IsNullOrEmpty(leftId)) return "左声道设备";
            if (string.IsNullOrEmpty(rightId)) return "右声道设备";
            bool leftOk = false, rightOk = false;
            try
            {
                var coll = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                for (int i = 0; i < coll.Count; i++)
                {
                    string id = null;
                    try { id = coll[i].ID; } catch { }
                    if (id == leftId) leftOk = true;
                    if (id == rightId) rightOk = true;
                    if (leftOk && rightOk) break;
                }
            }
            catch { }
            if (!leftOk) return "左声道设备";
            if (!rightOk) return "右声道设备";
            return null;
        }

        public void SetMaster(float m)
        {
            // 主音量走采样层: WasapiOut.Volume 实际是设备端点音量，会与左右滑条的端点绑定冲突
            if (leftProvider != null) leftProvider.Master = m;
            if (rightProvider != null) rightProvider.Master = m;
        }

        public void SetGain(float gain)
        {
            if (leftProvider != null) leftProvider.Gain = gain;
            if (rightProvider != null) rightProvider.Gain = gain;
        }

        public void SetDriftMode(int mode, int fixedPpm)
        {
            if (leftProvider != null) leftProvider.SetMode(mode, fixedPpm);
            if (rightProvider != null) rightProvider.SetMode(mode, fixedPpm);
        }

        public string BufferStatus()
        {
            try
            {
                if (leftProvider == null || rightProvider == null || sampleRate <= 0) return "-";
                int minL = leftProvider.TakeMinBufferedMs();
                int minR = rightProvider.TakeMinBufferedMs();
                // 调节水位在前: 它是校正环真正稳定的量, 也是判断是否需要扩缓冲的依据。
                // 瞬时值紧随其后: 含块粒度锯齿, 只用于看单次回调的抖动幅度。
                string s = "调节 L=" + leftProvider.RegulatedMs + "ms R=" + rightProvider.RegulatedMs + "ms" +
                    " 瞬时 L=" + leftProvider.BufferedMs + "ms R=" + rightProvider.BufferedMs + "ms" +
                    " 校正 L=" + Ppm(leftProvider.RatioPpm) + " R=" + Ppm(rightProvider.RatioPpm) +
                    " 最低 L=" + minL + "ms R=" + minR + "ms";
                long ul = leftProvider.UnderrunSamples, ur = rightProvider.UnderrunSamples;
                if (ul > 0 || ur > 0) s += " 欠载 L=" + ul + " R=" + ur;
                // 只在真出现时才显示: 迟到 = WasapiOut 没按时取数据(设备侧可能已欠载), 卡顿 = 音频线程被抢占
                string tl = leftProvider.TimingInfo, tr = rightProvider.TimingInfo;
                if (tl != null) s += " 左" + tl;
                if (tr != null) s += " 右" + tr;
                return s;
            }
            catch { return "-"; }
        }

        static string Ppm(double v)
        {
            return v.ToString("+0;-0;0") + "ppm";
        }

        // 停止事件在音频线程触发; stopping 期间(自己停的)一律忽略
        void OnOutputStopped(string side, StoppedEventArgs e)
        {
            if (stopping) return;
            string why = "输出" + side + "声道已停止";
            if (e != null && e.Exception != null) why += ": " + e.Exception.Message;
            else why += "（设备休眠/被独占/已移除）";
            RaiseStalled(why);
        }

        void OnCaptureStopped(StoppedEventArgs e)
        {
            if (stopping) return;
            string why = "捕获已停止";
            if (e != null && e.Exception != null) why += ": " + e.Exception.Message;
            RaiseStalled(why);
        }

        // 只上报第一次: 左右两路往往同时停, 后续还可能连环触发
        void RaiseStalled(string why)
        {
            if (Interlocked.Exchange(ref stallNotified, 1) != 0) return;
            var handler = Stalled;
            if (handler != null) { try { handler(this, why); } catch { } }
            else if (log != null) { try { log("[错误] " + why); } catch { } }  // 无人接管时才自己记, 避免与订阅方重复
        }

        public void Stop()
        {
            stopping = true; // 先置位: 下面的 Stop 会同步触发 Stopped 事件
            if (loopback != null) { try { loopback.StopRecording(); } catch { } }
            if (leftOut != null) { try { leftOut.Stop(); } catch { } }
            if (rightOut != null) { try { rightOut.Stop(); } catch { } }
            if (leftLine != null) leftLine.Clear();
            if (rightLine != null) rightLine.Clear();
        }

        static void TrimTo(BufferedWaveProvider buf, int targetBytes)
        {
            int excess = buf.BufferedBytes - targetBytes;
            if (excess <= 0) return;
            var tmp = new byte[excess];
            while (excess > 0)
            {
                int got = buf.Read(tmp, 0, excess);
                if (got <= 0) break;
                excess -= got;
            }
        }

        // Split 输出缓冲复用: 只在采样线程(DataAvailable→emit)访问, 无并发
        byte[] splitOut;
        float[] splitF;
        float[] splitSrcF;

        void Split(byte[] data, int offset, int bytes, int channel, BufferedWaveProvider target)
        {
            var f = loopback.WaveFormat;
            int bps = f.BitsPerSample / 8;
            int ch = f.Channels;
            int frame = bps * ch;
            if (frame <= 0 || bytes < frame) return;
            // 单声道捕获无第二声道 → 取模回 ch0 重复 (越界读会抛异常杀死进程)
            if (ch > 0) channel = channel % ch;
            int frames = bytes / frame;

            // 复用缓冲: 旧实现每样本 BitConverter.GetBytes 分配 byte[4] (~9.6万次/秒 → GC 抖动)
            if (splitOut == null || splitOut.Length < frames * 4) splitOut = new byte[frames * 4];
            if (splitF == null || splitF.Length < frames) splitF = new float[frames];

            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat;
            bool is16 = f.BitsPerSample == 16;
            if (isFloat && f.BitsPerSample == 32)
            {
                // 常用路径 (WASAPI 混音格式): 整块复制到 float[] 后按帧取声道,
                // 避免逐样本 BitConverter.ToSingle (与逐样本读取结果完全一致)
                int total = frames * ch;
                if (splitSrcF == null || splitSrcF.Length < total) splitSrcF = new float[total];
                Buffer.BlockCopy(data, offset, splitSrcF, 0, total * 4);
                for (int i = 0; i < frames; i++)
                    splitF[i] = splitSrcF[i * ch + channel];
            }
            else if (is16)
            {
                for (int i = 0; i < frames; i++)
                    splitF[i] = BitConverter.ToInt16(data, offset + i * frame + channel * bps) / 32768f;
            }
            else if (isFloat)
            {
                // 非 32bit 浮点: 保持原逐样本逻辑
                for (int i = 0; i < frames; i++)
                    splitF[i] = BitConverter.ToSingle(data, offset + i * frame + channel * bps);
            }
            else
            {
                for (int i = 0; i < frames; i++) splitF[i] = 0f;
            }
            Buffer.BlockCopy(splitF, 0, splitOut, 0, frames * 4);
            // 输出缓冲已满 → DiscardOnBufferOverflow 会整块静默丢弃 (听起来是断音), 限流记一条
            int need = frames * 4;
            if (target.BufferedBytes + need > target.BufferLength && WarnOverflow())
                log("[警告] 输出缓冲已满 (" + (target == leftBuf ? "左" : "右") + "声道 " + target.BufferedBytes + "/" +
                    target.BufferLength + " 字节)，本块已丢弃；输出设备可能卡顿或时钟校正跟不上");
            target.AddSamples(splitOut, 0, need);
        }

        // 溢出告警限流: 同一类事件 60 秒最多一条, 避免采样线程刷屏
        bool WarnOverflow()
        {
            int now = Environment.TickCount;
            if (lastOverflowWarnTick != 0 && now - lastOverflowWarnTick < 60000) return false;
            lastOverflowWarnTick = now;
            return true;
        }

        public void Dispose()
        {
            // 幂等: GiveUp / StopEngine / FormClosing 三条路径都会走到, 重复释放会抛
            // ObjectDisposedException, 而调用方多为 catch{} 兜底 → 真实故障被掩盖
            if (disposed) return;
            disposed = true;

            try { Stop(); } catch { }

            if (loopback != null) { try { loopback.Dispose(); } catch { } loopback = null; }
            // 采集线程已退出 (StopRecording 等待其结束) → 此时释放等待句柄无竞态
            if (dataReady != null)
            {
                try { dataReady.Close(); } catch { }
                dataReady = null;
            }
            if (leftOut != null) { try { leftOut.Dispose(); } catch { } leftOut = null; }
            if (rightOut != null) { try { rightOut.Dispose(); } catch { } rightOut = null; }
            if (captureDev != null)
            {
                try { captureDev.Dispose(); } catch { }
                captureDev = null;
            }
            // 左右设备由本引擎自建 → 由本引擎释放 (原 UI 所有的设备不再传入)
            if (leftDev != null)
            {
                try { leftDev.Dispose(); } catch { }
                leftDev = null;
            }
            if (rightDev != null)
            {
                try { rightDev.Dispose(); } catch { }
                rightDev = null;
            }
        }
    }

    class DriftCorrectingProvider : ISampleProvider
    {
        const int RingSize = 64;
        const int UpdateSamples = 1024;
        // 增益调度: 本环是"积分型对象 + 速率扰动", 时间常数 τ = T/(Cin*Kp)。
        // Kp=0.004 时 τ=23.75s、穿越频率 0.0067Hz, 而蓝牙设备时钟漂移的时间尺度是 40–60s,
        // 环与扰动同速 → 压不掉 (实测稳态摆动 std 8.1ms、均值偏差 +3.9ms)。
        // 原参数相位裕度尚有 85°, 提到 0.02 后 τ=4.7s、穿越 0.031Hz, PM 仍有 67°。
        // 排空段 (起播 195→95ms, 误差 >100%) 必须留在原增益, 否则会顶到 ±0.5% 限幅、
        // 音高滑移变陡; 故按误差分两段, 切换带滞回 + 约 400ms 斜坡, 避免校正值跳变。
        const double KpSteady = 0.02;
        const double KpFill = 0.004;
        const double GainEnterErr = 0.20;   // 误差降到 20% 以下 → 切稳态增益
        const double GainExitErr = 0.50;    // 误差回升到 50% 以上 → 退回填充增益
        // 兜底切换: 只用"误差<20%"判会死锁 —— 低增益下的稳态误差 = 速率差/KpFill,
        // 速率差 1500ppm 时稳态误差就是 37%, 永远够不到阈值, 环卡在低增益上, 偏移一直是 37ms。
        // 排空段最长约 100s (0.2% 速率下降 100ms), 128s 后必然结束, 故加一个时间兜底。
        const int SwitchAfterUpdates = 6000;   // 6000 × 1024 / 48k ≈ 128s
        const double MaxRatio = 0.005;
        // 补水位闸门的进入阈值(占目标的比例)。健康会话的瞬时水位会在 22–36ms 徘徊(块粒度锯齿),
        // 阈值必须压到它以下; 真断流时水位只剩小环里那 1ms, 任何合理阈值都能在一个块内触发。
        const double GateEnterRatio = 0.15;
        // 触顶判定窗口: 500 次更新 ≈ 10.7s (48kHz / 1024)
        const int ClampWarnUpdates = 500;
        // 同类异常事件限流: 采样线程上每 1024 样本就评估一次, 不限流会刷屏
        const int WarnIntervalMs = 60000;

        readonly BufferedWaveProvider src;
        readonly int targetSamples;
        readonly Action<string> log;
        readonly string tag;   // 声道标识: 左右两路会报出内容相同的告警, 不带标识分不清是哪路
        volatile int mode;          // 1=自适应 0=关闭 2=固定比例
        double fixedRatio;
        readonly byte[] tmp = new byte[RingSize * 4];
        readonly float[] tmpF = new float[RingSize];
        readonly float[] ring = new float[RingSize];
        int head;
        volatile int count;
        float prev;
        double phase;
        double smoothedTotal;
        // 供状态日志读的"调节水位": smoothedTotal 是 double 不能 volatile, 单独存毫秒整数
        volatile int smoothedMs;
        // 增益调度当前状态 + 斜坡后的实际 Kp (避免切换瞬间 ratio 阶跃 → 音高跳变)
        bool fastGain;
        double kpNow = KpFill;
        int updates;                // UpdateRatio 累计次数 (兜底切换用)
        int updateCounter;
        int minTotalSinceStatus = int.MaxValue;
        // 长整型: 静音整夜(48000/s)约 12.4 小时就会把 int 顶成负数, 欠载统计从此静默失效
        long underrunSamples;
        volatile float gain = 1.0f;
        volatile float master = 1.0f;
        volatile float ratio = 1.0f;
        // 欠载软收尾: 直接输出 0 是从任意电平硬切, 听感就是一声"咔";
        // 改为把最后一次输出按 3ms 指数衰减到 0, 恢复时再 3ms 淡入
        float lastOut;
        float fadeIn = 1f;
        float fadeStep;
        float decayCoef;
        // 欠载后的"补水位"闸门: 断粮即停止消费, 等水位回到目标 95% 再恢复。
        // 旧行为是"有多少吃多少", 输出端会一直贴着 0 水位啃零头 ——
        // 实测一次 0.8 秒的捕获断流会变成 7–10 秒的低水位/断音, 而暂停补满只要约 100ms。
        bool holding;
        int starveStreak;           // 连续处于"水位极低"的次数, 用于滤掉单次采样的锯齿谷底
        // 事件型诊断: 水位过低 / 校正触顶 (限流 + 恢复补记)
        bool lowWarned;
        int lastLowWarnTick;
        int clampStreak;
        double clampStartTotal;     // 本轮触顶开始时的水位 (判断"到底有没有在回升")
        int lastClampWarnTick;
        // 输出线程活性计数: 只在采样线程自增, UI 线程读取 (64 位进程读 long 不会撕裂)。
        // 冻结即代表 WasapiOut 不再调用 Read → 输出端停摆, 比看水位可靠。
        long renderedSamples;
        // 输出线程时序诊断: 我们的水位计数只能看见"我们给不出数据", 看不见
        // "WasapiOut 没按时来取" —— 后者会让设备侧自己欠载(蓝牙 A2DP 尤其容易),
        // 听感同样是"咔", 但日志里一片安静。这里量调用间隔与单次耗时把它暴露出来。
        long lastReadTs;
        int lateReads, slowReads;
        double maxGapMs, maxDurMs;
        int lastTimingWarnTick;

        public DriftCorrectingProvider(BufferedWaveProvider src, int targetSamples)
            : this(src, targetSamples, 1, 0, null)
        { }

        public DriftCorrectingProvider(BufferedWaveProvider src, int targetSamples, int mode, int fixedPpm)
            : this(src, targetSamples, mode, fixedPpm, null)
        { }

        public DriftCorrectingProvider(BufferedWaveProvider src, int targetSamples, int mode, int fixedPpm, Action<string> log)
            : this(src, targetSamples, mode, fixedPpm, log, null)
        { }

        public DriftCorrectingProvider(BufferedWaveProvider src, int targetSamples, int mode, int fixedPpm, Action<string> log, string tag)
        {
            this.src = src;
            this.log = log;
            this.tag = tag;
            this.targetSamples = Math.Max(1, targetSamples);
            this.mode = mode;
            this.smoothedTotal = this.targetSamples;
            this.smoothedMs = targetSamples * 1000 / Math.Max(1, src.WaveFormat.SampleRate);
            // 3ms 收尾/淡入: 48kHz 下约 144 样本
            float fadeSamples = Math.Max(1f, 0.003f * src.WaveFormat.SampleRate);
            fadeStep = 1f / fadeSamples;
            decayCoef = (float)Math.Exp(-1.0 / fadeSamples);
            if (mode == 0) fixedRatio = 1.0;
            else if (mode == 2)
            {
                int ppm = Math.Max(-5000, Math.Min(5000, fixedPpm));
                fixedRatio = 1.0 + ppm / 1000000.0;
            }
            else fixedRatio = 1.0;
            ratio = (float)fixedRatio;
        }

        public void SetMode(int newMode, int fixedPpm)
        {
            if (newMode == 2)
            {
                int ppm = Math.Max(-5000, Math.Min(5000, fixedPpm));
                fixedRatio = 1.0 + ppm / 1000000.0;
                ratio = (float)fixedRatio;
            }
            else if (newMode == 0)
            {
                fixedRatio = 1.0;
                ratio = 1f;
            }
            else
            {
                smoothedTotal = targetSamples;
                // 切回自适应时复位调度状态, 否则沿用上一次的 Kp 会产生一次阶跃
                fastGain = false;
                kpNow = KpFill;
            }
            mode = newMode;
        }

        public float Gain { get { return gain; } set { gain = value; } }

        public float Master { get { return master; } set { master = value; } }

        public WaveFormat WaveFormat { get { return src.WaveFormat; } }

        public int BufferedMs
        {
            get
            {
                int sr = src.WaveFormat.SampleRate;
                return sr > 0 ? (int)((long)(src.BufferedBytes / 4 + count) * 1000 / sr) : 0;
            }
        }

        public double RatioPpm { get { return ((double)ratio - 1.0) * 1e6; } }

        /// <summary>调节水位: 校正环实际调节的那个量 (2.1s 低通后的水位)。
        /// 与 BufferedMs 的区别: 后者是瞬时值, 含 WASAPI 回调块粒度造成的 ±18ms 锯齿,
        /// 日志里只看瞬时值会把块噪声误判成真实延迟抖动。</summary>
        public int RegulatedMs { get { return smoothedMs; } }

        public long UnderrunSamples { get { return Interlocked.Read(ref underrunSamples); } }

        /// <summary>累计渲染样本数 (只增不减), 看门狗用它判定输出线程是否还在跑。</summary>
        public long RenderedSamples { get { return renderedSamples; } }

        /// <summary>输出线程时序摘要; 全部正常时返回 null (状态行不显示, 免得干扰读数)。</summary>
        public string TimingInfo
        {
            get
            {
                if (lateReads == 0 && slowReads == 0) return null;
                return "迟到 " + lateReads + "次/最大 " + maxGapMs.ToString("0") + "ms" +
                       " 卡顿 " + slowReads + "次/最大 " + maxDurMs.ToString("0") + "ms";
            }
        }

        public int TakeMinBufferedMs()
        {
            int v = Interlocked.Exchange(ref minTotalSinceStatus, int.MaxValue);
            if (v == int.MaxValue) return BufferedMs;
            int sr = src.WaveFormat.SampleRate;
            return sr > 0 ? (int)((long)v * 1000 / sr) : 0;
        }

        public int Read(float[] samples, int offset, int countRequested)
        {
            long t0 = Stopwatch.GetTimestamp();
            renderedSamples += countRequested;   // 活性计数: 只要被调用就说明输出线程还活着
            // 时序诊断①: 两次调用间隔远大于块时长 → WasapiOut 没按时来取, 设备侧会自己欠载
            if (lastReadTs != 0)
            {
                double gapMs = (t0 - lastReadTs) * 1000.0 / Stopwatch.Frequency;
                int expMs = countRequested * 1000 / Math.Max(1, src.WaveFormat.SampleRate);
                if (gapMs > expMs * 1.5 + 15)
                {
                    lateReads++;
                    if (gapMs > maxGapMs) maxGapMs = gapMs;
                    if (WarnAllowed(ref lastTimingWarnTick))
                        Report("[警告] 输出线程取数据迟到 " + gapMs.ToString("0") + "ms (期望约 " + expMs +
                            "ms)，设备侧可能已欠载断音；若频繁出现请调高「延迟」档位或改用有线输出");
                }
            }
            lastReadTs = t0;
            float g = gain;
            float m = master;
            for (int i = 0; i < countRequested; i++)
            {
                if (count < 3 || holding)
                {
                    if (StepStarved())
                    {
                        Interlocked.Increment(ref underrunSamples);
                        // 软收尾: 把最后一次输出按 3ms 指数衰减到 0, 代替从任意电平硬切
                        fadeIn = 0f;
                        lastOut *= decayCoef;
                        if (lastOut > -1e-5f && lastOut < 1e-5f) lastOut = 0f;
                        samples[offset + i] = lastOut;
                        if (++updateCounter >= UpdateSamples) { updateCounter = 0; UpdateRatio(); }
                        continue;
                    }
                }

                int i1 = head;
                int i2 = i1 + 1; if (i2 >= RingSize) i2 -= RingSize;
                int i3 = i1 + 2; if (i3 >= RingSize) i3 -= RingSize;
                float v = Cubic(prev, ring[i1], ring[i2], ring[i3], (float)phase) * g * m;
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                if (fadeIn < 1f)
                {
                    // 欠载恢复淡入: 与 lastOut 的衰减对称, 避免电平从 0 直接跳到满幅
                    fadeIn += fadeStep;
                    if (fadeIn > 1f) fadeIn = 1f;
                    v *= fadeIn;
                }
                lastOut = v;
                samples[offset + i] = v;

                phase += ratio;
                while (phase >= 1.0)
                {
                    prev = ring[head];
                    head++;
                    if (head >= RingSize) head = 0;
                    count--;
                    phase -= 1.0;
                }

                if (++updateCounter >= UpdateSamples) { updateCounter = 0; UpdateRatio(); }
            }
            // 时序诊断②: 单次 Read 耗时超过 4ms 说明音频线程被抢占/卡住,
            // 高温或 GC 时会出现; 块时长只有 10ms, 吃满就意味着下一块必然迟到
            double durMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (durMs > 4)
            {
                slowReads++;
                if (durMs > maxDurMs) maxDurMs = durMs;
            }
            return countRequested;
        }

        /// <summary>欠载处理: 返回 true 表示本样本输出静音且不消费。
        /// holding 闸门是关键 —— 断粮后不再"有多少吃多少", 而是补到目标 95% 才恢复,
        /// 否则输出端会贴着 0 水位啃零头, 听感是长时间断音。</summary>
        bool StepStarved()
        {
            // count<3 只表示"这一环该续数据了", 每约 1.3ms 就会出现一次(64 样本小环),
            // 与"源没有数据"无关。旧实现拿它当断粮信号, 判据实际退化成"瞬时水位 < 50% 就静音",
            // 而瞬时值含块粒度锯齿(健康会话实测最低 22–36ms), 于是每 1–2 分钟误触发一次,
            // 每次静音 9–84ms + 3ms 淡出淡入 —— 听感就是"偶尔破音"。
            if (count < 3) Fill();
            int total = (int)(src.BufferedBytes / 4) + count;

            if (holding)
            {
                if (total >= targetSamples * 0.95) { holding = false; starveStreak = 0; return false; }
            }
            // 只看真实水位, 阈值压到 15%(95ms → 14ms): 真断流时 total 只剩小环里的 1ms,
            // 仍然会在一个块内立刻触发; 健康会话的锯齿谷底(22ms)碰不到。
            else if (total < targetSamples * GateEnterRatio)
            {
                // 连续两次(间隔约 1.3ms)都在阈值下才认, 单次采样恰好撞上谷底不算
                if (++starveStreak >= 2) holding = true;
            }
            else starveStreak = 0;
            return holding;
        }

        void Fill()
        {
            int want = RingSize - count;
            if (want <= 0) return;
            int got = src.Read(tmp, 0, want * 4);
            int n = got / 4;
            if (n <= 0) return;
            Buffer.BlockCopy(tmp, 0, tmpF, 0, n * 4);
            int idx = head + count;
            for (int i = 0; i < n; i++)
            {
                if (idx >= RingSize) idx -= RingSize;
                ring[idx] = tmpF[i];
                idx++;
            }
            count += n;
        }

        void UpdateRatio()
        {
            double total = src.BufferedBytes / 4 + count;
            if (total < minTotalSinceStatus) minTotalSinceStatus = (int)total;

            int sr = src.WaveFormat.SampleRate;

            // 平滑值所有模式都更新: 日志与告警统一用"调节水位"这个量
            smoothedTotal += 0.01 * (total - smoothedTotal);
            smoothedMs = MsOf(smoothedTotal, sr);

            // 事件型诊断①: 水位掉到目标 30% 以下 (与校正模式无关, 断流/设备卡顿都能看到)
            // 判据用平滑值: 瞬时值含 ±18ms 块粒度锯齿, 直接比阈值会误报
            if (smoothedTotal < targetSamples * 0.3)
            {
                if (!lowWarned && WarnAllowed(ref lastLowWarnTick))
                {
                    lowWarned = true;
                    Report("[警告] 缓冲水位偏低: 调节水位 " + smoothedMs + "ms / 目标 " + MsOf(targetSamples, sr) +
                        "ms (低于 30%)，可能出现欠载断音");
                }
            }
            else if (lowWarned && smoothedTotal >= targetSamples * 0.6)
            {
                lowWarned = false;
                Report("[状态] 缓冲水位已恢复: " + smoothedMs + "ms / 目标 " + MsOf(targetSamples, sr) + "ms");
            }

            if (mode != 1) return;

            double err = (smoothedTotal - targetSamples) / targetSamples;

            // 增益调度: 排空段(大误差)保持填充增益, 进入稳态段后换高增益
            // 时间兜底防止"低增益稳态误差恰好高于切换阈值"时永远切不过去
            updates++;
            if (!fastGain && (Math.Abs(err) < GainEnterErr || updates > SwitchAfterUpdates)) fastGain = true;
            else if (fastGain && Math.Abs(err) > GainExitErr) fastGain = false;
            double kpTarget = fastGain ? KpSteady : KpFill;
            kpNow += (kpTarget - kpNow) * 0.02;   // 约 1 秒过渡, 避免 ratio 阶跃导致音高跳变

            double r = 1.0 + kpNow * err;
            double lo = 1.0 - MaxRatio;
            double hi = 1.0 + MaxRatio;
            bool clamped = (r < lo) || (r > hi);
            if (r < lo) r = lo;
            else if (r > hi) r = hi;
            ratio = (float)r;

            // 事件型诊断②: 触顶 ≠ 故障。缓冲被抽空(捕获源短暂断流、设备切换)后, 环必须以 -0.5%
            // 全速回补, 这段一定会触顶且水位很低 —— 用"触顶 1 秒"判会把每次回补都误报成速率失配
            // (实测 21:53:56 就是这种情况: 默认输出被切走 1 秒, 水位掉到 0, 环在回补)。
            // 真正的失配是: 触顶持续 10 秒以上, 且这 10 秒里水位几乎没有回升。
            // 只报"负向触顶": 正向触顶是在排多余的水, 属自愈状态, 起播排空段因此也不会误报。
            if (clamped && fastGain && err < 0)
            {
                if (clampStreak == 0) clampStartTotal = smoothedTotal;
                clampStreak++;
                if (clampStreak >= ClampWarnUpdates)
                {
                    bool recovering = smoothedTotal - clampStartTotal > targetSamples * 0.1;
                    clampStreak = 0;   // 每 10 秒一个独立窗口
                    if (!recovering && WarnAllowed(ref lastClampWarnTick))
                        Report("[警告] 时钟校正已触顶 " + PpmOf(r) + " 超过 10 秒且水位未回升 (调节水位 " +
                            smoothedMs + "ms / 目标 " + MsOf(targetSamples, sr) + "ms)，两设备速率差超出可修正范围");
                }
            }
            else clampStreak = 0;
        }

        static int MsOf(double samples, int sr)
        {
            return sr > 0 ? (int)(samples * 1000 / sr) : 0;
        }

        static string PpmOf(double r)
        {
            return ((r - 1.0) * 1e6).ToString("+0;-0;0") + "ppm";
        }

        bool WarnAllowed(ref int lastTick)
        {
            int now = Environment.TickCount;
            if (lastTick != 0 && now - lastTick < WarnIntervalMs) return false;
            lastTick = now;
            return true;
        }

        void Report(string msg)
        {
            if (log == null) return;
            try { log(tag == null ? msg : "[" + tag + "] " + msg); } catch { }
        }

        static float Cubic(float p0, float p1, float p2, float p3, float t)
        {
            float a = -0.5f * p0 + 1.5f * p1 - 1.5f * p2 + 0.5f * p3;
            float b = p0 - 2.5f * p1 + 2f * p2 - 0.5f * p3;
            float c = -0.5f * p0 + 0.5f * p2;
            return ((a * t + b) * t + c) * t + p1;
        }
    }
}
