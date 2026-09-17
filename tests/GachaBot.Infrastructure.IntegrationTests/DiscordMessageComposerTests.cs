using GachaBot.Domain.Content;
using GachaBot.Infrastructure.Discord;

namespace GachaBot.Infrastructure.IntegrationTests;

public sealed class DiscordMessageComposerTests
{
    [Fact]
    public void Compose_WithMixedBlocks_CreatesLinkedRichEmbed()
    {
        var document = ContentDocument.Create(
        [
            new HeadingBlock("Version 3.6", 1),
            new TextBlock("New event details", 2),
            new LinkBlock("Official notes", new Uri("https://example.com/notes"), 3),
            new ImageBlock(new Uri("https://cdn.example.com/banner.webp"), "Event banner", 4),
            new KeyValueBlock([new KeyValueItem("Starts", "Tonight")], 5),
        ]);

        var result = DiscordMessageComposer.Compose("Update available", document, new Uri("https://example.com/source"));

        var message = Assert.Single(result.Messages);
        Assert.True(string.IsNullOrEmpty(message.Content));
        var embed = Assert.Single(message.Embeds);
        Assert.Equal("Update available", embed.Title);
        Assert.Equal(new Uri("https://example.com/source"), embed.Url);
        Assert.Contains("**Version 3.6**", embed.Description, StringComparison.Ordinal);
        Assert.Contains("[Official notes](https://example.com/notes)", embed.Description, StringComparison.Ordinal);
        var field = Assert.Single(embed.Fields);
        Assert.Equal("Starts", field.Name);
        Assert.Equal("Tonight", field.Value);
        Assert.Equal(new Uri("https://cdn.example.com/banner.webp"), embed.Image?.Url);
        Assert.Equal("Event banner", embed.Image?.AltText);
    }

