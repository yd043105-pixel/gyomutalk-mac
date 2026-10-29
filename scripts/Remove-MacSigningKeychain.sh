#!/usr/bin/env bash
# CI only: delete the temporary signing keychain and every decoded credential file. Safe to run twice.
set -uo pipefail
: "${RUNNER_TEMP:?This script only runs on a CI runner}"
keychain="$RUNNER_TEMP/gyomutalk-signing.keychain-db"
if [[ -f "$keychain" ]]; then
  security delete-keychain "$keychain" || true
fi
security list-keychains -d user -s "$HOME/Library/Keychains/login.keychain-db" || true
rm -rf "$RUNNER_TEMP/gyomutalk-secrets"
echo "Temporary signing keychain and credential files removed."
