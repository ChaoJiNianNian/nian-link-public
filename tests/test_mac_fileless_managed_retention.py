"""Synthetic-only, disposable tests; never deploy these fake native packages."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import unittest

from tests import test_mac_fileless_candidate_verifier as candidate_tests

ROOT = Path(__file__).resolve().parents[1]
MODULE = ROOT / 'scripts/MacFilelessManagedRetention.psm1'


class MacFilelessManagedRetentionTests(unittest.TestCase):
    def setUp(self):
        if not shutil.which('pwsh'):
            self.skipTest('PowerShell 7 required')
        self.fixture = candidate_tests.MacFilelessCandidateVerifierTests('test_valid_candidate_passes_without_writing')
        self.fixture.setUp()
        self.root = self.fixture.root
        (self.root / 'SYNTHETIC-TEST-ONLY-DO-NOT-DEPLOY.txt').write_text('Synthetic fixture; never deploy.\n')
        self.mode_root = self.root / '.artifacts/mac-package-managed/fileless'

    def tearDown(self):
        if hasattr(self, 'fixture'):
            self.fixture.tearDown()

    def call(self, command):
        env = os.environ | {'TEST_MODULE': str(MODULE), 'TEST_ROOT': str(self.root)}
        script = ("$ErrorActionPreference='Stop'; Import-Module $env:TEST_MODULE -Force; "
                  "$lock=Enter-NianManagedMacFilelessLock -ProjectRoot $env:TEST_ROOT; "
                  "try { " + command + " } finally { Exit-NianManagedMacFilelessLock -Lock $lock }")
        return subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command', script],
                              env=env, capture_output=True, text=True)

    def start(self, run_id):
        return self.call(f"Start-NianManagedMacFileless -ProjectRoot $env:TEST_ROOT -RunId '{run_id}' -Lock $lock")

    def test_reservation_is_persisted_before_candidate_and_pending_blocks_retry(self):
        run_id = 'a' * 32
        first = self.start(run_id)
        self.assertEqual(first.returncode, 0, first.stderr)
        state_path = self.mode_root / 'retention.json'
        state = json.loads(state_path.read_text(encoding='utf-8'))
        self.assertEqual(state['pending'], run_id)
        self.assertFalse((self.mode_root / ('test-' + run_id)).exists())
        before = state_path.read_bytes()
        self.assertNotEqual(self.start('b' * 32).returncode, 0)
        self.assertEqual(before, state_path.read_bytes())

    def test_unowned_contents_and_interrupted_index_refuse_without_changes(self):
        self.mode_root.mkdir(parents=True)
        outsider = self.mode_root / 'unowned.txt'
        outsider.write_text('keep')
        self.assertNotEqual(self.start('c' * 32).returncode, 0)
        self.assertEqual(outsider.read_text(), 'keep')
        self.assertFalse((self.mode_root / 'retention.json').exists())

    def test_missing_pending_maintenance_only_changes_index_when_output_absent(self):
        self.assertEqual(self.start('d' * 32).returncode, 0)
        candidate = self.mode_root / ('test-' + 'd' * 32)
        candidate.mkdir()
        state_path = self.mode_root / 'retention.json'
        before = state_path.read_bytes()
        command = 'Clear-NianManagedMacMissingPending -ProjectRoot $env:TEST_ROOT -Lock $lock'
        self.assertNotEqual(self.call(command).returncode, 0)
        self.assertEqual(before, state_path.read_bytes())
        candidate.rmdir()  # Disposable synthetic fixture only.
        result = self.call(command)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIsNone(json.loads(state_path.read_text(encoding='utf-8'))['pending'])

    def test_maintenance_entry_requires_testing_and_exact_action_without_deletion(self):
        scripts = self.root / 'scripts'; scripts.mkdir()
        for name in ('maintain-mac-fileless-retention.ps1', 'MacFilelessManagedRetention.psm1', 'BuildArtifactLock.psm1'):
            shutil.copy2(ROOT / 'scripts' / name, scripts / name)
        self.assertEqual(self.start('9' * 32).returncode, 0)
        state = self.mode_root / 'retention.json'; original = state.read_bytes()
        sentinel = self.root / 'SYNTHETIC-TEST-ONLY-DO-NOT-DEPLOY.txt'
        def maintain(action, testing):
            env = os.environ | {'TEST_SCRIPT': str(scripts / 'maintain-mac-fileless-retention.ps1'),
                                'TEST_ACTION': action, 'TEST_MODE': str(testing)}
            command = ("$parameters=@{Action=$env:TEST_ACTION; TestingOnly=[bool]::Parse($env:TEST_MODE)}; "
                       "& $env:TEST_SCRIPT @parameters")
            return subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command', command],
                                  env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
        for action, testing in (('missing-pending', False), ('MISSING-PREVIOUS', True), ('unknown', True)):
            with self.subTest(action=action, testing=testing):
                self.assertNotEqual(maintain(action, testing).returncode, 0)
                self.assertEqual(state.read_bytes(), original)
                self.assertTrue(sentinel.is_file())
        result = maintain('MISSING-PENDING', True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIsNone(json.loads(state.read_text(encoding='utf-8'))['pending'])
        self.assertTrue(sentinel.is_file())

    def test_index_duplicate_and_interrupted_next_are_refused_without_mutation(self):
        self.assertEqual(self.start('e' * 32).returncode, 0)
        state = self.mode_root / 'retention.json'
        original = state.read_bytes()
        for key in ('pending', 'Pending'):
            duplicated = original[:-1] + (',"' + key + '":"' + 'e' * 32 + '"}').encode()
            state.write_bytes(duplicated)
            self.assertNotEqual(self.call('Clear-NianManagedMacMissingPending -ProjectRoot $env:TEST_ROOT -Lock $lock').returncode, 0)
            self.assertEqual(state.read_bytes(), duplicated)
        state.write_bytes(original)
        next_file = self.mode_root / 'retention.json.next'
        next_file.write_text('interrupted')
        self.assertNotEqual(self.call('Clear-NianManagedMacMissingPending -ProjectRoot $env:TEST_ROOT -Lock $lock').returncode, 0)
        self.assertEqual(state.read_bytes(), original)
        self.assertEqual(next_file.read_text(), 'interrupted')

    def test_parser_rejects_unicode_normalization_collision_recursively(self):
        valid = "& (Get-Module MacFilelessManagedRetention) { Assert-MacUniqueJson '{\"nested\":{\"é\":1,\"other\":2}}' }"
        collision = "& (Get-Module MacFilelessManagedRetention) { Assert-MacUniqueJson '{\"nested\":{\"é\":1,\"e\\u0301\":1}}' }"
        self.assertEqual(self.call(valid).returncode, 0)
        self.assertNotEqual(self.call(collision).returncode, 0)

    def test_cross_process_lease_contention_does_not_initialize_or_reserve(self):
        base = self.mode_root.parent
        base.mkdir(parents=True)
        env = os.environ | {'TEST_LEASE': str(base / '.build.lock')}
        script = ("$s=[IO.File]::Open($env:TEST_LEASE,'OpenOrCreate','ReadWrite','None'); "
                  "[Console]::Out.WriteLine('LOCKED'); [Console]::Out.Flush(); "
                  "[Console]::In.ReadLine() | Out-Null; $s.Dispose()")
        process = subprocess.Popen(['pwsh', '-NoProfile', '-NonInteractive', '-Command', script],
                                   env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, text=True)
        try:
            self.assertEqual(process.stdout.readline().strip(), 'LOCKED')
            self.assertNotEqual(self.start('f' * 32).returncode, 0)
            self.assertFalse(self.mode_root.exists())
        finally:
            process.communicate(input='release\n', timeout=10)

    def test_registration_requires_verifier_and_keeps_two_snapshots(self):
        for rel in ('scripts/verify_mac_fileless_candidate.py', 'src/DesktopToolbox.Mac/build/package_app.py',
                    'src/DesktopToolbox.Mac/build/preview_identity.py'):
            target = self.root / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / rel, target)
        def complete(run_id):
            fixture = self.fixture
            return self.call(f"Complete-NianManagedMacFileless -ProjectRoot $env:TEST_ROOT -RunId '{run_id}' "
                             f"-Version '{fixture.version}' -BridgeSha256 '{fixture.pin}' "
                             f"-EvidenceSha256 '{fixture.evidence_hash}' -Lock $lock")
        def candidate(run_id):
            target = self.mode_root / ('test-' + run_id)
            shutil.copytree(self.fixture.output, target)
            summary = target / 'package-summary.json'
            data = json.loads(summary.read_text(encoding='utf-8'))
            data['app'] = str(target / 'NianDeskLink.app')
            data['archive'] = str(target / self.fixture.archive.name)
            summary.write_text(json.dumps(data), encoding='utf-8')
            return target
        first, second = '1' * 32, '2' * 32
        self.assertEqual(self.start(first).returncode, 0)
        first_target = candidate(first)
        checker = self.root / 'scripts/verify_mac_fileless_candidate.py'
        original_checker = checker.read_bytes()
        candidate_status = first_target / 'test-build-status.json'
        original_status = candidate_status.read_bytes()
        invalid_status = json.loads(original_status); invalid_status['filelessIsolatedTest'] = False
        candidate_status.write_text(json.dumps(invalid_status), encoding='utf-8')
        state_path = self.mode_root / 'retention.json'
        before = state_path.read_bytes()
        self.assertNotEqual(complete(first).returncode, 0)
        self.assertEqual(state_path.read_bytes(), before)
        candidate_status.write_bytes(original_status)
        checker.write_text('raise SystemExit(0)\n', encoding='utf-8')
        self.assertNotEqual(complete(first).returncode, 0)
        self.assertEqual(state_path.read_bytes(), before)
        checker.write_bytes(original_checker)
        dependency = self.root / 'src/DesktopToolbox.Mac/build/preview_identity.py'
        original_dependency = dependency.read_bytes()
        dependency.write_text('pass\n', encoding='utf-8')
        self.assertNotEqual(complete(first).returncode, 0)
        self.assertEqual(state_path.read_bytes(), before)
        dependency.write_bytes(original_dependency)
        package = self.root / 'src/DesktopToolbox.Mac/build/package_app.py'
        package_original = package.read_bytes()
        package.write_text('raise SystemExit(0)\n', encoding='utf-8')
        self.assertNotEqual(complete(first).returncode, 0)
        self.assertEqual(state_path.read_bytes(), before)
        package.write_bytes(package_original.replace(b'\r\n', b'\n'))
        self.assertEqual(complete(first).returncode, 0)
        self.assertEqual(self.start(second).returncode, 0)
        before_nested = state_path.read_bytes()
        needle = ('"id":"' + first + '"').encode()
        self.assertIn(needle, before_nested)
        duplicated_nested = before_nested.replace(needle, needle + b',' + needle, 1)
        state_path.write_bytes(duplicated_nested)
        self.assertNotEqual(self.call('Clear-NianManagedMacMissingPending -ProjectRoot $env:TEST_ROOT -Lock $lock').returncode, 0)
        self.assertEqual(state_path.read_bytes(), duplicated_nested)
        state_path.write_bytes(before_nested)
        candidate(second)
        result = complete(second)
        self.assertEqual(result.returncode, 0, result.stderr)
        index = self.mode_root / 'retention.json'
        state = json.loads(index.read_text(encoding='utf-8'))
        self.assertEqual((state['current']['id'], state['previous']['id']), (second, first))
        before = index.read_bytes()
        self.assertNotEqual(self.start('3' * 32).returncode, 0)
        self.assertEqual(index.read_bytes(), before)
        damaged = first_target / 'NianDeskLink.app/Contents/MacOS/NianDeskLink.dll'
        original = damaged.read_bytes()
        damaged.write_bytes(b'altered')
        self.assertNotEqual(self.start('4' * 32).returncode, 0)
        self.assertEqual(index.read_bytes(), before)
        damaged.write_bytes(original)
        maintenance = 'Clear-NianManagedMacMissingPrevious -ProjectRoot $env:TEST_ROOT -Lock $lock'
        self.assertNotEqual(self.call(maintenance).returncode, 0)
        self.assertEqual(index.read_bytes(), before)
        shutil.rmtree(first_target)  # Disposable synthetic fixture only.
        current_file = self.mode_root / ('test-' + second) / 'NianDeskLink.app/Contents/MacOS/NianDeskLink.dll'
        current_original = current_file.read_bytes()
        current_file.write_bytes(b'altered')
        self.assertNotEqual(self.call(maintenance).returncode, 0)
        self.assertEqual(index.read_bytes(), before)
        current_file.write_bytes(current_original)
        for name in ('maintain-mac-fileless-retention.ps1', 'MacFilelessManagedRetention.psm1', 'BuildArtifactLock.psm1'):
            shutil.copy2(ROOT / 'scripts' / name, self.root / 'scripts' / name)
        result = subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-File',
                                 str(self.root / 'scripts/maintain-mac-fileless-retention.ps1'),
                                 '-Action', 'MISSING-PREVIOUS', '-TestingOnly'],
                                capture_output=True, text=True, encoding='utf-8', errors='replace')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIsNone(json.loads(index.read_text(encoding='utf-8'))['previous'])
        self.assertEqual(self.start('5' * 32).returncode, 0)


if __name__ == '__main__':
    unittest.main()
