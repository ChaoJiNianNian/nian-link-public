"""Package a cross-published Avalonia application with Unix file modes from any OS."""
from pathlib import Path
import argparse
import hashlib
import importlib.util
import json
import plistlib
import re
import shutil
import stat
import uuid
import xml.etree.ElementTree as ET
import zipfile


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def require_discovery_bridge(publish, pin):
    if not isinstance(pin, str) or not re.fullmatch(r'[0-9a-f]{64}', pin):
        raise ValueError('A signed discovery bridge hash is required.')
    bridge = publish / 'libNianWifiEvidence.dylib'
    if bridge.is_symlink() or not bridge.is_file() or digest(bridge) != pin:
        raise ValueError('Signed discovery bridge is missing, linked or does not match.')
    return pin


def archive_mode(file):
    executable = file.name in {'NianDeskLink', 'DeskLinkMedia'} or file.suffix.lower() in {'.dylib', '.so', '.sh'}
    return '100755' if executable else '100644'


def preview_identity_helpers():
    # Reuse the candidate's independently validated v2 wire format and Mach-O parser.
    path = Path(__file__).resolve().parent / 'preview_identity.py'
    spec = importlib.util.spec_from_file_location('nian_preview_identity_helpers', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def package_cache(assets, override):
    raw = override or assets.get('project', {}).get('restore', {}).get('packagesPath')
    if not raw or not Path(raw).is_dir():
        raise ValueError('NuGet packages directory unavailable; restore or specify --packages-dir.')
    return Path(raw).resolve()


FILELESS_BUILD_INPUTS = (
    'scripts/build-mac-test.ps1',
    'scripts/BuildArtifactLock.psm1',
    'scripts/SdkBaseline.psm1',
    'scripts/FilelessArtifactCache.psm1',
    'scripts/FilelessBuildMetadata.psm1',
)
MANAGED_FILELESS_BUILD_INPUTS = (
    'scripts/MacFilelessManagedRetention.psm1',
    'scripts/verify_mac_fileless_candidate.py',
)


def collect_sources(project, assets, entry_project=None, *, fileless_test=False, managed_fileless=False):
    """Hash local restore-graph projects and known packaging inputs, not generated outputs.

    This records source candidates at packaging time, not proof of what was used
    during compilation or of a reproducible build. Package/runtime dependencies and
    the separately compiled Swift binaries need their own provenance.
    """
    project = Path(project).resolve(strict=True)
    source_root = (project / 'src').resolve(strict=True)
    mac = source_root / 'DesktopToolbox.Mac'
    entry = (project / entry_project).resolve(strict=True) if entry_project else mac / 'DesktopToolbox.Mac.csproj'
    if not entry.is_relative_to(source_root) or entry.suffix.lower() != '.csproj' or not entry.is_file():
        raise ValueError('Entry project must be a local source project.')
    libraries = assets.get('libraries')
    if not isinstance(libraries, dict):
        raise ValueError('Restore project graph is missing.')
    directories = {mac, entry.parent, source_root / 'DesktopToolbox.Collaboration.Ui'}
    for library in libraries.values():
        if library.get('type') != 'project':
            continue
        path = library.get('path')
        if not isinstance(path, str) or not path or Path(path.replace('\\', '/')).is_absolute():
            raise ValueError('Unsafe restore project path.')
        referenced = (entry.parent / path.replace('\\', '/')).resolve(strict=True)
        if not referenced.is_relative_to(source_root) or referenced.suffix.lower() != '.csproj' or not referenced.is_file():
            raise ValueError('Restore project must be a local source project.')
        directories.add(referenced.parent)

    excluded = {'bin', 'obj', 'Tests', '.work', '__pycache__'}
    paths = set()
    for directory in directories:
        if not directory.is_dir() or directory.is_symlink():
            raise ValueError('Source directory missing or linked.')
        for file in directory.rglob('*'):
            if any(part in excluded for part in file.relative_to(directory).parts):
                continue
            if file.is_symlink():
                raise ValueError('Linked source file or directory refused.')
            if file.is_file():
                paths.add(file)
    for relative in ('global.json', 'NuGet.Config', 'Directory.Build.props', 'Directory.Build.targets',
                     'src/Directory.Build.props', 'src/Directory.Build.targets',
                     'scripts/build-mac-test.ps1', 'THIRD-PARTY-NOTICES/lucide-1.8.0.txt'):
        file = project / relative
        if file.is_symlink():
            raise ValueError('Linked build input refused.')
        if file.is_file():
            paths.add(file)
    if managed_fileless and not fileless_test:
        raise ValueError('Managed fileless source inventory requires fileless testing.')
    if fileless_test:
        for relative in FILELESS_BUILD_INPUTS + (MANAGED_FILELESS_BUILD_INPUTS if managed_fileless else ()):
            file = project / relative
            for candidate in (file, *file.parents):
                if candidate == project.parent:
                    break
                try:
                    info = candidate.lstat()
                except FileNotFoundError as exc:
                    raise ValueError('Required fileless build input is missing: ' + relative) from exc
                if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
                    raise ValueError('Linked fileless build input refused: ' + relative)
            if not file.is_file():
                raise ValueError('Required fileless build input is missing: ' + relative)
            paths.add(file)
    return [{'path': file.relative_to(project).as_posix(), 'sha256': digest(file)}
            for file in sorted(paths, key=lambda file: file.relative_to(project).as_posix())]


def preview_bundle_identity(bundle):
    """Compute v2 identity for every unsigned payload file except its self-referential manifest."""
    if bundle.name != 'NianDeskLink.app':
        raise ValueError('Unexpected production bundle name.')
    with (bundle / 'Contents/Info.plist').open('rb') as stream:
        plist = plistlib.load(stream)
    bundle_id = plist.get('CFBundleIdentifier')
    if bundle_id not in {'com.niannian.nianlink', 'com.niannian.nianlink.multidevice-test', 'com.niannian.nianlink.discovery-test', 'com.niannian.nianlink.fileless-test'}:
        raise ValueError('Unexpected application bundle identity.')
    if (bundle / 'Contents/_CodeSignature').exists():
        raise ValueError('Preview identity must be calculated before signing.')
    apphost = bundle / 'Contents/MacOS/NianDeskLink'
    host = apphost.read_bytes()
    rows = {}
    for file in bundle.rglob('*'):
        if file.is_symlink():
            raise ValueError('Links are not accepted in the unsigned production payload.')
        if not file.is_file():
            continue
        relative = file.relative_to(bundle).as_posix()
        if relative == 'Contents/Resources/build-manifest.json':
            continue  # It contains the final apphost hash, so cannot seed its own identity.
        rows[bundle.name + '/' + relative] = {'bytes': file.stat().st_size, 'sha256': digest(file).upper(), 'unixMode': archive_mode(file)}
    helpers = preview_identity_helpers()
    payload_sha = helpers.preview_payload_sha256(rows, bundle.name, host)
    return {'scheme': helpers.PREVIEW_UUID_SCHEME, 'payloadSha256': payload_sha,
            'executableUuid': helpers.derive_preview_executable_uuid(bundle_id, payload_sha)}


def apply_preview_payload_identity(bundle):
    identity = preview_bundle_identity(bundle)
    apphost = bundle / 'Contents/MacOS/NianDeskLink'
    host = bytearray(apphost.read_bytes())
    offset, _ = preview_identity_helpers().macho_uuid(bytes(host))
    host[offset:offset + 16] = uuid.UUID(identity['executableUuid']).bytes
    apphost.write_bytes(host)
    if preview_bundle_identity(bundle) != identity:
        raise ValueError('The final unsigned payload did not retain its derived identity.')
    return identity


def verify_preview_archive(zipped, expected, bundle_id='com.niannian.nianlink'):
    helpers = preview_identity_helpers()
    prefix = 'NianDeskLink.app/'
    excluded = prefix + 'Contents/Resources/build-manifest.json'
    entries = zipped.infolist()
    if len(entries) != len({entry.filename for entry in entries}):
        raise ValueError('The production archive contains duplicate entries.')
    rows = {}
    for item in entries:
        if item.filename == excluded:
            continue
        if not item.filename.startswith(prefix) or item.is_dir() or item.flag_bits & 1:
            raise ValueError('Unexpected production archive entry.')
        data = zipped.read(item)
        rows[item.filename] = {'bytes': item.file_size, 'sha256': hashlib.sha256(data).hexdigest().upper(),
                               'unixMode': format(item.external_attr >> 16, 'o')}
    host = zipped.read(prefix + 'Contents/MacOS/NianDeskLink')
    payload = helpers.preview_payload_sha256(rows, 'NianDeskLink.app', host)
    plist = plistlib.loads(zipped.read(prefix + 'Contents/Info.plist'))
    if bundle_id not in {'com.niannian.nianlink', 'com.niannian.nianlink.multidevice-test', 'com.niannian.nianlink.discovery-test', 'com.niannian.nianlink.fileless-test'} or plist.get('CFBundleIdentifier') != bundle_id:
        raise ValueError('The archive bundle identity is unexpected.')
    identity = helpers.derive_preview_executable_uuid(bundle_id, payload)
    if payload != expected['payloadSha256'] or identity != expected['executableUuid'] or helpers.macho_uuid(host)[1] != identity:
        raise ValueError('The archived production payload does not match its derived preview identity.')
    manifest = json.loads(zipped.read(excluded))
    if (manifest.get('previewPayloadIdentity') != expected or
            manifest.get('previewPayloadIdentityExcludes') != ['Contents/Resources/build-manifest.json']):
        raise ValueError('The archived manifest does not match its preview identity.')
    listed = manifest.get('files')
    if not isinstance(listed, list) or len(listed) != len(rows):
        raise ValueError('The archived manifest does not list the exact unsigned payload.')
    by_path = {item.get('path'): item for item in listed if isinstance(item, dict)}
    if len(by_path) != len(listed) or set(by_path) != set(rows):
        raise ValueError('The archived manifest paths do not match the unsigned payload.')
    for path, row in rows.items():
        recorded = by_path[path]
        if recorded.get('bytes') != row['bytes'] or str(recorded.get('sha256', '')).upper() != row['sha256']:
            raise ValueError('The archived manifest file hash does not match: ' + path)


def read_isolated_assets(project, path, entry_project):
    root = (project / 'tests/.work').resolve()
    path = Path(path).resolve()
    if not path.is_relative_to(root) or path.name != 'project.assets.json':
        raise ValueError('Isolated assets must be under tests/.work.')
    assets = json.loads(path.read_text(encoding='utf-8-sig'))
    restored = assets.get('project', {}).get('restore', {}).get('projectPath')
    if not isinstance(restored, str) or Path(restored).resolve() != entry_project.resolve():
        raise ValueError('Isolated restore graph does not belong to the entry project.')
    return assets


def bundle_identity(original, multi_device_test=False, discovery_test=False, fileless_test=False):
    if original.get('CFBundleIdentifier') != 'com.niannian.nianlink':
        raise ValueError('Unexpected base application identity.')
    selected = dict(original)
    if sum((multi_device_test, discovery_test, fileless_test)) > 1:
        raise ValueError('Candidate identities are mutually exclusive.')
    if multi_device_test:
        selected['CFBundleIdentifier'] = 'com.niannian.nianlink.multidevice-test'
        selected['CFBundleName'] = '年年互联·多设备测试'
        selected['CFBundleDisplayName'] = '年年互联·多设备测试'
    if discovery_test or fileless_test:
        selected['CFBundleIdentifier'] = 'com.niannian.nianlink.fileless-test' if fileless_test else 'com.niannian.nianlink.discovery-test'
        selected['CFBundleName'] = '年年互联·免文件测试' if fileless_test else '年年互联·发现测试'
        selected['CFBundleDisplayName'] = selected['CFBundleName']
        selected['NSLocalNetworkUsageDescription'] = '用于在您开启发现后，通过局域网发现设备并连接已确认的电脑。发现期间设备名称、系统类型和公钥指纹会在局域网内可见。'
    return selected


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--publish', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--preview-control', action='store_true')
    parser.add_argument('--multi-device-test', action='store_true', help='Use a separate internal-test app identity.')
    parser.add_argument('--discovery-test', action='store_true', help='Use the separate discovery candidate identity.')
    parser.add_argument('--fileless-test', action='store_true', help='Use the isolated fileless candidate identity.')
    parser.add_argument('--managed-fileless', action='store_true', help='Include managed fileless build inputs.')
    parser.add_argument('--assets-file', help='Isolated restore graph, required only for fileless test.')
    parser.add_argument('--discovery-bridge-sha256', help='SHA-256 of the already-signed discovery bridge.')
    parser.add_argument('--packages-dir', help='NuGet cache; defaults to the restore location recorded in project.assets.json')
    args = parser.parse_args()
    if sum((args.preview_control, args.multi_device_test, args.discovery_test, args.fileless_test)) > 1:
        parser.error('Candidate modes are mutually exclusive.')
    if bool(args.discovery_test or args.fileless_test) != bool(args.discovery_bridge_sha256):
        parser.error('Discovery test and its signed bridge hash must be specified together.')
    if bool(args.fileless_test) != bool(args.assets_file):
        parser.error('Fileless test and its isolated restore graph must be specified together.')
    if args.managed_fileless and not args.fileless_test:
        parser.error('Managed fileless packaging requires fileless testing.')
    project = Path(__file__).resolve().parents[3]
    mac = project / 'src/DesktopToolbox.Mac'
    publish = Path(args.publish).resolve()
    output = Path(args.output).resolve()
    output.relative_to(project)
    if args.managed_fileless and (output.parent != project / '.artifacts/mac-package-managed/fileless' or
                                  not re.fullmatch(r'test-[0-9a-f]{32}', output.name)):
        raise ValueError('Managed fileless output must use its dedicated GUID directory.')
    publish.relative_to(output)
    discovery_pin = require_discovery_bridge(publish, args.discovery_bridge_sha256) if args.discovery_test or args.fileless_test else None
    entry_project = project / ('src/NianLink.MacDiscoveryCandidate/NianLink.MacDiscoveryCandidate.csproj' if args.discovery_test or args.fileless_test else 'src/DesktopToolbox.Mac/DesktopToolbox.Mac.csproj')
    assets = read_isolated_assets(project, args.assets_file, entry_project) if args.fileless_test else json.loads((entry_project.parent / 'obj/project.assets.json').read_text(encoding='utf-8-sig'))
    sources = collect_sources(project, assets, entry_project=entry_project.relative_to(project), fileless_test=True,
                              managed_fileless=args.managed_fileless) if args.fileless_test else None
    if args.discovery_test:
        restored = assets.get('project', {}).get('restore', {}).get('projectPath')
        if not isinstance(restored, str) or Path(restored).resolve() != entry_project.resolve():
            raise ValueError('Discovery restore graph does not belong to the discovery entry project.')
    packages_dir = package_cache(assets, args.packages_dir)
    icon = mac / 'Assets/NianDeskLink.icns'
    if not icon.is_file():
        raise SystemExit('The Mac application icon is missing.')
    icon_bytes = icon.read_bytes()
    if len(icon_bytes) < 16 or icon_bytes[:4] != b'icns' or int.from_bytes(icon_bytes[4:8], 'big') != len(icon_bytes):
        raise SystemExit('The Mac application icon has an invalid ICNS header.')
    with (mac / 'Assets/Info.plist').open('rb') as stream:
        plist = bundle_identity(plistlib.load(stream), multi_device_test=args.multi_device_test, discovery_test=args.discovery_test, fileless_test=args.fileless_test)
    if plist.get('CFBundleIconFile') != icon.name:
        raise SystemExit('CFBundleIconFile must identify the packaged application icon.')
    executable = publish / 'NianDeskLink'
    if not executable.is_file() or executable.read_bytes()[:4].hex() not in {'cffaedfe', 'cafebabe', 'bebafeca'}:
        raise SystemExit('The publish output is not a macOS Mach-O apphost.')
    forbidden_names = {'deskflow.conf', 'trusted-clients', 'trusted-servers', 'paired-control.json', 'connection.json'}
    for file in publish.rglob('*'):
        if file.is_symlink() or (file.is_file() and (file.suffix.lower() in {'.pem', '.key', '.pfx', '.p12'} or file.name.lower() in forbidden_names)):
            raise SystemExit('Unexpected security/profile material or link in publish output.')
    bundle = output / 'NianDeskLink.app'
    if bundle.exists():
        raise SystemExit('Application bundle already exists; use a new output directory.')
    binary_dir = bundle / 'Contents/MacOS'
    resources = bundle / 'Contents/Resources'
    binary_dir.parent.mkdir(parents=True)
    # This is a newly generated staging directory inside the checked release root.
    # Move it into the bundle rather than retaining a second runtime copy.
    shutil.move(str(publish), str(binary_dir))
    resources.mkdir(parents=True)
    (resources / icon.name).write_bytes(icon_bytes)
    plist['CFBundleVersion'] = plist['CFBundleShortVersionString'] = args.version
    with (bundle / 'Contents/Info.plist').open('wb') as stream:
        plistlib.dump(plist, stream, sort_keys=False)
    notices = resources / 'ThirdParty'
    shutil.copytree(mac / 'Assets/ThirdParty', notices)
    lucide = project / 'THIRD-PARTY-NOTICES/lucide-1.8.0.txt'
    if not lucide.is_file():
        raise SystemExit('The shared UI Lucide license notice is missing.')
    ui_notices = resources / 'THIRD-PARTY-NOTICES'
    ui_notices.mkdir()
    shutil.copy2(lucide, ui_notices / lucide.name)
    packages = []
    for key, library in sorted(assets.get('libraries', {}).items()):
        if library.get('type') != 'package':
            continue
        identifier, version = key.rsplit('/', 1)
        location = packages_dir / identifier.lower() / version.lower()
        if not location.is_dir():
            raise SystemExit('NuGet package is missing from the selected cache: ' + identifier + '/' + version)
        item = {'id': identifier, 'version': version}
        nuspec = next(location.glob('*.nuspec'), None)
        if nuspec:
            xml = ET.parse(nuspec).getroot()
            for node in xml.iter():
                if node.tag.rsplit('}', 1)[-1] == 'license':
                    item['license'] = node.text
                    item['licenseType'] = node.attrib.get('type')
        for notice in location.iterdir() if location.is_dir() else []:
            if notice.is_file() and any(word in notice.name.lower() for word in ('license', 'notice', 'copying')):
                destination = notices / 'package-notices' / (identifier + '-' + version) / notice.name
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(notice, destination)
        packages.append(item)
    # Runtime packs are not listed as ordinary NuGet library dependencies.
    # Copy the exact self-contained runtime notices rather than omitting them.
    runtime_config = json.loads((binary_dir / 'NianDeskLink.runtimeconfig.json').read_text(encoding='utf-8-sig'))
    for framework in runtime_config.get('runtimeOptions', {}).get('includedFrameworks', []):
        if framework['name'] != 'Microsoft.NETCore.App':
            raise SystemExit('Unexpected self-contained framework; review its notices before packaging.')
        runtime_id = 'Microsoft.NETCore.App.Runtime.osx-arm64'
        location = packages_dir / runtime_id.lower() / framework['version']
        for name in ('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'):
            source = location / name
            if not source.is_file():
                raise SystemExit('The exact bundled .NET runtime license or notices are missing.')
            destination = notices / 'package-notices' / (runtime_id + '-' + framework['version']) / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
        packages.append({'id': runtime_id, 'version': framework['version'], 'license': 'MIT', 'licenseType': 'expression'})
    (notices / 'NuGet-licenses.json').write_text(json.dumps(packages, indent=2) + '\n', encoding='utf-8')
    identity = apply_preview_payload_identity(bundle) if args.preview_control or args.multi_device_test or args.discovery_test or args.fileless_test else None
    if sources is None:
        sources = collect_sources(project, assets, entry_project=entry_project.relative_to(project))
    files = [{'path': file.relative_to(output).as_posix(), 'bytes': file.stat().st_size, 'sha256': digest(file)} for file in sorted(bundle.rglob('*')) if file.is_file()]
    manifest = {'app': 'NianDeskLink', 'version': args.version, 'target': 'osx-arm64', 'selfContained': True, 'signed': False, 'notarized': False, 'fileManifestExcludesItself': True, 'files': files, 'sources': sources,
                'sourceManifestScope': 'local restore-graph project trees, shared UI, and known build inputs observed at packaging time; not proof of build-time inputs or a reproducible build'}
    if args.multi_device_test:
        manifest['channel'] = 'multi-device-internal-test'
        manifest['bundleIdentifier'] = plist['CFBundleIdentifier']
    if args.discovery_test or args.fileless_test:
        manifest['channel'] = 'fileless-internal-test' if args.fileless_test else 'discovery-internal-test'
        manifest['bundleIdentifier'] = plist['CFBundleIdentifier']
        manifest['discoveryBridgeSha256'] = discovery_pin
    if identity is not None:
        manifest['previewPayloadIdentity'] = identity
        manifest['previewPayloadIdentityExcludes'] = ['Contents/Resources/build-manifest.json']
    (resources / 'build-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    archive = output / ('NianDeskLink-' + args.version + '-osx-arm64.zip')
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as zipped:
        for file in sorted(bundle.rglob('*')):
            if not file.is_file():
                continue
            name = file.relative_to(output).as_posix()
            item = zipfile.ZipInfo(name)
            item.create_system = 3
            item.external_attr = int(archive_mode(file), 8) << 16
            item.compress_type = zipfile.ZIP_DEFLATED
            zipped.writestr(item, file.read_bytes())
    with zipfile.ZipFile(archive) as zipped:
        for file in files:
            if hashlib.sha256(zipped.read(file['path'])).hexdigest() != file['sha256']:
                raise SystemExit('Archive payload verification failed.')
        apphost = zipped.getinfo('NianDeskLink.app/Contents/MacOS/NianDeskLink')
        if apphost.create_system != 3 or ((apphost.external_attr >> 16) & 0o111) != 0o111:
            raise SystemExit('macOS executable mode was not preserved.')
        if identity is not None:
            verify_preview_archive(zipped, identity, plist['CFBundleIdentifier'])
    summary = {'app': str(bundle), 'archive': str(archive), 'fileCount': len(files) + 1, 'bytes': archive.stat().st_size, 'sha256': digest(archive), 'signed': False, 'notarized': False}
    if identity is not None:
        summary['previewPayloadIdentity'] = identity
    (output / 'package-summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(summary, ensure_ascii=True))


if __name__ == '__main__':
    main()


