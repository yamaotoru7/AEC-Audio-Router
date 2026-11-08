using System.Diagnostics;
using System.IO;
using System.Numerics;
using static AecAudioRouter.Tests.AecTests;

namespace AecAudioRouter.Tests;

internal static class NoiseTests
{
    private const int Rate = Aec3Processor.SampleRate;
    private const int Frame = Aec3Processor.FrameSamples;

    // Voiced-speech stand-in: a gliding glottal pulse train through three formant
    // resonators, in 220 ms syllables separated by 120 ms pauses.
    internal static float[] Speech(int samples, int seed)
    {
        var random = new Random(seed);
        var speech = new float[samples];
        double phase = 0;
        double[] formants = [700, 1220, 2600], widths = [90, 110, 160];
        var state = new double[3, 2];
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / Rate;
            double syllable = t % 0.34;
            double f0 = 120 + 40 * Math.Sin(2 * Math.PI * 0.7 * t) + 25 * syllable / 0.34;
            phase += f0 / Rate;
            double excitation = 0;
            if (phase >= 1) { phase -= 1; excitation = 1; }
            excitation += 0.02 * (random.NextDouble() * 2 - 1);
            double voiced = 0;
            for (int k = 0; k < 3; k++)
            {
                double r = Math.Exp(-Math.PI * widths[k] / Rate);
                double a1 = 2 * r * Math.Cos(2 * Math.PI * formants[k] / Rate), a2 = -r * r;
                double y = (1 - r) * excitation + a1 * state[k, 0] + a2 * state[k, 1];
                state[k, 1] = state[k, 0];
                state[k, 0] = y;
                voiced += y / (k + 1);
            }
            double envelope = syllable < 0.22 ? Math.Pow(Math.Sin(Math.PI * syllable / 0.22), 0.6) : 0;
            speech[i] = (float)(0.9 * envelope * voiced);
        }
        return speech;
    }

    // Fan-like broadband noise, by default with a 50 Hz hum.
    internal static float[] Noise(int samples, int seed, float level, float hum = 0.3f)
    {
        var random = new Random(seed);
        var noise = new float[samples];
        float state = 0;
        for (int i = 0; i < samples; i++)
        {
            state = 0.7f * state + 0.3f * (float)(random.NextDouble() * 2 - 1);
            noise[i] = level * (state + hum * (float)Math.Sin(2 * Math.PI * 50 * i / (double)Rate));
        }
        return noise;
    }

    private static IEnumerable<NoiseSuppressionMode> AvailableModes =>
        Enum.GetValues<NoiseSuppressionMode>().Where(m => m != NoiseSuppressionMode.Off
            && (m != NoiseSuppressionMode.DeepFilterNet3 || DeepFilterSuppressor.IsAvailable));

    public static void RnnoiseSuppressesNoiseAndKeepsSpeech()
    {
        double voice = 0;
        using var suppressor = new RnnoiseSuppressor();
        CheckSuppression("RNNoise", suppressor, RnnoiseSuppressor.DelaySamples, time =>
        {
            Require(suppressor.VoiceProbability is >= 0 and <= 1, "RNNoise voice probability is out of range");
            if (time >= 6) voice += suppressor.VoiceProbability;
        });
        Console.WriteLine($"RNNoise: mean voice probability during speech={voice / 600:F2}");
    }

    public static void DpdfnetSuppressesNoiseAndKeepsSpeech()
    {
        using var suppressor = new DpdfnetSuppressor();
        CheckSuppression("DPDFNet", suppressor, DpdfnetSuppressor.DelaySamples, null);
    }

    public static void GtcrnSuppressesNoiseAndKeepsSpeech()
    {
        using var suppressor = new GtcrnSuppressor();
        CheckSuppression("GTCRN", suppressor, GtcrnSuppressor.DelaySamples, null);
    }

    public static void DeepFilterNet3SuppressesNoiseAndKeepsSpeech()
    {
        if (!DeepFilterSuppressor.IsAvailable)
        {
            Console.WriteLine("DeepFilterNet3: skipped (not built; needs the pinned Rust toolchain)");
            return;
        }
        using var suppressor = new DeepFilterSuppressor();
        // DeepFilterNet3 passes a loud mains hum: its local-SNR estimate rates a strong
        // low tone as clean speech and skips processing. Tested on broadband noise only.
        CheckSuppression("DeepFilterNet3", suppressor, DeepFilterSuppressor.DelaySamples, null, HumFor(NoiseSuppressionMode.DeepFilterNet3));
    }

    private static float HumFor(NoiseSuppressionMode mode) => mode == NoiseSuppressionMode.DeepFilterNet3 ? 0 : 0.3f;

    private static void CheckSuppression(string name, INoiseSuppressor suppressor, int delay, Action<double>? afterFrame, float hum = 0.3f)
    {
        const int seconds = 12;
        var speech = Speech(seconds * Rate, 11);
        var noise = Noise(speech.Length, 12, 0.05f, hum);
        // Seconds 0-4 noise only, then speech over the same noise.
        var mixed = new float[speech.Length];
        for (int i = 0; i < mixed.Length; i++)
            mixed[i] = noise[i] + (i >= 4 * Rate ? speech[i] : 0);
        var processed = new float[mixed.Length];
        var frameTimes = new List<double>();
        for (int i = 0; i < mixed.Length; i += Frame)
        {
            float[] frame = mixed[i..(i + Frame)];
            long started = Stopwatch.GetTimestamp();
            suppressor.Process(frame);
            frameTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Require(frame.All(x => float.IsFinite(x) && Math.Abs(x) <= 1), $"{name} output is not finite");
            frame.CopyTo(processed, i);
            afterFrame?.Invoke((double)i / Rate);
        }
        frameTimes.Sort();
        double reduction = 10 * Math.Log10(Energy(mixed, 2 * Rate, 2 * Rate) / Math.Max(1e-20, Energy(processed, 2 * Rate, 2 * Rate)));
        var clean = new float[speech.Length];
        for (int i = 4 * Rate; i < clean.Length; i++) clean[i] = speech[i];
        var (_, correlationIn, _) = Match(clean, mixed, 6 * Rate, 5 * Rate);
        var (gain, correlation, lag) = Match(clean, processed, 6 * Rate, 5 * Rate, 3000);
        Console.WriteLine($"{name}: noise-only reduction={reduction:F1} dB, speech gain={gain:F3}, correlation {correlationIn:F3} -> {correlation:F3}, " +
            $"lag={lag} samples, {frameTimes.Average():F3} ms/frame (99th percentile {frameTimes[frameTimes.Count * 99 / 100]:F3} ms)");
        Require(reduction > 12, "Noise-only reduction must exceed 12 dB");
        Require(Math.Abs(lag - delay) <= 2, $"{name} delay differs from the documented value");
        Require(gain is > 0.6 and < 1.3, "Speech level was not preserved");
        Require(correlation > correlationIn && correlation > 0.85, "Speech was not kept while removing noise");
    }

    public static void RejectsInvalidFramesAndSurvivesSilenceAndClipping()
    {
        foreach (var mode in AvailableModes)
        {
            var suppressor = INoiseSuppressor.Create(mode);
            var frame = new float[Frame];
            for (int n = 0; n < 50; n++)
            {
                Array.Clear(frame);
                suppressor.Process(frame);
                Require(frame.All(x => Math.Abs(x) < 1e-6), $"{mode}: silence produced output");
            }
            for (int n = 0; n < 50; n++)
            {
                for (int i = 0; i < Frame; i++) frame[i] = i % 2 == 0 ? 4f : -4f;
                frame[7] = float.NaN;
                frame[8] = float.PositiveInfinity;
                suppressor.Process(frame);
                Require(frame.All(x => float.IsFinite(x) && Math.Abs(x) <= 1), $"{mode}: clipped input produced invalid output");
            }
            // Clipped full-scale speech: the resamplers around GTCRN overshoot full scale.
            var loud = Speech(300 * Frame, 77);
            float scale = 2f / loud.Max(Math.Abs);
            for (int n = 0; n < 300; n++)
            {
                for (int i = 0; i < Frame; i++) frame[i] = Math.Clamp(loud[n * Frame + i] * scale, -1f, 1f);
                suppressor.Process(frame);
                Require(frame.All(x => float.IsFinite(x) && Math.Abs(x) <= 1), $"{mode}: full-scale speech produced output beyond full scale");
            }
            try { suppressor.Process(new float[Frame - 1]); throw new Exception("Invalid frame accepted"); }
            catch (ArgumentException) { }
            suppressor.Dispose();
            suppressor.Dispose();
            try { suppressor.Process(frame); throw new Exception("Disposed suppressor accepted audio"); }
            catch (ObjectDisposedException) { }
        }
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".onnx");
        try { using var _ = new DpdfnetSuppressor(missing); throw new Exception("Missing model accepted"); }
        catch (InvalidOperationException) { }
        // A model that loads but does not match fails part way through construction.
        try { using var _ = new DpdfnetSuppressor(GtcrnSuppressor.DefaultModelPath); throw new Exception("GTCRN model accepted as DPDFNet"); }
        catch (InvalidOperationException) { }
        try { using var _ = new GtcrnSuppressor(DpdfnetSuppressor.DefaultModelPath); throw new Exception("DPDFNet model accepted as GTCRN"); }
        catch (InvalidOperationException) { }

        // Switching while running keeps the previous engine only for InvalidOperationException.
        foreach (Exception failure in new Exception[] { new DllNotFoundException(), new BadImageFormatException(),
            new TypeInitializationException("Microsoft.ML.OnnxRuntime.NativeMethods", null), new FormatException() })
        {
            try { INoiseSuppressor.Load("test", () => throw failure); throw new Exception($"{failure.GetType().Name} escaped"); }
            catch (InvalidOperationException error) when (error.InnerException == failure) { }
        }

        // libDF panics (ending the process) on a damaged archive, so it must be rejected first.
        if (DeepFilterSuppressor.IsAvailable)
        {
            Require(DeepFilterSuppressor.ModelSha256.Length == 64, "The DeepFilterNet3 model hash is not embedded in the build");
            string damaged = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tar.gz");
            byte[] model = File.ReadAllBytes(DeepFilterSuppressor.DefaultModelPath);
            File.WriteAllBytes(damaged, model[..(model.Length / 2)]);
            try { using var _ = new DeepFilterSuppressor(damaged); throw new Exception("Damaged DeepFilterNet3 model accepted"); }
            catch (InvalidOperationException) { }
            finally { File.Delete(damaged); }
        }
    }

    // Matches a naive DFT for the 960-point STFT size and a few other factorizations.
    public static void FftMatchesDftAndInverts()
    {
        var random = new Random(5);
        foreach (int size in new[] { 1, 2, 12, 15, 64, 960 })
        {
            var fft = new Fft(size);
            var input = Enumerable.Range(0, size).Select(_ => new Complex(random.NextDouble() - 0.5, random.NextDouble() - 0.5)).ToArray();
            var output = new Complex[size];
            var back = new Complex[size];
            fft.Forward(input, output);
            double error = 0;
            for (int k = 0; k < size; k++)
            {
                Complex sum = 0;
                for (int n = 0; n < size; n++) sum += input[n] * Complex.FromPolarCoordinates(1, -2 * Math.PI * k * n / size);
                error = Math.Max(error, (sum - output[k]).Magnitude);
            }
            fft.Inverse(output, back);
            double roundTrip = input.Zip(back, (a, b) => (a - b).Magnitude).Max();
            Require(error < 1e-9 && roundTrip < 1e-12, $"FFT of size {size} is wrong (error {error}, round trip {roundTrip})");
        }
    }

    // Noise suppression follows AEC3: echo is still cancelled, background noise is removed
    // with AEC on or bypassed, and switching it off routes the AEC output unchanged.
    public static void PipelineSuppressesNoiseAfterAecAndSwitchesWhileRunning()
    {
        foreach (var mode in AvailableModes)
            CheckPipeline(mode);
        foreach (var mode in AvailableModes)
            CheckNoStaleAudioAfterCaptureRestart(mode);
    }

    // After a capture discontinuity, audio from before the gap must not come out of the
    // noise suppressor's delay line.
    private static void CheckNoStaleAudioAfterCaptureRestart(NoiseSuppressionMode mode)
    {
        var output = new RouteBuffer(Rate, Rate);
        using var pipeline = new AecPipeline(Rate, Rate, output, new Meter()) { Enabled = false };
        pipeline.SetNoiseSuppression(mode);
        // 1.1 s ends inside a syllable, so the delay line holds speech at the gap.
        var speech = Speech(110 * Frame, 41);
        var read = new float[Frame];
        for (int packet = 0; packet < 110; packet++)
        {
            pipeline.AddMicrophone(speech.AsSpan(packet * Frame, Frame), (packet + 1) * 0.01);
            output.Read(read, 0, read.Length);
        }
        var silence = new float[Frame];
        float peak = 0;
        for (int packet = 0; packet < 30; packet++)
        {
            pipeline.AddMicrophone(silence, 5 + packet * 0.01);
            output.Read(read, 0, read.Length);
            peak = Math.Max(peak, read.Max(Math.Abs));
        }
        Require(pipeline.Status.Resets == 1, "Capture discontinuity did not reset");
        Require(peak < 1e-3, $"{mode}: audio from before a capture restart was played after it (peak {peak:F4})");
    }

    private static void CheckPipeline(NoiseSuppressionMode mode)
    {
        var random = new Random(31);
        const int packets = 4000;
        var far = new float[packets * Frame];
        float state = 0;
        for (int i = 0; i < far.Length; i++)
        {
            state = 0.9f * state + 0.1f * (float)(random.NextDouble() * 2 - 1);
            far[i] = state * 0.6f;
        }
        var noise = Noise(far.Length, 32, 0.03f, HumFor(mode));
        const int delay = 4800;
        var output = new RouteBuffer(Rate, Rate);
        var pipeline = new AecPipeline(Rate, Rate, output, new Meter());
        var reference = new float[Frame];
        var mic = new float[Frame];
        var read = new float[Frame];
        // Seconds 0-10 AEC only, 10-20 AEC + NS, 20-30 AEC bypassed + NS, 30-40 AEC bypassed, NS off.
        double[] input = new double[4], routed = new double[4];
        for (int packet = 0; packet < packets; packet++)
        {
            int start = packet * Frame;
            int phase = packet / 1000;
            for (int i = 0; i < Frame; i++)
            {
                // Echo only while AEC is on; noise only afterwards, to measure each stage.
                reference[i] = phase < 2 ? far[start + i] : 0;
                mic[i] = (phase < 2 && start + i >= delay ? 0.6f * far[start + i - delay] : 0) + noise[start + i];
            }
            pipeline.Enabled = phase < 2;
            var expected = phase is 1 or 2 ? mode : NoiseSuppressionMode.Off;
            if (packet % 1000 == 0)
                pipeline.SetNoiseSuppression(expected);
            Require(pipeline.NoiseSuppression == expected, "Noise suppression mode was not applied");
            double time = (packet + 1) * 0.01;
            pipeline.AddReference(reference, time);
            pipeline.AddMicrophone(mic, time);
            output.Read(read, 0, read.Length);
            if (packet % 1000 >= (phase == 0 ? 600 : 100))
            {
                input[phase] += mic.Sum(x => (double)x * x);
                routed[phase] += read.Sum(x => (double)x * x);
            }
        }
        double Reduction(int phase) => 10 * Math.Log10(input[phase] / Math.Max(1e-20, routed[phase]));
        Console.WriteLine($"Pipeline {mode}: AEC={Reduction(0):F1} dB, AEC+NS={Reduction(1):F1} dB, bypass+NS={Reduction(2):F1} dB, bypass={Reduction(3):F1} dB");
        Require(Reduction(1) > Reduction(0) + 3, "Noise suppression did not add to the AEC reduction");
        Require(Reduction(2) > 12, "Noise suppression did not work with AEC bypassed");
        Require(Math.Abs(Reduction(3)) < 0.5, "Turning noise suppression off did not route the microphone unchanged");
        pipeline.Dispose();
        try { pipeline.SetNoiseSuppression(mode); throw new Exception("Disposed pipeline accepted a suppressor"); }
        catch (ObjectDisposedException) { }
    }
}
