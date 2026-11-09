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
    private readonly float[] unprocessed = new float[Aec3Processor.FrameSamples];
    private readonly ListenBackRecorder? recorder;
    private volatile bool enabled = true;
    private INoiseSuppressor? suppressor;
    private NoiseSuppressionMode noiseSuppression;
    private AudioFrame? currentReference;
    // Null when the microphone output is "なし": only the meter and the recorder get the audio.
    private readonly RouteBuffer? output;
    private readonly Meter meter;
    private bool disposed;
    private long missing;
    private long dropped;
    private long resets;
    private double lastMicTime = double.NaN;

    public AecPipeline(int microphoneRate, int referenceRate, RouteBuffer? output, Meter meter, ListenBackRecorder? recorder = null)
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
        this.recorder = recorder;
    }

    // When disabled, AEC3 keeps adapting on a copy so re-enabling takes effect
    // without reconvergence, while the unprocessed microphone is routed.
    public bool Enabled
    {
        get => enabled;
        set => enabled = value;
    }

    // Applied after AEC3 (or to the raw microphone while AEC is bypassed), so it never
    // affects the echo path that AEC3 adapts to.
    public NoiseSuppressionMode NoiseSuppression
    {
        get { lock (gate) return noiseSuppression; }
    }

    // Can be switched while running. Each switch starts a fresh suppressor: a reused one
    // would replay the audio still in its delay line from when it was last active.
    // Loading a model takes a moment, so it happens outside the lock while audio flows.
    public void SetNoiseSuppression(NoiseSuppressionMode mode)
    {
        var next = mode == NoiseSuppressionMode.Off ? null : INoiseSuppressor.Create(mode);
        INoiseSuppressor? previous;
        lock (gate)
        {
            if (disposed)
            {
                next?.Dispose();
                throw new ObjectDisposedException(nameof(AecPipeline));
            }
            previous = suppressor;
            suppressor = next;
            noiseSuppression = mode;
        }
        // Outside the lock: releasing a model takes a moment, and audio must keep flowing.
        // The audio thread no longer sees it once the lock is released.
        previous?.Dispose();
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
                    ResetAfterDiscontinuity();
                lastMicTime = frame.Time;
                microphones.Enqueue(frame);
                // Let reference callbacks arrive before processing the corresponding capture.
                if (microphones.Count <= AlignmentDelayMs / 10) return;
                Process(microphones.Dequeue());
            });
        }
    }

    // The microphone jumped in time (capture restarted): drop what belongs to the old timeline.
    private void ResetAfterDiscontinuity()
    {
        // Created first, so a failure leaves the working processor in place.
        var fresh = new Aec3Processor();
        processor.Dispose();
        processor = fresh;
        microphones.Clear();
        // Its timestamp may now lie ahead of the microphone; the queue is re-read by time.
        currentReference = null;
        // Push silence through the noise suppressor so the audio still in its delay line
        // (from before the gap) is not played after it.
        if (suppressor is not null)
        {
            for (int n = 0; n < SuppressorFlushFrames; n++)
            {
                Array.Clear(bypass);
                suppressor.Process(bypass);
            }
        }
        resets++;
    }

    // Covers the longest noise suppressor delay (DPDFNet, 50 ms).
    private const int SuppressorFlushFrames = INoiseSuppressor.MaxDelaySamples / Aec3Processor.FrameSamples + 1;

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
        if (recorder is not null) Array.Copy(microphone.Samples, unprocessed, unprocessed.Length);
        processor.Process(alignedReference, microphone.Samples);
        var routed = apply ? microphone.Samples : bypass;
        suppressor?.Process(routed);
        output?.Write(routed);
        recorder?.Write(unprocessed, routed, (apply ? Aec3Processor.DelaySamples : 0)
            + (suppressor is null ? 0 : INoiseSuppressor.DelayOf(noiseSuppression)));
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
            suppressor?.Dispose();
            suppressor = null;
            referenceStream.Dispose();
            microphoneStream.Dispose();
            references.Clear();
            currentReference = null;
            microphones.Clear();
        }
    }
}
