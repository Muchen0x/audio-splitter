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

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);
        const int ATTACH_PARENT_PROCESS = -1;

        static bool HasSwitch(string[] argv, string name)
        {
            for (int i = 0; i < argv.Length; i++)
                if (string.Equals(argv[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        [STAThread]
        static void Main(string[] argv)
        {
            // 路由内核自检 (不打开任何音频端点, 只跑矩阵断言):
            //   SplitterGUI.exe --selftest  → 结果写 log/selftest-routing.txt, 退出码 0/1
            if (argv != null && argv.Length > 0 &&
                string.Equals(argv[0], "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }   // 让命令行里能看到输出
                int code;
                try
                {
                    code = RoutingSelfTest.Run(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log"));
                }
                catch (Exception ex)
                {
                    try { Console.WriteLine("自检异常: " + ex); } catch { }
                    code = 2;
                }
                try { Console.WriteLine(code == 0 ? "自检通过" : "自检失败, 详见 log/selftest-routing.txt"); } catch { }
                Environment.ExitCode = code;
                return;
            }

            // 端点音量探针 (只读, 不碰音频链路):
            //   SplitterGUI.exe --volprobe → 打印各输出设备当前音量/静音 + ini 里的 UI 值
            // 用途: 停止分流后核对"音量到底还原了没有", 结果同时写 log/volume-probe.txt
            if (argv != null && argv.Length > 0 &&
                string.Equals(argv[0], "--volprobe", StringComparison.OrdinalIgnoreCase))
            {
                try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
                Environment.ExitCode = StereoSplitter.Infrastructure.VolumeProbe.Run(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log"));
                return;
            }

            // 端点清单干跑 (只读): SplitterGUI.exe --list ["名字片段"]
            // 用途: --setvol/--setdefault 之前先确认名字片段匹配到哪台设备。
            if (argv != null && argv.Length > 0 && HasSwitch(argv, "--list"))
            {
                try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
                string needle = null;
                for (int i = 0; i < argv.Length; i++)
                    if (string.Equals(argv[i], "--list", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                        needle = argv[++i];
                Environment.ExitCode = StereoSplitter.Infrastructure.EndpointAdmin.ListEndpoints(needle);
                return;
            }

            // 端点测试音: SplitterGUI.exe --playtest "设备名片段" [--sec 1.5]
            // 用途: 程序链路健康但某路没声时, 绕开程序直接向该端点放测试音 ——
            //       听得到 = 硬件/端点正常, 问题在程序或会话层; 听不到 = 硬件/端点层。
            if (argv != null && argv.Length > 0 && HasSwitch(argv, "--playtest"))
            {
                try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
                string needle = null;
                double sec = 1.5;
                for (int i = 0; i < argv.Length; i++)
                {
                    if (string.Equals(argv[i], "--playtest", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                        needle = argv[++i];
                    else if (string.Equals(argv[i], "--sec", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                        double.TryParse(argv[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out sec);
                }
                Environment.ExitCode = StereoSplitter.Infrastructure.EndpointAdmin.PlayTest(needle, sec);
                return;
            }

            // 端点维护 (会写端点, 只在显式给参数时执行):
            //   SplitterGUI.exe --setvol "Xiaomi9:20" --setvol "CABLE:6"
            //   SplitterGUI.exe --setdefault "Realtek"
            // 用途: 实例被强杀后端点音量卡在分流值、或默认输出还挂在虚拟声卡上(系统无声)时善后。
            // 名字片段匹配到多台设备时会拒绝执行并列出候选, 防误伤。
            if (argv != null && argv.Length > 0 && (HasSwitch(argv, "--setvol") || HasSwitch(argv, "--setdefault")))
            {
                try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
                var vols = new List<string>();
                string def = null;
                for (int i = 0; i < argv.Length; i++)
                {
                    if (string.Equals(argv[i], "--setvol", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                        vols.Add(argv[++i]);
                    else if (string.Equals(argv[i], "--setdefault", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                        def = argv[++i];
                }
                int code = 0;
                if (vols.Count > 0)
                    code |= StereoSplitter.Infrastructure.EndpointAdmin.SetVolumes(
                        vols, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log"));
                if (def != null)
                    code |= StereoSplitter.Infrastructure.EndpointAdmin.SetDefault(def);
                Environment.ExitCode = code;
                return;
            }

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
