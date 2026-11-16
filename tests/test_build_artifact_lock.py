"""Exercise actual cross-process build exclusion without touching installed apps."""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
MODULE = ROOT / 'scripts/BuildArtifactLock.psm1'
PWSH = shutil.which('pwsh')


@unittest.skipUnless(PWSH and os.name == 'nt', 'requires Windows PowerShell 7')
class BuildLockTests(unittest.TestCase):
    def test_excludes_concurrent_build_and_releases(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ready, done = root / 'ready', root / 'done'
            env = os.environ | {'NIAN_TEST_MODULE': str(MODULE), 'NIAN_TEST_ROOT': str(root),
                                'NIAN_TEST_READY': str(ready), 'NIAN_TEST_DONE': str(done)}
            holder_script = ("Import-Module $env:NIAN_TEST_MODULE -ErrorAction Stop; "
                             "$l=Enter-ToolboxBuildLock -ProjectRoot $env:NIAN_TEST_ROOT; "
                             "try { [IO.File]::WriteAllText($env:NIAN_TEST_READY,'ready'); "
                             "while (-not [IO.File]::Exists($env:NIAN_TEST_DONE)) { Start-Sleep -Milliseconds 50 } } "
                             "finally { Exit-ToolboxBuildLock -Lock $l }")
            contender_script = ("Import-Module $env:NIAN_TEST_MODULE -ErrorAction Stop; "
                                "try { $l=Enter-ToolboxBuildLock -ProjectRoot $env:NIAN_TEST_ROOT; "
                                "Exit-ToolboxBuildLock -Lock $l; exit 0 } catch { exit 42 }")
            holder = subprocess.Popen([PWSH, '-NoProfile', '-NonInteractive', '-Command', holder_script], env=env,
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            try:
                deadline = time.monotonic() + 10
                while not ready.exists() and holder.poll() is None and time.monotonic() < deadline:
                    time.sleep(.05)
                self.assertTrue(ready.exists(), 'lock holder did not start')
                contender = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', contender_script],
                                           env=env, capture_output=True, timeout=10)
                self.assertEqual(contender.returncode, 42)
                done.touch()
                self.assertEqual(holder.wait(timeout=10), 0)
                retry = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', contender_script],
                                       env=env, capture_output=True, timeout=10)
                self.assertEqual(retry.returncode, 0)
            finally:
                if holder.poll() is None:
                    holder.terminate()
                    holder.wait(timeout=10)


if __name__ == '__main__':
    unittest.main()
