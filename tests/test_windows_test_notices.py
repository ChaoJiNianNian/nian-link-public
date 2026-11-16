import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
MODULE = ROOT / 'scripts/WindowsTestNotices.psm1'
PWSH = shutil.which('pwsh')
PAIRS = {
    'vendor/notices/dotnet-runtime-10.0.15/microsoft.netcore.app.runtime.win-x64/LICENSE.TXT': 'dotnet-core-10.0.15/LICENSE.TXT',
    'vendor/notices/dotnet-runtime-10.0.15/microsoft.netcore.app.runtime.win-x64/THIRD-PARTY-NOTICES.TXT': 'dotnet-core-10.0.15/THIRD-PARTY-NOTICES.TXT',
    'vendor/notices/dotnet-runtime-10.0.15/microsoft.windowsdesktop.app.runtime.win-x64/LICENSE.txt': 'dotnet-windowsdesktop-10.0.15/LICENSE.txt',
    'vendor/notices/webview2-license.txt': 'webview2-sdk-1.0.4258.31/LICENSE.txt',
    'vendor/notices/webview2-notice.txt': 'webview2-sdk-1.0.4258.31/NOTICE.txt',
    'vendor/notices/lucide-1.8.0.txt': 'lucide-1.8.0/LICENSE.txt',
    'vendor/deskflow/metadata/licenses/Deskflow-LICENSE.txt': 'deskflow/LICENSE.txt',
    'vendor/deskflow/metadata/licenses/LicenseRef-OpenSSL-Exception.txt': 'deskflow/LicenseRef-OpenSSL-Exception.txt',
}
README = ("Internal test package: PARTIAL third-party notices only.\n"
          "These files are not a complete license inventory or release approval.\n"
          "Deskflow, Qt, OpenSSL, Windows SDK, runtimes and other components still require review.\n"
          "Deskflow's OpenSSL exception applies only to files that explicitly declare the exception; it does not apply to the whole project or other components.\n"
          "Lucide source content is checked against the reviewed 1.8.0 package with CRLF normalized to LF; see project audit for verification limits.\n")

