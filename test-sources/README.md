# Source regression fixtures

These files are real responses from the supported sites. Offline parser tests use them, so a changed selector or data format is caught against real markup rather than a hand-simplified copy.

Captured on 2026-08-13 (Europe/Warsaw).

| File | Original URL | Notes |
| --- | --- | --- |
| `wuthering-waves/article-menu-3657.json` | `https://hw-media-cdn-mingchao.kurogame.com/akiwebsite/website2.0/json/G152/en/ArticleMenu.json` | One real record cut from the large list; shows the truncated `articleContent` |
| `wuthering-waves/article-3657.json` | `https://hw-media-cdn-mingchao.kurogame.com/akiwebsite/website2.0/json/G152/en/article/3657.json` | Full detail response |
| `neverness-to-everness/index.html` | `https://nte.perfectworld.com/en/article/news/index.html` | Full first page of the news list |
| `neverness-to-everness/index1.html` | `https://nte.perfectworld.com/en/article/news/index1.html` | Full second page of the news list |
| `neverness-to-everness/article-263505.html` | `https://nte.perfectworld.com/en/article/news/gamebroad/20260810/263505.html` | Full article page |
| `neverness-to-everness/article-262479.html` | `https://nte.perfectworld.com/en/article/news/gamebroad/20260602/262479.html` | Article page with a `yyyy.MM.dd` date in `.articleDate` |
| `neverness-to-everness/article-261969.html` | `https://nte.perfectworld.com/en/article/news/gamebroad/20260428/261969.html` | Article page with CDN images and a technical duplicate from `docs.oa.wanmei.net` |
| `neverness-to-everness/article-257051-video.html` | `https://nte.perfectworld.com/en/article/news/gamenews/20250625/257051.html` | Real article fragment with a production YouTube iframe |
| `game8/nte-rendered-fragment.html` | `https://game8.co/games/Neverness-to-Everness/archives/593718` | DOM fragment after the JavaScript challenge; selector and value confirmed in Chrome |
| `game8/wuwa-rendered-fragment.html` | `https://game8.co/games/Wuthering-Waves/archives/453149` | Dynamic DOM fragment: livestream codes, a shared `data-date` timestamp, rewards as a list, an event section and a permanent code |

`dotnet test` never downloads fixtures. Refresh them in a separate change, because a new snapshot can require new rules in `source-definitions.json`.

The live Game8 contract can be checked on demand. The test loads the production `source-definitions.json` and starts Chromium, so it is excluded from the default offline run:

```powershell
dotnet test tests/GachaBot.Infrastructure.IntegrationTests/GachaBot.Infrastructure.IntegrationTests.csproj --configuration Release --no-restore -- --explicit only --filter-class 'GachaBot.Infrastructure.IntegrationTests.Game8LiveContractTests'
```
