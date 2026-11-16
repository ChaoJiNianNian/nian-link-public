import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

MODULE = Path(__file__).resolve().parents[1] / 'scripts/DiscoveryPortablePaths.psm1'
PWSH = shutil.which('pwsh')
SCRIPT = ("Import-Module $env:TEST_MODULE -ErrorAction Stop; "
          "$pathParameters=@{ProjectRoot=$env:TEST_ROOT;OutputRoot=$env:TEST_OUTPUT_ROOT;Output=$env:TEST_OUTPUT;DataRoot=$env:TEST_LOCAL;Profile=$env:TEST_PROFILE}; "
          "if($env:TEST_MODE) { $pathParameters.ManagedMode=$env:TEST_MODE }; Assert-DiscoveryBuildPaths @pathParameters")

@unittest.skipUnless(PWSH and os.name == 'nt', 'requires Windows PowerShell 7')
class DiscoveryPortablePathTests(unittest.TestCase):
    def run_policy(self, root, output, profile, output_root=None, managed_mode=None):
        local = root / 'local'; local.mkdir(exist_ok=True)
        env = os.environ | {'TEST_MODULE': str(MODULE), 'TEST_ROOT': str(root), 'TEST_LOCAL': str(local),
              'TEST_OUTPUT_ROOT': str(output_root or root / '.artifacts/discovery-portable'), 'TEST_OUTPUT': str(output), 'TEST_PROFILE': str(profile),
              'TEST_MODE': managed_mode or ''}
        return subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', SCRIPT], env=env, capture_output=True).returncode

    def test_only_new_matching_dedicated_targets_are_allowed(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); ident = 'test-' + 'a' * 32
            output = root / '.artifacts/discovery-portable' / ident
            profile = root / 'local/nian-link/test-profiles/discovery-portable' / ident
            self.assertEqual(0, self.run_policy(root, output, profile))
            self.assertNotEqual(0, self.run_policy(root, output, root / 'local/NianLinkMultiDeviceCandidate'))
            output.mkdir(parents=True)
            self.assertNotEqual(0, self.run_policy(root, output, profile))

    def test_existing_profile_and_mismatched_identifiers_are_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); ident = 'test-' + 'b' * 32
            output = root / '.artifacts/discovery-portable' / ident
            profile = root / 'local/nian-link/test-profiles/discovery-portable' / ident
            profile.mkdir(parents=True)
            self.assertNotEqual(0, self.run_policy(root, output, profile))
            self.assertNotEqual(0, self.run_policy(root, output, profile.parent / ('test-' + 'c' * 32)))

    def test_linked_ancestor_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); (root / 'real').mkdir(); (root / 'local').mkdir()
            try: (root / '.artifacts').symlink_to(root / 'real', target_is_directory=True)
            except OSError:
                created = subprocess.run(['cmd', '/c', 'mklink', '/J', str(root / '.artifacts'), str(root / 'real')], capture_output=True)
                if created.returncode != 0: self.skipTest('directory junction unavailable')
            ident = 'test-' + 'd' * 32
            self.assertNotEqual(0, self.run_policy(root, root / '.artifacts/discovery-portable' / ident,
                                                  root / 'local/nian-link/test-profiles/discovery-portable' / ident))

    def test_explicit_managed_mode_accepts_only_its_matching_root(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); ident = 'test-' + 'e' * 32
            profile = root / 'local/nian-link/test-profiles/discovery-portable' / ident
            for mode in ('discovery', 'fileless', 'diagnostics'):
                base = root / '.artifacts/discovery-portable-managed' / mode
                self.assertEqual(0, self.run_policy(root, base / ident, profile, base, mode))
                self.assertNotEqual(0, self.run_policy(root, base / ident, profile, base))
                wrong = 'discovery' if mode != 'discovery' else 'fileless'
                self.assertNotEqual(0, self.run_policy(root, base / ident, profile, base, wrong))
            self.assertNotEqual(0, self.run_policy(root, root / 'elsewhere' / ident, profile, root / 'elsewhere', 'fileless'))

if __name__ == '__main__': unittest.main()
