using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AecAudioRouter;

// Plays back what the last session recorded, before and after processing. Opened only while
// stopped (and modal, so the session cannot start while it plays).
public partial class ListenBackWindow : Window
{
    private const int WaveformColumns = 300;
    private readonly ListenBackRecording recording;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private ListenBackPlayer? player;

    internal ListenBackSource Source { get; }

    // `processed` is the track to start with; Processed reports the one chosen last.
    internal ListenBackWindow(ListenBackRecording recording, bool processed)
    {
        this.recording = recording;
        Source = new ListenBackSource(recording) { Processed = processed };
        InitializeComponent();
        (processed ? AfterLane : BeforeLane).IsChecked = true;
        int seconds = Math.Max(1, (int)Math.Round(recording.Duration.TotalSeconds));
        Headline.Text = Title = $"停止前の {seconds} 秒";
        var before = ListenBackRecording.Peaks(recording.Before, WaveformColumns);
        var after = ListenBackRecording.Peaks(recording.After, WaveformColumns);
        // One scale for both, so the difference shows.
        float scale = Math.Max(before.DefaultIfEmpty().Max(), after.DefaultIfEmpty().Max());
        BeforeWave.Stroke = (Brush)FindResource("Muted");
        AfterWave.Stroke = (Brush)FindResource("Accent");
        BeforeWave.SetPeaks(before, scale);
        AfterWave.SetPeaks(after, scale);
        timer.Tick += (_, _) => UpdatePosition();
        UpdatePosition();
    }

    internal bool Processed => Source.Processed;

    private void PlayClick(object sender, RoutedEventArgs e)
    {
        if (player is not null)
        {
            StopPlayback();
            return;
        }
        if (Source.AtEnd)
            Source.Seek(0);
        ListenBackPlayer? started = null;
        try
        {
            started = new ListenBackPlayer(Source);
            started.Stopped += error => PlaybackStopped(started, error);
            started.Play();
        }
        catch (Exception error)
        {
            started?.Dispose();
            DeviceText.Text = $"再生できませんでした。 {error.Message}";
            return;
        }
        player = started;
        DeviceText.Text = started.DeviceName;
        timer.Start();
        UpdatePosition();
    }

    // Raised on the UI thread, also after StopPlayback has disposed the player.
    private void PlaybackStopped(ListenBackPlayer stopped, Exception? error)
    {
        if (!ReferenceEquals(player, stopped))
            return;
        StopPlayback();
        if (error is not null)
            DeviceText.Text = $"再生が止まりました。 {error.Message}";
        else if (Source.AtEnd)
            Source.Seek(0);
        UpdatePosition();
    }

    private void StopPlayback()
    {
        var previous = player;
        player = null;
        timer.Stop();
        previous?.Dispose();
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        PlayButton.Content = player is null ? "▶ 再生 (_P)" : "■ 止める (_P)";
        BeforeWave.Progress = AfterWave.Progress = Source.Progress;
        TimeText.Text = $@"{Source.Position:m\:ss} / {recording.Duration:m\:ss}";
    }

    private void LaneChecked(object sender, RoutedEventArgs e)
    {
        Source.Processed = AfterLane.IsChecked == true;
    }

    // A click on the lane already being heard moves the playback position; on the other lane
    // it only switches, so the comparison stays at the same moment.
    private void LaneMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } lane)
            return;
        var wave = ReferenceEquals(lane, AfterLane) ? AfterWave : BeforeWave;
        if (wave.ActualWidth <= 0)
            return;
        Source.Seek(e.GetPosition(wave).X / wave.ActualWidth);
        UpdatePosition();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        StopPlayback();
        base.OnClosed(e);
    }
}
