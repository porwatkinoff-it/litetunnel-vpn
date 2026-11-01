#!/bin/bash
# Builds the Windows and Linux packages: the app as one self-contained file
# (no .NET needed on the machine), the patched sing-box from
# desktop/vendor (built by build-sing-box.sh), licences and a short README.
#
#   desktop/scripts/package.sh                 # win-x64 linux-x64
#   desktop/scripts/package.sh linux-arm64     # also win-arm64
#
# Output: dist/LiteTunnel-<version>-windows-x64.zip and
# dist/LiteTunnel-<version>-linux-x64.tar.gz at the repository root.
set -euo pipefail

SING_BOX_VERSION="${SING_BOX_VERSION:-1.14.4}"
DESKTOP="$(cd "$(dirname "$0")/.." && pwd)"
REPO="$(cd "$DESKTOP/.." && pwd)"
DIST="$REPO/dist"
VENDOR="$DESKTOP/vendor/sing-box-$SING_BOX_VERSION"
PATCH="$REPO/scripts/patches/sing-box-reality-mlkem.patch"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$DESKTOP/Directory.Build.props")"
RIDS=("$@")
[ ${#RIDS[@]} -gt 0 ] || RIDS=(win-x64 linux-x64)

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

[ -n "$VERSION" ] || { echo "no <Version> in Directory.Build.props" >&2; exit 1; }
[ -f "$PATCH" ] || { echo "missing $PATCH" >&2; exit 1; }
[ -f "$VENDOR/BUILD.txt" ] || { echo "missing $VENDOR, run build-sing-box.sh first" >&2; exit 1; }

sha256() {
  if command -v sha256sum >/dev/null; then sha256sum "$@"; else shasum -a 256 "$@"; fi
}

# The tar on macOS would otherwise add ._ files, extended attributes and the
# builder's user name.
make_tar() {
  local out="$1" dir="$2" name="$3"
  if tar --version 2>/dev/null | grep -q bsdtar; then
    COPYFILE_DISABLE=1 tar --no-xattrs --no-mac-metadata --uid 0 --gid 0 --uname root --gname root \
      -czf "$out" -C "$dir" "$name"
  else
    tar --no-xattrs --owner 0 --group 0 --numeric-owner -czf "$out" -C "$dir" "$name"
  fi
}

# Notepad reads the README better with Windows line endings.
fill() {
  local src="$1" dst="$2" crlf="$3"
  sed -e "s/@VERSION@/$VERSION/g" -e "s/@SING_BOX@/$SING_BOX_VERSION/g" "$src" >"$dst"
  [ "$crlf" = 0 ] || perl -pi -e 's/\r?\n/\r\n/' "$dst"
}

archives=()
for rid in "${RIDS[@]}"; do
  case "$rid" in
    win-x64 | win-arm64) os=windows exe=.exe lib=libcronet.dll ;;
    linux-x64 | linux-arm64) os=linux exe= lib=libcronet.so ;;
    *) echo "unknown runtime $rid" >&2; exit 1 ;;
  esac
  arch="${rid#*-}"
  core="$VENDOR/$os-${arch/x64/amd64}"
  [ -f "$core/sing-box$exe" ] || { echo "missing $core/sing-box$exe, run build-sing-box.sh $os/${arch/x64/amd64}" >&2; exit 1; }

  name="LiteTunnel-$VERSION-$os-$arch"
  publish="$DESKTOP/build/publish/$rid"
  stage="$DESKTOP/build/package"
  rm -rf "$publish" "${stage:?}/$name"
  mkdir -p "$stage/$name"

  # One file, nothing trimmed or compressed: compression and self-extraction
  # cost CPU on every start. The native Skia and HarfBuzz libraries stay
  # next to it.
  "$DOTNET" publish "$DESKTOP/src/LiteTunnel.App/LiteTunnel.App.csproj" -c Release -r "$rid" \
    --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false \
    -p:DebugType=none -p:DebugSymbols=false -o "$publish"

  cp -R "$publish"/. "$stage/$name"/
  cp "$core/sing-box$exe" "$core/$lib" "$stage/$name"/
  cp "$REPO/LICENSE" "$stage/$name/LICENSE"
  cp "$core/LICENSE" "$stage/$name/LICENSE.sing-box"
  cp "$VENDOR/BUILD.txt" "$stage/$name/SING-BOX-BUILD.txt"
  cp "$PATCH" "$stage/$name"/

  out="$DIST/$name"
  if [ "$os" = windows ]; then
    fill "$DESKTOP/packaging/windows/README.txt" "$stage/$name/README.txt" 1
    out="$out.zip"
    rm -f "$out"
    (cd "$stage" && zip -qrX "$out" "$name")
  else
    fill "$DESKTOP/packaging/linux/README.txt" "$stage/$name/README.txt" 0
    cp "$DESKTOP/packaging/linux/install.sh" "$DESKTOP/packaging/linux/litetunnel.desktop" "$stage/$name"/
    cp "$DESKTOP/src/LiteTunnel.App/Assets/LiteTunnel.png" "$stage/$name/litetunnel.png"
    find "$stage/$name" -type f -exec chmod 644 {} +
    chmod 755 "$stage/$name/LiteTunnel" "$stage/$name/sing-box" "$stage/$name/install.sh"
    out="$out.tar.gz"
    rm -f "$out"
    make_tar "$out" "$stage" "$name"
  fi
  archives+=("$out")
done

echo
for a in "${archives[@]}"; do
  echo "$(du -h "$a" | cut -f1)	$a"
done
(cd "$DIST" && sha256 "${archives[@]##*/}")
