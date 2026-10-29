#!/usr/bin/env python3
"""Developer ID signing, notarization and verification for Mac distribution candidates.

The input is always a trial ZIP that already passed the native verification workflow; this tool
only re-signs and packages it, so the trial flow stays the single source of the app contents.

  sign      trial ZIP -> Developer ID signed .app (hardened runtime, secure timestamp, profile)
  notarize  signed .app -> notarized, stapled ZIP + .sha256   (or a clearly named rehearsal ZIP)
  verify    candidate ZIP -> checks Gatekeeper would accept it, writes a JSON report

Credentials are never read from files in the repository:
  MAC_SIGN_IDENTITY        identity SHA-1 or "Developer ID Application: … (TEAMID)" (required)
  MAC_SIGN_KEYCHAIN        keychain that holds the identity (CI temporary keychain; omit locally)
  MAC_TEAM_ID              10-character Apple Team ID
  MAC_PROVISIONING_PROFILE path to the Developer ID Mac Catalyst profile (outside the repository)
  NOTARY_KEYCHAIN_PROFILE  local: name saved with `xcrun notarytool store-credentials`
  APPLE_NOTARY_KEY_PATH, APPLE_NOTARY_KEY_ID, APPLE_NOTARY_ISSUER_ID   CI: App Store Connect API key
  MAC_SIGN_REHEARSAL=1     self-signed rehearsal: no profile, no notarization, never distributable
"""
import argparse, datetime, fnmatch, hashlib, json, os, plistlib, re, shutil, subprocess, sys, tempfile, time
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
BUNDLE_ID = 'kr.school.kyomutalk.mac'
ENTITLEMENTS = ROOT / 'src/SchoolMessenger.Mac/Platforms/MacCatalyst/Entitlements.Distribution.plist'
NESTED = ('.framework', '.xpc', '.app', '.bundle', '.appex')
SPARKLE = 'Contents/Frameworks/Sparkle.framework'
UPDATER = 'Contents/PlugIns/GyomutalkUpdater.bundle'


def run(*args, capture=False, check=True):
    result = subprocess.run([str(a) for a in args], text=True, capture_output=capture, cwd=ROOT)
    if check and result.returncode:
        raise SystemExit(f'{args[0]} failed ({result.returncode}): {(result.stderr or "").strip()}')
    return result


def rehearsal():
    return os.environ.get('MAC_SIGN_REHEARSAL') == '1'


def version():
    return ET.parse(ROOT / 'Directory.Build.props').findtext('./PropertyGroup/Version')


def team_id():
    team = os.environ.get('MAC_TEAM_ID', '')
    if not re.fullmatch(r'[A-Z0-9]{10}', team):
        raise SystemExit('MAC_TEAM_ID must be the 10-character Apple Team ID.')
    return team


def keychain_args():
    keychain = os.environ.get('MAC_SIGN_KEYCHAIN')
    return ['--keychain', keychain] if keychain else []


def identity():
    value = os.environ.get('MAC_SIGN_IDENTITY', '')
    if not value or value == '-':
        raise SystemExit('Set MAC_SIGN_IDENTITY; ad-hoc signing belongs to the trial build, not a distribution candidate.')
    return value


def identity_sha1(value):
    """Resolve the signing identity to its certificate SHA-1 so the profile check compares exact certificates."""
    listing = run('security', 'find-identity', '-p', 'codesigning', *([os.environ['MAC_SIGN_KEYCHAIN']] if os.environ.get('MAC_SIGN_KEYCHAIN') else []), capture=True).stdout
    matches = {m.group(1) for m in re.finditer(r'\)\s+([0-9A-F]{40})\s+"([^"]+)"', listing) if value in (m.group(1), m.group(2))}
    if len(matches) != 1:
        raise SystemExit(f'Signing identity must match exactly one certificate in the keychain (found {len(matches)}).')
    return matches.pop()


def unzip(archive, folder):
    run('ditto', '-x', '-k', archive, folder)
    apps = sorted(Path(folder).glob('*.app'))
    if len(apps) != 1:
        raise SystemExit(f'{archive}: expected exactly one .app')
    return apps[0]


