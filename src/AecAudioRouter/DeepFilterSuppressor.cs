using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace AecAudioRouter;

internal static class DeepFilterNative
{
    // libDF's C API (native/build-deepfilternet.ps1), copied as deepfilter.dll.
    private const string Library = "deepfilter";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr df_create([MarshalAs(UnmanagedType.LPUTF8Str)] string path, float attenuationLimitDb, IntPtr logLevel);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern UIntPtr df_get_frame_length(DeepFilterHandle state);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern float df_process_frame(DeepFilterHandle state, float[] input, [Out] float[] output);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void df_free(IntPtr state);
}

internal sealed class DeepFilterHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public DeepFilterHandle(string modelPath) : base(true) =>
        // 100 dB: no attenuation limit, as in the deepFilter command line default.
        SetHandle(DeepFilterNative.df_create(modelPath, 100f, IntPtr.Zero));
    protected override bool ReleaseHandle() { DeepFilterNative.df_free(handle); return true; }
}

// DeepFilterNet3 (Hendrik Schröter, MIT / Apache-2.0), experimental. Runs the author's own
// streaming implementation (libDF with the tract runtime), which does the STFT, the three
// networks and the deep filtering internally on 480-sample hops at 48 kHz.
// Only present when the build had the pinned Rust toolchain (see IsAvailable).
// libDF aborts the process on internal errors (Rust panics cannot cross the C API), so the
// model file is checked against its pinned SHA-256 (DeepFilterSha256 in the project file)
// before it is handed over.
// Known weakness: a strong low tone such as mains hum is rated as clean speech by its
// local-SNR estimate and passes through unprocessed.
// Owned by AecPipeline; its lock serializes all calls and disposal.
internal sealed class DeepFilterSuppressor : INoiseSuppressor
{
    public const string ModelFile = "DeepFilterNet3_onnx.tar.gz";
    // Output lags input by three hops (30 ms): the analysis window and two frames of
    // look-ahead (measured).
    public const int DelaySamples = 3 * Aec3Processor.FrameSamples;
    private readonly DeepFilterHandle handle;
    private readonly float[] input = new float[Aec3Processor.FrameSamples];

    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "models", ModelFile);
    public static bool IsAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "deepfilter.dll")) && File.Exists(DefaultModelPath);

    // The hash the build verified when it downloaded the model.
    internal static string ModelSha256 { get; } = typeof(DeepFilterSuppressor).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "DeepFilterSha256")?.Value ?? "";

    public DeepFilterSuppressor() : this(DefaultModelPath) { }

    public DeepFilterSuppressor(string modelPath)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "deepfilter.dll")))
            throw new InvalidOperationException("DeepFilterNet3 はこのビルドに含まれていません（Rust のツールチェーンが要ります）。");
        if (!File.Exists(modelPath))
            throw new InvalidOperationException($"DeepFilterNet3 のモデルが見つかりません。 {modelPath}");
        string path = Path.GetFullPath(modelPath);
        FileStream model;
        try
        {
            model = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"DeepFilterNet3 のモデルを開けませんでした。 {error.Message}", error);
        }
        using (model)
        {
            // A truncated or damaged archive would make libDF panic, which ends the process.
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(model)), ModelSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"DeepFilterNet3 のモデルが壊れています（ハッシュが一致しません）。 {path}");
            // Opened with read sharing only and kept open until libDF has loaded it,
            // so the file cannot be changed between the check and the load.
            handle = new DeepFilterHandle(path);
        }
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new InvalidOperationException("DeepFilterNet3 を初期化できませんでした。");
        }
        if (DeepFilterNative.df_get_frame_length(handle).ToUInt64() != Aec3Processor.FrameSamples)
        {
            handle.Dispose();
            throw new InvalidOperationException("DeepFilterNet3 のフレーム長が 480 サンプルではありません。");
        }
    }

    public void Process(float[] frame)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (frame.Length != Aec3Processor.FrameSamples)
            throw new ArgumentException("ノイズ除去は 480 サンプル（10 ms）のフレームを必要とします。");
        for (int n = 0; n < frame.Length; n++)
            input[n] = float.IsFinite(frame[n]) ? Math.Clamp(frame[n], -1f, 1f) : 0f;
        DeepFilterNative.df_process_frame(handle, input, frame);
        for (int n = 0; n < frame.Length; n++)
            frame[n] = float.IsFinite(frame[n]) ? Math.Clamp(frame[n], -1f, 1f) : 0f;
    }

    public void Dispose() => handle.Dispose();
}
