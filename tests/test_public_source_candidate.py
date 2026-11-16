"""Synthetic safety tests for the non-publishable source candidate collector."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile
import shutil
import subprocess

SCRIPT = Path(__file__).resolve().parents[1] / 'scripts/public_source_candidate.py'
spec = importlib.util.spec_from_file_location('public_source_candidate', SCRIPT)
candidate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(candidate)


class CandidateTests(unittest.TestCase):
    DOCUMENTED_BUILD_INPUTS = (
        'scripts/build-brand-icons.py',
        'scripts/build-mac-test.ps1',
        'scripts/build-mac-export-test.ps1',
        'scripts/build-mac-export-test.py',
        'scripts/verify-mac-export-test.py',
        'scripts/build-windows-installer.ps1',
        'scripts/stage-windows-dependencies.ps1',
    )

    def test_documented_packaging_and_brand_build_inputs_cannot_be_omitted(self):
        # Independent contract: these are documented entry points or direct
        # dependencies of documented installation/export and asset workflows.
        for name in self.DOCUMENTED_BUILD_INPUTS:
            with self.subTest(missing=name):
                with self.assertRaisesRegex(ValueError, 'required candidate files are missing'):
                    candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

    def test_documented_build_inputs_are_collected_and_missing_input_is_rejected(self):
        required = set(self.DOCUMENTED_BUILD_INPUTS)
        for missing in (None, *self.DOCUMENTED_BUILD_INPUTS):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as d:
                root = Path(d)
                for name in required - {missing}:
                    self.fixture(root, name, b'# synthetic source for inventory test\n')
                # Keep the real SINGLE_FILES allowlist: this also detects a
                # required script accidentally omitted from collection.
                with patch.object(candidate, 'REQUIRED_FILES', required):
                    collected = candidate.collect(root)
                    self.assertEqual(set(collected), required - {missing})
                    if missing is None:
                        candidate.validate_complete(collected)
                    else:
                        with self.assertRaises(ValueError) as error:
                            candidate.validate_complete(collected)
                        self.assertIn(missing, str(error.exception))

    def test_vault_quarantine_research_collects_only_two_required_sources(self):
        required = {
            'tests/experiments/VaultQuarantine/VaultQuarantine.Tests.csproj',
            'tests/experiments/VaultQuarantine/VaultQuarantineTests.cs',
        }
        self.assertTrue(required <= candidate.REQUIRED_FILES)
        for missing in (None, *sorted(required)):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as d:
                root = Path(d)
                for name in required - {missing}:
                    self.fixture(root, name, b'// synthetic research source\n')
                # Unknown siblings and local test data must never be collected.
                for name in (
                    'tests/experiments/VaultQuarantine/unreviewed.cs',
                    'tests/experiments/VaultQuarantine/bin/Debug/private.dll',
                    'tests/experiments/VaultQuarantine/obj/generated.cs',
                    'tests/.work/vault-quarantine/profile.json',
                ):
                    self.fixture(root, name, b'synthetic excluded content')
                with patch.object(candidate, 'REQUIRED_FILES', required):
                    collected = candidate.collect(root)
                    self.assertEqual(set(collected), required - {missing})
                    if missing is None:
                        candidate.validate_complete(collected)
                    else:
                        with self.assertRaises(ValueError) as error:
                            candidate.validate_complete(collected)
                        self.assertIn(missing, str(error.exception))

    def test_hardening_fails_if_required_inventory_is_accidentally_removed(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            self.fixture(root, 'src/Core/code.cs', b'class Synthetic {}')
            out = root / 'output.zip'
            with patch.object(candidate, 'REQUIRED_FILES', {'src/Core/code.cs'}):
                with self.assertRaisesRegex(ValueError, 'manifest'):
                    candidate.write_archive(root, out)
            self.assertFalse(out.exists())

    def test_hardening_accepts_verified_manifest_and_rejects_missing_record(self):
        import hashlib, json
        manifest_name = 'vendor/notices/upstream-license-source-manifest.json'
        notices = ['vendor/notices/qtbase-6.8.3/LICENSES/a.txt',
                   'vendor/notices/openssl-3.5.9/LICENSE.txt']
        values = [b'audited original A', b'audited original B']
        records = [{'destination': n, 'sha256': hashlib.sha256(v).hexdigest()}
                   for n, v in zip(notices, values)]
        for incomplete in (False, True):
            with self.subTest(incomplete=incomplete), tempfile.TemporaryDirectory() as d:
                root = Path(d)
                for name, value in zip(notices, values):
                    self.fixture(root, name, value)
                manifest = json.dumps({'records': records[:1] if incomplete else records}).encode('utf-8')
                self.fixture(root, manifest_name, manifest)
                required = set(notices + [manifest_name])
                with patch.object(candidate, 'SINGLE_FILES', (manifest_name,)), \
                     patch.object(candidate, 'TREES', ('vendor/notices',)), \
                     patch.object(candidate, 'REQUIRED_FILES', required), \
                     patch.object(candidate, 'UPSTREAM_NOTICE_REQUIRED_FILES', required), \
                     patch.object(candidate, 'UPSTREAM_MANIFEST_SHA256', hashlib.sha256(manifest).hexdigest()), \
                     patch.object(candidate, 'validate_dotnet_notice_contents'), \
                     patch.object(candidate, 'validate_qttools_notice_contents'), \
                     patch.object(candidate, 'validate_managed_runtime_notice_contents'):
                    out = root / 'output.zip'
                    if incomplete:
                        with self.assertRaisesRegex(ValueError, 'incomplete'):
                            candidate.write_archive(root, out)
                        self.assertFalse(out.exists())
                    else:
                        candidate.write_archive(root, out)
                        with zipfile.ZipFile(out) as archive:
                            self.assertEqual(archive.read(notices[0]), values[0])
                            self.assertEqual(archive.read(notices[1]), values[1])

    def test_hardening_rejects_nul_markdown_and_modified_marker(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            self.fixture(root, 'docs/report.md', bytes(20))
            with self.assertRaisesRegex(ValueError, 'NUL'):
                candidate.payload(root, 'docs/report.md')
            marker = 'vendor/deskflow/original/settings/Deskflow.conf'
            self.fixture(root, marker, b'not-the-portable-marker')
            with self.assertRaisesRegex(ValueError, 'marker'):
                candidate.payload(root, marker)

    def test_hardening_rejects_altered_license_or_manifest_before_archive(self):
        import hashlib, json
        notice = 'vendor/notices/qtbase-6.8.3/LICENSES/sample.txt'
        manifest_name = 'vendor/notices/upstream-license-source-manifest.json'
        original = b'original licensed text'
        manifest = {'records': [{'destination': notice, 'sha256': hashlib.sha256(original).hexdigest()}]}
        manifest_bytes = json.dumps(manifest, sort_keys=True).encode('utf-8')
        expected_manifest = hashlib.sha256(manifest_bytes).hexdigest()
        for alter in ('license', 'manifest'):
            with self.subTest(alter=alter), tempfile.TemporaryDirectory() as d:
                root = Path(d)
                self.fixture(root, notice, b'modified text' if alter == 'license' else original)
                data = b'{"records":[]}' if alter == 'manifest' else manifest_bytes
                self.fixture(root, manifest_name, data)
                out = root / 'output.zip'
                required = {notice, manifest_name}
                with patch.object(candidate, 'SINGLE_FILES', (manifest_name,)), \
                     patch.object(candidate, 'TREES', ('vendor/notices',)), \
                     patch.object(candidate, 'REQUIRED_FILES', required), \
                     patch.object(candidate, 'UPSTREAM_NOTICE_REQUIRED_FILES', required), \
                     patch.object(candidate, 'UPSTREAM_MANIFEST_SHA256', expected_manifest, create=True), \
                     patch.object(candidate, 'validate_dotnet_notice_contents'), \
                     patch.object(candidate, 'validate_qttools_notice_contents'), \
                     patch.object(candidate, 'validate_managed_runtime_notice_contents'):
                    with self.assertRaises(ValueError):
                        candidate.write_archive(root, out)
                self.assertFalse(out.exists())

    def test_deskflow_coff_provenance_tool_is_collected_and_required(self):
        paths = ['tests/experiments/DeskflowCoffEvidence/compare_coff.py',
                 'tests/experiments/DeskflowCoffEvidence/test_compare_coff.py',
                 'tests/experiments/DeskflowCoffEvidence/README.md']
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for name in paths:
                self.fixture(root, name, b'synthetic provenance material')
            collected = candidate.collect(root)
            for name in paths:
                with self.subTest(name=name):
                    self.assertIn(name, collected)
                    self.assertIn(name, candidate.REQUIRED_FILES)
                    with self.assertRaises(ValueError):
                        candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

    def test_synthetic_two_generation_retention_materials_are_required(self):
        paths = [
            'tests/experiments/TestPackageRetention/RetentionFixture.psm1',
            'tests/experiments/TestPackageRetention/test_retention_fixture.py',
            'tests/experiments/TestPackageRetention/README.md',
            'docs/测试包两代保留方案.md',
        ]
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for name in paths:
                self.fixture(root, name, b'synthetic retention material')
            excluded = 'tests/experiments/TestPackageRetention/unreviewed-output.json'
            self.fixture(root, excluded, b'synthetic output')
            collected = candidate.collect(root)
            for name in paths:
                with self.subTest(name=name):
                    self.assertIn(name, collected)
                    self.assertIn(name, candidate.REQUIRED_FILES)
                    with self.assertRaises(ValueError):
                        candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))
            self.assertNotIn(excluded, collected)
        self.assertIn('scripts/BuildArtifactLock.psm1', candidate.REQUIRED_FILES)

    def test_managed_discovery_builder_runtime_and_tests_are_required(self):
        paths = ['scripts/DiscoveryManagedRetention.psm1', 'scripts/maintain-discovery-retention.ps1',
                 'tests/test_discovery_managed_retention.py']
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for name in paths:
                self.fixture(root, name, b'synthetic managed discovery material')
            collected = candidate.collect(root)
            for name in paths:
                with self.subTest(name=name):
                    self.assertIn(name, collected)
                    self.assertIn(name, candidate.REQUIRED_FILES)
                    with self.assertRaises(ValueError):
                        candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

    def test_mac_fileless_verifier_and_its_tests_are_required(self):
        paths = ['scripts/verify_mac_fileless_candidate.py', 'tests/test_mac_fileless_candidate_verifier.py',
                 'scripts/MacFilelessManagedRetention.psm1', 'scripts/maintain-mac-fileless-retention.ps1',
                 'tests/test_mac_fileless_managed_retention.py']
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for name in paths:
                self.fixture(root, name, b'synthetic Mac verifier material')
            collected = candidate.collect(root)
            for name in paths:
                with self.subTest(name=name):
                    self.assertIn(name, collected)
                    self.assertIn(name, candidate.REQUIRED_FILES)
                    with self.assertRaises(ValueError):
                        candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

    def test_multidevice_candidate_verifier_and_tests_are_required(self):
        paths = ['scripts/verify_multidevice_candidate.py', 'tests/test_multidevice_candidate_verifier.py']
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for name in paths: self.fixture(root, name, b'synthetic source')
            collected = candidate.collect(root)
            for name in paths:
                with self.subTest(name=name):
                    self.assertIn(name, collected)
                    self.assertIn(name, candidate.REQUIRED_FILES)
                    with self.assertRaises(ValueError):
                        candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

    def test_upstream_qt_and_openssl_license_inventory_is_complete(self):
        import hashlib, json
        root = SCRIPT.parent.parent
        manifest_name = 'vendor/notices/upstream-license-source-manifest.json'
        manifest = json.loads((root / manifest_name).read_text(encoding='utf-8'))
        records = manifest['records']
        self.assertEqual(len(records), 132)
        paths = [r['destination'] for r in records]
        self.assertEqual(len(paths), len(set(paths)))
        collected = set(candidate.collect(root))
        for record in records:
            path = record['destination']
            with self.subTest(path=path):
                self.assertIn(path, collected)
                self.assertIn(path, candidate.REQUIRED_FILES)
                self.assertEqual(hashlib.sha256((root / path).read_bytes()).hexdigest(), record['sha256'])
                with self.assertRaises(ValueError):
                    candidate.validate_complete(list(candidate.REQUIRED_FILES - {path}))
        self.assertIn(manifest_name, candidate.REQUIRED_FILES)

    def test_required_deskflow_portable_marker_is_exact_and_collected(self):
        name = 'vendor/deskflow/original/settings/Deskflow.conf'
        project_root = SCRIPT.parent.parent
        self.assertEqual((project_root / name).read_bytes(), b' ')
        self.assertIn(name, candidate.collect(project_root))
        self.assertIn(name, candidate.REQUIRED_FILES)
        with self.assertRaises(ValueError):
            candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

    def test_reviewed_research_critical_files_cannot_be_silently_omitted(self):
        critical = [
            'tests/experiments/PreviewFenceModel/FenceTests.cs',
            'tests/experiments/NativePreviewFence/PreviewFenceState.cpp',
            'tests/experiments/NativePreviewFence/async_queue_harness.cpp',
            'tests/experiments/NativeFenceQt/0006-native-qt-fence-test-only.patch',
            'tests/experiments/NativeFenceQt/0007-partial-frame-barrier-test-only.patch',
            'tests/experiments/MacRecovery/MacOwnerTlsReorderTests.cs',
            'tests/experiments/MacReturnUiRace/ProgramResearch.cs',
            'tests/experiments/MacReturnUiRace/ProgramCrossOwner.cs',
            'tests/experiments/MacReturnUiRace/ProgramDynamic.cs',
            'tests/experiments/MacReturnUiRace/ProgramCloseOwnership.cs',
            'tests/experiments/MacReturnUiRace/ProgramStoppedEntry.cs',
            'tests/experiments/MacReturnUiRace/ProgramRecovery.cs',
            'tests/PeerControlOwner.UiRaceFixture/Program.cs',
            'tests/PeerControlOwner.UiRaceFixture/ProgramRecovery.cs',
            'tests/PeerControlOwner.UiRaceFixture/ProgramCloseOwnership.cs',
            'tests/PeerControlOwner.UiFixture/Program.cs',
            'tests/PeerControlOwner.UiFixture/ResearchResultFile.cs',
            'tests/PeerControlOwner.Tests/ResearchResultFileTests.cs',
        ]
        for name in critical:
            with self.subTest(name=name):
                self.assertIn(name, candidate.REQUIRED_FILES)
                with self.assertRaises(ValueError):
                    candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

    def test_native_and_ui_research_source_materials_are_required(self):
        paths = [
            'tests/experiments/LegacyDuplicateVault/LegacyDuplicateVault.Tests.csproj',
            'tests/experiments/LegacyDuplicateVault/LegacyDuplicateVaultTests.cs',
            'tests/experiments/LegacyDuplicateVault/README.md',
            'tests/experiments/EmergencyReturn/ReadParticipationTests.cs',
            'tests/experiments/FenceRealServer/README.md',
            'tests/experiments/NativeFenceQt/run-tests.ps1',
            'tests/experiments/NativePreviewFence/run-tests.ps1',
            'tests/experiments/NativePreviewFence/async_queue_harness.cpp',
            'tests/PeerControlOwner.UiFixture/PeerControlOwner.UiFixture.csproj',
            'tests/PeerControlOwner.UiRaceFixture/PeerControlOwner.UiRaceFixture.csproj',
            'tests/MacMulticastProbe/probe.py', 'tests/MacMulticastProbe/test_probe.py',
            'tests/NamedPasteboardProbe.swift', 'tests/test_fileless_packaging.py',
            'tests/test_native_research_paths.py', 'tests/test_research_fixture_portability.py',
        ]
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for name in paths:
                self.fixture(root, name, b'synthetic review material')
            collected = candidate.collect(root)
            for name in paths:
                with self.subTest(name=name):
                    self.assertIn(name, collected)
                    self.assertIn(name, candidate.REQUIRED_FILES)
                    with self.assertRaises(ValueError):
                        candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

    def test_reviewed_research_sources_are_required_without_build_outputs(self):
        paths = [
            'tests/experiments/EmergencyReturn/EmergencyReturn.Experimental.Tests.csproj',
            'tests/experiments/MacRecovery/MacRecovery.Experimental.Tests.csproj',
            'tests/experiments/MacReturnUiRace/MacReturnUiRace.csproj',
            'tests/experiments/PreviewFenceModel/PreviewFenceModel.Tests.csproj',
            'tests/experiments/PreviewFenceTls/PreviewFenceTls.Tests.csproj',
            'tests/experiments/PreviewOwnerGate/PreviewOwnerGate.Tests.csproj',
            'tests/experiments/VaultGrantIdentity/VaultGrantIdentity.Tests.csproj',
        ]
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for name in paths:
                self.fixture(root, name, b'synthetic experiment')
            self.fixture(root, 'tests/experiments/MacRecovery/bin/private-output.dll', b'excluded')
            collected = candidate.collect(root)
            self.assertFalse(any('/bin/' in path or '/obj/' in path for path in collected))
            for name in paths:
                with self.subTest(name=name):
                    self.assertIn(name, collected)
                    self.assertIn(name, candidate.REQUIRED_FILES)
                    with self.assertRaises(ValueError):
                        candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

    def test_native_dependency_evidence_and_license_materials_are_required(self):
        paths = ['docs/托管依赖与许可核查.md', 'docs/Deskflow多设备源码核验.md',
                 'docs/OpenSSL源码签名核验.md', 'docs/Mac历史组合包依赖与许可核验.md',
                 'docs/托管构建入口隔离核验.md', 'docs/Windows运行时分发依赖核验.md',
                 'docs/Windows原生构建入口本机核验.md',
                 'docs/Mac历史Deskflow镜像来源核验.md',
                 'vendor/notices/mac-runtime-packages/HarfBuzzSharp.NativeAssets.macOS-8.3.1.3/LICENSE.txt',
                 'vendor/notices/mac-runtime-packages/HarfBuzzSharp.NativeAssets.macOS-8.3.1.3/THIRD-PARTY-NOTICES.txt',
                 'vendor/notices/mac-runtime-packages/SkiaSharp.NativeAssets.macOS-3.119.4/LICENSE.txt',
                 'vendor/notices/mac-runtime-packages/SkiaSharp.NativeAssets.macOS-3.119.4/THIRD-PARTY-NOTICES.txt',
                 'vendor/notices/mac-runtime-packages/Microsoft.NETCore.App.Runtime.osx-arm64-10.0.13/LICENSE.TXT',
                 'vendor/notices/mac-runtime-packages/Microsoft.NETCore.App.Runtime.osx-arm64-10.0.13/THIRD-PARTY-NOTICES.TXT',
                 'vendor/deskflow/metadata/licenses/Deskflow-LICENSE.txt',
                 'vendor/deskflow/metadata/licenses/LicenseRef-OpenSSL-Exception.txt',
                 'vendor/deskflow/metadata/licenses/SOURCES.md',
                 'vendor/notices/avalonia-12.1.4-license.txt',
                 'vendor/notices/skiasharp-3.119.4-managed-license.txt',
                 'vendor/notices/harfbuzzsharp-8.3.1.3-managed-license.txt',
                 'vendor/notices/avalonia-angle-windows-natives-2.1.27548.20260419-license.txt']
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for name in paths:
                self.fixture(root, name, b'synthetic dependency evidence')
            collected = candidate.collect(root)
            for name in paths:
                with self.subTest(name=name):
                    self.assertIn(name, collected)
                    self.assertIn(name, candidate.REQUIRED_FILES)
                    with self.assertRaises(ValueError):
                        candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

    def test_native_mac_build_materials_are_required_for_review(self):
        paths = ['scripts/mac_native_build.py', 'tests/test_mac_native_build.py',
                 'tests/test_mac_native_input_validation.py',
                 'scripts/mac_wifi_bridge_build.py', 'tests/test_mac_wifi_bridge_build.py',
                 'docs/Mac原生组件源码与构建说明.md', 'docs/Mac-WiFi桥隔离构建说明.md']
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for name in paths:
                self.fixture(root, name, b'synthetic review material')
            collected = candidate.collect(root)
            for name in paths:
                with self.subTest(name=name):
                    self.assertIn(name, collected)
                    self.assertIn(name, candidate.REQUIRED_FILES)
                    with self.assertRaises(ValueError):
                        candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

    def test_multidevice_review_guides_are_collected_and_required(self):
        guides = [
            'docs/多设备版使用说明-内部测试.md',
            'docs/免文件配对内测使用说明.md',
            'docs/GitHub上传与项目状态指南.md',
        ]
        # Exercise the collector's Markdown resolution with the same local links
        # used by the guides, not just membership in a static list.
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            self.fixture(root, guides[0],
                         '[old](USER-GUIDE.md) [github](GitHub上传与项目状态指南.md)'.encode('utf-8'))
            self.fixture(root, guides[1],
                         '[multi](多设备版使用说明-内部测试.md)'.encode('utf-8'))
            self.fixture(root, guides[2],
                         '[multi](多设备版使用说明-内部测试.md) [old](USER-GUIDE.md) '
                         '[fileless](免文件配对内测使用说明.md)'.encode('utf-8'))
            self.fixture(root, 'docs/USER-GUIDE.md', b'# older guide')
            collected = candidate.collect(root)
            for name in guides:
                with self.subTest(name=name):
                    self.assertIn(name, collected)
                    self.assertIn(name, candidate.REQUIRED_FILES)
                    with self.assertRaisesRegex(ValueError, 'required candidate files are missing'):
                        candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

        # Real documents and their links must survive the real collection plan.
        project_root = SCRIPT.parent.parent
        collected = candidate.collect(project_root)
        for name in guides:
            self.assertIn(name, collected)
        self.assertIn('(GitHub上传与项目状态指南.md)', (project_root / guides[0]).read_text(encoding='utf-8'))
        for name in guides[1:]:
            self.assertIn('(多设备版使用说明-内部测试.md)', (project_root / name).read_text(encoding='utf-8'))

    def test_shared_owner_source_and_its_guard_tests_are_required(self):
        paths = ['src/NianLink.MultiDevice.Shared/PeerControlOwner.cs',
                 'tests/PeerControlOwner.Tests/PeerControlOwner.Tests.csproj',
                 'tests/PeerControlOwner.Tests/PeerControlOwnerTests.cs',
                 'tests/PeerControlOwner.Tests/GuardedPeerTlsTests.cs']
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for path in paths: self.fixture(root, path, b'isolated source')
            collected = candidate.collect(root)
            for path in paths:
                with self.subTest(path=path):
                    self.assertIn(path, collected)
                    self.assertIn(path, candidate.REQUIRED_FILES)
                    with self.assertRaises(ValueError):
                        candidate.validate_complete(list(candidate.REQUIRED_FILES - {path}))

        project_root = SCRIPT.parent.parent
        for project in ('src/NianLink.DiscoveryPortable/NianLink.DiscoveryPortable.csproj',
                        'src/NianLink.MacDiscoveryCandidate/NianLink.MacDiscoveryCandidate.csproj'):
            reference = '../NianLink.MultiDevice.Shared/PeerControlOwner.cs'
            text = (project_root / project).read_text(encoding='utf-8')
            self.assertIn(f'Compile Include="{reference}"', text)
            self.assertEqual((project_root / project).parent.joinpath(reference).resolve(),
                             (project_root / paths[0]).resolve())

    def test_current_fileless_test_sources_and_build_support_are_required(self):
        paths = [
            'scripts/FilelessBuildMetadata.psm1',
            'tests/FilelessCandidateIntegration.Tests/FilelessCandidateIntegration.Tests.csproj',
            'tests/FilelessPairing.UiFixture/FilelessPairing.UiFixture.csproj',
            'tests/FilelessPackagedIntegration.Tests/FilelessPackagedIntegration.Tests.csproj',
            'tests/PairingDiagnostics.Tests/PairingDiagnostics.Tests.csproj',
            'tests/Mac.MultiDeviceVisual/Mac.MultiDeviceVisual.csproj',
            'tests/FilelessCandidateGate.Tests/FilelessCandidateGate.Tests.csproj',
            'tests/Mac.SettingsVisual/Mac.SettingsVisual.csproj',
            'tests/MacManagedDatagramProbe/MacManagedDatagramProbe.csproj',
            'tests/MacManagedDatagramProbe.Tests/MacManagedDatagramProbe.Tests.csproj',
            'tests/MacResponderCandidate.Tests/MacResponderCandidate.Tests.csproj',
            'tests/MacSyntheticMtlsProbe/MacSyntheticMtlsProbe.csproj',
            'tests/MacSyntheticMtlsProbe.Tests/MacSyntheticMtlsProbe.Tests.csproj',
            'tests/SingleThemeProbe/SingleThemeProbe.csproj',
            'tests/PortableBrandProbe/PortableBrandProbe.csproj',
            'tests/test_multidevice_theme_isolation.py',
            'scripts/build-mac-blue-delivery.py',
            'scripts/mac-blue-test-bootstrap.py',
            'tests/test_mac_blue_package.py',
            'src/NianLink.MultiDevice/mini-windows.css',
            'src/NianLink.MultiDevice/MainWindow.BlueDialogs.cs',
            'src/NianLink.MultiDevice/DetachedPreviewWindow.ClearBlue.cs',
            'tests/FilelessCandidateIntegration.Tests/HighLevelPairingTests.cs',
            'tests/FilelessPairing.UiFixture/Program.cs',
            'tests/FilelessPackagedIntegration.Tests/PackagedBinaryIdentityTests.cs',
            'tests/PairingDiagnostics.Tests/PairingDiagnosticsTests.cs',
            'tests/Mac.MultiDeviceVisual/Program.cs',
            'tests/SingleThemeProbe/Program.cs',
            'tests/PortableBrandProbe/Program.cs',
        ]
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for path in paths:
                self.fixture(root, path, b'isolated source')
            self.fixture(root, 'tests/FilelessPairing.UiFixture/bin/private.dll', b'ignored')
            collected = candidate.collect(root)
            for path in paths:
                with self.subTest(path=path):
                    self.assertIn(path, collected)
                    self.assertIn(path, candidate.REQUIRED_FILES)
                    with self.assertRaises(ValueError):
                        candidate.validate_complete(list(candidate.REQUIRED_FILES - {path}))
            self.assertNotIn('tests/FilelessPairing.UiFixture/bin/private.dll', collected)

    def test_mac_discovery_packaging_sources_are_collected(self):
        paths = ['src/NianLink.MacDiscoveryCandidate/NianLink.MacDiscoveryCandidate.csproj',
                 'src/DesktopToolbox.Mac/Tests/MacDiscoveryTestStartupTests.cs',
                 'scripts/stage-mac-test.py', 'tests/test_mac_discovery_package.py']
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for path in paths:
                self.fixture(root, path, b'isolated source')
            collected = candidate.collect(root)
            for path in paths:
                self.assertIn(path, collected)
                self.assertIn(path, candidate.REQUIRED_FILES)

    def test_mac_wifi_bridge_sources_are_collected_and_scanned_as_text(self):
        paths = [
            'src/NianLink.MacDiscoveryNative/WifiEvidenceBridge.h',
            'src/NianLink.MacDiscoveryNative/WifiEvidenceBridge.m',
            'src/NianLink.MacDiscoveryNative/WifiEvidenceCore.h',
            'src/NianLink.MacDiscoveryNative/WifiEvidenceCore.c',
            'tests/MacWifiBridge/WifiEvidenceCoreTests.c',
            'tests/MacWifiBridge/WifiEvidenceBridgeSmoke.c',
            'tests/MacWifiBridge/WifiEvidenceBridgeNameTests.m',
            'tests/test_mac_wifi_bridge_project.py',
            'docs/Mac-WiFi原生证据桥接计划.md',
        ]
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for path in paths:
                self.fixture(root, path, b'isolated source')
            collected = candidate.collect(root)
            for path in paths:
                self.assertIn(path, collected)
                self.assertIn(path, candidate.REQUIRED_FILES)
                self.assertEqual(candidate.payload(root, path), b'isolated source')
            self.fixture(root, paths[1], b'-----BEGIN ' + b'PRIVATE KEY-----')
            with self.assertRaises(ValueError):
                candidate.payload(root, paths[1])

    def test_discovery_portable_policy_sources_are_collected(self):
        paths = ['scripts/DiscoveryPortablePaths.psm1', 'scripts/discovery_portable_policy.py',
                 'scripts/WindowsTestNotices.psm1', 'tests/test_windows_test_notices.py',
                 'scripts/WindowsTestKeyboardPayload.psm1', 'tests/test_windows_test_keyboard_payload.py',
                 'tests/test_discovery_portable_paths.py', 'tests/test_discovery_portable_policy.py']
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for path in paths:
                self.fixture(root, path, b'isolated source')
            collected = candidate.collect(root)
            for path in paths:
                self.assertIn(path, collected)
                self.assertIn(path, candidate.REQUIRED_FILES)

    def test_mac_probe_and_boundary_tests_are_collected_without_build_outputs(self):
        paths = [
            'tests/MacDiscoveryBoundary.Tests/MacDiscoveryBoundary.Tests.csproj',
            'tests/MacDiscoveryBoundary.Tests/SyntheticMappingTests.cs',
            'tests/MacDiscoveryBoundary.Tests/MacSystemEvidenceTests.cs',
            'tests/MacDiscoveryBoundary.Tests/MacProbeTests.cs',
            'tests/MacSystemProbe/MacSystemProbe.csproj',
            'tests/MacSystemProbe/Probe.cs',
            'tests/MacSystemProbe/Program.cs',
            'tests/test_mac_discovery_project.py',
        ]
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for path in paths:
                self.fixture(root, path, b'isolated source')
            self.fixture(root, 'tests/MacSystemProbe/bin/private.dll', b'ignored')
            collected = candidate.collect(root)
            for path in paths:
                self.assertIn(path, collected)
                self.assertIn(path, candidate.REQUIRED_FILES)
            self.assertNotIn('tests/MacSystemProbe/bin/private.dll', collected)

    def test_discovery_network_mapping_sources_are_required(self):
        for path in ('src/DesktopToolbox.DeskLink/DiscoveryNetworkBinding.cs',
                     'src/NianLink.MacDiscoveryBoundary/NianLink.MacDiscoveryBoundary.csproj',
                     'src/NianLink.MacDiscoveryBoundary/SyntheticMacMapping.cs',
                     'src/NianLink.MacDiscoveryBoundary/MacSystemEvidence.cs',
                     'tests/MacDiscoveryBoundary.Tests/MacDiscoveryBoundary.Tests.csproj',
                     'tests/MacDiscoveryBoundary.Tests/SyntheticMappingTests.cs',
                     'tests/MacDiscoveryBoundary.Tests/MacSystemEvidenceTests.cs',
                     'tests/test_mac_discovery_project.py',
                     'src/DesktopToolbox.DeskLink/DiscoveryProtocolSessions.cs',
                     'src/DesktopToolbox.DeskLink/DiscoveryReviewLease.cs',
                     'src/NianLink.CandidateDiscovery/NianLink.CandidateDiscovery.csproj',
                     'src/NianLink.DiscoveryPortable/NianLink.DiscoveryPortable.csproj',
                     'src/NianLink.MultiDevice/DiscoveryVolumePolicy.cs',
                     'tests/StandaloneSettings.Tests/DiscoveryVolumePolicyTests.cs',
                     'src/NianLink.CandidateDiscovery/CandidateDiscoverySessions.cs',
                     'src/DesktopToolbox.DeskLink/WindowsHardwareInterface.cs',
                     'src/NianLink.CandidateDiscovery/CandidateDiscoveryRun.cs',
                     'src/NianLink.CandidateDiscovery/WindowsDiscoveryDatagramSocket.cs',
                     'src/NianLink.CandidateDiscovery/CandidateSyntheticRules.cs',
                     'tests/DeskLink.Tests/DiscoveryNetworkBindingTests.cs',
                     'tests/DeskLink.Tests/DiscoveryProtocolSessionTests.cs',
                     'tests/MultiDevice.Windows.Tests/WindowsHardwareInterfaceTests.cs',
                     'tests/ConditionalCommitIsolation.Tests/HardwareInterfaceIsolationTests.cs',
                     'tests/ConditionalCommitIsolation.Tests/CandidateDiscoveryRunTests.cs',
                     'tests/ConditionalCommitIsolation.Tests/CandidateSyntheticRulesTests.cs',
                     'tests/ConditionalCommitIsolation.Tests/CandidateProbeExecutionPolicyTests.cs',
                     'tests/ConditionalCommitIsolation.Tests/DiscoveryReviewLeaseTests.cs',
                     'tests/CandidateSocketProbe/CandidateSocketProbe.csproj',
                     'tests/CandidateSocketProbe/Program.cs',
                     'tests/CandidateSocketProbe/ProbeExecutionPolicy.cs',
                     'src/NianLink.MultiDevice/PairingReviewGuard.cs',
                     'scripts/DiscoveryPortablePaths.psm1', 'scripts/discovery_portable_policy.py',
                     'tests/test_discovery_portable_paths.py', 'tests/test_discovery_portable_policy.py',
                     'src/NianLink.MultiDevice/DiscoveryUiLifetime.cs',
                     'src/NianLink.MultiDevice/MainWindow.Discovery.cs',
                     'tests/MultiDevice.Windows.Tests/DiscoveryUiLifetimeTests.cs',
                     'tests/MultiPeerTrust.Tests/PairingReviewLifecycleTests.cs',
                     'tests/MultiDevice.Windows.Tests/PairingReviewGuardTests.cs',
                     'tests/native-hardware-interface/layout.cpp',
                     'tests/test_discovery_candidate_project.py',
                     'tests/ConditionalCommitIsolation.Tests/DiscoverySocketConfigurationTests.cs'):
            with self.subTest(path=path):
                self.assertIn(path, candidate.REQUIRED_FILES)
                with self.assertRaises(ValueError):
                    candidate.validate_complete(list(candidate.REQUIRED_FILES - {path}))

    def test_private_udp_probe_sources_are_required_without_build_outputs(self):
        paths = ['tests/PrivateUdpProbe/' + name for name in
                 ('PrivateUdpProbe.csproj', 'Program.cs', 'ProbeSocket.cs', 'ProbeEnvironment.cs', 'ProbeRules.cs', 'ProbeParentIdentity.cs', 'ProbeChildCleanup.cs')]
        paths.append('tests/ConditionalCommitIsolation.Tests/PrivateUdpProbeRulesTests.cs')
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            for path in paths:
                self.fixture(root, path, b'isolated source')
            self.fixture(root, 'tests/PrivateUdpProbe/bin/private.dll', b'ignored')
            self.fixture(root, 'tests/PrivateUdpProbe/obj/private.dll', b'ignored')
            collected = candidate.collect(root)
            for path in paths:
                self.assertIn(path, collected)
                self.assertIn(path, candidate.REQUIRED_FILES)
            self.assertNotIn('tests/PrivateUdpProbe/bin/private.dll', collected)
            self.assertNotIn('tests/PrivateUdpProbe/obj/private.dll', collected)

    def fixture(self, root, name, data):
        p = root / name
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)
        return p

    def test_only_explicit_roots_and_excluded_components(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            self.fixture(root, 'src/Core/code.cs', b'class Core {}')
            self.fixture(root, 'src/Core/bin/copy.cs', b'ignored')
            self.fixture(root, 'src/Core/.work/private.cs', b'ignored')
            self.fixture(root, 'test-profiles/identity.json', b'ignored')
            self.fixture(root, 'assets/brand/nian-link-logo-original.png', b'ignored')
            self.fixture(root, 'assets/brand/nian-link-logo-approved.png', b'png fixture')
            self.assertEqual(candidate.collect(root), ['assets/brand/nian-link-logo-approved.png', 'src/Core/code.cs'])

    def test_binary_not_on_list_is_rejected_not_silently_bundled(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            self.fixture(root, 'src/Core/unknown.dll', b'binary')
            with self.assertRaisesRegex(ValueError, 'unexpected binary'):
                candidate.collect(root)

    def test_credentials_are_rejected_without_echoing_content(self):
        for payload in (b'-----BEGIN ' + b'PRIVATE KEY-----', b'ghp_' + b'a' * 36,
                        b'192.168.' + b'31.255'):
            with self.subTest(payload=payload), tempfile.TemporaryDirectory() as d:
                root = Path(d)
                self.fixture(root, 'src/Core/code.cs', payload)
                with self.assertRaises(ValueError) as err:
                    candidate.collect(root)
                self.assertNotIn(payload.decode(), str(err.exception))

    def test_utf16_private_key_marker_is_rejected_without_echoing_content(self):
        marker = '-----BEGIN ' + 'PRIVATE KEY-----'
        for encoding in ('utf-16-le', 'utf-16-be'):
            for bom in (False, True):
                with self.subTest(encoding=encoding, bom=bom), tempfile.TemporaryDirectory() as d:
                    root = Path(d)
                    prefix = (b'\xff\xfe' if encoding == 'utf-16-le' else b'\xfe\xff') if bom else b''
                    self.fixture(root, 'src/Core/unsafe.cs', prefix + marker.encode(encoding))
                    with self.assertRaises(ValueError) as err:
                        candidate.collect(root)
                    self.assertNotIn(marker, str(err.exception))

    def test_source_bytes_are_never_silently_modified(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            p = self.fixture(root, 'tests/LanAddress.Tests/LanAddressSelectorTests.cs', b'192.168.84.2')
            data = candidate.payload(root, p.relative_to(root).as_posix())
            self.assertEqual(data, p.read_bytes())

    def test_utf16_text_is_scanned_without_rewriting_source_bytes(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            data = '普通测试文本'.encode('utf-16')
            path = 'src/Core/utf16.txt'
            self.fixture(root, path, data)
            self.assertEqual(candidate.payload(root, path), data)

    def test_link_is_rejected(self):
        with tempfile.TemporaryDirectory() as d, tempfile.TemporaryDirectory() as external:
            root = Path(d)
            src = root / 'src'; src.mkdir()
            try:
                (src / 'external').symlink_to(Path(external), target_is_directory=True)
            except (OSError, NotImplementedError):
                self.skipTest('symlink creation not available')
            with self.assertRaisesRegex(ValueError, 'link'):
                candidate.collect(root)

    def test_real_linked_file_is_rejected(self):
        with tempfile.TemporaryDirectory() as d, tempfile.TemporaryDirectory() as outside:
            root = Path(d)
            linked = root / 'src' / 'Core' / 'code.cs'
            linked.parent.mkdir(parents=True)
            target = Path(outside) / 'external.cs'
            target.write_bytes(b'synthetic external file')
            try:
                linked.symlink_to(target)
            except (OSError, NotImplementedError):
                self.skipTest('file symlink creation not available')
            with self.assertRaisesRegex(ValueError, 'link'):
                candidate.collect(root)

    def test_reparse_detection_rejects_a_candidate_file(self):
        for alias in (False, True):
            with self.subTest(alias=alias), tempfile.TemporaryDirectory() as d:
                base = Path(d)
                (base / 'alias').mkdir()
                (base / 'target').mkdir()
                root = base / 'alias' / '..' / 'target' if alias else base / 'target'
                target = self.fixture(root, 'src/Core/code.cs', b'ordinary fixture').resolve(strict=True)
                original = candidate._link
                with patch.object(candidate, '_link', side_effect=lambda p: p == target or original(p)):
                    with self.assertRaisesRegex(ValueError, 'link'):
                        candidate.collect(root)

    def test_link_check_recognizes_reparse_attribute_on_regular_mode(self):
        import stat
        from types import SimpleNamespace
        path = Path('synthetic-file.cs')
        with patch.object(Path, 'lstat', return_value=SimpleNamespace(st_mode=stat.S_IFREG, st_file_attributes=0x400)):
            self.assertTrue(candidate._link(path))
        with patch.object(Path, 'lstat', return_value=SimpleNamespace(st_mode=stat.S_IFREG, st_file_attributes=0)):
            self.assertFalse(candidate._link(path))

    def test_missing_local_markdown_link_is_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            self.fixture(root, 'README.md', b'[guide](docs/missing.md)')
            with self.assertRaisesRegex(ValueError, 'missing local documentation'):
                candidate.collect(root)

    def test_existing_local_markdown_link_is_allowed(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            self.fixture(root, 'README.md', b'[guide](docs/BUILDING.md#test)')
            self.fixture(root, 'docs/BUILDING.md', b'# Build')
            self.assertIn('docs/BUILDING.md', candidate.collect(root))

    def test_gitignore_is_included_in_candidate(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            self.fixture(root, '.gitignore', b'**/bin/\n')
            self.assertIn('.gitignore', candidate.collect(root))

    def test_explicit_ci_workflow_is_included(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            self.fixture(root, '.github/workflows/ci.yml', b'name: ci\n')
            self.assertIn('.github/workflows/ci.yml', candidate.collect(root))

    def test_windows_multipeer_trust_tests_are_included_without_build_outputs(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            self.fixture(root, 'tests/MultiPeerTrust.Tests/MultiPeerTrust.Tests.csproj', b'<Project />')
            self.fixture(root, 'tests/MultiPeerTrust.Tests/bin/private.dll', b'ignored')
            self.assertIn('tests/MultiPeerTrust.Tests/MultiPeerTrust.Tests.csproj', candidate.collect(root))
            self.fixture(root, 'tests/MultiDevice.Windows.Tests/MultiDevice.Windows.Tests.csproj', b'<Project />')
            self.fixture(root, 'tests/MultiDevice.Windows.Tests/obj/private.dll', b'ignored')
            self.assertIn('tests/MultiDevice.Windows.Tests/MultiDevice.Windows.Tests.csproj', candidate.collect(root))
            self.assertNotIn('tests/MultiDevice.Windows.Tests/obj/private.dll', candidate.collect(root))
            self.fixture(root, 'tests/native-peer-bindings/harness.cpp', b'int main() {}')
            self.fixture(root, 'tests/native-peer-bindings/CMakeLists.txt', b'cmake_minimum_required(VERSION 3.24)')
            self.assertIn('tests/native-peer-bindings/harness.cpp', candidate.collect(root))
            self.assertIn('tests/native-peer-bindings/CMakeLists.txt', candidate.collect(root))

    def test_conditional_commit_test_source_is_included_without_generated_files(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            project = 'tests/ConditionalCommitIsolation.Tests/ConditionalCommitIsolation.Tests.csproj'
            implementation = 'tests/ConditionalCommitIsolation.Tests/IsolatedCommitExperiment.cs'
            regression = 'tests/ConditionalCommitIsolation.Tests/IsolatedCommitTests.cs'
            live_session = 'tests/ConditionalCommitIsolation.Tests/PairingTlsConsentSession.TestOnly.cs'
            live_regression = 'tests/ConditionalCommitIsolation.Tests/LiveTlsConsentCapabilityTests.cs'
            live_vault = 'tests/ConditionalCommitIsolation.Tests/LiveTlsVaultExperiment.cs'
            additional = tuple('tests/ConditionalCommitIsolation.Tests/' + value for value in (
                'VaultVariants/MultiDeviceCredentialVault.cs', 'VaultVariants/MultiDeviceCredentialVault.PendingGrant.cs',
                'VaultVariants/MultiDeviceCredentialVault.PendingFreeze.cs', 'PeerVariants/PeerInvitationStore.Pending.cs',
                'PeerInvitationStore.LiveStage.TestOnly.cs', 'PeerInvitationStore.V3.TestOnly.cs', 'PeerStageV3Tests.cs',
                'PeerPrepareStageTests.cs', 'LivePrepareProtocol.TestOnly.cs', 'IsolatedTestPathGuard.cs',
                'LoopbackTlsProxy.cs', 'PrepareTransportFailureTests.cs', 'PrepareChildProcessTests.cs',
                'OfflineRecoverySnapshots.TestOnly.cs', 'OfflineRecoverySnapshotTests.cs',
                'IsolatedRecoveryQuery.TestOnly.cs', 'LiveRecoveryQueryTests.cs',
                'LivePrepareV3RecoveryTests.cs',
                'PrepareV3ChildProcessTests.cs', 'VaultVariantV3CompatibilityTests.cs', 'HistoricalSecurePairingPeerTests.cs',
            ))
            self.fixture(root, project, b'<Project />')
            self.fixture(root, implementation, b'// isolated experiment\n')
            self.fixture(root, regression, b'// isolated tests\n')
            self.fixture(root, live_session, b'// isolated session\n')
            self.fixture(root, live_regression, b'// isolated tls tests\n')
            self.fixture(root, live_vault, b'// isolated vault test code\n')
            for path in additional:
                self.fixture(root, path, b'// isolated test source\n')
            self.fixture(root, 'tests/ConditionalCommitIsolation.Tests/bin/private.dll', b'ignored')
            self.fixture(root, 'tests/ConditionalCommitIsolation.Tests/obj/private.dll', b'ignored')
            self.fixture(root, 'tests/ConditionalCommitIsolation.Tests/test-profiles/private.pem', b'ignored')
            self.fixture(root, 'tests/ConditionalCommitIsolation.Tests/.work/private.pem', b'ignored')
            collected = candidate.collect(root)
            for path in (project, implementation, regression, live_session, live_regression, live_vault, *additional):
                self.assertIn(path, collected)
                self.assertIn(path, candidate.REQUIRED_FILES)
            self.assertNotIn('tests/ConditionalCommitIsolation.Tests/bin/private.dll', collected)
            self.assertNotIn('tests/ConditionalCommitIsolation.Tests/obj/private.dll', collected)
            self.assertNotIn('tests/ConditionalCommitIsolation.Tests/test-profiles/private.pem', collected)
            self.assertNotIn('tests/ConditionalCommitIsolation.Tests/.work/private.pem', collected)

    def test_isolated_test_source_rejects_embedded_secret_patterns(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            secret = b'-' * 5 + b'BEGIN PRIVATE KEY' + b'-' * 5
            self.fixture(root, 'tests/ConditionalCommitIsolation.Tests/PeerVariants/unsafe.cs', secret)
            with self.assertRaisesRegex(ValueError, 'sensitive pattern'):
                candidate.collect(root)

    def test_real_candidate_requires_release_basics(self):
        with self.assertRaisesRegex(ValueError, 'required candidate files'):
            candidate.validate_complete(['README.md'])
        candidate.validate_complete(list(candidate.REQUIRED_FILES))

    def test_multidevice_build_entry_and_lock_are_required(self):
        self.assertIn('scripts/build-multidevice-windows-test.ps1', candidate.REQUIRED_FILES)
        self.assertIn('scripts/BuildArtifactLock.psm1', candidate.REQUIRED_FILES)
        self.assertIn('scripts/build-multidevice-installer-test.ps1', candidate.REQUIRED_FILES)
        self.assertIn('installer/NianLinkMultiDeviceTest.iss', candidate.REQUIRED_FILES)
        self.assertIn('installer/languages/ChineseSimplified.LICENSE.txt', candidate.REQUIRED_FILES)
        self.assertIn('tests/test_mac_package_identity.py', candidate.REQUIRED_FILES)

    def test_beta_builder_inputs_are_included_in_the_source_candidate(self):
        for name in ('installer/NianLinkMultiDeviceBetaTest.iss', 'docs/Windows多设备Beta使用说明.md',
                     'src/NianLink.MultiDevice/BetaPathPolicy.cs', 'tests/StandaloneSettings.Tests/BetaPathPolicyTests.cs'):
            with self.subTest(path=name):
                self.assertIn(name, candidate.REQUIRED_FILES)

    def test_multidevice_patch_chain_is_required(self):
        for name in ('0001-current-user-native-control.patch',
                     '0002-preview-input-isolation-experimental.patch',
                     '0003-multidevice-conditional-switch-experimental.patch',
                     '0004-multidevice-identity-binding-experimental.patch'):
            path = 'vendor/deskflow/metadata/' + name
            with self.subTest(patch=name):
                self.assertIn(path, candidate.REQUIRED_FILES)
                with self.assertRaisesRegex(ValueError, 'required candidate files'):
                    candidate.validate_complete(list(candidate.REQUIRED_FILES - {path}))

    def test_candidate_scripts_do_not_reference_another_private_project(self):
        paths = candidate.collect(SCRIPT.parent.parent)
        for name in paths:
            if name.startswith('scripts/'):
                self.assertNotIn(b'D:/Projects/' + b'pc-toolbox', candidate.payload(SCRIPT.parent.parent, name), name)

    def test_gitignore_excludes_private_data_and_keeps_source_assets(self):
        git = shutil.which('git')
        if not git:
            self.skipTest('git is not installed')
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            subprocess.run([git, '-C', str(root), 'init', '-q'], check=True)
            (root / '.gitignore').write_bytes((SCRIPT.parent.parent / '.gitignore').read_bytes())
            excluded = ['src/Core/bin/cache.dll', 'src/Core/obj/cache.json',
                        'test-profiles/device.json', '.artifacts/build/out.txt',
                        'tests/.work/user-note.json', 'profiles/pairing.json',
                        'local/desk-switch.json', 'local/connection.json',
                        'local/年年互联配对.json',
                        'local/device.pem', 'local/device.pfx', 'local/pair-token',
                        'logs/event.log', 'releases/test-setup.exe']
            retained = ['LICENSE', 'src/Core/example.cs', 'assets/brand/NianLink.ico',
                        'src/DesktopToolbox.Mac/Assets/NianDeskLink.icns',
                        'vendor/deskflow/metadata/0001-current-user-native-control.patch']
            process = subprocess.run([git, '-C', str(root), 'check-ignore', '--no-index', '--stdin', '-z'],
                                     input=('\0'.join(excluded + retained) + '\0').encode('utf-8'),
                                     capture_output=True, check=False)
            self.assertEqual(process.returncode, 0)
            self.assertEqual(set(process.stdout.decode('utf-8').split('\0')) - {''}, set(excluded))

    def test_archive_rechecks_required_files_before_creating_output(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / 'project'; root.mkdir()
            self.fixture(root, 'src/Core/code.cs', b'class Core {}')
            output = Path(d) / 'candidate.zip'
            with patch.object(candidate, 'REQUIRED_FILES', {'src/Core/code.cs', 'src/Core/missing.cs'}):
                with self.assertRaisesRegex(ValueError, 'required candidate files'):
                    candidate.write_archive(root, output)
            self.assertFalse(output.exists())

    def test_archive_is_deterministic_and_never_overwrites(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d) / 'project'; root.mkdir()
            self.fixture(root, 'src/Core/code.cs', b'class Core {}')
            output = Path(d) / 'candidate.zip'
            with patch.object(candidate, 'REQUIRED_FILES', {'src/Core/code.cs'}), \
                 patch.object(candidate, 'validate_upstream_notice_contents'), \
                 patch.object(candidate, 'validate_dotnet_notice_contents'), \
                     patch.object(candidate, 'validate_qttools_notice_contents'), \
                     patch.object(candidate, 'validate_managed_runtime_notice_contents'):
                candidate.write_archive(root, output)
                with zipfile.ZipFile(output) as archive:
                    self.assertIn('src/Core/code.cs', archive.namelist())
                    self.assertIn('CANDIDATE-NOT-FOR-PUBLICATION.txt', archive.namelist())
                old = output.read_bytes()
                second = Path(d) / 'candidate-second.zip'
                candidate.write_archive(root, second)
                self.assertEqual(old, second.read_bytes())
                with self.assertRaises(FileExistsError):
                    candidate.write_archive(root, output)
                self.assertEqual(old, output.read_bytes())


if __name__ == '__main__':
    unittest.main()
