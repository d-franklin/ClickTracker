# ClickTracker

Self-hosted URL shortener that records who clicked a short link, from where, and with what browser.

A short path returns a `302` to the target URL and captures the request. Clicks are queued in
memory and written to SQLite by a background worker, so the redirect never waits on storage. The
storage sits behind an interface, so the same redirect service can later export to OpenTelemetry
and Grafana Alloy without changing the request path.

## What it does

- **Short link resolution** — `GET /{id}` returns a `302` to the URL configured for that ID.
- **Click capture** — every click records all request headers verbatim, the client address, the
  request method, scheme and host, and a UTC timestamp.
- **Asynchronous persistence** — captured clicks are queued in memory and written by a background
  worker, so the redirect is never delayed by storage.
- **SQLite click storage** — clicks are persisted through a storage interface that a later
  OTEL / Grafana Alloy implementation can replace.
- **Link configuration file** — a JSON file separate from `appsettings.json` maps link IDs to target
  URLs. It is loaded at startup and reloaded automatically when it changes on disk.
- **Admin API** — an authenticated API creates, replaces, deletes and lists links by writing the
  link file.

Bot traffic is recorded like any other click, and the service never serves a landing page. It
only issues redirects.

## Quick start

```bash
dotnet run --project ClickTracker
```

The service listens on `http://localhost:5235`. With the default `links.json`:

```bash
curl -i http://localhost:5235/example
# HTTP/1.1 302 Found
# Location: https://example.com
```

Add a link:

```bash
curl -X PUT http://localhost:5235/admin/links/my-link \
  -H 'X-Api-Key: your-key' \
  -H 'Content-Type: application/json' \
  -d '{"url":"https://example.org"}'
```

The admin key comes from OpenBao, then `CLICKTRACKER_ADMIN_API_KEY`, then `appsettings.json`. The
simplest way to set it locally is the environment variable.

## How it works

One ASP.NET Core process serves two surfaces.

**The redirect** (`GET /{id}`) resolves the ID against an in-memory snapshot of the link file,
materialises the request into a click record, hands it to a bounded queue with a non-blocking
write, and returns `302 Found`. No storage I/O happens on this path.

**The click writer** is a background service that drains the queue in batches and writes each
batch through `IClickStore` in one transaction. A failed batch is retried a bounded number of
times and then dropped with an error log, so a storage fault never stops the service.

**The link file** is the source of truth. It is watched for changes, debounced, then re-read and
swapped in as a new snapshot. A malformed file leaves the previous snapshot in force. The admin
API writes the file and updates the snapshot itself, so an admin write is visible in the next
request without waiting for the watcher.

```
                    ┌─────────────────────────────────────────┐
   GET /{id}  ────► │  LinkStore  ──► in-memory snapshot      │
                    │                  (cache, never authority)│
                    └──────────────┬──────────────────────────┘
                                   │ target URL
                                   ▼
                    ┌─────────────────────────────────────────┐
                    │  ClickCapture ──► ClickQueue (bounded)  │
                    └──────────────┬──────────────────────────┘
                                   │ non-blocking write
                                   ▼
                    ┌─────────────────────────────────────────┐
                    │  ClickWriter (background)               │
                    │    └──► IClickStore ──► SQLite (WAL)    │
                    └─────────────────────────────────────────┘
```

### Modules

| Module | Owns | Depends on |
|---|---|---|
| `Links` | The link file and the in-memory snapshot | — |
| `Clicks` | Click capture, the queue and the storage interface | — |
| `Admin` | The admin API and the admin API key | `Links` |

`Clicks` does not depend on `Links`. The redirect handler passes the resolved link ID and target
URL into the click record, so the click pipeline has no knowledge of link storage.

Full detail lives in [`architecture.md`](architecture.md) and the per-module specs.

## Admin API

Every route requires the `X-Api-Key` header. There is no unauthenticated admin route.

| Method | Route | Body | Result |
|---|---|---|---|
| `GET` | `/admin/links` | — | `200` with the list of links |
| `PUT` | `/admin/links/{id}` | `{"url":"https://..."}` | `200`, or `400` if the ID or URL is invalid |
| `DELETE` | `/admin/links/{id}` | — | `204`, or `404` if the ID is unknown |

Link IDs may not contain `/` or whitespace, and may not start with `admin`. Target URLs must be
absolute `http` or `https` URLs.

```bash
curl http://localhost:5235/admin/links -H 'X-Api-Key: your-key'
# [{"id":"example","url":"https://example.com"}]
```

## Configuration

`appsettings.json` holds application settings. Link definitions live in their own file and are
never part of application configuration.

