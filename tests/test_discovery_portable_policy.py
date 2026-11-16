import hashlib
import importlib.util
import json
import re
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/discovery_portable_policy.py'
spec = importlib.util.spec_from_file_location('discovery_portable_policy', SCRIPT)
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)

class DiscoveryPortablePolicyTests(unittest.TestCase):
    def fixture(self, root):
        bundle = root / 'bundle'; source = root / 'source'
        files = {
            'App/NianLink.DiscoveryPortable.exe': b'exe',
            'App/NianLink.DiscoveryPortable.dll': b'entry',
            'App/NianLink.DiscoveryPortable.runtimeconfig.json': b'{}',
            'App/NianLink.CandidateDiscovery.dll': b'dll',
            'App/NianLink.DiscoveryPortable.deps.json': json.dumps({'targets': {'test': {'NianLink.CandidateDiscovery/1.0.0': {'runtime': {'NianLink.CandidateDiscovery.dll': {}}}}}}).encode(),
            'Keyboard/deskflow-core.exe': b'engine',
            'Keyboard/settings/Deskflow.conf': b'portable-marker',
            'App/discovery-portable-id.txt': b'a' * 32,
            '笔记本-D盘启动.cmd': policy.expected_launcher('a' * 32, 'D').encode('ascii'),
            '台式机-H盘启动.cmd': policy.expected_launcher('a' * 32, 'H').encode('ascii'),
        }
        sources = {'src/NianLink.CandidateDiscovery/NianLink.CandidateDiscovery.csproj': b'project',
                   'src/NianLink.CandidateDiscovery/CandidateDiscoveryRun.cs': b'source',
                   'src/NianLink.MultiDevice/NianLink.MultiDevice.csproj': b'app',
                   'src/NianLink.DiscoveryPortable/NianLink.DiscoveryPortable.csproj': b'portable',
                   'src/DesktopToolbox.DeskLink/DiscoveryReviewLease.cs': b'shared',
                   'src/NianLink.MultiDevice.Shared/PeerControlOwner.cs': b'owner guard'}
        def save(base, data):
            rows = []
            for path, value in data.items():
                file = base / path; file.parent.mkdir(parents=True, exist_ok=True); file.write_bytes(value)
                rows.append({'path': path, 'bytes': len(value), 'sha256': hashlib.sha256(value).hexdigest()})
            return rows
        manifest = {'channel': 'discovery-portable-v1', 'runId': 'a' * 32, 'files': save(bundle, files), 'sources': save(source, sources)}
        return bundle, source, manifest

    def test_valid_fixture_passes(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle, source, manifest = self.fixture(Path(tmp)); policy.validate(bundle, source, manifest)

    def test_beta_candidate_requires_exact_isolated_launcher(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle, source, manifest = self.fixture(Path(tmp))
            manifest['betaPortable'] = True
            with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)
            launcher = bundle / 'Start-NianLink-Beta.cmd'
            launcher.write_bytes(b'unsafe')
            manifest['files'].append({'path': launcher.name, 'bytes': 6, 'sha256': hashlib.sha256(b'unsafe').hexdigest()})
            with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)
            correct = b'@echo off\r\nstart "" "%~dp0App\\NianLink.DiscoveryPortable.exe" --beta-portable\r\n'
            launcher.write_bytes(correct)
            manifest['files'][-1].update(bytes=len(correct), sha256=hashlib.sha256(correct).hexdigest())
            policy.validate(bundle, source, manifest)
            manifest['betaPortable'] = False
            with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)
            manifest['betaPortable'] = 'true'
            with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)

    def test_shared_owner_source_is_required_and_build_inventory_matches_policy(self):
        path = 'src/NianLink.MultiDevice.Shared/PeerControlOwner.cs'
        self.assertIn(path, policy.REQUIRED_SOURCE)
        self.assertIn('src/NianLink.MultiDevice.Shared', policy.SOURCE_DIRS)
        build = (SCRIPT.parent / 'build-multidevice-windows-test.ps1').read_text(encoding='utf-8')
        match = re.search(r'foreach\s*\(\$sourceDir\s+in\s+@\(([^)]*)\)\)', build)
        self.assertIsNotNone(match)
        listed = re.findall(r"'([^']+)'", match.group(1))
        self.assertEqual(set(policy.SOURCE_DIRS), set(listed))
        with tempfile.TemporaryDirectory() as tmp:
            bundle, source, manifest = self.fixture(Path(tmp))
            manifest['sources'] = [row for row in manifest['sources'] if row['path'] != path]
            with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)

    def test_missing_discovery_dll_or_dependency_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle, source, manifest = self.fixture(Path(tmp))
            manifest['files'] = [row for row in manifest['files'] if row['path'] != 'App/NianLink.CandidateDiscovery.dll']
            with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)

    def test_changed_source_or_payload_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle, source, manifest = self.fixture(Path(tmp))
            (source / manifest['sources'][1]['path']).write_bytes(b'changed')
            with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)
        with tempfile.TemporaryDirectory() as tmp:
            bundle, source, manifest = self.fixture(Path(tmp))
            (bundle / 'App/NianLink.CandidateDiscovery.dll').write_bytes(b'changed')
            with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)

    def test_changed_package_identity_cannot_redirect_profile(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle, source, manifest = self.fixture(Path(tmp))
            altered = b'b' * 32
            (bundle / 'App/discovery-portable-id.txt').write_bytes(altered)
            row = next(x for x in manifest['files'] if x['path'] == 'App/discovery-portable-id.txt')
            row['sha256'] = hashlib.sha256(altered).hexdigest()
            with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)

    def test_test_profiles_never_included_in_payload(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle, source, manifest = self.fixture(Path(tmp))
            path = bundle / 'test-profiles' / 'identity.json'; path.parent.mkdir(); path.write_bytes(b'{}')
            manifest['files'].append({'path': 'test-profiles/identity.json', 'bytes': 2, 'sha256': hashlib.sha256(b'{}').hexdigest()})
            with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)

    def test_any_ordinary_entry_component_is_rejected(self):
        for name in ('NianLink.MultiDevice.exe', 'NianLink.MultiDevice.dll',
                     'NianLink.MultiDevice.deps.json', 'NianLink.MultiDevice.runtimeconfig.json'):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as tmp:
                bundle, source, manifest = self.fixture(Path(tmp))
                path = bundle / 'App' / name; path.write_bytes(b'old')
                manifest['files'].append({'path': 'App/' + name, 'bytes': 3, 'sha256': hashlib.sha256(b'old').hexdigest()})
                with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)

    def test_unsafe_or_duplicate_paths_and_unlisted_payload_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle, source, manifest = self.fixture(Path(tmp))
            manifest['files'].append(dict(manifest['files'][0], path='../outside'))
            with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)
        with tempfile.TemporaryDirectory() as tmp:
            bundle, source, manifest = self.fixture(Path(tmp))
            (bundle / 'unexpected.bin').write_bytes(b'x')
            with self.assertRaises(ValueError): policy.validate(bundle, source, manifest)

if __name__ == '__main__': unittest.main()
