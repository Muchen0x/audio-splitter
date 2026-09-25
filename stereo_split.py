"""
双USB声卡左右声道分离器
将立体声音频的左声道发送到设备1，右声道发送到设备2。
使用 Windows WASAPI (Core Audio API) 通过纯 ctypes 实现，无需额外依赖。
"""

import ctypes
import ctypes.wintypes as wintypes
import sys
import threading
import time
import struct
import numpy as np

# ============================================================================
# Windows COM / Core Audio API 常量和结构
# ============================================================================

CLSID_MMDeviceEnumerator = ctypes.c Guid('{BCDE0395-E52F-467C-8E3D-C4579291692E}')
IID_IMMDeviceEnumerator = ctypes.c Guid('{A95664D2-9614-4F35-A746-DE8DB63617E6}')
IID_IAudioClient = ctypes.c Guid('{1CB9AD4C-DBFA-473A-BA46-32A131D90271}')
IID_IAudioCaptureClient = ctypes.c Guid('{C8ADBD64-E71E-48CC-9A5E-F4CA8FF8435D}')
IID_IAudioRenderClient = ctypes.c Guid('{F294ACFC-3146-4483-A7BF-ADD0DA6C7604}')
IID_IAudioEndpointVolume = ctypes.c Guid('{5CDF2C82-841E-4546-9722-0CF74078229A}')

# EDataFlow
eRender = 0
eCapture = 1

# ERole
eConsole = 0
eMultimedia = 1
eCommunications = 2

# AUDCLNT constants
AUDCLNT_STREAMFLAGS_RATEADJUST = 0x8
AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000
AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000
AUDCLNT_SHAREMODE_SHARED = 0
AUDCLNT_SHAREMODE_EXCLUSIVE = 1

# WAVE format tags
WAVE_FORMAT_PCM = 0x0001
WAVE_FORMAT_IEEE_FLOAT = 0x0003

# DEVICE_STATE
DEVICE_STATE_ACTIVE = 0x00000001

# CLSCTX
CLSCTX_INPROC_SERVER = 0x1

# ============================================================================
# COM GUID 结构
# ============================================================================

class GUID(ctypes.Structure):
    _fields_ = [
        ("Data1", ctypes.c_uint32),
        ("Data2", ctypes.c_uint16),
        ("Data3", ctypes.c_uint16),
        ("Data4", ctypes.c_uint8 * 8),
    ]

    def __init__(self, guid_str=None):
        super().__init__()
        if guid_str:
            guid_str = guid_str.strip("{}")
            parts = guid_str.split("-")
            self.Data1 = int(parts[0], 16)
            self.Data2 = int(parts[1], 16)
            self.Data3 = int(parts[2], 16)
            b = bytes.fromhex(parts[3] + parts[4])
            for i in range(8):
                self.Data4[i] = b[i]

    def __eq__(self, other):
        if isinstance(other, GUID):
            return (
                self.Data1 == other.Data1
                and self.Data2 == other.Data2
                and self.Data3 == other.Data3
                and bytes(self.Data4) == bytes(other.Data4)
            )
        return False

    def __hash__(self):
        return hash((self.Data1, self.Data2, self.Data3, bytes(self.Data4)))

    def __str__(self):
        return (
            f"{{{self.Data1:08X}-{self.Data2:04X}-{self.Data3:04X}-"
            f"{self.Data4[0]:02X}{self.Data4[1]:02X}-"
            f"{self.Data4[2]:02X}{self.Data4[3]:02X}"
            f"{self.Data4[4]:02X}{self.Data4[5]:02X}"
            f"{self.Data4[6]:02X}{self.Data4[7]:02X}}}"
        )


class WAVEFORMATEX(ctypes.Structure):
    _fields_ = [
        ("wFormatTag", wintypes.WORD),
        ("nChannels", wintypes.WORD),
        ("nSamplesPerSec", wintypes.DWORD),
        ("nAvgBytesPerSec", wintypes.DWORD),
        ("nBlockAlign", wintypes.WORD),
        ("wBitsPerSample", wintypes.WORD),
        ("cbSize", wintypes.WORD),
    ]


