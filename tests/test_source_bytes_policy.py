"""Ensure Git autocrlf does not alter audited upstream license bytes."""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class SourceBytesPolicyTests(unittest.TestCase):
    def test_autocrlf_preserves_original_license_and_manifest_bytes(self):
        policy = (ROOT / '.gitattributes').read_bytes()
        contents = {
            'vendor/notices/qtbase-6.8.3/LICENSES/EXAMPLE.txt': b'qt-license\nsecond-line\n',
            'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/src/3rdparty/litehtml/LICENSE': b'notice A\nnotice B\n',
            'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/src/3rdparty/litehtml/src/gumbo/LICENSE': b'notice A\r\nnotice B\r\n',
            'vendor/notices/qttools-license-source-manifest.json': b'{\n  \"records\": []\n}\n',
            'vendor/notices/openssl-3.5.9/LICENSE.txt': b'openssl-license\nsecond-line\n',
            'vendor/notices/upstream-license-source-manifest.json': b'{\r\n  "records": []\r\n}\r\n',
            'vendor/deskflow/metadata/0001-control.patch': b'patch line one\r\npatch line two\r\n',
            'vendor/deskflow/metadata/licenses/Deskflow-LICENSE.txt': b'licence A\nlicence B\n',
            'vendor/notices/windows-runtime-packages/runtime.txt': b'notice A\r\nnotice B\r\n',
            'vendor/notices/dotnet-runtime-10.0.14/source-manifest.json': b'{\n  "records": []\n}\n',
            'vendor/notices/dotnet-runtime-10.0.14/microsoft.netcore.app.runtime.win-x64/LICENSE.TXT': b'license\r\nnotice\r\n',
            'vendor/notices/dotnet-runtime-10.0.15/source-manifest.json': b'{\n  "records": []\n}\n',
            'vendor/notices/dotnet-runtime-10.0.15/microsoft.netcore.app.runtime.win-x64/LICENSE.TXT': b'license\r\nnotice\r\n',
            'vendor/notices/windows-runtime-packages/source-manifest.json': b'{\n  "records": []\n}\n',
            'vendor/notices/windows-runtime-packages/Microsoft.NETCore.App.Runtime.win-x64-10.0.13/LICENSE.TXT': b'license\r\nnotice\r\n',
            'THIRD-PARTY-NOTICES/lucide-1.8.0.txt': b'notice C\nnotice D\n',
            'docs/screenshots/example.png': b'synthetic image bytes\nsecond line\n',
            'src/example.cs': b'class Example { }\n',
        }
        with tempfile.TemporaryDirectory() as d:
            base = Path(d)
            repo, checkout = base / 'repo', base / 'checkout'
            repo.mkdir(); checkout.mkdir()
            def git(*args):
                return subprocess.run(['git', '-c', 'core.autocrlf=true', *args], cwd=repo,
                                      capture_output=True, check=True).stdout
            git('init', '-q')
            (repo / '.gitattributes').write_bytes(policy)
            for name, data in contents.items():
                path = repo / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(data)
            git('add', '.gitattributes', *contents)
            for name, data in contents.items():
                self.assertEqual(git('show', ':' + name), data, name)
            git('checkout-index', '--all', '--prefix=' + str(checkout) + '/')
            for name, data in contents.items():
                expected = data.replace(b'\n', b'\r\n') if name == 'src/example.cs' else data
                self.assertEqual((checkout / name).read_bytes(), expected, name)


if __name__ == '__main__':
    unittest.main()
