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
        NoWheelComboBox cmbLeft, cmbRight, cmbDefault;
        TrackBar volLeft, volRight, volMaster, volGain;
        Label lblVolLeft, lblVolRight, lblVolMaster, lblVolGain;
        Label lblDot, lblStatus;
        Button btnStart;
        TextBox txtLog;
        CheckBox chkMuteL, chkMuteR, chkMuteMaster;
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

        List<MMDevice> devices = new List<MMDevice>();
        MMDeviceEnumerator enumerator;

        AudioEngine engine;
        bool running = false;

        public MainForm()
        {
            Text = "Stereo Splitter - 双声道分流";
            Size = new Size(520, 655);
            MinimumSize = new Size(520, 655);
            Font = new Font("Microsoft YaHei UI", 9F);

            var lblL = new Label { Text = "左声道设备:", Location = new Point(15, 15), AutoSize = true };
            cmbLeft = new NoWheelComboBox { Location = new Point(110, 12), Width = 375, DropDownStyle = ComboBoxStyle.DropDownList };

            var lblR = new Label { Text = "右声道设备:", Location = new Point(15, 45), AutoSize = true };
            cmbRight = new NoWheelComboBox { Location = new Point(110, 42), Width = 375, DropDownStyle = ComboBoxStyle.DropDownList };

            var lblD = new Label { Text = "默认输出:", Location = new Point(15, 75), AutoSize = true };
            cmbDefault = new NoWheelComboBox { Location = new Point(110, 72), Width = 375, DropDownStyle = ComboBoxStyle.DropDownList };

            var lblVL = new Label { Text = "左音量:", Location = new Point(15, 115), AutoSize = true };
            volLeft = new TrackBar { Location = new Point(80, 110), Width = 300, Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10 };
            lblVolLeft = new Label { Text = "100%", Location = new Point(390, 115), AutoSize = true };
            chkMuteL = new CheckBox { Text = "静音", Location = new Point(435, 114), AutoSize = true };

            var lblVR = new Label { Text = "右音量:", Location = new Point(15, 150), AutoSize = true };
            volRight = new TrackBar { Location = new Point(80, 145), Width = 300, Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10 };
            lblVolRight = new Label { Text = "100%", Location = new Point(390, 150), AutoSize = true };
            chkMuteR = new CheckBox { Text = "静音", Location = new Point(435, 149), AutoSize = true };

            var lblVM = new Label { Text = "主音量:", Location = new Point(15, 185), AutoSize = true };
            volMaster = new TrackBar { Location = new Point(80, 180), Width = 300, Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10 };
            lblVolMaster = new Label { Text = "100%", Location = new Point(390, 185), AutoSize = true };
            chkMuteMaster = new CheckBox { Text = "静音", Location = new Point(435, 184), AutoSize = true };

            var lblG = new Label { Text = "增益:", Location = new Point(15, 220), AutoSize = true };
            volGain = new TrackBar { Location = new Point(80, 215), Width = 300, Minimum = 0, Maximum = 200, Value = 100, TickFrequency = 10 };
            lblVolGain = new Label { Text = "100%", Location = new Point(390, 220), AutoSize = true };

            btnStart = new Button { Text = "开始分流", Location = new Point(190, 255), Width = 130, Height = 35 };

            lblDot = new Label { Text = "●", Location = new Point(17, 260), AutoSize = true, ForeColor = Color.Gray, Font = new Font("Microsoft YaHei UI", 10F) };
            lblStatus = new Label { Text = "状态: 就绪", Location = new Point(37, 265), AutoSize = true, ForeColor = Color.Gray };

            chkMinimizeToTray = new CheckBox { Text = "关闭时最小化到托盘", Location = new Point(335, 263), AutoSize = true };

            txtLog = new TextBox
            {
                Location = new Point(15, 300),
                Size = new Size(475, 265),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
                Font = new Font("Consolas", 9F)
            };

            Controls.AddRange(new Control[] { lblL, cmbLeft, lblR, cmbRight, lblD, cmbDefault, lblVL, volLeft, lblVolLeft, chkMuteL, lblVR, volRight, lblVolRight, chkMuteR, lblVM, volMaster, lblVolMaster, chkMuteMaster, lblG, volGain, lblVolGain, lblDot, lblStatus, chkMinimizeToTray, btnStart, txtLog });

            btnStart.Click += BtnStart_Click;
            chkMinimizeToTray.CheckedChanged += (s, e) => SaveSettings();
            cmbLeft.SelectedIndexChanged += (s, e) => { if (!loadingDevices) { BindChannelVolumeSync("[音量]"); SaveSettings(); } };
            cmbRight.SelectedIndexChanged += (s, e) => { if (!loadingDevices) { BindChannelVolumeSync("[音量]"); SaveSettings(); } };
            cmbDefault.SelectedIndexChanged += (s, e) => { if (!loadingDevices) { BindDefaultVolumeSync("[音量]"); SaveSettings(); } };
            volLeft.Scroll += (s, e) =>
            {
                lblVolLeft.Text = volLeft.Value + "%";
                // 双向同步: 写入左声道设备端点音量
                if (!volSyncing && syncLeftVol != null)
                {
                    try { syncLeftVol.MasterVolumeLevelScalar = volLeft.Value / 100f; }
                    catch (Exception ex) { Log("[错误] 同步左声道端点音量失败: " + ex.Message); }
                }
                SaveSettings();
            };
            volRight.Scroll += (s, e) =>
            {
                lblVolRight.Text = volRight.Value + "%";
                // 双向同步: 写入右声道设备端点音量
                if (!volSyncing && syncRightVol != null)
                {
                    try { syncRightVol.MasterVolumeLevelScalar = volRight.Value / 100f; }
                    catch (Exception ex) { Log("[错误] 同步右声道端点音量失败: " + ex.Message); }
                }
                SaveSettings();
            };
            volMaster.Scroll += (s, e) =>
            {
                lblVolMaster.Text = volMaster.Value + "%";
                ApplyVolumes();
                // 双向同步: 写入当前默认输出设备的端点音量
                if (!volSyncing && syncVol != null)
                {
                    try { syncVol.MasterVolumeLevelScalar = volMaster.Value / 100f; }
                    catch (Exception ex) { Log("[错误] 同步端点音量失败: " + ex.Message); }
                }
                SaveSettings();
            };
            chkMuteMaster.CheckedChanged += (s, e) =>
            {
                ApplyVolumes();
                if (!volSyncing && syncVol != null)
                {
                    try { syncVol.Mute = chkMuteMaster.Checked; }
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
            volLeft.Enabled = !busy;
            volRight.Enabled = !busy;
            volMaster.Enabled = !busy;
            volGain.Enabled = !busy;
            chkMuteL.Enabled = !busy;
            chkMuteR.Enabled = !busy;
            chkMuteMaster.Enabled = !busy;
            chkMinimizeToTray.Enabled = !busy;
            btnStart.Enabled = !busy;

            if (trayIcon != null) trayIcon.Text = "Stereo Splitter - " + txt;
        }

        void MainForm_Load(object sender, EventArgs e)
        {
            try
            {
                enumerator = new MMDeviceEnumerator();
                RefreshDevices();
                RecordRestoreTarget();
                BindDefaultVolumeSync("[初始化]");
                BindChannelVolumeSync("[初始化]");
                ApplyIniVolumeOverrides();
                SetAppState(AppState.Ready);
                RegisterActivateHandler();
                Log("[初始化] 就绪。在「默认输出」选择设备，点「开始分流」时自动完成配置。");
                Log("[初始化] 提示: 默认输出设备不能与左右目标相同，否则会回环。");
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
                    // 退旧
                    if (syncVol != null)
                    {
                        try { syncVol.OnVolumeNotification -= SyncVol_OnVolumeNotification; } catch { }
                        try { syncVol.Dispose(); } catch { }
                        syncVol = null;
                        syncDev = null;
                    }

                    // 绑新
                    syncDev = def;
                    syncVol = def.AudioEndpointVolume;
                    syncVol.OnVolumeNotification += SyncVol_OnVolumeNotification;
                }

                volSyncing = true;
                int v = (int)Math.Round(syncVol.MasterVolumeLevelScalar * 100);
                volMaster.Value = Math.Max(0, Math.Min(100, v));
                lblVolMaster.Text = volMaster.Value + "%";
                chkMuteMaster.Checked = syncVol.Mute;
                volSyncing = false;

                Log(stage + " 主音量同步 → [" + FindIdxById(def.ID) + "] " + def.FriendlyName + " (" + v + "%" + (syncVol.Mute ? ", 静音" : "") + ")");
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
                    if (volSyncing) return;
                    volSyncing = true;
                    int v = Math.Max(0, Math.Min(100, (int)Math.Round(data.MasterVolume * 100)));
                    if (volMaster.Value != v)
                    {
                        volMaster.Value = v;
                        lblVolMaster.Text = v + "%";
                        ApplyVolumes();
                        SaveSettings();
                    }
                    if (chkMuteMaster.Checked != data.Muted)
                    {
                        chkMuteMaster.Checked = data.Muted;
                        ApplyVolumes();
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
            if (vol != null)
            {
                try { vol.OnVolumeNotification -= handler; } catch { }
                try { vol.Dispose(); } catch { }
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
                    if (volSyncing) return;
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
                    if (volSyncing) return;
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
                    Log("[错误] 回环检查未通过: 默认输出与左右目标相同。");
                    return false;
                }
                Log("[配置] 回环检查通过 (默认输出与左右目标无冲突)");

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
                File.WriteAllText(IniPath, sb.ToString());
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
                    if (syncVol != null)
                    {
                        try { syncVol.Mute = m; }
                        catch (Exception ex)
                        {
                            Log("[错误] 恢复主静音失败: " + ex.Message);
                            try { chkMuteMaster.Checked = syncVol.Mute; } catch { }
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
                    " 增益=" + volGain.Value + "%");
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
                RestoreDeviceSelection("DeviceDefault", cmbDefault, "默认输出");

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
                MessageBox.Show("请选择默认输出设备。", "提示");
                return;
            }

            SetAppState(AppState.Starting);

            // 点击开始时自动配置: 设默认输出 + 重绑音量 + 自动选左右 + 回环检查
            if (!AutoConfigure())
            {
                SetAppState(AppState.Failed);
                MessageBox.Show("自动配置失败，已中止启动。\n详见日志。", "启动中止");
                return;
            }

            if (cmbLeft.SelectedIndex < 0 || cmbRight.SelectedIndex < 0)
            {
                SetAppState(AppState.Failed);
                MessageBox.Show("请选择左右设备。", "提示");
                return;
            }
            if (cmbLeft.SelectedIndex == cmbRight.SelectedIndex)
            {
                SetAppState(AppState.Failed);
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
                SetAppState(AppState.Failed);
                MessageBox.Show(
                    "默认输出设备与目标设备相同，会造成声音回环！\n\n" +
                    "请先在 Windows 声音设置中把默认输出改为:\n" + string.Join("\n", others),
                    "设备冲突");
                return;
            }

            try
            {
                engine = new AudioEngine(devices[cmbLeft.SelectedIndex], devices[rightIdxSafe()], Log);
                engine.Start();
                engine.SetGain(pendingGain);
                SetAppState(AppState.Running);
                ApplyVolumes();
                Log("[启动] 端点绑定: 主=" + DevInfo(syncDev) + " / 左=" + DevInfo(syncLeftDev) + " / 右=" + DevInfo(syncRightDev));
                Log("[启动] 分流已启动 (增益 " + (int)Math.Round(pendingGain * 100) + "%)");
            }
            catch (Exception ex)
            {
                Log("[错误] 启动失败: " + ex.Message);
                if (engine != null) { engine.Dispose(); engine = null; }
                SetAppState(AppState.Failed);
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
        }

        void ApplyVolumes()
        {
            if (engine == null) return;
            // 左右音量/静音走设备端点(已双向绑定)，主音量走采样层
            float master = volMaster.Value / 100f;
            if (chkMuteMaster.Checked) master = 0f;
            engine.SetMaster(master);
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

            if (syncVol != null)
            {
                try { syncVol.OnVolumeNotification -= SyncVol_OnVolumeNotification; } catch { }
                try { syncVol.Dispose(); } catch { }
                syncVol = null;
                syncDev = null;
            }
            UnbindChannel(ref syncLeftDev, ref syncLeftVol, LeftVol_OnVolumeNotification);
            UnbindChannel(ref syncRightDev, ref syncRightVol, RightVol_OnVolumeNotification);

            SaveSettings();
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
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + msg + "\r\n");
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

        public AudioEngine(MMDevice leftDev, MMDevice rightDev, Action<string> log)
        {
            this.leftDev = leftDev;
            this.rightDev = rightDev;
            this.log = log;
        }

        public void Start()
        {
            var enumerator = new MMDeviceEnumerator();
            var captureDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            log("[启动] 捕获源: " + captureDev.FriendlyName);

            loopback = new WasapiLoopbackCapture(captureDev);
            var wf = loopback.WaveFormat;
            log("[启动] 格式: " + wf.SampleRate + "Hz / " + wf.Encoding + " / " + wf.BitsPerSample + "bit / " + wf.Channels + "ch");

            var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(wf.SampleRate, 1);
            leftBuf = new BufferedWaveProvider(outFormat) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true };
            rightBuf = new BufferedWaveProvider(outFormat) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true };

            leftOut = new WasapiOut(leftDev, AudioClientShareMode.Shared, true, 50);
            rightOut = new WasapiOut(rightDev, AudioClientShareMode.Shared, true, 50);
            leftProvider = new AlwaysFullProvider(leftBuf);
            rightProvider = new AlwaysFullProvider(rightBuf);
            leftOut.Init(leftProvider);
            rightOut.Init(rightProvider);

            loopback.DataAvailable += (s, e) =>
            {
                Split(e.Buffer, e.BytesRecorded, 0, leftBuf);
                Split(e.Buffer, e.BytesRecorded, 1, rightBuf);
            };

            loopback.StartRecording();

            int waits = 0;
            while (leftBuf.BufferedBytes < 19200 && waits < 100) { Thread.Sleep(20); waits++; }
            log("[启动] 预缓冲完成 (" + leftBuf.BufferedBytes + " 字节)");

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

        public void Stop()
        {
            if (loopback != null) { try { loopback.StopRecording(); } catch { } }
            if (leftOut != null) { try { leftOut.Stop(); } catch { } }
            if (rightOut != null) { try { rightOut.Stop(); } catch { } }
        }

        void Split(byte[] data, int bytes, int channel, BufferedWaveProvider target)
        {
            var f = loopback.WaveFormat;
            int bps = f.BitsPerSample / 8;
            int ch = f.Channels;
            int frame = bps * ch;
            int frames = bytes / frame;
            byte[] outBuf = new byte[frames * 4];

            for (int i = 0; i < frames; i++)
            {
                int off = i * frame + channel * bps;
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
