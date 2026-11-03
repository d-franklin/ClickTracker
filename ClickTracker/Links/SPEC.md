---
id: links-module
type: module-design
status: active
title: Links Module
parent: clicktracker-architecture
---

## Responsibility

Owns the link definitions: the JSON file on disk, the in-memory snapshot the redirect path reads, and
the watcher that keeps them in sync.

`Link` is an ID and its target URL. `LinkStore` is the only type that reads or writes the file.

The file is a flat JSON object of ID to absolute URL:

```json
{
  "gh": "https://github.com/your-org",
  "docs": "https://docs.example.com/handbook"
}
```

IDs are matched exactly and are case-sensitive. An ID must be non-empty and contain no `/` or
whitespace. The `admin` prefix is reserved. Target URLs must be absolute `http` or `https` URLs.

The file is read through the source-generated JSON context, not through reflection. The in-memory map
uses ordinal comparison.

## Behaviour

- **Load** — the file is read at startup. A missing file starts the service with an empty link set. A
  malformed file is a startup error, because there is no earlier snapshot to fall back on.
- **Read** — lookups, listing and snapshot access go to the in-memory map, never to the file.
- **Write** — the admin API calls `Upsert` or `Delete`. A definition that breaks the ID or URL rules is
  rejected with `InvalidLinkException` before anything is written. The file is written first and the
  map is updated only after the write succeeds, so a failed write leaves the file and the running
  service unchanged. The write is atomic: the whole file is written to a temporary file in the same
  directory and then moved over the original.
- **Watch** — a `FileSystemWatcher` on the file's directory filters by file name. A change is
  debounced before the file is re-read. A re-read that fails to parse leaves the previous map in
  force and logs an error. A re-read that succeeds replaces the map.

## Boundary

`Links` owns `links.json` and nothing else. It does not know about HTTP, clicks, storage or
authentication. It exposes the snapshot and the mutations the admin API needs.

Allowed dependencies: the framework and `System.Text.Json`. No other ClickTracker module.
