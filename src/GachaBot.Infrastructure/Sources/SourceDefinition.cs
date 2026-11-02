using System.Text.Json;
using System.Text.Json.Serialization;
using GachaBot.Application.Ingestion;
using GachaBot.Domain.Games;
using Microsoft.Extensions.Configuration;

namespace GachaBot.Infrastructure.Sources;

public sealed record SourceDefinition
{
    public required string Key { get; init; }

    public required GameKey Game { get; init; }

    public required SourceTrust Trust { get; init; }

    public required string Handler { get; init; }

    public required Uri Url { get; init; }

    public string? ArticleUrlTemplate { get; init; }

    public JsonArticleRules? JsonArticle { get; init; }

    public HtmlArticleRules? HtmlArticle { get; init; }

    public BrowserCollectionRules? BrowserCollection { get; init; }

    public TimelineRules? Timeline { get; init; }

    public EventCalendarRules? EventCalendar { get; init; }
}

public sealed record JsonArticleRules
{
    public required string DetailUrlTemplate { get; init; }

    public required string ExternalIdField { get; init; }

    public required string TitleField { get; init; }

    public required string ContentField { get; init; }

    public string? PublishedAtField { get; init; }

    public string? PublishedAtFormat { get; init; }

    public double PublishedAtUtcOffsetHours { get; init; }

    public string? CoverField { get; init; }

    public bool StopWhenKnown { get; init; } = true;

}

public sealed record HtmlArticleRules
{
    public required string ItemSelector { get; init; }

    public string HrefAttribute { get; init; } = "href";

    public required string ExternalIdPattern { get; init; }

    public required string TitleSelector { get; init; }

    public string? CategorySelector { get; init; }

    public string? PublishedAtSelector { get; init; }

    public string? PublishedAtFormat { get; init; }

    public string? DetailPublishedAtSelector { get; init; }

    public string? DetailPublishedAtFormat { get; init; }

    public required string DetailContentSelector { get; init; }

    public required string PaginationUrlTemplate { get; init; }

    public int FirstPage { get; init; }

    public int MaximumPages { get; init; } = 1;

    public bool StopWhenKnown { get; init; } = true;

    public bool StopOnMissingPage { get; init; } = true;

    public string[] IgnoredImageHosts { get; init; } = [];

    public string[] IgnoredTitlePrefixes { get; init; } = [];
}

public sealed record BrowserCollectionRules
{
    public required string ReadySelector { get; init; }

    public required string SectionHeadingSelector { get; init; }

    public string[] SectionHeadingContains { get; init; } = [];

    public string[] SectionHeadingExcludes { get; init; } = [];

    public string[] CurrentSectionHeadingContains { get; init; } = [];

    public string[] PermanentSectionHeadingContains { get; init; } = [];

    public bool TrackAllNonPermanentSections { get; init; }

    public required string ItemSelector { get; init; }

    public required string ValueAttribute { get; init; }

    public string RowSelector { get; init; } = "tr";

    public string? ExpirySelector { get; init; }

    public string? ExpiryPattern { get; init; }

    public string? ExpiryMarker { get; init; }

    public string[] ExpiryDateFormats { get; init; } = ["MM/dd/yyyy"];

    public string[] UnknownExpiryValues { get; init; } = ["TBD"];

    public string[] PermanentExpiryValues { get; init; } = [];

    public bool MissingExpiryIsActive { get; init; }

    public string MissingExpiryDisplay { get; init; } = "Unknown";

    public string? RewardItemSelector { get; init; }

    public string? SectionExpirySelector { get; init; }

    public string? SectionExpiryValueAttribute { get; init; }

    public bool SectionExpiryUnixTimeMilliseconds { get; init; }

    public string? SectionRewardItemSelector { get; init; }

    public string? SectionRewardCodeSelector { get; init; }

    public string CurrentAggregateExternalId { get; init; } = "aggregate:current";

    public string PermanentAggregateExternalId { get; init; } = "aggregate:permanent";

    public string PermanentTitle { get; init; } = "Permanent Redeem Codes";

    public int NavigationTimeoutSeconds { get; init; } = 60;

    public int ReadyTimeoutSeconds { get; init; } = 45;
}

public sealed record TimelineRules
{
    public double ServerUtcOffsetHours { get; init; } = 8;

    public int? ServerResetHour { get; init; }
}

public sealed record EventCalendarRules
{
    public required string ReadySelector { get; init; }

