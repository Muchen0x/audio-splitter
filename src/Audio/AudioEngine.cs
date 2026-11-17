using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using StereoSplitter.Devices;

namespace StereoSplitter
{
    /// <summary>
    /// 音频引擎 (P1 起为 N 路): 一路 loopback 捕获 → 每路一条 延迟线/缓冲/校正/WasapiOut。
    ///
    /// 公开接口保持与两路时代完全一致 (Start 旧签名、Left*/Right* 属性、BufferStatus 文本),
    /// 这样 UI 不用跟着改 —— P1 的验收标准就是"旧 ini 启动, 两路听感与日志与 HEAD 一致"。
    /// 多路由 RoutingModel 描述, 由它编译出每路的混音矩阵。
    /// </summary>
    class AudioEngine : IDisposable
    {
        WasapiLoopbackCapture loopback;
        MMDevice captureDev;
        // 设备 ID: MMDevice 在 Start 所在线程(后台)自建, 不跨线程传递 (NAudio #214)
        readonly string leftDevId, rightDevId;
        readonly int driftMode, fixedPpm;
        Action<string> log;
        volatile int sampleRate;
        volatile int captureChannels;   // 运行中重编译矩阵要用

        /// <summary>捕获源的实际声道数 (启动后才有值; 0 = 还没启动)。UI 建模板时优先用它,
        /// 拿不到就查端点格式 —— 模板必须知道源有几个声道, 否则真 5.1 源会被当成立体声上混。</summary>
        public int CaptureChannels { get { return captureChannels; } }

        readonly List<SinkSpec> specs = new List<SinkSpec>();
        readonly List<SinkRuntime> sinks = new List<SinkRuntime>();
        RoutingModel routing;

        bool splitErrLogged = false;
        volatile bool silentStartup = false;
        int firstDataLogged = 0;
        AutoResetEvent dataReady;

        // 捕获端固定开销估算 (WASAPI loopback 自身缓冲), 只用于界面选项与启动日志里的"总预估"
        internal const int CaptureLatencyEstimateMs = 10;

        // 时钟环稳态目标 (ms): 档位越小目标越低, 上限 100ms。
        // 余量按"捕获端交货间隙一次消耗 ~35ms (USB/HDMI 大块粒度实测) + 输出周期"校准,
        // 各档稳态剩余余量 ≥10ms, 避免瞬时水位探底穿零产生欠载:
        //   标准 100/低延迟 80/极低 65/超低 60/极限 55 → 剩余 15/15/10/15/15ms。
        public static int DriftTargetMs(int preMs, int outMs)
        {
            int margin = (preMs >= 100 || outMs >= 50) ? 25 :
                         (preMs >= 50 || outMs >= 30) ? 30 :
                         (preMs >= 25 || outMs >= 20) ? 40 :
                         (preMs >= 10 || outMs >= 10) ? 45 : 50;
            return Math.Min(100, Math.Max(preMs, outMs) + margin);
        }

        // 界面/日志用的"总预估延迟" = 捕获 + 内部缓冲目标 + 输出缓冲, 不含 USB/DAC/声波
        public static int EstimatedTotalMs(int preMs, int outMs)
        {
            return CaptureLatencyEstimateMs + DriftTargetMs(preMs, outMs) + outMs;
        }

        // ---- 告警去重: 多路 provider 各自限流, 同一现象必然打多条 ----
        readonly object logGate = new object();
        readonly Dictionary<string, int> logCategoryTick = new Dictionary<string, int>();

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
        /// 开头的路标记必须跳过 —— 路标签不同, 计入类别键会让去重完全失效。</summary>
        static string CategoryKey(string msg)
        {
            int start = 0;
            if (msg.Length > 3 && msg[0] == '[')
            {
                int close = msg.IndexOf(']');
                if (close > 0 && close < 12) start = close + 1;
            }
            int n = start;
            while (n < msg.Length && n < start + 40)
            {
                char c = msg[n];
                if (c == ':' || c == '(' || c == '+' || c == '-' || (c >= '0' && c <= '9')) break;
                n++;
            }
            return msg.Substring(start, n - start).TrimEnd();
        }

