using System;
using System.Diagnostics;
using NAudio.Wave;

namespace StereoSplitter
{
    /// <summary>
    /// 时钟校正 + 欠载保护 + 音量/联动增益的单路输出 provider。
    ///
    /// 多声道支持 (P1 起): 输入是**交错**采样流 (L,R,L,R…), 因此全部算法必须按**帧**
    /// 推进。若沿用"每输出一个样本就 phase += ratio"的单声道写法, phase 落在两个样本
    /// 之间时 Catmull-Rom 会在 L 与 R 之间插值 → 左右互相漏音 (ratio≠1 时持续存在,
    /// 听感是声场塌陷、人声发闷)。按帧推进后, 同一帧的所有通道共用一个 phase, 跨通道
    /// 相位天然一致 —— 相关声对放在同一台设备上时不可能出现偏音。
    ///
    /// 水位口径同理: BufferedBytes 是**字节**, 一帧 = 4 × channels 字节。旧实现直接
    /// 用 `/4` (等于样本数) 当水位, 2ch 时会把水位算成 2 倍 → 校正环把实际延迟拉到
    /// 目标的一半。
    /// </summary>
    class DriftCorrectingProvider : ISampleProvider
    {
        const int RingSize = 64;          // 小环深度 (帧)
        const int UpdateSamples = 1024;   // 每 1024 **帧** 评估一次 (48kHz 约 21ms)
        // 补水位闸门的进入阈值(占目标的比例)。健康会话的瞬时水位会在 22-36ms 徘徊(块粒度锯齿),
        // 阈值必须压到它以下; 真断流时水位只剩小环里那 1ms, 任何合理阈值都能在一个块内触发。
        const double GateEnterRatio = 0.15;
        const int WarnIntervalMs = 60000;
        // 缓冲堆积削峰: 水位涨到目标的 2 倍, 就认定"设备这一小段没消费"(USB 等时调度抖动、
        // 端点被选择性挂起、驱动重同步), 直接丢掉最旧的一截数据压回 1.3 倍。
        // 为什么不用"加大 ratio 慢慢排": ratio 上限 ±1% 每秒只能排 10ms, 堆 150ms 要 15 秒,
        // 而看门狗 5 秒就判链路中断 → 整条链路重建、两路静音约 1 秒。
        // 丢 40ms 旧音频只是"跳一下", 重建是"没声了"。
        const double OverfillTrigRatio = 2.0;
        const double OverfillKeepRatio = 1.3;

        readonly PcmRingBuffer src;
        readonly int targetSamples;       // 单位: 帧
        readonly int channels;
        readonly Action<string> log;
        readonly string tag;              // 路标识: 多路会报出内容相同的告警, 不带标识分不清是哪路
        readonly int memberId;            // 本路在时钟组内的成员编号
        readonly ClockGroup group;        // 共享校正决策 (ratio/fastGain/kpNow)
        volatile int mode;                // 1=自适应 0=关闭 2=固定比例
        double fixedRatio;

        readonly byte[] tmp;              // 读字节暂存 (RingSize 帧)
        readonly float[] tmpF;            // 读浮点暂存
        readonly float[] ring;            // channels × RingSize, 按通道分段存
        readonly float[] prev;            // 每通道上一帧 (Cubic 的 p0)
        int head;
        volatile int count;               // 小环中的帧数
        double phase;                     // 帧小数相位, 每帧推进一次
        double smoothedTotal;
        volatile int smoothedMs;
        int updateCounter;                // 帧计数
        int minTotalSinceStatus = int.MaxValue;
        long underrunSamples;
        volatile float gain = 1.0f;
        volatile float master = 1.0f;
        volatile float ratio = 1.0f;
        // 欠载软收尾: 直接输出 0 是从任意电平硬切, 听感就是一声"咔";
        // 改为把最后一次输出按 3ms 指数衰减到 0, 恢复时再 3ms 淡入。按通道各存一份,
        // 否则左右会被衰减成同一个值 → 断流瞬间声像塌到中间。
        readonly float[] lastOut;
        float fadeIn = 1f;
        float fadeStep;
        float decayCoef;
        // 联动增益: 组内其他成员 holding 时本路降到 0.5 (-6dB), 保持声像居中而非偏到一侧。
        float linkGain = 1f;
        float linkGainTarget = 1f;
        // 欠载后的"补水位"闸门: 断粮即停止消费, 等水位回到目标 95% 再恢复。
        bool holding;
        int starveStreak;
        bool lowWarned;
        int lastLowWarnTick;
        long renderedSamples;
        long trims;                       // 累计削峰次数 (看门狗据此区分"偶发抖动"与"设备真的跟不上")
        int lastTrimTick;
        int lastTrimWarnTick;
        // 输出线程时序诊断: 我们的水位计数只能看见"我们给不出数据", 看不见
        // "WasapiOut 没按时来取" —— 后者会让设备侧自己欠载, 听感同样是"咔"。
        long lastReadTs;
        int lateReads, slowReads;
        double maxGapMs, maxDurMs;
        int lastTimingWarnTick;

