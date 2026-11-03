---
id: clicktracker-architecture
type: architecture-design
status: active
title: ClickTracker Architecture
parent: clicktracker
---

## Topology

One ASP.NET Core process (Kestrel) serves two surfaces:

- `GET /{id}` — the short link. Resolves the ID against the in-memory link snapshot, returns `302 Found`
  to the target URL, and records the click.
- `/admin/links` — the admin API. Authenticated with an API key, mutates the link file.

`Program.cs` is the composition root. It wires the modules together and hosts the redirect endpoint,
which is the only place that joins link resolution to click capture.

The application targets Native AOT. It is hosted with `WebApplication.CreateSlimBuilder`, listens on
HTTP only, and is published with `PublishAot=true`. A local run uses the JIT compiler; the published
binary is native.

| Module | Spec | Owns | Depends on |
|---|---|---|---|
| Links | `ClickTracker/Links/SPEC.md` | The link file and the in-memory snapshot | — |
| Clicks | `ClickTracker/Clicks/SPEC.md` | Click capture, the queue and the storage interface | — |
| Admin | `ClickTracker/Admin/SPEC.md` | The admin API and the admin API key | Links |

`Clicks` does not depend on `Links`. The redirect handler passes the resolved link ID and target URL
into the click record, so the click pipeline has no knowledge of link storage.

## Request flows

### Short link

1. The ID is looked up in the in-memory snapshot. A miss returns `404`.
2. The request is materialised into a click record: headers copied out, plus client address, method,
   scheme, host and a UTC timestamp.
3. The record is handed to the queue with a non-blocking write.
4. `302 Found` is returned with the target URL in `Location`.

No storage I/O happens on this path. A `HEAD` request is redirected the same way but is not recorded,
because a link check is not a click. Routing matches HTTP methods exactly, so both verbs are mapped
explicitly.

### Click persistence

A background service drains the queue in batches — up to `BatchSize` records or `BatchIntervalMs`,
whichever comes first — and writes each batch through `IClickStore`. A failed batch is retried a
bounded number of times and then dropped with an error log, so a storage fault never stops the
service.

### Link reload

The link file is loaded into memory at startup and watched for changes. A change is debounced, then
the file is re-read, parsed and swapped in as a new snapshot. A malformed file leaves the previous
snapshot in force and logs an error. The admin API writes the file and updates the snapshot itself,
so an admin write is visible in the next request without waiting for the watcher.

## Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Process shape | One ASP.NET Core process, one project, folders per module | The service is a single deployment unit with a minimal API. Separate projects would add solution overhead without a boundary that review does not already enforce. |
| Link source of truth | The JSON file; the in-memory map is a cache | A link change must survive a restart, and the admin API must be able to rewrite definitions without a running service. |
| Link file watching | `FileSystemWatcher` with a debounce | The built-in configuration reload is for configuration, not for data the service also writes. A dedicated watcher gives the admin API a deterministic write-then-swap behaviour. |
| Click write path | Bounded `Channel` drained by a background service | The redirect must never wait on the database. A bounded channel makes the failure mode explicit: the oldest click is discarded instead of memory growing without limit. |
| Storage seam | `IClickStore` with a batch write | An OTEL or Grafana Alloy implementation replaces SQLite without touching the request path. |
| SQLite access | `Microsoft.Data.Sqlite` with hand-written SQL | One table and one insert path. An ORM would add a migration story to a schema that will not grow. |
| Admin key source | OpenBao, then the environment variable, then `appsettings.json` | See `ClickTracker/Admin/SPEC.md`. |
| Admin key rotation | Re-read OpenBao when a presented key does not match | Rotation works with no restart and no timer, and a wrong key costs one extra read. |
| OpenBao client | Raw HTTP against the OpenBao API | OpenBao has no official .NET client. The HTTP API is one call, and the same code works against Vault because the APIs are compatible. |
| Client address | Forwarded headers are trusted | Behind a reverse proxy the socket address is the proxy, not the visitor. Known proxy networks and `X-Forwarded-Proto` are configured explicitly, which also keeps `UseHttpsRedirection` from redirecting a request that already arrived over HTTPS. |
| Host | `WebApplication.CreateSlimBuilder` | The AOT-supported minimal API path. It excludes HTTPS endpoints, static web assets and regex or alpha route constraints, so the app serves HTTP only, ships no static files, and uses plain route parameters. |
| Publish | `PublishAot=true` in the project file | One project serves both modes. `dotnet run` JITs locally, `dotnet publish` produces the native binary. |
| JSON | Source-generated `JsonSerializerContext` | Reflection is unavailable in AOT. Every type crossing the HTTP boundary, and the link file, is read and written through the generated context. |
| Configuration binding | `EnableConfigurationBindingGenerator=true` | The compile-time binding generator replaces the reflection-based binder, so options objects bind without dynamic code. |
| TLS | HTTP only, TLS terminated by the reverse proxy | The app has no HTTPS endpoint under `CreateSlimBuilder`. The proxy sets `X-Forwarded-Proto`, which the app trusts, so the recorded scheme is correct and no HTTPS redirect middleware is used. |
| Globalization | `InvariantGlobalization=true` | Removes the ICU dependency from the native binary. Link IDs are matched with ordinal comparison, so no culture-sensitive comparison is needed. |
| SQLite native library | `e_sqlite3` is deployed next to the executable | `Microsoft.Data.Sqlite` does not statically link SQLite. The publish carries the native library for the target RID, which is proved by running the published binary. |
| Trim fallback | `AllowMissingPrunePackageData=true` in the project file | Some SDK installs ship without the `PrunePackageData` folder. The trimmer then analyses the framework assemblies directly, which is slower but produces the same result. |
| Dependencies | `Microsoft.Data.Sqlite` and `Microsoft.AspNetCore.OpenApi` | Everything else is in the framework. Both packages support AOT on .NET 10. |

