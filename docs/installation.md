# Installation

[Back to the documentation index](README.md)

This guide sets up a production instance: the bot, the dashboard behind HTTPS, and their storage. To look around first without any of this, use the [local preview](../README.md#try-it-locally).

## What you need

- A Linux host with Docker and Compose. The image bundles the .NET runtime and the Chromium build used for Game8 pages.
- A PostgreSQL database and a dedicated user for it.
- An S3-compatible bucket (for example Garage, SeaweedFS or MinIO) and an access key limited to that bucket.
- A Discord application, created below.
- A hostname for the dashboard. The default `Caddyfile` serves `dockers.lan` on a LAN with Caddy's internal certificate authority.

## 1. Create the Discord application

1. In the [Discord Developer Portal](https://discord.com/developers/applications), create an application and add a bot.
2. Copy the bot token; it goes into `Discord__BotToken`.
3. Under **OAuth2**, add the redirect URL Caddy serves: `https://dockers.lan/signin-discord`, or `https://your-domain/signin-discord` after changing `Caddyfile`. Do not use the internal port `8791` as the callback.
4. Copy the OAuth client ID and secret.
5. Generate an install URL with the `bot` and `applications.commands` scopes, and add the bot to your server.
6. In the destination channel, give the bot **View Channel**, **Send Messages**, **Embed Links**, **Attach Files** and **Read Message History**. It needs no server-wide permissions.

## 2. Configure `.env`

Copy `.env.example` to `.env`. It documents every setting; these are required:

| Setting | Purpose |
| --- | --- |
| `DatabaseStorage__ConnectionString` | PostgreSQL connection for content, server settings and the publication queue |
| `S3Media__Endpoint`, `S3Media__AccessKey`, `S3Media__SecretKey` | Address and credentials of the S3-compatible store |
| `S3Media__Bucket` | Existing bucket for archived artwork, for example `gachabot-media` |
| `Discord__BotToken` | Bot token from step 1 |
| `Discord__OAuth__ClientId`, `Discord__OAuth__ClientSecret` | OAuth2 credentials for signing in to the dashboard |

Optional: `Discord__ActivityName` changes the bot's activity text (default `Schmidley`). `SourcePolling__IntervalMinutes` changes how often sources are checked (default `60`).

Never commit `.env`, database backups or data-protection keys.

## 3. Set up HTTPS

The app listens on `127.0.0.1:8791` only; Caddy publishes it on ports 80 and 443 and redirects HTTP to HTTPS.

**On a LAN**, keep `tls internal` in `Caddyfile` and trust Caddy's root certificate once on every device that opens the dashboard:

```powershell
docker compose cp proxy:/data/caddy/pki/authorities/local/root.crt .\caddy-local-root.crt
Import-Certificate -FilePath .\caddy-local-root.crt -CertStoreLocation Cert:\CurrentUser\Root
```

**On the Internet**, replace `dockers.lan` with a DNS name that points to the host, remove `tls internal`, and keep ports 80 and 443 reachable. Caddy obtains and renews the certificate itself. Remember to update the OAuth redirect URL to match.

## 4. Start and connect

```sh
docker compose up --build -d
docker compose logs -f gachabot
```

1. Wait for `Application started` in the log. The health endpoint on the host is `http://localhost:8791/health`.
2. In Discord, run `/gachabot-configure` in your server. Whoever runs it can sign in to the dashboard; another administrator can run it again, without changing the channel, to get access too.
3. Open the dashboard and sign in with Discord. The first scan of each source records a baseline and posts nothing.
4. Choose subjects and reminders, then run `/gachabot-enable`. See [Configure a guild](../README.md#configure-a-guild).

Keep the `gachabot-data` volume. It holds the ASP.NET data-protection keys that keep sign-ins valid across restarts, and the Chromium profile. Back it up with PostgreSQL and the bucket; see [Operations](operations.md).

`Discord__GuildId`, `Discord__ChannelId` and `Discord__AdministratorUserIds` are legacy settings for migrating an old single-server installation. A new installation does not need them.
