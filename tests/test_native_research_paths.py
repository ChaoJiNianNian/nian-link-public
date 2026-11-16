"""Synthetic checks for research runners; never starts a Deskflow core."""
import shutil
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = [ROOT / "tests/experiments/NativePreviewFence/run-tests.ps1",
           ROOT / "tests/experiments/NativeFenceQt/run-tests.ps1"]

class NativeResearchPathTests(unittest.TestCase):
    def test_research_runners_fail_closed_without_explicit_paths(self):
        for script in SCRIPTS:
            with self.subTest(script=script.name, directory=script.parent.name):
                source = script.read_text(encoding="utf-8-sig")
                self.assertIn("Explicit isolated research paths are required", source)
                self.assertIn("Path is outside isolated work directory", source)
                self.assertNotIn("D:\\Projects\\nian-link", source)
                self.assertNotIn("D:\\NianToolboxBuildCache", source)
        shell = shutil.which("pwsh")
        if not shell:
            self.skipTest("PowerShell 7 is unavailable")
        for script in SCRIPTS:
            noargs = subprocess.run([shell, "-NoProfile", "-NonInteractive", "-File", str(script)], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
            self.assertNotEqual(noargs.returncode, 0)
            self.assertIn("Explicit isolated research paths are required", noargs.stderr)
            with tempfile.TemporaryDirectory() as temp:
                args = [shell, "-NoProfile", "-NonInteractive", "-File", str(script), "-Candidate", temp, "-CacheRoot", temp]
                if script.parent.name == "NativeFenceQt": args += ["-CoreCandidate", temp]
                invalid = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
                self.assertNotEqual(invalid.returncode, 0)
                self.assertIn("Path is outside isolated work directory", invalid.stderr)

    @unittest.skipUnless(os.name == "nt" and shutil.which("pwsh"), "Windows and PowerShell 7 are required")
    def test_research_runners_reject_nested_junction_before_cmake(self):
        work = ROOT / "tests/.work"
        work.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=work, prefix="junction-fixture-") as in_work, tempfile.TemporaryDirectory(prefix="nian-external-") as outside:
            in_work, outside = Path(in_work), Path(outside)
            shim = outside / "shim"; shim.mkdir()
            (shim / "cmake.cmd").write_text("@echo CALLED_CMAKE\r\nexit /b 77\r\n", encoding="ascii")
            environment = os.environ.copy()
            environment["PATH"] = str(shim) + os.pathsep + environment.get("PATH", "")
            for script in SCRIPTS:
                for tree in ("source", "build"):
                    with self.subTest(directory=script.parent.name, tree=tree):
                        candidate = in_work / (script.parent.name + "-" + tree); candidate.mkdir()
                        if script.parent.name == "NativePreviewFence":
                            source = candidate / "source/deskflow-081f6478e654a112168879e67a41ce7d7e4ae3af"
                            build = candidate / "build-02"
                        else:
                            source = candidate / "source"
                            build = candidate / "build-02"
                        source.mkdir(parents=True); build.mkdir()
                        link = (source if tree == "source" else build) / "src"
                        create = subprocess.run(["cmd.exe", "/d", "/c", "mklink", "/J", str(link), str(outside)], capture_output=True, text=True, encoding="utf-8", errors="replace")
                        if create.returncode:
                            self.skipTest("Cannot create Windows junction in this environment")
                        try:
                            args = [shutil.which("pwsh"), "-NoProfile", "-NonInteractive", "-File", str(script), "-Candidate", str(candidate), "-CacheRoot", str(outside)]
                            if script.parent.name == "NativeFenceQt": args += ["-CoreCandidate", str(candidate)]
                            result = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20, env=environment)
                            self.assertNotEqual(result.returncode, 0)
                            self.assertIn("Linked research tree is not allowed", result.stderr)
                            self.assertNotIn("CALLED_CMAKE", result.stdout, "CMake must not run before rejecting a junction")
                        finally:
                            os.rmdir(link)

if __name__ == "__main__": unittest.main()