def macho(path):
    with open(path, 'rb') as handle:
        magic = handle.read(4)
    return magic in (b'\xcf\xfa\xed\xfe', b'\xce\xfa\xed\xfe', b'\xca\xfe\xba\xbe', b'\xbe\xba\xfe\xca')


def signing_order(bundle):
    """Inside-out order for nested code that is not Sparkle or the updater plug-in."""
    info = plistlib.loads((bundle / 'Contents/Info.plist').read_bytes())
    main = bundle / 'Contents/MacOS' / info['CFBundleExecutable']
    special = (bundle / SPARKLE, bundle / UPDATER)
    nested = [p for p in (bundle / 'Contents').rglob('*') if p.is_dir() and p.suffix in NESTED and not any(p == s or s in p.parents for s in special)]
    containers = [p for p in nested] + list(special)
    loose = [p for p in (bundle / 'Contents').rglob('*') if p.is_file() and not p.is_symlink() and p != main and not any(c in p.parents for c in containers) and macho(p)]
    return sorted(loose, key=lambda p: -len(p.parts)) + sorted(nested, key=lambda p: -len(p.parts))


def entitlements_for(target):
    data = plistlib.loads(ENTITLEMENTS.read_bytes())
    if rehearsal():
        # A self-signed rehearsal certificate has no Team ID and no profile, so these cannot be validated.
        data.pop('keychain-access-groups', None)
        data['com.apple.security.cs.disable-library-validation'] = True
    else:
        prefix = team_id() + '.'
        data['keychain-access-groups'] = [g.replace('$(AppIdentifierPrefix)', prefix) for g in data['keychain-access-groups']]
    target.write_bytes(plistlib.dumps(data))
    return data


def check_profile(profile, entitlements, certificate_sha1):
    decoded = plistlib.loads(run('security', 'cms', '-D', '-i', profile, capture=True).stdout.encode())
    team = team_id()
    granted = decoded.get('Entitlements', {})
    problems = []
    if team not in decoded.get('TeamIdentifier', []):
        problems.append('profile Team ID differs from MAC_TEAM_ID')
    if granted.get('com.apple.application-identifier') != f'{team}.{BUNDLE_ID}':
        problems.append(f'profile App ID is {granted.get("com.apple.application-identifier")}, expected {team}.{BUNDLE_ID}')
    if not decoded.get('ProvisionsAllDevices'):
        problems.append('profile is not a Developer ID profile (ProvisionsAllDevices missing)')
    if 'MacCatalyst' not in decoded.get('Platform', []) and 'OSX' not in decoded.get('Platform', []):
        problems.append('profile platform is not Mac Catalyst')
    expires = decoded['ExpirationDate'].replace(tzinfo=datetime.timezone.utc) if decoded['ExpirationDate'].tzinfo is None else decoded['ExpirationDate']
    if expires < datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=7):
        problems.append(f'profile expires {expires:%Y-%m-%d}; renew it first')
    if certificate_sha1 not in {hashlib.sha1(c).hexdigest().upper() for c in decoded.get('DeveloperCertificates', [])}:
        problems.append('signing certificate is not included in the profile')
    for group in entitlements.get('keychain-access-groups', []):
        if not any(fnmatch.fnmatchcase(group, allowed) for allowed in granted.get('keychain-access-groups', [])):
            problems.append(f'profile does not allow keychain group {group}')
    if problems:
        raise SystemExit('Provisioning profile rejected: ' + '; '.join(problems))
    return expires


