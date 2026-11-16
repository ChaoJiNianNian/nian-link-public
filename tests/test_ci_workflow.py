"""Security and scope checks for the managed-source CI workflow."""
from pathlib import Path
import importlib.util
import re
import unittest


ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / '.github/workflows/managed-ci.yml'


class ManagedCiPolicyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = WORKFLOW.read_text(encoding='utf-8')

    def test_actions_are_pinned_to_full_commit_hashes(self):
        actions = re.findall(r'^\s*(?:-\s*)?uses:\s*(\S+)', self.workflow, re.MULTILINE)
        self.assertGreaterEqual(len(actions), 2)
        for action in actions:
            self.assertRegex(action, r'^[\w.-]+/[\w.-]+@[0-9a-f]{40}$')

    def test_token_is_read_only_and_checkout_does_not_persist_credentials(self):
        self.assertRegex(self.workflow, r'(?m)^permissions:\s*\n\s+contents:\s*read\s*$')
        self.assertRegex(self.workflow, r'(?m)^\s+persist-credentials:\s*false\s*$')
        self.assertNotRegex(self.workflow, r'(?i)pull_request_target|write-all|contents:\s*write|secrets\.')

    def test_only_source_builds_and_no_release_or_artifact_publishing(self):
        self.assertIn('scripts/build-managed.py --target windows --with-tests', self.workflow)
        self.assertIn('scripts/build-managed.py --target mac', self.workflow)
        self.assertIn('scripts/build-managed.py --target windows --multi-device', self.workflow)
        self.assertIn('scripts/build-managed.py --target mac --multi-device', self.workflow)
        self.assertNotRegex(self.workflow, r'(?i)upload-artifact|gh\s+release|dotnet\s+publish|workflow_run|deploy')

    def test_offline_integrity_checks_are_explicitly_wired(self):
        for name in ("test_public_source_candidate.py", "test_github_screenshots.py", "test_qt_config_summary.py", "test_github_native_clean_build.py", "test_native_core_build.py", "test_verify_qt_official_archive.py", "test_qt_pe_unsigned_hash.py", "test_managed_runtime_notices.py", "test_qttools_notice_integrity.py", "test_dotnet_notice_integrity.py", "test_fetch_upstream_sources.py", "test_sdk_baseline.py", "test_source_portability_inclusion.py", "test_source_bytes_policy.py"):
            with self.subTest(name=name):
                self.assertIn("python -B -m unittest discover -s tests -p " + name, self.workflow)

    def test_pe_provenance_checks_are_wired(self):
        self.assertIn('python -B -m unittest discover -s tests/experiments/DeskflowPeProvenance -p test_compare_pe.py', self.workflow)

    def test_clean_builds_precede_known_full_test_blocker(self):
        blocker = self.workflow.index('run: python scripts/build-managed.py --target windows --with-tests')
        for command in ('--target mac', '--target windows --multi-device', '--target mac --multi-device'):
            with self.subTest(command=command):
                self.assertLess(self.workflow.index('run: python scripts/build-managed.py ' + command), blocker)

    def test_runner_and_sdk_are_explicit(self):
        self.assertRegex(self.workflow, r'(?m)^\s+runs-on:\s*windows-2025\s*$')
        self.assertRegex(self.workflow, r'(?m)^\s+dotnet-version:\s*[\'"]?10\.0\.404[\'"]?\s*$')
        self.assertRegex(self.workflow, r'(?m)^\s+timeout-minutes:\s*\d+\s*$')

    def test_windows_ci_test_entry_includes_isolated_commit_regressions(self):
        self.assertRegex(self.workflow, r'(?m)^\s+run:\s+python scripts/build-managed\.py --target windows --with-tests\s*$')
        spec = importlib.util.spec_from_file_location('managed_ci_plan', ROOT / 'scripts/build-managed.py')
        assert spec and spec.loader
        build = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(build)
        commands = build.create_commands(ROOT, 'dotnet', 'windows', True, None)
        expected = ROOT / 'tests/ConditionalCommitIsolation.Tests/ConditionalCommitIsolation.Tests.csproj'
        self.assertEqual(sum(command[1] == 'test' and Path(command[2]) == expected for command in commands), 1)


if __name__ == '__main__':
    unittest.main()