        // 长期运行看门狗: 输出/捕获线程会因设备休眠、被独占、拔插而"悄悄停止"
        volatile bool stopping;
        bool disposed;
        int stallNotified;
        int lastCaptureTick;
        /// <summary>链路意外中断 (参数为人话原因)。在音频线程触发, 订阅方须自行回 UI 线程。</summary>
        public event Action<AudioEngine, string> Stalled;

        // 起播余量: 覆盖输出端起播时对缓冲的预拉取。
        const int PrePullGuardMs = 80;

        public AudioEngine(string leftDevId, string rightDevId, Action<string> log, int driftMode, int fixedPpm)
        {
            this.leftDevId = leftDevId;
            this.rightDevId = rightDevId;
            this.log = log;
            this.driftMode = driftMode;
            this.fixedPpm = fixedPpm;
        }

        /// <summary>N 路入口: 设备全部写在 RoutingModel 里, 不再需要左右 ID (两路兼容入口仍可传)。</summary>
        public AudioEngine(Action<string> log, int driftMode, int fixedPpm)
            : this(null, null, log, driftMode, fixedPpm)
        {
        }

        public string CaptureId
        {
            get
            {
                try { return captureDev != null ? captureDev.ID : null; } catch { return null; }
            }
        }

        public int LastCaptureTick { get { return lastCaptureTick; } }

        /// <summary>当前路数 (P1 为 2, P3 起可变)。</summary>
        public int SinkCount { get { return sinks.Count; } }

        /// <summary>按路取运行时 (越界返回 null), 供将来的 UI 列表使用。</summary>
        public SinkRuntime SinkAt(int index)
        {
            return index >= 0 && index < sinks.Count ? sinks[index] : null;
        }

        DriftCorrectingProvider ProviderAt(int i)
        {
            return i >= 0 && i < sinks.Count ? sinks[i].Provider : null;
        }

        public long LeftRenderedSamples { get { var p = ProviderAt(0); return p == null ? 0 : p.RenderedSamples; } }
        public long RightRenderedSamples { get { var p = ProviderAt(1); return p == null ? 0 : p.RenderedSamples; } }
        public int LeftBufferMs { get { var p = ProviderAt(0); return p == null ? 0 : p.BufferedMs; } }
        public int RightBufferMs { get { var p = ProviderAt(1); return p == null ? 0 : p.BufferedMs; } }
        public int LeftRegulatedMs { get { var p = ProviderAt(0); return p == null ? 0 : p.RegulatedMs; } }
        public int RightRegulatedMs { get { var p = ProviderAt(1); return p == null ? 0 : p.RegulatedMs; } }

        /// <summary>共享校正比例 (ppm)。独立组时返回第一路的独立 ratio。</summary>
        public int SharedRatioPpm
        {
            get
            {
                var p = ProviderAt(0);
                if (p != null) return (int)p.RatioPpm;
                return 0;
            }
        }

        /// <summary>启动结果: 预缓冲是否真的拿到了数据 (旧实现超时也当成功, 导致空缓冲起播)。</summary>
        public enum StartOutcome
        {
            Ready,
            Insufficient,
            NoAudioYet
        }

        /// <summary>两路兼容入口 (UI 现有调用): 等价于 split-lr 模式 + 两路同组。</summary>
        public StartOutcome Start(float gain, float master, int prebufferMs, int outLatencyMs, int leftDelayMs, int rightDelayMs,
            Func<bool> aborted, string expectedCaptureId)
        {
            routing = RoutingModel.SplitLr("s0", leftDevId, "左", leftDelayMs,
                                           "s1", rightDevId, "右", rightDelayMs);
            return StartCore(routing, gain, master, prebufferMs, outLatencyMs, aborted, expectedCaptureId);
        }

        /// <summary>N 路入口 (P3 起由 UI 列表驱动)。</summary>
        public StartOutcome Start(RoutingModel model, float gain, float master, int prebufferMs, int outLatencyMs,
            Func<bool> aborted, string expectedCaptureId)
        {
            routing = model;
            return StartCore(model, gain, master, prebufferMs, outLatencyMs, aborted, expectedCaptureId);
        }

