using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace StereoSplitter
{
    class Program
    {
        internal const string MutexName = "StereoSplitter_GUI_SingleInstance";
        internal const string ActivateEventName = "Local\\StereoSplitter_Activate";

        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // 已有实例: 发激活通知后退出
                    try
                    {
                        using (var evt = EventWaitHandle.OpenExisting(ActivateEventName))
                        {
                            evt.Set();
                        }
                    }
                    catch { }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
        }
    }

    class NoWheelComboBox : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A;
            if (m.Msg == WM_MOUSEWHEEL) return;
            base.WndProc(ref m);
        }
    }

    class MainForm : Form
    {
        NoWheelComboBox cmbLeft, cmbRight, cmbDefault, cmbLatency;
        NumericUpDown nudDelayL, nudDelayR;
        CheckBox chkWaterLog;
        NumericUpDown nudWaterInterval;
        static readonly int[][] LatencyProfiles = new int[][] {
            new int[] { 100, 50 },
            new int[] { 50, 30 },
            new int[] { 25, 20 },
            new int[] { 15, 10 },
            new int[] { 5, 5 },
        };
        System.Windows.Forms.Timer waterTimer;
        TrackBar volLeft, volRight, volMaster, volGain;
        Label lblVolLeft, lblVolRight, lblVolMaster, lblVolGain;
        Label lblDot, lblStatus;
        Button btnStart;
        Button btnRefresh;
        Button btnClearLog;
        string sessionLogFile;
        SplitContainer splitContainer;
        TextBox txtLog;
        CheckBox chkMuteL, chkMuteR, chkMuteMaster, chkMuteCapture;
        CheckBox chkMinimizeToTray;
        NotifyIcon trayIcon;
        EventWaitHandle activateEvent;
        bool exiting = false;
        bool trayTipShown = false;
        bool loadingSettings = true;
        float pendingGain = 1.0f;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        enum AppState { Ready, Starting, Running, Stopped, Failed }
        AppState state = AppState.Ready;

        MMDevice syncDev;
        AudioEndpointVolume syncVol;
        MMDevice syncLeftDev, syncRightDev;
        AudioEndpointVolume syncLeftVol, syncRightVol;
        volatile bool volSyncing = false;
        bool loadingDevices = false;
        string restoreDefaultId = null;
        // 捕获设备进入时的原静音状态, 退出/换绑时恢复
        bool captureMuteEntered = false;
        bool captureMuteApplied = false;

        List<MMDevice> devices = new List<MMDevice>();
        MMDeviceEnumerator enumerator;

        AudioEngine engine;
        bool running = false;

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
            volGain = new TrackBar { Location = new Point(80, 245), Width = 300, Height = 34, AutoSize = false, Minimum = 0, Maximum = 200, Value = 100, TickFrequency = 10 };
            lblVolGain = new Label { Text = "100%", Location = new Point(390, 250), AutoSize = true };

            btnStart = new Button { Text = "开始分流", Location = new Point(190, 285), Width = 130, Height = 35 };

            lblDot = new Label { Text = "●", Location = new Point(17, 290), AutoSize = true, ForeColor = Color.Gray, Font = new Font("Microsoft YaHei UI", 10F) };
            lblStatus = new Label { Text = "状态: 就绪", Location = new Point(37, 295), AutoSize = true, ForeColor = Color.Gray };

            var lblLatency = new Label { Text = "启动延迟:", Location = new Point(15, 108), AutoSize = true };
            cmbLatency = new NoWheelComboBox { Location = new Point(85, 105), Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbLatency.Items.AddRange(new object[] { "标准 (预缓冲100ms)", "低延迟 (50ms)", "极低 (25ms, 可能断音)", "超低 (15ms, 断音增多)", "极限 (5ms, 频繁断音)" });
            cmbLatency.SelectedIndex = 0;

            chkMinimizeToTray = new CheckBox { Text = "关闭时最小化到托盘", Location = new Point(290, 107), AutoSize = true };

            // 水位监控组: 在日志面板标题行, 清除日志按钮左边
            var lblWater = new Label { Text = "水位监控:", Location = new Point(60, 8), AutoSize = true };
            chkWaterLog = new CheckBox { Text = "启用", Location = new Point(135, 7), AutoSize = true };
            var lblWaterIv = new Label { Text = "间隔", Location = new Point(192, 8), AutoSize = true };
            nudWaterInterval = new NumericUpDown { Location = new Point(227, 5), Width = 60, Minimum = 5, Maximum = 3600, Value = 30 };
            var lblWaterSec = new Label { Text = "秒", Location = new Point(291, 8), AutoSize = true };

            var lblLogTitle = new Label { Text = "日志", Location = new Point(15, 8), AutoSize = true };
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

            splitContainer.Panel1.Controls.AddRange(new Control[] { lblD, cmbDefault, chkMuteCapture, btnRefresh, lblL, cmbLeft, lblDL, nudDelayL, lblMsL, lblR, cmbRight, lblDR, nudDelayR, lblMsR, lblLatency, cmbLatency, chkMinimizeToTray, lblVL, volLeft, lblVolLeft, chkMuteL, lblVR, volRight, lblVolRight, chkMuteR, lblVM, volMaster, lblVolMaster, chkMuteMaster, lblG, volGain, lblVolGain, lblDot, lblStatus, btnStart });
            splitContainer.Panel2.Controls.AddRange(new Control[] { lblLogTitle, lblWater, chkWaterLog, lblWaterIv, nudWaterInterval, lblWaterSec, btnClearLog, txtLog });

            btnStart.Click += BtnStart_Click;
            btnRefresh.Click += (s, e) => RefreshDevicesClick();
            btnClearLog.Click += (s, e) =>
            {
                txtLog.Clear();
                try { if (sessionLogFile != null) File.WriteAllText(sessionLogFile, ""); } catch { }
            };
            chkMinimizeToTray.CheckedChanged += (s, e) => SaveSettings();
            cmbLatency.SelectedIndexChanged += (s, e) => { if (!loadingSettings) SaveSettings(); };
            nudDelayL.ValueChanged += (s, e) => { if (!loadingSettings) SaveSettings(); };
            nudDelayR.ValueChanged += (s, e) => { if (!loadingSettings) SaveSettings(); };
            chkWaterLog.CheckedChanged += (s, e) => { if (!loadingSettings) SaveSettings(); };
            nudWaterInterval.ValueChanged += (s, e) =>
            {
                if (waterTimer != null) waterTimer.Interval = Math.Max(5, (int)nudWaterInterval.Value) * 1000;
                if (!loadingSettings) SaveSettings();
            };
            cmbLeft.SelectedIndexChanged += (s, e) => { if (!loadingDevices) SaveSettings(); };
            cmbRight.SelectedIndexChanged += (s, e) => { if (!loadingDevices) SaveSettings(); };
            cmbDefault.SelectedIndexChanged += (s, e) => { if (!loadingDevices) SaveSettings(); };
            volLeft.Scroll += (s, e) =>
            {
                lblVolLeft.Text = volLeft.Value + "%";
                // 调音量即取消静音 (主动操作优先于勾选状态)
                if (chkMuteL.Checked) chkMuteL.Checked = false;
                // 双向同步: 写入左声道设备端点音量
                if (!volSyncing && syncLeftVol != null)
                {
                    try
                    {
                        syncLeftVol.MasterVolumeLevelScalar = volLeft.Value / 100f;
                        if (syncLeftVol.Mute) syncLeftVol.Mute = false;
                    }
                    catch (Exception ex) { Log("[错误] 同步左声道端点音量失败: " + ex.Message); }
                }
                SaveSettings();
            };
            volRight.Scroll += (s, e) =>
            {
                lblVolRight.Text = volRight.Value + "%";
                // 调音量即取消静音 (主动操作优先于勾选状态)
                if (chkMuteR.Checked) chkMuteR.Checked = false;
                // 双向同步: 写入右声道设备端点音量
                if (!volSyncing && syncRightVol != null)
                {
                    try
                    {
                        syncRightVol.MasterVolumeLevelScalar = volRight.Value / 100f;
                        if (syncRightVol.Mute) syncRightVol.Mute = false;
                    }
                    catch (Exception ex) { Log("[错误] 同步右声道端点音量失败: " + ex.Message); }
                }
                SaveSettings();
            };
            volMaster.Scroll += (s, e) =>
            {
                lblVolMaster.Text = volMaster.Value + "%";
                ApplyVolumes();
                // 调音量即取消采样层主静音 (捕获消音锁定不受拖动影响)
                if (chkMuteMaster.Checked) chkMuteMaster.Checked = false;
                // 双向同步: 写入当前默认输出设备的端点音量 (不改捕获消音状态)
                if (!volSyncing && syncVol != null)
                {
                    try { syncVol.MasterVolumeLevelScalar = volMaster.Value / 100f; }
                    catch (Exception ex) { Log("[错误] 同步端点音量失败: " + ex.Message); }
                }
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
                if (!volSyncing && syncVol != null)
                {
                    try
                    {
                        syncVol.Mute = chkMuteCapture.Checked;
                        captureMuteApplied = (syncVol.Mute != captureMuteEntered);
                    }
                    catch { }
                }
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
                if (!volSyncing && syncLeftVol != null)
                {
                    try { syncLeftVol.Mute = chkMuteL.Checked; }
                    catch { }
                }
                SaveSettings();
            };
            chkMuteR.CheckedChanged += (s, e) =>
            {
                if (!volSyncing && syncRightVol != null)
                {
                    try { syncRightVol.Mute = chkMuteR.Checked; }
                    catch { }
                }
                SaveSettings();
            };

            LoadSettings();
            InitTray();

            // 会话日志文件 (参照 screen-mirroring-capture: 每会话一个 session 文件, UI+文件双写)
            try
            {
                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
                Directory.CreateDirectory(logDir);
                sessionLogFile = Path.Combine(logDir, "session_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
            }
            catch { sessionLogFile = null; }

            waterTimer = new System.Windows.Forms.Timer
            {
                Interval = Math.Max(5, (int)nudWaterInterval.Value) * 1000
            };
            waterTimer.Tick += (s, e) =>
            {
                if (chkWaterLog.Checked && running && engine != null) Log("[状态] 缓冲水位 " + engine.BufferStatus());
            };
            waterTimer.Start();

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
            cmbLatency.Enabled = !busy && !run;
            nudDelayL.Enabled = !busy && !run;
            nudDelayR.Enabled = !busy && !run;
            chkWaterLog.Enabled = !busy;
            nudWaterInterval.Enabled = !busy;
            btnStart.Enabled = !busy;

            // 数值标签: 仅分流运行中(双向绑定生效)为黑, 其余灰色表示无效
            SetVolumeBindVisual(run);

            if (trayIcon != null) trayIcon.Text = "Stereo Splitter - " + txt;
        }

        void MainForm_Load(object sender, EventArgs e)
        {
            try
            {
                enumerator = new MMDeviceEnumerator();
                RefreshDevices();
                RecordRestoreTarget();
                ApplyIniVolumeOverrides();
                // 校准日志框高度, 此后由 Anchor 自动跟随分隔条拖动
                txtLog.Height = splitContainer.Panel2.Height - txtLog.Top - 15;
                SetAppState(AppState.Ready);
                RegisterActivateHandler();
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

        void RecordRestoreTarget()
        {
            // 记录进入时的默认输出，退出时恢复
            try
            {
                var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                restoreDefaultId = def.ID;

                if (IsVirtualDevice(def))
                {
                    // 默认是虚拟设备(如安装器切成 CABLE): 选真实扬声器作恢复目标
                    foreach (var d in devices)
                    {
                        if (!IsVirtualDevice(d))
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

        bool IsVirtualDevice(MMDevice d)
        {
            string n = d.FriendlyName;
            return n.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("虚拟", StringComparison.Ordinal) >= 0 ||
                   n.IndexOf("MIXLINE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void BindDefaultVolumeSync(string stage)
        {
            try
            {
                // 主音量绑定「默认输出」下拉选中项, 不是系统当前默认
                if (cmbDefault.SelectedIndex < 0 || cmbDefault.SelectedIndex >= devices.Count) return;
                var def = devices[cmbDefault.SelectedIndex];

                if (syncDev == null || syncDev.ID != def.ID || syncVol == null)
                {
                    // 退旧前恢复旧设备消音状态 (我们改过才恢复)
                    if (captureMuteApplied && syncVol != null)
                    {
                        try { syncVol.Mute = captureMuteEntered; } catch { }
                    }

                    // 退旧 (不 Dispose: MMDevice.AudioEndpointVolume 是缓存实例, Dispose 会使后续绑定拿到已释放 RCW)
                    if (syncVol != null)
                    {
                        try { syncVol.OnVolumeNotification -= SyncVol_OnVolumeNotification; } catch { }
                        syncVol = null;
                        syncDev = null;
                    }

                    // 绑新
                    syncDev = def;
                    syncVol = def.AudioEndpointVolume;
                    syncVol.OnVolumeNotification += SyncVol_OnVolumeNotification;
                    captureMuteEntered = syncVol.Mute;
                    captureMuteApplied = false;
                    // 勾选跟人走: 换绑后新设备立即消音 (旧设备已按 applied 恢复原状态)
                    if (chkMuteCapture.Checked)
                    {
                        try { syncVol.Mute = true; } catch { }
                    }
                }

                volSyncing = true;
                int v = (int)Math.Round(syncVol.MasterVolumeLevelScalar * 100);
                volMaster.Value = Math.Max(0, Math.Min(100, v));
                lblVolMaster.Text = volMaster.Value + "%";
                chkMuteCapture.Checked = syncVol.Mute;
                captureMuteApplied = (syncVol.Mute != captureMuteEntered);
                volSyncing = false;

                Log(stage + " 主音量同步 → [" + FindIdxById(def.ID) + "] " + def.FriendlyName + " (" + v + "%" + (syncVol.Mute ? ", 消音" : "") + ")");
            }
            catch (Exception ex)
            {
                volSyncing = false;
                Log("[错误] " + stage + " 主音量同步失败: " + ex.Message);
                syncVol = null;
                syncDev = null;
            }
        }

        int FindIdxById(string id)
        {
            return devices.FindIndex(d => d.ID == id);
        }

        string DevInfo(MMDevice d)
        {
            if (d == null) return "(未绑定)";
            return "[" + FindIdxById(d.ID) + "] " + d.FriendlyName;
        }

        string FindNameById(string id)
        {
            foreach (var d in devices)
                if (d.ID == id) return d.FriendlyName;
            return id;
        }

        void SyncVol_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            // Windows 侧音量变化 → 更新 UI (双向同步)
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (volSyncing || syncVol == null) return;
                    volSyncing = true;
                    int v = Math.Max(0, Math.Min(100, (int)Math.Round(data.MasterVolume * 100)));
                    if (volMaster.Value != v)
                    {
                        volMaster.Value = v;
                        lblVolMaster.Text = v + "%";
                        ApplyVolumes();
                        SaveSettings();
                    }
                    if (chkMuteCapture.Checked && !data.Muted)
                    {
                        // 消音锁定: 任何外部(系统音量条/快捷键/驱动)取消静音 → 静默恢复, 直到用户取消勾选
                        try { syncVol.Mute = true; }
                        catch (Exception ex) { Log("[错误] 恢复捕获消音失败: " + ex.Message); }
                    }
                    else if (chkMuteCapture.Checked != data.Muted)
                    {
                        chkMuteCapture.Checked = data.Muted;
                    }
                    volSyncing = false;
                }));
            }
            catch { }
        }

        void BindChannelVolumeSync(string stage)
        {
            BindOneChannel(stage, "左", cmbLeft.SelectedIndex, ref syncLeftDev, ref syncLeftVol,
                LeftVol_OnVolumeNotification, volLeft, lblVolLeft, chkMuteL);
            BindOneChannel(stage, "右", cmbRight.SelectedIndex, ref syncRightDev, ref syncRightVol,
                RightVol_OnVolumeNotification, volRight, lblVolRight, chkMuteR);

            if (cmbLeft.SelectedIndex >= 0 && cmbLeft.SelectedIndex == cmbRight.SelectedIndex)
                Log("[提示] 左右声道选择了同一设备 " + DevInfo(syncLeftDev) + "，两滑条联动同一端点");
        }

        void BindOneChannel(string stage, string channel, int idx,
            ref MMDevice dev, ref AudioEndpointVolume vol, AudioEndpointVolumeNotificationDelegate handler,
            TrackBar slider, Label lbl, CheckBox chkMute)
        {
            if (idx < 0 || idx >= devices.Count)
            {
                UnbindChannel(ref dev, ref vol, handler);
                return;
            }

            var d = devices[idx];
            try
            {
                // 设备已变化 → 解绑旧端点
                if (vol != null && (dev == null || dev.ID != d.ID))
                    UnbindChannel(ref dev, ref vol, handler);

                if (vol == null)
                {
                    vol = d.AudioEndpointVolume;
                    vol.OnVolumeNotification += handler;
                }
                dev = d;

                volSyncing = true;
                int v = (int)Math.Round(vol.MasterVolumeLevelScalar * 100);
                slider.Value = Math.Max(0, Math.Min(100, v));
                lbl.Text = v + "%";
                chkMute.Checked = vol.Mute;
                volSyncing = false;

                Log(stage + " " + channel + "声道音量同步 → [" + idx + "] " + d.FriendlyName +
                    " (" + v + "%" + (vol.Mute ? ", 静音" : "") + ")");
            }
            catch (Exception ex)
            {
                volSyncing = false;
                Log("[错误] " + stage + " " + channel + "声道音量同步失败: " + ex.Message);
                UnbindChannel(ref dev, ref vol, handler);
            }
        }

        void UnbindChannel(ref MMDevice dev, ref AudioEndpointVolume vol, AudioEndpointVolumeNotificationDelegate handler)
        {
            // 不 Dispose: MMDevice.AudioEndpointVolume 是缓存实例, Dispose 会使后续绑定拿到已释放 RCW
            if (vol != null)
            {
                try { vol.OnVolumeNotification -= handler; } catch { }
                vol = null;
            }
            dev = null;
        }

        void LeftVol_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (volSyncing || syncLeftVol == null) return;
                    volSyncing = true;
                    int v = Math.Max(0, Math.Min(100, (int)Math.Round(data.MasterVolume * 100)));
                    if (volLeft.Value != v)
                    {
                        volLeft.Value = v;
                        lblVolLeft.Text = v + "%";
                        SaveSettings();
                    }
                    if (chkMuteL.Checked != data.Muted) chkMuteL.Checked = data.Muted;
                    volSyncing = false;
                }));
            }
            catch { }
        }

        void RightVol_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (volSyncing || syncRightVol == null) return;
                    volSyncing = true;
                    int v = Math.Max(0, Math.Min(100, (int)Math.Round(data.MasterVolume * 100)));
                    if (volRight.Value != v)
                    {
                        volRight.Value = v;
                        lblVolRight.Text = v + "%";
                        SaveSettings();
                    }
                    if (chkMuteR.Checked != data.Muted) chkMuteR.Checked = data.Muted;
                    volSyncing = false;
                }));
            }
            catch { }
        }

        bool AutoConfigure()
        {
            if (cmbDefault.SelectedIndex < 0) return false;

            var dev = devices[cmbDefault.SelectedIndex];
            try
            {
                // 1. 设为系统默认输出
                int hr = PolicyConfigHelper.SetDefaultDevice(dev.ID);
                if (hr != 0)
                {
                    Log("[错误] 配置 1/4 设置系统默认输出 → [" + cmbDefault.SelectedIndex + "] " +
                        dev.FriendlyName + " 失败 HRESULT=0x" + hr.ToString("X8"));
                    var def1 = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    int idx1 = devices.FindIndex(d => d.ID == def1.ID);
                    if (idx1 >= 0) cmbDefault.SelectedIndex = idx1;
                    return false;
                }
                Log("[配置] 1/4 设置系统默认输出 → [" + cmbDefault.SelectedIndex + "] " + dev.FriendlyName + " 成功");

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
                    for (int i = 0; i < devices.Count; i++)
                    {
                        if (i == defIdx) continue;
                        if (IsVirtualDevice(devices[i])) continue;
                        if (l < 0) l = i;
                        else { r = i; break; }
                    }
                    // 不够则任意非默认
                    if (l < 0 || r < 0)
                    {
                        for (int i = 0; i < devices.Count; i++)
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
                        Log("[配置] 3/4 左右目标: 自动改选(冲突/无效) L=[" + l + "] " + devices[l].FriendlyName +
                            " / R=[" + r + "] " + devices[r].FriendlyName);
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
                        devices[cmbLeft.SelectedIndex].FriendlyName + " / R=[" + cmbRight.SelectedIndex + "] " +
                        devices[cmbRight.SelectedIndex].FriendlyName);
                }

                // 4. 重绑左右音量同步到左右目标设备
                BindChannelVolumeSync("[配置] 4/4");

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
        Dictionary<string, string> settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void LoadSettings()
        {
            settings.Clear();
            try
            {
                if (File.Exists(IniPath))
                {
                    foreach (var line in File.ReadAllLines(IniPath))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        settings[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
                }
            }
            catch { }

            chkMinimizeToTray.Checked = GetInt("MinimizeToTray", 0) != 0;
            cmbLatency.SelectedIndex = Math.Max(0, Math.Min(4, GetInt("LatencyProfile", 0)));
            nudDelayL.Value = Math.Max(0, Math.Min(2000, GetInt("DelayLeft", 0)));
            nudDelayR.Value = Math.Max(0, Math.Min(2000, GetInt("DelayRight", 0)));
            chkWaterLog.Checked = GetInt("WaterLogOn", 1) != 0;
            nudWaterInterval.Value = Math.Max(5, Math.Min(3600, GetInt("WaterIntervalSec", 30)));

            int gain = Math.Max(0, Math.Min(200, GetInt("Gain", 100)));
            volGain.Value = gain;
            lblVolGain.Text = gain + "%";
            pendingGain = gain / 100f;
        }

        int GetInt(string key, int def)
        {
            string s;
            int v;
            if (settings.TryGetValue(key, out s) && int.TryParse(s, out v)) return v;
            return def;
        }

        string GetStr(string key)
        {
            string s;
            settings.TryGetValue(key, out s);
            return s;
        }

        string DeviceIdAt(int idx)
        {
            return (idx >= 0 && idx < devices.Count) ? devices[idx].ID : "";
        }

        void SaveSettings()
        {
            // 初始化阶段(读ini/Bind回读/覆盖回写)不落盘, 防止把ini冲成端点回读值
            if (loadingSettings) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("MinimizeToTray=" + (chkMinimizeToTray.Checked ? 1 : 0));
                sb.AppendLine("LatencyProfile=" + Math.Max(0, cmbLatency.SelectedIndex));
                sb.AppendLine("DelayLeft=" + (int)nudDelayL.Value);
                sb.AppendLine("DelayRight=" + (int)nudDelayR.Value);
                sb.AppendLine("WaterLogOn=" + (chkWaterLog.Checked ? 1 : 0));
                sb.AppendLine("WaterIntervalSec=" + (int)nudWaterInterval.Value);
                sb.AppendLine("Gain=" + volGain.Value);
                sb.AppendLine("DeviceLeft=" + DeviceIdAt(cmbLeft.SelectedIndex));
                sb.AppendLine("DeviceRight=" + DeviceIdAt(cmbRight.SelectedIndex));
                sb.AppendLine("DeviceDefault=" + DeviceIdAt(cmbDefault.SelectedIndex));
                sb.AppendLine("VolumeLeft=" + volLeft.Value);
                sb.AppendLine("VolumeRight=" + volRight.Value);
                sb.AppendLine("VolumeMaster=" + volMaster.Value);
                sb.AppendLine("MuteLeft=" + (chkMuteL.Checked ? 1 : 0));
                sb.AppendLine("MuteRight=" + (chkMuteR.Checked ? 1 : 0));
                sb.AppendLine("MuteMaster=" + (chkMuteMaster.Checked ? 1 : 0));
                sb.AppendLine("MuteCapture=" + (chkMuteCapture.Checked ? 1 : 0));
                File.WriteAllText(IniPath, sb.ToString());
                // 同步回内存字典: 刷新设备等场景按字典恢复选中, 不读过期文件值
                settings["MinimizeToTray"] = chkMinimizeToTray.Checked ? "1" : "0";
                settings["LatencyProfile"] = Math.Max(0, cmbLatency.SelectedIndex).ToString();
                settings["DelayLeft"] = ((int)nudDelayL.Value).ToString();
                settings["DelayRight"] = ((int)nudDelayR.Value).ToString();
                settings["WaterLogOn"] = chkWaterLog.Checked ? "1" : "0";
                settings["WaterIntervalSec"] = ((int)nudWaterInterval.Value).ToString();
                settings["Gain"] = volGain.Value.ToString();
                settings["DeviceLeft"] = DeviceIdAt(cmbLeft.SelectedIndex);
                settings["DeviceRight"] = DeviceIdAt(cmbRight.SelectedIndex);
                settings["DeviceDefault"] = DeviceIdAt(cmbDefault.SelectedIndex);
                settings["VolumeLeft"] = volLeft.Value.ToString();
                settings["VolumeRight"] = volRight.Value.ToString();
                settings["VolumeMaster"] = volMaster.Value.ToString();
                settings["MuteLeft"] = chkMuteL.Checked ? "1" : "0";
                settings["MuteRight"] = chkMuteR.Checked ? "1" : "0";
                settings["MuteMaster"] = chkMuteMaster.Checked ? "1" : "0";
                settings["MuteCapture"] = chkMuteCapture.Checked ? "1" : "0";
            }
            catch { }
        }

        void RestoreDeviceSelection(string key, NoWheelComboBox combo, string label)
        {
            string id = GetStr(key);
            if (string.IsNullOrEmpty(id)) return;
            int idx = devices.FindIndex(d => d.ID == id);
            if (idx >= 0) combo.SelectedIndex = idx;
            else Log("[初始化] 未找到已保存的" + label + "设备，使用默认选择");
        }

        void RestoreEndpointVolume(AudioEndpointVolume ep, TrackBar slider, Label lbl, int iniVal, string name)
        {
            if (ep == null) return;
            try
            {
                ep.MasterVolumeLevelScalar = iniVal / 100f;
            }
            catch (Exception ex)
            {
                Log("[错误] 恢复" + name + "音量到端点失败: " + ex.Message);
                try
                {
                    int real = Math.Max(0, Math.Min(100, (int)Math.Round(ep.MasterVolumeLevelScalar * 100)));
                    slider.Value = real;
                    lbl.Text = real + "%";
                    Log("[提示] " + name + "音量回退为端点实际值 " + real + "%");
                }
                catch { }
            }
        }

        void ApplyIniVolumeOverrides()
        {
            bool any = false;
            volSyncing = true;
            try
            {
                if (settings.ContainsKey("VolumeMaster"))
                {
                    int v = Math.Max(0, Math.Min(100, GetInt("VolumeMaster", 100)));
                    volMaster.Value = v;
                    lblVolMaster.Text = v + "%";
                    RestoreEndpointVolume(syncVol, volMaster, lblVolMaster, v, "主");
                    any = true;
                }
                if (settings.ContainsKey("MuteMaster"))
                {
                    bool m = GetInt("MuteMaster", 0) != 0;
                    chkMuteMaster.Checked = m;
                    any = true;
                }
                if (settings.ContainsKey("MuteCapture"))
                {
                    bool m = GetInt("MuteCapture", 0) != 0;
                    chkMuteCapture.Checked = m;
                    if (syncVol != null)
                    {
                        try
                        {
                            syncVol.Mute = m;
                            captureMuteApplied = (m != captureMuteEntered);
                        }
                        catch (Exception ex)
                        {
                            Log("[错误] 恢复捕获消音失败: " + ex.Message);
                            try { chkMuteCapture.Checked = syncVol.Mute; } catch { }
                        }
                    }
                    any = true;
                }
                if (settings.ContainsKey("VolumeLeft"))
                {
                    int v = Math.Max(0, Math.Min(100, GetInt("VolumeLeft", 100)));
                    volLeft.Value = v;
                    lblVolLeft.Text = v + "%";
                    RestoreEndpointVolume(syncLeftVol, volLeft, lblVolLeft, v, "左");
                    any = true;
                }
                if (settings.ContainsKey("MuteLeft"))
                {
                    bool m = GetInt("MuteLeft", 0) != 0;
                    chkMuteL.Checked = m;
                    if (syncLeftVol != null)
                    {
                        try { syncLeftVol.Mute = m; }
                        catch (Exception ex)
                        {
                            Log("[错误] 恢复左静音失败: " + ex.Message);
                            try { chkMuteL.Checked = syncLeftVol.Mute; } catch { }
                        }
                    }
                    any = true;
                }
                if (settings.ContainsKey("VolumeRight"))
                {
                    int v = Math.Max(0, Math.Min(100, GetInt("VolumeRight", 100)));
                    volRight.Value = v;
                    lblVolRight.Text = v + "%";
                    RestoreEndpointVolume(syncRightVol, volRight, lblVolRight, v, "右");
                    any = true;
                }
                if (settings.ContainsKey("MuteRight"))
                {
                    bool m = GetInt("MuteRight", 0) != 0;
                    chkMuteR.Checked = m;
                    if (syncRightVol != null)
                    {
                        try { syncRightVol.Mute = m; }
                        catch (Exception ex)
                        {
                            Log("[错误] 恢复右静音失败: " + ex.Message);
                            try { chkMuteR.Checked = syncRightVol.Mute; } catch { }
                        }
                    }
                    any = true;
                }
            }
            finally
            {
                volSyncing = false;
            }

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
                ThreadPool.RegisterWaitForSingleObject(activateEvent, delegate(object state, bool timedOut)
                {
                    try
                    {
                        if (IsHandleCreated && !IsDisposed)
                            BeginInvoke(new Action(delegate { RestoreFromTray(); }));
                    }
                    catch { }
                }, null, -1, true);
            }
            catch { }
        }

        void UnbindDefaultVolumeSync()
        {
            // 不 Dispose: MMDevice.AudioEndpointVolume 是缓存实例, Dispose 会使后续绑定拿到已释放 RCW
            if (syncVol != null)
            {
                try { syncVol.OnVolumeNotification -= SyncVol_OnVolumeNotification; } catch { }
                syncVol = null;
                syncDev = null;
            }
        }

        // 解除全部音量双向绑定 (停止分流/启动失败/退出时统一调用)
        void UnbindAllVolumeSync()
        {
            bool had = syncVol != null || syncLeftVol != null || syncRightVol != null;
            if (!had) return;

            // 恢复捕获设备进入时的原静音状态 (我们改过才恢复)
            if (captureMuteApplied && syncVol != null)
            {
                try { syncVol.Mute = captureMuteEntered; } catch { }
                captureMuteApplied = false;
            }

            UnbindDefaultVolumeSync();
            UnbindChannel(ref syncLeftDev, ref syncLeftVol, LeftVol_OnVolumeNotification);
            UnbindChannel(ref syncRightDev, ref syncRightVol, RightVol_OnVolumeNotification);
            Log("[停止] 双向音量绑定已解除");
        }

        // 数值标签有效/无效视觉 (仅 Running 即绑定生效时为黑)
        void SetVolumeBindVisual(bool bound)
        {
            Color c = bound ? SystemColors.ControlText : SystemColors.GrayText;
            if (lblVolLeft != null) lblVolLeft.ForeColor = c;
            if (lblVolRight != null) lblVolRight.ForeColor = c;
            if (lblVolMaster != null) lblVolMaster.ForeColor = c;
        }

        // 启动失败: 解绑(含恢复捕获静音) + Failed 状态
        void StartFailed()
        {
            UnbindAllVolumeSync();
            SetAppState(AppState.Failed);
        }

        // 把 UI 当前音量值写入已绑定端点 (点开始分流时, UI 为唯一权威源)
        void WriteUIValuesToDevice()
        {
            try
            {
                volSyncing = true;
                if (syncVol != null)
                {
                    syncVol.MasterVolumeLevelScalar = volMaster.Value / 100f;
                    if (syncVol.Mute != chkMuteCapture.Checked) syncVol.Mute = chkMuteCapture.Checked;
                    captureMuteApplied = (syncVol.Mute != captureMuteEntered);
                }
                if (syncLeftVol != null)
                {
                    syncLeftVol.MasterVolumeLevelScalar = volLeft.Value / 100f;
                    if (syncLeftVol.Mute != chkMuteL.Checked) syncLeftVol.Mute = chkMuteL.Checked;
                }
                if (syncRightVol != null)
                {
                    syncRightVol.MasterVolumeLevelScalar = volRight.Value / 100f;
                    if (syncRightVol.Mute != chkMuteR.Checked) syncRightVol.Mute = chkMuteR.Checked;
                }
                Log("[启动] 按UI值写入端点: 主=" + volMaster.Value + "% 左=" + volLeft.Value +
                    "% 右=" + volRight.Value + "% (捕获消音=" + (chkMuteCapture.Checked ? "是" : "否") + ")");
            }
            catch (Exception ex)
            {
                Log("[错误] 按UI值写入端点失败: " + ex.Message);
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
            Log("[刷新] 完成, 共 " + devices.Count + " 个设备");
        }

        void RefreshDevices()
        {
            loadingDevices = true;
            try
            {
                devices.Clear();
                cmbLeft.Items.Clear();
                cmbRight.Items.Clear();
                cmbDefault.Items.Clear();

                var coll = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                for (int i = 0; i < coll.Count; i++)
                {
                    var dev = coll[i];
                    devices.Add(dev);
                    cmbLeft.Items.Add("[" + i + "] " + dev.FriendlyName);
                    cmbRight.Items.Add("[" + i + "] " + dev.FriendlyName);
                    cmbDefault.Items.Add("[" + i + "] " + dev.FriendlyName);
                }

                var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                int defIdx = devices.FindIndex(d => d.ID == def.ID);
                if (cmbLeft.Items.Count > 0) cmbLeft.SelectedIndex = 0;
                if (cmbRight.Items.Count > 1) cmbRight.SelectedIndex = 1;
                if (cmbDefault.Items.Count > 0) cmbDefault.SelectedIndex = defIdx >= 0 ? defIdx : 0;

                // 恢复 ini 保存的设备选择 (按设备 ID, 找不到则保留上面的默认)
                RestoreDeviceSelection("DeviceLeft", cmbLeft, "左");
                RestoreDeviceSelection("DeviceRight", cmbRight, "右");
                RestoreDeviceSelection("DeviceDefault", cmbDefault, "捕获输出");

                Log("[初始化] 发现 " + devices.Count + " 个输出设备，默认输出: [" + defIdx + "] " + def.FriendlyName);
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

            SetAppState(AppState.Starting);

            // 捕获 UI 当前值 (AutoConfigure 的 Bind 会用设备真值回填覆盖, 须提前存下)
            int uiL = volLeft.Value, uiR = volRight.Value, uiM = volMaster.Value;
            bool uiMl = chkMuteL.Checked, uiMr = chkMuteR.Checked,
                 uiMm = chkMuteMaster.Checked, uiMc = chkMuteCapture.Checked;

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

            var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            int defIdx = devices.FindIndex(d => d.ID == def.ID);
            if (defIdx == cmbLeft.SelectedIndex || defIdx == cmbRight.SelectedIndex)
            {
                var others = new List<string>();
                for (int i = 0; i < devices.Count; i++)
                    if (i != cmbLeft.SelectedIndex && i != cmbRight.SelectedIndex)
                        others.Add("[" + i + "] " + devices[i].FriendlyName);
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
                WriteUIValuesToDevice();

                engine = new AudioEngine(devices[cmbLeft.SelectedIndex], devices[rightIdxSafe()], Log);
                int prof = cmbLatency.SelectedIndex < 0 ? 0 : cmbLatency.SelectedIndex;
                engine.Start(pendingGain, CurrentMaster(), LatencyProfiles[prof][0], LatencyProfiles[prof][1],
                    (int)nudDelayL.Value, (int)nudDelayR.Value);
                SetAppState(AppState.Running);
                ApplyVolumes();
                Log("[启动] 端点绑定: 主=" + DevInfo(syncDev) + " / 左=" + DevInfo(syncLeftDev) + " / 右=" + DevInfo(syncRightDev));
                Log("[启动] 分流已启动 (增益 " + (int)Math.Round(pendingGain * 100) + "%)");
            }
            catch (Exception ex)
            {
                Log("[错误] 启动失败: " + ex.Message);
                if (engine != null) { engine.Dispose(); engine = null; }
                StartFailed();
                MessageBox.Show("启动失败: " + ex.Message, "错误");
            }
        }

        int rightIdxSafe() { return cmbRight.SelectedIndex; }

        void StopEngine()
        {
            if (engine != null)
            {
                try { engine.Stop(); engine.Dispose(); } catch { }
                engine = null;
            }
            if (state == AppState.Running || state == AppState.Starting)
            {
                SetAppState(AppState.Stopped);
                Log("[停止] 分流已停止。");
            }
            UnbindAllVolumeSync();
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

            StopEngine();

            // 恢复默认输出
            try
            {
                if (restoreDefaultId != null && enumerator != null)
                {
                    var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (def.ID != restoreDefaultId)
                    {
                        int hr = PolicyConfigHelper.SetDefaultDevice(restoreDefaultId);
                        if (hr == 0) Log("[恢复] 默认输出 → " + FindNameById(restoreDefaultId));
                        else Log("[错误] 恢复默认输出失败 HRESULT=0x" + hr.ToString("X8"));
                    }
                }
            }
            catch { }

            SaveSettings();
            if (waterTimer != null)
            {
                waterTimer.Stop();
                waterTimer.Dispose();
                waterTimer = null;
            }
            if (activateEvent != null)
            {
                try { activateEvent.Close(); } catch { }
                activateEvent = null;
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

            // 格式: HH:mm:ss [LEVEL] [阶段] 内容 (参照 screen-mirroring-capture)
            string level = "INFO";
            if (msg.StartsWith("[错误]")) level = "ERROR";
            else if (msg.StartsWith("[警告]")) level = "WARNING";
            string line = DateTime.Now.ToString("HH:mm:ss") + " [" + level + "] " + msg;

            // UI: 单条 8192 截断 + 条目间空行 + 自动滚底
            string ui = line;
            if (ui.Length > 8192) ui = ui.Substring(0, 8192) + "\n...(日志过长已截断)";
            txtLog.AppendText(ui + "\r\n\r\n");
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.ScrollToCaret();

            // 文件持久化 (写原文, 不截断)
            try
            {
                if (sessionLogFile != null) File.AppendAllText(sessionLogFile, line + "\n");
            }
            catch { }
        }
    }

    // 不接受焦点的 SplitContainer: 点击/拖动分隔条不产生焦点框
    class NoFocusSplitContainer : SplitContainer
    {
        protected override void Select(bool directed, bool forward)
        {
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
        readonly int targetBytes;
        readonly Action<byte[], int, int> emit;
        long buffered;

        public DelayLine(int targetBytes, Action<byte[], int, int> emit)
        {
            this.targetBytes = Math.Max(0, targetBytes);
            this.emit = emit;
        }

        public void Write(byte[] data, int len)
        {
            if (len <= 0) return;
            var copy = new byte[len];
            Buffer.BlockCopy(data, 0, copy, 0, len);
            blocks.Enqueue(new Block { Data = copy, Len = len });
            buffered += len;

            while (buffered > targetBytes && blocks.Count > 0)
            {
                var b = blocks.Peek();
                int avail = b.Len - b.Offset;
                int take = (int)Math.Min(avail, buffered - targetBytes);
                emit(b.Data, b.Offset, take);
                b.Offset += take;
                buffered -= take;
                if (b.Offset >= b.Len) blocks.Dequeue();
            }
        }

        public void Clear()
        {
            blocks.Clear();
            buffered = 0;
        }
    }

    class AudioEngine : IDisposable
    {
        WasapiLoopbackCapture loopback;
        WasapiOut leftOut, rightOut;
        BufferedWaveProvider leftBuf, rightBuf;
        AlwaysFullProvider leftProvider, rightProvider;
        MMDevice leftDev, rightDev;
        Action<string> log;
        volatile int sampleRate;
        DelayLine leftLine, rightLine;

        public AudioEngine(MMDevice leftDev, MMDevice rightDev, Action<string> log)
        {
            this.leftDev = leftDev;
            this.rightDev = rightDev;
            this.log = log;
        }

        public void Start(float gain, float master, int prebufferMs, int outLatencyMs, int leftDelayMs, int rightDelayMs)
        {
            var enumerator = new MMDeviceEnumerator();
            var captureDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            log("[启动] 捕获源: " + captureDev.FriendlyName);

            loopback = new WasapiLoopbackCapture(captureDev);
            var wf = loopback.WaveFormat;
            sampleRate = wf.SampleRate;
            log("[启动] 格式: " + wf.SampleRate + "Hz / " + wf.Encoding + " / " + wf.BitsPerSample + "bit / " + wf.Channels + "ch");

            var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(wf.SampleRate, 1);
            leftBuf = new BufferedWaveProvider(outFormat) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true };
            rightBuf = new BufferedWaveProvider(outFormat) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true };

            leftOut = new WasapiOut(leftDev, AudioClientShareMode.Shared, true, outLatencyMs);
            rightOut = new WasapiOut(rightDev, AudioClientShareMode.Shared, true, outLatencyMs);
            leftProvider = new AlwaysFullProvider(leftBuf);
            rightProvider = new AlwaysFullProvider(rightBuf);
            // Play 之前设好增益/主音量, 否则首帧以满音量(1.0)出声造成跳变
            leftProvider.Gain = gain;
            rightProvider.Gain = gain;
            leftProvider.Master = master;
            rightProvider.Master = master;
            leftOut.Init(leftProvider);
            rightOut.Init(rightProvider);

            // 左右独立延迟线 (mono float32 = 4字节/样本, 目标水位对齐到样本边界)
            leftLine = new DelayLine((int)(((long)wf.SampleRate * leftDelayMs * 4 / 1000) & ~3L),
                (buf, off, len) => Split(buf, off, len, 0, leftBuf));
            rightLine = new DelayLine((int)(((long)wf.SampleRate * rightDelayMs * 4 / 1000) & ~3L),
                (buf, off, len) => Split(buf, off, len, 1, rightBuf));

            loopback.DataAvailable += (s, e) =>
            {
                leftLine.Write(e.Buffer, e.BytesRecorded);
                rightLine.Write(e.Buffer, e.BytesRecorded);
            };

            loopback.StartRecording();

            // 预缓冲字节数按采样率动态计算 (mono float32 = 4字节/样本), 向下对齐到样本边界
            int prebufferBytes = (int)(((long)wf.SampleRate * prebufferMs * 4 / 1000) & ~3L);
            int waits = 0;
            // 等待上限随预缓冲与延迟补偿动态放宽 (延迟 2000ms 时固定 2s 会超时)
            int maxWaits = (prebufferMs + Math.Max(leftDelayMs, rightDelayMs) + 1000) / 20 + 50;
            while ((leftBuf.BufferedBytes < prebufferBytes || rightBuf.BufferedBytes < prebufferBytes) && waits < maxWaits)
            { Thread.Sleep(20); waits++; }

            // 修剪快路多攒的水位: 等待以慢路为终点时, 无延迟的快路会多攒数据, 抵消延迟线的时差
            // 两 buf 统一修剪到预缓冲阈值 → 净声道差 = 延迟线的 (L - R)
            TrimTo(leftBuf, prebufferBytes);
            TrimTo(rightBuf, prebufferBytes);

            log("[启动] 预缓冲完成 (L=" + leftBuf.BufferedBytes + " R=" + rightBuf.BufferedBytes +
                " 字节, 档位 " + prebufferMs + "ms / 输出 " + outLatencyMs + "ms)");
            if (leftDelayMs > 0 || rightDelayMs > 0)
                log("[启动] 延迟补偿: 左=" + leftDelayMs + "ms 右=" + rightDelayMs + "ms");

            leftOut.Play();
            rightOut.Play();
            log("[启动] 输出 L=" + leftDev.FriendlyName);
            log("[启动] 输出 R=" + rightDev.FriendlyName);
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

        public string BufferStatus()
        {
            try
            {
                if (leftBuf == null || rightBuf == null || sampleRate <= 0) return "-";
                int msL = leftBuf.BufferedBytes * 1000 / (sampleRate * 4);
                int msR = rightBuf.BufferedBytes * 1000 / (sampleRate * 4);
                return "L=" + msL + "ms R=" + msR + "ms";
            }
            catch { return "-"; }
        }

        public void Stop()
        {
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

        void Split(byte[] data, int offset, int bytes, int channel, BufferedWaveProvider target)
        {
            var f = loopback.WaveFormat;
            int bps = f.BitsPerSample / 8;
            int ch = f.Channels;
            int frame = bps * ch;
            int frames = bytes / frame;
            byte[] outBuf = new byte[frames * 4];

            for (int i = 0; i < frames; i++)
            {
                int off = offset + i * frame + channel * bps;
                float sample;
                if (f.Encoding == WaveFormatEncoding.IeeeFloat)
                    sample = BitConverter.ToSingle(data, off);
                else if (f.BitsPerSample == 16)
                    sample = BitConverter.ToInt16(data, off) / 32768f;
                else
                    sample = 0f;
                Buffer.BlockCopy(BitConverter.GetBytes(sample), 0, outBuf, i * 4, 4);
            }
            target.AddSamples(outBuf, 0, outBuf.Length);
        }

        public void Dispose()
        {
            Stop();
            if (loopback != null) loopback.Dispose();
            if (leftOut != null) leftOut.Dispose();
            if (rightOut != null) rightOut.Dispose();
        }
    }

    class PolicyConfigHelper
    {
        static readonly Guid CLSID_PolicyConfigClient = new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9");

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfigVista
        {
            [System.Runtime.InteropServices.PreserveSig]
            int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
            [System.Runtime.InteropServices.PreserveSig]
            int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
            [System.Runtime.InteropServices.PreserveSig]
            int ResetDeviceFormat(string pszDeviceName);
            [System.Runtime.InteropServices.PreserveSig]
            int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);
            [System.Runtime.InteropServices.PreserveSig]
            int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
            [System.Runtime.InteropServices.PreserveSig]
            int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
            [System.Runtime.InteropServices.PreserveSig]
            int GetShareMode(string pszDeviceName, IntPtr pMode);
            [System.Runtime.InteropServices.PreserveSig]
            int SetShareMode(string pszDeviceName, IntPtr mode);
            [System.Runtime.InteropServices.PreserveSig]
            int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
            [System.Runtime.InteropServices.PreserveSig]
            int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
            [System.Runtime.InteropServices.PreserveSig]
            int SetDefaultEndpoint([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszDeviceName, int role);
            [System.Runtime.InteropServices.PreserveSig]
            int SetEndpointVisibility(string pszDeviceName, bool bVisible);
        }

        public static int SetDefaultDevice(string deviceId)
        {
            Type t = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient);
            object obj = Activator.CreateInstance(t);
            try
            {
                var cfg = (IPolicyConfigVista)obj;
                int hr = 0;
                // eConsole=0, eMultimedia=1, eCommunications=2
                for (int role = 0; role <= 2; role++)
                {
                    hr = cfg.SetDefaultEndpoint(deviceId, role);
                    if (hr != 0) break;
                }
                return hr;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(obj);
            }
        }
    }

    class AlwaysFullProvider : ISampleProvider
    {
        readonly BufferedWaveProvider src;
        readonly byte[] tmp = new byte[65536];
        volatile float gain = 1.0f;
        volatile float master = 1.0f;

        public AlwaysFullProvider(BufferedWaveProvider src) { this.src = src; }

        public float Gain { get { return gain; } set { gain = value; } }

        public float Master { get { return master; } set { master = value; } }

        public WaveFormat WaveFormat { get { return src.WaveFormat; } }

        public int Read(float[] samples, int offset, int count)
        {
            int want = Math.Min(count * 4, tmp.Length);
            want = Math.Min(want, src.BufferedBytes);
            int got = want > 0 ? src.Read(tmp, 0, want) : 0;
            int n = got / 4;
            float g = gain;
            float m = master;
            for (int i = 0; i < n; i++)
            {
                float v = BitConverter.ToSingle(tmp, i * 4) * g * m;
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                samples[offset + i] = v;
            }
            for (int i = n; i < count; i++) samples[offset + i] = 0f;
            return count;
        }
    }
}
