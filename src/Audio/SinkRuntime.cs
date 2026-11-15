using System;
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
    /// 每路独立持有 BufferedWaveProvider / DelayLine / DriftCorrectingProvider / WasapiOut:
    /// 延迟、水位、时钟环、端点音量都互不干扰; 互相同步只由 ClockGroup 决定。
    /// </summary>
    sealed class SinkRuntime : IDisposable
    {
        readonly Action<string> log;
        readonly WaveFormat captureFormat;
        readonly int srcCh, srcBps, srcFrame;
        readonly bool srcIsFloat, srcIsFloat32, srcIs16;
        readonly string label;

        public SinkSpec Spec { get; private set; }
        public MMDevice Device { get; private set; }
        public WaveFormat OutFormat { get; private set; }
        public BufferedWaveProvider Buffer { get; private set; }
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

            OutFormat = WaveFormat.CreateIeeeFloatWaveFormat(captureFormat.SampleRate, OutCh);
            Buffer = new BufferedWaveProvider(OutFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(1000),
                DiscardOnBufferOverflow = true
            };
            FillBytes = fillTargetSamples * OutCh * 4;

            Output = new WasapiOut(device, AudioClientShareMode.Shared, true, outLatencyMs);
            Provider = new DriftCorrectingProvider(Buffer, driftTargetSamples, OutCh, driftMode, fixedPpm,
                log, label, memberId, group);
            Output.Init(Provider);

            // 延迟线: 输入是交错多声道原始字节, 按 blockAlign(整帧) 算目标字节才天然对齐
            Line = new DelayLine(BytesOf(captureFormat, DelayMs), Consume);
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
                        if (acc > 1f) acc = 1f; else if (acc < -1f) acc = -1f;
                        mixed[db + p] = acc;
                    }
                }
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

        public void SetGain(float g) { if (Provider != null) Provider.Gain = g; }
        public void SetMaster(float m) { if (Provider != null) Provider.Master = m; }
        public void SetDriftMode(int mode, int ppm) { if (Provider != null) Provider.SetMode(mode, ppm); }

        public void Start() { if (Output != null) Output.Play(); }

        public void Stop()
        {
            if (Output != null) { try { Output.Stop(); } catch { } }
            if (Line != null) { try { Line.Clear(); } catch { } }
        }

        public void Dispose()
        {
            if (Output != null) { try { Output.Dispose(); } catch { } Output = null; }
            if (Device != null) { try { Device.Dispose(); } catch { } Device = null; }
            Buffer = null;
            Line = null;
            Provider = null;
        }
    }
}
