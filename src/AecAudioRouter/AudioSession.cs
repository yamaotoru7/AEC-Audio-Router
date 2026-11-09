using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AecAudioRouter;

internal enum ReferenceMode { Input, Loopback, File }

internal sealed record RouteSettings(
    ReferenceMode Mode, string? ReferenceEndpoint, string? PlaybackFile,
    string Microphone, string Output, int ReferenceChannel, int MicrophoneChannel)
{
    public void Validate()
    {
        if (string.IsNullOrEmpty(ReferenceEndpoint))
            throw new ArgumentException("参照音声のデバイスを選んでください。");
        if (string.IsNullOrEmpty(Microphone) || string.IsNullOrEmpty(Output))
            throw new ArgumentException("マイクとルーティング先を選んでください。");
        if (Mode != ReferenceMode.Input && ReferenceEndpoint == Output)
            throw new ArgumentException("参照用の再生先とマイクの出力先は別のデバイスを選んでください。");
        if (Mode == ReferenceMode.File && string.IsNullOrWhiteSpace(PlaybackFile))
            throw new ArgumentException("再生する WAV ファイルを選んでください。");
        if (Mode == ReferenceMode.Input && ReferenceEndpoint == Microphone &&
            (ReferenceChannel == 0 || MicrophoneChannel == 0 || ReferenceChannel == MicrophoneChannel))
            throw new ArgumentException("同じ入力デバイスを使う場合、参照とマイクには別々の CH 番号を指定してください。");
    }
}

internal sealed record Endpoint(string Id, string Name)
{
    // The microphone output choice that sends the processed audio nowhere.
    public static readonly Endpoint None = new("none", "なし（出力しない）");

    public static Endpoint[] List(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var devices = new List<Endpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
                devices.Add(new Endpoint(device.ID, device.FriendlyName));
        }

        return devices.OrderBy(device => device.Name).ToArray();
    }
}

internal sealed class AudioSession(Action<string> onStopped, Action onFileEnded) : IDisposable
{
    public const int RequestedOutputMs = 30;
    public const int MaxQueueMs = 80;

    private readonly Stack<IDisposable> resources = new();
    private readonly object timingGate = new();
    private RouteBuffer? route;
    public bool Routed => route is not null;
    private AecPipeline? aec;
    private int disposed;
    private volatile bool aecEnabled = true;
    private NoiseSuppressionMode noiseSuppression;
    private int faulted;
    private long micTicks;
    private long micBlocks;

    public Meter Reference { get; } = new();
    public Meter Microphone { get; } = new();
    public Meter Output { get; } = new();
    // The last seconds before and after processing, for listening back after the session stops.
    public ListenBackRecorder ListenBack { get; } = new();
    public int SampleRate { get; private set; }
    public bool AecEnabled
    {
        get => aecEnabled;
        set
        {
            aecEnabled = value;
            if (aec is { } pipeline) pipeline.Enabled = value;
        }
    }

    // Set from the UI thread. While running, a failure to load the engine throws and
    // leaves the previous mode in effect.
    public NoiseSuppressionMode NoiseSuppression
    {
        get => noiseSuppression;
        set
        {
            aec?.SetNoiseSuppression(value);
            noiseSuppression = value;
        }
    }

    public (long Missing, long Dropped, long Resets) AecStatus => aec?.Status ?? default;

