using System;
using System.Runtime.InteropServices;

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
        /// </summary>
        public static bool TrySetDefault(string deviceId, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(deviceId))
            {
                error = "设备 ID 为空";
                return false;
            }

            int lastHr = 0;
            string lastException = null;

            for (int i = 0; i < ClientClsids.Length; i++)
            {
                object obj = null;
                try
                {
                    Type t = Type.GetTypeFromCLSID(ClientClsids[i]);
                    if (t == null) continue;
                    obj = Activator.CreateInstance(t);
                    var cfg = (IPolicyConfigVista)obj;

                    // eConsole=0, eMultimedia=1 决定成败
                    int hrConsole = cfg.SetDefaultEndpoint(deviceId, 0);
                    int hrMulti = cfg.SetDefaultEndpoint(deviceId, 1);
                    // eCommunications=2 尽力而为, 不参与判定
                    try { cfg.SetDefaultEndpoint(deviceId, 2); } catch { }

                    if (hrConsole == 0 && hrMulti == 0) return true;

                    lastHr = hrConsole != 0 ? hrConsole : hrMulti;
                    // E_NOTIMPL / E_NOINTERFACE 等 → 换下一个 CLSID 再试
                    if (i < ClientClsids.Length - 1) continue;
                }
                catch (Exception ex)
                {
                    lastException = ex.Message;
                }
                finally
                {
                    if (obj != null)
                    {
                        try { Marshal.ReleaseComObject(obj); } catch { }
                    }
                }
            }

            error = lastException != null
                ? "COM 调用异常: " + lastException
                : AudioErrors.Describe(lastHr);
            return false;
        }
    }
}
