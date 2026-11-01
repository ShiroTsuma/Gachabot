# Gachabot

Gachabot is a self-hosted Discord bot and dashboard for publishing updates and in-game events from gacha games. It currently supports Wuthering Waves and Neverness to Everness, keeps a publication history for every guild, and lets each guild choose its own channel, games, and event reminders.

The project is not affiliated with Discord, Kuro Games, Hotta Studio, Game8, or WuWa Tracker.

## What it does

- imports configured official announcements and event calendars;
- publishes posts independently to each configured Discord guild;
- shows imported content, scheduled posts, sent messages, sources, and guild configuration in a web dashboard;
- deduplicates source updates, retains revisions, and retries failed Discord deliveries;
- supports per-guild start and end-event reminder offsets;
- stores data in PostgreSQL and media in an S3-compatible bucket.

## Requirements

- Docker Engine with Docker Compose, recommended for a permanent deployment;
- PostgreSQL available to the bot;
- an S3-compatible object store such as Garage or MinIO, with a bucket for media;
- a Discord application with a bot and OAuth2 credentials;
- a public HTTPS address if the dashboard is used outside localhost.

To run from source instead, install .NET SDK 10.0.302 (or a compatible 10.0.x SDK). The rendered Game8 sources also require Chromium through Playwright.

## Start with Docker Compose

The compose file starts Gachabot and a Caddy HTTPS reverse proxy. PostgreSQL and S3 storage are deliberately external services, so provide their addresses in `.env`.

```powershell
Copy-Item .env.example .env
# Edit .env and replace every replace-with-* value.
docker compose up --build -d
docker compose logs -f gachabot
```

Open `https://dockers.lan` for the default LAN configuration. PostgreSQL and S3 must be reachable and configured before startup.

Follow [installation and Discord setup](docs/installation.md) to configure credentials, trust the local TLS certificate, set up OAuth and connect your first guild. For a public deployment, that guide also covers replacing the LAN hostname and enabling public TLS.

## Documentation

| Guide | Contents |
| --- | --- |
| [Installation](docs/installation.md) | Environment settings, TLS, Discord application and first connection |
| [User guide](docs/user-guide.md) | Dashboard and guild workflows |
| [Architecture](docs/architecture.md) | Components and data flow |
| [Operations](docs/operations.md) | Deployment and maintenance |
| [Sources](docs/sources.md) | Source configuration and parsing |

## Configure a guild

Run these slash commands in each guild. They are intentionally restricted to members with **Manage Server**.

```text
/gachabot-configure channel:#updates wuthering-waves:true neverness-to-everness:true delete-obsolete-posts:true
/gachabot-event-schedule start-before-hours:0 end-before-hours:48
/gachabot-subjects game:Wuthering Waves
/gachabot-subjects game:Neverness to Everness
/gachabot-enable
/gachabot-status
```

`start-before-hours` and `end-before-hours` accept `-1` to disable that type of event post, or values from `0` to `72`, including fractions such as `0.5`. The defaults publish an event at its start and send an end reminder 48 hours before it finishes. Use `/gachabot-disable` to pause a guild or `/gachabot-enable` to resume it.

`delete-obsolete-posts:true` is a per-guild setting on `/gachabot-configure`. It removes only messages posted by GachaBot after their source content expires or is archived; messages from people and other bots are never touched. Omit the option during later configuration changes to keep the existing setting.

`/gachabot-subjects` opens a private interactive selector for one game. Toggle the subjects you want (events, codes, news, updates, maintenance, characters, and announcements), then press **Save subjects**. Subject choices are stored separately for each game and affect both future posts and pending event reminders.

`/gachabot-configure` saves a new guild in the paused state, so you can choose the channel, games, schedule, and subjects before anything is posted. `/gachabot-enable` starts publications and queues currently active events that have not already been published for that guild. Reconfiguring a guild that is already enabled leaves it enabled. Each guild has separate delivery and retry state, so a failure on one server cannot block another.

## Dashboard and sources

The dashboard is the operational view: use **Guilds** to inspect channels, selected games, queued posts, and delivered messages; use **Events** for the calendar and source filtering; use **Content** to inspect imported blocks and manual posts.

The source registry is [`src/GachaBot.Web/source-definitions.json`](src/GachaBot.Web/source-definitions.json). It contains the source address, parser, and field rules. Enable or disable a source operationally through the `Sources` section of `appsettings.json` or environment variables. Check each source's terms and access rules before enabling it in your own deployment. More source details are in [docs/sources.md](docs/sources.md); architecture and operations are in [docs/architecture.md](docs/architecture.md) and [docs/operations.md](docs/operations.md).

The first scan of official announcement sources is treated as a baseline and does not flood a new channel with old announcements. Event timelines are handled separately and can schedule active or upcoming events according to each guild's configuration.

## Run from source

Configure PostgreSQL and S3 values exactly as for Docker, then run:

```powershell
dotnet restore GachaBot.slnx --configfile NuGet.Config
dotnet build GachaBot.slnx --no-restore
dotnet test GachaBot.slnx --no-build --no-restore

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Discord__Enabled = 'false'
$env:Workers__Enabled = 'false'
dotnet run --no-build --no-restore --project src/GachaBot.Web
```

The development profile allows anonymous dashboard access by default and disables automatic workers. Imported sources can still be refreshed manually in the dashboard. To enable Game8 sources locally after the first build, install the pinned Playwright browser:

```powershell
& 'src/GachaBot.Web/bin/Debug/net10.0/playwright.ps1' install chromium
```

`BrowserAutomation__ChromiumSandbox` defaults to `true` for a local host. Docker Compose sets it to `false`, because the Playwright image otherwise cannot start Chromium on hosts that disable unprivileged user namespaces.

## License

The project is source-available under the [LICENSE](LICENSE). Individuals may run an unmodified instance for personal, non-commercial use; redistribution, commercial use, and distributing modified versions require written permission from ShiroTsuma.