def sign(args):
    who = identity()
    work = Path(tempfile.mkdtemp(prefix='gyomutalk-sign-'))
    try:
        bundle = unzip(args.trial, work)
        run(sys.executable, ROOT / 'tests/mac-bundle.py', bundle, args.architecture)  # trial must still be intact
        entitlements_file = Path(args.output) / f'entitlements-{args.architecture}.plist'
        Path(args.output).mkdir(parents=True, exist_ok=True)
        entitlements = entitlements_for(entitlements_file)
        if not rehearsal():
            profile = os.environ.get('MAC_PROVISIONING_PROFILE')
            if not profile or Path(profile).resolve().is_relative_to(ROOT):
                raise SystemExit('MAC_PROVISIONING_PROFILE must point to a profile stored outside the repository.')
            check_profile(profile, entitlements, identity_sha1(who))
            shutil.copyfile(profile, bundle / 'Contents/embedded.provisionprofile')
        stamp = ['--timestamp=none'] if rehearsal() else ['--timestamp']
        base = ['codesign', '--force', '--options', 'runtime', *stamp, '--sign', who, *keychain_args()]
        for item in signing_order(bundle):
            run(*base, item)
        sparkle = bundle / SPARKLE / 'Versions/B'
        # Sparkle's documented Developer ID order; the downloader keeps its own network entitlement.
        run(*base, sparkle / 'XPCServices/Installer.xpc')
        run(*base, '--preserve-metadata=entitlements', sparkle / 'XPCServices/Downloader.xpc')
        run(*base, sparkle / 'Autoupdate')
        run(*base, sparkle / 'Updater.app')
        run(*base, bundle / SPARKLE)
        run(*base, bundle / UPDATER)
        run(*base, '--entitlements', entitlements_file, bundle)
        run('codesign', '--verify', '--deep', '--strict', '--verbose=2', bundle)
        target = Path(args.output) / args.architecture / bundle.name
        if target.exists():
            shutil.rmtree(target)
        target.parent.mkdir(parents=True, exist_ok=True)
        run('ditto', bundle, target)
        print(f'Signed {target} ({"rehearsal, self-signed" if rehearsal() else "Developer ID"})')
    finally:
        shutil.rmtree(work, ignore_errors=True)


def notary_auth():
    profile = os.environ.get('NOTARY_KEYCHAIN_PROFILE')
    if profile:
        keychain = os.environ.get('NOTARY_KEYCHAIN')
        return ['--keychain-profile', profile, *(['--keychain', keychain] if keychain else [])]
    key, key_id, issuer = (os.environ.get(n) for n in ('APPLE_NOTARY_KEY_PATH', 'APPLE_NOTARY_KEY_ID', 'APPLE_NOTARY_ISSUER_ID'))
    if key and key_id and issuer:
        return ['--key', key, '--key-id', key_id, '--issuer', issuer]
    raise SystemExit('Set NOTARY_KEYCHAIN_PROFILE (local Mac) or APPLE_NOTARY_KEY_PATH/APPLE_NOTARY_KEY_ID/APPLE_NOTARY_ISSUER_ID (CI).')


def package(app, archive):
    if archive.exists():
        archive.unlink()
    run('ditto', '-c', '-k', '--keepParent', app, archive)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    Path(str(archive) + '.sha256').write_text(f'{digest}  {archive.name}\n')
    return digest


def notarize(args):
    app = Path(args.app)
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    name = f'gyomutalk-mac-{version()}-macos-{args.architecture}'
    if rehearsal():
        package(app, output / f'{name}-rehearsal-not-for-distribution.zip')
        print('Rehearsal package created without notarization.')
        return
    auth = notary_auth()
    with tempfile.TemporaryDirectory(prefix='gyomutalk-notary-') as work:
        upload = Path(work) / f'{name}-notary-upload.zip'
        run('ditto', '-c', '-k', '--keepParent', app, upload)
        submitted = run('xcrun', 'notarytool', 'submit', upload, *auth, '--wait', '--timeout', '45m', '--output-format', 'json', capture=True, check=False)
    try:
        result = json.loads(submitted.stdout or '{}')
    except json.JSONDecodeError:
        result = {}
    submission = result.get('id')
    if submission:
        run('xcrun', 'notarytool', 'log', submission, *auth, output / f'{name}-notary-log.json', check=False)
    if result.get('status') != 'Accepted':
        raise SystemExit(f'Notarization was not accepted: {result.get("status") or submitted.stderr.strip()} (see {name}-notary-log.json)')
    run('xcrun', 'stapler', 'staple', app)
    run('xcrun', 'stapler', 'validate', app)
    digest = package(app, output / f'{name}.zip')
    print(f'Notarized and stapled {name}.zip sha256={digest}')


