using System.Numerics;

namespace AecAudioRouter;

// Frame-by-frame STFT with a 50% hop, for the spectral models (DPDFNet, GTCRN).
// Equivalent to a centered STFT (torch.stft / librosa.stft, center=True) whose leading
// padding is zeros, and to the matching inverse when the analysis and synthesis window
// satisfy w[n]^2 + w[n + hop]^2 = 1 (true for vorbis and for sqrt of a periodic Hann),
// so overlap-add needs no further normalization.
internal sealed class StreamingStft
{
    private readonly int hop;
    private readonly float[] window;
    private readonly float[] history;
    private readonly double[] overlap;
    private readonly Fft fft;
    private readonly Complex[] time;
    private readonly Complex[] frequency;

    public StreamingStft(float[] window)
    {
        if (window.Length < 2 || window.Length % 2 != 0)
            throw new ArgumentException("窓の長さは偶数にしてください。", nameof(window));
        this.window = window;
        hop = window.Length / 2;
        history = new float[window.Length];
        overlap = new double[hop];
        fft = new Fft(window.Length);
        time = new Complex[window.Length];
        frequency = new Complex[window.Length];
    }

    public int Hop => hop;
    public int Bins => hop + 1;

    // Appends one hop of input and writes the spectrum of the latest window,
    // interleaved as [re0, im0, re1, im1, ...] (Bins pairs).
    public void Analyze(ReadOnlySpan<float> input, Span<float> spectrum)
    {
        if (input.Length != hop || spectrum.Length != 2 * Bins)
            throw new ArgumentException("STFT の入力の長さが一致しません。");
        Array.Copy(history, hop, history, 0, hop);
        for (int n = 0; n < hop; n++)
            history[hop + n] = float.IsFinite(input[n]) ? Math.Clamp(input[n], -1f, 1f) : 0f;
        for (int n = 0; n < window.Length; n++)
            time[n] = new Complex(history[n] * window[n], 0);
        fft.Forward(time, frequency);
        for (int k = 0; k < Bins; k++)
        {
            spectrum[2 * k] = (float)frequency[k].Real;
            spectrum[2 * k + 1] = (float)frequency[k].Imaginary;
        }
    }

    // Inverts one interleaved spectrum and overlap-adds it, writing one finished hop.
    public void Synthesize(ReadOnlySpan<float> spectrum, Span<float> output)
    {
        if (output.Length != hop || spectrum.Length != 2 * Bins)
            throw new ArgumentException("STFT の出力の長さが一致しません。");
        // Real signal: rebuild the conjugate-symmetric upper half, then inverse.
        for (int k = 0; k < Bins; k++)
            frequency[k] = new Complex(spectrum[2 * k], spectrum[2 * k + 1]);
        for (int k = 1; k < Bins - 1; k++)
            frequency[window.Length - k] = Complex.Conjugate(frequency[k]);
        fft.Inverse(frequency, time);
        for (int n = 0; n < hop; n++)
        {
            double sample = overlap[n] + time[n].Real * window[n];
            overlap[n] = time[hop + n].Real * window[hop + n];
            output[n] = double.IsFinite(sample) ? (float)Math.Clamp(sample, -1, 1) : 0f;
        }
    }

    public static float[] VorbisWindow(int length)
    {
        var window = new float[length];
        for (int n = 0; n < length; n++)
        {
            double s = Math.Sin(Math.PI * (n + 0.5) / length);
            window[n] = (float)Math.Sin(0.5 * Math.PI * s * s);
        }
        return window;
    }

    // Square root of the periodic Hann window (torch.hann_window(length).pow(0.5)).
    public static float[] SqrtHannWindow(int length)
    {
        var window = new float[length];
        for (int n = 0; n < length; n++)
            window[n] = (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / length));
        return window;
    }
}
