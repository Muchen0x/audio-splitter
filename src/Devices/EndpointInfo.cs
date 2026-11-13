using System;

namespace StereoSplitter.Devices
{
    /// <summary>
    /// 端点的纯数据描述: 不含任何 COM 引用, 可随意持有/跨线程传递。
    /// 设备列表只保存它, 端点操作一律走 DeviceCatalog.Open 新建独立实例。
    /// </summary>
    sealed class EndpointInfo
    {
        public string ID;
        public string FriendlyName;
        public bool IsVirtual;

        public override string ToString()
        {
            return FriendlyName ?? ID;
        }
    }
}
