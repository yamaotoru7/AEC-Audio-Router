using System.Text.Json;

namespace AecAudioRouter.Tests;

internal static class UpdaterTests
{
    public static void ParsesGitHubReleaseAndComparesVersions()
    {
        const string json = """
            {
              "tag_name": "v0.2.10", "draft": false, "prerelease": false,
              "html_url": "https://github.com/yamaotoru7/AEC-Audio-Router/releases/tag/v0.2.10",
              "body": "AEC Audio Router 0.2.10。\r\n\r\n- **修正**：x\r\n",
              "assets": [
                { "name": "AecAudioRouter-0.2.10-setup.exe", "browser_download_url": "https://example.test/setup.exe" },
                { "name": "AecAudioRouter-0.2.10-setup.exe.sha256", "browser_download_url": "https://example.test/setup.exe.sha256" }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var release = Updater.Parse(doc.RootElement) ?? throw new Exception("release was not parsed");
        Check(release.Version == new Version(0, 2, 10), "version");
        Check(release.InstallerName == "AecAudioRouter-0.2.10-setup.exe", "installer name");
        Check(release.InstallerUrl == "https://example.test/setup.exe", "installer url");
        Check(release.ChecksumUrl == "https://example.test/setup.exe.sha256", "checksum url");
        Check(release.Notes == "AEC Audio Router 0.2.10。\n\n- **修正**：x", "notes");

        // Numeric, not string, comparison: 0.2.10 is newer than 0.2.9.
        Check(Updater.IsNewer(release, new Version(0, 2, 9)), "0.2.10 > 0.2.9");
        Check(!Updater.IsNewer(release, new Version(0, 2, 10)), "same version is not newer");
        Check(!Updater.IsNewer(release, new Version(1, 0, 0)), "older release is not newer");

        // Not installable: no .sha256, pre-release, unreadable tag.
        Check(Updater.Parse(Mutate(json, ".exe.sha256\"", ".exe.txt\"")) is null, "missing .sha256");
        Check(Updater.Parse(Mutate(json, "\"prerelease\": false", "\"prerelease\": true")) is null, "prerelease");
        Check(Updater.Parse(Mutate(json, "\"v0.2.10\"", "\"latest\"")) is null, "bad tag");

        var now = DateTimeOffset.Now;
        Check(Updater.IsDue(null, now), "never checked");
        Check(!Updater.IsDue(now.AddHours(-23), now), "checked within a day");
        Check(Updater.IsDue(now.AddHours(-25), now), "checked over a day ago");
    }

    private static JsonElement Mutate(string json, string from, string to) =>
        JsonDocument.Parse(json.Replace(from, to)).RootElement.Clone();

    private static void Check(bool condition, string what)
    {
        if (!condition)
            throw new Exception($"check failed: {what}");
    }
}
