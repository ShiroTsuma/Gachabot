using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using GachaBot.Application.Ingestion;
using GachaBot.Domain.Content;

namespace GachaBot.Infrastructure.Sources;

/// <summary>
/// Imports Game8's dated NTE event tables. Permanent and historical sections are
/// excluded through the source definition; Game8 supplies calendar dates only.
/// </summary>
public sealed partial class Game8EventCalendarHandler(IRenderedPageClient pageClient) : ISourceHandler
{
    public const string HandlerKey = "game8-event-calendar";

    public string Key => HandlerKey;

    public async IAsyncEnumerable<SourceContentSnapshot> FetchAsync(
        SourceDefinition definition,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rules = definition.EventCalendar
            ?? throw new InvalidOperationException($"Source '{definition.Key}' has no EventCalendar rules.");
        var html = await pageClient.GetContentAsync(
            new RenderedPageRequest(
                definition.Url,
                rules.ReadySelector,
                TimeSpan.FromSeconds(rules.NavigationTimeoutSeconds),
                TimeSpan.FromSeconds(rules.ReadyTimeoutSeconds)),
            cancellationToken).ConfigureAwait(false);
        var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        if (document.QuerySelector(rules.ReadySelector) is null)
        {
            throw new InvalidOperationException(
                $"Rendered page '{definition.Url}' did not contain ready selector '{rules.ReadySelector}'.");
        }

        var candidates = FindCandidates(document, definition, rules);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Event calendar '{definition.Url}' contained no dated current or upcoming events.");
        }

        foreach (var candidate in candidates
                     .DistinctBy(item => item.ExternalId, StringComparer.Ordinal)
                     .OrderBy(item => item.StartAtUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return CreateSnapshot(candidate, definition);
        }
    }

