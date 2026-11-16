using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AecAudioRouter;

// Plays a RouteBuffer on a shared-mode WASAPI endpoint with as little delay as it can.
// Audio is written to the device as soon as it is produced rather than once per device
// period, and the amount left in the device when the engine reads (the slack) is kept
// just above what the timing jitter needs: an underrun raises the target, a long run
// without one lowers it, and slack above the target is trimmed from the queue.
internal sealed class RouteRenderer : IDisposable
{
    public const double MinMarginMs = 2;
    public const double MaxMarginMs = 20;
    private const double MarginStepMs = 2;
    private const double MarginDecaySeconds = 20;
    // How long the slack must stay above the target before it is trimmed.
    private const double TrimWindowSeconds = 1;
    private const double TrimHysteresisMs = 1;

    private readonly RouteBuffer source;
    private readonly AudioClient client;
    private readonly AudioRenderClient render;
    // The device position, which also counts what the engine has taken but not yet played.
    private readonly AudioClockClient clock;
    private readonly double clockFrequency;
    private readonly AutoResetEvent deviceEvent = new(false);
    private readonly Thread thread;
    private readonly Action<Exception> onError;
    private readonly int channels;
    private readonly int bufferFrames;
    private readonly int periodFrames;
    private readonly float[] mono;
    private readonly float[] interleaved;
    private readonly object statsGate = new();
    private volatile bool stopping;
    private bool disposed;

    // Audio thread only.
    // Before the first audio arrives the device plays silence by design, not by underrun.
    private bool playing;
    private double marginMs = MinMarginMs;
    private int windowSlack = int.MaxValue;
    private long windowStart;
    private long lastUnderrun;
    private long lastMarginChange;
    private int pendingSkip;
    private long lastDeviceEvent;
    private int paddingAfterDeviceEvent;
    private int writtenSinceDeviceEvent;
    private long writtenTotal;

    // Guarded by statsGate.
    private double latencySum;
    private int latencyCount;
    private double currentMarginMs = MinMarginMs;