    public string SectionHeadingSelector { get; init; } = "h3";

    public string[] IncludedSectionHeadings { get; init; } = [];

    public string[] ExcludedSectionHeadings { get; init; } = [];

    public string RowSelector { get; init; } = "tr";

    public string TitleSelector { get; init; } = "td:nth-child(1) a[href]";

    public string DurationSelector { get; init; } = "td:nth-child(2)";

    public string? DetailsSelector { get; init; } = "td:nth-child(3)";

    public string? RewardsSelector { get; init; } = "td:nth-child(4)";

    public string? ImageSelector { get; init; } = "img";

    public double DateUtcOffsetHours { get; init; }

    /// <summary>
    /// Hour at which a dated event starts and ends in the configured source-server time zone.
    /// </summary>
    public int DayStartHour { get; init; }

    public int NavigationTimeoutSeconds { get; init; } = 60;

    public int ReadyTimeoutSeconds { get; init; } = 45;
}

public static class SourceDefinitionCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<SourceDefinition> Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var catalog = JsonSerializer.Deserialize<CatalogDocument>(json, SerializerOptions)
            ?? throw new InvalidOperationException("Source definition document is empty.");
        return Validate(catalog.SourceDefinitions);
    }

    public static IReadOnlyList<SourceDefinition> FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var definitions = configuration.GetSection("SourceDefinitions").Get<SourceDefinition[]>() ?? [];
        return Validate(definitions);
    }

    private static SourceDefinition[] Validate(SourceDefinition[] definitions)
    {
        if (definitions.Length == 0)
        {
            throw new InvalidOperationException("At least one SourceDefinition is required.");
        }

        foreach (var definition in definitions)
        {
            Validate(definition);
        }

        var duplicate = definitions
            .GroupBy(definition => definition.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Source key '{duplicate.Key}' is duplicated.");
        }

        return definitions;
    }

    private static void Validate(SourceDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Key) || string.IsNullOrWhiteSpace(definition.Handler))
        {
            throw new InvalidOperationException("Every source requires Key and Handler.");
        }

        if (!definition.Url.IsAbsoluteUri || definition.Url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"Source '{definition.Key}' must use an absolute HTTPS Url.");
        }

        if (definition.Handler == JsonArticleFeedHandler.HandlerKey && definition.JsonArticle is null)
        {
            throw new InvalidOperationException($"Source '{definition.Key}' requires JsonArticle rules.");
        }

        if (definition.Handler == PagedHtmlArticleFeedHandler.HandlerKey && definition.HtmlArticle is null)
        {
            throw new InvalidOperationException($"Source '{definition.Key}' requires HtmlArticle rules.");
        }

        if (definition.Handler == RenderedHtmlCodeHandler.HandlerKey && definition.BrowserCollection is null)
        {
            throw new InvalidOperationException($"Source '{definition.Key}' requires BrowserCollection rules.");
        }

        if (definition.Handler == WutheringWavesTimelineHandler.HandlerKey && definition.Timeline is null)
        {
            throw new InvalidOperationException($"Source '{definition.Key}' requires Timeline rules.");
        }

        if (definition.Timeline is { ServerResetHour: < 0 or > 23 })
        {
            throw new InvalidOperationException(
                $"Source '{definition.Key}' requires Timeline.ServerResetHour between 0 and 23.");
        }

        if (definition.Handler == Game8EventCalendarHandler.HandlerKey && definition.EventCalendar is null)
        {
            throw new InvalidOperationException($"Source '{definition.Key}' requires EventCalendar rules.");
        }

        if (definition.EventCalendar is { DayStartHour: < 0 or > 23 })
        {
            throw new InvalidOperationException(
                $"Source '{definition.Key}' requires EventCalendar.DayStartHour between 0 and 23.");
        }

        if (definition.BrowserCollection is { } browser &&
            (string.IsNullOrWhiteSpace(browser.CurrentAggregateExternalId) ||
             string.IsNullOrWhiteSpace(browser.PermanentAggregateExternalId) ||
             string.IsNullOrWhiteSpace(browser.RowSelector)))
        {
            throw new InvalidOperationException(
                $"Source '{definition.Key}' requires non-empty aggregate and row selectors.");
        }
    }

    private sealed record CatalogDocument
    {
        public SourceDefinition[] SourceDefinitions { get; init; } = [];
    }
}
