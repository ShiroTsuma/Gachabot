using GachaBot.Application.Publishing;
using GachaBot.Domain.Content;
using Microsoft.EntityFrameworkCore;

namespace GachaBot.Infrastructure.Database;

public sealed class PublicationQueueStore(
    AppDbContext dbContext,
    IGuildDestinationStore destinations) : IPublicationQueueStore
{
    public async Task<LeasedPublication?> TryLeaseDueAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var activeDestinations = await destinations.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        PublicationRecord? publication;
        GuildDestination? destination;
        while (true)
        {
            publication = await dbContext.Publications
                .Include(item => item.Content)
                .Where(item =>
                    (item.State == PublicationState.Pending && item.DueAtUtc <= nowUtc) ||
                    (item.State == PublicationState.Processing && item.UpdatedAtUtc <= nowUtc.AddMinutes(-5)))
                .OrderBy(item => item.Content.SourcePublishedAtUtc ?? item.Content.CreatedAtUtc)
                .ThenBy(item => item.DueAtUtc)
                .ThenBy(item => item.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (publication is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            destination = publication.DestinationGuildId is null || publication.DestinationChannelId is null
                ? null
                : activeDestinations.SingleOrDefault(item =>
                    item.GuildId == checked((ulong)publication.DestinationGuildId.Value) &&
                    item.ChannelId == checked((ulong)publication.DestinationChannelId.Value) &&
                    item.SubscribesTo(publication.Content.Game, publication.Content.Kind));
            if (destination is not null)
            {
                break;
            }

            publication.State = PublicationState.Cancelled;
            publication.LastError = "Cancelled because this guild no longer accepts this game, subject, or channel.";
            publication.UpdatedAtUtc = nowUtc;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        publication.State = PublicationState.Processing;
        publication.AttemptCount++;
        publication.UpdatedAtUtc = nowUtc;
        var existingProviderMessageId = publication.Purpose == PublicationPurpose.Standard
            ? await dbContext.Publications
                .Where(item =>
                    item.ContentId == publication.ContentId &&
                    item.DestinationGuildId == publication.DestinationGuildId &&
                    item.DestinationChannelId == publication.DestinationChannelId &&
                    item.Purpose == PublicationPurpose.Standard &&
                    item.State == PublicationState.Published &&
                    item.ProviderMessageId != null)
                .OrderByDescending(item => item.UpdatedAtUtc)
                .Select(item => item.ProviderMessageId)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false)
            : null;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var document = ContentDocumentJson.Deserialize(publication.Content.DocumentJson);
        return new LeasedPublication(
            publication.Id,
            new PublicationPayload(
                publication.ContentId,
                publication.Content.SourceKey,
                publication.Content.ExternalId,
                publication.Content.Game,
                publication.Content.Kind,
                destination,
                publication.Content.Title,
                publication.Content.SourceUrl is null ? null : new Uri(publication.Content.SourceUrl),
                document,
                existingProviderMessageId,
                publication.Purpose switch
                {
                    PublicationPurpose.EventStart => GachaBot.Application.Publishing.PublicationPurpose.EventStart,
                    PublicationPurpose.EventEndingReminder => GachaBot.Application.Publishing.PublicationPurpose.EventEndingReminder,
                    _ => GachaBot.Application.Publishing.PublicationPurpose.Standard,
                }),
            publication.AttemptCount);
    }

    public async Task MarkPublishedAsync(
        Guid publicationId,
        PublishReceipt receipt,
        CancellationToken cancellationToken)
    {
        var publication = await RequireAsync(publicationId, cancellationToken).ConfigureAwait(false);
        publication.State = PublicationState.Published;
        publication.ProviderMessageId = receipt.ProviderMessageId;
        publication.LastError = null;
        publication.UpdatedAtUtc = receipt.PublishedAtUtc;
        var nextDueAtUtc = await dbContext.Publications
            .Where(item =>
                item.ContentId == publication.ContentId &&
                (item.State == PublicationState.Pending || item.State == PublicationState.Processing))
            .OrderBy(item => item.DueAtUtc)
            .Select(item => (DateTimeOffset?)item.DueAtUtc)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        publication.Content.Status = nextDueAtUtc.HasValue
            ? ContentStatus.Scheduled
            : ContentStatus.Published;
        publication.Content.ScheduledAtUtc = nextDueAtUtc;
        publication.Content.PublishedAtUtc = receipt.PublishedAtUtc;
        publication.Content.UpdatedAtUtc = receipt.PublishedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(
        Guid publicationId,
        string failureMessage,
        DateTimeOffset retryAtUtc,
        CancellationToken cancellationToken)
    {
        var publication = await RequireAsync(publicationId, cancellationToken).ConfigureAwait(false);
        // Backoff caps at 15 minutes, so this keeps retrying for about a day and survives a Discord outage
        // or a missing channel permission that gets fixed later.
        publication.State = publication.AttemptCount >= 100 ? PublicationState.Failed : PublicationState.Pending;
        publication.LastError = failureMessage.Length <= 2_000
            ? failureMessage
            : failureMessage[..2_000];
        publication.DueAtUtc = retryAtUtc;
        publication.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PublicationRecord> RequireAsync(Guid publicationId, CancellationToken cancellationToken) =>
        await dbContext.Publications
            .Include(publication => publication.Content)
            .SingleOrDefaultAsync(publication => publication.Id == publicationId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new KeyNotFoundException($"Publication '{publicationId}' was not found.");
}