        public DriftCorrectingProvider(PcmRingBuffer src, int targetSamples, int channels, int mode, int fixedPpm,
            Action<string> log, string tag, int memberId, ClockGroup group)
        {
            this.src = src;
            this.log = log;
            this.tag = tag;
            this.memberId = memberId;
            this.group = group;
            this.channels = Math.Max(1, channels);
            this.targetSamples = Math.Max(1, targetSamples);
            this.mode = mode;
            int ch = this.channels;
            tmp = new byte[RingSize * 4 * ch];
            tmpF = new float[RingSize * ch];
            ring = new float[RingSize * ch];
            prev = new float[ch];
            lastOut = new float[ch];
            this.smoothedTotal = this.targetSamples;
            this.smoothedMs = targetSamples * 1000 / Math.Max(1, src.WaveFormat.SampleRate);
            // 3ms 收尾/淡入: 48kHz 下约 144 帧
            float fadeFrames = Math.Max(1f, 0.003f * src.WaveFormat.SampleRate);
            fadeStep = 1f / fadeFrames;
            decayCoef = (float)Math.Exp(-1.0 / fadeFrames);
            if (mode == 0) fixedRatio = 1.0;
            else if (mode == 2)
            {
                int ppm = Math.Max(-10000, Math.Min(10000, fixedPpm));
                fixedRatio = 1.0 + ppm / 1000000.0;
            }
            else fixedRatio = 1.0;
            ratio = (float)fixedRatio;
        }

        public void SetMode(int newMode, int fixedPpm)
        {
            if (newMode == 2)
            {
                int ppm = Math.Max(-10000, Math.Min(10000, fixedPpm));
                fixedRatio = 1.0 + ppm / 1000000.0;
                ratio = (float)fixedRatio;
            }
            else if (newMode == 0)
            {
                fixedRatio = 1.0;
                ratio = 1f;
            }
            // 切回自适应(mode==1)时不重置 smoothedTotal: 关闭期间 UpdateRatio 仍在跟踪
            // 实际水位, 它是准的; 重置成 targetSamples 会让控制器"假装水位正常"约 1.5 秒不排水。
            mode = newMode;
        }

        public float Gain { get { return gain; } set { gain = value; } }

        public float Master { get { return master; } set { master = value; } }

        public WaveFormat WaveFormat { get { return src.WaveFormat; } }

        /// <summary>本路输出声道数。</summary>
        public int Channels { get { return channels; } }

        /// <summary>本路在时钟组内的成员编号。</summary>
        public int MemberId { get { return memberId; } }

        int FramesBuffered
        {
            get
            {
                int c = count;
                if (c < 0) c = 0;
                else if (c > RingSize) c = RingSize;
                return src.BufferedBytes / (4 * channels) + c;
            }
        }

        /// <summary>时钟环稳态目标 (ms)。看门狗用它算相对阈值, 而不是比一个写死的毫秒数。</summary>
        public int TargetMs { get { return MsOf(targetSamples, src.WaveFormat.SampleRate); } }

        /// <summary>累计削峰次数 (只增不减)。</summary>
        public long TrimCount { get { return System.Threading.Interlocked.Read(ref trims); } }

        /// <summary>距上次削峰的毫秒数 (从未削峰视为很久以前)。</summary>
        public int SinceLastTrimMs
        {
            get
            {
                int t = lastTrimTick;
                if (t == 0) return int.MaxValue;
                int d = unchecked(Environment.TickCount - t);
                return d < 0 ? int.MaxValue : d;
            }
        }

        public int BufferedMs
        {
            get
            {
                int sr = src.WaveFormat.SampleRate;
                return sr > 0 ? (int)((long)FramesBuffered * 1000 / sr) : 0;
            }
        }

        public double RatioPpm
        {
            get
            {
                if (group != null && mode == 1) return group.RatioPpm;
                return ((double)ratio - 1.0) * 1e6;
            }
        }

        /// <summary>调节水位: 校正环实际调节的那个量 (2.1s 低通后的水位, 单位 ms)。</summary>
        public int RegulatedMs { get { return smoothedMs; } }