    [Fact]
    public void Compose_WithYouTubeLink_UsesRawMessageContentForNativeDiscordUnfurl()
    {
        var video = new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
        var document = ContentDocument.Create(
        [
            new TextBlock("Watch the character trailer.", 1),
            new LinkBlock("Character trailer", video, 2),
            new LinkBlock("Official notes", new Uri("https://example.com/notes"), 3),
        ]);

        var result = DiscordMessageComposer.Compose("New resonator", document, null);

        var message = Assert.Single(result.Messages);
        Assert.Equal(video.AbsoluteUri, message.Content);
        var description = Assert.Single(message.Embeds).Description;
        Assert.Contains("Watch the character trailer.", description, StringComparison.Ordinal);
        Assert.Contains("[Official notes](https://example.com/notes)", description, StringComparison.Ordinal);
        Assert.DoesNotContain("Character trailer", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_WithDuplicateYouTubeLinks_EmitsOneCanonicalUnfurlUrl()
    {
        var document = ContentDocument.Create(
        [
            new LinkBlock("Short link", new Uri("https://youtu.be/dQw4w9WgXcQ?t=10"), 1),
            new LinkBlock("Embedded link", new Uri("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ"), 2),
        ]);

        var result = DiscordMessageComposer.Compose("Video", document, null);

        Assert.Equal(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            Assert.Single(result.Messages).Content);
    }

    [Fact]
    public void Compose_WithFourThousandCharacters_FitsDiscordRichDescription()
    {
        var document = ContentDocument.Create([new TextBlock(new string('x', 4_000), 1)]);

        var result = DiscordMessageComposer.Compose("Long update", document, null);

        Assert.Single(result.Messages);
        Assert.All(result.Messages, AssertDiscordLimits);
        Assert.Equal(4_000, Assert.Single(Assert.Single(result.Messages).Embeds).Description?.Length);
    }

    [Fact]
    public void Compose_RedeemCode_UsesFencedCodeBlockForDiscordCopyAction()
    {
        var document = ContentDocument.Create(
        [
            new CodeBlock("WELCOMETONTE", 1),
            new KeyValueBlock(
            [
                new KeyValueItem("Rewards", "Celebration Fireworks Avatar Frame"),
                new KeyValueItem("Expires", "08/18/2026"),
            ], 2),
        ]);

        var result = DiscordMessageComposer.Compose("All Version 1.3 Redeem Codes", document, null);

        Assert.Contains(
            $"```{Environment.NewLine}WELCOMETONTE{Environment.NewLine}```",
            Assert.Single(Assert.Single(result.Messages).Embeds).Description,
            StringComparison.Ordinal);
        var embed = Assert.Single(Assert.Single(result.Messages).Embeds);
        Assert.Empty(embed.Fields);
        Assert.Contains("**Rewards:** Celebration Fireworks Avatar Frame", embed.Description, StringComparison.Ordinal);
        Assert.Contains("**Expires:** 08/18/2026", embed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_RedeemCode_KeepsEachCodeWithItsDetailsInOrder()
    {
        var document = ContentDocument.Create(
        [
            new CodeBlock("WITCHHOUSE", 1),
            new KeyValueBlock(
            [
                new KeyValueItem("Rewards", "Annulith x100"),
                new KeyValueItem("Expires", "20.09.2026"),
            ], 2),
            new CodeBlock("THEWHOOTS", 3),
            new KeyValueBlock(
            [
                new KeyValueItem("Rewards", "Colorless Dye x5"),
                new KeyValueItem("Expires", "20.09.2026"),
            ], 4),
        ]);

        var result = DiscordMessageComposer.Compose("All Active Redeem Codes", document, null);

        var embed = Assert.Single(Assert.Single(result.Messages).Embeds);
        Assert.Empty(embed.Fields);
        var description = embed.Description!;
        Assert.Contains($"```{Environment.NewLine}WITCHHOUSE{Environment.NewLine}```", description, StringComparison.Ordinal);
        Assert.Contains($"```{Environment.NewLine}THEWHOOTS{Environment.NewLine}```", description, StringComparison.Ordinal);
        Assert.True(description.IndexOf("WITCHHOUSE", StringComparison.Ordinal) <
            description.IndexOf("Annulith x100", StringComparison.Ordinal));
        Assert.True(description.IndexOf("Annulith x100", StringComparison.Ordinal) <
            description.IndexOf("THEWHOOTS", StringComparison.Ordinal));
        Assert.True(description.IndexOf("THEWHOOTS", StringComparison.Ordinal) <
            description.IndexOf("Colorless Dye x5", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_RedeemCode_IncludesStableDiscordRelativeTimestampInPublishedPayload()
    {
        var document = ContentDocument.Create(
        [
            new CodeBlock("F5F4D3B2A2", 1),
            new KeyValueBlock(
                [new KeyValueItem("Expires", "19.08.2026 (<t:1787184000:R>)")],
                2),
        ]);

        var result = DiscordMessageComposer.Compose("Current Redeem Codes", document, null);

        var embed = Assert.Single(Assert.Single(result.Messages).Embeds);
        Assert.Empty(embed.Fields);
        Assert.Contains("**Expires:** 19.08.2026 (<t:1787184000:R>)", embed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_WhenDocumentAlreadyLinksSource_DoesNotAddGenericSourceLink()
    {
        var source = new Uri("https://game8.co/codes");
        var document = ContentDocument.Create(
            [new LinkBlock("Source and redemption details", source, 1)]);

        var result = DiscordMessageComposer.Compose("Codes", document, source);

        var embed = Assert.Single(Assert.Single(result.Messages).Embeds);
        Assert.Equal(source, embed.Url);
        Assert.Equal(1, embed.Description!.Split(source.AbsoluteUri, StringSplitOptions.None).Length - 1);
        Assert.Contains("Source and redemption details", embed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_PlainSourceText_EscapesDiscordMarkdownWithoutRemovingLineBreaks()
    {
        var document = ContentDocument.Create(
            [new TextBlock("Accounts penalized in this round:\n2010**7781 | 2006**5818", 1)]);

        var result = DiscordMessageComposer.Compose("Account Action Notice", document, null);

        Assert.Contains(
            "Accounts penalized in this round:\n2010\\*\\*7781 | 2006\\*\\*5818",
            Assert.Single(Assert.Single(result.Messages).Embeds).Description,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "2010**7781",
            Assert.Single(Assert.Single(result.Messages).Embeds).Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_LargeDocument_AlwaysRespectsDiscordEmbedLimits()
    {
        var fields = Enumerable.Range(1, 25)
            .Select(index => new KeyValueItem($"Field {index}", new string('v', 200)))
            .ToArray();
        var images = Enumerable.Range(1, 10)
            .Select(index => new GalleryImage(
                new Uri($"https://cdn.example.com/{index}.webp"),
                $"Image {index}"))
            .ToArray();
        var document = ContentDocument.Create(
        [
            new TextBlock(new string('a', 4_000), 1),
            new TextBlock(new string('b', 4_000), 2),
            new KeyValueBlock(fields, 3),
            new GalleryBlock(images, 4),
        ]);

        var result = DiscordMessageComposer.Compose(new string('T', 300), document, new Uri("https://example.com/source"));

        Assert.True(result.Messages.Count > 1);
        Assert.All(result.Messages, AssertDiscordLimits);
        Assert.Equal(10, result.Messages.SelectMany(message => message.Embeds).Count(embed => embed.Image is not null));
    }

    [Fact]
    public void ProviderMessageIds_RoundTripsMultipleDiscordMessagesAndLegacySingleId()
    {
        Assert.Equal([123UL], DiscordProviderMessageIds.Parse("123"));

        var serialized = DiscordProviderMessageIds.Format([123UL, 456UL, 789UL]);

        Assert.Equal("123,456,789", serialized);
        Assert.Equal([123UL, 456UL, 789UL], DiscordProviderMessageIds.Parse(serialized));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("123,not-a-snowflake")]
    [InlineData("123,,456")]
    public void ProviderMessageIds_RejectsMalformedStoredValue(string value)
    {
        Assert.Throws<InvalidOperationException>(() => DiscordProviderMessageIds.Parse(value));
    }

    private static void AssertDiscordLimits(DiscordOutboundMessage message)
    {
        Assert.True(message.Content is null || message.Content.Length <= 2_000);
        Assert.InRange(message.Embeds.Count, 1, 10);
        Assert.All(message.Embeds, embed =>
        {
            Assert.True(embed.Title is null || embed.Title.Length <= 256);
            Assert.True(embed.Description is null || embed.Description.Length <= 4_096);
            Assert.True(embed.Fields.Count <= 25);
            Assert.All(embed.Fields, field =>
            {
                Assert.InRange(field.Name.Length, 1, 256);
                Assert.InRange(field.Value.Length, 1, 1_024);
            });
        });
        Assert.True(message.Embeds.Sum(embed => embed.CharacterCount) <= 6_000);
    }
}
