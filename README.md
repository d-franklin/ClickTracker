# ClickTracker performance

Measured performance of the ClickTracker redirect path, using the k6 scripts in `loadtest/`.

**The load generator and the service ran on the same 32-core host.** k6 consumed more CPU than
the service in every measurement, so the throughput figures below are a floor, not a ceiling.

## Headline

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
| Threads | 86-95 |

## Test environment

| | |
|---|---|
| Service | Native AOT publish (`dotnet publish -r linux-x64 -c Release`) |
| Runtime | .NET 10, ASP.NET Core minimal API, HTTP only |
| Load generator | k6 v2.3.0, same host |
| Host | 32 CPUs, 62 GB RAM, Linux, loopback |
| Links | 51 IDs, loaded from an isolated `links.json` |
| Database | Isolated `clicks.db`, WAL mode |
| Traffic mix | 85% `GET` on a known ID, 5% `HEAD` on a known ID, 10% `GET` on an unknown ID |

Every request asserted the response status, the presence of a `Location` header, and that the
`Location` matched the configured target URL. Redirects were not followed.

## Closed loop

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

## Open loop

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

## Redirect path without click capture

`HEAD` requests are not recorded as clicks, so a HEAD-only run isolates the link lookup and
redirect from the click pipeline.

| VUs | req/s | avg | p95 | p99 |
|---:|---:|---:|---:|---:|
| 100 | 106,091 | 0.684 ms | 1.32 ms | 2.08 ms |
| **200** | **113,334** | 1.239 ms | 2.56 ms | 3.81 ms |
| 400 | 110,096 | 1.786 ms | 3.84 ms | 6.11 ms |

Click capture costs about 22% of peak throughput: 88,513 req/s mixed against 113,334 req/s for
HEAD-only traffic.

## The click pipeline breaks first

The redirect path never touches storage, so the first thing to fail under load is the queue that
feeds SQLite.

| Load | Clicks/s offered | Written to SQLite | Dropped |
|---|---:|---:|---:|
| 30,000 req/s for 5 min | 25,500 | 25,489 | 0 |
| Closed-loop sweep, peak | ~75,000 | 49,022 | 29.9% |

The SQLite writer sustained about 25,500 clicks/s with zero drops. Above roughly 75,000 clicks/s
it fell behind, and the bounded `DropOldest` queue discarded the oldest clicks. Across the sweep
that was 6,024,079 of the 20,126,538 clicks enqueued.

A dropped click is invisible to the client. The request still returns a 302; the loss only shows
up in the service log as `N clicks were dropped`.

### Storage cost

| | |
|---|---|
| Click rows | 35,496,692 |
| Database size | 8.37 GB (247 bytes per row) |
| Growth rate | 5.4 MB/s under 25,500 clicks/s |
| WAL file | bounded at 36 MB by checkpointing |

The row size is dominated by the verbatim header capture.

## CPU cost

| Run | App CPU | k6 CPU | App CPU per request |
|---|---:|---:|---:|
| 20,000 req/s, mixed | 263% of one core | 315% of one core | 132.2 µs |
| 20,000 req/s, HEAD only | 200% of one core | 315% of one core | 100.0 µs |
| 40,000 req/s, mixed | 422% of one core | 700% of one core | 105.5 µs |

At roughly 100 µs of CPU per request, the redirect path is the throughput ceiling on a single
host. The app burned more than two cores at 20,000 req/s.

## What this does not cover

- Behaviour behind a reverse proxy, with TLS termination, or with forwarded headers from a real
  proxy. The test ran against Kestrel directly on loopback.
- The admin API. It is not on the request path under test.
- Click durability under sustained overload. The 29.9% loss figure is a characterisation of the
  queue's `DropOldest` behaviour, not a measured failure threshold.
- Click loss over a restart. The queue is in memory, so anything still queued at shutdown is
  lost.
- Any other storage backend. `IClickStore` has exactly one implementation here.

## Reproducing

```bash
dotnet publish ClickTracker/ClickTracker.csproj -r linux-x64 -c Release -o /tmp/ct-publish

# Run the service against an isolated links.json and clicks.db. Point Links:FilePath and
# Clicks:DatabasePath at absolute paths so the repo's own files are left alone.
mkdir -p /tmp/ct-run
cp /tmp/ct-publish/appsettings.json /tmp/ct-run/
cp ClickTracker/links.json /tmp/ct-run/
cd /tmp/ct-run && ASPNETCORE_URLS=http://127.0.0.1:5080 /tmp/ct-publish/ClickTracker

# Sweep the VU count.
export BASE_URL=http://127.0.0.1:5080
export LINK_IDS=link-001,link-002,link-003
loadtest/run-matrix.sh --vus 10,25,50,100,200,400,800

# Or hold a fixed arrival rate.
loadtest/run-matrix.sh --executor constant-arrival-rate --rates 20000,30000,40000

# Measure CPU per request and click throughput.
loadtest/measure-cost.sh 20000 60s
```

See `loadtest/README.md` for the script's options.
