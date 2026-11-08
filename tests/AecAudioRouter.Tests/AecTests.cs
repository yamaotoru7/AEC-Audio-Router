namespace AecAudioRouter.Tests;

internal static class AecTests
{
    private const int Rate = Aec3Processor.SampleRate;

    public static void CancelsDelayedEchoAndPreservesDoubleTalk()
    {
        foreach (int delayMs in new[] { 30, 100, 200 })
        {
            const int seconds = 18;
            var random = new Random(734 + delayMs);
            var far = new float[seconds * Rate];
            var near = new float[far.Length];
            var mixed = new float[far.Length];
            var processed = new float[far.Length];
            float farState = 0, nearState = 0;
            int delay = Rate * delayMs / 1000;
            for (int i = 0; i < far.Length; i++)
            {
                farState = 0.82f * farState + 0.18f * (float)(random.NextDouble() * 2 - 1);
                nearState = 0.65f * nearState + 0.35f * (float)(random.NextDouble() * 2 - 1);
                far[i] = farState * 0.65f;
                if (i >= 12 * Rate)
                {
                    double t = (double)i / Rate;
                    double envelope = 0.2 + 0.8 * Math.Pow(Math.Sin(2 * Math.PI * 2.7 * t), 2);
                    near[i] = (float)(envelope * (0.15 * Math.Sin(2 * Math.PI * 173 * t)
                        + 0.08 * Math.Sin(2 * Math.PI * 346 * t) + 0.04 * Math.Sin(2 * Math.PI * 691 * t))) + nearState * 0.02f;
                }
                float echo = i >= delay ? 0.65f * far[i - delay] : 0;
                if (i >= delay + 113) echo += 0.2f * far[i - delay - 113];
                mixed[i] = echo + near[i];
            }

            using var aec = new Aec3Processor();
            for (int i = 0; i < far.Length; i += 480)
            {
                float[] microphone = mixed[i..(i + 480)];
                aec.Process(far[i..(i + 480)], microphone);
                Require(microphone.All(float.IsFinite), "AEC output contains nonfinite samples");
                microphone.CopyTo(processed, i);
            }
            double erle = 10 * Math.Log10(Energy(mixed, 8 * Rate, 4 * Rate) / Math.Max(1e-20, Energy(processed, 8 * Rate, 4 * Rate)));
            var (gain, correlation, lag) = Match(near, processed, 14 * Rate, 3 * Rate);
            Console.WriteLine($"AEC3 delay={delayMs} ms: echo reduction={erle:F1} dB, double-talk gain={gain:F3}, correlation={correlation:F3}, lag={lag} samples");
            Require(erle > 20, "Delayed echo reduction must exceed 20 dB");
            Require(gain > 0.5 && gain < 1.3 && correlation > 0.8, "Double-talk near-end signal was not preserved");
        }
    }

    public static void PreservesNearEndWithoutReferenceAndRejectsInvalidFrames()
    {
        using var aec = new Aec3Processor();
        var silence = new float[480];
        double before = 0, after = 0;
        for (int frame = 0; frame < 400; frame++)
        {
            float[] mic = Enumerable.Range(0, 480).Select(i => (float)(0.15 * Math.Sin(2 * Math.PI * 440 * (frame * 480 + i) / Rate))).ToArray();
            if (frame > 100) before += mic.Sum(x => (double)x * x);
            aec.Process(silence, mic);
            if (frame > 100) after += mic.Sum(x => (double)x * x);
        }
        Require(after / before is > 0.85 and < 1.15, "Near-end only level changed excessively");
        try { aec.Process(new float[479], new float[480]); throw new Exception("Invalid frame accepted"); }
        catch (ArgumentException) { }
        aec.Dispose();
        aec.Dispose();
        try { aec.Process(silence, new float[480]); throw new Exception("Disposed processor accepted audio"); }
        catch (ObjectDisposedException) { }
    }

