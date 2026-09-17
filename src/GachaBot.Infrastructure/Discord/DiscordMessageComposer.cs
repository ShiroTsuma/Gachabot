using System.Text;
using GachaBot.Domain.Content;
using GachaBot.Infrastructure.Links;

namespace GachaBot.Infrastructure.Discord;

public static class DiscordMessageComposer
{
    private const int AccentColor = 0x5865F2;

    public static DiscordComposition Compose(string title, ContentDocument document, Uri? sourceUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(document);

        var descriptionUnits = new List<string>();
        var fields = new List<DiscordEmbedField>();
        var images = new List<DiscordImage>();
        var autoEmbedLinks = new List<Uri>();

        var blocks = document.Blocks.ToArray();
        for (var index = 0; index < blocks.Length; index++)
        {
            var block = blocks[index];
            switch (block)
            {
                case HeadingBlock heading:
                    descriptionUnits.Add($"**{EscapePlainText(heading.Text)}**");
                    break;
                case TextBlock textBlock:
                    descriptionUnits.Add(EscapePlainText(textBlock.Text));
                    break;
                case LinkBlock link when YouTubeLink.TryCanonicalize(link.Url, out var youtubeUri):
                    autoEmbedLinks.Add(youtubeUri);
                    break;
                case LinkBlock link:
                    descriptionUnits.Add($"[{EscapeLabel(link.Label)}]({link.Url.AbsoluteUri})");
                    break;
                case ImageBlock image:
                    images.Add(new DiscordImage(image.Url, image.AltText, image.Caption));
                    break;
                case GalleryBlock gallery:
                    images.AddRange(gallery.Images.Select(image =>
                        new DiscordImage(image.Url, image.AltText, image.Caption)));
                    break;
                case KeyValueBlock keyValue:
                    fields.AddRange(keyValue.Items.Select(item =>
                        new DiscordEmbedField(item.Key, item.Value, false)));
                    break;
                case CodeBlock code:
                    if (index + 1 < blocks.Length &&
                        blocks[index + 1] is KeyValueBlock codeDetails)
                    {
                        descriptionUnits.Add($"{FormatCode(code)}{Environment.NewLine}{Environment.NewLine}{FormatDetails(codeDetails)}");
                        index++;
                    }
                    else
                    {
                        descriptionUnits.Add(FormatCode(code));
                    }

                    break;
            }
        }

        var uniqueImages = images
            .DistinctBy(image => image.Url.AbsoluteUri, StringComparer.Ordinal)
            .ToArray();
        var descriptionParts = SplitUnits(descriptionUnits, DiscordEmbedLimits.Description);
        var embeds = BuildContentEmbeds(title.Trim(), sourceUrl, descriptionParts, fields, uniqueImages);
        var uniqueAutoEmbedLinks = autoEmbedLinks
            .DistinctBy(uri => uri.AbsoluteUri, StringComparer.Ordinal)
            .ToArray();
        var messages = PackMessages(embeds, uniqueAutoEmbedLinks);
        var text = BuildAccessibleText(title.Trim(), descriptionUnits, fields, uniqueAutoEmbedLinks, sourceUrl);
        return new DiscordComposition(text, messages);
    }

    private static List<DiscordRichEmbed> BuildContentEmbeds(
        string title,
        Uri? sourceUrl,
        List<string> descriptions,
        IReadOnlyList<DiscordEmbedField> fields,
        DiscordImage[] images)
    {
        var first = new DiscordRichEmbed(
            Truncate(title, DiscordEmbedLimits.Title),
            descriptions.Count == 0 ? null : descriptions[0],
            sourceUrl,
            AccentColor,
            [],
            images.Length == 0 ? null : images[0],
            images.Length == 0
                ? null
                : Truncate(images[0].Caption ?? images[0].AltText, DiscordEmbedLimits.Footer));
        var embeds = new List<DiscordRichEmbed> { first };

        foreach (var description in descriptions.Skip(1))
        {
            embeds.Add(new DiscordRichEmbed(null, description, null, AccentColor, [], null, null));
        }

        var targetIndex = 0;
        foreach (var field in fields)
        {
            while (targetIndex < embeds.Count && !CanAddField(embeds[targetIndex], field))
            {
                targetIndex++;
            }

            if (targetIndex == embeds.Count)
            {
                embeds.Add(new DiscordRichEmbed(null, null, null, AccentColor, [], null, null));
            }

            var target = embeds[targetIndex];
            embeds[targetIndex] = target with { Fields = [.. target.Fields, field] };
        }

        foreach (var image in images.Skip(1))
        {
            embeds.Add(new DiscordRichEmbed(
                null,
                null,
                null,
                AccentColor,
                [],
                image,
                Truncate(image.Caption ?? image.AltText, DiscordEmbedLimits.Footer)));
        }

        return embeds;
    }

    private static bool CanAddField(DiscordRichEmbed embed, DiscordEmbedField field) =>
        embed.Fields.Count < DiscordEmbedLimits.Fields &&
        embed.CharacterCount + field.Name.Length + field.Value.Length <= DiscordEmbedLimits.TotalCharacters;

