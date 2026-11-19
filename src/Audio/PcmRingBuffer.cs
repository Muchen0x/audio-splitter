using System;
using NAudio.Wave;

namespace StereoSplitter
{
    /// <summary>
    /// <see cref="BufferedWaveProvider"/> 的线程安全封装。
    ///
    /// 为什么必须加锁: NAudio 的 BufferedWaveProvider 内部就是一个无锁的环形数组
    /// (bytesRead / bytesWritten 两个游标), 从来没有声明过线程安全。而本工程有**三条线程**
    /// 同时碰它:
    ///   - 捕获线程: Consume() → AddSamples (写)
    ///   - 输出线程: DriftCorrectingProvider.Fill() → Read (读), 每台设备一条
    ///   - UI/启动线程: TrimToFill() → Read (读), BufferedBytes (读游标)
    /// 两读一写无锁混跑会让游标和计数撕裂 —— 轻则水位读数乱跳、少读到一整块(听感是"顿一下"),
    /// 重则读到被覆写一半的数据(爆音)。日志里那些"水位毫无征兆从 60ms 跳到 180ms"就有它一份。
    ///
    /// 锁的粒度是"一次字节搬运", 临界区只有一次 memcpy (一个回调块约 1~10ms 音频, 几 KB),
    /// 音频线程不会被长期阻塞; 相比撕裂带来的断音, 这点开销可以忽略。
    ///
    /// 短读语义 (ReadFully = false): Read 返回**实际读到的字节数**, 可能为 0 或小于请求量,
    /// 且绝不在目标数组尾部补零。两个消费方都依赖这个契约:
    ///   - DriftCorrectingProvider.Fill() 用 got/(4*ch) 当小环帧数, 补零会被当成真实音频帧
    ///     灌进小环, 水位/欠载统计与全部延迟调参随之失真;
    ///   - Discard() 用 got<=0 判空、用累计 got 记账削掉的量。
    /// 输出端最终需要的补零由 DriftCorrectingProvider 的 holding 软收尾负责, 不在这里做。
    /// </summary>
    sealed class PcmRingBuffer
    {
        readonly object sync = new object();
        readonly BufferedWaveProvider inner;
        readonly byte[] scratch = new byte[8192];   // Discard 用, 仅持有锁时访问

        public PcmRingBuffer(WaveFormat format, int durationMs)
        {
            inner = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(durationMs),
                DiscardOnBufferOverflow = true,
                // NAudio 2.2.1 默认为 true: 数据不足时把剩余部分 Array.Clear 补零, 并返回
                // 请求的完整长度。这会让"实际读取量"永远等于请求量, 上面的短读契约失效。
                ReadFully = false
            };
        }

        public WaveFormat WaveFormat { get { return inner.WaveFormat; } }

        /// <summary>缓存容量 (字节)。构造后不变, 无需加锁。</summary>
        public int BufferLength { get { return inner.BufferLength; } }

        public int BufferedBytes
        {
            get { lock (sync) return inner.BufferedBytes; }
        }

        public void AddSamples(byte[] data, int offset, int count)
        {
            if (count <= 0) return;
            lock (sync) inner.AddSamples(data, offset, count);
        }

        /// <summary>返回实际读出的字节数 (0 = 空缓冲)。不会在 buffer 尾部补零。</summary>
        public int Read(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return 0;
            lock (sync) return inner.Read(buffer, offset, count);
        }

        /// <summary>
        /// 丢弃最旧的 count 字节 (不超过当前存量), 返回实际丢弃的字节数。
        /// 用于缓冲堆积后的就地削峰 —— 丢掉一小段旧音频, 远好于拆掉整条链路重建。
        /// </summary>
        public int Discard(int count)
        {
            if (count <= 0) return 0;
            lock (sync)
            {
                // 契约: 丢弃量不得超过当前存量, 返回值必须是真丢掉的字节数。
                // 即便 ReadFully=false 已让短读如实返回, 仍要先夹一次 —— TrimOverfill 会把
                // 这个返回值折算成削掉的帧数, 并把 smoothedTotal 直接对齐到 keep; 一旦记账
                // 虚增, 真实水位和"以为的水位"就会分叉, 校正环与看门狗判据都跟着失真。
                int want = Math.Min(count, inner.BufferedBytes);
                if (want <= 0) return 0;

                int dropped = 0;
                while (dropped < want)
                {
                    int chunk = Math.Min(want - dropped, scratch.Length);
                    int got = inner.Read(scratch, 0, chunk);
                    if (got <= 0) break;
                    dropped += got;
                }
                return dropped;
            }
        }

        public void Clear()
        {
            lock (sync) inner.ClearBuffer();
        }
    }
}
