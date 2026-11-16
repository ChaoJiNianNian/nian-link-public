"""Source review snapshots must include the test fixtures and SDK policy they reference."""
import importlib.util
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("source_candidate", ROOT / "scripts/public_source_candidate.py")
candidate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(candidate)

class PortableFixtureInclusionTests(unittest.TestCase):
    def test_conditional_experiment_uses_explicit_sources_and_test_only_peer(self):
        project = ROOT / "tests/ConditionalCommitIsolation.Tests/ConditionalCommitIsolation.Tests.csproj"
        items = [item.attrib.get("Include", "") for item in ET.parse(project).iter("Compile")]
        product_sources = [item.replace("\\", "/") for item in items
                           if item.replace("\\", "/").startswith("../../src/DesktopToolbox.DeskLink/")]
        self.assertTrue(product_sources)
        self.assertFalse(any("*" in item or "?" in item for item in product_sources),
                         "New production partials must not silently enter the historical experiment")
        self.assertNotIn("../../src/DesktopToolbox.DeskLink/SecurePairingPeer.cs", product_sources)
        test_peer = "tests/ConditionalCommitIsolation.Tests/Historical/SecurePairingPeer.cs"
        self.assertIn(test_peer, candidate.collect(ROOT))
        self.assertIn(test_peer, candidate.REQUIRED_FILES)
        with self.assertRaises(ValueError):
            candidate.validate_complete(list(candidate.REQUIRED_FILES - {test_peer}))
        product_project = ROOT / "src/DesktopToolbox.DeskLink/DesktopToolbox.DeskLink.csproj"
        product_includes = [item.attrib.get("Include", "") for item in ET.parse(product_project).iter("Compile")]
        self.assertFalse(any("ConditionalCommitIsolation.Tests" in item for item in product_includes))

    def test_required_portable_fixtures_and_policy_are_collected(self):
        required = {"tests/experiments/DeskflowPeProvenance/compare_pe.py", "tests/experiments/DeskflowPeProvenance/test_compare_pe.py", "tests/experiments/DeskflowPeProvenance/README.md", "tests/experiments/DeskflowPeProvenance/comparison-evidence.json", "tests/experiments/DeskflowPeProvenance/link-input-manifest.json", "scripts/verify_qt_official_archive.py", "scripts/qt_pe_unsigned_hash.py", "tests/test_verify_qt_official_archive.py", "tests/test_qt_pe_unsigned_hash.py", "docs/Qt历史文件来源核验.md", "tests/experiments/QtSourceProvenance/source-tree-comparison.json", "scripts/build_deskflow_core.py", "tests/test_native_core_build.py", "docs/Deskflow四补丁隔离构建.md", "docs/Conditional隔离测试源码缺口.md", "docs/conditional-pdb-evidence.json", "scripts/github_native_clean_build.py", "scripts/print_native_clean_evidence.py", "tests/test_github_native_clean_build.py", ".github/workflows/native-clean-manual.yml", "docs/Windows原生源码干净构建.md", "scripts/qt_config_summary.py", "tests/test_qt_config_summary.py", "docs/Qt构建配置摘要说明.md", "docs/OpenSSL运行路径与provider核验.md", "docs/openssl-runtime-evidence.json", "docs/Windows原生源码本机全链证据.md", "docs/windows-native-local-evidence.json", "docs/功能详解.md", "tests/TestSupport/SyntheticWorkDirectory.cs", "src/DesktopToolbox.Mac/Tests/SyntheticWorkDirectoryTests.cs", "scripts/SdkBaseline.psm1", "tests/test_sdk_baseline.py", "tests/test_source_portability_inclusion.py", "scripts/fetch_upstream_sources.py", "tests/test_fetch_upstream_sources.py", "docs/上游源码获取说明.md"}
        paths = set(candidate.collect(ROOT))
        for name in required:
            with self.subTest(path=name):
                self.assertIn(name, paths)
                self.assertIn(name, candidate.REQUIRED_FILES)

if __name__ == "__main__":
    unittest.main()
