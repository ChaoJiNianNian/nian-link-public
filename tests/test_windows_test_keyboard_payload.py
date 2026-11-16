import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
MODULE = ROOT / 'scripts/WindowsTestKeyboardPayload.psm1'
PWSH = shutil.which('pwsh')
EXPECTED = {
    'deskflow-core.exe': '04ce02fb644b2e3e80a23f50077aaca1168c5a1ea520e5d45e48aa68104cfffa',
    'libcrypto-3-x64.dll': '6ed47166ec5b90ed4fa6b95cef063a7e95380ec451ef3e76be7b356422ff996b',
    'libssl-3-x64.dll': '134027937006c66800d81c45d831ed34c25422acd4b6c165e30ca76fae128438',
    'Qt6Core.dll': '0cc62e841959fb8f6d366ea116c6f6bbc273e30f0ef90d8aa2e4adeb81fd63f8',
    'Qt6Gui.dll': 'ca9f20d9b59d5bc321a87005b926b853ebcd6a2394823e14d3c8b5fc5702fa8c',
    'Qt6Network.dll': '2c2f65c70989646f06d668883c031f06fac3f8de1f459278c94cc50dfe89988d',
    'Qt6Widgets.dll': 'd34ef78b3d3b5506ee8279ce6efbfc263d773b74860af76e66b0715346897a3a',
    'platforms/qoffscreen.dll': '733d193144fb9c8a00b6d5e96c730668c86aef16db63754914d11e135d56e5a2',
    'platforms/qwindows.dll': '12577a7c4f2230bbea53f279573d7e3d1a08d631197855481e1e1580f61bb877',
}


