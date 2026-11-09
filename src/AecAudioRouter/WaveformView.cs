using System.Windows;
using System.Windows.Media;

namespace AecAudioRouter;

// Draws the peaks of a recording as vertical bars on a decibel scale (so quiet speech still
// shows next to loud echo), the played part brighter, with a playhead.
internal sealed class WaveformView : FrameworkElement
{
    // The range drawn below the loudest peak.
    private const double RangeDb = 48;
    private static readonly Brush PlayheadBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF4)));
    private float[] peaks = [];
    private float scale = 1;
    private double progress;
    private Brush stroke = Brushes.White;
    private Brush faded = Brushes.Gray;

    public Brush Stroke
    {
        get => stroke;
        set
        {
            stroke = value;
            faded = Freeze(new SolidColorBrush(value is SolidColorBrush solid ? solid.Color : Colors.White) { Opacity = 0.4 });
            InvalidateVisual();
        }
    }

    // `scale` is the level drawn at full height, shared by both tracks so they compare.
    public void SetPeaks(float[] values, float fullScale)
    {
        peaks = values;
        scale = Math.Max(fullScale, 1e-4f);
        InvalidateVisual();
    }

    public double Progress
    {
        get => progress;
        set
        {
            if (progress == value) return;
            progress = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext context)
    {
        double width = ActualWidth, height = ActualHeight;
        // Transparent background so the whole area takes clicks.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (peaks.Length == 0 || width <= 0)
            return;
        double middle = height / 2, column = width / peaks.Length;
        double bar = Math.Max(1, column * 0.7);
        for (int i = 0; i < peaks.Length; i++)
        {
            double x = i * column;
            double db = 20 * Math.Log10(Math.Max(peaks[i], 1e-9f) / scale);
            double half = Math.Max(0.5, Math.Clamp(1 + db / RangeDb, 0, 1) * (middle - 1));
            context.DrawRectangle((i + 0.5) / peaks.Length <= progress ? stroke : faded, null,
                new Rect(x, middle - half, bar, 2 * half));
        }
        if (progress > 0)
            context.DrawRectangle(PlayheadBrush, null, new Rect(Math.Min(width - 2, progress * width), 0, 2, height));
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