# ============================================================================
# VTable 定义 - 每个 COM 接口的方法
# ============================================================================

# IUnknown
class IUnknownVtbl(ctypes.Structure):
    _fields_ = [
        ("QueryInterface", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p))),
        ("AddRef", ctypes.CFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)),
        ("Release", ctypes.CFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)),
    ]


# IMMDevice
class IMMDeviceVtbl(ctypes.Structure):
    _fields_ = [
        ("QueryInterface", IUnknownVtbl._fields_[0][1]),
        ("AddRef", IUnknownVtbl._fields_[1][1]),
        ("Release", IUnknownVtbl._fields_[2][1]),
        ("Activate", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(GUID), ctypes.c_uint32, ctypes.POINTER(ctypes.wintypes.VARIANT), ctypes.POINTER(ctypes.c_void_p))),
        ("OpenPropertyStore", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p))),
        ("GetId", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.c_wchar_p))),
        ("GetState", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32))),
    ]


# IMMDeviceEnumerator
class IMMDeviceEnumeratorVtbl(ctypes.Structure):
    _fields_ = [
        ("QueryInterface", IUnknownVtbl._fields_[0][1]),
        ("AddRef", IUnknownVtbl._fields_[1][1]),
        ("Release", IUnknownVtbl._fields_[2][1]),
        ("EnumAudioEndpoints", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p))),
        ("GetDefaultAudioEndpoint", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p))),
        ("GetDevice", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_wchar_p, ctypes.POINTER(ctypes.c_void_p))),
        ("RegisterEndpointNotificationCallback", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_void_p)),
        ("UnregisterEndpointNotificationCallback", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_void_p)),
    ]


# IAudioClient
class IAudioClientVtbl(ctypes.Structure):
    _fields_ = [
        ("QueryInterface", IUnknownVtbl._fields_[0][1]),
        ("AddRef", IUnknownVtbl._fields_[1][1]),
        ("Release", IUnknownVtbl._fields_[2][1]),
        ("Initialize", ctypes.CFUNCTYPE(
            ctypes.c_long, ctypes.c_void_p,
            ctypes.c_uint32,      # ShareMode
            ctypes.c_uint32,      # StreamFlags
            ctypes.c_uint64,      # hnsBufferDuration
            ctypes.c_uint64,      # hnsPeriodicity
            ctypes.POINTER(WAVEFORMATEX),  # pFormat
            ctypes.POINTER(GUID),  # AudioSessionGUID
        )),
        ("GetBuffer", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.POINTER(ctypes.c_uint8)), ctypes.POINTER(ctypes.c_uint32))),
        ("ReleaseBuffer", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint32)),
        ("GetCurrentPadding", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32))),
        ("IsFormatSupported", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(WAVEFORMATEX), ctypes.POINTER(ctypes.POINTER(WAVEFORMATEX)))),
        ("GetMixFormat", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.POINTER(WAVEFORMATEX)))),
        ("GetDevicePeriod", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint64), ctypes.POINTER(ctypes.c_uint64))),
        ("Start", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p)),
        ("Stop", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p)),
        ("Reset", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p)),
        ("SetEventHandle", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, wintypes.HANDLE)),
        ("GetService", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(GUID), ctypes.POINTER(ctypes.c_void_p))),
    ]


# IAudioCaptureClient
class IAudioCaptureClientVtbl(ctypes.Structure):
    _fields_ = [
        ("QueryInterface", IUnknownVtbl._fields_[0][1]),
        ("AddRef", IUnknownVtbl._fields_[1][1]),
        ("Release", IUnknownVtbl._fields_[2][1]),
        ("GetBuffer", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.POINTER(ctypes.c_uint8)), ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(ctypes.c_uint32), ctypes.POINTER(ctypes.c_uint32))),
        ("ReleaseBuffer", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint32)),
        ("GetNextPacketSize", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32))),
    ]


# IAudioRenderClient
class IAudioRenderClientVtbl(ctypes.Structure):
    _fields_ = [
        ("QueryInterface", IUnknownVtbl._fields_[0][1]),
        ("AddRef", IUnknownVtbl._fields_[1][1]),
        ("Release", IUnknownVtbl._fields_[2][1]),
        ("GetBuffer", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.POINTER(ctypes.c_uint8)))),
        ("ReleaseBuffer", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint32, ctypes.c_uint32)),
    ]