```json
{
  "Links": { "FilePath": "links.json" },
  "Clicks": {
    "DatabasePath": "clicks.db",
    "QueueCapacity": 10000,
    "BatchSize": 100,
    "BatchIntervalMs": 250
  },
  "Admin": {
    "ApiKey": "",
    "OpenBao": { "Address": "", "SecretPath": "secret/data/clicktracker" }
  },
  "ForwardedHeaders": {
    "KnownNetworks": ["10.0.0.0/8"],
    "KnownProxies": []
  }
}
```

| Variable | Purpose |
|---|---|
| `CLICKTRACKER_ADMIN_API_KEY` | Admin API key, used when OpenBao has none |
| `OPENBAO_ADDR` | OpenBao address |
| `OPENBAO_TOKEN` | OpenBao token |
| `OPENBAO_SECRET_PATH` | Path to the secret, defaults to `secret/data/clicktracker` |
| `ASPNETCORE_URLS` | Listen address, defaults to `http://localhost:5235` |

The OpenBao secret is expected at `{Address}/v1/{SecretPath}` with an `adminApiKey` field. A key
that does not match triggers one re-read, so rotation works with no restart.

`QueueCapacity` is a hard bound. When the queue is full the oldest click is discarded rather than
letting memory grow, and the count is logged so the loss is visible.

## Reading the clicks

Clicks are read by querying SQLite directly.

```bash
sqlite3 clicks.db 'SELECT link_id, COUNT(*) FROM clicks GROUP BY link_id;'
sqlite3 clicks.db 'SELECT occurred_at_utc, link_id, client_address, headers_json FROM clicks LIMIT 5;'
```

| Column | Type | Notes |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY AUTOINCREMENT` | |
| `occurred_at_utc` | `TEXT` | ISO-8601 UTC, taken when the request arrived |
| `link_id` | `TEXT` | The short ID that was clicked |
| `target_url` | `TEXT` | Kept so a later link change does not rewrite history |
| `client_address` | `TEXT` | Null when the address is unknown |
| `method` | `TEXT` | |
| `scheme` | `TEXT` | |
| `host` | `TEXT` | |
| `headers_json` | `TEXT` | Every request header, verbatim, as a JSON object of string arrays |

Indexed on `link_id` and `occurred_at_utc`. The database runs in WAL mode so clicks can be read
while they are written.

## Performance

Measured with the k6 scripts in [`loadtest/`](loadtest/). **The load generator and the service ran
on the same 32-core host**, and k6 consumed more CPU than the service in every measurement, so
the throughput figures are a floor rather than a ceiling.

30,000 req/s sustained for 5 minutes, p95 0.26 ms, p99 2.03 ms, zero failed requests.

| Metric | Value |
|---|---|
| Throughput | 29,976 req/s achieved against a 30,000 req/s offered rate |
| Requests | 8,992,866 over 300 s |
| Latency (redirect) | avg 0.157 ms, p50 0.075 ms, p95 0.262 ms, p99 2.03 ms |
| Failed requests | 0 |
| Failed assertions | 0 of 24,282,017 |
| Clicks dropped | 0 |
| Resident memory | 145 MB → 139 MB (flat) |
| Open file descriptors | 16 → 719 → 16 at rest |

### Closed loop

`ramping-vus`, 10 s ramp, 30 s steady, 5 s drain. Throughput peaks, then falls away as latency
grows.

| VUs | req/s | avg | p95 | p99 |
|---:|---:|---:|---:|---:|
| 10 | 57,825 | 0.085 ms | 0.138 ms | 0.280 ms |
| 25 | 65,186 | 0.236 ms | 0.401 ms | 0.802 ms |
| 50 | 76,948 | 0.437 ms | 0.784 ms | 1.43 ms |
| **100** | **88,513** | 0.794 ms | 1.50 ms | 2.85 ms |
| 200 | 84,593 | 1.35 ms | 2.86 ms | 6.66 ms |
| 400 | 79,056 | 2.18 ms | 4.69 ms | 11.19 ms |
| 800 | 74,030 | 3.51 ms | 8.19 ms | 18.53 ms |

Zero failed requests at every level. The knee is at 100 VUs.

### Open loop

`constant-arrival-rate`, 30 s per level. This is the meaningful latency measure, because the
offered rate is fixed regardless of how slow the service gets.

| Offered | Achieved | avg | p95 | p99 | Dropped iterations |
|---:|---:|---:|---:|---:|---:|
| 20,000 | 19,941 | 0.250 ms | 0.280 ms | 2.93 ms | 1,700 (0.3%) |
| 30,000 | 29,998 | 0.199 ms | 0.816 ms | 2.96 ms | 0 |
| 40,000 | 39,916 | 0.346 ms | 1.81 ms | 4.64 ms | 2,394 (0.2%) |
| 60,000 | 59,352 | 1.88 ms | 10.96 ms | 22.53 ms | 19,247 (1.1%) |
| 80,000 | 65,625 | 5.20 ms | 16.81 ms | 42.38 ms | 226,572 (9.4%) |

Zero failed requests at every level. The service ceiling is roughly 65,000 req/s; above that, k6
could not land the arrivals and dropped 9.4% of them.

### The click pipeline breaks first

The redirect path never touches storage, so the first thing to fail under load is the queue that
feeds SQLite.

| Load | Clicks/s offered | Written to SQLite | Dropped |
|---|---:|---:|---:|
| 30,000 req/s for 5 min | 25,500 | 25,489 | 0 |
| Closed-loop sweep, peak | ~75,000 | 49,022 | 29.9% |

The SQLite writer sustained about 25,500 clicks/s with zero drops. Above roughly 75,000 clicks/s
it fell behind, and the bounded `DropOldest` queue discarded the oldest clicks — 6,024,079 of the
20,126,538 clicks enqueued across the sweep. A dropped click is invisible to the client: the
request still returns a `302`, and the loss only shows up in the service log as
`N clicks were dropped`.

Each click row costs about 247 bytes, dominated by the verbatim header capture. 35.5M clicks
occupied 8.37 GB.

### CPU cost

| Run | App CPU | k6 CPU | App CPU per request |
|---|---:|---:|---:|
| 20,000 req/s, mixed | 263% of one core | 315% of one core | 132.2 µs |
| 20,000 req/s, HEAD only | 200% of one core | 315% of one core | 100.0 µs |
| 40,000 req/s, mixed | 422% of one core | 700% of one core | 105.5 µs |

At roughly 100 µs of CPU per request, the redirect path is the throughput ceiling on a single
host. The app burned more than two cores at 20,000 req/s.

### What the numbers do not cover

- Behaviour behind a reverse proxy, with TLS termination, or with forwarded headers from a real
  proxy. The test ran against Kestrel directly on loopback.
- The admin API. It is not on the request path under test.
- Click durability under sustained overload. The 29.9% loss figure characterises the queue's
  `DropOldest` behaviour, it is not a measured failure threshold.
- Click loss over a restart. The queue is in memory, so anything still queued at shutdown is lost.
- Any other storage backend. `IClickStore` has exactly one implementation here.

See [`loadtest/README.md`](loadtest/README.md) for how to run the tests.

## Development

```bash
# Run the service.
dotnet run --project ClickTracker

