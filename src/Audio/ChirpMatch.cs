using System;

namespace StereoSplitter
{
    /// <summary>全段匹配滤波的结果。</summary>
    sealed class FullChirpHit
    {
        public bool Found;
        /// <summary>扫频起点在信号里的位置 (采样, 含小数 → 亚采样精度)。</summary>
        public double Offset;
        /// <summary>主峰的归一化余弦相似度 (0~1, 与极性无关)。判"到底像不像扫频"用它。</summary>
        public double Score;
        /// <summary>主峰 / 整段相关序列的均方根。它是"这个峰有多突出"的度量, 与响度无关。</summary>
        public double Psr;
        /// <summary>是否取到了主峰之前的独立前置峰 (直达声先于反射)。</summary>
        public bool DirectPreferred;
    }

    /// <summary>
    /// 全段匹配滤波 —— 在**整段**信号上做一次 FFT 互相关, 不去猜"声音从哪儿开始"。
    ///
    /// <para><b>为什么整段滑一遍, 不做粗定位</b>: 校准在音乐播放中进行, 扫频是叠在音乐上出去的。
    /// 任何"先找声音从哪儿开始"的捷径都会失效 —— 滑动包络的峰值是音乐, 按峰值百分比取的阈值
    /// 会锁在音乐最响的地方, 挑出的窄窗甚至可能完全不含扫频。所以这里不猜, 整段匹配一遍。</para>
    ///
    /// <para><b>为什么用 FFT 而不是直接滑</b>: 1 秒 × 48kHz 的段配 120ms 模板, 朴素做法是
    /// 45600 × 5760 ≈ 2.6 亿次乘加, 单段就要一两秒; 零填充到 65536 后做 3 次 FFT 约 300 万次蝶形,
    /// 单段十几毫秒。这不是优化, 是可行性问题。</para>
    ///
    /// <para><b>归一化怎么做</b>: 频域只能给出**未归一化**的 dot[k] = Σ seg[k+i]·tmpl[i]。
    /// 但窗能量 Σ seg[k+i]² 可以用前缀和 O(1) 求出来, 于是
    /// score[k] = dot[k] / (√E[k] · ‖tmpl‖) 就是精确的余弦相似度 —— 与音量无关,
    /// 音乐响不响不影响"像不像扫频"这个判断。</para>
    /// </summary>
    static class ChirpMatch
    {
        /// <summary>
        /// 窗能量低于全段最大值的这个比例时, 不参与判峰。
        /// <para>理由: 近乎静音的窗里 dot 和 √E 都是噪声量级, 相除会炸出一个虚假的高分
        /// (分母趋零), 抢走主峰。−40dB 的窗对"有没有扫频"本来也没有信息量。</para>
        /// </summary>
        const double EnergyFloorRatio = 1e-4;

        /// <summary>在整段 <paramref name="seg"/> 里定位 <paramref name="tmpl"/> 的起点。</summary>
        public static FullChirpHit Locate(float[] seg, float[] tmpl)
        {
            var miss = new FullChirpHit { Found = false };
            if (seg == null || tmpl == null) return miss;
            int n = seg.Length, m = tmpl.Length;
            if (m < 32 || n < m + 64) return miss;

            double tmplNorm = 0;
            for (int i = 0; i < m; i++) tmplNorm += (double)tmpl[i] * tmpl[i];
            tmplNorm = Math.Sqrt(tmplNorm);
            if (tmplNorm <= 1e-9) return miss;

            // 窗能量用前缀和: E[k] = pre[k+m] - pre[k]。先算, 顺便得到全段最大窗能量。
            var pre = new double[n + 1];
            for (int i = 0; i < n; i++) { double s = seg[i]; pre[i + 1] = pre[i] + s * s; }

            int last = n - m;                       // 有效偏移 0..last
            double maxE = 0;
            for (int k = 0; k <= last; k++)
            {
                double e = pre[k + m] - pre[k];
                if (e > maxE) maxE = e;
            }
            if (maxE <= 1e-12) return miss;          // 整段近似静音
            double eMin = maxE * EnergyFloorRatio;

            // 零填充到 >= n+m-1: 循环相关的后半段会绕回开头, 不零填充就会污染靠前的偏移。
            int len = NextPow2(n + m);
            var sr = new double[len];
            var si = new double[len];
            var tr = new double[len];
            var ti = new double[len];
            for (int i = 0; i < n; i++) sr[i] = seg[i];
            for (int i = 0; i < m; i++) tr[i] = tmpl[i];

            Fft(sr, si, false);
            Fft(tr, ti, false);
            // S · conj(T):  (sr+i·si)(tr−i·ti) = (sr·tr + si·ti) + i(si·tr − sr·ti)
            for (int i = 0; i < len; i++)
            {
                double re = sr[i] * tr[i] + si[i] * ti[i];
                double im = si[i] * tr[i] - sr[i] * ti[i];
                sr[i] = re;
                si[i] = im;
            }
            Fft(sr, si, true);                       // sr[k] = dot[k]

            var dots = new double[last + 1];
            var ncc = new double[last + 1];
            double sumSq = 0;
            for (int k = 0; k <= last; k++)
            {
                double d = sr[k];
                dots[k] = d;
                double e = pre[k + m] - pre[k];
                double v = e > eMin ? d / (Math.Sqrt(e) + 1e-12) : 0.0;
                ncc[k] = v;
                sumSq += v * v;
            }
            double rms = Math.Sqrt(sumSq / (last + 1));

            int bestAt = -1;
            double bestAbs = 0;
            for (int k = 0; k <= last; k++)
            {
                double a = Math.Abs(ncc[k]);
                if (a > bestAbs) { bestAbs = a; bestAt = k; }
            }
            if (bestAt < 0 || bestAbs <= 0) return miss;

            int pick = PickDirect(dots, 0, last, bestAt, m);

            double sub = 0;
            if (pick > 0 && pick < last)
            {
                double sign = ncc[pick] < 0 ? -1.0 : 1.0;
                double y0 = ncc[pick - 1] * sign;
                double y1 = ncc[pick] * sign;
                double y2 = ncc[pick + 1] * sign;
                double den = y0 - 2 * y1 + y2;
                if (Math.Abs(den) > 1e-12)
                {
                    sub = 0.5 * (y0 - y2) / den;
                    if (sub > 1) sub = 1; else if (sub < -1) sub = -1;
                }
            }

            return new FullChirpHit
            {
                Found = true,
                Offset = pick + sub,
                // 置信度始终取**主峰**的: 即使最终采用了前置的直达峰, "这段里有没有扫频"
                // 仍应由最强那个峰回答 (前置峰可能只是直达声的弱一半)。
                Score = bestAbs / tmplNorm,
                Psr = rms > 1e-12 ? bestAbs / rms : 0,
                DirectPreferred = pick != bestAt
            };
        }

