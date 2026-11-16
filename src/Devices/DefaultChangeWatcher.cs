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
    // 系统默认输出变化/设备断开监听 (COM回调线程 → 转发给主窗体处理)
    class DefaultChangeWatcher : IMMNotificationClient
    {
        readonly Action<DataFlow, Role, string> onDefaultChanged;
        readonly Action<string> onDeviceLost;

        // 回调线程上不能直接记日志 (UI/SessionLog 都不保证线程安全), 异常只做计数,
        // 由主窗体看门狗 (UI 线程) 定期读走并落日志。见 MainForm.WatchdogTick 开头。
        long callbackErrors;
        public long CallbackErrors { get { return Interlocked.Read(ref callbackErrors); } }
        public void ResetCallbackErrors() { Interlocked.Exchange(ref callbackErrors, 0); }

        public DefaultChangeWatcher(Action<DataFlow, Role, string> cb, Action<string> deviceLost)
        {
            onDefaultChanged = cb;
            onDeviceLost = deviceLost;
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            try { onDefaultChanged(flow, role, defaultDeviceId); }
            catch { Interlocked.Increment(ref callbackErrors); }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState != DeviceState.Active) NotifyLost(deviceId);
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { NotifyLost(deviceId); }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        void NotifyLost(string deviceId)
        {
            try { if (onDeviceLost != null) onDeviceLost(deviceId); }
            catch { Interlocked.Increment(ref callbackErrors); }
        }
    }
}
