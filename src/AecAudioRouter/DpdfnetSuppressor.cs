using System.Globalization;
using System.IO;
using Microsoft.ML.OnnxRuntime;

namespace AecAudioRouter;

// DPDFNet (Ceva, Apache-2.0): a streaming deep-filtering network for 48 kHz speech.
// Heavier than RNNoise but removes non-stationary noise (keyboard, clatter) better.
//
// The ONNX model processes one STFT frame per call and carries its recurrent state
// between calls. The framing around it matches the reference script
// (ceva-ip/DPDFNet onnx_model/infer_dpdfnet_onnx.py): a 960-point vorbis-window STFT
// with a 480-sample hop, and overlap-add of the windowed inverse.
// Owned by AecPipeline; its lock serializes all calls and disposal.
internal sealed class DpdfnetSuppressor : INoiseSuppressor
{
    public const string ModelFile = "dpdfnet2_48khz_hr.onnx";
    private const int Hop = Aec3Processor.FrameSamples;
    private const int Window = 2 * Hop;
    private const int Bins = Window / 2 + 1;
    // Output lags input by five hops (50 ms): one hop to fill the analysis window and
    // four in the model and the overlap-add (the reference script trims 2 windows).
    public const int DelaySamples = 5 * Hop;

    private readonly InferenceSession session;
    private readonly RunOptions run = new();
    private readonly string[] inputNames;
    private readonly string[] outputNames;
    private readonly float[] spectrum = new float[Bins * 2];
    private readonly float[] enhanced = new float[Bins * 2];
    private readonly float[] stateA;
    private readonly float[] stateB;
    private readonly OrtValue spectrumValue;
    private readonly OrtValue enhancedValue;
    private readonly OrtValue stateAValue;
    private readonly OrtValue stateBValue;
    private bool stateInA = true;
    private readonly StreamingStft stft = new(StreamingStft.VorbisWindow(Window));
    private bool disposed;

    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "models", ModelFile);

    public DpdfnetSuppressor() : this(DefaultModelPath) { }

    public DpdfnetSuppressor(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new InvalidOperationException($"DPDFNet のモデルが見つかりません。 {modelPath}");
        using var options = new SessionOptions
        {
            // One frame is a small graph; extra threads only add wake-up latency.
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
            throw new InvalidOperationException($"DPDFNet のモデルを読み込めませんでした。 {error.Message}", error);
        }
        try
        {
            var meta = session.ModelMetadata.CustomMetadataMap;
            string Meta(string key) => meta.TryGetValue(key, out var value) ? value
                : throw new InvalidOperationException($"DPDFNet のモデルに {key} がありません。");
            int Int(string key) => int.Parse(Meta(key), CultureInfo.InvariantCulture);
            if (Int("sample_rate") != Aec3Processor.SampleRate || Int("n_fft") != Window || Int("hop_length") != Hop
                || Int("window_length") != Window || Int("freq_bins") != Bins || Meta("window_type") != "vorbis")
                throw new InvalidOperationException("DPDFNet のモデルの STFT 設定が想定と異なります。");
            if (session.InputNames.Count != 2 || session.OutputNames.Count != 2)
                throw new InvalidOperationException("DPDFNet のモデルの入出力が想定と異なります。");
            inputNames = [.. session.InputNames];
            outputNames = [.. session.OutputNames];

            // The state starts at the trained normalization means, not zeros.
            int size = Int("state_size"), erbSize = Int("erb_norm_state_size"), specSize = Int("spec_norm_state_size");
            float[] Floats(string key) => Meta(key).Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            float[] erb = Floats("erb_norm_init"), spec = Floats("spec_norm_init");
            if (erb.Length != erbSize || spec.Length != specSize || erbSize + specSize > size)
                throw new InvalidOperationException("DPDFNet のモデルの状態の初期値が不正です。");
            stateA = new float[size];
            stateB = new float[size];
            erb.CopyTo(stateA, 0);
            spec.CopyTo(stateA, erbSize);

            long[] spectrumShape = [1, 1, Bins, 2], stateShape = [size];
            spectrumValue = OrtValue.CreateTensorValueFromMemory(spectrum, spectrumShape);
            enhancedValue = OrtValue.CreateTensorValueFromMemory(enhanced, spectrumShape);
            stateAValue = OrtValue.CreateTensorValueFromMemory(stateA, stateShape);
            stateBValue = OrtValue.CreateTensorValueFromMemory(stateB, stateShape);
        }
        catch
        {
            session.Dispose();
            run.Dispose();
            throw;
        }
    }

    public void Process(float[] frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (frame.Length != Hop)
            throw new ArgumentException("ノイズ除去は 480 サンプル（10 ms）のフレームを必要とします。");

        stft.Analyze(frame, spectrum);

        var stateIn = stateInA ? stateAValue : stateBValue;
        var stateOut = stateInA ? stateBValue : stateAValue;
        try
        {
            session.Run(run, inputNames, [spectrumValue, stateIn], outputNames, [enhancedValue, stateOut]);
        }
        catch (OnnxRuntimeException error)
        {
            throw new InvalidOperationException($"DPDFNet の音声処理に失敗しました。 {error.Message}", error);
        }
        stateInA = !stateInA;
        stft.Synthesize(enhanced, frame);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        spectrumValue.Dispose();
        enhancedValue.Dispose();
        stateAValue.Dispose();
        stateBValue.Dispose();
        run.Dispose();
        session.Dispose();
    }
}
