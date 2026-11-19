using System;
using System.Threading;

namespace StereoSplitter
{
    /// <summary>
    /// F-03: 重建 / 释放共用的生命周期门。
    ///
    /// 语义只有一条: 一次"在途操作"(单路重建、整组重建、修剪)全程独占 sync,
    /// 而 Shutdown 必须取同一把锁才能释放资源 —— 拿到锁即证明在途操作已离开临界区,
    /// 所以释放与重建永远不会交错。
    ///
    /// 两个容易做错的细节:
    ///   1. 取消标志必须在**取锁之前**置位。若在途 body 正在锁内等预缓冲, 它靠
    ///      IsShutdown / cancelled 每 5ms 决定是否退出; 若 Shutdown 先卡在锁外而不置位,
    ///      body 会把整个等待超时跑满, 关窗就被拖住几秒。
    ///   2. "宣告关闭"与"执行一次释放"必须是一次原子认领。两个线程同时 Dispose 时,
    ///      只有第一个能认领成功, 第二个直接返回, 不会重复释放 COM 端点。
    /// </summary>
    sealed class LifecycleGate
    {
        readonly object sync = new object();
        int shutdownClaimed;               // 0=存活, 1=已被某个 Shutdown 认领
        volatile bool shutdown;            // 锁外可读: 在途操作的取消检查
        volatile int inFlight;             // 当前在临界区内的操作数 (诊断 / 自检)

        /// <summary>已宣告关闭。在途操作读它来决定是否提前退出等待循环。</summary>
        public bool IsShutdown { get { return shutdown; } }
        public int InFlight { get { return inFlight; } }

        /// <summary>
        /// 在生命周期锁内执行一次操作。已关闭时不执行 body, 返回 rejected() ——
        /// 这保证"迟到的重建"不会把新管线挂到已经释放的对象上。
        /// </summary>
        public T Run<T>(Func<T> body, Func<T> rejected)
        {
            lock (sync)
            {
                if (shutdown) return rejected();
                Interlocked.Increment(ref inFlight);
                try { return body(); }
                finally { Interlocked.Decrement(ref inFlight); }
            }
        }

        /// <summary>Action 版: 返回 false = 已关闭, body 未执行。</summary>
        public bool Run(Action body)
        {
            return Run(delegate { body(); return true; }, delegate { return false; });
        }

        /// <summary>
        /// 关闭生命周期: 先在锁外原子认领并置位(唤醒在途取消检查), 再取锁执行唯一一次释放。
        /// 返回 false = 已经关过 (调用方据此跳过重复释放)。
        /// </summary>
        public bool Shutdown(Action release)
        {
            if (Interlocked.Exchange(ref shutdownClaimed, 1) != 0) return false;
            shutdown = true;               // 必须早于 lock(sync): 见类注释 1
            lock (sync)
            {
                if (release != null) release();
            }
            return true;
        }
    }
}