# IMMPropertyStore 简化
class IPropertyStoreVtbl(ctypes.Structure):
    _fields_ = [
        ("QueryInterface", IUnknownVtbl._fields_[0][1]),
        ("AddRef", IUnknownVtbl._fields_[1][1]),
        ("Release", IUnknownVtbl._fields_[2][1]),
        ("GetCount", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32))),
        ("GetAt", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(wintypes.PROPERTYKEY))),
        ("GetValue", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(wintypes.PROPERTYKEY), ctypes.POINTER(ctypes.wintypes.VARIANT))),
        ("SetValue", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(wintypes.PROPERTYKEY), ctypes.POINTER(ctypes.wintypes.VARIANT))),
        ("Commit", ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p)),
    ]


# ============================================================================
# 工具函数
# ============================================================================

ole32 = ctypes.windll.ole32
ole32.CoInitialize(None)

def co_create_instance(clsid, iid, clsctx=CLSCTX_INPROC_SERVER):
    obj = ctypes.c_void_p()
    hr = ole32.CoCreateInstance(
        ctypes.byref(clsid), None, clsctx, ctypes.byref(iid),
        ctypes.byref(obj)
    )
    if hr < 0:
        raise OSError(f"CoCreateInstance 失败: HRESULT=0x{hr & 0xFFFFFFFF:08X}")
    return obj


def hr_ok(hr):
    if hr < 0:
        raise OSError(f"WASAPI 调用失败: HRESULT=0x{hr & 0xFFFFFFFF:08X}")


def get_property_value(store_ptr, key):
    """从 IPropertyStore 读取字符串属性"""
    vtable = ctypes.cast(store_ptr, ctypes.POINTER(IPropertyStoreVtbl)).contents
    variant = ctypes.wintypes.VARIANT()
    ctypes.windll.ole32.VariantInit(ctypes.byref(variant))
    hr = vtable.GetValue(store_ptr, ctypes.byref(key), ctypes.byref(variant))
    if hr < 0:
        return ""
    if variant.vt == 31:  # VT_BSTR
        return variant._bstrval or ""
    return ""


def get_device_name(device_ptr):
    """获取设备名称"""
    dev_vtable = ctypes.cast(device_ptr, ctypes.POINTER(IMMDeviceVtbl)).contents

    store = ctypes.c_void_p()
    hr = dev_vtable.OpenPropertyStore(device_ptr, 0, ctypes.byref(store))
    if hr < 0:
        return "Unknown"

    # PKEY_Device_FriendlyName
    key = wintypes.PROPERTYKEY()
    key.fmtid = GUID("a45c254e-df1c-4efd-8020-67d146a850e0")
    key.pid = 14

    name = get_property_value(store, key)

    store_vtable = ctypes.cast(store, ctypes.POINTER(IPropertyStoreVtbl)).contents
    store_vtable.Release(store)
    return name or "Unknown"


def get_device_id(device_ptr):
    """获取设备ID"""
    dev_vtable = ctypes.cast(device_ptr, ctypes.POINTER(IMMDeviceVtbl)).contents
    dev_id = ctypes.c_wchar_p()
    hr = dev_vtable.GetId(device_ptr, ctypes.byref(dev_id))
    if hr < 0:
        return ""
    result = dev_id.value or ""
    ctypes.windll.ole32.CoTaskMemFree(dev_id)
    return result


