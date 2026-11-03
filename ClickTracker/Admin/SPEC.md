---
id: admin-module
type: module-design
status: active
title: Admin Module
parent: clicktracker-architecture
---

## Responsibility

Owns the admin API surface and the admin API key.

## Endpoints

All routes live under `/admin/links` and all of them require the API key.

| Method and path | Purpose | Success | Errors |
|---|---|---|---|
| `GET /admin/links` | List every link ID and target URL | `200` | `401` bad key |
| `PUT /admin/links/{id}` | Create or replace the target for an ID | `200` | `400` invalid ID or URL, `401` bad key |
| `DELETE /admin/links/{id}` | Remove a link | `204` | `404` unknown ID, `401` bad key |

The key is presented in the `X-Api-Key` header.

The request and response types are registered on the source-generated `JsonSerializerContext`:

| Type | Used for |
|---|---|
| `UpdateLinkRequest` | The body of `PUT /admin/links/{id}` |
| `LinkDto` | One link in the list response |
| `LinkDto[]` | The body of `GET /admin/links` |

## The admin API key

`ApiKeyProvider` resolves the key from the first source that has one:

1. OpenBao — `{OPENBAO_ADDR}/v1/{OPENBAO_SECRET_PATH}`, header `X-Vault-Token: {OPENBAO_TOKEN}`,
   field `adminApiKey` under `data.data`.
2. The environment variable `CLICKTRACKER_ADMIN_API_KEY`.
3. `appsettings.json` at `Admin:ApiKey`.

The resolved key is cached. When a presented key does not match, the provider re-reads OpenBao once
and compares again, so a rotated key takes effect without a restart. If it still does not match, the
request is rejected with `401`.

The OpenBao response is read with `JsonDocument`, so nothing in the path relies on reflection.

Comparison is fixed-time, so a wrong key cannot be discovered one byte at a time. The key is never
logged and never returned by any endpoint.

## Boundary

`Admin` depends on `Links` for every link mutation and on nothing else in ClickTracker. It does not
touch SQLite and does not see the click pipeline.

Allowed dependencies: the framework and `Links`.
