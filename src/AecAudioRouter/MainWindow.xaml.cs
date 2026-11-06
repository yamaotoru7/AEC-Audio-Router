using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace AecAudioRouter;

public partial class MainWindow : Window
{
    private const string CableDownloadUrl = "https://github.com/tarekwasfy01/Virtual-Cables/releases/tag/Virtual-Cables";
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Process process = Process.GetCurrentProcess();
    private readonly string? settingsPath;
    private AppSettings settings;
    private ReleaseInfo? availableUpdate;
    private bool checkingUpdate;
    private Endpoint[] inputs = [];
    private Endpoint[] outputs = [];
    private AudioSession? session;
    private TimeSpan previousCpu;
    private long previousCpuTime;
    private double? cpuPercent;

    private ReferenceMode Mode => (ReferenceMode)ReferenceKind.SelectedIndex;

    public MainWindow() : this(AppSettings.DefaultPath) { }

    // A null path disables persistence (used by tests).
    internal MainWindow(string? settingsPath)
    {
        this.settingsPath = settingsPath;
        InitializeComponent();
        var saved = settings = settingsPath is null ? new AppSettings() : AppSettings.Load(settingsPath);
        ReferenceKind.SelectedIndex = (int)saved.ReferenceMode;
        ReferenceChannel.Text = saved.ReferenceChannel;
        MicChannel.Text = saved.MicrophoneChannel;
        FilePath.Text = saved.PlaybackFile ?? "";
        AecToggle.IsChecked = saved.AecEnabled;
        DeepFilterItem.IsEnabled = DeepFilterSuppressor.IsAvailable;
        NoiseMode.SelectedIndex = (int)(saved.NoiseSuppression == NoiseSuppressionMode.DeepFilterNet3 && !DeepFilterSuppressor.IsAvailable
            ? NoiseSuppressionMode.Off : saved.NoiseSuppression);
        timer.Tick += UpdateMeters;
        ResetMeters();
        ReloadDevices(saved);
        Loaded += (_, _) => CheckForUpdatesOnStartup();
    }

    // Installed copies check the GitHub releases at most once per Updater.Interval, without dialogs.
    private async void CheckForUpdatesOnStartup()
    {
        if (settingsPath is null || !settings.CheckForUpdates || !Updater.IsInstalledCopy
            || !Updater.IsDue(settings.LastUpdateCheck, DateTimeOffset.Now))
            return;
        await Task.Delay(TimeSpan.FromSeconds(5));
        await CheckForUpdatesAsync(interactive: false);
    }

    private async void UpdateClick(object sender, RoutedEventArgs e)
    {
        if (availableUpdate is { } known)
            OfferUpdate(known);
        else
            await CheckForUpdatesAsync(interactive: true);
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (checkingUpdate)
            return;
        checkingUpdate = true;
        UpdateButton.IsEnabled = false;
        if (interactive)
            UpdateButton.Content = "確認中…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var latest = await Updater.GetLatestAsync(timeout.Token);
            settings = settings with { LastUpdateCheck = DateTimeOffset.Now };
            SaveSettings();
            if (latest is not null && Updater.IsNewer(latest, Updater.CurrentVersion))
            {
                availableUpdate = latest;
                UpdateButton.Style = (Style)FindResource("Primary");
                UpdateButton.ToolTip = $"AEC Audio Router {latest.Version} があります（現在 {Updater.CurrentVersion}）。";
                if (interactive)
                    OfferUpdate(latest);
            }
            else if (interactive)
            {
                MessageBox.Show(this, $"AEC Audio Router {Updater.CurrentVersion} は最新です。", "更新の確認",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Trace.TraceWarning($"更新を確認できませんでした。 {error}");
            if (interactive)
                MessageBox.Show(this, $"更新を確認できませんでした。\n{error.Message}", "更新の確認",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            checkingUpdate = false;
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = availableUpdate is { } update ? $"{update.Version} に更新" : "更新を確認";
        }
    }

    private void OfferUpdate(ReleaseInfo release)
    {
        var dialog = new UpdateWindow(release, session is not null) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.InstallerPath is not { } installer)
            return;
        try
        {
            Updater.StartInstaller(installer);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"インストーラーを起動できませんでした。\n{error.Message}", "更新",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        // Settings are saved and the session stopped in OnClosed; the installer relaunches the app.
        Close();
    }

    private void ReloadClick(object sender, RoutedEventArgs e) => ReloadDevices();

    // Devices that are no longer present are left unselected.
    private void ReloadDevices(AppSettings? saved = null)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var capture = Endpoint.List(enumerator, DataFlow.Capture);
            var render = Endpoint.List(enumerator, DataFlow.Render);
            string? mic = saved?.Microphone ?? MicDevice.SelectedValue as string;
            string? output = saved?.Output ?? OutputDevice.SelectedValue as string;
            string? reference = saved?.ReferenceDevice ?? ReferenceDevice.SelectedValue as string;
            inputs = capture;
            outputs = render;
            SetDevices(MicDevice, inputs, mic);
            SetDevices(OutputDevice, outputs, output);
            SetReferenceDevices(reference);

            StartButton.IsEnabled = inputs.Length != 0 && outputs.Length != 0;
            Status.Text = $"デバイス  入力 {inputs.Length}  ·  出力 {outputs.Length}";
        }
        catch (Exception error)
        {
            StartButton.IsEnabled = false;
            Status.Text = $"デバイス一覧を取得できませんでした。 {error.Message}";
        }
    }

