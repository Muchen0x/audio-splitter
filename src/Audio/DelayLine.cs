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
        int targetBytes;
        readonly Action<byte[], int, int> emit;
        readonly object sync = new object(); // Write(采集线程) 与 Clear/SetTarget(UI线程) 并发保护
        long buffered;

        public DelayLine(int targetBytes, Action<byte[], int, int> emit)
        {
            this.targetBytes = Math.Max(0, targetBytes);
            this.emit = emit;
        }

        /// <summary>当前目标字节数 (= 稳态时恒定的 FIFO 深度)。</summary>
        public int TargetBytes { get { lock (sync) return targetBytes; } }

        /// <summary>
        /// 运行中更换延迟目标 (字节, 必须按整帧对齐)。
        /// <para>增大: 什么也不用做 —— 下一次 Write 时 buffered 还没到新目标, 数据先攒在
        /// 队列里不往下游放, 下游因此"停一拍", 净效果就是这段延迟被补上。</para>
        /// <para>减小: 立刻丢掉**最旧**的超出部分, 而不是等下一次 Write 一次性倒给下游。
        /// 倒出去会让下游缓冲水位瞬间超出目标 2 倍, 触发它的"堆积削峰"并打一条告警 ——
        /// 明明是用户主动改延迟。就地丢最旧的语义与"减少延迟 = 跳过一小段"完全一致,
        /// 下游看到的只是一次正常的数据间隙。</para>
        /// <para>调用方可持有 feedSync 以保证不与在途 Write 交错; 内部仍加自己的锁。</para>
        /// </summary>
        public void SetTarget(int bytes)
        {
            int t = Math.Max(0, bytes);
            lock (sync)
            {
                if (t == targetBytes) return;
                bool shrinking = t < targetBytes;
                targetBytes = t;
                if (shrinking) TrimOldestLocked();
            }
        }

        /// <summary>丢弃最旧的 buffered − targetBytes, 让存量立刻等于新目标 (持 sync 调用)。</summary>
        void TrimOldestLocked()
        {
            while (buffered > targetBytes && blocks.Count > 0)
            {
                Block b = blocks.Peek();
                int avail = b.Len - b.Offset;
                int over = (int)(buffered - targetBytes);
                int take = avail < over ? avail : over;
                b.Offset += take;
                buffered -= take;
                if (b.Offset >= b.Len)
                {
                    blocks.Dequeue();
                    RecycleLocked(b.Data);
                }
            }
        }

        void RecycleLocked(byte[] data)
        {
            if (data == null) return;
            if (pooledBytes + data.Length <= PoolMaxBytes)
            {
                pool.Add(data);
                pooledBytes += data.Length;
            }
        }

        public void Write(byte[] data, int len)
        {
            if (len <= 0) return;
            lock (sync)
            {
                // 快路径 (默认延迟0的常态): 不复制不入队直接投递
                // emit 同步消费 (AddSamples 立即拷入缓冲), e.Buffer 在回调返回前用完 → 生命周期安全
                if (targetBytes == 0 && buffered == 0)
                {
                    emit(data, 0, len);
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
                Buffer.BlockCopy(data, 0, copy, 0, len);
                blocks.Enqueue(new Block { Data = copy, Len = len });
                buffered += len;

                while (buffered > targetBytes && blocks.Count > 0)
                {
                    var b = blocks.Peek();
                    int avail = b.Len - b.Offset;
                    int take = (int)Math.Min(avail, buffered - targetBytes);
                    // emit 抛异常时整块丢弃: 否则该块永远卡在队头, 之后每次 Write 都在同一处失败,
                    // 队列只进不出 → 长时间运行内存无界增长
                    if (EmitSafe(b.Data, b.Offset, take))
                    {
                        b.Offset += take;
                        buffered -= take;
                    }
                    else
                    {
                        // 失败时丢的是**整块剩余**(不只是本次 take): 记账必须同步扣掉整段,
                        // 否则 buffered 永久大于真实存量 → 延迟线实际长度悄悄变短, 而水位读数还正常
                        buffered -= (b.Len - b.Offset);
                        b.Offset = b.Len;
                    }
                    if (b.Offset >= b.Len)
                    {
                        blocks.Dequeue();
                        RecycleLocked(b.Data);
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
                    RecycleLocked(b.Data);
                }
                buffered = 0;
            }
        }
    }
}
