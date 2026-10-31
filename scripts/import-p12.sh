#!/bin/bash
# Imports a .p12 into the login keychain when Keychain Access refuses it.
# Files exported by OpenSSL 3+ use AES-256 and a SHA-256 MAC, which the macOS
# importer rejects as "wrong password?" even when it is right. This re-packs the
# certificate and key in the old format (3DES, SHA-1) under a throwaway
# password, imports that, and lets codesign use the key without asking.
#
# usage: scripts/import-p12.sh <file.p12>    (asks for the .p12 password)
set -euo pipefail

[ $# -eq 1 ] || { sed -n '8p' "$0"; exit 2; }
P12="$1"
# The openssl in macOS (LibreSSL) cannot read these files either.
OPENSSL="$(command -v /opt/homebrew/bin/openssl /usr/local/bin/openssl | head -1 || true)"
[ -n "$OPENSSL" ] || { echo "Нужен OpenSSL 3+: brew install openssl" >&2; exit 1; }

umask 077
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
export TEMP_PASS="$("$OPENSSL" rand -hex 16)"

echo "Введите пароль от $(basename "$P12") (символы не отображаются):"
# openssl asks on the terminal itself, with echo off.
"$OPENSSL" pkcs12 -in "$P12" -nodes -out "$TMP/all.pem" 2>"$TMP/err" || {
  grep -qi "mac verify" "$TMP/err" && echo "Пароль не подходит, попробуйте ещё раз." >&2 || cat "$TMP/err" >&2
  exit 1
}
"$OPENSSL" pkcs12 -export -in "$TMP/all.pem" -out "$TMP/legacy.p12" \
  -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1 -passout env:TEMP_PASS
security import "$TMP/legacy.p12" -k "$HOME/Library/Keychains/login.keychain-db" \
  -P "$TEMP_PASS" -T /usr/bin/codesign
security find-identity -v -p codesigning