        /// <summary>
        /// 在 <paramref name="bestAt"/> 之前找一个**独立的**显著峰, 找到就返回它 (直达声先到)。
        ///
        /// <para>判据不是阈值而是"有没有谷": 从主峰往前走到 |dot| 掉到主峰 25% 以下 (那就是两峰之间的谷),
        /// 再看谷之前——也就是 [bestAt−m, bestAt−gap) 区间——的最高点; 它若仍达到主峰的 45%, 就是独立前置峰。
        /// 旁瓣与主峰之间没有谷, 所以这个判据天然不会把旁瓣当成直达声。</para>
        ///
        /// <para>用未归一化的 dot 而不是 score: 反射更强时归一化的分母会把直达主瓣吃掉,
        /// 直达峰反而被埋掉; dot 是线性叠加, 先到的那个一定先抬起来。</para>
        /// </summary>
        /// <param name="dots">未归一化相关序列。</param>
        /// <param name="from">有效范围起点 (含)。</param>
        /// <param name="to">有效范围终点 (含)。</param>
        /// <param name="bestAt">主峰下标。</param>
        /// <param name="m">模板长度, 决定最多往前回溯多远。</param>
        public static int PickDirect(double[] dots, int from, int to, int bestAt, int m)
        {
            if (dots == null) return bestAt;
            double maxDot = 0;
            for (int i = from; i <= to; i++) { double a = Math.Abs(dots[i]); if (a > maxDot) maxDot = a; }
            if (maxDot <= 0) return bestAt;

            // 只在主峰前 5ms 以外找候选: 主瓣肩和第一旁瓣都挤在峰值 ±0.3ms 内,
            // 隔开 5ms 就不可能把它们误认为"另一个声源"; 而直达与一次反射通常相隔几毫秒到几十毫秒。
            int gap = Math.Max(8, m / 12);
            int lo = Math.Max(from, bestAt - m);
            int hi = bestAt - gap;
            if (hi <= lo) return bestAt;

            int j = -1;
            double bj = 0;
            for (int k = lo; k < hi; k++)
            {
                double a = Math.Abs(dots[k]);
                if (a > bj) { bj = a; j = k; }
            }
            if (j < 0 || bj < maxDot * 0.45) return bestAt;

            // 顺着该峰的上升沿走到峰顶 (上限 1/4 模板长, 免得跨到别的声源上)
            int lim = Math.Min(to, j + m / 4);
            while (j < lim && Math.Abs(dots[j + 1]) >= Math.Abs(dots[j])) j++;
            return j;
        }

        static int NextPow2(int v)
        {
            int p = 1;
            while (p < v) p <<= 1;
            return p;
        }

        /// <summary>
        /// 原地基-2 复数 FFT。位反转 + 三层循环, 旋转因子逐级递推。
        /// <para>递推的相位误差在 65536 点上是 1e-12 量级 —— 对"定位一个毫秒级的包络峰"完全无感,
        /// 不值得为它引入查表和三角函数表。</para>
        /// </summary>
        static void Fft(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j &= ~bit;
                j |= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1;
                double ang = 2.0 * Math.PI / len * (inverse ? 1.0 : -1.0);
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1.0, ci = 0.0;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = a + half;
                        double xr = re[b] * cr - im[b] * ci;
                        double xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr;
                        im[b] = im[a] - xi;
                        re[a] += xr;
                        im[a] += xi;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }

            if (inverse)
                for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }
    }
}
