#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
bundle="$1"
architecture="$2"
[[ "$architecture" == arm64 || "$architecture" == x86_64 ]] || exit 2
folder=.tools/update-frameworks/sparkle-2.10.1
archive=.tools/update-frameworks/Sparkle-2.10.1.tar.xz
mkdir -p "$folder"
if [[ ! -f "$archive" ]]; then curl --fail --location --retry 3 'https://github.com/sparkle-project/Sparkle/releases/download/2.10.1/Sparkle-2.10.1.tar.xz' --output "$archive"; fi
echo "1a856d2a9edb8e628de88121d7be04cf0e2167f8641650b78fca00b14244cf27  $archive" | shasum -a 256 -c -
if [[ ! -d "$folder/Sparkle.framework" ]]; then tar -xf "$archive" -C "$folder"; fi
plugin="$bundle/Contents/PlugIns/GyomutalkUpdater.bundle"
mkdir -p "$plugin/Contents/MacOS" "$bundle/Contents/Frameworks"
ditto "$folder/Sparkle.framework" "$bundle/Contents/Frameworks/Sparkle.framework"
xcrun --sdk macosx clang -fobjc-arc -bundle -arch "$architecture" -mmacosx-version-min=15.0 \
  -F "$folder" -framework Cocoa -framework Sparkle \
  -Wl,-rpath,@loader_path/../../../../Frameworks \
  src/SchoolMessenger.Mac/UpdaterPlugin/Updater.m -o "$plugin/Contents/MacOS/GyomutalkUpdater"
python3 - "$bundle" "$plugin" "$architecture" <<'PY'
import json,plistlib,sys,xml.etree.ElementTree as ET
from pathlib import Path
bundle,plugin=map(Path,sys.argv[1:3]);publisher=Path('config/update-publisher-mac.json');config=json.loads((publisher if publisher.exists() else Path('config/update-publisher.json')).read_text());architecture='arm64' if sys.argv[3]=='arm64' else 'x64'
assert config['product']=='mac' and config['repository']=='yd043105-pixel/gyomutalk-mac'
version=ET.parse('Directory.Build.props').findtext('./PropertyGroup/Version');major,minor,patch=map(int,version.split('.'))
assert minor<1000 and patch<1000
info=plistlib.loads((bundle/'Contents/Info.plist').read_bytes())
info.update(SUFeedURL='https://github.com/'+config['repository']+'/releases/latest/download/appcast-'+architecture+'.xml',SUPublicEDKey=config['publicKey'],SUEnableAutomaticChecks=True,SUScheduledCheckInterval=86400,SUEnableInstallerLauncherService=True,SUShowReleaseNotes=False,CFBundleVersion=str(major*1000000+minor*1000+patch))
(bundle/'Contents/Info.plist').write_bytes(plistlib.dumps(info))
(plugin/'Contents/Info.plist').write_bytes(plistlib.dumps(dict(CFBundleIdentifier='kr.school.kyomutalk.mac.updater',CFBundleName='GyomutalkUpdater',CFBundleExecutable='GyomutalkUpdater',CFBundlePackageType='BNDL',NSPrincipalClass='GYUpdaterPlugin')))
PY
# Re-sign every nested helper inside-out; retain sandbox entitlements on the host.
identity="${MAC_CODE_SIGN_IDENTITY:--}"
framework="$bundle/Contents/Frameworks/Sparkle.framework/Versions/B"
codesign -f -s "$identity" -o runtime "$framework/XPCServices/Installer.xpc"
codesign -f -s "$identity" -o runtime --preserve-metadata=entitlements "$framework/XPCServices/Downloader.xpc"
codesign -f -s "$identity" -o runtime "$framework/Autoupdate"
codesign -f -s "$identity" -o runtime "$framework/Updater.app"
codesign -f -s "$identity" -o runtime "$bundle/Contents/Frameworks/Sparkle.framework"
codesign -f -s "$identity" "$plugin"
codesign -f -s "$identity" --entitlements src/SchoolMessenger.Mac/Platforms/MacCatalyst/Entitlements.plist "$bundle"
codesign --verify --deep --strict "$bundle"
