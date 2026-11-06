using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AecAudioRouter;

internal static class NoiseNative
{
    private const string Library = "Aec3Native";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ns_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ns_destroy(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern float ns_process(NoiseHandle handle, [In, Out] float[] samples, int count);
}

internal sealed class NoiseHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public NoiseHandle() : base(true) => SetHandle(NoiseNative.ns_create());
    protected override bool ReleaseHandle() { NoiseNative.ns_destroy(handle); return true; }
}

internal enum NoiseSuppressionMode
{
    Off,
    // RNNoise: about 0.1 ms per 10 ms frame, 20 ms delay.
    Light,
    // DPDFNet: about 1 ms per 10 ms frame, 50 ms delay.
    HighQuality,
    // GTCRN at 16 kHz (output band-limited to 8 kHz) for speech recognition and voice AI:
    // about 0.4 ms per 10 ms frame, 33 ms delay.
    Speech16k,
    // DeepFilterNet3 through libDF (experimental, only in builds with Rust): about 0.3 ms per 10 ms frame, 30 ms delay.
    DeepFilterNet3
}

// Removes background noise from 48 kHz, 10 ms frames.
// Runs after AEC3 so the echo canceller still sees a linear echo path.
internal interface INoiseSuppressor : IDisposable
{
    // Processes one frame in place.
    void Process(float[] frame);

    public static INoiseSuppressor Create(NoiseSuppressionMode mode) => mode switch
    {
        NoiseSuppressionMode.Light => new RnnoiseSuppressor(),
        NoiseSuppressionMode.HighQuality => new DpdfnetSuppressor(),
        NoiseSuppressionMode.Speech16k => new GtcrnSuppressor(),
        NoiseSuppressionMode.DeepFilterNet3 => new DeepFilterSuppressor(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

// RNNoise: a small recurrent network, light enough for any CPU.
// Owned by AecPipeline; its lock serializes all calls and disposal.
internal sealed class RnnoiseSuppressor : INoiseSuppressor
{
    // Output lags input by two frames (20 ms): overlap-add plus one frame for the pitch filter.
    public const int DelaySamples = 2 * Aec3Processor.FrameSamples;
    private readonly NoiseHandle handle;

    public RnnoiseSuppressor()
    {
        handle = new NoiseHandle();
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new InvalidOperationException("RNNoise を初期化できませんでした。");
        }
    }

    // Voice-activity probability (0 to 1) of the last processed frame.
    public float VoiceProbability { get; private set; }

    public void Process(float[] frame)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (frame.Length != Aec3Processor.FrameSamples)
            throw new ArgumentException("ノイズ除去は 480 サンプル（10 ms）のフレームを必要とします。");
        float vad = NoiseNative.ns_process(handle, frame, frame.Length);
        if (vad < 0)
            throw new InvalidOperationException("RNNoise の音声処理に失敗しました。");
        VoiceProbability = vad;
    }

    public void Dispose() => handle.Dispose();
}
