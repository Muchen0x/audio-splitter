using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace StereoSplitter
{
    class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
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
        Button btnStart;
        TextBox txtLog;
        CheckBox chkMuteL, chkMuteR, chkMuteMaster;
        float pendingGain = 1.0f;

        MMDevice syncDev;
        AudioEndpointVolume syncVol;
        bool volSyncing = false;
        bool autoConfiguring = false;
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

            Controls.AddRange(new Control[] { lblL, cmbLeft, lblR, cmbRight, lblD, cmbDefault, lblVL, volLeft, lblVolLeft, chkMuteL, lblVR, volRight, lblVolRight, chkMuteR, lblVM, volMaster, lblVolMaster, chkMuteMaster, lblG, volGain, lblVolGain, btnStart, txtLog });

            btnStart.Click += BtnStart_Click;
            cmbDefault.SelectedIndexChanged += CmbDefault_SelectedIndexChanged;
            volLeft.Scroll += (s, e) => { lblVolLeft.Text = volLeft.Value + "%"; ApplyVolumes(); };
            volRight.Scroll += (s, e) => { lblVolRight.Text = volRight.Value + "%"; ApplyVolumes(); };
            volMaster.Scroll += (s, e) =>
            {
                lblVolMaster.Text = volMaster.Value + "%";
                ApplyVolumes();
                // 双向同步: 写入当前默认输出设备的端点音量
                if (!volSyncing && syncVol != null)
                {
                    try { syncVol.MasterVolumeLevelScalar = volMaster.Value / 100f; }
                    catch (Exception ex) { Log("同步端点音量失败: " + ex.Message); }
                }
            };
            chkMuteMaster.CheckedChanged += (s, e) =>
            {
                ApplyVolumes();
                if (!volSyncing && syncVol != null)
                {
                    try { syncVol.Mute = chkMuteMaster.Checked; }
                    catch { }
                }
            };
            volGain.Scroll += (s, e) =>
            {
                lblVolGain.Text = volGain.Value + "%";
                pendingGain = volGain.Value / 100f;
                if (engine != null) engine.SetGain(pendingGain);
            };
            chkMuteL.CheckedChanged += (s, e) => ApplyVolumes();
            chkMuteR.CheckedChanged += (s, e) => ApplyVolumes();

            Load += MainForm_Load;
            FormClosing += MainForm_FormClosing;
        }

        void MainForm_Load(object sender, EventArgs e)
        {
            try
            {
                enumerator = new MMDeviceEnumerator();
                RefreshDevices();
                RecordRestoreTarget();
                BindDefaultVolumeSync();
                Log("就绪。在「默认输出」选择设备即自动完成配置，然后点「开始分流」。");
                Log("提示: 默认输出设备不能与左右目标相同，否则会回环。");
            }
            catch (Exception ex)
            {
                Log("初始化失败: " + ex.Message);
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
                    Log("当前默认输出是虚拟设备，退出时将恢复到: " + FindNameById(restoreDefaultId));
                }
            }
            catch (Exception ex)
            {
                Log("记录默认输出失败: " + ex.Message);
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

        void BindDefaultVolumeSync()
        {
            try
            {
                var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                if (syncDev != null && syncDev.ID == def.ID && syncVol != null)
                    return; // 已绑定相同设备

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

                volSyncing = true;
                int v = (int)Math.Round(syncVol.MasterVolumeLevelScalar * 100);
                volMaster.Value = Math.Max(0, Math.Min(100, v));
                lblVolMaster.Text = volMaster.Value + "%";
                chkMuteMaster.Checked = syncVol.Mute;
                volSyncing = false;

                Log("主音量已同步到默认输出: " + def.FriendlyName + " (" + v + "%)");
            }
            catch (Exception ex)
            {
                volSyncing = false;
                Log("端点音量绑定失败: " + ex.Message);
                syncVol = null;
                syncDev = null;
            }
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
            if (volSyncing) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    volSyncing = true;
                    int v = Math.Max(0, Math.Min(100, (int)Math.Round(data.MasterVolume * 100)));
                    if (volMaster.Value != v)
                    {
                        volMaster.Value = v;
                        lblVolMaster.Text = v + "%";
                        ApplyVolumes();
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

        void CmbDefault_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (autoConfiguring) return;
            AutoConfigure();
        }

        void AutoConfigure()
        {
            if (cmbDefault.SelectedIndex < 0) return;

            if (running)
            {
                Log("分流运行中，请先停止再切换默认输出。");
                autoConfiguring = true;
                try
                {
                    var def0 = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    int idx0 = devices.FindIndex(d => d.ID == def0.ID);
                    if (idx0 >= 0) cmbDefault.SelectedIndex = idx0;
                }
                finally { autoConfiguring = false; }
                return;
            }

            var dev = devices[cmbDefault.SelectedIndex];
            autoConfiguring = true;
            try
            {
                // 1. 设为系统默认输出
                int hr = PolicyConfigHelper.SetDefaultDevice(dev.ID);
                if (hr != 0)
                {
                    Log("设置默认输出失败 HRESULT=0x" + hr.ToString("X8"));
                    var def1 = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    int idx1 = devices.FindIndex(d => d.ID == def1.ID);
                    if (idx1 >= 0) cmbDefault.SelectedIndex = idx1;
                    return;
                }
                Log("默认输出 → [" + cmbDefault.SelectedIndex + "] " + dev.FriendlyName);

                int defIdx = cmbDefault.SelectedIndex;

                // 2. 左右目标若与默认冲突/无效 → 自动选前两个非默认非虚拟设备
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
                        Log("目标设备 → L=" + devices[l].FriendlyName + " / R=" + devices[r].FriendlyName);
                    }
                    else
                    {
                        Log("无法自动选择目标设备，请手动选择左右声道。");
                    }
                }
                else
                {
                    Log("目标设备 L=" + devices[cmbLeft.SelectedIndex].FriendlyName +
                        " / R=" + devices[cmbRight.SelectedIndex].FriendlyName);
                }

                // 3. 重绑主音量同步到新默认设备
                BindDefaultVolumeSync();

                Log("配置完成，点击「开始分流」。");
            }
            catch (Exception ex)
            {
                Log("自动配置失败: " + ex.Message);
            }
            finally
            {
                autoConfiguring = false;
            }
        }

        void RefreshDevices()
        {
            autoConfiguring = true;
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
                Log("默认输出: [" + defIdx + "] " + def.FriendlyName);
            }
            finally
            {
                autoConfiguring = false;
            }
        }

        void BtnStart_Click(object sender, EventArgs e)
        {
            if (running)
            {
                StopEngine();
                return;
            }

            if (cmbLeft.SelectedIndex < 0 || cmbRight.SelectedIndex < 0)
            {
                MessageBox.Show("请选择左右设备。", "提示");
                return;
            }
            if (cmbLeft.SelectedIndex == cmbRight.SelectedIndex)
            {
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
                running = true;
                btnStart.Text = "停止分流";
                ApplyVolumes();
                Log("分流已启动。");
            }
            catch (Exception ex)
            {
                Log("启动失败: " + ex.Message);
                if (engine != null) { engine.Dispose(); engine = null; }
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
            running = false;
            btnStart.Text = "开始分流";
            Log("已停止。");
        }

        void ApplyVolumes()
        {
            if (engine == null) return;
            float master = volMaster.Value / 100f;
            float l = master * (chkMuteL.Checked ? 0f : volLeft.Value / 100f);
            float r = master * (chkMuteR.Checked ? 0f : volRight.Value / 100f);
            if (chkMuteMaster.Checked) { l = 0f; r = 0f; }
            engine.SetVolume(l, r);
        }

        void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
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
                        if (hr == 0) Log("已恢复默认输出: " + FindNameById(restoreDefaultId));
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
        }

        delegate void LogDelegate(string msg);
        void Log(string msg)
        {
            if (txtLog.InvokeRequired)
            {
                try { BeginInvoke(new LogDelegate(Log), msg); } catch { }
                return;
            }
            txtLog.AppendText(msg + "\r\n");
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
            log("捕获源: " + captureDev.FriendlyName);

            loopback = new WasapiLoopbackCapture(captureDev);
            log("格式: " + loopback.WaveFormat);

            var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(loopback.WaveFormat.SampleRate, 1);
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

            leftOut.Play();
            rightOut.Play();
            log("输出: L=" + leftDev.FriendlyName);
            log("输出: R=" + rightDev.FriendlyName);
        }

        public void SetVolume(float l, float r)
        {
            if (leftOut != null) leftOut.Volume = l;
            if (rightOut != null) rightOut.Volume = r;
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

        public AlwaysFullProvider(BufferedWaveProvider src) { this.src = src; }

        public float Gain { get { return gain; } set { gain = value; } }

        public WaveFormat WaveFormat { get { return src.WaveFormat; } }

        public int Read(float[] samples, int offset, int count)
        {
            int want = Math.Min(count * 4, tmp.Length);
            want = Math.Min(want, src.BufferedBytes);
            int got = want > 0 ? src.Read(tmp, 0, want) : 0;
            int n = got / 4;
            float g = gain;
            for (int i = 0; i < n; i++)
            {
                float v = BitConverter.ToSingle(tmp, i * 4) * g;
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                samples[offset + i] = v;
            }
            for (int i = n; i < count; i++) samples[offset + i] = 0f;
            return count;
        }
    }
}