    private void OpenCableDownload(object sender, RoutedEventArgs e)
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(CableDownloadUrl) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Status.Text = $"ブラウザーを開けませんでした。 {CableDownloadUrl}";
            Trace.TraceError(error.ToString());
        }
    }

    private void ReferenceKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceDevice is not null)
            SetReferenceDevices(ReferenceDevice.SelectedValue as string);
    }

    private void SetReferenceDevices(string? preferred)
    {
        SetDevices(ReferenceDevice, Mode == ReferenceMode.Input ? inputs : outputs, preferred);
        ReferenceLabel.Content = Mode switch
        {
            ReferenceMode.Input => "参照入力",
            ReferenceMode.Loopback => "取り込む再生デバイス",
            _ => "WAV の再生先"
        };
        FileSettings.Visibility = Mode == ReferenceMode.File ? Visibility.Visible : Visibility.Collapsed;
        ReferenceChannel.IsEnabled = Mode != ReferenceMode.File;
    }

    private static void SetDevices(ComboBox box, Endpoint[] devices, string? preferred)
    {
        box.ItemsSource = devices;
        box.SelectedValue = preferred;
    }

    private void BrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "WAV 音声 (*.wav)|*.wav", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
            FilePath.Text = dialog.FileName;
    }

    private static int ReadChannel(TextBox box)
    {
        if (!int.TryParse(box.Text, out int channel) || channel < 0)
            throw new ArgumentException("チャンネルには 0 または正の整数を入力してください。");
        return channel;
    }

    private void StartClick(object sender, RoutedEventArgs e)
    {
        AudioSession? pending = null;
        try
        {
            var route = new RouteSettings(
                Mode, ReferenceDevice.SelectedValue as string, FilePath.Text,
                MicDevice.SelectedValue as string ?? "", OutputDevice.SelectedValue as string ?? "",
                Mode == ReferenceMode.File ? 0 : ReadChannel(ReferenceChannel), ReadChannel(MicChannel));
            route.Validate();
            pending = new AudioSession(
                message => Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(session, pending))
                        StopSession(message);
                }),
                () => Dispatcher.BeginInvoke(() =>
                {
                    if (ReferenceEquals(session, pending))
                        Status.Text = "WAV の再生終了。マイクは出力中です。";
                }));

            pending.AecEnabled = IsAecOn;
            pending.NoiseSuppression = NoiseSelection;
            pending.Start(route);
            previousCpu = process.TotalProcessorTime;
            previousCpuTime = Stopwatch.GetTimestamp();
            cpuPercent = null;
            session = pending;
            SetRunning(true);
            Status.Text = $"{(IsAecOn ? "AEC3" : "AEC3 無効")}  ·  {session.SampleRate / 1000.0:0.###} kHz  ·  モノラル  ·  同期用バッファ {AecPipeline.AlignmentDelayMs} ms";
            timer.Start();
            SaveSettings();
        }
        catch (Exception error)
        {
            pending?.Dispose();
            StopSession($"開始できませんでした。 {error.Message}");
        }
    }

    private void StopClick(object sender, RoutedEventArgs e) => StopSession("");

    private void StopSession(string message)
    {
        timer.Stop();
        var previous = session;
        session = null;
        previous?.Dispose();
        SetRunning(false);
        ResetMeters();
        Status.Text = message;
    }

    private void SetRunning(bool running)
    {
        Settings.IsEnabled = !running;
        ReloadButton.IsEnabled = !running;
        StartButton.IsEnabled = !running && inputs.Length != 0 && outputs.Length != 0;
        StopButton.IsEnabled = running;
        StateLabel.Text = running ? "出力中" : "停止中";
        UpdateAecState();
    }

    private bool IsAecOn => AecToggle.IsChecked == true;

    private void AecToggleChanged(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent, before the rest of the window exists.
        if (AecState is null)
            return;
        if (session is { } active)
            active.AecEnabled = IsAecOn;
        UpdateAecState();
    }

    private NoiseSuppressionMode NoiseSelection => (NoiseSuppressionMode)Math.Max(0, NoiseMode.SelectedIndex);

    private void NoiseModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (session is not { } active || active.NoiseSuppression == NoiseSelection)
            return;
        try
        {
            active.NoiseSuppression = NoiseSelection;
        }
        catch (InvalidOperationException error)
        {
            // The previous engine stays in effect; show it again.
            Status.Text = $"ノイズ除去を切り替えられませんでした。 {error.Message}";
            NoiseMode.SelectedIndex = (int)active.NoiseSuppression;
        }
    }

    private void UpdateAecState() =>
        AecState.Text = session is null ? "AEC3 停止中" : IsAecOn ? "AEC3 有効" : "AEC3 無効";

    private void UpdateMeters(object? sender, EventArgs e)
    {
        if (session is not { } active)
            return;

        RenderLevel(ReferenceMeter, ReferenceValue, active.Reference.ReadAndReset());
        RenderLevel(MicMeter, MicValue, active.Microphone.ReadAndReset());
        RenderLevel(OutputMeter, OutputValue, active.Output.ReadAndReset());
        var (callback, queued, dropped, missing) = active.ReadTiming();

        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(previousCpuTime, now).TotalSeconds;
        if (elapsed >= 1)
        {
            var cpu = process.TotalProcessorTime;
            cpuPercent = 100 * (cpu - previousCpu).TotalSeconds / (elapsed * Environment.ProcessorCount);
            previousCpu = cpu;
            previousCpuTime = now;
        }

        CpuValue.Text = cpuPercent.HasValue ? $"{cpuPercent.Value:F2}%" : "—";
        ProcessingValue.Text = callback.HasValue ? $"{callback.Value:F3} ms" : "—";
        QueueValue.Text = $"{queued:F1} ms";
        BufferStats.Text = $"バッファ要求 {AudioSession.RequestedOutputMs} ms  ·  廃棄 {dropped:N0} / 無音補完 {missing:N0} サンプル";
        var aecStats = active.AecStatus;
        BufferStats.Text += $"\n参照欠落 {aecStats.Missing:N0} / 同期廃棄 {aecStats.Dropped:N0} フレーム  ·  AEC 再同期 {aecStats.Resets:N0}";
    }

    private void ResetMeters()
    {
        RenderLevel(ReferenceMeter, ReferenceValue, default);
        RenderLevel(MicMeter, MicValue, default);
        RenderLevel(OutputMeter, OutputValue, default);
        CpuValue.Text = ProcessingValue.Text = QueueValue.Text = "—";
        BufferStats.Text = $"バッファ要求 {AudioSession.RequestedOutputMs} ms";
    }

    private static void RenderLevel(ProgressBar bar, TextBlock text, Level level)
    {
        double peak = Level.ToDbfs(level.Peak);
        double rms = Level.ToDbfs(level.Rms);
        bar.Value = Math.Clamp(peak, bar.Minimum, bar.Maximum);
        text.Text = $"Peak {FormatDb(peak)}  ·  RMS {FormatDb(rms)}";
    }

    private static string FormatDb(double db) => double.IsNegativeInfinity(db) ? "−∞" : db.ToString("F1");

    private void SaveSettings()
    {
        if (settingsPath is null)
            return;
        settings = settings with
        {
            ReferenceMode = Mode,
            ReferenceDevice = ReferenceDevice.SelectedValue as string,
            ReferenceChannel = ReferenceChannel.Text,
            PlaybackFile = string.IsNullOrEmpty(FilePath.Text) ? null : FilePath.Text,
            Microphone = MicDevice.SelectedValue as string,
            MicrophoneChannel = MicChannel.Text,
            Output = OutputDevice.SelectedValue as string,
            AecEnabled = IsAecOn,
            NoiseSuppression = NoiseSelection
        };
        settings.Save(settingsPath);
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveSettings();
        StopSession("");
        process.Dispose();
        base.OnClosed(e);
    }
}
