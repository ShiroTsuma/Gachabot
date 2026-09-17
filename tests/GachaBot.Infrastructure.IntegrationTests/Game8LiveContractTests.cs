using GachaBot.Application.Ingestion;
using GachaBot.Domain.Content;
using GachaBot.Infrastructure.Configuration;
using GachaBot.Infrastructure.Sources;
using Microsoft.Extensions.Options;

namespace GachaBot.Infrastructure.IntegrationTests;

public sealed class Game8LiveContractTests
{
    [Fact(Explicit = true)]
    public async Task NteProductionDefinition_AggregatesCurrentCodesWithDetails()
    {
        var catalogJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "source-definitions.json"));
        var definition = SourceDefinitionCatalog.Parse(catalogJson).Single(source =>
            source.Key == "game8-neverness-to-everness-redeem-codes");
        Assert.Equal("input.a-clipboard__textInput", definition.BrowserCollection?.ReadySelector);
        var profilePath = Path.Combine(
            Path.GetTempPath(),
            "gachabot-playwright-contract",
            Guid.NewGuid().ToString("N"));
        try
        {
            await using var pageClient = new PlaywrightRenderedPageClient(
                Options.Create(new BrowserAutomationOptions { ProfilePath = profilePath }));
            var source = new ConfiguredGameContentSource(
                definition,
                new SourceHandlerResolver([new RenderedHtmlCodeHandler(
                    pageClient,
                    new FixedTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero)))]));
            var items = new List<SourceContentSnapshot>();

            await foreach (var item in source.FetchAsync(TestContext.Current.CancellationToken))
            {
                items.Add(item);
            }

            var current = Assert.Single(items, item => item.ExternalId == "aggregate:current");
            Assert.Equal("All Active Redeem Codes", current.Title);
            Assert.NotEmpty(current.Document.Blocks.OfType<KeyValueBlock>().SelectMany(block => block.Items));
            Assert.False(current.ExpiresAtUtc.HasValue);
            var permanent = Assert.Single(items, item => item.ExternalId == "aggregate:permanent");
            Assert.Contains(
                permanent.Document.Blocks.OfType<KeyValueBlock>().SelectMany(block => block.Items),
                item => item.Key == "NTENENE");
            Assert.Contains(items, item =>
                item.ExternalId != "aggregate:current" &&
                item.ExternalId != "aggregate:permanent" &&
                item.ExpiresAtUtc.HasValue);
        }
        finally
        {
            if (Directory.Exists(profilePath))
            {
                Directory.Delete(profilePath, recursive: true);
            }
        }
    }

    [Fact(Explicit = true)]
    public async Task WutheringWavesProductionDefinition_ExtractsCurrentCodesWithDetails()
    {
        var catalogJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "source-definitions.json"));
        var definition = SourceDefinitionCatalog.Parse(catalogJson).Single(source =>
            source.Key == "game8-wuthering-waves-redeem-codes");
        var profilePath = Path.Combine(
            Path.GetTempPath(),
            "gachabot-playwright-contract",
            Guid.NewGuid().ToString("N"));
        try
        {
            await using var pageClient = new PlaywrightRenderedPageClient(
                Options.Create(new BrowserAutomationOptions { ProfilePath = profilePath }));
            var source = new ConfiguredGameContentSource(
                definition,
                new SourceHandlerResolver([new RenderedHtmlCodeHandler(pageClient, TimeProvider.System)]));
            var items = new List<SourceContentSnapshot>();

            await foreach (var item in source.FetchAsync(TestContext.Current.CancellationToken))
            {
                items.Add(item);
            }

            var current = Assert.Single(items, item => item.ExternalId == "aggregate:current");
            var permanent = Assert.Single(items, item => item.ExternalId == "aggregate:permanent");
            Assert.NotEmpty(current.Document.Blocks.OfType<KeyValueBlock>().SelectMany(block => block.Items));
            Assert.Contains(current.Document.Blocks.OfType<KeyValueBlock>().SelectMany(block => block.Items), item =>
                item.Value.Contains("Rewards:", StringComparison.Ordinal));
            Assert.Contains(
                permanent.Document.Blocks.OfType<KeyValueBlock>().SelectMany(block => block.Items),
                item => item.Key == "WUTHERINGGIFT");
        }
        finally
        {
            if (Directory.Exists(profilePath))
            {
                Directory.Delete(profilePath, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
