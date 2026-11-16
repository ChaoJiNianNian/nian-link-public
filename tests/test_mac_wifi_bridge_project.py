import pathlib
import os
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
NATIVE = ROOT / 'src' / 'NianLink.MacDiscoveryNative'
TESTS = ROOT / 'tests' / 'MacWifiBridge'


class MacWifiBridgeProjectTests(unittest.TestCase):
    def test_sources_are_present_and_not_referenced_by_mac_product(self):
        for name in ('WifiEvidenceCore.h', 'WifiEvidenceCore.c', 'WifiEvidenceBridge.h', 'WifiEvidenceBridge.m'):
            with self.subTest(name=name):
                self.assertTrue((NATIVE / name).is_file())
        mac_project = (ROOT / 'src/DesktopToolbox.Mac/DesktopToolbox.Mac.csproj').read_text(encoding='utf-8')
        self.assertNotIn('NianLink.MacDiscoveryNative', mac_project)

    def test_bridge_exposes_only_evidence_and_uses_required_apis(self):
        source = (NATIVE / 'WifiEvidenceBridge.m').read_text(encoding='utf-8')
        for required in ('nian_wifi_evidence_v1', 'CWWiFiClient', 'SCNetworkInterfaceCopyAll', 'kSCNetworkInterfaceTypeIEEE80211'):
            with self.subTest(required=required):
                self.assertIn(required, source)
        for forbidden in ('.ssid', '.bssid', '.hardwareAddress', 'scanForNetworks', 'socket(', 'Keychain'):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, source)

    def test_native_smoke_requires_explicit_run(self):
        source = (TESTS / 'WifiEvidenceBridgeSmoke.c').read_text(encoding='utf-8')
        self.assertIn('"--run"', source)
        self.assertIn('nian_wifi_evidence_v1', source)
        self.assertNotIn('socket(', source)
        self.assertTrue((TESTS / 'WifiEvidenceBridgeNameTests.m').is_file())

    def test_core_c_syntax_on_windows_or_behavior_elsewhere(self):
        work = ROOT / 'tests' / '.work'
        work.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='wifi-abi-', dir=work) as temp:
            temp_path = pathlib.Path(temp)
            self.assertEqual(temp_path.resolve().parent, work.resolve())
            exe = temp_path / ('wifi-core-tests.exe' if os.name == 'nt' else 'wifi-core-tests')
            env = dict(os.environ, TMP=str(temp_path), TEMP=str(temp_path), TMPDIR=str(temp_path))
            command = [os.environ.get('NIAN_TEST_CC', 'gcc'), '-std=c11', '-Wall', '-Wextra', '-Werror', '-I', str(NATIVE),
                       str(TESTS / 'WifiEvidenceCoreTests.c'), str(NATIVE / 'WifiEvidenceCore.c')]
            if os.name == 'nt':
                command.append('-fsyntax-only')
            else:
                command.extend(['-o', str(exe)])
            build = subprocess.run(command, capture_output=True, text=True, env=env)
            self.assertEqual(build.returncode, 0, build.stdout + build.stderr)
            smoke = subprocess.run([os.environ.get('NIAN_TEST_CC', 'gcc'), '-std=c11', '-Wall', '-Wextra', '-Werror',
                                    '-I', str(NATIVE), '-fsyntax-only', str(TESTS / 'WifiEvidenceBridgeSmoke.c')],
                                   capture_output=True, text=True, env=env)
            self.assertEqual(smoke.returncode, 0, smoke.stdout + smoke.stderr)
            if os.name != 'nt':
                run = subprocess.run([str(exe)], capture_output=True, text=True)
                self.assertEqual(run.returncode, 0, run.stdout + run.stderr)


if __name__ == '__main__':
    unittest.main()
