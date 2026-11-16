"""Stage a locally built test archive on macOS without touching installed apps."""
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import plistlib
import re
import shlex
import stat
import subprocess
import sys
import zipfile


def require_private_owned(path):
    info = path.lstat()
    if (not hasattr(os, 'geteuid') or not stat.S_ISDIR(info.st_mode) or
            info.st_uid != os.geteuid() or stat.S_IMODE(info.st_mode) != 0o700):
        raise PermissionError('Test directory must be owned by the current user and private.')


def discovery_target(root, label):
    if not re.fullmatch(r'nian-link-test-[0-9a-f]{32}', label):
        raise ValueError('Unexpected discovery test label.')
    if any(p.is_symlink() for p in (root, *root.parents)) or not root.is_dir():
        raise ValueError('Linked or missing test parent refused.')
    return root / label


def prepare_discovery(root, label):
    target = discovery_target(root, label)
    target.mkdir(mode=0o700, exist_ok=False)
    require_private_owned(target)
    return target


def create_discovery_profile(root, label):
    target = discovery_target(root, label)
    require_private_owned(target)
    profile = target / 'profile'
    profile.mkdir(mode=0o700, exist_ok=False)
    require_private_owned(profile)
    return profile


def create_discovery_launcher(root, desktop, label, script_source, fileless=False, blue=False):
    """Install a separate Finder launcher without replacing any existing files."""
    if blue and not fileless:
        raise ValueError('Blue launcher requires the fileless test.')
    target = discovery_target(root, label)
    require_private_owned(target)
    require_private_owned(target / 'profile')
    bundle = target / 'NianDeskLink.app'
    if bundle.is_symlink() or not bundle.is_dir() or desktop.is_symlink() or not desktop.is_dir():
        raise ValueError('Discovery test bundle or desktop unavailable.')
    if script_source.is_symlink() or not script_source.is_file():
        raise ValueError('Discovery launcher helper unavailable.')
    helper = target / ('launch-fileless-helper.py' if fileless else 'launch-discovery-helper.py')
    launcher_name = ('启动年年互联清透蓝测试-' + label[-32:-24] + '.command' if blue else
                     '启动年年互联免文件测试.command' if fileless else '启动年年互联发现测试.command')
    launcher = desktop / launcher_name
    if helper.exists() or helper.is_symlink() or launcher.exists() or launcher.is_symlink():
        raise FileExistsError('Discovery launcher or helper already exists.')
    helper_bytes = script_source.read_bytes()
    if not helper_bytes or len(helper_bytes) > 1024 * 1024:
        raise ValueError('Invalid discovery launcher helper.')
    action = 'launch-blue-fileless' if blue else 'launch-fileless' if fileless else 'launch-discovery'
    command = '#!/bin/sh\nexec /usr/bin/python3 ' + shlex.quote(str(helper)) + ' ' + label + ' ' + action + '\n'
    created = []
    try:
        for path, mode, data in ((helper, 0o600, helper_bytes), (launcher, 0o700, command.encode('utf-8'))):
            fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, mode)
            try:
                info = os.fstat(fd)
                created.append((path, info.st_dev, info.st_ino))
                output = os.fdopen(fd, 'wb')
            except BaseException:
                os.close(fd)
                raise
            with output:
                output.write(data)
    except BaseException:
        for path, device, inode in reversed(created):
            try:
                current = path.lstat()
                if stat.S_ISREG(current.st_mode) and (current.st_dev, current.st_ino) == (device, inode):
                    path.unlink()
            except FileNotFoundError:
                pass
        raise
    return {'launcher': str(launcher), 'helper': str(helper)}


def require_discovery_bridge(bundle, pin):
    if not isinstance(pin, str) or not re.fullmatch(r'[0-9a-f]{64}', pin):
        raise ValueError('Missing or invalid signed discovery bridge hash.')
    bridge = bundle / 'Contents/MacOS/libNianWifiEvidence.dylib'
    if any(path.is_symlink() for path in (bundle, bundle / 'Contents', bundle / 'Contents/MacOS', bridge)) or not bridge.is_file():
        raise ValueError('Discovery bridge missing or linked.')
    if hashlib.sha256(bridge.read_bytes()).hexdigest() != pin:
        raise ValueError('Discovery bridge hash mismatch.')
    return bridge


