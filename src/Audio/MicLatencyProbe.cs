using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace StereoSplitter
{
    /// <summary>一个可供校准使用的录音端点 (麦克风 / 线路输入 / 立体声混音)。</summary>
    sealed class MicDeviceInfo
    {
        public string Id = "";
        public string Name = "";
    }

    /// <summary>单路的测量结果。</summary>
    sealed class LaneMeasurement
    {
        /// <summary>端到端延迟 (毫秒): 从软件开始送样本, 到麦克风"听到"扫频起点, 已扣除前导静音。</summary>
        public double LatencyMs;
        /// <summary>相关系数 (0~1)。越高越可信, 低于 <see cref="MicLatencyProbe.MinConfidence"/> 会被判为失败。</summary>
        public double Confidence;
        /// <summary>多次测量之间的极差 (毫秒)。大 = 环境不稳或回声重, 建议重测。</summary>
        public double SpreadMs;
        public int Takes;
    }

    /// <summary>
    /// 麦克风声学延迟测量。
    ///
    /// <para><b>为什么能做到</b>: 每台输出设备的真实延迟绝大部分在设备内部 (蓝牙编码、DSP、功放),
    /// Windows 只能报告链路缓冲 (往往为 0), 驱动层拿不到准确值。唯一可靠的办法是让声音真的跑一遍:
    /// 向该端点放一段扫频 (chirp) → 麦克风录下来 → 找出它到达的时刻。</para>
    ///
    /// <para><b>为什么测"差值"就够</b>: 测量里含有麦克风采集侧的固定延迟 (采集缓冲 + A/D),
    /// 它是一个与输出设备无关的常数, 两路相减时正好抵消。剩下的差就是"两路声音到达耳朵的时间差",
    /// 也正是需要补偿的量 —— 注意它**天然包含了两只音箱到麦克风的距离差**, 所以麦克风应摆在
    /// 实际听音位置, 而不是随便放在桌上。</para>
    ///
    /// <para><b>为什么用扫频而不是脉冲</b>: 单频/短脉冲与音乐、噪声容易混淆, 包络峰值也容易被
    /// 回声拖偏; 扫频在互相关中会收敛成一个很尖的峰, 且对房间反射不敏感 (反射能量被时间上摊开)。</para>
    ///
    /// <para><b>线程模型</b>: <see cref="Start"/> 打开采集并开始累积; 之后可在任意线程调用
    /// <see cref="MeasureLane"/> (内部会阻塞 ~1 秒)。采集回调在 WASAPI 线程上只做拷贝, 与测量互斥。</para>
    /// </summary>
    sealed class MicLatencyProbe : IDisposable
    {
        // ---------- 信号参数 ----------
        /// <summary>扫频起始/结束频率。覆盖人声与小型音箱的主要频段, 又不至于太高被房间吸收。</summary>
        public const double ChirpStartHz = 400;
        public const double ChirpEndHz = 4000;
        /// <summary>扫频时长。太短能量不足 (信噪比差), 太长会被回声重叠、峰变宽。</summary>
        public const double ChirpSeconds = 0.060;
        /// <summary>播放增益。0.25 ≈ -12dBFS, 足够响但不会削顶。</summary>
        const float PlayGain = 0.25f;
        /// <summary>扫频之前的静音: 让 WASAPI 缓冲先排满, 保证 Play() 到出声的路径是稳态。</summary>
        const double LeadInSeconds = 0.150;
        /// <summary>扫频之后的静音尾巴 (仅用于把播放队列填满, 不参与分析)。</summary>
        const double TailSeconds = 0.500;
        /// <summary>每次测量从 Play() 起等多久再分析 (毫秒)。</summary>
        const int TakeWaitMs = 1150;
        /// <summary>相关系数下限。低于它多半是没收到声/环境太吵, 结果不可信。</summary>
        public const double MinConfidence = 0.25;

        enum SampleKind { Unsupported, Float32, Pcm16, Pcm24, Pcm32 }

        // ---------- 采集状态 ----------
        readonly MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly object gate = new object();
        readonly List<float[]> blocks = new List<float[]>();
        readonly List<double> blockStart = new List<double>();
        readonly List<int> blockIndex = new List<int>();

        MMDevice mic;
        WasapiCapture capture;
        WaveFormat captureFormat;
        SampleKind captureKind;
        int rate;
        int channels;
        int totalSamples;
        volatile bool stopping;
        volatile bool captureFailed;
        Exception captureError;
        string captureDeviceId = "";

        public string CaptureDeviceName { get { return mic == null ? "(未打开)" : Devices.DeviceCatalog.SafeName(mic); } }
        public int CaptureRate { get { return rate; } }
        /// <summary>最近一次分析窗内的峰值电平 (dBFS)。用来在失败时告诉用户"麦克风到底听见了多响"，比笼统的"没收到声"好定位。</summary>
        public double LastPeakDb { get; private set; }

        // ==================== 设备枚举 ====================

        /// <summary>列出活动的录音端点。失败返回空表 (没有麦克风/权限被拒都走这里)。</summary>
        public static List<MicDeviceInfo> ListDevices()
        {
            var list = new List<MicDeviceInfo>();
            MMDeviceEnumerator en = null;
            MMDeviceCollection coll = null;
            try
            {
                en = new MMDeviceEnumerator();
                coll = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                for (int i = 0; i < coll.Count; i++)
                {
                    MMDevice d = null;
                    try
                    {
                        d = coll[i];
                        list.Add(new MicDeviceInfo { Id = d.ID, Name = Devices.DeviceCatalog.SafeName(d) });
                    }
                    catch { }
                    finally { if (d != null) { try { d.Dispose(); } catch { } } }
                }
            }
            catch { }
            finally
            {
                // MMDeviceCollection 没有实现 IDisposable (RCW 由 GC 回收), 只释放枚举器
                if (en != null) { try { en.Dispose(); } catch { } }
            }
            return list;
        }

        // ==================== 采集 ====================

        /// <summary>打开录音端点并开始累积。抛异常表示麦克风不可用 (被占用/被隐私设置禁用/设备已移除)。</summary>
        public void Start(string deviceId)
        {
            if (capture != null) throw new InvalidOperationException("采集已启动");
            captureDeviceId = deviceId ?? "";
            try { mic = enumerator.GetDevice(captureDeviceId); }
            catch (Exception ex) { throw new InvalidOperationException("打不开麦克风: " + ex.Message, ex); }
            if (mic == null) throw new InvalidOperationException("麦克风不存在或已拔出");

            capture = new WasapiCapture(mic, false, 100);
            captureFormat = capture.WaveFormat;
            captureKind = KindOf(captureFormat);
            if (captureKind == SampleKind.Unsupported)
                throw new InvalidOperationException("麦克风格式不支持: " + captureFormat);
            rate = captureFormat.SampleRate;
            channels = captureFormat.Channels;

            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            capture.StartRecording();

            // 让采集先跑起来再清空: 丢掉设备刚启动时的那段瞬态 (有些驱动会先吐一段乱码/爆音)
            Thread.Sleep(150);
            lock (gate)
            {
                blocks.Clear();
                blockStart.Clear();
                blockIndex.Clear();
                totalSamples = 0;
                stopping = false;
                captureFailed = false;
                captureError = null;
            }
        }

        void OnStopped(object sender, StoppedEventArgs e)
        {
            if (e != null && e.Exception != null)
            {
                captureError = e.Exception;
                captureFailed = true;
            }
        }

        void OnData(object sender, WaveInEventArgs e)
        {
            if (e == null || e.Buffer == null || e.BytesRecorded <= 0) return;
            try
            {
                double now = clock.Elapsed.TotalSeconds;
                float[] mono = ToMono(e.Buffer, e.BytesRecorded);
                if (mono.Length == 0) return;
                double dur = (double)mono.Length / rate;
                lock (gate)
                {
                    // 这一块数据的"起点时刻" ≈ 收到它的时刻 − 它自身的时长。
                    // 采集侧那段固定延迟 (缓冲 + A/D) 未知但恒定, 两路相减时抵消, 所以不必校准。
                    blockStart.Add(now - dur);
                    blockIndex.Add(totalSamples);
                    blocks.Add(mono);
                    totalSamples += mono.Length;
                }
            }
            catch
            {
                // 单块解析失败不该打断整次校准; 丢一块而已 (后续块的时间戳仍然是自洽的)
            }
        }

        // ==================== 测量 ====================

        /// <summary>
        /// 测量某一路输出设备的端到端延迟。
        /// <para><paramref name="takes"/> 次独立测量, 取相关系数最高的一次作为结果,
        /// 同时返回两次之间的极差供上层判断稳定性。</para>
        /// </summary>
        public LaneMeasurement MeasureLane(string renderDeviceId, int takes)
        {
            if (capture == null) throw new InvalidOperationException("采集尚未启动");
            if (captureFailed) throw new InvalidOperationException("麦克风采集中断: " + (captureError == null ? "未知原因" : captureError.Message));
            if (string.IsNullOrEmpty(renderDeviceId)) throw new InvalidOperationException("该输出卡片没有绑定设备");
            if (takes < 1) takes = 1;
            if (takes > 4) takes = 4;

            MMDevice dev = null;
            try
            {
                try { dev = enumerator.GetDevice(renderDeviceId); }
                catch (Exception ex) { throw new InvalidOperationException("打不开输出设备: " + ex.Message, ex); }
                if (dev == null) throw new InvalidOperationException("输出设备已不在 (被拔出或禁用)");

                var result = new LaneMeasurement { Takes = takes, LatencyMs = double.NaN, Confidence = 0 };
                var samples = new List<double>();
                Exception lastError = null;

                for (int k = 0; k < takes; k++)
                {
                    if (stopping) throw new InvalidOperationException("已取消");
                    try
                    {
                        double ms, conf;
                        PlayOnce(dev, out ms, out conf);
                        samples.Add(ms);
                        if (conf > result.Confidence) result.Confidence = conf;
                    }
                    catch (Exception ex)
                    {
                        // 单次失败不放弃整路: 第一次最常见的失败原因是设备刚从空闲唤醒
                        // (蓝牙/USB 声卡上电要几百毫秒, 首包可能整段落在分析窗之外)。
                        lastError = ex;
                    }
                    // 两次之间留一点空隙, 免得上一次的尾巴还在采集缓冲里
                    Thread.Sleep(150);
                }

                if (samples.Count == 0)
                {
                    if (lastError != null) throw lastError;
                    throw new InvalidOperationException("测量失败 (未取到有效样本)");
                }
                // 中位数: 单次离群 (系统调度、突发噪声) 不应该把结果拖走
                var sorted = new List<double>(samples);
                sorted.Sort();
                result.LatencyMs = sorted[sorted.Count / 2];
                result.SpreadMs = sorted[sorted.Count - 1] - sorted[0];
                result.Takes = samples.Count;
                return result;
            }
            finally { if (dev != null) { try { dev.Dispose(); } catch { } } }
        }

        /// <summary>向一个端点播一次扫频, 并算出它到达麦克风的时间。</summary>
        void PlayOnce(MMDevice dev, out double latencyMs, out double confidence)
        {
            WaveFormat fmt = dev.AudioClient.MixFormat;
            SampleKind kind = KindOf(fmt);
            if (kind == SampleKind.Unsupported)
                throw new InvalidOperationException("输出设备格式不支持: " + fmt);

            byte[] payload = BuildPayload(fmt, kind);
            // 用显式 IEEE float 建 provider: WASAPI 共享模式的混音引擎永远是 float32,
            // 这样不会被 WAVE_FORMAT_EXTENSIBLE 的子格式判定坑到; 万一设备拒绝, 再退回混音格式
            // (payload 本来就是按混音格式的采样类型生成的, 两条路径共用同一份字节)。
            WaveFormat playFmt = (kind == SampleKind.Float32)
                ? WaveFormat.CreateIeeeFloatWaveFormat(fmt.SampleRate, fmt.Channels)
                : fmt;

            double t0;
            using (var player = new WasapiOut(dev, AudioClientShareMode.Shared, false, 60))
            {
                try
                {
                    player.Init(new PayloadProvider(playFmt, payload));
                }
                catch (Exception)
                {
                    player.Init(new PayloadProvider(fmt, payload));
                }
                t0 = clock.Elapsed.TotalSeconds;
                player.Play();
                Thread.Sleep(TakeWaitMs);
                try { player.Stop(); } catch { }
            }

            Analyze(t0, out latencyMs, out confidence);
        }

        /// <summary>在已录到的样本里定位扫频起点。t0 = 调用 Play() 的时刻。</summary>
        void Analyze(double t0, out double latencyMs, out double confidence)
        {
            // 分析窗: t0 前 50ms 到 t0 后约 1.1s。前导静音 150ms + 设备延迟 + 采集缓冲都落在里面。
            const double preRoll = 0.05;
            double segStart = t0 - preRoll;
            double segEnd = t0 + LeadInSeconds + ChirpSeconds + 0.90;
            int n = (int)Math.Round((segEnd - segStart) * rate);
            if (n <= 0) { latencyMs = double.NaN; confidence = 0; return; }

            // —— 时基: 采集流是连续的, 所以"采样序号 → 时刻"是一条直线, 只需要一个原点。
            // 原点用**所有块的中位数**估计 (每块都能反推出一个原点: 到达时刻 − 该块末采样序号/采样率)。
            // 为什么不用"逐块时间戳": DataAvailable 的到达时刻有几十毫秒抖动 (上限就是一个缓冲长度),
            // 逐块用它会让每次测量的基准都晃一下, 实测同一台设备的环路延迟在 35~44ms 之间跳。
            // 单原点则把这点抖动抹平, 剩下的固定偏差对两路完全相同, 相减时抵消。
            double origin;
            int[] absIdx = null;
            float[][] datas = null;
            lock (gate)
            {
                if (blocks.Count == 0) { latencyMs = double.NaN; confidence = 0; return; }
                var origins = new double[blocks.Count];
                for (int b = 0; b < blocks.Count; b++)
                    origins[b] = blockStart[b] - (double)blockIndex[b] / rate;
                Array.Sort(origins);
                origin = origins[origins.Length / 2];
                datas = blocks.ToArray();
                absIdx = blockIndex.ToArray();
            }

            int s0 = (int)Math.Round((segStart - origin) * rate);
            var seg = new float[n];
            for (int b = 0; b < datas.Length; b++)
            {
                int abs = absIdx[b];
                int len = datas[b].Length;
                if (abs + len <= s0 || abs >= s0 + n) continue;
                int from = Math.Max(0, s0 - abs);
                int to = Math.Min(len, s0 + n - abs);
                int dst = abs + from - s0;
                for (int i = from; i < to; i++) seg[dst + (i - from)] = datas[b][i];
            }

            float[] tmpl = BuildChirp(rate);
            ChirpHit hit = LocateChirp(seg, tmpl);
            confidence = hit.Score;

            float peak = 0;
            for (int i = 0; i < n; i++) { float a = Math.Abs(seg[i]); if (a > peak) peak = a; }
            LastPeakDb = peak <= 0 ? -120.0 : 20.0 * Math.Log10(peak);

            if (!hit.Found)
            {
                latencyMs = double.NaN;
                throw new InvalidOperationException(
                    "麦克风完全没有收到声音 (峰值 " + LastPeakDb.ToString("0") + " dBFS)。请确认: 麦克风没被静音/禁用、" +
                    "选的是正确的录音设备、音箱音量不是 0、并且麦克风确实能听到这两只音箱。");
            }
            if (hit.Score < MinConfidence)
            {
                latencyMs = double.NaN;
                throw new InvalidOperationException(
                    "测试音太弱或环境太吵 (相关系数 " + hit.Score.ToString("0.00") + " < " +
                    MinConfidence.ToString("0.00") + "，峰值 " + LastPeakDb.ToString("0") + " dBFS)。" +
                    "请调高该路音量、关掉音乐/风扇, 或把麦克风挪近一些后重试。");
            }

            double arrival = origin + (s0 + hit.Offset) / rate;
            latencyMs = (arrival - t0 - LeadInSeconds) * 1000.0;
        }

        public void Stop()
        {
            stopping = true;
            if (capture != null)
            {
                try { capture.StopRecording(); } catch { }
                try { capture.Dispose(); } catch { }
                capture = null;
            }
            if (mic != null) { try { mic.Dispose(); } catch { } mic = null; }
            lock (gate)
            {
                blocks.Clear();
                blockStart.Clear();
                blockIndex.Clear();
                totalSamples = 0;
            }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }
            try { enumerator.Dispose(); } catch { }
        }

        // ==================== 信号与算法 ====================

        /// <summary>
        /// 生成扫频模板 (单声道, 汉宁包络)。播放与互相关用的是**同一个函数、同一段时长**,
        /// 只是采样率可能不同 (输出 48k / 麦克风 44.1k), 频率随绝对时间的变化轨迹一致,
        /// 所以互相关的峰仍然尖锐。
        /// </summary>
        public static float[] BuildChirp(int sampleRate)
        {
            int n = (int)Math.Round(ChirpSeconds * sampleRate);
            if (n < 32) n = 32;
            var s = new float[n];
            double dt = 1.0 / sampleRate;
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double frac = (double)i / (n - 1);                       // 0..1
                double f = ChirpStartHz + (ChirpEndHz - ChirpStartHz) * frac;
                double env = 0.5 - 0.5 * Math.Cos(2 * Math.PI * frac);  // 汉宁窗: 两端归零, 边界不留咔哒声
                s[i] = (float)(Math.Sin(phase) * env);
                phase += 2 * Math.PI * f * dt;
            }
            return s;
        }

        /// <summary>互相关定位结果。</summary>
        public struct ChirpHit
        {
            /// <summary>扫频起点在信号里的位置 (采样, 含小数 → 亚采样精度)。</summary>
            public double Offset;
            /// <summary>归一化相关系数 0~1 (与极性无关)。</summary>
            public double Score;
            public bool Found;
        }

        /// <summary>
        /// 在 <paramref name="seg"/> 里找 <paramref name="tmpl"/> 的起点。
        /// <para>两步: 先用滑动峰值包络粗定位"声音从哪开始"(把搜索范围从整段压到几十毫秒),
        /// 再在这个窄窗里做归一化互相关 + 抛物线插值。归一化分母是本窗的信号能量, 所以
        /// 音量大小不影响峰的位置; 取绝对值后再插值, 所以麦克风/音箱极性反了也能正确定位。</para>
        /// </summary>
        public static ChirpHit LocateChirp(float[] seg, float[] tmpl)
        {
            var miss = new ChirpHit { Found = false };
            if (seg == null || tmpl == null) return miss;
            int m = tmpl.Length;
            if (m < 32 || seg.Length < m + 64) return miss;

            double tmplNorm = 0;
            for (int i = 0; i < m; i++) tmplNorm += (double)tmpl[i] * tmpl[i];
            tmplNorm = Math.Sqrt(tmplNorm);
            if (tmplNorm <= 1e-9) return miss;

            // —— 1) 峰值包络粗定位 ——
            int blk = Math.Max(16, m / 20);            // ≈3ms
            int nb = seg.Length / blk;
            if (nb < 3) return miss;
            var env = new float[nb];
            float envMax = 0;
            for (int b = 0; b < nb; b++)
            {
                int off = b * blk;
                float mx = 0;
                for (int i = 0; i < blk; i++)
                {
                    float a = Math.Abs(seg[off + i]);
                    if (a > mx) mx = a;
                }
                env[b] = mx;
                if (mx > envMax) envMax = mx;
            }
            if (envMax <= 1e-5f) return miss;          // 近似静音 (< -100dBFS)

            float th = envMax * 0.30f;
            int first = -1;
            for (int b = 0; b < nb; b++) if (env[b] >= th) { first = b * blk; break; }
            if (first < 0) first = 0;

            // 窗留够余量: 汉宁包络的 30% 阈值大约在扫频开始后 10~15ms 才越过,
            // 所以往前多留 30ms; 后面留 120ms 兜住"前面有个短促噪声把阈值骗过去"的情况。
            int from = Math.Max(0, first - blk * 10);
            int to = Math.Min(seg.Length - m, first + blk * 40);
            if (to < from) to = from;

            // —— 2) 归一化互相关 ——
            int nOff = to - from + 1;
            var score = new double[nOff];
            double bestAbs = 0, bestSigned = 0;
            int bestAt = -1;
            for (int o = from; o <= to; o++)
            {
                double dot = 0, energy = 0;
                int end = o + m;
                for (int i = o; i < end; i++)
                {
                    double s = seg[i];
                    dot += s * tmpl[i - o];
                    energy += s * s;
                }
                double sc = dot / (Math.Sqrt(energy) + 1e-12);
                score[o - from] = sc;
                double a = Math.Abs(sc);
                if (a > bestAbs) { bestAbs = a; bestAt = o; bestSigned = sc; }
            }
            if (bestAt < 0 || bestAbs <= 0) return miss;

            // —— 3) 抛物线插值 (在带符号的分数上做, 极性取最优峰的正负) ——
            double sub = 0;
            if (bestAt > from && bestAt < to)
            {
                double sign = bestSigned < 0 ? -1.0 : 1.0;
                double y0 = score[bestAt - 1 - from] * sign;
                double y1 = score[bestAt - from] * sign;
                double y2 = score[bestAt + 1 - from] * sign;
                double den = y0 - 2 * y1 + y2;
                if (Math.Abs(den) > 1e-12)
                {
                    sub = 0.5 * (y0 - y2) / den;
                    if (sub > 1) sub = 1; else if (sub < -1) sub = -1;
                }
            }

            return new ChirpHit
            {
                Offset = bestAt + sub,
                Score = bestAbs / tmplNorm,   // 除以模板范数 → 余弦相似度, 与音量无关
                Found = true
            };
        }

        // ==================== 采样格式转换 ====================

        static SampleKind KindOf(WaveFormat f)
        {
            if (f == null) return SampleKind.Unsupported;
            WaveFormatEncoding enc = f.Encoding;
            if (enc == WaveFormatEncoding.Extensible)
            {
                // WAVE_FORMAT_EXTENSIBLE 的真实编码在 SubFormat GUID 的 Data1 里
                var we = f as WaveFormatExtensible;
                if (we == null) return SampleKind.Unsupported;
                try { enc = (WaveFormatEncoding)BitConverter.ToInt32(we.SubFormat.ToByteArray(), 0); }
                catch { return SampleKind.Unsupported; }
            }
            if (enc == WaveFormatEncoding.IeeeFloat) return f.BitsPerSample == 32 ? SampleKind.Float32 : SampleKind.Unsupported;
            if (enc == WaveFormatEncoding.Pcm)
            {
                if (f.BitsPerSample == 16) return SampleKind.Pcm16;
                if (f.BitsPerSample == 24) return SampleKind.Pcm24;
                if (f.BitsPerSample == 32) return SampleKind.Pcm32;
            }
            return SampleKind.Unsupported;
        }

        /// <summary>按目标格式生成整段播放缓冲: 前导静音 + 扫频(所有声道同相) + 尾巴静音。</summary>
        static byte[] BuildPayload(WaveFormat fmt, SampleKind kind)
        {
            int r = fmt.SampleRate;
            int ch = fmt.Channels;
            int bytesPer = fmt.BitsPerSample / 8;
            int lead = (int)Math.Round(LeadInSeconds * r);
            int tail = (int)Math.Round(TailSeconds * r);
            float[] chirp = BuildChirp(r);
            int total = lead + chirp.Length + tail;
            var buf = new byte[total * ch * bytesPer];
            for (int i = 0; i < chirp.Length; i++)
            {
                float v = chirp[i] * PlayGain;
                int frame = lead + i;
                for (int c = 0; c < ch; c++)
                    WriteSample(buf, (frame * ch + c) * bytesPer, kind, v);
            }
            return buf;
        }

        static void WriteSample(byte[] b, int off, SampleKind kind, float v)
        {
            if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
            switch (kind)
            {
                case SampleKind.Float32:
                    {
                        byte[] t = BitConverter.GetBytes(v);
                        b[off] = t[0]; b[off + 1] = t[1]; b[off + 2] = t[2]; b[off + 3] = t[3];
                        break;
                    }
                case SampleKind.Pcm16:
                    {
                        short s = (short)Math.Round(v * 32767f);
                        b[off] = (byte)(s & 0xFF);
                        b[off + 1] = (byte)((s >> 8) & 0xFF);
                        break;
                    }
                case SampleKind.Pcm24:
                    {
                        int i32 = (int)Math.Round(v * 8388607f);
                        b[off] = (byte)(i32 & 0xFF);
                        b[off + 1] = (byte)((i32 >> 8) & 0xFF);
                        b[off + 2] = (byte)((i32 >> 16) & 0xFF);
                        break;
                    }
                case SampleKind.Pcm32:
                    {
                        int i32 = (int)Math.Round(v * 2147483647.0);
                        b[off] = (byte)(i32 & 0xFF);
                        b[off + 1] = (byte)((i32 >> 8) & 0xFF);
                        b[off + 2] = (byte)((i32 >> 16) & 0xFF);
                        b[off + 3] = (byte)((i32 >> 24) & 0xFF);
                        break;
                    }
            }
        }

        float ReadSample(byte[] b, int off)
        {
            switch (captureKind)
            {
                case SampleKind.Float32:
                    return BitConverter.ToSingle(b, off);
                case SampleKind.Pcm16:
                    return (short)(b[off] | (b[off + 1] << 8)) / 32768f;
                case SampleKind.Pcm24:
                    {
                        int i32 = b[off] | (b[off + 1] << 8) | (b[off + 2] << 16);
                        if ((i32 & 0x800000) != 0) i32 |= unchecked((int)0xFF000000);
                        return i32 / 8388608f;
                    }
                case SampleKind.Pcm32:
                    return BitConverter.ToInt32(b, off) / 2147483648f;
                default:
                    return 0f;
            }
        }

        /// <summary>多声道交错 → 单声道 (取各声道平均: 麦克风只接了单边也能收到, 且降低单声道噪声)。</summary>
        float[] ToMono(byte[] buf, int bytes)
        {
            int bytesPer = captureFormat.BitsPerSample / 8;
            int frame = bytesPer * channels;
            if (frame <= 0) return new float[0];
            int frames = bytes / frame;
            if (frames <= 0) return new float[0];
            var mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                int baseOff = i * frame;
                float sum = 0;
                for (int c = 0; c < channels; c++) sum += ReadSample(buf, baseOff + c * bytesPer);
                mono[i] = sum / channels;
            }
            return mono;
        }

        /// <summary>把一段预生成的字节流喂给 WasapiOut; 读完之后补零 (不断流, 避免驱动报欠载)。</summary>
        sealed class PayloadProvider : IWaveProvider
        {
            readonly WaveFormat format;
            readonly byte[] data;
            int pos;

            public PayloadProvider(WaveFormat format, byte[] data)
            {
                this.format = format;
                this.data = data ?? new byte[0];
            }

            public WaveFormat WaveFormat { get { return format; } }

            public int Read(byte[] buffer, int offset, int count)
            {
                int n = 0;
                if (pos < data.Length)
                {
                    n = Math.Min(count, data.Length - pos);
                    Buffer.BlockCopy(data, pos, buffer, offset, n);
                    pos += n;
                }
                for (int i = offset + n; i < offset + count; i++) buffer[i] = 0;
                return count;
            }
        }
    }
}
