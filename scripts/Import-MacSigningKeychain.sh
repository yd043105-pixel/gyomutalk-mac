#!/usr/bin/env bash
# CI only: import a .p12 into a throwaway keychain under $RUNNER_TEMP.
# Local Macs keep the Developer ID identity in the login keychain and never run this script.
# Input:  $1 = .p12 path, MAC_P12_PASSWORD in the environment.
# Output: MAC_SIGN_KEYCHAIN and MAC_SIGN_IDENTITY (certificate SHA-1) appended to $GITHUB_ENV.
set -euo pipefail
: "${RUNNER_TEMP:?This script only runs on a CI runner}"
: "${GITHUB_ENV:?This script only runs in GitHub Actions}"
: "${MAC_P12_PASSWORD:?MAC_P12_PASSWORD is required}"
p12="$1"
keychain="$RUNNER_TEMP/gyomutalk-signing.keychain-db"
password="$(openssl rand -hex 32)"
security create-keychain -p "$password" "$keychain"
security set-keychain-settings -lut 3600 "$keychain"
security unlock-keychain -p "$password" "$keychain"
security import "$p12" -k "$keychain" -f pkcs12 -P "$MAC_P12_PASSWORD" -T /usr/bin/codesign -T /usr/bin/security >/dev/null
rm -f "$p12"
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$password" "$keychain" >/dev/null
# Search the temporary keychain first; keep the runner's existing keychains for Apple's intermediates.
existing="$(security list-keychains -d user | sed -e 's/^ *"//' -e 's/"$//')"
# shellcheck disable=SC2086
security list-keychains -d user -s "$keychain" $existing
identities="$(security find-identity -p codesigning "$keychain" | grep -E '^ +[0-9]+\) [0-9A-F]{40} ' || true)"
# find-identity prints "matching" and "valid" sections; count distinct certificates.
[[ "$(printf '%s\n' "$identities" | awk '{print $2}' | sort -u | grep -c .)" == 1 ]] || { echo "Expected exactly one signing identity in the imported .p12" >&2; exit 1; }
sha1="$(printf '%s\n' "$identities" | awk 'NR==1{print $2}')"
name="$(printf '%s\n' "$identities" | head -n 1 | sed -E 's/^[^"]*"(.*)"[^"]*$/\1/')"
if [[ "${MAC_SIGN_REHEARSAL:-0}" != 1 ]]; then
  [[ "$name" == "Developer ID Application: "*"(${MAC_TEAM_ID:?MAC_TEAM_ID is required})" ]] || { echo "Imported identity is not a Developer ID Application certificate for team $MAC_TEAM_ID: $name" >&2; exit 1; }
fi
{ echo "MAC_SIGN_KEYCHAIN=$keychain"; echo "MAC_SIGN_IDENTITY=$sha1"; } >> "$GITHUB_ENV"
echo "Imported signing identity: $name ($sha1)"
