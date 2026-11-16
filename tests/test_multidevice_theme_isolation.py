import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OLD = "../DesktopToolbox.App/DeskLink/mini-windows.css"
NEW = "../NianLink.MultiDevice/mini-windows.css"


def mini_source(project):
    tree = ET.parse(ROOT / project)
    return [node.attrib["Include"] for node in tree.iter("Content")
            if node.attrib.get("Link") == "CollaborationUi/mini-windows.css"]


class MiniThemeIsolationTests(unittest.TestCase):
    def test_multidevice_uses_own_stylesheet_and_legacy_keeps_shared_stylesheet(self):
        self.assertEqual(mini_source("src/NianLink.MultiDevice/NianLink.MultiDevice.csproj"), [NEW])
        self.assertEqual(mini_source("src/NianLink.DiscoveryPortable/NianLink.DiscoveryPortable.csproj"), [NEW])
        self.assertEqual(mini_source("src/NianLink.Windows/NianLink.Windows.csproj"), [OLD])
        old = (ROOT / "src/DesktopToolbox.App/DeskLink/mini-windows.css").read_text(encoding="utf-8")
        new = (ROOT / "src/NianLink.MultiDevice/mini-windows.css").read_text(encoding="utf-8")
        self.assertNotIn("--nl-glass: #f6f9ff", old)
        self.assertIn("--nl-glass: #f6f9ff", new)


if __name__ == "__main__":
    unittest.main()
