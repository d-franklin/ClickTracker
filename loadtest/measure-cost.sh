#!/usr/bin/env bash
# Measures the cost of a fixed offered load: how much CPU the service burns per request, how many
# clicks actually reach SQLite, and how many the queue drops on the way.
#
# Usage: loadtest/measure-cost.sh <rate> [duration]

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RATE="${1:-20000}"
DURATION="${2:-60s}"
DB="${DB:-/tmp/ct-perf/clicks.db}"
APP_PID="${APP_PID:-$(pgrep -x ClickTracker | head -1)}"
APP_LOG="${APP_LOG:-/tmp/ct-perf/app.log}"
SUMMARY="${SUMMARY:-/tmp/ct-perf/cost-run.json}"

if [[ -z "$APP_PID" ]]; then
  echo "could not find the ClickTracker process" >&2
  exit 1
fi

cpu_ticks() {
  # utime + stime for the process, in clock ticks.
  awk '{print $14 + $15}' "/proc/$1/stat"
}

rows() {
  python3 -c "
import sqlite3
print(sqlite3.connect('$DB').execute('select count(*) from clicks').fetchone()[0])
"
}

log_drops() {
  grep "clicks were dropped" "$APP_LOG" | tail -1 | grep -oE "[0-9]+" | tail -1 || echo 0
}

start_app_cpu="$(cpu_ticks "$APP_PID")"
start_rows="$(rows)"
start_drops="$(log_drops)"

"$ROOT/k6" run --quiet --summary-export "$SUMMARY" --summary-mode=full \
  -e "EXECUTOR=constant-arrival-rate" -e "RATE=$RATE" -e "DURATION=$DURATION" \
  "$ROOT/loadtest/redirect.js" > /tmp/ct-perf/cost-run.log 2>&1 &
K6_PID=$!

# Sample the load generator's CPU while the run is in flight. The value in /proc is cumulative, so
# the difference between samples is what gets added up.
k6_cpu_ticks=0
previous_k6_cpu=0
while kill -0 "$K6_PID" 2>/dev/null; do
  if [[ -r "/proc/$K6_PID/stat" ]]; then
    current_k6_cpu="$(cpu_ticks "$K6_PID")"
    k6_cpu_ticks=$(( k6_cpu_ticks + current_k6_cpu - previous_k6_cpu ))
    previous_k6_cpu="$current_k6_cpu"
  fi
  sleep 1
done
wait "$K6_PID" || true

end_app_cpu="$(cpu_ticks "$APP_PID")"
end_rows="$(rows)"
end_drops="$(log_drops)"

hz="$(getconf CLK_TCK)"

python3 - "$APP_PID" "$end_app_cpu" "$start_app_cpu" "$k6_cpu_ticks" "$hz" \
  "$start_rows" "$end_rows" "$start_drops" "$end_drops" "$SUMMARY" <<'PY'
import json
import sys

(pid, end_cpu, start_cpu, k6_ticks, hz, start_rows, end_rows, start_drops, end_drops, summary) = sys.argv[1:]
end_cpu, start_cpu = int(end_cpu), int(start_cpu)
k6_ticks = int(k6_ticks)
hz = int(hz)
start_rows, end_rows = int(start_rows), int(end_rows)
start_drops, end_drops = int(start_drops), int(end_drops)

metrics = json.load(open(summary))["metrics"]
requests = metrics["http_reqs"]["count"]
rate = metrics["http_reqs"]["rate"]
window = requests / rate if rate else 0

app_cpu = (end_cpu - start_cpu) / hz
k6_cpu = k6_ticks / hz

get_clicks = requests * 0.85
written = end_rows - start_rows
dropped = end_drops - start_drops

print()
print(f"app pid                 {pid}")
print(f"offered rate            {rate:,.0f} req/s achieved over {window:.1f} s")
print(f"requests                {requests:,}")
print(f"app CPU time            {app_cpu:.1f} s  ({app_cpu / window * 100:.0f}% of one core)")
print(f"k6  CPU time            {k6_cpu:.1f} s  ({k6_cpu / window * 100:.0f}% of one core)")
print(f"app CPU per request     {app_cpu / requests * 1e6:.1f} us")
print(f"clicks enqueued         {get_clicks:,.0f}")
print(f"clicks written          {written:,}  ({written / window:,.0f} clicks/s)")
print(f"clicks dropped          {dropped:,}  ({dropped / get_clicks * 100:.1f}% of enqueued)")
PY
