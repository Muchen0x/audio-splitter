using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace StereoSplitter.Infrastructure
{
    /// <summary>
    /// 未处理异常兜底: 写 crash 日志 + 提示一次。
    /// 旧实现没有注册任何全局处理器, UI 线程一抛异常进程直接消失, 现场无迹可查。
    /// </summary>
    static class CrashReporter
    {
        static int reported = 0;
        static int errorReported = 0;

        /// <summary>最近一次崩溃日志写入失败原因; null 表示至少有一处落盘成功。</summary>
        public static string LastWriteError;
        public static bool Healthy { get { return LastWriteError == null; } }

        public static void Report(Exception ex, string source)
        {
            string body = BuildBody(ex, source);
            string savedPath = null;
            string firstError = null;

            // F-44: 主位置失败时依次尝试次要位置, 仍失败则回退 Windows 事件日志
            System.Collections.Generic.List<string> dirs = CandidateDirs();
            for (int i = 0; i < dirs.Count; i++)
            {
                try
                {
                    Directory.CreateDirectory(dirs[i]);
                    string p = Path.Combine(dirs[i],
                        "crash_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" +
                        Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
                    File.WriteAllText(p, body, Encoding.UTF8);
                    savedPath = p;
                    break;
                }
                catch (Exception wex)
                {
                    if (firstError == null) firstError = wex.GetType().Name + ": " + wex.Message;
                }
            }

            if (savedPath == null)
            {
                bool eventLogged = TryWriteEventLog(body);
                LastWriteError = (firstError ?? "(全部位置均失败)") +
                    (eventLogged ? " [已回退写入 Windows 应用程序事件日志]" : " [事件日志回退也失败]");
                NoteErrorOnce();
            }
            else LastWriteError = null;

            // 只弹一次, 避免同一异常反复触发时提示风暴; 不再谎称"日志已保存"
            if (System.Threading.Interlocked.Exchange(ref reported, 1) == 0)
            {
                try
                {
                    string msg = "程序遇到未处理的错误。";
                    if (savedPath != null) msg += "\n\n日志已保存到:\n" + savedPath;
                    else msg += "\n\n日志写入失败, 崩溃现场可能丢失:\n" + LastWriteError;
                    if (ex != null) msg += "\n\n" + ex.Message;
                    MessageBox.Show(msg, "Audio Splitter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            }
        }

        static string BuildBody(Exception ex, string source)
        {
            var sb = new StringBuilder();
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.AppendLine("来源: " + source);
            sb.AppendLine("版本: " + Application.ProductVersion);
            sb.AppendLine("系统: " + Environment.OSVersion + " / CLR " + Environment.Version);
            sb.AppendLine("工作集: " + (Environment.WorkingSet / 1024 / 1024) + " MB");
            sb.AppendLine();
            sb.AppendLine(ex != null ? ex.ToString() : "(无异常对象)");
            return sb.ToString();
        }

        static System.Collections.Generic.List<string> CandidateDirs()
        {
            var list = new System.Collections.Generic.List<string>();
            try { list.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log")); } catch { }
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(local)) list.Add(Path.Combine(local, "StereoSplitter", "log"));
            }
            catch { }
            try { list.Add(Path.Combine(Path.GetTempPath(), "StereoSplitter", "log")); } catch { }
            return list;
        }

        /// <summary>次要位置也写不进去时回退到 Windows 事件日志; 创建事件源需管理员权限, 失败即放弃。</summary>
        static bool TryWriteEventLog(string body)
        {
            try
            {
                const string src = "StereoSplitter";
                if (!System.Diagnostics.EventLog.SourceExists(src))
                {
                    try { System.Diagnostics.EventLog.CreateEventSource(src, "Application"); }
                    catch { return false; }
                }
                System.Diagnostics.EventLog.WriteEntry(src, body, System.Diagnostics.EventLogEntryType.Error);
                return true;
            }
            catch { return false; }
        }

        static void NoteErrorOnce()
        {
            if (System.Threading.Interlocked.Exchange(ref errorReported, 1) == 0)
            {
                try { Console.Error.WriteLine("[CrashReporter] 崩溃日志写入失败 (仅报告一次): " + LastWriteError); } catch { }
                try { System.Diagnostics.Trace.WriteLine("[CrashReporter] " + LastWriteError); } catch { }
            }
        }
    }
}
