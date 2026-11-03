# Load tests

k6 scripts for measuring the ClickTracker redirect path.

## The script

`redirect.js` drives the redirect endpoint with a mix of traffic:

| Share | Request | What it exercises |
|---|---|---|
| 85% | `GET /{known-id}` | The full path: link lookup, click capture, queue write, 302 |
| 5% | `HEAD /{known-id}` | The redirect without click capture |
| 10% | `GET /{unknown-id}` | The 404 miss path |

Redirects are not followed (`maxRedirects: 0`). Following them would measure the target site
instead of ClickTracker. The `Location` header is checked against the configured target instead.

Every run asserts the response status, the presence of a `Location` header and, when
`LINK_TARGETS` is supplied, that the `Location` matches the configured target URL.

## Running it

The service must be running and reachable. Then:

```bash
export BASE_URL=http://127.0.0.1:5080
export LINK_IDS=link-001,link-002,link-003
export LINK_TARGETS='{"link-001":"https://example.com"}'

./k6 run loadtest/redirect.js
```

## The matrix runner

`run-matrix.sh` runs the script across a set of load levels and prints one row per level.

```bash
# Closed loop: sweep the VU count to find the knee.
loadtest/run-matrix.sh --vus 10,25,50,100,200,400,800

# Open loop: hold a fixed arrival rate and watch the latency tail.
loadtest/run-matrix.sh --executor constant-arrival-rate --rates 5000,10000,20000,40000
```

Each run writes a k6 JSON summary and a console log to `$OUT_DIR` (default
`/tmp/ct-perf/results`). `report.py` turns those summaries into the table.

## Measuring the cost of a run

`measure-cost.sh` runs one arrival-rate load and reports the CPU the service burned per request,
how many clicks reached SQLite and how many the queue dropped.

```bash
loadtest/measure-cost.sh 20000 60s
```

## Notes on running this locally

- **The load generator and the service share the machine.** k6 used more CPU than the service in
  every measurement, so the absolute throughput is a floor, not a ceiling.
- **Do not pre-allocate a VU per request.** Each VU holds a socket, and the local ephemeral port
  range only has room for a few tens of thousands. The script caps the arrival-rate VU count for
  this reason.
- **Watch the disk.** Each click row is roughly 300 bytes, most of it the verbatim header
  capture.
