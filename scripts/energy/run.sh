#!/bin/bash
# One VPN app's energy test on an iPhone connected by cable. Instruments sees
# the phone only while it is unlocked, so every stretch starts and ends with a
# snapshot taken with the screen on:
#   load  Safari runs speed.cloudflare.com through the tunnel for LOAD seconds
#   idle  the screen locked for at least 5 minutes, then unlocked
# Counts the CPU time and idle wake-ups of the app and its extensions only,
# into energy/<label>.txt and energy/results.tsv.
#
# usage: DEVICE=<udid> scripts/energy/run.sh load <label> <App.app>
#            the app connected to its server, the phone on the home screen
#        DEVICE=<udid> scripts/energy/run.sh watch <label>
#            in the background while the screen is locked: when it was
#        DEVICE=<udid> scripts/energy/run.sh idle <label>
#            after unlocking: the last snapshot and the report
set -euo pipefail

[ $# -ge 2 ] && [ -n "${DEVICE:-}" ] || { sed -n '10,15p' "$0"; exit 2; }
STEP=$1 LABEL=$2 LOAD=${LOAD:-90}
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/energy"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
cd "$ROOT"
mkdir -p "$OUT"

# Executable names of the app's running processes; the extensions are the
# ones under PlugIns/.
app_processes() {
  xcrun devicectl device info processes --device "$DEVICE" --json-output "$TMP/ps.json" >/dev/null 2>&1
  python3 -I - "$TMP/ps.json" "/$1/" <<'EOF'
import json, sys, urllib.parse
procs = json.load(open(sys.argv[1]))["result"]["runningProcesses"]
for p in procs:
    path = urllib.parse.unquote(p.get("executable", ""))
    if sys.argv[2] in path:
        print(("ext " if "/PlugIns/" in path else "app ") + path.rstrip("/").rsplit("/", 1)[-1])
EOF
}

locked() {
  xcrun devicectl device info lockState --device "$DEVICE" --json-output "$TMP/lock.json" >/dev/null 2>&1
  python3 -I -c 'import json, sys; sys.exit(0 if json.load(open(sys.argv[1]))["result"]["passcodeRequired"] else 1)' "$TMP/lock.json"
}

case $STEP in
load)
  app_processes "$3" >"$TMP/names"
  grep -q '^ext ' "$TMP/names" || { echo "у $3 не запущено расширение VPN: подключение не включено" >&2; exit 1; }
  cut -d' ' -f2- "$TMP/names" | sort -u >"$OUT/$LABEL.names"
  echo "процессы $3: $(tr '\n' ' ' <"$OUT/$LABEL.names")"
  scripts/energy/snap.sh "$LABEL-load0"
  xcrun devicectl device process launch --device "$DEVICE" --payload-url https://speed.cloudflare.com com.apple.mobilesafari >/dev/null
  echo "нагрузка: тест скорости в Safari, $LOAD с"
  sleep "$LOAD"
  scripts/energy/snap.sh "$LABEL-load1"
  ;;
watch)
  # Asking every 15 seconds: often enough for the minutes, rare enough not
  # to keep the phone awake.
  rm -f "$OUT/$LABEL.lock"
  since=""
  for _ in $(seq 160); do
    if locked; then
      [ -n "$since" ] || since=$(date +%s)
    elif [ -n "$since" ]; then
      echo "$since $(date +%s)" >"$OUT/$LABEL.lock"
      exit 0
    fi
    sleep 15
  done
  ;;
idle)
  scripts/energy/snap.sh "$LABEL-idle1"
  names=()
  while IFS= read -r n; do names+=("$n"); done <"$OUT/$LABEL.names"
  {
    echo "== $LABEL: нагрузка"
    python3 -I scripts/energy/summarize.py diff "energy/$LABEL-load0.tsv" "energy/$LABEL-load1.tsv" "${names[@]}"
    echo
    echo "== $LABEL: покой"
    if [ -f "$OUT/$LABEL.lock" ]; then
      read -r from to <"$OUT/$LABEL.lock"
      echo "экран заблокирован $(( (to - from) / 60 )) мин"
    fi
    # By name, not pid: a tunnel that restarts keeps its name.
    python3 -I scripts/energy/summarize.py diff "energy/$LABEL-load1.tsv" "energy/$LABEL-idle1.tsv" "${names[@]}"
  } >"$OUT/$LABEL.txt"
  [ -f "$OUT/results.tsv" ] || printf 'приложение\tфаза\tCPU мс\tпробуждений\tCPU мс/мин\tпробуждений/мин\tминут\n' >"$OUT/results.tsv"
  awk -v l="$LABEL" '/^== /{phase = /покой/ ? "покой" : "нагрузка"} /^ИТОГО/{sub(/^ИТОГО/, l "\t" phase); print}' "$OUT/$LABEL.txt" >>"$OUT/results.tsv"
  cat "$OUT/$LABEL.txt"
  ;;
*)
  sed -n '10,15p' "$0"; exit 2
  ;;
esac
