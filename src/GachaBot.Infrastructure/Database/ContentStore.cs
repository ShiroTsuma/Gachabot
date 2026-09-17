using GachaBot.Application.Content;
using GachaBot.Application.Ingestion;
using GachaBot.Application.Publishing;
using GachaBot.Domain.Content;
using GachaBot.Infrastructure.Sources;
using Microsoft.EntityFrameworkCore;

namespace GachaBot.Infrastructure.Database;

public sealed class ContentStore(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    GachaBot.Application.Publishing.IGuildDestinationStore? guildDestinations = null,
    IContentRetentionPolicy? retentionPolicy = null)
    : IIngestionSink, ISourceStateStore, ISourceContentLookup, IContentScheduleStore, IContentManagementStore,
        IObsoleteDiscordPublicationStore
{
    private const int LookupChunkSize = 500;

    public Task<bool> ExistsAsync(
        string sourceKey,
        string externalId,
        CancellationToken cancellationToken) => dbContext.ContentItems.AnyAsync(
            item => item.SourceKey == sourceKey && item.ExternalId == externalId,
            cancellationToken);

    public async Task<bool> NeedsContentRefreshAsync(
        string sourceKey,
        string externalId,
        CancellationToken cancellationToken)
    {
        var documentJson = await dbContext.ContentItems
            .Where(item => item.SourceKey == sourceKey && item.ExternalId == externalId)
            .Select(item => item.DocumentJson)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (documentJson is null)
        {
            return true;
        }

        return NeedsContentRefresh(documentJson);
    }

    public async Task<IReadOnlyDictionary<string, SourceContentState>> GetContentStatesAsync(
        string sourceKey,
        IReadOnlyCollection<string> externalIds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(externalIds);
        var states = new Dictionary<string, SourceContentState>(StringComparer.Ordinal);
        foreach (var chunk in externalIds.Distinct(StringComparer.Ordinal).Chunk(LookupChunkSize))
        {
            var rows = await dbContext.ContentItems
                .AsNoTracking()
                .Where(item =>
                    item.SourceKey == sourceKey &&
                    item.ExternalId != null &&
                    chunk.Contains(item.ExternalId))
                .Select(item => new { item.ExternalId, item.DocumentJson })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in rows)
            {
                states[row.ExternalId!] = new SourceContentState(
                    true,
                    NeedsContentRefresh(row.DocumentJson));
            }
        }

        return states;
    }

    public async Task<ContentUpsertOutcome> UpsertAsync(
        SourceContentSnapshot snapshot,
        PublicationDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var existing = await dbContext.ContentItems
            .SingleOrDefaultAsync(item => item.Identity == snapshot.Identity, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null &&
            string.Equals(snapshot.ExternalId, "aggregate:permanent", StringComparison.Ordinal))
        {
            existing = await dbContext.ContentItems
                .SingleOrDefaultAsync(
                    item => item.SourceKey == snapshot.SourceKey && item.ExternalId == "active-codes",
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                existing.Identity = snapshot.Identity;
                existing.ExternalId = snapshot.ExternalId;
            }
        }

        var now = timeProvider.GetUtcNow();
        var isExplicitlyExpired = snapshot.ExpiresAtUtc.HasValue && snapshot.ExpiresAtUtc.Value < now;
        var isRetentionExpired = IsRetentionExpired(snapshot, now);
        var isAlreadyExpired = isExplicitlyExpired || isRetentionExpired;
        var archiveReason = isExplicitlyExpired ? ArchiveReason.Expired : ArchiveReason.Retention;
        var publicationDueAtUtc = snapshot.PublishAtUtc is { } requestedDueAt && requestedDueAt > now
            ? requestedDueAt
            : now;
        // Timeline imports publish a newly discovered event once it is active;
        // they only wait when the announced start is still in the future.
        var shouldQueuePublication = disposition == PublicationDisposition.AutoPublish ||
            (disposition == PublicationDisposition.ScheduleUpcoming && !isAlreadyExpired);
        var shouldSchedulePublication = shouldQueuePublication && publicationDueAtUtc > now;
        var documentJson = ContentDocumentJson.Serialize(snapshot.Document);
        var archivedSuperseded = await ArchiveSupersededSourceItemsAsync(
            snapshot,
            now,
            cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            var created = new ContentRecord
            {
                Id = Guid.NewGuid(),
                Identity = snapshot.Identity,
                SourceKey = snapshot.SourceKey,
                ExternalId = snapshot.ExternalId,
                Game = snapshot.Game,
                Kind = snapshot.Kind,
                Title = snapshot.Title,
                SourceUrl = snapshot.SourceUrl.AbsoluteUri,
                DocumentJson = documentJson,
                DocumentHash = snapshot.Document.Hash,
                Status = isAlreadyExpired
                    ? ContentStatus.Archived
                    : disposition == PublicationDisposition.AwaitReview
                        ? ContentStatus.Draft
                        : shouldSchedulePublication
                            ? ContentStatus.Scheduled
                        : ContentStatus.Active,
                AwaitingReview = !isAlreadyExpired && disposition == PublicationDisposition.AwaitReview,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                SourcePublishedAtUtc = snapshot.PublishedAtUtc,
                ExpiresAtUtc = snapshot.ExpiresAtUtc,
                ArchivedAtUtc = isAlreadyExpired ? now : null,
                ArchiveReason = isAlreadyExpired ? archiveReason : null,
            };
            dbContext.ContentItems.Add(created);
            if (!isAlreadyExpired && shouldQueuePublication)
            {
                created.ScheduledAtUtc = publicationDueAtUtc;
                if (disposition == PublicationDisposition.ScheduleUpcoming &&
                    await AddEventPublicationsAsync(created, cancellationToken).ConfigureAwait(false))
                {
                    created.Status = created.ScheduledAtUtc > now
                        ? ContentStatus.Scheduled
                        : ContentStatus.Active;
                }
                else
                {
                    await AddPublicationsAsync(created, publicationDueAtUtc, cancellationToken).ConfigureAwait(false);
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ContentUpsertOutcome.Created;
        }

        if (existing.DocumentHash == snapshot.Document.Hash &&
            string.Equals(existing.Title, snapshot.Title, StringComparison.Ordinal))
        {
            if (isAlreadyExpired && existing.Status != ContentStatus.Archived)
            {
                existing.Status = ContentStatus.Archived;
                existing.ArchivedAtUtc = now;
                existing.ArchiveReason = archiveReason;
                existing.AwaitingReview = false;
                existing.UpdatedAtUtc = now;
                await CancelPendingPublicationsAsync([existing.Id], now, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (!isAlreadyExpired &&
                      existing.Status == ContentStatus.Archived &&
                      existing.ArchiveReason != ArchiveReason.Manual)
            {
                existing.Status = disposition == PublicationDisposition.AwaitReview
                    ? ContentStatus.Draft
                    : ContentStatus.Active;
                existing.ArchivedAtUtc = null;
                existing.ArchiveReason = null;
                existing.AwaitingReview = disposition == PublicationDisposition.AwaitReview;
                existing.ExpiresAtUtc = snapshot.ExpiresAtUtc;
                existing.UpdatedAtUtc = now;
            }
            else if (!isAlreadyExpired &&
                     existing.AwaitingReview &&
                     disposition == PublicationDisposition.AutoPublish &&
                     !IsManualArchive(existing))
            {
                existing.AwaitingReview = false;
                if (existing.Status != ContentStatus.Published)
                {
                    existing.Status = ContentStatus.Active;
                }

                existing.ScheduledAtUtc = publicationDueAtUtc;
                existing.UpdatedAtUtc = now;
                await AddMissingPublicationsAsync(existing, publicationDueAtUtc, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!isAlreadyExpired &&
                !IsManualArchive(existing) &&
                disposition == PublicationDisposition.ScheduleUpcoming &&
                await AddMissingPublicationsAsync(existing, publicationDueAtUtc, cancellationToken)
                    .ConfigureAwait(false))
            {
                if (existing.Status != ContentStatus.Published)
                {
                    existing.Status = shouldSchedulePublication ? ContentStatus.Scheduled : ContentStatus.Active;
                }

                existing.ScheduledAtUtc = publicationDueAtUtc;
                existing.UpdatedAtUtc = now;
            }

            if (archivedSuperseded > 0 || dbContext.ChangeTracker.HasChanges())
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return ContentUpsertOutcome.Unchanged;
        }

        if (string.Equals(existing.Title, snapshot.Title, StringComparison.Ordinal) &&
            (IsLegacyFeedImageOnlyChange(existing.DocumentJson, snapshot.Document) ||
             IsLegacyTruncatedContentRepair(existing.DocumentJson, snapshot.Document)))
        {
            existing.DocumentJson = documentJson;
            existing.DocumentHash = snapshot.Document.Hash;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ContentUpsertOutcome.Unchanged;
        }

        dbContext.ContentRevisions.Add(new ContentRevisionRecord
        {
            Id = Guid.NewGuid(),
            ContentId = existing.Id,
            PreviousTitle = existing.Title,
            PreviousDocumentJson = existing.DocumentJson,
            PreviousDocumentHash = existing.DocumentHash,
            ChangedAtUtc = now,
        });
        existing.Title = snapshot.Title;
        existing.DocumentJson = documentJson;
        existing.DocumentHash = snapshot.Document.Hash;
        existing.UpdatedAtUtc = now;
        existing.SourcePublishedAtUtc = snapshot.PublishedAtUtc;
        existing.ExpiresAtUtc = snapshot.ExpiresAtUtc;
        var preserveManualArchive = IsManualArchive(existing);
        existing.AwaitingReview = !preserveManualArchive &&
            !isAlreadyExpired &&
            disposition == PublicationDisposition.AwaitReview;
        if (preserveManualArchive)
        {
            existing.Status = ContentStatus.Archived;
        }
        else if (isAlreadyExpired)
        {
            existing.Status = ContentStatus.Archived;
            existing.ArchivedAtUtc = now;
            existing.ArchiveReason = archiveReason;
            await CancelPendingPublicationsAsync([existing.Id], now, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (shouldQueuePublication)
        {
            existing.Status = shouldSchedulePublication ? ContentStatus.Scheduled : ContentStatus.Active;
            existing.ArchivedAtUtc = null;
            existing.ArchiveReason = null;
            existing.ScheduledAtUtc = publicationDueAtUtc;
            if (snapshot.PublishAtUtc.HasValue)
            {
                await CancelPendingPublicationsAsync([existing.Id], now, cancellationToken).ConfigureAwait(false);
            }

            if (disposition == PublicationDisposition.ScheduleUpcoming &&
                await AddEventPublicationsAsync(existing, cancellationToken).ConfigureAwait(false))
            {
                existing.Status = existing.ScheduledAtUtc > now
                    ? ContentStatus.Scheduled
                    : ContentStatus.Active;
            }
            else
            {
                await AddPublicationsAsync(existing, publicationDueAtUtc, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            existing.Status = disposition == PublicationDisposition.AwaitReview
                ? ContentStatus.Draft
                : ContentStatus.Active;
            existing.ArchivedAtUtc = null;
            existing.ArchiveReason = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ContentUpsertOutcome.Updated;
    }

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

        var identities = snapshots.Select(snapshot => snapshot.Identity).ToArray();
        if (identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
        {
            throw new InvalidOperationException("An ingestion batch cannot contain duplicate identities.");
        }

        var existingItems = await dbContext.ContentItems
            .Where(item => identities.Contains(item.Identity))
            .ToDictionaryAsync(item => item.Identity, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        var outcomes = new ContentUpsertOutcome[snapshots.Count];
        var baselineCreates = new List<(int Index, SourceContentSnapshot Snapshot)>();
        var hasTrackedChanges = false;
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            if (disposition == PublicationDisposition.SuppressBaseline &&
                !existingItems.ContainsKey(snapshot.Identity) &&
                !snapshot.ReplacesSourceItems)
            {
                baselineCreates.Add((index, snapshot));
                continue;
            }

            if (!snapshot.ReplacesSourceItems &&
                snapshot.ExpiresAtUtc is null &&
                !string.Equals(snapshot.ExternalId, "aggregate:permanent", StringComparison.Ordinal))
            {
                outcomes[index] = await UpsertStandardInMemoryAsync(
                    snapshot,
                    disposition,
                    existingItems.GetValueOrDefault(snapshot.Identity),
                    cancellationToken).ConfigureAwait(false);
                hasTrackedChanges |= outcomes[index] != ContentUpsertOutcome.Unchanged ||
                    dbContext.ChangeTracker.HasChanges();
            }
            else
            {
                outcomes[index] = await UpsertAsync(snapshot, disposition, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (hasTrackedChanges)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (baselineCreates.Count > 0)
        {
            await InsertBaselineAsync(
                    baselineCreates.Select(item => item.Snapshot).ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in baselineCreates)
            {
                outcomes[item.Index] = ContentUpsertOutcome.Created;
            }
        }

        return outcomes;
    }

    private async Task<ContentUpsertOutcome> UpsertStandardInMemoryAsync(
        SourceContentSnapshot snapshot,
        PublicationDisposition disposition,
        ContentRecord? existing,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var isRetentionExpired = IsRetentionExpired(snapshot, now);
        var documentJson = ContentDocumentJson.Serialize(snapshot.Document);
        if (existing is null)
        {
            var created = new ContentRecord
            {
                Id = Guid.NewGuid(),
                Identity = snapshot.Identity,
                SourceKey = snapshot.SourceKey,
                ExternalId = snapshot.ExternalId,
                Game = snapshot.Game,
                Kind = snapshot.Kind,
                Title = snapshot.Title,
                SourceUrl = snapshot.SourceUrl.AbsoluteUri,
                DocumentJson = documentJson,
                DocumentHash = snapshot.Document.Hash,
                Status = isRetentionExpired
                    ? ContentStatus.Archived
                    : disposition == PublicationDisposition.AwaitReview
                        ? ContentStatus.Draft
                        : ContentStatus.Active,
                AwaitingReview = !isRetentionExpired && disposition == PublicationDisposition.AwaitReview,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                SourcePublishedAtUtc = snapshot.PublishedAtUtc,
                ArchivedAtUtc = isRetentionExpired ? now : null,
                ArchiveReason = isRetentionExpired ? ArchiveReason.Retention : null,
            };
            dbContext.ContentItems.Add(created);
            if (!isRetentionExpired && disposition == PublicationDisposition.AutoPublish)
            {
                await AddPublicationsAsync(created, now, cancellationToken).ConfigureAwait(false);
            }

            return ContentUpsertOutcome.Created;
        }

        if (existing.DocumentHash == snapshot.Document.Hash &&
            string.Equals(existing.Title, snapshot.Title, StringComparison.Ordinal))
        {
            if (isRetentionExpired && existing.Status != ContentStatus.Archived)
            {
                existing.Status = ContentStatus.Archived;
                existing.ArchivedAtUtc = now;
                existing.ArchiveReason = ArchiveReason.Retention;
                existing.AwaitingReview = false;
                existing.UpdatedAtUtc = now;
                await CancelPendingPublicationsAsync([existing.Id], now, cancellationToken).ConfigureAwait(false);
            }
            else if (!isRetentionExpired && existing.Status == ContentStatus.Archived &&
                existing.ArchiveReason != ArchiveReason.Manual)
            {
                existing.Status = disposition == PublicationDisposition.AwaitReview
                    ? ContentStatus.Draft
                    : ContentStatus.Active;
                existing.ArchivedAtUtc = null;
                existing.ArchiveReason = null;
                existing.AwaitingReview = disposition == PublicationDisposition.AwaitReview;
                existing.UpdatedAtUtc = now;
            }
            else if (!isRetentionExpired &&
                     existing.AwaitingReview &&
                     disposition == PublicationDisposition.AutoPublish &&
                     !IsManualArchive(existing))
            {
                existing.AwaitingReview = false;
                if (existing.Status != ContentStatus.Published)
                {
                    existing.Status = ContentStatus.Active;
                }

                existing.UpdatedAtUtc = now;
                await AddMissingPublicationsAsync(existing, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            return ContentUpsertOutcome.Unchanged;
        }

        if (string.Equals(existing.Title, snapshot.Title, StringComparison.Ordinal) &&
            (IsLegacyFeedImageOnlyChange(existing.DocumentJson, snapshot.Document) ||
             IsLegacyTruncatedContentRepair(existing.DocumentJson, snapshot.Document)))
        {
            existing.DocumentJson = documentJson;
            existing.DocumentHash = snapshot.Document.Hash;
            return ContentUpsertOutcome.Unchanged;
        }

        dbContext.ContentRevisions.Add(new ContentRevisionRecord
        {
            Id = Guid.NewGuid(),
            ContentId = existing.Id,
            PreviousTitle = existing.Title,
            PreviousDocumentJson = existing.DocumentJson,
            PreviousDocumentHash = existing.DocumentHash,
            ChangedAtUtc = now,
        });
        existing.Title = snapshot.Title;
        existing.DocumentJson = documentJson;
        existing.DocumentHash = snapshot.Document.Hash;
        existing.UpdatedAtUtc = now;
        existing.SourcePublishedAtUtc = snapshot.PublishedAtUtc;
        var preserveManualArchive = IsManualArchive(existing);
        existing.AwaitingReview = !preserveManualArchive &&
            !isRetentionExpired &&
            disposition == PublicationDisposition.AwaitReview;
        if (preserveManualArchive)
        {
            existing.Status = ContentStatus.Archived;
        }
        else if (isRetentionExpired)
        {
            existing.Status = ContentStatus.Archived;
            existing.ArchivedAtUtc = now;
            existing.ArchiveReason = ArchiveReason.Retention;
            await CancelPendingPublicationsAsync([existing.Id], now, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            existing.Status = disposition == PublicationDisposition.AwaitReview
                ? ContentStatus.Draft
                : ContentStatus.Active;
            existing.ArchivedAtUtc = null;
            existing.ArchiveReason = null;
        }

        if (!preserveManualArchive && disposition == PublicationDisposition.AutoPublish)
        {
            await AddPublicationsAsync(existing, now, cancellationToken).ConfigureAwait(false);
        }

        return ContentUpsertOutcome.Updated;
    }

    public async Task<bool> HasCompletedBaselineAsync(
        string sourceKey,
        CancellationToken cancellationToken) =>
        await dbContext.SourceStates
            .Where(state => state.SourceKey == sourceKey)
            .Select(state => state.HasCompletedBaseline)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task MarkSucceededAsync(
        string sourceKey,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        var state = await dbContext.SourceStates
            .SingleOrDefaultAsync(item => item.SourceKey == sourceKey, cancellationToken)
            .ConfigureAwait(false);
        if (state is null)
        {
            dbContext.SourceStates.Add(new SourceStateRecord
            {
                SourceKey = sourceKey,
                HasCompletedBaseline = true,
                LastSuccessfulRunUtc = completedAtUtc,
                LastAttemptUtc = completedAtUtc,
            });
        }
        else
        {
            state.HasCompletedBaseline = true;
            state.LastSuccessfulRunUtc = completedAtUtc;
            state.LastAttemptUtc = completedAtUtc;
            state.LastFailureMessage = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(
        string sourceKey,
        DateTimeOffset attemptedAtUtc,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var state = await dbContext.SourceStates
            .SingleOrDefaultAsync(item => item.SourceKey == sourceKey, cancellationToken)
            .ConfigureAwait(false);
        var normalizedFailure = failureMessage.Length <= 2_000
            ? failureMessage
            : failureMessage[..2_000];
        if (state is null)
        {
            dbContext.SourceStates.Add(new SourceStateRecord
            {
                SourceKey = sourceKey,
                HasCompletedBaseline = false,
                LastAttemptUtc = attemptedAtUtc,
                LastFailureMessage = normalizedFailure,
            });
        }
        else
        {
            state.LastAttemptUtc = attemptedAtUtc;
            state.LastFailureMessage = normalizedFailure;
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ScheduleAsync(
        Guid contentId,
        DateTimeOffset publishAtUtc,
        CancellationToken cancellationToken)
    {
        var content = await dbContext.ContentItems
            .SingleOrDefaultAsync(item => item.Id == contentId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Content '{contentId}' was not found.");
        if (content.Status == ContentStatus.Archived)
        {
            throw new InvalidOperationException("Archived content cannot be scheduled.");
        }

        var now = timeProvider.GetUtcNow();
        content.Status = ContentStatus.Scheduled;
        content.ScheduledAtUtc = publishAtUtc.ToUniversalTime();
        content.UpdatedAtUtc = now;
        content.AwaitingReview = false;
        await CancelPendingPublicationsAsync([contentId], now, cancellationToken).ConfigureAwait(false);
        await AddPublicationsAsync(content, content.ScheduledAtUtc.Value, cancellationToken).ConfigureAwait(false);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DashboardMetrics> GetMetricsAsync(CancellationToken cancellationToken)
    {
        var active = await dbContext.ContentItems.CountAsync(
            content => content.Status == ContentStatus.Active || content.Status == ContentStatus.Published,
            cancellationToken).ConfigureAwait(false);
        var scheduled = await dbContext.ContentItems.CountAsync(
            content => content.Status == ContentStatus.Scheduled,
            cancellationToken).ConfigureAwait(false);
        var review = await dbContext.ContentItems.CountAsync(
            content => content.AwaitingReview,
            cancellationToken).ConfigureAwait(false);
        var archived = await dbContext.ContentItems.CountAsync(
            content => content.Status == ContentStatus.Archived,
            cancellationToken).ConfigureAwait(false);
        var lastImport = await dbContext.SourceStates
            .MaxAsync(state => state.LastSuccessfulRunUtc, cancellationToken)
            .ConfigureAwait(false);
        return new DashboardMetrics(active, scheduled, review, archived, lastImport);
    }

    public async Task<IReadOnlyList<ContentListItem>> ListAsync(
        ContentStatus? status,
        string? sourceKey,
        CancellationToken cancellationToken)
    {
        var query = dbContext.ContentItems.AsNoTracking();
        if (status.HasValue)
        {
            query = query.Where(content => content.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            query = query.Where(content => content.SourceKey == sourceKey);
        }

        return await query
            .OrderByDescending(content => content.UpdatedAtUtc)
            .Select(content => new ContentListItem(
                content.Id,
                content.SourceKey,
                content.Game,
                content.Kind,
                content.Title,
                content.Status,
                content.ArchiveReason,
                content.AwaitingReview,
                content.SourcePublishedAtUtc,
                content.UpdatedAtUtc,
                content.ScheduledAtUtc,
                content.SourceUrl == null ? null : new Uri(content.SourceUrl)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ContentDetails?> GetAsync(Guid contentId, CancellationToken cancellationToken)
    {
        var content = await dbContext.ContentItems
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == contentId, cancellationToken)
            .ConfigureAwait(false);
        return content is null
            ? null
            : new ContentDetails(
                content.Id,
                content.SourceKey,
                content.Game,
                content.Kind,
                content.Title,
                content.Status,
                content.ArchiveReason,
                content.AwaitingReview,
                ContentDocumentJson.Deserialize(content.DocumentJson),
                content.SourceUrl is null ? null : new Uri(content.SourceUrl),
                content.CreatedAtUtc,
                content.UpdatedAtUtc,
                content.SourcePublishedAtUtc,
                content.ScheduledAtUtc,
                content.PublishedAtUtc,
                content.ExpiresAtUtc);
    }

    public async Task<Guid> CreateManualAsync(
        CreateManualContentCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var now = timeProvider.GetUtcNow();
        var content = new ContentRecord
        {
            Id = Guid.NewGuid(),
            Identity = $"manual:{Guid.NewGuid():N}",
            SourceKey = "manual",
            Game = command.Game,
            Kind = command.Kind,
            Title = command.Title.Trim(),
            DocumentJson = ContentDocumentJson.Serialize(command.Document),
            DocumentHash = command.Document.Hash,
            Status = command.PublishAtUtc.HasValue ? ContentStatus.Scheduled : ContentStatus.Draft,
            ScheduledAtUtc = command.PublishAtUtc?.ToUniversalTime(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        dbContext.ContentItems.Add(content);
        if (content.ScheduledAtUtc.HasValue)
        {
            await AddPublicationsAsync(content, content.ScheduledAtUtc.Value, cancellationToken).ConfigureAwait(false);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return content.Id;
    }

    public async Task ArchiveAsync(Guid contentId, CancellationToken cancellationToken)
    {
        var content = await dbContext.ContentItems
            .SingleOrDefaultAsync(item => item.Id == contentId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Content '{contentId}' was not found.");
        var now = timeProvider.GetUtcNow();
        content.Status = ContentStatus.Archived;
        content.ArchivedAtUtc = now;
        content.ArchiveReason = ArchiveReason.Manual;
        content.AwaitingReview = false;
        content.UpdatedAtUtc = now;
        await CancelPendingPublicationsAsync([contentId], now, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreAsync(Guid contentId, CancellationToken cancellationToken)
    {
        var content = await dbContext.ContentItems.SingleOrDefaultAsync(
            item => item.Id == contentId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Content '{contentId}' was not found.");
        if (content.Status != ContentStatus.Archived)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        content.Status = ContentStatus.Active;
        content.ArchivedAtUtc = null;
        content.ArchiveReason = null;
        content.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RepublishAsync(Guid contentId, CancellationToken cancellationToken)
    {
        var content = await dbContext.ContentItems.SingleOrDefaultAsync(
            item => item.Id == contentId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Content '{contentId}' was not found.");
        if (content.Status == ContentStatus.Archived)
        {
            throw new InvalidOperationException("Archived content must be restored before republishing.");
        }

        var now = timeProvider.GetUtcNow();
        var hasPendingPublication = await dbContext.Publications.AnyAsync(
            publication => publication.ContentId == contentId &&
                (publication.State == PublicationState.Pending || publication.State == PublicationState.Processing),
            cancellationToken).ConfigureAwait(false);
        if (!hasPendingPublication)
        {
            await AddPublicationsAsync(content, now, cancellationToken).ConfigureAwait(false);
        }

        content.Status = ContentStatus.Scheduled;
        content.ScheduledAtUtc = now;
        content.AwaitingReview = false;
        content.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RepublishToGuildAsync(
        Guid contentId,
        ulong guildId,
        CancellationToken cancellationToken)
    {
        var content = await dbContext.ContentItems.SingleOrDefaultAsync(
            item => item.Id == contentId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Content '{contentId}' was not found.");
        if (content.Status == ContentStatus.Archived)
        {
            throw new InvalidOperationException("Archived content must be restored before republishing.");
        }

        var destinationStore = guildDestinations
            ?? throw new InvalidOperationException("Guild configuration is unavailable.");
        var destination = (await destinationStore.ListActiveAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.GuildId == guildId && item.SubscribesTo(content.Game, content.Kind))
            ?? throw new InvalidOperationException(
                "The selected guild is not active or does not subscribe to this content's game and subject.");
        var now = timeProvider.GetUtcNow();
        var hasPendingPublication = await dbContext.Publications.AnyAsync(
            publication =>
                publication.ContentId == contentId &&
                publication.DestinationGuildId == checked((long)destination.GuildId) &&
                publication.DestinationChannelId == checked((long)destination.ChannelId) &&
                (publication.State == PublicationState.Pending || publication.State == PublicationState.Processing),
            cancellationToken).ConfigureAwait(false);
        if (!hasPendingPublication)
        {
            AddPublication(content, destination, now, now);
        }

        content.Status = ContentStatus.Scheduled;
        content.ScheduledAtUtc = now;
        content.AwaitingReview = false;
        content.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PublishedDiscordMessage>> GetPublishedMessageIdsAsync(
        Guid contentId,
        CancellationToken cancellationToken)
    {
        var publications = await dbContext.Publications.AsNoTracking()
            .Where(publication =>
                publication.ContentId == contentId &&
                publication.State == PublicationState.Published &&
                publication.ProviderMessageId != null &&
                publication.DestinationGuildId != null &&
                publication.DestinationChannelId != null)
            .Select(publication => new
            {
                publication.DestinationGuildId,
                publication.DestinationChannelId,
                publication.ProviderMessageId,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return publications.Select(publication => new PublishedDiscordMessage(
            checked((ulong)publication.DestinationGuildId!.Value),
            checked((ulong)publication.DestinationChannelId!.Value),
            publication.ProviderMessageId!)).ToArray();
    }

    public async Task<IReadOnlyList<ObsoleteDiscordPublication>> ListForGuildAsync(
        ulong guildId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (guildId == 0 || guildId > long.MaxValue)
        {
            return [];
        }

        var publications = await dbContext.Publications.AsNoTracking()
            .Where(publication =>
                publication.DestinationGuildId == checked((long)guildId) &&
                publication.DestinationChannelId != null &&
                publication.ProviderMessageId != null &&
                publication.State == PublicationState.Published &&
                (publication.Content.Status == ContentStatus.Archived ||
                 (publication.Content.ExpiresAtUtc != null && publication.Content.ExpiresAtUtc <= nowUtc)))
            .Select(publication => new ObsoleteDiscordPublication(
                publication.Id,
                checked((ulong)publication.DestinationGuildId!.Value),
                checked((ulong)publication.DestinationChannelId!.Value),
                publication.ProviderMessageId!))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return publications;
    }

    public async Task MarkDeletedAsync(
        IReadOnlyCollection<Guid> publicationIds,
        DateTimeOffset deletedAtUtc,
        CancellationToken cancellationToken)
    {
        if (publicationIds.Count == 0)
        {
            return;
        }

        var publications = await dbContext.Publications
            .Where(publication => publicationIds.Contains(publication.Id) && publication.State == PublicationState.Published)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var publication in publications)
        {
            publication.State = PublicationState.Deleted;
            publication.LastError = null;
            publication.UpdatedAtUtc = deletedAtUtc;
        }

        if (publications.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteAsync(Guid contentId, CancellationToken cancellationToken)
    {
        var content = await dbContext.ContentItems.SingleOrDefaultAsync(
            item => item.Id == contentId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Content '{contentId}' was not found.");
        dbContext.ContentItems.Remove(content);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApproveAsync(Guid contentId, CancellationToken cancellationToken)
    {
        var content = await dbContext.ContentItems
            .SingleOrDefaultAsync(item => item.Id == contentId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Content '{contentId}' was not found.");
        if (!content.AwaitingReview || content.Status == ContentStatus.Archived)
        {
            throw new InvalidOperationException("Only an active review candidate can be approved.");
        }

        var now = timeProvider.GetUtcNow();
        content.AwaitingReview = false;
        content.Status = ContentStatus.Scheduled;
        content.ScheduledAtUtc = now;
        content.UpdatedAtUtc = now;
        await AddPublicationsAsync(content, now, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ArchiveExpiredAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var retentionCutoff = nowUtc.AddMonths(-1);
        var candidates = await dbContext.ContentItems
            .Where(content =>
                content.Status != ContentStatus.Archived &&
                ((content.ExpiresAtUtc != null && content.ExpiresAtUtc < nowUtc) ||
                 (content.SourcePublishedAtUtc != null && content.SourcePublishedAtUtc <= retentionCutoff) ||
                 (content.SourcePublishedAtUtc == null && content.CreatedAtUtc <= retentionCutoff)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        candidates = candidates
            .Where(content =>
                content.ExpiresAtUtc is not null && content.ExpiresAtUtc < nowUtc ||
                ShouldArchiveForRetention(content, nowUtc))
            .ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        var ids = candidates.Select(content => content.Id).ToArray();
        foreach (var content in candidates)
        {
            content.Status = ContentStatus.Archived;
            content.ArchivedAtUtc = nowUtc;
            content.ArchiveReason = content.ExpiresAtUtc is not null && content.ExpiresAtUtc < nowUtc
                ? ArchiveReason.Expired
                : ArchiveReason.Retention;
            content.AwaitingReview = false;
            content.UpdatedAtUtc = nowUtc;
        }

        await CancelPendingPublicationsAsync(ids, nowUtc, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return candidates.Count;
    }

    private async Task AddPublicationsAsync(
        ContentRecord content,
        DateTimeOffset dueAtUtc,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var destinations = guildDestinations is null
            ? new[]
            {
                new GachaBot.Application.Publishing.GuildDestination(
                    1,
                    1,
                    0,
                    true,
                    GachaBot.Application.Publishing.GuildDestinationGames.All,
                    now),
            }
            : await guildDestinations.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        foreach (var destination in destinations.Where(destination => destination.SubscribesTo(content.Game, content.Kind)))
        {
            AddPublication(content, destination, dueAtUtc, now);
        }
    }

    private async Task<bool> AddMissingPublicationsAsync(
        ContentRecord content,
        DateTimeOffset dueAtUtc,
        CancellationToken cancellationToken)
    {
        if (content.Kind == ContentKind.Event &&
            content.SourcePublishedAtUtc.HasValue &&
            content.ExpiresAtUtc.HasValue)
        {
            return await AddEventPublicationsAsync(content, cancellationToken).ConfigureAwait(false);
        }

        var now = timeProvider.GetUtcNow();
        var destinations = guildDestinations is null
            ? new[]
            {
                new GachaBot.Application.Publishing.GuildDestination(
                    1,
                    1,
                    0,
                    true,
                    GachaBot.Application.Publishing.GuildDestinationGames.All,
                    now),
            }
            : await guildDestinations.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var added = false;
        foreach (var destination in destinations.Where(destination => destination.SubscribesTo(content.Game, content.Kind)))
        {
            var exists = await dbContext.Publications.AnyAsync(
                publication =>
                    publication.ContentId == content.Id &&
                    publication.DestinationGuildId == checked((long)destination.GuildId) &&
                    publication.DestinationChannelId == checked((long)destination.ChannelId) &&
                    (publication.State == PublicationState.Pending ||
                     publication.State == PublicationState.Processing ||
                     publication.State == PublicationState.Published),
                cancellationToken).ConfigureAwait(false);
            if (exists)
            {
                continue;
            }

            AddPublication(content, destination, dueAtUtc, now);
            added = true;
        }

        return added;
    }

    private void AddPublication(
        ContentRecord content,
        GachaBot.Application.Publishing.GuildDestination destination,
        DateTimeOffset dueAtUtc,
        DateTimeOffset createdAtUtc,
        PublicationPurpose purpose = PublicationPurpose.Standard) => dbContext.Publications.Add(new PublicationRecord
    {
        Id = Guid.NewGuid(),
        Content = content,
        ContentId = content.Id,
        DestinationGuildId = checked((long)destination.GuildId),
        DestinationChannelId = checked((long)destination.ChannelId),
        DueAtUtc = dueAtUtc,
        State = PublicationState.Pending,
        Purpose = purpose,
        CreatedAtUtc = createdAtUtc,
        UpdatedAtUtc = createdAtUtc,
    });

    public async Task ReconcileEventPublicationsForGuildAsync(
        ulong guildId,
        CancellationToken cancellationToken)
    {
        if (guildId == 0 || guildId > long.MaxValue || guildDestinations is null)
        {
            return;
        }

        var destination = (await guildDestinations.ListActiveAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.GuildId == guildId);
        if (destination is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var events = await dbContext.ContentItems
            .Where(content =>
                content.Kind == ContentKind.Event &&
                content.Status != ContentStatus.Archived &&
                content.SourcePublishedAtUtc != null &&
                content.ExpiresAtUtc != null &&
                content.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var content in events)
        {
            await CancelPendingEventPublicationsAsync(content.Id, destination, now, cancellationToken)
                .ConfigureAwait(false);
            if (destination.SubscribesTo(content.Game, content.Kind))
            {
                await AddEventPublicationsAsync(content, [destination], cancellationToken).ConfigureAwait(false);
            }
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> AddEventPublicationsAsync(
        ContentRecord content,
        CancellationToken cancellationToken) => await AddEventPublicationsAsync(
        content,
        await GetActiveDestinationsAsync(cancellationToken).ConfigureAwait(false),
        cancellationToken).ConfigureAwait(false);

    private async Task<bool> AddEventPublicationsAsync(
        ContentRecord content,
        IReadOnlyList<GachaBot.Application.Publishing.GuildDestination> destinations,
        CancellationToken cancellationToken)
    {
        if (content.SourcePublishedAtUtc is not { } startsAtUtc ||
            content.ExpiresAtUtc is not { } endsAtUtc)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        var added = false;
        DateTimeOffset? earliestDueAtUtc = null;
        foreach (var destination in destinations.Where(destination => destination.SubscribesTo(content.Game, content.Kind)))
        {
            if (destination.EventStartOffsetHours >= 0)
            {
                var startDueAtUtc = Max(now, startsAtUtc.AddHours(-destination.EventStartOffsetHours));
                added |= await AddEventPublicationIfMissingAsync(
                    content,
                    destination,
                    PublicationPurpose.EventStart,
                    startDueAtUtc,
                    now,
                    cancellationToken).ConfigureAwait(false);
                earliestDueAtUtc = earliestDueAtUtc is null
                    ? startDueAtUtc
                    : Min(earliestDueAtUtc.Value, startDueAtUtc);
            }

            if (destination.EventEndOffsetHours >= 0)
            {
                var endDueAtUtc = Max(now, endsAtUtc.AddHours(-destination.EventEndOffsetHours));
                added |= await AddEventPublicationIfMissingAsync(
                    content,
                    destination,
                    PublicationPurpose.EventEndingReminder,
                    endDueAtUtc,
                    now,
                    cancellationToken).ConfigureAwait(false);
                earliestDueAtUtc = earliestDueAtUtc is null
                    ? endDueAtUtc
                    : Min(earliestDueAtUtc.Value, endDueAtUtc);
            }
        }

        if (earliestDueAtUtc.HasValue)
        {
            content.ScheduledAtUtc = earliestDueAtUtc;
        }

        return added;
    }

    private async Task<bool> AddEventPublicationIfMissingAsync(
        ContentRecord content,
        GachaBot.Application.Publishing.GuildDestination destination,
        PublicationPurpose purpose,
        DateTimeOffset dueAtUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.Publications.AnyAsync(
            publication =>
                publication.ContentId == content.Id &&
                publication.DestinationGuildId == checked((long)destination.GuildId) &&
                publication.DestinationChannelId == checked((long)destination.ChannelId) &&
                publication.Purpose == purpose &&
                (publication.State == PublicationState.Pending ||
                 publication.State == PublicationState.Processing ||
                 publication.State == PublicationState.Published),
            cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return false;
        }

        AddPublication(content, destination, dueAtUtc, now, purpose);
        return true;
    }

    private async Task CancelPendingEventPublicationsAsync(
        Guid contentId,
        GachaBot.Application.Publishing.GuildDestination destination,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Publications.Where(publication =>
                publication.ContentId == contentId &&
                publication.DestinationGuildId == checked((long)destination.GuildId) &&
                publication.DestinationChannelId == checked((long)destination.ChannelId) &&
                (publication.Purpose == PublicationPurpose.EventStart ||
                 publication.Purpose == PublicationPurpose.EventEndingReminder) &&
                publication.State == PublicationState.Pending)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var publication in rows)
        {
            publication.State = PublicationState.Cancelled;
            publication.LastError = "Cancelled after this guild changed its event notification schedule.";
            publication.UpdatedAtUtc = now;
        }
    }

    private async Task<IReadOnlyList<GachaBot.Application.Publishing.GuildDestination>> GetActiveDestinationsAsync(
        CancellationToken cancellationToken)
    {
        if (guildDestinations is not null)
        {
            return await guildDestinations.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        }

        return
        [
            new GachaBot.Application.Publishing.GuildDestination(
                1,
                1,
                0,
                true,
                GachaBot.Application.Publishing.GuildDestinationGames.All,
                timeProvider.GetUtcNow()),
        ];
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private async Task InsertBaselineAsync(
        IReadOnlyList<SourceContentSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var snapshot in snapshots)
        {
            var isExplicitlyExpired = snapshot.ExpiresAtUtc.HasValue && snapshot.ExpiresAtUtc.Value < now;
            var isRetentionExpired = IsRetentionExpired(snapshot, now);
            var isArchived = isExplicitlyExpired || isRetentionExpired;
            dbContext.ContentItems.Add(new ContentRecord
            {
                Id = Guid.NewGuid(),
                Identity = snapshot.Identity,
                SourceKey = snapshot.SourceKey,
                ExternalId = snapshot.ExternalId,
                Game = snapshot.Game,
                Kind = snapshot.Kind,
                Title = snapshot.Title,
                SourceUrl = snapshot.SourceUrl.AbsoluteUri,
                DocumentJson = ContentDocumentJson.Serialize(snapshot.Document),
                DocumentHash = snapshot.Document.Hash,
                Status = isArchived ? ContentStatus.Archived : ContentStatus.Active,
                AwaitingReview = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                SourcePublishedAtUtc = snapshot.PublishedAtUtc,
                ExpiresAtUtc = snapshot.ExpiresAtUtc,
                ArchivedAtUtc = isArchived ? now : null,
                ArchiveReason = isArchived
                    ? isExplicitlyExpired ? ArchiveReason.Expired : ArchiveReason.Retention
                    : null,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ArchiveSupersededSourceItemsAsync(
        SourceContentSnapshot snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!snapshot.ReplacesSourceItems)
        {
            return 0;
        }

        var candidates = await dbContext.ContentItems
            .Where(content =>
                content.SourceKey == snapshot.SourceKey &&
                content.Game == snapshot.Game &&
                content.Kind == snapshot.Kind &&
                content.Identity != snapshot.Identity &&
                content.ExternalId != null &&
                content.ExternalId != "active-codes" &&
                !content.ExternalId.StartsWith("aggregate:") &&
                content.Status != ContentStatus.Archived)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var ids = candidates.Select(content => content.Id).ToArray();
        foreach (var content in candidates)
        {
            content.Status = ContentStatus.Archived;
            content.ArchivedAtUtc = now;
            content.ArchiveReason = ArchiveReason.Superseded;
            content.UpdatedAtUtc = now;
        }

        await CancelPendingPublicationsAsync(ids, now, cancellationToken).ConfigureAwait(false);
        return candidates.Count;
    }

    private async Task CancelPendingPublicationsAsync(
        IReadOnlyCollection<Guid> contentIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var publications = await dbContext.Publications
            .Where(publication =>
                contentIds.Contains(publication.ContentId) &&
                publication.State == PublicationState.Pending)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var publication in publications)
        {
            publication.State = PublicationState.Cancelled;
            publication.UpdatedAtUtc = now;
        }
    }

    private static bool IsManualArchive(ContentRecord content) =>
        content.Status == ContentStatus.Archived &&
        content.ArchiveReason == ArchiveReason.Manual;

    private bool IsRetentionExpired(SourceContentSnapshot snapshot, DateTimeOffset nowUtc) =>
        retentionPolicy?.ShouldArchive(
            snapshot.SourceKey,
            snapshot.PublishedAtUtc,
            nowUtc,
            nowUtc) == true;

    private bool ShouldArchiveForRetention(ContentRecord content, DateTimeOffset nowUtc) =>
        retentionPolicy?.ShouldArchive(
            content.SourceKey,
            content.SourcePublishedAtUtc,
            content.CreatedAtUtc,
            nowUtc) == true;

    private static bool IsLegacyFeedImageOnlyChange(
        string storedDocumentJson,
        ContentDocument incomingDocument)
    {
        var storedDocument = ContentDocumentJson.Deserialize(storedDocumentJson);
        var retainedBlocks = storedDocument.Blocks
            .Where(block => block is not ImageBlock image || !IsLegacyWutheringWavesFeedImage(image.Url))
            .ToArray();
        return retainedBlocks.Length != storedDocument.Blocks.Count &&
            retainedBlocks.Length > 0 &&
            ContentDocument.Create(retainedBlocks).Hash == incomingDocument.Hash;
    }

    private static bool IsLegacyWutheringWavesFeedImage(Uri uri) =>
        string.Equals(
            uri.AbsoluteUri,
            "https://hw-media-cdn-mingchao.kurogame.com/akiwebsite/website2.0/json/G152/en/ArticleMenu.json",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyTruncatedContentRepair(
        string storedDocumentJson,
        ContentDocument incomingDocument)
    {
        var stored = ContentDocumentJson.Deserialize(storedDocumentJson);
        var storedTextLength = stored.Blocks.OfType<TextBlock>().Sum(block => block.Text.Length);
        var incomingTextLength = incomingDocument.Blocks.OfType<TextBlock>().Sum(block => block.Text.Length);
        return storedTextLength < 200 &&
            !stored.Blocks.OfType<ImageBlock>().Any() &&
            (incomingTextLength >= 200 || incomingDocument.Blocks.OfType<ImageBlock>().Any());
    }

    private static bool NeedsContentRefresh(string documentJson)
    {
        var document = ContentDocumentJson.Deserialize(documentJson);
        var textLength = document.Blocks.OfType<TextBlock>().Sum(block => block.Text.Length);
        return textLength < 200 && !document.Blocks.OfType<ImageBlock>().Any();
    }
}
