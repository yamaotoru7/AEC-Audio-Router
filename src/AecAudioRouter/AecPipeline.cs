namespace AecAudioRouter;

internal sealed class AecPipeline : IDisposable
{
    public const int AlignmentDelayMs = 40;
    private readonly object gate = new();
    private readonly AudioFrameStream referenceStream;
    private readonly AudioFrameStream microphoneStream;
    private Aec3Processor processor;
    private readonly Queue<AudioFrame> references = new();
    private readonly Queue<AudioFrame> microphones = new();
    private readonly float[] alignedReference = new float[Aec3Processor.FrameSamples];
    private readonly float[] bypass = new float[Aec3Processor.FrameSamples];
    private volatile bool enabled = true;
    private AudioFrame? currentReference;
    private readonly RouteBuffer output;
    private readonly Meter meter;
    private bool disposed;
    private long missing;
    private long dropped;
    private long resets;
    private double lastMicTime = double.NaN;

    public AecPipeline(int microphoneRate, int referenceRate, RouteBuffer output, Meter meter)
    {
        processor = new Aec3Processor();
        try
        {
            microphoneStream = new AudioFrameStream(microphoneRate);
            try { referenceStream = new AudioFrameStream(referenceRate); }
            catch { microphoneStream.Dispose(); throw; }
        }
        catch { processor.Dispose(); throw; }
        this.output = output;
        this.meter = meter;
    }

    // When disabled, AEC3 keeps adapting on a copy so re-enabling takes effect
    // without reconvergence, while the unprocessed microphone is routed.
    public bool Enabled
    {
        get => enabled;
        set => enabled = value;
    }

    public (long Missing, long Dropped, long Resets) Status
    {
        get { lock (gate) return (missing, dropped, resets); }
    }

    public void AddReference(ReadOnlySpan<float> samples, double arrivalTime)
    {
        lock (gate)
        {
            if (disposed) return;
            referenceStream.Push(samples, arrivalTime, frame =>
            {
                references.Enqueue(frame);
                // Never retain more than half a second if capture stops.
                while (references.Count > 50) { references.Dequeue(); dropped++; }
            });
        }
    }

    public void AddMicrophone(ReadOnlySpan<float> samples, double arrivalTime)
    {
        lock (gate)
        {
            if (disposed) return;
            microphoneStream.Push(samples, arrivalTime, frame =>
            {
                if (!double.IsNaN(lastMicTime) && Math.Abs(frame.Time - lastMicTime - 0.01) > 0.1)
                {
                    microphones.Clear();
                    processor.Dispose();
                    processor = new Aec3Processor();
                    resets++;
                }
                lastMicTime = frame.Time;
                microphones.Enqueue(frame);
                // Let reference callbacks arrive before processing the corresponding capture.
                if (microphones.Count <= AlignmentDelayMs / 10) return;
                Process(microphones.Dequeue());
            });
        }
    }

    private void Process(AudioFrame microphone)
    {
        // Interpolate the reference on the microphone clock, using adjacent frame
        // timestamps to compensate small device-clock differences continuously.
        // The sinc converter has already band-limited both streams to 48 kHz.
        bool incomplete = false;
        for (int i = 0; i < alignedReference.Length; i++)
        {
            double time = microphone.Time + (double)i / Aec3Processor.SampleRate;
            while (references.TryPeek(out var ready) && ready.Time <= time)
            {
                if (currentReference is not null && ready.Time < time - 0.02) dropped++;
                currentReference = references.Dequeue();
            }
            double duration = 0.01;
            bool hasNext = references.TryPeek(out var next);
            if (currentReference is not null && hasNext && next!.Time - currentReference.Time is > 0.009 and < 0.011)
                duration = next.Time - currentReference.Time;
            double position = currentReference is null ? -1 : (time - currentReference.Time) / duration * Aec3Processor.FrameSamples;
            if (position < 0 || position >= Aec3Processor.FrameSamples)
            {
                alignedReference[i] = 0;
                incomplete = true;
                continue;
            }
            int index = (int)position;
            float first = currentReference!.Samples[index];
            float second = index + 1 < Aec3Processor.FrameSamples ? currentReference.Samples[index + 1]
                : hasNext && next!.Time - currentReference.Time < 0.011 ? next.Samples[0] : 0;
            alignedReference[i] = first + (second - first) * (float)(position - index);
        }
        if (incomplete) missing++;
        bool apply = enabled;
        if (!apply) Array.Copy(microphone.Samples, bypass, bypass.Length);
        processor.Process(alignedReference, microphone.Samples);
        var routed = apply ? microphone.Samples : bypass;
        output.Write(routed);
        var level = new LevelWindow();
        foreach (float sample in routed) level.Add(sample);
        meter.Add(level);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            processor.Dispose();
            referenceStream.Dispose();
            microphoneStream.Dispose();
            references.Clear();
            currentReference = null;
            microphones.Clear();
        }
    }
}
