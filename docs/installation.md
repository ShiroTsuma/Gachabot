# Installation and Discord setup

[Back to the README](../README.md)

For the default LAN host, open `https://dockers.lan`. Caddy redirects HTTP to HTTPS; the app's port `8791` is bound to the server's loopback interface only. The local health endpoint remains `http://localhost:8791/health` on the server.

`Caddyfile` uses Caddy's internal CA for `dockers.lan`. Trust its public root certificate once on every device that opens the dashboard:

```powershell
docker compose cp proxy:/data/caddy/pki/authorities/local/root.crt .\caddy-local-root.crt
Import-Certificate -FilePath .\caddy-local-root.crt -CertStoreLocation Cert:\CurrentUser\Root
```

For a public deployment, replace `dockers.lan` in `Caddyfile` with a real DNS name pointing to the server, remove the `tls internal` line, and keep ports 80 and 443 reachable. Caddy will then obtain and renew the certificate automatically.

Keep the named `gachabot-data` volume: it contains ASP.NET data-protection keys and the browser profile. Back it up together with PostgreSQL and the S3 bucket. Source artwork is copied to that S3 bucket; the dashboard serves the archived copy and Discord sends event artwork as an attachment instead of relying on the source CDN.

### Required configuration

`.env.example` documents every setting. At minimum, configure the following values before a production start:

| Setting | Purpose |
| --- | --- |
| `DatabaseStorage__ConnectionString` | PostgreSQL connection string for all content, guild settings, and publication state. |
| `S3Media__Endpoint`, `__AccessKey`, `__SecretKey` | Address and credentials of the private S3-compatible object store. |
| `S3Media__Bucket` | Existing bucket used for stored media, e.g. `gachabot-media`. |
| `Discord__BotToken` | Bot token from the Discord Developer Portal. |
| `Discord__OAuth__ClientId`, `Discord__OAuth__ClientSecret` | OAuth2 credentials used to sign in to the dashboard. |

Use a dedicated database user and a dedicated S3 access key restricted to this bucket. Never commit `.env`, database backups, or data-protection keys. Put the dashboard behind an HTTPS reverse proxy when it is public, and set the matching public OAuth redirect URL.

## Create and connect a Discord application

1. In the [Discord Developer Portal](https://discord.com/developers/applications), create an application and add a bot.
2. Copy its bot token to `Discord__BotToken` and keep `Discord__Enabled=true`.
3. In **OAuth2**, add the exact redirect URL served by Caddy: `https://dockers.lan/signin-discord` for the default LAN setup, or `https://your-domain/signin-discord` after changing `Caddyfile`. Do not use the internal port `8791` as a production OAuth callback.
4. Copy the OAuth client ID and client secret into `.env`.
5. Generate an install URL with the `bot` and `applications.commands` scopes, then add the bot to your test guild.
6. Give the bot permissions to view the target channel, send messages, embed links, attach files, and read message history. The person running setup needs Discord's **Manage Server** permission; the bot itself does not need it.

Run `/gachabot-configure` in Discord before opening the dashboard. The person who runs it receives dashboard access; another administrator can run the command again (without changing the channel) to register their own access. `GuildId`, `ChannelId`, and `AdministratorUserIds` are legacy migration settings and are not needed for a new installation.

Set `Discord__ActivityName=Schmidley` if you want to override the default Discord activity text.

