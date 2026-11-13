using System;
using System.Runtime.InteropServices;

namespace StereoSplitter
{
    /// <summary>音频设备不可用 (拔出/停用/被占用), 携带可直接展示给用户的说明。</summary>
    sealed class AudioDeviceException : Exception
    {
        public AudioDeviceException(string message) : base(message) { }
        public AudioDeviceException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>把 HRESULT / 异常翻译成用户能看懂的说明, 避免日志出现裸 "尚未实现"。</summary>
    static class AudioErrors
    {
        public static string Describe(int hr)
        {
            switch (unchecked((uint)hr))
            {
                case 0x80070651: return "设备已被移除 (0x80070651), 请刷新设备后重试";
                case 0x80070005: return "拒绝访问音频设备 (0x80070005), 请检查是否被其它程序独占";
                case 0x8007000E: return "内存不足 (0x8007000E)";
                case 0x80004001: return "设备接口未实现 (0x80004001, 通常表示设备已失效或系统版本不支持)";
                case 0x80004005: return "未指定的错误 (0x80004005)";
                case 0x88890004: return "音频端点已失效 (0x88890004), 设备可能已拔出或格式变更";
                case 0x88890008: return "设备不支持该音频格式 (0x88890008)";
                case 0x8889000A: return "设备被独占占用 (0x8889000A)";
                case 0x8007001F: return "设备无法访问 (0x8007001F)";
                case 0x80070490: return "找不到设备 (0x80070490)";
                default: return "HRESULT=0x" + hr.ToString("X8");
            }
        }

        public static string Describe(Exception ex)
        {
            if (ex == null) return "(未知错误)";

            var com = ex as COMException;
            if (com != null) return Describe(com.ErrorCode);

            if (ex is NotImplementedException)
                return "设备状态异常 (接口未实现), 多为设备已拔出但仍在使用旧设备 ID, 请刷新设备";

            if (ex is UnauthorizedAccessException)
                return "无权限访问音频设备, 请检查独占模式设置";

            var mm = ex as NAudio.MmException;
            if (mm != null) return Describe((int)mm.Result);

            return ex.Message;
        }
    }
}