def sign_discovery_bundle(bundle, pin):
    bridge = require_discovery_bridge(bundle, pin)
    binaries = bundle / 'Contents/MacOS'
    apphost = binaries / 'NianDeskLink'
    macho = (bytes.fromhex('cffaedfe'), bytes.fromhex('feedfacf'), bytes.fromhex('cafebabe'), bytes.fromhex('bebafeca'))
    if apphost.is_symlink() or not apphost.is_file() or apphost.read_bytes()[:4] not in macho:
        raise ValueError('Discovery apphost missing or invalid.')
    resources, native = [], []
    for path in sorted(binaries.rglob('*')):
        if path.is_symlink():
            raise ValueError('Linked executable refused.')
        if not path.is_file() or path in (bridge, apphost):
            continue
        with path.open('rb') as source:
            magic = source.read(4)
        if magic in macho:
            native.append(path)
        else:
            # Files in Contents/MacOS are treated as nested code, even .NET PE
            # assemblies and ordinary JS/HTML/JSON. Their signatures use xattrs.
            resources.append(path)
    subprocess.run(['/usr/bin/codesign', '--verify', '--strict', str(bridge)], check=True)
    for path in resources + native:
        subprocess.run(['/usr/bin/codesign', '--force', '--sign', '-', str(path)], check=True)
    subprocess.run(['/usr/bin/codesign', '--force', '--sign', '-', str(bundle)], check=True)
    subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(bundle)], check=True)
    require_discovery_bridge(bundle, pin)
    subprocess.run(['/usr/bin/codesign', '--verify', '--strict', str(bridge)], check=True)


def launch_blue_bundle(root, bundle, profile):
    """Keep a process-wide test-launch lock until this new app instance exits."""
    import fcntl
    lock_path = root / '.nian-link-blue-test-launch.lock'
    fd = os.open(lock_path, os.O_RDWR | os.O_CREAT | getattr(os, 'O_NOFOLLOW', 0), 0o600)
    locked = False
    try:
        info = os.fstat(fd)
        if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.geteuid() or
                stat.S_IMODE(info.st_mode) & 0o077):
            raise PermissionError('Unsafe test launch lock.')
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            locked = True
        except BlockingIOError as error:
            raise RuntimeError('Another test instance is already running.') from error
        escaped_root = re.sub(r'([.\\^$*+?{}\[\]|()])', r'\\\1', str(root))
        test_path = (r'^' + escaped_root +
            r'/nian-link-test-[0-9a-f]{32}/NianDeskLink\.app/Contents/MacOS/NianDeskLink([[:space:]]|$)')
        running = subprocess.run(['/usr/bin/pgrep', '-f', test_path], check=False, capture_output=True, text=True)
        if running.returncode == 0:
            raise RuntimeError('Another test instance is already running; close it normally first.')
        if running.returncode != 1:
            raise RuntimeError('Unable to verify the current test processes.')
        subprocess.run(['/usr/bin/open', '-n', '-W', str(bundle), '--args', '--data-dir', str(profile)], check=True)
    finally:
        if locked:
            fcntl.flock(fd, fcntl.LOCK_UN)
        os.close(fd)


def launch_discovery(root, label, fileless=False, force_new=False):
    if force_new and not fileless:
        raise ValueError('Blue launcher requires the fileless test.')
    target = discovery_target(root, label)
    require_private_owned(target)
    profile = target / 'profile'
    require_private_owned(profile)
    bundle = target / 'NianDeskLink.app'
    info_path = bundle / 'Contents/Info.plist'
    manifest_path = bundle / 'Contents/Resources/build-manifest.json'
    for path in (bundle, bundle / 'Contents', bundle / 'Contents/Resources', info_path, manifest_path):
        if path.is_symlink():
            raise ValueError('Linked test bundle refused.')
    with info_path.open('rb') as source:
        info = plistlib.load(source)
    expected_identity = ('com.niannian.nianlink.fileless-test', '年年互联·免文件测试') if fileless else ('com.niannian.nianlink.discovery-test', '年年互联·发现测试')
    if (info.get('CFBundleIdentifier'), info.get('CFBundleDisplayName')) != expected_identity:
        raise ValueError('Unexpected test bundle identity.')
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    if manifest.get('channel') != ('fileless-internal-test' if fileless else 'discovery-internal-test') or manifest.get('bundleIdentifier') != info['CFBundleIdentifier']:
        raise ValueError('Unexpected test manifest.')
    bridge = require_discovery_bridge(bundle, manifest.get('discoveryBridgeSha256'))
    subprocess.run(['/usr/bin/codesign', '--verify', '--strict', str(bridge)], check=True)
    subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(bundle)], check=True)
    if force_new:
        launch_blue_bundle(root, bundle, profile)
    else:
        subprocess.run(['/usr/bin/open', str(bundle), '--args', '--data-dir', str(profile)], check=True)
    return {'launchRequested': True, 'bundleId': info['CFBundleIdentifier']}


