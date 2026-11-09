using static AecAudioRouter.Tests.AecTests;

namespace AecAudioRouter.Tests;

internal static class ListenBackTests
{
    private const int Rate = Aec3Processor.SampleRate;
    private const int Frame = Aec3Processor.FrameSamples;

    // The recorder keeps only the last 30 seconds, in order, and delays the unprocessed
    // track by the given amount so it lines up with the processed one.
    public static void RecorderKeepsTheLastSecondsAligned()
    {
        var recorder = new ListenBackRecorder();
        Require(recorder.Snapshot() is null, "An empty recorder returned a recording");
        const int delay = 960;
        int frames = (ListenBackRecorder.Seconds + 5) * 100;
        var before = new float[Frame];
        var after = new float[Frame];
        for (int frame = 0; frame < frames; frame++)
        {
            for (int i = 0; i < Frame; i++)
            {
                long n = (long)frame * Frame + i;
                before[i] = Value(n);
                // The processed audio comes out `delay` samples late.
                after[i] = n >= delay ? Value(n - delay) : 0;
            }
            recorder.Write(before, after, delay);
        }
        var recording = recorder.Snapshot()!;
        Require(recording.Length == ListenBackRecorder.Seconds * Rate, $"Kept {recording.Length} samples");
        long first = (long)frames * Frame - recording.Length - delay;
        for (int i = 0; i < recording.Length; i++)
        {
            Require(recording.After[i] == Value(first + i), $"Processed sample {i} out of order");
            Require(recording.Before[i] == recording.After[i], $"Unprocessed sample {i} not aligned");
        }

        // A short recording starts at the beginning, the unprocessed track delayed with silence.
        var shortRecorder = new ListenBackRecorder();
        for (int i = 0; i < Frame; i++) before[i] = after[i] = 1;
        shortRecorder.Write(before, after, 100);
        var short_ = shortRecorder.Snapshot()!;
        Require(short_.Length == Frame && short_.Before[99] == 0 && short_.Before[100] == 1 && short_.After[0] == 1,
            "Short recording not delayed from its start");
        try { shortRecorder.Write(before, after, ListenBackRecorder.MaxDelaySamples + 1); throw new Exception("Accepted a delay beyond the history"); }
        catch (ArgumentOutOfRangeException) { }

        static float Value(long n) => (n % 9973) / 9973f;
    }

    // Switching tracks keeps the position; a seek moves it; the end reads nothing.
    public static void SourceSwitchesTracksAtTheSamePosition()
    {
        var recording = new ListenBackRecording(Enumerable.Repeat(-0.5f, 1000).ToArray(), Enumerable.Repeat(0.5f, 1000).ToArray());
        var source = new ListenBackSource(recording);
        var buffer = new float[300];
        Require(source.Read(buffer, 0, 300) == 300 && buffer.All(x => x == 0.5f), "Did not start on the processed track");
        source.Processed = false;
        Require(source.Read(buffer, 0, 300) == 300 && buffer.All(x => x == -0.5f), "Did not switch to the unprocessed track");
        Require(Math.Abs(source.Progress - 0.6) < 1e-9, $"Position {source.Progress} after two reads");
        source.Seek(0.9);
        Require(source.Read(buffer, 0, 300) == 100, "Did not stop at the end");
        Require(source.AtEnd && source.Read(buffer, 0, 300) == 0, "Read past the end");
        source.Seek(-1);
        Require(source.Progress == 0 && !source.AtEnd, "Seek was not clamped");
    }

    // Through the real pipeline (no reference, so AEC3 leaves the voice alone), the two tracks
    // line up with AEC3 on or bypassed and with every noise suppressor, switched while running.
    public static void PipelineRecordsBothTracksInStep()
    {
        var modes = Enum.GetValues<NoiseSuppressionMode>()
            .Where(m => m != NoiseSuppressionMode.DeepFilterNet3 || DeepFilterSuppressor.IsAvailable)
            .SelectMany(m => new[] { (Mode: m, Aec: true), (Mode: m, Aec: false) }).ToArray();
        var speech = NoiseTests.Speech(modes.Length * 3 * Rate, 41);
        var output = new RouteBuffer(Rate, Rate);
        var recorder = new ListenBackRecorder();
        using var pipeline = new AecPipeline(Rate, Rate, output, new Meter(), recorder);
        var silence = new float[Frame];
        var mic = new float[Frame];
        var read = new float[Frame];
        for (int packet = 0; packet < speech.Length / Frame; packet++)
        {
            if (packet % 300 == 0)
            {
                pipeline.SetNoiseSuppression(modes[packet / 300].Mode);
                pipeline.Enabled = modes[packet / 300].Aec;
            }
            Array.Copy(speech, packet * Frame, mic, 0, Frame);
            double time = (packet + 1) * 0.01;
            pipeline.AddReference(silence, time);
            pipeline.AddMicrophone(mic, time);
            output.Read(read, 0, Frame);
        }
        var recording = recorder.Snapshot()!;
        // The recording ends 40 ms (the alignment delay) short of the input.
        int offset = speech.Length - recording.Length;
        for (int index = 0; index < modes.Length; index++)
        {
            // The last two seconds of each setting, past the switch and the suppressor's start-up.
            int start = (index * 3 + 1) * Rate - offset;
            var (_, forward, lagAfter) = Match(recording.Before, recording.After, start, Rate, 200);
            var (_, backward, lagBefore) = Match(recording.After, recording.Before, start, Rate, 200);
            int lag = forward >= backward ? lagAfter : -lagBefore;
            var (mode, aec) = modes[index];
            Console.WriteLine($"Listen back {mode}, AEC {(aec ? "on" : "off")}: correlation={Math.Max(forward, backward):F3}, lag={lag} samples");
            // DPDFNet reshapes this noiseless synthetic voice (correlation about 0.43), but the peak
            // still marks the lag.
            Require(Math.Max(forward, backward) > 0.3, $"{mode}, AEC {aec}: the tracks do not match");
            Require(Math.Abs(lag) <= 2, $"{mode}, AEC {aec}: the tracks are out of step");
        }
    }

    // With the microphone output set to "なし" there is no route buffer; the meter and the
    // recording still get the processed audio.
    public static void PipelineWithoutOutputStillRecords()
    {
        var recorder = new ListenBackRecorder();
        var meter = new Meter();
        using var pipeline = new AecPipeline(Rate, Rate, null, meter, recorder);
        var speech = NoiseTests.Speech(Rate, 43);
        var silence = new float[Frame];
        var mic = new float[Frame];
        for (int packet = 0; packet < speech.Length / Frame; packet++)
        {
            Array.Copy(speech, packet * Frame, mic, 0, Frame);
            double time = (packet + 1) * 0.01;
            pipeline.AddReference(silence, time);
            pipeline.AddMicrophone(mic, time);
        }
        var recording = recorder.Snapshot()!;
        // All but the 40 ms held for alignment.
        Require(recording.Length == speech.Length - AecPipeline.AlignmentDelayMs * Rate / 1000, $"Recorded {recording.Length} samples");
        float input = speech.Max(Math.Abs);
        Require(recording.After.Max(Math.Abs) > 0.5 * input, "Nothing processed was recorded");
        Require(meter.ReadAndReset().Peak > 0.5 * input, "The output meter got nothing");
    }
}
