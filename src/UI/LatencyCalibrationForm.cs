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
    /// <para><b>测量期间要求分流已停止</b>: 否则分流器自己的音频会和测试音混在一起,
    /// 相关系数被拉低, 结果不可信。这一点由调用方在打开窗口前校验。</para>
    /// </summary>
    sealed class LatencyCalibrationForm : Form
    {
        /// <summary>
        /// 每路测几次。注意是"轮转"而不是"一路测完再测下一路": 按 轮次 × 路 交替进行,
        /// 这样两路的测量条件 (房间噪声、系统调度、设备温度) 尽量相同, 差值才可信。
        /// </summary>
        const int Takes = 3;

        readonly IList<CalibLane> lanes;
        readonly Action<string, int> applyDelay;   // (deviceId, delayMs) → 写回卡片并保存

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

        public LatencyCalibrationForm(IList<CalibLane> lanes, Action<string, int> applyDelay)
        {
            if (lanes == null) throw new ArgumentNullException("lanes");
            this.lanes = lanes;
            this.applyDelay = applyDelay;

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
                "程序会逐路播放一段 60ms 扫频，用麦克风测出每一路「声音到达耳朵」的时间差，\n" +
                "再把差值补到较快的那一路。分流器需处于「已停止」状态。";
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
            else if (l.SpreadMs > 25) it.SubItems[4].Text = "不稳 (极差 " + l.SpreadMs.ToString("0") + "ms)";
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
                    ? "已写入卡片。点「关闭」后即可开始分流。"
                    : (changes == 0 ? "各路的到达时间已经一致，无需改动。" :
                       "已算出建议值，点「应用建议值」写入卡片（改的是卡片上的延迟，停止状态下才会生效）。");
                btnApply.Enabled = !applied && changes > 0;
            }
            else
            {
                lblState.Text = "点「开始校准」：会轮转向每一路播放测试音，约需 " +
                    (int)Math.Ceiling(lanes.Count * Takes * 1.4) + " 秒。";
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
                probe.Start(micId);
                Post(delegate { Append("采集采样率 " + probe.CaptureRate + " Hz。开始轮转测量 (每路 " + Takes + " 次)。"); });

                // 轮转: 第 1 轮测完每一路, 再测第 2 轮 …… 让两路的测量条件尽量一致
                for (int k = 0; k < Takes; k++)
                {
                    for (int i = 0; i < lanes.Count; i++)
                    {
                        if (cancel) throw new InvalidOperationException("已取消");
                        CalibLane lane = lanes[i];
                        int row = i;
                        int round = k + 1;
                        Post(delegate
                        {
                            lblState.Text = "测量中… 第 " + round + "/" + Takes + " 轮 · " + lane.Display;
                            if (row < lv.Items.Count) lv.Items[row].SubItems[4].Text = "测量中…";
                        });

                        try
                        {
                            LaneMeasurement m = probe.MeasureLane(lane.DeviceId, 1);
                            lane.Samples.Add(m.LatencyMs);
                            if (m.Confidence > lane.Confidence) lane.Confidence = m.Confidence;
                            RecomputeLane(lane);
                            Post(delegate
                            {
                                UpdateRow(row);
                                Append(string.Format("  {0}: {1:0.0} ms  (r={2:0.00})",
                                    lane.Display, m.LatencyMs, m.Confidence));
                            });
                        }
                        catch (Exception ex)
                        {
                            // 单次失败不中断: 设备刚从空闲唤醒时首包经常落在分析窗之外
                            Post(delegate { Append("  " + lane.Display + ": 第 " + round + " 次未取到 — " + ex.Message); });
                        }
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
                Post(delegate { Append("[失败] " + msg); lblState.Text = "校准失败: " + msg; });
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

        /// <summary>一路的多次样本 → 中位数 + 极差。中位数比"取最可信的那次"稳: 它不受单次离群值影响。</summary>
        static void RecomputeLane(CalibLane lane)
        {
            if (lane.Samples.Count == 0) { lane.MeasuredMs = double.NaN; lane.SpreadMs = 0; return; }
            var sorted = new List<double>(lane.Samples);
            sorted.Sort();
            lane.MeasuredMs = sorted[sorted.Count / 2];
            lane.SpreadMs = sorted[sorted.Count - 1] - sorted[0];
        }

        /// <summary>
        /// 算出建议延迟: 让"设备延迟 + 卡片延迟"最大的那一路成为基准, 其余各路补到同一总量。
        /// 也就是说**已有的手动延迟会被保留** (它可能是用户按距离手调的), 只补差额。
        /// </summary>
        void ComputeSuggestions()
        {
            double maxTotal = double.MinValue;
            int valid = 0;
            for (int i = 0; i < lanes.Count; i++)
            {
                CalibLane l = lanes[i];
                if (double.IsNaN(l.MeasuredMs)) continue;
                valid++;
                double total = l.CurrentDelayMs + l.MeasuredMs;
                if (total > maxTotal) maxTotal = total;
            }
            if (valid < 2) return;

            int minSugg = int.MaxValue;
            for (int i = 0; i < lanes.Count; i++)
            {
                CalibLane l = lanes[i];
                if (double.IsNaN(l.MeasuredMs)) continue;
                l.SuggestedMs = (int)Math.Round(maxTotal - l.MeasuredMs);
                l.HasSuggestion = true;
                if (l.SuggestedMs < minSugg) minSugg = l.SuggestedMs;
            }
            // 理论上有基准路保证 ≥ 0; 数值抖动可能出 -1, 统一抬上来
            if (minSugg < 0)
            {
                for (int i = 0; i < lanes.Count; i++)
                    if (lanes[i].HasSuggestion) lanes[i].SuggestedMs -= minSugg;
            }
            const int MaxDelay = 2000;   // 与卡片延迟框的上限一致
            for (int i = 0; i < lanes.Count; i++)
                if (lanes[i].HasSuggestion && lanes[i].SuggestedMs > MaxDelay) lanes[i].SuggestedMs = MaxDelay;
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
            else Append("提示: 数字来自实测, 建议先应用再试听; 若仍有声像偏移, 可在卡片上微调。");
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
            applied = n > 0;
            for (int i = 0; i < lanes.Count; i++) UpdateRow(i);
            Append("已写入 " + n + " 路的延迟值。");
            RefreshState();
        }
    }
}
