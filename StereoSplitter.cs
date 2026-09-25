using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace StereoSplitter
{
    class Program
    {
        static bool running = true;

        const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        const int AUDCLNT_STREAMFLAGS_RATEADJUST = 0x8;
        const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
        const int AUDCLNT_SHAREMODE_SHARED = 0;
        const int DEVICE_STATE_ACTIVE = 0x00000001;
        const int WAVE_FORMAT_PCM = 0x0001;
        const int WAVE_FORMAT_IEEE_FLOAT = 0x0003;
        const int CLSCTX_INPROC_SERVER = 0x1;
        const int COINIT_APARTMENTTHREADED = 0x2;
        const int STGM_READ = 0;

        static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        static readonly Guid IID_IMMDeviceEnumerator = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        // Use IAudioClient3 since IAudioClient IID fails on this system; vtable slots for base IAudioClient methods are identical
        static readonly Guid IID_IAudioClient = new Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
        static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48CC-9A5E-F4CA8FF8435D");
        static readonly Guid IID_IAudioRenderClient = new Guid("F294ACFC-3146-4483-A7BF-ADD0DA6C7604");
        static readonly Guid PKEY_Device_FriendlyName_FmtId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");

        [DllImport("ole32.dll")]
        static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

        [DllImport("ole32.dll")]
        static extern void CoUninitialize();

        [DllImport("ole32.dll")]
        static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext,
            ref Guid riid, out IntPtr ppv);

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PROPVARIANT pvar);

        [DllImport("ole32.dll")]
        static extern int CoTaskMemFree(IntPtr pv);

        [StructLayout(LayoutKind.Sequential)]
        struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROPVARIANT
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr pwszVal;
        }

        #region Raw vtable delegates

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_QueryInterface(IntPtr self, ref Guid riid, out IntPtr ppv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate uint D_Release(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_EnumAudioEndpoints(IntPtr self, int dataFlow, int stateMask, out IntPtr ppDevices);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_GetDefaultAudioEndpoint(IntPtr self, int dataFlow, int role, out IntPtr ppEndpoint);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_GetCount(IntPtr self, out uint pCount);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_GetAt(IntPtr self, uint index, out IntPtr ppDevice);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_Activate(IntPtr self, ref Guid iid, int clsCtx, IntPtr pActParams, out IntPtr ppInterface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_OpenPropertyStore(IntPtr self, int stgmAccess, out IntPtr ppProperties);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_GetId(IntPtr self, out IntPtr ppstrId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_PropStore_GetValue(IntPtr self, ref PROPERTYKEY key, out PROPVARIANT pv);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_IAudioClient_Initialize(IntPtr self, int shareMode, int streamFlags,
            long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, ref Guid sessionGuid);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_IAudioClient_GetMixFormat(IntPtr self, out IntPtr ppFormat);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_IAudioClient_GetCurrentPadding(IntPtr self, out uint pNumPadding);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_IAudioClient_GetService(IntPtr self, IntPtr pRiid, out IntPtr ppInterface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_IAudioClient_GetServiceRef(IntPtr self, ref Guid riid, out IntPtr ppInterface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_IAudioClient_Start(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_IAudioClient_Stop(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_Capture_GetNextPacketSize(IntPtr self, out uint pSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_Capture_GetBuffer(IntPtr self, out IntPtr ppData, out uint pFrames,
            out uint pFlags, out long pDevPos, out long pQpcPos);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_Capture_ReleaseBuffer(IntPtr self, uint frames);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_Render_GetBuffer(IntPtr self, uint frames, out IntPtr ppData);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int D_Render_ReleaseBuffer(IntPtr self, uint frames, uint flags);

        #endregion

        static IntPtr VFn(IntPtr obj, int slot)
        {
            IntPtr vtable = Marshal.ReadIntPtr(obj);
            return Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        }

        static T GetFn<T>(IntPtr obj, int slot) where T : class
        {
            return (T)(object)Marshal.GetDelegateForFunctionPointer(VFn(obj, slot), typeof(T));
        }

        static void Check(int hr, string msg)
        {
            if (hr != 0)
                throw new Exception(msg + " HRESULT: 0x" + hr.ToString("X8"));
        }

        // IMMDeviceEnumerator vtable slots (after 3 IUnknown)
        const int SLOT_EnumAudioEndpoints = 3;
        const int SLOT_GetDefaultAudioEndpoint = 4;

        // IMMDeviceCollection vtable slots
        const int SLOT_Coll_GetCount = 3;
        const int SLOT_Coll_GetAt = 4;

        // IMMDevice vtable slots
        const int SLOT_Dev_Activate = 3;
        const int SLOT_Dev_OpenPropertyStore = 4;
        const int SLOT_Dev_GetId = 5;

        // IPropertyStore vtable slots
        const int SLOT_Prop_GetValue = 5; // IUnknown(3) + GetCount + GetAt + GetValue

        // IAudioClient vtable slots
        const int SLOT_AC_Initialize = 3;
        const int SLOT_AC_GetMixFormat = 8;
        const int SLOT_AC_GetCurrentPadding = 6;
        const int SLOT_AC_GetService = 14;
        const int SLOT_AC_Start = 10;
        const int SLOT_AC_Stop = 11;

        // IAudioCaptureClient vtable slots
        const int SLOT_Cap_GetBuffer = 3;
        const int SLOT_Cap_ReleaseBuffer = 4;
        const int SLOT_Cap_GetNextPacketSize = 5;

        // IAudioRenderClient vtable slots
        const int SLOT_Ren_GetBuffer = 3;
        const int SLOT_Ren_ReleaseBuffer = 4;

        static string ReadLineSafe()
        {
            try
            {
                string line = Console.ReadLine();
                return line == null ? "" : line.Trim();
            }
            catch
            {
                return "";
            }
        }

        static void Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            try { Console.InputEncoding = System.Text.Encoding.UTF8; } catch { }

            Console.WriteLine("============================================================");
            Console.WriteLine("  Stereo Splitter - Dual USB Audio Card");
            Console.WriteLine("  Split stereo: L -> Device 1 (left), R -> Device 2 (right)");
            Console.WriteLine("============================================================");
            Console.WriteLine();

            try
            {
                CoInitializeEx(IntPtr.Zero, 0x0 /*COINIT_MULTITHREADED*/);

                Guid clsid = CLSID_MMDeviceEnumerator;
                Guid iid = IID_IMMDeviceEnumerator;
                IntPtr enumerator;
                Check(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out enumerator),
                    "CoCreateInstance(MMDeviceEnumerator)");

                // Enumerate devices
                var enumFn = GetFn<D_EnumAudioEndpoints>(enumerator, SLOT_EnumAudioEndpoints);
                IntPtr collection;
                Check(enumFn(enumerator, 0, DEVICE_STATE_ACTIVE, out collection), "EnumAudioEndpoints");

                var getCount = GetFn<D_GetCount>(collection, SLOT_Coll_GetCount);
                uint deviceCount;
                Check(getCount(collection, out deviceCount), "GetCount");

                var getAt = GetFn<D_GetAt>(collection, SLOT_Coll_GetAt);

                List<IntPtr> devicePtrs = new List<IntPtr>();
                List<string> deviceNames = new List<string>();
                List<bool> deviceIsUsb = new List<bool>();

                Console.WriteLine("=== Audio Output Devices ===");
                for (uint i = 0; i < deviceCount; i++)
                {
                    IntPtr dev;
                    Check(getAt(collection, i, out dev), "GetAt " + i);
                    devicePtrs.Add(dev);

                    string name = GetDeviceName(dev);
                    string id = GetDeviceId(dev);
                    bool isUsb = name.ToLower().Contains("usb") || id.ToLower().Contains("usb\\")
                        || id.ToLower().Contains("\\usb");
                    deviceNames.Add(name);
                    deviceIsUsb.Add(isUsb);

                    Console.WriteLine("  [" + i + "] " + name + (isUsb ? " [USB]" : ""));
                }
                Console.WriteLine();

                // Also enumerate capture devices
                IntPtr capCollection;
                Check(enumFn(enumerator, 1 /*eCapture*/, DEVICE_STATE_ACTIVE, out capCollection), "EnumAudioEndpoints(capture)");
                var getCapCount = GetFn<D_GetCount>(capCollection, SLOT_Coll_GetCount);
                uint capCount;
                Check(getCapCount(capCollection, out capCount), "GetCount(capture)");
                var getCapAt = GetFn<D_GetAt>(capCollection, SLOT_Coll_GetAt);

                Console.WriteLine("=== Audio Capture Devices ===");
                for (uint i = 0; i < capCount; i++)
                {
                    IntPtr capDev;
                    Check(getCapAt(capCollection, i, out capDev), "GetAt(capture) " + i);
                    string capName = GetDeviceName(capDev);
                    Console.WriteLine("  [" + i + "] " + capName);
                }
                Console.WriteLine();

                if (devicePtrs.Count < 2)
                {
                    Console.WriteLine("ERROR: Need at least 2 audio output devices!");
                    Console.WriteLine("Press Enter to exit...");
                    ReadLineSafe();
                    return;
                }

                int leftIdx, rightIdx;
                List<int> usbIdx = new List<int>();
                for (int i = 0; i < deviceIsUsb.Count; i++)
                    if (deviceIsUsb[i]) usbIdx.Add(i);

                if (usbIdx.Count >= 2)
                {
                    leftIdx = usbIdx[0];
                    rightIdx = usbIdx[1];
                    Console.WriteLine("Auto-selected " + usbIdx.Count + " USB devices.");
                }
                else
                {
                    Console.Write("  Left channel device index: ");
                    string lIdxStr = ReadLineSafe();
                    Console.Write("  Right channel device index: ");
                    string rIdxStr = ReadLineSafe();
                    if (!int.TryParse(lIdxStr, out leftIdx) || !int.TryParse(rIdxStr, out rightIdx))
                    {
                        Console.WriteLine("Invalid input!");
                        return;
                    }
                }

                if (leftIdx == rightIdx || leftIdx >= devicePtrs.Count || rightIdx >= devicePtrs.Count)
                {
                    Console.WriteLine("Invalid device selection!");
                    return;
                }

                Console.WriteLine();
                Console.WriteLine("  Left  -> " + deviceNames[leftIdx]);
                Console.WriteLine("  Right -> " + deviceNames[rightIdx]);
                Console.Write("\nStart? (y/n): ");
                string conf = ReadLineSafe().ToLower();
                if (conf != "y" && conf != "yes" && conf != "")
                {
                    Console.WriteLine("Cancelled.");
                    return;
                }

                Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    running = false;
                };

                // Get default render device for loopback
                var getDef = GetFn<D_GetDefaultAudioEndpoint>(enumerator, SLOT_GetDefaultAudioEndpoint);
                IntPtr defaultDev;
                Check(getDef(enumerator, 0, 0, out defaultDev), "GetDefaultAudioEndpoint");
                Console.WriteLine("[DEBUG] defaultDev=0x" + defaultDev.ToString("X"));

                // Test QI on default device
                var qiDev = GetFn<D_QueryInterface>(defaultDev, 0);
                Guid dbgIidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
                IntPtr unkPtr;
                int hrQ = qiDev(defaultDev, ref dbgIidUnknown, out unkPtr);
                Console.WriteLine("[DEBUG] defaultDev QI IUnknown=0x" + hrQ.ToString("X8"));
                Guid dbgIidImmDev = new Guid("D666063F-1587-4E43-81F1-B948E807363F");
                IntPtr immDevPtr;
                hrQ = qiDev(defaultDev, ref dbgIidImmDev, out immDevPtr);
                Console.WriteLine("[DEBUG] defaultDev QI IMMDevice=0x" + hrQ.ToString("X8"));

                // Print vtable of default device
                IntPtr defVtable = Marshal.ReadIntPtr(defaultDev);
                Console.WriteLine("[DEBUG] defaultDev vtable=0x" + defVtable.ToString("X"));
                for (int vi = 0; vi < 7; vi++)
                {
                    IntPtr fn = Marshal.ReadIntPtr(defVtable, vi * IntPtr.Size);
                    Console.WriteLine("[DEBUG]   dev vtable[" + vi + "]: 0x" + fn.ToString("X"));
                }

                RunRouting(defaultDev, devicePtrs[leftIdx], devicePtrs[rightIdx],
                    deviceNames[leftIdx], deviceNames[rightIdx]);

                CoUninitialize();
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("Error: " + ex.Message);
                Console.WriteLine();
                Console.WriteLine("Press Enter to exit...");
                ReadLineSafe();
            }
        }

        static void RunRouting(IntPtr captureDev, IntPtr leftDev, IntPtr rightDev,
            string leftName, string rightName)
        {
            Console.WriteLine();
            Console.WriteLine("=== Setup loopback capture ===");

            // Activate IAudioClient on capture device
            IntPtr capAC = Activate(captureDev, IID_IAudioClient);

            // Get mix format
            var getMixFmt = GetFn<D_IAudioClient_GetMixFormat>(capAC, SLOT_AC_GetMixFormat);
            IntPtr mixFmtPtr;
            Check(getMixFmt(capAC, out mixFmtPtr), "GetMixFormat");
            WAVEFORMATEX mixFmt = Marshal.PtrToStructure<WAVEFORMATEX>(mixFmtPtr);

            Console.WriteLine("  Source: " + mixFmt.nChannels + "ch " + mixFmt.nSamplesPerSec + "Hz " + mixFmt.wBitsPerSample + "bit");

            // Print audio client vtable
            IntPtr acVtable = Marshal.ReadIntPtr(capAC);
            Console.WriteLine("[DEBUG] capAC vtable=0x" + acVtable.ToString("X"));
            for (int vi = 0; vi < 24; vi++)
            {
                IntPtr fn = Marshal.ReadIntPtr(acVtable, vi * IntPtr.Size);
                Console.WriteLine("[DEBUG]   ac vtable[" + vi + "]: 0x" + fn.ToString("X"));
            }

            // Initialize loopback with 1-second buffer
            var initFn = GetFn<D_IAudioClient_Initialize>(capAC, SLOT_AC_Initialize);
            Guid empty = Guid.Empty;
            long bufferDuration = 10000000; // 1 second in 100-nanosecond units
            int hrInit = initFn(capAC, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
                bufferDuration, 0, mixFmtPtr, ref empty);
            Console.WriteLine("[DEBUG] Initialize(1s buffer): hr=0x" + hrInit.ToString("X8"));
            if (hrInit != 0)
            {
                hrInit = initFn(capAC, AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK,
                    0, 0, mixFmtPtr, ref empty);
                Console.WriteLine("[DEBUG] Initialize(0 buffer): hr=0x" + hrInit.ToString("X8"));
            }
            Check(hrInit, "Initialize loopback");
            CoTaskMemFree(mixFmtPtr);

            // Also test non-loopback GetService for comparison
            {
                IntPtr testAC = Activate(captureDev, IID_IAudioClient);
                var testGetMix = GetFn<D_IAudioClient_GetMixFormat>(testAC, SLOT_AC_GetMixFormat);
                IntPtr testMixPtr;
                testGetMix(testAC, out testMixPtr);
                var testInit = GetFn<D_IAudioClient_Initialize>(testAC, SLOT_AC_Initialize);
                Guid g2 = Guid.Empty;
                int hrNI = testInit(testAC, AUDCLNT_SHAREMODE_SHARED, 0, 0, 0, testMixPtr, ref g2);
                Console.WriteLine("[DEBUG] Non-loopback Initialize: 0x" + hrNI.ToString("X8"));
                CoTaskMemFree(testMixPtr);
                if (hrNI == 0)
                {
                    var testSvc = GetFn<D_IAudioClient_GetService>(testAC, SLOT_AC_GetService);
                    Guid iidR = new Guid("F294ACFC-3146-4483-A7BF-ADD0DA6C7604");
                    IntPtr iidRPtr = Marshal.AllocHGlobal(16);
                    Marshal.StructureToPtr(iidR, iidRPtr, false);
                    IntPtr testRc;
                    int hrSvc2 = testSvc(testAC, iidRPtr, out testRc);
                    Console.WriteLine("[DEBUG] Non-loopback GetService(RenderClient): 0x" + hrSvc2.ToString("X8") + " ptr=0x" + testRc.ToString("X"));
                    Marshal.FreeHGlobal(iidRPtr);
                }
                var testRelease = GetFn<D_Release>(testAC, 2);
                testRelease(testAC);
            }

            // Try QI on audio client to get base IAudioClient
            {
                var qi = GetFn<D_QueryInterface>(capAC, 0);
                Guid iidBaseAC = new Guid("1CB9AD4C-DBFA-473A-BA46-32A131D9027C");
                IntPtr baseAC;
                int hrQi = qi(capAC, ref iidBaseAC, out baseAC);
                Console.WriteLine("[DEBUG] QI(IAudioClient) on capAC: 0x" + hrQi.ToString("X8") + " ptr=0x" + baseAC.ToString("X"));
                if (hrQi == 0 && baseAC != IntPtr.Zero)
                {
                    var getSvc2 = GetFn<D_IAudioClient_GetServiceRef>(baseAC, SLOT_AC_GetService);
                    Guid iidCap2 = new Guid("C8ADBD64-E71E-48CC-9A5E-F4CA8FF8435D");
                    IntPtr cap2;
                    int hrSvc2 = getSvc2(baseAC, ref iidCap2, out cap2);
                    Console.WriteLine("[DEBUG] GetService on QI'd base: 0x" + hrSvc2.ToString("X8") + " ptr=0x" + cap2.ToString("X"));
                }
            }

            // Try GetService with proper initialization
            var getSvc = GetFn<D_IAudioClient_GetService>(capAC, SLOT_AC_GetService);
            var getSvcRef = GetFn<D_IAudioClient_GetServiceRef>(capAC, SLOT_AC_GetService);
            IntPtr capCli = IntPtr.Zero;
            int hrSvc = -1;

            // Try ref Guid version
            Guid iidCapRef = new Guid("C8ADBD64-E71E-48CC-9A5E-F4CA8FF8435D");
            IntPtr testResultRef;
            hrSvc = getSvcRef(capAC, ref iidCapRef, out testResultRef);
            Console.WriteLine("[DEBUG] GetService(ref Guid): 0x" + hrSvc.ToString("X8") + " ptr=0x" + testResultRef.ToString("X"));
            if (hrSvc == 0) { capCli = testResultRef; }

            if (hrSvc != 0)
            {
                // Try IntPtr version
                Guid iidCap = new Guid("C8ADBD64-E71E-48CC-9A5E-F4CA8FF8435D");
                IntPtr iidCapPtr = Marshal.AllocHGlobal(16);
                Marshal.StructureToPtr(iidCap, iidCapPtr, false);
                try
                {
                    IntPtr testResult;
                    hrSvc = getSvc(capAC, iidCapPtr, out testResult);
                    Console.WriteLine("[DEBUG] GetService(IntPtr): 0x" + hrSvc.ToString("X8") + " ptr=0x" + testResult.ToString("X"));
                    if (hrSvc == 0) capCli = testResult;
                }
                finally
                {
                    Marshal.FreeHGlobal(iidCapPtr);
                }
            }

            Check(hrSvc, "GetService");

            Console.WriteLine();
            Console.WriteLine("=== Left channel: " + leftName + " ===");
            IntPtr leftAC, leftRC;
            SetupRender(leftDev, mixFmt, out leftAC, out leftRC);
            var startLeft = GetFn<D_IAudioClient_Start>(leftAC, SLOT_AC_Start);
            Check(startLeft(leftAC), "Start left");

            Console.WriteLine("=== Right channel: " + rightName + " ===");
            IntPtr rightAC, rightRC;
            SetupRender(rightDev, mixFmt, out rightAC, out rightRC);
            var startRight = GetFn<D_IAudioClient_Start>(rightAC, SLOT_AC_Start);
            Check(startRight(rightAC), "Start right");

            var startCap = GetFn<D_IAudioClient_Start>(capAC, SLOT_AC_Start);
            Check(startCap(capAC), "Start capture");

            Console.WriteLine();
            Console.WriteLine("=== Routing active! Ctrl+C to stop ===");
            Console.WriteLine();

            // Get function pointers
            var getNextPkt = GetFn<D_Capture_GetNextPacketSize>(capCli, SLOT_Cap_GetNextPacketSize);
            var capGetBuf = GetFn<D_Capture_GetBuffer>(capCli, SLOT_Cap_GetBuffer);
            var capRelBuf = GetFn<D_Capture_ReleaseBuffer>(capCli, SLOT_Cap_ReleaseBuffer);
            var leftGetPad = GetFn<D_IAudioClient_GetCurrentPadding>(leftAC, SLOT_AC_GetCurrentPadding);
            var rightGetPad = GetFn<D_IAudioClient_GetCurrentPadding>(rightAC, SLOT_AC_GetCurrentPadding);
            var leftRenGet = GetFn<D_Render_GetBuffer>(leftRC, SLOT_Ren_GetBuffer);
            var leftRenRel = GetFn<D_Render_ReleaseBuffer>(leftRC, SLOT_Ren_ReleaseBuffer);
            var rightRenGet = GetFn<D_Render_GetBuffer>(rightRC, SLOT_Ren_GetBuffer);
            var rightRenRel = GetFn<D_Render_ReleaseBuffer>(rightRC, SLOT_Ren_ReleaseBuffer);

            int channels = mixFmt.nChannels;
            int srcFrameSize = channels * (mixFmt.wBitsPerSample / 8);

            while (running)
            {
                try
                {
                    uint pktSize;
                    if (getNextPkt(capCli, out pktSize) != 0 || pktSize == 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    uint frames = pktSize / (uint)srcFrameSize;
                    if (frames == 0) continue;

                    IntPtr bufPtr;
                    uint framesRead, flags;
                    long devPos, qpcPos;
                    int hr = capGetBuf(capCli, out bufPtr, out framesRead, out flags, out devPos, out qpcPos);
                    if (hr != 0 || framesRead == 0)
                    {
                        if (hr == 0) capRelBuf(capCli, 0);
                        continue;
                    }

                    int totalBytes = (int)framesRead * srcFrameSize;
                    byte[] src = new byte[totalBytes];
                    Marshal.Copy(bufPtr, src, 0, totalBytes);
                    capRelBuf(capCli, framesRead);

                    byte[] leftData = new byte[framesRead * 2];
                    byte[] rightData = new byte[framesRead * 2];

                    if (mixFmt.wBitsPerSample == 32 && mixFmt.wFormatTag == WAVE_FORMAT_IEEE_FLOAT)
                    {
                        for (uint f = 0; f < framesRead; f++)
                        {
                            int off = (int)f * srcFrameSize;
                            float l = BitConverter.ToSingle(src, off);
                            float r = channels >= 2 ? BitConverter.ToSingle(src, off + 4) : l;
                            WriteI16(leftData, f * 2, ClampF(l * 32767f));
                            WriteI16(rightData, f * 2, ClampF(r * 32767f));
                        }
                    }
                    else if (mixFmt.wBitsPerSample == 16)
                    {
                        for (uint f = 0; f < framesRead; f++)
                        {
                            int off = (int)f * srcFrameSize;
                            short l = BitConverter.ToInt16(src, off);
                            short r = channels >= 2 ? BitConverter.ToInt16(src, off + 2) : l;
                            WriteI16(leftData, f * 2, l);
                            WriteI16(rightData, f * 2, r);
                        }
                    }
                    else if (mixFmt.wBitsPerSample == 24)
                    {
                        for (uint f = 0; f < framesRead; f++)
                        {
                            int off = (int)f * srcFrameSize;
                            int lRaw = src[off] | (src[off + 1] << 8) | (src[off + 2] << 16);
                            if ((lRaw & 0x800000) != 0) lRaw |= unchecked((int)0xFF000000);
                            short l16 = (short)(lRaw >> 8);
                            short r16 = l16;
                            if (channels >= 2)
                            {
                                int rRaw = src[off + 3] | (src[off + 4] << 8) | (src[off + 5] << 16);
                                if ((rRaw & 0x800000) != 0) rRaw |= unchecked((int)0xFF000000);
                                r16 = (short)(rRaw >> 8);
                            }
                            WriteI16(leftData, f * 2, l16);
                            WriteI16(rightData, f * 2, r16);
                        }
                    }
                    else if (mixFmt.wBitsPerSample == 32 && mixFmt.wFormatTag == WAVE_FORMAT_PCM)
                    {
                        for (uint f = 0; f < framesRead; f++)
                        {
                            int off = (int)f * srcFrameSize;
                            int l = BitConverter.ToInt32(src, off);
                            int r = channels >= 2 ? BitConverter.ToInt32(src, off + 4) : l;
                            WriteI16(leftData, f * 2, (short)(l >> 16));
                            WriteI16(rightData, f * 2, (short)(r >> 16));
                        }
                    }

                    SendRender(leftAC, leftRenGet, leftRenRel, leftGetPad, leftData, framesRead);
                    SendRender(rightAC, rightRenGet, rightRenRel, rightGetPad, rightData, framesRead);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: " + ex.Message);
                    Thread.Sleep(10);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Stopping...");
            var stopCap = GetFn<D_IAudioClient_Stop>(capAC, SLOT_AC_Stop);
            var stopLeft = GetFn<D_IAudioClient_Stop>(leftAC, SLOT_AC_Stop);
            var stopRight = GetFn<D_IAudioClient_Stop>(rightAC, SLOT_AC_Stop);
            stopCap(capAC);
            stopLeft(leftAC);
            stopRight(rightAC);
            Console.WriteLine("Stopped.");
        }

        static IntPtr Activate(IntPtr device, Guid iid)
        {
            var activate = GetFn<D_Activate>(device, SLOT_Dev_Activate);

            // Test with known working IID first (IAudioSessionManager)
            Guid iidTest = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            IntPtr testResult;
            int hrTest = activate(device, ref iidTest, CLSCTX_INPROC_SERVER, IntPtr.Zero, out testResult);
            Console.WriteLine("[DEBUG] Activate(IID_IAudioSessionManager) hr=0x" + hrTest.ToString("X8") + " ptr=0x" + testResult.ToString("X"));

            // Also try IAudioClient3
            Guid iidAC3 = new Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
            IntPtr ac3Result;
            int hrAC3 = activate(device, ref iidAC3, CLSCTX_INPROC_SERVER, IntPtr.Zero, out ac3Result);
            Console.WriteLine("[DEBUG] Activate(IID_IAudioClient3) hr=0x" + hrAC3.ToString("X8") + " ptr=0x" + ac3Result.ToString("X"));

            IntPtr result;
            int hr = activate(device, ref iid, CLSCTX_INPROC_SERVER, IntPtr.Zero, out result);
            Console.WriteLine("[DEBUG] Activate(target) IID=" + iid.ToString("B") + " hr=0x" + hr.ToString("X8") + " ptr=0x" + result.ToString("X"));

            Check(hr, "Activate");
            return result;
        }

        static IntPtr GetService(IntPtr audioClient, Guid iid)
        {
            var getSvc = GetFn<D_IAudioClient_GetService>(audioClient, SLOT_AC_GetService);
            IntPtr iidPtr = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.StructureToPtr(iid, iidPtr, false);
                IntPtr result;
                int hr = getSvc(audioClient, iidPtr, out result);
                Console.WriteLine("[DEBUG] GetService IID=" + iid.ToString("B") + " hr=0x" + hr.ToString("X8") + " ptr=0x" + result.ToString("X"));
                Check(hr, "GetService");
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(iidPtr);
            }
        }

        static void SetupRender(IntPtr device, WAVEFORMATEX srcFmt, out IntPtr ac, out IntPtr rc)
        {
            ac = Activate(device, IID_IAudioClient);

            var getMixFmt = GetFn<D_IAudioClient_GetMixFormat>(ac, SLOT_AC_GetMixFormat);
            IntPtr mixPtr;
            Check(getMixFmt(ac, out mixPtr), "GetMixFormat render");
            WAVEFORMATEX fmt = Marshal.PtrToStructure<WAVEFORMATEX>(mixPtr);
            CoTaskMemFree(mixPtr);

            fmt.nChannels = 1;
            fmt.wBitsPerSample = 16;
            fmt.wFormatTag = WAVE_FORMAT_PCM;
            fmt.nBlockAlign = 2;
            fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * 2;
            fmt.cbSize = 0;

            Console.WriteLine("  Output: " + fmt.nChannels + "ch " + fmt.nSamplesPerSec + "Hz " + fmt.wBitsPerSample + "bit");

            IntPtr fmtPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WAVEFORMATEX)));
            Marshal.StructureToPtr(fmt, fmtPtr, false);
            try
            {
                var initFn = GetFn<D_IAudioClient_Initialize>(ac, SLOT_AC_Initialize);
                Guid empty = Guid.Empty;
                Check(initFn(ac, AUDCLNT_SHAREMODE_SHARED,
                    AUDCLNT_STREAMFLAGS_RATEADJUST | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM,
                    0, 0, fmtPtr, ref empty), "Initialize render");
            }
            finally
            {
                Marshal.FreeHGlobal(fmtPtr);
            }

            rc = GetService(ac, IID_IAudioRenderClient);
        }

        static void SendRender(IntPtr ac, D_Render_GetBuffer getBuf, D_Render_ReleaseBuffer relBuf,
            D_IAudioClient_GetCurrentPadding getPad, byte[] data, uint frames)
        {
            uint padding;
            if (getPad(ac, out padding) != 0) return;
            if (padding > frames) return;

            IntPtr bufPtr;
            if (getBuf(ac, frames, out bufPtr) != 0) return;
            Marshal.Copy(data, 0, bufPtr, data.Length);
            relBuf(ac, frames, 0);
        }

        static string GetDeviceName(IntPtr device)
        {
            try
            {
                var openStore = GetFn<D_OpenPropertyStore>(device, SLOT_Dev_OpenPropertyStore);
                IntPtr storePtr;
                if (openStore(device, STGM_READ, out storePtr) != 0) return "Unknown";

                var getValue = GetFn<D_PropStore_GetValue>(storePtr, SLOT_Prop_GetValue);
                PROPERTYKEY key = new PROPERTYKEY();
                key.fmtid = PKEY_Device_FriendlyName_FmtId;
                key.pid = 14;

                PROPVARIANT pv;
                if (getValue(storePtr, ref key, out pv) != 0) return "Unknown";
                string name = Marshal.PtrToStringUni(pv.pwszVal);
                PropVariantClear(ref pv);
                return name ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        static string GetDeviceId(IntPtr device)
        {
            try
            {
                var getId = GetFn<D_GetId>(device, SLOT_Dev_GetId);
                IntPtr idPtr;
                if (getId(device, out idPtr) != 0) return "";
                string id = Marshal.PtrToStringUni(idPtr);
                CoTaskMemFree(idPtr);
                return id ?? "";
            }
            catch
            {
                return "";
            }
        }

        static short ClampF(float v)
        {
            if (v > 32767f) return 32767;
            if (v < -32768f) return -32768;
            return (short)v;
        }

        static void WriteI16(byte[] buf, uint off, short v)
        {
            buf[off] = (byte)(v & 0xFF);
            buf[off + 1] = (byte)((v >> 8) & 0xFF);
        }
    }
}
