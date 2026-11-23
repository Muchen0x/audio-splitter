using System;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace StereoSplitter.Devices
{
    /// <summary>
    /// 系统默认输出切换 (未文档化的 IPolicyConfig)。
    ///
    /// 相对原实现的两处修正:
    ///   1. 双 CLSID 回退: Win7 期的 870af99c… 在部分 Win10/11 上不实现 SetDefaultEndpoint
    ///      (返回 E_NOTIMPL "尚未实现"), 追加 Win10 的 294935ce… 再试一次。
    ///   2. 角色判定: 原实现 for(role 0..2){ if(hr != 0) break; } 会把
    ///      "控制台+多媒体设置成功、仅通信角色失败" 误判为整体失败。
    ///      改为只以 eConsole / eMultimedia 判定成败, eCommunications 仅尽力而为。
    ///
    /// 两个必须知道的副作用 (R-12):
    ///   - **接口是未文档化的**: `IPolicyConfig` 的 CLSID 与 IID 从来没进过公开 SDK, Windows
    ///     更新有可能让它消失 (届时上面的双 CLSID + 回读 + 回滚会表现为"切换默认失败"并给出诊断串,
    ///     等效于"这个功能没了", 不会静默改坏系统)。
    ///   - **会连带切换通信设备**: 三个角色一起设, 通话/会议 (Teams、微信语音、Discord) 的声音
    ///     也会跟着走目标设备。这是分流能捕获通话声音的前提, 但反过来也意味着通话改从分流输出放。
    ///      调用方通过 `commRoleSwitched` 得知这件事是否真的发生了, 并写进日志。
    /// </summary>
    static class SystemDefaultPolicy
    {
        static readonly Guid[] ClientClsids =
        {
            new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"),
            new Guid("294935ce-f637-4e7c-a41b-ab255460b862")
        };

        [ComImport]
        [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfigVista
        {
            [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
            [PreserveSig] int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
            [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
            [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr pMixFormat);
            [PreserveSig] int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
            [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
            [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
            [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr mode);
            [PreserveSig] int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
            [PreserveSig] int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int role);
            [PreserveSig] int SetEndpointVisibility(string pszDeviceName, bool bVisible);
        }

        /// <summary>
        /// 把指定端点设为系统默认输出。成功返回 true; 失败时 error 为人话描述。
        /// F-22: 修改前记录三角色各自默认端点; 必需角色失败时尽力回滚已改角色并报告每个
        /// 角色最终状态; 成功后用系统枚举 API 回读 Console/Multimedia, 不只依赖 HRESULT;
        /// 逐次 COM HRESULT 追加到诊断串, 回滚不会覆盖它。
        /// </summary>
        /// <param name="commRoleSwitched">通信角色 (eCommunications) 是否也跟着切了。R-12:
        /// 这个角色决定 Teams / 微信语音 / Discord 走哪台设备, 系统级副作用, 调用方应把它写进日志。</param>
        public static bool TrySetDefault(string deviceId, out string error, out bool commRoleSwitched)
        {
            error = null;
            commRoleSwitched = false;
            if (string.IsNullOrEmpty(deviceId)) { error = "设备 ID 为空"; return false; }

            string[] before = new string[3];
            for (int r = 0; r < 3; r++) before[r] = ReadDefault((Role)r);

            var diag = new StringBuilder();
            string lastException = null;
            int lastHr = 0;

            for (int i = 0; i < ClientClsids.Length; i++)
            {
                object obj = null;
                try
                {
                    Type t = Type.GetTypeFromCLSID(ClientClsids[i]);
                    if (t == null) { diag.Append("CLSID" + i + " 类型不可用 | "); continue; }
                    obj = Activator.CreateInstance(t);
                    var cfg = (IPolicyConfigVista)obj;

                    bool requiredOk = true;
                    for (int r = 0; r < 2; r++)   // eConsole=0, eMultimedia=1 决定成败
                    {
                        int hr = cfg.SetDefaultEndpoint(deviceId, r);
                        diag.Append("CLSID" + i + " " + RoleName(r) + " HRESULT=0x" + hr.ToString("X8") + " | ");
                        if (hr != 0) { requiredOk = false; lastHr = hr; }
                    }
                    // eCommunications=2 尽力而为, 同样保留 HRESULT
                    int hrComm = cfg.SetDefaultEndpoint(deviceId, 2);
                    commRoleSwitched = hrComm == 0;
                    diag.Append("CLSID" + i + " " + RoleName(2) + " HRESULT=0x" + hrComm.ToString("X8") + " | ");

                    if (requiredOk)
                    {
                        // 回读校验: 通过系统枚举 API 确认默认端点真的切过去了
                        string nowConsole = ReadDefault(Role.Console);
                        string nowMulti = ReadDefault(Role.Multimedia);
                        diag.Append("回读 Console=" + (nowConsole ?? "(无)") +
                                    " Multimedia=" + (nowMulti ?? "(无)") + " | ");
                        if (string.Equals(nowConsole, deviceId, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(nowMulti, deviceId, StringComparison.OrdinalIgnoreCase))
                            return true;
                        lastHr = unchecked((int)0x80004005);   // 回读不一致, 记为未指定错误
                    }

                    // 必需角色失败或回读不符: 尽力恢复已修改角色, 再尝试下一个 CLSID
                    RestoreRoles(cfg, before, deviceId, diag);
                }
                catch (Exception ex)
                {
                    lastException = ex.Message;
                    diag.Append("CLSID" + i + " 异常=" + ex.Message + " | ");
                    if (obj != null)
                    {
                        try { RestoreRoles((IPolicyConfigVista)obj, before, deviceId, diag); } catch { }
                    }
                }
                finally
                {
                    if (obj != null) { try { Marshal.ReleaseComObject(obj); } catch { } }
                }
            }

            string[] after = new string[3];
            for (int r = 0; r < 3; r++) after[r] = ReadDefault((Role)r);
            var sb = new StringBuilder();
            sb.Append("切换系统默认输出失败 (已尽力回滚已修改角色); ");
            sb.Append(lastException != null ? "COM异常=" + lastException : "最后HRESULT=" + AudioErrors.Describe(lastHr));
            sb.Append("; 逐次诊断: ").Append(diag.ToString());
            sb.Append("; 最终状态: ");
            for (int r = 0; r < 3; r++)
            {
                if (r > 0) sb.Append(", ");
                sb.Append(RoleName(r)).Append(": 修改前=").Append(before[r] ?? "(未读到)")
                  .Append(", 最终=").Append(after[r] ?? "(未读到)")
                  .Append(string.Equals(before[r], after[r], StringComparison.OrdinalIgnoreCase)
                      ? " [已恢复/未变]" : " [可能残留, 请人工核对]");
            }
            error = sb.ToString();
            return false;
        }

        /// <summary>经系统枚举 API 读取某角色当前默认端点 ID; 失败/无默认返回 null。</summary>
        static string ReadDefault(Role role)
        {
            try
            {
                using (var e = new MMDeviceEnumerator())
                using (var d = e.GetDefaultAudioEndpoint(DataFlow.Render, role))
                    return d == null ? null : d.ID;
            }
            catch { return null; }
        }

        /// <summary>尽力把三角色恢复到修改前记录的端点; HRESULT 追加到 diag, 不覆盖已有诊断。</summary>
        static void RestoreRoles(IPolicyConfigVista cfg, string[] before, string targetId, StringBuilder diag)
        {
            for (int r = 0; r < 3; r++)
            {
                if (string.IsNullOrEmpty(before[r])) continue;   // 没读到原值, 无法回滚
                if (string.Equals(before[r], targetId, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    int hr = cfg.SetDefaultEndpoint(before[r], r);
                    diag.Append("回滚 " + RoleName(r) + "->" + before[r] + " HRESULT=0x" + hr.ToString("X8") + " | ");
                }
                catch (Exception ex)
                {
                    diag.Append("回滚 " + RoleName(r) + " 异常=" + ex.Message + " | ");
                }
            }
        }

        static string RoleName(int r)
        {
            if (r == 0) return "Console";
            if (r == 1) return "Multimedia";
            return "Communications";
        }
    }
}