    public static void ResamplesIrregularPacketsAndRejectsAliasing()
    {
        foreach (int rate in new[] { 8000, 16000, 44100, 48000, 96000, 192000 })
        {
            using var stream = new AudioFrameStream(rate);
            var frames = new List<AudioFrame>();
            var input = Enumerable.Range(0, rate).Select(i => (float)(0.2 * Math.Sin(2 * Math.PI * 1000 * i / rate))).ToArray();
            int offset = 0;
            var random = new Random(rate);
            while (offset < input.Length)
            {
                int count = Math.Min(input.Length - offset, random.Next(1, rate / 30));
                stream.Push(input.AsSpan(offset, count), (double)(offset + count) / rate, frames.Add);
                offset += count;
            }
            Require(frames.Count == 100, $"Wrong resampled duration at {rate} Hz");
            Require(frames.All(f => f.Samples.Length == 480 && f.Samples.All(float.IsFinite)), "Malformed frame");
            double power = frames.Skip(10).Average(f => f.Samples.Average(x => (double)x * x));
            Require(Math.Abs(power - 0.02) < 0.002, $"Resampling changed passband level at {rate} Hz");
        }
        using var downsample = new AudioFrameStream(96000);
        var down = new List<AudioFrame>();
        for (int frame = 0; frame < 100; frame++)
        {
            float[] high = Enumerable.Range(0, 960).Select(i => (float)(0.2 * Math.Sin(2 * Math.PI * 30000 * (frame * 960 + i) / 96000))).ToArray();
            downsample.Push(high, (frame + 1) * 0.01, down.Add);
        }
        double aliasPower = down.Skip(10).Average(f => f.Samples.Average(x => (double)x * x));
        Require(aliasPower < 0.00002, "Downsampling aliases ultrasonic content");
    }

    public static void PipelineHandlesJitterSilenceAndCaptureRestarts()
    {
        var output = new RouteBuffer(Rate, Rate / 2);
        var meter = new Meter();
        using var pipeline = new AecPipeline(Rate, Rate, output, meter);
        var silence = new float[960];
        var mic = new float[960];
        var read = new float[960];
        for (int packet = 0; packet < 500; packet++)
        {
            for (int i = 0; i < mic.Length; i++) mic[i] = (float)(0.1 * Math.Sin(2 * Math.PI * 440 * (packet * 960 + i) / Rate));
            double time = (packet + 1) * 0.02;
            // Simulate loopback stopping for 2 seconds, then resuming, with jitter.
            if (packet < 150 || packet >= 250)
                pipeline.AddReference(silence, time + (packet % 3 - 1) * 0.002);
            pipeline.AddMicrophone(mic, time);
            output.Read(read, 0, read.Length);
            Require(read.All(float.IsFinite), "Nonfinite pipeline output");
        }
        Require(pipeline.Status.Missing >= 190 && pipeline.Status.Missing <= 210, "Silent loopback was not replaced by silence correctly");
        Require(meter.ReadAndReset().Rms > 0.05, "Microphone stopped while reference was absent");
        pipeline.AddMicrophone(mic, 12.02);
        Require(pipeline.Status.Resets == 1, "Capture discontinuity did not reset AEC");
        pipeline.Dispose();
        pipeline.AddReference(silence, 13);
        pipeline.AddMicrophone(mic, 13);
    }

