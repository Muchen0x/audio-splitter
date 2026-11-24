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
    /// <para><b>测量期间不需要停音乐</b>: 扫频是叠在正在播放的音乐上出去的, 定位靠
    /// <see cref="ChirpMatch"/> 的全段匹配滤波 + 峰显著度 (PSR), 不靠"先猜声音从哪儿开始"。
    /// 音乐盖过扫频时**报错而不是给一个数** —— 一个偏掉的延迟会让声场比不校准更歪。
    /// 分流在跑时链路照常运行 (端点不动、缓冲不断流), 校准只是多开一个播放流。</para>
    /// </summary>
    sealed class LatencyCalibrationForm : Form
    {
        /// <summary>
        /// 窗体基准字体。R-02 的同类问题: 原先每次校准都 <c>new Font(...)</c>, 而
        /// <c>Control.Dispose</c> 不释放 Font (只释放它缓存的 HFONT), 于是每开一次校准窗
        /// 就漏一个 GDI 字体句柄。改成进程内唯一实例 —— 校准窗可能开很多次, 字体只需一份。
        /// </summary>
        static readonly Font BaseFont = new Font("Microsoft YaHei UI", 9F);

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
        /// <summary>
        /// 分流当前是否正在运行。只影响提示文案与「应用后是否立即生效」的说明 ——
        /// 测量本身不再需要上层静音, 运行与停止两种状态下走的是同一条路。
        /// </summary>
        readonly bool hostRunning;

        readonly RoundedCombo cmbMic = new RoundedCombo { Height = 24, ItemHeight = 24, MaxDropDownItems = 10 };
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
        /// <summary>测量中点了关闭: 先停止, 等线程收尾后再真正关窗 (不让声音留在半空)。</summary>
        bool closeAfterStop;

        public LatencyCalibrationForm(IList<CalibLane> lanes, int outLatencyMs, Action<string, int> applyDelay,
            bool hostRunning = false)
        {
            if (lanes == null) throw new ArgumentNullException("lanes");
            this.lanes = lanes;
            this.applyDelay = applyDelay;
            this.outLatencyMs = Math.Max(5, Math.Min(500, outLatencyMs));
            this.hostRunning = hostRunning;

            Text = "延迟校准 · 麦克风互相关";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(580, 520);
            Font = BaseFont;

            BuildUi();
            ReloadMics();
            FillLanes();
            UpdateTip();
            RefreshState();
        }

        // ==================== 界面 ====================

        void BuildUi()
        {
            var lblMic = new Label { Text = "麦克风:", Location = new Point(12, 15), AutoSize = true };

            cmbMic.Location = new Point(72, 12);
            cmbMic.Width = 400;
            // 弹层宽度跟着本体走 (RoundedCombo.DropDownWidth 默认 0 = 同宽): 比本体宽出来的那一截,
            // 视觉上就是两个错开的框。名字太长时靠行内省略号收尾。

            btnRescan.Location = new Point(480, 11);
            btnRescan.Width = 88;
            btnRescan.Height = 24;
            btnRescan.Click += (s, e) => ReloadMics();

            lblTip.Location = new Point(12, 44);
            lblTip.Size = new Size(556, 50);
            lblTip.AutoSize = false;
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
            btnStart.Click += (s, e) => ToggleMeasure();

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
            // 与主窗口同一套 DPI 放大 (控件都按 96 DPI 摆好后, 在 Load 里整体乘一次)
            Load += (s2, e2) => Dpi.ScaleForm(this);
        }

        /// <summary>提示文案: 说清"音乐不用停"以及失败时该调什么。</summary>
        void UpdateTip()
        {
            lblTip.Text = "把麦克风放在你实际听音的位置（两只音箱中间）。程序会逐路播放 120ms 测试音" +
                "（每路预热 2 秒后测 3 次），测出每一路「声音到达耳朵」的时间差，再把差值补到较快的那一路。" +
                "缓冲按当前「延迟」档位测量；测试音会叠在正在播放的音乐上，不必停音乐，" +
                "但音乐太响时会测不准（程序会直接报错，不会给一个错的值）。";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            cancel = true;       // 测量线程是后台线程, 让它自己发现取消并退出
            // 测量中直接销毁窗口会把还在响的播放流丢在半空: 转成"停止", 线程收尾后自己关。
            // 只拦用户关闭 (Alt+F4 / 关闭按钮); 系统关机等其它原因照常放行, 免得拖住系统。
            if (measuring && (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None))
            {
                closeAfterStop = true;
                e.Cancel = true;
                StopMeasure();
                return;
            }
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
            // 极差门槛 10ms: 音乐环境下本来就比纯扫频环境抖, 若放到 18ms,
            // "三段各偏一点、中位数看着正常"的情况会被当成好数据收下。
            else if (l.SpreadMs > 10.0) it.SubItems[4].Text = "不稳 (极差 " + l.SpreadMs.ToString("0") + "ms)";
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
                    ? (hostRunning
                        ? "已写入卡片，新的延迟已生效（分流无需重启）。"
                        : "已写入卡片。点「关闭」后即可开始分流。")
                    : (changes == 0 ? "各路的到达时间已经一致，无需改动。" :
                       (hostRunning
                        ? "已算出建议值，点「应用建议值」写入卡片（分流运行中，立即生效）。"
                        : "已算出建议值，点「应用建议值」写入卡片（改的是卡片上的延迟，停止状态下才会生效）。"));
                btnApply.Enabled = !applied && changes > 0;
            }
            else
            {
                // 每路 = 2 段预热 + Takes 段测量, 每段 1 秒, 再加开流/分析开销
                lblState.Text = "点「开始校准」逐路播放测试音（约 " +
                    (int)Math.Ceiling(lanes.Count * 5.5) + " 秒），过程中可点「停止校准」中止。";
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

        /// <summary>同一个按钮两副面孔: 空闲时开始, 测量中变成停止。宽度按"停止校准"排的, 不跳。</summary>
        void ToggleMeasure()
        {
            if (measuring) StopMeasure();
            else StartMeasure();
        }

        /// <summary>
        /// 请求停止。真正的收尾 (按钮复位、状态文案) 在线程的 finally 里做 ——
        /// 这里只负责让线程**尽快**发现, 并立刻给出"正在停止"的反馈, 免得用户以为点了没反应。
        /// </summary>
        void StopMeasure()
        {
            if (!measuring) return;
            cancel = true;
            btnStart.Text = "正在停止…";
            btnStart.Enabled = false;
            lblState.Text = "正在停止…（当前这段放完就停）";
        }

        /// <summary>测量期间锁住输入: 换麦克风/刷新/应用都会和采集线程打架。</summary>
        void SetMeasuringUi(bool on)
        {
            btnStart.Text = on ? "停止校准" : "开始校准";
            btnStart.Enabled = on || mics.Count > 0;
            btnClose.Enabled = !on;
            cmbMic.Enabled = !on;
            btnRescan.Enabled = !on;
            if (on) btnApply.Enabled = false;
        }

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
            closeAfterStop = false;
            SetMeasuringUi(true);
            txtLog.Clear();
            lblState.Text = "正在打开麦克风…";

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
            // 已有结果的路数: 中途停止时用它告诉用户"停在哪", 也是"这半套数据不能用"的依据。
            int measured = 0;
            try
            {
                Post(delegate { lblState.Text = "正在打开麦克风…"; Append("麦克风: " + micName); });
                probe = new MicLatencyProbe();
                probe.OutLatencyMs = outLatencyMs;
                probe.ShouldCancel = delegate { return cancel; };
                probe.Start(micId);
                Post(delegate
                {
                    Append("采集采样率 " + probe.CaptureRate + " Hz；按 " + outLatencyMs + "ms 缓冲测量 (" +
                        Takes + " 段/路, 预热 2 段)。");
                    Append("  扫频 " + (MicLatencyProbe.ChirpSeconds * 1000).ToString("0") +
                        "ms, 可测上限 " + MicLatencyProbe.MaxMeasurableMs.ToString("0") + "ms；" +
                        "测试音叠在正在播放的音乐上。");
                });
                if (cancel) return;

                for (int i = 0; i < lanes.Count; i++)
                {
                    if (cancel) return;
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
                        // 停止可能恰好落在测量返回之后: 这一路的结果已经不可信 (分段可能没测全),
                        // 丢弃比留下更安全 —— 半路的数据会被算进建议值, 补出来的延迟是歪的。
                        if (cancel) return;
                        lane.MeasuredMs = m.LatencyMs;
                        lane.Confidence = m.Confidence;
                        lane.SpreadMs = m.SpreadMs;
                        lane.Samples.Clear();
                        lane.Samples.AddRange(m.Samples);
                        measured++;
                        Post(delegate
                        {
                            UpdateRow(row);
                            Append(string.Format("  {0}: {1:0.0} ms  (r={2:0.00}, PSR {3:0.0}, 极差 {4:0.0}ms, {5} 段)",
                                lane.Display, m.LatencyMs, m.Confidence,
                                m.Psr, m.SpreadMs, m.Takes));
                            Append("    各段: " + JoinMs(m.Samples));
                        });
                    }
                    catch (Exception ex)
                    {
                        if (cancel) return;
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
                if (cancel) return;      // 停止引起的异常不进"失败"分支, 由 finally 统一收尾
                string msg = ex == null ? "(未知错误)" : ex.Message;
                // 分流在跑时最常见的失败原因: 该系统的驱动不允许同一端点被两个共享流同时打开
                string hint = hostRunning
                    ? " — 若提示「打不开输出设备/被占用」，说明该设备不允许与分流同时打开，请停止分流后重测"
                    : "";
                Post(delegate { Append("[失败] " + msg + hint); lblState.Text = "校准失败: " + msg; });
            }
            finally
            {
                try { if (probe != null) probe.Dispose(); } catch { }
                bool stopped = cancel;
                int done = measured;
                Post(delegate
                {
                    measuring = false;
                    SetMeasuringUi(false);
                    if (stopped)
                    {
                        // 已测到的行保留 (方便判断是不是某台设备有问题), 但不出建议值:
                        // 只测了一部分就补延迟, 结果是按"残缺的基准"算的。
                        btnApply.Enabled = false;
                        lblState.Text = done == 0
                            ? "已停止校准，未测到有效数据。"
                            : "已停止校准（测到 " + done + "/" + lanes.Count + " 路），未应用任何改动。";
                        Append(done == 0
                            ? "[已停止] 校准被手动停止。"
                            : "[已停止] 校准被手动停止，已测到的结果保留在表里，但不生成建议值 —— 需要完整测完才能算差值。");
                    }
                    else if (!applied) RefreshState();

                    // 测量中点了关闭: 停稳了才真正关窗 (此时播放流已释放, 不会有残响)
                    if (closeAfterStop)
                    {
                        closeAfterStop = false;
                        Close();
                    }
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
            Append("注意: 本次测量时音乐可能在响, 扫频是叠在音乐上的。若应用后声像仍有偏移, " +
                "把音乐音量调小后重测一次做对照 —— 差异明显就说明这一次被音乐干扰了。");
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
