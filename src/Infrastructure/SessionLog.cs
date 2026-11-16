using System;
using System.IO;
using System.Threading;

namespace StereoSplitter.Infrastructure
{
    enum LogLevel
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// 会话日志文件: 每次启动一个 session 文件, 行格式 HH:mm:ss.fff [LEVEL] 内容。
    /// 只负责落盘与清理, UI 展示由调用方处理 (原实现把两件事混在 MainForm.Log 里)。
    /// 刷盘策略: AutoFlush 关闭, INFO 行由 2s 定时器批量落盘 (P2-8);
    /// ERROR/WARNING 立即落盘 —— 崩溃前的排障信息不允许丢。
    /// </summary>
    sealed class SessionLog : IDisposable
    {
        StreamWriter writer;
        readonly string filePath;
        readonly object writeLock = new object();
        Timer flushTimer;

        public string FilePath
        {
            get { return filePath; }
        }

        public SessionLog(string directory)
        {
            Directory.CreateDirectory(directory);
            PruneOldLogs(directory);
            filePath = Path.Combine(directory, "session_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".log");
            writer = new StreamWriter(filePath, true);
            writer.AutoFlush = false;
            // 批量刷盘: Info 行最多滞后 2s 落盘; 定时器回调与本类所有写路径共用 writeLock
            flushTimer = new Timer(delegate { FlushTick(); }, null, 2000, 2000);
        }

        void FlushTick()
        {
            try
            {
                lock (writeLock)
                {
                    if (writer != null) writer.Flush();
                }
            }
            catch { }
        }

        /// <summary>按调用方给的时间戳写一行 (UI 与文件共用同一个时间戳, 不各取一次 Now)。
        /// Write 会被多线程调用 (UI 线程 marshal 之外, 日志框未建/已释放时治理线程直写), 全程持锁。</summary>
        public void Write(string timestamp, LogLevel level, string message)
        {
            try
            {
                lock (writeLock)
                {
                    if (writer == null) return;
                    writer.WriteLine(timestamp + " [" + Tag(level) + "] " + message);
                    if (level != LogLevel.Info) writer.Flush(); // 错误/警告立即落盘
                }
            }
            catch { }
        }

        public void Write(LogLevel level, string message)
        {
            Write(DateTime.Now.ToString("HH:mm:ss.fff"), level, message);
        }

        /// <summary>清空当前会话日志 (UI 上点"清除日志"时同步)。</summary>
        public void Clear()
        {
            try
            {
                lock (writeLock)
                {
                    if (writer != null)
                    {
                        writer.Flush();
                        writer.BaseStream.SetLength(0);
                    }
                }
                if (writer == null && File.Exists(filePath))
                {
                    File.WriteAllText(filePath, "");
                }
            }
            catch { }
        }

        public static LogLevel Classify(string message)
        {
            if (message == null) return LogLevel.Info;
            if (message.StartsWith("[错误]")) return LogLevel.Error;
            if (message.StartsWith("[警告]")) return LogLevel.Warning;
            return LogLevel.Info;
        }

        public static string Tag(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Error: return "ERROR";
                case LogLevel.Warning: return "WARNING";
                default: return "INFO";
            }
        }

        /// <summary>删 7 天前的, 再把总数压到 100 以内 (防 log/ 无限累积)。
        /// 覆盖 session_*.log 与 crash_*.log: 崩溃日志同样会无限累积。</summary>
        static void PruneOldLogs(string logDir)
        {
            try
            {
                var dir = new DirectoryInfo(logDir);
                var files = dir.GetFiles("*.log");
                DateTime cutoff = DateTime.Now.AddDays(-7);
                foreach (var fi in files)
                    if (fi.LastWriteTime < cutoff) { try { fi.Delete(); } catch { } }

                files = dir.GetFiles("*.log");
                if (files.Length > 100)
                {
                    Array.Sort(files, delegate(FileInfo a, FileInfo b) { return a.LastWriteTime.CompareTo(b.LastWriteTime); });
                    for (int i = 0; i < files.Length - 100; i++)
                    { try { files[i].Delete(); } catch { } }
                }
            }
            catch { }
        }

        public void Dispose()
        {
            // 先停定时器再关流: 之后 tick 里 writer 判空直接返回, 无并发窗口
            if (flushTimer != null)
            {
                try { flushTimer.Dispose(); } catch { }
                flushTimer = null;
            }
            if (writer == null) return;
            try
            {
                lock (writeLock)
                {
                    writer.Dispose(); // StreamWriter.Dispose 内部先 Flush, 收尾日志不丢
                    writer = null;
                }
            }
            catch { }
        }
    }
}