@unittest.skipUnless(PWSH and os.name == 'nt', 'requires Windows PowerShell 7')
class WindowsTestKeyboardPayloadTests(unittest.TestCase):
    def make_fixture(self, root):
        source = root / 'source'
        output = root / 'output'
        source.mkdir()
        output.mkdir()
        rows = []
        for path, content in [('deskflow-core.exe', b'core'), ('Qt6Core.dll', b'qt'),
                              ('platforms/qwindows.dll', b'plugin')]:
            file = source / path
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_bytes(content)
            rows.append({'path': path, 'sha256': hashlib.sha256(content).hexdigest()})
        return source, output, rows

    def invoke(self, source, output, rows, operation='Copy-TestEngineItems', archive=None):
        env = os.environ | {'TEST_MODULE': str(MODULE), 'TEST_SOURCE': str(source),
                            'TEST_OUTPUT': str(output), 'TEST_ROWS': json.dumps(rows),
                            'TEST_OPERATION': operation, 'TEST_ARCHIVE': str(archive or '')}
        cmd = ("$ErrorActionPreference='Stop'; Import-Module $env:TEST_MODULE -ErrorAction Stop; "
               "& (Get-Module WindowsTestKeyboardPayload) { "
               "$rows = @(ConvertFrom-Json -InputObject $env:TEST_ROWS); "
               "if ($env:TEST_OPERATION -eq 'Copy-TestEngineItems') "
               "{ Copy-TestEngineItems -Source $env:TEST_SOURCE -Destination $env:TEST_OUTPUT -Entries $rows } "
               "elseif ($env:TEST_OPERATION -eq 'Assert-TestEngineItems') "
               "{ Assert-TestEngineItems -Directory $env:TEST_OUTPUT -Entries $rows } "
               "elseif ($env:TEST_OPERATION -eq 'Assert-TestEngineArchive') { "
               "$zip = [IO.Compression.ZipFile]::OpenRead($env:TEST_ARCHIVE); "
               "try { $files = @{}; foreach ($item in $zip.Entries) { $files[$item.FullName] = $item }; "
               "Assert-TestEngineArchive -Entries $files -PinnedItems $rows } finally { $zip.Dispose() } } "
               "else { Get-WindowsTestEngineManifest | ConvertTo-Json -Compress } }")
        return subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', cmd],
                              env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')

    def test_fixed_manifest_matches_the_reviewed_nine_files(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, output, rows = self.make_fixture(Path(tmp))
            result = self.invoke(source, output, rows, operation='manifest')
            self.assertEqual(result.returncode, 0, result.stderr)
            entries = json.loads(result.stdout)
            actual = {row['path']: row['sha256'].lower() for row in entries}
            self.assertEqual(actual, EXPECTED)
            self.assertEqual(len(entries), len(actual))

    def test_copies_all_pinned_files_and_detects_output_tampering(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, output, rows = self.make_fixture(Path(tmp))
            result = self.invoke(source, output, rows)
            self.assertEqual(result.returncode, 0, result.stderr)
            for row in rows:
                self.assertEqual((source / row['path']).read_bytes(), (output / row['path']).read_bytes())
            self.assertEqual(self.invoke(source, output, rows, 'Assert-TestEngineItems').returncode, 0)
            (output / 'platforms/qwindows.dll').write_bytes(b'tampered')
            self.assertNotEqual(self.invoke(source, output, rows, 'Assert-TestEngineItems').returncode, 0)

    def test_missing_or_tampered_input_fails_before_copy(self):
        for mode in ('missing', 'changed'):
            for relative in ('deskflow-core.exe', 'Qt6Core.dll', 'platforms/qwindows.dll'):
                with self.subTest(mode=mode, relative=relative), tempfile.TemporaryDirectory() as tmp:
                    source, output, rows = self.make_fixture(Path(tmp))
                    path = source / relative
                    if mode == 'missing': path.unlink()
                    else: path.write_bytes(b'tampered')
                    self.assertNotEqual(self.invoke(source, output, rows).returncode, 0)
                    self.assertEqual(list(output.iterdir()), [])

    def test_existing_output_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, output, rows = self.make_fixture(Path(tmp))
            existing = output / 'Qt6Core.dll'
            existing.write_bytes(b'keep')
            self.assertNotEqual(self.invoke(source, output, rows).returncode, 0)
            self.assertEqual(existing.read_bytes(), b'keep')
            self.assertFalse((output / 'deskflow-core.exe').exists())

    def test_linked_source_parent_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, output, rows = self.make_fixture(Path(tmp))
            directory = source / 'platforms'
            real = source / 'platforms-real'
            directory.rename(real)
            result = subprocess.run(['cmd', '/c', 'mklink', '/J', str(directory), str(real)], capture_output=True)
            if result.returncode != 0: self.skipTest('directory junction unavailable')
            self.assertNotEqual(self.invoke(source, output, rows).returncode, 0)
            self.assertEqual(list(output.iterdir()), [])

    def test_linked_output_parent_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, output, rows = self.make_fixture(Path(tmp))
            outside = Path(tmp) / 'outside'
            outside.mkdir()
            link = output / 'platforms'
            result = subprocess.run(['cmd', '/c', 'mklink', '/J', str(link), str(outside)], capture_output=True)
            if result.returncode != 0: self.skipTest('directory junction unavailable')
            self.assertNotEqual(self.invoke(source, output, rows).returncode, 0)
            self.assertEqual(list(outside.iterdir()), [])
            self.assertFalse((output / 'deskflow-core.exe').exists())

    def test_relative_traversal_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, output, rows = self.make_fixture(Path(tmp))
            rows[0]['path'] = '../outside.dll'
            self.assertNotEqual(self.invoke(source, output, rows).returncode, 0)
            self.assertEqual(list(output.iterdir()), [])

    def test_archive_must_contain_the_pinned_payload_not_just_staged_manifest(self):
        with tempfile.TemporaryDirectory() as tmp:
            source, output, rows = self.make_fixture(Path(tmp))
            good = Path(tmp) / 'good.zip'
            bad = Path(tmp) / 'bad.zip'
            missing = Path(tmp) / 'missing.zip'
            extra = Path(tmp) / 'extra.zip'
            marker_bad = Path(tmp) / 'marker-bad.zip'
            marker_missing = Path(tmp) / 'marker-missing.zip'
            for archive, alteration in [(good, None), (bad, 'tamper'), (missing, 'omit'),
                                        (extra, 'extra'), (marker_bad, 'marker-bad'), (marker_missing, 'marker-missing')]:
                with zipfile.ZipFile(archive, 'w') as zipped:
                    for row in rows:
                        name = row['path']
                        if name == 'platforms/qwindows.dll' and alteration == 'omit': continue
                        data = (source / name).read_bytes()
                        if name == 'platforms/qwindows.dll' and alteration == 'tamper': data = b'wrong'
                        zipped.writestr('Keyboard/' + name, data)
                    if alteration != 'marker-missing':
                        marker = (ROOT / 'vendor/deskflow/original/settings/Deskflow.conf').read_bytes()
                        zipped.writestr('Keyboard/settings/Deskflow.conf', b'wrong' if alteration == 'marker-bad' else marker)
                    if alteration == 'extra': zipped.writestr('Keyboard/injected.dll', b'wrong')
            self.assertEqual(self.invoke(source, output, rows, 'Assert-TestEngineArchive', good).returncode, 0)
            for archive in (bad, missing, extra, marker_bad, marker_missing):
                self.assertNotEqual(self.invoke(source, output, rows, 'Assert-TestEngineArchive', archive).returncode, 0)

    def test_builder_checks_all_inputs_before_publish_and_before_archive(self):
        build = (ROOT / 'scripts/build-multidevice-windows-test.ps1').read_text(encoding='utf-8')
        preflight = build.index('Assert-WindowsTestEngineInputs -Directory $engineRoot')
        copy = build.index('Copy-WindowsTestEngineInputs -Source $engineRoot -Destination $keyboard')
        final = build.index('Assert-WindowsTestEngineInputs -Directory $keyboard')
        self.assertLess(preflight, build.index("$publishArgs = @('publish'"))
        self.assertLess(preflight, copy)
        self.assertLess(copy, final)
        self.assertLess(final, build.index('Compress-Archive -LiteralPath $bundle'))
        self.assertIn('Assert-WindowsTestEngineArchive -Entries $entries', build)


if __name__ == '__main__':
    unittest.main()
