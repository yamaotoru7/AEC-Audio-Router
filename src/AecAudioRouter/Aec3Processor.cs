using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AecAudioRouter;

internal static class Aec3Native
{
    private const string Library = "Aec3Native";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int aec3_abi_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr aec3_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void aec3_destroy(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int aec3_render(Aec3Handle handle, float[] samples, int count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int aec3_capture(Aec3Handle handle, [In, Out] float[] samples, int count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr aec3_resampler_create(int rate);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void aec3_resampler_destroy(IntPtr handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int aec3_resample(ResamplerHandle handle, float[] input, int count, [Out] float[] output);
}

internal sealed class Aec3Handle : SafeHandleZeroOrMinusOneIsInvalid
{
    public Aec3Handle() : base(true) => SetHandle(Aec3Native.aec3_create());
    protected override bool ReleaseHandle() { Aec3Native.aec3_destroy(handle); return true; }
}

internal sealed class ResamplerHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public ResamplerHandle(int rate) : base(true) => SetHandle(Aec3Native.aec3_resampler_create(rate));
    protected override bool ReleaseHandle() { Aec3Native.aec3_resampler_destroy(handle); return true; }
}

// Owned by AecPipeline; its lock serializes all calls and disposal.
internal sealed class Aec3Processor : IDisposable
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 480;
    private readonly Aec3Handle handle;

    public Aec3Processor()
    {
        try
        {
            if (Aec3Native.aec3_abi_version() != 1)
                throw new InvalidOperationException("AEC3 DLL のバージョンが一致しません。");
            handle = new Aec3Handle();
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new InvalidOperationException("WebRTC AEC3 を初期化できませんでした。");
            }
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException("AEC3Native.dll を読み込めません。Windows x64 版を再ビルドしてください。", error);
        }
    }

    public void Process(float[] reference, float[] microphone)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (reference.Length != FrameSamples || microphone.Length != FrameSamples)
            throw new ArgumentException("AEC3 は 480 サンプル（10 ms）のフレームを必要とします。");
        if (Aec3Native.aec3_render(handle, reference, reference.Length) != 0 ||
            Aec3Native.aec3_capture(handle, microphone, microphone.Length) != 0)
            throw new InvalidOperationException("WebRTC AEC3 の音声処理に失敗しました。");
    }

    public void Dispose() => handle.Dispose();
}
