"""Managed discovery-package retention, exercised only in temporary projects."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
MODULE = ROOT / 'scripts/DiscoveryManagedRetention.psm1'
LOCK = ROOT / 'scripts/BuildArtifactLock.psm1'
PWSH = shutil.which('pwsh')
spec = importlib.util.spec_from_file_location('retention_policy_fixture', ROOT / 'scripts/discovery_portable_policy.py')
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)
ARCHIVES = {
    'discovery': 'NianLink-discovery-portable-windows-internal-test.zip',
    'fileless': 'NianLink-fileless-portable-windows-internal-test.zip',
    'diagnostics': 'NianLink-fileless-diagnostics-windows-internal-test.zip',
}


@unittest.skipUnless(PWSH and os.name == 'nt', 'Windows PowerShell 7 required')
class DiscoveryManagedRetentionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='nian-managed-retention-')
        self.root = Path(self.temp.name) / 'project'
        self.root.mkdir()
        (self.root / 'scripts').mkdir()
        shutil.copyfile(ROOT / 'scripts/discovery_portable_policy.py', self.root / 'scripts/discovery_portable_policy.py')
        self.sources = {
            'src/NianLink.CandidateDiscovery/NianLink.CandidateDiscovery.csproj': b'project',
            'src/NianLink.CandidateDiscovery/CandidateDiscoveryRun.cs': b'candidate',
            'src/NianLink.MultiDevice/NianLink.MultiDevice.csproj': b'project',
            'src/NianLink.DiscoveryPortable/NianLink.DiscoveryPortable.csproj': b'project',
            'src/DesktopToolbox.DeskLink/DiscoveryReviewLease.cs': b'shared',
            'src/NianLink.MultiDevice.Shared/PeerControlOwner.cs': b'owner',
        }
        for path, content in self.sources.items():
            target = self.root / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)

    def tearDown(self):
        self.temp.cleanup()

    def test_builder_opt_in_reserves_before_output_and_commits_after_policy(self):
        script = (ROOT / 'scripts/build-multidevice-windows-test.ps1').read_text(encoding='utf-8')
        self.assertIn('[switch]$ManagedRetention', script)
        self.assertIn("$PSBoundParameters.ContainsKey('ManagedRetention')", script)
        self.assertIn('if ($useManagedRetention -and -not $DiscoveryPortable)', script)
        self.assertIn('Enter-NianManagedDiscoveryLock -ProjectRoot $root', script)
        self.assertIn('Exit-NianManagedDiscoveryLock -Lock $lock', script)
        reserve = script.index('Start-NianManagedDiscovery -ProjectRoot')
        mkdir = script.index('New-Item -ItemType Directory -Path $output')
        final_policy = script.index('discovery_portable_policy.py')
        complete = script.index('Complete-NianManagedDiscovery -ProjectRoot')
        self.assertLess(reserve, mkdir)
        self.assertLess(final_policy, complete)

    def invoke(self, operation, mode='fileless', run_id='a' * 32):
        script = ("$ErrorActionPreference='Stop'; Import-Module $env:TEST_MODULE -ErrorAction Stop; "
                  "$lock=Enter-NianManagedDiscoveryLock -ProjectRoot $env:TEST_ROOT; "
                  "try { if($env:TEST_OP -eq 'start') { Start-NianManagedDiscovery -ProjectRoot $env:TEST_ROOT -Mode $env:TEST_MODE -RunId $env:TEST_RUN -Lock $lock } "
                  "elseif($env:TEST_OP -eq 'recover') { Clear-NianManagedMissingPrevious -ProjectRoot $env:TEST_ROOT -Mode $env:TEST_MODE -Lock $lock } "
                  "elseif($env:TEST_OP -eq 'recover-pending') { Clear-NianManagedMissingPending -ProjectRoot $env:TEST_ROOT -Mode $env:TEST_MODE -Lock $lock } "
                  "else { Complete-NianManagedDiscovery -ProjectRoot $env:TEST_ROOT -Mode $env:TEST_MODE -RunId $env:TEST_RUN -Lock $lock } } "
                  "finally { Exit-NianManagedDiscoveryLock -Lock $lock }")
        env = os.environ | {'TEST_ROOT': str(self.root), 'TEST_MODULE': str(MODULE), 'TEST_LOCK': str(LOCK),
                            'TEST_OP': operation, 'TEST_MODE': mode, 'TEST_RUN': run_id}
        return subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', script], env=env,
                              capture_output=True, text=True, encoding='utf-8', errors='replace')

    def mode_root(self, mode='fileless'):
        return self.root / '.artifacts/discovery-portable-managed' / mode

    def prepare(self, run_id, mode='fileless'):
        target = self.mode_root(mode) / ('test-' + run_id)
        target.mkdir()
        name = '年年互联-发现便携测试' if mode == 'discovery' else '年年互联-免文件便携测试'
        bundle = target / name
        files = {
            'App/NianLink.DiscoveryPortable.exe': b'exe',
            'App/NianLink.DiscoveryPortable.dll': b'entry',
            'App/NianLink.DiscoveryPortable.runtimeconfig.json': b'{}',
            'App/NianLink.CandidateDiscovery.dll': b'dll',
            'App/NianLink.DiscoveryPortable.deps.json': json.dumps({'targets': {'test': {'NianLink.CandidateDiscovery/1.0.0': {'runtime': {'NianLink.CandidateDiscovery.dll': {}}}}}}).encode(),
            'Keyboard/deskflow-core.exe': b'engine', 'Keyboard/settings/Deskflow.conf': b'portable-marker',
            'App/discovery-portable-id.txt': run_id.encode('ascii'),
            '笔记本-D盘启动.cmd': policy.expected_launcher(run_id, 'D').encode('ascii'),
            '台式机-H盘启动.cmd': policy.expected_launcher(run_id, 'H').encode('ascii'),
            'App/demo.txt': ('synthetic-payload-' + run_id).encode(),
        }
        rows = []
        for path, payload in files.items():
            output = bundle / path
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_bytes(payload)
            rows.append({'path': path, 'bytes': len(payload), 'sha256': hashlib.sha256(payload).hexdigest().upper()})
        archive = target / ARCHIVES[mode]
        with zipfile.ZipFile(archive, 'w') as zf:
            for path, payload in files.items():
                zf.writestr(name + '/' + path, payload)
        status = {'testingOnly': True, 'channel': 'discovery-portable-v1', 'runId': run_id,
                  'archiveSha256': hashlib.sha256(archive.read_bytes()).hexdigest().upper(),
                  'files': rows,
                  'sources': [{'path': path, 'bytes': len(payload), 'sha256': hashlib.sha256(payload).hexdigest().upper()}
                              for path, payload in self.sources.items()],
                  'filelessIsolatedTest': mode != 'discovery', 'pairingDiagnostics': mode == 'diagnostics'}
        (target / 'test-build-status.json').write_text(json.dumps(status), encoding='utf-8')
        return target

    def success(self, run_id, mode='fileless'):
        result = self.invoke('start', mode, run_id)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.prepare(run_id, mode)
        result = self.invoke('complete', mode, run_id)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_two_successes_then_refuses_third_without_creating_output(self):
        self.success('a' * 32)
        self.success('b' * 32)
        before = (self.mode_root() / 'retention.json').read_bytes()
        failed = self.invoke('start', run_id='c' * 32)
        self.assertNotEqual(failed.returncode, 0)
        self.assertEqual(before, (self.mode_root() / 'retention.json').read_bytes())
        self.assertFalse((self.mode_root() / ('test-' + 'c' * 32)).exists())
        self.assertFalse((self.root / 'test-profiles').exists())

    def test_ambiguous_index_object_keys_are_rejected_without_rewriting(self):
        self.success('a' * 32)
        index = self.mode_root() / 'retention.json'
        valid = json.loads(index.read_text(encoding='utf-8'))
        record = valid['current']
        encode = lambda value: json.dumps(value, ensure_ascii=True, separators=(',', ':'))
        current = encode(record)
        prefix = '{"schema":"nian-managed-discovery-v1","mode":"fileless","current":' + current + ',"previous":null,'
        cases = {
            'identical': prefix + '"pending":null,"pending":null}',
            'different': prefix + '"pending":"' + 'f' * 32 + '","pending":null}',
            'escaped': prefix + '"pend\\u0069ng":null,"pending":null}',
            'case': prefix + '"pending":null,"PENDING":null}',
            'unicode_normalization': prefix + '"pending":null,"é":null,"e\\u0301":null}',
            'nested_array_normalization': prefix + '"pending":[{"holder":{"é":1,"e\\u0301":2}}]}',
            'nested_array_case': prefix + '"pending":[{"holder":{"Key":1,"key":2}}]}',
        }
        for field in ('id', 'manifestHash', 'archiveHash'):
            wrong = 'b' * 32 if field == 'id' else 'B' * 64
            other = ','.join(encode(k) + ':' + encode(v) for k, v in record.items())
            for label, first in (('same', record[field]), ('different', wrong)):
                repeated = '{' + encode(field) + ':' + encode(first) + ',' + other + '}'
                cases['record_' + field + '_' + label] = ('{"schema":"nian-managed-discovery-v1","mode":"fileless","current":' + repeated + ',"previous":null,"pending":null}')
        for label, raw in cases.items():
            with self.subTest(label=label):
                original = raw.encode('utf-8')
                index.write_bytes(original)
                result = self.invoke('start', run_id='b' * 32)
                self.assertNotEqual(result.returncode, 0, label)
                self.assertIn('Ambiguous managed JSON object key', result.stderr)
                self.assertEqual(index.read_bytes(), original)
                self.assertFalse((self.mode_root() / 'retention.json.next').exists())
                self.assertFalse((self.mode_root() / ('test-' + 'b' * 32)).exists())

    def test_ambiguous_candidate_status_is_rejected_without_committing(self):
        self.success('a' * 32)
        self.assertEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        candidate = self.prepare('b' * 32)
        status = candidate / 'test-build-status.json'
        index = self.mode_root() / 'retention.json'
        pending = index.read_bytes()
        original = status.read_text(encoding='utf-8')
        cases = {
            'same_top': original.replace('"testingOnly": true', '"testingOnly": true, "testingOnly": true', 1),
            'different_top': original.replace('"testingOnly": true', '"testingOnly": false, "testingOnly": true', 1),
            'escaped_top': original.replace('"testingOnly": true', '"testing\\u004fnly": false, "testingOnly": true', 1),
            'same_nested': original.replace('"path": ', '"path": "App/NianLink.DiscoveryPortable.exe", "path": ', 1),
            'different_nested': original.replace('"path": ', '"path": "wrong", "path": ', 1),
            'nested_unicode': original.replace('"testingOnly": true', '"extra": [{"é": 1, "e\\u0301": 2}], "testingOnly": true', 1),
        }
        for label, value in cases.items():
            with self.subTest(label=label):
                self.assertNotEqual(value, original)
                index.write_bytes(pending)
                data = value.encode('utf-8')
                status.write_bytes(data)
                result = self.invoke('complete', run_id='b' * 32)
                self.assertNotEqual(result.returncode, 0, label)
                self.assertIn('Ambiguous managed JSON object key', result.stderr)
                self.assertEqual(index.read_bytes(), pending)
                self.assertEqual(status.read_bytes(), data)
                self.assertFalse((self.mode_root() / 'retention.json.next').exists())

    def test_oversized_index_and_status_are_refused_without_rewriting(self):
        self.success('a' * 32)
        index = self.mode_root() / 'retention.json'
        original = index.read_bytes()
        oversized = original + b' ' * (8 * 1024 * 1024 + 1)
        index.write_bytes(oversized)
        result = self.invoke('start', run_id='b' * 32)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Managed JSON exceeds size limit', result.stderr)
        self.assertEqual(index.read_bytes(), oversized)
        index.write_bytes(original)
        self.assertEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        target = self.prepare('b' * 32)
        status = target / 'test-build-status.json'
        oversized_status = status.read_bytes() + b' ' * (8 * 1024 * 1024 + 1)
        status.write_bytes(oversized_status)
        pending = index.read_bytes()
        result = self.invoke('complete', run_id='b' * 32)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Managed JSON exceeds size limit', result.stderr)
        self.assertEqual(index.read_bytes(), pending)
        self.assertEqual(status.read_bytes(), oversized_status)

    def test_recorded_status_requires_unambiguous_bounded_json(self):
        self.success('a' * 32)
        index = self.mode_root() / 'retention.json'
        status = self.mode_root() / ('test-' + 'a' * 32) / 'test-build-status.json'
        original = status.read_text(encoding='utf-8')
        state = json.loads(index.read_bytes())
        cases = {
            'duplicate': (original.replace('"testingOnly": true', '"testingOnly": false, "testingOnly": true', 1).encode('utf-8'),
                          'Ambiguous managed JSON object key'),
            'oversized': (original.encode('utf-8') + b' ' * (8 * 1024 * 1024 + 1),
                          'Managed JSON exceeds size limit'),
        }
        for label, (payload, message) in cases.items():
            with self.subTest(label=label):
                status.write_bytes(payload)
                state['current']['manifestHash'] = hashlib.sha256(payload).hexdigest().upper()
                stored_index = json.dumps(state).encode('utf-8')
                index.write_bytes(stored_index)
                result = self.invoke('start', run_id='b' * 32)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(message, result.stderr)
                self.assertEqual(index.read_bytes(), stored_index)
                self.assertEqual(status.read_bytes(), payload)
                self.assertFalse((self.mode_root() / 'retention.json.next').exists())

    def test_failed_validation_retains_pending_and_existing_version(self):
        self.success('a' * 32)
        self.assertEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        target = self.prepare('b' * 32)
        (target / ARCHIVES['fileless']).write_bytes(b'changed')
        failed = self.invoke('complete', run_id='b' * 32)
        self.assertNotEqual(failed.returncode, 0)
        self.assertNotEqual(self.invoke('start', run_id='c' * 32).returncode, 0)
        self.assertTrue((self.mode_root() / ('test-' + 'a' * 32)).exists())

    def test_failed_or_interrupted_reservation_does_not_allow_another(self):
        self.assertEqual(self.invoke('start', run_id='a' * 32).returncode, 0)
        self.assertNotEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        self.assertFalse((self.mode_root() / ('test-' + 'b' * 32)).exists())

    def test_modes_and_unmanaged_historical_directory_are_separate(self):
        old = self.root / '.artifacts/discovery-portable' / ('test-' + 'f' * 32)
        old.mkdir(parents=True)
        (old / 'old.txt').write_text('existing test')
        self.success('a' * 32, 'fileless')
        self.success('b' * 32, 'diagnostics')
        self.success('c' * 32, 'discovery')
        self.assertEqual((old / 'old.txt').read_text(), 'existing test')

    def test_untracked_output_and_interrupted_index_are_refused(self):
        self.success('a' * 32)
        (self.mode_root() / ('test-' + 'f' * 32)).mkdir()
        self.assertNotEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        other = self.mode_root('diagnostics')
        self.assertEqual(self.invoke('start', 'diagnostics', 'c' * 32).returncode, 0)
        (other / 'retention.json.next').write_text('{}')
        self.assertNotEqual(self.invoke('complete', 'diagnostics', 'c' * 32).returncode, 0)

    def test_modified_successful_archive_and_unowned_directory_are_refused(self):
        self.success('a' * 32)
        (self.mode_root() / ('test-' + 'a' * 32) / ARCHIVES['fileless']).write_bytes(b'corrupted')
        self.assertNotEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        unmanaged = self.mode_root('diagnostics')
        unmanaged.mkdir(parents=True)
        (unmanaged / 'keep.txt').write_text('untouched')
        self.assertNotEqual(self.invoke('start', 'diagnostics', 'c' * 32).returncode, 0)
        self.assertEqual((unmanaged / 'keep.txt').read_text(), 'untouched')

    def test_modified_extracted_bundle_blocks_future_build(self):
        self.success('a' * 32)
        target = self.mode_root() / ('test-' + 'a' * 32) / '年年互联-免文件便携测试/App/demo.txt'
        target.write_bytes(b'changed')
        self.assertNotEqual(self.invoke('start', run_id='b' * 32).returncode, 0)

    def test_extra_untracked_package_root_material_blocks_future_build(self):
        self.success('a' * 32)
        extra = self.mode_root() / ('test-' + 'a' * 32) / 'profile'
        extra.mkdir()
        (extra / 'identity.json').write_text('synthetic credential')
        self.assertNotEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        self.assertTrue((extra / 'identity.json').exists())

    def test_manifest_mode_flags_must_be_actual_booleans(self):
        self.assertEqual(self.invoke('start').returncode, 0)
        target = self.prepare('a' * 32)
        status = target / 'test-build-status.json'
        value = json.loads(status.read_text())
        value['filelessIsolatedTest'] = 'true'
        status.write_text(json.dumps(value))
        self.assertNotEqual(self.invoke('complete').returncode, 0)

    def test_corrupt_zip_with_matching_manifest_hash_is_not_committed(self):
        self.assertEqual(self.invoke('start').returncode, 0)
        target = self.prepare('a' * 32)
        archive = target / ARCHIVES['fileless']
        archive.write_bytes(b'not a zip')
        status = target / 'test-build-status.json'
        value = json.loads(status.read_text())
        value['archiveSha256'] = hashlib.sha256(archive.read_bytes()).hexdigest().upper()
        status.write_text(json.dumps(value))
        self.assertNotEqual(self.invoke('complete').returncode, 0)

    def test_policy_failure_never_commits_candidate(self):
        self.success('a' * 32)
        self.assertEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        target = self.prepare('b' * 32)
        index = self.mode_root() / 'retention.json'
        before = index.read_bytes()
        status = target / 'test-build-status.json'
        value = json.loads(status.read_text(encoding='utf-8'))
        value['sources'] = []
        status.write_text(json.dumps(value), encoding='utf-8')
        self.assertNotEqual(self.invoke('complete', run_id='b' * 32).returncode, 0)
        self.assertEqual(before, index.read_bytes())

    def test_replaced_policy_cannot_approve_changed_source(self):
        self.success('a' * 32)
        self.assertEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        self.prepare('b' * 32)
        index = self.mode_root() / 'retention.json'
        before = index.read_bytes()
        (self.root / 'src/NianLink.CandidateDiscovery/CandidateDiscoveryRun.cs').write_text('changed')
        (self.root / 'scripts/discovery_portable_policy.py').write_text('raise SystemExit(0)')
        self.assertNotEqual(self.invoke('complete', run_id='b' * 32).returncode, 0)
        self.assertEqual(before, index.read_bytes())

    def test_missing_index_is_not_reinitialized_over_existing_packages(self):
        self.success('a' * 32)
        (self.mode_root() / 'retention.json').unlink()  # only this test's index
        self.assertNotEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        self.assertTrue((self.mode_root() / ('test-' + 'a' * 32)).exists())

    def test_recovery_rejects_changed_current_and_preserves_index(self):
        self.success('a' * 32)
        self.success('b' * 32)
        shutil.rmtree(self.mode_root() / ('test-' + 'a' * 32))  # only this test's temporary fixture
        state = self.mode_root() / 'retention.json'
        before = state.read_bytes()
        current = self.mode_root() / ('test-' + 'b' * 32) / ARCHIVES['fileless']
        current.write_bytes(b'changed current')
        self.assertNotEqual(self.invoke('recover').returncode, 0)
        self.assertEqual(state.read_bytes(), before)

    def test_explicit_recovery_of_missing_pending_preserves_current(self):
        self.success('a' * 32)
        self.assertEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        index = self.mode_root() / 'retention.json'
        result = self.invoke('recover-pending')
        self.assertEqual(result.returncode, 0, result.stderr)
        state = json.loads(index.read_text())
        self.assertEqual(state['current']['id'], 'a' * 32)
        self.assertIsNone(state['pending'])
        self.assertEqual(self.invoke('start', run_id='c' * 32).returncode, 0)

    def test_impossible_full_slots_with_pending_refuses_recovery_without_mutation(self):
        self.success('a' * 32)
        self.success('b' * 32)
        index = self.mode_root() / 'retention.json'
        state = json.loads(index.read_text())
        state['pending'] = 'c' * 32
        index.write_text(json.dumps(state), encoding='utf-8')
        before = index.read_bytes()
        self.assertNotEqual(self.invoke('recover-pending').returncode, 0)
        self.assertEqual(index.read_bytes(), before)
        state['pending'] = None
        state['current'] = None
        index.write_text(json.dumps(state), encoding='utf-8')
        before = index.read_bytes()
        self.assertNotEqual(self.invoke('recover').returncode, 0)
        self.assertEqual(index.read_bytes(), before)

    def test_pending_recovery_refuses_existing_output_unknown_files_and_changed_current(self):
        self.success('a' * 32)
        self.assertEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        index = self.mode_root() / 'retention.json'; before = index.read_bytes()
        target = self.prepare('b' * 32)
        self.assertNotEqual(self.invoke('recover-pending').returncode, 0)
        self.assertEqual(index.read_bytes(), before)
        shutil.rmtree(target)  # only this test's temporary fixture
        unknown = self.mode_root() / 'unknown.txt'; unknown.write_text('keep')
        self.assertNotEqual(self.invoke('recover-pending').returncode, 0)
        self.assertEqual(index.read_bytes(), before)
        unknown.unlink()  # only this test's temporary fixture
        current = self.mode_root() / ('test-' + 'a' * 32) / ARCHIVES['fileless']
        current.write_bytes(b'changed current')
        self.assertNotEqual(self.invoke('recover-pending').returncode, 0)
        self.assertEqual(index.read_bytes(), before)

    def test_maintenance_entry_updates_only_missing_records_and_requires_test_scope(self):
        for name in ('maintain-discovery-retention.ps1', 'DiscoveryManagedRetention.psm1', 'BuildArtifactLock.psm1'):
            shutil.copyfile(ROOT / 'scripts' / name, self.root / 'scripts' / name)
        self.success('a' * 32)
        self.assertEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        script = self.root / 'scripts/maintain-discovery-retention.ps1'
        index = self.mode_root() / 'retention.json'; before = index.read_bytes()
        args = [PWSH, '-NoProfile', '-NonInteractive', '-File', str(script), '-Mode', 'fileless', '-Action', 'missing-pending']
        unscoped = subprocess.run(args, capture_output=True)
        self.assertNotEqual(unscoped.returncode, 0)
        self.assertEqual(index.read_bytes(), before)
        done = subprocess.run([*args, '-TestingOnly'], capture_output=True, text=True, encoding='utf-8', errors='replace')
        self.assertEqual(done.returncode, 0, done.stderr)
        self.assertIsNone(json.loads(index.read_text())['pending'])
        self.success('c' * 32)
        shutil.rmtree(self.mode_root() / ('test-' + 'a' * 32))  # only this test's temporary fixture
        done = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-File', str(script), '-Mode', 'fileless',
                               '-Action', 'missing-previous', '-TestingOnly'],
                              capture_output=True, text=True, encoding='utf-8', errors='replace')
        self.assertEqual(done.returncode, 0, done.stderr)
        state = json.loads(index.read_text())
        self.assertIsNone(state['previous'])
        self.assertEqual(state['current']['id'], 'c' * 32)

    def test_maintenance_case_variants_never_select_the_wrong_action(self):
        for name in ('maintain-discovery-retention.ps1', 'DiscoveryManagedRetention.psm1', 'BuildArtifactLock.psm1'):
            shutil.copyfile(ROOT / 'scripts' / name, self.root / 'scripts' / name)
        self.success('a' * 32)
        self.assertEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        script = self.root / 'scripts/maintain-discovery-retention.ps1'
        index = self.mode_root() / 'retention.json'; before = index.read_bytes()
        base = [PWSH, '-NoProfile', '-NonInteractive', '-File', str(script), '-TestingOnly']
        wrong = subprocess.run([*base, '-Mode', 'fileless', '-Action', 'MISSING-PREVIOUS'], capture_output=True)
        self.assertNotEqual(wrong.returncode, 0)
        self.assertEqual(index.read_bytes(), before)
        pending = subprocess.run([*base, '-Mode', 'FILELESS', '-Action', 'MISSING-PENDING'], capture_output=True)
        self.assertEqual(pending.returncode, 0, pending.stderr)
        self.assertIsNone(json.loads(index.read_text())['pending'])
        self.success('c' * 32)
        shutil.rmtree(self.mode_root() / ('test-' + 'a' * 32))  # only this test's temporary fixture
        previous = subprocess.run([*base, '-Mode', 'FILELESS', '-Action', 'MISSING-PREVIOUS'], capture_output=True)
        self.assertEqual(previous.returncode, 0, previous.stderr)
        self.assertIsNone(json.loads(index.read_text())['previous'])

    def test_maintenance_supports_discovery_with_case_normalization(self):
        for name in ('maintain-discovery-retention.ps1', 'DiscoveryManagedRetention.psm1', 'BuildArtifactLock.psm1'):
            shutil.copyfile(ROOT / 'scripts' / name, self.root / 'scripts' / name)
        self.success('a' * 32, 'discovery')
        self.assertEqual(self.invoke('start', 'discovery', 'b' * 32).returncode, 0)
        script = self.root / 'scripts/maintain-discovery-retention.ps1'
        index = self.mode_root('discovery') / 'retention.json'; before = index.read_bytes()
        base = [PWSH, '-NoProfile', '-NonInteractive', '-File', str(script), '-Mode', 'DISCOVERY', '-TestingOnly']
        wrong = subprocess.run([*base, '-Action', 'MISSING-PREVIOUS'], capture_output=True)
        self.assertNotEqual(wrong.returncode, 0)
        self.assertEqual(index.read_bytes(), before)
        done = subprocess.run([*base, '-Action', 'MISSING-PENDING'], capture_output=True)
        self.assertEqual(done.returncode, 0, done.stderr)
        state = json.loads(index.read_text())
        self.assertIsNone(state['pending'])
        self.assertEqual(state['current']['id'], 'a' * 32)

    def test_old_package_revalidation_does_not_require_old_source(self):
        self.success('a' * 32)
        (self.root / 'src/NianLink.CandidateDiscovery/CandidateDiscoveryRun.cs').write_text('new source')
        self.assertEqual(self.invoke('start', run_id='b' * 32).returncode, 0)

    def test_wrong_project_lock_cannot_reserve(self):
        other = Path(self.temp.name) / 'other'
        other.mkdir()
        script = ("Import-Module $env:TEST_MODULE -ErrorAction Stop; "
                  "$lock=Enter-NianManagedDiscoveryLock -ProjectRoot $env:TEST_ROOT; "
                  "try { Start-NianManagedDiscovery -ProjectRoot $env:TEST_OTHER -Mode fileless -RunId ('a'*32) -Lock $lock } "
                  "finally { Exit-NianManagedDiscoveryLock -Lock $lock }")
        env = os.environ | {'TEST_MODULE': str(MODULE), 'TEST_ROOT': str(self.root), 'TEST_OTHER': str(other)}
        result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', "$ErrorActionPreference='Stop'; " + script],
                                env=env, capture_output=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((other / '.artifacts').exists())

    def junction(self, link, target):
        env = os.environ | {'TEST_LINK': str(link), 'TEST_TARGET': str(target)}
        result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command',
                                 'New-Item -ItemType Junction -Path $env:TEST_LINK -Target $env:TEST_TARGET -ErrorAction Stop | Out-Null'],
                                env=env, capture_output=True)
        if result.returncode:
            self.skipTest('directory junction unavailable')

    def test_linked_managed_ancestor_and_bundle_child_are_refused(self):
        elsewhere = Path(self.temp.name) / 'elsewhere'
        elsewhere.mkdir()
        (elsewhere / 'untouched.txt').write_text('untouched')
        self.junction(self.root / '.artifacts', elsewhere)
        self.assertNotEqual(self.invoke('start').returncode, 0)
        self.assertEqual((elsewhere / 'untouched.txt').read_text(), 'untouched')

    def test_linked_existing_bundle_child_is_refused(self):
        self.success('a' * 32)
        app = self.mode_root() / ('test-' + 'a' * 32) / '年年互联-免文件便携测试/App'
        shutil.rmtree(app)  # only this test's temporary fixture
        elsewhere = Path(self.temp.name) / 'elsewhere'
        elsewhere.mkdir()
        (elsewhere / 'untouched.txt').write_text('untouched')
        self.junction(app, elsewhere)
        self.assertNotEqual(self.invoke('start', run_id='b' * 32).returncode, 0)
        self.assertEqual((elsewhere / 'untouched.txt').read_text(), 'untouched')

    def test_independent_file_lease_blocks_even_without_project_mutex(self):
        base = self.root / '.artifacts/discovery-portable-managed'
        base.mkdir(parents=True)
        ready, done = self.root / 'ready', self.root / 'done'
        env = os.environ | {'TEST_LEASE': str(base / '.build.lock'), 'TEST_READY': str(ready), 'TEST_DONE': str(done)}
        script = ("$f=[IO.File]::Open($env:TEST_LEASE,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); "
                  "try { [IO.File]::WriteAllText($env:TEST_READY,'ready'); while(-not [IO.File]::Exists($env:TEST_DONE)) { Start-Sleep -Milliseconds 50 } } "
                  "finally { $f.Dispose() }")
        holder = subprocess.Popen([PWSH, '-NoProfile', '-NonInteractive', '-Command', script], env=env,
                                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            deadline = time.monotonic() + 10
            while not ready.exists() and holder.poll() is None and time.monotonic() < deadline:
                time.sleep(.05)
            self.assertTrue(ready.exists())
            self.assertNotEqual(self.invoke('start').returncode, 0)
            self.assertFalse(self.mode_root().exists())
        finally:
            done.touch()
            holder.wait(timeout=10)

    def test_explicit_recovery_for_a_previous_already_removed(self):
        self.success('a' * 32)
        self.success('b' * 32)
        self.assertNotEqual(self.invoke('recover').returncode, 0)
        shutil.rmtree(self.mode_root() / ('test-' + 'a' * 32))  # only this test's temporary fixture
        self.assertNotEqual(self.invoke('start', run_id='c' * 32).returncode, 0)
        result = self.invoke('recover')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.invoke('start', run_id='c' * 32).returncode, 0)
        self.assertTrue((self.mode_root() / ('test-' + 'b' * 32)).exists())


if __name__ == '__main__':
    unittest.main()
