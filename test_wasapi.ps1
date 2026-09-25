# WASAPI Loopback Test via .NET COM
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
public class MMDeviceEnumeratorClass { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDeviceEnumerator {
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMDevice {
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr actParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
    [PreserveSig] int GetId(out IntPtr id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("1CB9AD4C-DBFA-473A-BA46-32A131D9027C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioClient {
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, ref Guid sessionGuid);
    [PreserveSig] int GetBufferSize(out uint size);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("C8ADBD64-E71E-48CC-9A5E-F4CA8FF8435D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioCaptureClient {
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out long devPos, out long qpcPos);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint size);
}

[StructLayout(LayoutKind.Sequential)]
public struct WAVEFORMATEX {
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}
"@

try {
    Write-Host "Creating MMDeviceEnumerator via CLSID..."
    $type = [Type]::GetTypeFromCLSID([Guid]"BCDE0395-E52F-467C-8E3D-C4579291692E")
    $raw = [Activator]::CreateInstance($type)
    $unk = [System.Runtime.InteropServices.Marshal]::GetIUnknownForObject($raw)

    # Use vtable to call EnumAudioEndpoints directly (bypass QI)
    $vtable = [System.Runtime.InteropServices.Marshal]::ReadIntPtr($unk, 0)
    Write-Host "Vtable: 0x$([IntPtr]::Size -eq 8 ? $vtable.ToInt64().ToString('X') : $vtable.ToInt32().ToString('X'))"

    # Read slot 3 = EnumAudioEndpoints
    $enumFnPtr = [System.Runtime.InteropServices.Marshal]::ReadIntPtr($vtable, 3 * [IntPtr]::Size)
    Write-Host "EnumAudioEndpoints fn: 0x$($enumFnPtr.ToInt64().ToString('X'))"

    # Call EnumAudioEndpoints(0, 1, &devices) - RENDER, STATE_ACTIVE
    $devicesPtr = [IntPtr]::Zero
    $enumFnDelegate = [delegate[int, int, int, IntPtr]]param($self, $flow, $mask, $outDevices)
    # This won't work easily in PS. Let's use a simpler approach.

    # Actually, let's just try activating IAudioClient3 on a known device and calling methods via vtable
    # First get the device pointer we already know works from our C# code

    Write-Host "Trying direct vtable approach for GetService..."

} catch {
    Write-Host "EXCEPTION: $_"
}