        public long UnderrunSamples { get { return System.Threading.Interlocked.Read(ref underrunSamples); } }

        /// <summary>累计渲染样本数 (只增不减), 看门狗用它判定输出线程是否还在跑。</summary>
        public long RenderedSamples { get { return renderedSamples; } }

        public string TimingInfo
        {
            get
            {
                if (lateReads == 0 && slowReads == 0) return null;
                return "迟到 " + lateReads + "次/最大 " + maxGapMs.ToString("0") + "ms" +
                       " 卡顿 " + slowReads + "次/最大 " + maxDurMs.ToString("0") + "ms";
            }
        }

        public int TakeMinBufferedMs()
        {
            int v = System.Threading.Interlocked.Exchange(ref minTotalSinceStatus, int.MaxValue);
            int frames = v == int.MaxValue ? FramesBuffered : v;
            int sr = src.WaveFormat.SampleRate;
            return sr > 0 ? (int)((long)frames * 1000 / sr) : 0;
        }

        // ---- 输出电平 (RMS): 诊断"程序写的是真音频还是静音" ----
        readonly object rmsSync = new object();
        long rmsSumSq;      // 输出样本平方和 ×1e9 (定点累加, 避免跨线程 double 撕裂)
        long rmsCount;

        /// <summary>
        /// 取自上次调用以来的输出 RMS (0~1, 已含增益/主音量/联动), 同时清零重计。
        /// 返回 -1 = 统计窗口内没有写入任何样本 (流未跑)。
        /// </summary>
        public double TakeLevelRms()
        {
            long sq, n;
            lock (rmsSync) { sq = rmsSumSq; n = rmsCount; rmsSumSq = 0; rmsCount = 0; }
            if (n == 0) return -1;
            return Math.Sqrt(sq / (double)n / 1e9);
        }

        public int Read(float[] samples, int offset, int countRequested)
        {
            long t0 = Stopwatch.GetTimestamp();
            renderedSamples += countRequested;   // 活性计数: 只要被调用就说明输出线程还活着
            int ch = channels;
            int frames = countRequested / ch;
            if (frames <= 0) return countRequested;

            // 时序诊断①: 两次调用间隔远大于块时长 → WasapiOut 没按时来取, 设备侧会自己欠载
            int sr = src.WaveFormat.SampleRate;
            int expMs = sr > 0 ? frames * 1000 / sr : 0;
            if (lastReadTs != 0)
            {
                double gapMs = (t0 - lastReadTs) * 1000.0 / Stopwatch.Frequency;
                if (gapMs > expMs * 1.5 + 15)
                {
                    lateReads++;
                    if (gapMs > maxGapMs) maxGapMs = gapMs;
                    if (WarnAllowed(ref lastTimingWarnTick))
                        Report("[警告] 输出线程取数据迟到 " + gapMs.ToString("0") + "ms (期望约 " + expMs +
                            "ms)，设备侧可能已欠载断音；若频繁出现请调高「延迟」档位或改用有线输出");
                }
            }
            lastReadTs = t0;
            float g = gain;
            float m = master;
            double sqAcc = 0;   // 本块输出样本平方和 (RMS 诊断)
            int nAcc = 0;

            for (int f = 0; f < frames; f++)
            {
                // 联动增益平滑跟踪: 组内其他成员 holding 时本路降到 -6dB, 约 3ms 过渡
                if (linkGainTarget != linkGain)
                {
                    if (linkGainTarget > linkGain) { linkGain += fadeStep; if (linkGain > linkGainTarget) linkGain = linkGainTarget; }
                    else { linkGain -= fadeStep; if (linkGain < linkGainTarget) linkGain = linkGainTarget; }
                }
                float gm = g * m * linkGain;
                int baseIdx = offset + f * ch;

                if (count < 3 || holding)
                {
                    if (StepStarved())
                    {
                        System.Threading.Interlocked.Increment(ref underrunSamples);
                        // 软收尾: 把最后一次输出按 3ms 指数衰减到 0, 代替从任意电平硬切
                        fadeIn = 0f;
                        for (int c = 0; c < ch; c++)
                        {
                            float lo = lastOut[c] * decayCoef;
                            if (lo > -1e-5f && lo < 1e-5f) lo = 0f;
                            lastOut[c] = lo;
                            samples[baseIdx + c] = lo;
                            sqAcc += lo * lo; nAcc++;
                        }
                        if (++updateCounter >= UpdateSamples) { updateCounter = 0; UpdateRatio(); }
                        continue;
                    }
                }

                int i1 = head;
                int i2 = i1 + 1; if (i2 >= RingSize) i2 -= RingSize;
                int i3 = i1 + 2; if (i3 >= RingSize) i3 -= RingSize;
                for (int c = 0; c < ch; c++)
                {
                    int cb = c * RingSize;
                    float v = Cubic(prev[c], ring[cb + i1], ring[cb + i2], ring[cb + i3], (float)phase) * gm;
                    if (v > 1f) v = 1f;
                    else if (v < -1f) v = -1f;
                    if (fadeIn < 1f) v *= fadeIn;   // 欠载恢复淡入: 与 lastOut 的衰减对称
                    lastOut[c] = v;
                    samples[baseIdx + c] = v;
                    sqAcc += v * v; nAcc++;
                }
                if (fadeIn < 1f)
                {
                    fadeIn += fadeStep;
                    if (fadeIn > 1f) fadeIn = 1f;
                }

                // ratio 的作用对象是**帧**: 同一帧所有通道共用 phase
                phase += ratio;
                while (phase >= 1.0)
                {
                    // count 保护: 帧首 phase 恒 <1, 但 ratio>1 时一帧最多扣 2 帧; 若这一帧
                    // 正好把环里最后一帧吃掉 (捕获瞬断 / 欠载那一下), 不判空就会把 count 扣成
                    // -1 → 下次 Fill 的 wantFrames 变 RingSize+1, 越界写坏 tmp/tmpF, 或
                    // idx=head-1 撞 ring[-1] 写坏相邻通道。相位归零等下一轮 Fill 补齐。
                    if (count <= 0) { phase = 0.0; break; }
                    for (int c = 0; c < ch; c++) prev[c] = ring[c * RingSize + head];
                    head++;
                    if (head >= RingSize) head = 0;
                    count--;
                    phase -= 1.0;
                }

                if (++updateCounter >= UpdateSamples) { updateCounter = 0; UpdateRatio(); }
            }
            // 时序诊断②: 单次 Read 耗时超过 4ms 说明音频线程被抢占/卡住
            double durMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (durMs > 4)
            {
                slowReads++;
                if (durMs > maxDurMs) maxDurMs = durMs;
            }
            if (nAcc > 0)
                lock (rmsSync) { rmsSumSq += (long)(sqAcc * 1e9); rmsCount += nAcc; }
            return countRequested;
        }