    // Loopback delivers no packets while nothing plays. A pause longer than the alignment
    // buffer but shorter than the 100 ms discontinuity limit must not leave the reference
    // stamped in the past (it would then arrive too late for every microphone frame).
    public static void PipelineKeepsCancellingAfterShortReferencePauses()
    {
        const int seconds = 30;
        var random = new Random(51);
        var far = new float[seconds * Rate];
        float state = 0;
        for (int i = 0; i < far.Length; i++)
        {
            state = 0.85f * state + 0.15f * (float)(random.NextDouble() * 2 - 1);
            far[i] = state * 0.6f;
        }
        // Pauses of 30, 60 and 90 ms: the render stream is silent, so loopback sends nothing.
        (double Start, double Length)[] pauses = [(8, 0.03), (15, 0.06), (22, 0.09)];
        bool Paused(int frame) => pauses.Any(p => frame * 0.01 >= p.Start && frame * 0.01 < p.Start + p.Length);
        for (int frame = 0; frame < seconds * 100; frame++)
            if (Paused(frame)) Array.Clear(far, frame * 480, 480);

        int delay = Rate * 50 / 1000;
        var output = new RouteBuffer(Rate, Rate);
        using var pipeline = new AecPipeline(Rate, Rate, output, new Meter());
        var mic = new float[480];
        var read = new float[480];
        var input = new double[pauses.Length];
        var routed = new double[pauses.Length];
        for (int frame = 0; frame < seconds * 100; frame++)
        {
            int start = frame * 480;
            double time = (frame + 1) * 0.01;
            if (!Paused(frame))
                pipeline.AddReference(far.AsSpan(start, 480), time + 0.003 + (frame % 3) * 0.001);
            for (int i = 0; i < 480; i++) mic[i] = start + i >= delay ? 0.6f * far[start + i - delay] : 0;
            pipeline.AddMicrophone(mic, time + 0.004);
            output.Read(read, 0, read.Length);
            // Measure 2 to 6 seconds after each pause.
            for (int p = 0; p < pauses.Length; p++)
                if (time >= pauses[p].Start + 2 && time < pauses[p].Start + 6)
                {
                    input[p] += mic.Sum(x => (double)x * x);
                    routed[p] += read.Sum(x => (double)x * x);
                }
        }
        for (int p = 0; p < pauses.Length; p++)
        {
            double erle = 10 * Math.Log10(input[p] / Math.Max(1e-20, routed[p]));
            Console.WriteLine($"Pipeline after a {pauses[p].Length * 1000:F0} ms reference pause: echo reduction={erle:F1} dB");
            Require(erle > 15, $"Echo cancellation did not recover after a {pauses[p].Length * 1000:F0} ms reference pause");
        }
        Require(pipeline.Status.Missing < 100, $"Reference stayed missing after short pauses ({pipeline.Status.Missing} frames)");

        // A device period of arrival jitter (audio is released in 10 ms periods) is not a gap.
        using var stream = new AudioFrameStream(Rate);
        var jitter = new Random(3);
        var packet = new float[480];
        for (int i = 0; i < 6000; i++)
            stream.Push(packet, (i + 1) * 0.01 + jitter.NextDouble() * 0.01, _ => { });
        Require(stream.Discontinuities == 0, $"10 ms of arrival jitter was taken as a gap ({stream.Discontinuities} resets)");
    }

    public static void PipelineBypassRoutesRawMicrophoneAndKeepsAdapting()
    {
        var random = new Random(7);
        var far = new float[31 * Rate];
        float state = 0;
        for (int i = 0; i < far.Length; i++)
        {
            state = 0.9f * state + 0.1f * (float)(random.NextDouble() * 2 - 1);
            far[i] = state * 0.6f;
        }
        const int delay = 4800;
        var output = new RouteBuffer(Rate, Rate);
        using var pipeline = new AecPipeline(Rate, Rate, output, new Meter());
        var reference = new float[480];
        var mic = new float[480];
        var read = new float[480];
        // Seconds 0-10 ON, 10-20 OFF (bypass), 20-30 ON again.
        double[] input = new double[3], routed = new double[3];
        for (int packet = 0; packet < 3000; packet++)
        {
            int start = packet * 480;
            for (int i = 0; i < 480; i++)
            {
                reference[i] = far[start + i];
                mic[i] = start + i >= delay ? 0.6f * far[start + i - delay] : 0;
            }
            int phase = packet / 1000;
            pipeline.Enabled = phase != 1;
            double time = (packet + 1) * 0.01;
            pipeline.AddReference(reference, time);
            pipeline.AddMicrophone(mic, time);
            output.Read(read, 0, read.Length);
            // Skip convergence and the 40 ms alignment delay around each switch.
            if (packet % 1000 >= (phase == 0 ? 600 : 10) && packet % 1000 < 110 + (phase == 0 ? 890 : 0))
            {
                input[phase] += mic.Sum(x => (double)x * x);
                routed[phase] += read.Sum(x => (double)x * x);
            }
        }
        double Erle(int phase) => 10 * Math.Log10(input[phase] / Math.Max(1e-20, routed[phase]));
        Console.WriteLine($"Bypass: ON={Erle(0):F1} dB, OFF={Erle(1):F1} dB, ON again (first second)={Erle(2):F1} dB");
        Require(Erle(0) > 15, "AEC did not cancel echo while enabled");
        Require(Math.Abs(Erle(1)) < 0.5, "Bypass did not route the unprocessed microphone");
        Require(Erle(2) > 15, "AEC needed to reconverge after bypass");
    }

