#!/usr/bin/env bash
# Rehearsal only: a one-day self-signed code-signing certificate that exercises the same
# keychain, signing and verification path without any Apple credentials. Never distributable.
set -euo pipefail
folder="$1"
mkdir -p "$folder"; chmod 700 "$folder"
# LibreSSL in /usr/bin writes PKCS#12 files that `security import` accepts without legacy flags.
/usr/bin/openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=Gyomutalk Rehearsal Signing (not for distribution)" \
  -addext "keyUsage=critical,digitalSignature" -addext "extendedKeyUsage=critical,codeSigning" -addext "basicConstraints=critical,CA:false" \
  -keyout "$folder/rehearsal.key" -out "$folder/rehearsal.cer" 2>/dev/null
/usr/bin/openssl pkcs12 -export -inkey "$folder/rehearsal.key" -in "$folder/rehearsal.cer" -out "$folder/rehearsal.p12" -passout env:MAC_P12_PASSWORD
rm -f "$folder/rehearsal.key"