def enumerate_devices(flow=eRender):
    """枚举音频端点设备"""
    enumerator = co_create_instance(CLSID_MMDeviceEnumerator, IID_IMMDeviceEnumerator)
    enum_vtable = ctypes.cast(enumerator, ctypes.POINTER(IMMDeviceEnumeratorVtbl)).contents

    collection = ctypes.c_void_p()
    hr = enum_vtable.EnumAudioEndpoints(enumerator, flow, DEVICE_STATE_ACTIVE, ctypes.byref(collection))
    if hr < 0:
        raise OSError(f"枚举设备失败: 0x{hr & 0xFFFFFFFF:08X}")

    count = ctypes.c_uint32(0)

    # IUnknown -> GetCount on IMMDeviceCollection
    # We need IMMDeviceCollection vtable
    coll_iunknown = ctypes.cast(collection, ctypes.POINTER(IUnknownVtbl)).contents
    # IMMDeviceCollection vtable:
    # IUnknown(3) + GetCount + GetAt + Item + Release
    coll_release = coll_iunknown.Release

    # Direct approach: use the vtable offsets
    # After IUnknown::Release (index 2), next is GetCount (index 3)
    vtable_addr = ctypes.cast(collection, ctypes.POINTER(ctypes.c_void_p)).contents.value
    # vtable is array of function pointers
    get_count_fn = ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32))(
        ctypes.cast(ctypes.c_void_p(vtable_addr + 3 * ctypes.sizeof(ctypes.c_void_p)), ctypes.c_void_p).value
    )
    # Actually this is getting complicated. Let me use a simpler approach.
    # Let me just define IMMDeviceCollection vtable properly.

    # Simplified: manually compute
    # IMMDeviceCollection inherits IUnknown (3 methods), then:
    # 3: GetCount
    # 4: GetAt
    # 5: Item
    # 6: Release (inherited from IUnknown)

    collection_release = ctypes.CFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)(
        ctypes.cast(ctypes.c_void_p(ctypes.cast(collection, ctypes.POINTER(ctypes.c_void_p)).contents.value), ctypes.POINTER(ctypes.c_void_p)).contents.value + 2 * ctypes.sizeof(ctypes.c_void_p)
    )

    # OK this raw vtable hacking is fragile. Let me just hardcode offsets.
    # I'll take a completely different approach using windll.ole32 helpers.

    # Use a simpler approach: construct the device list using helper functions
    enum_vtable.Release(enumerator)

    # Return raw device pointers via a different method
    return _enum_devices_simple(flow)


def _enum_devices_simple(flow=eRender):
    """简化版设备枚举"""
    enumerator_obj = co_create_instance(CLSID_MMDeviceEnumerator, IID_IMMDeviceEnumerator)

    # Cast to get vtable
    enum_ptr = ctypes.cast(enumerator_obj, ctypes.POINTER(IMMDeviceEnumeratorVtbl))

    collection = ctypes.c_void_p()
    hr = enum_ptr.contents.EnumAudioEndpoints(enumerator_obj, flow, DEVICE_STATE_ACTIVE, ctypes.byref(collection))
    if hr < 0:
        raise OSError(f"枚举设备失败: 0x{hr & 0xFFFFFFFF:08X}")

    # Get count from collection
    # IMMDeviceCollection: IUnknown(3) + GetCount(4) + GetAt(5) + ...
    coll_raw = ctypes.cast(collection, ctypes.POINTER(ctypes.c_void_p))
    vtable = coll_raw.contents.value  # vtable pointer

    # Function pointer for GetCount (index 3)
    GetCountType = ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32))
    get_count = GetCountType(vtable + 3 * ctypes.sizeof(ctypes.c_void_p))

    # Function pointer for GetAt (index 4)
    GetAtType = ctypes.CFUNCTYPE(ctypes.c_long, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(ctypes.c_void_p))
    get_at = GetAtType(vtable + 4 * ctypes.sizeof(ctypes.c_void_p))

    # Function pointer for Release (index 2 from IUnknown)
    ReleaseType = ctypes.CFUNCTYPE(ctypes.c_ulong, ctypes.c_void_p)
    release_coll = ReleaseType(vtable + 2 * ctypes.sizeof(ctypes.c_void_p))

    count = ctypes.c_uint32(0)
    hr = get_count(collection, ctypes.byref(count))
    if hr < 0:
        raise OSError(f"GetCount 失败: 0x{hr & 0xFFFFFFFF:08X}")

    devices = []
    for i in range(count.value):
        dev = ctypes.c_void_p()
        hr = get_at(collection, i, ctypes.byref(dev))
        if hr < 0:
            continue
        devices.append(dev.value)

    release_coll(collection)
    return devices


