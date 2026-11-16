"""Read-only verification against synthetic fileless Mac package fixtures."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
import tempfile
import unittest
import uuid
import warnings
import zipfile
import zlib
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
package_spec = importlib.util.spec_from_file_location('mac_package_fixture', ROOT / 'src/DesktopToolbox.Mac/build/package_app.py')
package = importlib.util.module_from_spec(package_spec)
package_spec.loader.exec_module(package)
verify_spec = importlib.util.spec_from_file_location('mac_fileless_verify', ROOT / 'scripts/verify_mac_fileless_candidate.py')
verify = importlib.util.module_from_spec(verify_spec)
verify_spec.loader.exec_module(verify)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def macho(kind):
    header = struct.pack('<8I', 0xFEEDFACF, 0x0100000C, 0, kind, 1 if kind == 2 else 0, 24 if kind == 2 else 0, 0, 0)
    return header + (struct.pack('<2I', 27, 24) + uuid.UUID(int=1).bytes if kind == 2 else b'')


class MacFilelessCandidateVerifierTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='nian-mac-fileless-verify-')
        self.root = Path(self.temp.name) / 'project'
        self.output = self.root / '.artifacts/mac-package' / ('test-' + 'a' * 32)
        self.output.mkdir(parents=True)
        self.bundle = self.output / 'NianDeskLink.app'
        self.pin = sha(macho(6))
        self.version = '0.8.4'
        binaries = {
            'Contents/MacOS/NianDeskLink': macho(2), 'Contents/MacOS/DeskLinkMedia': macho(2),
            'Contents/MacOS/libDeskLinkWeb.dylib': macho(6), 'Contents/MacOS/libNianWifiEvidence.dylib': macho(6),
            'Contents/MacOS/NianDeskLink.dll': b'synthetic-managed-app',
            'Contents/MacOS/NianLink.MacCandidateDiscovery.dll': b'synthetic-candidate',
            'Contents/MacOS/DesktopToolbox.DeskLink.dll': b'synthetic-desklink',
            'Contents/MacOS/NianDeskLink.runtimeconfig.json': b'{"runtimeOptions":{}}',
            'Contents/Resources/NianDeskLink.icns': b'icns' + (16).to_bytes(4, 'big') + b'\0' * 8,
            'Contents/Resources/ThirdParty/NuGet-licenses.json': b'[]',
            'Contents/Resources/one.bin': b'x',
            'Contents/Info.plist': plistlib.dumps({'CFBundleIdentifier': 'com.niannian.nianlink.fileless-test',
                'CFBundleVersion': self.version, 'CFBundleShortVersionString': self.version}),
        }
        for name, value in binaries.items():
            path = self.bundle / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(value)
        identity = package.apply_preview_payload_identity(self.bundle)
        files = [{'path': item.relative_to(self.output).as_posix(), 'bytes': item.stat().st_size, 'sha256': sha(item.read_bytes())}
                 for item in sorted(self.bundle.rglob('*')) if item.is_file()]
        native_source_names = ('DeskLinkMedia.swift', 'RealtimeCapture.swift', 'RealtimeCaptureOutput.swift', 'build-media.sh',
                               'media-sources.json', 'DeskLinkWeb.swift', 'build-web.sh')
        source_names = [*package.FILELESS_BUILD_INPUTS, 'scripts/MacFilelessManagedRetention.psm1',
                        'scripts/verify_mac_fileless_candidate.py', 'src/NianLink.MacDiscoveryCandidate/NianLink.MacDiscoveryCandidate.csproj',
                        *('src/DesktopToolbox.Mac/Native/' + name for name in native_source_names)]
        manifest = {'app': 'NianDeskLink', 'version': self.version, 'target': 'osx-arm64', 'selfContained': True,
                    'signed': False, 'notarized': False, 'fileManifestExcludesItself': True,
                    'channel': 'fileless-internal-test', 'bundleIdentifier': 'com.niannian.nianlink.fileless-test',
                    'discoveryBridgeSha256': self.pin, 'files': files,
                    'sources': [{'path': name, 'sha256': sha((name.rsplit('/', 1)[-1] if name.startswith('src/DesktopToolbox.Mac/Native/') else name).encode())}
                                for name in source_names],
                    'previewPayloadIdentity': identity,
                    'previewPayloadIdentityExcludes': ['Contents/Resources/build-manifest.json']}
        self.manifest = self.bundle / 'Contents/Resources/build-manifest.json'
        self.manifest.write_text(json.dumps(manifest), encoding='utf-8')
        source_names = ('DeskLinkMedia.swift', 'RealtimeCapture.swift', 'RealtimeCaptureOutput.swift', 'build-media.sh',
                        'media-sources.json', 'DeskLinkWeb.swift', 'build-web.sh')
        evidence = {'schema': 'mac-native-build-v1', 'label': 'native-' + 'b' * 32,
                    'sourceFiles': [{'name': name, 'sha256': sha(name.encode()).upper()} for name in source_names],
                    'binaries': [{'name': name, 'sha256': sha(binaries['Contents/MacOS/' + name]).upper(), 'architecture': 'arm64'}
                                 for name in ('DeskLinkMedia', 'libDeskLinkWeb.dylib')]}
        self.evidence = self.output / 'native-build-evidence.json'
        self.evidence.write_text(json.dumps(evidence), encoding='utf-8')
        self.evidence_hash = sha(self.evidence.read_bytes())
        status = {'testingOnly': True, 'multiDevice': False, 'sourceRebuildVerified': False, 'macRealDeviceVerified': False,
                  'deskflowIncluded': False, 'signed': False, 'notarized': False, 'discovery': True,
                  'filelessIsolatedTest': True, 'nativeEvidenceSha256': self.evidence_hash.upper()}
        (self.output / 'test-build-status.json').write_text(json.dumps(status), encoding='utf-8')
        self.archive = self.output / f'NianDeskLink-{self.version}-osx-arm64.zip'
        self.make_archive()

    def tearDown(self):
        self.temp.cleanup()

    def make_archive(self, mutate=None):
        with zipfile.ZipFile(self.archive, 'w', compression=zipfile.ZIP_DEFLATED) as out:
            for item in sorted(self.bundle.rglob('*')):
                if item.is_file():
                    relative = item.relative_to(self.output).as_posix()
                    info = zipfile.ZipInfo(relative); info.create_system = 3
                    info.external_attr = int(package.archive_mode(item), 8) << 16
                    if mutate:
                        info = mutate(info)
                    out.writestr(info, item.read_bytes())
        identity = json.loads(self.manifest.read_text(encoding='utf-8'))['previewPayloadIdentity']
        summary = {'app': str(self.bundle), 'archive': str(self.archive), 'fileCount': sum(x.is_file() for x in self.bundle.rglob('*')),
                   'bytes': self.archive.stat().st_size, 'sha256': sha(self.archive.read_bytes()),
                   'signed': False, 'notarized': False, 'previewPayloadIdentity': identity}
        (self.output / 'package-summary.json').write_text(json.dumps(summary), encoding='utf-8')

    def check(self):
        return verify.verify_candidate(self.root, self.output, version=self.version,
                                       bridge_sha256=self.pin, evidence_sha256=self.evidence_hash)

    def test_valid_candidate_passes_without_writing(self):
        before = {p.relative_to(self.root): sha(p.read_bytes()) for p in self.root.rglob('*') if p.is_file()}
        result = self.check()
        self.assertEqual(result['channel'], 'fileless-internal-test')
        self.assertEqual(before, {p.relative_to(self.root): sha(p.read_bytes()) for p in self.root.rglob('*') if p.is_file()})

    def test_managed_root_requires_explicit_mode_and_validates_real_content(self):
        managed = self.root / '.artifacts/mac-package-managed/fileless' / self.output.name
        managed.parent.mkdir(parents=True)
        shutil.copytree(self.output, managed)
        summary_path = managed / 'package-summary.json'
        summary = json.loads(summary_path.read_text(encoding='utf-8'))
        summary['app'] = str(managed / 'NianDeskLink.app')
        summary['archive'] = str(managed / self.archive.name)
        summary_path.write_text(json.dumps(summary), encoding='utf-8')
        options = dict(version=self.version, bridge_sha256=self.pin, evidence_sha256=self.evidence_hash)
        with self.assertRaises(ValueError):
            verify.verify_candidate(self.root, managed, **options)
        with self.assertRaises(ValueError):
            verify.verify_candidate(self.root, self.output, managed_fileless=True, **options)
        before = {p.relative_to(self.root): sha(p.read_bytes()) for p in self.root.rglob('*') if p.is_file()}
        result = verify.verify_candidate(self.root, managed, managed_fileless=True, **options)
        self.assertEqual(result['channel'], 'fileless-internal-test')
        self.assertEqual(before, {p.relative_to(self.root): sha(p.read_bytes()) for p in self.root.rglob('*') if p.is_file()})
        manifest = managed / 'NianDeskLink.app/Contents/Resources/build-manifest.json'
        original_manifest = manifest.read_bytes()
        data = json.loads(original_manifest)
        data['sources'] = [row for row in data['sources'] if row['path'] != 'scripts/MacFilelessManagedRetention.psm1']
        manifest.write_text(json.dumps(data), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'source inventory'):
            verify.verify_candidate(self.root, managed, managed_fileless=True, **options)
        manifest.write_bytes(original_manifest)
        status_path = managed / 'test-build-status.json'
        status = json.loads(status_path.read_text(encoding='utf-8'))
        status['filelessIsolatedTest'] = False
        status_path.write_text(json.dumps(status), encoding='utf-8')
        with self.assertRaises(ValueError):
            verify.verify_candidate(self.root, managed, managed_fileless=True, **options)
        status['filelessIsolatedTest'] = True
        status_path.write_text(json.dumps(status), encoding='utf-8')
        (managed / 'NianDeskLink.app/Contents/MacOS/NianDeskLink.dll').write_bytes(b'tampered')
        with self.assertRaises(ValueError):
            verify.verify_candidate(self.root, managed, managed_fileless=True, **options)

    def test_rejects_extra_missing_or_changed_unpacked_files(self):
        file = self.bundle / 'Contents/MacOS/NianDeskLink.dll'
        original = file.read_bytes()
        for changed in (b'changed', None):
            if changed is None:
                file.unlink()
            else:
                file.write_bytes(changed)
            with self.assertRaises(ValueError):
                self.check()
            file.write_bytes(original)
        (self.bundle / 'Contents/MacOS/unlisted.dll').write_bytes(b'unlisted')
        with self.assertRaises(ValueError):
            self.check()

    def test_rejects_unlisted_empty_directory_even_when_archive_matches_all_files(self):
        (self.bundle / 'Contents/Resources/untracked-empty').mkdir()
        with self.assertRaises(ValueError):
            self.check()

    def test_actual_packager_with_synthetic_inputs_emits_expected_directory_set(self):
        # Disposable fixtures contain fake Mach-O bytes; never deploy them.
        (self.root / 'SYNTHETIC-TEST-ONLY-DO-NOT-DEPLOY.txt').write_text('Synthetic inputs; do not deploy.\n')
        for name in (*package.FILELESS_BUILD_INPUTS, *package.MANAGED_FILELESS_BUILD_INPUTS, 'src/DesktopToolbox.Mac/build/package_app.py',
                     'src/DesktopToolbox.Mac/build/preview_identity.py'):
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / name, path)
        source_names = ('DeskLinkMedia.swift', 'RealtimeCapture.swift', 'RealtimeCaptureOutput.swift',
                        'build-media.sh', 'media-sources.json', 'DeskLinkWeb.swift', 'build-web.sh')
        for name in source_names:
            path = self.root / 'src/DesktopToolbox.Mac/Native' / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(name.encode())
        entry = self.root / 'src/NianLink.MacDiscoveryCandidate/NianLink.MacDiscoveryCandidate.csproj'
        entry.parent.mkdir(parents=True, exist_ok=True); entry.write_text('<Project/>')
        ui = self.root / 'src/DesktopToolbox.Collaboration.Ui'; ui.mkdir(parents=True); (ui / 'synthetic.txt').write_text('SYNTHETIC')
        assets_dir = self.root / 'src/DesktopToolbox.Mac/Assets'; assets_dir.mkdir(parents=True, exist_ok=True)
        (assets_dir / 'NianDeskLink.icns').write_bytes(b'icns' + (16).to_bytes(4, 'big') + b'\0' * 8)
        (assets_dir / 'Info.plist').write_bytes(plistlib.dumps({'CFBundleIdentifier': 'com.niannian.nianlink',
                                                               'CFBundleIconFile': 'NianDeskLink.icns'}))
        third_party = assets_dir / 'ThirdParty'; third_party.mkdir(); (third_party / 'SYNTHETIC.txt').write_text('SYNTHETIC')
        notice = self.root / 'THIRD-PARTY-NOTICES/lucide-1.8.0.txt'; notice.parent.mkdir(); notice.write_text('SYNTHETIC')
        assets = self.root / 'tests/.work/synthetic/project.assets.json'; assets.parent.mkdir(parents=True)
        assets.write_text(json.dumps({'libraries': {}, 'project': {'restore': {'projectPath': str(entry)}}}), encoding='utf-8')
        packages = self.root / 'SYNTHETIC-package-cache'; packages.mkdir()
        output = self.root / '.artifacts/mac-package-managed/fileless' / ('test-' + 'b' * 32)
        output.mkdir(parents=True)
        shutil.copytree(self.bundle / 'Contents/MacOS', output / 'publish')
        result = subprocess.run([os.sys.executable, '-B', str(self.root / 'src/DesktopToolbox.Mac/build/package_app.py'),
                                 '--publish', str(output / 'publish'), '--output', str(output), '--version', self.version,
                                 '--fileless-test', '--managed-fileless', '--assets-file', str(assets), '--discovery-bridge-sha256', self.pin,
                                 '--packages-dir', str(packages)], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        shutil.copy2(self.evidence, output / 'native-build-evidence.json')
        shutil.copy2(self.output / 'test-build-status.json', output / 'test-build-status.json')
        verify.verify_candidate(self.root, output, version=self.version, bridge_sha256=self.pin,
                                evidence_sha256=self.evidence_hash, managed_fileless=True)

    def test_rejects_status_summary_evidence_and_expected_pin_changes(self):
        for file, key, value in ((self.output / 'test-build-status.json', 'signed', True),
                                 (self.output / 'package-summary.json', 'sha256', '0' * 64)):
            original = file.read_bytes(); data = json.loads(original); data[key] = value
            file.write_text(json.dumps(data), encoding='utf-8')
            with self.assertRaises(ValueError): self.check()
            file.write_bytes(original)
        with self.assertRaises(ValueError):
            verify.verify_candidate(self.root, self.output, version=self.version, bridge_sha256='f' * 64, evidence_sha256=self.evidence_hash)
        with self.assertRaises(ValueError):
            verify.verify_candidate(self.root, self.output, version=self.version, bridge_sha256=self.pin, evidence_sha256='f' * 64)

    def test_rejects_missing_source_or_duplicate_source_even_when_archive_matches(self):
        original = self.manifest.read_bytes()
        for transform in (lambda rows: rows.pop(), lambda rows: rows.append(dict(rows[0]))):
            data = json.loads(original); transform(data['sources'])
            self.manifest.write_text(json.dumps(data), encoding='utf-8'); self.make_archive()
            with self.assertRaises(ValueError): self.check()
        self.manifest.write_bytes(original); self.make_archive()

    def test_rejects_native_source_inventory_disagreement_with_historical_evidence(self):
        data = json.loads(self.manifest.read_text(encoding='utf-8'))
        row = next(item for item in data['sources'] if item['path'] == 'src/DesktopToolbox.Mac/Native/DeskLinkMedia.swift')
        row['sha256'] = '0' * 64
        self.manifest.write_text(json.dumps(data), encoding='utf-8'); self.make_archive()
        with self.assertRaisesRegex(ValueError, 'native source'):
            self.check()

    def test_rejects_unknown_preview_identity_scheme_even_if_metadata_agrees(self):
        data = json.loads(self.manifest.read_text(encoding='utf-8'))
        data['previewPayloadIdentity']['scheme'] = 'unknown-scheme'
        self.manifest.write_text(json.dumps(data), encoding='utf-8'); self.make_archive()
        with self.assertRaisesRegex(ValueError, 'identity'):
            self.check()

    def test_rejects_boolean_file_length_even_if_it_equals_one(self):
        data = json.loads(self.manifest.read_text(encoding='utf-8'))
        row = next(item for item in data['files'] if item['path'].endswith('/one.bin'))
        self.assertEqual(row['bytes'], 1)
        row['bytes'] = True
        self.manifest.write_text(json.dumps(data), encoding='utf-8'); self.make_archive()
        with self.assertRaisesRegex(ValueError, 'inventory'):
            self.check()

    def test_rejects_wrong_zip_mode_even_if_summary_is_updated(self):
        def corrupt(info):
            if info.filename.endswith('/NianDeskLink'):
                info.external_attr = 0o100644 << 16
            return info
        self.make_archive(corrupt)
        with self.assertRaises(ValueError): self.check()

    def test_rejects_malformed_zip_or_unsafe_member_even_if_summary_is_updated(self):
        with zipfile.ZipFile(self.archive, 'a') as out:
            out.writestr('NianDeskLink.app/../outside', b'unsafe')
        summary = self.output / 'package-summary.json'
        data = json.loads(summary.read_text()); data['bytes'] = self.archive.stat().st_size; data['sha256'] = sha(self.archive.read_bytes())
        with zipfile.ZipFile(self.archive) as actual: data['fileCount'] = len(actual.infolist())
        summary.write_text(json.dumps(data), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'Unsafe candidate path'): self.check()

    def test_rejects_duplicate_zip_entry_even_if_summary_count_is_updated(self):
        with warnings.catch_warnings():
            warnings.simplefilter('ignore', UserWarning)
            with zipfile.ZipFile(self.archive, 'a') as out:
                out.writestr('NianDeskLink.app/Contents/MacOS/NianDeskLink.dll', b'duplicate')
        summary = self.output / 'package-summary.json'
        data = json.loads(summary.read_text()); data['bytes'] = self.archive.stat().st_size; data['sha256'] = sha(self.archive.read_bytes())
        with zipfile.ZipFile(self.archive) as actual: data['fileCount'] = len(actual.infolist())
        summary.write_text(json.dumps(data), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'Duplicate'): self.check()

    def test_rejects_nul_in_raw_zip_name_and_local_central_mismatch(self):
        def longer_name(info):
            if info.filename.endswith('/one.bin'):
                info.filename += 'ABC'
            return info

        for both_headers in (True, False):
            with self.subTest(both_headers=both_headers):
                self.make_archive(longer_name)
                raw = self.archive.read_bytes()
                old = b'NianDeskLink.app/Contents/Resources/one.binABC'
                new = b'NianDeskLink.app/Contents/Resources/one.bin\x00BC'
                with zipfile.ZipFile(self.archive) as zipped:
                    local = zipped.getinfo(old.decode()).header_offset + 30
                central = raw.rfind(old)
                self.assertEqual(raw[local:local + len(old)], old)
                self.assertGreater(central, local)
                raw = bytearray(raw)
                raw[local:local + len(old)] = new
                if both_headers:
                    raw[central:central + len(old)] = new
                raw = bytes(raw)
                self.archive.write_bytes(raw)
                summary = self.output / 'package-summary.json'
                data = json.loads(summary.read_text(encoding='utf-8'))
                data['sha256'] = sha(raw)
                summary.write_text(json.dumps(data), encoding='utf-8')
                with self.assertRaises(ValueError):
                    self.check()
        self.make_archive()
        raw = self.archive.read_bytes()
        old = b'NianDeskLink.app/Contents/Resources/one.bin'
        new = b'NianDeskLink.app/Contents/Resources/one.ban'
        with zipfile.ZipFile(self.archive) as zipped:
            local = zipped.getinfo(old.decode()).header_offset + 30
        self.assertEqual(raw[local:local + len(old)], old)
        raw = raw[:local] + new + raw[local + len(old):]
        self.archive.write_bytes(raw)
        summary = self.output / 'package-summary.json'
        data = json.loads(summary.read_text(encoding='utf-8'))
        data['sha256'] = sha(raw)
        summary.write_text(json.dumps(data), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'Invalid candidate archive'):
            self.check()

    def test_rejects_unicode_path_extra_and_malformed_local_and_central_extra(self):
        def extra(info):
            if info.filename.endswith('/one.bin'):
                name = info.filename.encode('utf-8')
                payload = b'\x01' + struct.pack('<I', zlib.crc32(name)) + name + b'\x00bad'
                info.extra = struct.pack('<HH', 0x1234, len(payload)) + payload
            return info

        name = b'NianDeskLink.app/Contents/Resources/one.bin'
        for target, corrupt in (('local', 'unicode'), ('central', 'unicode'), ('both', 'unicode'),
                                ('local', 'length'), ('central', 'length')):
            with self.subTest(target=target, corrupt=corrupt):
                self.make_archive(extra)
                raw = bytearray(self.archive.read_bytes())
                with zipfile.ZipFile(self.archive) as zipped:
                    local_name = zipped.getinfo(name.decode()).header_offset + 30
                    central = zipped.start_dir
                while raw[central:central + 4] == b'PK\x01\x02':
                    name_len, extra_len, comment_len = struct.unpack_from('<HHH', raw, central + 28)
                    central_name = central + 46
                    if raw[central_name:central_name + name_len] == name:
                        break
                    central = central_name + name_len + extra_len + comment_len
                else:
                    self.fail('central entry not found')
                self.assertGreater(central_name, local_name)
                offsets = [local_name + len(name), central_name + len(name)]
                for position in offsets if target == 'both' else [offsets[0 if target == 'local' else 1]]:
                    self.assertEqual(raw[position:position + 2], b'\x34\x12')
                    if corrupt == 'unicode':
                        raw[position:position + 2] = b'\x75\x70'
                    else:
                        raw[position + 2:position + 4] = b'\xff\xff'
                raw = bytes(raw)
                self.archive.write_bytes(raw)
                summary = self.output / 'package-summary.json'
                data = json.loads(summary.read_text(encoding='utf-8'))
                data['sha256'] = sha(raw)
                summary.write_text(json.dumps(data), encoding='utf-8')
                with self.assertRaises(ValueError):
                    self.check()

    def test_archive_limits_are_checked_before_preview_helper_reads_payload(self):
        with patch.object(verify, 'MAX_ENTRIES', 1), patch.object(verify.package, 'verify_preview_archive', side_effect=AssertionError('must not run')):
            with self.assertRaises(ValueError): self.check()

    def test_rejects_unexpected_summary_name_plist_or_native_payload(self):
        summary = self.output / 'package-summary.json'; original = summary.read_bytes()
        data = json.loads(original); data['archive'] = str(self.output / 'other.zip')
        summary.write_text(json.dumps(data), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'summary names'): self.check()
        summary.write_bytes(original)
        plist = self.bundle / 'Contents/Info.plist'; original = plist.read_bytes()
        value = plistlib.loads(original); value['CFBundleIdentifier'] = 'com.niannian.nianlink'
        plist.write_bytes(plistlib.dumps(value))
        with self.assertRaisesRegex(ValueError, 'plist identity'): self.check()
        plist.write_bytes(original)
        media = self.bundle / 'Contents/MacOS/DeskLinkMedia'
        media.write_bytes(macho(2) + b'changed')
        with self.assertRaisesRegex(ValueError, 'Native helper'): self.check()

    def test_rejects_summary_paths_pointing_to_other_directory_with_same_names(self):
        summary = self.output / 'package-summary.json'; original = summary.read_bytes()
        for key, name in (('app', 'NianDeskLink.app'), ('archive', self.archive.name)):
            value = json.loads(original); value[key] = str(self.root / 'unrelated' / name)
            summary.write_text(json.dumps(value), encoding='utf-8')
            with self.assertRaisesRegex(ValueError, 'summary'):
                self.check()
        summary.write_bytes(original)

    def test_rejects_duplicate_json_keys_in_all_security_inputs(self):
        paths = [self.output / 'test-build-status.json', self.output / 'package-summary.json',
                 self.evidence, self.manifest]
        keys = ['testingOnly', 'signed', 'schema', 'channel']
        for path, key in zip(paths, keys):
            with self.subTest(name=path.name):
                original = path.read_bytes()
                value = json.loads(original)[key]
                text = original.decode('utf-8').rstrip()
                try:
                    path.write_text(text[:-1] + ', ' + json.dumps(key) + ': ' + json.dumps(value) + '}', encoding='utf-8')
                    if path == self.manifest: self.make_archive()
                    with self.assertRaisesRegex(ValueError, 'duplicate'):
                        self.check()
                finally:
                    path.write_bytes(original)
                    if path == self.manifest: self.make_archive()
        original = self.evidence.read_bytes()
        text = original.decode('utf-8').replace('"name": "DeskLinkMedia.swift"',
                                                '"name": "DeskLinkMedia.swift", "name": "DeskLinkMedia.swift"', 1)
        self.evidence.write_text(text, encoding='utf-8')
        changed_hash = sha(self.evidence.read_bytes())
        status = self.output / 'test-build-status.json'; old_status = status.read_bytes()
        state = json.loads(old_status); state['nativeEvidenceSha256'] = changed_hash.upper()
        status.write_text(json.dumps(state), encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            verify.verify_candidate(self.root, self.output, version=self.version, bridge_sha256=self.pin, evidence_sha256=changed_hash)
        status.write_bytes(old_status); self.evidence.write_bytes(original)
        status = self.output / 'test-build-status.json'; original = status.read_bytes()
        text = original.decode('utf-8')
        for replacement in ('"testingOnly": false, "testingOnly": true',
                            '"testingOnly": true, "testingOnly": false',
                            '"testingOnly": true, "TestingOnly": false'):
            with self.subTest(replacement=replacement):
                status.write_text(text.replace('"testingOnly": true', replacement, 1), encoding='utf-8')
                with self.assertRaisesRegex(ValueError, 'duplicate'):
                    self.check()
        status.write_bytes(original)

    def test_rejects_wrong_project_root_and_unexpected_sibling(self):
        with self.assertRaises(ValueError):
            verify.verify_candidate(self.root / 'other', self.output, version=self.version,
                                    bridge_sha256=self.pin, evidence_sha256=self.evidence_hash)
        (self.output / 'profile.json').write_text('{}')
        with self.assertRaises(ValueError): self.check()

    def test_rejects_junction_inside_bundle(self):
        if os.name != 'nt' or not shutil.which('pwsh'):
            self.skipTest('Windows PowerShell junction required')
        import subprocess
        target = Path(self.temp.name) / 'outside'; target.mkdir()
        (target / 'keep.txt').write_text('keep')
        link = self.bundle / 'Contents/MacOS/linked'
        env = os.environ | {'TEST_LINK': str(link), 'TEST_TARGET': str(target)}
        created = subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command',
                                  'New-Item -ItemType Junction -Path $env:TEST_LINK -Target $env:TEST_TARGET -ErrorAction Stop | Out-Null'],
                                 env=env, capture_output=True)
        if created.returncode:
            self.skipTest('junction unavailable')
        with self.assertRaises(ValueError): self.check()
        self.assertEqual((target / 'keep.txt').read_text(), 'keep')


if __name__ == '__main__':
    unittest.main()
