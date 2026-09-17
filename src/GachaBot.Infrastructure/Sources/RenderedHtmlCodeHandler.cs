using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using GachaBot.Application.Ingestion;
using GachaBot.Domain.Content;

namespace GachaBot.Infrastructure.Sources;

public sealed record RenderedPageRequest(
    Uri Url,
    string ReadySelector,
    TimeSpan NavigationTimeout,
    TimeSpan ReadyTimeout);

public interface IRenderedPageClient
{
    Task<string> GetContentAsync(
        RenderedPageRequest request,
        CancellationToken cancellationToken);
}

public sealed class RenderedHtmlCodeHandler(
    IRenderedPageClient pageClient,
    TimeProvider timeProvider) : ISourceHandler
{
    public const string HandlerKey = "rendered-html-code-collection";

    public string Key => HandlerKey;

    public async IAsyncEnumerable<SourceContentSnapshot> FetchAsync(
        SourceDefinition definition,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rules = definition.BrowserCollection
            ?? throw new InvalidOperationException($"Source '{definition.Key}' has no BrowserCollection rules.");
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

        var currentSections = FindSections(document, rules, rules.CurrentSectionHeadingContains);
        var permanentSections = FindSections(document, rules, rules.PermanentSectionHeadingContains);
        if (currentSections.Count == 0 && permanentSections.Count == 0)
        {
            throw new InvalidOperationException(
                $"Rendered page '{definition.Url}' was ready but contained no matching code section.");
        }

        var now = timeProvider.GetUtcNow();
        var currentRows = currentSections.Count == 0
            ? permanentSections.SelectMany(section => section.Rows).ToArray()
            : currentSections.SelectMany(section => section.Rows).ToArray();
        var currentCandidates = FindCodes(currentRows, rules);
        var currentCodes = currentCandidates
            .Where(code => code.ExpiryRecognized &&
                !code.IsPermanent &&
                (code.ExpiresAtUtc is null || now < code.ExpiresAtUtc.Value))
            .ToArray();
        var currentTitle = currentSections.FirstOrDefault()?.Title ??
            permanentSections.FirstOrDefault()?.Title ??
            "Current Redeem Codes";
        yield return new SourceContentSnapshot(
            definition.Key,
            rules.CurrentAggregateExternalId,
            definition.Game,
            ContentKind.RedeemCode,
            currentTitle,
            definition.Url,
            CreateAggregateDocument(
                definition,
                currentCodes,
                "No currently active redeem codes.",
                cancellationToken),
            null,
            currentCodes.Length == 0 ? now.AddTicks(-1) : null,
            ReplacesSourceItems: true);

        var currentValues = currentCandidates
            .Where(candidate => !candidate.IsPermanent)
            .Select(candidate => candidate.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var permanentCodes = FindCodes(
                permanentSections.SelectMany(section => section.Rows).ToArray(),
                rules)
            .Where(code => code.ExpiryRecognized &&
                code.IsPermanent &&
                !currentValues.Contains(code.Value))
            .ToArray();
        yield return new SourceContentSnapshot(
            definition.Key,
            rules.PermanentAggregateExternalId,
            definition.Game,
            ContentKind.RedeemCode,
            rules.PermanentTitle,
            definition.Url,
            CreateAggregateDocument(
                definition,
                permanentCodes,
                "No permanent redeem codes found.",
                cancellationToken),
            null,
            permanentCodes.Length == 0 ? now.AddTicks(-1) : null,
            ReplacesSourceItems: true);

        foreach (var expiredCandidate in currentCandidates.Where(candidate =>
                     candidate.ExpiryRecognized &&
                     candidate.ExpiresAtUtc.HasValue &&
                     candidate.ExpiresAtUtc.Value <= now))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expiredCode = RedeemCode.Create(
                definition.Game,
                expiredCandidate.Value,
                definition.Url,
                expiredCandidate.ExpiresAtUtc);
            var details = new List<KeyValueItem>();
            if (!string.IsNullOrWhiteSpace(expiredCandidate.Rewards))
            {
                details.Add(new KeyValueItem("Rewards", expiredCandidate.Rewards));
            }

            details.Add(new KeyValueItem("Expired", expiredCandidate.ExpiryDisplay));
            yield return new SourceContentSnapshot(
                definition.Key,
                expiredCode.Code,
                definition.Game,
                ContentKind.RedeemCode,
                $"Expired redeem code: {expiredCode.Code}",
                definition.Url,
                ContentDocument.Create(
                [
                    new CodeBlock(expiredCode.Code, 1),
                    new KeyValueBlock(details, 2),
                    new LinkBlock("Source and redemption details", definition.Url, 3),
                ]),
                null,
                expiredCode.ExpiresAtUtc);
        }
    }

    private static ContentDocument CreateAggregateDocument(
        SourceDefinition definition,
        IReadOnlyList<CodeCandidate> codes,
        string emptyMessage,
        CancellationToken cancellationToken)
    {
        var blocks = new List<ContentBlock>();
        var position = 1;
        var codeItems = new List<KeyValueItem>();
        foreach (var candidate in codes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var code = RedeemCode.Create(
                definition.Game,
                candidate.Value,
                definition.Url,
                candidate.ExpiresAtUtc);
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(candidate.Rewards))
            {
                details.Add($"Rewards: {candidate.Rewards}");
            }

            details.Add($"Expires: {candidate.ExpiryDisplay}");
            codeItems.Add(new KeyValueItem(code.Code, string.Join('\n', details)));
        }

        if (codeItems.Count > 0)
        {
            blocks.Add(new KeyValueBlock(codeItems, position++));
        }

        if (codes.Count == 0)
        {
            blocks.Add(new TextBlock(emptyMessage, position++));
        }

        blocks.Add(new LinkBlock("Source and redemption details", definition.Url, position));
        return ContentDocument.Create(blocks);
    }

    private static List<CodeSection> FindSections(
        IDocument document,
        BrowserCollectionRules rules,
        IReadOnlyList<string> targetHeadingContains)
    {
        var elements = document.All.ToArray();
        var sections = new List<CodeSection>();
        for (var headingIndex = 0; headingIndex < elements.Length; headingIndex++)
        {
            var heading = elements[headingIndex];
            if (!heading.Matches(rules.SectionHeadingSelector))
            {
                continue;
            }

            var headingText = SourceParsing.NormalizeWhitespace(heading.TextContent);
            if (rules.SectionHeadingContains.Any(required =>
                    !headingText.Contains(required, StringComparison.OrdinalIgnoreCase)) ||
                rules.SectionHeadingExcludes.Any(excluded =>
                    headingText.Contains(excluded, StringComparison.OrdinalIgnoreCase)) ||
                targetHeadingContains.Any(required =>
                    !headingText.Contains(required, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var rows = new List<IElement>();
            for (var index = headingIndex + 1; index < elements.Length; index++)
            {
                var element = elements[index];
                if (element.Matches(rules.SectionHeadingSelector))
                {
                    break;
                }

                if (element.Matches(rules.RowSelector) && element.QuerySelector(rules.ItemSelector) is not null)
                {
                    rows.Add(element);
                }
            }

            if (rows.Count > 0)
            {
                sections.Add(new CodeSection(headingText, rows));
            }
        }

        return sections;
    }

    private static List<CodeCandidate> FindCodes(
        IReadOnlyList<IElement> rows,
        BrowserCollectionRules rules)
    {
        var result = new List<CodeCandidate>();
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var item = row.QuerySelector(rules.ItemSelector);
            var value = SourceParsing.NormalizeWhitespace(
                item?.GetAttribute(rules.ValueAttribute) ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var expiryText = rules.ExpirySelector is null
                ? string.Empty
                : SourceParsing.NormalizeWhitespace(row.QuerySelector(rules.ExpirySelector)?.TextContent ?? string.Empty);
            var expiry = ParseExpiry(expiryText, rules);
            var rewards = rules.RewardItemSelector is null
                ? string.Empty
                : string.Join(", ", row.QuerySelectorAll(rules.RewardItemSelector)
                    .Select(element => CleanReward(element.TextContent))
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            var candidate = new CodeCandidate(
                value,
                expiry.Display,
                expiry.ExpiresAtUtc,
                rewards,
                expiry.IsRecognized,
                expiry.IsPermanent);
            if (indexes.TryGetValue(value, out var existingIndex))
            {
                result[existingIndex] = Merge(result[existingIndex], candidate);
            }
            else
            {
                indexes.Add(value, result.Count);
                result.Add(candidate);
            }
        }

        return result;
    }

    private static ParsedExpiry ParseExpiry(string text, BrowserCollectionRules rules)
    {
        if (rules.ExpirySelector is null)
        {
            return new ParsedExpiry(rules.MissingExpiryDisplay, null, true, true);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ParsedExpiry(
                rules.MissingExpiryDisplay,
                null,
                rules.MissingExpiryIsActive,
                rules.MissingExpiryIsActive);
        }

        if (string.IsNullOrWhiteSpace(rules.ExpiryPattern))
        {
            return new ParsedExpiry("Unknown", null, false, false);
        }

        var match = Regex.Match(text, rules.ExpiryPattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!match.Success || !match.Groups.TryGetValue("value", out var valueGroup))
        {
            if (rules.MissingExpiryIsActive &&
                !string.IsNullOrWhiteSpace(rules.ExpiryMarker) &&
                !text.Contains(rules.ExpiryMarker, StringComparison.OrdinalIgnoreCase))
            {
                return new ParsedExpiry(rules.MissingExpiryDisplay, null, true, true);
            }

            return new ParsedExpiry("Unknown", null, false, false);
        }

        var value = SourceParsing.NormalizeWhitespace(valueGroup.Value);
        if (rules.UnknownExpiryValues.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return new ParsedExpiry(value, null, true, false);
        }

        if (rules.PermanentExpiryValues.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return new ParsedExpiry(value, null, true, true);
        }

        foreach (var format in rules.ExpiryDateFormats)
        {
            if (DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                var expiresAtUtc = new DateTimeOffset(
                    date.AddDays(1).ToDateTime(TimeOnly.MinValue),
                    TimeSpan.Zero);
                var display = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{date:dd.MM.yyyy} (<t:{expiresAtUtc.ToUnixTimeSeconds()}:R>)");
                return new ParsedExpiry(display, expiresAtUtc, true, false);
            }
        }

        return new ParsedExpiry(value, null, false, false);
    }

    private static CodeCandidate Merge(CodeCandidate existing, CodeCandidate incoming)
    {
        var expiry = !existing.ExpiryRecognized ||
            (existing.ExpiresAtUtc is null && incoming.ExpiresAtUtc is not null)
                ? incoming
                : existing;
        var rewards = incoming.Rewards.Length > existing.Rewards.Length
            ? incoming.Rewards
            : existing.Rewards;
        return expiry with { Rewards = rewards };
    }

    private static string CleanReward(string value) =>
        SourceParsing.NormalizeWhitespace(value).TrimStart('・', '•', '-', ' ');

    private sealed record CodeSection(string Title, IReadOnlyList<IElement> Rows);

    private sealed record CodeCandidate(
        string Value,
        string ExpiryDisplay,
        DateTimeOffset? ExpiresAtUtc,
        string Rewards,
        bool ExpiryRecognized,
        bool IsPermanent);

    private sealed record ParsedExpiry(
        string Display,
        DateTimeOffset? ExpiresAtUtc,
        bool IsRecognized,
        bool IsPermanent);
}