## Data model

`clicks` is the only table.

| Column | Type | Notes |
|---|---|---|
| `id` | `INTEGER PRIMARY KEY AUTOINCREMENT` | |
| `occurred_at_utc` | `TEXT` | ISO-8601 UTC, captured when the request was received |
| `link_id` | `TEXT` | The short ID that was clicked |
| `target_url` | `TEXT` | The URL served, kept so a later link change does not rewrite history |
| `client_address` | `TEXT` | Null when the address is unknown |
| `method` | `TEXT` | |
| `scheme` | `TEXT` | |
| `host` | `TEXT` | |
| `headers_json` | `TEXT` | Every request header, verbatim, as a JSON object of string arrays |

Indexed on `link_id` and `occurred_at_utc`. The database runs in WAL mode so clicks can be read while
they are written.

## Configuration

`appsettings.json` holds application settings. Link definitions live in their own file and are never
part of application configuration.

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
    "OpenBao": {
      "Address": "",
      "SecretPath": "secret/data/clicktracker"
    }
  },
  "ForwardedHeaders": {
    "KnownNetworks": ["10.0.0.0/8"],
    "KnownProxies": []
  }
}
```

Environment variables: `CLICKTRACKER_ADMIN_API_KEY` for the admin API key, `OPENBAO_ADDR` and
`OPENBAO_TOKEN` for the OpenBao connection, `OPENBAO_SECRET_PATH` for the secret path. Standard
ASP.NET Core configuration rules apply to everything else.

The OpenBao secret is expected at `{Address}/v1/{SecretPath}` with the field `adminApiKey`.

## Invariants

1. The redirect path performs no storage I/O. It materialises the request and hands it to the queue.
2. `SqliteClickStore` is the only code that touches SQLite.
3. The link file is the only source of truth for link definitions. The in-memory map is a cache and
   never the authority.
4. Only `LinkStore` writes the link file.
5. A malformed link file never takes the service down. The previous snapshot stays in force and the
   error is logged.
6. The admin API key is never logged and never returned by any endpoint.
7. Every admin route requires the key. There is no unauthenticated admin route.
8. The `admin` path prefix is reserved and cannot be used as a link ID.
9. No route constraints are used. `CreateSlimBuilder` does not support regex or alpha constraints, so
   link IDs and target URLs are validated in the Links module.
10. Every type that crosses the HTTP boundary is registered on the source-generated
    `JsonSerializerContext`. Nothing relies on reflection-based serialization.
11. The application publishes with no AOT or trim warnings.

## Verification

Each module is testable without a running server: `Links` against a temporary directory, `Clicks`
against a temporary SQLite file, `Admin` against a temporary link file and a stubbed key provider. The
redirect path is covered by an integration test that asserts the `302`, the `Location` header and that
the click reaches the database through the queue.

**AOT publish.** `dotnet publish -r linux-x64 -p:PublishAot=true` must complete with no AOT or trim
warnings, and the published binary must start, serve a redirect, write a click to SQLite and serve the
OpenAPI document.

The tests live in `ClickTracker.Tests` and run with `dotnet test --project
ClickTracker.Tests/ClickTracker.Tests.csproj`.

## Out of scope

- An OTEL or Grafana Alloy `IClickStore` implementation. The interface is the seam for it, but the
  implementation is later work.
- A read API for clicks. Clicks are read by querying SQLite.
- Retention or pruning of click data. The database grows until it is deleted.