    public RouteRenderer(MMDevice device, RouteBuffer source, Action<Exception> onError)
    {
        this.source = source;
        this.onError = onError;
        client = device.AudioClient;
        channels = client.MixFormat.Channels;
        // The engine converts the rate if the endpoint does not run at 48 kHz.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(source.SampleRate, channels);
        client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            0, 0, format, Guid.Empty);
        client.SetEventHandle(deviceEvent.SafeWaitHandle.DangerousGetHandle());
        bufferFrames = client.BufferSize;
        periodFrames = (int)Math.Max(1, client.DefaultDevicePeriod * source.SampleRate / 10_000_000);
        render = client.AudioRenderClient;
        clock = client.AudioClockClient;
        clockFrequency = clock.Frequency;
        mono = new float[bufferFrames];
        interleaved = new float[bufferFrames * channels];
        thread = new Thread(Run) { IsBackground = true, Name = "RouteRenderer", Priority = ThreadPriority.Highest };
    }

    public void Start() => thread.Start();

    // The mean delay since the last call, from microphone capture until the output device
    // plays the sample (null if nothing was played), and the current slack target.
    public (double? LatencyMs, double MarginMs) ReadAndReset()
    {
        lock (statsGate)
        {
            double? latency = latencyCount > 0 ? latencySum / latencyCount : null;
            latencySum = 0;
            latencyCount = 0;
            return (latency, currentMarginMs);
        }
    }

    private void Run()
    {
        IntPtr task = IntPtr.Zero;
        try
        {
            int index = 0;
            task = AvSetMmThreadCharacteristics("Pro Audio", ref index);
            client.Start();
            windowStart = lastUnderrun = lastMarginChange = lastDeviceEvent = Stopwatch.GetTimestamp();
            var handles = new WaitHandle[] { deviceEvent, source.Written };
            while (!stopping)
            {
                int signalled = WaitHandle.WaitAny(handles, 200);
                if (stopping) break;
                if (signalled == WaitHandle.WaitTimeout) continue;
                long now = Stopwatch.GetTimestamp();
                int padding = client.CurrentPadding;
                if (signalled == 0)
                    padding = OnDeviceEvent(padding, now);
                Fill(padding, now);
            }
        }
        catch (Exception error)
        {
            if (!stopping) onError(error);
        }
        finally
        {
            if (task != IntPtr.Zero) AvRevertMmThreadCharacteristics(task);
        }
    }

    // Runs right after the engine has read a period: `padding` is the slack.
    private int OnDeviceEvent(int padding, long now)
    {
        double periods = Math.Max(1, Math.Round(Stopwatch.GetElapsedTime(lastDeviceEvent, now).TotalSeconds * source.SampleRate / periodFrames));
        int consumed = paddingAfterDeviceEvent + writtenSinceDeviceEvent - padding;
        int starved = playing && padding == 0 ? (int)(periods * periodFrames) - consumed : 0;
        lastDeviceEvent = now;
        if (starved > 0)
        {
            source.AddMissing(starved);
            // One step per episode: a stalled microphone starves many periods in a row.
            if (Stopwatch.GetElapsedTime(lastUnderrun, now).TotalSeconds > 0.5)
                marginMs = Math.Min(MaxMarginMs, marginMs + MarginStepMs);
            lastUnderrun = lastMarginChange = now;
            pendingSkip = 0;
            windowSlack = int.MaxValue;
            windowStart = now;
        }
        else if (Stopwatch.GetElapsedTime(lastMarginChange, now).TotalSeconds >= MarginDecaySeconds && marginMs > MinMarginMs)
        {
            marginMs = Math.Max(MinMarginMs, marginMs - 1);
            lastMarginChange = now;
        }

        // Audio still queued counts as slack too: it reaches the device before the next read.
        int slack = padding + source.Count - pendingSkip;
        windowSlack = Math.Min(windowSlack, slack);
        if (Stopwatch.GetElapsedTime(windowStart, now).TotalSeconds >= TrimWindowSeconds)
        {
            int excess = windowSlack - (int)(marginMs * source.SampleRate / 1000);
            // Most of the slack sits in the device, out of reach: skip that much of the
            // audio still to come instead.
            if (excess > TrimHysteresisMs * source.SampleRate / 1000)
                pendingSkip += excess;
            windowSlack = int.MaxValue;
            windowStart = now;
        }
        lock (statsGate) currentMarginMs = marginMs;
        paddingAfterDeviceEvent = padding;
        writtenSinceDeviceEvent = 0;
        return padding;
    }

    private void Fill(int padding, long now)
    {
        int free = bufferFrames - padding;
        if (free <= 0) return;
        if (pendingSkip > 0)
            pendingSkip -= source.Skip(pendingSkip);
        int frames = source.Take(mono.AsSpan(0, free), out double captured);
        if (frames == 0) return;
        for (int frame = 0, i = 0; frame < frames; frame++)
            for (int channel = 0; channel < channels; channel++)
                interleaved[i++] = mono[frame];
        Marshal.Copy(interleaved, 0, render.GetBuffer(frames), frames * channels);
        render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
        long before = writtenTotal;
        writtenTotal += frames;
        writtenSinceDeviceEvent += frames;
        playing = true;
        if (!double.IsNaN(captured))
        {
            // The first sample plays once the device position reaches it: after the `padding`
            // frames still in the buffer and what the engine has taken but not played.
            double ahead = Math.Max(padding, before - PlayedFrames(now));
            double latency = (double)now / Stopwatch.Frequency + ahead / source.SampleRate - captured;
            lock (statsGate)
            {
                latencySum += latency * 1000;
                latencyCount++;
            }
        }
    }

    // The device position now, in frames of the stream format.
    private double PlayedFrames(long now)
    {
        clock.GetPosition(out ulong position, out ulong qpc100ns);
        double sampled = (double)now / Stopwatch.Frequency - qpc100ns / 1e7;
        return (position / clockFrequency + Math.Max(0, sampled)) * source.SampleRate;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stopping = true;
        deviceEvent.Set();
        if (thread.IsAlive) thread.Join(1000);
        try { client.Stop(); } catch (COMException) { }
        clock.Dispose();
        render.Dispose();
        client.Dispose();
        deviceEvent.Dispose();
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);
    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
