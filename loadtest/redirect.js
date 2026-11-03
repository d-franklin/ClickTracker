// Load test for the ClickTracker redirect path.
//
// The redirect path is the only path that matters for throughput: it resolves a link ID against
// the in-memory snapshot, materialises a click record and hands it to the queue. No storage I/O
// happens on it.
//
// Run with:
//   ./k6 run loadtest/redirect.js
//
// The scenario shape, target load and thresholds are driven by environment variables so the same
// script serves a smoke test and a saturation run:
//
//   BASE_URL       base URL of the running service           (default http://127.0.0.1:5080)
//   LINK_IDS       comma-separated link IDs to hit            (default example)
//   LINK_TARGETS   JSON object of id to expected target URL   (optional, enables Location checks)
//   MISS_ID        link ID that does not exist               (default this-link-does-not-exist)
//   MISS_PERCENT   percentage of iterations hitting a miss   (default 10)
//   HEAD_PERCENT   percentage of iterations using HEAD       (default 5)
//   EXECUTOR       ramping-vus or constant-arrival-rate      (default ramping-vus)
//   VUS            target VU count                           (default 50)
//   RATE           arrival rate for constant-arrival-rate     (default 500)
//   TIME_UNIT      arrival rate time unit                    (default 1s)
//   RAMP           ramp-up duration                          (default 10s)
//   DURATION       steady-state duration                     (default 30s)
//   PREALLOC_VUS   VUs pre-allocated for arrival rate        (default scales with the rate)
//   MAX_VUS        VU ceiling for arrival rate               (default 8x PREALLOC_VUS)
//   P95_MS         p95 latency threshold in milliseconds     (default 50)
//   P99_MS         p99 latency threshold in milliseconds     (default 200)
//   FAIL_RATE      max accepted http_req_failed rate         (default 0.01)

import http from 'k6/http';
import { check } from 'k6';
import { Counter, Rate } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://127.0.0.1:5080';
const LINK_IDS = (__ENV.LINK_IDS || 'example')
  .split(',')
  .map((id) => id.trim())
  .filter(Boolean);
const MISS_ID = __ENV.MISS_ID || 'this-link-does-not-exist';
const MISS_PERCENT = Number(__ENV.MISS_PERCENT || '10');
const HEAD_PERCENT = Number(__ENV.HEAD_PERCENT || '5');

const EXECUTOR = __ENV.EXECUTOR || 'ramping-vus';
const VUS = Number(__ENV.VUS || '50');
const RATE = Number(__ENV.RATE || '500');
const TIME_UNIT = __ENV.TIME_UNIT || '1s';
const RAMP = __ENV.RAMP || '10s';
const DURATION = __ENV.DURATION || '30s';
const DRAIN = __ENV.DRAIN || '5s';

const P95_MS = Number(__ENV.P95_MS || '50');
const P99_MS = Number(__ENV.P99_MS || '200');
const FAIL_RATE = Number(__ENV.FAIL_RATE || '0.01');

// Optional id to target URL map, so the Location header can be checked rather than only the status.
const LINK_TARGETS = __ENV.LINK_TARGETS ? JSON.parse(__ENV.LINK_TARGETS) : {};

// clicktracker_redirect_ok covers both the GET and the HEAD path, since both resolve a link and
// return a redirect.
const redirectOk = new Rate('clicktracker_redirect_ok');
const missOk = new Rate('clicktracker_miss_ok');
const locationMismatch = new Counter('clicktracker_location_mismatch');

// 404 is the expected answer for the miss path. Without this, k6 counts every miss as a failed
// request and the failure rate measures the test mix rather than the service.
const expectedStatuses = http.expectedStatuses(200, 302, 404);

function buildScenario() {
  if (EXECUTOR === 'constant-arrival-rate') {
    // VUs are not scaled with the arrival rate. Each VU holds a socket, and the local ephemeral
    // port range only has room for a few tens of thousands, so a pre-allocated VU per request
    // makes the load generator run out of ports before the service runs out of capacity.
    const preAllocated = Number(
      __ENV.PREALLOC_VUS || Math.min(500, Math.max(50, Math.ceil(RATE / 50)))
    );
    const max = Number(__ENV.MAX_VUS || preAllocated * 8);

    return {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: TIME_UNIT,
      duration: DURATION,
      preAllocatedVUs: preAllocated,
      maxVUs: max,
      gracefulStop: '10s',
    };
  }

  return {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: RAMP, target: VUS },
      { duration: DURATION, target: VUS },
      { duration: DRAIN, target: 0 },
    ],
    gracefulRampDown: '10s',
  };
}

export const options = {
  // The redirect itself is what is under test. Following it would measure the target site, not
  // ClickTracker, so the Location header is inspected instead of being resolved.
  maxRedirects: 0,
  scenarios: {
    redirects: buildScenario(),
  },
  thresholds: {
    http_req_failed: [`rate<${FAIL_RATE}`],
    'http_req_duration{name:redirect}': [`p(95)<${P95_MS}`, `p(99)<${P99_MS}`],
    'http_req_duration{name:not_found}': [`p(95)<${P95_MS}`, `p(99)<${P99_MS}`],
    'http_req_duration{name:head}': [`p(95)<${P95_MS}`, `p(99)<${P99_MS}`],
    clicktracker_redirect_ok: [`rate>${1 - FAIL_RATE}`],
    clicktracker_miss_ok: [`rate>${1 - FAIL_RATE}`],
    clicktracker_location_mismatch: ['count==0'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  discardResponseBodies: true,
  noConnectionReuse: false,
};

export function setup() {
  const response = http.get(`${BASE_URL}/${LINK_IDS[0]}`, {
    tags: { name: 'setup' },
  });

  if (response.status !== 302) {
    throw new Error(`Service is not ready: GET /${LINK_IDS[0]} returned ${response.status}`);
  }

  return { linkIds: LINK_IDS };
}

export default function (data) {
  const roll = Math.random() * 100;

  if (roll < MISS_PERCENT) {
    const response = http.get(`${BASE_URL}/${MISS_ID}`, {
      tags: { name: 'not_found' },
      responseCallback: expectedStatuses,
    });
    const ok = response.status === 404;
    missOk.add(ok);
    check(response, { 'miss returns 404': () => ok });
    return;
  }

  const id = data.linkIds[Math.floor(Math.random() * data.linkIds.length)];

  if (roll < MISS_PERCENT + HEAD_PERCENT) {
    const response = http.head(`${BASE_URL}/${id}`, {
      tags: { name: 'head' },
      responseCallback: expectedStatuses,
    });
    const ok = response.status === 302;
    redirectOk.add(ok);
    check(response, { 'head returns 302': () => ok });
    return;
  }

  const response = http.get(`${BASE_URL}/${id}`, {
    tags: { name: 'redirect' },
    responseCallback: expectedStatuses,
  });
  const expected = LINK_TARGETS[id];
  const location = response.headers.Location;

  const ok = response.status === 302;
  redirectOk.add(ok);

  check(response, {
    'redirect returns 302': () => ok,
    'redirect has a Location': () => !!location,
    'redirect Location is the configured target': () => {
      if (!expected) {
        return true;
      }

      const matches = location === expected;
      if (!matches) {
        locationMismatch.add(1);
      }

      return matches;
    },
  });
}