        /// <summary>欠载处理: 返回 true 表示本帧输出静音且不消费。
        /// holding 闸门是关键 —— 断粮后不再"有多少吃多少", 而是补到目标 95% 才恢复,
        /// 否则输出端会贴着 0 水位啃零头, 听感是长时间断音。</summary>
        bool StepStarved()
        {
            // count<3 只表示"这一环该续数据了", 与"源没有数据"无关。
            if (count < 3) Fill();
            int total = FramesBuffered;

            if (holding)
            {
                if (total >= targetSamples * 0.95)
                {
                    holding = false;
                    starveStreak = 0;
                    if (group != null) group.NotifyHolding(memberId, false);
                    return false;
                }
            }
            // 只看真实水位, 阈值压到 15%。真断流时 total 只剩小环里那几帧, 仍会在一个块内触发;
            // 健康会话的锯齿谷底(22ms)碰不到。
            else if (total < targetSamples * GateEnterRatio)
            {
                // 连续两次都在阈值下才认, 单次采样恰好撞上谷底不算
                if (++starveStreak >= 2)
                {
                    holding = true;
                    if (group != null) group.NotifyHolding(memberId, true);
                }
            }
            else starveStreak = 0;
            return holding;
        }

        void Fill()
        {
            int ch = channels;
            // 小环计数夹到 [0, RingSize]: 无论上游怎么抖, wantFrames 都不该越界
            if (count < 0) count = 0;
            else if (count > RingSize) count = RingSize;
            int wantFrames = RingSize - count;
            if (wantFrames <= 0) return;
            int got = src.Read(tmp, 0, wantFrames * 4 * ch);
            int n = got / (4 * ch);
            if (n <= 0) return;
            Buffer.BlockCopy(tmp, 0, tmpF, 0, n * ch * 4);
            int idx = head + count;
            for (int f = 0; f < n; f++)
            {
                if (idx >= RingSize) idx -= RingSize;
                int sb = f * ch;
                for (int c = 0; c < ch; c++) ring[c * RingSize + idx] = tmpF[sb + c];
                idx++;
            }
            count += n;
        }

