# Sources

[Back to the documentation index](README.md)

Every source is a JSON definition in `src/GachaBot.Web/source-definitions.json` handled by a small, generic handler. Fetching and parsing are separate from change detection and publishing policy.

| Key | Game | Reads | Trust | Handler |
| --- | --- | --- | --- | --- |
| `official-wuthering-waves` | Wuthering Waves | Kuro Games' `ArticleMenu.json` and per-article JSON | `Official` | `json-article-feed` |
| `official-neverness-to-everness` | NTE | Perfect World's paginated news list and article pages | `Official` | `paged-html-article-feed` |
| `wuwatracker-wuthering-waves-events` | Wuthering Waves | [WuWa Tracker timeline](https://wuwatracker.com/pl/timeline) | `Trusted` | `wuthering-waves-timeline` |
| `game8-neverness-to-everness-events` | NTE | [Game8 event calendar](https://game8.co/games/Neverness-to-Everness/archives/592073) | `Trusted` | `game8-event-calendar` |
| `game8-wuthering-waves-redeem-codes` | Wuthering Waves | [Game8 codes page](https://game8.co/games/Wuthering-Waves/archives/453149) | `Trusted` | `rendered-html-code-collection` |
| `game8-neverness-to-everness-redeem-codes` | NTE | [Game8 codes page](https://game8.co/games/Neverness-to-Everness/archives/593718) | `Trusted` | `rendered-html-code-collection` |

**Trust** decides what happens after the baseline: `Official` and `Trusted` sources post new and changed content automatically; `ReviewRequired` sources save drafts that wait for **Approve & publish**; `Disabled` sources are skipped.

## Shared behavior

- **Baseline.** The first successful scan stores everything and posts nothing. Later scans compare each item's identity `(source, external ID)` and document hash; only a changed title or document creates a revision and a post.
- **Normalized hashing.** Pages are converted to blocks before hashing. Paragraphs, `<br>`, list items and table rows and cells are preserved. Discord timestamps use the stored Unix time, so the passing of days does not change a hash.
- **Isolated failures.** A `403`, timeout, changed selector or invalid JSON fails only that source. The error and attempt time are stored in `SourceStates`, shown on **Sources**, and the last good data stays in place.
- **Classification.** Article titles map to maintenance, update, character or news. An article *about* events is news; only calendar sources create events.

## Source notes

**Wuthering Waves news.** The raw `ArticleMenu.json` is not in date order: pinned old articles can come before the newest one. The handler parses `startTime`, sorts newest first, and keeps scanning past known items (`StopWhenKnown: false`), so a pinned article cannot hide a newer one. State is checked in bulk, and only new or repairable items fetch their detail JSON; an unchanged scan of the 600+ items costs two state queries.

**NTE news.** Pages are read newest first until the first known article; a missing next page (`404`) ends pagination normally. The date comes from the list tile (`yyyy-MM-dd`) or, as a fallback, the article header (`yyyy.MM.dd`). Duplicate technical images from `docs.oa.wanmei.net` are dropped (`IgnoredImageHosts`), and titles starting with "Account Action Notice" are skipped (`IgnoredTitlePrefixes`).

**WuWa Tracker events.** Banners and activities are read with their official links. Start and end are normalized to the 04:00 server reset in UTC+8; the hour embedded in the source payload is not used.

**Game8 event calendar.** Only the "List of Current Events" and "List of Upcoming Events" tables are read; permanent and previous events are excluded. Game8 gives dates without hours, so events start and end at the configured server-day start. Dates such as "December 28 - January 11, 2027" omit the start year and are resolved to the previous year. Each item's identifier includes its start date, so the next edition of a recurring event is a separate item.

**Game8 redeem codes.** Game8 can answer `202` and render the page only after a JavaScript challenge, so these sources use Playwright and wait for a configured element. Codes, rewards and expiry dates are read from table rows. Each game produces two lists: current codes (`aggregate:current`) and permanent codes (`aggregate:permanent`). When the page has no version-specific section, dated codes under "All Active Redeem Codes" count as current. An expired code is kept as an archived item with its rewards, and codes with an unrecognized expiry are not published.

## Handlers

| Handler | Use for |
| --- | --- |
| `json-article-feed` | A JSON list with an optional JSON detail per item |
| `paged-html-article-feed` | An HTML list with numbered pages and HTML article pages |
| `wuthering-waves-timeline` | The WuWa Tracker timeline |
| `game8-event-calendar` | Game8 tables of dated events, rendered with Playwright |
| `rendered-html-code-collection` | Pages that need a browser, read into code lists by selectors |

## Tests

Real responses are stored in `test-sources/` and copied to the test output; parser tests never use the network. Keep the original URL and capture date in [test-sources/README.md](../test-sources/README.md) when refreshing a fixture.

`Game8LiveContractTests` are marked explicit. They load the production `source-definitions.json`, start Chromium and check Game8's live markup; see the test-sources README for the command.

## Adding a source

1. If the site fits an existing handler, add a definition to `source-definitions.json` with a stable `Key`, the game, trust, URL and selector or field rules.
2. Optionally override a source per instance without editing the definition: `Sources__<key>__Trust=ReviewRequired` changes its trust, `Sources__<key>__Enabled=false` turns it off.
3. Save a real response under `test-sources/` and add a parser contract test.
4. Write a new `ISourceHandler` only for a new kind of transport or navigation.
