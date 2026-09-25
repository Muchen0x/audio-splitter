using System;
using System.Threading;
using NAudio.Wave;
using NAudio.CoreAudioApi;

class VolumeTest
{
    static volatile float peak = 0;

    static void Main()
    {
        var en = new MMDeviceEnumerator();
        var coll = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        MMDevice cable = null;
        for (int i = 0; i < coll.Count; i++)
            if (coll[i].FriendlyName.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0)
                cable = coll[i];
        if (cable == null) { Console.WriteLine("CABLE not found"); return; }

        Console.WriteLine("Device: " + cable.FriendlyName);

        var ev = cable.AudioEndpointVolume;
        Console.WriteLine("Initial: scalar=" + ev.MasterVolumeLevelScalar + " mute=" + ev.Mute);
        ev.Mute = false;

        var cap = new WasapiLoopbackCapture(cable);
        Console.WriteLine("Capture fmt: " + cap.WaveFormat);
        cap.DataAvailable += (s, e) =>
        {
            int n = e.BytesRecorded / 4;
            for (int i = 0; i < n; i++)
            {
                float v = Math.Abs(BitConverter.ToSingle(e.Buffer, i * 4));
                if (v > peak) peak = v;
            }
        };

        var gen = new SineProvider(cap.WaveFormat, 440f, 0.5f);
        var output = new WasapiOut(cable, AudioClientShareMode.Shared, true, 100);
        output.Init(gen);

        cap.StartRecording();
        output.Play();
        Thread.Sleep(1000);

        float p10 = Measure(ev, 1.0f);
        float p05 = Measure(ev, 0.5f);
        float p00 = Measure(ev, 0.0f);

        output.Stop();
        cap.StopRecording();
        ev.MasterVolumeLevelScalar = 1.0f;

        Console.WriteLine();
        Console.WriteLine("peak@100% = " + p10.ToString("F4"));
        Console.WriteLine("peak@50%  = " + p05.ToString("F4"));
        Console.WriteLine("peak@0%   = " + p00.ToString("F4"));
        Console.WriteLine("ratio(50/100) = " + (p10 > 0 ? (p05 / p10).ToString("F3") : "n/a"));
        if (p10 > 0 && p05 / p10 < 0.7f)
            Console.WriteLine("RESULT: endpoint volume AFFECTS loopback capture");
        else
            Console.WriteLine("RESULT: endpoint volume does NOT affect loopback capture");
    }

    static float Measure(AudioEndpointVolume ev, float vol)
    {
        ev.MasterVolumeLevelScalar = vol;
        Thread.Sleep(600);
        peak = 0;
        Thread.Sleep(1000);
        return peak;
    }
}

class SineProvider : ISampleProvider
{
    readonly WaveFormat fmt;
    readonly float freq, amp;
    double pos = 0;

    public SineProvider(WaveFormat fmt, float freq, float amp)
    {
        this.fmt = fmt;
        this.freq = freq;
        this.amp = amp;
    }

    public WaveFormat WaveFormat { get { return fmt; } }

    public int Read(float[] samples, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            samples[offset + i] = (float)(amp * Math.Sin(2 * Math.PI * freq * pos / fmt.SampleRate));
            pos++;
        }
        return count;
    }
}
