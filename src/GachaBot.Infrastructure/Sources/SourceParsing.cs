using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using GachaBot.Domain.Content;
using GachaBot.Infrastructure.Links;

namespace GachaBot.Infrastructure.Sources;

internal static partial class SourceParsing
{
    internal static async Task<IReadOnlyList<ContentBlock>> HtmlToBlocksAsync(
        string html,
        Uri baseUri,
        string fallbackText,
        CancellationToken cancellationToken,
        Func<Uri, bool>? includeImage = null)
    {
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        foreach (var unwanted in document.QuerySelectorAll("script, style, noscript"))
        {
            unwanted.Remove();
        }

        var blocks = new List<ContentBlock>();
        var text = ExtractFormattedText(document.Body ?? document.DocumentElement);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = fallbackText;
        }

        foreach (var chunk in SplitText(text, 4_000))
        {
            blocks.Add(new TextBlock(chunk, blocks.Count + 1));
        }

        var position = blocks.Count + 1;
        var youtubeLinks = document.QuerySelectorAll("a[href], iframe[src]")
            .Select(element => new
            {
                Element = element,
                Candidate = element.GetAttribute(element.LocalName == "a" ? "href" : "src"),
            })
            .Select(item => new
            {
                item.Element,
                Resolved = TryResolveHttpUri(baseUri, item.Candidate, out var uri) ? uri : null,
            })
            .Where(item => item.Resolved is not null && YouTubeLink.TryCanonicalize(item.Resolved, out _))
            .Select(item => new
            {
                item.Element,
                Canonical = CanonicalYouTubeUri(item.Resolved!),
            })
            .DistinctBy(item => item.Canonical.AbsoluteUri, StringComparer.Ordinal)
            .Take(5);
        foreach (var video in youtubeLinks)
        {
            var label = NormalizeWhitespace(
                video.Element.GetAttribute("title") ?? video.Element.TextContent ?? string.Empty);
            blocks.Add(new LinkBlock(
                label.Length == 0 ? "YouTube video" : $"YouTube: {Truncate(label, 247)}",
                video.Canonical,
                position++));
        }

        foreach (var image in document.Images.Take(9))
        {
            var source = image.GetAttribute("src") ?? image.GetAttribute("data-src");
            if (!TryResolveHttpUri(baseUri, source, out var imageUri))
            {
                continue;
            }

            if (includeImage is not null && !includeImage(imageUri))
            {
                continue;
            }

            var alt = image.GetAttribute("alt");
            blocks.Add(new ImageBlock(
                imageUri,
                string.IsNullOrWhiteSpace(alt) ? fallbackText : Truncate(alt, 512),
                position++));
        }

        return blocks;
    }

    private static Uri CanonicalYouTubeUri(Uri candidate)
    {
        _ = YouTubeLink.TryCanonicalize(candidate, out var canonical);
        return canonical;
    }

    internal static bool TryResolveHttpUri(Uri baseUri, string? candidate, out Uri resolved)
    {
        if (!string.IsNullOrWhiteSpace(candidate) &&
            Uri.TryCreate(baseUri, candidate.Trim(), out var value) &&
            value.IsAbsoluteUri &&
            (value.Scheme == Uri.UriSchemeHttp || value.Scheme == Uri.UriSchemeHttps))
        {
            resolved = value;
            return true;
        }

        resolved = null!;
        return false;
    }

    internal static DateTimeOffset? ParseUtcPlusEight(string? value)
    {
        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
        {
            return null;
        }

        return new DateTimeOffset(local, TimeSpan.FromHours(8)).ToUniversalTime();
    }

    internal static ContentKind Classify(string title, string? category = null)
    {
        var value = $"{title} {category}";
        if (value.Contains("maintenance", StringComparison.OrdinalIgnoreCase))
        {
            return ContentKind.Maintenance;
        }

        // Articles about events are not events: only dated calendar entries may be ContentKind.Event,
        // otherwise they reach the events topic and bypass each guild's start/ending reminder settings.
        if (value.Contains("patch", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("update", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("version", StringComparison.OrdinalIgnoreCase))
        {
            return ContentKind.Update;
        }

        if (value.Contains("profile", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("resonator", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("character", StringComparison.OrdinalIgnoreCase))
        {
            return ContentKind.Character;
        }

        return ContentKind.News;
    }

    internal static string NormalizeWhitespace(string value) =>
        WhitespaceRegex().Replace(value, " ").Trim();

    private static string ExtractFormattedText(INode root)
    {
        var builder = new StringBuilder();
        AppendNode(root, builder);
        var lines = builder.ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(NormalizeWhitespace)
            .ToArray();
        var result = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.Length == 0 && (result.Count == 0 || result[^1].Length == 0))
            {
                continue;
            }

            result.Add(line);
        }

        return string.Join('\n', result).Trim();
    }

    private static void AppendNode(INode node, StringBuilder builder)
    {
        if (node is IText text)
        {
            builder.Append(text.Data);
            return;
        }

        if (node is not IElement element)
        {
            foreach (var child in node.ChildNodes)
            {
                AppendNode(child, builder);
            }

            return;
        }

        if (element.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine();
            return;
        }

        if (element.LocalName.Equals("tr", StringComparison.OrdinalIgnoreCase))
        {
            var cells = element.Children
                .Where(child => child.LocalName is "td" or "th")
                .Select(child => NormalizeWhitespace(child.TextContent))
                .Where(value => value.Length > 0);
            builder.AppendLine(string.Join(" | ", cells));
            return;
        }

        var isListItem = element.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase);
        if (isListItem)
        {
            EnsureLineStart(builder);
            builder.Append("• ");
        }

        foreach (var child in element.ChildNodes)
        {
            AppendNode(child, builder);
        }

        if (IsTextBoundary(element.LocalName))
        {
            EnsureLineEnd(builder);
        }
    }

    private static bool IsTextBoundary(string localName) => localName is
        "address" or "article" or "blockquote" or "div" or "dl" or "dt" or "dd" or
        "figcaption" or "footer" or "form" or "h1" or "h2" or "h3" or "h4" or
        "h5" or "h6" or "header" or "hr" or "li" or "main" or "nav" or "ol" or
        "p" or "pre" or "section" or "table" or "tbody" or "tfoot" or "thead" or "ul";

    private static void EnsureLineStart(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.AppendLine();
        }
    }

    private static void EnsureLineEnd(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.AppendLine();
        }
    }

    internal static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 1)] + "…";

    private static IEnumerable<string> SplitText(string value, int maximumLength)
    {
        var remaining = value;
        while (remaining.Length > maximumLength)
        {
            var boundary = remaining.LastIndexOf('\n', maximumLength - 1, maximumLength);
            if (boundary < maximumLength / 2)
            {
                boundary = remaining.LastIndexOf(' ', maximumLength - 1, maximumLength);
            }
            if (boundary < maximumLength / 2)
            {
                boundary = maximumLength;
            }

            yield return remaining[..boundary].TrimEnd();
            remaining = remaining[boundary..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