    public void Start(RouteSettings settings)
    {
        settings.Validate();
        var devices = Own(new MMDeviceEnumerator());
        var micDevice = Own(devices.GetDevice(settings.Microphone));
        var mic = Own(new WasapiCapture(micDevice, false, 20));
        var micFormat = mic.WaveFormat;
        var micDecoder = new PcmSamples(micFormat);
        ValidateChannel(micDecoder, settings.MicrophoneChannel);
        SampleRate = Aec3Processor.SampleRate;
        bool routed = settings.Output != Endpoint.None.Id;
        var buffer = routed ? new RouteBuffer(SampleRate, SampleRate * MaxQueueMs / 1000) : null;
        route = buffer;

        bool sharedCapture = settings.Mode == ReferenceMode.Input && settings.ReferenceEndpoint == settings.Microphone;
        if (sharedCapture)
            ValidateChannel(micDecoder, settings.ReferenceChannel);

        // Grows only if WASAPI supplies a larger packet than expected.
        var samples = new float[SampleRate / 10];
        var sharedReference = new float[SampleRate / 10];
        mic.DataAvailable += (_, packet) =>
        {
            if (packet.BytesRecorded == 0 || Volatile.Read(ref disposed) != 0 || Volatile.Read(ref faulted) != 0)
                return;

            long start = Stopwatch.GetTimestamp();
            try
            {
                double arrival = (double)start / Stopwatch.Frequency;
                var bytes = packet.Buffer.AsSpan(0, packet.BytesRecorded);
                int frames = bytes.Length / micDecoder.BlockAlign;
                if (frames > samples.Length)
                    Array.Resize(ref samples, frames);

                var mono = samples.AsSpan(0, frames);
                var level = micDecoder.Analyze(bytes, settings.MicrophoneChannel, mono);
                Microphone.Add(level);
                if (sharedCapture)
                {
                    if (frames > sharedReference.Length) Array.Resize(ref sharedReference, frames);
                    var referenceMono = sharedReference.AsSpan(0, frames);
                    Reference.Add(micDecoder.Analyze(bytes, settings.ReferenceChannel, referenceMono));
                    aec!.AddReference(referenceMono, arrival);
                }

                aec!.AddMicrophone(mono, arrival);
                long elapsed = Stopwatch.GetTimestamp() - start;
                lock (timingGate)
                {
                    micTicks += elapsed;
                    micBlocks++;
                }
            }
            catch (Exception error) { UnexpectedStop("マイク処理", error); }
        };
        mic.RecordingStopped += (_, e) => UnexpectedStop("マイク入力", e.Exception);

        WasapiOut? output = null;
        if (buffer is not null)
        {
            var outputDevice = Own(devices.GetDevice(settings.Output));
            output = Own(new WasapiOut(outputDevice, AudioClientShareMode.Shared, false, RequestedOutputMs));
            output.Init(new SampleToWaveProvider(buffer));
            output.PlaybackStopped += (_, e) => UnexpectedStop("マイク出力", e.Exception);
        }

        WasapiCapture? referenceCapture = null;
        WasapiOut? referencePlayback = null;
        int referenceRate = micFormat.SampleRate;
        if (!sharedCapture)
        {
            var referenceDevice = Own(devices.GetDevice(settings.ReferenceEndpoint!));
            if (settings.Mode == ReferenceMode.File)
            {
                var file = Own(new WaveFileReader(settings.PlaybackFile));
                if (file.Length == 0)
                    throw new ArgumentException("WAV ファイルに音声データがありません。");
                referencePlayback = Own(new WasapiOut(referenceDevice, AudioClientShareMode.Shared, false, RequestedOutputMs));
                referencePlayback.Init(file);
                referencePlayback.PlaybackStopped += (_, e) =>
                {
                    if (e.Exception is not null)
                        UnexpectedStop("参照 WAV 再生", e.Exception);
                    else if (Volatile.Read(ref disposed) == 0)
                        onFileEnded();
                };
            }
            // File mode also uses the actual endpoint loopback: file read-ahead is
            // not the time at which the speaker rendered those samples.
            referenceCapture = Own(settings.Mode != ReferenceMode.Input
                ? new LowLatencyLoopbackCapture(referenceDevice)
                : new WasapiCapture(referenceDevice, false, 20));
            referenceRate = referenceCapture.WaveFormat.SampleRate;
            var decoder = new PcmSamples(referenceCapture.WaveFormat);
            ValidateChannel(decoder, settings.ReferenceChannel);
            var referenceSamples = new float[referenceRate / 10];
            referenceCapture.DataAvailable += (_, packet) =>
            {
                if (packet.BytesRecorded == 0 || Volatile.Read(ref disposed) != 0 || Volatile.Read(ref faulted) != 0) return;
                double arrival = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
                try
                {
                    var bytes = packet.Buffer.AsSpan(0, packet.BytesRecorded);
                    int frames = bytes.Length / decoder.BlockAlign;
                    if (frames > referenceSamples.Length) Array.Resize(ref referenceSamples, frames);
                    var mono = referenceSamples.AsSpan(0, frames);
                    Reference.Add(decoder.Analyze(bytes, settings.ReferenceChannel, mono));
                    aec!.AddReference(mono, arrival);
                }
                catch (Exception error) { UnexpectedStop("AEC3 参照処理", error); }
            };
            referenceCapture.RecordingStopped += (_, e) => UnexpectedStop("参照入力", e.Exception);
        }

        aec = Own(new AecPipeline(micFormat.SampleRate, referenceRate, buffer, Output, ListenBack) { Enabled = aecEnabled });
        aec.SetNoiseSuppression(noiseSuppression);
        referenceCapture?.StartRecording();
        mic.StartRecording();
        output?.Play();
        referencePlayback?.Play();
    }

    // QueueMs is null without an output.
    public (double? CallbackMs, double? QueueMs, long Dropped, long Missing) ReadTiming()
    {
        double? callback;
        lock (timingGate)
        {
            callback = micBlocks > 0 ? 1000.0 * micTicks / Stopwatch.Frequency / micBlocks : null;
            micTicks = micBlocks = 0;
        }
        if (route is null)
            return (callback, null, 0, 0);
        var (queued, dropped, missing) = route.Status;
        return (callback, queued, dropped, missing);
    }

    private static void ValidateChannel(PcmSamples decoder, int channel)
    {
        if (channel < 0 || channel > decoder.Channels)
            throw new ArgumentException($"チャンネルは 1〜{decoder.Channels} または 0 を指定してください。");
    }

    private void UnexpectedStop(string path, Exception? error)
    {
        if (Volatile.Read(ref disposed) == 0 && Interlocked.Exchange(ref faulted, 1) == 0)
            onStopped(error is null ? $"{path}が停止しました。" : $"{path}が停止しました。{error.Message}");
    }

    private T Own<T>(T resource) where T : IDisposable
    {
        resources.Push(resource);
        return resource;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        while (resources.TryPop(out var resource))
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception error)
            {
                Trace.TraceError(error.ToString());
            }
        }
    }
}

// Poll at 20 ms, rather than the 100 ms default of WasapiLoopbackCapture.
internal sealed class LowLatencyLoopbackCapture(MMDevice device) : WasapiCapture(device, false, 20)
{
    protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
        base.GetAudioClientStreamFlags() | AudioClientStreamFlags.Loopback;
}
