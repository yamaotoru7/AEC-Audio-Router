using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;

namespace AecAudioRouter.Tests;

internal static class AudioTests
{
    [STAThread]
    public static int Main(string[] args)
    {
        Action[] tests =
        [
            StereoMeterKeepsBothChannelsAndRouteCanSelectOne,
            IntegerPcmDecodesNegativeFullScaleAndSilence,
            MeterResetsAndNonFiniteFloatBecomesSilence,
            BoundedRouteEvictsOldSamplesAndFillsUnderrunWithSilence,
            InAppPlaybackMeterReadsWavWithoutChangingTheBytes,
            ReferenceOutputCannotBeTheMicrophoneRouteDestination,
            SettingsRoundTripAndFallBackWhenMissingOrCorrupt,
            UpdaterTests.ParsesGitHubReleaseAndComparesVersions,
            AecTests.CancelsDelayedEchoAndPreservesDoubleTalk,
            AecTests.PreservesNearEndWithoutReferenceAndRejectsInvalidFrames,
            AecTests.ResamplesIrregularPacketsAndRejectsAliasing,
            AecTests.PipelineHandlesJitterSilenceAndCaptureRestarts,
            AecTests.PipelineCancelsEchoAcrossRatesWithJitterAndClockDrift,
            AecTests.PipelineBypassRoutesRawMicrophoneAndKeepsAdapting,
            NoiseTests.FftMatchesDftAndInverts,
            NoiseTests.RnnoiseSuppressesNoiseAndKeepsSpeech,
            NoiseTests.DpdfnetSuppressesNoiseAndKeepsSpeech,
            NoiseTests.GtcrnSuppressesNoiseAndKeepsSpeech,
            NoiseTests.DeepFilterNet3SuppressesNoiseAndKeepsSpeech,
            NoiseTests.RejectsInvalidFramesAndSurvivesSilenceAndClipping,
            NoiseTests.PipelineSuppressesNoiseAfterAecAndSwitchesWhileRunning
        ];
        int failures = 0;
        foreach (var test in args.Contains("--wpf") ? tests.Append(WpfWindowLoads).Append(WpfWindowRestoresAndSavesSettings) : tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS {test.Method.Name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Method.Name}: {error}");
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static void SettingsRoundTripAndFallBackWhenMissingOrCorrupt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AecAudioRouterTest-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Equal(new AppSettings(), AppSettings.Load(path));
            var saved = new AppSettings
            {
                ReferenceMode = ReferenceMode.File, ReferenceDevice = "{ref}", ReferenceChannel = "2",
                PlaybackFile = @"C:udio	est.wav", Microphone = "{mic}", MicrophoneChannel = "1",
                Output = "{out}", AecEnabled = false, NoiseSuppression = NoiseSuppressionMode.HighQuality
            };
            saved.Save(path);
            Equal(saved, AppSettings.Load(path));
            File.WriteAllText(path, "{ not json");
            Equal(new AppSettings(), AppSettings.Load(path));
            File.WriteAllText(path, "{\"ReferenceMode\": 99}");
            Equal(ReferenceMode.Loopback, AppSettings.Load(path).ReferenceMode);
            File.WriteAllText(path, "{\"NoiseSuppression\": 99}");
            Equal(NoiseSuppressionMode.Off, AppSettings.Load(path).NoiseSuppression);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void WpfWindowLoads()
    {
        var window = new MainWindow(null);
        try
        {
            var reference = (ComboBox)window.FindName("ReferenceKind");
            Equal(3, reference.Items.Count);
            var cableDownload = (Button)window.FindName("CableDownloadButton");
            Equal(true, cableDownload.IsEnabled);
            var fileSettings = (DockPanel)window.FindName("FileSettings");
            Equal(Visibility.Collapsed, fileSettings.Visibility);
            reference.SelectedIndex = (int)ReferenceMode.File;
            Equal(Visibility.Visible, fileSettings.Visibility);
            var status = (TextBlock)window.FindName("Status");
            Equal(true, status.Text.StartsWith("デバイス  入力", StringComparison.Ordinal));
            Console.WriteLine(status.Text);
        }
        finally
        {
            window.Close();
        }
    }

    private static void WpfWindowRestoresAndSavesSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AecAudioRouterTest-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            new AppSettings
            {
                ReferenceMode = ReferenceMode.File, ReferenceChannel = "3", PlaybackFile = @"C:udio	est.wav",
                Microphone = "{missing-device}", MicrophoneChannel = "2", AecEnabled = false,
                NoiseSuppression = NoiseSuppressionMode.Light
            }.Save(path);
            var window = new MainWindow(path);
            try
            {
                Equal((int)ReferenceMode.File, ((ComboBox)window.FindName("ReferenceKind")).SelectedIndex);
                Equal("3", ((TextBox)window.FindName("ReferenceChannel")).Text);
                Equal("2", ((TextBox)window.FindName("MicChannel")).Text);
                Equal(@"C:udio	est.wav", ((TextBox)window.FindName("FilePath")).Text);
                Equal<bool?>(false, ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("AecToggle")).IsChecked);
                Equal<object?>(null, ((ComboBox)window.FindName("MicDevice")).SelectedValue);
                var noise = (ComboBox)window.FindName("NoiseMode");
                Equal((int)NoiseSuppressionMode.Light, noise.SelectedIndex);
                noise.SelectedIndex = (int)NoiseSuppressionMode.HighQuality;
                ((TextBox)window.FindName("MicChannel")).Text = "1";
            }
            finally
            {
                window.Close();
            }
            var saved = AppSettings.Load(path);
            Equal("1", saved.MicrophoneChannel);
            Equal(false, saved.AecEnabled);
            Equal(NoiseSuppressionMode.HighQuality, saved.NoiseSuppression);
            Equal(ReferenceMode.File, saved.ReferenceMode);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void StereoMeterKeepsBothChannelsAndRouteCanSelectOne()
    {
        var format = new WaveFormat(48000, 16, 2);
        var pcm = new PcmSamples(format);
        byte[] data = [0, 128, 0, 0, 0, 64, 0, 192];

        var both = pcm.Analyze(data).Level;
        Equal(1f, both.Peak);
        Near(Math.Sqrt(1.5 / 4), both.Rms);
        Equal(0.0, Level.ToDbfs(both.Peak));
        Near(-6.0206, Level.ToDbfs(0.5), 1e-4);

        float[] mono = new float[2];
        var right = pcm.Analyze(data, 2, mono).Level;
        Equal(true, mono.SequenceEqual([0f, -0.5f]));
        Equal(0.5f, right.Peak);

        pcm.Analyze(data, 0, mono);
        Equal(true, mono.SequenceEqual([-0.5f, 0f]));
    }

    private static void IntegerPcmDecodesNegativeFullScaleAndSilence()
    {
        (int Bits, byte[] Bytes)[] cases =
        [
            (8, [0, 128]),
            (16, [0, 128, 0, 0]),
            (24, [0, 0, 128, 0, 0, 0]),
            (32, [0, 0, 0, 128, 0, 0, 0, 0])
        ];
        float[] mono = new float[2];
        foreach (var (bits, bytes) in cases)
        {
            var decoder = new PcmSamples(new WaveFormat(48000, bits, 1));
            decoder.Analyze(bytes, 1, mono);
            Equal(true, mono.SequenceEqual([-1f, 0f]));
        }
    }

    private static void MeterResetsAndNonFiniteFloatBecomesSilence()
    {
        var meter = new Meter();
        var decoder = new PcmSamples(new WaveFormatExtensible(48000, 32, 1));
        Span<byte> sample = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(sample, float.NaN);
        meter.Add(decoder.Analyze(sample));
        BinaryPrimitives.WriteSingleLittleEndian(sample, 0.5f);
        meter.Add(decoder.Analyze(sample));
        var level = meter.ReadAndReset();
        Equal(0.5f, level.Peak);
        Near(Math.Sqrt(0.25 / 2), level.Rms);
        Equal(true, double.IsNegativeInfinity(Level.ToDbfs(meter.ReadAndReset().Rms)));
    }

    private static void BoundedRouteEvictsOldSamplesAndFillsUnderrunWithSilence()
    {
        var route = new RouteBuffer(48000, 4);
        float[] output = new float[6];
        route.Write([1, 2, 3]);
        route.Read(output, 0, 2);
        Equal(true, output[..2].SequenceEqual([1f, 2f]));

        route.Write([4, 5, 6, 7, 8]);
        Near(4.0 / 48, route.Status.QueuedMs);
        route.Read(output, 1, 5);
        Equal(true, output.SequenceEqual([1f, 5f, 6f, 7f, 8f, 0f]));
        Equal((0.0, 2L, 1L), route.Status);
    }

    private static void InAppPlaybackMeterReadsWavWithoutChangingTheBytes()
    {
        using var wave = new MemoryStream();
        var format = new WaveFormat(48000, 16, 1);
        byte[] bytes = [0, 64, 0, 192];
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(wave), format))
            writer.Write(bytes, 0, bytes.Length);
        wave.Position = 0;

