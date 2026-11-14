using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using StereoSplitter.Devices;

namespace StereoSplitter
{
    class DriftCorrectingProvider : ISampleProvider
    {
        const int RingSize = 64;
        const int UpdateSamples = 1024;
        // 增益调度: 本环是"积分型对象 + 速率扰动", 时间常数 τ = T/(Cin*Kp)。
        // Kp=0.004 时 τ=23.75s、穿越频率 0.0067Hz, 而蓝牙设备时钟漂移的时间尺度是 40–60s,
        // 环与扰动同速 → 压不掉 (实测稳态摆动 std 8.1ms、均值偏差 +3.9ms)。
        // 原参数相位裕度尚有 85°, 提到 0.02 后 τ=4.7s、穿越 0.031Hz, PM 仍有 67°。
        // 排空段 (起播 195→95ms, 误差 >100%) 必须留在原增益, 否则会顶到 ±0.5% 限幅、
        // 音高滑移变陡; 故按误差分两段, 切换带滞回 + 约 400ms 斜坡, 避免校正值跳变。
        const double KpSteady = 0.02;
        const double KpFill = 0.004;
        const double GainEnterErr = 0.20;   // 误差降到 20% 以下 → 切稳态增益
        const double GainExitErr = 0.50;    // 误差回升到 50% 以上 → 退回填充增益
        // 兜底切换: 只用"误差<20%"判会死锁 —— 低增益下的稳态误差 = 速率差/KpFill,
        // 速率差 1500ppm 时稳态误差就是 37%, 永远够不到阈值, 环卡在低增益上, 偏移一直是 37ms。
        // 排空段最长约 100s (0.2% 速率下降 100ms), 128s 后必然结束, 故加一个时间兜底。
        const int SwitchAfterUpdates = 6000;   // 6000 × 1024 / 48k ≈ 128s
        const double MaxRatio = 0.005;
        // 补水位闸门的进入阈值(占目标的比例)。健康会话的瞬时水位会在 22–36ms 徘徊(块粒度锯齿),
        // 阈值必须压到它以下; 真断流时水位只剩小环里那 1ms, 任何合理阈值都能在一个块内触发。
        const double GateEnterRatio = 0.15;
        // 触顶判定窗口: 500 次更新 ≈ 10.7s (48kHz / 1024)
        const int ClampWarnUpdates = 500;
        // 同类异常事件限流: 采样线程上每 1024 样本就评估一次, 不限流会刷屏
        const int WarnIntervalMs = 60000;

        readonly BufferedWaveProvider src;
        readonly int targetSamples;
        readonly Action<string> log;
        readonly string tag;   // 声道标识: 左右两路会报出内容相同的告警, 不带标识分不清是哪路
        volatile int mode;          // 1=自适应 0=关闭 2=固定比例
        double fixedRatio;
        readonly byte[] tmp = new byte[RingSize * 4];
        readonly float[] tmpF = new float[RingSize];
        readonly float[] ring = new float[RingSize];
        int head;
        volatile int count;
        float prev;
        double phase;
        double smoothedTotal;
        // 供状态日志读的"调节水位": smoothedTotal 是 double 不能 volatile, 单独存毫秒整数
        volatile int smoothedMs;
        // 增益调度当前状态 + 斜坡后的实际 Kp (避免切换瞬间 ratio 阶跃 → 音高跳变)
        bool fastGain;
        double kpNow = KpFill;
        int updates;                // UpdateRatio 累计次数 (兜底切换用)
        int updateCounter;
        int minTotalSinceStatus = int.MaxValue;
        // 长整型: 静音整夜(48000/s)约 12.4 小时就会把 int 顶成负数, 欠载统计从此静默失效
        long underrunSamples;
        volatile float gain = 1.0f;
        volatile float master = 1.0f;
        volatile float ratio = 1.0f;
        // 欠载软收尾: 直接输出 0 是从任意电平硬切, 听感就是一声"咔";
        // 改为把最后一次输出按 3ms 指数衰减到 0, 恢复时再 3ms 淡入
        float lastOut;
        float fadeIn = 1f;
        float fadeStep;
        float decayCoef;
        // 欠载后的"补水位"闸门: 断粮即停止消费, 等水位回到目标 95% 再恢复。
        // 旧行为是"有多少吃多少", 输出端会一直贴着 0 水位啃零头 ——
        // 实测一次 0.8 秒的捕获断流会变成 7–10 秒的低水位/断音, 而暂停补满只要约 100ms。
        bool holding;
        int starveStreak;           // 连续处于"水位极低"的次数, 用于滤掉单次采样的锯齿谷底
        // 事件型诊断: 水位过低 / 校正触顶 (限流 + 恢复补记)
        bool lowWarned;
        int lastLowWarnTick;
        int clampStreak;
        double clampStartTotal;     // 本轮触顶开始时的水位 (判断"到底有没有在回升")
        int lastClampWarnTick;
        // 输出线程活性计数: 只在采样线程自增, UI 线程读取 (64 位进程读 long 不会撕裂)。
        // 冻结即代表 WasapiOut 不再调用 Read → 输出端停摆, 比看水位可靠。
        long renderedSamples;
        // 输出线程时序诊断: 我们的水位计数只能看见"我们给不出数据", 看不见
        // "WasapiOut 没按时来取" —— 后者会让设备侧自己欠载(蓝牙 A2DP 尤其容易),
        // 听感同样是"咔", 但日志里一片安静。这里量调用间隔与单次耗时把它暴露出来。
        long lastReadTs;
        int lateReads, slowReads;
        double maxGapMs, maxDurMs;
        int lastTimingWarnTick;

