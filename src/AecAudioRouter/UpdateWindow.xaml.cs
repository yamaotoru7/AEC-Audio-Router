using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace AecAudioRouter;

// Shows the release notes. "更新して再起動" downloads and verifies the installer; DialogResult is true when it is ready to start.
public partial class UpdateWindow : Window
{
    private readonly ReleaseInfo release;
    private CancellationTokenSource? download;

    internal string? InstallerPath { get; private set; }

    internal UpdateWindow(ReleaseInfo release, bool routing)
    {
        this.release = release;
        InitializeComponent();
        Headline.Text = $"AEC Audio Router {release.Version} があります";
        Subline.Text = $"現在の版は {Updater.CurrentVersion} です。" + (routing ? "更新すると出力中の音声は停止します。" : "");
        Notes.Text = release.Notes.Length > 0 ? release.Notes : "（リリースノートはありません）";
    }

    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        download = new CancellationTokenSource();
        try
        {
            ProgressText.Text = $"{release.InstallerName} をダウンロードしています…";
            var progress = new Progress<double>(p => ProgressText.Text = $"{release.InstallerName} をダウンロードしています… {p:P0}");
            InstallerPath = await Updater.DownloadAsync(release, progress, download.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException) when (download.IsCancellationRequested)
        {
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or TaskCanceledException)
        {
            Trace.TraceError(error.ToString());
            ProgressText.Text = $"ダウンロードできませんでした。 {error.Message}";
            InstallButton.IsEnabled = true;
        }
        finally
        {
            download?.Dispose();
            download = null;
        }
    }

    private void LaterClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        download?.Cancel();
        base.OnClosing(e);
    }

    private void OpenPage(object sender, RoutedEventArgs e)
    {
        string url = release.PageUrl.Length > 0 ? release.PageUrl : Updater.ReleasesPage;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            ProgressText.Text = $"ブラウザーを開けませんでした。 {url}";
            Trace.TraceError(error.ToString());
        }
    }
}
