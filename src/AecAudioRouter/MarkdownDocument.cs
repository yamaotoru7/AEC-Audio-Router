using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AecAudioRouter;

// Renders the Markdown of GitHub release notes as a FlowDocument: headings, bullet and
// numbered lists (nested by indentation), paragraphs, fenced code, horizontal rules, and
// inline **bold**, *italic*, `code` and [links](https://...). Anything else stays as text.
internal static partial class MarkdownDocument
{
    private static readonly FontFamily Monospace = new("Cascadia Mono, Consolas");

    public static FlowDocument Build(string markdown, Brush accent, Brush codeBackground)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // Open lists by nesting depth, each with the indentation of its markers.
        var lists = new List<(List List, int Indent)>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var block = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AddInlines(block.Inlines, string.Join(" ", paragraph), accent, codeBackground);
            document.Blocks.Add(block);
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.TrimStart().StartsWith("```"))
            {
                FlushParagraph();
                lists.Clear();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```"); i++) code.Add(lines[i].TrimEnd());
                document.Blocks.Add(new Paragraph(new Run(string.Join("\n", code)))
                {
                    FontFamily = Monospace, FontSize = 12, Background = codeBackground,
                    Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 8)
                });
                continue;
            }
            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }
            if (HeadingPattern().Match(line) is { Success: true } heading)
            {
                FlushParagraph();
                lists.Clear();
                int level = heading.Groups[1].Length;
                var block = new Paragraph
                {
                    FontWeight = FontWeights.SemiBold,
                    FontSize = level switch { 1 => 19, 2 => 17, _ => 15 },
                    Margin = new Thickness(0, document.Blocks.Count == 0 ? 0 : 8, 0, 6)
                };
                AddInlines(block.Inlines, heading.Groups[2].Value, accent, codeBackground);
                document.Blocks.Add(block);
                continue;
            }
            if (RulePattern().IsMatch(line))
            {
                FlushParagraph();
                lists.Clear();
                document.Blocks.Add(new BlockUIContainer(new System.Windows.Controls.Separator { Margin = new Thickness(0, 4, 0, 8) }));
                continue;
            }
            if (ItemPattern().Match(line) is { Success: true } item)
            {
                FlushParagraph();
                int indent = item.Groups[1].Length;
                bool numbered = char.IsDigit(item.Groups[2].Value[0]);
                while (lists.Count > 0 && indent < lists[^1].Indent) lists.RemoveAt(lists.Count - 1);
                if (lists.Count == 0 || indent > lists[^1].Indent)
                {
                    var list = new List
                    {
                        MarkerStyle = numbered ? TextMarkerStyle.Decimal : lists.Count == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle,
                        Margin = new Thickness(0, 0, 0, lists.Count == 0 ? 8 : 0),
                        Padding = new Thickness(20, 0, 0, 0)
                    };
                    if (lists.Count == 0) document.Blocks.Add(list);
                    else lists[^1].List.ListItems.LastListItem.Blocks.Add(list);
                    lists.Add((list, indent));
                }
                var text = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
                AddInlines(text.Inlines, item.Groups[3].Value, accent, codeBackground);
                lists[^1].List.ListItems.Add(new ListItem(text));
                continue;
            }
            // A continuation line of a list item (indented, not a new marker).
            if (lists.Count > 0 && paragraph.Count == 0 && char.IsWhiteSpace(lines[i][0]))
            {
                var last = (Paragraph)lists[^1].List.ListItems.LastListItem.Blocks.FirstBlock;
                last.Inlines.Add(new Run(" "));
                AddInlines(last.Inlines, line.Trim(), accent, codeBackground);
                continue;
            }
            lists.Clear();
            paragraph.Add(line.Trim());
        }
        FlushParagraph();
        return document;
    }

    // Inline markup, left to right; unmatched markers stay as literal text.
    private static void AddInlines(InlineCollection target, string text, Brush accent, Brush codeBackground)
    {
        int position = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            if (match.Index > position) target.Add(new Run(text[position..match.Index]));
            if (match.Groups["code"].Success)
                target.Add(new Run(match.Groups["code"].Value) { FontFamily = Monospace, Background = codeBackground });
            else if (match.Groups["bold"].Success)
            {
                var bold = new Bold();
                AddInlines(bold.Inlines, match.Groups["bold"].Value, accent, codeBackground);
                target.Add(bold);
            }
            else if (match.Groups["italic"].Success)
            {
                var italic = new Italic();
                AddInlines(italic.Inlines, match.Groups["italic"].Value, accent, codeBackground);
                target.Add(italic);
            }
            else
            {
                var link = new Hyperlink { NavigateUri = new Uri(match.Groups["url"].Value), Foreground = accent };
                AddInlines(link.Inlines, match.Groups["label"].Value, accent, codeBackground);
                link.RequestNavigate += (_, e) => OpenLink(e.Uri);
                target.Add(link);
            }
            position = match.Index + match.Length;
        }
        if (position < text.Length) target.Add(new Run(text[position..]));
    }

    private static void OpenLink(Uri uri)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Trace.TraceError(error.ToString());
        }
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ItemPattern();

    // Only http(s) links become hyperlinks; other targets stay as text.
    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>.+?)\*\*|__(?<bold>.+?)__|(?<![\w*])\*(?<italic>[^*\s][^*]*?)\*(?![\w*])|(?<![\w_])_(?<italic>[^_\s][^_]*?)_(?![\w_])|\[(?<label>[^\]]+)\]\((?<url>https?://[^)\s]+)\)")]
    private static partial Regex InlinePattern();
}
