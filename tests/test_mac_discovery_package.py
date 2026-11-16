"""Offline tests for discovery-test package and isolated staging."""
import importlib.util
import hashlib
import contextlib
import io
from pathlib import Path
import plistlib
import shlex
import stat
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

ROOT = Path(__file__).resolve().parents[1]
def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

package = load('discovery_package', 'src/DesktopToolbox.Mac/build/package_app.py')
stage = load('discovery_stage', 'scripts/stage-mac-test.py')


class DiscoveryPackageTests(unittest.TestCase):
    def test_discovery_bundle_identity_is_distinct_and_mode_conflicts_fail(self):
        original = {'CFBundleIdentifier': 'com.niannian.nianlink', 'CFBundleName': 'year'}
        selected = package.bundle_identity(original, discovery_test=True)
        self.assertEqual('com.niannian.nianlink.discovery-test', selected['CFBundleIdentifier'])
        self.assertEqual('年年互联·发现测试', selected['CFBundleDisplayName'])
        self.assertEqual('com.niannian.nianlink', original['CFBundleIdentifier'])
        with self.assertRaises(ValueError):
            package.bundle_identity(original, multi_device_test=True, discovery_test=True)

    def test_new_entry_restore_graph_uses_new_project_directory(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for directory in ('src/DesktopToolbox.Mac', 'src/DesktopToolbox.Collaboration.Ui',
                              'src/NianLink.MacDiscoveryCandidate', 'src/Nian.Input.Core'):
                (root / directory).mkdir(parents=True)
            (root / 'src/NianLink.MacDiscoveryCandidate/Entry.csproj').write_text('entry')
            (root / 'src/Nian.Input.Core/Core.csproj').write_text('core')
            graph = {'libraries': {'core/1': {'type': 'project', 'path': '../Nian.Input.Core/Core.csproj'}}}
            rows = package.collect_sources(root, graph, entry_project='src/NianLink.MacDiscoveryCandidate/Entry.csproj')
            self.assertIn('src/NianLink.MacDiscoveryCandidate/Entry.csproj', [x['path'] for x in rows])
            self.assertIn('src/Nian.Input.Core/Core.csproj', [x['path'] for x in rows])

    def test_discovery_uuid_is_domain_separated(self):
        helper = package.preview_identity_helpers()
        payload = 'A' * 64
        ids = {'com.niannian.nianlink', 'com.niannian.nianlink.multidevice-test', 'com.niannian.nianlink.discovery-test'}
        self.assertEqual(3, len({helper.derive_preview_executable_uuid(value, payload) for value in ids}))

    def test_conflicting_package_modes_fail_before_reading_publish(self):
        result = subprocess.run([sys.executable, str(ROOT / 'src/DesktopToolbox.Mac/build/package_app.py'),
                                 '--publish', 'ignored', '--output', 'ignored', '--version', '0.0.1',
                                 '--discovery-test', '--multi-device-test'], capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('mutually exclusive', result.stderr)

    def test_build_script_selects_the_distinct_entry_and_mode(self):
        source = (ROOT / 'scripts/build-mac-test.ps1').read_text(encoding='utf-8')
        self.assertIn('[switch]$DiscoveryTesting', source)
        self.assertIn('src/NianLink.MacDiscoveryCandidate/NianLink.MacDiscoveryCandidate.csproj', source)
        self.assertIn('--discovery-test', source)

    def test_discovery_signing_preserves_the_pinned_bridge(self):
        with tempfile.TemporaryDirectory() as folder:
            bundle = Path(folder) / 'NianDeskLink.app'
            binaries = bundle / 'Contents/MacOS'
            binaries.mkdir(parents=True)
            bridge = binaries / 'libNianWifiEvidence.dylib'
            bridge.write_bytes(bytes.fromhex('cffaedfe') + b'signed-bridge')
            helper = binaries / 'other.dylib'
            helper.write_bytes(bytes.fromhex('cffaedfe') + b'helper')
            (binaries / 'NianDeskLink').write_bytes(bytes.fromhex('cffaedfe') + b'host')
            pin = hashlib.sha256(bridge.read_bytes()).hexdigest()
            with patch.object(stage.subprocess, 'run') as run:
                stage.sign_discovery_bundle(bundle, pin)
            commands = [call.args[0] for call in run.call_args_list]
            self.assertIn(['/usr/bin/codesign', '--force', '--sign', '-', str(helper)], commands)
            self.assertIn(['/usr/bin/codesign', '--force', '--sign', '-', str(bundle)], commands)
            self.assertFalse(any('--force' in cmd and str(bridge) in cmd for cmd in commands))
            self.assertFalse(any('--deep' in cmd and '--sign' in cmd for cmd in commands))
            self.assertIn(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(bundle)], commands)

    def test_managed_assemblies_are_signed_before_bundle_without_presigning_apphost(self):
        with tempfile.TemporaryDirectory() as folder:
            bundle = Path(folder) / 'NianDeskLink.app'
            binaries = bundle / 'Contents/MacOS'
            binaries.mkdir(parents=True)
            bridge = binaries / 'libNianWifiEvidence.dylib'
            bridge.write_bytes(bytes.fromhex('cffaedfe') + b'bridge')
            host = binaries / 'NianDeskLink'
            host.write_bytes(bytes.fromhex('cffaedfe') + b'host')
            managed = binaries / 'System.Net.WebSockets.Client.dll'
            managed.write_bytes(b'MZ' + b'managed')
            native = binaries / 'libSystem.Native.dylib'
            native.write_bytes(bytes.fromhex('cffaedfe') + b'native')
            pin = hashlib.sha256(bridge.read_bytes()).hexdigest()
            with patch.object(stage.subprocess, 'run') as run:
                stage.sign_discovery_bundle(bundle, pin)
            commands = [call.args[0] for call in run.call_args_list]
            sign = lambda path: ['/usr/bin/codesign', '--force', '--sign', '-', str(path)]
            self.assertIn(sign(managed), commands)
            self.assertIn(sign(native), commands)
            self.assertLess(commands.index(sign(managed)), commands.index(sign(bundle)))
            self.assertLess(commands.index(sign(native)), commands.index(sign(bundle)))
            self.assertNotIn(sign(host), commands)
            self.assertNotIn(sign(bridge), commands)

    def test_resources_under_macos_are_signed_before_the_outer_app(self):
        with tempfile.TemporaryDirectory() as folder:
            bundle = Path(folder) / 'NianDeskLink.app'
            binaries = bundle / 'Contents/MacOS'
            ui = binaries / 'CollaborationUi'
            ui.mkdir(parents=True)
            bridge = binaries / 'libNianWifiEvidence.dylib'
            bridge.write_bytes(bytes.fromhex('cffaedfe') + b'bridge')
            (binaries / 'NianDeskLink').write_bytes(bytes.fromhex('cffaedfe') + b'host')
            resources = [ui / 'mini.js', ui / 'index.html', ui / 'mini.css', binaries / 'NianDeskLink.runtimeconfig.json']
            for resource in resources:
                resource.write_text('fixture', encoding='utf-8')
            pin = hashlib.sha256(bridge.read_bytes()).hexdigest()
            with patch.object(stage.subprocess, 'run') as run:
                stage.sign_discovery_bundle(bundle, pin)
            commands = [call.args[0] for call in run.call_args_list]
            sign = lambda path: ['/usr/bin/codesign', '--force', '--sign', '-', str(path)]
            for resource in resources:
                self.assertIn(sign(resource), commands)
                self.assertLess(commands.index(sign(resource)), commands.index(sign(bundle)))

    def test_discovery_signing_rejects_wrong_bridge_hash_before_signing(self):
        with tempfile.TemporaryDirectory() as folder:
            bundle = Path(folder) / 'NianDeskLink.app'
            binaries = bundle / 'Contents/MacOS'
            binaries.mkdir(parents=True)
            (binaries / 'libNianWifiEvidence.dylib').write_bytes(b'not the pinned bridge')
            with patch.object(stage.subprocess, 'run') as run:
                with self.assertRaises(ValueError):
                    stage.sign_discovery_bundle(bundle, 'a' * 64)
            run.assert_not_called()

    def test_managed_signing_failure_stops_before_signing_the_outer_app(self):
        with tempfile.TemporaryDirectory() as folder:
            bundle = Path(folder) / 'NianDeskLink.app'
            binaries = bundle / 'Contents/MacOS'
            binaries.mkdir(parents=True)
            bridge = binaries / 'libNianWifiEvidence.dylib'
            bridge.write_bytes(bytes.fromhex('cffaedfe') + b'bridge')
            (binaries / 'NianDeskLink').write_bytes(bytes.fromhex('cffaedfe') + b'host')
            managed = binaries / 'System.Net.WebSockets.Client.dll'
            managed.write_bytes(b'MZ' + b'managed')
            pin = hashlib.sha256(bridge.read_bytes()).hexdigest()
            def fail_managed(command, **kwargs):
                if '--sign' in command and command[-1] == str(managed):
                    raise subprocess.CalledProcessError(1, command)
            with patch.object(stage.subprocess, 'run', side_effect=fail_managed) as run:
                with self.assertRaises(subprocess.CalledProcessError):
                    stage.sign_discovery_bundle(bundle, pin)
            commands = [call.args[0] for call in run.call_args_list]
            self.assertNotIn(['/usr/bin/codesign', '--force', '--sign', '-', str(bundle)], commands)
            self.assertFalse(any('/usr/bin/open' in command for command in commands))

    def test_discovery_signing_rejects_bridge_changed_by_signing(self):
        with tempfile.TemporaryDirectory() as folder:
            bundle = Path(folder) / 'NianDeskLink.app'
            binaries = bundle / 'Contents/MacOS'
            binaries.mkdir(parents=True)
            bridge = binaries / 'libNianWifiEvidence.dylib'
            bridge.write_bytes(bytes.fromhex('cffaedfe') + b'signed-bridge')
            (binaries / 'NianDeskLink').write_bytes(bytes.fromhex('cffaedfe') + b'host')
            pin = hashlib.sha256(bridge.read_bytes()).hexdigest()
            def mutate_on_outer_sign(command, **kwargs):
                if '--sign' in command and command[-1] == str(bundle):
                    bridge.write_bytes(b'changed')
            with patch.object(stage.subprocess, 'run', side_effect=mutate_on_outer_sign):
                with self.assertRaises(ValueError):
                    stage.sign_discovery_bundle(bundle, pin)

    def test_packager_requires_a_matching_discovery_bridge(self):
        with tempfile.TemporaryDirectory() as folder:
            publish = Path(folder)
            bridge = publish / 'libNianWifiEvidence.dylib'
            bridge.write_bytes(b'signed fixture')
            pin = hashlib.sha256(bridge.read_bytes()).hexdigest()
            self.assertEqual(pin, package.require_discovery_bridge(publish, pin))
            with self.assertRaises(ValueError):
                package.require_discovery_bridge(publish, 'a' * 64)
            with self.assertRaises(ValueError):
                package.require_discovery_bridge(publish, None)

    def test_profile_requires_exact_random_label_and_exclusive_creation(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            root.mkdir()
            label = 'nian-link-test-' + 'a' * 32
            with patch.object(stage, 'require_private_owned', return_value=None):
                created = stage.prepare_discovery(root, label)
                profile = stage.create_discovery_profile(root, label)
                self.assertEqual(created / 'profile', profile)
                with self.assertRaises(FileExistsError):
                    stage.create_discovery_profile(root, label)
                with self.assertRaises(ValueError):
                    stage.prepare_discovery(root, 'nian-link-test-OTHER')
                self.assertTrue(profile.is_dir())

    def test_invalid_owner_or_mode_refused_before_creating_profile(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            root.mkdir()
            label = 'nian-link-test-' + 'b' * 32
            (root / label).mkdir()
            with patch.object(stage, 'require_private_owned', side_effect=PermissionError('unsafe')):
                with self.assertRaises(PermissionError):
                    stage.create_discovery_profile(root, label)
            self.assertFalse((root / label / 'profile').exists())

    def test_launch_passes_the_dedicated_profile_only_after_bundle_verification(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            label = 'nian-link-test-' + 'c' * 32
            target = root / label
            profile = target / 'profile'
            contents = target / 'NianDeskLink.app' / 'Contents'
            resources = contents / 'Resources'
            resources.mkdir(parents=True)
            profile.mkdir()
            binaries = contents / 'MacOS'
            binaries.mkdir()
            bridge = binaries / 'libNianWifiEvidence.dylib'
            bridge.write_bytes(b'test bridge')
            pin = hashlib.sha256(bridge.read_bytes()).hexdigest()
            (contents / 'Info.plist').write_bytes(plistlib.dumps({'CFBundleIdentifier': 'com.niannian.nianlink.discovery-test', 'CFBundleDisplayName': '年年互联·发现测试'}))
            (resources / 'build-manifest.json').write_text('{"channel":"discovery-internal-test","bundleIdentifier":"com.niannian.nianlink.discovery-test","discoveryBridgeSha256":"' + pin + '"}', encoding='utf-8')
            with patch.object(stage, 'require_private_owned'), patch.object(stage.subprocess, 'run') as run:
                result = stage.launch_discovery(root, label)
            self.assertTrue(result['launchRequested'])
            self.assertEqual(['/usr/bin/codesign', '--verify', '--strict', str(bridge)], run.call_args_list[0].args[0])
            self.assertEqual(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(target / 'NianDeskLink.app')], run.call_args_list[1].args[0])
            self.assertEqual(['/usr/bin/open', str(target / 'NianDeskLink.app'), '--args', '--data-dir', str(profile)], run.call_args_list[2].args[0])

    def test_launcher_uses_existing_profile_and_never_overwrites(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            desktop = Path(folder) / 'Desktop'
            desktop.mkdir()
            label = 'nian-link-test-' + 'f' * 32
            target = root / label
            (target / 'profile').mkdir(parents=True)
            (target / 'NianDeskLink.app').mkdir()
            source = Path(folder) / 'helper.py'
            source.write_bytes(b'helper fixture')
            with patch.object(stage, 'require_private_owned'):
                result = stage.create_discovery_launcher(root, desktop, label, source)
                self.assertEqual(b'helper fixture', (target / 'launch-discovery-helper.py').read_bytes())
                launcher = desktop / '启动年年互联发现测试.command'
                self.assertEqual(str(launcher), result['launcher'])
                self.assertIn(label + ' launch-discovery', launcher.read_text(encoding='utf-8'))
                self.assertIn('launch-discovery-helper.py', launcher.read_text(encoding='utf-8'))
                with self.assertRaises(FileExistsError):
                    stage.create_discovery_launcher(root, desktop, label, source)
                self.assertEqual(b'helper fixture', (target / 'launch-discovery-helper.py').read_bytes())

    def test_launcher_refuses_bad_label_or_existing_desktop_file(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            desktop = Path(folder) / 'Desktop'
            root.mkdir(); desktop.mkdir()
            source = Path(folder) / 'helper.py'
            source.write_bytes(b'helper fixture')
            label = 'nian-link-test-' + 'e' * 32
            target = root / label
            (target / 'profile').mkdir(parents=True)
            (target / 'NianDeskLink.app').mkdir()
            launcher = desktop / '启动年年互联发现测试.command'
            launcher.write_text('keep', encoding='utf-8')
            with patch.object(stage, 'require_private_owned'):
                with self.assertRaises(ValueError):
                    stage.create_discovery_launcher(root, desktop, '../invalid', source)
                with self.assertRaises(FileExistsError):
                    stage.create_discovery_launcher(root, desktop, label, source)
            self.assertEqual('keep', launcher.read_text(encoding='utf-8'))
            self.assertFalse((target / 'launch-discovery-helper.py').exists())

    def test_blue_fileless_launcher_is_separate_from_existing_and_uses_own_profile(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects with spaces'
            desktop = Path(folder) / 'Desktop'
            desktop.mkdir()
            label = 'nian-link-test-' + 'a1234567' + 'f' * 24
            target = root / label
            (target / 'profile').mkdir(parents=True)
            (target / 'NianDeskLink.app').mkdir()
            source = Path(folder) / 'helper.py'
            source.write_bytes(b'new helper fixture')
            existing = desktop / '启动年年互联免文件测试.command'
            existing.write_bytes(b'old launcher')
            with patch.object(stage, 'require_private_owned'):
                result = stage.create_discovery_launcher(root, desktop, label, source, fileless=True, blue=True)
                launcher = desktop / '启动年年互联清透蓝测试-a1234567.command'
                self.assertEqual(str(launcher), result['launcher'])
                self.assertEqual(b'old launcher', existing.read_bytes())
                self.assertEqual(b'new helper fixture', Path(result['helper']).read_bytes())
                command = launcher.read_text(encoding='utf-8')
                self.assertIn('launch-fileless-helper.py', command)
                self.assertIn(label + ' launch-blue-fileless', command)
                self.assertEqual(['exec', '/usr/bin/python3', str(target / 'launch-fileless-helper.py'), label, 'launch-blue-fileless'],
                                 shlex.split(command.splitlines()[1]))
                with self.assertRaises(FileExistsError):
                    stage.create_discovery_launcher(root, desktop, label, source, fileless=True, blue=True)

    def test_blue_launcher_refuses_unsupported_mode_and_bad_label(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            desktop = Path(folder) / 'Desktop'
            root.mkdir(); desktop.mkdir()
            source = Path(folder) / 'helper.py'
            source.write_bytes(b'helper fixture')
            with patch.object(stage, 'require_private_owned'):
                with self.assertRaises(ValueError):
                    stage.create_discovery_launcher(root, desktop, '../unsafe', source, fileless=True, blue=True)
                with self.assertRaises(ValueError):
                    stage.create_discovery_launcher(root, desktop, 'nian-link-test-' + 'b' * 32, source, blue=True)
            self.assertEqual([], list(desktop.iterdir()))

    def test_launcher_creation_failure_removes_only_its_own_helper(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            desktop = Path(folder) / 'Desktop'
            desktop.mkdir()
            label = 'nian-link-test-' + 'c' * 32
            target = root / label
            (target / 'profile').mkdir(parents=True)
            (target / 'NianDeskLink.app').mkdir()
            source = Path(folder) / 'helper.py'
            source.write_bytes(b'helper fixture')
            real_open = stage.os.open
            def fail_launcher(path, flags, mode=0o777):
                if Path(path).parent == desktop:
                    raise PermissionError('simulated launcher failure')
                return real_open(path, flags, mode)
            with patch.object(stage, 'require_private_owned'), patch.object(stage.os, 'open', side_effect=fail_launcher):
                with self.assertRaises(PermissionError):
                    stage.create_discovery_launcher(root, desktop, label, source, fileless=True, blue=True)
            self.assertFalse((target / 'launch-fileless-helper.py').exists())
            self.assertEqual([], list(desktop.iterdir()))

    def test_launcher_failure_does_not_delete_replaced_helper(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            desktop = Path(folder) / 'Desktop'
            desktop.mkdir()
            label = 'nian-link-test-' + 'd' * 32
            target = root / label
            (target / 'profile').mkdir(parents=True)
            (target / 'NianDeskLink.app').mkdir()
            source = Path(folder) / 'helper.py'
            source.write_bytes(b'helper fixture')
            real_open = stage.os.open
            helper = target / 'launch-fileless-helper.py'
            def replace_helper_then_fail(path, flags, mode=0o777):
                if Path(path).parent == desktop:
                    replacement = target / 'replacement.py'
                    replacement.write_bytes(b'owned by another process')
                    replacement.replace(helper)
                    raise PermissionError('simulated concurrent replacement')
                return real_open(path, flags, mode)
            with patch.object(stage, 'require_private_owned'), patch.object(stage.os, 'open', side_effect=replace_helper_then_fail):
                with self.assertRaises(PermissionError):
                    stage.create_discovery_launcher(root, desktop, label, source, fileless=True, blue=True)
            self.assertEqual(b'owned by another process', helper.read_bytes())

    def test_existing_blue_launcher_is_never_replaced(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            desktop = Path(folder) / 'Desktop'
            desktop.mkdir()
            label = 'nian-link-test-' + 'e' * 32
            target = root / label
            (target / 'profile').mkdir(parents=True)
            (target / 'NianDeskLink.app').mkdir()
            source = Path(folder) / 'helper.py'
            source.write_bytes(b'helper fixture')
            existing = desktop / '启动年年互联清透蓝测试-eeeeeeee.command'
            existing.write_bytes(b'existing launcher')
            with patch.object(stage, 'require_private_owned'):
                with self.assertRaises(FileExistsError):
                    stage.create_discovery_launcher(root, desktop, label, source, fileless=True, blue=True)
            self.assertEqual(b'existing launcher', existing.read_bytes())
            self.assertFalse((target / 'launch-fileless-helper.py').exists())

    def test_blue_launcher_cli_dispatch_is_explicit(self):
        label = 'nian-link-test-' + '1' * 32
        fake_home = Path('fixture-home')
        with patch.object(sys, 'argv', ['stage-mac-test.py', label, 'create-blue-fileless-launcher']), \
             patch.object(Path, 'home', return_value=fake_home), \
             patch.object(stage, 'create_discovery_launcher', return_value={'launcher': 'ok'}) as create, \
             contextlib.redirect_stdout(io.StringIO()):
            stage.main()
        self.assertEqual(fake_home / 'Projects', create.call_args.args[0])
        self.assertEqual(fake_home / 'Desktop', create.call_args.args[1])
        self.assertEqual(label, create.call_args.args[2])
        self.assertEqual({'fileless': True, 'blue': True}, create.call_args.kwargs)

    def test_blue_launch_opens_new_instance_with_the_isolated_profile(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            label = 'nian-link-test-' + '4' * 32
            target = root / label
            profile = target / 'profile'
            contents = target / 'NianDeskLink.app' / 'Contents'
            resources = contents / 'Resources'
            resources.mkdir(parents=True)
            profile.mkdir()
            binaries = contents / 'MacOS'
            binaries.mkdir()
            bridge = binaries / 'libNianWifiEvidence.dylib'
            bridge.write_bytes(b'test bridge')
            pin = hashlib.sha256(bridge.read_bytes()).hexdigest()
            (contents / 'Info.plist').write_bytes(plistlib.dumps({'CFBundleIdentifier': 'com.niannian.nianlink.fileless-test', 'CFBundleDisplayName': '年年互联·免文件测试'}))
            (resources / 'build-manifest.json').write_text('{"channel":"fileless-internal-test","bundleIdentifier":"com.niannian.nianlink.fileless-test","discoveryBridgeSha256":"' + pin + '"}', encoding='utf-8')
            fake_fcntl = SimpleNamespace(LOCK_EX=2, LOCK_NB=4, LOCK_UN=8, flock=MagicMock())
            real_stat = stage.os.fstat
            with patch.object(stage, 'require_private_owned'), patch.object(stage.os, 'geteuid', return_value=0, create=True), \
                 patch.object(stage.os, 'fstat') as fake_stat, \
                 patch.dict(sys.modules, {'fcntl': fake_fcntl}), patch.object(stage.subprocess, 'run') as run:
                def user_owned(fd):
                    real_stat(fd)
                    return SimpleNamespace(st_mode=stat.S_IFREG | 0o600, st_uid=0)
                fake_stat.side_effect = user_owned
                def no_active_test(args, **kwargs):
                    return SimpleNamespace(returncode=1 if args[0] == '/usr/bin/pgrep' else 0)
                run.side_effect = no_active_test
                result = stage.launch_discovery(root, label, fileless=True, force_new=True)
            self.assertTrue(result['launchRequested'])
            self.assertEqual(['/usr/bin/open', '-n', '-W', str(target / 'NianDeskLink.app'), '--args', '--data-dir', str(profile)], run.call_args_list[-1].args[0])

    def test_blue_launch_refuses_other_running_isolated_test_before_open(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            label = 'nian-link-test-' + '7' * 32
            target = root / label
            (target / 'profile').mkdir(parents=True)
            contents = target / 'NianDeskLink.app' / 'Contents'
            (contents / 'Resources').mkdir(parents=True)
            (contents / 'MacOS').mkdir()
            bridge = contents / 'MacOS/libNianWifiEvidence.dylib'
            bridge.write_bytes(b'test bridge')
            pin = hashlib.sha256(bridge.read_bytes()).hexdigest()
            (contents / 'Info.plist').write_bytes(plistlib.dumps({'CFBundleIdentifier': 'com.niannian.nianlink.fileless-test', 'CFBundleDisplayName': '年年互联·免文件测试'}))
            (contents / 'Resources/build-manifest.json').write_text('{"channel":"fileless-internal-test","bundleIdentifier":"com.niannian.nianlink.fileless-test","discoveryBridgeSha256":"' + pin + '"}', encoding='utf-8')
            fake_fcntl = SimpleNamespace(LOCK_EX=2, LOCK_NB=4, LOCK_UN=8, flock=MagicMock())
            def fake_run(args, **kwargs): return SimpleNamespace(returncode=0)
            real_stat = stage.os.fstat
            with patch.object(stage, 'require_private_owned'), patch.object(stage.os, 'geteuid', return_value=0, create=True), \
                 patch.object(stage.os, 'fstat', side_effect=lambda fd: SimpleNamespace(st_mode=stat.S_IFREG | 0o600, st_uid=0)), \
                 patch.dict(sys.modules, {'fcntl': fake_fcntl}), patch.object(stage.subprocess, 'run', side_effect=fake_run) as run:
                with self.assertRaisesRegex(RuntimeError, 'test instance'):
                    stage.launch_discovery(root, label, fileless=True, force_new=True)
            self.assertFalse(any(call.args[0][0] == '/usr/bin/open' for call in run.call_args_list))

    def test_fdopen_failure_removes_created_helper(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            desktop = Path(folder) / 'Desktop'
            desktop.mkdir()
            label = 'nian-link-test-' + '5' * 32
            target = root / label
            (target / 'profile').mkdir(parents=True)
            (target / 'NianDeskLink.app').mkdir()
            source = Path(folder) / 'helper.py'
            source.write_bytes(b'helper fixture')
            with patch.object(stage, 'require_private_owned'), patch.object(stage.os, 'fdopen', side_effect=OSError('simulated fdopen failure')):
                with self.assertRaises(OSError):
                    stage.create_discovery_launcher(root, desktop, label, source, fileless=True, blue=True)
            self.assertFalse((target / 'launch-fileless-helper.py').exists())
            self.assertEqual([], list(desktop.iterdir()))

    def test_blue_launch_releases_lock_when_open_fails(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            root.mkdir()
            bundle = root / 'fixture.app'
            profile = root / 'profile'
            fake_fcntl = SimpleNamespace(LOCK_EX=2, LOCK_NB=4, LOCK_UN=8, flock=MagicMock())
            def fake_run(args, **kwargs):
                if args[0] == '/usr/bin/pgrep': return SimpleNamespace(returncode=1)
                raise OSError('simulated open failure')
            with patch.object(stage.os, 'geteuid', return_value=0, create=True), \
                 patch.object(stage.os, 'fstat', return_value=SimpleNamespace(st_mode=stat.S_IFREG | 0o600, st_uid=0)), \
                 patch.dict(sys.modules, {'fcntl': fake_fcntl}), patch.object(stage.subprocess, 'run', side_effect=fake_run):
                with self.assertRaises(OSError): stage.launch_blue_bundle(root, bundle, profile)
            self.assertEqual(2, fake_fcntl.flock.call_count)
            self.assertEqual(fake_fcntl.LOCK_EX | fake_fcntl.LOCK_NB, fake_fcntl.flock.call_args_list[0].args[1])
            self.assertEqual(fake_fcntl.LOCK_UN, fake_fcntl.flock.call_args_list[1].args[1])

    def test_blue_launch_fails_closed_on_lock_contention_or_process_check_error(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            root.mkdir()
            fake_fcntl = SimpleNamespace(LOCK_EX=2, LOCK_NB=4, LOCK_UN=8, flock=MagicMock(side_effect=BlockingIOError()))
            with patch.object(stage.os, 'geteuid', return_value=0, create=True), \
                 patch.object(stage.os, 'fstat', return_value=SimpleNamespace(st_mode=stat.S_IFREG | 0o600, st_uid=0)), \
                 patch.dict(sys.modules, {'fcntl': fake_fcntl}), patch.object(stage.subprocess, 'run') as run:
                with self.assertRaisesRegex(RuntimeError, 'test instance'):
                    stage.launch_blue_bundle(root, root / 'fixture.app', root / 'profile')
                run.assert_not_called()
            fake_fcntl.flock.reset_mock(side_effect=True)
            with patch.object(stage.os, 'geteuid', return_value=0, create=True), \
                 patch.object(stage.os, 'fstat', return_value=SimpleNamespace(st_mode=stat.S_IFREG | 0o600, st_uid=0)), \
                 patch.dict(sys.modules, {'fcntl': fake_fcntl}), patch.object(stage.subprocess, 'run', return_value=SimpleNamespace(returncode=2)) as run:
                with self.assertRaisesRegex(RuntimeError, 'Unable to verify'):
                    stage.launch_blue_bundle(root, root / 'fixture.app', root / 'profile')
                self.assertEqual(['/usr/bin/pgrep', '-f'], run.call_args.args[0][:2])

    def test_launch_rejects_wrong_bundle_before_any_system_command(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            label = 'nian-link-test-' + 'd' * 32
            target = root / label
            (target / 'profile').mkdir(parents=True)
            contents = target / 'NianDeskLink.app' / 'Contents'
            contents.mkdir(parents=True)
            (contents / 'Info.plist').write_bytes(plistlib.dumps({'CFBundleIdentifier': 'com.niannian.nianlink', 'CFBundleDisplayName': '年年互联·独立测试'}))
            with patch.object(stage, 'require_private_owned'), patch.object(stage.subprocess, 'run') as run:
                with self.assertRaises(ValueError):
                    stage.launch_discovery(root, label)
            run.assert_not_called()

    def test_directory_identity_requires_current_owner_private_mode_and_no_link(self):
        path = Path('fixture')
        cases = [(stat.S_IFDIR | 0o700, 123, True), (stat.S_IFDIR | 0o755, 123, False),
                 (stat.S_IFDIR | 0o700, 456, False), (stat.S_IFLNK | 0o700, 123, False),
                 (stat.S_IFREG | 0o700, 123, False)]
        with patch.object(stage.os, 'geteuid', return_value=123, create=True):
            for mode, owner, allowed in cases:
                with patch.object(Path, 'lstat', return_value=SimpleNamespace(st_mode=mode, st_uid=owner)):
                    if allowed:
                        stage.require_private_owned(path)
                    else:
                        with self.assertRaises(PermissionError):
                            stage.require_private_owned(path)


if __name__ == '__main__':
    unittest.main()
