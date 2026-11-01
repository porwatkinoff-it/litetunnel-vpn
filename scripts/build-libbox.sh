#!/bin/bash
# Builds Libbox.xcframework (sing-box core) for iOS, iOS Simulator and macOS.
# Only the protocols the app uses are compiled in, to keep the Network Extension
# small (iOS kills a packet tunnel that goes past ~50 MB).
set -euo pipefail

SING_BOX_TAG="${SING_BOX_TAG:-v1.14.4}"
GOMOBILE_VERSION="v0.1.13"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/build/sing-box"

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"
export PATH="$(go env GOPATH)/bin:$PATH"

if [ ! -d "$SRC" ]; then
  git clone --depth 1 --branch "$SING_BOX_TAG" https://github.com/SagerNet/sing-box.git "$SRC"
fi

# Reality against Xray 26.9+ (see the patch). Skipped when already applied.
PATCH="$ROOT/scripts/patches/sing-box-reality-mlkem.patch"
git -C "$SRC" apply --reverse --check "$PATCH" 2>/dev/null || git -C "$SRC" apply "$PATCH"

go install "github.com/sagernet/gomobile/cmd/gomobile@$GOMOBILE_VERSION"
go install "github.com/sagernet/gomobile/cmd/gobind@$GOMOBILE_VERSION"

# with_quic: Hysteria2, TUIC. with_utls: Reality and fp=. with_gvisor: TUN stack.
TAGS="with_gvisor,with_quic,with_utls,with_wireguard,with_clash_api,grpcnotrace,badlinkname,tfogo_checklinkname0"
VERSION="${SING_BOX_TAG#v}"
LDFLAGS="-X github.com/sagernet/sing-box/constant.Version=$VERSION -X runtime.godebugDefault=multipathtcp=0,tlssha1=1 -checklinkname=0 -s -w -buildid="

cd "$SRC"
gomobile bind -v \
  -target "${PLATFORMS:-ios,iossimulator,macos}" \
  -libname=box \
  -tags-not-macos=with_low_memory \
  -iosversion=16.0 -macosversion=13.0 \
  -trimpath -buildvcs=false \
  -ldflags "$LDFLAGS" \
  -tags "$TAGS" \
  ./experimental/libbox

rm -rf "$ROOT/Frameworks/Libbox.xcframework"
mv Libbox.xcframework "$ROOT/Frameworks/"
echo "Libbox.xcframework -> $ROOT/Frameworks"