    private static List<DiscordOutboundMessage> PackMessages(
        IReadOnlyList<DiscordRichEmbed> embeds,
        IReadOnlyList<Uri> autoEmbedLinks)
    {
        var messages = new List<DiscordOutboundMessage>();
        var current = new List<DiscordRichEmbed>();
        var currentCharacters = 0;

        foreach (var embed in embeds)
        {
            if (current.Count > 0 &&
                (current.Count == DiscordEmbedLimits.EmbedsPerMessage ||
                 currentCharacters + embed.CharacterCount > DiscordEmbedLimits.TotalCharacters))
            {
                messages.Add(new DiscordOutboundMessage(null, current.ToArray()));
                current = [];
                currentCharacters = 0;
            }

            current.Add(embed);
            currentCharacters += embed.CharacterCount;
        }

        if (current.Count > 0)
        {
            messages.Add(new DiscordOutboundMessage(null, current.ToArray()));
        }

        var contentParts = SplitUnits(
            autoEmbedLinks.Select(link => link.AbsoluteUri),
            DiscordEmbedLimits.Content);
        for (var index = 0; index < contentParts.Count; index++)
        {
            if (index < messages.Count)
            {
                messages[index] = messages[index] with { Content = contentParts[index] };
            }
            else
            {
                messages.Add(new DiscordOutboundMessage(contentParts[index], []));
            }
        }

        return messages;
    }

    private static List<string> SplitUnits(IEnumerable<string> units, int limit)
    {
        var parts = new List<string>();
        var builder = new StringBuilder();

        foreach (var unit in units.SelectMany(unit => SplitText(unit, limit)))
        {
            var separatorLength = builder.Length == 0 ? 0 : 2;
            if (builder.Length + separatorLength + unit.Length > limit)
            {
                parts.Add(builder.ToString());
                builder.Clear();
            }

            if (builder.Length > 0)
            {
                builder.AppendLine().AppendLine();
            }

            builder.Append(unit);
        }

        if (builder.Length > 0)
        {
            parts.Add(builder.ToString());
        }

        return parts;
    }

    private static IEnumerable<string> SplitText(string text, int limit)
    {
        var remaining = text;
        while (remaining.Length > limit)
        {
            var boundary = remaining.LastIndexOf('\n', limit - 1, limit);
            if (boundary < limit / 2)
            {
                boundary = remaining.LastIndexOf(' ', limit - 1, limit);
            }

            if (boundary < limit / 2)
            {
                boundary = limit;
            }

            while (boundary > 0 && remaining[boundary - 1] == '\\')
            {
                boundary--;
            }

            yield return remaining[..boundary].TrimEnd();
            remaining = remaining[boundary..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }

    private static string BuildAccessibleText(
        string title,
        IEnumerable<string> descriptions,
        IEnumerable<DiscordEmbedField> fields,
        IEnumerable<Uri> autoEmbedLinks,
        Uri? sourceUrl)
    {
        var builder = new StringBuilder().AppendLine(title);
        foreach (var description in descriptions)
        {
            builder.AppendLine().AppendLine(description);
        }

        foreach (var field in fields)
        {
            builder.AppendLine().Append(field.Name).Append(": ").Append(field.Value);
        }

        foreach (var link in autoEmbedLinks)
        {
            builder.AppendLine().Append(link.AbsoluteUri);
        }

        if (sourceUrl is not null)
        {
            builder.AppendLine().Append(sourceUrl.AbsoluteUri);
        }

        return builder.ToString().Trim();
    }

    private static string FormatCode(CodeBlock code)
    {
        var safeCode = code.Code.Replace("```", "``\u200B`", StringComparison.Ordinal);
        return $"```{code.Language}{Environment.NewLine}{safeCode}{Environment.NewLine}```";
    }

    private static string FormatDetails(KeyValueBlock details) => string.Join(
        Environment.NewLine,
        details.Items.Select(item => $"**{EscapePlainText(item.Key)}:** {EscapePlainText(item.Value)}"));

    private static string EscapeLabel(string value) => value.Replace("]", "\\]", StringComparison.Ordinal);

    private static string EscapePlainText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\\' or '*' or '_' or '~' or '`' or '[' or ']')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string Truncate(string value, int limit) => value.Length <= limit ? value : value[..limit];
}

public static class DiscordEmbedLimits
{
    public const int Content = 2_000;
    public const int EmbedsPerMessage = 10;
    public const int TotalCharacters = 6_000;
    public const int Title = 256;
    public const int Description = 4_096;
    public const int Fields = 25;
    public const int FieldName = 256;
    public const int FieldValue = 1_024;
    public const int Footer = 2_048;
    public const int Author = 256;
}

public sealed record DiscordComposition(
    string Text,
    IReadOnlyList<DiscordOutboundMessage> Messages);

public sealed record DiscordOutboundMessage(
    string? Content,
    IReadOnlyList<DiscordRichEmbed> Embeds);

public sealed record DiscordRichEmbed(
    string? Title,
    string? Description,
    Uri? Url,
    int Color,
    IReadOnlyList<DiscordEmbedField> Fields,
    DiscordImage? Image,
    string? Footer)
{
    public int CharacterCount =>
        (Title?.Length ?? 0) +
        (Description?.Length ?? 0) +
        Fields.Sum(item => item.Name.Length + item.Value.Length) +
        (Footer?.Length ?? 0);
}

public sealed record DiscordEmbedField(string Name, string Value, bool Inline);

public sealed record DiscordImage(
    Uri Url,
    string AltText,
    string? Caption,
    string? AttachmentFileName = null);
