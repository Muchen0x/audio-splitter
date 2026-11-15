using System;

namespace StereoSplitter
{
    /// <summary>
    /// 左右两个 DriftCorrectingProvider 共享的校正决策核心。
    ///
    /// 为什么需要共享: 左右两个 provider 各自独立算 ratio 时, 两台 USB 音箱的晶振
    /// 差异会让两侧 ratio 不等 (一侧上采样、一侧下采样), Catmull-Rom 插值在两个
    /// 方向上的频域响应不对称, 长期平均增益有差异 -> 听感偏音。
    ///
    /// 共享后: err 取左右平均, ratio / fastGain / kpNow 全部共享, 左右插值方向始终
    /// 一致。副作用是左右水位反向偏移 (dL - dR) * target / (2 Kp), 同型号 USB 音箱
    /// dL-dR 约 100ppm 时偏移约 0.25ms, 完全可接受。
    ///
    /// 线程安全: 左右 provider 各自在自己的采样线程调用 Update, 内部用锁保护。
    /// 锁开销可忽略 (每约 21ms 一次, 无竞争约 10ns)。
    /// </summary>
    class DriftCorrectionShared
    {
        const double KpSteady = 0.02;
        const double KpFill = 0.004;
        const double GainEnterErr = 0.20;   // 误差降到 20% 以下 -> 切稳态增益
        const double GainExitErr = 0.50;    // 误差回升到 50% 以上 -> 退回填充增益
        // 左右各调一次 Update, updates 累计速度翻倍, 阈值翻倍保持实际时间不变
        const int SwitchAfterUpdates = 12000;   // 原 6000 x 2, 仍约 128s
        const double MaxRatio = 0.005;
        const int ClampWarnUpdates = 1000;       // 原 500 x 2, 仍约 10.7s
        const int WarnIntervalMs = 60000;
        // kpNow 过渡系数减半: 原 0.02 -> 0.01, 保持约 1 秒过渡 (调用频率翻倍补偿)
        const double KpTransitionRate = 0.01;

        readonly object sync = new object();
        readonly double targetSamples;
        readonly int sampleRate;
        readonly Action<string> log;

        double errLeft, errRight;
        bool fastGain;
        double kpNow = KpFill;
        int updates;
        double ratio = 1.0;
        volatile float ratioOut = 1f;

        int clampStreak;
        double clampStartAvgTotal;
        int lastClampWarnTick;

        public DriftCorrectionShared(double targetSamples, int sampleRate, Action<string> log)
        {
            this.targetSamples = Math.Max(1, targetSamples);
            this.sampleRate = sampleRate;
            this.log = log;
        }

        /// <summary>由某一侧 provider 调用: 提交自己的平滑水位, 重算共享 ratio。</summary>
        public void Update(bool isLeft, double mySmoothedTotal)
        {
            double myErr = (mySmoothedTotal - targetSamples) / targetSamples;
            lock (sync)
            {
                if (isLeft) errLeft = myErr;
                else errRight = myErr;
                double avgErr = (errLeft + errRight) / 2.0;

                updates++;
                if (!fastGain && (Math.Abs(avgErr) < GainEnterErr || updates > SwitchAfterUpdates)) fastGain = true;
                else if (fastGain && Math.Abs(avgErr) > GainExitErr) fastGain = false;
                double kpTarget = fastGain ? KpSteady : KpFill;
                kpNow += (kpTarget - kpNow) * KpTransitionRate;

                double r = 1.0 + kpNow * avgErr;
                double lo = 1.0 - MaxRatio, hi = 1.0 + MaxRatio;
                bool clamped = (r < lo) || (r > hi);
                if (r < lo) r = lo;
                else if (r > hi) r = hi;
                ratio = r;
                ratioOut = (float)r;

                // 触顶诊断 (负向触顶, 与原 provider 逻辑一致):
                // 触顶不一定是故障 -- 回补段会触顶且水位很低。真正的失配是触顶持续 10 秒以上
                // 且水位未回升。只报负向: 正向是在排水, 属自愈。
                if (clamped && fastGain && avgErr < 0)
                {
                    double avgTotal = avgErr * targetSamples + targetSamples;
                    if (clampStreak == 0) clampStartAvgTotal = avgTotal;
                    clampStreak++;
                    if (clampStreak >= ClampWarnUpdates)
                    {
                        bool recovering = avgTotal - clampStartAvgTotal > targetSamples * 0.1;
                        clampStreak = 0;
                        if (!recovering && WarnAllowed(ref lastClampWarnTick))
                            Report("[警告] 时钟校正已触顶 " + PpmOf(r) + " 超过 10 秒且水位未回升 (平均调节水位 " +
                                MsOf(avgTotal) + "ms / 目标 " + MsOf(targetSamples) + "ms)，两设备速率差超出可修正范围");
                    }
                }
                else clampStreak = 0;
            }
        }

        public float Ratio { get { return ratioOut; } }

        public double RatioPpm
        {
            get { double r; lock (sync) r = ratio; return (r - 1.0) * 1e6; }
        }

        bool WarnAllowed(ref int lastTick)
        {
            int now = Environment.TickCount;
            if (lastTick != 0 && unchecked(now - lastTick) < WarnIntervalMs) return false;
            lastTick = now;
            return true;
        }

        void Report(string msg) { try { if (log != null) log(msg); } catch { } }

        int MsOf(double samples) { return sampleRate > 0 ? (int)(samples * 1000 / sampleRate) : 0; }

        static string PpmOf(double r) { return ((r - 1.0) * 1e6).ToString("+0;-0;0") + "ppm"; }
    }
}
