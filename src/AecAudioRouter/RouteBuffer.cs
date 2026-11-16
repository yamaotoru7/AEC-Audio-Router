namespace AecAudioRouter;

// A bounded, live mono stream. Favor current audio if the reader falls behind.
// Every sample carries the time its microphone sound was captured, so the reader
// can tell how old the audio is when it reaches the output device.
internal sealed class RouteBuffer(int sampleRate, int capacity)
{
    private readonly object gate = new();
    private readonly float[] samples = new float[capacity];
    private readonly double[] times = new double[capacity];
    private int head;
    private int count;
    private long dropped;
    private long missing;

    public int SampleRate => sampleRate;

    // Signalled on every write, so the output can pass the audio on without waiting
    // for its next device period.
    public AutoResetEvent Written { get; } = new(false);

    public int Count
    {
        get
        {
            lock (gate)
                return count;
        }
    }

    public (double QueuedMs, long Dropped, long Missing) Status
    {
        get
        {
            lock (gate)
                return (1000.0 * count / sampleRate, dropped, missing);
        }
    }

    // captured: when the first sample was captured (Stopwatch seconds), NaN if unknown.
    public void Write(ReadOnlySpan<float> source, double captured = double.NaN)
    {
        lock (gate)
        {
            int overflow = Math.Max(0, count + source.Length - samples.Length);
            int evicted = Math.Min(count, overflow);
            head = (head + evicted) % samples.Length;
            count -= evicted;
            dropped += overflow;
            int skipped = overflow - evicted;
            source = source[skipped..];
            captured += (double)skipped / sampleRate;

            for (int i = 0; i < source.Length; i++)
            {
                int slot = (head + count + i) % samples.Length;
                samples[slot] = source[i];
                times[slot] = captured + (double)i / sampleRate;
            }
            count += source.Length;
        }
        Written.Set();
    }

    // Takes up to destination.Length samples without padding. Returns how many were
    // taken and when the first of them was captured.
    public int Take(Span<float> destination, out double captured)
    {
        lock (gate)
        {
            int read = Math.Min(count, destination.Length);
            captured = read > 0 ? times[head] : double.NaN;
            int first = Math.Min(read, samples.Length - head);
            samples.AsSpan(head, first).CopyTo(destination);
            samples.AsSpan(0, read - first).CopyTo(destination[first..]);
            head = (head + read) % samples.Length;
            count -= read;
            return read;
        }
    }

    // Discards up to `requested` of the oldest samples to shorten the delay.
    public int Skip(int requested)
    {
        lock (gate)
        {
            int skipped = Math.Min(count, requested);
            head = (head + skipped) % samples.Length;
            count -= skipped;
            dropped += skipped;
            return skipped;
        }
    }

    // The output ran dry for this many samples and played silence.
    public void AddMissing(int samples)
    {
        lock (gate)
            missing += samples;
    }

    // Reads exactly `requested` samples, filling a shortfall with silence.
    public int Read(float[] buffer, int offset, int requested)
    {
        int read = Take(buffer.AsSpan(offset, requested), out _);
        Array.Clear(buffer, offset + read, requested - read);
        AddMissing(requested - read);
        return requested;
    }
}
