using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using GachaBot.Application.Ingestion;
using GachaBot.Domain.Content;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GachaBot.Infrastructure.Sources;

public sealed partial class PagedHtmlArticleFeedHandler(
    HttpClient httpClient,
    ISourceContentLookup contentLookup,
    ILogger<PagedHtmlArticleFeedHandler>? logger = null) : ISourceHandler
{
    public const string HandlerKey = "paged-html-article-feed";

    private readonly ILogger _logger = logger ?? NullLogger<PagedHtmlArticleFeedHandler>.Instance;

    public string Key => HandlerKey;

    public async IAsyncEnumerable<SourceContentSnapshot> FetchAsync(
        SourceDefinition definition,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rules = definition.HtmlArticle
            ?? throw new InvalidOperationException($"Source '{definition.Key}' has no HtmlArticle rules.");
        var idRegex = new Regex(rules.ExternalIdPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var preparedItems = 0;
        LogScanStarted(_logger, definition.Key, rules.FirstPage + 1, rules.MaximumPages);
        for (var page = rules.FirstPage; page < rules.FirstPage + rules.MaximumPages; page++)
        {
            var pageNumber = page - rules.FirstPage + 1;
            var pageUri = BuildPageUri(rules.PaginationUrlTemplate, page);
            LogPageFetchStarted(_logger, definition.Key, pageNumber, pageUri);
            using var listDocument = await GetHtmlAsync(
                pageUri,
                rules.StopOnMissingPage,
                cancellationToken).ConfigureAwait(false);
            if (listDocument is null)
            {
                LogMissingPageStoppedScan(_logger, definition.Key, pageNumber, preparedItems);
                yield break;
            }
            var anchors = listDocument.QuerySelectorAll(rules.ItemSelector);
            LogPageCandidatesFound(_logger, definition.Key, pageNumber, anchors.Length, preparedItems);
            if (anchors.Length == 0)
            {
                LogEmptyPageStoppedScan(_logger, definition.Key, pageNumber, preparedItems);
                yield break;
            }

            foreach (var anchor in anchors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var href = anchor.GetAttribute(rules.HrefAttribute);
                if (!SourceParsing.TryResolveHttpUri(pageUri, href, out var articleUri))
                {
                    continue;
                }

                var match = idRegex.Match(articleUri.AbsolutePath);
                if (!match.Success)
                {
                    continue;
                }

                var externalId = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                if (!seen.Add(externalId))
                {
                    continue;
                }

                if (rules.StopWhenKnown && await contentLookup.ExistsAsync(
                        definition.Key,
                        externalId,
                        cancellationToken).ConfigureAwait(false))
                {
                    LogKnownItemStoppedScan(
                        _logger,
                        definition.Key,
                        externalId,
                        pageNumber,
                        preparedItems);
                    yield break;
                }

                var title = Text(anchor, rules.TitleSelector);
                if (string.IsNullOrWhiteSpace(title) ||
                    rules.IgnoredTitlePrefixes.Any(prefix => title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var category = Text(anchor, rules.CategorySelector);
                LogItemFetchStarted(
                    _logger,
                    definition.Key,
                    preparedItems + 1,
                    pageNumber,
                    externalId,
                    title);
                using var detailDocument = await GetHtmlAsync(
                    articleUri,
                    allowNotFound: true,
                    cancellationToken).ConfigureAwait(false);
                if (detailDocument is null)
                {
                    LogMissingArticleSkipped(_logger, definition.Key, externalId, articleUri);
                    continue;
                }
                var detail = detailDocument.QuerySelector(rules.DetailContentSelector)
                    ?? throw new InvalidOperationException(
                        $"Detail selector '{rules.DetailContentSelector}' was not found at {articleUri}.");
                var blocks = (await SourceParsing.HtmlToBlocksAsync(
                    detail.InnerHtml,
                    articleUri,
                    title,
                    cancellationToken,
                    imageUri => !rules.IgnoredImageHosts.Contains(
                        imageUri.Host,
                        StringComparer.OrdinalIgnoreCase)).ConfigureAwait(false)).ToList();
                blocks.Add(new LinkBlock("Official announcement", articleUri, blocks.Count + 1));

                var publishedAt = ParseDate(
                    Text(anchor, rules.PublishedAtSelector),
                    rules.PublishedAtFormat)
                    ?? ParseDate(
                        Text(detailDocument.DocumentElement, rules.DetailPublishedAtSelector),
                        rules.DetailPublishedAtFormat);

                preparedItems++;
                LogItemPrepared(_logger, definition.Key, preparedItems, externalId, title);

                yield return new SourceContentSnapshot(
                    definition.Key,
                    externalId,
                    definition.Game,
                    SourceParsing.Classify(title, category),
                    title,
                    articleUri,
                    ContentDocument.Create(blocks),
                    publishedAt,
                    null);
            }
        }

        LogPageLimitReached(_logger, definition.Key, rules.MaximumPages, preparedItems);
    }

    private async Task<IDocument?> GetHtmlAsync(
        Uri uri,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (allowNotFound && response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return await new HtmlParser().ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
    }

    private static Uri BuildPageUri(string template, int page)
    {
        var suffix = page == 0 ? string.Empty : page.ToString(CultureInfo.InvariantCulture);
        return new Uri(template
            .Replace("{page}", page.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{pageSuffix}", suffix, StringComparison.Ordinal));
    }

    private static string Text(IElement element, string? selector) =>
        string.IsNullOrWhiteSpace(selector)
            ? string.Empty
            : SourceParsing.NormalizeWhitespace(element.QuerySelector(selector)?.TextContent ?? string.Empty);

    private static DateTimeOffset? ParseDate(string value, string? format) =>
        !string.IsNullOrWhiteSpace(format) && DateTimeOffset.TryParseExact(
            value,
            format,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
                ? parsed
                : null;
}
