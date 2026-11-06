using NAudio.Wave;

namespace AecAudioRouter;

// A bounded, live mono stream. Favor current audio if the reader falls behind.
internal sealed class RouteBuffer(int sampleRate, int capacity) : ISampleProvider
{
    private readonly object gate = new();
    private readonly float[] samples = new float[capacity];
    private int head;
    private int count;
    private long dropped;
    private long missing;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

    public (double QueuedMs, long Dropped, long Missing) Status
    {
        get
        {
            lock (gate)
                return (1000.0 * count / sampleRate, dropped, missing);
        }
    }

    public void Write(ReadOnlySpan<float> source)
    {
        lock (gate)
        {
            int overflow = Math.Max(0, count + source.Length - samples.Length);
            int evicted = Math.Min(count, overflow);
            head = (head + evicted) % samples.Length;
            count -= evicted;
            dropped += overflow;
            source = source[(overflow - evicted)..];

            int tail = (head + count) % samples.Length;
            int first = Math.Min(source.Length, samples.Length - tail);
            source[..first].CopyTo(samples.AsSpan(tail));
            source[first..].CopyTo(samples);
            count += source.Length;
        }
    }

    public int Read(float[] buffer, int offset, int requested)
    {
        lock (gate)
        {
            int read = Math.Min(count, requested);
            int first = Math.Min(read, samples.Length - head);
            samples.AsSpan(head, first).CopyTo(buffer.AsSpan(offset));
            samples.AsSpan(0, read - first).CopyTo(buffer.AsSpan(offset + first));
            head = (head + read) % samples.Length;
            count -= read;
            Array.Clear(buffer, offset + read, requested - read);
            missing += requested - read;
            return requested;
        }
    }
}
