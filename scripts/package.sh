#!/bin/bash
# Builds dist/LiteTunnel-<version>.ipa and .dmg without an Apple Developer
# account: Release builds, ad-hoc signed with the app's entitlements.
# The system accepts ad-hoc VPN entitlements only in special setups (TrollStore
# on iOS). For a normal iPhone or Mac, set DEVELOPMENT_TEAM in
# Config/Signing.xcconfig (paid team) and build signed in Xcode, or re-sign these.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DD="$ROOT/.build-dd"
OUT="$ROOT/dist"
VERSION="$(sed -n 's/^ *MARKETING_VERSION: *"\(.*\)"/\1/p' "$ROOT/project.yml")"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"

build() { # scheme destination
  xcodebuild -project LiteTunnel.xcodeproj -scheme "$1" -configuration Release \
    -destination "$2" -derivedDataPath "$DD" CODE_SIGNING_ALLOWED=NO build -quiet
}

# The entitlements files hold $(LT_APP_GROUP); take the value Xcode wrote into
# the app's Info.plist, so the entitlement matches what the code opens.
entitlements() { # entitlements-file app out
  local group
  group="$(/usr/libexec/PlistBuddy -c 'Print :LTAppGroup' "$2/$3")"
  sed 's/\$(LT_APP_GROUP)/'"$group"'/' "$1"
}

cd "$ROOT"
xcodegen generate --quiet
mkdir -p "$OUT"

echo "== iOS"
build LiteTunnel-iOS 'generic/platform=iOS'
mkdir -p "$TMP/Payload"
cp -R "$DD/Build/Products/Release-iphoneos/LiteTunnel.app" "$TMP/Payload/"
APP="$TMP/Payload/LiteTunnel.app"
entitlements Config/PacketTunnel-iOS.entitlements "$APP" Info.plist >"$TMP/tunnel-ios.plist"
entitlements Config/LiteTunnel-iOS.entitlements "$APP" Info.plist >"$TMP/app-ios.plist"
# Inside out: the extension first, then the app that seals it.
codesign -f -s - --entitlements "$TMP/tunnel-ios.plist" "$APP/PlugIns/PacketTunnel.appex"
codesign -f -s - --entitlements "$TMP/app-ios.plist" "$APP"
IPA="$OUT/LiteTunnel-$VERSION.ipa"
rm -f "$IPA"
(cd "$TMP" && zip -qry "$IPA" Payload)

echo "== macOS"
build LiteTunnel-macOS 'generic/platform=macOS'
mkdir -p "$TMP/dmg"
cp -R "$DD/Build/Products/Release/LiteTunnel.app" "$TMP/dmg/"
APP="$TMP/dmg/LiteTunnel.app"
entitlements Config/PacketTunnel-macOS.entitlements "$APP" Contents/Info.plist >"$TMP/tunnel-mac.plist"
entitlements Config/LiteTunnel-macOS.entitlements "$APP" Contents/Info.plist >"$TMP/app-mac.plist"
# macOS refuses to launch an ad-hoc app that claims the Network Extension
# entitlement (no profile). Without it the app runs the core itself and
# connects through the system proxy instead of a VPN.
/usr/libexec/PlistBuddy -c 'Delete :com.apple.developer.networking.networkextension' "$TMP/app-mac.plist"
codesign -f -s - -o runtime --entitlements "$TMP/tunnel-mac.plist" "$APP/Contents/PlugIns/PacketTunnel.appex"
# The proxy helper runs outside the sandbox: no entitlements.
codesign -f -s - -o runtime "$APP/Contents/XPCServices/ProxyHelper.xpc"
codesign -f -s - -o runtime --entitlements "$TMP/app-mac.plist" "$APP"
ln -s /Applications "$TMP/dmg/Applications"
DMG="$OUT/LiteTunnel-$VERSION.dmg"
rm -f "$DMG"
# hdiutil can size an image from -srcfolder too small for the app ("no space
# left on device", seen on macOS 27.2), so give it the size, with room to spare.
SIZE=$(( $(du -sm "$TMP/dmg" | cut -f1) * 3 / 2 + 20 ))
hdiutil create -quiet -volname LiteTunnel -srcfolder "$TMP/dmg" -fs HFS+ -size "${SIZE}m" -format UDZO "$DMG"

ls -lh "$IPA" "$DMG"