def main():
    root = Path.home() / 'Projects'
    label, action = sys.argv[1:3]
    if action == 'prepare-discovery' and len(sys.argv) == 3:
        print(json.dumps({'prepared': str(prepare_discovery(root, label))}))
        return
    if action == 'create-discovery-profile' and len(sys.argv) == 3:
        print(json.dumps({'profile': str(create_discovery_profile(root, label))}))
        return
    if action == 'create-discovery-launcher' and len(sys.argv) == 3:
        print(json.dumps(create_discovery_launcher(root, Path.home() / 'Desktop', label, Path(__file__).resolve()), ensure_ascii=False))
        return
    if action == 'create-fileless-launcher' and len(sys.argv) == 3:
        print(json.dumps(create_discovery_launcher(root, Path.home() / 'Desktop', label, Path(__file__).resolve(), fileless=True), ensure_ascii=False))
        return
    if action == 'create-blue-fileless-launcher' and len(sys.argv) == 3:
        print(json.dumps(create_discovery_launcher(root, Path.home() / 'Desktop', label, Path(__file__).resolve(), fileless=True, blue=True), ensure_ascii=False))
        return
    if action == 'launch-discovery' and len(sys.argv) == 3:
        print(json.dumps(launch_discovery(root, label)))
        return
    if action == 'launch-fileless' and len(sys.argv) == 3:
        print(json.dumps(launch_discovery(root, label, fileless=True)))
        return
    if action == 'launch-blue-fileless' and len(sys.argv) == 3:
        print(json.dumps(launch_discovery(root, label, fileless=True, force_new=True)))
        return
    if not label.startswith('nian-link-test-') or not label[15:].isalnum():
        raise SystemExit('Unexpected test label')
    target = root / label
    if any(p.is_symlink() for p in (root, *root.parents)) or not root.is_dir():
        raise SystemExit('Linked or missing test parent refused')
    if action == 'prepare':
        target.mkdir(mode=0o700, exist_ok=False)
        print(json.dumps({'prepared': str(target)}))
        raise SystemExit(0)
    if action != 'extract' or len(sys.argv) not in (4, 5):
        raise SystemExit('Unexpected operation')
    multi_device = len(sys.argv) == 5 and sys.argv[4] == '--multi-device-test'
    discovery = len(sys.argv) == 5 and sys.argv[4] == '--discovery-test'
    fileless = len(sys.argv) == 5 and sys.argv[4] == '--fileless-test'
    if len(sys.argv) == 5 and not (multi_device or discovery or fileless):
        raise SystemExit('Unexpected test type')
    if discovery or fileless:
        discovery_target(root, label)
        require_private_owned(target)
    expected = sys.argv[3].lower()
    if len(expected) != 64 or any(c not in '0123456789abcdef' for c in expected):
        raise SystemExit('Invalid expected hash')
    if target.is_symlink() or not target.is_dir():
        raise SystemExit('Unexpected test directory')
    archive = target / 'NianDeskLink-test.zip'
    if archive.is_symlink() or not archive.is_file():
        raise SystemExit('Unexpected archive')
    sha = hashlib.sha256()
    with archive.open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            sha.update(block)
    digest = sha.hexdigest()
    if digest != expected:
        raise SystemExit('Archive hash mismatch')
    bundle = target / 'NianDeskLink.app'
    if bundle.exists() or bundle.is_symlink():
        raise SystemExit('Refusing to overwrite test application')
    with zipfile.ZipFile(archive) as zipped:
        entries = zipped.infolist()
        names = [x.filename for x in entries]
        if len(names) != len(set(names)) or sum(x.file_size for x in entries) > 1024 ** 3:
            raise SystemExit('Unexpected archive size or duplicate path')
        for entry in entries:
            path = PurePosixPath(entry.filename)
            mode = entry.external_attr >> 16
            if (path.is_absolute() or '..' in path.parts or not path.parts or path.parts[0] != bundle.name
                    or entry.is_dir() or stat.S_ISLNK(mode) or entry.flag_bits & 1):
                raise SystemExit('Unsafe archive entry')
        zipped.extractall(target)
        for entry in entries:
            mode = entry.external_attr >> 16
            (target / entry.filename).chmod(0o755 if mode & 0o111 else 0o644)
    with (bundle / 'Contents/Info.plist').open('rb') as source:
        info = plistlib.load(source)
    expected_identity = (('com.niannian.nianlink.fileless-test', '年年互联·免文件测试') if fileless else
                         ('com.niannian.nianlink.discovery-test', '年年互联·发现测试') if discovery else
                         ('com.niannian.nianlink.multidevice-test', '年年互联·多设备测试') if multi_device else
                         ('com.niannian.nianlink', '年年互联·独立测试'))
    if (info.get('CFBundleIdentifier'), info.get('CFBundleDisplayName')) != expected_identity:
        raise SystemExit('Unexpected app identity')
    if multi_device or discovery or fileless:
        manifest_path = bundle / 'Contents/Resources/build-manifest.json'
        manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
        channel = 'fileless-internal-test' if fileless else 'discovery-internal-test' if discovery else 'multi-device-internal-test'
        if manifest.get('channel') != channel or manifest.get('bundleIdentifier') != expected_identity[0]:
            raise SystemExit('Unexpected test manifest')
    if discovery or fileless:
        sign_discovery_bundle(bundle, manifest.get('discoveryBridgeSha256'))
    else:
        subprocess.run(['/usr/bin/codesign', '--force', '--deep', '--sign', '-', str(bundle)], check=True)
        subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(bundle)], check=True)
    result = {'app': str(bundle), 'archiveSha256': digest, 'bundleId': info['CFBundleIdentifier'],
              'displayName': info['CFBundleDisplayName'], 'adHocSigned': True, 'notarized': False,
              'appLaunched': False}
    (target / 'staging-result.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
