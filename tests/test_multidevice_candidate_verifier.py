"""Only synthetic files; no real keyboard binary or paired data is executed."""
import hashlib
import importlib.util
import json
import io
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('multi_candidate_verify', ROOT / 'scripts/verify_multidevice_candidate.py')
verify = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify)


def sha(data): return hashlib.sha256(data).hexdigest()


class MultiDeviceCandidateVerifierTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='SYNTHETIC-MULTI-NO-DEPLOY-')
        self.root = Path(self.temp.name) / 'project'
        self.output = self.root / '.artifacts/multidevice-windows-managed/multidevice' / ('test-' + 'a' * 32)
        self.bundle = self.output / '年年互联-多设备测试'
        self.bundle.mkdir(parents=True)
        payload = {
            'App/NianLink.MultiDevice.exe': b'SYNTHETIC exe', 'App/NianLink.MultiDevice.dll': b'SYNTHETIC dll',
            'App/NianLink.MultiDevice.deps.json': b'{}', 'App/NianLink.MultiDevice.runtimeconfig.json': b'{}',
            'App/DisplayBridge.exe': b'SYNTHETIC', 'App/DisplayBridge.dll': b'SYNTHETIC',
            'App/DisplayBridge.deps.json': b'{}', 'App/DisplayBridge.runtimeconfig.json': b'{}',
            'Keyboard/deskflow-core.exe': b'SYNTHETIC core', 'Keyboard/settings/Deskflow.conf': b'\n',
            '启动多设备测试.cmd': (b'@echo off\r\nstart "" "%~dp0App\\NianLink.MultiDevice.exe" '
                                    b'"%LOCALAPPDATA%\\NianLinkMultiDeviceCandidate" "%~dp0Keyboard\\deskflow-core.exe"\r\n'),
            '使用说明.txt': b'SYNTHETIC', '测试说明.txt': b'SYNTHETIC',
        }
        for name, data in payload.items():
            path = self.bundle / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(data)
        self.archive = self.output / 'NianLink-multidevice-windows-internal-test.zip'
        self.status = self.output / 'test-build-status.json'
        self.repack()

    def tearDown(self): self.temp.cleanup()

    def repack(self, *, mac=False):
        files = [p for p in self.bundle.rglob('*') if p.is_file()]
        with zipfile.ZipFile(self.archive, 'w', compression=zipfile.ZIP_DEFLATED) as out:
            for p in files: out.write(p, p.relative_to(self.output).as_posix())
        rows = [{'path': p.relative_to(self.bundle).as_posix(), 'bytes': p.stat().st_size, 'sha256': sha(p.read_bytes()).upper()}
                for p in files]
        value = {'testingOnly': True, 'realDeviceVerified': False, 'sourceRebuildVerified': False, 'signed': False,
                 'macClientIncluded': mac, 'keyboardCoreSha256': sha((self.bundle/'Keyboard/deskflow-core.exe').read_bytes()).upper(),
                 'archiveSha256': sha(self.archive.read_bytes()).upper(), 'files': rows}
        self.status.write_text(json.dumps(value), encoding='utf-8')
        self.expected_status = sha(self.status.read_bytes())

    def check(self, **kwargs):
        with patch.object(verify, '_verify_fixed_keyboard', return_value=None), patch.object(verify, '_verify_mac_export', return_value=None):
            return verify.verify_candidate(self.root, self.output, status_sha256=self.expected_status, **kwargs)

    def test_synthetic_valid_no_write_and_independent_status_required(self):
        before = {p.relative_to(self.root): sha(p.read_bytes()) for p in self.root.rglob('*') if p.is_file()}
        self.check()
        self.assertEqual(before, {p.relative_to(self.root): sha(p.read_bytes()) for p in self.root.rglob('*') if p.is_file()})
        with self.assertRaises(ValueError):
            verify.verify_candidate(self.root, self.output, status_sha256='f' * 64)

    def test_changed_bundle_or_archive_status_and_privacy_are_rejected(self):
        file = self.bundle / 'App/NianLink.MultiDevice.dll'; old = file.read_bytes()
        file.write_bytes(b'changed')
        with self.assertRaises(ValueError): self.check()
        file.write_bytes(old)
        (self.bundle / 'profile').mkdir(); (self.bundle / 'profile/private.json').write_text('SYNTHETIC')
        self.repack()
        with self.assertRaises(ValueError): self.check()

    def test_duplicate_keys_bool_length_and_bad_path_rejected(self):
        original = self.status.read_bytes()
        self.status.write_bytes(original.rstrip()[:-1] + b',"testingOnly":true}')
        self.expected_status = sha(self.status.read_bytes())
        with self.assertRaises(ValueError): self.check()
        data = json.loads(original); data['files'][0]['bytes'] = True
        self.status.write_text(json.dumps(data), encoding='utf-8'); self.expected_status = sha(self.status.read_bytes())
        with self.assertRaises(ValueError): self.check()
        data = json.loads(original); data['files'][0]['path'] = '../outside'
        self.status.write_text(json.dumps(data), encoding='utf-8'); self.expected_status = sha(self.status.read_bytes())
        with self.assertRaises(ValueError): self.check()

    def test_fixed_validator_cannot_be_overridden_by_external_parameter(self):
        with self.assertRaises(TypeError):
            verify.verify_candidate(self.root, self.output, status_sha256=self.expected_status, keyboard_validator=lambda *_: None)

    def test_extra_empty_directory_must_exist_in_zip_and_private_marker_is_narrow(self):
        (self.bundle/'App/unlisted-empty').mkdir()
        with self.assertRaises(ValueError): self.check()
        (self.bundle/'App/unlisted-empty').rmdir()
        (self.bundle/'App/Deskflow.conf').write_text('SYNTHETIC')
        self.repack()
        with self.assertRaisesRegex(ValueError, 'Private'): self.check()

    @unittest.skipUnless(os.name == 'nt' and shutil.which('pwsh'), 'Windows PowerShell required')
    def test_actual_compress_archive_with_empty_directory_is_accepted(self):
        (self.bundle/'App/synthetic-empty').mkdir()
        env = os.environ | {'TEST_BUNDLE': str(self.bundle), 'TEST_ARCHIVE': str(self.archive)}
        result = subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command',
                                 'Compress-Archive -LiteralPath $env:TEST_BUNDLE -DestinationPath $env:TEST_ARCHIVE -Force'],
                                env=env, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr.decode('utf-8', errors='replace'))
        status = json.loads(self.status.read_text(encoding='utf-8')); status['archiveSha256'] = sha(self.archive.read_bytes()).upper()
        self.status.write_text(json.dumps(status), encoding='utf-8'); self.expected_status = sha(self.status.read_bytes())
        self.check()

    def test_actual_private_store_names_are_refused_even_in_self_consistent_package(self):
        names = ('multi-device-trust.bin', 'multi-device-trust.bin.' + 'a'*32 + '.tmp',
                 'pending-prepare.bin', 'pending-grant.bin', 'device-settings.bin', 'peer-invitation.bin',
                 'peer-finalizing.bin', 'peer-promotion.bin', 'peer-reservation.bin', 'peer-retired.bin',
                 'peer-ready-evidence.bin', 'peer-pending-invitation.bin', 'peer-aborted-' + 'a'*32 + '.bin',
                 'peer-completed-' + 'a'*32 + '.bin', 'peer-bindings.json', 'server.conf',
                 '.multi-device-trust.lock', '.peer-invitation.lock', '.session.lock', '.settings.lock',
                 '.identity.lock', 'identity.json', 'trust.json')
        for name in names:
            with self.subTest(name=name):
                path = self.bundle/'App'/name; path.write_bytes(b'SYNTHETIC')
                self.repack()
                try:
                    with self.assertRaisesRegex(ValueError, 'Private'): self.check()
                finally: path.unlink()  # Synthetic fixture only.

    def test_unknown_status_or_file_inventory_fields_are_rejected(self):
        original = json.loads(self.status.read_text(encoding='utf-8'))
        for nested in (False, True):
            data = json.loads(json.dumps(original))
            (data['files'][0] if nested else data)['unreviewed'] = 'SYNTHETIC'
            self.status.write_text(json.dumps(data), encoding='utf-8'); self.expected_status = sha(self.status.read_bytes())
            with self.subTest(nested=nested), self.assertRaises(ValueError): self.check()

    def test_bad_status_mismatches_and_both_duplicate_orders_are_rejected(self):
        original = self.status.read_bytes()
        for suffix in (b',"testingOnly":true}', b',"testingOnly":false}', b',"TestingOnly":true}'):
            self.status.write_bytes(original.rstrip()[:-1] + suffix); self.expected_status = sha(self.status.read_bytes())
            with self.subTest(suffix=suffix), self.assertRaisesRegex(ValueError, 'duplicate|Duplicate'):
                self.check()
        for key, value in (('testingOnly', 1), ('signed', 0), ('macClientIncluded', 'false'), ('archiveSha256', '0'*64)):
            status = json.loads(original); status[key] = value
            self.status.write_text(json.dumps(status), encoding='utf-8'); self.expected_status = sha(self.status.read_bytes())
            with self.subTest(key=key), self.assertRaises(ValueError): self.check()

    def test_zip_raw_nul_and_local_name_mismatch_are_rejected(self):
        base = self.archive.read_bytes()
        with zipfile.ZipFile(self.archive) as zipped:
            item = next(i for i in zipped.infolist() if i.filename.endswith('/App/DisplayBridge.exe'))
            local = item.header_offset + 30; raw_name = item.filename.encode('utf-8')
        self.assertEqual(base[local:local+len(raw_name)], raw_name)
        for replacement in (raw_name[:-1]+b'\0', raw_name[:-1]+b'X'):
            data = base[:local] + replacement + base[local+len(raw_name):]
            self.archive.write_bytes(data)
            status = json.loads(self.status.read_text(encoding='utf-8')); status['archiveSha256'] = sha(data).upper()
            self.status.write_text(json.dumps(status), encoding='utf-8'); self.expected_status = sha(self.status.read_bytes())
            with self.subTest(replacement=replacement[-1:]), self.assertRaises(ValueError): self.check()
        central = base.rfind(raw_name)
        self.assertGreater(central, local)
        nul_name = raw_name[:-1] + b'\0'
        data = bytearray(base); data[local:local+len(raw_name)] = nul_name; data[central:central+len(raw_name)] = nul_name
        self.archive.write_bytes(data)
        status = json.loads(self.status.read_text(encoding='utf-8')); status['archiveSha256'] = sha(data).upper()
        self.status.write_text(json.dumps(status), encoding='utf-8'); self.expected_status = sha(self.status.read_bytes())
        with self.assertRaises(ValueError): self.check()

    def test_payload_change_during_external_validation_is_rejected(self):
        def mutate(*_): (self.bundle/'App/DisplayBridge.dll').write_bytes(b'changed')
        with patch.object(verify, '_verify_fixed_keyboard', side_effect=mutate):
            with self.assertRaisesRegex(ValueError, 'changed during'):
                verify.verify_candidate(self.root, self.output, status_sha256=self.expected_status)

    def test_unix_special_file_types_in_zip_are_rejected(self):
        for kind in (stat.S_IFIFO, stat.S_IFCHR, stat.S_IFSOCK):
            with self.subTest(kind=kind):
                with zipfile.ZipFile(self.archive, 'w') as out:
                    for file in self.bundle.rglob('*'):
                        if not file.is_file(): continue
                        info = zipfile.ZipInfo(file.relative_to(self.output).as_posix())
                        if file.name == 'DisplayBridge.dll':
                            info.create_system = 3; info.external_attr = (kind | 0o644) << 16
                        out.writestr(info, file.read_bytes())
                data = json.loads(self.status.read_text(encoding='utf-8')); data['archiveSha256'] = sha(self.archive.read_bytes()).upper()
                self.status.write_text(json.dumps(data), encoding='utf-8'); self.expected_status = sha(self.status.read_bytes())
                with self.assertRaises(ValueError): self.check()

    def test_synthetic_mac_requires_explicit_version_and_matching_manifest(self):
        directory = self.bundle/'App/MacClient'; directory.mkdir()
        nested = io.BytesIO()
        with zipfile.ZipFile(nested, 'w') as out: out.writestr('NianDeskLink.app/Contents/SYNTHETIC', b'SYNTHETIC')
        archive = directory/'NianDeskLink-macOS-arm64.zip'
        with zipfile.ZipFile(archive, 'w') as out:
            out.writestr('NianDeskLink-1.2.3-osx-arm64.zip', nested.getvalue())
            out.writestr('deskflow-1.27.0-macos-arm64.dmg', b'SYNTHETIC')
        manifest = {'file': archive.name, 'version': '1.2.3', 'mode': 'multi-device-test', 'channel': 'internal-test',
                    'includesDeskflow': True, 'notarized': False, 'bytes': archive.stat().st_size, 'sha256': sha(archive.read_bytes())}
        (directory/'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
        self.repack(mac=True)
        with self.assertRaises(ValueError): self.check()
        with self.assertRaises(ValueError): self.check(mac_version='1.2.4')
        with patch.object(verify, '_verify_fixed_keyboard'), patch.object(verify, '_verify_mac_export') as mac:
            verify.verify_candidate(self.root, self.output, status_sha256=self.expected_status, mac_version='1.2.3')
            mac.assert_called_once_with(self.root, directory, '1.2.3')

    def test_bogus_fixed_keyboard_module_cannot_be_executed(self):
        module = self.root/'scripts/WindowsTestKeyboardPayload.psm1'; module.parent.mkdir()
        module.write_text('throw "SYNTHETIC"', encoding='utf-8')
        with patch.object(verify.subprocess, 'run', side_effect=AssertionError('untrusted code must not run')):
            with self.assertRaisesRegex(ValueError, 'differs'):
                verify._verify_fixed_keyboard(self.root, self.bundle, self.archive)

    def test_real_fixed_keyboard_validator_rejects_synthetic_payload_without_writes(self):
        module = self.root/'scripts/WindowsTestKeyboardPayload.psm1'; module.parent.mkdir()
        shutil.copy2(ROOT/'scripts/WindowsTestKeyboardPayload.psm1', module)
        before = {p.relative_to(self.root): sha(p.read_bytes()) for p in self.root.rglob('*') if p.is_file()}
        with self.assertRaisesRegex(ValueError, 'Fixed keyboard validation failed'):
            verify.verify_candidate(self.root, self.output, status_sha256=self.expected_status)
        self.assertEqual(before, {p.relative_to(self.root): sha(p.read_bytes()) for p in self.root.rglob('*') if p.is_file()})

    def test_bogus_mac_validator_is_refused_before_execution(self):
        scripts = self.root/'scripts'; scripts.mkdir()
        for name in ('verify-mac-export-test.py', 'build-mac-export-test.py'):
            (scripts/name).write_text('raise SystemExit(0)', encoding='utf-8')
        with patch.object(verify.subprocess, 'run', side_effect=AssertionError('untrusted code must not run')):
            with self.assertRaisesRegex(ValueError, 'differs'):
                verify._verify_mac_export(self.root, self.bundle, '1.2.3')

    def test_limits_are_checked_before_external_validators(self):
        with patch.object(verify, 'MAX_ENTRIES', 1), \
             patch.object(verify, '_verify_fixed_keyboard', side_effect=AssertionError('must not run')):
            with self.assertRaises(ValueError):
                verify.verify_candidate(self.root, self.output, status_sha256=self.expected_status)

    @unittest.skipUnless(os.name == 'nt' and shutil.which('pwsh'), 'Windows junction required')
    def test_link_inside_bundle_refuses_without_touching_target(self):
        target = Path(self.temp.name)/'protected'; target.mkdir(); (target/'sentinel').write_text('keep')
        link = self.bundle/'App/linked'; env = os.environ | {'TEST_LINK': str(link), 'TEST_TARGET': str(target)}
        created = subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command',
                                  'New-Item -ItemType Junction -Path $env:TEST_LINK -Target $env:TEST_TARGET -ErrorAction Stop | Out-Null'],
                                 env=env, capture_output=True)
        if created.returncode: self.skipTest('junction unavailable')
        with self.assertRaises(ValueError): self.check()
        self.assertEqual((target/'sentinel').read_text(), 'keep')


if __name__ == '__main__': unittest.main()
