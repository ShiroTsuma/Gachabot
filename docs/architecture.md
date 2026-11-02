# Architecture

[Back to the documentation index](README.md)

Gachabot is a modular monolith in four projects. Dependencies point inward, and an architecture test enforces it:

| Project | Contains | Depends on |
| --- | --- | --- |
| `GachaBot.Domain` | Content documents and blocks, games, statuses, redeem codes | nothing |
| `GachaBot.Application` | Use-case contracts: ingestion, content management, publishing, media | Domain |
| `GachaBot.Infrastructure` | PostgreSQL stores, source adapters, Playwright, Discord, S3, background workers | Application |
| `GachaBot.Web` | Blazor Server dashboard and the composition root | all of the above |

```text
Sources ──> source adapters ──> ingestion coordinator ──> PostgreSQL (schema per source) + S3 media
                                        │
                                        ├── baseline and trust policy
                                        └── publication queue ──> dispatcher ──> Discord REST

Dashboard ──> application contracts ──> composite store ──> every source schema
```

## Content documents

Content is not stored as a Discord embed. A `ContentDocument` is an ordered list of validated blocks:

- `HeadingBlock`, `TextBlock`, `LinkBlock`
- `ImageBlock` and `GalleryBlock`, with alt text
- `KeyValueBlock`
- `CodeBlock`

The canonical JSON of the blocks is hashed with SHA-256. The hash, not the raw page, decides whether content changed, so markup noise on a source page does not create revisions. Each change keeps the previous title and document in `ContentRevisions`. A new block type needs serialization and rendering, but no table change.

## One schema per source

Every source writes to its own PostgreSQL schema named after its key; dashboard posts use `manual`. Content, revisions, source state and the publication queue of a source live together, so one import is a single atomic write. The composite store routes writes to the right schema and merges reads across all of them for the dashboard and the dispatcher.

Imports are batched: an adapter looks up the state of up to 500 identifiers per query, and the coordinator writes changes in batches of 50. An unchanged scan of a large feed costs only a few queries.

## Publication

Creating or changing content adds `PublicationRecord` rows in the same transaction, one per destination server and purpose:

| Purpose | Created for | Due |
| --- | --- | --- |
| `Standard` | News, updates, codes, manual posts | Immediately, or at the scheduled time |
| `EventStart` | Dated events, where the server enabled start posts | Start minus the server's offset |
| `EventEndingReminder` | Dated events, where the server enabled reminders | End minus the server's offset |

The dispatcher wakes every 15 seconds, leases due rows, composes the messages and sends them through Discord REST. A failure reschedules the row with exponential backoff, capped at 15 minutes, for up to 100 attempts (about a day). Each server has its own rows, so one failing server does not block another.

The composer turns blocks into rich embeds within Discord's limits (10 embeds and 6,000 characters per message, 4,096 description characters and 25 fields per embed) and splits larger documents into several messages. `ProviderMessageId` stores all their IDs in order, so an update can edit, add or remove parts. If a later part fails to send, the parts already sent are deleted before the retry. Redeem code lists are an exception to editing: a changed list is posted as new messages, and the old ones are deleted only where the server enabled obsolete-post cleanup.

The dashboard preview renders the same composer output, so there is no second formatter. Delivery is at least once: if the process stops after Discord accepts a message but before the receipt is saved, the message can be sent again.

## Events

Events come only from calendar sources, and each has a start and an end. Changing a server's offsets, subjects or enabled state recalculates its pending event posts; an edited event moves its pending reminders. Calendar sources include the start date in each event's identifier, so the next edition of a recurring event is a new item rather than a date change of the previous one.

## Time, retention and media

Everything is stored in UTC. The new-post form reads its input as Europe/Warsaw; lists display the server's timezone.

An hourly worker archives expired content and official announcements older than one month, and cancels their pending posts. Artwork is downloaded over HTTPS from public addresses only, checked to be an image within the size limit, optimized, and stored in S3 under a content hash.
