using System;
using System.Diagnostics;
using System.Threading;

namespace StereoSplitter.Infrastructure
{
    /// <summary>
    /// "预期内可吞"的异常出口。
    ///
    /// 全仓 130 多处 <c>catch { }</c> 里绝大多数是合理的 (COM 拆除、日志兜底、窗口已销毁时的
    /// <c>BeginInvoke</c>), 但"合理吞掉"和"吞掉了一个 bug"在日志上长得一模一样 —— 排障时
    /// 无法区分。这里只做三件极轻的事: 计数、DEBUG 下写 <see cref="Trace"/>、留最后一条现场,
    /// 会话结束时汇总成一行。**任何方法都不许抛异常**, 它本身就处在错误处理路径上。
    /// </summary>
    static class Swallow
    {
        static int _count;
        static string _lastWhere;
        static string _lastMessage;

        /// <summary>进程启动至今被记下次数 (拆除类不计数, 见 <see cref="Dispose"/>)。</summary>
        public static int Count { get { return Volatile.Read(ref _count); } }

        /// <summary>
        /// 非拆除路径用: 记录一次"本不该发生但发生了"的异常。
        /// 枚举/查询/状态组装这类"失败了也能继续跑, 但结果会失真"的地方该用它。
        /// </summary>
        public static void Note(string where, Exception ex)
        {
            Interlocked.Increment(ref _count);
            _lastWhere = where;
            _lastMessage = ex == null ? "(null)" : ex.GetType().Name + ": " + ex.Message;
#if DEBUG
            try { Trace.WriteLine("[吞] " + where + " → " + _lastMessage); } catch { }
#endif
        }

        /// <summary>
        /// 拆除路径用: 与原来的 <c>catch { }</c> 完全等价 (静默、不计数) —— 关闭/崩溃路径上
        /// 记日志只会制造二次异常。写成方法只是为了把"我知道这里在吞"显式化。
        /// </summary>
        public static void Dispose(IDisposable d)
        {
            if (d == null) return;
            try { d.Dispose(); } catch { }
        }

        /// <summary>会话结束时汇总成一行; 一次都没有则返回 null (不占日志行)。</summary>
        public static string Summary()
        {
            int n = Count;
            if (n == 0) return null;
            return "本次会话吞掉 " + n + " 次非拆除类异常 (最近: " +
                   (_lastWhere ?? "?") + " → " + (_lastMessage ?? "?") + ")";
        }
    }
}
