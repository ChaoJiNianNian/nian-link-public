import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
PROBE = ROOT / 'tests' / 'MacWifiProbe'


class MacWifiProbeProjectTests(unittest.TestCase):
    def test_source_and_native_rule_tests_are_present(self):
        for name in ('WifiProbeMain.swift', 'WifiProbeRules.swift', 'WifiProbeRulesTests.swift'):
            with self.subTest(name=name):
                self.assertTrue((PROBE / name).is_file())

    def test_probe_has_explicit_only_run_and_no_network_or_sensitive_access(self):
        source = (PROBE / 'WifiProbeMain.swift').read_text(encoding='utf-8')
        self.assertIn('CommandLine.arguments', source)
        self.assertIn('"--run"', source)
        self.assertIn('CWWiFiClient.shared().interfaces()', source)
        self.assertIn('SCNetworkInterfaceCopyAll()', source)
        for forbidden in ('.ssid', '.bssid', '.hardwareAddress', 'scanForNetworks', 'socket(', 'Keychain'):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, source)

    def test_production_mac_project_does_not_reference_probe(self):
        project = (ROOT / 'src' / 'DesktopToolbox.Mac' / 'DesktopToolbox.Mac.csproj').read_text(encoding='utf-8')
        self.assertNotIn('MacWifiProbe', project)


if __name__ == '__main__':
    unittest.main()
