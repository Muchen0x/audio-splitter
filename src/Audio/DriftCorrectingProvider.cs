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
        // 补水位闸门的进入阈值(占目标的比例)。健康会话的瞬时水位会在 22-36ms 徘徊(块粒度锯齿),
        // 阈值必须压到它以下; 真断流时水位只剩小环里那 1ms, 任何合理阈值都能在一个块内触发。
        const double GateEnterRatio = 0.15;
        // 同类异常事件限流: 采样线程上每 1024 样本就评估一次, 不限流会刷屏
        const int WarnIntervalMs = 60000;

        readonly BufferedWaveProvider src;
        readonly int targetSamples;
        readonly Action<string> log;
        readonly string tag;   // 声道标识: 左右两路会报出内容相同的告警, 不带标识分不清是哪路
        readonly bool isLeft;                    // 标识本侧是左还是右, 传给 shared 区分 err 写入
        readonly DriftCorrectionShared shared;   // 左右共享的校正决策 (ratio/fastGain/kpNow)
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
        // 联动增益: 对侧 holding 时本侧降到 0.5 (-6dB), 保持声像居中而非偏到一侧。
        // 平滑过渡用 fadeStep (约 3ms), 与欠载 fadeIn 节奏一致。
        float linkGain = 1f;
        float linkGainTarget = 1f;
        // 欠载后的"补水位"闸门: 断粮即停止消费, 等水位回到目标 95% 再恢复。
        // 旧行为是"有多少吃多少", 输出端会一直贴着 0 水位啃零头 ——
        // 实测一次 0.8 秒的捕获断流会变成 7–10 秒的低水位/断音, 而暂停补满只要约 100ms。
        bool holding;
        int starveStreak;           // 连续处于"水位极低"的次数, 用于滤掉单次采样的锯齿谷底
        // 事件型诊断: 水位过低 (限流 + 恢复补记); 校正触顶诊断已搬到 shared
        bool lowWarned;
        int lastLowWarnTick;
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

        public DriftCorrectingProvider(BufferedWaveProvider src, int targetSamples, int mode, int fixedPpm,
            Action<string> log, string tag, bool isLeft, DriftCorrectionShared shared)
        {
            this.src = src;
            this.log = log;
            this.tag = tag;
            this.isLeft = isLeft;
            this.shared = shared;
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
                // 切回自适应(mode==1)时不重置 smoothedTotal: 关闭期间 UpdateRatio 仍在跟踪
                // 实际水位, smoothedTotal 是准的; 重置成 targetSamples 会让控制器"假装水位正常"
                // 约 1.5 秒不排水, 在水位已堆积(如 240ms)时会导致继续涨到 400ms 才开始排。
                // fastGain/kpNow 在 shared 里, 共享状态保持连续, 不需要重置。
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

        public double RatioPpm
        {
            get
            {
                if (shared != null && mode == 1) return shared.RatioPpm;
                return ((double)ratio - 1.0) * 1e6;
            }
        }

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
                // 联动增益平滑跟踪: 对侧 holding 时本侧降到 -6dB, 约 3ms 过渡 (与 fadeIn 一致)
                if (linkGainTarget != linkGain)
                {
                    if (linkGainTarget > linkGain) { linkGain += fadeStep; if (linkGain > linkGainTarget) linkGain = linkGainTarget; }
                    else { linkGain -= fadeStep; if (linkGain < linkGainTarget) linkGain = linkGainTarget; }
                }
                float gm = g * m * linkGain;
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
                float v = Cubic(prev, ring[i1], ring[i2], ring[i3], (float)phase) * gm;
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
                if (total >= targetSamples * 0.95)
                {
                    holding = false;
                    starveStreak = 0;
                    if (shared != null) shared.NotifyHolding(isLeft, false);
                    return false;
                }
            }
            // 只看真实水位, 阈值压到 15%(95ms → 14ms): 真断流时 total 只剩小环里的 1ms,
            // 仍然会在一个块内立刻触发; 健康会话的锯齿谷底(22ms)碰不到。
            else if (total < targetSamples * GateEnterRatio)
            {
                // 连续两次(间隔约 1.3ms)都在阈值下才认, 单次采样恰好撞上谷底不算
                if (++starveStreak >= 2)
                {
                    holding = true;
                    if (shared != null) shared.NotifyHolding(isLeft, true);
                }
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

            // 联动增益与校正模式无关: 即使关闭校正, holding 联动仍需工作
            if (shared != null)
                linkGainTarget = shared.OtherHolding(isLeft) ? 0.5f : 1.0f;

            if (mode != 1) return;

            // 校正决策委托给 shared: err 取左右平均, ratio / fastGain / kpNow 全部共享,
            // 左右插值方向始终一致 -> 消除稳态偏音。
            // 副作用: 左右水位反向偏移 (dL-dR)*target/(2*Kp), 同型号 USB 音箱约 0.25ms。
            if (shared != null)
            {
                shared.Update(isLeft, smoothedTotal);
                ratio = shared.Ratio;
            }
        }

        static int MsOf(double samples, int sr)
        {
            return sr > 0 ? (int)(samples * 1000 / sr) : 0;
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
