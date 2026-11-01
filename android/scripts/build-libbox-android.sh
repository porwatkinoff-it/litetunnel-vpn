#!/bin/bash
# Builds libbox.aar (sing-box core) for Android, the same version and the same
# protocol set as Libbox.xcframework in scripts/build-libbox.sh.
set -euo pipefail

SING_BOX_TAG="${SING_BOX_TAG:-v1.14.4}"
GOMOBILE_VERSION="v0.1.13"
ANDROID_DIR="$(cd "$(dirname "$0")/.." && pwd)"
ROOT="$(cd "$ANDROID_DIR/.." && pwd)"
SRC="$ROOT/build/sing-box"

export ANDROID_HOME="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
export ANDROID_NDK_HOME="${ANDROID_NDK_HOME:-$ANDROID_HOME/ndk/27.1.12297006}"
export JAVA_HOME="${JAVA_HOME:-$(/usr/libexec/java_home -v 21)}"
export PATH="$(go env GOPATH)/bin:$JAVA_HOME/bin:$PATH"

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
# max-page-size: phones with 16 KB pages (Android 15+) refuse to load a 4 KB-aligned .so.
LDFLAGS="-X github.com/sagernet/sing-box/constant.Version=$VERSION -X runtime.godebugDefault=multipathtcp=0,tlssha1=1 -checklinkname=0 -s -w -buildid= -extldflags=-Wl,-z,max-page-size=16384"

# Phones only: arm64 and 32-bit arm. The emulator on Apple silicon is arm64 too.
cd "$SRC"
gomobile bind -v \
  -o "$ANDROID_DIR/app/libs/libbox.aar" \
  -target "${PLATFORMS:-android/arm64,android/arm}" \
  -androidapi 24 \
  -javapkg=io.nekohasekai \
  -libname=box \
  -trimpath -buildvcs=false \
  -ldflags "$LDFLAGS" \
  -tags "$TAGS" \
  ./experimental/libbox

rm -f "$ANDROID_DIR/app/libs/libbox-sources.jar"
echo "libbox.aar -> $ANDROID_DIR/app/libs"
