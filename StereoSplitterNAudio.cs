using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace StereoSplitter
{
    class Program
    {
        static bool running = true;

        static void Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            try { Console.InputEncoding = System.Text.Encoding.UTF8; } catch { }

            Console.WriteLine("============================================================");
            Console.WriteLine("  Stereo Splitter - Dual USB Audio Card (NAudio)");
            Console.WriteLine("  Split stereo: L -> Device 1 (left), R -> Device 2 (right)");
            Console.WriteLine("============================================================");
            Console.WriteLine();

            try
            {
                var enumerator = new MMDeviceEnumerator();

                // Enumerate render devices
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                var deviceList = new List<MMDevice>();
                var nameList = new List<string>();

                Console.WriteLine("=== Audio Output Devices ===");
                for (int i = 0; i < devices.Count; i++)
                {
                    var dev = devices[i];
                    deviceList.Add(dev);
                    nameList.Add(dev.FriendlyName);
                    Console.WriteLine("  [" + i + "] " + dev.FriendlyName);
                    Console.WriteLine("       ID: " + dev.ID);
                }
                Console.WriteLine();

                // Default device
                var defaultDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                Console.WriteLine("Default output: " + defaultDev.FriendlyName);
                int defaultIdx = deviceList.FindIndex(d => d.ID == defaultDev.ID);
                Console.WriteLine();

                // Manual selection or auto-detect USB
                int leftIdx = -1, rightIdx = -1;
                bool autoMode = false;

                // Check command line args: StereoSplitter.exe <leftIdx> <rightIdx>
                if (args.Length >= 2)
                {
                    int li2, ri2;
                    if (int.TryParse(args[0], out li2) && int.TryParse(args[1], out ri2) &&
                        li2 >= 0 && li2 < deviceList.Count && ri2 >= 0 && ri2 < deviceList.Count && li2 != ri2)
                    {
                        leftIdx = li2;
                        rightIdx = ri2;
                        autoMode = true;
                        Console.WriteLine("Using command line args: L=" + leftIdx + " R=" + rightIdx);
                    }
                }

                if (!autoMode)
                {
                    // Auto-detect USB devices
                    var usbDevices = new List<int>();
                    for (int i = 0; i < nameList.Count; i++)
                    {
                        string lower = nameList[i].ToLower();
                        if (lower.Contains("usb") || lower.Contains("speaker") || lower.Contains("headphone") || lower.Contains("耳机"))
                            usbDevices.Add(i);
                    }

                    if (usbDevices.Count >= 2)
                    {
                        Console.WriteLine("Auto-detected audio devices:");
                        foreach (int i in usbDevices)
                            Console.WriteLine("  [" + i + "] " + nameList[i]);
                        Console.WriteLine();
                        Console.WriteLine("Usage: StereoSplitter.exe <leftIdx> <rightIdx>");
                        Console.WriteLine("Example: StereoSplitter.exe " + usbDevices[0] + " " + usbDevices[1]);
                        Console.WriteLine();

                        // Interactive mode - default to first two USB devices if user just presses enter
                        Console.Write("Use [" + usbDevices[0] + "] L / [" + usbDevices[1] + "] R? (y/n, or enter indices): ");
                        string resp = (Console.ReadLine() ?? "").Trim().ToLower();
                        if (resp == "y" || resp == "")
                        {
                            leftIdx = usbDevices[0];
                            rightIdx = usbDevices[1];
                        }
                        else if (resp.Contains(" "))
                        {
                            string[] parts = resp.Split(' ');
                            int li3, ri3;
                            if (parts.Length >= 2 && int.TryParse(parts[0], out li3) && int.TryParse(parts[1], out ri3))
                            {
                                leftIdx = li3;
                                rightIdx = ri3;
                            }
                        }
                    }

                    if (leftIdx < 0 || rightIdx < 0)
                    {
                        Console.Write("Left device index [" + (defaultIdx >= 0 ? defaultIdx : 0) + "]: ");
                        string s = Console.ReadLine();
                        int li;
                        leftIdx = (!string.IsNullOrWhiteSpace(s) && int.TryParse(s, out li)) ? li : (defaultIdx >= 0 ? defaultIdx : 0);

                        Console.Write("Right device index: ");
                        s = Console.ReadLine();
                        int ri;
                        rightIdx = (int.TryParse(s, out ri)) ? ri : -1;
                    }

                    // Validate indices
                    while (leftIdx < 0 || leftIdx >= deviceList.Count)
                    {
                        Console.Write("Left device index (0-" + (deviceList.Count - 1) + "): ");
                        int li4;
                        if (int.TryParse(Console.ReadLine(), out li4)) leftIdx = li4;
                    }
                    while (rightIdx < 0 || rightIdx >= deviceList.Count || rightIdx == leftIdx)
                    {
                        Console.Write("Right device index (0-" + (deviceList.Count - 1) + ", not " + leftIdx + "): ");
                        int ri4;
                        if (int.TryParse(Console.ReadLine(), out ri4)) rightIdx = ri4;
                    }
                }

                Console.WriteLine();
                Console.WriteLine("Left:  [" + leftIdx + "] " + nameList[leftIdx]);
                Console.WriteLine("Right: [" + rightIdx + "] " + nameList[rightIdx]);

                // Check if default output conflicts with targets (would cause feedback loop)
                if (defaultIdx == leftIdx || defaultIdx == rightIdx)
                {
                    Console.WriteLine();
                    Console.WriteLine("!!! 捕获源（默认输出）与目标设备冲突，会造成声音回环 !!!");
                    Console.WriteLine("当前默认输出: [" + defaultIdx + "] " + nameList[defaultIdx]);
                    Console.WriteLine();
                    Console.WriteLine("请先把 Windows 默认输出改为其他设备（步骤）:");
                    Console.WriteLine("  右键任务栏喇叭图标 → 声音设置 → 输出 → 选择设备");
                    Console.WriteLine("  可选设备:");
                    for (int i = 0; i < nameList.Count; i++)
                    {
                        if (i != leftIdx && i != rightIdx)
                            Console.WriteLine("    [" + i + "] " + nameList[i]);
                    }
                    Console.WriteLine();
                    Console.WriteLine("改完后重新运行本程序。");
                    Console.WriteLine();
                    Console.WriteLine("Press Enter to exit...");
                    Console.ReadLine();
                    return;
                }

                Console.WriteLine("捕获源（默认输出）: [" + defaultIdx + "] " + nameList[defaultIdx]);
                Console.WriteLine();
                Console.Write("Start routing? (y/n): ");
                if ((Console.ReadLine() ?? "").Trim().ToLower() != "y")
                {
                    Console.WriteLine("Cancelled.");
                    return;
                }

                RunRouting(deviceList[leftIdx], deviceList[rightIdx]);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                Console.WriteLine();
                Console.WriteLine("Press Enter to exit...");
                Console.ReadLine();
            }
        }

        static void RunRouting(MMDevice leftDev, MMDevice rightDev)
        {
            Console.WriteLine();
            Console.WriteLine("=== Setup loopback capture ===");

            // Use default render device for loopback capture
            var enumerator = new MMDeviceEnumerator();
            var captureDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            Console.WriteLine("Capturing from: " + captureDev.FriendlyName);

            var loopback = new WasapiLoopbackCapture(captureDev);
            Console.WriteLine("Capture format: " + loopback.WaveFormat);

            // Output format: mono float at same sample rate
            var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(loopback.WaveFormat.SampleRate, 1);

            // Use BufferedWaveProvider for proper buffering
            var leftBuffered = new BufferedWaveProvider(outFormat) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true };
            var rightBuffered = new BufferedWaveProvider(outFormat) { BufferDuration = TimeSpan.FromMilliseconds(500), DiscardOnBufferOverflow = true };

            var leftOut = new WasapiOut(leftDev, AudioClientShareMode.Shared, true, 50);
            var rightOut = new WasapiOut(rightDev, AudioClientShareMode.Shared, true, 50);

            leftOut.Init(new AlwaysFullSampleProvider(leftBuffered));
            rightOut.Init(new AlwaysFullSampleProvider(rightBuffered));

            long totalCaptured = 0;
            int dataEvents = 0;
            loopback.DataAvailable += (s, e) =>
            {
                totalCaptured += e.BytesRecorded;
                dataEvents++;
                SplitAndBuffer(e.Buffer, e.BytesRecorded, loopback.WaveFormat, 0, leftBuffered);
                SplitAndBuffer(e.Buffer, e.BytesRecorded, loopback.WaveFormat, 1, rightBuffered);
            };

            loopback.StartRecording();

            // Pre-buffer 100ms before starting playback
            Console.WriteLine("Pre-buffering...");
            int waitMs = 0;
            while (leftBuffered.BufferedBytes < 19200 && waitMs < 2000) // 19200B = 100ms mono float 48k
            {
                Thread.Sleep(20);
                waitMs += 20;
            }
            Console.WriteLine("Pre-buffered: left=" + leftBuffered.BufferedBytes + "B right=" + rightBuffered.BufferedBytes + "B");

            Console.WriteLine("=== Left channel: " + leftDev.FriendlyName + " ===");
            leftOut.Play();
            Console.WriteLine("=== Right channel: " + rightDev.FriendlyName + " ===");
            rightOut.Play();

            Console.WriteLine();
            Console.WriteLine("=== Routing active! Ctrl+C to stop ===");
            Console.WriteLine();

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; running = false; };

            int tick = 0;
            while (running)
            {
                Thread.Sleep(1000);
                tick++;
                Console.WriteLine("[t=" + tick + "] leftBuf=" + leftBuffered.BufferedBytes + "B rightBuf=" + rightBuffered.BufferedBytes + "B");
            }

            Console.WriteLine();
            Console.WriteLine("Stopping...");
            loopback.StopRecording();
            leftOut.Stop();
            rightOut.Stop();
            loopback.Dispose();
            leftOut.Dispose();
            rightOut.Dispose();
            Console.WriteLine("Stopped.");
        }

        static void SplitAndBuffer(byte[] data, int bytesRecorded, WaveFormat srcFmt, int channel, BufferedWaveProvider target)
        {
            int bytesPerSample = srcFmt.BitsPerSample / 8;
            int channels = srcFmt.Channels;
            int frameSize = bytesPerSample * channels;
            int outBytesPerSample = 4; // float32
            int outFrames = bytesRecorded / frameSize;
            byte[] outBuf = new byte[outFrames * outBytesPerSample];

            for (int f = 0; f < outFrames; f++)
            {
                int srcOff = f * frameSize + channel * bytesPerSample;
                float sample;
                if (srcFmt.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    sample = BitConverter.ToSingle(data, srcOff);
                }
                else if (srcFmt.BitsPerSample == 16)
                {
                    sample = BitConverter.ToInt16(data, srcOff) / 32768f;
                }
                else
                {
                    sample = 0f;
                }
                Buffer.BlockCopy(BitConverter.GetBytes(sample), 0, outBuf, f * outBytesPerSample, outBytesPerSample);
            }

            target.AddSamples(outBuf, 0, outBuf.Length);
        }
    }

    // Wraps BufferedWaveProvider and always returns full buffer (silence padding)
    class AlwaysFullSampleProvider : ISampleProvider
    {
        readonly BufferedWaveProvider source;
        readonly WaveFormat fmt;
        readonly byte[] tempBuf;

        public AlwaysFullSampleProvider(BufferedWaveProvider source)
        {
            this.source = source;
            this.fmt = source.WaveFormat;
            this.tempBuf = new byte[65536];
        }

        public WaveFormat WaveFormat { get { return fmt; } }

        public int Read(float[] samples, int offset, int count)
        {
            int bytesNeeded = count * 4; // float32
            int bytesToRead = Math.Min(bytesNeeded, tempBuf.Length);
            bytesToRead = Math.Min(bytesToRead, source.BufferedBytes);

            int bytesRead = 0;
            if (bytesToRead > 0)
            {
                bytesRead = source.Read(tempBuf, 0, bytesToRead);
            }

            int samplesRead = bytesRead / 4;
            for (int i = 0; i < samplesRead; i++)
            {
                samples[offset + i] = BitConverter.ToSingle(tempBuf, i * 4);
            }
            // Fill rest with silence
            for (int i = samplesRead; i < count; i++)
                samples[offset + i] = 0f;
            return count;
        }
    }
}
