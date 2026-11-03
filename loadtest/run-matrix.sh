#!/usr/bin/env bash
# Runs the ClickTracker load test across a matrix of load levels and prints one row per level.
#
# Usage:
#   loadtest/run-matrix.sh                        # closed-loop sweep over VU counts
#   loadtest/run-matrix.sh --executor constant-arrival-rate --rates 1000,5000,10000
#
# Each run writes a k6 JSON summary to $OUT_DIR so the numbers can be re-read later.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
K6="$ROOT/k6"
SCRIPT="$ROOT/loadtest/redirect.js"

BASE_URL="${BASE_URL:-http://127.0.0.1:5080}"
OUT_DIR="${OUT_DIR:-/tmp/ct-perf/results}"
RAMP="${RAMP:-10s}"
DURATION="${DURATION:-30s}"
DRAIN="${DRAIN:-5s}"
EXECUTOR="${EXECUTOR:-ramping-vus}"
VU_LEVELS="${VU_LEVELS:-10,25,50,100,200,400,800}"
RATE_LEVELS="${RATE_LEVELS:-1000,2500,5000,10000,20000,40000}"
LINK_IDS="${LINK_IDS:-link-001,link-002,link-003,link-004,link-005,link-006,link-007,link-008,link-009,link-010,link-011,link-012,link-013,link-014,link-015,link-016,link-017,link-018,link-019,link-020,link-021,link-022,link-023,link-024,link-025,link-026,link-027,link-028,link-029,link-030,link-031,link-032,link-033,link-034,link-035,link-036,link-037,link-038,link-039,link-040,link-041,link-042,link-043,link-044,link-045,link-046,link-047,link-048,link-049,link-050}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --executor) EXECUTOR="$2"; shift 2 ;;
    --rates) RATE_LEVELS="$2"; shift 2 ;;
    --vus) VU_LEVELS="$2"; shift 2 ;;
    --duration) DURATION="$2"; shift 2 ;;
    --ramp) RAMP="$2"; shift 2 ;;
    --out) OUT_DIR="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

mkdir -p "$OUT_DIR"

LEVELS="$VU_LEVELS"
[[ "$EXECUTOR" == "constant-arrival-rate" ]] && LEVELS="$RATE_LEVELS"

echo "executor=$EXECUTOR base=$BASE_URL ramp=$RAMP duration=$DURATION"

for level in ${LEVELS//,/ }; do
  label="${EXECUTOR}-${level}"
  export BASE_URL LINK_IDS
  export EXECUTOR RAMP DURATION DRAIN

  if [[ "$EXECUTOR" == "constant-arrival-rate" ]]; then
    export RATE="$level"
    export VUS="$level"
  else
    export VUS="$level"
  fi

  echo "=== $label ==="
  "$K6" run --quiet --summary-mode=full --summary-export "$OUT_DIR/$label.json" "$SCRIPT" \
    > "$OUT_DIR/$label.log" 2>&1 || true
done

python3 "$ROOT/loadtest/report.py" "$OUT_DIR"