        public DriftCorrectingProvider(BufferedWaveProvider src, int targetSamples)
            : this(src, targetSamples, 1, 0, null)
        { }

        public DriftCorrectingProvider(BufferedWaveProvider src, int targetSamples, int mode, int fixedPpm)
            : this(src, targetSamples, mode, fixedPpm, null)
        { }

        public DriftCorrectingProvider(BufferedWaveProvider src, int targetSamples, int mode, int fixedPpm, Action<string> log)
            : this(src, targetSamples, mode, fixedPpm, log, null)
        { }

        public DriftCorrectingProvider(BufferedWaveProvider src, int targetSamples, int mode, int fixedPpm, Action<string> log, string tag)
        {
            this.src = src;
            this.log = log;
            this.tag = tag;
            this.targetSamples = Math.Max(1, targetSamples);
            this.mode = mode;
            this.smoothedTotal = this.targetSamples;
            this.smoothedMs = targetSamples * 1000 / Math.Max(1, src.WaveFormat.SampleRate);
            // 3ms 收尾/淡入: 48kHz 下约 144 样本
            float fadeSamples = Math.Max(1f, 0.003f * src.WaveFormat.SampleRate);
            fadeStep = 1f / fadeSamples;
            decayCoef = (float)Math.Exp(-1.0 / fadeSamples);
            if (mode == 0) fixedRatio = 1.0;
            else if (mode == 2)
            {
                int ppm = Math.Max(-5000, Math.Min(5000, fixedPpm));
                fixedRatio = 1.0 + ppm / 1000000.0;
            }
            else fixedRatio = 1.0;
            ratio = (float)fixedRatio;
        }

        public void SetMode(int newMode, int fixedPpm)
        {
            if (newMode == 2)
            {
                int ppm = Math.Max(-5000, Math.Min(5000, fixedPpm));
                fixedRatio = 1.0 + ppm / 1000000.0;
                ratio = (float)fixedRatio;
            }
            else if (newMode == 0)
            {
                fixedRatio = 1.0;
                ratio = 1f;
            }
            else
            {
                smoothedTotal = targetSamples;
                // 切回自适应时复位调度状态, 否则沿用上一次的 Kp 会产生一次阶跃
                fastGain = false;
                kpNow = KpFill;
            }
            mode = newMode;
        }

        public float Gain { get { return gain; } set { gain = value; } }

        public float Master { get { return master; } set { master = value; } }

        public WaveFormat WaveFormat { get { return src.WaveFormat; } }

        public int BufferedMs
        {
            get
            {
                int sr = src.WaveFormat.SampleRate;
                return sr > 0 ? (int)((long)(src.BufferedBytes / 4 + count) * 1000 / sr) : 0;
            }
        }

        public double RatioPpm { get { return ((double)ratio - 1.0) * 1e6; } }

        /// <summary>调节水位: 校正环实际调节的那个量 (2.1s 低通后的水位)。
        /// 与 BufferedMs 的区别: 后者是瞬时值, 含 WASAPI 回调块粒度造成的 ±18ms 锯齿,
        /// 日志里只看瞬时值会把块噪声误判成真实延迟抖动。</summary>
        public int RegulatedMs { get { return smoothedMs; } }

