#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
[[ "$(uname -s)" == Darwin ]] || { echo 'Mac builds require macOS and Xcode.' >&2; exit 1; }
runtime="${1:-maccatalyst-arm64}"
[[ "$runtime" == maccatalyst-arm64 || "$runtime" == maccatalyst-x64 ]] || exit 2
project=src/SchoolMessenger.Mac/SchoolMessenger.Mac.csproj
mkdir -p artifacts/mac
dotnet publish "$project" -c Release -r "$runtime" --no-restore --nologo \
  -p:CreatePackage=false -p:CodesignKey=- -p:CodesignRequireProvisioningProfile=false
bundle="$(find "src/SchoolMessenger.Mac/bin/Release/net10.0-maccatalyst/$runtime" -maxdepth 1 -type d -name '*.app' -print -quit)"
[[ -d "$bundle" ]] || { echo 'Mac application bundle missing' >&2; exit 1; }
arch="${runtime#maccatalyst-}"
[[ "$arch" != x64 ]] || arch=x86_64
bash scripts/Embed-MacUpdater.sh "$bundle" "$arch"
python3 tests/mac-bundle.py "$bundle" "${runtime#maccatalyst-}"
version="$(python3 -c 'import xml.etree.ElementTree as E;print(E.parse("Directory.Build.props").findtext("./PropertyGroup/Version"))')"
archive="artifacts/mac/gyomutalk-mac-$version-macos-${runtime#maccatalyst-}.zip"
/usr/bin/ditto -c -k --keepParent "$bundle" "$archive"
shasum -a 256 "$archive" > "$archive.sha256"
echo "Built local/ad-hoc trial: $archive (not Developer ID signed or notarized)"
