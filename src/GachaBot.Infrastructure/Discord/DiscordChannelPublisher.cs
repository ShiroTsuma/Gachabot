using Discord;
using Discord.Rest;
using GachaBot.Application.Publishing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GachaBot.Infrastructure.Discord;

public sealed partial class DiscordChannelPublisher(
    DiscordRestClient client,
    IOptions<DiscordOptions> options,
    TimeProvider timeProvider,
    DiscordMediaMessagePlanner mediaPlanner,
    ILogger<DiscordChannelPublisher> logger) : IDiscordPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private bool _loggedIn;

    public async Task<PublishReceipt> PublishAsync(
        PublicationPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);
        var channel = await client.GetChannelAsync(payload.Destination.ChannelId, options: null)
            .ConfigureAwait(false) as ITextChannel
            ?? throw new InvalidOperationException("Configured Discord destination is not a text channel.");
        if (channel.GuildId != payload.Destination.GuildId)
        {
            throw new InvalidOperationException("Configured Discord destination channel does not belong to its guild.");
        }

        var composition = DiscordMessageComposer.Compose(
            PublicationTitle(payload),
            payload.Document,
            payload.SourceUrl);
        var messages = await mediaPlanner.PrepareAsync(payload, composition, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
            var attachmentCount = messages.Sum(message => message.Attachments.Count);
            var imageCount = composition.Messages.Sum(message =>
                message.Embeds.Count(embed => embed.Image is not null));
            LogPublicationPrepared(
                logger,
                payload.ContentId,
                messages.Count,
                attachmentCount,
                imageCount - attachmentCount);
            }
            // A changed code list is news: an edit to an old message further up the channel goes unnoticed.
            // The old post is only removed in guilds that asked for obsolete posts to be deleted.
            var repost = payload.Kind == GachaBot.Domain.Content.ContentKind.RedeemCode;
            if (!string.IsNullOrWhiteSpace(payload.ExistingProviderMessageId) &&
                (!repost || payload.Destination.DeleteObsoleteMessages))
            {
                return await UpdateExistingAsync(
                    channel,
                    payload.ExistingProviderMessageId,
                    messages,
                    repost,
                    cancellationToken).ConfigureAwait(false);
            }

            var messageIds = new List<ulong>(messages.Count);
            for (var index = 0; index < messages.Count; index++)
            {
            cancellationToken.ThrowIfCancellationRequested();
            var outboundMessage = messages[index];
            if (logger.IsEnabled(LogLevel.Information))
            {
                var attachmentBytes = outboundMessage.Attachments.Sum(attachment => attachment.Length);
                LogMessageSendStarted(
                    logger,
                    payload.ContentId,
                    index + 1,
                    messages.Count,
                    outboundMessage.Attachments.Count,
                    attachmentBytes);
            }
            var message = await DiscordMessageSender.SendAsync(channel, outboundMessage, cancellationToken)
                .ConfigureAwait(false);
            messageIds.Add(message.Id);
            }

            return new PublishReceipt(
                DiscordProviderMessageIds.Format(messageIds),
                timeProvider.GetUtcNow());
        }
        finally
        {
            foreach (var attachment in messages.SelectMany(message => message.Attachments)
                         .Where(attachment => attachment.DeleteAfterUse && File.Exists(attachment.FullPath)))
            {
                File.Delete(attachment.FullPath);
            }
        }
    }

    public async Task DeleteMessagesAsync(
        GuildDestination destination,
        string providerMessageIds,
        CancellationToken cancellationToken)
    {
        await EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);
        var channel = await client.GetChannelAsync(destination.ChannelId, options: null)
            .ConfigureAwait(false) as ITextChannel
            ?? throw new InvalidOperationException("Configured Discord destination is not a text channel.");
        if (channel.GuildId != destination.GuildId)
        {
            throw new InvalidOperationException("Configured Discord destination channel does not belong to its guild.");
        }
        await DeleteMessagesAsync(
            channel,
            DiscordProviderMessageIds.Parse(providerMessageIds),
            new RequestOptions { CancelToken = cancellationToken }).ConfigureAwait(false);
    }

    private async Task<PublishReceipt> UpdateExistingAsync(
        ITextChannel channel,
        string providerMessageId,
        IReadOnlyList<DiscordPreparedMessage> messages,
        bool repost,
        CancellationToken cancellationToken)
    {
        var requestOptions = new RequestOptions { CancelToken = cancellationToken };
        var existingIds = DiscordProviderMessageIds.Parse(providerMessageId);
        if (repost || messages.Any(message => message.Attachments.Count > 0))
        {
            var replacementIds = new List<ulong>(messages.Count);
            foreach (var outbound in messages)
            {
                var replacement = await DiscordMessageSender.SendAsync(channel, outbound, cancellationToken)
                    .ConfigureAwait(false);
                replacementIds.Add(replacement.Id);
            }

            await DeleteMessagesAsync(channel, existingIds, requestOptions).ConfigureAwait(false);
            return new PublishReceipt(
                DiscordProviderMessageIds.Format(replacementIds),
                timeProvider.GetUtcNow());
        }

        var currentIds = new List<ulong>(messages.Count);
        for (var index = 0; index < messages.Count; index++)
        {
            var outboundMessage = messages[index];
            if (index >= existingIds.Count)
            {
                var created = await DiscordMessageSender.SendAsync(channel, outboundMessage, cancellationToken)
                    .ConfigureAwait(false);
                currentIds.Add(created.Id);
                continue;
            }

            if (await channel.GetMessageAsync(existingIds[index], CacheMode.AllowDownload, requestOptions)
                    .ConfigureAwait(false) is not IUserMessage message)
            {
                // Someone deleted the earlier post; send it again instead of failing on every retry.
                var replacement = await DiscordMessageSender.SendAsync(channel, outboundMessage, cancellationToken)
                    .ConfigureAwait(false);
                currentIds.Add(replacement.Id);
                continue;
            }

            var embeds = DiscordMessageSender.BuildEmbeds(outboundMessage.Embeds);
            await message.ModifyAsync(properties =>
            {
                properties.Content = outboundMessage.Content ?? string.Empty;
                properties.Embeds = embeds;
                properties.AllowedMentions = AllowedMentions.None;
            }, requestOptions).ConfigureAwait(false);
            currentIds.Add(message.Id);
        }

        foreach (var obsoleteId in existingIds.Skip(messages.Count))
        {
            if (await channel.GetMessageAsync(obsoleteId, CacheMode.AllowDownload, requestOptions)
                    .ConfigureAwait(false) is IUserMessage obsolete)
            {
                await obsolete.DeleteAsync(requestOptions).ConfigureAwait(false);
            }
        }

        return new PublishReceipt(
            DiscordProviderMessageIds.Format(currentIds),
            timeProvider.GetUtcNow());
    }

    public async ValueTask DisposeAsync()
    {
        _loginLock.Dispose();
        await client.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (_loggedIn)
        {
            return;
        }

        await _loginLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loggedIn)
            {
                await client.LoginAsync(TokenType.Bot, options.Value.BotToken).ConfigureAwait(false);
                _loggedIn = true;
            }
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private static string PublicationTitle(PublicationPayload payload) => payload.Purpose switch
    {
        PublicationPurpose.EventStart => $"Event starting: {payload.Title}",
        PublicationPurpose.EventEndingReminder => $"Ending soon: {payload.Title}",
        _ => payload.Title,
    };

    private static async Task DeleteMessagesAsync(
        ITextChannel channel,
        IEnumerable<ulong> messageIds,
        RequestOptions requestOptions)
    {
        foreach (var messageId in messageIds)
        {
            if (await channel.GetMessageAsync(messageId, CacheMode.AllowDownload, requestOptions)
                    .ConfigureAwait(false) is IUserMessage message)
            {
                await message.DeleteAsync(requestOptions).ConfigureAwait(false);
            }
        }
    }

}