        StartOutcome StartCore(RoutingModel model, float gain, float master, int prebufferMs, int outLatencyMs,
            Func<bool> aborted, string expectedCaptureId)
        {
            specs.Clear();
            for (int i = 0; i < model.SinkSpecs.Count; i++)
                if (model.SinkSpecs[i].Enabled) specs.Add(model.SinkSpecs[i]);
            if (specs.Count == 0)
                throw new AudioDeviceException("没有启用任何输出设备，请至少启用一路");

            var enumerator = new MMDeviceEnumerator();
            var devs = new List<MMDevice>();
            try
            {
                // 启动前校验: 设备可能在这几秒内被拔出, 用陈旧 ID 建 WasapiOut 只会抛 "尚未实现" 这类鬼话
                string missing = FindMissingSink(enumerator, specs);
                if (missing != null)
                    throw new AudioDeviceException(missing + "不可用（可能已拔出或被禁用），请刷新设备后重试");

                for (int i = 0; i < specs.Count; i++) devs.Add(enumerator.GetDevice(specs[i].DeviceId));
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
            captureChannels = wf.Channels;
            log("[启动] 格式: " + wf.SampleRate + "Hz / " + wf.Encoding + " / " + wf.BitsPerSample + "bit / " + wf.Channels + "ch");

            // 端点传输类型: 蓝牙 A2DP 自成一条链路, 偶发"咔哒"多出在链路层而不是缓冲层
            var transports = new string[devs.Count];
            bool anyBt = false;
            for (int i = 0; i < devs.Count; i++)
            {
                string t = DeviceCatalog.TransportName(devs[i]);
                transports[i] = t;
                if (DeviceCatalog.IsBluetooth(t)) anyBt = true;
            }
            log("[启动] 端点传输: " + JoinPerSink(specs, i => DeviceCatalog.TransportText(transports[i])));

            // 跨采样率/位深交给系统 (NAudio 的 AUTOCONVERTPCM), 声道数不得大于端点声道数
            int driftTargetMs = DriftTargetMs(prebufferMs, outLatencyMs);
            int driftTargetSamples = (int)((long)wf.SampleRate * driftTargetMs / 1000);
            int fillTargetMs = driftTargetMs + PrePullGuardMs;
            int fillTargetSamples = (int)((long)wf.SampleRate * fillTargetMs / 1000);

            // 时钟组: 同一 ClockGroupId 的路共享 ratio; 空 ID = 每路独立组
            var groups = new Dictionary<string, ClockGroup>();
            var memberGroup = new ClockGroup[specs.Count];
            var memberId = new int[specs.Count];
            int maxGroupSize = 1;
            for (int i = 0; i < specs.Count; i++)
            {
                string gid = specs[i].ClockGroupId ?? "";
                ClockGroup g;
                if (gid.Length == 0)
                {
                    g = new ClockGroup(driftTargetSamples, wf.SampleRate, LogThrottled);
                    g.MemberCount = 1;
                    memberId[i] = 0;
                    memberGroup[i] = g;
                }
                else
                {
                    if (!groups.TryGetValue(gid, out g))
                    {
                        // 新建组: 成员数从 0 起, 否则第一个成员的编号会错位 (0,2,4…)
                        g = new ClockGroup(driftTargetSamples, wf.SampleRate, LogThrottled);
                        g.MemberCount = 0;
                        groups[gid] = g;
                    }
                    memberId[i] = g.MemberCount;
                    g.MemberCount = g.MemberCount + 1;
                    if (g.MemberCount > maxGroupSize) maxGroupSize = g.MemberCount;
                    memberGroup[i] = g;
                }
            }
            var compiled = model.Compile(specs, wf.Channels);

            sinks.Clear();
            for (int i = 0; i < specs.Count; i++)
            {
                // 独立组也要把组传下去: 传 null 会让该路永远不做漂移校正 (ratio 恒 1.0),
                // 多声道规格下设备间时钟差会无限累积成左右偏移。
                ClockGroup g = memberGroup[i];
                var sink = new SinkRuntime(specs[i], devs[i], wf, driftTargetSamples, fillTargetSamples,
                    outLatencyMs, driftMode, fixedPpm, memberId[i], g, LogThrottled);
                sink.Matrix = compiled.SinkMatrix[i];
                sink.SetGain(gain);
                sink.SetMaster(master);
                int idx = i;
                sink.Output.PlaybackStopped += delegate(object s, StoppedEventArgs e) { OnOutputStopped(specs[idx].Label, e); };
                sinks.Add(sink);
            }
            if (anyBt && outLatencyMs < 20)
                log("[提示] 蓝牙端点使用 " + outLatencyMs + "ms 输出缓冲偏紧；若链路上偶发\"咔哒\"，把「延迟」档位调到 25ms/20ms 或更高");
            for (int i = 0; i < sinks.Count; i++)
                log("[启动] 路由 " + specs[i].Label + " " + specs[i].OutChannels + "ch: " + RoutingModel.Describe(compiled.SinkMatrix[i]));

            loopback.RecordingStopped += delegate(object s, StoppedEventArgs e) { OnCaptureStopped(e); };

            dataReady = new AutoResetEvent(false);
            loopback.DataAvailable += (s, e) =>
            {
                try
                {
                    // 同一条原始字节送进每路的延迟线; 延迟线之后各自按矩阵混音
                    for (int i = 0; i < sinks.Count; i++) sinks[i].Line.Write(e.Buffer, e.BytesRecorded);
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
                try
                {
                    var ev = dataReady;
                    if (ev != null) ev.Set();
                }
                catch (ObjectDisposedException) { }
                catch { }
            };

            loopback.StartRecording();

            int targetMs = driftTargetSamples * 1000 / wf.SampleRate;
            // 等待上限随填充目标与延迟补偿动态放宽 (延迟 2000ms 时固定 2s 会超时)
            int maxDelay = 0;
            for (int i = 0; i < specs.Count; i++) if (specs[i].DelayMs > maxDelay) maxDelay = specs[i].DelayMs;
            int maxWaitMs = fillTargetMs + maxDelay + 1000;
            int waitStart = Environment.TickCount;
            while (!AllReachedFill())
            {
                if (aborted != null && aborted()) throw new OperationCanceledException("启动已取消");
                if (Environment.TickCount - waitStart >= maxWaitMs) break;
                var ev = dataReady;
                if (ev != null) ev.WaitOne(20); else Thread.Sleep(20);
            }

            // 先取水位再修剪: 修剪会把快路压回目标值, 之后再判断就永远"达标"
            var got = new int[sinks.Count];
            for (int i = 0; i < sinks.Count; i++) got[i] = sinks[i].BufferedBytes;
            for (int i = 0; i < sinks.Count; i++) sinks[i].TrimToFill();

            bool allFill = true, anyEmpty = false;
            for (int i = 0; i < sinks.Count; i++)
            {
                if (got[i] < sinks[i].FillBytes) allFill = false;
                if (got[i] == 0) anyEmpty = true;
            }

            StartOutcome outcome;
            if (allFill)
            {
                outcome = StartOutcome.Ready;
                log("[启动] 预缓冲完成 (" + JoinPairs(specs, i => got[i].ToString()) + " 字节, 填充目标 " + fillTargetMs +
                    "ms / 时钟目标 " + targetMs + "ms / 档位 " + prebufferMs + "ms / 输出 " + outLatencyMs +
                    "ms / 总预估≈" + (targetMs + outLatencyMs + CaptureLatencyEstimateMs) +
                    "ms [捕获" + CaptureLatencyEstimateMs + "+内部" + targetMs + "+输出" + outLatencyMs + ", 不含USB/DAC])");
            }
            else if (anyEmpty)
            {
                outcome = StartOutcome.NoAudioYet;
                log("[警告] 捕获源 " + captureName + " 在 " + (maxWaitMs / 1000.0).ToString("0.0") +
                    " 秒内没有音频数据 (" + JoinPairs(specs, i => got[i].ToString()) + " 字节)，已按静音启动，播放声音后会自动补齐");
            }
            else
            {
                outcome = StartOutcome.Insufficient;
                log("[警告] 预缓冲不足 (" + JoinPairs(specs, i => got[i].ToString()) + " / 目标 " +
                    (sinks.Count > 0 ? sinks[0].FillBytes.ToString() : "0") + " 字节)，开头可能出现欠载");
            }

            silentStartup = (outcome == StartOutcome.NoAudioYet);
            log("[启动] 时钟漂移补偿: " + (driftMode == 0 ? "关闭" : driftMode == 2 ? "固定 " + fixedPpm + "ppm" : "自适应") +
                ", 比例上限 ±0.50% / 时钟组 " + (maxGroupSize > 1 ? maxGroupSize + " 路共享" : "每路独立"));
            if (maxDelay > 0)
                log("[启动] 延迟补偿: " + JoinPairs(specs, i => specs[i].DelayMs + "ms"));

            if (aborted != null && aborted()) throw new OperationCanceledException("启动已取消");

            for (int i = 0; i < sinks.Count; i++) sinks[i].Start();
            for (int i = 0; i < sinks.Count; i++)
                log("[启动] 输出 " + specs[i].Label + "=" + DeviceCatalog.SafeName(sinks[i].Device));
            return outcome;
        }

        bool AllReachedFill()
        {
            if (sinks.Count == 0) return true;
            for (int i = 0; i < sinks.Count; i++) if (!sinks[i].ReachedFill) return false;
            return true;
        }

        /// <summary>"左"→"L"、"右"→"R", 其余用 Label; 让两路日志与旧版逐字可比。</summary>
        static string ShortTag(string label)
        {
            if (label == "左") return "L";
            if (label == "右") return "R";
            return label;
        }

        static string JoinPairs(IList<SinkSpec> list, Func<int, string> f)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(ShortTag(list[i].Label)).Append('=').Append(f(i));
            }
            return sb.ToString();
        }

