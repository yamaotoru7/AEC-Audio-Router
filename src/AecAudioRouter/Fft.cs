using System.Numerics;

namespace AecAudioRouter;

// Mixed-radix (any factorization) complex FFT for a fixed length, without allocation per call.
// Used for the 960-point STFT of DPDFNet (960 = 2^6 * 3 * 5), so a power-of-two FFT will not do.
internal sealed class Fft
{
    private readonly int length;
    private readonly int[] factors;
    private readonly Complex[] twiddles;
    private readonly Complex[] work;

    public Fft(int length)
    {
        if (length < 1) throw new ArgumentOutOfRangeException(nameof(length));
        this.length = length;
        var found = new List<int>();
        for (int rest = length, factor = 2; rest > 1;)
        {
            if (rest % factor == 0) { found.Add(factor); rest /= factor; }
            else factor++;
        }
        factors = [.. found];
        twiddles = new Complex[length];
        for (int k = 0; k < length; k++)
            twiddles[k] = Complex.FromPolarCoordinates(1, -2 * Math.PI * k / length);
        work = new Complex[length];
    }

    public int Length => length;

    // output[k] = sum_n input[n] * exp(-2 pi i k n / N). input and output must not overlap.
    public void Forward(ReadOnlySpan<Complex> input, Span<Complex> output)
    {
        if (input.Length != length || output.Length != length)
            throw new ArgumentException("FFT の長さが一致しません。");
        Transform(input, 1, output, length, 0);
    }

    // output[n] = (1 / N) * sum_k input[k] * exp(+2 pi i k n / N), via conj(FFT(conj(x))) / N.
    public void Inverse(ReadOnlySpan<Complex> input, Span<Complex> output)
    {
        if (input.Length != length || output.Length != length)
            throw new ArgumentException("FFT の長さが一致しません。");
        for (int i = 0; i < length; i++) work[i] = Complex.Conjugate(input[i]);
        Transform(work, 1, output, length, 0);
        double scale = 1.0 / length;
        for (int i = 0; i < length; i++) output[i] = Complex.Conjugate(output[i]) * scale;
    }

    // Decimation in time: split into p interleaved sub-sequences, transform each, then
    // combine with p-point DFT butterflies. Each output column k is read before written.
    private void Transform(ReadOnlySpan<Complex> input, int stride, Span<Complex> output, int size, int depth)
    {
        if (size == 1) { output[0] = input[0]; return; }
        int p = factors[depth], m = size / p, step = length / size;
        for (int r = 0; r < p; r++)
            Transform(input[(r * stride)..], stride * p, output.Slice(r * m, m), m, depth + 1);
        Span<Complex> column = stackalloc Complex[p];
        for (int k = 0; k < m; k++)
        {
            for (int r = 0; r < p; r++)
                column[r] = output[r * m + k] * twiddles[r * k * step % length];
            for (int q = 0; q < p; q++)
            {
                Complex sum = column[0];
                for (int r = 1; r < p; r++)
                    sum += column[r] * twiddles[r * q * m * step % length];
                output[q * m + k] = sum;
            }
        }
    }
}
