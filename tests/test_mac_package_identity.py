"""Offline checks for standalone Mac bundle identity generation."""
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest
import uuid
import hashlib
import json
import plistlib
import re


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('package_app', ROOT / 'src/DesktopToolbox.Mac/build/package_app.py')
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


def apphost(identifier):
    # Minimal ARM64 executable with one LC_UUID load command.
    return struct.pack('<8I', 0xFEEDFACF, 0x0100000C, 0, 2, 1, 24, 0, 0) + struct.pack('<2I', 27, 24) + uuid.UUID(identifier).bytes


class MacPackageIdentityTests(unittest.TestCase):
    def test_realtime_capture_policy_recognizes_exact_packaged_bundle_ids(self):
        # Static cross-language contract only: actual Swift and ScreenCaptureKit
        # behavior require separate macOS native tests.
        with (ROOT / 'src/DesktopToolbox.Mac/Assets/Info.plist').open('rb') as stream:
            original = plistlib.load(stream)
        modes = ({}, {'multi_device_test': True}, {'discovery_test': True}, {'fileless_test': True})
        packaged = {package.bundle_identity(original, **mode)['CFBundleIdentifier'] for mode in modes}
        self.assertEqual(packaged, {'com.niannian.nianlink', 'com.niannian.nianlink.multidevice-test',
                                    'com.niannian.nianlink.discovery-test', 'com.niannian.nianlink.fileless-test'})
        swift = (ROOT / 'src/DesktopToolbox.Mac/Native/RealtimeCapture.swift').read_text(encoding='utf-8')
        match = re.search(r'static let ownBundles:\s*Set<String>\s*=\s*(\[[^\]]*\])', swift, re.DOTALL)
        self.assertIsNotNone(match, 'Capture policy must expose an explicit exact-ID set.')
        declared = json.loads(match.group(1))
        self.assertEqual(len(declared), len(set(declared)), 'Duplicate bundle identifiers are not expected.')
        self.assertEqual(set(declared), packaged | {'com.nian.desktoptoolbox.desklink',
                                                   'com.nian.desktoptoolbox.desklink.native-candidate',
                                                   'com.nian.desktoptoolbox.desklink.preview-candidate'})

    def test_actual_multidevice_plist_keeps_network_purpose_and_separate_identity(self):
        with (ROOT / 'src/DesktopToolbox.Mac/Assets/Info.plist').open('rb') as stream:
            original = plistlib.load(stream)
        candidate = package.bundle_identity(original, multi_device_test=True)
        self.assertEqual(original['CFBundleIdentifier'], 'com.niannian.nianlink')
        self.assertEqual(candidate['CFBundleIdentifier'], 'com.niannian.nianlink.multidevice-test')
        self.assertTrue(candidate['NSLocalNetworkUsageDescription'].strip())
        self.assertEqual(candidate['NSLocalNetworkUsageDescription'], original['NSLocalNetworkUsageDescription'])

    def test_multidevice_candidate_has_its_own_payload_uuid(self):
        with tempfile.TemporaryDirectory() as folder:
            bundle = Path(folder) / 'NianDeskLink.app'
            binary = bundle / 'Contents/MacOS/NianDeskLink'
            binary.parent.mkdir(parents=True)
            host = apphost('00000000-0000-0000-0000-000000000001')
            binary.write_bytes(host)
            (bundle / 'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleIdentifier': 'com.niannian.nianlink.multidevice-test'}))
            first = package.apply_preview_payload_identity(bundle)
            self.assertNotEqual(first['executableUuid'], '00000000-0000-0000-0000-000000000001')
            self.assertEqual(package.preview_identity_helpers().macho_uuid(binary.read_bytes())[1], first['executableUuid'])
            self.assertEqual(package.preview_bundle_identity(bundle), first)
            (bundle / 'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleIdentifier': 'com.niannian.nianlink'}))
            self.assertNotEqual(package.preview_bundle_identity(bundle)['executableUuid'], first['executableUuid'])

    def test_multidevice_test_bundle_identity_is_separate_from_installed_app(self):
        original = {'CFBundleIdentifier': 'com.niannian.nianlink', 'CFBundleName': '年年互联', 'CFBundleDisplayName': '年年互联'}
        selected = package.bundle_identity(original, multi_device_test=True)
        self.assertEqual(selected['CFBundleIdentifier'], 'com.niannian.nianlink.multidevice-test')
        self.assertEqual(selected['CFBundleDisplayName'], '年年互联·多设备测试')
        self.assertEqual(original['CFBundleIdentifier'], 'com.niannian.nianlink')
        self.assertEqual(package.bundle_identity(original, multi_device_test=False), original)
        with self.assertRaises(ValueError):
            package.bundle_identity({'CFBundleIdentifier': 'unexpected'}, multi_device_test=True)

    def test_helper_is_local_and_loadable(self):
        helper = package.preview_identity_helpers()
        self.assertTrue(Path(helper.__file__).resolve().is_relative_to(ROOT))
        self.assertEqual(helper.PREVIEW_UUID_SCHEME, 'preview-payload-v2')

    def test_identity_changes_with_payload_and_bundle_but_not_existing_uuid(self):
        helper = package.preview_identity_helpers()
        first = apphost('00000000-0000-0000-0000-000000000001')
        second = apphost('00000000-0000-0000-0000-000000000002')
        rows = {'NianDeskLink.app/Contents/MacOS/NianDeskLink': {'bytes': len(first), 'sha256': 'ignored', 'unixMode': '100755'},
                'NianDeskLink.app/Contents/Resources/test.txt': {'bytes': 1, 'sha256': 'A' * 64, 'unixMode': '100644'}}
        a = helper.preview_payload_sha256(rows, 'NianDeskLink.app', first)
        self.assertEqual(a, helper.preview_payload_sha256(rows, 'NianDeskLink.app', second))
        changed = dict(rows)
        changed['NianDeskLink.app/Contents/Resources/test.txt'] = dict(rows['NianDeskLink.app/Contents/Resources/test.txt'], sha256='B' * 64)
        self.assertNotEqual(a, helper.preview_payload_sha256(changed, 'NianDeskLink.app', first))
        self.assertNotEqual(helper.derive_preview_executable_uuid('com.niannian.nianlink', a),
                            helper.derive_preview_executable_uuid('com.example.other', a))

    def test_invalid_macho_fails_closed(self):
        with self.assertRaises(ValueError):
            package.preview_identity_helpers().macho_uuid(b'not-a-mach-o')

    def test_icon_notice_is_present(self):
        notice = ROOT / 'THIRD-PARTY-NOTICES/lucide-1.8.0.txt'
        self.assertIn('ISC License', notice.read_text(encoding='utf-8'))
        self.assertIn('Cole Bemis', notice.read_text(encoding='utf-8'))

    def test_packages_cache_uses_restore_metadata_or_explicit_override(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)
            self.assertEqual(package.package_cache({'project': {'restore': {'packagesPath': str(path)}}}, None), path)
            self.assertEqual(package.package_cache({}, str(path)), path)
            with self.assertRaises(ValueError):
                package.package_cache({}, None)

    def test_source_manifest_covers_restored_transitive_projects_and_build_inputs(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            mac = root / 'src/DesktopToolbox.Mac'
            core = root / 'src/Nian.Input.Core'
            ui = root / 'src/DesktopToolbox.Collaboration.Ui'
            for directory in (mac, core, ui, root / 'scripts'):
                directory.mkdir(parents=True)
            files = {
                'src/DesktopToolbox.Mac/DesktopToolbox.Mac.csproj': b'mac',
                'src/Nian.Input.Core/Nian.Input.Core.csproj': b'core project',
                'src/Nian.Input.Core/Source.cs': b'core source',
                'src/DesktopToolbox.Collaboration.Ui/index.html': b'ui',
                'src/Directory.Build.props': b'properties',
                'scripts/build-mac-test.ps1': b'build',
            }
            for name, data in files.items():
                (root / name).write_bytes(data)
            (core / 'obj').mkdir()
            (core / 'obj/secret.txt').write_text('must not be included')
            assets = {'libraries': {'Nian.Input.Core/1.0': {'type': 'project', 'path': '../Nian.Input.Core/Nian.Input.Core.csproj'}}}
            entries = package.collect_sources(root, assets)
            by_path = {entry['path']: entry['sha256'] for entry in entries}
            self.assertEqual(by_path, {name: hashlib.sha256(data).hexdigest() for name, data in files.items()})
            self.assertEqual([entry['path'] for entry in entries], sorted(by_path))

    def test_source_manifest_refuses_project_outside_source_tree(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'src/DesktopToolbox.Mac').mkdir(parents=True)
            outside = root / 'outside.csproj'
            outside.write_text('<Project/>')
            assets = {'libraries': {'bad/1': {'type': 'project', 'path': '../../outside.csproj'}}}
            with self.assertRaises(ValueError):
                package.collect_sources(root, assets)

    def test_real_restore_graph_includes_every_local_project(self):
        assets_file = ROOT / 'src/DesktopToolbox.Mac/obj/project.assets.json'
        if not assets_file.is_file():
            self.skipTest('Restore Mac dependencies before checking the project graph.')
        assets = json.loads(assets_file.read_text(encoding='utf-8-sig'))
        entries = {entry['path'] for entry in package.collect_sources(ROOT, assets)}
        for info in assets['libraries'].values():
            if info.get('type') == 'project':
                file = (assets_file.parent.parent / info['path'].replace('\\', '/')).resolve()
                self.assertIn(file.relative_to(ROOT).as_posix(), entries)
        self.assertIn('src/Nian.Input.Security/PrivateFiles.cs', entries)
        self.assertFalse(any('/obj/' in path or '/bin/' in path or '/__pycache__/' in path for path in entries))


if __name__ == '__main__':
    unittest.main()
