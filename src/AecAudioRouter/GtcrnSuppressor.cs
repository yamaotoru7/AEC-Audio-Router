using System.IO;
using Microsoft.ML.OnnxRuntime;

namespace AecAudioRouter;

// GTCRN (Rong Xiaobin, MIT): an ultra-light speech enhancement network at 16 kHz.
// The output keeps only the band below 8 kHz, which is all that speech recognition
// and voice AI front ends use, in exchange for a small, fast model.
//
// The streaming ONNX model (stream/onnx_models/gtcrn_simple.onnx) processes one STFT
// frame per call and returns its convolution and recurrent caches for the next call.
// Framing matches stream/gtcrn_stream.py: 512-point sqrt-Hann STFT with a 256-sample
// (16 ms) hop at 16 kHz. Audio is resampled 48 -> 16 -> 48 kHz around it, and queues
// absorb the mismatch between 16 ms hops and 10 ms frames.
// Owned by AecPipeline; its lock serializes all calls and disposal.
internal sealed class GtcrnSuppressor : INoiseSuppressor
{
    public const string ModelFile = "gtcrn_simple.onnx";
    private const int ModelRate = 16000;
    private const int Hop = 256;
    private const int Window = 2 * Hop;
    private const int Bins = Hop + 1;
    private const int Block = ModelRate / 100;
    // Output lags input by about 33 ms: 16 ms of queue priming, 16 ms of overlap-add,
    // and the two sinc resamplers (measured).
    public const int DelaySamples = 1602;

    private static readonly string[] InputNames = ["mix", "conv_cache", "tra_cache", "inter_cache"];
    private static readonly string[] OutputNames = ["enh", "conv_cache_out", "tra_cache_out", "inter_cache_out"];

    private readonly InferenceSession session;
    private readonly RunOptions run = new();
    private readonly float[] spectrum = new float[Bins * 2];
    private readonly float[] enhanced = new float[Bins * 2];
    private readonly OrtValue spectrumValue;
    private readonly OrtValue enhancedValue;
    // Two sets of caches, alternating between input and output on each call.
    private readonly OrtValue[][] caches = new OrtValue[2][];
    // Run arguments for each direction, built once so the audio thread does not allocate.
    private readonly OrtValue[][] inputs = new OrtValue[2][];
    private readonly OrtValue[][] outputs = new OrtValue[2][];
    private int direction;
    private readonly StreamingStft stft = new(StreamingStft.SqrtHannWindow(Window));
    private readonly ResamplerHandle down;
    private readonly ResamplerHandle up;
    private readonly float[] block = new float[Block];
    private readonly float[] input = new float[Hop + Block];
    private int inputCount;
    // Primed with one hop of silence so a 10 ms block is always ready.
    private readonly float[] output = new float[2 * Hop + Block];
    private int outputCount = Hop;
    private readonly float[] hopOutput = new float[Hop];
    private bool disposed;

    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "models", ModelFile);

    public GtcrnSuppressor() : this(DefaultModelPath) { }

    public GtcrnSuppressor(string modelPath)
    {
        try
        {
            if (!File.Exists(modelPath))
                throw new InvalidOperationException($"GTCRN のモデルが見つかりません。 {modelPath}");
            using var options = new SessionOptions
            {
                IntraOpNumThreads = 1,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            try
            {
                session = new InferenceSession(modelPath, options);
            }
            catch (OnnxRuntimeException error)
            {
                throw new InvalidOperationException($"GTCRN のモデルを読み込めませんでした。 {error.Message}", error);
            }
            down = new ResamplerHandle(Aec3Processor.SampleRate, ModelRate);
            up = new ResamplerHandle(ModelRate, Aec3Processor.SampleRate);
            if (down.IsInvalid || up.IsInvalid)
                throw new InvalidOperationException("GTCRN 用のリサンプラーを作れませんでした。");
            if (!session.InputNames.SequenceEqual(InputNames) || !session.OutputNames.SequenceEqual(OutputNames))
                throw new InvalidOperationException("GTCRN のモデルの入出力が想定と異なります。");
            long[] spectrumShape = [1, Bins, 1, 2];
            if (!session.InputMetadata["mix"].Dimensions.Select(d => (long)d).SequenceEqual(spectrumShape))
                throw new InvalidOperationException("GTCRN のモデルの STFT 設定が想定と異なります。");
            spectrumValue = OrtValue.CreateTensorValueFromMemory(spectrum, spectrumShape);
            enhancedValue = OrtValue.CreateTensorValueFromMemory(enhanced, spectrumShape);
            for (int set = 0; set < 2; set++)
            {
                // Caches start at zero, as in the reference script. Stored as each is
                // created, so a failure part way still releases the ones before it.
                caches[set] = new OrtValue[InputNames.Length - 1];
                for (int c = 0; c < caches[set].Length; c++)
                {
                    long[] shape = session.InputMetadata[InputNames[c + 1]].Dimensions.Select(d => (long)d).ToArray();
                    caches[set][c] = OrtValue.CreateTensorValueFromMemory(new float[shape.Aggregate(1L, (a, b) => a * b)], shape);
                }
            }
            for (int d = 0; d < 2; d++)
            {
                inputs[d] = [spectrumValue, .. caches[d]];
                outputs[d] = [enhancedValue, .. caches[1 - d]];
            }
        }
        catch
        {
            // Releases whatever was created before the failure (the rest is still null).
            Dispose();
            throw;
        }
    }

    public void Process(float[] frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame.Length != Aec3Processor.FrameSamples)
            throw new ArgumentException("ノイズ除去は 480 サンプル（10 ms）のフレームを必要とします。");
        for (int n = 0; n < frame.Length; n++)
            frame[n] = float.IsFinite(frame[n]) ? Math.Clamp(frame[n], -1f, 1f) : 0f;
        if (Aec3Native.aec3_resample(down, frame, frame.Length, block) != Block)
            throw new InvalidOperationException("GTCRN 用のリサンプリングに失敗しました。");
        block.CopyTo(input, inputCount);
        inputCount += Block;

        while (inputCount >= Hop)
        {
            stft.Analyze(input.AsSpan(0, Hop), spectrum);
            try
            {
                session.Run(run, InputNames, inputs[direction], OutputNames, outputs[direction]);
            }
            catch (OnnxRuntimeException error)
            {
                throw new InvalidOperationException($"GTCRN の音声処理に失敗しました。 {error.Message}", error);
            }
            direction = 1 - direction;
            stft.Synthesize(enhanced, hopOutput);
            hopOutput.CopyTo(output, outputCount);
            outputCount += Hop;
            inputCount -= Hop;
            Array.Copy(input, Hop, input, 0, inputCount);
        }

        // Priming keeps at least one block queued: production (256 per hop) never
        // falls more than one hop behind consumption (160 per block).
        Array.Copy(output, block, Block);
        outputCount -= Block;
        Array.Copy(output, Block, output, 0, outputCount);
        if (Aec3Native.aec3_resample(up, block, Block, frame) != frame.Length)
            throw new InvalidOperationException("GTCRN 用のリサンプリングに失敗しました。");
        // The sinc upsampler can overshoot full scale on loud speech.
        for (int n = 0; n < frame.Length; n++)
            frame[n] = float.IsFinite(frame[n]) ? Math.Clamp(frame[n], -1f, 1f) : 0f;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var value in caches.Where(c => c is not null).SelectMany(c => c)) value?.Dispose();
        spectrumValue?.Dispose();
        enhancedValue?.Dispose();
        down?.Dispose();
        up?.Dispose();
        run.Dispose();
        session?.Dispose();
    }
}
