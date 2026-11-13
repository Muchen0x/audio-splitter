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

        public static void Report(Exception ex, string source)
        {
            string path = null;
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "crash_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".log");

                var sb = new StringBuilder();
                sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                sb.AppendLine("来源: " + source);
                sb.AppendLine("版本: " + Application.ProductVersion);
                sb.AppendLine("系统: " + Environment.OSVersion + " / CLR " + Environment.Version);
                sb.AppendLine("工作集: " + (Environment.WorkingSet / 1024 / 1024) + " MB");
                sb.AppendLine();
                sb.AppendLine(ex != null ? ex.ToString() : "(无异常对象)");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }

            // 只弹一次, 避免同一异常反复触发时提示风暴
            if (System.Threading.Interlocked.Exchange(ref reported, 1) == 0)
            {
                try
                {
                    string msg = "程序遇到未处理的错误。";
                    if (path != null) msg += "\n\n日志已保存到:\n" + path;
                    if (ex != null) msg += "\n\n" + ex.Message;
                    MessageBox.Show(msg, "Stereo Splitter", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            }
        }
    }
}
