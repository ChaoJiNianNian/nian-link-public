"""Offline project-graph guard for the synthetic Mac discovery boundary."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
BOUNDARY = (ROOT / 'src/NianLink.MacDiscoveryBoundary/NianLink.MacDiscoveryBoundary.csproj').resolve()
PROBE = (ROOT / 'tests/MacSystemProbe/MacSystemProbe.csproj').resolve()
CANDIDATE = (ROOT / 'src/NianLink.MacCandidateDiscovery/NianLink.MacCandidateDiscovery.csproj').resolve()
MANAGED_PROBE = (ROOT / 'tests/MacManagedWifiProbe/MacManagedWifiProbe.csproj').resolve()
MAPPING_PROBE = (ROOT / 'tests/MacManagedMappingProbe/MacManagedMappingProbe.csproj').resolve()


class MacDiscoveryProjectTests(unittest.TestCase):
    def test_mac_application_cannot_reference_synthetic_boundary(self):
        seen = set()

        def visit(path):
            path = path.resolve()
            if path in seen:
                return
            seen.add(path)
            for reference in ET.parse(path).iter('ProjectReference'):
                visit(path.parent / reference.attrib['Include'])

        for application in ('src/DesktopToolbox.Mac/DesktopToolbox.Mac.csproj',
                            'src/NianLink.Windows/NianLink.Windows.csproj',
                            'src/NianLink.MultiDevice/NianLink.MultiDevice.csproj',
                            'src/NianLink.DiscoveryPortable/NianLink.DiscoveryPortable.csproj'):
            with self.subTest(application=application):
                seen.clear()
                visit(ROOT / application)
                self.assertNotIn(BOUNDARY, seen)
                self.assertNotIn(PROBE, seen)
                self.assertNotIn(CANDIDATE, seen)
                self.assertNotIn(MANAGED_PROBE, seen)
                self.assertNotIn(MAPPING_PROBE, seen)


if __name__ == '__main__':
    unittest.main()
