using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace AecAudioRouter;

// A published release that the updater can install: its installer and the .sha256 written by build-installer.ps1.
internal sealed record ReleaseInfo(Version Version, string Tag, string Notes, string PageUrl,
    string InstallerName, string InstallerUrl, string ChecksumUrl);

// Checks the public GitHub releases (no sign-in) and installs a newer one with the Inno Setup installer.
internal static class Updater
{
    public const string Repository = "yamaotoru7/AEC-Audio-Router";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    // Inno Setup writes the uninstall entry as "<AppId>_is1" (AppId in installer/AecAudioRouter.iss).
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{84EEF39B-A016-4925-8D16-B80D564559E5}_is1";
    private const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    // The download gives up when no data arrives for this long (settable for tests).
    internal static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    // True only for the copy the installer put in place (not bin\Debug or a copied folder).
    public static bool IsInstalledCopy
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return key?.GetValue("InstallLocation") is string dir && string.Equals(
                    Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now) => lastCheck is not { } last || now - last >= Interval;

    // The latest release, or null when nothing installable is published yet.
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Parse(json.RootElement);
    }

    // GitHub's release JSON → ReleaseInfo. Drafts, pre-releases, unreadable tags and releases without the installer and its .sha256 are skipped.
    internal static ReleaseInfo? Parse(JsonElement release)
    {
        if (Bool(release, "draft") || Bool(release, "prerelease"))
            return null;
        string tag = Text(release, "tag_name");
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            return null;
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (release.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var asset in list.EnumerateArray())
                assets[Text(asset, "name")] = Text(asset, "browser_download_url");
        // The name becomes the file name in the download folder: it must not be a path.
        var installer = assets.Keys.FirstOrDefault(n => n.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)
            && n == Path.GetFileName(n) && n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
        if (installer is null || !assets.TryGetValue(installer + ".sha256", out var checksum))
            return null;
        return new ReleaseInfo(version, tag, Text(release, "body").Replace("\r\n", "\n").Trim(), Text(release, "html_url"),
            installer, assets[installer], checksum);
    }

    public static bool IsNewer(ReleaseInfo release, Version current) => release.Version > current;

    internal static string DownloadRoot => Path.Combine(Path.GetTempPath(), "AecAudioRouter-update");

    // Downloads the installer into a folder of its own under %TEMP% and checks it against
    // the published .sha256. On failure nothing is left behind; folders of earlier attempts
    // are removed after a day (the installer started from one may still be running sooner).
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double> progress, CancellationToken ct)
    {
        RemoveOldDownloads();
        // One folder per attempt: another instance updating at the same time cannot
        // overwrite this file between its check and its start.
        string directory = Path.Combine(DownloadRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            return await DownloadIntoAsync(release, Path.Combine(directory, release.InstallerName), progress, ct);
        }
        catch
        {
            TryDelete(directory);
            throw;
        }
    }

    private static async Task<string> DownloadIntoAsync(ReleaseInfo release, string path, IProgress<double> progress, CancellationToken ct)
    {
        string expected = (await Http.GetStringAsync(release.ChecksumUrl, ct)).Trim().Split((char[]?)null, 2)[0].ToLowerInvariant();
        if (expected.Length != 64)
            throw new InvalidDataException($"{release.InstallerName}.sha256 を読めませんでした。");

        // HttpClient.Timeout stops at the headers (ResponseHeadersRead), so a body that
        // stops arriving is caught by this timer, restarted whenever data comes in.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
            {
                stall.CancelAfter(StallTimeout);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                    progress.Report((double)done / total.Value);
            }
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"{release.InstallerName} のダウンロードが {StallTimeout.TotalSeconds:0} 秒間進みませんでした。", error);
        }

        string actual;
        await using (var file = File.OpenRead(path))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (actual != expected)
            throw new InvalidDataException("ダウンロードしたインストーラーのハッシュが一致しません。");
        return path;
    }

    private static void RemoveOldDownloads()
    {
        if (!Directory.Exists(DownloadRoot))
            return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        foreach (var entry in new DirectoryInfo(DownloadRoot).EnumerateFileSystemInfos())
        {
            // Files directly in the folder are from versions that saved there.
            if (entry is FileInfo || entry.LastWriteTimeUtc < cutoff)
                TryDelete(entry.FullName);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"更新用の一時ファイルを消せませんでした。 {path} {error.Message}");
        }
    }

    // Installs silently and relaunches the app afterwards (/RELAUNCH=1 in AecAudioRouter.iss). The caller closes the app.
    public static void StartInstaller(string installer)
    {
        string log = Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "update-setup.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        using var _ = Process.Start(new ProcessStartInfo(installer,
            $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1 /LOG=\"{log}\"") { UseShellExecute = false });
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AecAudioRouter", CurrentVersion.ToString()));
        return client;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