        public long UnderrunSamples { get { return Interlocked.Read(ref underrunSamples); } }

        /// <summary>累计渲染样本数 (只增不减), 看门狗用它判定输出线程是否还在跑。</summary>
        public long RenderedSamples { get { return renderedSamples; } }

        /// <summary>输出线程时序摘要; 全部正常时返回 null (状态行不显示, 免得干扰读数)。</summary>
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
            int v = Interlocked.Exchange(ref minTotalSinceStatus, int.MaxValue);
            if (v == int.MaxValue) return BufferedMs;
            int sr = src.WaveFormat.SampleRate;
            return sr > 0 ? (int)((long)v * 1000 / sr) : 0;
        }

        public int Read(float[] samples, int offset, int countRequested)
        {
            long t0 = Stopwatch.GetTimestamp();
            renderedSamples += countRequested;   // 活性计数: 只要被调用就说明输出线程还活着
            // 时序诊断①: 两次调用间隔远大于块时长 → WasapiOut 没按时来取, 设备侧会自己欠载
            if (lastReadTs != 0)
            {
                double gapMs = (t0 - lastReadTs) * 1000.0 / Stopwatch.Frequency;
                int expMs = countRequested * 1000 / Math.Max(1, src.WaveFormat.SampleRate);
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
            for (int i = 0; i < countRequested; i++)
            {
                if (count < 3 || holding)
                {
                    if (StepStarved())
                    {
                        Interlocked.Increment(ref underrunSamples);
                        // 软收尾: 把最后一次输出按 3ms 指数衰减到 0, 代替从任意电平硬切
                        fadeIn = 0f;
                        lastOut *= decayCoef;
                        if (lastOut > -1e-5f && lastOut < 1e-5f) lastOut = 0f;
                        samples[offset + i] = lastOut;
                        if (++updateCounter >= UpdateSamples) { updateCounter = 0; UpdateRatio(); }
                        continue;
                    }
                }

                int i1 = head;
                int i2 = i1 + 1; if (i2 >= RingSize) i2 -= RingSize;
                int i3 = i1 + 2; if (i3 >= RingSize) i3 -= RingSize;
                float v = Cubic(prev, ring[i1], ring[i2], ring[i3], (float)phase) * g * m;
                if (v > 1f) v = 1f;
                else if (v < -1f) v = -1f;
                if (fadeIn < 1f)
                {
                    // 欠载恢复淡入: 与 lastOut 的衰减对称, 避免电平从 0 直接跳到满幅
                    fadeIn += fadeStep;
                    if (fadeIn > 1f) fadeIn = 1f;
                    v *= fadeIn;
                }
                lastOut = v;
                samples[offset + i] = v;

                phase += ratio;
                while (phase >= 1.0)
                {
                    prev = ring[head];
                    head++;
                    if (head >= RingSize) head = 0;
                    count--;
                    phase -= 1.0;
                }

                if (++updateCounter >= UpdateSamples) { updateCounter = 0; UpdateRatio(); }
            }
            // 时序诊断②: 单次 Read 耗时超过 4ms 说明音频线程被抢占/卡住,
            // 高温或 GC 时会出现; 块时长只有 10ms, 吃满就意味着下一块必然迟到
            double durMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (durMs > 4)
            {
                slowReads++;
                if (durMs > maxDurMs) maxDurMs = durMs;
            }
            return countRequested;
        }

        /// <summary>欠载处理: 返回 true 表示本样本输出静音且不消费。
        /// holding 闸门是关键 —— 断粮后不再"有多少吃多少", 而是补到目标 95% 才恢复,
        /// 否则输出端会贴着 0 水位啃零头, 听感是长时间断音。</summary>
        bool StepStarved()
        {
            // count<3 只表示"这一环该续数据了", 每约 1.3ms 就会出现一次(64 样本小环),
            // 与"源没有数据"无关。旧实现拿它当断粮信号, 判据实际退化成"瞬时水位 < 50% 就静音",
            // 而瞬时值含块粒度锯齿(健康会话实测最低 22–36ms), 于是每 1–2 分钟误触发一次,
            // 每次静音 9–84ms + 3ms 淡出淡入 —— 听感就是"偶尔破音"。
            if (count < 3) Fill();
            int total = (int)(src.BufferedBytes / 4) + count;

            if (holding)
            {
                if (total >= targetSamples * 0.95) { holding = false; starveStreak = 0; return false; }
            }
            // 只看真实水位, 阈值压到 15%(95ms → 14ms): 真断流时 total 只剩小环里的 1ms,
            // 仍然会在一个块内立刻触发; 健康会话的锯齿谷底(22ms)碰不到。
            else if (total < targetSamples * GateEnterRatio)
            {
                // 连续两次(间隔约 1.3ms)都在阈值下才认, 单次采样恰好撞上谷底不算
                if (++starveStreak >= 2) holding = true;
            }
            else starveStreak = 0;
            return holding;
        }

        void Fill()
        {
            int want = RingSize - count;
            if (want <= 0) return;
            int got = src.Read(tmp, 0, want * 4);
            int n = got / 4;
            if (n <= 0) return;
            Buffer.BlockCopy(tmp, 0, tmpF, 0, n * 4);
            int idx = head + count;
            for (int i = 0; i < n; i++)
            {
                if (idx >= RingSize) idx -= RingSize;
                ring[idx] = tmpF[i];
                idx++;
            }
            count += n;
        }

        void UpdateRatio()
        {
            double total = src.BufferedBytes / 4 + count;
            if (total < minTotalSinceStatus) minTotalSinceStatus = (int)total;

            int sr = src.WaveFormat.SampleRate;

            // 平滑值所有模式都更新: 日志与告警统一用"调节水位"这个量
            smoothedTotal += 0.01 * (total - smoothedTotal);
            smoothedMs = MsOf(smoothedTotal, sr);

            // 事件型诊断①: 水位掉到目标 30% 以下 (与校正模式无关, 断流/设备卡顿都能看到)
            // 判据用平滑值: 瞬时值含 ±18ms 块粒度锯齿, 直接比阈值会误报
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

            if (mode != 1) return;

            double err = (smoothedTotal - targetSamples) / targetSamples;

            // 增益调度: 排空段(大误差)保持填充增益, 进入稳态段后换高增益
            // 时间兜底防止"低增益稳态误差恰好高于切换阈值"时永远切不过去
            updates++;
            if (!fastGain && (Math.Abs(err) < GainEnterErr || updates > SwitchAfterUpdates)) fastGain = true;
            else if (fastGain && Math.Abs(err) > GainExitErr) fastGain = false;
            double kpTarget = fastGain ? KpSteady : KpFill;
            kpNow += (kpTarget - kpNow) * 0.02;   // 约 1 秒过渡, 避免 ratio 阶跃导致音高跳变

            double r = 1.0 + kpNow * err;
            double lo = 1.0 - MaxRatio;
            double hi = 1.0 + MaxRatio;
            bool clamped = (r < lo) || (r > hi);
            if (r < lo) r = lo;
            else if (r > hi) r = hi;
            ratio = (float)r;

            // 事件型诊断②: 触顶 ≠ 故障。缓冲被抽空(捕获源短暂断流、设备切换)后, 环必须以 -0.5%
            // 全速回补, 这段一定会触顶且水位很低 —— 用"触顶 1 秒"判会把每次回补都误报成速率失配
            // (实测 21:53:56 就是这种情况: 默认输出被切走 1 秒, 水位掉到 0, 环在回补)。
            // 真正的失配是: 触顶持续 10 秒以上, 且这 10 秒里水位几乎没有回升。
            // 只报"负向触顶": 正向触顶是在排多余的水, 属自愈状态, 起播排空段因此也不会误报。
            if (clamped && fastGain && err < 0)
            {
                if (clampStreak == 0) clampStartTotal = smoothedTotal;
                clampStreak++;
                if (clampStreak >= ClampWarnUpdates)
                {
                    bool recovering = smoothedTotal - clampStartTotal > targetSamples * 0.1;
                    clampStreak = 0;   // 每 10 秒一个独立窗口
                    if (!recovering && WarnAllowed(ref lastClampWarnTick))
                        Report("[警告] 时钟校正已触顶 " + PpmOf(r) + " 超过 10 秒且水位未回升 (调节水位 " +
                            smoothedMs + "ms / 目标 " + MsOf(targetSamples, sr) + "ms)，两设备速率差超出可修正范围");
                }
            }
            else clampStreak = 0;
        }

        static int MsOf(double samples, int sr)
        {
            return sr > 0 ? (int)(samples * 1000 / sr) : 0;
        }

        static string PpmOf(double r)
        {
            return ((r - 1.0) * 1e6).ToString("+0;-0;0") + "ppm";
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
