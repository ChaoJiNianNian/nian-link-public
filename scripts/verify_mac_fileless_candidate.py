"""Read-only consistency checks for a newly built internal Mac fileless candidate.

This does not establish authorship, Apple signing, notarization, licence
compliance, source reproducibility, or successful operation on a Mac.
The expected bridge and native-evidence hashes must come from the caller's
independently checked build inputs, not from the candidate being verified.
Current candidate ZIP format requires empty local and central extra fields;
if the package writer changes format, revalidate before using this checker.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import re
import stat
import struct
import unicodedata
import zipfile


ROOT = Path(__file__).resolve().parents[1]
_PACKAGE_FILE = ROOT / 'src/DesktopToolbox.Mac/build/package_app.py'
_SPEC = importlib.util.spec_from_file_location('nian_mac_candidate_package', _PACKAGE_FILE)
package = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(package)

MAX_ENTRIES = 10_000
MAX_MEMBER_BYTES = 256 * 1024 * 1024
MAX_EXPANDED_BYTES = 1024 * 1024 * 1024
MAX_ARCHIVE_BYTES = 1024 * 1024 * 1024
MAX_JSON_BYTES = 8 * 1024 * 1024
MAX_PLIST_BYTES = 2 * 1024 * 1024
SOURCE_NAMES = {'DeskLinkMedia.swift', 'RealtimeCapture.swift', 'RealtimeCaptureOutput.swift',
                'build-media.sh', 'media-sources.json', 'DeskLinkWeb.swift', 'build-web.sh'}
BINARY_NAMES = {'DeskLinkMedia': 2, 'libDeskLinkWeb.dylib': 6}
PRIVATE_NAMES = {'deskflow.conf', 'trusted-clients', 'trusted-servers', 'paired-control.json',
                 'connection.json', 'profile', 'profiles', 'test-profiles', '_codesignature'}
REQUIRED = {'Contents/Info.plist', 'Contents/MacOS/NianDeskLink', 'Contents/MacOS/NianDeskLink.dll',
            'Contents/MacOS/NianDeskLink.runtimeconfig.json', 'Contents/MacOS/DesktopToolbox.DeskLink.dll',
            'Contents/MacOS/NianLink.MacCandidateDiscovery.dll', 'Contents/MacOS/DeskLinkMedia',
            'Contents/MacOS/libDeskLinkWeb.dylib', 'Contents/MacOS/libNianWifiEvidence.dylib',
            'Contents/Resources/NianDeskLink.icns', 'Contents/Resources/ThirdParty/NuGet-licenses.json',
            'Contents/Resources/build-manifest.json'}


def _absolute(value):
    return Path(os.path.abspath(os.fspath(value)))


def _reject_links(path):
    for item in (path, *path.parents):
        try:
            info = item.lstat()
        except OSError as exc:
            raise ValueError('Missing or inaccessible candidate path.') from exc
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError('Linked candidate path refused.')


def _unique_object(pairs):
    result = {}
    folded = set()
    for key, value in pairs:
        normalized = unicodedata.normalize('NFC', key).casefold()
        if normalized in folded:
            raise ValueError('Ambiguous or duplicate JSON key.')
        folded.add(normalized)
        result[key] = value
    return result


def _read_json(path, maximum=MAX_JSON_BYTES):
    _reject_links(path)
    if not path.is_file() or path.stat().st_size > maximum:
        raise ValueError('Missing or oversized JSON input.')
    try:
        return json.loads(path.read_text(encoding='utf-8-sig'), object_pairs_hook=_unique_object)
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError('Invalid JSON input.') from exc


def _hash_file(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for block in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def _safe_relative(name):
    if not isinstance(name, str) or not name or '\\' in name or ':' in name or any(ord(c) < 32 for c in name):
        raise ValueError('Unsafe candidate path.')
    parts = name.split('/')
    if any(not x or x in ('.', '..') or x.endswith(('.', ' ')) for x in parts):
        raise ValueError('Unsafe candidate path.')
    return parts


def _key(name):
    return unicodedata.normalize('NFC', name).casefold()


def _private(parts):
    return (any(part.casefold() in PRIVATE_NAMES for part in parts) or
            parts[-1].lower().endswith(('.pem', '.key', '.pfx', '.p12')))


def _walk_bundle(bundle):
    result = {}
    directories = {}
    stack = [bundle]
    while stack:
        directory = stack.pop()
        _reject_links(directory)
        with os.scandir(directory) as entries:
            for entry in entries:
                path = Path(entry.path)
                info = entry.stat(follow_symlinks=False)
                if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
                    raise ValueError('Linked bundle content refused.')
                relative = path.relative_to(bundle).as_posix()
                parts = _safe_relative(relative)
                if _private(parts):
                    raise ValueError('Private or signed bundle content refused.')
                if entry.is_dir(follow_symlinks=False):
                    key = _key(relative)
                    if key in directories:
                        raise ValueError('Duplicate bundle directory.')
                    directories[key] = relative
                    stack.append(path)
                elif entry.is_file(follow_symlinks=False):
                    key = _key(relative)
                    if key in result or info.st_size > MAX_MEMBER_BYTES:
                        raise ValueError('Duplicate or oversized bundle file.')
                    result[key] = (relative, path, info.st_size)
                    if len(result) > MAX_ENTRIES:
                        raise ValueError('Too many bundle files.')
                else:
                    raise ValueError('Non-regular bundle content refused.')
    return result, directories


def _macho_header(path, kind):
    with path.open('rb') as source:
        header = source.read(32)
    if len(header) != 32:
        raise ValueError('Truncated native Mach-O.')
    magic, cpu, _, actual_kind = struct.unpack_from('<4I', header)
    if magic != 0xFEEDFACF or cpu != 0x0100000C or actual_kind != kind:
        raise ValueError('Unexpected native Mach-O identity.')


def _check_native_evidence(evidence, bundle):
    if not isinstance(evidence, dict) or evidence.get('schema') != 'mac-native-build-v1' or not re.fullmatch(r'native-[0-9a-f]{32}', str(evidence.get('label', ''))):
        raise ValueError('Unexpected native evidence identity.')
    sources = evidence.get('sourceFiles')
    binaries = evidence.get('binaries')
    source_hashes = {}
    for records, expected in ((sources, SOURCE_NAMES), (binaries, set(BINARY_NAMES))):
        if not isinstance(records, list) or len(records) != len(expected):
            raise ValueError('Incomplete native evidence.')
        seen = set()
        for item in records:
            if not isinstance(item, dict) or item.get('name') not in expected or item['name'] in seen or not re.fullmatch(r'[0-9A-F]{64}', str(item.get('sha256', ''))):
                raise ValueError('Invalid native evidence entry.')
            seen.add(item['name'])
            if records is binaries:
                file = bundle / 'Contents/MacOS' / item['name']
                if item.get('architecture') != 'arm64' or _hash_file(file).upper() != item['sha256']:
                    raise ValueError('Native helper does not match evidence.')
                _macho_header(file, BINARY_NAMES[item['name']])
            else:
                source_hashes[item['name']] = item['sha256'].lower()
    return source_hashes


def _check_sources(manifest, managed_fileless=False):
    sources = manifest.get('sources')
    if not isinstance(sources, list) or not sources or len(sources) > MAX_ENTRIES:
        raise ValueError('Missing or oversized source inventory.')
    seen = {}
    for item in sources:
        if not isinstance(item, dict) or not re.fullmatch(r'[0-9a-fA-F]{64}', str(item.get('sha256', ''))):
            raise ValueError('Invalid source inventory entry.')
        name = item.get('path'); parts = _safe_relative(name); key = _key(name)
        if _private(parts) or key in seen or any(x.casefold() in ('.work', 'bin', 'obj') for x in parts):
            raise ValueError('Unsafe or duplicate source inventory entry.')
        seen[key] = item['sha256'].lower()
    required = set(package.FILELESS_BUILD_INPUTS) | {'src/NianLink.MacDiscoveryCandidate/NianLink.MacDiscoveryCandidate.csproj'}
    if managed_fileless:
        required.update(package.MANAGED_FILELESS_BUILD_INPUTS)
    if not {_key(name) for name in required} <= set(seen):
        raise ValueError('Fileless source inventory lacks a required build input.')
    return seen


def _check_manifest_files(manifest):
    rows = manifest.get('files')
    if not isinstance(rows, list) or not rows or len(rows) > MAX_ENTRIES:
        raise ValueError('Missing or oversized file inventory.')
    seen = set()
    for item in rows:
        if (not isinstance(item, dict) or type(item.get('bytes')) is not int or
                not 0 <= item['bytes'] <= MAX_MEMBER_BYTES or
                not isinstance(item.get('sha256'), str) or not re.fullmatch(r'[0-9a-fA-F]{64}', item['sha256'])):
            raise ValueError('Invalid file inventory entry.')
        name = item.get('path'); parts = _safe_relative(name); key = _key(name)
        if parts[0] != 'NianDeskLink.app' or len(parts) < 2 or _private(parts[1:]) or key in seen:
            raise ValueError('Unsafe or duplicate file inventory entry.')
        seen.add(key)


def verify_candidate(project_root, candidate, *, version, bridge_sha256, evidence_sha256, managed_fileless=False):
    root = _absolute(project_root); output = _absolute(candidate)
    if type(managed_fileless) is not bool:
        raise ValueError('Managed fileless mode must be explicit.')
    if not isinstance(version, str) or not re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)', version):
        raise ValueError('Invalid candidate version.')
    if not all(isinstance(value, str) and re.fullmatch(r'[0-9a-fA-F]{64}', value) for value in (bridge_sha256, evidence_sha256)):
        raise ValueError('Independent bridge and evidence digests are required.')
    output_parent = root / ('.artifacts/mac-package-managed/fileless' if managed_fileless else '.artifacts/mac-package')
    if output.parent != output_parent or not re.fullmatch(r'test-[0-9a-f]{32}', output.name):
        raise ValueError('Candidate is outside the dedicated Mac test output.')
    _reject_links(output)
    if not output.is_dir():
        raise ValueError('Candidate directory is missing.')
    archive_name = f'NianDeskLink-{version}-osx-arm64.zip'
    expected = {'NianDeskLink.app', archive_name, 'package-summary.json', 'native-build-evidence.json', 'test-build-status.json'}
    with os.scandir(output) as entries:
        children = list(entries)
    if len(children) != len(expected) or {item.name for item in children} != expected:
        raise ValueError('Unexpected candidate output items.')
    for item in children:
        info = item.stat(follow_symlinks=False)
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError('Linked candidate output refused.')
    bundle = output / 'NianDeskLink.app'; archive = output / archive_name
    if not bundle.is_dir() or not archive.is_file() or archive.stat().st_size > MAX_ARCHIVE_BYTES:
        raise ValueError('Missing or oversized candidate package.')
    status = _read_json(output / 'test-build-status.json')
    summary = _read_json(output / 'package-summary.json')
    evidence_path = output / 'native-build-evidence.json'
    evidence = _read_json(evidence_path)
    if not isinstance(status, dict) or not isinstance(summary, dict):
        raise ValueError('Invalid package status or summary.')
    for key in ('testingOnly', 'discovery', 'filelessIsolatedTest'):
        if status.get(key) is not True: raise ValueError('Candidate is not the isolated fileless build.')
    for key in ('multiDevice', 'sourceRebuildVerified', 'macRealDeviceVerified', 'deskflowIncluded', 'signed', 'notarized'):
        if status.get(key) is not False: raise ValueError('Unexpected candidate status.')
    if str(status.get('nativeEvidenceSha256', '')).lower() != evidence_sha256.lower() or _hash_file(evidence_path) != evidence_sha256.lower():
        raise ValueError('Native evidence digest mismatch.')
    if summary.get('signed') is not False or summary.get('notarized') is not False:
        raise ValueError('Unexpected summary signature status.')
    for key, expected_path in (('app', bundle), ('archive', archive)):
        value = summary.get(key)
        if (not isinstance(value, str) or not os.path.isabs(value) or
                os.path.normcase(os.path.abspath(value)) != os.path.normcase(os.fspath(expected_path))):
            raise ValueError('Package summary names an unexpected output.')
    if type(summary.get('bytes')) is not int or summary['bytes'] != archive.stat().st_size or str(summary.get('sha256', '')).lower() != _hash_file(archive):
        raise ValueError('Archive summary differs from actual bytes.')
    tree, directories = _walk_bundle(bundle)
    if not {_key(name) for name in REQUIRED} <= set(tree):
        raise ValueError('Required candidate payload is missing.')
    manifest_path = bundle / 'Contents/Resources/build-manifest.json'
    manifest = _read_json(manifest_path)
    if not isinstance(manifest, dict) or manifest.get('app') != 'NianDeskLink' or manifest.get('version') != version or manifest.get('target') != 'osx-arm64' or manifest.get('selfContained') is not True or manifest.get('signed') is not False or manifest.get('notarized') is not False or manifest.get('fileManifestExcludesItself') is not True:
        raise ValueError('Unexpected candidate manifest status.')
    bundle_id = 'com.niannian.nianlink.fileless-test'
    if manifest.get('channel') != 'fileless-internal-test' or manifest.get('bundleIdentifier') != bundle_id or manifest.get('discoveryBridgeSha256') != bridge_sha256.lower():
        raise ValueError('Unexpected fileless identity or bridge pin.')
    info_path = bundle / 'Contents/Info.plist'
    if info_path.stat().st_size > MAX_PLIST_BYTES:
        raise ValueError('Oversized candidate plist.')
    try:
        plist = plistlib.loads(info_path.read_bytes())
    except (ValueError, TypeError, plistlib.InvalidFileException) as exc:
        raise ValueError('Invalid candidate plist.') from exc
    if not isinstance(plist, dict) or plist.get('CFBundleIdentifier') != bundle_id or plist.get('CFBundleVersion') != version or plist.get('CFBundleShortVersionString') != version:
        raise ValueError('Candidate plist identity or version mismatch.')
    bridge = bundle / 'Contents/MacOS/libNianWifiEvidence.dylib'
    if _hash_file(bridge) != bridge_sha256.lower():
        raise ValueError('Candidate bridge digest mismatch.')
    _macho_header(bridge, 6)
    native_sources = _check_native_evidence(evidence, bundle)
    _check_manifest_files(manifest)
    sources = _check_sources(manifest, managed_fileless=managed_fileless)
    for name, expected_hash in native_sources.items():
        source_path = _key('src/DesktopToolbox.Mac/Native/' + name)
        if sources.get(source_path) != expected_hash:
            raise ValueError('Historical native source evidence differs from the source inventory.')
    identity = manifest.get('previewPayloadIdentity')
    if (not isinstance(identity, dict) or set(identity) != {'scheme', 'payloadSha256', 'executableUuid'} or
            identity.get('scheme') != package.preview_identity_helpers().PREVIEW_UUID_SCHEME or
            not re.fullmatch(r'[0-9A-F]{64}', str(identity.get('payloadSha256', ''))) or
            not re.fullmatch(r'[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}', str(identity.get('executableUuid', ''))) or
            identity != summary.get('previewPayloadIdentity')):
        raise ValueError('Preview identity summary differs.')
    try:
        with zipfile.ZipFile(archive) as zipped:
            entries = zipped.infolist()
            if not entries or len(entries) > MAX_ENTRIES or type(summary.get('fileCount')) is not int or summary['fileCount'] != len(entries):
                raise ValueError('Unexpected archive entry count.')
            seen = set(); implied_directories = {}; total = 0
            for item in entries:
                # Our candidate writer emits no ZIP extras. Reject all of them in
                # both headers, including malformed or alternate Unicode names.
                if item.extra:
                    raise ValueError('Unexpected archive extra field.')
                zipped.fp.seek(item.header_offset)
                local_header = zipped.fp.read(30)
                if len(local_header) != 30 or local_header[:4] != b'PK\x03\x04':
                    raise ValueError('Invalid local archive header.')
                if struct.unpack_from('<H', local_header, 28)[0] != 0:
                    raise ValueError('Unexpected local archive extra field.')
                if item.orig_filename != item.filename or any(ord(c) < 32 or ord(c) == 127 for c in item.orig_filename):
                    raise ValueError('Ambiguous or unsafe original archive name.')
                parts = _safe_relative(item.filename)
                if parts[0] != 'NianDeskLink.app' or len(parts) < 2 or item.is_dir() or item.flag_bits & 1 or _private(parts[1:]):
                    raise ValueError('Unsafe archive item.')
                relative = '/'.join(parts[1:]); key = _key(relative)
                relative_parts = parts[1:]
                for index in range(1, len(relative_parts)):
                    directory = '/'.join(relative_parts[:index])
                    directory_key = _key(directory)
                    if directory_key in implied_directories and implied_directories[directory_key] != directory:
                        raise ValueError('Ambiguous archive directory.')
                    implied_directories[directory_key] = directory
                if key in seen or item.file_size > MAX_MEMBER_BYTES:
                    raise ValueError('Duplicate or oversized archive item.')
                seen.add(key); total += item.file_size
                if total > MAX_EXPANDED_BYTES:
                    raise ValueError('Candidate archive expands beyond the safety limit.')
                if item.create_system != 3 or format(item.external_attr >> 16, 'o') != package.archive_mode(Path(relative)):
                    raise ValueError('Archive Unix mode differs from the expected file type.')
                record = tree.get(key)
                if record is None or record[0] != relative or record[2] != item.file_size:
                    raise ValueError('Archive and extracted bundle differ.')
                contents = zipped.read(item)
                if hashlib.sha256(contents).hexdigest() != _hash_file(record[1]):
                    raise ValueError('Archive and extracted bundle content differs.')
            if seen != set(tree):
                raise ValueError('Archive and extracted bundle file sets differ.')
            if directories != implied_directories:
                raise ValueError('Archive and extracted bundle directory sets differ.')
            package.verify_preview_archive(zipped, identity, bundle_id)
    except (OSError, KeyError, TypeError, zipfile.BadZipFile, struct.error) as exc:
        raise ValueError('Invalid candidate archive or preview identity.') from exc
    return {'channel': 'fileless-internal-test', 'archiveSha256': _hash_file(archive), 'fileCount': len(tree),
            'previewPayloadIdentity': identity}


def main():
    parser = argparse.ArgumentParser(description='Read-only consistency check for a new isolated Mac fileless test candidate.')
    parser.add_argument('--project-root', required=True)
    parser.add_argument('--candidate', required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--bridge-sha256', required=True)
    parser.add_argument('--evidence-sha256', required=True)
    parser.add_argument('--managed-fileless', action='store_true', help='Check only the separate managed fileless candidate root.')
    args = parser.parse_args()
    result = verify_candidate(args.project_root, args.candidate, version=args.version,
                              bridge_sha256=args.bridge_sha256, evidence_sha256=args.evidence_sha256,
                              managed_fileless=args.managed_fileless)
    print(json.dumps(result, sort_keys=True))


if __name__ == '__main__':
    main()
