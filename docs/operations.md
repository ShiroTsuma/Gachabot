# Operations

[Back to the documentation index](README.md)

## Where state lives

| Data | Location |
| --- | --- |
| Content, revisions, server settings, publication queue | PostgreSQL, one schema per source plus `manual` for posts written in the dashboard |
| Archived artwork | The S3 bucket |
| Data-protection keys, Chromium profile, temporary downloads | The `gachabot-data` volume under `/app/data` |

The app serves the dashboard on port `8791`; `/health` reports that the host is up.

## Update

```sh
git pull --ff-only
docker compose up -d --build
docker compose logs -f gachabot
```

Back up before updating (see below). After the restart, the log should show `Application started` and, within a minute, `Source polling completed for N sources; 0 failed`.

Schemas are created on start with `EnsureCreated` and small upgrade statements. A version that changes the data model must be preceded by a backup; numbered migrations are on the [roadmap](ROADMAP.md).

## Back up

For a single instance:

1. `docker compose stop gachabot`
2. Back up the PostgreSQL database and the bucket, and copy the `gachabot-data` volume.
3. `docker compose start gachabot`
4. Now and then, restore a backup into a separate database and volume to make sure it works.

## Diagnose

```sh
docker compose ps
docker compose logs --tail 200 gachabot
```

| Symptom | Where to look |
| --- | --- |
| Nothing was posted after the first start | Expected: the first scan of each source only records a baseline. |
| A source stopped updating | **Sources** in the dashboard shows the last import, the last failed attempt and its error. Game8 occasionally returns `403` or changes its markup; the other sources keep working. |
| A post did not reach a server | The server's **Queue and history** page lists pending, sent, failed and cancelled posts with their errors. Check `/gachabot-status` and the bot's channel permissions. |
| A post failed | Deliveries retry with backoff for about a day before they are marked `Failed`. Fix the cause (often a missing permission), then use **Publish again** or **Send to one guild only**. |
| The dashboard does not react to clicks | The browser could not load `_framework/blazor.web.js`. Rebuild the image from the current `Dockerfile`. |

Useful log lines: `Source polling completed` (one per poll), `Completed ingestion for source …` (per source counts), and `Discord publication … failed on attempt …`.

## Media archive

Artwork is downloaded into `/app/data/media-staging`, optimized and uploaded to the bucket; the temporary download is removed afterwards. Limits:

| Setting | Default | Meaning |
| --- | --- | --- |
| `MediaArchive__MaximumDownloadSizeMegabytes` | 50 | Largest image accepted from a source |
| `MediaArchive__MaximumStoredImageSizeMegabytes` | 9 | Target size after optimization; fits Discord's attachment limit |
| `MediaArchive__MaximumImageDimension` | 4096 | Longest side after optimization |
| `S3Media__GarbageCollectionGraceHours` | 24 | Age before an unreferenced object may be deleted |

An image that cannot be optimized safely is stored as the original and marked `Uncompressable`. Unreferenced objects are collected every six hours.

Versions released before November 2026 kept every temporary download. After upgrading such an instance, remove the leftovers once:

```sh
docker compose exec gachabot sh -c 'find /app/data/media-staging -name "*.tmp" -mmin +10 -delete'
```

`scripts/Migrate-MediaArchive.ps1` rebuilds archived copies, including those of archived content, without resetting the database. Run it with the bot stopped; without `-Apply` it only prints the plan, `-SourceKey` limits it to one source, and `-DeleteLegacy` removes the old hash-based folder after a successful move.

## Reset one source

Every source has its own PostgreSQL schema, for example `official-neverness-to-everness`. To start one source over: stop the app, back up, drop or empty that schema, and start again. The app recreates the tables, and the next scan records a new baseline.

## Secrets and access

Outside the development profile the app refuses to start without Discord OAuth. Server settings are changed only through the `gachabot-*` commands, which require **Manage Server**. The sign-in cookie is HttpOnly and SameSite=Lax and lasts eight hours. Keep the bot token, OAuth secret, database credentials and data-protection keys out of the repository and logs.
