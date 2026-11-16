"""Regression checks for explicitly isolated fileless candidate packaging."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
import hashlib
import json
import plistlib
import os
import subprocess
import shutil
import sys
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('fileless_package', ROOT / 'src/DesktopToolbox.Mac/build/package_app.py')
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)
stage_spec = importlib.util.spec_from_file_location('fileless_stage', ROOT / 'scripts/stage-mac-test.py')
stage = importlib.util.module_from_spec(stage_spec)
stage_spec.loader.exec_module(stage)

FILELESS_SOURCE_INPUTS = (
    'scripts/build-mac-test.ps1',
    'scripts/BuildArtifactLock.psm1',
    'scripts/SdkBaseline.psm1',
    'scripts/FilelessArtifactCache.psm1',
    'scripts/FilelessBuildMetadata.psm1',
)


def make_fileless_graph(root, missing=None):
    for directory in ('src/DesktopToolbox.Mac', 'src/DesktopToolbox.Collaboration.Ui',
                      'src/NianLink.MacDiscoveryCandidate', 'src/Nian.Input.Core', 'scripts'):
        (root / directory).mkdir(parents=True, exist_ok=True)
    (root / 'src/NianLink.MacDiscoveryCandidate/Entry.csproj').write_bytes(b'entry')
    (root / 'src/Nian.Input.Core/Core.csproj').write_bytes(b'core')
    for name in FILELESS_SOURCE_INPUTS:
        if name != missing:
            (root / name).write_bytes(('fixture:' + name).encode())
    return {'libraries': {'core/1': {'type': 'project', 'path': '../Nian.Input.Core/Core.csproj'}}}


class FilelessPackagingTests(unittest.TestCase):
    def test_managed_fileless_source_inventory_requires_managed_executables_only_when_opted_in(self):
        with tempfile.TemporaryDirectory(prefix='SYNTHETIC-NO-DEPLOY-') as folder:
            root = Path(folder)
            graph = make_fileless_graph(root)
            args = dict(entry_project='src/NianLink.MacDiscoveryCandidate/Entry.csproj', fileless_test=True)
            package.collect_sources(root, graph, **args)
            with self.assertRaises(ValueError):
                package.collect_sources(root, graph, managed_fileless=True, **args)
            for name in ('scripts/MacFilelessManagedRetention.psm1', 'scripts/verify_mac_fileless_candidate.py'):
                (root / name).write_text('SYNTHETIC')
            rows = package.collect_sources(root, graph, managed_fileless=True, **args)
            paths = {item['path'] for item in rows}
            self.assertIn('scripts/MacFilelessManagedRetention.psm1', paths)
            self.assertIn('scripts/verify_mac_fileless_candidate.py', paths)
            for name in ('scripts/MacFilelessManagedRetention.psm1', 'scripts/verify_mac_fileless_candidate.py'):
                path = root / name
                original = path.read_bytes()
                path.unlink()  # Disposable synthetic fixture only.
                with self.assertRaises(ValueError):
                    package.collect_sources(root, graph, managed_fileless=True, **args)
                path.write_bytes(original)

    def test_fileless_source_inventory_records_every_builder_and_helper_hash(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            graph = make_fileless_graph(root)
            def collect():
                rows = package.collect_sources(root, graph,
                    entry_project='src/NianLink.MacDiscoveryCandidate/Entry.csproj', fileless_test=True)
                return {row['path']: row['sha256'] for row in rows}
            before = collect()
            for name in FILELESS_SOURCE_INPUTS:
                self.assertEqual(before[name], hashlib.sha256((root / name).read_bytes()).hexdigest())
            for name in FILELESS_SOURCE_INPUTS:
                with self.subTest(name=name):
                    file = root / name
                    original = file.read_bytes()
                    file.write_bytes(original + b'-changed')
                    after = collect()
                    self.assertEqual({key for key in before if before[key] != after[key]}, {name})
                    file.write_bytes(original)

    def test_fileless_source_inventory_rejects_each_missing_input(self):
        for missing in FILELESS_SOURCE_INPUTS:
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                graph = make_fileless_graph(root, missing=missing)
                with self.assertRaisesRegex(ValueError, 'fileless.*input|input.*fileless'):
                    package.collect_sources(root, graph,
                        entry_project='src/NianLink.MacDiscoveryCandidate/Entry.csproj', fileless_test=True)

    def test_fileless_source_inventory_rejects_linked_scripts_directory(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            actual = root / 'scripts-real'
            actual.mkdir()
            link = root / 'scripts'
            try:
                link.symlink_to(actual, target_is_directory=True)
            except OSError:
                if os.name != 'nt' or not shutil.which('pwsh'):
                    self.skipTest('Directory symlink unavailable.')
                env = os.environ | {'FIXTURE_LINK': str(link), 'FIXTURE_TARGET': str(actual)}
                result = subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command',
                    'New-Item -ItemType Junction -Path $env:FIXTURE_LINK -Target $env:FIXTURE_TARGET -ErrorAction Stop | Out-Null'],
                    env=env, capture_output=True, text=True)
                if result.returncode:
                    self.skipTest('Directory link unavailable.')
            graph = make_fileless_graph(root)
            with self.assertRaisesRegex(ValueError, '[Ll]ink'):
                package.collect_sources(root, graph,
                    entry_project='src/NianLink.MacDiscoveryCandidate/Entry.csproj', fileless_test=True)

    def test_fileless_cli_refuses_missing_helper_before_moving_publish(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            graph = make_fileless_graph(root, missing='scripts/FilelessArtifactCache.psm1')
            package_script = root / 'src/DesktopToolbox.Mac/build/package_app.py'
            package_script.parent.mkdir(parents=True)
            shutil.copyfile(ROOT / 'src/DesktopToolbox.Mac/build/package_app.py', package_script)
            entry = root / 'src/NianLink.MacDiscoveryCandidate/Entry.csproj'
            # The actual entry name is fixed by the packaging CLI.
            target_entry = entry.with_name('NianLink.MacDiscoveryCandidate.csproj')
            target_entry.write_bytes(entry.read_bytes())
            assets = root / 'tests/.work/fileless/obj/project.assets.json'
            assets.parent.mkdir(parents=True)
            graph['project'] = {'restore': {'projectPath': str(target_entry)}}
            assets.write_text(json.dumps(graph), encoding='utf-8')
            publish = root / 'output/publish'
            publish.mkdir(parents=True)
            bridge = publish / 'libNianWifiEvidence.dylib'
            bridge.write_bytes(b'synthetic bridge')
            result = subprocess.run([sys.executable, '-B', str(package_script), '--publish', str(publish),
                '--output', str(root / 'output'), '--version', '0.0.1', '--fileless-test',
                '--assets-file', str(assets), '--discovery-bridge-sha256',
                hashlib.sha256(bridge.read_bytes()).hexdigest()], capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('fileless', result.stderr.lower())
            self.assertIn('input', result.stderr.lower())
            self.assertTrue(bridge.is_file(), 'Publish inputs must remain in place on a missing helper.')

    def test_distinct_mac_identity_and_conflicts(self):
        original = {'CFBundleIdentifier': 'com.niannian.nianlink'}
        selected = package.bundle_identity(original, fileless_test=True)
        self.assertEqual('com.niannian.nianlink.fileless-test', selected['CFBundleIdentifier'])
        self.assertEqual('年年互联·免文件测试', selected['CFBundleDisplayName'])
        with self.assertRaises(ValueError):
            package.bundle_identity(original, discovery_test=True, fileless_test=True)

    def test_assets_must_be_from_isolated_restore_root(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            entry = root / 'src/NianLink.MacDiscoveryCandidate/NianLink.MacDiscoveryCandidate.csproj'
            entry.parent.mkdir(parents=True)
            entry.write_text('fixture')
            assets = root / 'tests/.work/fileless-mac-test/obj/NianLink.MacDiscoveryCandidate/project.assets.json'
            assets.parent.mkdir(parents=True)
            import json
            assets.write_text(json.dumps({'project': {'restore': {'projectPath': str(entry)}}}))
            self.assertEqual(str(entry), package.read_isolated_assets(root, assets, entry)['project']['restore']['projectPath'])
            with self.assertRaises(ValueError):
                package.read_isolated_assets(root, entry, entry)
            assets.write_text(json.dumps({'project': {'restore': {'projectPath': str(root / 'other.csproj')}}}))
            with self.assertRaises(ValueError):
                package.read_isolated_assets(root, assets, entry)

    def test_packaging_opt_in_and_bridge_paths_are_explicit(self):
        win = (ROOT / 'scripts/build-multidevice-windows-test.ps1').read_text(encoding='utf-8')
        mac = (ROOT / 'scripts/build-mac-test.ps1').read_text(encoding='utf-8')
        for source in (win, mac):
            self.assertIn('[switch]$FilelessTesting', source)
            self.assertIn('-p:UseArtifactsOutput=true', source)
        self.assertIn('Get-NianDiscoveryBuildProperties', win)
        self.assertIn('Get-NianDiscoveryBuildProperties -FilelessTesting $true -PairingDiagnostics $false', mac)
        self.assertIn('-p:NianMacReturnResearch=false', mac)
        self.assertIn('Get-NianFilelessArtifactCache', win)
        self.assertIn("Get-NianFilelessArtifactCache -ProjectRoot $root -Platform 'mac'", mac)
        self.assertIn('Invoke-NianFilelessPublish', mac)
        self.assertIn('NianDisplayBridgeOutputDirectory', win)
        self.assertIn('--assets-file', mac)
        self.assertIn('--fileless-test', mac)

    def test_fileless_windows_bundle_uses_separate_matching_guide(self):
        script = (ROOT / 'scripts/build-multidevice-windows-test.ps1').read_text(encoding='utf-8')
        guide = (ROOT / 'docs/免文件配对内测使用说明.md').read_text(encoding='utf-8')
        self.assertIn("if ($FilelessTesting) { 'docs/免文件配对内测使用说明.md' } else { 'docs/USER-GUIDE.md' }", script)
        self.assertIn("if ($FilelessTesting) { $readme =", script)
        for phrase in ('免文件安全配对（测试）', '本机键鼠指纹', '控制通道指纹', '查看未完成的配对', '结果待确认', '安全终止', '开始连接', '台式机-H盘启动.cmd'):
            with self.subTest(phrase=phrase):
                self.assertIn(phrase, guide)
        self.assertNotIn('点「生成身份卡」', guide)

    def test_mac_runtime_uses_distinct_service_and_launch_path(self):
        app = (ROOT / 'src/DesktopToolbox.Mac/App.axaml.cs').read_text(encoding='utf-8')
        vault = (ROOT / 'src/Nian.Input.Security/MacKeychainIdentityVault.cs').read_text(encoding='utf-8')
        stage = (ROOT / 'scripts/stage-mac-test.py').read_text(encoding='utf-8')
        self.assertIn('CreateFilelessCandidate()', app)
        self.assertIn('app.niannian.nianlink-fileless-test.identity.v1', vault)
        self.assertIn('launch-fileless', stage)
        self.assertIn('启动年年互联免文件测试.command', stage)

    def test_fileless_launch_checks_identity_and_uses_isolated_profile(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Projects'
            label = 'nian-link-test-' + 'a' * 32
            target = root / label
            profile = target / 'profile'
            profile.mkdir(parents=True)
            contents = target / 'NianDeskLink.app' / 'Contents'
            resources = contents / 'Resources'
            binaries = contents / 'MacOS'
            resources.mkdir(parents=True)
            binaries.mkdir()
            bridge = binaries / 'libNianWifiEvidence.dylib'
            bridge.write_bytes(b'fixture')
            pin = hashlib.sha256(bridge.read_bytes()).hexdigest()
            identity = 'com.niannian.nianlink.fileless-test'
            (contents / 'Info.plist').write_bytes(plistlib.dumps({'CFBundleIdentifier': identity, 'CFBundleDisplayName': '年年互联·免文件测试'}))
            (resources / 'build-manifest.json').write_text(json.dumps({'channel': 'fileless-internal-test', 'bundleIdentifier': identity, 'discoveryBridgeSha256': pin}))
            with patch.object(stage, 'require_private_owned'), patch.object(stage.subprocess, 'run') as run:
                with self.assertRaises(ValueError):
                    stage.launch_discovery(root, label)
                run.assert_not_called()
                stage.launch_discovery(root, label, fileless=True)
                self.assertEqual(['/usr/bin/open', str(target / 'NianDeskLink.app'), '--args', '--data-dir', str(profile)], run.call_args_list[-1].args[0])

    def test_fileless_launcher_keeps_discovery_launcher_intact(self):
        with tempfile.TemporaryDirectory() as folder:
            root, desktop = Path(folder) / 'Projects', Path(folder) / 'Desktop'
            desktop.mkdir()
            old = desktop / '启动年年互联发现测试.command'
            old.write_text('unchanged')
            label = 'nian-link-test-' + 'b' * 32
            target = root / label
            (target / 'profile').mkdir(parents=True)
            (target / 'NianDeskLink.app').mkdir()
            helper = Path(folder) / 'helper.py'
            helper.write_text('fixture')
            with patch.object(stage, 'require_private_owned'):
                result = stage.create_discovery_launcher(root, desktop, label, helper, fileless=True)
            self.assertEqual('unchanged', old.read_text())
            self.assertIn('launch-fileless', Path(result['launcher']).read_text(encoding='utf-8'))

    @unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 is required')
    def test_build_marker_is_read_without_executing_assembly(self):
        with tempfile.TemporaryDirectory() as folder:
            fixture = Path(folder) / 'fixture.dll'
            module = ROOT / 'scripts/FilelessBuildMetadata.psm1'
            script = '''
param($module, $fixture)
Import-Module $module
Add-Type -TypeDefinition '[assembly: System.Reflection.AssemblyMetadata("NianFilelessPairingCandidate", "true")] public class Fixture { }' -OutputAssembly $fixture
if (-not [NianBuildMetadata]::HasExactlyOne($fixture, 'NianFilelessPairingCandidate', 'true')) { exit 1 }
if ([NianBuildMetadata]::HasExactlyOne($fixture, 'NianFilelessPairingCandidate', 'false')) { exit 2 }
if ([NianBuildMetadata]::HasExactlyOne($fixture, 'Other', 'true')) { exit 3 }
'''
            test_script = Path(folder) / 'verify.ps1'
            test_script.write_text(script, encoding='utf-8')
            result = subprocess.run(['pwsh', '-NoProfile', '-File', str(test_script), str(module), str(fixture)], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)

    @unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 is required')
    def test_fileless_mode_rejects_default_candidate_before_build(self):
        cases = [
            ('build-multidevice-windows-test.ps1', ['-Dotnet', 'missing', '-PackagesDirectory', 'missing']),
            ('build-mac-test.ps1', ['-Dotnet', 'missing', '-PackagesDirectory', 'missing', '-NativeDirectory', 'missing', '-Version', '0.0.1']),
        ]
        for script, args in cases:
            with self.subTest(script=script):
                result = subprocess.run(['pwsh', '-NoProfile', '-File', str(ROOT / 'scripts' / script), *args, '-TestingOnly', '-FilelessTesting'], capture_output=True, text=True, encoding='utf-8', errors='replace')
                self.assertNotEqual(0, result.returncode)
                self.assertIn('Fileless testing requires', result.stderr)


if __name__ == '__main__':
    unittest.main()
