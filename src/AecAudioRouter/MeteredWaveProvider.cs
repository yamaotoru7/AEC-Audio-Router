using NAudio.Wave;

namespace AecAudioRouter;

internal sealed class MeteredWaveProvider(IWaveProvider source, Meter meter) : IWaveProvider
{
    private readonly PcmSamples decoder = new(source.WaveFormat);

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        if (read > 0)
            meter.Add(decoder.Analyze(buffer.AsSpan(offset, read)));
        return read;
    }
}
