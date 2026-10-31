#!/bin/bash
# Builds dist/LiteTunnel-<version>-<team>.ipa signed with a ready-made
# certificate and ad hoc profile (a bought per-device certificate, or your own).
#
# usage: scripts/sign-ios.sh <profile.mobileprovision> [--install]
#
# Import the .p12 into the login keychain first (double-click it); the identity
# is the one whose certificate the profile lists. The profile must allow
# packet-tunnel-provider and have an App Group: the app is built with the
# profile's bundle ID and its first group, which the app and the tunnel share.
# --install puts the app on a connected iPhone that the profile lists.
set -euo pipefail

[ $# -ge 1 ] || { sed -n '5p' "$0"; exit 2; }
PROFILE="$1"
INSTALL="${2:-}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DD="${LT_DERIVED_DATA:-$ROOT/.build-dd}"
OUT="$ROOT/dist"
VERSION="$(sed -n 's/^ *MARKETING_VERSION: *"\(.*\)"/\1/p' "$ROOT/project.yml")"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"

die() { echo "ОШИБКА: $*" >&2; exit 1; }
pl() { /usr/libexec/PlistBuddy -c "Print :$1" "$TMP/profile.plist"; }

# --- what the profile allows ---

security cms -D -i "$PROFILE" >"$TMP/profile.plist" 2>/dev/null || die "не читается профиль $PROFILE"
TEAM="$(pl TeamIdentifier:0)"
APP_ID="$(pl Entitlements:application-identifier)"
BUNDLE_ID="${APP_ID#"$TEAM".}"
case "$BUNDLE_ID" in *'*'*) die "профиль с маской ($APP_ID): в таком VPN не разрешён" ;; esac
pl Entitlements:com.apple.developer.networking.networkextension | grep -q packet-tunnel-provider ||
  die "профиль не разрешает packet-tunnel-provider"
GROUP="$(pl Entitlements:com.apple.security.application-groups:0 2>/dev/null)" || die "в профиле нет App Group"
GET_TASK_ALLOW="$(pl Entitlements:get-task-allow)"

plutil -extract DeveloperCertificates.0 raw -o - "$TMP/profile.plist" | base64 -D >"$TMP/cert.der"
SHA1="$(openssl x509 -inform der -in "$TMP/cert.der" -noout -fingerprint -sha1 | sed 's/.*=//; s/://g')"
security find-identity -v -p codesigning | grep -q "$SHA1" ||
  die "сертификата из профиля нет в связке ключей: дважды щёлкните по .p12 и введите его пароль"

echo "Профиль: $(pl Name), команда $TEAM, до $(pl ExpirationDate)"
echo "ID: $BUNDLE_ID, группа: $GROUP"

DEVICE=""
if [ "$INSTALL" = --install ]; then
  devices="$(xcrun devicectl list devices 2>/dev/null)"
  for udid in $(pl ProvisionedDevices | sed '1d;$d'); do
    if echo "$devices" | grep "$udid" | grep -qE "available|connected"; then DEVICE="$udid"; break; fi
  done
  [ -n "$DEVICE" ] || die "не подключён ни один iPhone из профиля (подключите кабелем и разблокируйте)"
fi

# --- build ---

cd "$ROOT"
xcodegen generate --quiet
xcodebuild -project LiteTunnel.xcodeproj -scheme LiteTunnel-iOS -configuration Release \
  -destination 'generic/platform=iOS' -derivedDataPath "$DD" CODE_SIGNING_ALLOWED=NO \
  LT_BUNDLE_PREFIX="$BUNDLE_ID" LT_APP_GROUP="$GROUP" build -quiet

mkdir -p "$TMP/Payload"
cp -R "$DD/Build/Products/Release-iphoneos/LiteTunnel.app" "$TMP/Payload/"
APP="$TMP/Payload/LiteTunnel.app"
TUNNEL="$APP/PlugIns/PacketTunnel.appex"
[ "$(/usr/libexec/PlistBuddy -c 'Print :LTAppGroup' "$APP/Info.plist")" = "$GROUP" ] || die "группа не попала в Info.plist"

# --- sign ---

/usr/libexec/PlistBuddy \
  -c "Add :application-identifier string $APP_ID" \
  -c "Add :com.apple.developer.team-identifier string $TEAM" \
  -c "Add :get-task-allow bool $GET_TASK_ALLOW" \
  -c "Add :com.apple.developer.networking.networkextension array" \
  -c "Add :com.apple.developer.networking.networkextension:0 string packet-tunnel-provider" \
  -c "Add :com.apple.security.application-groups array" \
  -c "Add :com.apple.security.application-groups:0 string $GROUP" \
  "$TMP/entitlements.plist" >/dev/null

# The profile covers one App ID, so the tunnel is signed with it too, as
# re-signing tools do. Inside out: the extension first, then the app.
for bundle in "$TUNNEL" "$APP"; do
  cp "$PROFILE" "$bundle/embedded.mobileprovision"
  codesign -f -s "$SHA1" --timestamp=none --generate-entitlement-der \
    --entitlements "$TMP/entitlements.plist" "$bundle"
done
codesign --verify --deep --strict "$APP"

mkdir -p "$OUT"
IPA="$OUT/LiteTunnel-$VERSION-$TEAM.ipa"
rm -f "$IPA"
(cd "$TMP" && zip -qry "$IPA" Payload)
ls -lh "$IPA"

if [ -n "$DEVICE" ]; then
  echo "== Установка на $DEVICE"
  xcrun devicectl device install app --device "$DEVICE" "$APP"
fi
