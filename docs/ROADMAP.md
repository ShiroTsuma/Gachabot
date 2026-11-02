# Roadmap

[Back to the documentation index](README.md)

Known limitations of the current version, roughly in order of impact. Each item names where the change belongs.

## Delivery

- **Partial multi-message posts.** A post split into several Discord messages removes the parts already sent when a later part fails, so a retry does not duplicate them. If that cleanup also fails, the retry can still duplicate the first parts. Recording sent message IDs as they go would let a retry resume instead. (`DiscordChannelPublisher`)
- **Concurrent refreshes.** **Refresh all** in the dashboard can run while the hourly poll is importing the same source. Both can queue a post for the same changed code list. A per-source lock in `SourceOperations` and cancelling older pending posts for the same message would remove the duplicate.
- **Enabling a server announces running events.** `/gachabot-enable` queues "Event starting" posts for every event already running. A server that joins mid-patch receives several such posts at once.
- **Obsolete-post cleanup and retention.** Servers with `delete-obsolete-posts` also lose official news posts once they pass the one-month retention. Cleanup could be limited to expired, superseded and manually archived content.
- **Posts before their artwork.** A post can be sent with the source's image URL before the artwork is archived, and images that fail to archive are downloaded again on each poll. (`ArchivingIngestionSink`)

## Sources

- **Browser recovery.** If the Chromium used for Game8 crashes, Game8 sources fail until the app restarts. (`PlaywrightRenderedPageClient`)
- **WuWa event times.** WuWa Tracker times are normalized to the 04:00 (UTC+8) server reset. A banner that opens at another hour is announced at the reset.

## Dashboard

- **Timezones.** The new-post form reads times as Europe/Warsaw, while lists show the server's timezone. Running the container with a matching `TZ` keeps them consistent; showing one zone everywhere is planned.
- **Access model.** Anyone who has run `/gachabot-configure` on any server can sign in to the dashboard and manage all content. This is intended for a private, LAN-only dashboard. A public instance would need access limited to operators or to each user's own server.
- **Deleting content with a missing channel.** Permanent deletion stops if a server's channel no longer exists.

## Maintenance

- **Schema migrations.** Schemas are created with `EnsureCreated` plus targeted upgrade statements. Numbered EF Core migrations should replace them before the next incompatible schema change.
- **Duplicate upsert paths.** `ContentStore` has two similar upsert implementations (database and in-memory baseline). Merging them would prevent the two from drifting apart.
- **One-time data migrations.** Several legacy migrations still run on every import (the `active-codes` rename, legacy feed repairs, and adopting events stored under their pre-edition IDs). They can be removed once all instances have upgraded.
- **Media archive hardening.** The private-network check for downloaded images resolves DNS separately from the connection. Checking the address actually connected to would close a DNS-rebinding gap.
