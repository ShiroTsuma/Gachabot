using GachaBot.Domain.Content;
using GachaBot.Domain.Games;

namespace GachaBot.Application.Content;

public sealed record ContentListItem(
    Guid Id,
    string SourceKey,
    GameKey Game,
    ContentKind Kind,
    string Title,
    ContentStatus Status,
    ArchiveReason? ArchiveReason,
    bool AwaitingReview,
    DateTimeOffset? SourcePublishedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ScheduledAtUtc,
    Uri? SourceUrl);

public sealed record ContentDetails(
    Guid Id,
    string SourceKey,
    GameKey Game,
    ContentKind Kind,
    string Title,
    ContentStatus Status,
    ArchiveReason? ArchiveReason,
    bool AwaitingReview,
    ContentDocument Document,
    Uri? SourceUrl,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? SourcePublishedAtUtc,
    DateTimeOffset? ScheduledAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record DashboardMetrics(
    int Active,
    int Scheduled,
    int AwaitingReview,
    int Archived,
    DateTimeOffset? LastSuccessfulImportUtc);

public sealed record CreateManualContentCommand(
    GameKey Game,
    ContentKind Kind,
    string Title,
    ContentDocument Document,
    DateTimeOffset? PublishAtUtc);

public sealed record PublishedDiscordMessage(
    ulong GuildId,
    ulong ChannelId,
    string ProviderMessageId);

public interface IContentManagementStore
{
    Task<DashboardMetrics> GetMetricsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ContentListItem>> ListAsync(
        ContentStatus? status,
        string? sourceKey,
        CancellationToken cancellationToken);

    Task<ContentDetails?> GetAsync(Guid contentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ContentDetails>> ListEventsEndingAfterAsync(
        DateTimeOffset endsAfterUtc,
        CancellationToken cancellationToken);

    Task<Guid> CreateManualAsync(
        CreateManualContentCommand command,
        CancellationToken cancellationToken);

    Task ArchiveAsync(Guid contentId, CancellationToken cancellationToken);

    Task RestoreAsync(Guid contentId, CancellationToken cancellationToken);

    Task RepublishAsync(Guid contentId, CancellationToken cancellationToken);

    Task RepublishToGuildAsync(
        Guid contentId,
        ulong guildId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PublishedDiscordMessage>> GetPublishedMessageIdsAsync(
        Guid contentId,
        CancellationToken cancellationToken);

    Task DeleteAsync(Guid contentId, CancellationToken cancellationToken);

    Task ApproveAsync(Guid contentId, CancellationToken cancellationToken);

    Task<int> ArchiveExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

public interface IContentDeletionService
{
    Task DeleteAsync(Guid contentId, CancellationToken cancellationToken);
}
