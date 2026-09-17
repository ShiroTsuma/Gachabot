using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using GachaBot.Application.Ingestion;
using GachaBot.Domain.Content;
using GachaBot.Domain.Games;
using GachaBot.Infrastructure.Sources;
using Microsoft.Extensions.Logging;

namespace GachaBot.Infrastructure.IntegrationTests;

public sealed class ConfiguredSourceTests
{
    [Fact]
    public async Task Game8EventCalendar_ImportsCurrentAndUpcomingRowsButSkipsPermanentEvents()
    {
        const string html = """
            <main>
              <h2>Neverness to Everness Events Calendar</h2>
              <h3>List of Upcoming Events</h3>
              <div><table><tbody>
                <tr><th>Event</th><th>Duration</th></tr>
                <tr>
                  <td><a href="/games/Neverness-to-Everness/archives/617001"><img data-src="https://img.game8.co/617001/shipwreck.png" />Shipwreck Salvage</a></td>
                  <td>August 28 - September 30, 2026</td>
                  <td>Recover treasure from the ocean floor.</td><td>・Annulith<br>・Fons</td>
                </tr>
              </tbody></table></div>
              <h3>List of Current Events</h3>
              <table><tbody><tr>
                <td><a href="/games/Neverness-to-Everness/archives/616281">Surf Breaker</a></td>
                <td>August 19 - September 30, 2026</td>
                <td>Race through summer heat and ocean breeze.</td><td>・Annulith</td>
              </tr></tbody></table>
              <h3>All Permanent Events</h3>
              <table><tbody><tr>
                <td><a href="/games/Neverness-to-Everness/archives/100">Login Gift</a></td><td>Permanent</td>
              </tr></tbody></table>
            </main>
            """;
        var definition = new SourceDefinition
        {
            Key = "game8-neverness-to-everness-events",
            Game = GameKey.NevernessToEverness,
            Trust = SourceTrust.Trusted,
            Handler = Game8EventCalendarHandler.HandlerKey,
            Url = new Uri("https://game8.co/games/Neverness-to-Everness/archives/592073"),
            EventCalendar = new EventCalendarRules
            {
                ReadySelector = "h2",
                IncludedSectionHeadings = ["List of Current Events", "List of Upcoming Events"],
                ExcludedSectionHeadings = ["Permanent", "Previous"],
                DateUtcOffsetHours = 1,
                DayStartHour = 5,
            },
        };
        var source = ConfiguredSource(
            definition,
            new Game8EventCalendarHandler(new StubRenderedPageClient(html)));

        Assert.True(source.SchedulesUpcomingEvents);
        var items = await ReadAllAsync(source);

        Assert.Equal(2, items.Count);
        var upcoming = Assert.Single(items, item => item.Title == "Shipwreck Salvage");
        Assert.Equal(new DateTimeOffset(2026, 8, 28, 4, 0, 0, TimeSpan.Zero), upcoming.PublishedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 4, 0, 0, TimeSpan.Zero), upcoming.ExpiresAtUtc);
        Assert.Equal("https://game8.co/games/Neverness-to-Everness/archives/617001", upcoming.SourceUrl.AbsoluteUri);
        Assert.Contains(upcoming.Document.Blocks.OfType<ImageBlock>(), block =>
            block.Url.AbsoluteUri == "https://img.game8.co/617001/shipwreck.png");
        Assert.Contains(upcoming.Document.Blocks.OfType<KeyValueBlock>(), block =>
            block.Items.Any(item => item.Key == "Rewards" && item.Value.Contains("Annulith", StringComparison.Ordinal)));
        Assert.Contains(upcoming.Document.Blocks.OfType<KeyValueBlock>(), block =>
            block.Items.Any(item => item.Key == "Start" && item.Value.Contains("<t:", StringComparison.Ordinal)));
        Assert.DoesNotContain(items, item => item.Title == "Login Gift");
    }

    [Fact]
    public async Task WutheringWavesTimeline_ExtractsBannersAndActivitiesWithOfficialLinks()
    {
        const string payload = """
            16:{"banners":[{"name":"Denia Banner","description":"$undefined","coverImgSrc":"/api/event-cover-images/file/denia.png","sourceUrl":"https://wutheringwaves.kurogames.com/en/main/news/detail/5310","startDate":"2026-08-20 10:00:00","endDate":"2026-09-10 09:59:59"}],"activities":[{"name":"Combat Event","description":"Clear stages.","sourceUrl":"https://wutheringwaves.kurogames.com/en/main/news/detail/5310","startDate":"2026-08-22 10:00","endDate":"2026-09-29 11:59:59"}]}
            """;
        var encodedPayload = JsonSerializer.Serialize(payload);
        var client = ClientRouting(_ => Response(
            $"<html><script>self.__next_f.push([1,{encodedPayload}])</script></html>",
            "text/html"));
        var definition = new SourceDefinition
        {
            Key = "wuwatracker-wuthering-waves-events",
            Game = GameKey.WutheringWaves,
            Trust = SourceTrust.Trusted,
            Handler = WutheringWavesTimelineHandler.HandlerKey,
            Url = new Uri("https://wuwatracker.com/pl/timeline"),
            Timeline = new TimelineRules { ServerUtcOffsetHours = 8 },
        };

        var items = await ReadAllAsync(ConfiguredSource(
            definition,
            new WutheringWavesTimelineHandler(client)));

        Assert.Equal(2, items.Count);
        var banner = Assert.Single(items, item => item.Title == "Denia Banner");
        Assert.Equal(ContentKind.Event, banner.Kind);
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 2, 0, 0, TimeSpan.Zero), banner.PublishedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 1, 59, 59, TimeSpan.Zero), banner.ExpiresAtUtc);
        Assert.Equal("https://wutheringwaves.kurogames.com/en/main/news/detail/5310", banner.SourceUrl.AbsoluteUri);
        var cover = Assert.Single(banner.Document.Blocks.OfType<ImageBlock>());
        Assert.Equal("https://wuwatracker.com/api/event-cover-images/file/denia.png", cover.Url.AbsoluteUri);
        var schedule = Assert.Single(banner.Document.Blocks.OfType<KeyValueBlock>());
        Assert.Contains(schedule.Items, item => item.Key == "Start" && item.Value == "<t:1787191200:F> · <t:1787191200:R>");
        Assert.Contains(schedule.Items, item => item.Key == "End" && item.Value == "<t:1789005599:F> · <t:1789005599:R>");
        Assert.Contains(banner.Document.Blocks.OfType<LinkBlock>(), block =>
            block.Label == "Schedule: WuWa Tracker");
    }

    [Fact]
    public async Task JsonArticleFeed_UsesOneStateLookupAndProcessesNewestUnknownArticleFirst()
    {
        const string listJson = """
            [
              { "articleId": 1858, "articleTitle": "Pinned old FAQ", "startTime": "2025-01-06 20:42:42" },
              { "articleId": 758, "articleTitle": "Pinned old details", "startTime": "2024-05-23 10:00:00" },
              { "articleId": 5281, "articleTitle": "Newest maintenance", "startTime": "2026-08-13 11:00:00" },
              { "articleId": 5245, "articleTitle": "Previous preview", "startTime": "2026-08-07 20:00:00" },
              { "articleId": 6000, "articleTitle": "Article without a date" }
            ]
            """;
        var detailOrder = new List<string>();
        var client = ClientRouting(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("feed.json", StringComparison.Ordinal))
            {
                return Response(listJson, "application/json");
            }

            var externalId = Path.GetFileNameWithoutExtension(request.RequestUri.AbsolutePath);
            detailOrder.Add(externalId);
            return Response($$"""
                {
                  "articleId": {{externalId}},
                  "articleTitle": "Article {{externalId}}",
                  "articleContent": "<p>Complete article {{externalId}} content.</p>",
                  "startTime": "{{(externalId == "5281" ? "2026-08-13 11:00:00" : "2024-05-23 10:00:00")}}"
                }
                """, "application/json");
        });
        var lookup = new BatchOnlySourceContentLookup(
            new Dictionary<string, SourceContentState>(StringComparer.Ordinal)
            {
                ["1858"] = new(true, false),
                ["5245"] = new(true, false),
            });
        var definition = new SourceDefinition
        {
            Key = "official-wuthering-waves",
            Game = GameKey.WutheringWaves,
            Trust = SourceTrust.Official,
            Handler = JsonArticleFeedHandler.HandlerKey,
            Url = new Uri("https://cdn.example.test/feed.json"),
            ArticleUrlTemplate = "https://game.example.test/news/{externalId}",
            JsonArticle = new JsonArticleRules
            {
                DetailUrlTemplate = "https://cdn.example.test/article/{externalId}.json",
                ExternalIdField = "articleId",
                TitleField = "articleTitle",
                ContentField = "articleContent",
                PublishedAtField = "startTime",
                PublishedAtFormat = "yyyy-MM-dd HH:mm:ss",
                PublishedAtUtcOffsetHours = 8,
                StopWhenKnown = false,
            },
        };
        var source = ConfiguredSource(definition, new JsonArticleFeedHandler(client, lookup));

        var items = await ReadAllAsync(source);

        Assert.Equal(1, lookup.BatchCalls);
        Assert.Equal(["5281", "758", "6000"], detailOrder);
        Assert.Equal(["5281", "758", "6000"], items.Select(item => item.ExternalId));
    }

    [Fact]
    public async Task JsonArticleFeed_LoadsFullDetailInsteadOfTruncatedListContent()
    {
        var listJson = $"[{TestSourceFixture.Read("wuthering-waves", "article-menu-3657.json")}]";
        var detailJson = TestSourceFixture.Read("wuthering-waves", "article-3657.json");
        var requests = new List<Uri>();
        var client = ClientRouting(request =>
        {
            requests.Add(request.RequestUri!);
            return request.RequestUri!.AbsolutePath.EndsWith("ArticleMenu.json", StringComparison.Ordinal)
                ? Response(listJson, "application/json")
                : Response(detailJson, "application/json");
        });
        var definition = new SourceDefinition
        {
            Key = "official-wuthering-waves",
            Game = GameKey.WutheringWaves,
            Trust = SourceTrust.Official,
            Handler = JsonArticleFeedHandler.HandlerKey,
            Url = new Uri("https://cdn.example.test/ArticleMenu.json"),
            ArticleUrlTemplate = "https://game.example.test/news/{externalId}",
            JsonArticle = new JsonArticleRules
            {
                DetailUrlTemplate = "https://cdn.example.test/article/{externalId}.json",
                ExternalIdField = "articleId",
                TitleField = "articleTitle",
                ContentField = "articleContent",
                PublishedAtField = "startTime",
                PublishedAtFormat = "yyyy-MM-dd HH:mm:ss",
                PublishedAtUtcOffsetHours = 8,
                CoverField = "suggestCover",
            },
        };
        var source = ConfiguredSource(
            definition,
            new JsonArticleFeedHandler(client, new StubSourceContentLookup(string.Empty, string.Empty)));

        var item = Assert.Single(await ReadAllAsync(source));

        Assert.Equal("3657", item.ExternalId);
        Assert.Equal(ContentKind.Update, item.Kind);
        Assert.Equal(new DateTimeOffset(2025, 11, 20, 2, 0, 0, TimeSpan.Zero), item.PublishedAtUtc);
        var text = string.Join(' ', item.Document.Blocks.OfType<TextBlock>().Select(block => block.Text));
        Assert.Contains("Maintenance Time", text, StringComparison.Ordinal);
        Assert.Contains("floating in mid-air", text, StringComparison.Ordinal);
        Assert.True(text.Length > 15_000);
        Assert.NotEmpty(item.Document.Blocks.OfType<ImageBlock>());
        Assert.Contains(requests, uri => uri.AbsolutePath.EndsWith("/article/3657.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PagedHtmlArticles_FollowsLatestPagesAndStopsAtFirstKnownItem()
    {
        var firstPage = TestSourceFixture.Read("neverness-to-everness", "index.html");
        var secondPage = TestSourceFixture.Read("neverness-to-everness", "index1.html");
        var detail = TestSourceFixture.Read("neverness-to-everness", "article-263505.html");
        var requests = new List<Uri>();
        var client = ClientRouting(request =>
        {
            requests.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("index.html", StringComparison.Ordinal))
            {
                return Response(firstPage, "text/html");
            }

            if (path.EndsWith("index1.html", StringComparison.Ordinal))
            {
                return Response(secondPage, "text/html");
            }

            return Response(detail, "text/html");
        });
        var definition = new SourceDefinition
        {
            Key = "official-neverness-to-everness",
            Game = GameKey.NevernessToEverness,
            Trust = SourceTrust.Official,
            Handler = PagedHtmlArticleFeedHandler.HandlerKey,
            Url = new Uri("https://nte.perfectworld.com/en/article/news/index.html"),
            HtmlArticle = new HtmlArticleRules
            {
                ItemSelector = ".listNews > a[href]",
                ExternalIdPattern = @"/(\d+)\.html$",
                TitleSelector = ".title",
                CategorySelector = ".type",
                PublishedAtSelector = ".date",
                PublishedAtFormat = "yyyy-MM-dd",
                DetailContentSelector = ".articleContent",
                PaginationUrlTemplate = "https://nte.perfectworld.com/en/article/news/index{pageSuffix}.html",
                FirstPage = 0,
                MaximumPages = 26,
                StopWhenKnown = true,
            },
        };
        var known = new StubSourceContentLookup("official-neverness-to-everness", "263286");
        var source = ConfiguredSource(definition, new PagedHtmlArticleFeedHandler(client, known));

        var items = await ReadAllAsync(source);

        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.Contains(
            item.Document.Blocks.OfType<TextBlock>(),
            block => block.Text.Contains("continued support", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(requests, uri => uri.AbsolutePath.EndsWith("index1.html", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, uri => uri.AbsolutePath.EndsWith("index2.html", StringComparison.Ordinal));
        var formattedText = string.Join('\n', items[0].Document.Blocks
            .OfType<TextBlock>()
            .Select(block => block.Text));
        Assert.Contains(
            "Accounts penalized in this round:\n2010**7781 | 2006**5818 | 2004**7452",
            formattedText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PagedHtmlArticles_SkipsMissingDetailArticleAndContinues()
    {
        const string list = """
            <div class="listNews">
              <a href="/en/article/news/gameevent/20260821/263696.html"><h2 class="title">Removed duplicate</h2></a>
              <a href="/en/article/news/gameevent/20260821/263686.html"><h2 class="title">Valid announcement</h2></a>
            </div>
            """;
        var logger = new RecordingLogger<PagedHtmlArticleFeedHandler>();
        var client = ClientRouting(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("index.html", StringComparison.Ordinal) => Response(list, "text/html"),
            var path when path.EndsWith("263696.html", StringComparison.Ordinal) =>
                new HttpResponseMessage(HttpStatusCode.NotFound),
            _ => Response("<main class=\"articleContent\"><p>Available content.</p></main>", "text/html"),
        });
        var definition = new SourceDefinition
        {
            Key = "official-neverness-to-everness",
            Game = GameKey.NevernessToEverness,
            Trust = SourceTrust.Official,
            Handler = PagedHtmlArticleFeedHandler.HandlerKey,
            Url = new Uri("https://nte.perfectworld.com/en/article/news/index.html"),
            HtmlArticle = new HtmlArticleRules
            {
                ItemSelector = ".listNews > a[href]",
                ExternalIdPattern = @"/(\d+)\.html$",
                TitleSelector = ".title",
                DetailContentSelector = ".articleContent",
                PaginationUrlTemplate = "https://nte.perfectworld.com/en/article/news/index{pageSuffix}.html",
                MaximumPages = 1,
            },
        };

        var items = await ReadAllAsync(ConfiguredSource(
            definition,
            new PagedHtmlArticleFeedHandler(
                client,
                new StubSourceContentLookup(string.Empty, string.Empty),
                logger)));

        var item = Assert.Single(items);
        Assert.Equal("263686", item.ExternalId);
        Assert.Contains(logger.Messages, message => message.Contains("skipping missing article 263696", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PagedHtmlArticles_CleanBaselineReadsAllTwentySixPages()
    {
        var listRequests = 0;
        var detailRequests = 0;
        var logger = new RecordingLogger<PagedHtmlArticleFeedHandler>();
        var client = ClientRouting(request =>
        {
            var fileName = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
            if (!fileName.StartsWith("index", StringComparison.Ordinal))
            {
                detailRequests++;
                return Response("<main class=\"articleContent\"><p>Article body.</p></main>", "text/html");
            }

            listRequests++;
            var suffix = fileName["index".Length..];
            var page = suffix.Length == 0 ? 0 : int.Parse(suffix, CultureInfo.InvariantCulture);
            if (page >= 26)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var anchors = string.Join('\n', Enumerable.Range(1, 3).Select(offset =>
            {
                var externalId = page * 3 + offset;
                return $$"""
                    <a href="/en/article/news/gamenews/20260814/{{externalId}}.html">
                      <h2 class="title">Article {{externalId}}</h2>
                    </a>
                    """;
            }));
            return Response($$"""<div class="listNews">{{anchors}}</div>""", "text/html");
        });
        var definition = new SourceDefinition
        {
            Key = "official-neverness-to-everness",
            Game = GameKey.NevernessToEverness,
            Trust = SourceTrust.Official,
            Handler = PagedHtmlArticleFeedHandler.HandlerKey,
            Url = new Uri("https://nte.perfectworld.com/en/article/news/index.html"),
            HtmlArticle = new HtmlArticleRules
            {
                ItemSelector = ".listNews > a[href]",
                ExternalIdPattern = @"/(\d+)\.html$",
                TitleSelector = ".title",
                DetailContentSelector = ".articleContent",
                PaginationUrlTemplate = "https://nte.perfectworld.com/en/article/news/index{pageSuffix}.html",
                MaximumPages = 100,
                StopWhenKnown = true,
                StopOnMissingPage = true,
            },
        };
        var source = ConfiguredSource(
            definition,
            new PagedHtmlArticleFeedHandler(
                client,
                new StubSourceContentLookup(string.Empty, string.Empty),
                logger));

        var items = await ReadAllAsync(source);

        Assert.Equal(78, items.Count);
        Assert.Equal(78, items.Select(item => item.ExternalId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(27, listRequests);
        Assert.Equal(78, detailRequests);
        Assert.Contains(logger.Messages, message => message.Contains(
            "fetching page 26",
            StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains(
            "prepared item 78",
            StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains(
            "page 27 was not found",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task PagedHtmlArticles_ExtractsYouTubeIframeFromCapturedOfficialMarkup()
    {
        const string list = """
            <div class="listNews">
              <a href="/en/article/news/gamenews/20250625/257051.html">
                <h2 class="title">NTE Opening Animation | Hypervortex Before the Storm</h2>
                <p class="date">2025-06-25</p><p class="type">News</p>
              </a>
            </div>
            """;
        var detail = TestSourceFixture.Read("neverness-to-everness", "article-257051-video.html");
        var client = ClientRouting(request => request.RequestUri!.AbsolutePath.EndsWith("index.html", StringComparison.Ordinal)
            ? Response(list, "text/html")
            : Response(detail, "text/html"));
        var definition = new SourceDefinition
        {
            Key = "official-neverness-to-everness",
            Game = GameKey.NevernessToEverness,
            Trust = SourceTrust.Official,
            Handler = PagedHtmlArticleFeedHandler.HandlerKey,
            Url = new Uri("https://nte.perfectworld.com/en/article/news/index.html"),
            HtmlArticle = new HtmlArticleRules
            {
                ItemSelector = ".listNews > a[href]",
                ExternalIdPattern = @"/(\d+)\.html$",
                TitleSelector = ".title",
                CategorySelector = ".type",
                PublishedAtSelector = ".date",
                PublishedAtFormat = "yyyy-MM-dd",
                DetailPublishedAtSelector = ".articleDate",
                DetailPublishedAtFormat = "yyyy.MM.dd",
                DetailContentSelector = ".articleContent",
                PaginationUrlTemplate = "https://nte.perfectworld.com/en/article/news/index{pageSuffix}.html",
                FirstPage = 0,
                MaximumPages = 1,
                StopWhenKnown = false,
            },
        };
        var source = ConfiguredSource(
            definition,
            new PagedHtmlArticleFeedHandler(client, new StubSourceContentLookup(string.Empty, string.Empty)));

        var item = Assert.Single(await ReadAllAsync(source));

        var link = Assert.Single(item.Document.Blocks.OfType<LinkBlock>(), link =>
            link.Url.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("YouTube: YouTube video player", link.Label);
        Assert.Equal("https://www.youtube.com/watch?v=6M7b_00KDgk", link.Url.AbsoluteUri);
        Assert.Equal(new DateTimeOffset(2025, 6, 25, 0, 0, 0, TimeSpan.Zero), item.PublishedAtUtc);
    }

    [Fact]
    public async Task PagedHtmlArticles_UsesDetailDateAndStopsCleanlyWhenNextPageIsMissing()
    {
        const string list = """
            <div class="listNews">
              <a href="/en/article/news/gamebroad/20260602/262479.html">
                <h2 class="title">Version 1.1 Update Notes</h2><p class="type">News</p>
              </a>
            </div>
            """;
        var detail = TestSourceFixture.Read("neverness-to-everness", "article-262479.html");
        var requests = new List<Uri>();
        var client = ClientRouting(request =>
        {
            requests.Add(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("index.html", StringComparison.Ordinal))
            {
                return Response(list, "text/html");
            }

            if (path.EndsWith("262479.html", StringComparison.Ordinal))
            {
                return Response(detail, "text/html");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var definition = new SourceDefinition
        {
            Key = "official-neverness-to-everness",
            Game = GameKey.NevernessToEverness,
            Trust = SourceTrust.Official,
            Handler = PagedHtmlArticleFeedHandler.HandlerKey,
            Url = new Uri("https://nte.perfectworld.com/en/article/news/index.html"),
            HtmlArticle = new HtmlArticleRules
            {
                ItemSelector = ".listNews > a[href]",
                ExternalIdPattern = @"/(\d+)\.html$",
                TitleSelector = ".title",
                CategorySelector = ".type",
                PublishedAtSelector = ".date",
                PublishedAtFormat = "yyyy-MM-dd",
                DetailPublishedAtSelector = ".articleDate",
                DetailPublishedAtFormat = "yyyy.MM.dd",
                DetailContentSelector = ".articleContent",
                PaginationUrlTemplate = "https://nte.perfectworld.com/en/article/news/index{pageSuffix}.html",
                FirstPage = 0,
                MaximumPages = 100,
                StopWhenKnown = false,
                StopOnMissingPage = true,
            },
        };
        var source = ConfiguredSource(
            definition,
            new PagedHtmlArticleFeedHandler(client, new StubSourceContentLookup(string.Empty, string.Empty)));

        var item = Assert.Single(await ReadAllAsync(source));

        Assert.Equal(new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero), item.PublishedAtUtc);
        Assert.Contains(requests, uri => uri.AbsolutePath.EndsWith("index1.html", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PagedHtmlArticles_IgnoresConfiguredTechnicalImageHosts()
    {
        const string list = """
            <div class="listNews"><a href="/en/article/news/gamebroad/20260428/261969.html">
              <h2 class="title">NTE — PlayStation 5 FAQ</h2><p class="date">2026-04-28</p>
            </a></div>
            """;
        var detail = TestSourceFixture.Read("neverness-to-everness", "article-261969.html");
        var client = ClientRouting(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path.EndsWith("index.html", StringComparison.Ordinal) => Response(list, "text/html"),
            var path when path.EndsWith("261969.html", StringComparison.Ordinal) => Response(detail, "text/html"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var definition = new SourceDefinition
        {
            Key = "official-neverness-to-everness",
            Game = GameKey.NevernessToEverness,
            Trust = SourceTrust.Official,
            Handler = PagedHtmlArticleFeedHandler.HandlerKey,
            Url = new Uri("https://nte.perfectworld.com/en/article/news/index.html"),
            HtmlArticle = new HtmlArticleRules
            {
                ItemSelector = ".listNews > a[href]",
                ExternalIdPattern = @"/(\d+)\.html$",
                TitleSelector = ".title",
                PublishedAtSelector = ".date",
                PublishedAtFormat = "yyyy-MM-dd",
                DetailContentSelector = ".articleContent",
                PaginationUrlTemplate = "https://nte.perfectworld.com/en/article/news/index{pageSuffix}.html",
                MaximumPages = 2,
                StopWhenKnown = false,
                IgnoredImageHosts = ["docs.oa.wanmei.net"],
            },
        };
        var source = ConfiguredSource(
            definition,
            new PagedHtmlArticleFeedHandler(client, new StubSourceContentLookup(string.Empty, string.Empty)));

        var item = Assert.Single(await ReadAllAsync(source));
        var images = item.Document.Blocks.OfType<ImageBlock>().ToArray();

        Assert.NotEmpty(images);
        Assert.DoesNotContain(images, image => image.Url.Host == "docs.oa.wanmei.net");
        Assert.Contains(images, image => image.Url.Host == "ntevmg.perfectworld.com");
    }

    [Fact]
    public async Task RenderedHtmlCodes_SeparatesCurrentAndPermanentAndIgnoresAllActiveFallback()
    {
        var html = TestSourceFixture.Read("game8", "nte-rendered-fragment.html");
        var definition = new SourceDefinition
        {
            Key = "game8-neverness-to-everness-redeem-codes",
            Game = GameKey.NevernessToEverness,
            Trust = SourceTrust.ReviewRequired,
            Handler = RenderedHtmlCodeHandler.HandlerKey,
            Url = new Uri("https://game8.co/games/Neverness-to-Everness/archives/593718"),
            BrowserCollection = new BrowserCollectionRules
            {
                ReadySelector = "input.a-clipboard__textInput",
                SectionHeadingSelector = "h2, h3",
                SectionHeadingContains = ["Redeem Codes"],
                SectionHeadingExcludes = ["Expired"],
                CurrentSectionHeadingContains = ["Version"],
                PermanentSectionHeadingContains = ["All Active Redeem Codes"],
                ItemSelector = "input.a-clipboard__textInput",
                ValueAttribute = "value",
                RowSelector = "tr",
                ExpirySelector = ".a-red",
                ExpiryPattern = @"Expiry Date:\s*(?<value>\d{2}/\d{2}/\d{4}|TBD|TBA)",
                ExpiryDateFormats = ["MM/dd/yyyy"],
                UnknownExpiryValues = ["TBD"],
                PermanentExpiryValues = ["TBA"],
                RewardItemSelector = "td:nth-child(2) .align",
                CurrentAggregateExternalId = "aggregate:current",
                PermanentAggregateExternalId = "aggregate:permanent",
                PermanentTitle = "Permanent Redeem Codes",
            },
        };
        var source = ConfiguredSource(
            definition,
            new RenderedHtmlCodeHandler(
                new StubRenderedPageClient(html),
                new FixedTimeProvider(new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero))));

        var items = await ReadAllAsync(source);

        var current = Assert.Single(items, candidate => candidate.ExternalId == "aggregate:current");
        Assert.Equal("All Version 1.3 Redeem Codes", current.Title);
        Assert.Empty(current.Document.Blocks.OfType<CodeBlock>());
        Assert.True(current.ExpiresAtUtc < new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));
        Assert.DoesNotContain(current.Document.Blocks.OfType<CodeBlock>(), block => block.Code == "WELCOMETONTE");
        var permanent = Assert.Single(items, candidate => candidate.ExternalId == "aggregate:permanent");
        Assert.Equal("Permanent Redeem Codes", permanent.Title);
        Assert.Equal(["NTENENE"], permanent.Document.Blocks.OfType<CodeBlock>().Select(block => block.Code));
        var archived = Assert.Single(items, candidate => candidate.ExternalId == "FOGDENGAME");
        Assert.True(archived.ExpiresAtUtc < new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));
        Assert.Contains(archived.Document.Blocks.OfType<KeyValueBlock>(), block => block.Items.Any(field =>
            field.Key == "Rewards" && field.Value.Contains("Annulith x100", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RenderedHtmlCodes_UsesAllActiveSectionWhenCurrentVersionSectionIsMissing()
    {
        const string html = """
            <main>
              <h2>All Neverness to Everness Redeem Codes</h2>
              <h2>All Active Redeem Codes</h2>
              <table>
                <tr>
                  <td>
                    <input class="a-clipboard__textInput" type="text" value="WITCHHOUSE" readonly>
                    <span class="a-red">Expiry Date: 09/20/2026</span>
                  </td>
                  <td><div class="align">・ Annulith x100</div></td>
                </tr>
                <tr>
                  <td>
                    <input class="a-clipboard__textInput" type="text" value="COMEBACK" readonly>
                    <span class="a-red">Expiry Date: TBD</span>
                  </td>
                  <td><div class="align">・ Fabricated Dice x1</div></td>
                </tr>
                <tr>
                  <td>
                    <input class="a-clipboard__textInput" type="text" value="NTENENE" readonly>
                    <span class="a-red">Expiry Date: TBA</span>
                  </td>
                  <td><div class="align">・ Fons x10,000</div></td>
                </tr>
              </table>
              <h2>Neverness to Everness Expired Codes</h2>
            </main>
            """;
        var definition = new SourceDefinition
        {
            Key = "game8-neverness-to-everness-redeem-codes",
            Game = GameKey.NevernessToEverness,
            Trust = SourceTrust.ReviewRequired,
            Handler = RenderedHtmlCodeHandler.HandlerKey,
            Url = new Uri("https://game8.co/games/Neverness-to-Everness/archives/593718"),
            BrowserCollection = new BrowserCollectionRules
            {
                ReadySelector = "input.a-clipboard__textInput",
                SectionHeadingSelector = "h2, h3",
                SectionHeadingContains = ["Redeem Codes"],
                SectionHeadingExcludes = ["Expired"],
                CurrentSectionHeadingContains = ["Version"],
                PermanentSectionHeadingContains = ["All Active Redeem Codes"],
                ItemSelector = "input.a-clipboard__textInput",
                ValueAttribute = "value",
                RowSelector = "tr",
                ExpirySelector = ".a-red",
                ExpiryPattern = @"Expiry Date:\s*(?<value>\d{2}/\d{2}/\d{4}|TBD|TBA)",
                ExpiryDateFormats = ["MM/dd/yyyy"],
                UnknownExpiryValues = ["TBD"],
                PermanentExpiryValues = ["TBA"],
                RewardItemSelector = "td:nth-child(2) .align",
                CurrentAggregateExternalId = "aggregate:current",
                PermanentAggregateExternalId = "aggregate:permanent",
                PermanentTitle = "Permanent Redeem Codes",
            },
        };
        var source = ConfiguredSource(
            definition,
            new RenderedHtmlCodeHandler(
                new StubRenderedPageClient(html),
                new FixedTimeProvider(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero))));

        var items = await ReadAllAsync(source);

        var current = Assert.Single(items, item => item.ExternalId == "aggregate:current");
        Assert.Equal("All Active Redeem Codes", current.Title);
        Assert.Equal(
            ["WITCHHOUSE", "COMEBACK"],
            current.Document.Blocks.OfType<CodeBlock>().Select(block => block.Code));
        var currentBlocks = current.Document.Blocks.ToArray();
        var witchHouseIndex = Array.FindIndex(currentBlocks, block =>
            block is CodeBlock code && code.Code == "WITCHHOUSE");
        Assert.True(witchHouseIndex >= 0);
        var witchHouseDetails = Assert.IsType<KeyValueBlock>(currentBlocks[witchHouseIndex + 1]);
        Assert.Contains(witchHouseDetails.Items, item =>
            item.Key == "Rewards" && item.Value.Contains("Annulith x100", StringComparison.Ordinal));
        Assert.Contains(witchHouseDetails.Items, item =>
            item.Key == "Expires" && item.Value.Contains("20.09.2026", StringComparison.Ordinal));
        var permanent = Assert.Single(items, item => item.ExternalId == "aggregate:permanent");
        Assert.Equal(["NTENENE"], permanent.Document.Blocks.OfType<CodeBlock>().Select(block => block.Code));
    }

    [Fact]
    public async Task RenderedHtmlCodes_SeparatesDatedAndPermanentCodesAndFormatsDiscordExpiry()
    {
        var html = TestSourceFixture.Read("game8", "wuwa-rendered-fragment.html");
        var definition = new SourceDefinition
        {
            Key = "game8-wuthering-waves-redeem-codes",
            Game = GameKey.WutheringWaves,
            Trust = SourceTrust.ReviewRequired,
            Handler = RenderedHtmlCodeHandler.HandlerKey,
            Url = new Uri("https://game8.co/games/Wuthering-Waves/archives/453149"),
            BrowserCollection = new BrowserCollectionRules
            {
                ReadySelector = "input.a-clipboard__textInput",
                SectionHeadingSelector = "h2, h3",
                SectionHeadingContains = ["Codes"],
                SectionHeadingExcludes = ["Expired"],
                CurrentSectionHeadingContains = ["All Active"],
                PermanentSectionHeadingContains = ["List of All Active Redeem Codes"],
                ItemSelector = "input.a-clipboard__textInput",
                ValueAttribute = "value",
                RowSelector = "tr",
                ExpirySelector = "td:nth-child(2)",
                ExpiryPattern = @"Expiry:\s*(?<value>[A-Za-z]+ \d{1,2}, \d{4})",
                ExpiryMarker = "Expiry:",
                ExpiryDateFormats = ["MMMM d, yyyy"],
                MissingExpiryIsActive = true,
                MissingExpiryDisplay = "No expiry announced",
                RewardItemSelector = "td:nth-child(2) .align",
                CurrentAggregateExternalId = "aggregate:current",
                PermanentAggregateExternalId = "aggregate:permanent",
                PermanentTitle = "Permanent Redeem Codes",
            },
        };
        var source = ConfiguredSource(
            definition,
            new RenderedHtmlCodeHandler(
                new StubRenderedPageClient(html),
                new FixedTimeProvider(new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero))));

        var items = await ReadAllAsync(source);
        var current = Assert.Single(items, item => item.ExternalId == "aggregate:current");
        var permanent = Assert.Single(items, item => item.ExternalId == "aggregate:permanent");

        Assert.Equal(["F5F4D3B2A2"], current.Document.Blocks.OfType<CodeBlock>().Select(block => block.Code));
        Assert.Equal(["WUTHERINGGIFT"], permanent.Document.Blocks.OfType<CodeBlock>().Select(block => block.Code));
        var details = permanent.Document.Blocks.OfType<KeyValueBlock>().ToArray();
        Assert.Contains(details, block => block.Items.Any(field =>
            field.Key == "Rewards" && field.Value.Contains("Shell Credit x15,000", StringComparison.Ordinal)));
        Assert.Contains(details, block => block.Items.Any(field =>
            field.Key == "Expires" && field.Value == "No expiry announced"));
        Assert.Contains(current.Document.Blocks.OfType<KeyValueBlock>(), block => block.Items.Any(field =>
            field.Key == "Expires" && field.Value == "19.08.2026 (<t:1787184000:R>)"));
    }

    [Fact]
    public async Task RenderedHtmlCodes_HashIgnoresElapsedTimeAndUnextractedHtmlChanges()
    {
        var html = TestSourceFixture.Read("game8", "wuwa-rendered-fragment.html");
        var changedChrome = html.Replace("</main>", "<div class=\"comments\">99 comments</div></main>", StringComparison.Ordinal);
        var definition = WuwaCodeDefinition();
        var first = ConfiguredSource(definition, new RenderedHtmlCodeHandler(
            new StubRenderedPageClient(html),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero))));
        var second = ConfiguredSource(definition, new RenderedHtmlCodeHandler(
            new StubRenderedPageClient(changedChrome),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero))));

        var firstCurrent = Assert.Single(await ReadAllAsync(first), item => item.ExternalId == "aggregate:current");
        var secondCurrent = Assert.Single(await ReadAllAsync(second), item => item.ExternalId == "aggregate:current");

        Assert.Equal(firstCurrent.Document.Hash, secondCurrent.Document.Hash);
    }

    [Fact]
    public void SourceDefinitionCatalog_RejectsHandlerWithoutItsRules()
    {
        const string json = """
            { "SourceDefinitions": [{
              "Key": "broken", "Game": "WutheringWaves", "Trust": "Official",
              "Handler": "json-article-feed", "Url": "https://example.test/feed.json"
            }] }
            """;

        var exception = Assert.Throws<InvalidOperationException>(() => SourceDefinitionCatalog.Parse(json));

        Assert.Contains("JsonArticle", exception.Message, StringComparison.Ordinal);
    }

    private static ConfiguredGameContentSource ConfiguredSource(
        SourceDefinition definition,
        ISourceHandler handler) =>
        new ConfiguredGameContentSource(definition, new SourceHandlerResolver([handler]));

    private static SourceDefinition WuwaCodeDefinition() => new()
    {
        Key = "game8-wuthering-waves-redeem-codes",
        Game = GameKey.WutheringWaves,
        Trust = SourceTrust.ReviewRequired,
        Handler = RenderedHtmlCodeHandler.HandlerKey,
        Url = new Uri("https://game8.co/games/Wuthering-Waves/archives/453149"),
        BrowserCollection = new BrowserCollectionRules
        {
            ReadySelector = "input.a-clipboard__textInput",
            SectionHeadingSelector = "h2, h3",
            SectionHeadingContains = ["Codes"],
            SectionHeadingExcludes = ["Expired"],
            CurrentSectionHeadingContains = ["All Active"],
            PermanentSectionHeadingContains = ["List of All Active Redeem Codes"],
            ItemSelector = "input.a-clipboard__textInput",
            ValueAttribute = "value",
            RowSelector = "tr",
            ExpirySelector = "td:nth-child(2)",
            ExpiryPattern = @"Expiry:\s*(?<value>[A-Za-z]+ \d{1,2}, \d{4})",
            ExpiryMarker = "Expiry:",
            ExpiryDateFormats = ["MMMM d, yyyy"],
            MissingExpiryIsActive = true,
            MissingExpiryDisplay = "No expiry announced",
            RewardItemSelector = "td:nth-child(2) .align",
            CurrentAggregateExternalId = "aggregate:current",
            PermanentAggregateExternalId = "aggregate:permanent",
            PermanentTitle = "Permanent Redeem Codes",
        },
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static HttpClient ClientRouting(Func<HttpRequestMessage, HttpResponseMessage> route) =>
        new(new RoutingResponseHandler(route));

    private static HttpResponseMessage Response(string content, string mediaType) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, mediaType) };

    private static async Task<List<SourceContentSnapshot>> ReadAllAsync(
        ConfiguredGameContentSource source)
    {
        var result = new List<SourceContentSnapshot>();
        await foreach (var item in source.FetchAsync(TestContext.Current.CancellationToken))
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class RoutingResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(route(request));
    }

    private sealed class StubSourceContentLookup(string sourceKey, string externalId) : ISourceContentLookup
    {
        public Task<bool> ExistsAsync(
            string candidateSourceKey,
            string candidateExternalId,
            CancellationToken cancellationToken) => Task.FromResult(
                string.Equals(candidateSourceKey, sourceKey, StringComparison.Ordinal) &&
                string.Equals(candidateExternalId, externalId, StringComparison.Ordinal));

        public Task<bool> NeedsContentRefreshAsync(
            string candidateSourceKey,
            string candidateExternalId,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class BatchOnlySourceContentLookup(
        IReadOnlyDictionary<string, SourceContentState> states) : ISourceContentLookup
    {
        public int BatchCalls { get; private set; }

        public Task<IReadOnlyDictionary<string, SourceContentState>> GetContentStatesAsync(
            string sourceKey,
            IReadOnlyCollection<string> externalIds,
            CancellationToken cancellationToken)
        {
            BatchCalls++;
            return Task.FromResult(states);
        }

        public Task<bool> ExistsAsync(
            string sourceKey,
            string externalId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("JSON feeds must use the batch lookup.");

        public Task<bool> NeedsContentRefreshAsync(
            string sourceKey,
            string externalId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("JSON feeds must use the batch lookup.");
    }

    private sealed class StubRenderedPageClient(string html) : IRenderedPageClient
    {
        public Task<string> GetContentAsync(
            RenderedPageRequest request,
            CancellationToken cancellationToken) => Task.FromResult(html);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
