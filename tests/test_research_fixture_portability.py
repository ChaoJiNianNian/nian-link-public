"""Keep Windows research fixtures inside the configured isolated project root."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class ResearchFixturePortabilityTests(unittest.TestCase):
    def test_server_fixture_uses_build_configured_research_root(self):
        source = (ROOT / "tests/MultiPeerTrust.Tests/FenceServerSkeletonTests.cs").read_text(encoding="utf-8-sig")
        self.assertNotIn("D:\\Projects\\nian-link", source)
        self.assertIn('"NianFenceResearchRoot"', source)
        self.assertIn('Path.TrimEndingDirectorySeparator', source)

if __name__ == "__main__": unittest.main()
