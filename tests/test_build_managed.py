"""Contract checks for the public, source-only build entry point."""
from pathlib import Path
import importlib.util
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("build_managed", ROOT / "scripts" / "build-managed.py")
assert SPEC and SPEC.loader
BUILD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BUILD)


class ManagedBuildPlanTests(unittest.TestCase):
    def test_windows_build_uses_real_standalone_project_and_win_runtime(self):
        commands = BUILD.create_commands(ROOT, "dotnet", "windows", False, None)
        self.assertEqual(len(commands), 1)
        self.assertEqual(commands[0][0:2], ["dotnet", "build"])
        self.assertEqual(Path(commands[0][2]), ROOT / "src/NianLink.Windows/NianLink.Windows.csproj")
        self.assertIn("win-x64", commands[0])
        self.assertIn("false", commands[0])

    def test_mac_build_enables_existing_deskflow_preview_instead_of_native_candidate(self):
        commands = BUILD.create_commands(ROOT, "dotnet", "mac", False, None)
        self.assertEqual(Path(commands[0][2]), ROOT / "src/DesktopToolbox.Mac/DesktopToolbox.Mac.csproj")
        self.assertIn("osx-arm64", commands[0])
        self.assertIn("-p:PreviewControlEngine=true", commands[0])
        self.assertIn("-p:NativeInputEngine=false", commands[0])

    def test_tests_are_explicit_and_packages_are_passed_as_one_literal_argument(self):
        packages = Path("some directory") / "cache;literal"
        commands = BUILD.create_commands(ROOT, "dotnet", "windows", True, packages)
        self.assertGreater(len(commands), 1)
        self.assertTrue(all(command[1] == "test" for command in commands[1:]))
        self.assertTrue(all(f"-p:RestorePackagesPath={packages}" in command for command in commands))
        self.assertIn("DeskLink.Tests.csproj", " ".join(commands[1]))

    def test_invalid_target_is_rejected(self):
        with self.assertRaises(ValueError):
            BUILD.create_commands(ROOT, "dotnet", "linux", False, None)

    def test_multi_device_candidates_have_explicit_separate_build_switch(self):
        windows = BUILD.create_commands(ROOT, "dotnet", "windows", False, None, multi_device=True)
        mac = BUILD.create_commands(ROOT, "dotnet", "mac", False, None, multi_device=True)
        self.assertEqual(Path(windows[0][2]), ROOT / "src/NianLink.MultiDevice/NianLink.MultiDevice.csproj")
        self.assertIn("-p:MultiDeviceCandidate=true", mac[0])
        self.assertIn("-p:PreviewControlEngine=false", mac[0])

    def test_windows_only_dpapi_tests_are_in_windows_build_plan(self):
        windows = BUILD.create_commands(ROOT, "dotnet", "windows", True, None)
        mac = BUILD.create_commands(ROOT, "dotnet", "mac", True, None)
        self.assertTrue(any("MultiPeerTrust.Tests.csproj" in command[2] for command in windows[1:]))
        self.assertTrue(any("NianLink.Smoke.Tests.csproj" in command[2] for command in windows[1:]))
        self.assertTrue(any("MultiDevice.Windows.Tests.csproj" in command[2] for command in windows[1:]))
        self.assertFalse(any("MultiPeerTrust.Tests.csproj" in command[2] for command in mac[1:]))
        self.assertFalse(any("NianLink.Smoke.Tests.csproj" in command[2] for command in mac[1:]))
        self.assertFalse(any("MultiDevice.Windows.Tests.csproj" in command[2] for command in mac[1:]))

    def test_isolated_conditional_commit_runs_once_only_for_windows_with_tests(self):
        project = ROOT / "tests/ConditionalCommitIsolation.Tests/ConditionalCommitIsolation.Tests.csproj"
        for multi_device in (False, True):
            windows = BUILD.create_commands(ROOT, "dotnet", "windows", True, None, multi_device=multi_device)
            self.assertEqual([Path(cmd[2]) for cmd in windows[1:]].count(project), 1)
            self.assertTrue(all(Path(cmd[2]) != project for cmd in BUILD.create_commands(ROOT, "dotnet", "windows", False, None, multi_device=multi_device)))
            self.assertTrue(all(Path(cmd[2]) != project for cmd in BUILD.create_commands(ROOT, "dotnet", "mac", True, None, multi_device=multi_device)))


if __name__ == "__main__":
    unittest.main()