        static string JoinPerSink(IList<SinkSpec> list, Func<int, string> f)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(" / ");
                sb.Append(f(i));
            }
            return sb.ToString();
        }

        /// <summary>一次枚举同时校验全部输出设备是否仍在活动输出列表中; 返回缺失路名, 都正常返回 null</summary>
        static string FindMissingSink(MMDeviceEnumerator enumerator, IList<SinkSpec> list)
        {
            if (list.Count == 0) return "输出设备";
            for (int i = 0; i < list.Count; i++)
                if (string.IsNullOrEmpty(list[i].DeviceId)) return list[i].Label + "声道设备";
            var seen = new bool[list.Count];
            try
            {
                var coll = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                for (int i = 0; i < coll.Count; i++)
                {
                    string id = null;
                    try { id = coll[i].ID; } catch { }
                    if (id == null) continue;
                    for (int s = 0; s < list.Count; s++)
                        if (!seen[s] && list[s].DeviceId == id) seen[s] = true;
                }
            }
            catch { }
            for (int i = 0; i < list.Count; i++)
                if (!seen[i]) return list[i].Label + "声道设备";
            return null;
        }

        public void SetMaster(float m)
        {
            for (int i = 0; i < sinks.Count; i++) sinks[i].SetMaster(m);
        }

        /// <summary>
        /// 运行中热替换路由: 重新 Compile 出矩阵并逐路原子换引用, 不停链路、不断音。
        /// 返回 false = 没有可替换的链路 (未运行 / 声道数变了需要重建)。
        ///
        /// 只换矩阵不换 provider: 输出声道数一变就得重建 BufferedWaveProvider 与端点,
        /// 那是"改设备"级别的事, 交给下一次启动 (P2 校验会先拦住超限配置)。
        /// </summary>
        public bool SetRouting(RoutingModel model)
        {
            if (model == null || sinks.Count == 0) return false;
            var compiled = model.Compile(specs, captureChannels);
            bool any = false;
            for (int i = 0; i < sinks.Count; i++)
            {
                int idx = compiled.IndexOf(specs[i].Key);
                if (idx < 0) continue;
                if (compiled.SinkOutCh[idx] != sinks[i].OutCh) continue;   // 声道数变了, 不能热换
                sinks[i].Matrix = compiled.SinkMatrix[idx];
                any = true;
            }
            return any;
        }

        public void SetGain(float gain)
        {
            for (int i = 0; i < sinks.Count; i++) sinks[i].SetGain(gain);
        }

        public void SetDriftMode(int mode, int fixedPpm)
        {
            for (int i = 0; i < sinks.Count; i++) sinks[i].SetDriftMode(mode, fixedPpm);
        }

        public string BufferStatus()
        {
            try
            {
                if (sinks.Count == 0 || sampleRate <= 0) return "-";
                var a = ProviderAt(0);
                var b = ProviderAt(1);
                if (sinks.Count == 2 && a != null && b != null)
                {
                    int minL = a.TakeMinBufferedMs();
                    int minR = b.TakeMinBufferedMs();
                    int regL = a.RegulatedMs, regR = b.RegulatedMs;
                    string s = "调节 L=" + regL + "ms R=" + regR + "ms 差=" + (regL - regR) + "ms" +
                        " 瞬时 L=" + a.BufferedMs + "ms R=" + b.BufferedMs + "ms" +
                        " 校正=" + Ppm(a.RatioPpm) +
                        " 最低 L=" + minL + "ms R=" + minR + "ms";
                    long ul = a.UnderrunSamples, ur = b.UnderrunSamples;
                    if (ul > 0 || ur > 0) s += " 欠载 L=" + ul + " R=" + ur;
                    double lv = a.TakeLevelRms(), rv = b.TakeLevelRms();
                    s += " 电平 L=" + LevelPct(lv) + "% R=" + LevelPct(rv) + "%";
                    string tl = a.TimingInfo, tr = b.TimingInfo;
                    if (tl != null) s += " 左" + tl;
                    if (tr != null) s += " 右" + tr;
                    return s;
                }
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < sinks.Count; i++)
                {
                    var p = sinks[i].Provider;
                    if (p == null) continue;
                    if (sb.Length > 0) sb.Append(" | ");
                    sb.Append(specs[i].Label).Append(' ')
                      .Append(p.RegulatedMs).Append('/').Append(p.TakeMinBufferedMs()).Append("ms ")
                      .Append(Ppm(p.RatioPpm));
                    double lvl = p.TakeLevelRms();
                    if (lvl >= 0) sb.Append(" 电平").Append(LevelPct(lvl)).Append('%');
                    if (p.UnderrunSamples > 0) sb.Append(" 欠载").Append(p.UnderrunSamples);
                }
                return sb.ToString();
            }
            catch { return "-"; }
        }

        static string Ppm(double v)
        {
            return v.ToString("+0;-0;0") + "ppm";
        }

        /// <summary>电平 RMS 转百分比显示; -1 (窗口内无样本) 显示 0。</summary>
        static string LevelPct(double rms)
        {
            return rms < 0 ? "0" : Math.Round(rms * 100, 1).ToString("0.#");
        }

        // 停止事件在音频线程触发; stopping 期间(自己停的)一律忽略
        void OnOutputStopped(string side, StoppedEventArgs e)
        {
            if (stopping) return;
            string why = "输出" + side + "已停止";
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
            for (int i = 0; i < sinks.Count; i++) sinks[i].Stop();
        }

        public void Dispose()
        {
            // 幂等: GiveUp / StopEngine / FormClosing 三条路径都会走到, 重复释放会抛
            if (disposed) return;
            disposed = true;

            try { Stop(); } catch { }

            if (loopback != null) { try { loopback.Dispose(); } catch { } loopback = null; }
            if (dataReady != null)
            {
                try { dataReady.Close(); } catch { }
                dataReady = null;
            }
            for (int i = 0; i < sinks.Count; i++) sinks[i].Dispose();
            sinks.Clear();
            if (captureDev != null)
            {
                try { captureDev.Dispose(); } catch { }
                captureDev = null;
            }
        }
    }
}
