"""Read-only consistency checks for a new Windows multi-device test candidate.

The independently supplied status digest is a consistency anchor, not a
signature. This does not prove provenance, licence compliance, reproducible
builds, installed-package integrity, or real-device operation.
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat
import struct
import subprocess
import sys
import unicodedata
import zipfile

MAX_ENTRIES = 20_000
MAX_MEMBER = 512 * 1024 * 1024
MAX_EXPANDED = 2 * 1024 * 1024 * 1024
MAX_ARCHIVE = 2 * 1024 * 1024 * 1024
MAX_JSON = 8 * 1024 * 1024
MAX_CENTRAL = 16 * 1024 * 1024
MAX_MAC_MEMBER = 256 * 1024 * 1024
MAX_MAC_TOTAL = 512 * 1024 * 1024
BUNDLE = '年年互联-多设备测试'
ARCHIVE = 'NianLink-multidevice-windows-internal-test.zip'
MAC_ARCHIVE = 'NianDeskLink-macOS-arm64.zip'
PINS = {
    'scripts/WindowsTestKeyboardPayload.psm1': '21B604326FF07DF5CE6C7E951E0B30228E0AEAE41AFE683085405E384B21483E',
    'scripts/verify-mac-export-test.py': '111E8DF8F308FA63BCCB4DD213F417884AA9C2547FC0C6216E93B49EEA466437',
    'scripts/build-mac-export-test.py': '16504FFC19187CFB759C40968DE2884B79ABFD7FC702F0EDBF56B7751F3D1029',
}
REQUIRED = {'App/NianLink.MultiDevice.exe', 'App/NianLink.MultiDevice.dll', 'App/NianLink.MultiDevice.deps.json',
            'App/NianLink.MultiDevice.runtimeconfig.json', 'App/DisplayBridge.exe', 'App/DisplayBridge.dll',
            'App/DisplayBridge.deps.json', 'App/DisplayBridge.runtimeconfig.json', 'Keyboard/deskflow-core.exe',
            'Keyboard/settings/Deskflow.conf', '启动多设备测试.cmd', '使用说明.txt', '测试说明.txt'}
PRIVATE = {'profile', 'profiles', 'test-profiles', 'trusted-clients', 'trusted-servers',
           'paired-control.json', 'connection.json', 'multi-device-trust.bin', 'pending-prepare.bin',
           'pending-grant.bin', 'device-settings.bin', 'peer-invitation.bin', 'peer-finalizing.bin',
           'peer-promotion.bin', 'peer-reservation.bin', 'peer-retired.bin', 'peer-ready-evidence.bin',
           'peer-pending-invitation.bin', 'peer-bindings.json', 'server.conf', 'identity.json', 'trust.json',
           '.multi-device-trust.lock', '.peer-invitation.lock', '.session.lock', '.settings.lock', '.identity.lock', '.trust.lock'}


def _absolute(value): return Path(os.path.abspath(os.fspath(value)))
def _key(value): return unicodedata.normalize('NFC', value).casefold()


def _reject_links(path):
    for item in (path, *path.parents):
        try: info = item.lstat()
        except OSError as exc: raise ValueError('Missing or inaccessible candidate path.') from exc
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError('Linked candidate path refused.')


def _safe(name):
    if not isinstance(name, str) or not name or '\\' in name or ':' in name or any(ord(c) < 32 or ord(c) == 127 for c in name):
        raise ValueError('Unsafe candidate path.')
    parts = name.split('/')
    if any(not p or p in ('.', '..') or p.endswith(('.', ' ')) for p in parts): raise ValueError('Unsafe candidate path.')
    return parts


def _private(parts):
    name = '/'.join(parts)
    def private_part(part):
        value = _key(part)
        return (value in PRIVATE or any(value.startswith(base + '.') for base in PRIVATE if '.' in base) or
                value.startswith(('peer-aborted-', 'peer-completed-')))
    return (any(private_part(p) for p in parts) or parts[-1].lower().endswith(('.pem', '.key', '.pfx', '.p12')) or
            (_key(parts[-1]) == 'deskflow.conf' and _key(name) not in
             {'keyboard/settings/deskflow.conf', _key(BUNDLE + '/Keyboard/settings/Deskflow.conf')}))


def _unique(pairs):
    result = {}; seen = set()
    for key, value in pairs:
        normalized = _key(key)
        if normalized in seen: raise ValueError('Duplicate or ambiguous JSON key.')
        seen.add(normalized); result[key] = value
    return result


def _read_json(path, maximum=MAX_JSON):
    _reject_links(path)
    if not path.is_file() or path.stat().st_size > maximum: raise ValueError('Missing or oversized JSON.')
    try: return json.loads(path.read_text(encoding='utf-8-sig'), object_pairs_hook=_unique)
    except (OSError, UnicodeError, json.JSONDecodeError) as exc: raise ValueError('Invalid JSON.') from exc


def _hash(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''): digest.update(block)
    return digest.hexdigest()


def _tree(bundle):
    files = {}; directories = {}; pending = [bundle]; total = 0
    while pending:
        current = pending.pop(); _reject_links(current)
        with os.scandir(current) as children:
            for child in children:
                path = Path(child.path); info = child.stat(follow_symlinks=False)
                if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400: raise ValueError('Linked bundle item.')
                rel = path.relative_to(bundle).as_posix(); parts = _safe(rel); key = _key(rel)
                if _private(parts): raise ValueError('Private material refused.')
                if child.is_dir(follow_symlinks=False):
                    if key in directories: raise ValueError('Duplicate directory.')
                    directories[key] = rel; pending.append(path)
                elif child.is_file(follow_symlinks=False):
                    if key in files or info.st_size > MAX_MEMBER: raise ValueError('Duplicate or oversized file.')
                    files[key] = (rel, path, info.st_size); total += info.st_size
                    if total > MAX_EXPANDED: raise ValueError('Bundle exceeds size limit.')
                else: raise ValueError('Non-regular bundle item.')
                if len(files) + len(directories) > MAX_ENTRIES: raise ValueError('Too many bundle items.')
    return files, directories


def _zip_central_limits(source, *, max_bytes=MAX_ARCHIVE, max_entries=MAX_ENTRIES):
    source.seek(0, 2); length = source.tell()
    if length > max_bytes or length < 22: raise ValueError('Missing or oversized ZIP.')
    source.seek(max(0, length - 65557)); tail = source.read(); offset = tail.rfind(b'PK\x05\x06')
    if offset < 0 or offset + 22 > len(tail): raise ValueError('ZIP end record missing.')
    _, disk, central_disk, disk_count, count, size, start, comment = struct.unpack_from('<4s4H2LH', tail, offset)
    end = length - len(tail) + offset
    if (disk or central_disk or disk_count != count or count == 0 or count > max_entries or count == 0xffff or
            size == 0xffffffff or start == 0xffffffff or size > MAX_CENTRAL or start + size != end or
            offset + 22 + comment != len(tail)):
        raise ValueError('Unsupported or oversized ZIP directory.')
    source.seek(0)
    return count


def _zip_entries(zipped, source, *, max_member=MAX_MEMBER, max_total=MAX_EXPANDED, expected_count=None):
    files = {}; directories = {}; total = 0
    infos = zipped.infolist()
    if expected_count is not None and len(infos) != expected_count: raise ValueError('ZIP entry count differs.')
    explicit_dirs = set()
    for item in infos:
        if item.orig_filename != item.filename or item.extra or item.flag_bits & 1: raise ValueError('Ambiguous ZIP item.')
        source.seek(item.header_offset); header = source.read(30)
        if len(header) != 30 or header[:4] != b'PK\x03\x04' or struct.unpack_from('<H', header, 28)[0]:
            raise ValueError('Invalid or unsupported local ZIP header.')
        local_flags, local_method = struct.unpack_from('<HH', header, 6)
        if local_flags != item.flag_bits or local_method != item.compress_type: raise ValueError('Local and central ZIP metadata differ.')
        name_length = struct.unpack_from('<H', header, 26)[0]
        local_name = source.read(name_length)
        try: local_name = local_name.decode('utf-8' if item.flag_bits & 0x800 else 'cp437')
        except UnicodeError as exc: raise ValueError('Invalid local ZIP name.') from exc
        if local_name != item.orig_filename: raise ValueError('Local and central ZIP names differ.')
        raw = item.filename[:-1] if item.is_dir() else item.filename
        parts = _safe(raw); key = _key(raw)
        if _private(parts): raise ValueError('Private ZIP item refused.')
        mode_type = stat.S_IFMT(item.external_attr >> 16)
        allowed_types = (0, stat.S_IFDIR) if item.is_dir() else (0, stat.S_IFREG)
        if item.create_system not in (0, 3) or mode_type not in allowed_types or (item.create_system == 3 and mode_type == 0):
            raise ValueError('Unexpected ZIP item type.')
        if item.is_dir():
            if item.file_size or item.compress_size: raise ValueError('ZIP directory has content.')
            if key in explicit_dirs or (key in directories and directories[key] != raw): raise ValueError('Duplicate ZIP directory.')
            explicit_dirs.add(key)
            directories[key] = raw
        else:
            if key in files or item.file_size > max_member: raise ValueError('Duplicate or oversized ZIP item.')
            files[key] = (raw, item); total += item.file_size
            if total > max_total: raise ValueError('ZIP expands beyond limit.')
        for index in range(1, len(parts)):
            directory = '/'.join(parts[:index]); dkey = _key(directory)
            if dkey in directories and directories[dkey] != directory: raise ValueError('Ambiguous ZIP directory.')
            directories[dkey] = directory
    return files, directories


def _check_pins(root, names):
    raw = {}
    for name in names:
        path = root / name; _reject_links(path)
        if not path.is_file() or path.stat().st_size > MAX_JSON: raise ValueError('Reviewed validator missing or oversized.')
        try: text = path.read_text(encoding='utf-8')
        except (OSError, UnicodeError) as exc: raise ValueError('Invalid validator source.') from exc
        if hashlib.sha256(text.encode('utf-8')).hexdigest().upper() != PINS[name]: raise ValueError('Reviewed validator differs.')
        raw[name] = _hash(path)
    return raw


def _verify_fixed_keyboard(root, bundle, archive):
    name = 'scripts/WindowsTestKeyboardPayload.psm1'; before = _check_pins(root, [name])
    command = ("$ErrorActionPreference='Stop'; Import-Module $env:NIAN_VERIFY_ENGINE -ErrorAction Stop; "
               "Assert-WindowsTestEngineInputs -Directory $env:NIAN_VERIFY_KEYBOARD; "
               "$zip=[IO.Compression.ZipFile]::OpenRead($env:NIAN_VERIFY_ARCHIVE); try { "
               "$entries=@{}; foreach($e in $zip.Entries) { if(!$e.FullName.EndsWith('/')) { "
               "$entries[$e.FullName.Substring($env:NIAN_VERIFY_PREFIX.Length)]=$e } }; "
               "Assert-WindowsTestEngineArchive -Entries $entries } finally { $zip.Dispose() }")
    env = os.environ | {'NIAN_VERIFY_ENGINE': str(root/name), 'NIAN_VERIFY_KEYBOARD': str(bundle/'Keyboard'),
                        'NIAN_VERIFY_ARCHIVE': str(archive), 'NIAN_VERIFY_PREFIX': BUNDLE + '/'}
    try: result = subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command', command], env=env,
                                 capture_output=True, timeout=120)
    except (OSError, subprocess.TimeoutExpired) as exc: raise ValueError('Fixed keyboard validator unavailable.') from exc
    if result.returncode or _check_pins(root, [name]) != before: raise ValueError('Fixed keyboard validation failed.')


def _preflight_mac(directory):
    manifest = _read_json(directory/'manifest.json', 16384)
    archive = directory/MAC_ARCHIVE; _reject_links(archive)
    if not archive.is_file(): raise ValueError('Mac export archive missing.')
    with archive.open('rb') as source:
        count = _zip_central_limits(source, max_bytes=MAX_MEMBER)
        with zipfile.ZipFile(source) as zipped:
            files, _ = _zip_entries(zipped, source, max_member=MAX_MAC_MEMBER, max_total=MAX_MAC_TOTAL, expected_count=count)
            for _, item in files.values():
                if item.filename.lower().endswith('.zip'):
                    if item.file_size > MAX_MAC_MEMBER: raise ValueError('Nested Mac archive too large.')
                    nested = io.BytesIO(zipped.read(item)); nested_count = _zip_central_limits(nested, max_bytes=MAX_MAC_MEMBER)
                    with zipfile.ZipFile(nested) as inner: _zip_entries(inner, nested, max_member=MAX_MAC_MEMBER, max_total=MAX_MAC_TOTAL, expected_count=nested_count)
    return manifest


def _verify_mac_export(root, directory, version):
    names = ['scripts/verify-mac-export-test.py', 'scripts/build-mac-export-test.py']
    before = _check_pins(root, names)
    try: result = subprocess.run([sys.executable, '-B', str(root/names[0]), '--directory', str(directory),
                                  '--version', version, '--multi-device-testing'], capture_output=True, timeout=120)
    except (OSError, subprocess.TimeoutExpired) as exc: raise ValueError('Mac export validator unavailable.') from exc
    if result.returncode or _check_pins(root, names) != before: raise ValueError('Mac export validation failed.')


def verify_candidate(project_root, candidate, *, status_sha256, mac_version=None):
    root = _absolute(project_root); output = _absolute(candidate)
    if output.parent != root/'.artifacts/multidevice-windows-managed/multidevice' or not re.fullmatch(r'test-[a-f0-9]{32}', output.name):
        raise ValueError('Candidate outside dedicated managed root.')
    if not isinstance(status_sha256, str) or not re.fullmatch(r'[a-fA-F0-9]{64}', status_sha256):
        raise ValueError('Independent status digest required.')
    if mac_version is not None and (not isinstance(mac_version, str) or not re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', mac_version)):
        raise ValueError('Invalid explicit Mac version.')
    _reject_links(output)
    if not output.is_dir() or {p.name for p in output.iterdir()} != {BUNDLE, ARCHIVE, 'test-build-status.json'}:
        raise ValueError('Unexpected candidate items.')
    bundle = output/BUNDLE; archive = output/ARCHIVE; status_path = output/'test-build-status.json'
    for p in (bundle, archive, status_path): _reject_links(p)
    status = _read_json(status_path)
    if _hash(status_path) != status_sha256.lower(): raise ValueError('Status digest mismatch.')
    if (not isinstance(status, dict) or set(status) != {'testingOnly', 'realDeviceVerified', 'sourceRebuildVerified', 'signed',
            'macClientIncluded', 'keyboardCoreSha256', 'archiveSha256', 'files'} or
            status.get('testingOnly') is not True or any(status.get(k) is not False for k in ('realDeviceVerified','sourceRebuildVerified','signed')) or type(status.get('macClientIncluded')) is not bool):
        raise ValueError('Unexpected candidate status.')
    if not bundle.is_dir() or not archive.is_file() or archive.stat().st_size > MAX_ARCHIVE: raise ValueError('Candidate payload missing or oversized.')
    if not isinstance(status.get('archiveSha256'), str) or status['archiveSha256'].lower() != _hash(archive): raise ValueError('Archive digest mismatch.')
    files, directories = _tree(bundle)
    if not {_key(name) for name in REQUIRED} <= set(files): raise ValueError('Required candidate files missing.')
    rows = status.get('files')
    if not isinstance(rows, list) or not rows or len(rows) > MAX_ENTRIES: raise ValueError('Invalid file inventory.')
    seen = set()
    for row in rows:
        if not isinstance(row, dict) or set(row) != {'path', 'bytes', 'sha256'} or type(row.get('bytes')) is not int or row['bytes'] < 0 or not isinstance(row.get('sha256'), str) or not re.fullmatch(r'[a-fA-F0-9]{64}', row['sha256']): raise ValueError('Invalid file inventory item.')
        rel = row.get('path'); parts = _safe(rel); key = _key(rel)
        if _private(parts) or key in seen or key not in files: raise ValueError('Unsafe or duplicate inventory item.')
        seen.add(key); record = files[key]
        if record[0] != rel or record[2] != row['bytes'] or _hash(record[1]) != row['sha256'].lower(): raise ValueError('Bundle differs from inventory.')
    if seen != set(files): raise ValueError('Unlisted bundle files.')
    core = files[_key('Keyboard/deskflow-core.exe')][1]
    if not isinstance(status.get('keyboardCoreSha256'), str) or status['keyboardCoreSha256'].lower() != _hash(core): raise ValueError('Keyboard digest mismatch.')
    with archive.open('rb') as source:
        count = _zip_central_limits(source)
        try:
            with zipfile.ZipFile(source) as zipped:
                zipfiles, zipdirs = _zip_entries(zipped, source, expected_count=count)
                prefix = BUNDLE + '/'; prefixkey = _key(prefix)
                expected_dirs = {_key(BUNDLE): BUNDLE}
                expected_dirs.update({_key(prefix + name): prefix + name for name in directories.values()})
                if zipdirs != expected_dirs: raise ValueError('ZIP and bundle directories differ.')
                if set(zipfiles) != {prefixkey + k for k in files}: raise ValueError('ZIP and bundle file sets differ.')
                for key, (rel, item) in zipfiles.items():
                    record = files[key[len(prefixkey):]]
                    if rel != prefix + record[0] or item.file_size != record[2]: raise ValueError('ZIP and bundle metadata differ.')
                    with zipped.open(item) as stream: digest = hashlib.file_digest(stream, 'sha256').hexdigest()
                    if digest != _hash(record[1]): raise ValueError('ZIP and bundle content differ.')
        except (zipfile.BadZipFile, OSError, KeyError, struct.error) as exc: raise ValueError('Invalid candidate ZIP.') from exc
    mac_dir = bundle/'App/MacClient'
    if status['macClientIncluded']:
        if mac_version is None or not mac_dir.is_dir() or {p.name for p in mac_dir.iterdir()} != {MAC_ARCHIVE, 'manifest.json'}: raise ValueError('Explicit Mac version and exact export required.')
        mac_manifest = _preflight_mac(mac_dir)
        if (not isinstance(mac_manifest, dict) or mac_manifest.get('version') != mac_version or
                mac_manifest.get('file') != MAC_ARCHIVE or mac_manifest.get('mode') != 'multi-device-test' or
                mac_manifest.get('channel') != 'internal-test' or mac_manifest.get('includesDeskflow') is not True or
                mac_manifest.get('notarized') is not False or type(mac_manifest.get('bytes')) is not int or
                mac_manifest['bytes'] != (mac_dir/MAC_ARCHIVE).stat().st_size or
                not isinstance(mac_manifest.get('sha256'), str) or mac_manifest['sha256'].lower() != _hash(mac_dir/MAC_ARCHIVE)):
            raise ValueError('Mac export manifest or version differs.')
        _verify_mac_export(root, mac_dir, mac_version)
    elif mac_version is not None or mac_dir.exists(): raise ValueError('Unexpected Mac export.')
    _verify_fixed_keyboard(root, bundle, archive)
    after_files, after_directories = _tree(bundle)
    if after_directories != directories or set(after_files) != set(files): raise ValueError('Bundle changed during verification.')
    for row in rows:
        record = after_files[_key(row['path'])]
        if record[0] != row['path'] or record[2] != row['bytes'] or _hash(record[1]) != row['sha256'].lower():
            raise ValueError('Bundle changed during verification.')
    if _hash(status_path) != status_sha256.lower() or _hash(archive) != status['archiveSha256'].lower(): raise ValueError('Candidate changed during verification.')
    return {'verified': True, 'testingOnly': True, 'archiveSha256': status['archiveSha256'].upper()}


def main():
    parser = argparse.ArgumentParser(description='Read-only Windows multi-device test candidate consistency check.')
    parser.add_argument('--project-root', required=True); parser.add_argument('--candidate', required=True)
    parser.add_argument('--status-sha256', required=True); parser.add_argument('--mac-version')
    args = parser.parse_args()
    print(json.dumps(verify_candidate(args.project_root, args.candidate, status_sha256=args.status_sha256, mac_version=args.mac_version)))


if __name__ == '__main__': main()
