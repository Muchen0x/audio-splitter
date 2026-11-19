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
        // 会话内跨进程互斥: 不带 Global\ 前缀的命名对象默认位于 Local\ (当前登录会话)
        // 命名空间, 足以保证同一会话内 GUI 与写型维护命令互斥。若改为 Global\ 跨会话,
        // 必须显式配置 MutexSecurity ACL (默认 ACL 仅允许创建者/管理员), 否则另一会话或
        // 低权限进程打开该锁会抛 UnauthorizedAccessException。GUI 与维护命令共用此锁。
        internal const string MutexName = "StereoSplitter_GUI_SingleInstance";
        internal const string ActivateEventName = "Local\\StereoSplitter_Activate";

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int dwProcessId);

        // ASFW_ANY: 把"允许抢前台"的许可发给本会话里的任意进程
        const int ASFW_ANY = -1;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);
        const int ATTACH_PARENT_PROCESS = -1;

        // 退出码: 0 成功; 2 命令行语法/参数错误 (统一, F-19); 3 volprobe 配置不可信;
        // 10 写型维护命令因已有实例/跨进程锁被占用而拒绝 (F-18)。
        const int ExitUsage = 2;
        const int ExitBusy = 10;

        enum Command { Gui, SelfTest, VolProbe, List, PlayTest, Maintenance }

        /// <summary>严格解析后的命令结果 (解析阶段不触碰任何设备)。</summary>
        sealed class Parsed
        {
            public Command Cmd = Command.Gui;
            public string Needle;                  // --list / --playtest / --setdefault 的值
            public double Seconds = 1.5;
            public bool Force;
            public bool Unmute;
            public readonly List<string> Vols = new List<string>();
            public string Error;

            public bool Fail(string msg) { if (Error == null) Error = msg; return false; }

            public bool SetPrimary(Command c)
            {
                if (Cmd == Command.Gui) { Cmd = c; return true; }
                if (Cmd == c) return true;
                return Fail("命令冲突: 一次只能指定一种命令 (" + Cmd + " 与 " + c + ")");
            }
        }

        static bool IsOption(string s)
        {
            return s != null && s.StartsWith("--", StringComparison.Ordinal);
        }

        static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static int resolverInstalled;

        /// 静态构造里就装上解析器: 它早于 Main 执行, 覆盖"JIT 阶段就可能需要 NAudio"的情形。
        static Program()
        {
            InstallAssemblyResolver();
        }

        /// <summary>
        /// 单文件发布: 同目录缺 NAudio dll 时从内嵌资源加载(同目录有 dll 时 .NET probing 优先)。
        /// <para>
        /// 必须早于**任何** NAudio 类型被解析。原先这段内联在 Main 后半段(命令行分支之后),
        /// 于是单文件模式下 --selftest / --volprobe / --list / --playtest / --setvol 全部
        /// 在 resolver 就位前就撞上 NAudio → 直接退出(自检返回码 2 且不落日志)。
        /// 幂等: 多处调用只注册一次。
        /// </para>
        /// </summary>
        static void InstallAssemblyResolver()
        {
            if (Interlocked.Exchange(ref resolverInstalled, 1) != 0) return;
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
                {
                    try
                    {
                        string name = new AssemblyName(args.Name).Name + ".dll";
                        if (!name.StartsWith("NAudio", StringComparison.OrdinalIgnoreCase)) return null;
                        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                        {
                            if (s == null) return null;
                            var ms = new MemoryStream();
                            s.CopyTo(ms);
                            return Assembly.Load(ms.ToArray());
                        }
                    }
                    catch { return null; }
                };
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] argv)
        {
            // 兜底: 静态构造理论上已装好, 这里再确保一次 (幂等), 让后续任何分支都安全。
            InstallAssemblyResolver();

            // F-19: 统一严格解析器。解析阶段只做语法校验, 不触碰任何设备;
            // 未知选项/缺失值/重复单值选项/值以 -- 开头一律返回使用说明 + 退出码 2。
            Parsed p;
            if (!TryParseArgs(argv, out p))
            {
                try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }
                PrintUsage(p.Error);
                Environment.ExitCode = ExitUsage;
                return;
            }

            if (p.Cmd == Command.Gui)
            {
                RunGui();
                return;
            }

            // 非 GUI 命令: 附加父控制台以便输出可见
            try { AttachConsole(ATTACH_PARENT_PROCESS); } catch { }

            string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            switch (p.Cmd)
            {
                case Command.SelfTest:
                {
                    int code;
                    try { code = RoutingSelfTest.Run(logDir); }
                    catch (Exception ex)
                    {
                        try { Console.WriteLine("自检异常: " + ex); } catch { }
                        code = ExitUsage;
                    }
                    try { Console.WriteLine(code == 0 ? "自检通过" : "自检失败, 详见 log/selftest-routing.txt"); } catch { }
                    Environment.ExitCode = code;
                    return;
                }
                case Command.VolProbe:
                    Environment.ExitCode = StereoSplitter.Infrastructure.VolumeProbe.Run(logDir);
                    return;
                case Command.List:
                    Environment.ExitCode = StereoSplitter.Infrastructure.EndpointAdmin.ListEndpoints(p.Needle);
                    return;
                case Command.PlayTest:
                    Environment.ExitCode = StereoSplitter.Infrastructure.EndpointAdmin.PlayTest(p.Needle, p.Seconds);
                    return;
                case Command.Maintenance:
                    // F-18: 写型维护命令在 GUI/其它维护实例运行时默认拒绝, 需 --force 才放行
                    Environment.ExitCode = RunMaintenanceUnderLock(p, logDir);
                    return;
            }

        }

        /// <summary>GUI 分支: 与写型维护命令共用同一命名 Mutex, 当前会话内单实例。</summary>
        static void RunGui()
        {
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

        /// <summary>F-18: 写型维护命令与 GUI 共用同一把会话内跨进程锁。
        /// 已有 GUI/维护实例持锁时默认拒绝; 显式 --force 才在无互斥保护下执行并说明后果。</summary>
        static int RunMaintenanceUnderLock(Parsed p, string logDir)
        {
            try
            {
                bool createdNew;
                using (var mutex = new Mutex(true, MutexName, out createdNew))
                {
                    if (!createdNew)
                    {
                        if (!p.Force)
                        {
                            Console.WriteLine("[拒绝] 已有 GUI 或维护实例正在运行 (跨进程锁 " + MutexName + " 被占用)。");
                            Console.WriteLine("       写型维护命令默认拒绝执行, 避免与运行中的 GUI 竞争系统音频状态:");
                            Console.WriteLine("       GUI 仍持有进入分流时的音量基线, 停止时会把旧值写回并覆盖本次修改;");
                            Console.WriteLine("       默认输出也可能被 GUI 的变化处理再次切换, 形成两个独立控制面。");
                            Console.WriteLine("       如确需执行请追加 --force, 后果: 无单实例保护, 结果可能被 GUI 覆盖。");
                            return ExitBusy;
                        }
                        Console.WriteLine("[警告] --force: 存在其它实例, 本次写型维护不与其他进程互斥;");
                        Console.WriteLine("        GUI 停止/退出时可能用旧音量基线覆盖本次结果, 默认设备可能被再次切换。");
                    }
                    try
                    {
                        return ExecuteMaintenance(p, logDir);
                    }
                    finally
                    {
                        if (createdNew) { try { mutex.ReleaseMutex(); } catch { } }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[失败] 无法获取跨进程锁: " + ex.Message);
                return ExitBusy;
            }
        }

        /// <summary>F-41: 不再用 code |= 掩盖失败来源; 分别打印每项状态, 返回首个非零错误。</summary>
        static int ExecuteMaintenance(Parsed p, string logDir)
        {
            int first = 0;
            if (p.Vols.Count > 0)
            {
                int c = StereoSplitter.Infrastructure.EndpointAdmin.SetVolumes(p.Vols, logDir, p.Unmute);
                Console.WriteLine("[退出状态] --setvol = " + c + (c == 0 ? " (成功)" : " (失败)"));
                if (c != 0 && first == 0) first = c;
            }
            if (p.Needle != null)
            {
                int c = StereoSplitter.Infrastructure.EndpointAdmin.SetDefault(p.Needle);
                Console.WriteLine("[退出状态] --setdefault = " + c + (c == 0 ? " (成功)" : " (失败)"));
                if (c != 0 && first == 0) first = c;
            }
            Console.WriteLine("[退出状态] 汇总 = " + first + (first == 0 ? " (全部成功)" : " (首个非零错误)"));
            return first;
        }

        static void PrintUsage(string error)
        {
            if (!string.IsNullOrEmpty(error)) Console.WriteLine("参数错误: " + error);
            Console.WriteLine("用法: SplitterGUI.exe [命令] [选项]");
            Console.WriteLine("  (无参数)                          启动图形界面");
            Console.WriteLine("  --selftest                        路由内核自检 (退出 0/1)");
            Console.WriteLine("  --volprobe                        打印端点音量与 ini 的 UI 值");
            Console.WriteLine("  --list [名字片段]                  列出渲染端点 (只读)");
            Console.WriteLine("  --playtest \"名字片段\" [--sec 秒]   播放测试音, 0<秒<=" +
                StereoSplitter.Infrastructure.EndpointAdmin.MaxPlayTestSeconds + ", 默认 1.5");
            Console.WriteLine("  --setvol \"名字片段:百分比\"         设置端点音量, 百分比 0..100 (可重复)");
            Console.WriteLine("  --setdefault \"名字片段\"           切换系统默认输出");
            Console.WriteLine("  --unmute                          允许 --setvol 显式解除静音 (默认不解除)");
            Console.WriteLine("  --force                           写型命令在已有实例运行时仍强行执行");
            Console.WriteLine("说明: 未知选项/缺失值/重复单值选项/值以 -- 开头 -> 退出码 " + ExitUsage +
                              "; 被已有实例拒绝 -> " + ExitBusy + "。");
        }

        static bool Bad(Parsed p, string msg)
        {
            p.Fail(msg);
            return false;
        }

        /// <summary>F-19/F-21: 统一严格解析。全部语法验证在此完成, 之后才允许任何设备写入。
        /// 命令不再只认 argv[0]; 未知参数/缺失值/重复单值选项/值以 -- 开头一律拒绝。</summary>
        static bool TryParseArgs(string[] argv, out Parsed p)
        {
            p = new Parsed();
            if (argv == null) argv = new string[0];
            if (argv.Length == 0) return true;   // 无参 = 启动 GUI

            bool secSeen = false, forceSeen = false, unmuteSeen = false;
            bool listSeen = false, playSeen = false, selfSeen = false, probeSeen = false, defaultSeen = false;

            for (int i = 0; i < argv.Length; i++)
            {
                string tok = argv[i] ?? "";

                if (Eq(tok, "--selftest"))
                {
                    if (selfSeen) return Bad(p, "重复选项 --selftest");
                    selfSeen = true;
                    if (!p.SetPrimary(Command.SelfTest)) return false;
                    continue;
                }
                if (Eq(tok, "--volprobe"))
                {
                    if (probeSeen) return Bad(p, "重复选项 --volprobe");
                    probeSeen = true;
                    if (!p.SetPrimary(Command.VolProbe)) return false;
                    continue;
                }
                if (Eq(tok, "--list"))
                {
                    if (listSeen) return Bad(p, "重复选项 --list");
                    listSeen = true;
                    if (!p.SetPrimary(Command.List)) return false;
                    if (i + 1 < argv.Length && !IsOption(argv[i + 1])) p.Needle = argv[++i];  // 值可选
                    continue;
                }
                if (Eq(tok, "--playtest"))
                {
                    if (playSeen) return Bad(p, "重复选项 --playtest");
                    playSeen = true;
                    if (!p.SetPrimary(Command.PlayTest)) return false;
                    if (i + 1 >= argv.Length || IsOption(argv[i + 1])) return Bad(p, "--playtest 缺少设备名片段");
                    p.Needle = argv[++i];
                    continue;
                }
                if (Eq(tok, "--sec"))
                {
                    if (secSeen) return Bad(p, "重复单值选项 --sec");
                    secSeen = true;
                    if (i + 1 >= argv.Length || IsOption(argv[i + 1])) return Bad(p, "--sec 缺少值");
                    string raw = argv[++i];
                    double tmp;
                    if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out tmp) ||
                        double.IsNaN(tmp) || double.IsInfinity(tmp) ||
                        tmp <= 0 || tmp > StereoSplitter.Infrastructure.EndpointAdmin.MaxPlayTestSeconds)
                        return Bad(p, "--sec 需为 (0, " +
                            StereoSplitter.Infrastructure.EndpointAdmin.MaxPlayTestSeconds +
                            "] 内的有限数值, 收到: " + raw);
                    p.Seconds = tmp;   // 临时变量: TryParse 失败不会把默认值 1.5 覆盖成 0
                    continue;
                }
                if (Eq(tok, "--setvol"))
                {
                    if (!p.SetPrimary(Command.Maintenance)) return false;
                    if (i + 1 >= argv.Length || IsOption(argv[i + 1])) return Bad(p, "--setvol 缺少值");
                    p.Vols.Add(argv[++i]);   // 可重复: 每个 --setvol 带一个值
                    continue;
                }
                if (Eq(tok, "--setdefault"))
                {
                    if (defaultSeen) return Bad(p, "重复单值选项 --setdefault");
                    defaultSeen = true;
                    if (!p.SetPrimary(Command.Maintenance)) return false;
                    if (i + 1 >= argv.Length || IsOption(argv[i + 1])) return Bad(p, "--setdefault 缺少值");
                    p.Needle = argv[++i];
                    continue;
                }
                if (Eq(tok, "--force"))
                {
                    if (forceSeen) return Bad(p, "重复选项 --force");
                    forceSeen = true;
                    if (!p.SetPrimary(Command.Maintenance)) return false;
                    p.Force = true;
                    continue;
                }
                if (Eq(tok, "--unmute"))
                {
                    if (unmuteSeen) return Bad(p, "重复选项 --unmute");
                    unmuteSeen = true;
                    if (!p.SetPrimary(Command.Maintenance)) return false;
                    p.Unmute = true;
                    continue;
                }
                return Bad(p, "未知选项或参数: " + tok);
            }

            // 跨选项一致性校验 (仍在解析阶段, 未打开任何设备)
            if (p.Cmd == Command.Gui) return Bad(p, "未指定有效命令 (空操作)");
            if (secSeen && p.Cmd != Command.PlayTest) return Bad(p, "--sec 只能与 --playtest 一起使用");
            if (p.Cmd == Command.Maintenance && p.Vols.Count == 0 && p.Needle == null)
                return Bad(p, "写型维护命令未指定任何 --setvol/--setdefault 值 (空操作)");
            if (unmuteSeen && p.Vols.Count == 0) return Bad(p, "--unmute 需要至少一个 --setvol");
            return true;
        }
    }
}
