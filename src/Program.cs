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
    class Program
    {
        internal const string MutexName = "StereoSplitter_GUI_SingleInstance";
        internal const string ActivateEventName = "Local\\StereoSplitter_Activate";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int dwProcessId);

        // ASFW_ANY: 把"允许抢前台"的许可发给本会话里的任意进程
        const int ASFW_ANY = -1;

        [STAThread]
        static void Main()
        {
            // 单文件发布: 同目录无 NAudio dll 时从嵌入资源加载
            // (同目录有 dll 时 .NET probing 优先, 开发环境行为不变)
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    string name = new AssemblyName(args.Name).Name + ".dll";
                    if (name.StartsWith("NAudio", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                        {
                            if (s != null)
                            {
                                var ms = new MemoryStream();
                                s.CopyTo(ms);
                                return Assembly.Load(ms.ToArray());
                            }
                        }
                    }
                }
                catch { }
                return null;
            };

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // 已有实例: 先把前台权限转授出去, 再发激活通知, 然后退出。
                    // Windows 前台锁定只允许"当前前台进程"把窗口提到最前, 第二个实例此刻是前台,
                    // 但如果只是 Set() 一下就退出, 许可会随进程消失 → 已有实例的 SetForegroundWindow
                    // 被系统拒绝, 表现为任务栏按钮闪一下、窗口仍在后面。ASFW_ANY 把许可显式留在会话里。
                    try { AllowSetForegroundWindow(ASFW_ANY); } catch { }
                    try
                    {
                        using (var evt = EventWaitHandle.OpenExisting(ActivateEventName))
                        {
                            evt.Set();
                        }
                    }
                    catch { }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // 全局兜底: UI 线程与后台线程的未处理异常都写 crash 日志, 不静默消失
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Infrastructure.CrashReporter.Report(e.Exception, "UI");
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Infrastructure.CrashReporter.Report(e.ExceptionObject as Exception, "AppDomain");
                };
                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
        }
    }
}
