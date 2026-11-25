using System;
using System.IO;

namespace StereoSplitter.Infrastructure
{
    /// <summary>
    /// 运行期数据位置 (配置 / 日志) 的唯一来源 (R-04)。
    ///
    /// <para><b>要解决的问题</b>: 配置与会话日志原先都写在 exe 同目录。
    /// 装到 <c>C:\Program Files\...</c> 时该目录不可写 —— <c>Load()</c> 读不到 ini
    /// (缺文件不算错误, 不置 LastLoadError) → 以默认值启动 → 之后每次 <c>WriteAll</c> 都失败,
    /// 于是每 60 秒一条"设置写入失败", 用户的全部配置**每次重启归零**, 而且日志也写不下来,
    /// 连"为什么丢设置"都查不到。</para>
    ///
    /// <para><b>策略</b>: 能写就还用 exe 目录 (便携模式)。本项目就是这么分发的 ——
    /// 配置跟着程序走、换台机器拷一个文件夹就行, 这个直觉不该被改掉。只有 exe 目录不可写时,
    /// 才退到 <c>%LOCALAPPDATA%\AudioSplitter</c>, 并把 exe 目录里已有的 ini 复制一份过去
    /// (只复制一次, 已存在则不覆盖), 于是"装到 Program Files"从"每次丢设置"变成
    /// "第一次迁移, 之后一切正常"。</para>
    ///
    /// <para><b>为什么统一裁掉尾部分隔符</b>: <c>AppDomain.BaseDirectory</c> 带尾部分隔符, 而
    /// <c>Path.GetDirectoryName(...)</c> 不带; 两处混用会让"同一个目录"在字符串比较里不相等
    /// (SettingsStore 的跨进程锁就是按路径算 FNV 的, 不同写法会算出两把锁)。</para>
    /// </summary>
    static class AppPaths
    {
        // 程序显示名/exe 名已改成「音频分流」, 但这两个旧名是升级迁移的锚点:
        // 改掉会让老用户的 ini 与 %LOCALAPPDATA% 备份同时失联, 所以原样保留。
        /// <summary>%LOCALAPPDATA% 下的子目录名。</summary>
        public const string ProductFolder = "AudioSplitter";

        const string IniName = "SplitterGUI.ini";

        static readonly object gate = new object();
        static bool resolved;
        static string dataDir;
        static bool portable;
        static string resolveNote;   // 迁移/回退的说明, 供 UI 启动日志取用 (null = 一切照旧)

        /// <summary>exe 所在目录 (无尾部分隔符)。用 BaseDirectory 而不是
        /// <c>Application.ExecutablePath</c>: 二者在桌面应用里等价, 但前者不让
        /// Infrastructure 层反过来依赖 WinForms。</summary>
        public static string ExeDir
        {
            get
            {
                string dir = null;
                try { dir = AppDomain.CurrentDomain.BaseDirectory; } catch { }
                if (string.IsNullOrEmpty(dir)) return ".";
                return TrimSeparator(dir);
            }
        }

        /// <summary>配置与日志的根目录 (懒解析, 进程内只算一次)。</summary>
        public static string DataDir
        {
            get { EnsureResolved(); return dataDir; }
        }

        /// <summary>true = 配置与日志就在 exe 目录 (便携模式)。</summary>
        public static bool IsPortable
        {
            get { EnsureResolved(); return portable; }
        }

        /// <summary>一次性的落位说明 (回退到 LOCALAPPDATA / 迁移了旧 ini); null = 无需提示。</summary>
        public static string ResolveNote
        {
            get { EnsureResolved(); return resolveNote; }
        }

        public static string IniPath { get { return Path.Combine(DataDir, IniName); } }

        public static string LogDir { get { return Path.Combine(DataDir, "log"); } }

        static void EnsureResolved()
        {
            if (resolved) return;
            lock (gate)
            {
                if (resolved) return;
                try { Resolve(); }
                catch
                {
                    // 解析本身不该让程序起不来: 实在不行就退回"和以前一样"(exe 目录)
                    dataDir = ExeDir;
                    portable = true;
                }
                resolved = true;
            }
        }

        static void Resolve()
        {
            string exe = ExeDir;
            if (IsWritable(exe))
            {
                dataDir = exe;
                portable = true;
                return;
            }

            string fallback = LocalDataDir();
            try { Directory.CreateDirectory(fallback); }
            catch
            {
                // 连 LOCALAPPDATA 都建不出来 (极少见: 漫游配置损坏/磁盘满) → 维持旧行为,
                // 至少不会把程序拦在启动之外; 写盘失败会由 SettingsStore 照常留痕。
                dataDir = exe;
                portable = true;
                return;
            }

            dataDir = fallback;
            portable = false;

            // 迁移一次: exe 目录里可能有随安装包发下来的 ini (只读目录也能读到)
            string src = Path.Combine(exe, IniName);
            string dst = Path.Combine(fallback, IniName);
            if (File.Exists(src) && !File.Exists(dst))
            {
                try
                {
                    File.Copy(src, dst);
                    resolveNote = "exe 目录不可写（" + exe + "），已把原有配置迁移到 " + dst;
                }
                catch (Exception ex)
                {
                    resolveNote = "exe 目录不可写（" + exe + "），迁移原有配置失败（" + ex.Message + "），" +
                                  "将以默认值启动并把新配置保存到 " + dst;
                }
            }
            else
            {
                resolveNote = "exe 目录不可写（" + exe + "），配置与日志改存到 " + fallback;
            }
        }

        /// <summary>%LOCALAPPDATA%\AudioSplitter; 取不到就退 %TEMP%\AudioSplitter。</summary>
        static string LocalDataDir()
        {
            string root = null;
            try { root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } catch { }
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                try { root = Path.GetTempPath(); } catch { }
            }
            if (string.IsNullOrEmpty(root)) root = ".";
            return Path.Combine(TrimSeparator(root), ProductFolder);
        }

        /// <summary>
        /// 真去建一个临时文件再删 —— 只查 ACL 不可靠 (继承的拒绝项、只读属性、
        /// 受控文件夹访问 都会让"看起来可写"的目录实际写不进去)。CreateNew + DeleteOnClose
        /// 保证连异常路径都不留垃圾文件。
        /// </summary>
        static bool IsWritable(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                if (!Directory.Exists(dir)) return false;
                string probe = Path.Combine(dir, ".write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                           FileOptions.DeleteOnClose))
                { }
                return true;
            }
            catch { return false; }
        }

        static string TrimSeparator(string dir)
        {
            if (string.IsNullOrEmpty(dir) || dir.Length <= 1) return dir;
            char last = dir[dir.Length - 1];
            if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
                return dir.Substring(0, dir.Length - 1);
            return dir;
        }
    }
}
