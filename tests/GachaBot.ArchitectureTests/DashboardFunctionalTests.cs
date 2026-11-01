using GachaBot.Application.Content;
using GachaBot.Application.Ingestion;
using GachaBot.Application.Publishing;
using GachaBot.Domain.Content;
using GachaBot.Domain.Games;
using GachaBot.Infrastructure.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GachaBot.ArchitectureTests;

public sealed class DashboardFunctionalTests : IClassFixture<DashboardApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly DashboardApplicationFactory _factory;

    public DashboardFunctionalTests(DashboardApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    [Theory]
    [InlineData("/", "Last checked")]
    [InlineData("/events", "Events")]
    [InlineData("/content/new", "New post")]
    [InlineData("/sources", "Sources")]
    [InlineData("/archive", "Archive")]
    [InlineData("/guilds", "Guilds")]
    [InlineData("/guilds/1", "What will be published")]
    [InlineData("/guide", "How it works")]
    public async Task DashboardRoute_ReturnsExpectedLandmark(string route, string landmark)
    {
        using var response = await _client.GetAsync(route, TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains(landmark, html, StringComparison.Ordinal);
        Assert.Contains("<main>", html, StringComparison.Ordinal);
        Assert.Contains("<nav", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_ReturnsHealthyStatus()
    {
        using var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains("healthy", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sources_OffersPerSourceRefreshAndExtractedContentNavigation()
    {
        using var response = await _client.GetAsync("/sources", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var decodedHtml = System.Net.WebUtility.HtmlDecode(html);

        response.EnsureSuccessStatusCode();
        Assert.Contains("official-wuthering-waves", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("Refresh source", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("View content", decodedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentDetails_ShowsExtractedBlocksAndDiscordPreview()
    {
        Guid id;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IContentManagementStore>();
            id = await store.CreateManualAsync(
                new CreateManualContentCommand(
                    GameKey.WutheringWaves,
                    ContentKind.Event,
                    "Preview event",
                    ContentDocument.Create(
                    [
                        new HeadingBlock("Event heading", 1),
                        new ImageBlock(new Uri("https://cdn.example.com/event.webp"), "Event art", 2),
                        new LinkBlock(
                            "Event trailer",
                            new Uri("https://youtu.be/dQw4w9WgXcQ"),
                            3),
                    ]),
                    null),
                TestContext.Current.CancellationToken);
        }

        using var response = await _client.GetAsync($"/content/{id}", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var decodedHtml = System.Net.WebUtility.HtmlDecode(html);

        response.EnsureSuccessStatusCode();
        Assert.Contains("EXTRACTED SOURCE DATA", html, StringComparison.Ordinal);
        Assert.Contains("DISCORD PREVIEW", html, StringComparison.Ordinal);
        Assert.Contains("Appearance simulation", html, StringComparison.Ordinal);
        Assert.Contains("Exact payload", html, StringComparison.Ordinal);
        Assert.Contains("Event heading", html, StringComparison.Ordinal);
        Assert.Contains("https://cdn.example.com/event.webp", html, StringComparison.Ordinal);
        Assert.Contains($"/media/", html, StringComparison.Ordinal);
        Assert.Contains(id.ToString("D"), html, StringComparison.Ordinal);
        Assert.Contains("Discord rich embed", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("\"Title\": \"Preview event\"", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("\"Color\": 5793266", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("Native YouTube preview", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("https://www.youtube.com/watch?v=dQw4w9WgXcQ", decodedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentIndex_OffersAccessibleColumnSorting()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IContentManagementStore>();
            foreach (var title in new[] { "Zulu update", "Alpha update" })
            {
                await store.CreateManualAsync(
                    new CreateManualContentCommand(
                        GameKey.WutheringWaves,
                        ContentKind.Update,
                        title,
                        ContentDocument.Create([new TextBlock("Details", 1)]),
                        null),
                    TestContext.Current.CancellationToken);
            }
        }

        using var response = await _client.GetAsync(
            "/content?sort=title&direction=asc",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains("aria-sort=\"ascending\"", html, StringComparison.Ordinal);
        Assert.Contains("sort=title", html, StringComparison.Ordinal);
        Assert.Contains("content-game", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("Alpha update", StringComparison.Ordinal) <
            html.IndexOf("Zulu update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Events_ShowsCalendarEntryForImportedEvent()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var sink = scope.ServiceProvider.GetRequiredService<IIngestionSink>();
            await sink.UpsertAsync(
                new SourceContentSnapshot(
                    "wuwatracker-wuthering-waves-events",
                    "combat-event:20260822",
                    GameKey.WutheringWaves,
                    ContentKind.Event,
                    "Combat Event",
                    new Uri("https://wutheringwaves.kurogames.com/en/main/news/detail/5310"),
                    ContentDocument.Create(
                    [
                        new TextBlock("Clear stages.", 1),
                        new ImageBlock(new Uri("https://example.com/combat-event.webp"), "Combat event artwork", 2),
                    ]),
                    DateTimeOffset.UtcNow.AddHours(-2),
                    DateTimeOffset.UtcNow.AddDays(4)),
                PublicationDisposition.SuppressBaseline,
                TestContext.Current.CancellationToken);
        }

        using var response = await _client.GetAsync("/events?game=WutheringWaves", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains("Through ", html, StringComparison.Ordinal);
        Assert.Contains("Combat Event", html, StringComparison.Ordinal);
        Assert.Contains("<h1>Events</h1>", html, StringComparison.Ordinal);
        Assert.Contains("calendar-event-rails", html, StringComparison.Ordinal);
        Assert.Contains("calendar-event-title", html, StringComparison.Ordinal);
        Assert.Contains("calendar-event-art", html, StringComparison.Ordinal);
        Assert.Contains("palette-", html, StringComparison.Ordinal);
        Assert.DoesNotContain("calendar-grid", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_DoesNotOfferRedundantSourceFilter()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var sink = scope.ServiceProvider.GetRequiredService<IIngestionSink>();
            foreach (var source in new[]
                     {
                         (Key: "game8-neverness-to-everness-events", Title: "NTE Game8 event"),
                         (Key: "wuwatracker-wuthering-waves-events", Title: "WuWa tracker event"),
                     })
            {
                await sink.UpsertAsync(
                    new SourceContentSnapshot(
                        source.Key,
                        source.Key,
                        GameKey.NevernessToEverness,
                        ContentKind.Event,
                        source.Title,
                        new Uri("https://example.com/events"),
                        ContentDocument.Create([new TextBlock("Details", 1)]),
                        DateTimeOffset.UtcNow.AddHours(1),
                        DateTimeOffset.UtcNow.AddDays(3)),
                    PublicationDisposition.SuppressBaseline,
                    TestContext.Current.CancellationToken);
            }
        }

        using var response = await _client.GetAsync(
            "/events",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains("NTE Game8 event", html, StringComparison.Ordinal);
        Assert.Contains("WuWa tracker event", html, StringComparison.Ordinal);
        Assert.DoesNotContain("event-source", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentIndex_WithoutSortQuery_UsesUpdatedDescendingDefault()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IContentManagementStore>();
            await store.CreateManualAsync(
                new CreateManualContentCommand(
                    GameKey.NevernessToEverness,
                    ContentKind.Update,
                    "Default sort regression",
                    ContentDocument.Create([new TextBlock("Details", 1)]),
                    null),
                TestContext.Current.CancellationToken);
        }

        using var response = await _client.GetAsync("/content", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains("Default sort regression", html, StringComparison.Ordinal);
        Assert.Contains("aria-sort=\"descending\"", html, StringComparison.Ordinal);
        Assert.Contains("sort=updated", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentIndex_SortsBySourceDateAndKeepsMissingDatesLast()
    {
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var sink = scope.ServiceProvider.GetRequiredService<IIngestionSink>();
            foreach (var item in new[]
                     {
                         (Id: "source-date-old", Title: "Older source date", Date: new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero)),
                         (Id: "source-date-new", Title: "Newer source date", Date: new DateTimeOffset(2026, 7, 28, 0, 0, 0, TimeSpan.Zero)),
                     })
            {
                await sink.UpsertAsync(
                    new SourceContentSnapshot(
                        "source-date-sort",
                        item.Id,
                        GameKey.NevernessToEverness,
                        ContentKind.News,
                        item.Title,
                        new Uri($"https://example.com/{item.Id}"),
                        ContentDocument.Create([new TextBlock("Details", 1)]),
                        item.Date,
                        null),
                    PublicationDisposition.SuppressBaseline,
                    TestContext.Current.CancellationToken);
            }

            var store = scope.ServiceProvider.GetRequiredService<IContentManagementStore>();
            await store.CreateManualAsync(
                new CreateManualContentCommand(
                    GameKey.NevernessToEverness,
                    ContentKind.News,
                    "Missing source date",
                    ContentDocument.Create([new TextBlock("Manual", 1)]),
                    null),
                TestContext.Current.CancellationToken);
        }

        using var response = await _client.GetAsync(
            "/content?sort=source-date&direction=asc",
            TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains("Source date", html, StringComparison.Ordinal);
        Assert.Contains("sort=source-date", html, StringComparison.Ordinal);
        Assert.Contains("02.06.2026", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("Older source date", StringComparison.Ordinal) <
            html.IndexOf("Newer source date", StringComparison.Ordinal));
        Assert.True(
            html.IndexOf("Newer source date", StringComparison.Ordinal) <
            html.IndexOf("Missing source date", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContentDetails_ShowsDateExtractedFromSourceSeparatelyFromBotPublicationDate()
    {
        Guid id;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var sink = scope.ServiceProvider.GetRequiredService<IIngestionSink>();
            var document = ContentDocument.Create([new TextBlock("Update details", 1)]);
            var snapshot = new SourceContentSnapshot(
                "official-neverness-to-everness",
                "262479",
                GameKey.NevernessToEverness,
                ContentKind.Update,
                "Version 1.1 Update Notes",
                new Uri("https://nte.perfectworld.com/en/article/news/gamebroad/20260602/262479.html"),
                document,
                new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero),
                null);
            await sink.UpsertAsync(
                snapshot,
                PublicationDisposition.SuppressBaseline,
                TestContext.Current.CancellationToken);
            var store = scope.ServiceProvider.GetRequiredService<IContentManagementStore>();
            id = (await store.ListAsync(
                null,
                snapshot.SourceKey,
                TestContext.Current.CancellationToken)).Single().Id;
        }

        using var response = await _client.GetAsync($"/content/{id}", TestContext.Current.CancellationToken);
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains("Source date", html, StringComparison.Ordinal);
        Assert.Contains("02 Jun 2026", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentDetails_ShowsManualArchiveReason()
    {
        Guid id;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IContentManagementStore>();
            id = await store.CreateManualAsync(
                new CreateManualContentCommand(
                    GameKey.WutheringWaves,
                    ContentKind.News,
                    "Operator archived item",
                    ContentDocument.Create([new TextBlock("Details", 1)]),
                    null),
                TestContext.Current.CancellationToken);
            await store.ArchiveAsync(id, TestContext.Current.CancellationToken);
        }

        using var response = await _client.GetAsync($"/content/{id}", TestContext.Current.CancellationToken);
        var html = System.Net.WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        response.EnsureSuccessStatusCode();
        Assert.Contains("Archive reason", html, StringComparison.Ordinal);
        Assert.Contains("Manual", html, StringComparison.Ordinal);
    }
}

public sealed class DashboardApplicationFactory : WebApplicationFactory<Program>
{
    private readonly InMemoryDatabaseRoot _databaseRoot = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Workers:Enabled", "false");
        builder.UseSetting("DatabaseStorage:ConnectionString", "Host=test;Database=test;Username=test;Password=test");
        builder.UseSetting("S3Media:Endpoint", "http://garage.test");
        builder.UseSetting("S3Media:AccessKey", "test");
        builder.UseSetting("S3Media:SecretKey", "test");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISourceDatabaseFactory>();
            services.AddSingleton<ISourceDatabaseFactory>(new TestSourceDatabaseFactory(_databaseRoot));
            services.RemoveAll<IGuildDestinationStore>();
            services.AddSingleton<IGuildDestinationStore, LegacyGuildDestinationStore>();
            services.RemoveAll<ISourceOperations>();
            services.AddSingleton<ISourceOperations, DashboardSourceOperations>();
        });
    }

}

public sealed class TestSourceDatabaseFactory(InMemoryDatabaseRoot databaseRoot) : ISourceDatabaseFactory
{
    public IReadOnlyList<string> DatabaseKeys { get; } = ["test"];

    public AppDbContext CreateDbContext(string sourceKey) => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("dashboard-functional-tests", databaseRoot)
            .Options);

}

public sealed class DashboardSourceOperations : ISourceOperations
{
    private const string SourceKey = "official-wuthering-waves";

    public Task<IReadOnlyList<SourceStatus>> GetStatusesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceStatus>>(
        [
            new SourceStatus(SourceKey, SourceTrust.Official, true, DateTimeOffset.UtcNow, null, null),
        ]);

    public Task<IReadOnlyList<SourceRunResult>> RunAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceRunResult>>([]);

    public Task<SourceRunResult> RunAsync(string sourceKey, CancellationToken cancellationToken) =>
        Task.FromResult(new SourceRunResult(sourceKey, true, null, null));
}