    private static List<EventCandidate> FindCandidates(
        IDocument document,
        SourceDefinition definition,
        EventCalendarRules rules)
    {
        var result = new List<EventCandidate>();
        var elements = document.All.ToArray();
        for (var headingIndex = 0; headingIndex < elements.Length; headingIndex++)
        {
            var heading = elements[headingIndex];
            if (!heading.Matches(rules.SectionHeadingSelector))
            {
                continue;
            }

            var headingText = SourceParsing.NormalizeWhitespace(heading.TextContent);
            if (!rules.IncludedSectionHeadings.Any(value =>
                    headingText.Contains(value, StringComparison.OrdinalIgnoreCase)) ||
                rules.ExcludedSectionHeadings.Any(value =>
                    headingText.Contains(value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            for (var index = headingIndex + 1; index < elements.Length; index++)
            {
                var element = elements[index];
                if (element.Matches(rules.SectionHeadingSelector))
                {
                    break;
                }

                if (!element.Matches(rules.RowSelector) ||
                    !TryReadCandidate(element, definition, rules, out var candidate))
                {
                    continue;
                }

                result.Add(candidate);
            }
        }

        return result;
    }

    private static bool TryReadCandidate(
        IElement row,
        SourceDefinition definition,
        EventCalendarRules rules,
        out EventCandidate candidate)
    {
        candidate = default!;
        var titleElement = row.QuerySelector(rules.TitleSelector);
        var title = SourceParsing.NormalizeWhitespace(titleElement?.TextContent ?? string.Empty);
        var duration = SourceParsing.NormalizeWhitespace(
            row.QuerySelector(rules.DurationSelector)?.TextContent ?? string.Empty);
        if (string.IsNullOrWhiteSpace(title) ||
            !TryParseDuration(
                duration,
                rules.DateUtcOffsetHours,
                rules.DayStartHour,
                out var startAtUtc,
                out var endAtUtc))
        {
            return false;
        }

        var detailsUrl = SourceParsing.TryResolveHttpUri(
            definition.Url,
            titleElement?.GetAttribute("href"),
            out var resolvedDetailsUrl)
            ? resolvedDetailsUrl
            : definition.Url;
        var description = rules.DetailsSelector is null
            ? string.Empty
            : SourceParsing.NormalizeWhitespace(row.QuerySelector(rules.DetailsSelector)?.TextContent ?? string.Empty);
        var rewards = rules.RewardsSelector is null
            ? string.Empty
            : SourceParsing.NormalizeWhitespace(row.QuerySelector(rules.RewardsSelector)?.TextContent ?? string.Empty);
        Uri? imageUrl = null;
        if (rules.ImageSelector is not null)
        {
            var image = row.QuerySelector(rules.ImageSelector);
            if (SourceParsing.TryResolveHttpUri(
                    definition.Url,
                    image?.GetAttribute("data-src") ?? image?.GetAttribute("src"),
                    out var resolvedImageUrl) &&
                resolvedImageUrl.Scheme == Uri.UriSchemeHttps)
            {
                imageUrl = resolvedImageUrl;
            }
        }

        // Recurring events reuse one Game8 page, so the start date keeps each edition separate.
        var externalId = detailsUrl != definition.Url
            ? $"{detailsUrl.AbsolutePath.Trim('/').Replace('/', ':')}:{startAtUtc:yyyyMMdd}"
            : $"{Slug(title)}:{startAtUtc:yyyyMMdd}";
        candidate = new EventCandidate(
            externalId,
            title,
            detailsUrl,
            description,
            rewards,
            imageUrl,
            startAtUtc,
            endAtUtc);
        return true;
    }

    private static SourceContentSnapshot CreateSnapshot(EventCandidate candidate, SourceDefinition definition)
    {
        var blocks = new List<ContentBlock>
        {
            new HeadingBlock(candidate.Title, 1),
        };
        if (!string.IsNullOrWhiteSpace(candidate.Description))
        {
            blocks.Add(new TextBlock(candidate.Description, blocks.Count + 1));
        }

        if (candidate.ImageUrl is not null)
        {
            blocks.Add(new ImageBlock(
                candidate.ImageUrl,
                $"{candidate.Title} event artwork",
                blocks.Count + 1,
                "Artwork: Game8"));
        }

        var details = new List<KeyValueItem>
        {
            new("Start", DiscordTimestampFormatter.Format(candidate.StartAtUtc)),
            new("End", DiscordTimestampFormatter.Format(candidate.EndAtUtc.AddTicks(-1))),
        };
        if (!string.IsNullOrWhiteSpace(candidate.Rewards))
        {
            details.Add(new KeyValueItem("Rewards", candidate.Rewards));
        }

        blocks.Add(new KeyValueBlock(details, blocks.Count + 1));
        blocks.Add(new TextBlock("Game8 provides event dates without hours; this source schedules them at the configured NTE server-day start.", blocks.Count + 1));
        blocks.Add(new LinkBlock("Schedule: Game8", definition.Url, blocks.Count + 1));
        return new SourceContentSnapshot(
            definition.Key,
            candidate.ExternalId,
            definition.Game,
            ContentKind.Event,
            candidate.Title,
            candidate.DetailsUrl,
            ContentDocument.Create(blocks),
            candidate.StartAtUtc,
            candidate.EndAtUtc,
            PublishAtUtc: candidate.StartAtUtc);
    }

    private static bool TryParseDuration(
        string value,
        double utcOffsetHours,
        int dayStartHour,
        out DateTimeOffset startAtUtc,
        out DateTimeOffset endAtUtc)
    {
        startAtUtc = default;
        endAtUtc = default;
        var match = DurationPattern().Match(value);
        if (!match.Success ||
            !int.TryParse(match.Groups["endYear"].Value, CultureInfo.InvariantCulture, out var endYear))
        {
            return false;
        }

        var startYear = match.Groups["startYear"].Success &&
            int.TryParse(match.Groups["startYear"].Value, CultureInfo.InvariantCulture, out var explicitStartYear)
            ? explicitStartYear
            : endYear;
        if (!DateOnly.TryParseExact(
                $"{match.Groups["startMonth"].Value} {match.Groups["startDay"].Value}, {startYear}",
                "MMMM d, yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var startDate) ||
            !DateOnly.TryParseExact(
                $"{match.Groups["endMonth"].Value} {match.Groups["endDay"].Value}, {endYear}",
                "MMMM d, yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var endDate))
        {
            return false;
        }

        // "December 28 - January 11, 2027" only states the end year; the start is in the year before.
        if (!match.Groups["startYear"].Success && startDate > endDate)
        {
            startDate = startDate.AddYears(-1);
        }

        var offset = TimeSpan.FromHours(utcOffsetHours);
        var dayStart = new TimeOnly(dayStartHour, 0);
        startAtUtc = new DateTimeOffset(startDate.ToDateTime(dayStart), offset).ToUniversalTime();
        endAtUtc = new DateTimeOffset(endDate.AddDays(1).ToDateTime(dayStart), offset).ToUniversalTime();
        return endAtUtc > startAtUtc;
    }

    private static string Slug(string value)
    {
        var normalized = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(normalized) ? "event" : normalized;
    }

    [GeneratedRegex(
        "^(?<startMonth>[A-Za-z]+)\\s+(?<startDay>\\d{1,2})(?:,\\s*(?<startYear>\\d{4}))?\\s*-\\s*(?<endMonth>[A-Za-z]+)\\s+(?<endDay>\\d{1,2}),\\s*(?<endYear>\\d{4})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();

    private sealed record EventCandidate(
        string ExternalId,
        string Title,
        Uri DetailsUrl,
        string Description,
        string Rewards,
        Uri? ImageUrl,
        DateTimeOffset StartAtUtc,
        DateTimeOffset EndAtUtc);
}