    public static void PipelineCancelsEchoAcrossRatesWithJitterAndClockDrift()
    {
        foreach (int ppm in new[] { -150, 150 }) CheckClockDrift(ppm);
    }

    private static void CheckClockDrift(int ppm)
    {
        const int seconds = 90;
        const int micRate = 44100;
        double referenceRate = 48000 * (1 + ppm / 1_000_000.0);
        var random = new Random(94);
        var far = new float[(seconds + 1) * Rate];
        float state = 0;
        for (int i = 0; i < far.Length; i++)
        {
            state = 0.9f * state + 0.1f * (float)(random.NextDouble() * 2 - 1);
            far[i] = state * 0.6f;
        }
        float Signal(double time)
        {
            double index = time * Rate;
            if (index < 0 || index >= far.Length - 1) return 0;
            int i = (int)index;
            return (float)(far[i] + (far[i + 1] - far[i]) * (index - i));
        }

        var events = new List<(double Time, bool Reference, float[] Samples)>();
        for (int packet = 0; packet < seconds * 50; packet++)
        {
            int start = packet * 960;
            double time = (start + 960) / referenceRate + 0.003 + (packet % 3) * 0.001;
            events.Add((time, true, Enumerable.Range(0, 960).Select(i => Signal((start + i) / referenceRate)).ToArray()));
        }
        for (int packet = 0; packet < seconds * 100; packet++)
        {
            int start = packet * 441;
            events.Add(((packet + 1) * 0.01 + 0.004, false,
                Enumerable.Range(0, 441).Select(i => 0.6f * Signal((double)(start + i) / micRate - 0.1)).ToArray()));
        }
        var output = new RouteBuffer(Rate, Rate);
        using var pipeline = new AecPipeline(micRate, Rate, output, new Meter());
        var processed = new float[480];
        double inputPower = 0, outputPower = 0;
        foreach (var packet in events.OrderBy(e => e.Time))
        {
            if (packet.Reference) pipeline.AddReference(packet.Samples, packet.Time);
            else
            {
                pipeline.AddMicrophone(packet.Samples, packet.Time);
                output.Read(processed, 0, processed.Length);
                if (packet.Time > 16)
                {
                    inputPower += packet.Samples.Average(x => (double)x * x);
                    outputPower += processed.Average(x => (double)x * x);
                }
            }
        }
        double erle = 10 * Math.Log10(inputPower / Math.Max(1e-20, outputPower));
        Console.WriteLine($"Pipeline 44.1/48 kHz, jitter, {ppm} ppm drift: echo reduction={erle:F1} dB, reference missing={pipeline.Status.Missing}, dropped={pipeline.Status.Dropped}");
        Require(erle > 15, "Asynchronous, resampled pipeline failed to cancel echo");
        Require(pipeline.Status.Missing < 25, "Excessive reference loss in asynchronous pipeline");
    }

    internal static double Energy(float[] samples, int offset, int count) =>
        samples.Skip(offset).Take(count).Sum(x => (double)x * x);

    internal static (double Gain, double Correlation, int Lag) Match(float[] expected, float[] actual, int start, int count, int maxLag = 800)
    {
        // AEC's split/merge filters add algorithmic delay. Find it independently.
        double best = double.NegativeInfinity, gain = 0;
        int bestLag = 0;
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double cross = 0, sourcePower = 0, outputPower = 0;
            for (int i = start; i < start + count; i += 4)
            {
                double x = expected[i], y = actual[i + lag];
                cross += x * y;
                sourcePower += x * x;
                outputPower += y * y;
            }
            double correlation = cross / Math.Sqrt(Math.Max(1e-30, sourcePower * outputPower));
            if (correlation > best) { best = correlation; gain = cross / sourcePower; bestLag = lag; }
        }
        return (gain, best, bestLag);
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
