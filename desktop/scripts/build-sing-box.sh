#!/bin/bash
# Builds the sing-box core for the desktop app from source, with the Reality
# patch: the release binaries strip the X25519MLKEM768 key share, and Xray
# 26.9+ turns such clients away (see scripts/patches/sing-box-reality-mlkem.patch).
#
# Everything else is as in the official release workflow at the tag
# (.github/workflows/build.yml): the same build tags, ldflags and libcronet,
# CGO off, naive outbound through purego with libcronet next to the binary.
#
#   desktop/scripts/build-sing-box.sh                 # windows/amd64 linux/amd64
#   desktop/scripts/build-sing-box.sh linux/arm64     # also windows/arm64
#
# Output: desktop/vendor/sing-box-<version>/<os>-<arch>/, plus BUILD.txt there.
set -euo pipefail

SING_BOX_TAG="${SING_BOX_TAG:-v1.14.4}"
VERSION="${SING_BOX_TAG#v}"
DESKTOP="$(cd "$(dirname "$0")/.." && pwd)"
REPO="$(cd "$DESKTOP/.." && pwd)"
PATCH="$REPO/scripts/patches/sing-box-reality-mlkem.patch"
SRC="$DESKTOP/build/sing-box-$SING_BOX_TAG"
OUT="$DESKTOP/vendor/sing-box-$VERSION"
TARGETS=("$@")
[ ${#TARGETS[@]} -gt 0 ] || TARGETS=(windows/amd64 linux/amd64)

# The Go that is installed, never a downloaded one. The release uses 1.26.x.
export GOTOOLCHAIN="${GOTOOLCHAIN:-local}"
GO_MINOR="$(go env GOVERSION | sed -E 's/^go1\.([0-9]+).*/\1/')"

for target in "${TARGETS[@]}"; do
  case "$target" in
    windows/amd64 | linux/amd64 | linux/arm64) ;;
    windows/arm64)
      # Before Go 1.27, AES and SHA-256 on windows/arm64 run without CPU
      # features (golang/go#76791); the release patches Go for that.
      if [ "$GO_MINOR" -lt 27 ]; then
        echo "windows/arm64 needs Go 1.27 or later" >&2
        exit 1
      fi
      ;;
    *)
      echo "unknown target: $target (windows/amd64, windows/arm64, linux/amd64, linux/arm64)" >&2
      exit 1
      ;;
  esac
done

if [ ! -d "$SRC" ]; then
  mkdir -p "$DESKTOP/build"
  git clone --depth 1 --branch "$SING_BOX_TAG" https://github.com/SagerNet/sing-box.git "$SRC"
fi

# Skipped when already applied.
git -C "$SRC" apply --reverse --check "$PATCH" 2>/dev/null || git -C "$SRC" apply "$PATCH"

# libcronet for naive, from the cronet-go commit the release pins.
CRONET_GO_VERSION="$(cat "$SRC/.github/CRONET_GO_VERSION")"
CRONET="$DESKTOP/build/cronet-go-lib"
if [ ! -d "$CRONET/.git" ]; then
  git init -q "$CRONET"
  git -C "$CRONET" remote add origin https://github.com/sagernet/cronet-go.git
fi
LIBRARIES=()
for target in "${TARGETS[@]}"; do
  os="${target%/*}"
  arch="${target#*/}"
  if [ "$os" = windows ]; then
    LIBRARIES+=("/lib/${os}_${arch}/libcronet.dll")
  else
    LIBRARIES+=("/lib/${os}_${arch}/libcronet.so")
  fi
done
git -C "$CRONET" sparse-checkout set --no-cone "${LIBRARIES[@]}"
git -C "$CRONET" fetch -q --depth=1 --filter=blob:none origin "$CRONET_GO_VERSION"
git -C "$CRONET" checkout -q FETCH_HEAD

LDFLAGS="-X 'github.com/sagernet/sing-box/constant.Version=$VERSION' $(cat "$SRC/release/LDFLAGS") -s -w -buildid="
mkdir -p "$OUT"

for target in "${TARGETS[@]}"; do
  os="${target%/*}"
  arch="${target#*/}"
  dir="$OUT/$os-$arch"
  if [ "$os" = windows ]; then
    tags="$(cat "$SRC/release/DEFAULT_BUILD_TAGS_WINDOWS")"
    binary=sing-box.exe
    library=libcronet.dll
  else
    tags="$(cat "$SRC/release/DEFAULT_BUILD_TAGS"),with_purego"
    binary=sing-box
    library=libcronet.so
  fi

  echo "sing-box $VERSION for $os/$arch"
  rm -rf "$dir"
  mkdir -p "$dir"
  (cd "$SRC" && CGO_ENABLED=0 GOOS="$os" GOARCH="$arch" \
    go build -trimpath -o "$dir/$binary" -tags "$tags" -ldflags "$LDFLAGS" ./cmd/sing-box)
  cp "$CRONET/lib/${os}_${arch}/$library" "$dir/"
  cp "$SRC/LICENSE" "$dir/"

  # The patch is in: its helper's name is left in the binary.
  if [ "$(strings "$dir/$binary" | grep -c realityHybridIndex)" -eq 0 ]; then
    echo "$dir/$binary: no realityHybridIndex, the Reality patch is not in" >&2
    exit 1
  fi
done

{
  echo "sing-box $VERSION, built by desktop/scripts/build-sing-box.sh"
  echo "source:  https://github.com/SagerNet/sing-box $SING_BOX_TAG ($(git -C "$SRC" rev-parse HEAD))"
  echo "patch:   scripts/patches/sing-box-reality-mlkem.patch ($(shasum -a 256 "$PATCH" | cut -d' ' -f1))"
  echo "cronet:  https://github.com/sagernet/cronet-go $CRONET_GO_VERSION"
  echo "go:      $(go env GOVERSION)"
  echo "linux:   $(cat "$SRC/release/DEFAULT_BUILD_TAGS"),with_purego"
  echo "windows: $(cat "$SRC/release/DEFAULT_BUILD_TAGS_WINDOWS")"
  echo
  (cd "$OUT" && find . -type f ! -name BUILD.txt | sort | sed 's|^\./||' | xargs shasum -a 256)
} > "$OUT/BUILD.txt"

echo "-> $OUT"
