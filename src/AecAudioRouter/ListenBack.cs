using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AecAudioRouter;

// Keeps the last seconds of the microphone before and after processing, in memory only,
// so they can be compared after the session stops. Written by AecPipeline (under its lock)
// one 10 ms frame at a time; Snapshot may be called from another thread.
internal sealed class ListenBackRecorder
{
    public const int Seconds = 30;
    private const int Rate = Aec3Processor.SampleRate;
    private const int Capacity = Seconds * Rate;
    // The longest processing delay: AEC3 and the slowest noise suppressor.
    public const int MaxDelaySamples = Aec3Processor.DelaySamples + INoiseSuppressor.MaxDelaySamples;
    // Holds the unprocessed microphone long enough to delay it by MaxDelaySamples.
    private const int HistoryMask = 4095;

    private readonly object gate = new();
    private readonly float[] before = new float[Capacity];
    private readonly float[] after = new float[Capacity];
    private readonly float[] history = new float[HistoryMask + 1];
    private long written;

    // `processed` lags `unprocessed` by `delay` samples (AEC3 and the noise suppressor).
    // The unprocessed track is delayed by the same amount, so both line up sample for sample
    // and switching between them during playback does not jump in time.
    public void Write(ReadOnlySpan<float> unprocessed, ReadOnlySpan<float> processed, int delay)
    {
        if (unprocessed.Length != processed.Length)
            throw new ArgumentException("処理前と処理後の長さが違います。");
        if (delay < 0 || delay > MaxDelaySamples)
            throw new ArgumentOutOfRangeException(nameof(delay));
        lock (gate)
        {
            for (int i = 0; i < unprocessed.Length; i++)
            {
                long n = written + i;
                history[n & HistoryMask] = unprocessed[i];
                int slot = (int)(n % Capacity);
                before[slot] = n >= delay ? history[(n - delay) & HistoryMask] : 0;
                after[slot] = processed[i];
            }
            written += unprocessed.Length;
        }
    }

    // The recorded audio in chronological order, or null if nothing was recorded.
    public ListenBackRecording? Snapshot()
    {
        lock (gate)
        {
            int length = (int)Math.Min(written, Capacity);
            if (length == 0)
                return null;
            int start = (int)((written - length) % Capacity);
            return new ListenBackRecording(Unroll(before, start, length), Unroll(after, start, length));
        }
    }

    private static float[] Unroll(float[] ring, int start, int length)
    {
        var result = new float[length];
        int first = Math.Min(length, ring.Length - start);
        ring.AsSpan(start, first).CopyTo(result);
        ring.AsSpan(0, length - first).CopyTo(result.AsSpan(first));
        return result;
    }
}

internal sealed record ListenBackRecording(float[] Before, float[] After)
{
    public const int SampleRate = Aec3Processor.SampleRate;

    public int Length => After.Length;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Length / SampleRate);

    // The peak of each of `columns` equal slices, for drawing a waveform.
    public static float[] Peaks(float[] samples, int columns)
    {
        var peaks = new float[columns];
        for (int column = 0; column < columns; column++)
        {
            int start = (int)((long)samples.Length * column / columns);
            int end = Math.Max(start + 1, (int)((long)samples.Length * (column + 1) / columns));
            float peak = 0;
            for (int i = start; i < end && i < samples.Length; i++)
                peak = Math.Max(peak, Math.Abs(samples[i]));
            peaks[column] = peak;
        }
        return peaks;
    }
}

// Plays one of the two tracks; the track can be switched at any time and playback carries on
// from the same position in the other one.
internal sealed class ListenBackSource(ListenBackRecording recording) : ISampleProvider
{
    private long position;
    private volatile bool processed = true;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(ListenBackRecording.SampleRate, 1);

    public bool Processed
    {
        get => processed;
        set => processed = value;
    }

    public double Progress => recording.Length == 0 ? 0 : (double)Interlocked.Read(ref position) / recording.Length;

    public TimeSpan Position => TimeSpan.FromSeconds((double)Interlocked.Read(ref position) / ListenBackRecording.SampleRate);

    public bool AtEnd => Interlocked.Read(ref position) >= recording.Length;

    public void Seek(double fraction) =>
        Interlocked.Exchange(ref position, (long)(Math.Clamp(fraction, 0, 1) * recording.Length));

    public int Read(float[] buffer, int offset, int count)
    {
        long start = Interlocked.Read(ref position);
        int read = (int)Math.Clamp(recording.Length - start, 0, count);
        if (read == 0)
            return 0;
        (processed ? recording.After : recording.Before).AsSpan((int)start, read).CopyTo(buffer.AsSpan(offset));
        // A seek from the UI thread meanwhile wins.
        Interlocked.CompareExchange(ref position, start + read, start);
        return read;
    }
}

// Plays a recording on the Windows default output device.
internal sealed class ListenBackPlayer : IDisposable
{
    private readonly MMDevice device;
    private readonly WasapiOut output;

    // Raised on the thread that created the player when playback ends or fails.
    public event Action<Exception?>? Stopped;

    public ListenBackPlayer(ListenBackSource source)
    {
        using var devices = new MMDeviceEnumerator();
        device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        try
        {
            output = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
            output.Init(new SampleToWaveProvider(source));
        }
        catch
        {
            output?.Dispose();
            device.Dispose();
            throw;
        }
        output.PlaybackStopped += (_, e) => Stopped?.Invoke(e.Exception);
    }

    public string DeviceName => device.FriendlyName;

    public void Play() => output.Play();

    public void Dispose()
    {
        output.Dispose();
        device.Dispose();
    }
}