        void UpdateRatio()
        {
            // 堆积过深 → 先就地削峰, 再把削峰后的真实水位喂给校正环。
            // 这条路径取代了以往"等看门狗判定链路中断 → 重建整条链路"的处理:
            // 设备短暂不消费属于可自愈的瞬态, 不该用 1 秒静音去换。
            TrimOverfill(FramesBuffered);

            int total = FramesBuffered;
            if (total < minTotalSinceStatus) minTotalSinceStatus = total;

            int sr = src.WaveFormat.SampleRate;

            // 平滑值所有模式都更新: 日志与告警统一用"调节水位"这个量
            smoothedTotal += 0.01 * (total - smoothedTotal);
            smoothedMs = MsOf(smoothedTotal, sr);

            // 事件型诊断①: 水位掉到目标 30% 以下 (与校正模式无关)
            if (smoothedTotal < targetSamples * 0.3)
            {
                if (!lowWarned && WarnAllowed(ref lastLowWarnTick))
                {
                    lowWarned = true;
                    Report("[警告] 缓冲水位偏低: 调节水位 " + smoothedMs + "ms / 目标 " + MsOf(targetSamples, sr) +
                        "ms (低于 30%)，可能出现欠载断音");
                }
            }
            else if (lowWarned && smoothedTotal >= targetSamples * 0.6)
            {
                lowWarned = false;
                Report("[状态] 缓冲水位已恢复: " + smoothedMs + "ms / 目标 " + MsOf(targetSamples, sr) + "ms");
            }

            // 联动增益与校正模式无关: 即使关闭校正, holding 联动仍需工作
            if (group != null)
                linkGainTarget = group.OtherHolding(memberId) ? 0.5f : 1.0f;

            if (mode != 1) return;

            // 校正决策委托给组: err 取组内平均, ratio / fastGain / kpNow 全部共享,
            // 组内插值方向始终一致 -> 消除稳态偏音。
            if (group != null)
            {
                group.Update(memberId, smoothedTotal);
                ratio = group.Ratio;
            }
        }

        static int MsOf(double frames, int sr)
        {
            return sr > 0 ? (int)(frames * 1000 / sr) : 0;
        }

        /// <summary>
        /// 水位超过目标的 2 倍时丢弃最旧的数据压回 1.3 倍, 返回削掉的帧数 (0 = 未触发)。
        /// 代价是时间轴跳过一小段 (听感"跳一下"), 换来的是链路不重建。
        /// </summary>
        int TrimOverfill(int total)
        {
            int trig = (int)(targetSamples * OverfillTrigRatio);
            if (total <= trig) return 0;

            int keep = (int)(targetSamples * OverfillKeepRatio);
            int dropFrames = total - keep;
            if (dropFrames <= 0) return 0;

            int dropped = src.Discard(dropFrames * 4 * channels) / (4 * channels);
            if (dropped <= 0) return 0;

            System.Threading.Interlocked.Increment(ref trims);
            lastTrimTick = Environment.TickCount;

            // 平滑水位直接对齐到削峰后的真值: 否则它还要约 2 秒才跟上, 期间 ratio 一直贴在
            // +1% 上限, 看门狗的"触顶 + 净涨"判据会把这次自愈当成设备故障再拆一次链路。
            smoothedTotal = keep;
            int sr = src.WaveFormat.SampleRate;
            smoothedMs = MsOf(smoothedTotal, sr);

            if (WarnAllowed(ref lastTrimWarnTick))
                Report("[提示] 缓冲堆积到 " + MsOf(total, sr) + "ms (目标 " + MsOf(targetSamples, sr) +
                    "ms)，已丢弃 " + MsOf(dropped, sr) + "ms 最旧数据压回 " + MsOf(keep, sr) +
                    "ms；偶发属设备短暂卡顿，频繁出现请调高「延迟」档位");
            return dropped;
        }

        bool WarnAllowed(ref int lastTick)
        {
            int now = Environment.TickCount;
            if (lastTick != 0 && now - lastTick < WarnIntervalMs) return false;
            lastTick = now;
            return true;
        }

        void Report(string msg)
        {
            if (log == null) return;
            try { log(tag == null ? msg : "[" + tag + "] " + msg); } catch { }
        }

        static float Cubic(float p0, float p1, float p2, float p3, float t)
        {
            float a = -0.5f * p0 + 1.5f * p1 - 1.5f * p2 + 0.5f * p3;
            float b = p0 - 2.5f * p1 + 2f * p2 - 0.5f * p3;
            float c = -0.5f * p0 + 0.5f * p2;
            return ((a * t + b) * t + c) * t + p1;
        }
    }
}
