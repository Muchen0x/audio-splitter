using System;

namespace StereoSplitter
{
    /// <summary>
    /// 一组共享同一个校正比例 (ratio) 的输出路。
    ///
    /// 为什么需要共享: 同一对相关声对(如左/右分离模式下的 L 与 R)分居两台设备时,
    /// 各自独立算 ratio 会让两侧 ratio 不等 (一侧上采样、一侧下采样), Catmull-Rom
    /// 插值在两个方向上的频域响应不对称, 长期平均增益有差异 -> 听感偏音。
    /// 共享后 err 取组内平均, ratio / fastGain / kpNow 全部共享, 插值方向始终一致。
    ///
    /// 分组策略见 docs/多设备声道路由设计.md §5.5:
    ///   - 相关声对跨设备 (mono-left/mono-right) -> 必须同组
    ///   - 拼装环绕 (每台设备各持一对) -> 每路独立组, 否则个体水位发散会让设备间
    ///     对齐随时间漂移 (水位即延迟)
    ///
    /// 线程安全: 各组员在各自采样线程调用 Update, 内部用锁保护。锁开销可忽略
    /// (每约 21ms 一次的短临界区)。
    /// </summary>
    class ClockGroup
    {
        const int MaxMembers = 8;
        const double KpSteady = 0.02;
        const double KpFill = 0.004;
        const double GainEnterErr = 0.20;   // 误差降到 20% 以下 -> 切稳态增益
        const double GainExitErr = 0.50;    // 误差回升到 50% 以上 -> 退回填充增益
        const double MaxRatio = 0.005;
        const int WarnIntervalMs = 60000;

        // 以下三个常量都是"每个成员"的基准值, 运行时按 MemberCount 归一:
        //   - 切换阈值: N 个成员各 21ms 调一次, 累计速度是 1 个成员的 N 倍 -> 阈值乘 N
        //   - 触顶告警: 同理乘 N
        //   - kp 过渡系数: 调用频率乘 N -> 系数除 N (保持约 1 秒过渡)
        // N=2 时得到 12000 / 1000 / 0.01, 与共享 ratio 改造完成时的取值逐值相同。
        const int SwitchAfterBase = 6000;   // 约 128 秒 (6000 * 21ms)
        const int ClampWarnBase = 500;      // 约 10.7 秒
        const double KpTransitionBase = 0.02;

        readonly object sync = new object();
        readonly double targetSamples;
        readonly int sampleRate;
        readonly Action<string> log;

        readonly double[] err = new double[MaxMembers];
        // holding 状态跨线程读写: volatile 数组元素不能直接声明, 用 Interlocked 语义的
        // bool 数组 + lock 已足够 (写入极稀疏, 读取在采样线程)
        readonly bool[] holding = new bool[MaxMembers];
        int members = 1;

        bool fastGain;
        double kpNow = KpFill;
        int updates;
        double ratio = 1.0;
        volatile float ratioOut = 1f;

        int clampStreak;
        double clampStartAvgTotal;
        int lastClampWarnTick;

        public ClockGroup(double targetSamples, int sampleRate, Action<string> log)
        {
            this.targetSamples = Math.Max(1, targetSamples);
            this.sampleRate = sampleRate;
            this.log = log;
        }

        /// <summary>组内成员数 (0 表示本组无成员, 供引擎判定是否独立组)。</summary>
        public int MemberCount
        {
            get { lock (sync) return members; }
            set
            {
                int n = value < 0 ? 0 : (value > MaxMembers ? MaxMembers : value);
                lock (sync) { if (members != n) members = n; }
            }
        }

        /// <summary>由某个成员调用: 提交自己的平滑水位(帧), 重算共享 ratio。</summary>
        public void Update(int memberId, double mySmoothedTotal)
        {
            if (memberId < 0 || memberId >= MaxMembers) return;
            double myErr = (mySmoothedTotal - targetSamples) / targetSamples;
            lock (sync)
            {
                err[memberId] = myErr;
                int n = members < 1 ? 1 : members;
                double sum = 0;
                for (int i = 0; i < n; i++) sum += err[i];
                double avgErr = sum / n;

                updates++;
                int switchAfter = SwitchAfterBase * n;
                if (!fastGain && (Math.Abs(avgErr) < GainEnterErr || updates > switchAfter)) fastGain = true;
                else if (fastGain && Math.Abs(avgErr) > GainExitErr) fastGain = false;
                double kpTarget = fastGain ? KpSteady : KpFill;
                kpNow += (kpTarget - kpNow) * (KpTransitionBase / n);

                double r = 1.0 + kpNow * avgErr;
                double lo = 1.0 - MaxRatio, hi = 1.0 + MaxRatio;
                bool clamped = (r < lo) || (r > hi);
                if (r < lo) r = lo;
                else if (r > hi) r = hi;
                ratio = r;
                ratioOut = (float)r;

                // 触顶诊断 (只报负向): 触顶不一定是故障 -- 回补段会触顶且水位很低。
                // 真正的失配是触顶持续 10 秒以上且水位未回升。正向触顶是在排水, 属自愈。
                if (clamped && fastGain && avgErr < 0)
                {
                    double avgTotal = avgErr * targetSamples + targetSamples;
                    if (clampStreak == 0) clampStartAvgTotal = avgTotal;
                    clampStreak++;
                    if (clampStreak >= ClampWarnBase * n)
                    {
                        bool recovering = avgTotal - clampStartAvgTotal > targetSamples * 0.1;
                        clampStreak = 0;
                        if (!recovering && WarnAllowed(ref lastClampWarnTick))
                            Report("[警告] 时钟校正已触顶 " + PpmOf(r) + " 超过 10 秒且水位未回升 (平均调节水位 " +
                                MsOf(avgTotal) + "ms / 目标 " + MsOf(targetSamples) + "ms)，各设备速率差超出可修正范围");
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

        /// <summary>某成员 holding 状态变化时通知本组。</summary>
        public void NotifyHolding(int memberId, bool isHolding)
        {
            if (memberId < 0 || memberId >= MaxMembers) return;
            lock (sync) holding[memberId] = isHolding;
        }

        /// <summary>组内**其他成员**是否有在 holding — 本成员是否应降低联动增益。
        /// 注意查询范围必须限组内: 独立组恒为 false, 拼装拓扑下别的设备欠载不会把本路降 6dB。</summary>
        public bool OtherHolding(int memberId)
        {
            lock (sync)
            {
                int n = members < 1 ? 1 : members;
                for (int i = 0; i < n; i++)
                    if (i != memberId && holding[i]) return true;
                return false;
            }
        }

        bool WarnAllowed(ref int lastTick)
        {
            int now = Environment.TickCount;
            if (lastTick != 0 && unchecked(now - lastTick) < WarnIntervalMs) return false;
            lastTick = now;
            return true;
        }

        void Report(string msg) { try { if (log != null) log(msg); } catch { } }

        int MsOf(double frames) { return sampleRate > 0 ? (int)(frames * 1000 / sampleRate) : 0; }

        static string PpmOf(double r) { return ((r - 1.0) * 1e6).ToString("+0;-0;0") + "ppm"; }
    }
}
