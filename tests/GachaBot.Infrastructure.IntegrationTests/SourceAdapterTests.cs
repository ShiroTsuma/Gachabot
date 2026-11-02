using System.Net;
using System.Text;
using GachaBot.Application.Ingestion;
using GachaBot.Domain.Content;
using GachaBot.Domain.Games;
using GachaBot.Infrastructure.Sources;

namespace GachaBot.Infrastructure.IntegrationTests;

public sealed class SourceAdapterTests
{
    [Fact]
    public async Task WutheringWavesSource_MapsOfficialJsonToExtensibleBlocks()
    {
        const string json = """
            [{
              "articleContent": "<div><p>Maintenance details</p><img src=\"https://cdn.example.com/banner.webp\" alt=\"Version banner\"></div>",
              "articleDesc": "Server maintenance",
              "articleId": 5281,
              "articleTitle": "Wuthering Waves Version 3.6 Update Maintenance Notice",
              "articleType": 58,
              "startTime": "2026-08-13 11:00:00",
              "suggestCover": ""
            }]
            """;
        var source = new WutheringWavesOfficialSource(ClientReturning(json, "application/json"));

        var items = await ReadAllAsync(source);

        var item = Assert.Single(items);
        Assert.Equal("5281", item.ExternalId);
        Assert.Equal(ContentKind.Maintenance, item.Kind);
        Assert.Equal(GameKey.WutheringWaves, item.Game);
        Assert.Contains(item.Document.Blocks, block => block is TextBlock);
        var image = Assert.Single(item.Document.Blocks.OfType<ImageBlock>());
        Assert.Equal("https://cdn.example.com/banner.webp", image.Url.AbsoluteUri);
        Assert.DoesNotContain(
            item.Document.Blocks.OfType<ImageBlock>(),
            block => block.Url.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            new DateTimeOffset(2026, 8, 13, 3, 0, 0, TimeSpan.Zero),
            item.PublishedAtUtc);
    }

    [Fact]
    public async Task WutheringWavesSource_DoesNotClassifyArticlesAboutEventsAsEvents()
    {
        const string json = """
            [{
              "articleContent": "<p>Event overview</p>",
              "articleId": 5529,
              "articleTitle": "Upcoming Events in Wuthering Waves Version 3.7",
              "startTime": "2026-10-20 18:00:00",
              "suggestCover": ""
            }]
            """;
        var source = new WutheringWavesOfficialSource(ClientReturning(json, "application/json"));

        var item = Assert.Single(await ReadAllAsync(source));

        Assert.Equal(ContentKind.Update, item.Kind);
    }

    [Fact]
    public async Task WutheringWavesSource_ExtractsAndCanonicalizesYouTubeLinksFromArticleMarkup()
    {
        const string json = """
            [{
              "articleContent": "<p>Character trailer</p><a href=\"https://youtu.be/dQw4w9WgXcQ?t=10\">Watch</a><iframe src=\"https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ\"></iframe>",
              "articleId": 6000,
              "articleTitle": "Character Preview",
              "startTime": "2026-08-13 11:00:00",
              "suggestCover": ""
            }]
            """;
        var source = new WutheringWavesOfficialSource(ClientReturning(json, "application/json"));

        var item = Assert.Single(await ReadAllAsync(source));

        var link = Assert.Single(item.Document.Blocks.OfType<LinkBlock>());
        Assert.Equal("YouTube: Watch", link.Label);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", link.Url.AbsoluteUri);
    }

    [Fact]
    public async Task NevernessToEvernessSource_MapsOfficialListAndCategory()
    {
        const string html = """
            <div class="listNews">
              <a href="/en/article/news/gameevent/20260812/263600.html">
                <div class="listItem"><h2 class="title">Summer Event</h2>
                <p class="date">2026-08-12</p><p class="type">Events</p></div>
              </a>
            </div>
            """;
        var source = new NevernessToEvernessOfficialSource(ClientReturning(html, "text/html"));

        var item = Assert.Single(await ReadAllAsync(source));

        Assert.Equal("263600", item.ExternalId);
        Assert.Equal(ContentKind.News, item.Kind);
        Assert.Equal(GameKey.NevernessToEverness, item.Game);
        Assert.Equal("https://nte.perfectworld.com/en/article/news/gameevent/20260812/263600.html", item.SourceUrl.AbsoluteUri);
    }

    [Fact]
    public async Task Game8RedeemSource_ExtractsAndNormalizesCandidateCodes()
    {
        const string html = """
            <h2>Active Wuthering Waves Codes</h2>
            <table><tr><th>Code</th><th>Reward</th></tr>
              <tr><td><code> wuwa-2026 </code></td><td>Astrite</td></tr>
              <tr><td><code>WUWA-2026</code></td><td>Duplicate</td></tr>
            </table>
            <h2>Expired codes</h2><table><tr><td><code>OLD123</code></td></tr></table>
            """;
        var source = new Game8RedeemCodeSource(
            ClientReturning(html, "text/html"),
            GameKey.WutheringWaves,
            new Uri("https://game8.co/games/Wuthering-Waves/archives/453149"));

        var item = Assert.Single(await ReadAllAsync(source));

        Assert.Equal("WUWA-2026", item.ExternalId);
        Assert.Equal(ContentKind.RedeemCode, item.Kind);
        Assert.Equal(SourceTrust.ReviewRequired, source.Trust);
    }

    private static HttpClient ClientReturning(string content, string mediaType) =>
        new(new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType),
        }));

    private static async Task<List<SourceContentSnapshot>> ReadAllAsync(IGameContentSource source)
    {
        var result = new List<SourceContentSnapshot>();
        await foreach (var item in source.FetchAsync(TestContext.Current.CancellationToken))
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class StaticResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
