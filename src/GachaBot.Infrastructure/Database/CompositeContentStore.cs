using GachaBot.Application.Content;
using GachaBot.Application.Ingestion;
using GachaBot.Domain.Content;
using GachaBot.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;

namespace GachaBot.Infrastructure.Database;

public sealed class CompositeContentStore(
    ISourceDatabaseFactory databaseFactory,
    TimeProvider timeProvider,
    GachaBot.Application.Publishing.IGuildDestinationStore guildDestinations,
    IContentRetentionPolicy retentionPolicy)
    : IIngestionSink, ISourceStateStore, ISourceContentLookup, IContentScheduleStore,
        IContentManagementStore, ISourceStateQuery, GachaBot.Application.Publishing.IEventPublicationScheduleStore,
        GachaBot.Application.Publishing.IObsoleteDiscordPublicationStore
{
    public CompositeContentStore(ISourceDatabaseFactory databaseFactory, TimeProvider timeProvider)
        : this(
            databaseFactory,
            timeProvider,
            new LegacyGuildDestinationStore(),
            new OfficialContentRetentionPolicy([]))
    {
    }

    public async Task<bool> ExistsAsync(
        string sourceKey,
        string externalId,
        CancellationToken cancellationToken) => await WithStoreAsync(
        sourceKey,
        store => store.ExistsAsync(sourceKey, externalId, cancellationToken)).ConfigureAwait(false);

    public async Task<bool> NeedsContentRefreshAsync(
        string sourceKey,
        string externalId,
        CancellationToken cancellationToken) => await WithStoreAsync(
        sourceKey,
        store => store.NeedsContentRefreshAsync(sourceKey, externalId, cancellationToken)).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, SourceContentState>> GetContentStatesAsync(
        string sourceKey,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken) => await WithStoreAsync(
        sourceKey,
        store => store.GetContentStatesAsync(sourceKey, externalIds, cancellationToken)).ConfigureAwait(false);

    public async Task<ContentUpsertOutcome> UpsertAsync(
        SourceContentSnapshot snapshot,
        PublicationDisposition disposition,
        CancellationToken cancellationToken) => await WithStoreAsync(
        snapshot.SourceKey,
        store => store.UpsertAsync(snapshot, disposition, cancellationToken)).ConfigureAwait(false);

    public async Task<IReadOnlyList<ContentUpsertOutcome>> UpsertBatchAsync(
        IReadOnlyList<SourceContentSnapshot> snapshots,
        PublicationDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (snapshots.Count == 0)
        {
            return [];
        }

        var sourceKey = snapshots[0].SourceKey;
        if (snapshots.Any(snapshot => !string.Equals(snapshot.SourceKey, sourceKey, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("An ingestion batch must belong to one source.");
        }

        return await WithStoreAsync(
            sourceKey,
            store => store.UpsertBatchAsync(snapshots, disposition, cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> HasCompletedBaselineAsync(
        string sourceKey,
        CancellationToken cancellationToken) => await WithStoreAsync(
        sourceKey,
        store => store.HasCompletedBaselineAsync(sourceKey, cancellationToken)).ConfigureAwait(false);

    public async Task MarkSucceededAsync(
        string sourceKey,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) => await WithStoreAsync(
        sourceKey,
        store => store.MarkSucceededAsync(sourceKey, completedAtUtc, cancellationToken)).ConfigureAwait(false);

    public async Task MarkFailedAsync(
        string sourceKey,
        DateTimeOffset attemptedAtUtc,
        string failureMessage,
        CancellationToken cancellationToken) => await WithStoreAsync(
        sourceKey,
        store => store.MarkFailedAsync(sourceKey, attemptedAtUtc, failureMessage, cancellationToken)).ConfigureAwait(false);

    public async Task<SourceStateSnapshot?> GetSourceStateAsync(
        string sourceKey,
        CancellationToken cancellationToken)
    {
        await using var context = databaseFactory.CreateDbContext(sourceKey);
        return await context.SourceStates.AsNoTracking()
            .Where(state => state.SourceKey == sourceKey)
            .Select(state => new SourceStateSnapshot(
                state.SourceKey,
                state.HasCompletedBaseline,
                state.LastSuccessfulRunUtc,
                state.LastAttemptUtc,
                state.LastFailureMessage))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ScheduleAsync(
        Guid contentId,
        DateTimeOffset publishAtUtc,
        CancellationToken cancellationToken) => await WithContentStoreAsync(
        contentId,
        store => store.ScheduleAsync(contentId, publishAtUtc, cancellationToken),
        cancellationToken).ConfigureAwait(false);

    public async Task<DashboardMetrics> GetMetricsAsync(CancellationToken cancellationToken)
    {
        var metrics = new List<DashboardMetrics>();
        foreach (var key in databaseFactory.DatabaseKeys)
        {
            metrics.Add(await WithStoreAsync(
                key,
                store => store.GetMetricsAsync(cancellationToken)).ConfigureAwait(false));
        }

        return new DashboardMetrics(
            metrics.Sum(metric => metric.Active),
            metrics.Sum(metric => metric.Scheduled),
            metrics.Sum(metric => metric.AwaitingReview),
            metrics.Sum(metric => metric.Archived),
            metrics.Max(metric => metric.LastSuccessfulImportUtc));
    }

    public async Task<IReadOnlyList<ContentListItem>> ListAsync(
        ContentStatus? status,
        string? sourceKey,
        CancellationToken cancellationToken)
    {
        var keys = string.IsNullOrWhiteSpace(sourceKey)
            ? databaseFactory.DatabaseKeys
            : [sourceKey];
        var items = new List<ContentListItem>();
        foreach (var key in keys)
        {
            items.AddRange(await WithStoreAsync(
                key,
                store => store.ListAsync(status, sourceKey, cancellationToken)).ConfigureAwait(false));
        }

        return items.OrderByDescending(item => item.UpdatedAtUtc).ToArray();
    }

    public async Task<ContentDetails?> GetAsync(Guid contentId, CancellationToken cancellationToken)
    {
        foreach (var key in databaseFactory.DatabaseKeys)
        {
            var details = await WithStoreAsync(
                key,
                store => store.GetAsync(contentId, cancellationToken)).ConfigureAwait(false);
            if (details is not null)
            {
                return details;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<ContentDetails>> ListEventsEndingAfterAsync(
        DateTimeOffset endsAfterUtc,
        CancellationToken cancellationToken)
    {
        var events = new List<ContentDetails>();
        foreach (var key in databaseFactory.DatabaseKeys)
        {
            events.AddRange(await WithStoreAsync(
                key,
                store => store.ListEventsEndingAfterAsync(endsAfterUtc, cancellationToken)).ConfigureAwait(false));
        }

        return events;
    }

    public async Task<Guid> CreateManualAsync(
        CreateManualContentCommand command,
        CancellationToken cancellationToken) => await WithStoreAsync(
        SourceDatabaseFactory.ManualDatabaseKey,
        store => store.CreateManualAsync(command, cancellationToken)).ConfigureAwait(false);

    public async Task ArchiveAsync(Guid contentId, CancellationToken cancellationToken) =>
        await WithContentStoreAsync(
            contentId,
            store => store.ArchiveAsync(contentId, cancellationToken),
            cancellationToken).ConfigureAwait(false);

    public Task RestoreAsync(Guid contentId, CancellationToken cancellationToken) =>
        WithContentStoreAsync(contentId, store => store.RestoreAsync(contentId, cancellationToken), cancellationToken);

    public Task RepublishAsync(Guid contentId, CancellationToken cancellationToken) =>
        WithContentStoreAsync(contentId, store => store.RepublishAsync(contentId, cancellationToken), cancellationToken);

    public Task RepublishToGuildAsync(
        Guid contentId,
        ulong guildId,
        CancellationToken cancellationToken) =>
        WithContentStoreAsync(
            contentId,
            store => store.RepublishToGuildAsync(contentId, guildId, cancellationToken),
            cancellationToken);

    public async Task<IReadOnlyList<PublishedDiscordMessage>> GetPublishedMessageIdsAsync(
        Guid contentId,
        CancellationToken cancellationToken)
    {
        foreach (var key in databaseFactory.DatabaseKeys)
        {
            await using var context = databaseFactory.CreateDbContext(key);
            if (await context.ContentItems.AnyAsync(item => item.Id == contentId, cancellationToken)
                    .ConfigureAwait(false))
            {
                return await new ContentStore(context, timeProvider, guildDestinations, retentionPolicy)
                    .GetPublishedMessageIdsAsync(contentId, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new KeyNotFoundException($"Content '{contentId}' was not found.");
    }

    public Task DeleteAsync(Guid contentId, CancellationToken cancellationToken) =>
        WithContentStoreAsync(contentId, store => store.DeleteAsync(contentId, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<GachaBot.Application.Publishing.ObsoleteDiscordPublication>> ListForGuildAsync(
        ulong guildId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var publications = new List<GachaBot.Application.Publishing.ObsoleteDiscordPublication>();
        foreach (var key in databaseFactory.DatabaseKeys)
        {
            publications.AddRange(await WithStoreAsync(
                key,
                store => store.ListForGuildAsync(guildId, nowUtc, cancellationToken)).ConfigureAwait(false));
        }

        return publications;
    }

    public async Task MarkDeletedAsync(
        IReadOnlyCollection<Guid> publicationIds,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken)
    {
        foreach (var key in databaseFactory.DatabaseKeys)
        {
            await WithStoreAsync(
                key,
                store => store.MarkDeletedAsync(publicationIds, deletedAtUtc, cancellationToken)).ConfigureAwait(false);
        }
    }

    public async Task ApproveAsync(Guid contentId, CancellationToken cancellationToken) =>
        await WithContentStoreAsync(
            contentId,
            store => store.ApproveAsync(contentId, cancellationToken),
            cancellationToken).ConfigureAwait(false);

    public async Task<int> ArchiveExpiredAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var key in databaseFactory.DatabaseKeys)
        {
            count += await WithStoreAsync(
                key,
                store => store.ArchiveExpiredAsync(nowUtc, cancellationToken)).ConfigureAwait(false);
        }

        return count;
    }

    public async Task ReconcileForGuildAsync(ulong guildId, CancellationToken cancellationToken)
    {
        foreach (var key in databaseFactory.DatabaseKeys)
        {
            await WithStoreAsync(
                key,
                store => store.ReconcileEventPublicationsForGuildAsync(guildId, cancellationToken)).ConfigureAwait(false);
        }
    }

    private async Task<TResult> WithStoreAsync<TResult>(
        string sourceKey,
        Func<ContentStore, Task<TResult>> operation)
    {
        await using var context = databaseFactory.CreateDbContext(sourceKey);
        return await operation(new ContentStore(context, timeProvider, guildDestinations, retentionPolicy)).ConfigureAwait(false);
    }

    private async Task WithStoreAsync(
        string sourceKey,
        Func<ContentStore, Task> operation)
    {
        await using var context = databaseFactory.CreateDbContext(sourceKey);
        await operation(new ContentStore(context, timeProvider, guildDestinations, retentionPolicy)).ConfigureAwait(false);
    }

    private async Task WithContentStoreAsync(
        Guid contentId,
        Func<ContentStore, Task> operation,
        CancellationToken cancellationToken)
    {
        foreach (var key in databaseFactory.DatabaseKeys)
        {
            await using var context = databaseFactory.CreateDbContext(key);
            if (!await context.ContentItems.AnyAsync(
                    item => item.Id == contentId,
                    cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await operation(new ContentStore(context, timeProvider, guildDestinations, retentionPolicy)).ConfigureAwait(false);
            return;
        }

        throw new KeyNotFoundException($"Content '{contentId}' was not found.");
    }
}
