---
id: clicktracker
type: goal-and-requirements
title: ClickTracker
status: active
---

# ClickTracker

Self-hosted URL shortener that records who clicked a short link, from where, and with what browser.

## Overview

ClickTracker is a small ASP.NET Core service that turns a short path into a redirect and records the
details of every request that hit it. Links live in a JSON file separate from application
configuration. Clicks live in a local SQLite database behind a storage interface, so the same
redirect service can later export to OpenTelemetry and Grafana Alloy without changing the request
path.

## Problem

Links you publish in posts, newsletters and chat give no signal. Nothing tells you whether anyone
followed them or where the traffic came from, and hosted shorteners keep that data on someone else's
infrastructure.

## Capabilities

- **Short link resolution** — `GET /{id}` returns a 302 redirect to the URL configured for that ID.
- **Click capture** — every click records all request headers verbatim, the client address, the request method, scheme and host, and a UTC timestamp.
- **Asynchronous persistence** — captured clicks are queued in memory and written by a background
  worker, so the redirect is never delayed by storage.
- **SQLite click storage** — clicks are persisted to SQLite through a storage interface that a later
  OTEL / Grafana Alloy implementation can replace. Clicks are read by querying the database directly.
- **Link configuration file** — a JSON file separate from `appsettings.json` maps link IDs to target
  URLs as a flat object. It is loaded into memory at startup and reloaded automatically when it
  changes on disk.
- **Admin API** — an authenticated API creates, replaces, deletes and lists links by writing the link
  configuration file.
- **Admin authentication** — the admin API requires an API key resolved from OpenBao, then an
  environment variable, then `appsettings.json`.

## Non-Goals

- Blocking or filtering bots. Bot traffic is recorded like any other click, because it is useful for
  analysis.
- Serving a landing page or any page content. The service only issues redirects and never reads or
  interprets the target page.

## Tech Notes

| Aspect | Choice | Rationale |
|---|---|---|
| Runtime | C# on .NET 10, ASP.NET Core minimal API | The request path is a single redirect; a minimal API keeps it small. |
| Publish target | Native AOT, with a JIT build for local development | The service deploys as a single native binary. |
| Link configuration | JSON file separate from `appsettings.json`, flat map of ID to URL | Links change at runtime and are edited by the admin API; application configuration does not. A flat map makes duplicate IDs impossible. |
| Click storage | SQLite behind a storage interface | SQLite keeps the deployment to one process and one file; the interface is the seam for an OTEL / Grafana Alloy sink. |
| Click write path | In-memory queue drained by a background worker | The redirect must not wait for the database. |
| Secrets | Admin API key from OpenBao, then an environment variable, then `appsettings.json` | Central secret management wins over local config files, so the same key works in local development and in a deployment that has a secret store. |
| Redirect | 302 Found | Temporary, so browsers and proxies do not cache it permanently and every click reaches the service. |

## Alternatives Considered

- **Dub** — Link attribution platform with a dashboard and team workspaces. ClickTracker is a redirect
  and event collector, not a marketing analytics product. https://dub.co
- **Shlink** — Mature PHP self-hosted shortener with a REST API, CLI and separate web client.
  ClickTracker targets a single .NET process with no web client. https://shlink.io
- **LiteURL** — Single-user Hono and React shortener with SQLite analytics. ClickTracker has no UI,
  and its storage interface is the seam for an OTEL / Grafana Alloy sink rather than a built-in
  dashboard. https://github.com/gentpan/LiteURL
