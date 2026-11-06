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
    }

    private static void Check(bool condition, string what)
    {
        if (!condition)
            throw new Exception($"check failed: {what}");
    }
}
