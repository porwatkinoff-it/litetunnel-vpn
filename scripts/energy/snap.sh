#!/bin/bash
# Saves energy/<name>.tsv: every process's running CPU time and idle wake-ups
# on an iPhone connected by cable, from a 3-second Instruments recording. The
# phone must be unlocked: Instruments counts a locked one as offline. The cost
# of a stretch of time is the difference of two snapshots:
#   python3 scripts/energy/summarize.py diff energy/a.tsv energy/b.tsv <process-name>...
# Nothing records in between, so the phone sleeps as it would off the cable.
#
# usage: DEVICE=<udid> scripts/energy/snap.sh <name>
set -euo pipefail

[ $# -eq 1 ] && [ -n "${DEVICE:-}" ] || { sed -n '9p' "$0"; exit 2; }
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/energy"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
mkdir -p "$OUT"

WHEN="$(date +%s)"
xcrun xctrace record --template 'Activity Monitor' --device "$DEVICE" --all-processes \
  --time-limit 3s --output "$TMP/run.trace" >"$TMP/record.log" 2>&1 ||
  { cat "$TMP/record.log" >&2; exit 1; }
xcrun xctrace export --input "$TMP/run.trace" \
  --xpath '/trace-toc/run[@number="1"]/data/table[@schema="activity-monitor-process-live"]' >"$TMP/live.xml"
python3 -I "$ROOT/scripts/energy/summarize.py" snapshot "$TMP/live.xml" "$WHEN" >"$OUT/$1.tsv"
echo "снимок $1: $(grep -vc '^#' "$OUT/$1.tsv") процессов"
