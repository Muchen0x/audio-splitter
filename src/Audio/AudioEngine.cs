using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using StereoSplitter.Devices;
using StereoSplitter.Infrastructure;

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

        // R-01: 已打开、但所有权还没移交给 SinkRuntime 的端点。
        // 端点的所有权链是 "StartCore 局部 devs → SinkRuntime.Device → TeardownPipeline",
        // 移交发生在 SinkRuntime 构造成功那一刻。在此之前抛异常 (Init 失败/端点被独占/
        // 取消), 调用方只能 Dispose 引擎, 而 Dispose 只认 sinks 字段 —— 看不到这些局部变量,
        // 端点就漏到 GC 收 RCW 为止 (期间设备可能"无法停用/无法移除/枚举变慢")。
        // 这里把"建一台就登记一台"补上, 失败路径由 AbortStartup 统一回收。
        readonly List<MMDevice> pendingDevs = new List<MMDevice>();
        readonly object devGate = new object();   // start 线程与关窗线程都会碰 pendingDevs

        int splitErrCount;                        // 采样处理异常累计次数 (只记首条与每 100 条)
        volatile bool silentStartup = false;
        int firstDataLogged = 0;
        AutoResetEvent dataReady;

        // 捕获端固定开销估算 (WASAPI loopback 自身缓冲), 只用于界面选项与启动日志里的"总预估"
        internal const int CaptureLatencyEstimateMs = 10;

        // 时钟环稳态目标 (ms): 档位越小目标越低, 上限 100ms。
        // 余量按"捕获端交货间隙一次消耗 ~35ms (USB/HDMI 大块粒度实测) + 输出周期"校准,
        // 各档稳态剩余余量 ≥10ms, 避免瞬时水位探底穿零产生欠载:
        //   标准 100/低延迟 80/极低 65/超低 60/极限 60 → 剩余 15/15/10/15/20ms。
        public static int DriftTargetMs(int preMs, int outMs)
        {
            int margin = (preMs >= 100 || outMs >= 50) ? 25 :
                         (preMs >= 50 || outMs >= 30) ? 30 :
                         (preMs >= 25 || outMs >= 20) ? 40 :
                         (preMs >= 10 || outMs >= 10) ? 45 : 55;
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
        volatile bool disposed;
        int lastCaptureTick;
        /// <summary>整链路故障 (捕获中断 / 输出线程全体失效)。在音频线程触发, 订阅方须自行回 UI 线程。</summary>
        public event Action<AudioEngine, string> Stalled;
        /// <summary>单路输出故障 (参数: 路下标 + 人话原因)。订阅方可只重建这一路。</summary>
        public event Action<AudioEngine, int, string> SinkStalled;
        // 逐路去重: 一次设备事件会让 PlaybackStopped 与看门狗同时上报同一条故障。
        // 按下标分开记, 否则第一路报过之后第二路就永远报不出来 (两路同时掉线时只剩一路被救)。
        int[] stallNotified = new int[0];
        int captureStalled;             // 捕获级故障同样只报一次 (Stop 时置位, 引擎实例随之弃用)
        // F-03: 同一时刻只允许一个单路重建, 且 Dispose 必须取同一把锁才能释放 sinks。
        // 用 LifecycleGate 而不是裸 lock: 取消标志要在取锁前就对在途重建可见, 且"关闭+释放"
        // 必须是一次原子认领, 否则关窗 / GiveUp / FormClosing 三条路径并发时会重复释放端点。
        readonly LifecycleGate lifecycle = new LifecycleGate();

        // 起播余量: 覆盖输出端起播时对缓冲的预拉取 + 捕获端起播供应延迟。
        // 80 -> 20 (2026-11-18): 余量过大时校正环要用顶格放水 1~3 分钟才进稳态。
        // 20 -> 40 (同日实测): 20ms 时起播第 1 秒瞬时水位探到 9ms 并穿零一次 (欠载 460 样本,
        // 实测起播瞬间供缺口约 69ms); 40ms 完整覆盖该缺口, 放水段仅 ~8 秒。
        // 头几秒的剩余抖动由 holding 闸门 + 3ms 淡入淡出兜底。
        const int PrePullGuardMs = 40;

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

        /// <summary>全部输出路的端点 ID (供看门狗做存活探测; 路序与 SinkAt 一致)。</summary>
        public string[] SinkEndpointIds()
        {
            int n = sinks.Count;
            var ids = new string[n];
            for (int i = 0; i < n; i++)
            {
                var rt = sinks[i];
                ids[i] = rt != null && rt.Spec != null ? rt.Spec.DeviceId : null;
            }
            return ids;
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

        /// <summary>
        /// 启动的公共入口 —— 只负责"失败必回滚", 装配细节全在 StartCoreInner。
        /// R-01: 原实现没有 try/catch, 回滚责任被推给调用方; 调用方 (MainForm.Engine.cs)
        /// 只能 Dispose 引擎, 而引擎的 Dispose 只遍历 sinks 字段, 于是:
        /// 第 i 路构造失败时 devs[0..i-1] 在 sinks 里能被回收, devs[i..] 无人释放;
        /// 若失败点在 new WasapiLoopbackCapture 之前, devs 全部泄漏。
        /// 设备被独占 / 蓝牙未就绪 / USB 刚插上 —— 正是 MaxAutoRestart=3 反复踩的路径。
        /// </summary>
        StartOutcome StartCore(RoutingModel model, float gain, float master, int prebufferMs, int outLatencyMs,
            Func<bool> aborted, string expectedCaptureId)
        {
            try
            {
                return StartCoreInner(model, gain, master, prebufferMs, outLatencyMs, aborted, expectedCaptureId);
            }
            catch
            {
                AbortStartup();
                throw;
            }
        }

        /// <summary>
        /// 启动失败后的就地回滚: 先放"尚未移交给 SinkRuntime"的端点, 再走正常 Dispose 收掉
        /// 已经装配起来的链路。Dispose 内部由 LifecycleGate 认领唯一一次释放, 重复调用安全
        /// (调用方 catch 里还会再 Dispose 一次, 第二次直接返回), 所以这里不必关心谁先谁后。
        /// </summary>
        void AbortStartup()
        {
            ReleasePendingDevices();
            try { Dispose(); } catch { }
        }

        /// <summary>
        /// 回收全部未移交端点。start 线程与关窗线程都可能走到, 用锁保护。
        /// 出锁前先拷一份再放, 不持锁调 COM —— 释放端点是同步调用, 没必要占着锁。
        /// </summary>
        void ReleasePendingDevices()
        {
            MMDevice[] left = null;
            lock (devGate)
            {
                if (pendingDevs.Count > 0)
                {
                    left = pendingDevs.ToArray();
                    pendingDevs.Clear();
                }
            }
            if (left == null) return;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != null) { try { left[i].Dispose(); } catch { } }
        }

        /// <summary>某端点已随 SinkRuntime 构造成功一起移交 (释放责任归它), 从待回收清单摘掉。</summary>
        void HandOffDevice(MMDevice dev)
        {
            lock (devGate)
            {
                int at = pendingDevs.IndexOf(dev);
                if (at >= 0) pendingDevs.RemoveAt(at);
            }
        }

        StartOutcome StartCoreInner(RoutingModel model, float gain, float master, int prebufferMs, int outLatencyMs,
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
                // R-13: 枚举本身失败 (COM 不可用) 与"设备确实不在"必须分开 —— 旧实现把前者也算成缺失,
                // 一次瞬时 COM 故障就会让启动停在"设备不可用"; 现在跳过预检, 让随后的 GetDevice 报真实原因。
                bool probeFailed;
                string missing = FindMissingSink(enumerator, specs, out probeFailed);
                if (probeFailed)
                    log("[警告] 无法枚举活动输出端点，跳过启动前的设备在位校验（接下来打开设备时会给出真实原因）");
                else if (missing != null)
                    throw new AudioDeviceException(missing + "不可用（可能已拔出或被禁用），请刷新设备后重试");

                for (int i = 0; i < specs.Count; i++)
                {
                    var d = enumerator.GetDevice(specs[i].DeviceId);
                    devs.Add(d);
                    // R-01: 建一台就登记一台。GetDevice 本身也可能在第 k 台抛, 分两次记账
                    // (先全建完再登记) 会让前 k-1 台漏在 pendingDevs 之外。
                    lock (devGate) pendingDevs.Add(d);
                }
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
                // R-01: 构造成功这一刻端点所有权已归 sink, 立刻"摘登记 + 入列"。
                // 后面几行 (矩阵/增益/订阅) 万一抛出, AbortStartup→Dispose 才能顺着 sinks
                // 找到这个半装配对象, 由它的 Dispose 连带释放端点 —— 若等赋完值再入列,
                // 中间任何一步抛出都是"端点已移交、对象却没人引用", 两头都不释放。
                HandOffDevice(devs[i]);
                sinks.Add(sink);
                sink.Matrix = compiled.SinkMatrix[i];
                sink.SetGain(gain);
                sink.SetMaster(master);
                // 单路重建时管线会被整体换掉, 所以订阅挂在 SinkRuntime 上 (只订一次), 而不是挂在 WasapiOut 实例上
                sink.OutputStopped += OnSinkOutputStopped;
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
                    // Feed 内部会跳过"正在单路重建"的那一路, 并保证不写进已拆掉的缓冲
                    for (int i = 0; i < sinks.Count; i++) sinks[i].Feed(e.Buffer, e.BytesRecorded);
                    if (e.BytesRecorded > 0) lastCaptureTick = Environment.TickCount;
                    if (silentStartup && e.BytesRecorded > 0 && Interlocked.Exchange(ref firstDataLogged, 1) == 0)
                        log("[启动] 音频流已就绪 (捕获源开始有数据)");
                }
                catch (Exception ex)
                {
                    // R-09: 只在首条记日志会让"持续失败"在日志里只剩一行 —— 之后几百次异常完全不可见。
                    // 回调线程上不能每次都写日志 (写盘比混音贵), 折中: 首条全量, 之后每 100 条报一次计数。
                    int n = Interlocked.Increment(ref splitErrCount);
                    if (n == 1) log("[错误] 采样数据处理异常: " + ex.Message);
                    else if (n % 100 == 0) log("[错误] 采样数据处理异常已累计 " + n + " 次 (最近: " + ex.Message + ")");
                }
                try
                {
                    var ev = dataReady;
                    if (ev != null) ev.Set();
                }
                catch (ObjectDisposedException) { }          // 关窗竞态, 预期内
                catch (Exception ex) { Swallow.Note("dataReady.Set", ex); }
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
            // R-03: maxGroupSize 是"单组成员数", 分组后它恒 ≤8 —— 只报它会在 9 台设备时
            // 写成"8 路共享", 与实际 9 路不符, 排障时误导。组数 >1 时把组数一并报出来。
            string clockText = groups.Count > 1
                ? groups.Count + " 组共享 (最大 " + maxGroupSize + " 路/组, 每组上限 " + ClockGroup.MaxMembers + ")"
                : (maxGroupSize > 1 ? maxGroupSize + " 路共享" : "每路独立");
            log("[启动] 时钟漂移补偿: " + (driftMode == 0 ? "关闭" : driftMode == 2 ? "固定 " + fixedPpm + "ppm" : "自适应") +
                ", 比例上限 ±1% / 时钟组 " + clockText);
            if (groups.Count > 1)
                log("[提示] 输出设备超过 " + ClockGroup.MaxMembers + " 台, 已按上限分成 " + groups.Count +
                    " 个时钟组；跨组之间不共享校正比例（单声道/立体声规格接这么多设备通常意味着该改用多声道规格）");
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
        /// <param name="probeFailed">true = 枚举活动端点这一步本身就失败了 (COM 不可用 / 设备服务未起)。
        /// 此时返回值无意义: 既不能说"都在", 也不能说"缺失", 调用方必须按"本轮无法判断"处理。</param>
        static string FindMissingSink(MMDeviceEnumerator enumerator, IList<SinkSpec> list, out bool probeFailed)
        {
            probeFailed = false;
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
            catch (Exception ex)
            {
                // R-09/R-13: 这里的吞会让"枚举失败"退化成"全部设备都缺失", 必须记下来并如实上报
                Swallow.Note("FindMissingSink 枚举活动端点", ex);
                probeFailed = true;
                return null;
            }
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
        /// 只换矩阵不换 provider: 输出声道数一变就得重建缓冲 (PcmRingBuffer) 与端点,
        /// 那是"改设备"级别的事, 交给下一次启动 (P2 校验会先拦住超限配置)。
        /// </summary>
        public bool SetRouting(RoutingModel model)
        {
            if (model == null || sinks.Count == 0) return false;

            // F-14: 编译用的 SinkSpec 以"运行中的链路"为准(稳定键/设备/声道数/延迟都不可热换),
            // 但把新模型同名 spec 的 Volume 复制进来 —— 否则 SinkSpec.Volume 的热更新永远进不了矩阵。
            var compileSpecs = new List<SinkSpec>(specs.Count);
            for (int i = 0; i < specs.Count; i++)
            {
                var run = specs[i];
                var fresh = FindSinkByKey(model.SinkSpecs, run.Key);
                if (fresh == null) return false;                       // 新模型缺了某一路 → 整体拒绝
                if (!string.Equals(fresh.DeviceId, run.DeviceId, StringComparison.Ordinal)) return false;
                if (fresh.OutChannels != run.OutChannels) return false;
                if (fresh.DelayMs != run.DelayMs) return false;
                if (!fresh.Enabled) return false;
                var copy = run.Clone();                                // 链路级参数保持运行值
                copy.Volume = fresh.Volume;                            // 唯一允许热换的 spec 级量
                compileSpecs.Add(copy);
            }
            if (compileSpecs.Count != sinks.Count) return false;       // 路数不符 → 整体拒绝

            var compiled = model.Compile(compileSpecs, captureChannels);
            // 全验证: 每一路都必须能在编译结果里找到且声道数匹配, 才允许提交任何矩阵。
            // 原实现逐路检查逐路替换, 前几路生效后发现不匹配仍可能返回 true, 留下半成品。
            var newMatrices = new float[sinks.Count][,];
            for (int i = 0; i < sinks.Count; i++)
            {
                int idx = compiled.IndexOf(specs[i].Key);
                if (idx < 0) return false;
                if (compiled.SinkOutCh[idx] != sinks[i].OutCh) return false;
                newMatrices[i] = compiled.SinkMatrix[idx];
            }
            // 全部通过 → 一次性提交 (sinks[i].Matrix 是引用替换, 逐路之间也不撕裂)
            for (int i = 0; i < sinks.Count; i++) sinks[i].Matrix = newMatrices[i];
            return true;
        }

        /// <summary>
        /// 运行中逐路改延迟: 只换各路的延迟线目标, 不重建端点/缓冲/矩阵。
        /// <para>与 <see cref="SetRouting"/> 的分工: 那个只能换矩阵, 延迟/设备/声道数都算
        /// "链路级"改不了。延迟这一项其实可以单独热改 —— 延迟线本身就是个可调深度的 FIFO,
        /// 换深度不会影响任何已经连好的端点, 于是"边听边调"只需要放开这一个参数。</para>
        /// <para>返回 false = 有路在模型里找不到(设备被换掉), 调用方应退回"停→开始"提示。
        /// 中途失败时已改的路保持新值 —— 它们本来就各自独立生效, 不存在半条链路的中间态。</para>
        /// </summary>
        public bool ApplyDelays(RoutingModel model)
        {
            if (model == null || sinks.Count == 0 || specs.Count != sinks.Count) return false;
            for (int i = 0; i < specs.Count; i++)
            {
                SinkSpec fresh = FindSinkByKey(model.SinkSpecs, specs[i].Key);
                if (fresh == null) return false;
                if (fresh.DelayMs == specs[i].DelayMs) continue;
                if (!sinks[i].SetDelayMs(fresh.DelayMs)) return false;
                // 同步运行规格: 之后 SetRouting 的"延迟不可热换"校验、看门狗、日志口径
                // 读到的都是这条链路当前的真实延迟 (而不是启动那一刻的旧值)。
                specs[i].DelayMs = Math.Max(0, Math.Min(2000, fresh.DelayMs));
            }
            return true;
        }

        static SinkSpec FindSinkByKey(IList<SinkSpec> list, string key)
        {
            if (list == null || key == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                if (!string.Equals(list[i].Key, key, StringComparison.Ordinal)) continue;
                if (list[i].Enabled) return list[i];
            }
            return null;
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
            catch (Exception ex) { Swallow.Note("BufferStatus 组装", ex); return "-"; }
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
        void OnSinkOutputStopped(SinkRuntime rt, StoppedEventArgs e)
        {
            if (stopping) return;
            int idx = sinks.IndexOf(rt);
            if (idx < 0) return;
            string side = rt != null && rt.Spec != null ? rt.Spec.Label : "第" + (idx + 1) + "路";
            string why = "输出" + side + "已停止";
            if (e != null && e.Exception != null) why += ": " + e.Exception.Message;
            else why += "（设备休眠/被独占/已移除）";
            RaiseSinkStalled(idx, why);
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
            if (Interlocked.Exchange(ref captureStalled, 1) != 0) return;
            var handler = Stalled;
            if (handler != null) { try { handler(this, why); } catch { } }
            else if (log != null) { try { log("[错误] " + why); } catch { } }  // 无人接管时才自己记, 避免与订阅方重复
        }

        /// <summary>某一路输出失效 (音频线程调用)。同一路同一轮只上报一次, 重建成功后由 ResetStallFlag 复位。</summary>
        void RaiseSinkStalled(int index, string why)
        {
            int n = sinks.Count;
            if (stallNotified.Length != n) stallNotified = new int[n];
            if (index < 0 || index >= n) { RaiseStalled(why); return; }
            if (Interlocked.Exchange(ref stallNotified[index], 1) != 0) return;
            var handler = SinkStalled;
            if (handler != null) { try { handler(this, index, why); } catch { } }
            else RaiseStalled(why);   // 没人做单路重建 → 退回整链路
        }

        /// <summary>单路重建成功后清掉该路的"已上报"标记, 让后续真实故障还能再上报一次。</summary>
        void ResetStallFlag(int index)
        {
            var arr = stallNotified;
            if (arr != null && index >= 0 && index < arr.Length) Interlocked.Exchange(ref arr[index], 0);
        }

        /// <summary>
        /// 只重建指定的一路: 重新打开该端点并装配全新管线, 其它路完全不停 (不重排缓冲、不断音)。
        /// 返回 false = 该路当前无法重建。endpointGone=true 表示端点已不在活动设备列表
        /// (拔出/被禁用/休眠未归) —— 这种情况调用方应"只等这一路", 而不是拆掉整条链路。
        /// </summary>
        public bool RestartSink(int index, out string error, out bool endpointGone)
        {
            // out 参数不能进闭包, 先用局部变量收集, 出临界区再回填。
            string err = null;
            bool gone = false;
            bool ok = lifecycle.Run(
                delegate { return RestartSinkCore(index, ref err, ref gone); },
                delegate { err = "引擎已停止"; return false; });
            error = err;
            endpointGone = gone;
            return ok;
        }

        /// <summary>
        /// RestartSink 的临界区主体。整个函数由 LifecycleGate.Run 包住: Dispose 取不到同一把锁
        /// 就不会动 sinks, 所以这里从 SinkAt 到 Rebuild 结束看到的都是同一批运行时对象。
        /// </summary>
        bool RestartSinkCore(int index, ref string error, ref bool endpointGone)
        {
            error = null;
            endpointGone = false;
            if (disposed || stopping) { error = "引擎已停止"; return false; }
            var rt = SinkAt(index);
            if (rt == null || rt.Spec == null) { error = "路号无效"; return false; }

            MMDevice dev = null;
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    // 端点不在活动列表里 (拔了/被禁用) 就别白折腾, 由调用方决定是等还是拆
                    var one = new List<SinkSpec>();
                    one.Add(rt.Spec);
                    bool probeFailed;
                    string gone = FindMissingSink(enumerator, one, out probeFailed);
                    if (probeFailed)
                        log("[警告] 无法枚举活动端点，本轮跳过" + rt.Spec.Label + "的在位校验，直接尝试重开设备");
                    else if (gone != null)
                    {
                        endpointGone = true;
                        error = "端点已不在活动设备列表中（可能已拔出或被禁用）";
                        // F-04: 以前在这里直接返回, 捕获回调会继续对死管线做混音/写缓冲,
                        // 旧 WasapiOut 挂着不拆, 故障前的 holding 与 err 也一直留在组里。
                        // 现在先暂停 Feed、拆掉无效管线、把成员摘出共享校正。
                        rt.SuspendForMissingEndpoint();
                        return false;
                    }
                    dev = enumerator.GetDevice(rt.Spec.DeviceId);
                }
            }
            catch (Exception ex)
            {
                error = "打开端点失败: " + ex.Message;
                return false;
            }

            // 从这里起 dev 是一个真实的 COM 端点: 走哪条分支都必须有人负责释放,
            // 否则中途一次异常就泄漏一个 IMMDevice。handedOff = 所有权已移交 Rebuild。
            bool handedOff = false;
            try
            {
                // F-02: 共享时钟组的恢复单位必须是"组", 不能只重建这一路。
                // 单路重建只等新管自己的预缓冲 (含额外的 PrePullGuardMs), 健康旧路已在
                // 低水位稳态; 两路随后共享同一 ratio, 水位差没有独立控制量能消除。
                // 返回 false 且 endpointGone=false → UI 走整链路重建, 复用 StartCore 已
                // 验证过的"统一预缓冲 + TrimToFill + 一起起播"对齐路径。
                // 独立组 (MemberCount==1) 仍走下面的单路重建, 其它路不断音。
                if (rt.Group != null && rt.Group.MemberCount > 1)
                {
                    // 这条分支不会调用 Rebuild, dev 的所有权仍在自己手里 → 就地释放
                    if (dev != null) { try { dev.Dispose(); } catch { } dev = null; }
                    handedOff = true;
                    rt.SuspendForMissingEndpoint();
                    error = "共享时钟组需整组对齐（升级为整链路重建）";
                    return false;
                }

                // dev 的所有权在这里转移给 rt.Rebuild: 无论成功、失败还是中途被取消,
                // 都由它就地完整回收, 调用方不再碰 dev。F-03 原实现在失败分支用
                // ReferenceEquals(dev, rt.Device) 判断要不要释放, 而 CreatePipeline 早已把新
                // device 写进公开字段, "相等"反而让这个新端点永远没人释放。
                handedOff = true;
                bool ok;
                try { ok = rt.Rebuild(dev, delegate { return stopping || disposed; }); }
                catch (Exception ex) { error = "重建异常: " + ex.Message; ok = false; }

                if (!ok)
                {
                    if (error == null) error = "装配音频管线失败（端点可能被独占）";
                    return false;
                }
                ResetStallFlag(index);
                return true;
            }
            finally
            {
                // 只有"尚未移交、也尚未就地释放"的端点归这里兜底
                if (!handedOff && dev != null) { try { dev.Dispose(); } catch { } }
            }
        }

        public void Stop()
        {
            // 先置位: 在途 Rebuild 的 cancelled 每 5ms 检查一次, 立刻退出等待循环;
            // 下面的 StopCore 会同步触发 Stopped 事件, 也要让它们按"自己停的"忽略。
            stopping = true;
            lifecycle.Run(delegate { StopCore(); });
        }

        /// <summary>停输出 (不含锁语义, 调用方须已持有生命周期锁)。</summary>
        void StopCore()
        {
            if (loopback != null) { try { loopback.StopRecording(); } catch { } }
            for (int i = 0; i < sinks.Count; i++) sinks[i].Stop();
        }

        public void Dispose()
        {
            // F-03: GiveUp / StopEngine / FormClosing 三条路径都会走到, 所以先在锁外把取消标志
            // 置位(让在途 Rebuild 尽快退出), 再由 LifecycleGate 原子认领并执行唯一一次释放。
            // 认领失败的调用直接返回, 不会重复 Dispose COM 端点; 而 Shutdown 只有取到与
            // RestartSink 同一把锁之后才会碰 sinks, 保证在途重建已经离开临界区。
            stopping = true;
            disposed = true;
            lifecycle.Shutdown(delegate
            {
                try { StopCore(); } catch { }

                if (loopback != null) { try { loopback.Dispose(); } catch { } loopback = null; }
                if (dataReady != null)
                {
                    try { dataReady.Close(); } catch { }
                    dataReady = null;
                }
                for (int i = 0; i < sinks.Count; i++) sinks[i].Dispose();
                sinks.Clear();
                // R-01 兜底: 正常路径下 AbortStartup 已经把这里清空 (空表直接返回, 零开销);
                // 但将来若新增"先开设备后装配"的路径, 忘了 try/catch 也不会再漏端点。
                ReleasePendingDevices();
                if (captureDev != null)
                {
                    try { captureDev.Dispose(); } catch { }
                    captureDev = null;
                }
            });
        }
    }
}
