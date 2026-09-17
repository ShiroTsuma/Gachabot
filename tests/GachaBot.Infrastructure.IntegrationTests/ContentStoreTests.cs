using GachaBot.Application.Content;
using GachaBot.Application.Ingestion;
using GachaBot.Application.Publishing;
using GachaBot.Domain.Content;
using GachaBot.Domain.Games;
using GachaBot.Infrastructure.Database;
using GachaBot.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;

namespace GachaBot.Infrastructure.IntegrationTests;

public sealed class ContentStoreTests : IAsyncLifetime
{
    private readonly string _databaseName = Guid.NewGuid().ToString("N");
    private AppDbContext _db = null!;
    private ContentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(_databaseName)
                .Options);
        await _db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        _store = new ContentStore(_db, TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UpsertAsync_RepeatedSnapshot_DoesNotDuplicateContent()
    {
        var snapshot = Snapshot("Original");

        var first = await _store.UpsertAsync(
            snapshot,
            PublicationDisposition.SuppressBaseline,
            TestContext.Current.CancellationToken);
        var second = await _store.UpsertAsync(
            snapshot,
            PublicationDisposition.AutoPublish,
            TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpsertOutcome.Created, first);
        Assert.Equal(ContentUpsertOutcome.Unchanged, second);
        Assert.Equal(1, await _db.ContentItems.CountAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_db.Publications);
    }

    [Fact]
    public async Task UpsertAsync_AutoPublishCreatesPublicationForEveryConfiguredGuild()
    {
        var destinations = new FixedDestinationStore(
        [
            new GuildDestination(101, 201, 301, true, GuildDestinationGames.All, DateTimeOffset.UtcNow),
            new GuildDestination(102, 202, 302, true, GuildDestinationGames.All, DateTimeOffset.UtcNow),
        ]);
        var store = new ContentStore(_db, TimeProvider.System, destinations);

        await store.UpsertAsync(
            Snapshot("New content"),
            PublicationDisposition.AutoPublish,
            TestContext.Current.CancellationToken);

        var publications = await _db.Publications
            .OrderBy(publication => publication.DestinationGuildId)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Collection(
            publications,
            publication => Assert.Equal(101, publication.DestinationGuildId),
            publication => Assert.Equal(102, publication.DestinationGuildId));
    }

    [Fact]
    public async Task UpsertAsync_AutoPublishPromotesExistingReviewDraftWithUnchangedDocument()
    {
        var destinations = new FixedDestinationStore(
        [
            new GuildDestination(101, 201, 301, true, GuildDestinationGames.All, DateTimeOffset.UtcNow),
        ]);
        var store = new ContentStore(_db, TimeProvider.System, destinations);
        var snapshot = Snapshot("Review candidate");

        await store.UpsertAsync(
            snapshot,
            PublicationDisposition.AwaitReview,
            TestContext.Current.CancellationToken);
        var outcome = await store.UpsertAsync(
            snapshot,
            PublicationDisposition.AutoPublish,
            TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpsertOutcome.Unchanged, outcome);
        var content = await _db.ContentItems.SingleAsync(TestContext.Current.CancellationToken);
        Assert.False(content.AwaitingReview);
        Assert.Equal(ContentStatus.Active, content.Status);
        var publication = await _db.Publications.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(101, publication.DestinationGuildId);
    }

    [Fact]
    public async Task UpsertAsync_AutoPublishSkipsGuildsThatDidNotSelectTheContentGame()
    {
        var destinations = new FixedDestinationStore(
        [
            new GuildDestination(
                101,
                201,
                301,
                true,
                new HashSet<GameKey> { GameKey.WutheringWaves },
                DateTimeOffset.UtcNow),
            new GuildDestination(
                102,
                202,
                302,
                true,
                new HashSet<GameKey> { GameKey.NevernessToEverness },
                DateTimeOffset.UtcNow),
        ]);
        var store = new ContentStore(_db, TimeProvider.System, destinations);

        await store.UpsertAsync(
            Snapshot("WUWA only"),
            PublicationDisposition.AutoPublish,
            TestContext.Current.CancellationToken);

        var publication = await _db.Publications.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(101, publication.DestinationGuildId);
    }

    [Fact]
    public async Task UpsertAsync_AutoPublishSkipsGuildsThatDidNotSelectTheContentSubject()
    {
        var destinations = new FixedDestinationStore(
        [
            new GuildDestination(
                101,
                201,
                301,
                true,
                new HashSet<GameKey> { GameKey.WutheringWaves },
                DateTimeOffset.UtcNow,
                TopicSubscriptions: new HashSet<GuildTopicSubscription>
                {
                    new(GameKey.WutheringWaves, ContentKind.Event),
                }),
            new GuildDestination(
                102,
                202,
                302,
                true,
                new HashSet<GameKey> { GameKey.WutheringWaves },
                DateTimeOffset.UtcNow,
                TopicSubscriptions: new HashSet<GuildTopicSubscription>
                {
                    new(GameKey.WutheringWaves, ContentKind.News),
                }),
        ]);
        var store = new ContentStore(_db, TimeProvider.System, destinations);

        await store.UpsertAsync(
            Snapshot("News only"),
            PublicationDisposition.AutoPublish,
            TestContext.Current.CancellationToken);

        var publication = await _db.Publications.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(102, publication.DestinationGuildId);
    }

    [Fact]
    public async Task UpsertAsync_ScheduleUpcomingQueuesFutureEventAtItsStart()
    {
        var startAtUtc = DateTimeOffset.UtcNow.AddHours(6);
        var snapshot = Snapshot("Future event") with
        {
            Kind = ContentKind.Event,
            PublishedAtUtc = startAtUtc,
            ExpiresAtUtc = startAtUtc.AddDays(7),
            PublishAtUtc = startAtUtc,
        };

        await _store.UpsertAsync(
            snapshot,
            PublicationDisposition.ScheduleUpcoming,
            TestContext.Current.CancellationToken);

        var content = await _db.ContentItems.SingleAsync(TestContext.Current.CancellationToken);
        var publication = await _db.Publications.SingleAsync(
            publication => publication.Purpose == PublicationPurpose.EventStart,
            TestContext.Current.CancellationToken);
        var endingReminder = await _db.Publications.SingleAsync(
            publication => publication.Purpose == PublicationPurpose.EventEndingReminder,
            TestContext.Current.CancellationToken);
        Assert.Equal(ContentStatus.Scheduled, content.Status);
        Assert.Equal(startAtUtc, content.ScheduledAtUtc);
        Assert.Equal(startAtUtc, publication.DueAtUtc);
        Assert.Equal(startAtUtc.AddDays(5), endingReminder.DueAtUtc);
    }

    [Fact]
    public async Task UpsertAsync_ScheduleUpcomingPublishesNewlyDiscoveredActiveEventImmediately()
    {
        var now = new DateTimeOffset(2026, 8, 22, 16, 0, 0, TimeSpan.Zero);
        var store = new ContentStore(_db, new FixedTimeProvider(now));
        var snapshot = Snapshot("Active event") with
        {
            Kind = ContentKind.Event,
            PublishedAtUtc = now.AddDays(-2),
            ExpiresAtUtc = now.AddDays(7),
            PublishAtUtc = now.AddDays(-2),
        };

        await store.UpsertAsync(
            snapshot,
            PublicationDisposition.ScheduleUpcoming,
            TestContext.Current.CancellationToken);

        var content = await _db.ContentItems.SingleAsync(TestContext.Current.CancellationToken);
        var publication = await _db.Publications.SingleAsync(
            publication => publication.Purpose == PublicationPurpose.EventStart,
            TestContext.Current.CancellationToken);
        Assert.Equal(ContentStatus.Active, content.Status);
        Assert.Equal(now, content.ScheduledAtUtc);
        Assert.Equal(now, publication.DueAtUtc);
    }

    [Fact]
    public async Task UpsertAsync_ScheduleUpcomingUsesEachGuildsEventNotificationOffsets()
    {
        var now = new DateTimeOffset(2026, 8, 22, 16, 0, 0, TimeSpan.Zero);
        var startsAt = now.AddHours(8);
        var endsAt = startsAt.AddDays(7);
        var destinations = new FixedDestinationStore(
        [
            new GuildDestination(
                101,
                201,
                301,
                true,
                GuildDestinationGames.All,
                now,
                EventStartOffsetHours: 4,
                EventEndOffsetHours: 0.5),
        ]);
        var store = new ContentStore(_db, new FixedTimeProvider(now), destinations);
        var snapshot = Snapshot("Offset event") with
        {
            Kind = ContentKind.Event,
            PublishedAtUtc = startsAt,
            ExpiresAtUtc = endsAt,
            PublishAtUtc = startsAt,
        };

        await store.UpsertAsync(snapshot, PublicationDisposition.ScheduleUpcoming, TestContext.Current.CancellationToken);

        var publications = await _db.Publications.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(startsAt.AddHours(-4), Assert.Single(
            publications,
            item => item.Purpose == PublicationPurpose.EventStart).DueAtUtc);
        Assert.Equal(endsAt.AddMinutes(-30), Assert.Single(
            publications,
            item => item.Purpose == PublicationPurpose.EventEndingReminder).DueAtUtc);
    }

    [Fact]
    public async Task UpsertAsync_ScheduleUpcomingSkipsEventPostsDisabledForTheGuild()
    {
        var now = new DateTimeOffset(2026, 8, 22, 16, 0, 0, TimeSpan.Zero);
        var destinations = new FixedDestinationStore(
        [
            new GuildDestination(
                101,
                201,
                301,
                true,
                GuildDestinationGames.All,
                now,
                EventStartOffsetHours: -1,
                EventEndOffsetHours: 24),
        ]);
        var store = new ContentStore(_db, new FixedTimeProvider(now), destinations);
        var startsAt = now.AddHours(8);
        var endsAt = startsAt.AddDays(7);
        var snapshot = Snapshot("Ending reminder only") with
        {
            Kind = ContentKind.Event,
            PublishedAtUtc = startsAt,
            ExpiresAtUtc = endsAt,
            PublishAtUtc = startsAt,
        };

        await store.UpsertAsync(snapshot, PublicationDisposition.ScheduleUpcoming, TestContext.Current.CancellationToken);

        var publication = Assert.Single(await _db.Publications.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PublicationPurpose.EventEndingReminder, publication.Purpose);
        Assert.Equal(endsAt.AddHours(-24), publication.DueAtUtc);
    }

    [Fact]
    public async Task ObsoleteDiscordPublications_AreListedAndMarkedDeleted()
    {
        var now = new DateTimeOffset(2026, 8, 22, 16, 0, 0, TimeSpan.Zero);
        var content = new ContentRecord
        {
            Id = Guid.NewGuid(),
            Identity = "test:expired-event",
            SourceKey = "test",
            Game = GameKey.WutheringWaves,
            Kind = ContentKind.Event,
            Title = "Expired event",
            DocumentJson = "[]",
            DocumentHash = "hash",
            Status = ContentStatus.Active,
            CreatedAtUtc = now.AddDays(-2),
            UpdatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(-1),
        };
        var publication = new PublicationRecord
        {
            Id = Guid.NewGuid(),
            Content = content,
            ContentId = content.Id,
            DestinationGuildId = 101,
            DestinationChannelId = 201,
            DueAtUtc = now.AddDays(-2),
            State = PublicationState.Published,
            ProviderMessageId = "9001",
            CreatedAtUtc = now.AddDays(-2),
            UpdatedAtUtc = now.AddDays(-2),
        };
        _db.ContentItems.Add(content);
        _db.Publications.Add(publication);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var obsolete = await _store.ListForGuildAsync(101, now, TestContext.Current.CancellationToken);
        Assert.Equal(publication.Id, Assert.Single(obsolete).PublicationId);

        await _store.MarkDeletedAsync([publication.Id], now, TestContext.Current.CancellationToken);

        Assert.Equal(PublicationState.Deleted, (await _db.Publications.SingleAsync(TestContext.Current.CancellationToken)).State);
        Assert.Empty(await _store.ListForGuildAsync(101, now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpsertAsync_ScheduleUpcomingQueuesAnActiveEventMissedDuringAnEarlierBaseline()
    {
        var now = new DateTimeOffset(2026, 8, 22, 16, 0, 0, TimeSpan.Zero);
        var store = new ContentStore(_db, new FixedTimeProvider(now));
        var snapshot = Snapshot("Active event") with
        {
            Kind = ContentKind.Event,
            PublishedAtUtc = now.AddDays(-2),
            ExpiresAtUtc = now.AddDays(7),
            PublishAtUtc = now.AddDays(-2),
        };

        await store.UpsertAsync(
            snapshot,
            PublicationDisposition.SuppressBaseline,
            TestContext.Current.CancellationToken);
        await store.UpsertAsync(
            snapshot,
            PublicationDisposition.ScheduleUpcoming,
            TestContext.Current.CancellationToken);

        var publication = await _db.Publications.SingleAsync(
            publication => publication.Purpose == PublicationPurpose.EventStart,
            TestContext.Current.CancellationToken);
        Assert.Equal(now, publication.DueAtUtc);
    }

    [Fact]
    public async Task RepublishToGuildAsync_QueuesOnlyTheSelectedEligibleGuild()
    {
        var destinations = new FixedDestinationStore(
        [
            new GuildDestination(
                101,
                201,
                301,
                true,
                new HashSet<GameKey> { GameKey.WutheringWaves },
                DateTimeOffset.UtcNow,
                "Alpha",
                "updates"),
            new GuildDestination(
                102,
                202,
                302,
                true,
                new HashSet<GameKey> { GameKey.WutheringWaves },
                DateTimeOffset.UtcNow,
                "Beta",
                "news"),
        ]);
        var store = new ContentStore(_db, TimeProvider.System, destinations);
        await store.UpsertAsync(
            Snapshot("Targeted content"),
            PublicationDisposition.SuppressBaseline,
            TestContext.Current.CancellationToken);
        var contentId = await _db.ContentItems.Select(item => item.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        await store.RepublishToGuildAsync(contentId, 101, TestContext.Current.CancellationToken);

        var publication = await _db.Publications.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(101, publication.DestinationGuildId);
        Assert.Equal(201, publication.DestinationChannelId);
        Assert.Equal(PublicationState.Pending, publication.State);
    }

    [Fact]
    public async Task RepublishToGuildAsync_RejectsGuildThatDoesNotObserveTheContentGame()
    {
        var destinations = new FixedDestinationStore(
        [
            new GuildDestination(
                101,
                201,
                301,
                true,
                new HashSet<GameKey> { GameKey.NevernessToEverness },
                DateTimeOffset.UtcNow),
        ]);
        var store = new ContentStore(_db, TimeProvider.System, destinations);
        await store.UpsertAsync(
            Snapshot("Ineligible target"),
            PublicationDisposition.SuppressBaseline,
            TestContext.Current.CancellationToken);
        var contentId = await _db.ContentItems.Select(item => item.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RepublishToGuildAsync(contentId, 101, TestContext.Current.CancellationToken));

        Assert.Contains("does not subscribe", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_db.Publications);
    }

    [Fact]
    public async Task GuildPublicationHistory_ReturnsQueueAndDeliveryStateForOneGuild()
    {
        var destinations = new FixedDestinationStore(
        [
            new GuildDestination(101, 201, 301, true, GuildDestinationGames.All, DateTimeOffset.UtcNow),
        ]);
        var store = new ContentStore(_db, TimeProvider.System, destinations);
        await store.UpsertAsync(
            Snapshot("History item"),
            PublicationDisposition.AutoPublish,
            TestContext.Current.CancellationToken);

        var history = await new GuildPublicationHistoryStore(new SingleSourceDatabaseFactory(_databaseName))
            .ListForGuildAsync(101, 10, TestContext.Current.CancellationToken);
        var pending = await new GuildPublicationHistoryStore(new SingleSourceDatabaseFactory(_databaseName))
            .ListPendingForGuildAsync(101, 10, TestContext.Current.CancellationToken);

        var item = Assert.Single(history);
        Assert.Equal("Patch notes", item.Title);
        Assert.Equal("Pending", item.State);
        Assert.Equal((ulong)201, item.ChannelId);
        Assert.Equal(item, Assert.Single(pending));
    }

    [Fact]
    public async Task UpsertAsync_ChangedDocument_CreatesRevisionAndPublication()
    {
        await _store.UpsertAsync(
            Snapshot("Original"),
            PublicationDisposition.SuppressBaseline,
            TestContext.Current.CancellationToken);

        var outcome = await _store.UpsertAsync(
            Snapshot("Changed"),
            PublicationDisposition.AutoPublish,
            TestContext.Current.CancellationToken);

        Assert.Equal(ContentUpsertOutcome.Updated, outcome);
        Assert.Equal(1, await _db.ContentRevisions.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await _db.Publications.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpsertAsync_OldOfficialContent_IsArchivedInsteadOfQueued()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var store = new ContentStore(
            _db,
            new FixedTimeProvider(now),
            retentionPolicy: new OfficialRetentionPolicy());

        await store.UpsertAsync(
            Snapshot("Old official post", now.AddMonths(-1).AddMinutes(-1)),
            PublicationDisposition.AutoPublish,
            TestContext.Current.CancellationToken);

        var content = await _db.ContentItems.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ContentStatus.Archived, content.Status);
        Assert.Equal(ArchiveReason.Retention, content.ArchiveReason);
        Assert.Empty(_db.Publications);
    }

    [Fact]
    public async Task ArchiveExpiredAsync_ArchivesOldOfficialContentAndCancelsEveryPendingPublication()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var activeStore = new ContentStore(_db, new FixedTimeProvider(now));
        await activeStore.UpsertAsync(
            Snapshot("Previously active", now.AddDays(-1)),
            PublicationDisposition.AutoPublish,
            TestContext.Current.CancellationToken);
        var content = await _db.ContentItems.SingleAsync(TestContext.Current.CancellationToken);
        content.SourcePublishedAtUtc = now.AddMonths(-1).AddMinutes(-1);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var store = new ContentStore(
            _db,
            new FixedTimeProvider(now),
            retentionPolicy: new OfficialRetentionPolicy());
        var archived = await store.ArchiveExpiredAsync(now, TestContext.Current.CancellationToken);

        Assert.Equal(1, archived);
        Assert.Equal(ContentStatus.Archived, content.Status);
        Assert.Equal(ArchiveReason.Retention, content.ArchiveReason);
        Assert.All(_db.Publications, publication => Assert.Equal(PublicationState.Cancelled, publication.State));
    }

    private static SourceContentSnapshot Snapshot(string text, DateTimeOffset? publishedAtUtc = null) => new(
        "official-wuwa",
        "42",
        GameKey.WutheringWaves,
        ContentKind.News,
        "Patch notes",
        new Uri("https://example.com/news/42"),
        ContentDocument.Create([new TextBlock(text, 1)]),
        publishedAtUtc ?? new DateTimeOffset(2026, 8, 13, 8, 0, 0, TimeSpan.Zero),
        null);

    private sealed class FixedDestinationStore(IReadOnlyList<GuildDestination> destinations) : IGuildDestinationStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<GuildDestination>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(destinations);

        public Task<IReadOnlyList<GuildDestination>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GuildDestination>>(
                destinations.Where(destination => destination.IsEnabled).ToArray());

        public Task ConfigureAsync(
            ulong guildId,
            string guildName,
            ulong channelId,
            string channelName,
            ulong configuredByUserId,
            IReadOnlySet<GameKey> games,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetEnabledAsync(ulong guildId, bool enabled, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetEventNotificationOffsetsAsync(
            ulong guildId,
            double startOffsetHours,
            double endOffsetHours,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetDeleteObsoleteMessagesAsync(
            ulong guildId,
            bool enabled,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SetTopicSubscriptionsAsync(
            ulong guildId,
            GameKey game,
            IReadOnlySet<ContentKind> kinds,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task MarkRemovedAsync(ulong guildId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RestoreRemovedAsync(ulong guildId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<GuildDestination>> ListRemovedBeforeAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GuildDestination>>([]);

        public Task<bool> DeleteRemovedAsync(
            ulong guildId,
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> IsAdministratorAsync(ulong userId, CancellationToken cancellationToken) =>
            Task.FromResult(destinations.Any(destination => destination.ConfiguredByUserId == userId));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SingleSourceDatabaseFactory(string databaseName) : ISourceDatabaseFactory
    {
        public IReadOnlyList<string> DatabaseKeys { get; } = ["test"];

        public AppDbContext CreateDbContext(string sourceKey) => new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName)
                .Options);
    }

    private sealed class OfficialRetentionPolicy : IContentRetentionPolicy
    {
        public bool ShouldArchive(
            string sourceKey,
            DateTimeOffset? sourcePublishedAtUtc,
            DateTimeOffset createdAtUtc,
            DateTimeOffset nowUtc) =>
            string.Equals(sourceKey, "official-wuwa", StringComparison.Ordinal) &&
            (sourcePublishedAtUtc ?? createdAtUtc) <= nowUtc.AddMonths(-1);
    }
}
