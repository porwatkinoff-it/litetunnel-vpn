#!/bin/bash
# Checks the configs the app builds against a real sing-box, with no device,
# signing or root. Two parts:
#   1. matrix: every supported link type x 4 settings variants -> sing-box check
#   2. e2e: local VLESS-Reality and Hysteria2 servers, a subscription served over
#      http (as links and as Xray JSON), and one client per server built by the
#      app's own code (LiteKit).
#      e2e.test resolves only on the server side, so a reply proves the tunnel.
#      Servers named "BAD ..." carry a wrong key or obfs and must be refused.
#
# usage: scripts/e2e/run.sh          run both parts, exit 1 on any failure
#        scripts/e2e/run.sh serve    keep the servers up for the app in the
#                                    Simulator (http://127.0.0.1:18090/sub, /xray)
# Needs Go, Xcode, python3, curl. Builds into build/e2e; rm -rf it to start over.
set -uo pipefail

SING_BOX_TAG="${SING_BOX_TAG:-v1.14.4}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
SRC="$ROOT/build/sing-box"
W="$ROOT/build/e2e"
SB="$W/sing-box"
K="$W/keys"
RS="$W/rulesets"

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
# curl prints 0.29, which printf %f rejects under a comma-decimal locale.
export LC_NUMERIC=C

die() { echo "ОШИБКА: $*" >&2; exit 1; }
mkdir -p "$W"

for port in 18080 18090 18444 20443 20444 20445; do
  nc -z 127.0.0.1 "$port" 2>/dev/null && die "порт $port уже занят, остановите старый запуск"
done

# --- setup: everything below is reused on the next run ---

if [ ! -x "$SB" ]; then
  if [ ! -d "$SRC" ]; then
    git clone --depth 1 --branch "$SING_BOX_TAG" https://github.com/SagerNet/sing-box.git "$SRC" || die "не удалось скачать sing-box"
  fi
  echo "== Сборка sing-box ${SING_BOX_TAG#v} (один раз, ~1 мин)"
  # Same protocol tags as the app's Libbox (scripts/build-libbox.sh).
  (cd "$SRC" && go build -trimpath -buildvcs=false \
    -tags "with_gvisor,with_quic,with_utls,with_wireguard,with_clash_api,badlinkname,tfogo_checklinkname0" \
    -ldflags "-X github.com/sagernet/sing-box/constant.Version=${SING_BOX_TAG#v} -checklinkname=0 -s -w" \
    -o "$SB" ./cmd/sing-box) || die "go build sing-box"
fi

if [ ! -f "$K/cert.pem" ]; then
  mkdir -p "$K"
  "$SB" generate reality-keypair >"$K/reality.txt"
  # A second pair whose public key the server does not know.
  "$SB" generate reality-keypair >"$K/reality-wrong.txt"
  "$SB" generate tls-keypair example.com >"$K/tls.pem"
  awk '/BEGIN PRIVATE KEY/,/END PRIVATE KEY/' "$K/tls.pem" >"$K/key.pem"
  awk '/BEGIN CERTIFICATE/,/END CERTIFICATE/' "$K/tls.pem" >"$K/cert.pem"
fi

if [ ! -f "$W/www/big.bin" ]; then
  mkdir -p "$W/www"
  openssl rand -hex 16 >"$W/www/index.txt"
  head -c 33554432 /dev/urandom >"$W/www/big.bin"
fi

