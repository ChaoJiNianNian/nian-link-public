"""Offline acceptance tests for the Windows Mac-native input boundary."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import struct
import subprocess
import tempfile
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[1]
POWERSHELL = shutil.which('pwsh')
SOURCES = ('DeskLinkMedia.swift', 'RealtimeCapture.swift', 'RealtimeCaptureOutput.swift',
           'build-media.sh', 'media-sources.json', 'DeskLinkWeb.swift', 'build-web.sh')


def macho(cpu=0x0100000c, kind=2, magic=0xfeedfacf):
    return struct.pack('<8I', magic, cpu, 0, kind, 0, 0, 0, 0)


@unittest.skipUnless(os.name == 'nt' and POWERSHELL, 'Windows and PowerShell 7 are required')
class MacNativeInputTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        scripts = self.root / 'scripts'
        scripts.mkdir()
        shutil.copyfile(ROOT / 'scripts/build-mac-test.ps1', scripts / 'build-mac-test.ps1')
        shutil.copyfile(ROOT / 'scripts/SdkBaseline.psm1', scripts / 'SdkBaseline.psm1')
        (scripts / 'BuildArtifactLock.psm1').write_text(
            "function Enter-ToolboxBuildLock { throw 'PASSED_NATIVE_VALIDATION' }\n"
            "Export-ModuleMember -Function Enter-ToolboxBuildLock\n", encoding='utf-8')
        self.source = self.root / 'src/DesktopToolbox.Mac/Native'
        self.source.mkdir(parents=True)
        for name in SOURCES:
            (self.source / name).write_bytes(('synthetic ' + name).encode())
        self.native_root = self.root / '.artifacts/mac-native'
        self.native_root.mkdir(parents=True)
        self.native = self.native_root / ('native-' + uuid.uuid4().hex)
        self.native.mkdir()
        self.sdk = self.root / 'sdk.cmd'
        self.sdk.write_text('@echo 10.0.404\r\n', encoding='ascii')
        self.packages = self.root / 'packages'
        self.packages.mkdir()
        self.binaries = {'DeskLinkMedia': macho(kind=2), 'libDeskLinkWeb.dylib': macho(kind=6)}
        self.write_evidence()

    def write_evidence(self):
        def record(name, data):
            return {'name': name, 'sha256': hashlib.sha256(data).hexdigest().upper()}
        source_files = [record(name, (self.source / name).read_bytes()) for name in SOURCES]
        binaries = []
        for name, data in self.binaries.items():
            (self.native / name).write_bytes(data)
            binaries.append({**record(name, data), 'architecture': 'arm64'})
        (self.native / 'build-evidence.json').write_text(json.dumps({
            'schema': 'mac-native-build-v1', 'label': self.native.name,
            'sourceFiles': source_files, 'binaries': binaries,
        }), encoding='utf-8')

    def invoke(self, *extra):
        cmd = [POWERSHELL, '-NoProfile', '-NonInteractive', '-File',
               str(self.root / 'scripts/build-mac-test.ps1'),
               '-NativeDirectory', str(self.native), '-Dotnet', str(self.sdk),
               '-PackagesDirectory', str(self.packages), '-Version', '1.0.0', '-TestingOnly', *extra]
        result = subprocess.run(cmd, text=True, encoding='utf-8', errors='replace', capture_output=True, timeout=30)
        self.assertNotEqual(result.returncode, 0)
        return result.stdout + result.stderr

    def test_valid_direct_arm64_inputs_reach_the_build_lock(self):
        self.assertIn('PASSED_NATIVE_VALIDATION', self.invoke())

    def test_previous_sdk_is_rejected_before_packaging(self):
        self.sdk.write_text('@echo 10.0.402\r\n', encoding='ascii')
        output = self.invoke()
        self.assertIn('Expected .NET SDK 10.0.404', output)
        self.assertNotIn('PASSED_NATIVE_VALIDATION', output)

    def test_nested_native_directory_is_rejected_even_without_links(self):
        old = self.native
        intermediate = self.native_root / 'nested'
        intermediate.mkdir()
        self.native = intermediate / old.name
        old.rename(self.native)
        output = self.invoke()
        self.assertNotIn('PASSED_NATIVE_VALIDATION', output)
        self.assertIn('direct', output)

    def test_malformed_wrong_cpu_and_wrong_file_kind_rejected_after_matching_hashes(self):
        cases = [
            ('DeskLinkMedia', b'short'),
            ('DeskLinkMedia', macho(magic=0xdeadbeef)),
            ('DeskLinkMedia', macho(cpu=0x01000007)),
            ('DeskLinkMedia', macho(kind=6)),
            ('libDeskLinkWeb.dylib', macho(kind=2)),
            ('libDeskLinkWeb.dylib', macho(cpu=0x01000007, kind=6)),
        ]
        for name, data in cases:
            with self.subTest(name=name, header=data.hex()):
                original = self.binaries[name]
                self.binaries[name] = data
                self.write_evidence()
                output = self.invoke()
                self.assertNotIn('PASSED_NATIVE_VALIDATION', output)
                self.assertIn('Mach-O', output)
                self.binaries[name] = original

    def test_discovery_bridge_header_is_checked_even_with_matching_hash(self):
        bridge_dir = self.root / '.artifacts/mac-discovery-native' / ('test-' + uuid.uuid4().hex)
        bridge_dir.mkdir(parents=True)
        bridge = bridge_dir / 'libNianWifiEvidence.dylib'
        cases = [
            (macho(kind=6)[:8], False),
            (macho(kind=6)[:31], False),
            (macho(kind=2), False),
            (macho(cpu=0x01000007, kind=6), False),
            (macho(magic=0xdeadbeef, kind=6), False),
            (macho(kind=6), True),
        ]
        for data, accepted in cases:
            with self.subTest(header=data.hex(), accepted=accepted):
                bridge.write_bytes(data)
                digest = hashlib.sha256(data).hexdigest()
                output = self.invoke('-DiscoveryTesting', '-DiscoveryBridge', str(bridge),
                                     '-DiscoveryBridgeSha256', digest)
                if accepted:
                    # This synthetic test reaches the lock, not a real signature check.
                    self.assertIn('PASSED_NATIVE_VALIDATION', output)
                else:
                    self.assertNotIn('PASSED_NATIVE_VALIDATION', output)
                    self.assertIn('Mach-O', output)

    def test_linked_native_directory_is_rejected(self):
        target = self.root / 'outside-native'
        self.native.rename(target)
        try:
            self.native.symlink_to(target, target_is_directory=True)
        except OSError:
            if os.name != 'nt':
                self.skipTest('Directory symlinks unavailable')
            made = subprocess.run(['cmd', '/c', 'mklink', '/J', str(self.native), str(target)],
                                  capture_output=True, timeout=10)
            if made.returncode != 0 or not os.path.isjunction(self.native):
                self.skipTest('Directory reparse points unavailable')
        try:
            output = self.invoke()
            self.assertNotIn('PASSED_NATIVE_VALIDATION', output)
            self.assertIn('linked directory', output)
        finally:
            if self.native.is_symlink():
                self.native.unlink()
            elif os.name == 'nt' and os.path.isjunction(self.native):
                os.rmdir(self.native)


if __name__ == '__main__':
    unittest.main()
