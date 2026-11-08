using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

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
                { "name": "AecAudioRouter-0.2.10-setup.exe", "browser_download_url": "https://example.test/setup.exe",
                  "url": "https://api.example.test/assets/1" },
                { "name": "AecAudioRouter-0.2.10-setup.exe.sha256", "browser_download_url": "https://example.test/setup.exe.sha256",
                  "url": "https://api.example.test/assets/2" }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var release = Updater.Parse(doc.RootElement) ?? throw new Exception("release was not parsed");
        Check(release.Version == new Version(0, 2, 10), "version");
        Check(release.InstallerName == "AecAudioRouter-0.2.10-setup.exe", "installer name");
        Check(release.InstallerUrl == "https://example.test/setup.exe", "installer url");
        Check(release.ChecksumUrl == "https://example.test/setup.exe.sha256", "checksum url");
        Check(release.InstallerApiUrl == "https://api.example.test/assets/1", "installer API url");
        Check(release.ChecksumApiUrl == "https://api.example.test/assets/2", "checksum API url");
        Check(release.Notes == "AEC Audio Router 0.2.10。\n\n- **修正**：x", "notes");

        // Numeric, not string, comparison: 0.2.10 is newer than 0.2.9.
        Check(Updater.IsNewer(release, new Version(0, 2, 9)), "0.2.10 > 0.2.9");
        Check(!Updater.IsNewer(release, new Version(0, 2, 10)), "same version is not newer");
        Check(!Updater.IsNewer(release, new Version(1, 0, 0)), "older release is not newer");

        // Not installable: no .sha256, pre-release, unreadable tag.
        Check(Updater.Parse(Mutate(json, ".exe.sha256\"", ".exe.txt\"")) is null, "missing .sha256");
        Check(Updater.Parse(Mutate(json, "\"prerelease\": false", "\"prerelease\": true")) is null, "prerelease");
        Check(Updater.Parse(Mutate(json, "\"v0.2.10\"", "\"latest\"")) is null, "bad tag");
        // The asset name becomes a file name in the download folder.
        Check(Updater.Parse(Mutate(json, "\"AecAudioRouter-0.2.10-setup.exe", "\"..\\\\..\\\\AecAudioRouter-0.2.10-setup.exe")) is null, "path in asset name");

        var now = DateTimeOffset.Now;
        Check(Updater.IsDue(null, now), "never checked");
        Check(!Updater.IsDue(now.AddHours(-23), now), "checked within a day");
        Check(Updater.IsDue(now.AddHours(-25), now), "checked over a day ago");
    }

    private static JsonElement Mutate(string json, string from, string to) =>
        JsonDocument.Parse(json.Replace(from, to)).RootElement.Clone();

    // Release notes are Markdown: lists nest, inline markup becomes formatting, links are clickable.
    public static void RendersReleaseNotesMarkdown()
    {
        const string notes = "AEC Audio Router 0.2.0。\r\n\r\n## 変更\r\n\r\n- **ノイズ除去**：`Alt+N` で選ぶ。\r\n  - 軽量（*RNNoise*）\r\n  - 高品質\r\n- [リリース](https://example.com/r) を見る\r\n\r\n---\r\n\r\n```\r\ncode **raw**\r\n```";
        var document = MarkdownDocument.Build(notes, Brushes.Teal, Brushes.Gray);
        var blocks = document.Blocks.ToList();
        // The runs' own text (TextRange.Text would also include list markers).
        static string Text(TextElement element) => element switch
        {
            Run run => run.Text,
            Span span => string.Concat(span.Inlines.Select(Text)),
            Paragraph paragraph => string.Concat(paragraph.Inlines.Select(Text)),
            _ => ""
        };
        Check(blocks.Count == 5, $"5 blocks, got {blocks.Count}");
        Check(blocks[0] is Paragraph first && Text(first) == "AEC Audio Router 0.2.0。", "paragraph");
        Check(blocks[1] is Paragraph heading && heading.FontWeight == FontWeights.SemiBold && Text(heading) == "変更", "heading");
        var list = (List)blocks[2];
        Check(list.ListItems.Count == 2, "top-level items");
        var item = list.ListItems.FirstListItem;
        var text = (Paragraph)item.Blocks.FirstBlock;
        Check(text.Inlines.FirstInline is Bold bold && Text(bold) == "ノイズ除去", "bold");
        Check(text.Inlines.OfType<Run>().Any(r => r.Text == "Alt+N" && r.Background == Brushes.Gray), "inline code");
        Check(item.Blocks.LastBlock is List nested && nested.ListItems.Count == 2, "nested list");
        var nestedText = (Paragraph)((List)item.Blocks.LastBlock).ListItems.FirstListItem.Blocks.FirstBlock;
        Check(nestedText.Inlines.OfType<Italic>().Any(), "italic");
        var link = ((Paragraph)list.ListItems.LastListItem.Blocks.FirstBlock).Inlines.OfType<Hyperlink>().Single();
        Check(link.NavigateUri == new Uri("https://example.com/r"), "link");
        Check(blocks[3] is BlockUIContainer, "rule");
        Check(blocks[4] is Paragraph code && Text(code) == "code **raw**", "code block keeps markup");

        // A link that only looks like a URL must not stop the update dialog from opening.
        var broken = (Paragraph)MarkdownDocument.Build("[a](https://[) と [b](http://host:99999/)", Brushes.Teal, Brushes.Gray).Blocks.Single();
        Check(!broken.Inlines.OfType<Hyperlink>().Any() && Text(broken) == "[a](https://[) と [b](http://host:99999/)", "malformed links stay text");
    }

    // Against a local HTTP server: a complete download, a body that stops arriving, and a wrong checksum.
    public static void DownloadsVerifiesAndGivesUpOnAStalledBody()
    {
        var installer = new byte[200_000];
        new Random(8).NextBytes(installer);
        string hash = Convert.ToHexStringLower(SHA256.HashData(installer));
        byte[] checksum = Encoding.ASCII.GetBytes(hash + "  installer\n");
        // Like GitHub's API, the asset URLs under /api/ return metadata unless the file is asked for.
        byte[] metadata = Encoding.ASCII.GetBytes("{\"name\": \"asset\"}");
        using var server = new TestHttpServer((path, accept) => path switch
        {
            "/ok-setup.exe" or "/bad-setup.exe" => (200, installer, false),
            "/stall-setup.exe" => (200, installer, true),
            "/bad-setup.exe.sha256" => (200, Encoding.ASCII.GetBytes(new string('0', 64) + "  bad-setup.exe\n"), false),
            // The github.com download URL failing as it did for anonymous clients.
            "/gone-setup.exe" or "/gone-setup.exe.sha256" => (504, Encoding.ASCII.GetBytes("504 Gateway Time-out"), false),
            "/api/gone-setup.exe" => (200, accept == "application/octet-stream" ? installer : metadata, false),
            "/api/gone-setup.exe.sha256" => (200, accept == "application/octet-stream" ? checksum : metadata, false),
            _ => (200, checksum, false)
        });
        ReleaseInfo Release(string name) => new(new Version(9, 9, 9), "v9.9.9", "", "", name,
            server.Url + name, server.Url + name + ".sha256", server.Url + "api/" + name, server.Url + "api/" + name + ".sha256");
        var progress = new Progress<double>(_ => { });
        var previous = Updater.StallTimeout;
        Updater.StallTimeout = TimeSpan.FromSeconds(1);
        var paths = new List<string>();
        var before = Directory.Exists(Updater.DownloadRoot) ? Directory.GetDirectories(Updater.DownloadRoot).ToHashSet() : [];
        try
        {
            string path = Updater.DownloadAsync(Release("ok-setup.exe"), progress, CancellationToken.None).GetAwaiter().GetResult();
            paths.Add(path);
            Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(installer), "downloaded installer");

            // The download URL answers 504: both files come through the API instead.
            string fallback = Updater.DownloadAsync(Release("gone-setup.exe"), progress, CancellationToken.None).GetAwaiter().GetResult();
            paths.Add(fallback);
            Check(File.ReadAllBytes(fallback).AsSpan().SequenceEqual(installer), "installer through the API");

            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { Updater.DownloadAsync(Release("stall-setup.exe"), progress, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("stalled download returned"); }
            catch (IOException) { }
            Check(watch.Elapsed < TimeSpan.FromSeconds(10), $"stalled download gave up after {watch.Elapsed.TotalSeconds:F1} s");

            try { Updater.DownloadAsync(Release("bad-setup.exe"), progress, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("wrong checksum accepted"); }
            catch (InvalidDataException) { }

            // Failed downloads leave nothing behind; the good one stays in a folder of its own.
            var added = Directory.GetDirectories(Updater.DownloadRoot).Where(d => !before.Contains(d)).ToArray();
            Check(added.Length == 2 && added.Contains(Path.GetDirectoryName(path)) && added.Contains(Path.GetDirectoryName(fallback)),
                $"leftover download folders: {added.Length}");
        }
        finally
        {
            Updater.StallTimeout = previous;
            foreach (string path in paths) Directory.Delete(Path.GetDirectoryName(path)!, true);
        }
    }

    // Serves each request with Connection: close. A stalled response sends the headers and
    // a tenth of the body, then holds the connection open.
    private sealed class TestHttpServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();

        // respond gets the path and the Accept header.
        public TestHttpServer(Func<string, string, (int Status, byte[] Body, bool Stall)> respond)
        {
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (SocketException) { return; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                var stream = client.GetStream();
                                var reader = new StreamReader(stream, Encoding.ASCII);
                                string path = (await reader.ReadLineAsync())?.Split(' ')[1] ?? "/";
                                string accept = "";
                                for (string? line; !string.IsNullOrEmpty(line = await reader.ReadLineAsync());)
                                    if (line.StartsWith("Accept:", StringComparison.OrdinalIgnoreCase))
                                        accept = line["Accept:".Length..].Trim();
                                var (status, body, stall) = respond(path, accept);
                                byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                                await stream.WriteAsync(head);
                                await stream.WriteAsync(stall ? body.AsMemory(0, body.Length / 10) : body);
                                if (stall) await Task.Delay(Timeout.Infinite, stop.Token);
                            }
                            catch (Exception error) when (error is IOException or OperationCanceledException) { }
                        }
                    });
                }
            });
        }

        public string Url { get; }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }

    private static void Check(bool condition, string what)
    {
        if (!condition)
            throw new Exception($"check failed: {what}");
    }
}
