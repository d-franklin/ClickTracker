#!/usr/bin/env python3
"""Turns the k6 JSON summaries in a results directory into one table."""

import json
import os
import sys


def stat(metrics, name, key):
    """Reads a stat out of a metric, returning None when the metric was never recorded."""
    entry = metrics.get(name)
    if entry is None:
        return None
    return entry.get(key)


def has_samples(entry):
    """k6 emits a tagged metric with all-zero values when that tag never carried a request."""
    if not entry:
        return False
    return any(value for key, value in entry.items() if key != "thresholds")


def primary(metrics, key):
    """Reads a latency stat for the request type that carried the load.

    A GET records a click, a HEAD does not, so a HEAD-only run measures the lookup and redirect
    path without the click pipeline in front of it.
    """
    for tag in ("name:redirect", "name:head"):
        entry = metrics.get(f"http_req_duration{{{tag}}}")
        if not has_samples(entry):
            continue
        value = entry.get(key)
        if value is not None:
            return value
    return None


def main(directory):
    rows = []
    for name in sorted(os.listdir(directory)):
        if not name.endswith(".json"):
            continue
        with open(os.path.join(directory, name), encoding="utf-8") as handle:
            report = json.load(handle)

        metrics = report.get("metrics", {})

        rows.append(
            {
                "run": name[: -len(".json")],
                "reqs": stat(metrics, "http_reqs", "count") or 0,
                "rps": stat(metrics, "http_reqs", "rate") or 0,
                "avg_ms": primary(metrics, "avg") or 0,
                "p95_ms": primary(metrics, "p(95)") or 0,
                "p99_ms": primary(metrics, "p(99)") or 0,
                "miss_p95_ms": stat(metrics, "http_req_duration{name:not_found}", "p(95)") or 0,
                "fail_pct": (stat(metrics, "http_req_failed", "value") or 0) * 100,
                "dropped": stat(metrics, "dropped_iterations", "count") or 0,
                "checks_failed": stat(metrics, "checks", "fails") or 0,
            }
        )

    if not rows:
        print("no k6 summaries found")
        return

    header = (
        f"{'run':<30}{'reqs':>12}{'req/s':>12}{'avg ms':>9}{'p95 ms':>9}"
        f"{'p99 ms':>9}{'miss p95':>10}{'fail %':>8}{'dropped':>9}{'bad checks':>12}"
    )
    print(header)
    print("-" * len(header))
    for row in rows:
        print(
            f"{row['run']:<30}{row['reqs']:>12,}{row['rps']:>12,.0f}"
            f"{row['avg_ms']:>9.3f}{row['p95_ms']:>9.3f}{row['p99_ms']:>9.3f}"
            f"{row['miss_p95_ms']:>10.3f}{row['fail_pct']:>8.2f}"
            f"{row['dropped']:>9,}{row['checks_failed']:>12,}"
        )


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
