namespace AecAudioRouter;

internal sealed record AudioFrame(double Time, float[] Samples);

// Converts arbitrary packet sizes to 10 ms, 48 kHz mono frames. Time is the
// estimated capture time, derived from callback arrival minus packet duration.
// A slowly corrected sample clock rejects scheduling jitter and follows drift.
internal sealed class AudioFrameStream : IDisposable
{
    // A device period (10 ms) of arrival jitter fits well inside the late limit.
    public const double MaxLateSeconds = 0.02;
    public const double MaxEarlySeconds = 0.1;
    private readonly int sampleRate;
    private readonly float[] input;
    private ResamplerHandle? resampler;
    private int used;
    private double nextTime = double.NaN;
    public long Discontinuities { get; private set; }

    public AudioFrameStream(int rate)
    {
        if (rate < 8000 || rate > 192000 || rate % 100 != 0)
            throw new NotSupportedException($"AEC3 の入力レート {rate} Hz には対応していません。");
        sampleRate = rate;
        input = new float[rate / 100];
        ResetResampler();
    }

    private void ResetResampler()
    {
        resampler?.Dispose();
        resampler = null;
        if (sampleRate == Aec3Processor.SampleRate) return;
        resampler = new ResamplerHandle(sampleRate);
        if (resampler.IsInvalid)
        {
            resampler.Dispose();
            throw new InvalidOperationException("AEC3 のリサンプラーを初期化できませんでした。");
        }
    }

    public void Push(ReadOnlySpan<float> samples, double arrivalTime, Action<AudioFrame> consume)
    {
        if (samples.IsEmpty) return;
        double observedStart = arrivalTime - (double)samples.Length / sampleRate;
        if (double.IsNaN(nextTime)) nextTime = observedStart;
        double error = observedStart - (nextTime + (double)used / sampleRate);
        // Samples that start later than expected mean the device sent nothing for a while
        // (loopback does this while nothing plays), so resynchronize after a short gap:
        // a stale clock would stamp the new audio in the past, and once that exceeds
        // AecPipeline.AlignmentDelayMs every reference frame arrives too late to be used.
        // Early samples can only be scheduling jitter, so allow more before resetting.
        if (error > MaxLateSeconds || error < -MaxEarlySeconds)
        {
            used = 0;
            nextTime = observedStart;
            Discontinuities++;
            ResetResampler();
        }
        else
        {
            nextTime += Math.Clamp(error * 0.001, -0.00001, 0.00001);
        }

        while (!samples.IsEmpty)
        {
            int take = Math.Min(input.Length - used, samples.Length);
            samples[..take].CopyTo(input.AsSpan(used));
            used += take;
            samples = samples[take..];
            if (used != input.Length) continue;
            var output = new float[Aec3Processor.FrameSamples];
            if (resampler is null) input.CopyTo(output, 0);
            else if (Aec3Native.aec3_resample(resampler, input, input.Length, output) != output.Length)
                throw new InvalidOperationException("AEC3 のリサンプリングに失敗しました。");
            consume(new AudioFrame(nextTime, output));
            nextTime += 0.01;
            used = 0;
        }
    }

    public void Dispose() => resampler?.Dispose();
}
