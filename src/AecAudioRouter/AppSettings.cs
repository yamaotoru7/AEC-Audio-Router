using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AecAudioRouter;

// User choices restored on the next launch. Device fields hold Windows endpoint IDs.
internal sealed record AppSettings
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AecAudioRouter", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public ReferenceMode ReferenceMode { get; init; } = ReferenceMode.Loopback;
    public string? ReferenceDevice { get; init; }
    public string ReferenceChannel { get; init; } = "0";
    public string? PlaybackFile { get; init; }
    public string? Microphone { get; init; }
    public string MicrophoneChannel { get; init; } = "0";
    public string? Output { get; init; }
    public bool AecEnabled { get; init; } = true;
    public NoiseSuppressionMode NoiseSuppression { get; init; }

    // Startup check of the GitHub releases (once per Updater.Interval, installed copy only).
    public bool CheckForUpdates { get; init; } = true;
    public DateTimeOffset? LastUpdateCheck { get; init; }

    // A missing or unreadable file falls back to defaults rather than blocking startup.
    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new AppSettings();
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
            if (!Enum.IsDefined(loaded.ReferenceMode))
                loaded = loaded with { ReferenceMode = ReferenceMode.Loopback };
            if (!Enum.IsDefined(loaded.NoiseSuppression))
                loaded = loaded with { NoiseSuppression = NoiseSuppressionMode.Off };
            return loaded;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Trace.TraceWarning($"設定を読み込めませんでした。初期値で起動します。 {error.Message}");
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write then replace, so an interrupted save cannot leave a truncated file.
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"設定を保存できませんでした。 {error.Message}");
        }
    }
}
