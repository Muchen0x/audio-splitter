using System;
using System.Collections.Generic;
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
        int errorReported = 0;

        /// <summary>F-44: 最近一次日志写入/刷盘失败原因; null = 健康。</summary>
        public string LastWriteError;
        public bool Healthy { get { return LastWriteError == null; } }

        public string FilePath
        {
            get { return filePath; }
        }

        /// <summary>写失败留痕并至少向诊断输出报告一次, 不再静默吞掉。</summary>
        void NoteWriteError(string where, Exception ex)
        {
            LastWriteError = where + ": " + ex.GetType().Name + ": " + ex.Message;
            if (Interlocked.Exchange(ref errorReported, 1) == 0)
            {
                try { Console.Error.WriteLine("[SessionLog] 日志写入失败, 后续日志可能不完整: " + LastWriteError); } catch { }
                try { System.Diagnostics.Trace.WriteLine("[SessionLog] " + LastWriteError); } catch { }
            }
        }

        public SessionLog(string directory)
        {
            Directory.CreateDirectory(directory);
            // R-06/F-44: 文件名加毫秒 + GUID, 避免同秒多实例打开同一文件互相追加
            filePath = Path.Combine(directory, "session_" +
                DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff") + "_" +
                Guid.NewGuid().ToString("N") + ".log");
            PruneOldLogs(directory);
            writer = new StreamWriter(filePath, false);   // 名字已唯一, 不再追加打开
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
            catch (Exception ex) { NoteWriteError("FlushTick", ex); }
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
            catch (Exception ex) { NoteWriteError("Write", ex); }
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
                        // 截断后必须把写指针拉回 0: 否则新内容写在旧偏移处, 文件前部留一片 NUL,
                        // 用文本工具打开就是"日志前面莫名其妙一堆空白/乱码"
                        writer.BaseStream.Position = 0;
                    }
                }
                if (writer == null && File.Exists(filePath))
                {
                    File.WriteAllText(filePath, "");
                }
            }
            catch (Exception ex) { NoteWriteError("Clear", ex); }
        }

        public static LogLevel Classify(string message)
        {
            if (message == null) return LogLevel.Info;
            // 先剥掉开头的路标记: 多路 provider 的行是 "[左] [警告] 缓冲水位偏低…",
            // 只认行首 "[警告]" 会把这类关键告警当 INFO, 崩溃前最多丢 2 秒 (批量刷盘窗口)。
            string s = message;
            for (int guard = 0; guard < 4; guard++)
            {
                if (s.Length < 3 || s[0] != '[') break;
                int close = s.IndexOf(']');
                if (close <= 1 || close > 24) break;
                string tag = s.Substring(1, close - 1).Trim();
                // F-36: Tag() 生成的是 "WARNING"/"ERROR", 用 OrdinalIgnoreCase 覆盖完整形式
                // 与大小写变体, 保证 Classify("[" + Tag(level) + "] ...") 往返正确。
                if (string.Equals(tag, "错误", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "ERROR", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "ERR", StringComparison.OrdinalIgnoreCase))
                    return LogLevel.Error;
                if (string.Equals(tag, "警告", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "WARN", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag, "WARNING", StringComparison.OrdinalIgnoreCase))
                    return LogLevel.Warning;
                s = s.Substring(close + 1).TrimStart();
            }
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

        /// <summary>删 7 天前的, 再把总数压到 100 以内。
        /// F-44/R-06: 清理仅限 session_*.log 与 crash_*.log; 跳过本会话活动日志与
        /// 仍被其它进程占用的文件, 避免删掉正在写的日志。</summary>
        void PruneOldLogs(string logDir)
        {
            try
            {
                var dir = new DirectoryInfo(logDir);
                DateTime cutoff = DateTime.Now.AddDays(-7);
                FileInfo[] files = LogFiles(dir);
                for (int i = 0; i < files.Length; i++)
                    if (files[i].LastWriteTime < cutoff) TryDeleteIfFree(files[i], filePath);

                files = LogFiles(dir);
                if (files.Length > 100)
                {
                    Array.Sort(files, delegate(FileInfo a, FileInfo b) { return a.LastWriteTime.CompareTo(b.LastWriteTime); });
                    for (int i = 0; i < files.Length - 100; i++) TryDeleteIfFree(files[i], filePath);
                }
            }
            catch (Exception ex) { NoteWriteError("PruneOldLogs", ex); }
        }

        static FileInfo[] LogFiles(DirectoryInfo dir)
        {
            var list = new List<FileInfo>();
            list.AddRange(dir.GetFiles("session_*.log"));
            list.AddRange(dir.GetFiles("crash_*.log"));
            return list.ToArray();
        }

        /// <summary>只删未被占用的日志: FileShare.None 试探, 被占用/只读则保留。</summary>
        static void TryDeleteIfFree(FileInfo fi, string activePath)
        {
            if (string.Equals(fi.FullName, activePath, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    fs.Flush();   // 确认句柄可用, 同时避免"声明未使用"告警
                }
                fi.Delete();
            }
            catch { /* 被占用或无权限: 保留, 不算日志写入失败 */ }
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
            catch (Exception ex) { NoteWriteError("Dispose", ex); }
        }
    }
}
