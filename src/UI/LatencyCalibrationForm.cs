using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>一路待校准的输出 (一台设备)。</summary>
    sealed class CalibLane
    {
        public string DeviceId = "";
        public string Display = "";        // 设备友好名
        public int CurrentDelayMs;         // 卡片上当前的延迟值
        public double MeasuredMs = double.NaN;
        public double Confidence;
        public double SpreadMs;
        public int SuggestedMs;
        public bool HasSuggestion;
        /// <summary>每次采到的延迟 (毫秒)。多轮取中位数, 单次抖动不左右结果。</summary>
        public readonly List<double> Samples = new List<double>();
    }

    /// <summary>
    /// 延迟校准窗口: 让麦克风"听"每一路, 测出它们到耳朵的真实延迟差, 再把差值补到快的那一路上。
    ///
    /// <para><b>为什么要人参与</b>: 蓝牙/声卡的真实延迟藏在设备内部, 驱动报不出来;
    /// 唯一可靠的办法是放一段声音、用麦克风录下来量。麦克风要摆在**实际听音位置**,
    /// 这样量到的差值天然包含两只音箱的距离差, 补出来的声场才是对齐的。</para>
    ///
    /// <para><b>测量期间分流必须不出声</b>: 否则分流器自己的音频会和测试音混在一起,
    /// 相关系数被拉低, 结果不可信。由调用方保证, 两种方式:</para>
    /// <list type="bullet">
    ///   <item>未运行: 直接测。</item>
    ///   <item>运行中 (<c>hostMuted=true</c>): 上层把分流的**采样层**临时静音 ——
    ///         链路照跑、端点不动, 只是贡献 0, 麦克风只收得到扫频。</item>
    /// </list>
    /// </summary>
    sealed class LatencyCalibrationForm : Form
    {
        /// <summary>
        /// 每一路测几段。这些段在**同一个播放流**里连着走 (前面另有 2 段预热不计),
        /// 取中位数。不再按"轮次 × 路"交替: 交替意味着每测一次就要重开一次流,
        /// 而蓝牙端点每次重开都要重新建链, 那是在测冷启动延迟。
        /// </summary>
        const int Takes = 3;

        readonly IList<CalibLane> lanes;
        readonly Action<string, int> applyDelay;   // (deviceId, delayMs) → 写回卡片并保存
        /// <summary>实际分流用的输出缓冲档位 (毫秒)。测量必须用同一个值, 否则两路的缓冲差测不准。</summary>
        readonly int outLatencyMs;
        /// <summary>分流是否正在运行、且已被上层临时静音 (只影响提示文案与失败时的建议)。</summary>
        readonly bool hostMuted;

        readonly NoWheelComboBox cmbMic = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Button btnRescan = new NoCueButton { Text = "刷新" };
        readonly ListView lv = new ListView();
        readonly Label lblTip = new Label();
        readonly Label lblState = new Label();
        readonly Button btnStart = new NoCueButton { Text = "开始校准" };
        readonly Button btnApply = new NoCueButton { Text = "应用建议值" };
        readonly Button btnClose = new NoCueButton { Text = "关闭" };
        readonly TextBox txtLog = new TextBox();

        List<MicDeviceInfo> mics = new List<MicDeviceInfo>();
        Thread worker;
        volatile bool cancel;
        bool measuring;
        bool applied;

        public LatencyCalibrationForm(IList<CalibLane> lanes, int outLatencyMs, Action<string, int> applyDelay, bool hostMuted = false)
        {
            if (lanes == null) throw new ArgumentNullException("lanes");
            this.lanes = lanes;
            this.applyDelay = applyDelay;
            this.outLatencyMs = Math.Max(5, Math.Min(500, outLatencyMs));
            this.hostMuted = hostMuted;

            Text = "延迟校准 · 麦克风互相关";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(580, 520);
            Font = new Font("Microsoft YaHei UI", 9F);

            BuildUi();
            ReloadMics();
            FillLanes();
            RefreshState();
        }

        // ==================== 界面 ====================

        void BuildUi()
        {
            var lblMic = new Label { Text = "麦克风:", Location = new Point(12, 15), AutoSize = true };

            cmbMic.Location = new Point(72, 12);
            cmbMic.Width = 400;
            cmbMic.DropDownWidth = 460;

            btnRescan.Location = new Point(480, 11);
            btnRescan.Width = 88;
            btnRescan.Height = 24;
            btnRescan.Click += (s, e) => ReloadMics();

            lblTip.Location = new Point(12, 44);
            lblTip.Size = new Size(556, 48);
            lblTip.Text = "把麦克风放在你实际听音的位置（两只音箱中间），校准期间请保持安静。\n" +
                "程序会逐路播放 60ms 扫频（每路先预热 2 秒再测 3 次），测出每一路「声音到达耳朵」的时间差，\n" +
                "再把差值补到较快的那一路。缓冲按当前「延迟」档位测量；" +
                (hostMuted
                    ? "分流已临时静音（关闭本窗口后自动恢复）。"
                    : "分流器需处于「已停止」状态。");
            lblTip.ForeColor = Color.FromArgb(90, 96, 106);

            lv.Location = new Point(12, 96);
            lv.Size = new Size(556, 140);
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = true;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.MultiSelect = false;
            lv.HideSelection = true;
            lv.Columns.Add("输出设备", 226);
            lv.Columns.Add("当前延迟", 66, HorizontalAlignment.Right);
            lv.Columns.Add("实测", 66, HorizontalAlignment.Right);
            lv.Columns.Add("建议", 66, HorizontalAlignment.Right);
            lv.Columns.Add("状态", 112);

            lblState.Location = new Point(12, 244);
            lblState.Size = new Size(556, 20);
            lblState.ForeColor = Color.FromArgb(90, 96, 106);

            btnStart.Location = new Point(12, 268);
            btnStart.Size = new Size(112, 28);
            btnStart.Click += (s, e) => StartMeasure();

            btnApply.Location = new Point(132, 268);
            btnApply.Size = new Size(112, 28);
            btnApply.Click += (s, e) => ApplySuggestions();

            btnClose.Location = new Point(478, 268);
            btnClose.Size = new Size(90, 28);
            btnClose.Click += (s, e) => Close();

            txtLog.Location = new Point(12, 306);
            txtLog.Size = new Size(556, 202);
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.White;
            txtLog.WordWrap = true;

            Controls.AddRange(new Control[]
            {
                lblMic, cmbMic, btnRescan, lblTip, lv, lblState,
                btnStart, btnApply, btnClose, txtLog
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            cancel = true;       // 测量线程是后台线程, 让它自己发现取消并退出
            base.OnFormClosing(e);
        }

        // ==================== 数据填充 ====================

        void ReloadMics()
        {
            mics = MicLatencyProbe.ListDevices();
            cmbMic.BeginUpdate();
            try
            {
                cmbMic.Items.Clear();
                for (int i = 0; i < mics.Count; i++) cmbMic.Items.Add(mics[i].Name);
            }
            finally { cmbMic.EndUpdate(); }

            if (mics.Count == 0)
            {
                lblState.Text = "没有找到可用的麦克风/录音设备 —— 请插上麦克风并确认系统「隐私 › 麦克风」已允许桌面应用访问。";
                btnStart.Enabled = false;
                return;
            }
            cmbMic.SelectedIndex = 0;
            RefreshState();
        }

        void FillLanes()
        {
            lv.BeginUpdate();
            try
            {
                lv.Items.Clear();
                for (int i = 0; i < lanes.Count; i++)
                {
                    CalibLane l = lanes[i];
                    var it = new ListViewItem(l.Display);
                    it.SubItems.Add(l.CurrentDelayMs + " ms");
                    it.SubItems.Add("-");
                    it.SubItems.Add("-");
                    it.SubItems.Add("待测");
                    lv.Items.Add(it);
                }
            }
            finally { lv.EndUpdate(); }
        }

        void UpdateRow(int i)
        {
            if (i < 0 || i >= lv.Items.Count || i >= lanes.Count) return;
            CalibLane l = lanes[i];
            ListViewItem it = lv.Items[i];
            it.SubItems[1].Text = l.CurrentDelayMs + " ms";
            it.SubItems[2].Text = double.IsNaN(l.MeasuredMs) ? "-" : l.MeasuredMs.ToString("0.0") + " ms";
            it.SubItems[3].Text = l.HasSuggestion ? (l.SuggestedMs + " ms") : "-";

            if (applied && l.HasSuggestion) it.SubItems[4].Text = "已应用";
            else if (double.IsNaN(l.MeasuredMs)) it.SubItems[4].Text = "待测";
            else if (l.SpreadMs > 18) it.SubItems[4].Text = "不稳 (极差 " + l.SpreadMs.ToString("0") + "ms)";
            else it.SubItems[4].Text = "r=" + l.Confidence.ToString("0.00");
        }

        void RefreshState()
        {
            if (measuring) return;
            if (mics.Count == 0) return;
            int valid = 0;
            for (int i = 0; i < lanes.Count; i++) if (!double.IsNaN(lanes[i].MeasuredMs)) valid++;
            if (valid == lanes.Count && lanes.Count > 0)
            {
                int changes = 0;
                for (int i = 0; i < lanes.Count; i++)
                    if (lanes[i].HasSuggestion && lanes[i].SuggestedMs != lanes[i].CurrentDelayMs) changes++;
                lblState.Text = applied
                    ? (hostMuted
                        ? "已写入卡片，新的延迟已生效（分流无需重启）。"
                        : "已写入卡片。点「关闭」后即可开始分流。")
                    : (changes == 0 ? "各路的到达时间已经一致，无需改动。" :
                       (hostMuted
                        ? "已算出建议值，点「应用建议值」写入卡片（分流运行中，立即生效）。"
                        : "已算出建议值，点「应用建议值」写入卡片（改的是卡片上的延迟，停止状态下才会生效）。"));
                btnApply.Enabled = !applied && changes > 0;
            }
            else
            {
                // 每路 = 2 段预热 + Takes 段测量, 每段 1 秒, 再加开流/分析开销
                lblState.Text = "点「开始校准」：会逐路播放测试音（每路预热 2 秒 + 测 " + Takes + " 次），约需 " +
                    (int)Math.Ceiling(lanes.Count * 5.5) + " 秒。";
                btnApply.Enabled = false;
            }
            btnStart.Enabled = !measuring;
        }

        void Append(string line)
        {
            if (txtLog.IsDisposed) return;
            txtLog.AppendText(line + Environment.NewLine);
        }

        void Post(Action a)
        {
            if (a == null || IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // ==================== 测量流程 ====================

        void StartMeasure()
        {
            if (measuring) return;
            int idx = cmbMic.SelectedIndex;
            if (idx < 0 || idx >= mics.Count) { Append("[跳过] 请先选择一个麦克风。"); return; }

            for (int i = 0; i < lanes.Count; i++)
            {
                lanes[i].MeasuredMs = double.NaN;
                lanes[i].HasSuggestion = false;
                lanes[i].SuggestedMs = lanes[i].CurrentDelayMs;
                lanes[i].Confidence = 0;
                lanes[i].SpreadMs = 0;
                lanes[i].Samples.Clear();
            }
            applied = false;
            FillLanes();

            measuring = true;
            cancel = false;
            btnStart.Enabled = false;
            btnApply.Enabled = false;
            btnClose.Enabled = false;
            cmbMic.Enabled = false;
            btnRescan.Enabled = false;
            txtLog.Clear();

            string micId = mics[idx].Id;
            string micName = mics[idx].Name;
            worker = new Thread(delegate () { Worker(micId, micName); });
            worker.IsBackground = true;
            worker.Name = "LatencyCalibration";
            worker.Start();
        }

        void Worker(string micId, string micName)
        {
            MicLatencyProbe probe = null;
            try
            {
                Post(delegate { lblState.Text = "正在打开麦克风…"; Append("麦克风: " + micName); });
                probe = new MicLatencyProbe();
                probe.OutLatencyMs = outLatencyMs;
                probe.Start(micId);
                Post(delegate { Append("采集采样率 " + probe.CaptureRate + " Hz；按 " + outLatencyMs + "ms 缓冲测量 (" + Takes + " 段/路, 预热 2 段)。"); });

                for (int i = 0; i < lanes.Count; i++)
                {
                    if (cancel) throw new InvalidOperationException("已取消");
                    CalibLane lane = lanes[i];
                    int row = i;
                    Post(delegate
                    {
                        lblState.Text = "测量中… " + lane.Display + " (预热 2s + 测 " + Takes + " 次)";
                        if (row < lv.Items.Count) lv.Items[row].SubItems[4].Text = "测量中…";
                    });

                    try
                    {
                        LaneMeasurement m = probe.MeasureLane(lane.DeviceId, Takes);
                        lane.MeasuredMs = m.LatencyMs;
                        lane.Confidence = m.Confidence;
                        lane.SpreadMs = m.SpreadMs;
                        lane.Samples.Clear();
                        lane.Samples.AddRange(m.Samples);
                        Post(delegate
                        {
                            UpdateRow(row);
                            Append(string.Format("  {0}: {1:0.0} ms  (r={2:0.00}, 极差 {3:0.0}ms, {4} 段)",
                                lane.Display, m.LatencyMs, m.Confidence, m.SpreadMs, m.Takes));
                            Append("    各段: " + JoinMs(m.Samples));
                        });
                    }
                    catch (Exception ex)
                    {
                        Post(delegate { Append("  " + lane.Display + ": 未取到 — " + ex.Message); });
                    }
                }

                ComputeSuggestions();

                Post(delegate
                {
                    for (int i = 0; i < lanes.Count; i++) UpdateRow(i);
                    ReportSuggestions();
                    RefreshState();
                });
            }
            catch (Exception ex)
            {
                string msg = ex == null ? "(未知错误)" : ex.Message;
                // 运行中校准最常见的失败原因: 该系统的驱动不允许同一端点被两个共享流同时打开
                string hint = hostMuted
                    ? " — 若提示「打不开输出设备/被占用」，说明该设备不允许与分流同时打开，请停止分流后重测"
                    : "";
                Post(delegate { Append("[失败] " + msg + hint); lblState.Text = "校准失败: " + msg; });
            }
            finally
            {
                try { if (probe != null) probe.Dispose(); } catch { }
                Post(delegate
                {
                    measuring = false;
                    btnStart.Enabled = true;
                    btnClose.Enabled = true;
                    cmbMic.Enabled = true;
                    btnRescan.Enabled = true;
                    if (!applied) RefreshState();
                });
            }
        }

        /// <summary>把各段读数拼成一行, 便于一眼看出是整体偏还是单段离群。</summary>
        static string JoinMs(IList<double> values)
        {
            if (values == null || values.Count == 0) return "-";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) sb.Append(" / ");
                sb.Append(values[i].ToString("0.0"));
            }
            return sb.ToString() + " ms";
        }

        /// <summary>
        /// 算出建议延迟: **以实测延迟最大的那一路为基准, 它的延迟设 0**, 其余各路补到与它对齐
        /// (建议值 = 最大值 − 本路实测)。
        /// <para>为什么基准归零而不是"统一抬到某个总量": 补延迟只会增加延迟, 不会减少。
        /// 把基准抬起来等于给所有路白白叠加一层的等待时间; 让最慢的那一路保持 0,
        /// 总延迟就是"最慢那台的延迟", 这是这套方案能达到的下限。</para>
        /// <para>基准由**实测**决定, 与卡片上原有的手动值无关 —— 原有延迟会被覆盖 (日志里会写明)。</para>
        /// </summary>
        void ComputeSuggestions()
        {
            double maxMeasured = double.MinValue;
            int valid = 0;
            for (int i = 0; i < lanes.Count; i++)
            {
                CalibLane l = lanes[i];
                if (double.IsNaN(l.MeasuredMs)) continue;
                valid++;
                if (l.MeasuredMs > maxMeasured) maxMeasured = l.MeasuredMs;
            }
            if (valid < 2) return;

            const int MaxDelay = 2000;   // 与卡片延迟框的上限一致
            for (int i = 0; i < lanes.Count; i++)
            {
                CalibLane l = lanes[i];
                if (double.IsNaN(l.MeasuredMs)) continue;
                int ms = (int)Math.Round(maxMeasured - l.MeasuredMs);
                if (ms < 0) ms = 0;      // 浮点抖动 (基准路自己)
                if (ms > MaxDelay) ms = MaxDelay;
                l.SuggestedMs = ms;
                l.HasSuggestion = true;
            }
        }

        void ReportSuggestions()
        {
            int changes = 0;
            for (int i = 0; i < lanes.Count; i++)
            {
                CalibLane l = lanes[i];
                if (!l.HasSuggestion) continue;
                int diff = l.SuggestedMs - l.CurrentDelayMs;
                if (diff != 0) changes++;
                Append(string.Format("  {0}: 当前 {1} ms → 建议 {2} ms ({3}{4})",
                    l.Display, l.CurrentDelayMs, l.SuggestedMs,
                    diff >= 0 ? "+" : "", diff));
            }
            if (changes == 0) Append("各路到达时间本来就一致，不需要补延迟。");
            else Append("提示: 最慢的一路保持在 0ms (基准), 其余按实测差值补齐; 数字来自实测, 建议应用后再试听; 若仍有声像偏移, 可在卡片上微调。");
        }

        void ApplySuggestions()
        {
            if (applyDelay == null) return;
            int n = 0;
            for (int i = 0; i < lanes.Count; i++)
            {
                CalibLane l = lanes[i];
                if (!l.HasSuggestion) continue;
                if (l.SuggestedMs == l.CurrentDelayMs) continue;
                try
                {
                    applyDelay(l.DeviceId, l.SuggestedMs);
                    l.CurrentDelayMs = l.SuggestedMs;
                    n++;
                }
                catch (Exception ex)
                {
                    Append("[写入失败] " + l.Display + ": " + ex.Message);
                }
            }
            // 只要点过「应用」就标记为已应用: 哪怕算出来的建议刚好等于当前值,
            // 用户也已经确认了这份结果, 不该再显示"尚未写入"。
            bool any = false;
            for (int i = 0; i < lanes.Count; i++) if (lanes[i].HasSuggestion) any = true;
            applied = any;
            for (int i = 0; i < lanes.Count; i++) UpdateRow(i);
            Append(n == 0 ? "建议值与当前一致, 无需改动。" : "已写入 " + n + " 路的延迟值。");
            RefreshState();
        }
    }
}
