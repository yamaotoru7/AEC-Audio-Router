using System.Buffers.Binary;
using NAudio.Wave;

namespace AecAudioRouter;

internal readonly record struct Level(float Peak, double Rms)
{
    public static double ToDbfs(double amplitude) => 20 * Math.Log10(amplitude);
}

internal struct LevelWindow
{
    private float peak;
    private double sumSquares;
    private long samples;

    public void Add(float value)
    {
        peak = Math.Max(peak, Math.Abs(value));
        sumSquares += (double)value * value;
        samples++;
    }

    public void Merge(LevelWindow other)
    {
        peak = Math.Max(peak, other.peak);
        sumSquares += other.sumSquares;
        samples += other.samples;
    }

    public readonly Level Level => samples == 0 ? default : new(peak, Math.Sqrt(sumSquares / samples));
}

internal sealed class Meter
{
    private readonly object gate = new();
    private LevelWindow window;

    public void Add(LevelWindow block)
    {
        lock (gate)
            window.Merge(block);
    }

    public Level ReadAndReset()
    {
        lock (gate)
        {
            var level = window.Level;
            window = default;
            return level;
        }
    }
}

internal sealed class PcmSamples
{
    private readonly int sampleBytes;
    private readonly WaveFormatEncoding encoding;
    public int Channels { get; }
    public int BlockAlign { get; }

    public PcmSamples(WaveFormat format)
    {
        if (format is WaveFormatExtensible extensible)
            format = extensible.ToStandardWaveFormat();

        encoding = format.Encoding;
        sampleBytes = format.BitsPerSample / 8;
        Channels = format.Channels;
        BlockAlign = format.BlockAlign;
        bool supported = encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 8 or 16 or 24 or 32
            || encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        if (!supported || Channels < 1 || BlockAlign != Channels * sampleBytes)
            throw new NotSupportedException($"対応していない音声形式です ({format})");
    }

    // channel == 0 uses all channels. For the mono route, mix by averaging.
    public LevelWindow Analyze(ReadOnlySpan<byte> bytes, int channel = 0, Span<float> mono = default)
    {
        if (channel < 0 || channel > Channels)
            throw new ArgumentException($"チャンネルは 1〜{Channels} または「すべて」を選んでください。");

        int first = channel == 0 ? 0 : channel - 1;
        int count = channel == 0 ? Channels : 1;
        int frames = bytes.Length / BlockAlign;
        var level = new LevelWindow();
        for (int frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            int offset = frame * BlockAlign + first * sampleBytes;
            for (int i = 0; i < count; i++, offset += sampleBytes)
            {
                float value = Decode(bytes[offset..]);
                sum += value;
                if (mono.IsEmpty)
                    level.Add(value);
            }

            if (!mono.IsEmpty)
            {
                float value = (float)Math.Clamp(sum / count, -1, 1);
                mono[frame] = value;
                level.Add(value);
            }
        }
        return level;
    }

    private float Decode(ReadOnlySpan<byte> bytes)
    {
        if (encoding == WaveFormatEncoding.IeeeFloat)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(bytes);
            return float.IsFinite(value) ? value : 0;
        }

        return sampleBytes switch
        {
            1 => (bytes[0] - 128) / 128f,
            2 => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768f,
            3 => ((bytes[0] << 8 | bytes[1] << 16 | bytes[2] << 24) >> 8) / 8388608f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648f
        };
    }
}
