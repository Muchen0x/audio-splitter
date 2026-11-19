using System;
using System.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace StereoSplitter
{
    /// <summary>
    /// 一路输出的运行时: 捕获原始字节 → 延迟线 → 矩阵混音 → 缓冲 → 校正 provider → 端点。
    ///
    /// 与旧实现的差别只有一处: 混音不再写死"取第 0/1 声道", 而是查该路的
    /// [outCh, srcCh] 矩阵。矩阵由 RoutingModel.Compile() 折叠出来, 所以逻辑声道层
    /// (可读、可视化) 不进逐样本循环 —— 运行时开销与旧版一致。
    ///
    /// 每路独立持有 PcmRingBuffer / DelayLine / DriftCorrectingProvider / WasapiOut:
    /// 延迟、水位、时钟环、端点音量都互不干扰; 互相同步只由 ClockGroup 决定。
    /// </summary>
    sealed class SinkRuntime : IDisposable
    {
        readonly Action<string> log;
        readonly WaveFormat captureFormat;
        readonly int srcCh, srcBps, srcFrame;
        readonly bool srcIsFloat, srcIsFloat32, srcIs16;
        readonly string label;

        // ---- 重建管线所需的原始参数: 单路重建时按原样重新装配, 不再向引擎/UI 要 ----
        readonly int driftTargetSamples, fillTargetSamples, outLatencyMs, driftMode, fixedPpm, memberId;
        readonly ClockGroup group;
        // 捕获线程与本类的重建互斥: Feed 持锁写延迟线, Rebuild 先在 paused 置位后抢同一把锁,
        // 保证拆卸瞬间没有在途 Write (否则会往已释放的缓冲里 memcpy)。
        readonly object feedSync = new object();
        volatile bool paused;
        volatile bool closed;
        // 音量/主音量记录在本类: 重建出的 provider 是全新对象, 必须把当前值重新灌回去
        volatile float gainNow = 1f, masterNow = 1f;

        /// <summary>输出线程停止 (设备休眠/被独占/拔出)。重建期间自身触发的不上报。</summary>
        public event Action<SinkRuntime, StoppedEventArgs> OutputStopped;

        public SinkSpec Spec { get; private set; }
        public MMDevice Device { get; private set; }
        public WaveFormat OutFormat { get; private set; }
        public PcmRingBuffer Buffer { get; private set; }
        public DelayLine Line { get; private set; }
        public DriftCorrectingProvider Provider { get; private set; }
        public WasapiOut Output { get; private set; }
        // 运行中热更新: 引擎 Compile 出新矩阵后整体替换引用, 混音线程每块取一次本地副本。
        // volatile 保证替换立刻对音频线程可见 (矩阵本身不可变, 换引用即"原子提交")。
        volatile float[,] matrix;
        public float[,] Matrix
        {
            get { return matrix; }
            set { matrix = value; }
        }
        public int OutCh { get; private set; }
        public int FillBytes { get; private set; }
        public int DelayMs { get; private set; }
        /// <summary>本路所属时钟组 (独立组也是一个只含自己的组, 不会为 null)。</summary>
        public ClockGroup Group { get { return group; } }
        /// <summary>本路在组内的成员编号, 供组级停用/激活使用。</summary>
        public int MemberId { get { return memberId; } }
        public bool Healthy { get; set; }
        public bool ReachedFill { get { return Buffer != null && Buffer.BufferedBytes >= FillBytes; } }

        // 混音复用缓冲: 只在捕获线程访问 (DelayLine.emit 同步调用), 无并发
        float[] srcF, mixed;
        byte[] outBytes;

        public SinkRuntime(SinkSpec spec, MMDevice device, WaveFormat captureFormat, int driftTargetSamples,
            int fillTargetSamples, int outLatencyMs, int driftMode, int fixedPpm,
            int memberId, ClockGroup group, Action<string> log)
        {
            this.Spec = spec;
            this.Device = device;
            this.log = log;
            this.label = spec.Label;
            this.captureFormat = captureFormat;
            this.srcCh = captureFormat.Channels;
            this.srcBps = captureFormat.BitsPerSample / 8;
            this.srcFrame = srcBps * srcCh;
            this.srcIsFloat = captureFormat.Encoding == WaveFormatEncoding.IeeeFloat;
            this.srcIsFloat32 = srcIsFloat && captureFormat.BitsPerSample == 32;
            this.srcIs16 = captureFormat.BitsPerSample == 16;
            this.OutCh = Math.Max(1, spec.OutChannels);
            this.DelayMs = Math.Max(0, spec.DelayMs);
            this.driftTargetSamples = Math.Max(1, driftTargetSamples);
            this.fillTargetSamples = fillTargetSamples;
            this.outLatencyMs = outLatencyMs;
            this.driftMode = driftMode;
            this.fixedPpm = fixedPpm;
            this.memberId = memberId;
            this.group = group;

            OutFormat = WaveFormat.CreateIeeeFloatWaveFormat(captureFormat.SampleRate, OutCh);
            FillBytes = fillTargetSamples * OutCh * 4;
            CreatePipeline(device);
        }

        /// <summary>
        /// 装配本路的全部运行期对象 (缓冲 → provider → WasapiOut → 延迟线)。
        /// 首次构造与单路重建共用, 保证两条路径装出来的链路完全一致。
        /// </summary>
        void CreatePipeline(MMDevice device)
        {
            Device = device;
            // 1000ms 容量: 远大于稳态目标 (≤100ms), 给"堆积→削峰"留出可丢的余量;
            // 封装层保证三线程 (捕获写 / 输出读 / UI 修剪) 不撕裂。
            Buffer = new PcmRingBuffer(OutFormat, 1000);
            Provider = new DriftCorrectingProvider(Buffer, driftTargetSamples, OutCh, driftMode, fixedPpm,
                log, label, memberId, group);
            Provider.Gain = gainNow;
            Provider.Master = masterNow;

            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, outLatencyMs);
            output.PlaybackStopped += OnPlaybackStopped;
            output.Init(Provider);
            Output = output;

            // 延迟线: 输入是交错多声道原始字节, 按 blockAlign(整帧) 算目标字节才天然对齐
            Line = new DelayLine(BytesOf(captureFormat, DelayMs), Consume);
        }

        void OnPlaybackStopped(object sender, StoppedEventArgs e)
        {
            // 重建/停止时是我们自己拆的, 不能让上层再当故障处理 (否则会连锁触发重建)。
            // 用引用比对而不是标志位: NAudio 的 PlaybackStopped 可能在拆除之后才异步送达,
            // 那时 paused 已经复位, 只有 "sender 已不是当前 Output" 能可靠识别迟到事件。
            if (closed || !ReferenceEquals(sender, Output)) return;
            var h = OutputStopped;
            if (h == null) return;
            try { h(this, e); } catch { }
        }

        /// <summary>
        /// 捕获线程入口: 原始字节投进本路延迟线。
        /// 重建期间直接跳过 (该路本来就在重启, 丢几十毫秒数据没有意义), 锁保证不会写进已释放的缓冲。
        /// </summary>
        public void Feed(byte[] data, int bytes)
        {
            if (bytes <= 0 || paused) return;
            lock (feedSync)
            {
                if (paused) return;
                var line = Line;
                if (line == null) return;
                line.Write(data, bytes);
            }
        }

        /// <summary>
        /// 就地重建本路 (只换这一路的缓冲/provider/端点, 其它路继续出声)。
        /// 顺序: 暂停写入 → 等在途 Feed 退出 → 拆旧管线 → 建新管线 → 等预缓冲 → 起播。
        /// 返回 false = 重建失败 (设备打不开/被独占), 调用方应升级为整链路重建。
        /// <para>与首次启动的差别: 不重新枚举/校验全部设备, 只碰这一路; 失败时快速返回, 不弹框。</para>
        /// </summary>
        public bool Rebuild(MMDevice device, Func<bool> cancelled)
        {
            if (device == null || closed) return false;

            paused = true;
            lock (feedSync) { }             // 等在途 Feed 退出 (DelayLine.Write 之后才轮到我们)

            var oldDevice = Device;
            TeardownPipeline();             // 先断引用再 Stop/Dispose: 旧管的 PlaybackStopped 会被识别为迟到事件
            // 拆掉的那一路可能正处于 holding: 不清掉的话组内其它路会一直被联动降 6dB
            if (group != null) { try { group.NotifyHolding(memberId, false); } catch { } }

            try
            {
                CreatePipeline(device);
            }
            catch
            {
                TeardownPipeline();         // 半成品 (端点在 Init 阶段被独占等) 也要清干净, 否则下次重试会叠加句柄
                paused = false;
                return false;
            }

            // 建立过程中窗体可能正在退出 (Dispose 已经跑过): 这时新管线没人管 → 自己拆掉, 别泄漏音频端点
            if (closed)
            {
                TeardownPipeline();
                paused = false;
                return false;
            }

            paused = false;                 // 恢复灌数据, 下面等它攒到填充目标

            // 等进行: 延迟线要先攒够 DelayMs 才会往下游放, 再加上缓冲填充目标,
            // 这段时间该路本来就是静音 (它正在重启), 起播时水位已足 → 不会有起播欠载。
            int sr = Math.Max(1, OutFormat.SampleRate);
            int fillMs = (int)((long)fillTargetSamples * 1000 / sr);
            int waitMs = Math.Min(5000, DelayMs + fillMs + 1200);
            int t0 = Environment.TickCount;
            while (!closed && (cancelled == null || !cancelled()))
            {
                var buf = Buffer;
                if (buf == null || buf.BufferedBytes >= FillBytes) break;
                if (unchecked(Environment.TickCount - t0) >= waitMs) break;
                Thread.Sleep(5);
            }

            if (closed || (cancelled != null && cancelled()))
            {
                TeardownPipeline();
                paused = false;
                return false;
            }

            var outNow = Output;
            try { if (outNow != null) outNow.Play(); }
            catch
            {
                TeardownPipeline();
                paused = false;
                return false;
            }

            // 旧设备对象在新管线建成后才释放: 先放会把新管线要用的端点句柄一起带走的风险留到最后
            if (oldDevice != null && !ReferenceEquals(oldDevice, device))
            {
                try { oldDevice.Dispose(); } catch { }
            }
            // 端点回归/重建起播后重新参与共享校正 (清掉停用期间可能残留的旧 err/holding)
            if (group != null) { try { group.ActivateMember(memberId); } catch { } }
            return true;
        }

        /// <summary>
        /// F-04: 确认端点已不在活动列表 / 共享组即将整组重建时调用。
        /// 原先 RestartSink 在 Rebuild 之前直接返回, 于是捕获回调继续对一条死管线做
        /// 延迟/混音/写缓冲, 旧 WasapiOut 一直挂着, 故障前的 holding 与 err 残留在组里
        /// (健康成员被永久压低 6dB、共享 ratio 被离线旧误差带偏)。
        /// 这里: 暂停 Feed 并等在途写入退出 → 拆掉无效输出管线 → 把成员摘出共享校正。
        /// paused 保持 true, 直到 Rebuild 成功才复位; 重建失败重试时会再次调用本方法。
        /// </summary>
        public void SuspendForMissingEndpoint()
        {
            paused = true;
            lock (feedSync) { }             // 等在途 Feed 退出
            TeardownPipeline();             // 先断引用再 Stop/Dispose: 迟到事件会被识别
            if (group != null)
            {
                try
                {
                    group.NotifyHolding(memberId, false);
                    group.DeactivateMember(memberId);
                }
                catch { }
            }
        }

        /// <summary>拆掉当前管线 (幂等), 供重建失败/退出时复用。调用方负责 paused 的置位。</summary>
        void TeardownPipeline()
        {
            var output = Output;
            Output = null;
            Provider = null;
            Buffer = null;
            Line = null;
            if (output == null) return;
            try { output.Stop(); } catch { }
            try { output.Dispose(); } catch { }
        }

        /// <summary>延迟后的原始字节 → 混音 → 加入本路缓冲。</summary>
        void Consume(byte[] data, int offset, int bytes)
        {
            if (bytes < srcFrame || srcFrame <= 0) return;
            int frames = bytes / srcFrame;
            int total = frames * srcCh;
            if (srcF == null || srcF.Length < total) srcF = new float[total];
            Decode(data, offset, frames, srcF);

            int oc = OutCh;
            int need = frames * oc;
            if (mixed == null || mixed.Length < need) mixed = new float[need];
            if (outBytes == null || outBytes.Length < need * 4) outBytes = new byte[need * 4];

            var m = Matrix;
            // 声道数变了但还没重建 (热替换挡不住的边界): 宁可直通也不要越界
            if (m != null && m.GetLength(0) != oc) m = null;
            if (m == null)
            {
                // 未装配矩阵 (理论上不会发生): 直通前 N 个源声道, 保证不静音
                for (int f = 0; f < frames; f++)
                    for (int p = 0; p < oc; p++)
                        mixed[f * oc + p] = srcF[f * srcCh + (p < srcCh ? p : 0)];
            }
            else
            {
                int sc = m.GetLength(1);
                int useCh = sc < srcCh ? sc : srcCh;
                int clipped = 0;
                for (int f = 0; f < frames; f++)
                {
                    int sb = f * srcCh;
                    int db = f * oc;
                    for (int p = 0; p < oc; p++)
                    {
                        float acc = 0f;
                        for (int s = 0; s < useCh; s++)
                        {
                            float k = m[p, s];
                            if (k != 0f) acc += k * srcF[sb + s];
                        }
                        if (acc > 1f) { acc = 1f; clipped++; }
                        else if (acc < -1f) { acc = -1f; clipped++; }
                        mixed[db + p] = acc;
                    }
                }
                // 限幅兜底是"不破音", 不是"不失真": 削平的波形听感是压缩与爆点,
                // 所以要么让用户看见 (这里), 要么在 Validate 里提前算峰值增益 (那边也会报)。
                if (clipped > 0) OnClipped(clipped);
            }

            System.Buffer.BlockCopy(mixed, 0, outBytes, 0, need * 4);
            // 输出缓冲已满 → DiscardOnBufferOverflow 会整块静默丢弃 (听起来是断音), 限流记一条
            if (Buffer.BufferedBytes + need * 4 > Buffer.BufferLength && WarnOverflow())
                log("[警告] 输出缓冲已满 (" + label + " " + Buffer.BufferedBytes + "/" + Buffer.BufferLength +
                    " 字节)，本块已丢弃；输出设备可能卡顿或时钟校正跟不上");
            Buffer.AddSamples(outBytes, 0, need * 4);
        }

        /// <summary>捕获字节 → 交错 float。快路径 (32bit 浮点) 整块 BlockCopy, 与逐样本结果一致。</summary>
        void Decode(byte[] data, int offset, int frames, float[] dst)
        {
            int ch = srcCh;
            int bps = srcBps;
            int frame = srcFrame;
            if (srcIsFloat32)
            {
                System.Buffer.BlockCopy(data, offset, dst, 0, frames * ch * 4);
            }
            else if (srcIs16)
            {
                for (int f = 0; f < frames; f++)
                    for (int c = 0; c < ch; c++)
                        dst[f * ch + c] = BitConverter.ToInt16(data, offset + f * frame + c * bps) / 32768f;
            }
            else if (srcIsFloat)
            {
                for (int f = 0; f < frames; f++)
                    for (int c = 0; c < ch; c++)
                        dst[f * ch + c] = BitConverter.ToSingle(data, offset + f * frame + c * bps);
            }
            else
            {
                Array.Clear(dst, 0, frames * ch);
            }
        }

        int lastOverflowWarnTick;
        bool WarnOverflow()
        {
            int now = Environment.TickCount;
            if (lastOverflowWarnTick != 0 && now - lastOverflowWarnTick < 60000) return false;
            lastOverflowWarnTick = now;
            return true;
        }

        long clippedSamples;
        int lastClipWarnTick;
        /// <summary>本路累计被限幅的样本数 (UI 可据此判断是否该降增益)。</summary>
        public long ClippedSamples { get { return clippedSamples; } }

        void OnClipped(int n)
        {
            clippedSamples += n;
            int now = Environment.TickCount;
            // 限幅是持续性的, 10 秒一条就够, 别把日志刷满
            if (lastClipWarnTick != 0 && unchecked(now - lastClipWarnTick) < 10000) return;
            lastClipWarnTick = now;
            log("[警告] " + label + " 峰值被限幅 (本段 " + n + " 样本, 累计 " + clippedSamples +
                ")，请调低该路增益或声道抽头系数");
        }

        /// <summary>延迟线的目标字节数 = 采样率 × 毫秒 / 1000 帧 × 整帧字节。</summary>
        static int BytesOf(WaveFormat wf, int ms)
        {
            if (ms <= 0) return 0;
            int blockAlign = Math.Max(1, wf.BlockAlign);
            long bytes = (long)wf.SampleRate * ms / 1000 * blockAlign;
            return (int)Math.Min(int.MaxValue, bytes);
        }

        public int BufferedBytes { get { return Buffer == null ? 0 : Buffer.BufferedBytes; } }

        /// <summary>修剪快路多攒的水位: 等待以慢路为终点时, 无延迟的快路会多攒数据,
        /// 抵消延迟线的时差。统一修剪到填充阈值后, 净声道差 = 延迟线的差。</summary>
        public void TrimToFill()
        {
            if (Buffer == null) return;
            int excess = Buffer.BufferedBytes - FillBytes;
            if (excess <= 0) return;
            var tmp = new byte[4096];
            while (excess > 0)
            {
                int got = Buffer.Read(tmp, 0, Math.Min(excess, tmp.Length));
                if (got <= 0) break;
                excess -= got;
            }
        }

        public void SetGain(float g)
        {
            gainNow = g;
            if (Provider != null) Provider.Gain = g;
        }

        public void SetMaster(float m)
        {
            masterNow = m;
            if (Provider != null) Provider.Master = m;
        }

        public void SetDriftMode(int mode, int ppm) { if (Provider != null) Provider.SetMode(mode, ppm); }

        public void Start() { if (Output != null) Output.Play(); }

        public void Stop()
        {
            if (Output != null) { try { Output.Stop(); } catch { } }
            if (Line != null) { try { Line.Clear(); } catch { } }
        }

        public void Dispose()
        {
            closed = true;
            paused = true;
            if (Output != null) { try { Output.Dispose(); } catch { } Output = null; }
            if (Device != null) { try { Device.Dispose(); } catch { } Device = null; }
            Buffer = null;
            Line = null;
            Provider = null;
        }
    }
}
