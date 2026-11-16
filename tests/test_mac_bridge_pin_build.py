"""Offline candidate-only build checks for per-build native bridge pinning."""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'src/NianLink.MacCandidateDiscovery/NianLink.MacCandidateDiscovery.csproj'
RESPONDER = ROOT / 'src/NianLink.MacResponderCandidate/NianLink.MacResponderCandidate.csproj'
DOTNET = shutil.which('dotnet')
if os.name == 'nt':
    fallback = Path(r'D:\Projects\pc-toolbox\.tools\dotnet\dotnet.exe')
    if fallback.is_file():
        DOTNET = str(fallback)
DOTNET = DOTNET or os.environ.get('DOTNET_HOST_PATH')


@unittest.skipUnless(DOTNET, 'dotnet SDK is unavailable')
class MacBridgePinBuildTests(unittest.TestCase):
    def test_pin_changes_rebuild_metadata_and_missing_pin_clears_it(self):
        work_root = ROOT / 'tests/.work'
        work_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='mac-bridge-pin-', dir=work_root) as temporary:
            root = Path(temporary)
            artifacts = root / 'artifacts'
            source = root / 'empty-source'
            source.mkdir()
            common = [DOTNET, 'build', str(PROJECT), '-c', 'Release', '--nologo', '--verbosity', 'quiet',
                      '--artifacts-path', str(artifacts), f'-p:RestoreSources={source}', '-p:NuGetAudit=false']
            pin_a, pin_b = 'a' * 64, 'b' * 64
            assembly = artifacts / 'bin/NianLink.MacCandidateDiscovery/release/NianLink.MacCandidateDiscovery.dll'
            info = artifacts / 'obj/NianLink.MacCandidateDiscovery/release/NianLink.MacCandidateDiscovery.AssemblyInfo.cs'

            def build(pin):
                result = subprocess.run(common + ([] if pin is None else [f'-p:NianWifiEvidencePinnedSha256={pin}']),
                                        cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=120)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                return assembly.read_bytes(), info.read_text(encoding='utf-8-sig')

            blob, text = build(None)
            self.assertNotIn('NianWifiEvidencePinnedSha256', text)
            for expected, absent in ((pin_a, pin_b), (pin_b, pin_a), (pin_a, pin_b)):
                blob, text = build(expected)
                self.assertIn(expected, text)
                self.assertIn(expected.encode(), blob)
                self.assertNotIn(absent.encode(), blob)
            blob, text = build(None)
            self.assertNotIn('NianWifiEvidencePinnedSha256', text)
            self.assertNotIn(pin_a.encode(), blob)
            for pin in ('BAD', 'A' * 64, 'a' * 63, 'a' * 64 + '\n'):
                invalid = subprocess.run(common + [f'-p:NianWifiEvidencePinnedSha256={pin}'], cwd=ROOT,
                                         capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=120)
                self.assertNotEqual(invalid.returncode, 0, 'invalid bridge pin must fail the build')

    def test_global_pin_reaches_candidate_through_responder_project(self):
        work_root = ROOT / 'tests/.work'
        work_root.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='mac-bridge-pin-graph-', dir=work_root) as temporary:
            root = Path(temporary)
            source = root / 'empty-source'
            source.mkdir()
            artifacts = root / 'artifacts'
            pin = 'c' * 64
            command = [DOTNET, 'build', str(RESPONDER), '-c', 'Release', '--nologo', '--verbosity', 'quiet',
                       '--artifacts-path', str(artifacts), f'-p:RestoreSources={source}', '-p:NuGetAudit=false',
                       f'-p:NianWifiEvidencePinnedSha256={pin}']
            result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True,
                                    encoding='utf-8', errors='replace', timeout=120)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            assembly = artifacts / 'bin/NianLink.MacResponderCandidate/release/NianLink.MacCandidateDiscovery.dll'
            self.assertIn(pin.encode(), assembly.read_bytes())


if __name__ == '__main__':
    unittest.main()
