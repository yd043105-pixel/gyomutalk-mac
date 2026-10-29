"""Inspect actual compiled Mac bundle; never infer signing/notarization from source."""
import plistlib, subprocess, sys
from pathlib import Path
bundle, architecture = Path(sys.argv[1]), sys.argv[2]
info = plistlib.loads((bundle / 'Contents/Info.plist').read_bytes())
assert info['CFBundleIdentifier'] == 'kr.school.kyomutalk.mac'
assert info['CFBundleShortVersionString'] == '1.0.0'
assert info['CFBundleDisplayName'] == '교무톡'
assert info['UIDeviceFamily'] == [2], 'Use the standard MAUI Catalyst control idiom'
assert info.get('NSLocalNetworkUsageDescription')
ats = info.get('NSAppTransportSecurity', {})
assert not ats.get('NSAllowsArbitraryLoads', False)
assert 'localhost' not in ats.get('NSExceptionDomains', {})
assert (bundle / 'Contents/Resources/PrivacyInfo.xcprivacy').exists()
executable = bundle / 'Contents/MacOS' / info['CFBundleExecutable']
arch = subprocess.check_output(['/usr/bin/lipo', '-archs', str(executable)], text=True).strip()
assert arch == ('x86_64' if architecture == 'x64' else 'arm64'), arch
subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(bundle)], check=True)
entitlements = subprocess.check_output(['/usr/bin/codesign', '-d', '--entitlements', ':-', str(bundle)], stderr=subprocess.DEVNULL)
entitlements = plistlib.loads(entitlements)
assert entitlements['com.apple.security.app-sandbox']
assert entitlements['com.apple.security.network.client']
assert entitlements['com.apple.security.files.user-selected.read-write']
assert not entitlements.get('com.apple.security.get-task-allow', False)
assert list(bundle.rglob('LICENSE')), 'Bundled MIT license missing'
print('PASS actual Mac bundle architecture, version, HTTPS policy, privacy manifest, sandbox, file-picker entitlement and ad-hoc signature integrity')