def verify(args):
    checks = []
    def check(ok, description):
        if not ok:
            raise SystemExit('FAILED ' + description)
        checks.append(description)
        print('PASS ' + description)
    with tempfile.TemporaryDirectory(prefix='gyomutalk-verify-') as work:
        bundle = unzip(args.archive, work)
        run(sys.executable, ROOT / 'tests/mac-bundle.py', bundle, args.architecture)
        check(True, 'bundle identity, version, architecture, HTTPS policy and sandbox match the verified trial')
        details = run('codesign', '-d', '--verbose=4', bundle, capture=True).stderr
        entitlements = plistlib.loads(run('codesign', '-d', '--entitlements', ':-', bundle, capture=True).stdout.encode() or plistlib.dumps({}))
        flags = re.search(r'flags=\S+\(([^)]*)\)', details)
        check(flags is not None and 'runtime' in flags.group(1), 'hardened runtime is enabled on the app')
        check(entitlements.get('com.apple.security.cs.allow-jit') is True, '.NET JIT entitlement is present for the hardened runtime')
        check(not entitlements.get('com.apple.security.get-task-allow'), 'debugging entitlement is absent')
        sparkle = bundle / SPARKLE / 'Versions/B'
        nested = signing_order(bundle) + [sparkle / 'XPCServices/Installer.xpc', sparkle / 'XPCServices/Downloader.xpc', sparkle / 'Autoupdate', sparkle / 'Updater.app', bundle / SPARKLE, bundle / UPDATER]
        for item in nested:
            nested_flags = re.search(r'flags=\S+\(([^)]*)\)', run('codesign', '-d', '--verbose=2', item, capture=True).stderr)
            check(nested_flags is not None and 'runtime' in nested_flags.group(1), f'hardened runtime on {item.relative_to(bundle)}')
        if rehearsal():
            check(entitlements.get('com.apple.security.cs.disable-library-validation') is True and 'keychain-access-groups' not in entitlements, 'rehearsal entitlements are marked and cannot pass as a distribution build')
        else:
            team = team_id()
            check(f'TeamIdentifier={team}' in details, f'signed by Team ID {team}')
            check('Authority=Developer ID Application:' in details, 'Developer ID Application certificate chain')
            check(re.search(r'^Timestamp=', details, re.M) is not None, 'secure timestamp from Apple')
            check(not entitlements.get('com.apple.security.cs.disable-library-validation'), 'library validation is enforced')
            check(entitlements.get('keychain-access-groups') == [f'{team}.{BUNDLE_ID}'], 'Keychain group uses the Team ID prefix')
            check((bundle / 'Contents/embedded.provisionprofile').exists(), 'Developer ID provisioning profile is embedded')
            run('xcrun', 'stapler', 'validate', bundle)
            check(True, 'notarization ticket is stapled')
            assessment = run('spctl', '--assess', '--type', 'execute', '--verbose=4', bundle, capture=True, check=False)
            check(assessment.returncode == 0 and 'source=Notarized Developer ID' in assessment.stderr, 'Gatekeeper accepts the app as Notarized Developer ID')
        native = 'arm64' if os.uname().machine == 'arm64' else 'x64'
        if args.launch and args.architecture == native:
            info = plistlib.loads((bundle / 'Contents/Info.plist').read_bytes())
            process = subprocess.Popen([bundle / 'Contents/MacOS' / info['CFBundleExecutable']], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            time.sleep(20)
            alive = process.poll() is None
            if alive:
                process.terminate()
                process.wait(timeout=10)
            check(alive, 'signed app starts and keeps running for 20 seconds under the hardened runtime')
    report = Path(args.report) if args.report else None
    if report:
        report.write_text(json.dumps({'archive': Path(args.archive).name, 'architecture': args.architecture, 'rehearsal': rehearsal(), 'checks': checks}, ensure_ascii=False, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    s = sub.add_parser('sign'); s.add_argument('trial'); s.add_argument('architecture', choices=['arm64', 'x64']); s.add_argument('output'); s.set_defaults(func=sign)
    n = sub.add_parser('notarize'); n.add_argument('app'); n.add_argument('architecture', choices=['arm64', 'x64']); n.add_argument('output'); n.set_defaults(func=notarize)
    v = sub.add_parser('verify'); v.add_argument('archive'); v.add_argument('architecture', choices=['arm64', 'x64']); v.add_argument('--report'); v.add_argument('--launch', action='store_true'); v.set_defaults(func=verify)
    if sys.platform != 'darwin':
        raise SystemExit('Mac distribution signing requires macOS and Xcode.')
    args = parser.parse_args()
    args.func(args)


if __name__ == '__main__':
    main()
