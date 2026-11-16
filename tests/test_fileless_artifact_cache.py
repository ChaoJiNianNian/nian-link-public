"""Exercise reusable isolated build output without accessing real profiles."""
import os
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import struct
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
MODULE = ROOT / 'scripts/FilelessArtifactCache.psm1'
PWSH = shutil.which('pwsh')
DOTNET = os.environ.get('NIAN_FILELESS_TEST_DOTNET')


@unittest.skipUnless(PWSH and os.name == 'nt', 'requires Windows PowerShell 7')
class FilelessArtifactCacheTests(unittest.TestCase):
    def resolve(self, root, platform, diagnostic=False, discovery=False):
        command = ("$ErrorActionPreference='Stop'; Import-Module $env:MODULE -ErrorAction Stop; "
                   "$lease=Enter-NianFilelessCacheLease -ProjectRoot $env:PROJECT; "
                   "try { Get-NianFilelessArtifactCache -ProjectRoot $env:PROJECT "
                   "-Platform $env:PLATFORM -PairingDiagnostics:([bool]::Parse($env:DIAGNOSTIC)) "
                   "-DiscoveryOnly:([bool]::Parse($env:DISCOVERY)) -Lease $lease } "
                   "finally { Exit-NianFilelessCacheLease -Lease $lease }")
        env = os.environ | {'MODULE': str(MODULE), 'PROJECT': str(root),
                            'PLATFORM': platform, 'DIAGNOSTIC': str(diagnostic), 'DISCOVERY': str(discovery)}
        return subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', command],
                              env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')

    def test_existing_lease_blocks_another_process_before_cache_creation(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'project'
            root.mkdir()
            ready, done = root / 'ready', root / 'done'
            env = os.environ | {'MODULE': str(MODULE), 'PROJECT': str(root), 'READY': str(ready), 'DONE': str(done)}
            script = ("$ErrorActionPreference='Stop'; Import-Module $env:MODULE -ErrorAction Stop; "
                      "$lease=Enter-NianFilelessCacheLease -ProjectRoot $env:PROJECT; "
                      "try { [IO.File]::WriteAllText($env:READY,'ready'); "
                      "while(-not [IO.File]::Exists($env:DONE)) { Start-Sleep -Milliseconds 50 } } "
                      "finally { Exit-NianFilelessCacheLease -Lease $lease }")
            holder = subprocess.Popen([PWSH, '-NoProfile', '-NonInteractive', '-Command', script], env=env,
                                      stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            try:
                deadline = time.monotonic() + 10
                while not ready.exists() and holder.poll() is None and time.monotonic() < deadline:
                    time.sleep(.05)
                self.assertTrue(ready.exists(), 'lease holder did not start')
                contender = self.resolve(root, 'windows')
                self.assertNotEqual(contender.returncode, 0)
                self.assertFalse((root / 'tests/.work/reusable-fileless-build/windows-fileless').exists())
                done.touch()
                self.assertEqual(holder.wait(timeout=10), 0)
                self.assertEqual(self.resolve(root, 'windows').returncode, 0)
            finally:
                if holder.poll() is None:
                    done.touch()
                    holder.wait(timeout=10)

    def test_lease_is_required_and_bound_to_project_root(self):
        with tempfile.TemporaryDirectory() as folder:
            first, second = Path(folder) / 'one', Path(folder) / 'two'
            first.mkdir(); second.mkdir()
            env = os.environ | {'MODULE': str(MODULE), 'PROJECT': str(first), 'OTHER': str(second)}
            script = ("$ErrorActionPreference='Stop'; Import-Module $env:MODULE -ErrorAction Stop; "
                      "$lease=Enter-NianFilelessCacheLease -ProjectRoot $env:PROJECT; "
                      "try { Get-NianFilelessArtifactCache -ProjectRoot $env:OTHER -Platform windows -Lease $lease } "
                      "finally { Exit-NianFilelessCacheLease -Lease $lease }")
            result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', script], env=env, capture_output=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse((second / 'tests').exists())

    def run_builder(self, root, script_name, arguments, legacy_override=False, env=None):
        parameters = {}
        index = 0
        while index < len(arguments):
            key = arguments[index].lstrip('-')
            if index + 1 < len(arguments) and not arguments[index + 1].startswith('-'):
                parameters[key] = arguments[index + 1]
                index += 2
            else:
                parameters[key] = True
                index += 1
        env = (env or os.environ) | {'BUILDER_SCRIPT': str(root / 'scripts' / script_name),
                                     'BUILDER_PARAMETERS': json.dumps(parameters),
                                     'BUILDER_LEGACY': str(legacy_override)}
        script = ("$ErrorActionPreference='Stop'; $builderParameters=ConvertFrom-Json $env:BUILDER_PARAMETERS -AsHashtable; "
                  "if($env:BUILDER_LEGACY -eq 'True') { $builderParameters.ManagedRetention=$false }; "
                  "& $env:BUILDER_SCRIPT @builderParameters")
        return subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', script], cwd=root, env=env,
                              capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30)

    def run_builder_with_held_cache(self, root, script_name, arguments, legacy_override=False):
        ready, done = root / 'lease-ready', root / 'lease-done'
        env = os.environ | {'MODULE': str(root / 'scripts/FilelessArtifactCache.psm1'),
                            'PROJECT': str(root), 'READY': str(ready), 'DONE': str(done)}
        script = ("$ErrorActionPreference='Stop'; Import-Module $env:MODULE -ErrorAction Stop; "
                  "$lease=Enter-NianFilelessCacheLease -ProjectRoot $env:PROJECT; "
                  "try { [IO.File]::WriteAllText($env:READY,'ready'); "
                  "while(-not [IO.File]::Exists($env:DONE)) { Start-Sleep -Milliseconds 50 } } "
                  "finally { Exit-NianFilelessCacheLease -Lease $lease }")
        holder = subprocess.Popen([PWSH, '-NoProfile', '-NonInteractive', '-Command', script], env=env,
                                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            deadline = time.monotonic() + 10
            while not ready.exists() and holder.poll() is None and time.monotonic() < deadline:
                time.sleep(.05)
            self.assertTrue(ready.exists(), 'lease holder did not start')
            return self.run_builder(root, script_name, arguments, legacy_override, env)
        finally:
            done.touch()
            holder.wait(timeout=10)

    def test_actual_windows_entry_refuses_busy_cache_without_reservation_or_output(self):
        engine = ROOT / 'tests/.work/multidevice-native-runtime-01/deskflow-core.exe'
        if not engine.is_file() or hashlib.sha256(engine.read_bytes()).hexdigest().upper() != '04CE02FB644B2E3E80A23F50077AACA1168C5A1EA520E5D45E48AA68104CFFFA':
            self.skipTest('validated isolated Windows test core unavailable')
        for mode in ('default', 'explicit_true', 'explicit_false', 'diagnostic_default'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as folder:
                root = Path(folder) / 'project'
                scripts = root / 'scripts'
                scripts.mkdir(parents=True)
                for name in ('build-multidevice-windows-test.ps1', 'BuildArtifactLock.psm1', 'FilelessArtifactCache.psm1',
                             'DiscoveryManagedRetention.psm1', 'DiscoveryPortablePaths.psm1'):
                    shutil.copyfile(ROOT / 'scripts' / name, scripts / name)
                (scripts / 'SdkBaseline.psm1').write_text('function Assert-NianSdk {}')
                (scripts / 'WindowsTestKeyboardPayload.psm1').write_text('function Assert-WindowsTestEngineInputs {}')
                runtime = root / 'tests/.work/multidevice-native-runtime-01'
                runtime.mkdir(parents=True)
                shutil.copyfile(engine, runtime / 'deskflow-core.exe')
                for name in ('NianLink.CandidateDiscovery', 'NianLink.MultiDevice', 'NianLink.DiscoveryPortable',
                             'DesktopToolbox.DeskLink', 'NianLink.MultiDevice.Shared'):
                    (root / 'src' / name).mkdir(parents=True)
                dotnet = root / 'fixture-dotnet.exe'; dotnet.write_bytes(b'not executed')
                packages = root / 'packages'; packages.mkdir()
                args = ['-Dotnet', str(dotnet), '-PackagesDirectory', str(packages), '-TestingOnly',
                        '-DiscoveryPortable', '-FilelessTesting']
                if mode == 'explicit_true':
                    args.append('-ManagedRetention')
                if mode == 'diagnostic_default':
                    args.append('-PairingDiagnostics')
                managed_mode = 'diagnostics' if mode == 'diagnostic_default' else 'fileless'
                legacy_override = mode == 'explicit_false'
                result = self.run_builder_with_held_cache(root, 'build-multidevice-windows-test.ps1', args, legacy_override)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('.cache-build.lock', result.stderr)
                self.assertFalse((root / '.artifacts/discovery-portable').exists())
                self.assertFalse((root / '.artifacts/discovery-portable-managed' / managed_mode).exists())
                cache_name = 'windows-fileless-diagnostics' if mode == 'diagnostic_default' else 'windows-fileless'
                cache = root / 'tests/.work/reusable-fileless-build' / cache_name; cache.mkdir()
                sentinel = cache / 'unowned.txt'; sentinel.write_text('keep')
                corrupt = self.run_builder(root, 'build-multidevice-windows-test.ps1', args, legacy_override)
                self.assertNotEqual(corrupt.returncode, 0)
                self.assertIn('unowned', corrupt.stderr)
                self.assertFalse((root / '.artifacts/discovery-portable').exists())
                self.assertFalse((root / '.artifacts/discovery-portable-managed' / managed_mode).exists())
                self.assertEqual(sentinel.read_text(), 'keep')
                (cache / '.nian-fileless-build-cache').write_text('nian-fileless-build-cache-v1:' + cache_name)
                started = self.run_builder(root, 'build-multidevice-windows-test.ps1', args, legacy_override)
                self.assertNotEqual(started.returncode, 0)  # fixture executable is intentionally invalid
                if mode == 'explicit_false':
                    self.assertTrue((root / '.artifacts/discovery-portable').is_dir())
                    self.assertFalse((root / '.artifacts/discovery-portable-managed/fileless').exists())
                else:
                    mode_root = root / '.artifacts/discovery-portable-managed' / managed_mode
                    index_file = mode_root / 'retention.json'
                    state = json.loads(index_file.read_text())
                    self.assertIsNotNone(state['pending'])
                    self.assertFalse((root / '.artifacts/discovery-portable').exists())
                    before = index_file.read_bytes()
                    outputs = sorted(p.name for p in mode_root.glob('test-*'))
                    retry = self.run_builder(root, 'build-multidevice-windows-test.ps1', args)
                    self.assertNotEqual(retry.returncode, 0)
                    self.assertIn('pending', retry.stderr)
                    self.assertEqual(index_file.read_bytes(), before)
                    self.assertEqual(sorted(p.name for p in mode_root.glob('test-*')), outputs)

    def prepare_mac_builder(self, root, capture=False):
        scripts = root / 'scripts'; scripts.mkdir(parents=True)
        for name in ('build-mac-test.ps1', 'BuildArtifactLock.psm1', 'FilelessArtifactCache.psm1'):
            shutil.copyfile(ROOT / 'scripts' / name, scripts / name)
        (scripts / 'SdkBaseline.psm1').write_text('function Assert-NianSdk {}')
        native_source = root / 'src/DesktopToolbox.Mac/Native'; native_source.mkdir(parents=True)
        source_names = ('DeskLinkMedia.swift', 'RealtimeCapture.swift', 'RealtimeCaptureOutput.swift', 'build-media.sh',
                        'media-sources.json', 'DeskLinkWeb.swift', 'build-web.sh')
        source_rows = []
        for name in source_names:
            data = ('fixture-' + name).encode()
            (native_source / name).write_bytes(data)
            source_rows.append({'name': name, 'sha256': hashlib.sha256(data).hexdigest().upper()})
        native = root / '.artifacts/mac-native' / ('native-' + 'a' * 32); native.mkdir(parents=True)
        binary_rows = []
        def macho(kind):
            return struct.pack('<IIIIIIII', 0xFEEDFACF, 0x0100000C, 0, kind, 0, 0, 0, 0)
        for name, kind in (('DeskLinkMedia', 2), ('libDeskLinkWeb.dylib', 6)):
            data = macho(kind)
            (native / name).write_bytes(data)
            binary_rows.append({'name': name, 'sha256': hashlib.sha256(data).hexdigest().upper(), 'architecture': 'arm64'})
        (native / 'build-evidence.json').write_text(json.dumps({'schema': 'mac-native-build-v1', 'label': native.name,
                                                                'sourceFiles': source_rows, 'binaries': binary_rows}))
        bridge = root / '.artifacts/mac-discovery-native/test/libNianWifiEvidence.dylib'
        bridge.parent.mkdir(parents=True); bridge.write_bytes(macho(6))
        dotnet = root / ('fixture-dotnet.ps1' if capture else 'fixture-dotnet.exe')
        if capture:
            dotnet.write_text('[IO.File]::WriteAllLines($env:TEST_CAPTURE, [string[]]$args)\n$global:LASTEXITCODE = 17\n')
        else:
            dotnet.write_bytes(b'not executed')
        packages = root / 'packages'; packages.mkdir()
        args = ['-NativeDirectory', str(native), '-Dotnet', str(dotnet), '-PackagesDirectory', str(packages),
                '-Version', '1.2.3', '-TestingOnly', '-DiscoveryTesting', '-FilelessTesting',
                '-DiscoveryBridge', str(bridge), '-DiscoveryBridgeSha256', hashlib.sha256(bridge.read_bytes()).hexdigest()]
        return scripts, args

    def test_actual_mac_entry_refuses_busy_cache_without_output(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'project'
            scripts, args = self.prepare_mac_builder(root)
            result = self.run_builder_with_held_cache(root, 'build-mac-test.ps1', args, legacy_override=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('.cache-build.lock', result.stderr)
            self.assertFalse((root / '.artifacts/mac-package').exists())
            for constants in ('FILELESS_PAIRING_DIAGNOSTICS;NIAN_MAC_RETURN_RESEARCH', ' '):
                with self.subTest(define_constants=constants):
                    env = os.environ | {'DefineConstants': constants}
                    polluted = self.run_builder(root, 'build-mac-test.ps1', args, legacy_override=True, env=env)
                    self.assertNotEqual(polluted.returncode, 0)
                    self.assertIn('DefineConstants', polluted.stderr)
                    self.assertFalse((root / '.artifacts/mac-package').exists())
            cache = root / 'tests/.work/reusable-fileless-build/mac-fileless'; cache.mkdir()
            sentinel = cache / 'unowned.txt'; sentinel.write_text('keep')
            corrupt = self.run_builder(root, 'build-mac-test.ps1', args, legacy_override=True)
            self.assertNotEqual(corrupt.returncode, 0)
            self.assertIn('unowned', corrupt.stderr)
            self.assertFalse((root / '.artifacts/mac-package').exists())
            self.assertEqual(sentinel.read_text(), 'keep')
            ordinary = self.run_builder(root, 'build-mac-test.ps1', [value for value in args if value != '-FilelessTesting'],
                                        env=os.environ | {'DefineConstants': 'NIAN_MAC_RETURN_RESEARCH'})
            self.assertNotEqual(ordinary.returncode, 0)  # fixture executable is intentionally invalid
            self.assertNotIn('DefineConstants', ordinary.stderr)
            self.assertTrue((root / '.artifacts/mac-package').is_dir())

    def test_managed_mac_entry_reserves_only_after_validation_and_cache_lease(self):
        with tempfile.TemporaryDirectory(prefix='SYNTHETIC-NO-DEPLOY-') as folder:
            root = Path(folder) / 'project'
            scripts, args = self.prepare_mac_builder(root, capture=True)
            shutil.copyfile(ROOT / 'scripts/MacFilelessManagedRetention.psm1', scripts / 'MacFilelessManagedRetention.psm1')
            for name in ('scripts/verify_mac_fileless_candidate.py', 'src/DesktopToolbox.Mac/build/package_app.py',
                         'src/DesktopToolbox.Mac/build/preview_identity.py'):
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / name, path)
            managed_args = args + ['-ManagedRetention']
            evidence = Path(args[args.index('-NativeDirectory') + 1]) / 'build-evidence.json'
            original_evidence = evidence.read_bytes()
            duplicated = original_evidence.rstrip()[:-1] + b',"schema":"mac-native-build-v1"}'
            evidence.write_bytes(duplicated)
            rejected = self.run_builder(root, 'build-mac-test.ps1', managed_args)
            self.assertNotEqual(rejected.returncode, 0)
            self.assertFalse((root / '.artifacts/mac-package-managed/fileless').exists())
            evidence.write_bytes(original_evidence)
            checker = root / 'scripts/verify_mac_fileless_candidate.py'
            original_checker = checker.read_bytes()
            checker.write_text('raise SystemExit(0)\n')
            rejected = self.run_builder(root, 'build-mac-test.ps1', managed_args)
            self.assertNotEqual(rejected.returncode, 0)
            self.assertFalse((root / '.artifacts/mac-package-managed/fileless').exists())
            checker.write_bytes(original_checker)
            held = self.run_builder_with_held_cache(root, 'build-mac-test.ps1', managed_args)
            self.assertNotEqual(held.returncode, 0)
            managed = root / '.artifacts/mac-package-managed/fileless'
            self.assertFalse(managed.exists())
            polluted = self.run_builder(root, 'build-mac-test.ps1', managed_args,
                                        env=os.environ | {'DefineConstants': 'SYNTHETIC'})
            self.assertNotEqual(polluted.returncode, 0)
            self.assertFalse(managed.exists())
            capture = root / 'SYNTHETIC-capture.txt'
            result = self.run_builder(root, 'build-mac-test.ps1', managed_args,
                                      env=os.environ | {'TEST_CAPTURE': str(capture)})
            self.assertNotEqual(result.returncode, 0)  # fake SDK stops before producing any app
            index = managed / 'retention.json'
            self.assertTrue(index.is_file(), f'Expected reservation before fake SDK failure: {result.stderr}')
            state = json.loads(index.read_text(encoding='utf-8'))
            self.assertIsNone(state['current'])
            self.assertIsNone(state['previous'])
            self.assertTrue((managed / ('test-' + state['pending'])).is_dir())
            self.assertFalse((root / '.artifacts/mac-package').exists())
            before = (managed / 'retention.json').read_bytes()
            self.assertNotEqual(self.run_builder(root, 'build-mac-test.ps1', managed_args,
                                                env=os.environ | {'TEST_CAPTURE': str(capture)}).returncode, 0)
            self.assertEqual(before, (managed / 'retention.json').read_bytes())

    def test_managed_mac_switch_refuses_non_fileless_before_creating_output(self):
        with tempfile.TemporaryDirectory(prefix='SYNTHETIC-NO-DEPLOY-') as folder:
            root = Path(folder) / 'project'
            scripts, args = self.prepare_mac_builder(root)
            args = [item for item in args if item != '-FilelessTesting'] + ['-ManagedRetention']
            result = self.run_builder(root, 'build-mac-test.ps1', args)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('ManagedRetention', result.stderr)
            self.assertFalse((root / '.artifacts/mac-package-managed').exists())
            self.assertFalse((root / '.artifacts/mac-package').exists())
            ordinary = [item for item in args if item != '-ManagedRetention']
            explicit_false = self.run_builder(root, 'build-mac-test.ps1', ordinary, legacy_override=True)
            self.assertNotEqual(explicit_false.returncode, 0)  # fake SDK is intentionally invalid
            self.assertNotIn('ManagedRetention', explicit_false.stderr)
            self.assertTrue((root / '.artifacts/mac-package').is_dir())

    def test_mac_fileless_default_and_explicit_override_route(self):
        for mode in ('default', 'explicit_true', 'explicit_false'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory(prefix='SYNTHETIC-NO-DEPLOY-') as folder:
                root = Path(folder) / 'project'
                scripts, args = self.prepare_mac_builder(root, capture=True)
                shutil.copyfile(ROOT / 'scripts/MacFilelessManagedRetention.psm1', scripts / 'MacFilelessManagedRetention.psm1')
                for name in ('scripts/verify_mac_fileless_candidate.py', 'src/DesktopToolbox.Mac/build/package_app.py',
                             'src/DesktopToolbox.Mac/build/preview_identity.py'):
                    path = root / name; path.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(ROOT / name, path)
                if mode == 'explicit_true': args.append('-ManagedRetention')
                capture = root / 'SYNTHETIC-capture.txt'
                result = self.run_builder(root, 'build-mac-test.ps1', args, legacy_override=(mode == 'explicit_false'),
                                          env=os.environ | {'TEST_CAPTURE': str(capture)})
                self.assertNotEqual(result.returncode, 0)  # Fake SDK records arguments then fails.
                self.assertTrue(capture.is_file(), result.stderr)
                managed = root / '.artifacts/mac-package-managed/fileless'
                legacy = root / '.artifacts/mac-package'
                if mode == 'explicit_false':
                    self.assertFalse(managed.exists())
                    self.assertEqual(len(list(legacy.glob('test-*'))), 1)
                else:
                    self.assertFalse(legacy.exists())
                    state = json.loads((managed / 'retention.json').read_text(encoding='utf-8'))
                    self.assertTrue((managed / ('test-' + state['pending'])).is_dir())

    def test_managed_mac_bad_cache_is_refused_before_reservation(self):
        with tempfile.TemporaryDirectory(prefix='SYNTHETIC-NO-DEPLOY-') as folder:
            root = Path(folder) / 'project'
            scripts, args = self.prepare_mac_builder(root)
            shutil.copyfile(ROOT / 'scripts/MacFilelessManagedRetention.psm1', scripts / 'MacFilelessManagedRetention.psm1')
            cache = root / 'tests/.work/reusable-fileless-build/mac-fileless'
            cache.mkdir(parents=True)
            sentinel = cache / 'unowned.txt'; sentinel.write_text('SYNTHETIC')
            result = self.run_builder(root, 'build-mac-test.ps1', args + ['-ManagedRetention'])
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse((root / '.artifacts/mac-package-managed/fileless').exists())
            self.assertEqual(sentinel.read_text(), 'SYNTHETIC')

    def test_managed_mac_packager_receives_single_complete_switch_argument(self):
        source = (ROOT / 'scripts/build-mac-test.ps1').read_text(encoding='utf-8')
        assignment = next(line.strip() for line in source.splitlines() if '$managedArguments = if' in line)
        invocation = next(line.strip() for line in source.splitlines() if '& $python -B' in line and '@managedArguments' in line)
        with tempfile.TemporaryDirectory(prefix='SYNTHETIC-NO-DEPLOY-') as folder:
            capture = Path(folder) / 'captured-argv.json'
            env = os.environ | {'TEST_CAPTURE': str(capture)}
            for enabled in (True, False):
                with self.subTest(managed=enabled):
                    script = ("$ErrorActionPreference='Stop'; function Capture-SyntheticArgs { "
                              "[IO.File]::WriteAllText($env:TEST_CAPTURE, (ConvertTo-Json -InputObject @($args) -Compress)) }; "
                              "$useManagedRetention=$" + str(enabled).lower() + "; $python='Capture-SyntheticArgs'; $root='SYNTHETIC'; "
                              "$publish='SYNTHETIC-publish'; $output='SYNTHETIC-output'; $Version='1.2.3'; "
                              "$packageMode='--fileless-test'; $PackagesDirectory='SYNTHETIC-cache'; "
                              "$bridgeArguments=@('--discovery-bridge-sha256','synthetic'); "
                              "$assetsArguments=@('--assets-file','synthetic'); " + assignment + '; ' + invocation)
                    result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', script],
                                            env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
                    self.assertEqual(result.returncode, 0, result.stderr)
                    args = json.loads(capture.read_text(encoding='utf-8'))
                    self.assertEqual(args.count('--managed-fileless'), int(enabled))
                    self.assertNotIn('--managed-fileless'[:1], args)

    @unittest.skipUnless(DOTNET and Path(DOTNET).is_file(), 'set NIAN_FILELESS_TEST_DOTNET to isolated SDK')
    def test_actual_mac_entry_gates_and_pin_defeat_polluted_environment(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'project'
            _, args = self.prepare_mac_builder(root, capture=True)
            pin = args[-1]
            capture = root / 'dotnet-arguments.txt'
            env = os.environ | {'TEST_CAPTURE': str(capture), 'DOTNET_CLI_HOME': str(root / 'cli'),
                'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
                'DOTNET_GENERATE_ASPNET_CERTIFICATE': 'false', 'DOTNET_MULTILEVEL_LOOKUP': '0',
                'NianWifiEvidencePinnedSha256': 'f' * 64, 'Version': '9.9.9'}
            for name in ('NianFilelessPairingCandidate', 'NianFilelessPairingIsolatedTest', 'NianPairingDiagnostics',
                         'NianPeerEmergencyResearch', 'NianFenceResearchIsolatedTest', 'NianMacReturnResearch'):
                env[name] = 'true'
            env.pop('DefineConstants', None)
            result = self.run_builder(root, 'build-mac-test.ps1', args, legacy_override=True, env=env)
            self.assertNotEqual(result.returncode, 0)  # fake SDK captures parameters then fails
            self.assertTrue(capture.exists(), result.stderr)
            arguments = capture.read_text(encoding='utf-8-sig').splitlines()
            self.assertIn('build', arguments)
            for expected in ('-p:NianFilelessPairingCandidate=true', '-p:NianFilelessPairingIsolatedTest=true',
                             '-p:NianPairingDiagnostics=false', '-p:NianPeerEmergencyResearch=false',
                             '-p:NianFenceResearchIsolatedTest=false', '-p:NianMacReturnResearch=false',
                             '-p:NianWifiEvidencePinnedSha256=' + pin, '-p:Version=1.2.3'):
                self.assertIn(expected, arguments)
            properties = [arg for arg in arguments if arg.startswith('-p:')]
            for name in ('NianLink.MacDiscoveryCandidate', 'DesktopToolbox.DeskLink', 'NianLink.MacCandidateDiscovery'):
                project = ROOT / 'src' / name / (name + '.csproj')
                evaluated = subprocess.run([DOTNET, 'msbuild', str(project), '-nologo',
                    '-getProperty:DefineConstants,Version,NianWifiEvidencePinnedSha256,NianMacReturnResearch,NianFenceResearchIsolatedTest',
                    '-getItem:AssemblyMetadata,AssemblyAttribute', *properties], cwd=ROOT, env=env,
                    capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30)
                self.assertEqual(evaluated.returncode, 0, evaluated.stderr)
                result = json.loads(evaluated.stdout)
                symbols = set(result['Properties']['DefineConstants'].split(';'))
                metadata = {item['Identity'] for item in result['Items']['AssemblyMetadata']}
                self.assertNotIn('NIAN_MAC_RETURN_RESEARCH', symbols)
                self.assertNotIn('NIAN_FENCE_RESEARCH', symbols)
                self.assertNotIn('NianFenceResearchRoot', metadata)
                self.assertEqual(result['Properties']['NianMacReturnResearch'], 'false')
                self.assertEqual(result['Properties']['NianFenceResearchIsolatedTest'], 'false')
                self.assertEqual(result['Properties']['Version'], '1.2.3')
                self.assertEqual(result['Properties']['NianWifiEvidencePinnedSha256'], pin)
                if name == 'NianLink.MacDiscoveryCandidate':
                    self.assertTrue({'DISCOVERY_CANDIDATE', 'MULTI_DEVICE_CANDIDATE', 'FILELESS_PAIRING_CANDIDATE'} <= symbols)
                    self.assertIn('NianFilelessPairingCandidate', metadata)
                if name == 'DesktopToolbox.DeskLink':
                    self.assertIn('FILELESS_PAIRING_ISOLATED_TEST', symbols)
                    self.assertIn('NianFilelessPairingIsolatedTest', metadata)
                if name == 'NianLink.MacCandidateDiscovery':
                    attrs = [item for item in result['Items']['AssemblyAttribute']
                             if item.get('_Parameter1') == 'NianWifiEvidencePinnedSha256']
                    self.assertEqual(len(attrs), 1)
                    self.assertEqual(attrs[0].get('_Parameter2'), pin)

    def test_explicit_managed_discovery_accepts_mode_but_rejects_non_discovery(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'project'; (root / 'scripts').mkdir(parents=True)
            shutil.copyfile(ROOT / 'scripts/build-multidevice-windows-test.ps1', root / 'scripts/build-multidevice-windows-test.ps1')
            args = ['-Dotnet', 'missing', '-PackagesDirectory', 'missing', '-TestingOnly', '-DiscoveryPortable']
            default = self.run_builder(root, 'build-multidevice-windows-test.ps1', args)
            self.assertNotIn('currently supports fileless', default.stderr)
            managed = self.run_builder(root, 'build-multidevice-windows-test.ps1', [*args, '-ManagedRetention'])
            self.assertNotEqual(managed.returncode, 0)
            self.assertNotIn('currently supports fileless', managed.stderr)
            self.assertIn('Missing or linked input', managed.stderr)
            non_discovery = self.run_builder(root, 'build-multidevice-windows-test.ps1',
                                             ['-Dotnet', 'missing', '-PackagesDirectory', 'missing', '-TestingOnly', '-ManagedRetention'])
            self.assertNotEqual(non_discovery.returncode, 0)
            self.assertIn('only for discovery', non_discovery.stderr)
            self.assertFalse((root / '.artifacts').exists())

    def test_polluted_define_constants_refused_before_discovery_reservation(self):
        engine = ROOT / 'tests/.work/multidevice-native-runtime-01/deskflow-core.exe'
        if not engine.is_file() or hashlib.sha256(engine.read_bytes()).hexdigest().upper() != '04CE02FB644B2E3E80A23F50077AACA1168C5A1EA520E5D45E48AA68104CFFFA':
            self.skipTest('validated isolated Windows test core unavailable')
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'project'; scripts = root / 'scripts'; scripts.mkdir(parents=True)
            for name in ('build-multidevice-windows-test.ps1', 'BuildArtifactLock.psm1', 'FilelessArtifactCache.psm1',
                         'DiscoveryManagedRetention.psm1', 'DiscoveryPortablePaths.psm1'):
                shutil.copyfile(ROOT / 'scripts' / name, scripts / name)
            (scripts / 'SdkBaseline.psm1').write_text('function Assert-NianSdk {}')
            (scripts / 'WindowsTestKeyboardPayload.psm1').write_text('function Assert-WindowsTestEngineInputs {}')
            runtime = root / 'tests/.work/multidevice-native-runtime-01'; runtime.mkdir(parents=True)
            shutil.copyfile(engine, runtime / 'deskflow-core.exe')
            for name in ('NianLink.CandidateDiscovery', 'NianLink.MultiDevice', 'NianLink.DiscoveryPortable',
                         'DesktopToolbox.DeskLink', 'NianLink.MultiDevice.Shared'):
                (root / 'src' / name).mkdir(parents=True)
            dotnet = root / 'fixture-dotnet.exe'; dotnet.write_bytes(b'not executed')
            packages = root / 'packages'; packages.mkdir()
            args = ['-Dotnet', str(dotnet), '-PackagesDirectory', str(packages), '-TestingOnly', '-DiscoveryPortable']
            for value in ('FILELESS_PAIRING_CANDIDATE;NIAN_PEER_EMERGENCY_RESEARCH', ' '):
                with self.subTest(value=value):
                    polluted = os.environ | {'DefineConstants': value}
                    result = self.run_builder(root, 'build-multidevice-windows-test.ps1', args, env=polluted)
                    self.assertNotEqual(result.returncode, 0)
                    self.assertIn('DefineConstants', result.stderr)
                    self.assertFalse((root / '.artifacts/discovery-portable-managed/discovery').exists())

    @unittest.skipUnless(DOTNET and Path(DOTNET).is_file(), 'set NIAN_FILELESS_TEST_DOTNET to isolated SDK')
    def test_real_msbuild_gates_ignore_polluted_environment_in_all_discovery_modes(self):
        with tempfile.TemporaryDirectory() as folder:
            environment = os.environ | {'MODULE': str(MODULE), 'DOTNET_CLI_HOME': folder,
                'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
                'DOTNET_GENERATE_ASPNET_CERTIFICATE': 'false', 'DOTNET_MULTILEVEL_LOOKUP': '0'}
            for name in ('NianFilelessPairingCandidate', 'NianFilelessPairingIsolatedTest',
                         'NianPairingDiagnostics', 'NianPeerEmergencyResearch', 'NianFenceResearchIsolatedTest'):
                environment[name] = 'true'
            environment.pop('DefineConstants', None)
            for fileless, diagnostic in ((False, False), (True, False), (True, True)):
                with self.subTest(fileless=fileless, diagnostic=diagnostic):
                    env = environment | {'TEST_FILELESS': str(fileless), 'TEST_DIAGNOSTIC': str(diagnostic)}
                    command = ("$ErrorActionPreference='Stop'; Import-Module $env:MODULE -ErrorAction Stop; "
                               "$items=@(Get-NianDiscoveryBuildProperties -FilelessTesting ([bool]::Parse($env:TEST_FILELESS)) "
                               "-PairingDiagnostics ([bool]::Parse($env:TEST_DIAGNOSTIC))); "
                               "ConvertTo-Json -InputObject $items -Compress")
                    got = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', command], env=env,
                                         capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30)
                    self.assertEqual(got.returncode, 0, got.stderr)
                    properties = json.loads(got.stdout)
                    for name in ('NianLink.DiscoveryPortable', 'NianLink.CandidateDiscovery', 'DesktopToolbox.DeskLink'):
                        project = ROOT / 'src' / name / (name + '.csproj')
                        result = subprocess.run([DOTNET, 'msbuild', str(project), '-nologo',
                                                 '-getProperty:DefineConstants', '-getItem:AssemblyMetadata', *properties],
                                                cwd=ROOT, env=env, capture_output=True, text=True,
                                                encoding='utf-8', errors='replace', timeout=30)
                        self.assertEqual(result.returncode, 0, result.stderr)
                        values = json.loads(result.stdout)
                        symbols = set(values['Properties']['DefineConstants'].split(';'))
                        metadata = {row['Identity'] for row in values['Items']['AssemblyMetadata']}
                        self.assertNotIn('NIAN_PEER_EMERGENCY_RESEARCH', symbols)
                        self.assertNotIn('NIAN_FENCE_RESEARCH', symbols)
                        self.assertNotIn('NianFenceResearchRoot', metadata)
                        self.assertEqual('FILELESS_PAIRING_CANDIDATE' in symbols, fileless and name == 'NianLink.DiscoveryPortable')
                        self.assertEqual('FILELESS_PAIRING_ISOLATED_TEST' in symbols, fileless and name == 'DesktopToolbox.DeskLink')
                        self.assertEqual('FILELESS_PAIRING_DIAGNOSTICS' in symbols, diagnostic and name != 'DesktopToolbox.DeskLink')
                        self.assertEqual('NianFilelessPairingCandidate' in metadata, fileless and name == 'NianLink.DiscoveryPortable')
                        self.assertEqual('NianFilelessPairingIsolatedTest' in metadata, fileless and name == 'DesktopToolbox.DeskLink')
                        self.assertEqual('NianPairingDiagnostics' in metadata, diagnostic and name != 'DesktopToolbox.DeskLink')

    def test_ordinary_discovery_is_managed_by_default_and_legacy_opt_out_isolated(self):
        engine = ROOT / 'tests/.work/multidevice-native-runtime-01/deskflow-core.exe'
        if not engine.is_file() or hashlib.sha256(engine.read_bytes()).hexdigest().upper() != '04CE02FB644B2E3E80A23F50077AACA1168C5A1EA520E5D45E48AA68104CFFFA':
            self.skipTest('validated isolated Windows test core unavailable')
        for mode in ('default', 'explicit_true', 'explicit_false'):
            with self.subTest(mode=mode), tempfile.TemporaryDirectory() as folder:
                root = Path(folder) / 'project'; scripts = root / 'scripts'; scripts.mkdir(parents=True)
                for name in ('build-multidevice-windows-test.ps1', 'BuildArtifactLock.psm1', 'FilelessArtifactCache.psm1',
                             'DiscoveryManagedRetention.psm1', 'DiscoveryPortablePaths.psm1'):
                    shutil.copyfile(ROOT / 'scripts' / name, scripts / name)
                (scripts / 'SdkBaseline.psm1').write_text('function Assert-NianSdk {}')
                (scripts / 'WindowsTestKeyboardPayload.psm1').write_text('function Assert-WindowsTestEngineInputs {}')
                runtime = root / 'tests/.work/multidevice-native-runtime-01'; runtime.mkdir(parents=True)
                shutil.copyfile(engine, runtime / 'deskflow-core.exe')
                for name in ('NianLink.CandidateDiscovery', 'NianLink.MultiDevice', 'NianLink.DiscoveryPortable',
                             'DesktopToolbox.DeskLink', 'NianLink.MultiDevice.Shared'):
                    (root / 'src' / name).mkdir(parents=True)
                dotnet = root / 'fixture-dotnet.exe'; dotnet.write_bytes(b'not executed')
                packages = root / 'packages'; packages.mkdir()
                args = ['-Dotnet', str(dotnet), '-PackagesDirectory', str(packages), '-TestingOnly', '-DiscoveryPortable']
                if mode == 'explicit_true':
                    args.append('-ManagedRetention')
                legacy = mode == 'explicit_false'
                busy = self.run_builder_with_held_cache(root, 'build-multidevice-windows-test.ps1', args, legacy)
                self.assertNotEqual(busy.returncode, 0)
                if legacy:
                    self.assertTrue((root / '.artifacts/discovery-portable').is_dir())
                    self.assertFalse((root / '.artifacts/discovery-portable-managed/discovery').exists())
                    continue
                self.assertIn('.cache-build.lock', busy.stderr)
                self.assertFalse((root / '.artifacts/discovery-portable-managed/discovery').exists())
                cache = root / 'tests/.work/reusable-fileless-build/windows-discovery'; cache.mkdir()
                sentinel = cache / 'unowned.txt'; sentinel.write_text('keep')
                corrupt = self.run_builder(root, 'build-multidevice-windows-test.ps1', args)
                self.assertNotEqual(corrupt.returncode, 0)
                self.assertIn('unowned', corrupt.stderr)
                self.assertFalse((root / '.artifacts/discovery-portable-managed/discovery').exists())
                self.assertEqual(sentinel.read_text(), 'keep')
                (cache / '.nian-fileless-build-cache').write_text('nian-fileless-build-cache-v1:windows-discovery')
                started = self.run_builder(root, 'build-multidevice-windows-test.ps1', args)
                self.assertNotEqual(started.returncode, 0)  # fixture executable is intentionally invalid
                managed = root / '.artifacts/discovery-portable-managed/discovery'
                state = json.loads((managed / 'retention.json').read_text())
                self.assertIsNotNone(state['pending'])
                self.assertFalse((root / '.artifacts/discovery-portable').exists())

    def test_same_build_reuses_cache_while_platform_and_diagnostic_are_separate(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'project'
            root.mkdir()
            win1 = self.resolve(root, 'windows')
            win2 = self.resolve(root, 'windows')
            diag = self.resolve(root, 'windows', True)
            mac = self.resolve(root, 'mac')
            discovery = self.resolve(root, 'windows', discovery=True)
            for result in (win1, win2, diag, mac, discovery):
                self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(win1.stdout.strip(), win2.stdout.strip())
            paths = [Path(r.stdout.strip()) for r in (win1, diag, mac, discovery)]
            self.assertEqual(len(set(paths)), 4)
            for path in paths:
                self.assertTrue(path.is_relative_to(root / 'tests' / '.work'))
                self.assertTrue(path.is_dir())
            sentinel = paths[0] / 'preserved.txt'
            sentinel.write_text('cache data')
            again = self.resolve(root, 'windows')
            self.assertEqual(again.returncode, 0, again.stderr)
            self.assertEqual(sentinel.read_text(), 'cache data')
            self.assertFalse((root / 'test-profiles').exists())
            self.assertEqual(paths[3].name, 'windows-discovery')
            self.assertNotEqual(self.resolve(root, 'windows', diagnostic=True, discovery=True).returncode, 0)
            self.assertNotEqual(self.resolve(root, 'mac', discovery=True).returncode, 0)

    def test_refuses_unowned_existing_cache_and_wrong_configuration(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'project'
            root.mkdir()
            result = self.resolve(root, 'mac', True)
            self.assertNotEqual(result.returncode, 0)
            result = self.resolve(root, 'windows')
            self.assertEqual(result.returncode, 0, result.stderr)
            marker = Path(result.stdout.strip()) / '.nian-fileless-build-cache'
            marker.write_text('not the expected owner', encoding='utf-8')
            failed = self.resolve(root, 'windows')
            self.assertNotEqual(failed.returncode, 0)
            self.assertEqual(marker.read_text(encoding='utf-8'), 'not the expected owner')

    def test_refuses_linked_work_parent(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'project'
            (root / 'tests').mkdir(parents=True)
            elsewhere = Path(folder) / 'outside'
            elsewhere.mkdir()
            env = os.environ | {'TEST_LINK': str(root / 'tests' / '.work'), 'TEST_TARGET': str(elsewhere)}
            junction = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command',
                                       'New-Item -ItemType Junction -Path $env:TEST_LINK -Target $env:TEST_TARGET -ErrorAction Stop | Out-Null'],
                                      env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
            if junction.returncode:
                self.skipTest(f'cannot create test-only junction: {junction.stderr}')
            result = self.resolve(root, 'windows')
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(list(elsewhere.iterdir()), [])

    def test_refuses_linked_cache_lease_file(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'project'
            base = root / 'tests/.work/reusable-fileless-build'
            base.mkdir(parents=True)
            elsewhere = Path(folder) / 'outside'; elsewhere.mkdir()
            sentinel = elsewhere / 'untouched.txt'; sentinel.write_text('keep')
            env = os.environ | {'TEST_LINK': str(base / '.cache-build.lock'), 'TEST_TARGET': str(elsewhere)}
            result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command',
                                     'New-Item -ItemType Junction -Path $env:TEST_LINK -Target $env:TEST_TARGET -ErrorAction Stop | Out-Null'],
                                    env=env, capture_output=True)
            if result.returncode:
                self.skipTest('directory junction unavailable')
            self.assertNotEqual(self.resolve(root, 'windows').returncode, 0)
            self.assertEqual(sentinel.read_text(), 'keep')

    def test_packagers_use_cache_without_changing_per_package_identity(self):
        win = (ROOT / 'scripts/build-multidevice-windows-test.ps1').read_text(encoding='utf-8')
        mac = (ROOT / 'scripts/build-mac-test.ps1').read_text(encoding='utf-8')
        self.assertIn('Get-NianFilelessArtifactCache', win)
        self.assertIn('FilelessArtifactCache.psm1', win)
        self.assertIn('Get-NianFilelessArtifactCache', mac)
        self.assertIn('FilelessArtifactCache.psm1', mac)
        self.assertIn("Get-NianFilelessArtifactCache -ProjectRoot $root -Platform 'mac'", mac)
        self.assertIn('Invoke-NianFilelessPublish', mac)
        self.assertNotIn('tests/.work/fileless-mac-', mac)
        self.assertIn("'.artifacts/mac-package/test-' + [guid]::NewGuid().ToString('N')", mac)
        self.assertIn('if ($FilelessTesting) { Invoke-NianFilelessPublish -Dotnet $Dotnet -PublishArgs $publishArgs -Lease $cacheLease }', mac)
        self.assertIn('else { & $Dotnet @publishArgs }', mac)
        for script in (win, mac):
            self.assertIn('Enter-NianFilelessCacheLease -ProjectRoot $root', script)
            self.assertIn('Exit-NianFilelessCacheLease -Lease $cacheLease', script)
        self.assertIn("$runId = [guid]::NewGuid().ToString('N')", win)
        self.assertIn("'test-' + $runId", win)
        self.assertIn("'discovery-portable-id.txt'", win)
        self.assertIn('PairingDiagnostics', win)

    def test_new_runtime_module_is_in_required_source_candidate(self):
        from scripts import public_source_candidate as candidate
        self.assertIn('scripts/FilelessArtifactCache.psm1', candidate.SINGLE_FILES)
        self.assertIn('scripts/FilelessArtifactCache.psm1', candidate.REQUIRED_FILES)
        self.assertIn('tests/test_fileless_artifact_cache.py', candidate.REQUIRED_FILES)

    @unittest.skipUnless(DOTNET and Path(DOTNET).is_file(), 'set NIAN_FILELESS_TEST_DOTNET to isolated SDK')
    def test_real_dotnet_reuse_tracks_source_properties_and_package_directory(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'project'
            root.mkdir()
            win = self.resolve(root, 'windows')
            diag = self.resolve(root, 'windows', True)
            mac = self.resolve(root, 'mac')
            for result in (win, diag, mac):
                self.assertEqual(result.returncode, 0, result.stderr)
            project = root / 'fixture.csproj'
            project.write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
<PropertyGroup Condition="'$(PairingDiagnostics)' == 'true'"><DefineConstants>$(DefineConstants);PAIRING_DIAGNOSTICS</DefineConstants></PropertyGroup>
<ItemGroup><AssemblyMetadata Include="BridgePin" Value="$(BridgePin)" /><Compile Remove="lib/**/*.cs" /><ProjectReference Include="lib/Library.csproj" /></ItemGroup>
</Project>''', encoding='utf-8')
            library = root / 'lib'
            library.mkdir()
            (library / 'Library.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>', encoding='utf-8')
            lib_source = library / 'Library.cs'
            lib_source.write_text('public static class Library { public static string Value => "lib-one"; }', encoding='utf-8')
            (root / 'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding='utf-8')
            code = '''using System.Reflection;
Console.WriteLine("SOURCE");
#if PAIRING_DIAGNOSTICS
Console.WriteLine("diagnostics");
#else
Console.WriteLine("normal");
#endif
Console.WriteLine(typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x => x.Key == "BridgePin").Value);
Console.WriteLine(typeof(Program).Assembly.GetName().Version);
Console.WriteLine(Library.Value);
'''
            source = root / 'Program.cs'
            env = os.environ | {'DOTNET_CLI_HOME': str(root / 'cli'), 'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
                                'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1', 'DOTNET_GENERATE_ASPNET_CERTIFICATE': 'false',
                                'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH': 'false', 'DOTNET_MULTILEVEL_LOOKUP': '0'}

            def publish(options):
                publish_args = ['publish', str(project), '--output', str(root / 'published'), *options]
                helper_env = env | {'TEST_DOTNET': DOTNET, 'TEST_MODULE': str(MODULE), 'TEST_ARGS': json.dumps(publish_args)}
                command = ("$ErrorActionPreference='Stop'; Import-Module $env:TEST_MODULE -ErrorAction Stop; "
                           "$lease=Enter-NianFilelessCacheLease -ProjectRoot $env:TEST_PROJECT; "
                           "try { Invoke-NianFilelessPublish -Dotnet $env:TEST_DOTNET -PublishArgs (ConvertFrom-Json $env:TEST_ARGS) -Lease $lease } "
                           "finally { Exit-NianFilelessCacheLease -Lease $lease }")
                helper_env['TEST_PROJECT'] = str(root)
                return subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', command], cwd=root,
                                      env=helper_env, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=90)

            def build(cache, src, diagnostic='false', pin='first', version='1.0.0', packages='packages-a', succeeds=True, old_timestamp=False):
                source.write_text(code.replace('SOURCE', src), encoding='utf-8')
                if old_timestamp:
                    os.utime(source, (1_000_000_000, 1_000_000_000))
                (root / packages).mkdir(exist_ok=True)
                options = ['-c', 'Release', '--nologo', '-p:UseArtifactsOutput=true', f'-p:ArtifactsPath={cache}',
                        f'-p:PairingDiagnostics={diagnostic}', f'-p:BridgePin={pin}',
                        f'-p:Version={version}', '--packages', str(root / packages),
                        '--source', str(root / packages), '-p:NuGetAudit=false', '--self-contained', 'false']
                result = publish(options)
                self.assertEqual(result.returncode == 0, succeeds, result.stdout[-3000:] + result.stderr[-1000:])
                if not succeeds:
                    return None
                binary = root / 'published' / 'fixture.dll'
                executed = subprocess.run([DOTNET, str(binary)], cwd=root, env=env, capture_output=True,
                                          text=True, encoding='utf-8', errors='replace', timeout=30)
                self.assertEqual(executed.returncode, 0, executed.stderr)
                return executed.stdout.splitlines()

            win_cache, diag_cache, mac_cache = (r.stdout.strip() for r in (win, diag, mac))
            self.assertEqual(build(win_cache, 'one'), ['one', 'normal', 'first', '1.0.0.0', 'lib-one'])
            self.assertEqual(build(win_cache, 'one'), ['one', 'normal', 'first', '1.0.0.0', 'lib-one'])
            lib_source.write_text('public static class Library { public static string Value => "lib-two"; }', encoding='utf-8')
            os.utime(lib_source, (1_000_000_000, 1_000_000_000))
            self.assertEqual(build(win_cache, 'two', old_timestamp=True), ['two', 'normal', 'first', '1.0.0.0', 'lib-two'])
            self.assertEqual(build(diag_cache, 'two', diagnostic='true'), ['two', 'diagnostics', 'first', '1.0.0.0', 'lib-two'])
            self.assertEqual(build(win_cache, 'two'), ['two', 'normal', 'first', '1.0.0.0', 'lib-two'])
            self.assertEqual(build(mac_cache, 'two', pin='changed', version='2.0.0', packages='packages-b'),
                             ['two', 'normal', 'changed', '2.0.0.0', 'lib-two'])
            self.assertEqual(build(mac_cache, 'three', pin='other', version='3.0.0', packages='packages-a'),
                             ['three', 'normal', 'other', '3.0.0.0', 'lib-two'])
            # Failed builds must not poison a subsequent invocation of the same cache.
            source.write_text('this is not valid C#', encoding='utf-8')
            last_published = (root / 'published' / 'fixture.dll').read_bytes()
            failed = publish(['-c', 'Release', '--nologo', '-p:UseArtifactsOutput=true', f'-p:ArtifactsPath={win_cache}'])
            self.assertNotEqual(failed.returncode, 0)
            self.assertEqual(last_published, (root / 'published' / 'fixture.dll').read_bytes())
            self.assertEqual(build(win_cache, 'four'), ['four', 'normal', 'first', '1.0.0.0', 'lib-two'])


if __name__ == '__main__':
    unittest.main()
