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
        /// <summary>相关系数 (0~1)。只用于排除"完全不像扫频"的东西, 低于 <see cref="MicLatencyProbe.MinScore"/> 会被判为失败。</summary>
        public double Confidence;
        /// <summary>多次测量之间的极差 (毫秒)。大 = 环境不稳或回声重, 建议重测。</summary>
        public double SpreadMs;
        /// <summary>各段峰显著度里最差的一个。它决定"音乐有没有盖过扫频", 是可信度的主判据。</summary>
        public double Psr;
        public int Takes;
        /// <summary>本次每一段的实测值 (毫秒), 供界面逐条打印, 便于判断是整体偏还是单段离群。</summary>
        public readonly List<double> Samples = new List<double>();
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
    /// <para><b>为什么不用先把音乐停掉</b>: 分流正在放音乐时, 扫频是**叠在音乐上**出去的。
    /// 这要求定位算法放弃"先猜声音从哪儿开始"的捷径 (包络峰值会锁在音乐最响处), 改在整段上
    /// 做一次匹配滤波 —— 见 <see cref="ChirpMatch"/>。代价是每段多十几毫秒的计算, 换来的是
    /// "放着音乐就能校准", 不必打断听感、也不必拆链路。</para>
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
        /// <summary>
        /// 扫频时长。
        /// <para>为什么是 120ms 而不是更短的 60ms: 校准允许在音乐播放中进行, 扫频要和音乐抢信噪比。
        /// 匹配滤波的处理增益 = 10·log10(时间带宽积) = 10·log10(T × 3600Hz), 60→120ms 直接多 3dB,
        /// 而且主瓣更窄、旁瓣更低。代价是听得更明显一点 —— 一声 120ms 的"啾"。</para>
        /// </summary>
        public const double ChirpSeconds = 0.120;
        /// <summary>
        /// 播放增益。0.35 ≈ -9dBFS。
        /// <para>要往音乐里"扎"进去, 所以比安静环境所需的更响; 不敢再高 —— 扫频叠在满幅音乐上
        /// 会让端点输出越过 0dBFS, 削顶产生的谐波会摊宽相关峰, 反而更不准。</para>
        /// </summary>
        const float PlayGain = 0.35f;
        /// <summary>
        /// 扫频之前的静音: 让 WASAPI 缓冲先排满, 保证 Play() 到出声的路径是稳态。
        /// <para>100ms 而不是更长: 前面已有 2 段预热, 端点早就是稳态了, 前导在这里只剩
        /// "给扫频让出一点位置"的作用, 缩短它直接换成可测延迟量程。</para>
        /// </summary>
        const double LeadInSeconds = 0.100;
        /// <summary>
        /// 每段的固定周期 (秒)。同一段内 = 前导静音 + 扫频 + 余量, 段与段首尾相接。
        /// <para>这么做是为了**一个播放流里连测多次**: 蓝牙端点每次新建流都要重新激活 A2DP 链路,
        /// 冷启动开销可达上百毫秒且每次不同, 逐次新建流等于在测"冷启动延迟"。
        /// 流内分段则是稳态 —— 与分流运行时的情形一致。</para>
        /// </summary>
        const double SegmentSeconds = 1.0;
        /// <summary>
        /// 可测延迟上限 (秒)。
        /// <para>扫频尾部必须落在"段末前 50ms"以内, 否则会抓到**下一段**的扫频, 结果被算成"延迟 = 段长"。
        /// 最后的 +50ms 是容差: 扫频被窗边截掉一小截时, 峰值位置仍在窗内, 结果依然可用。</para>
        /// </summary>
        const double MaxMeasurableSeconds = SegmentSeconds - 0.05 - LeadInSeconds - ChirpSeconds + 0.05;
        /// <summary>正式测量前的预热段数。2 段 ≈ 2 秒: 足够让蓝牙建链、编码器起振、功放退出待机。</summary>
        const int WarmupSegments = 2;

        /// <summary>
        /// 峰显著度下限 (主峰 / 相关序列均方根)。
        /// <para>相关系数只回答"这段波形像不像扫频", 不回答"它是不是这一段里最突出的东西"。
        /// 音乐很长、扫频很短, 相关系数可能被一两处巧合推到 0.4 以上; 而 PSR 要求峰比整段的
        /// 平均起伏高出一个数量级, 巧合做不到。两者是互补的: 前者防"没收到声", 后者防"被音乐骗"。</para>
        /// <para>这个数是自检里量出来的 (见 CaseMicChirpMatch): 类音乐噪声下真实扫频的 PSR
        /// 与纯噪声的最大 PSR 之间取对数中点, 并留一倍余量。</para>
        /// </summary>
        public const double MinPsr = 9.0;

        /// <summary>
        /// 相关系数下限。
        /// <para>它只用来排除"完全不像扫频"的东西 (纯音乐实测 0.06~0.07), 不是用来判可信度的 ——
        /// 归一化相关系数的分母是"窗内总能量", 音乐一直在响, 分母里绝大部分是音乐,
        /// 于是被系统性压低: 实测音乐 −10dB 时真扫频只到 0.31, −2dB 时只有 0.21,
        /// 而定位误差仍不到半个采样。拿 0.35 去卡它, 等于把能用的结果全判死。</para>
        /// <para>真正的判断交给 <see cref="MinPsr"/> —— 它是尺度无关的, 与音乐多响无关。</para>
        /// </summary>
        public const double MinScore = 0.12;

        /// <summary>可测延迟上限 (毫秒)。超过它扫频会落进下一段, 无法与本段区分。</summary>
        public static double MaxMeasurableMs { get { return MaxMeasurableSeconds * 1000.0; } }

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

        int outLatencyMs = 60;
        /// <summary>
        /// 播放测试音时向端点申请的缓冲 (毫秒)。
        /// <para>必须与实际分流时用的「延迟」档位一致: 不同端点对同一个缓冲请求的实际分配不同
        /// (USB 声卡基本照给, 蓝牙/A2DP 有自己的下限), 所以请求值不同 → 两路的缓冲差跟着变 →
        /// 测出来的**差值**就偏了。这正是"校准完仍有先后"的主因, 所以它由上层按当前档位传入。</para>
        /// </summary>
        public int OutLatencyMs
        {
            get { return outLatencyMs; }
            set { outLatencyMs = Math.Max(5, Math.Min(500, value)); }
        }

        /// <summary>
        /// 外部取消钩子: 返回 true 表示"立刻停下"。
        /// <para>为什么要传进来而不是只在上层两路之间检查: 一路要放 2 段预热 + 3 段测量 ≈ 5 秒,
        /// 只在外层检查的话点「停止」后还要再响 5 秒才停, 用户会以为没反应。
        /// 这里在播放等待 (50ms 一片) 和分析逐段之间都会问一次, 停下来的延迟 ≈ 50ms。</para>
        /// <para>它只做"中止", 不做清理 —— 清理仍在 <see cref="Stop"/>/<see cref="Dispose"/>。</para>
        /// </summary>
        public Func<bool> ShouldCancel;

        bool Canceled { get { Func<bool> f = ShouldCancel; return f != null && f(); } }

        public string CaptureDeviceName { get { return mic == null ? "(未打开)" : Devices.DeviceCatalog.SafeName(mic); } }
        public int CaptureRate { get { return rate; } }
        /// <summary>最近一次分析窗内的峰值电平 (dBFS)。用来在失败时告诉用户"麦克风到底听见了多响"，比笼统的"没收到声"好定位。</summary>
        public double LastPeakDb { get; private set; }
        /// <summary>最近一次分析的主峰显著度 (仅不静音模式计算; 静音模式为 0)。</summary>
        public double LastPsr { get; private set; }

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
        /// <para>一次打开播放流, 里面连续走「<see cref="WarmupSegments"/> 段预热 + <paramref name="takes"/> 段测量」。
        /// 预热段不参与统计, 只用来把端点从空闲推到稳态 (蓝牙要建链、功放要退待机);
        /// 测量段取中位数, 极差反映稳定性。</para>
        /// </summary>
        public LaneMeasurement MeasureLane(string renderDeviceId, int takes)
        {
            if (capture == null) throw new InvalidOperationException("采集尚未启动");
            if (captureFailed) throw new InvalidOperationException("麦克风采集中断: " + (captureError == null ? "未知原因" : captureError.Message));
            if (string.IsNullOrEmpty(renderDeviceId)) throw new InvalidOperationException("该输出卡片没有绑定设备");
            if (Canceled) throw new OperationCanceledException();
            if (takes < 1) takes = 1;
            if (takes > 4) takes = 4;

            MMDevice dev = null;
            try
            {
                try { dev = enumerator.GetDevice(renderDeviceId); }
                catch (Exception ex) { throw new InvalidOperationException("打不开输出设备: " + ex.Message, ex); }
                if (dev == null) throw new InvalidOperationException("输出设备已不在 (被拔出或禁用)");

                var result = new LaneMeasurement { Takes = takes, LatencyMs = double.NaN, Confidence = 0 };
                Exception lastError = null;

                List<double> latencies, confidences, psrs;
                List<string> errors;
                PlayRun(dev, takes, out latencies, out confidences, out psrs, out errors);

                for (int i = 0; i < latencies.Count; i++)
                {
                    result.Samples.Add(latencies[i]);
                    if (confidences[i] > result.Confidence) result.Confidence = confidences[i];
                }
                // 取最差的一段: 三千里有一段被音乐盖住, 这一路就该被质疑, 不能拿最好的那段交差。
                result.Psr = double.MaxValue;
                for (int i = 0; i < psrs.Count; i++) if (psrs[i] < result.Psr) result.Psr = psrs[i];
                if (result.Psr == double.MaxValue) result.Psr = 0;
                for (int i = 0; i < errors.Count && lastError == null; i++)
                    lastError = new InvalidOperationException(errors[i]);

                if (result.Samples.Count == 0)
                {
                    if (lastError != null) throw lastError;
                    throw new InvalidOperationException("测量失败 (未取到有效样本)");
                }
                // 中位数: 单段离群 (系统调度、突发噪声) 不应该把结果拖走
                var sorted = new List<double>(result.Samples);
                sorted.Sort();
                result.LatencyMs = sorted[sorted.Count / 2];
                result.SpreadMs = sorted[sorted.Count - 1] - sorted[0];
                result.Takes = result.Samples.Count;
                return result;
            }
            finally { if (dev != null) { try { dev.Dispose(); } catch { } } }
        }

        /// <summary>
        /// 在**同一个播放流**里连播 预热段 + 测量段, 再逐段定位扫频。
        ///
        /// <para><b>为什么要一个流</b>: 每次新建 WasapiOut 都会让端点重新激活 —— 蓝牙 A2DP 建链、
        /// USB 声卡退出省电, 首包要晚几十到几百毫秒, 而且每次都不同。逐次新建流测出来的是
        /// "冷启动延迟", 与分流运行时的持续流不是一回事, 差值也因此被放大/压缩。
        /// 流内分段则从第二段起就是稳态。</para>
        ///
        /// <para><b>时基</b>: 流内段间隔精确等于 <see cref="SegmentSeconds"/> (由设备时钟驱动),
        /// 所以第 k 段的起点 = t0 + (预热段数 + k) × 段长。t0 是调用 Play() 的时刻, 它到真正出声
        /// 还有一段固定延迟 (预填充 + 端点缓冲), 这段被算进结果里 —— 这正是运行时分流也要付的成本,
        /// 而且两路同源可比。</para>
        /// </summary>
        void PlayRun(MMDevice dev, int takes, out List<double> latencies, out List<double> confidences,
            out List<double> psrs, out List<string> errors)
        {
            latencies = new List<double>();
            confidences = new List<double>();
            psrs = new List<double>();
            errors = new List<string>();

            WaveFormat fmt = dev.AudioClient.MixFormat;
            SampleKind kind = KindOf(fmt);
            if (kind == SampleKind.Unsupported)
                throw new InvalidOperationException("输出设备格式不支持: " + fmt);

            int segments = WarmupSegments + takes;
            byte[] payload = BuildPayload(fmt, kind, segments);
            // 用显式 IEEE float 建 provider: WASAPI 共享模式的混音引擎永远是 float32,
            // 这样不会被 WAVE_FORMAT_EXTENSIBLE 的子格式判定坑到; 万一设备拒绝, 再退回混音格式
            // (payload 本来就是按混音格式的采样类型生成的, 两条路径共用同一份字节)。
            WaveFormat playFmt = (kind == SampleKind.Float32)
                ? WaveFormat.CreateIeeeFloatWaveFormat(fmt.SampleRate, fmt.Channels)
                : fmt;

            double t0;
            int waitMs = (int)Math.Round(segments * SegmentSeconds * 1000) + 400;
            using (var player = new WasapiOut(dev, AudioClientShareMode.Shared, false, outLatencyMs))
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
                // 等待这段播完。切成 50ms 一片是为了让「停止」能马上生效:
                // 一觉睡 5 秒的话, 用户点了停止还要听满 5 秒的扫频。
                for (int waited = 0; waited < waitMs; waited += 50)
                {
                    if (Canceled) break;
                    int slice = Math.Min(50, waitMs - waited);
                    Thread.Sleep(slice);
                }
                try { player.Stop(); } catch { }
            }

            if (Canceled) return;
            for (int k = 0; k < takes; k++)
            {
                if (stopping || Canceled) return;
                double segT0 = t0 + (WarmupSegments + k) * SegmentSeconds;
                try
                {
                    double ms, conf, psr;
                    Analyze(segT0, out ms, out conf, out psr);
                    latencies.Add(ms);
                    confidences.Add(conf);
                    psrs.Add(psr);
                }
                catch (Exception ex)
                {
                    // 单段失败不放弃整路: 取其余段的中位数仍然可用
                    errors.Add(ex.Message);
                }
            }
        }

        /// <summary>在已录到的样本里定位本段扫频的起点。<paramref name="segT0"/> = 该段在时间轴上的起点。</summary>
        void Analyze(double segT0, out double latencyMs, out double confidence, out double psr)
        {
            psr = 0;
            LastPsr = 0;
            // 分析窗卡死在本段内: 往前留 50ms 兜住时基误差, 往后留到段末前 50ms —— 再往后就可能
            // 抓到**下一段**的扫频 (流内分段是首尾相接的), 那会把结果算成"延迟 = 段长"。
            const double preRoll = 0.05;
            double segStart = segT0 - preRoll;
            double segEnd = segT0 + SegmentSeconds - 0.05;
            int n = (int)Math.Round((segEnd - segStart) * rate);

            if (n <= 0) { latencyMs = double.NaN; confidence = 0; return; }

            // —— 时基: 采集流是连续的, 所以"采样序号 → 时刻"是一条直线, 只需要一个原点。
            // 原点用**块的中位数**估计 (每块都能反推出一个原点: 到达时刻 − 该块末采样序号/采样率)。
            // 为什么不用"逐块时间戳": DataAvailable 的到达时刻有几十毫秒抖动 (上限就是一个缓冲长度),
            // 逐块用它会让每次测量的基准都晃一下, 实测同一台设备的环路延迟在 35~44ms 之间跳。
            // 单原点则把这点抖动抹平, 剩下的固定偏差对两路完全相同, 相减时抵消。
            //
            // 只取**离本段最近**的若干块: 麦克风时钟与系统时钟有 ppm 级偏差, 一次校准要连着跑十几秒,
            // 用整场会话的中位数会让前后的段各带一点点偏移。就近取则每段都自洽。
            double origin;
            int[] absIdx = null;
            float[][] datas = null;
            lock (gate)
            {
                if (blocks.Count == 0) { latencyMs = double.NaN; confidence = 0; psr = 0; return; }
                double lo = segStart - 0.25, hi = segEnd + 0.25;
                var near = new List<double>();
                for (int b = 0; b < blocks.Count; b++)
                {
                    double t = blockStart[b];
                    if (t + (double)blocks[b].Length / rate < lo || t > hi) continue;
                    near.Add(t - (double)blockIndex[b] / rate);
                }
                if (near.Count < 3)
                {
                    // 段附近没有足够的块 (例如段落在采集启动之前): 退回全部块, 至少能给出结果
                    near.Clear();
                    for (int b = 0; b < blocks.Count; b++)
                        near.Add(blockStart[b] - (double)blockIndex[b] / rate);
                }
                near.Sort();
                origin = near[near.Count / 2];
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

            float[] tmpl = BuildChirp(rate, ChirpSeconds);

            float peak = 0;
            for (int i = 0; i < n; i++) { float a = Math.Abs(seg[i]); if (a > peak) peak = a; }
            LastPeakDb = peak <= 0 ? -120.0 : 20.0 * Math.Log10(peak);

            // 唯一的定位路径: 整段匹配滤波 (见 ChirpMatch)。没有"先粗定位再窄窗互相关"那一步,
            // 因为那一步的前提是"段里只有扫频" —— 音乐正在放的时候不成立。
            string fail = null;
            FullChirpHit hit = ChirpMatch.Locate(seg, tmpl);
            confidence = hit.Score;
            psr = hit.Psr;
            LastPsr = hit.Psr;
            double offset = hit.Offset;

            if (!hit.Found)
            {
                fail = "麦克风没有收到测试音 (峰值 " + LastPeakDb.ToString("0") + " dBFS)。" +
                    "请确认麦克风没被静音/禁用、选的是正确的录音设备、该路音量不是 0。";
            }
            else if (hit.Score < MinScore)
            {
                fail = "测试音被环境声盖住了 (波形相似度 " + hit.Score.ToString("0.00") + " < " +
                    MinScore.ToString("0.00") + "，峰值 " + LastPeakDb.ToString("0") + " dBFS)。" +
                    "请调高该路音量、把麦克风挪近一些, 再重试一次。";
            }
            else if (hit.Psr < MinPsr)
            {
                // 最关键的保护: 音乐响到一定程度, 相关序列的底噪被抬高, 真峰与假峰的差距缩小,
                // 此时**给一个数不如不给** —— 一个偏了几十毫秒的延迟会让声场比不校准更歪,
                // 而用户没法从界面上看出它是错的。
                fail = "测试音被音乐盖住了 (峰显著度 " + hit.Psr.ToString("0.0") + " < " +
                    MinPsr.ToString("0.0") + "，峰值 " + LastPeakDb.ToString("0") + " dBFS)。" +
                    "请调高该路音量或暂时把音乐音量调小, 再重试一次。";
            }

            if (fail != null)
            {
                latencyMs = double.NaN;
                throw new InvalidOperationException(fail);
            }

            double arrival = origin + (s0 + offset) / rate;
            latencyMs = (arrival - segT0 - LeadInSeconds) * 1000.0;

            // 超量程: 说明"扫频"其实是被本段窗口边缘截到的下一段 (或者端点延迟异常大)。
            // 报错而不是给一个假值 —— 一个偏了整整一段的数字会让补偿方向都错。
            if (latencyMs < -20 || latencyMs > MaxMeasurableMs)
            {
                double bad = latencyMs;
                latencyMs = double.NaN;
                throw new InvalidOperationException(
                    "测得的延迟 " + bad.ToString("0") + " ms 超出量程 (0~" +
                    MaxMeasurableMs.ToString("0") + " ms)。" +
                    "多半是该端点延迟异常大, 或麦克风先听到了别的声源 (另一只音箱/回声)。");
            }
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
            return BuildChirp(sampleRate, ChirpSeconds);
        }

        /// <summary>
        /// 指定时长的扫频模板。播放侧与相关侧必须用**同一个时长**, 否则模板和录音对不上, 峰会糊掉。
        /// </summary>
        public static float[] BuildChirp(int sampleRate, double seconds)
        {
            int n = (int)Math.Round(seconds * sampleRate);
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

        /// <summary>
        /// 按目标格式生成整段播放缓冲: <paramref name="segments"/> 个首尾相接的等长段,
        /// 每段 = 前导静音 + 扫频 + 余量 (所有声道同相)。
        /// </summary>
        byte[] BuildPayload(WaveFormat fmt, SampleKind kind, int segments)
        {
            if (segments < 1) segments = 1;
            int r = fmt.SampleRate;
            int ch = fmt.Channels;
            int bytesPer = fmt.BitsPerSample / 8;
            int segLen = (int)Math.Round(SegmentSeconds * r);
            int lead = (int)Math.Round(LeadInSeconds * r);
            float[] chirp = BuildChirp(r, ChirpSeconds);
            long total = (long)segLen * segments;
            var buf = new byte[total * ch * bytesPer];
            for (int s = 0; s < segments; s++)
            {
                int segStartFrame = s * segLen;
                for (int i = 0; i < chirp.Length; i++)
                {
                    int frame = lead + i;
                    if (frame >= segLen) break;          // 段长被削到放不下扫频时截断 (正常人不会)
                    float v = chirp[i] * PlayGain;
                    int at = (segStartFrame + frame) * ch;
                    for (int c = 0; c < ch; c++)
                        WriteSample(buf, (at + c) * bytesPer, kind, v);
                }
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