@unittest.skipUnless(PWSH and os.name == 'nt', 'requires Windows PowerShell 7')
class WindowsTestNoticesTests(unittest.TestCase):
    def setup_fixture(self, root):
        source = root / 'source'; bundle = root / 'bundle'
        source.mkdir(); bundle.mkdir()
        app = bundle / 'App'; app.mkdir()
        keyboard = bundle / 'Keyboard'; keyboard.mkdir()
        (keyboard / 'deskflow-core.exe').write_bytes(b'synthetic-reviewed-core')
        (app / 'NianLink.DiscoveryPortable.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'includedFrameworks': [{'name': 'Microsoft.NETCore.App', 'version': '10.0.15'}, {'name': 'Microsoft.WindowsDesktop.App', 'version': '10.0.15'}]}}), encoding='utf-8')
        (app / 'DisplayBridge.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'includedFrameworks': [{'name': 'Microsoft.NETCore.App', 'version': '10.0.15'}]}}), encoding='utf-8')
        (app / 'NianLink.DiscoveryPortable.deps.json').write_text(json.dumps({'libraries': {'Microsoft.Web.WebView2/1.0.4258.31': {}}}), encoding='utf-8')
        lucide = app / 'CollaborationUi/assets/lucide.min.js'
        lucide.parent.mkdir(parents=True)
        lucide_source = source / 'src/DesktopToolbox.Collaboration.Ui/assets/lucide.min.js'
        lucide_source.parent.mkdir(parents=True)
        shutil.copyfile(ROOT / 'src/DesktopToolbox.Collaboration.Ui/assets/lucide.min.js', lucide_source)
        shutil.copyfile(lucide_source, lucide)
        for path in PAIRS:
            target = source / path
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / path, target)
        return source, bundle

    def run_stage(self, source, bundle):
        env = os.environ | {'TEST_MODULE': str(MODULE), 'TEST_SOURCE': str(source), 'TEST_BUNDLE': str(bundle)}
        # Synthetic core substitution stays inside the imported module for this test
        # process only; production always uses the fixed reviewed core hash.
        cmd = r'''$ErrorActionPreference = 'Stop'
Import-Module $env:TEST_MODULE -ErrorAction Stop
& (Get-Module WindowsTestNotices) {
    function script:Get-FileHash {
        param([string]$LiteralPath, [string]$Algorithm)
        $core = Join-Path $env:TEST_BUNDLE 'Keyboard/deskflow-core.exe'
        if ([string]::Equals([IO.Path]::GetFullPath($LiteralPath), [IO.Path]::GetFullPath($core), [StringComparison]::OrdinalIgnoreCase) -and [IO.File]::Exists($core) -and [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($core)) -ceq 'synthetic-reviewed-core') {
            return [pscustomobject]@{ Hash = '04CE02FB644B2E3E80A23F50077AACA1168C5A1EA520E5D45E48AA68104CFFFA' }
        }
        Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $LiteralPath -Algorithm $Algorithm
    }
}
Add-WindowsTestNotices -ProjectRoot $env:TEST_SOURCE -Bundle $env:TEST_BUNDLE'''
        return subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', cmd], env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')

    def test_stages_exact_unchanged_notices_with_partial_status(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, bundle = self.setup_fixture(Path(tmp))
            result = self.run_stage(source, bundle)
            self.assertEqual(result.returncode, 0, result.stderr)
            out = bundle / 'THIRD-PARTY-NOTICES'
            for original, dest in PAIRS.items():
                self.assertEqual((source / original).read_bytes(), (out / dest).read_bytes())
            readme = (out / 'README.txt').read_text(encoding='utf-8-sig')
            self.assertIn('PARTIAL', readme.upper())
            self.assertIn('TEST', readme.upper())
            self.assertIn('only to files that explicitly declare', readme.lower())
            self.assertEqual({p.relative_to(out).as_posix() for p in out.rglob('*') if p.is_file()}, set(PAIRS.values()) | {'README.txt'})

    def test_missing_or_changed_input_is_rejected_before_output(self):
        for mode in ('missing', 'changed'):
            for relative in ('vendor/notices/dotnet-runtime-10.0.15/microsoft.netcore.app.runtime.win-x64/LICENSE.TXT',
                             'vendor/deskflow/metadata/licenses/Deskflow-LICENSE.txt',
                             'vendor/deskflow/metadata/licenses/LicenseRef-OpenSSL-Exception.txt'):
                with self.subTest(mode=mode, relative=relative), tempfile.TemporaryDirectory() as tmp:
                    source, bundle = self.setup_fixture(Path(tmp))
                    item = source / relative
                    if mode == 'missing': item.unlink()
                    else: item.write_bytes(b'changed')
                    self.assertNotEqual(self.run_stage(source, bundle).returncode, 0)
                    self.assertFalse((bundle / 'THIRD-PARTY-NOTICES').exists())

    def test_core_missing_or_changed_cannot_receive_deskflow_notices(self):
        for mode in ('missing', 'changed'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as tmp:
                source, bundle = self.setup_fixture(Path(tmp))
                core = bundle / 'Keyboard/deskflow-core.exe'
                if mode == 'missing': core.unlink()
                else: core.write_bytes(b'another-core')
                self.assertNotEqual(self.run_stage(source, bundle).returncode, 0)
                self.assertFalse((bundle / 'THIRD-PARTY-NOTICES').exists())

    def test_linked_deskflow_notice_or_keyboard_parent_is_rejected(self):
        for relative in ('vendor/deskflow/metadata/licenses', 'Keyboard'):
            with self.subTest(relative=relative), tempfile.TemporaryDirectory() as tmp:
                source, bundle = self.setup_fixture(Path(tmp))
                target = (source if relative.startswith('vendor/') else bundle) / relative
                real = target.with_name(target.name + '-real')
                target.rename(real)
                proc = subprocess.run(['cmd', '/c', 'mklink', '/J', str(target), str(real)], capture_output=True)
                if proc.returncode != 0: self.skipTest('directory junction unavailable')
                self.assertNotEqual(self.run_stage(source, bundle).returncode, 0)
                self.assertFalse((bundle / 'THIRD-PARTY-NOTICES').exists())

    def test_payload_versions_and_lucide_identity_must_match(self):
        cases = {
            'NianLink.DiscoveryPortable.runtimeconfig.json': {'runtimeOptions': {'includedFrameworks': [{'name': 'Microsoft.NETCore.App', 'version': '10.0.16'}, {'name': 'Microsoft.WindowsDesktop.App', 'version': '10.0.15'}]}},
            'DisplayBridge.runtimeconfig.json': {'runtimeOptions': {'includedFrameworks': [{'name': 'Microsoft.NETCore.App', 'version': '10.0.16'}]}},
            'NianLink.DiscoveryPortable.deps.json': {'libraries': {'Microsoft.Web.WebView2/2.0.0': {}}},
        }
        for name, content in cases.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as tmp:
                source, bundle = self.setup_fixture(Path(tmp))
                (bundle / 'App' / name).write_text(json.dumps(content), encoding='utf-8')
                self.assertNotEqual(self.run_stage(source, bundle).returncode, 0)
                self.assertFalse((bundle / 'THIRD-PARTY-NOTICES').exists())
        with tempfile.TemporaryDirectory() as tmp:
            source, bundle = self.setup_fixture(Path(tmp))
            (bundle / 'App/CollaborationUi/assets/lucide.min.js').write_bytes(b'changed')
            self.assertNotEqual(self.run_stage(source, bundle).returncode, 0)
            self.assertFalse((bundle / 'THIRD-PARTY-NOTICES').exists())

    def test_existing_output_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, bundle = self.setup_fixture(Path(tmp))
            out = bundle / 'THIRD-PARTY-NOTICES'; out.mkdir()
            sentinel = out / 'sentinel.txt'; sentinel.write_bytes(b'keep')
            self.assertNotEqual(self.run_stage(source, bundle).returncode, 0)
            self.assertEqual(sentinel.read_bytes(), b'keep')

    def test_lucide_changed_in_source_and_payload_is_rejected_despite_header(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, bundle = self.setup_fixture(Path(tmp))
            src = source / 'src/DesktopToolbox.Collaboration.Ui/assets/lucide.min.js'
            payload = bundle / 'App/CollaborationUi/assets/lucide.min.js'
            changed = src.read_bytes() + b'\n// changed\n'
            src.write_bytes(changed); payload.write_bytes(changed)
            self.assertNotEqual(self.run_stage(source, bundle).returncode, 0)
            self.assertFalse((bundle / 'THIRD-PARTY-NOTICES').exists())

    def test_lucide_crlf_source_matches_the_normalized_official_content(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, bundle = self.setup_fixture(Path(tmp))
            src = source / 'src/DesktopToolbox.Collaboration.Ui/assets/lucide.min.js'
            payload = bundle / 'App/CollaborationUi/assets/lucide.min.js'
            crlf = src.read_bytes().replace(b'\r\n', b'\n').replace(b'\n', b'\r\n')
            src.write_bytes(crlf); payload.write_bytes(crlf)
            result = self.run_stage(source, bundle)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_linked_source_or_bundle_ancestor_is_rejected(self):
        for linked in ('source', 'bundle'):
            with self.subTest(linked=linked), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); source, bundle = self.setup_fixture(root)
                target = source / 'vendor/notices' if linked == 'source' else bundle
                real = target.with_name(target.name + '-real')
                target.rename(real)
                proc = subprocess.run(['cmd', '/c', 'mklink', '/J', str(target), str(real)], capture_output=True)
                if proc.returncode != 0: self.skipTest('directory junction unavailable')
                self.assertNotEqual(self.run_stage(source, bundle).returncode, 0)
                self.assertFalse((bundle / 'THIRD-PARTY-NOTICES').exists())

    def test_final_archive_pins_all_notices_and_partial_scope_readme(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for alteration in ('good', 'missing', 'changed', 'extra', 'readme-missing', 'readme-changed'):
                with self.subTest(alteration=alteration):
                    archive = root / (alteration + '.zip')
                    with zipfile.ZipFile(archive, 'w') as zipped:
                        for original, destination in PAIRS.items():
                            name = 'THIRD-PARTY-NOTICES/' + destination
                            if alteration == 'missing' and destination == 'deskflow/LICENSE.txt': continue
                            content = (ROOT / original).read_bytes()
                            if alteration == 'changed' and destination == 'deskflow/LicenseRef-OpenSSL-Exception.txt': content = b'changed'
                            zipped.writestr(name, content)
                        if alteration != 'readme-missing':
                            contents = b'\xef\xbb\xbf' + (('all licenses complete\n' if alteration == 'readme-changed' else README).encode('utf-8'))
                            zipped.writestr('THIRD-PARTY-NOTICES/README.txt', contents)
                        if alteration == 'extra': zipped.writestr('THIRD-PARTY-NOTICES/unreviewed.txt', b'extra')
                    env = os.environ | {'TEST_MODULE': str(MODULE), 'TEST_ARCHIVE': str(archive)}
                    command = r'''$ErrorActionPreference='Stop'
Import-Module $env:TEST_MODULE -ErrorAction Stop
$zip = [IO.Compression.ZipFile]::OpenRead($env:TEST_ARCHIVE)
try { $entries = @{}; foreach ($entry in $zip.Entries) { $entries[$entry.FullName] = $entry }; Assert-WindowsTestNoticesArchive -Entries $entries }
finally { $zip.Dispose() }'''
                    result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', command], env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
                    if alteration == 'good': self.assertEqual(result.returncode, 0, result.stderr)
                    else: self.assertNotEqual(result.returncode, 0)

    def test_builder_stages_notices_before_archive_and_manifest(self):
        build = (ROOT / 'scripts/build-multidevice-windows-test.ps1').read_text(encoding='utf-8')
        invocation = build.index('Add-WindowsTestNotices -ProjectRoot $root -Bundle $bundle')
        self.assertLess(invocation, build.index('Compress-Archive -LiteralPath $bundle'))
        self.assertLess(invocation, build.index('$rows = Get-ChildItem -LiteralPath $bundle'))
        self.assertIn('Assert-WindowsTestNoticesArchive -Entries $entries', build)

if __name__ == '__main__':
    unittest.main()
