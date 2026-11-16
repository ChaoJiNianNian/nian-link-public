"""Offline synthetic tests for the isolated macOS blue test delivery."""
import hashlib
import importlib.util
import json
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]

def load(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

builder = load('mac_blue_builder', 'scripts/build-mac-blue-delivery.py')
bootstrap = load('mac_blue_bootstrap', 'scripts/mac-blue-test-bootstrap.py')


class BlueDeliveryTests(unittest.TestCase):
    def test_delivery_contains_checked_archive_executable_launcher_and_explanation(self):
        with tempfile.TemporaryDirectory() as d:
            folder = Path(d); archive = folder / 'mac.zip'; archive.write_bytes(b'synthetic zip')
            stage = folder / 'stage.py'; stage.write_bytes(b'synthetic stage')
            boot = folder / 'boot.py'; boot.write_bytes(b'synthetic bootstrap')
            out = folder / 'delivery.zip'
            expected = hashlib.sha256(archive.read_bytes()).hexdigest()
            builder.build_archive(archive, stage, boot, out, expected)
            with zipfile.ZipFile(out) as z:
                self.assertEqual({'NianDeskLink-test.zip', 'stage-mac-test.py', 'mac-blue-test-bootstrap.py',
                                  'package-manifest.json', '安装并启动.command', '使用说明.txt'}, set(z.namelist()))
                self.assertTrue((z.getinfo('安装并启动.command').external_attr >> 16) & stat.S_IXUSR)
                command = z.read('安装并启动.command').decode('utf-8')
                self.assertIn("/usr/bin/python3 -c 'import sys'", command)
                self.assertIn('系统 Python 3 不可用', command)
                manifest = json.loads(z.read('package-manifest.json'))
                self.assertEqual(expected, manifest['archiveSha256'])
                self.assertEqual(hashlib.sha256(stage.read_bytes()).hexdigest(), manifest['stageSha256'])
                readme = z.read('使用说明.txt').decode('utf-8')
                self.assertIn('Deskflow', readme); self.assertIn('未公证', readme)

    def test_bad_hash_or_existing_delivery_never_overwrites(self):
        with tempfile.TemporaryDirectory() as d:
            folder = Path(d); archive = folder / 'mac.zip'; archive.write_bytes(b'archive')
            stage = folder / 'stage.py'; stage.write_bytes(b'stage')
            boot = folder / 'boot.py'; boot.write_bytes(b'bootstrap')
            out = folder / 'delivery.zip'
            with self.assertRaises(ValueError): builder.build_archive(archive, stage, boot, out, '0' * 64)
            self.assertFalse(out.exists())
            out.write_bytes(b'keep existing')
            with self.assertRaises(FileExistsError):
                builder.build_archive(archive, stage, boot, out, hashlib.sha256(b'archive').hexdigest())
            self.assertEqual(b'keep existing', out.read_bytes())

    def test_incomplete_delivery_is_removed_when_archive_write_fails(self):
        with tempfile.TemporaryDirectory() as d:
            folder = Path(d); archive = folder / 'mac.zip'; archive.write_bytes(b'archive')
            stage = folder / 'stage.py'; stage.write_bytes(b'stage')
            boot = folder / 'boot.py'; boot.write_bytes(b'bootstrap')
            out = folder / 'delivery.zip'
            with patch.object(builder.zipfile, 'ZipFile', side_effect=OSError('simulated write error')):
                with self.assertRaises(OSError):
                    builder.build_archive(archive, stage, boot, out, hashlib.sha256(b'archive').hexdigest())
            self.assertFalse(out.exists())

    def fixture(self, root):
        package = root / 'package'; package.mkdir()
        archive = package / 'NianDeskLink-test.zip'; archive.write_bytes(b'synthetic zip')
        helper = package / 'stage-mac-test.py'; helper.write_bytes(b'synthetic helper')
        manifest = {'channel':'mac-blue-internal-test', 'archiveSha256': hashlib.sha256(archive.read_bytes()).hexdigest(),
                    'stageSha256':hashlib.sha256(helper.read_bytes()).hexdigest()}
        (package / 'package-manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
        home = root / 'user'; home.mkdir(); (home / 'Projects').mkdir()
        deskflow = home / 'Applications/Deskflow.app/Contents/MacOS/deskflow-core'
        deskflow.parent.mkdir(parents=True); deskflow.write_bytes(b'synthetic engine')
        return package, home, archive

    def test_stage_uses_fresh_private_profile_and_unique_launcher(self):
        with tempfile.TemporaryDirectory() as d:
            package, home, archive = self.fixture(Path(d)); calls = []
            label = 'nian-link-test-' + 'a' * 32
            target = home / 'Projects' / label
            def run(args):
                calls.append(args)
                if args[3] == 'prepare-discovery': target.mkdir(mode=0o700)
                if args[3] == 'create-discovery-profile': (target / 'profile').mkdir()
                if args[3] == 'create-blue-fileless-launcher':
                    (target / 'launch-fileless-helper.py').write_bytes((package / 'stage-mac-test.py').read_bytes())
                return None
            result = bootstrap.install(package, home, '/usr/bin/python3', run, lambda: 'a' * 32,
                                       lambda _: True)
            self.assertEqual(label, result['label'])
            self.assertEqual(archive.read_bytes(), (target / 'NianDeskLink-test.zip').read_bytes())
            self.assertEqual(['prepare-discovery', 'extract', 'create-discovery-profile', 'create-blue-fileless-launcher'],
                             [args[3] for args in calls])
            self.assertEqual('--fileless-test', calls[1][-1])
            self.assertTrue((target / 'profile').is_dir())
            self.assertEqual(str(target / 'launch-fileless-helper.py'), result['helper'])

    def test_tampered_archive_or_missing_deskflow_creates_no_test_directory(self):
        with tempfile.TemporaryDirectory() as d:
            package, home, archive = self.fixture(Path(d)); calls = []
            archive.write_bytes(b'changed')
            with self.assertRaises(ValueError):
                bootstrap.install(package, home, '/usr/bin/python3', calls.append, lambda: 'b' * 32, lambda _: True)
            self.assertEqual([], calls); self.assertEqual([], list((home / 'Projects').iterdir()))
        with tempfile.TemporaryDirectory() as d:
            package, home, archive = self.fixture(Path(d)); calls = []
            with self.assertRaises(FileNotFoundError):
                bootstrap.install(package, home, '/usr/bin/python3', calls.append, lambda: 'b' * 32, lambda _: False)
            self.assertEqual([], calls); self.assertEqual([], list((home / 'Projects').iterdir()))

    def test_runtime_rejects_unsupported_macos_before_staging(self):
        bootstrap.verify_runtime('darwin', 'arm64', '14.0', lambda _: True)
        bootstrap.verify_runtime('darwin', 'arm64', '15.1.2', lambda _: True)
        for system, machine, version in [('darwin', 'arm64', '13.6'), ('darwin', 'x86_64', '14.0'),
                                         ('win32', 'arm64', '14.0'), ('darwin', 'arm64', ''),
                                         ('darwin', 'arm64', 'bad')]:
            with self.subTest(system=system, machine=machine, version=version):
                with self.assertRaises(RuntimeError): bootstrap.verify_runtime(system, machine, version, lambda _: True)
        with self.assertRaises(RuntimeError): bootstrap.verify_runtime('darwin', 'arm64', '14.0', lambda _: False)

    def test_existing_target_or_tampered_target_helper_is_never_launched(self):
        with tempfile.TemporaryDirectory() as d:
            package, home, archive = self.fixture(Path(d)); calls = []
            target = home / 'Projects' / ('nian-link-test-' + 'c' * 32)
            target.mkdir(); marker = target / 'existing.txt'; marker.write_bytes(b'do not replace')
            def reject_existing(args):
                calls.append(args)
                if args[3] == 'prepare-discovery': raise FileExistsError('existing test')
            with self.assertRaises(FileExistsError):
                bootstrap.install(package, home, '/usr/bin/python3', reject_existing, lambda: 'c' * 32, lambda _: True)
            self.assertEqual(b'do not replace', marker.read_bytes())
            self.assertEqual(['prepare-discovery'], [args[3] for args in calls])
        with tempfile.TemporaryDirectory() as d:
            package, home, archive = self.fixture(Path(d)); calls = []
            target = home / 'Projects' / ('nian-link-test-' + 'd' * 32)
            def changed_helper(args):
                calls.append(args)
                if args[3] == 'prepare-discovery': target.mkdir(mode=0o700)
                if args[3] == 'create-discovery-profile': (target / 'profile').mkdir()
                if args[3] == 'create-blue-fileless-launcher': (target / 'launch-fileless-helper.py').write_bytes(b'tampered')
            with self.assertRaisesRegex(ValueError, '启动工具'):
                bootstrap.install(package, home, '/usr/bin/python3', changed_helper, lambda: 'd' * 32, lambda _: True)
            self.assertFalse(any('launch-blue-fileless' in args for args in calls))

if __name__ == '__main__': unittest.main()
