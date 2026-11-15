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
    // 捕获时钟域内的固定延迟线: 数据攒到目标水位后按到达顺序放出
    // 稳态 FIFO 恒等于 target 字节 → 延迟精确恒定, 不引入新时钟漂移
    class DelayLine
    {
        class Block
        {
            public byte[] Data;
            public int Len;
            public int Offset;
        }

        readonly Queue<Block> blocks = new Queue<Block>();
        // 块缓冲复用: 延迟开启时每块原本都要 new byte[len], 稳态后可从池中取回。
        // 用 List 而非 Stack: 需要挑"最小够用块", Stack 弹到过小的块只能丢弃 → 池统计漂移且频繁重分配
        readonly List<byte[]> pool = new List<byte[]>();
        int pooledBytes;
        const int PoolMaxBytes = 1 << 20; // 池上限 1MB, 防止大块数据长期驻留
        readonly int targetBytes;
        readonly Action<byte[], int, int> emit;
        readonly object sync = new object(); // Write(采集线程) 与 Clear(UI线程) 并发保护
        long buffered;

        public DelayLine(int targetBytes, Action<byte[], int, int> emit)
        {
            this.targetBytes = Math.Max(0, targetBytes);
            this.emit = emit;
        }

        public void Write(byte[] data, int len)
        {
            Write(data, 0, len);
        }

        // 带偏移量版本: 级联延迟线 (捕获级 → 左右级) 转发时, 上游 emit 给出的是一块里的片段,
        // 从 0 开始拷会把片段之前的字节一起带下来 → 必须按 offset 拷
        public void Write(byte[] data, int offset, int len)
        {
            if (len <= 0) return;
            lock (sync)
            {
                // 快路径 (默认延迟0的常态): 不复制不入队直接投递
                // emit 同步消费 (AddSamples 立即拷入缓冲), e.Buffer 在回调返回前用完 → 生命周期安全
                if (targetBytes == 0 && buffered == 0)
                {
                    emit(data, offset, len);
                    return;
                }

                byte[] copy = null;
                // 挑最小的"够用"块: 过小的块留在池里等下次更长需求, 不再丢弃
                int best = -1;
                for (int pi = 0; pi < pool.Count; pi++)
                    if (pool[pi].Length >= len && (best < 0 || pool[pi].Length < pool[best].Length)) best = pi;
                if (best >= 0)
                {
                    copy = pool[best];
                    pool.RemoveAt(best);
                    pooledBytes -= copy.Length;
                }
                if (copy == null) copy = new byte[len];
                Buffer.BlockCopy(data, offset, copy, 0, len);
                blocks.Enqueue(new Block { Data = copy, Len = len });
                buffered += len;

                while (buffered > targetBytes && blocks.Count > 0)
                {
                    var b = blocks.Peek();
                    int avail = b.Len - b.Offset;
                    int take = (int)Math.Min(avail, buffered - targetBytes);
                    // emit 抛异常时整块丢弃: 否则该块永远卡在队头, 之后每次 Write 都在同一处失败,
                    // 队列只进不出 → 长时间运行内存无界增长
                    if (EmitSafe(b.Data, b.Offset, take)) b.Offset += take;
                    else b.Offset = b.Len;
                    buffered -= take;
                    if (b.Offset >= b.Len)
                    {
                        blocks.Dequeue();
                        if (pooledBytes + b.Data.Length <= PoolMaxBytes)
                        {
                            pool.Add(b.Data);
                            pooledBytes += b.Data.Length;
                        }
                    }
                }
            }
        }

        bool EmitSafe(byte[] data, int offset, int len)
        {
            try { emit(data, offset, len); return true; }
            catch { return false; }
        }

        public void Clear()
        {
            lock (sync)
            {
                while (blocks.Count > 0)
                {
                    var b = blocks.Dequeue();
                    if (pooledBytes + b.Data.Length <= PoolMaxBytes)
                    {
                        pool.Add(b.Data);
                        pooledBytes += b.Data.Length;
                    }
                }
                buffered = 0;
            }
        }
    }
}
