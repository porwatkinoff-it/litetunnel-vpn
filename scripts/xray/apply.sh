#!/bin/bash
# Adds LiteTunnel's "xray" outbound (scripts/xray/outbound.go) to a sing-box
# checkout: the servers sing-box cannot reach itself (XHTTP, mKCP, VLESS
# Encryption, TCP with an HTTP header, finalmask...) run through Xray-core,
# linked into the same core. Safe to run again on the same checkout.
#
#   scripts/xray/apply.sh <sing-box checkout>
#
# Called by scripts/build-libbox.sh, android/scripts/build-libbox-android.sh
# and desktop/scripts/build-sing-box.sh, after the Reality patch.
set -euo pipefail

# Xray-core v26.11.9. Its module path has no /v26, so Go takes it by commit.
XRAY_COMMIT="676eaf66f8f05d4d558ee7a74b4736623d3feeb9"

HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$(cd "${1:?usage: apply.sh <sing-box checkout>}" && pwd)"

# Xray-core needs Go 1.27, and only the installed Go is used.
export GOTOOLCHAIN=local
if [ "$(go env GOVERSION | sed -E 's/^go1\.([0-9]+).*/\1/')" -lt 27 ]; then
  echo "Xray-core needs Go 1.27 or later, found $(go env GOVERSION)" >&2
  exit 1
fi

mkdir -p "$SRC/protocol/xray"
cp "$HERE/outbound.go" "$SRC/protocol/xray/outbound.go"
git -C "$SRC" apply --reverse --check "$HERE/registry.patch" 2>/dev/null || git -C "$SRC" apply "$HERE/registry.patch"

cd "$SRC"
go get "github.com/xtls/xray-core@$XRAY_COMMIT"
go mod tidy