# Tiny stand-ins with the real tags, so configs that reference rule-sets load.
mkdir -p "$RS"
echo '{"version":3,"rules":[{"domain_suffix":["ru"]}]}' >"$RS/geosite-category-ru.json"
echo '{"version":3,"rules":[{"ip_cidr":["77.88.0.0/18"]}]}' >"$RS/geoip-ru.json"
echo '{"version":3,"rules":[{"domain_suffix":["doubleclick.net"]}]}' >"$RS/geosite-category-ads-all.json"
for f in "$RS"/*.json; do "$SB" rule-set compile "$f" -o "${f%.json}.srs" || die "rule-set $f"; done

# Rebuilt every run, so they always use the current LiteKit sources.
echo "== Сборка тестовых утилит из LiteKit"
for tool in matrix driver; do
  xcrun swiftc -o "$W/$tool" "$ROOT"/Packages/LiteKit/Sources/LiteKit/*.swift "$HERE/$tool/main.swift" || die "swiftc $tool"
done

cd "$W"
failures=0

# --- part 1: matrix ---

if [ "${1:-}" != serve ]; then
  echo "== 1. sing-box check: все типы ссылок × 4 набора настроек"
  rm -rf configs
  ./matrix configs "$RS" || failures=$((failures + 1))
  pass=0; fail=0
  for c in configs/*.json; do
    if msg=$("$SB" check -c "$c" 2>&1); then
      pass=$((pass + 1))
    else
      fail=$((fail + 1)); echo "FAIL $(basename "$c" .json): $(echo "$msg" | head -3)"
    fi
  done
  echo "check: прошли $pass, с ошибками $fail"
  failures=$((failures + fail))
fi

# --- part 2: e2e ---

rm -rf logs clients; mkdir -p logs clients
PIDS=()
cleanup() { for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null; done; wait 2>/dev/null; }
trap cleanup EXIT
# Background jobs of a script ignore Ctrl-C; exiting runs cleanup for them.
trap 'exit 130' INT TERM
wait_port() { for _ in $(seq 50); do nc -z 127.0.0.1 "$1" 2>/dev/null && return 0; sleep 0.1; done; return 1; }

python3 -I "$HERE/make_server.py" "$W" || die "server.json"
python3 -I -m http.server 18080 --bind 127.0.0.1 --directory www >logs/target.log 2>&1 & PIDS+=($!)
python3 -I "$HERE/sub_server.py" "$W" >logs/sub.log 2>&1 & PIDS+=($!)
"$SB" run -c server.json >logs/server.log 2>&1 & PIDS+=($!)
wait_port 18080 && wait_port 18090 && wait_port 20443 || { cat logs/server.log; die "серверы не запустились"; }

if [ "${1:-}" = serve ]; then
  echo "Серверы запущены. В приложении в симуляторе добавьте подписку:"
  echo "  http://127.0.0.1:18090/sub    (ссылки)"
  echo "  http://127.0.0.1:18090/xray   (Xray JSON)"
  echo "Ctrl-C — остановить. Журнал сервера: $W/logs/server.log"
  # A foreground tail would hold off the signal trap; wait does not.
  tail -f logs/sub.log & PIDS+=($!)
  wait $!
  exit 0
fi

echo "== 2. Подписка (код приложения: SubscriptionClient -> AppState.apply -> ConfigBuilder)"
./driver http://127.0.0.1:18090/sub clients/sub | tee logs/driver-sub.txt || die "driver"
echo "== 2б. Та же подписка в формате Xray JSON (как Remnawave отдаёт её Happ)"
./driver http://127.0.0.1:18090/xray clients/xray | tee logs/driver-xray.txt || die "driver xray"
# The server above gave the list only because the app sent x-hwid.
if curl -s -m 3 http://127.0.0.1:18090/sub | base64 -D | grep -q 'App%20not%20supported'; then
  echo "ok: без x-hwid сервер отдаёт заглушку, как Remnawave с лимитом устройств"
else
  echo "FAIL: сервер выдал подписку без x-hwid, проверка ничего не доказывает"; failures=$((failures + 1))
fi
cat logs/sub.log

echo "== Без туннеля e2e.test не открывается"
if curl -s -m 3 http://e2e.test:18080/index.txt >/dev/null 2>&1; then
  echo "FAIL: e2e.test открылся напрямую, тест ничего не докажет"; failures=$((failures + 1))
else
  echo "ok: прямого пути нет"
fi

TOKEN="$(cat www/index.txt)"
SIZE=$(stat -f %z www/big.bin)
n=0
while IFS=$'\t' read -r dir file name proto; do
  n=$((n + 1)); port=$((21080 + n)); file="$dir-$file"
  python3 -I "$HERE/patch.py" "clients/$dir/${file#*-}.json" "clients/$file.json" "$port"
  "$SB" check -c "clients/$file.json" || { echo "FAIL check $name"; failures=$((failures + 1)); continue; }
  "$SB" run -c "clients/$file.json" >"logs/$file.log" 2>&1 & cpid=$!; PIDS+=($cpid)
  wait_port "$port" || { echo "FAIL $name: клиент не запустился"; failures=$((failures + 1)); kill $cpid; continue; }
  got="$(curl -s -m 8 -x "socks5h://127.0.0.1:$port" http://e2e.test:18080/index.txt)"
  if [ "$got" = "$TOKEN" ]; then
    stats="$(curl -s -m 30 -x "socks5h://127.0.0.1:$port" -o /dev/null -w '%{size_download} %{speed_download} %{time_total}' http://e2e.test:18080/big.bin)"
    read -r bytes speed secs <<<"$stats"
    http="$(curl -s -m 8 -x "http://127.0.0.1:$port" -o /dev/null -w '%{http_code}' http://e2e.test:18080/index.txt)"
    ok=$([ "$bytes" = "$SIZE" ] && echo ok || echo "SHORT($bytes)")
    printf "PASS  %-22s %-10s ответ ok · 32 МиБ %s за %.2f с (%s МБ/с) · HTTP-прокси %s\n" "$name" "$proto" "$ok" "$secs" "$(echo "$speed/1048576" | bc)" "$http"
    case "$name" in BAD*) echo "  ^ FAIL: должен был быть отклонён"; failures=$((failures + 1)) ;; esac
    [ "$ok" = ok ] && [ "$http" = 200 ] || failures=$((failures + 1))
    kill $cpid 2>/dev/null; wait $cpid 2>/dev/null
  else
    # The error is logged once curl gives up, so stop the client before reading it.
    # A wrong salamander password gets no reply at all: it shows as "context canceled".
    kill $cpid 2>/dev/null; wait $cpid 2>/dev/null
    reason="$(sed -E $'s/\x1b\\[[0-9;]*m//g' "logs/$file.log" | grep -m1 ' ERROR ' | sed -E 's/.*\]: //' | cut -c1-90)"
    printf "BLOCK %-22s %-10s отклонён (%s)\n" "$name" "$proto" "${reason:-нет ответа}"
    case "$name" in BAD*) ;; *) echo "  ^ FAIL: должен был работать"; failures=$((failures + 1)) ;; esac
  fi
done < <(for d in sub xray; do awk -v d="$d" '/^client-/ { print d "\t" $0 }' "logs/driver-$d.txt"; done)

echo "== Сервер видел соединения"
grep -cE 'inbound/vless\[vless-in\]: inbound connection' logs/server.log | sed 's/^/vless-in: /'
grep -cE 'inbound/hysteria2\[hy2-in\]: inbound connection' logs/server.log | sed 's/^/hy2-in:   /'
grep -cE 'inbound/hysteria2\[hy2-plain\]: inbound connection' logs/server.log | sed 's/^/hy2-plain: /'

echo
if [ "$failures" -eq 0 ]; then
  echo "ВСЁ ПРОШЛО"
else
  echo "ОШИБОК: $failures (журналы: $W/logs)"; exit 1
fi
