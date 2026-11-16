"""Keep every release packager and CI on the independently validated SDK."""
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]

class SdkBaselineTests(unittest.TestCase):
    def test_sdk_check_uses_project_directory_and_restores_caller(self):
        pwsh = shutil.which('pwsh')
        if not pwsh:
            self.skipTest('PowerShell 7 required')
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / 'project'
            caller = Path(tmp) / 'caller'
            root.mkdir(); caller.mkdir()
            # Canonicalize 8.3 names before PowerShell stores and restores locations.
            root = root.resolve(strict=True)
            caller = caller.resolve(strict=True)
            (root / 'global.json').write_text('{"version":"10.0.404"}')
            (caller / 'global.json').write_text('{"version":"10.0.999"}')
            program = r'''
param($module, $root, $caller)
$ErrorActionPreference = 'Stop'
function global:FakeDotnet {
    $global:LASTEXITCODE = $global:fakeExitCode
    (Get-Content -LiteralPath (Join-Path (Get-Location).Path 'global.json') -Raw | ConvertFrom-Json).version
}
Import-Module $module -Force
$global:fakeExitCode = 0
Push-Location $caller
try {
    Assert-NianSdk -Dotnet FakeDotnet -ProjectRoot $root
    if ((Get-Location).Path -ne $caller) { throw 'Caller location changed' }
    '{"version":"10.0.999"}' | Set-Content -LiteralPath (Join-Path $root 'global.json')
    try { Assert-NianSdk -Dotnet FakeDotnet -ProjectRoot $root; throw 'Wrong version accepted' }
    catch { if ($_.Exception.Message -notmatch '^Expected \.NET SDK 10\.0\.404') { throw } }
    if ((Get-Location).Path -ne $caller) { throw 'Caller location changed on error' }
    '{"version":"10.0.404"}' | Set-Content -LiteralPath (Join-Path $root 'global.json')
    $global:fakeExitCode = 1
    try { Assert-NianSdk -Dotnet FakeDotnet -ProjectRoot $root; throw 'Bad exit code accepted' }
    catch { if ($_.Exception.Message -notmatch '^Expected \.NET SDK 10\.0\.404') { throw } }
    if ((Get-Location).Path -ne $caller) { throw 'Caller location changed after failed SDK execution' }
} finally { Pop-Location }
'''
            script = Path(tmp) / 'test.ps1'
            script.write_text(program, encoding='utf-8')
            result = subprocess.run([pwsh, '-NoProfile', '-File', str(script), str(ROOT / 'scripts/SdkBaseline.psm1'), str(root), str(caller)], capture_output=True, text=True, encoding='utf-8', errors='replace')
            self.assertEqual(result.returncode, 0, f'PowerShell exited {result.returncode}; stdout:\n{result.stdout[-3000:]}\nstderr:\n{result.stderr[-3000:]}')

    def test_sdk_module_supports_actual_windows_short_paths(self):
        import os
        if os.name != 'nt':
            self.skipTest('Windows 8.3 paths required')
        import ctypes
        pwsh = shutil.which('pwsh')
        if not pwsh:
            self.skipTest('PowerShell 7 required')
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp) / '.work'
            base.mkdir()
            root = base / 'synthetic-project'
            caller = base / 'synthetic-caller'
            root.mkdir(); caller.mkdir()
            root, caller = root.resolve(strict=True), caller.resolve(strict=True)
            def short_path(path):
                buffer = ctypes.create_unicode_buffer(32768)
                length = ctypes.windll.kernel32.GetShortPathNameW(str(path), buffer, len(buffer))
                if not length or length >= len(buffer):
                    self.skipTest('Windows short path not available')
                return buffer.value
            short_root, short_caller = short_path(root), short_path(caller)
            if short_caller.casefold() == str(caller).casefold():
                self.skipTest('8.3 names are disabled on this volume')
            (root / 'global.json').write_text('{"version":"10.0.404"}', encoding='utf-8')
            program = r'''
param($module, $root, $caller, $expectedCaller)
$ErrorActionPreference = 'Stop'
function global:FakeDotnet {
    $global:LASTEXITCODE = $global:fakeExitCode
    (Get-Content -LiteralPath (Join-Path (Get-Location).Path 'global.json') -Raw | ConvertFrom-Json).version
}
Import-Module $module -Force
$global:fakeExitCode = 0
Push-Location $caller
try {
    Assert-NianSdk -Dotnet FakeDotnet -ProjectRoot $root
    if ((Get-Location).Path -ne $expectedCaller) { throw 'Actual caller directory not restored' }
    '{"version":"10.0.999"}' | Set-Content -LiteralPath (Join-Path $root 'global.json')
    try { Assert-NianSdk -Dotnet FakeDotnet -ProjectRoot $root; throw 'Wrong version accepted' }
    catch { if ($_.Exception.Message -notmatch '^Expected \.NET SDK 10\.0\.404') { throw } }
    if ((Get-Location).Path -ne $expectedCaller) { throw 'Caller not restored after rejection' }
    '{"version":"10.0.404"}' | Set-Content -LiteralPath (Join-Path $root 'global.json')
    $global:fakeExitCode = 1
    try { Assert-NianSdk -Dotnet FakeDotnet -ProjectRoot $root; throw 'Bad exit code accepted' }
    catch { if ($_.Exception.Message -notmatch '^Expected \.NET SDK 10\.0\.404') { throw } }
    if ((Get-Location).Path -ne $expectedCaller) { throw 'Caller not restored after failed execution' }
} finally { Pop-Location }
'''
            script = Path(tmp) / 'short-test.ps1'
            script.write_text(program, encoding='utf-8')
            result = subprocess.run([pwsh, '-NoProfile', '-File', str(script), str(ROOT / 'scripts/SdkBaseline.psm1'), short_root, short_caller, str(caller)], capture_output=True, text=True, encoding='utf-8', errors='replace')
            self.assertEqual(result.returncode, 0, f'PowerShell exited {result.returncode}; stdout:\n{result.stdout[-3000:]}\nstderr:\n{result.stderr[-3000:]}')

    def test_sdk_module_handles_psdrive_and_literal_bracket_paths(self):
        import os
        if os.name != 'nt':
            self.skipTest('Windows file-system provider required')
        import ctypes
        pwsh = shutil.which('pwsh')
        if not pwsh:
            self.skipTest('PowerShell 7 required')
        with tempfile.TemporaryDirectory() as tmp:
            base = (Path(tmp) / '.work')
            base.mkdir()
            base = base.resolve(strict=True)
            project = base / 'project[a]'
            caller = base / 'caller[z]'
            trap = base / 'projecta'
            project.mkdir(); caller.mkdir(); trap.mkdir()
            (project / 'global.json').write_text('{"version":"10.0.404"}', encoding='utf-8')
            (trap / 'global.json').write_text('{"version":"10.0.999"}', encoding='utf-8')
            buffer = ctypes.create_unicode_buffer(32768)
            length = ctypes.windll.kernel32.GetShortPathNameW(str(base), buffer, len(buffer))
            if not length or length >= len(buffer):
                self.skipTest('Windows short path not available')
            program = r'''
param($module, $base, $shortBase)
$ErrorActionPreference = 'Stop'
function global:FakeDotnet {
    $global:LASTEXITCODE = $global:fakeExitCode
    (Get-Content -LiteralPath (Join-Path (Get-Location).Path 'global.json') -Raw | ConvertFrom-Json).version
}
Import-Module $module -Force
$global:fakeExitCode = 0
foreach ($driveRoot in @($base, $shortBase)) {
    New-PSDrive -Name NianSynthetic -PSProvider FileSystem -Root $driveRoot | Out-Null
    try {
        Push-Location -LiteralPath 'NianSynthetic:\caller[z]'
        try {
            $expected = (Get-Location).Path
            Assert-NianSdk -Dotnet FakeDotnet -ProjectRoot 'NianSynthetic:\project[a]'
            if ((Get-Location).Path -ne $expected) { throw 'Custom PSDrive caller was not restored' }
            '{"version":"10.0.999"}' | Set-Content -LiteralPath 'NianSynthetic:\project[a]\global.json'
            try { Assert-NianSdk -Dotnet FakeDotnet -ProjectRoot 'NianSynthetic:\project[a]'; throw 'Wrong version accepted' }
            catch { if ($_.Exception.Message -notmatch '^Expected \.NET SDK 10\.0\.404') { throw } }
            if ((Get-Location).Path -ne $expected) { throw 'PSDrive caller not restored after rejection' }
            '{"version":"10.0.404"}' | Set-Content -LiteralPath 'NianSynthetic:\project[a]\global.json'
            $global:fakeExitCode = 1
            try { Assert-NianSdk -Dotnet FakeDotnet -ProjectRoot 'NianSynthetic:\project[a]'; throw 'Bad exit code accepted' }
            catch { if ($_.Exception.Message -notmatch '^Expected \.NET SDK 10\.0\.404') { throw } }
            if ((Get-Location).Path -ne $expected) { throw 'PSDrive caller not restored after failed execution' }
            $global:fakeExitCode = 0
        } finally { Pop-Location }
    } finally { Remove-PSDrive -Name NianSynthetic }
}
Push-Location -LiteralPath (Join-Path $base 'caller[z]')
try { Assert-NianSdk -Dotnet FakeDotnet -ProjectRoot (Join-Path $base 'project[a]') } finally { Pop-Location }
'''
            script = Path(tmp) / 'psdrive-test.ps1'
            script.write_text(program, encoding='utf-8')
            result = subprocess.run([pwsh, '-NoProfile', '-File', str(script), str(ROOT / 'scripts/SdkBaseline.psm1'), str(base), buffer.value], capture_output=True, text=True, encoding='utf-8', errors='replace')
            self.assertEqual(result.returncode, 0, f'PowerShell exited {result.returncode}; stdout:\n{result.stdout[-3000:]}\nstderr:\n{result.stderr[-3000:]}')

    def test_all_packagers_check_sdk_in_project_directory(self):
        for name in ('build-mac-test.ps1', 'build-windows-installer.ps1', 'build-multidevice-windows-test.ps1'):
            with self.subTest(script=name):
                text = (ROOT / 'scripts' / name).read_text(encoding='utf-8')
                self.assertIn('Assert-NianSdk -Dotnet $Dotnet -ProjectRoot $root', text)
                self.assertIn('Push-Location -LiteralPath $root', text)

    def test_all_packagers_require_validated_sdk(self):
        for name in ("build-mac-test.ps1", "build-windows-installer.ps1", "build-multidevice-windows-test.ps1"):
            with self.subTest(script=name):
                text = (ROOT / "scripts" / name).read_text(encoding="utf-8")
                self.assertIn('Assert-NianSdk', text)
                self.assertNotIn("10.0.402", text)

    def test_ci_uses_same_sdk_for_install_and_exact_pin(self):
        workflow = (ROOT / ".github/workflows/managed-ci.yml").read_text(encoding="utf-8")
        self.assertRegex(workflow, r"dotnet-version:\s*['\"]10\.0\.404['\"]")
        self.assertIn('"version":"10.0.404","rollForward":"disable"', workflow)
        self.assertNotIn("10.0.402", workflow)

if __name__ == "__main__":
    unittest.main()