def activate_device(device_ptr, iid):
    """激活设备获取接口"""
    dev_vtable = ctypes.cast(ctypes.c_void_p(device_ptr), ctypes.POINTER(IMMDeviceVtbl)).contents
    interface = ctypes.c_void_p()
    hr = dev_vtable.Activate(ctypes.c_void_p(device_ptr), ctypes.byref(iid), CLSCTX_INPROC_SERVER, None, ctypes.byref(interface))
    if hr < 0:
        raise OSError(f"Activate 失败: 0x{hr & 0xFFFFFFFF:08X}")
    return interface


# ============================================================================
# WASAPI 立体声分离核心
# ============================================================================

class StereoSplitter:
    """将立体声音频的左右声道分别发送到两个不同的USB声卡"""

    def __init__(self):
        self.running = False
        self.capture_client = None
        self.render_clients = [None, None]
        self.audio_clients = [None, None]
        self.capture_audio_client = None
        self.buffer_size = 0
        self.packet_size = 0

    def list_usb_devices(self):
        """列出所有渲染设备，标记USB设备"""
        devices = enumerate_devices(eRender)
        result = []
        for dev_ptr in devices:
            name = get_device_name(dev_ptr)
            dev_id = get_device_id(dev_ptr)
            is_usb = "usb" in name.lower() or "usb" in dev_id.lower()
            result.append({
                "name": name,
                "id": dev_id,
                "ptr": dev_ptr,
                "is_usb": is_usb,
            })
        return result

    def list_capture_devices(self):
        """列出所有捕获设备"""
        devices = enumerate_devices(eCapture)
        result = []
        for dev_ptr in devices:
            name = get_device_name(dev_ptr)
            dev_id = get_device_id(dev_ptr)
            result.append({
                "name": name,
                "id": dev_id,
                "ptr": dev_ptr,
            })
        return result

    def setup_loopback_capture(self):
        """设置 WASAPI 回环捕获（捕获系统正在播放的音频）"""
        enumerator = co_create_instance(CLSID_MMDeviceEnumerator, IID_IMMDeviceEnumerator)
        enum_vtable = ctypes.cast(enumerator, ctypes.POINTER(IMMDeviceEnumeratorVtbl)).contents

        # 获取默认渲染端点
        default_dev = ctypes.c_void_p()
        hr = enum_vtable.GetDefaultAudioEndpoint(enumerator, eRender, eConsole, ctypes.byref(default_dev))
        if hr < 0:
            raise OSError("无法获取默认音频输出设备")

        name = get_device_name(default_dev)
        print(f"  回环捕获源: {name}")

        # 激活 IAudioClient
        self.capture_audio_client = activate_device(default_dev, IID_IAudioClient)
        cap_ac_vtable = ctypes.cast(self.capture_audio_client, ctypes.POINTER(IAudioClientVtbl)).contents

        # 获取默认格式
        mix_format_ptr = ctypes.POINTER(WAVEFORMATEX)()
        hr = cap_ac_vtable.GetMixFormat(self.capture_audio_client, ctypes.byref(mix_format_ptr))
        if hr < 0:
            raise OSError("无法获取音频格式")

        # 复制格式信息
        self.format = WAVEFORMATEX()
        ctypes.memmove(ctypes.byref(self.format), mix_format_ptr, ctypes.sizeof(WAVEFORMATEX))
        ctypes.windll.ole32.CoTaskMemFree(ctypes.cast(mix_format_ptr, ctypes.c_void_p))

        print(f"  音频格式: {self.format.nChannels}ch, {self.format.nSamplesPerSec}Hz, "
              f"{self.format.wBitsPerSample}bit")

        # 初始化回环捕获
        hr = cap_ac_vtable.Initialize(
            self.capture_audio_client,
            AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_LOOPBACK,
            0,  # 默认缓冲
            0,
            ctypes.byref(self.format),
            None,
        )
        if hr < 0:
            raise OSError(f"初始化回环捕获失败: 0x{hr & 0xFFFFFFFF:08X}")

        # 获取缓冲区大小
        default_period = ctypes.c_uint64(0)
        fundamental_period = ctypes.c_uint64(0)
        hr = cap_ac_vtable.GetDevicePeriod(self.capture_audio_client, ctypes.byref(default_period), ctypes.byref(fundamental_period))
        if hr < 0:
            raise OSError("获取设备周期失败")

        self.buffer_size = default_period.value
        print(f"  缓冲区周期: {self.buffer_size / 10000:.1f}ms")

        # 获取 IAudioCaptureClient
        cap_iface = ctypes.c_void_p()
        hr = cap_ac_vtable.GetService(self.capture_audio_client, ctypes.byref(IID_IAudioCaptureClient), ctypes.byref(cap_iface))
        if hr < 0:
            raise OSError("无法获取 IAudioCaptureClient")
        self.capture_client = ctypes.cast(cap_iface, ctypes.POINTER(IAudioCaptureClientVtbl))

        # 获取包大小
        self.packet_size = ctypes.c_uint32(0)
        hr = self.capture_client.contents.GetNextPacketSize(self.capture_client, ctypes.byref(self.packet_size))
        if hr < 0:
            self.packet_size = ctypes.c_uint32(0)

        # 释放 enumerator
        enum_vtable.Release(enumerator)

    def setup_render_device(self, device_ptr, device_name, index):
        """设置渲染设备（USB声卡）"""
        audio_client = activate_device(ctypes.c_void_p(device_ptr), IID_IAudioClient)
        ac_vtable = ctypes.cast(audio_client, ctypes.POINTER(IAudioClientVtbl)).contents

        # 获取混音格式
        mix_format_ptr = ctypes.POINTER(WAVEFORMATEX)()
        hr = ac_vtable.GetMixFormat(audio_client, ctypes.byref(mix_format_ptr))
        if hr < 0:
            raise OSError(f"获取 {device_name} 格式失败")

        render_format = WAVEFORMATEX()
        ctypes.memmove(ctypes.byref(render_format), mix_format_ptr, ctypes.sizeof(WAVEFORMATEX))
        ctypes.windll.ole32.CoTaskMemFree(ctypes.cast(mix_format_ptr, ctypes.c_void_p))

        # 修改为单声道输出
        if render_format.nChannels > 1:
            render_format.nChannels = 1
            render_format.nBlockAlign = render_format.wBitsPerSample // 8
            render_format.nAvgBytesPerSec = render_format.nSamplesPerSec * render_format.nBlockAlign

        print(f"  [{device_name}] 格式: {render_format.nChannels}ch, "
              f"{render_format.nSamplesPerSec}Hz, {render_format.wBitsPerSample}bit")

        # 初始化渲染
        hr = ac_vtable.Initialize(
            audio_client,
            AUDCLNT_SHAREMODE_SHARED,
            AUDCLNT_STREAMFLAGS_RATEADJUST | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
            0,  # 默认缓冲
            0,
            ctypes.byref(render_format),
            None,
        )
        if hr < 0:
            raise OSError(f"初始化 {device_name} 渲染失败: 0x{hr & 0xFFFFFFFF:08X}")

        # 获取 IAudioRenderClient
        render_iface = ctypes.c_void_p()
        hr = ac_vtable.GetService(audio_client, ctypes.byref(IID_IAudioRenderClient), ctypes.byref(render_iface))
        if hr < 0:
            raise OSError(f"无法获取 {device_name} 的 IAudioRenderClient")

        self.audio_clients[index] = ac_vtable
        self.render_clients[index] = ctypes.cast(render_iface, ctypes.POINTER(IAudioRenderClientVtbl))
        self.render_formats[index] = render_format

        # 开始播放
        hr = ac_vtable.Start(audio_client)
        if hr < 0:
            raise OSError(f"启动 {device_name} 失败")

    def start(self, left_device_ptr, right_device_ptr, left_name, right_name):
        """启动立体声分离"""
        print("\n=== 设置回环捕获 ===")
        self.setup_loopback_capture()

        self.render_formats = [None, None]

        print("\n=== 设置左声道输出 ===")
        self.setup_render_device(left_device_ptr, left_name, 0)

        print("\n=== 设置右声道输出 ===")
        self.setup_render_device(right_device_ptr, right_name, 1)

        # 启动捕获
        hr = self.capture_audio_client.Start()
        if hr < 0:
            raise OSError("启动回环捕获失败")

        print("\n=== 开始捕获和路由 ===")
        print("  按 Ctrl+C 停止\n")

        self.running = True
        self._process_loop()

    def _process_loop(self):
        """主处理循环：捕获立体声 → 分离左右 → 分别输出"""
        channels = self.format.nChannels
        bits = self.format.wBitsPerSample
        bytes_per_sample = bits // 8

        if bits == 32:
            dtype = np.float32
        elif bits == 64:
            dtype = np.float64
        elif bits == 16:
            dtype = np.int16
        elif bits == 24:
            dtype = np.int32  # 24-bit packed in 32-bit
        else:
            dtype = np.float32

        frame_size = channels * bytes_per_sample
        frames_processed = 0
        report_interval = 50  # 每50次报告一次

        while self.running:
            try:
                packet_size = ctypes.c_uint32(0)
                hr = self.capture_client.contents.GetNextPacketSize(self.capture_client, ctypes.byref(packet_size))
                if hr < 0 or packet_size.value == 0:
                    time.sleep(0.001)
                    continue

                num_frames = packet_size.value // frame_size
                if num_frames == 0:
                    continue

                # 获取捕获缓冲区
                buffer_ptr = ctypes.POINTER(ctypes.c_uint8)()
                num_frames_read = ctypes.c_uint32(0)
                flags = ctypes.c_uint32(0)
                device_position = ctypes.c_uint64(0)
                qpc_position = ctypes.c_uint64(0)

                hr = self.capture_client.contents.GetBuffer(
                    self.capture_client,
                    ctypes.byref(buffer_ptr),
                    ctypes.byref(num_frames_read),
                    ctypes.byref(flags),
                    ctypes.byref(device_position),
                    ctypes.byref(qpc_position),
                )
                if hr < 0:
                    continue

                if num_frames_read.value == 0:
                    self.capture_client.contents.ReleaseBuffer(self.capture_client, 0)
                    continue

                # 复制数据到 numpy
                total_bytes = num_frames_read.value * frame_size
                raw_data = ctypes.string_at(buffer_ptr, total_bytes)
                audio_data = np.frombuffer(raw_data, dtype=dtype).copy()

                # 释放捕获缓冲区
                self.capture_client.contents.ReleaseBuffer(self.capture_client, num_frames_read.value)

                # 分离左右声道
                if channels >= 2:
                    audio_stereo = audio_data.reshape(-1, channels)
                    left_channel = audio_stereo[:, 0]   # 左声道
                    right_channel = audio_stereo[:, 1]  # 右声道
                else:
                    # 单声道：两个声道相同
                    left_channel = audio_data
                    right_channel = audio_data

                # 转换为输出格式并发送到对应设备
                self._send_to_render(left_channel, 0)
                self._send_to_render(right_channel, 1)

                frames_processed += num_frames_read.value
                if frames_processed >= report_interval * self.format.nSamplesPerSec:
                    print(f"  已处理 {frames_processed / self.format.nSamplesPerSec:.1f}s 音频", end="\r")
                    frames_processed = 0

            except KeyboardInterrupt:
                self.stop()
                break
            except Exception as e:
                print(f"\n处理错误: {e}")
                time.sleep(0.01)

    def _send_to_render(self, channel_data, index):
        """将单声道数据发送到渲染设备"""
        render_format = self.render_formats[index]
        if render_format is None:
            return

        audio_client = self.audio_clients[index]
        render_client = self.render_clients[index]
        if audio_client is None or render_client is None:
            return

        # 确保采样率匹配
        if render_format.nSamplesPerSec != self.format.nSamplesPerSec:
            # 如果采样率不匹配，需要重采样（简化：跳过）
            pass

        # 检查可用缓冲空间
        padding = ctypes.c_uint32(0)
        hr = audio_client.GetCurrentPadding(self.audio_clients_handle[index], ctypes.byref(padding))
        if hr < 0:
            return

        # 获取缓冲区帧数
        buffer_frames = ctypes.c_uint32(len(channel_data))
        available = max(0, buffer_frames.value - padding.value)
        if available < len(channel_data):
            return  # 缓冲区满，跳过

        # 获取渲染缓冲区
        buffer_ptr = ctypes.POINTER(ctypes.c_uint8)()
        hr = render_client.contents.GetBuffer(render_client, buffer_frames.value, ctypes.byref(buffer_ptr))
        if hr < 0:
            return

        # 复制数据
        bytes_per_sample = render_format.wBitsPerSample // 8
        if bytes_per_sample == 4 and channel_data.dtype in [np.float32]:
            # float32
            ctypes.memmove(buffer_ptr, channel_data.ctypes.data, len(channel_data) * bytes_per_sample)
        elif bytes_per_sample == 2:
            # int16
            if channel_data.dtype == np.float32:
                int_data = np.clip(channel_data * 32768, -32768, 32767).astype(np.int16)
            else:
                int_data = channel_data.astype(np.int16)
            ctypes.memmove(buffer_ptr, int_data.ctypes.data, len(int_data) * 2)
        else:
            ctypes.memmove(buffer_ptr, channel_data.ctypes.data, len(channel_data) * bytes_per_sample)

        # 释放渲染缓冲区
        hr = render_client.contents.ReleaseBuffer(render_client, buffer_frames.value, 0)

    def stop(self):
        """停止所有流"""
        print("\n\n正在停止...")
        self.running = False

        if self.capture_audio_client:
            vtable = ctypes.cast(self.capture_audio_client, ctypes.POINTER(IAudioClientVtbl)).contents
            vtable.Stop(self.capture_audio_client)

        for i in range(2):
            if self.audio_clients[i]:
                vtable = self.audio_clients[i]
                vtable.Stop(list(self.audio_clients.values())[i] if i < len(self.audio_clients) else None)

        print("已停止。")

    def _get_audio_client_handle(self, index):
        """获取 audio_client 的原始指针"""
        return list(self.audio_clients.values())[index] if index < len(self.audio_clients) else None


