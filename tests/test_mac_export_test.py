import hashlib
import importlib.util
import json
import plistlib
import tempfile
import unittest
from unittest.mock import patch
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('mac_export_test', ROOT / 'scripts/build-mac-export-test.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class MacExportTest(unittest.TestCase):
    def setUp(self):
        work = ROOT / 'tests/.work'
        work.mkdir(exist_ok=True)
        self.tmp = tempfile.TemporaryDirectory(dir=work, prefix='mac-export-check-')
        self.root = Path(self.tmp.name)
        self.addCleanup(self.tmp.cleanup)
        self.source = self.root / 'src/Nian.Input.Mac/example.cs'
        self.source.parent.mkdir(parents=True)
        self.source.write_bytes(b'synthetic source')

    def make_app(self, *, version='0.1.5', identity='com.niannian.nianlink', tamper=False, extra=None):
        archive = self.root / 'synthetic.zip'
        plist = plistlib.dumps({'CFBundleIdentifier': identity, 'CFBundleShortVersionString': version})
        data = b'synthetic app'
        files = {module.APP_PLIST: plist, 'NianDeskLink.app/Contents/MacOS/NianDeskLink': data}
        manifest = {'version': version, 'target': 'osx-arm64', 'signed': False, 'notarized': False,
                    'sources': [{'path': 'src/Nian.Input.Mac/example.cs', 'sha256': hashlib.sha256(self.source.read_bytes()).hexdigest()}],
                    'files': [{'path': name, 'bytes': len(content), 'sha256': hashlib.sha256(content).hexdigest()} for name, content in files.items()]}
        with zipfile.ZipFile(archive, 'w') as z:
            for name, content in files.items():
                z.writestr(name, b'tampered' if tamper and name.endswith('/NianDeskLink') else content)
            z.writestr(module.APP_MANIFEST, json.dumps(manifest))
            if extra:
                z.writestr(extra, b'unexpected')
        return archive, module.sha(archive)

    def test_synthetic_app_inventory_and_identity_pass(self):
        archive, digest = self.make_app()
        module.verify_app(archive, '0.1.5', digest, self.root)

    def test_different_bundle_identity_is_rejected(self):
        archive, digest = self.make_app(identity='com.other.app')
        with self.assertRaisesRegex(ValueError, 'identity'):
            module.verify_app(archive, '0.1.5', digest, self.root)

    def test_multidevice_identity_requires_explicit_mode(self):
        archive, digest = self.make_app(identity='com.niannian.nianlink.multidevice-test')
        with self.assertRaisesRegex(ValueError, 'identity'):
            module.verify_app(archive, '0.1.5', digest, self.root)
        module.verify_app(archive, '0.1.5', digest, self.root, multi_device=True)

    def test_multidevice_mode_rejects_legacy_app(self):
        archive, digest = self.make_app()
        with self.assertRaisesRegex(ValueError, 'identity'):
            module.verify_app(archive, '0.1.5', digest, self.root, multi_device=True)

    def test_flat_app_keeps_contents_and_file_permissions(self):
        archive, _ = self.make_app(identity='com.niannian.nianlink.multidevice-test')
        flat = self.root / 'flat.zip'
        with zipfile.ZipFile(flat, 'w') as output:
            module.write_flat_app(output, archive)
        with zipfile.ZipFile(archive) as source, zipfile.ZipFile(flat) as output:
            self.assertEqual(set(source.namelist()), set(output.namelist()))
            for name in source.namelist():
                self.assertEqual(source.read(name), output.read(name))
                self.assertEqual(source.getinfo(name).external_attr, output.getinfo(name).external_attr)

    def test_internal_file_tampering_is_rejected_even_when_outer_hash_matches(self):
        archive, digest = self.make_app(tamper=True)
        with self.assertRaisesRegex(ValueError, 'checksum'):
            module.verify_app(archive, '0.1.5', digest, self.root)

    def test_unexpected_credential_file_is_rejected(self):
        archive, digest = self.make_app(extra='NianDeskLink.app/Contents/Resources/private.pem')
        with self.assertRaisesRegex(ValueError, 'profile material'):
            module.verify_app(archive, '0.1.5', digest, self.root)

    def test_parent_traversal_is_rejected(self):
        archive, digest = self.make_app(extra='NianDeskLink.app/../outside.txt')
        with self.assertRaisesRegex(ValueError, 'Unexpected'):
            module.verify_app(archive, '0.1.5', digest, self.root)

    def test_changed_source_is_rejected(self):
        archive, digest = self.make_app()
        self.source.write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'source differs'):
            module.verify_app(archive, '0.1.5', digest, self.root)

    def make_bundle(self, *, bad_dmg=False, extra=None, app_version='0.1.5', multi_device=False, flat=False, tamper_app=False):
        app, _ = self.make_app(version=app_version, identity='com.niannian.nianlink.multidevice-test' if multi_device else 'com.niannian.nianlink')
        official = self.root / 'vendor/deskflow'
        (official / 'metadata').mkdir(parents=True)
        (official / 'original').mkdir()
        licenses = {'LICENSE': b'license', 'LICENSE_EXCEPTION': b'exception'}
        rows = []
        for name, data in licenses.items():
            (official / 'original' / name).write_bytes(data)
            rows.append({'path': name, 'sha256': hashlib.sha256(data).hexdigest()})
        metadata = official / 'metadata/original-manifest.json'
        metadata.write_text(json.dumps({'files': rows}), encoding='utf-8')
        directory = self.root / 'bundle'; directory.mkdir()
        archive = directory / module.OUTFILE
        image = b'synthetic deskflow image'
        with zipfile.ZipFile(archive, 'w') as z:
            if flat:
                if tamper_app:
                    with zipfile.ZipFile(app) as inner:
                        for info in inner.infolist():
                            content = inner.read(info)
                            z.writestr(info, b'tampered' if info.filename.endswith('/NianDeskLink') else content)
                else:
                    module.write_flat_app(z, app)
            else:
                z.writestr('NianDeskLink-0.1.5-osx-arm64.zip', app.read_bytes())
            z.writestr(module.DMG_NAME, b'changed image' if bad_dmg else image)
            for name, data in licenses.items(): z.writestr('Deskflow-' + name + '.txt', data)
            z.writestr('安装说明.txt', (module.README_MULTI if flat else module.README_MULTI_NESTED if multi_device else module.README).encode('utf-8'))
            z.writestr('第三方说明.txt', module.NOTICE.encode('utf-8'))
            if extra: z.writestr(extra, b'unexpected')
        manifest = {'file': module.OUTFILE, 'version': '0.1.5', 'bytes': archive.stat().st_size,
                    'sha256': module.sha(archive), 'channel': 'internal-test', 'includesDeskflow': True, 'notarized': False}
        if multi_device: manifest['mode'] = 'multi-device-test'
        if flat: manifest['appLayout'] = 'flat-v1'
        (directory / 'manifest.json').write_text(json.dumps(manifest), encoding='utf-8')
        return directory, module.sha(metadata), hashlib.sha256(image).hexdigest()

    def test_flat_multidevice_bundle_verifies_and_rejects_tampering(self):
        directory, metadata_sha, image_sha = self.make_bundle(multi_device=True, flat=True)
        with patch.object(module, 'OFFICIAL_MANIFEST_SHA', metadata_sha), patch.object(module, 'DMG_SHA', image_sha):
            self.assertEqual('flat-v1', module.verify_bundle(directory, '0.1.5', self.root, multi_device=True)['appLayout'])

    def test_flat_multidevice_bundle_rejects_changed_app_file(self):
        directory, metadata_sha, image_sha = self.make_bundle(multi_device=True, flat=True, tamper_app=True)
        with patch.object(module, 'OFFICIAL_MANIFEST_SHA', metadata_sha), patch.object(module, 'DMG_SHA', image_sha):
            with self.assertRaisesRegex(ValueError, 'checksum'):
                module.verify_bundle(directory, '0.1.5', self.root, multi_device=True)

    def test_exact_synthetic_bundle_passes(self):
        directory, metadata_sha, image_sha = self.make_bundle()
        with patch.object(module, 'OFFICIAL_MANIFEST_SHA', metadata_sha), patch.object(module, 'DMG_SHA', image_sha):
            self.assertEqual('0.1.5', module.verify_bundle(directory, '0.1.5', self.root)['version'])

    def test_multidevice_bundle_cannot_be_mistaken_for_legacy_bundle(self):
        directory, metadata_sha, image_sha = self.make_bundle(multi_device=True)
        with patch.object(module, 'OFFICIAL_MANIFEST_SHA', metadata_sha), patch.object(module, 'DMG_SHA', image_sha):
            with self.assertRaisesRegex(ValueError, 'mode'):
                module.verify_bundle(directory, '0.1.5', self.root)
            self.assertEqual('multi-device-test', module.verify_bundle(directory, '0.1.5', self.root, multi_device=True)['mode'])

    def test_multidevice_bundle_rejects_legacy_mode(self):
        directory, metadata_sha, image_sha = self.make_bundle()
        with patch.object(module, 'OFFICIAL_MANIFEST_SHA', metadata_sha), patch.object(module, 'DMG_SHA', image_sha):
            with self.assertRaisesRegex(ValueError, 'mode'):
                module.verify_bundle(directory, '0.1.5', self.root, multi_device=True)

    def test_windows_version_must_match_bundle(self):
        directory, metadata_sha, image_sha = self.make_bundle()
        with patch.object(module, 'OFFICIAL_MANIFEST_SHA', metadata_sha), patch.object(module, 'DMG_SHA', image_sha):
            with self.assertRaisesRegex(ValueError, 'version'):
                module.verify_bundle(directory, '0.1.4', self.root)

    def test_nested_app_version_must_match_windows(self):
        directory, metadata_sha, image_sha = self.make_bundle(app_version='0.1.4')
        with patch.object(module, 'OFFICIAL_MANIFEST_SHA', metadata_sha), patch.object(module, 'DMG_SHA', image_sha):
            with self.assertRaisesRegex(ValueError, 'identity'):
                module.verify_bundle(directory, '0.1.5', self.root)

    def test_changed_deskflow_is_rejected_even_if_outer_manifest_matches(self):
        directory, metadata_sha, image_sha = self.make_bundle(bad_dmg=True)
        with patch.object(module, 'OFFICIAL_MANIFEST_SHA', metadata_sha), patch.object(module, 'DMG_SHA', image_sha):
            with self.assertRaisesRegex(ValueError, 'Deskflow image'):
                module.verify_bundle(directory, '0.1.5', self.root)

    def test_unexpected_outer_entry_is_rejected(self):
        directory, metadata_sha, image_sha = self.make_bundle(extra='private.json')
        with patch.object(module, 'OFFICIAL_MANIFEST_SHA', metadata_sha), patch.object(module, 'DMG_SHA', image_sha):
            with self.assertRaisesRegex(ValueError, 'unexpected'):
                module.verify_bundle(directory, '0.1.5', self.root)


if __name__ == '__main__':
    unittest.main()
