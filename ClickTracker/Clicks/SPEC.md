---
id: clicks-module
type: module-design
status: active
title: Clicks Module
parent: clicktracker-architecture
---

## Responsibility

Owns everything that happens to a click after the request arrives: materialising it from the HTTP
request, holding it until it can be written, and writing it through the storage interface.

`ClickRecord` is an immutable snapshot of one click:

| Field | Source |
|---|---|
| `OccurredAtUtc` | UTC timestamp taken when the request was received |
| `LinkId` | The short ID that was clicked |
| `TargetUrl` | The URL that was served |
| `ClientAddress` | `RemoteIpAddress` after forwarded headers are applied |
| `Method`, `Scheme`, `Host` | From the request |
| `Headers` | Every request header, copied verbatim |

The headers are copied out of the request at capture time. The request is recycled once the response
completes, so nothing may hold a reference to it.

`IClickStore` is the storage seam:

```csharp
public interface IClickStore
{
    Task WriteAsync(IReadOnlyList<ClickRecord> records, CancellationToken cancellationToken);
}
```

`SqliteClickStore` is the implementation. It creates the table if needed, writes a batch in one
transaction, and runs the database in WAL mode.

The headers are serialised to `headers_json` through the source-generated JSON context, so the
serialisation path contains no reflection.

## Behaviour

- **Capture** — the redirect handler builds a `ClickRecord` and hands it to `ClickQueue`.
- **Queue** — a bounded `Channel<ClickRecord>` in `DropOldest` mode. `TryWrite` never blocks. When
  the queue is full the oldest record is discarded. The channel does not report drops, so the count is
  derived from enqueued minus taken minus pending records; a click the writer has taken but not yet
  written counts as neither pending nor dropped. The count is logged periodically, so discarded
  clicks are visible rather than silent.
- **Write** — `ClickWriter`, a background service, drains the queue and writes batches of up to
  `BatchSize` records, or whatever has accumulated after `BatchIntervalMs`.
- **Failure** — a failed batch is retried up to three times with a short backoff, then dropped with
  an error log naming the batch size. A storage fault must not stop the service or the redirect path.

## Boundary

`Clicks` does not reference `Links`. The redirect handler passes the resolved link ID and target URL
in, so the click pipeline has no knowledge of link storage.

Allowed dependencies: the framework, and `Microsoft.Data.Sqlite` in the SQLite implementation only. No
other ClickTracker module.

The SQLite native library `e_sqlite3` is a deployment artefact rather than a managed dependency. In an
AOT publish it must be present next to the executable.