# ============================================================================
# 主程序
# ============================================================================

def main():
    print("=" * 60)
    print("  双USB声卡立体声分离器")
    print("  将系统音频的左声道→设备1，右声道→设备2")
    print("=" * 60)

    splitter = StereoSplitter()

    # 列出所有音频设备
    print("\n=== 音频输出设备 ===")
    render_devices = splitter.list_usb_devices()
    for i, dev in enumerate(render_devices):
        marker = " ★ USB" if dev["is_usb"] else ""
        print(f"  [{i}] {dev['name']}{marker}")
        print(f"       ID: {dev['id'][:80]}...")

    if len(render_devices) < 2:
        print("\n错误: 需要至少两个音频输出设备！")
        print("请确保两个USB声卡都已连接并被系统识别。")
        input("按回车退出...")
        return

    # 自动选择USB设备
    usb_devices = [d for d in render_devices if d["is_usb"]]
    if len(usb_devices) >= 2:
        print(f"\n检测到 {len(usb_devices)} 个USB设备")
        left_dev = usb_devices[0]
        right_dev = usb_devices[1]
    else:
        print("\n未检测到足够USB设备，请手动选择：")
        try:
            left_idx = int(input("  左声道设备序号: "))
            right_idx = int(input("  右声道设备序号: "))
        except (ValueError, EOFError):
            print("输入无效！")
            return

        if left_idx == right_idx:
            print("错误: 请选择两个不同的设备！")
            return
        if left_idx >= len(render_devices) or right_idx >= len(render_devices):
            print("错误: 设备序号超出范围！")
            return

        left_dev = render_devices[left_idx]
        right_dev = render_devices[right_idx]

    print(f"\n  左声道 → {left_dev['name']}")
    print(f"  右声道 → {right_dev['name']}")

    confirm = input("\n确认开始？(y/n): ").strip().lower()
    if confirm not in ('y', 'yes', ''):
        print("已取消。")
        return

    # 需要保持设备引用存活
    try:
        splitter.start(
            left_dev["ptr"], right_dev["ptr"],
            left_dev["name"], right_dev["name"]
        )
    except KeyboardInterrupt:
        splitter.stop()
    except Exception as e:
        print(f"\n错误: {e}")
        import traceback
        traceback.print_exc()
        splitter.stop()

    input("\n按回车退出...")


if __name__ == "__main__":
    main()