        using var reader = new WaveFileReader(wave);
        var meter = new Meter();
        var provider = new MeteredWaveProvider(reader, meter);
        var output = new byte[8];
        Equal(4, provider.Read(output, 2, 6));
        Equal(true, bytes.SequenceEqual(output[2..6]));
        Equal(new Level(0.5f, 0.5), meter.ReadAndReset());
        Equal(0, provider.Read(output, 0, output.Length));
    }

    private static void ReferenceOutputCannotBeTheMicrophoneRouteDestination()
    {
        var settings = new RouteSettings(ReferenceMode.Loopback, "speaker", "test.wav", "mic", "speaker", 0, 0);
        ExpectInvalid(settings);
        ExpectInvalid(settings with { Mode = ReferenceMode.File });
        ExpectInvalid(settings with { Mode = ReferenceMode.File, Output = "cable", PlaybackFile = "" });
        ExpectInvalid(settings with { Mode = ReferenceMode.Input, ReferenceEndpoint = "mic" });
        ExpectInvalid(settings with { Mode = ReferenceMode.Input, ReferenceEndpoint = "mic", ReferenceChannel = 1, MicrophoneChannel = 1 });
        (settings with { Mode = ReferenceMode.Input, ReferenceEndpoint = "mic", ReferenceChannel = 1, MicrophoneChannel = 2 }).Validate();
        (settings with { Output = "cable" }).Validate();
    }

    private static void ExpectInvalid(RouteSettings settings)
    {
        try
        {
            settings.Validate();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException("Invalid route was accepted.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void Near(double expected, double actual, double tolerance = 1e-8)
    {
        if (!(Math.Abs(expected - actual) <= tolerance))
            throw new InvalidOperationException($"Expected {expected} ± {tolerance}, got {actual}.");
    }
}
