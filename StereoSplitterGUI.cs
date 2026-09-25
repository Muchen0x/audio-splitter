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

    class MainForm : Form
    {
        ComboBox cmbLeft, cmbRight;
        TrackBar volLeft, volRight, volMaster;
        Label lblVolLeft, lblVolRight, lblVolMaster;
        Button btnStart;
        TextBox txtLog;
        CheckBox chkMuteL, chkMuteR;

        List<MMDevice> devices = new List<MMDevice>();
        MMDeviceEnumerator enumerator;

        AudioEngine engine;
        bool running = false;

        public MainForm()
        {
            Text = "Stereo Splitter - 双声道分流";
            Size = new Size(520, 570);
            MinimumSize = new Size(520, 570);
            Font = new Font("Microsoft YaHei UI", 9F);

            var lblL = new Label { Text = "左声道设备:", Location = new Point(15, 15), AutoSize = true };
            cmbLeft = new ComboBox { Location = new Point(110, 12), Width = 375, DropDownStyle = ComboBoxStyle.DropDownList };

            var lblR = new Label { Text = "右声道设备:", Location = new Point(15, 45), AutoSize = true };
            cmbRight = new ComboBox { Location = new Point(110, 42), Width = 375, DropDownStyle = ComboBoxStyle.DropDownList };

            var lblVL = new Label { Text = "左音量:", Location = new Point(15, 80), AutoSize = true };
            volLeft = new TrackBar { Location = new Point(80, 75), Width = 300, Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10 };
            lblVolLeft = new Label { Text = "100%", Location = new Point(390, 80), AutoSize = true };
            chkMuteL = new CheckBox { Text = "静音", Location = new Point(435, 79), AutoSize = true };

            var lblVR = new Label { Text = "右音量:", Location = new Point(15, 115), AutoSize = true };
            volRight = new TrackBar { Location = new Point(80, 110), Width = 300, Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10 };
            lblVolRight = new Label { Text = "100%", Location = new Point(390, 115), AutoSize = true };
            chkMuteR = new CheckBox { Text = "静音", Location = new Point(435, 114), AutoSize = true };

            var lblVM = new Label { Text = "主音量:", Location = new Point(15, 150), AutoSize = true };
            volMaster = new TrackBar { Location = new Point(80, 145), Width = 300, Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10 };
            lblVolMaster = new Label { Text = "100%", Location = new Point(390, 150), AutoSize = true };

            btnStart = new Button { Text = "开始分流", Location = new Point(190, 185), Width = 130, Height = 35 };

            txtLog = new TextBox
            {
                Location = new Point(15, 230),
                Size = new Size(475, 265),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.White,
                Font = new Font("Consolas", 9F)
            };

            Controls.AddRange(new Control[] { lblL, cmbLeft, lblR, cmbRight, lblVL, volLeft, lblVolLeft, chkMuteL, lblVR, volRight, lblVolRight, chkMuteR, lblVM, volMaster, lblVolMaster, btnStart, txtLog });

            btnStart.Click += BtnStart_Click;
            volLeft.Scroll += (s, e) => { lblVolLeft.Text = volLeft.Value + "%"; ApplyVolumes(); };
            volRight.Scroll += (s, e) => { lblVolRight.Text = volRight.Value + "%"; ApplyVolumes(); };
            volMaster.Scroll += (s, e) => { lblVolMaster.Text = volMaster.Value + "%"; ApplyVolumes(); };
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
                Log("就绪。选择左右设备后点击「开始分流」。");
                Log("提示: 默认输出设备不能与左右目标相同，否则会回环。");
            }
            catch (Exception ex)
            {
                Log("初始化失败: " + ex.Message);
            }
        }

        void RefreshDevices()
        {
            devices.Clear();
            cmbLeft.Items.Clear();
            cmbRight.Items.Clear();

            var coll = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int i = 0; i < coll.Count; i++)
            {
                var dev = coll[i];
                devices.Add(dev);
                cmbLeft.Items.Add("[" + i + "] " + dev.FriendlyName);
                cmbRight.Items.Add("[" + i + "] " + dev.FriendlyName);
            }

            var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            int defIdx = devices.FindIndex(d => d.ID == def.ID);
            if (cmbLeft.Items.Count > 0) cmbLeft.SelectedIndex = 0;
            if (cmbRight.Items.Count > 1) cmbRight.SelectedIndex = 1;
            Log("默认输出: [" + defIdx + "] " + def.FriendlyName);
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
            engine.SetVolume(l, r);
        }

        void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopEngine();
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
            leftOut.Init(new AlwaysFullProvider(leftBuf));
            rightOut.Init(new AlwaysFullProvider(rightBuf));

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

    class AlwaysFullProvider : ISampleProvider
    {
        readonly BufferedWaveProvider src;
        readonly byte[] tmp = new byte[65536];

        public AlwaysFullProvider(BufferedWaveProvider src) { this.src = src; }

        public WaveFormat WaveFormat { get { return src.WaveFormat; } }

        public int Read(float[] samples, int offset, int count)
        {
            int want = Math.Min(count * 4, tmp.Length);
            want = Math.Min(want, src.BufferedBytes);
            int got = want > 0 ? src.Read(tmp, 0, want) : 0;
            int n = got / 4;
            for (int i = 0; i < n; i++) samples[offset + i] = BitConverter.ToSingle(tmp, i * 4);
            for (int i = n; i < count; i++) samples[offset + i] = 0f;
            return count;
        }
    }
}