# Run the tests.
dotnet test --project ClickTracker.Tests/ClickTracker.Tests.csproj

# Publish the native binary.
dotnet publish ClickTracker/ClickTracker.csproj -r linux-x64 -c Release -o ./publish
```

The project targets .NET 10 and publishes as Native AOT. `dotnet run` JITs locally; the published
binary is native and needs no runtime installed.

Tests cover each module against a temporary directory or database, plus an integration test that
asserts the `302`, the `Location` header, and that the click reaches the database through the
queue.

### Pre-commit secret scan

`.githooks/pre-commit` runs [trufflehog](https://github.com/trufflesecurity/trufflehog) over the
staged files and blocks the commit when it finds something that looks like a secret. Only staged
files are scanned, so the check is fast and only reports what the commit introduces. Binary files
are skipped.

Enable it in a clone:

```bash
scripts/install-hooks.sh
```

or `git config core.hooksPath .githooks`.

| Variable | Effect |
|---|---|
| `TRUFFLEHOG_SKIP=1` | skip the scan for one commit |
| `TRUFFLEHOG_VERIFY=1` | let trufflehog verify findings against the provider, which needs network access |
| `TRUFFLEHOG_REQUIRED=1` | fail the commit when trufflehog is not installed, instead of warning |

A false positive is suppressed with a `trufflehog:ignore` comment on the line that triggered it.

## Project layout

```
ClickTracker/
├── Program.cs              Composition root, routes, middleware
├── Links/                  Link file, validation, in-memory snapshot
├── Clicks/                 Click capture, bounded queue, SQLite store
├── Admin/                  Admin API and API key resolution
├── appsettings.json        Application configuration
└── links.json              Link definitions
ClickTracker.Tests/         Unit and integration tests
loadtest/                   k6 scripts and reporting
.githooks/                  Versioned git hooks
scripts/                    Repository setup scripts
architecture.md             Architecture, decisions, invariants
goal-and-requirements.md    Scope and capability decisions
```

## License

[MIT](LICENSE) © 2026 Daniel Franklin
