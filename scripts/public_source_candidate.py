"""Collect a *review-only* source candidate; this does not publish or prove compliance.

Only explicit project-owned source roots and a few reviewed assets are eligible.
The default command prints a plan without writing. Even after successful scanning,
independent human review, a license decision, corresponding third-party source and
a clean-room rebuild are required before anything may be published.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import posixpath
from pathlib import Path, PurePosixPath
import re
import stat
import urllib.parse
import zipfile

ROOT = Path(__file__).resolve().parents[1]
TREES = (
    'src', 'installer', 'THIRD-PARTY-NOTICES', '.github/workflows',
    'vendor/deskflow/metadata', 'vendor/notices',
    'tests/TestSupport',
    'tests/DeskLink.Tests', 'tests/PeerControlOwner.Tests', 'tests/LanAddress.Tests', 'tests/PairingExport.Tests',
    'tests/PortOwnership.Tests', 'tests/Shutdown.Tests', 'tests/StandaloneSettings.Tests', 'tests/MultiPeerTrust.Tests',
    'tests/ConditionalCommitIsolation.Tests', 'tests/PrivateUdpProbe', 'tests/CandidateSocketProbe',
    'tests/MacDiscoveryBoundary.Tests', 'tests/MacSystemProbe', 'tests/MacWifiProbe', 'tests/MacWifiBridge', 'tests/MacManagedWifiProbe', 'tests/MacManagedMappingProbe',
    'tests/ui', 'tests/native-peer-bindings', 'tests/native-multipeer', 'tests/native-hardware-interface', 'tests/MultiDevice.UiFixture',
    'tests/NianLink.Smoke', 'tests/NianLink.Smoke.Tests', 'tests/MultiDevice.Windows.Tests',
    'tests/FilelessCandidateIntegration.Tests', 'tests/FilelessPairing.UiFixture',
    'tests/FilelessPackagedIntegration.Tests', 'tests/PairingDiagnostics.Tests',
    'tests/FilelessCandidateGate.Tests', 'tests/Mac.MultiDeviceVisual', 'tests/Mac.SettingsVisual',
    'tests/MacManagedDatagramProbe', 'tests/MacManagedDatagramProbe.Tests',
    'tests/MacResponderCandidate.Tests', 'tests/MacSyntheticMtlsProbe', 'tests/MacSyntheticMtlsProbe.Tests',
    'tests/SingleThemeProbe', 'tests/PortableBrandProbe',
    'tests/experiments/EmergencyReturn', 'tests/experiments/MacRecovery', 'tests/experiments/LegacyDuplicateVault',
    'tests/experiments/MacReturnUiRace', 'tests/experiments/PreviewFenceModel',
    'tests/experiments/PreviewFenceTls', 'tests/experiments/PreviewOwnerGate',
    'tests/experiments/VaultGrantIdentity',
    'tests/experiments/FenceRealServer', 'tests/experiments/NativeFenceQt',
    'tests/experiments/QtSourceProvenance',
    'tests/experiments/DeskflowCoffEvidence', 'tests/experiments/DeskflowPeProvenance',
    'tests/experiments/NativePreviewFence', 'tests/PeerControlOwner.UiFixture',
    'tests/PeerControlOwner.UiRaceFixture', 'tests/MacMulticastProbe',
)
SINGLE_FILES = (
    'tests/experiments/VaultQuarantine/VaultQuarantine.Tests.csproj',
    'tests/experiments/VaultQuarantine/VaultQuarantineTests.cs',
    'tests/experiments/TestPackageRetention/RetentionFixture.psm1',
    'tests/experiments/TestPackageRetention/test_retention_fixture.py',
    'tests/experiments/TestPackageRetention/README.md',
    'docs/测试包两代保留方案.md',
    'docs/多设备测试版使用与已知问题-发布准备.md',
    'docs/Windows测试版独立入口方案.md',
    'THIRD-PARTY-REVIEW.md', 'docs/使用说明.md', 'docs/构建与测试.md',
    'docs/许可与发布状态.md', 'docs/项目进度.md',
    'docs/进度技能树/index.html', 'docs/进度技能树/progress.js',
    'docs/进度技能树/style.css', 'docs/进度技能树/tree.js',
    'docs/screenshots/mac-devices.png',
    'docs/screenshots/mac-discovery.png',
    'docs/screenshots/mac-files.png',
    'docs/screenshots/mac-keyboard.png',
    'docs/screenshots/mac-screen.png',
    'docs/screenshots/windows-controlled.png',
    'docs/screenshots/windows-devices.png',
    'docs/screenshots/windows-screen.png',
    'docs/screenshots/README.md',
    'tests/test_github_screenshots.py',
    'docs/功能详解.md',
    'docs/Windows原生源码本机全链证据.md', 'docs/windows-native-local-evidence.json',
    'docs/OpenSSL运行路径与provider核验.md', 'docs/openssl-runtime-evidence.json',
    'scripts/qt_config_summary.py', 'tests/test_qt_config_summary.py', 'docs/Qt构建配置摘要说明.md',
    'scripts/github_native_clean_build.py', 'scripts/print_native_clean_evidence.py', 'tests/test_github_native_clean_build.py', 'docs/Windows原生源码干净构建.md',
    'docs/Conditional隔离测试源码缺口.md', 'docs/conditional-pdb-evidence.json',
    'scripts/build_deskflow_core.py', 'tests/test_native_core_build.py', 'docs/Deskflow四补丁隔离构建.md',
    'scripts/verify_qt_official_archive.py',
    'scripts/qt_pe_unsigned_hash.py',
    'tests/test_verify_qt_official_archive.py',
    'tests/test_qt_pe_unsigned_hash.py',
    'docs/Qt历史文件来源核验.md', 'docs/Mac历史Deskflow镜像来源核验.md',
    'docs/OpenSSL源码签名核验.md', 'docs/Mac历史组合包依赖与许可核验.md', 'docs/托管构建入口隔离核验.md', 'docs/Windows运行时分发依赖核验.md', 'docs/Windows原生构建入口本机核验.md',
    'tests/test_managed_runtime_notices.py',
    'scripts/fetch_upstream_sources.py', 'tests/test_fetch_upstream_sources.py', 'docs/上游源码获取说明.md',
    'scripts/SdkBaseline.psm1', 'tests/test_sdk_baseline.py', 'tests/test_source_portability_inclusion.py',
    'docs/二进制载荷依赖与许可审查.md', 'tests/test_dotnet_notice_integrity.py', 'tests/test_qttools_notice_integrity.py',
    'vendor/deskflow/original/settings/Deskflow.conf',
    '.gitattributes', 'tests/test_source_bytes_policy.py',
    '.gitignore', 'README.md', 'README.en.md', 'CONTRIBUTING.md', 'LICENSE', 'LICENSE-NOTICE.md',
    'THIRD-PARTY-NOTICES.md', 'docs/BUILDING.md', 'docs/USER-GUIDE.md',
    'docs/多设备版使用说明-内部测试.md', 'docs/免文件配对内测使用说明.md', 'docs/Windows多设备Beta使用说明.md', 'docs/GitHub上传与项目状态指南.md',
    'docs/开源准备计划.md', 'docs/开源许可核对.md', 'docs/Deskflow对应源码.md',
    'docs/Deskflow手动构建.md',
    'docs/多设备互联计划.md', 'docs/Mac局域网只读探针计划.md', 'docs/Mac发现网络绑定候选计划.md', 'docs/Mac发现候选界面计划.md',
    'docs/Mac-WiFi原生证据桥接计划.md', 'docs/Mac发现候选独立打包计划.md',
    'assets/brand/nian-link-logo-approved.png', 'assets/brand/NianLink.ico',
    'assets/brand/NianLinkBrand.png', 'assets/brand/README.md',
    'scripts/build-brand-icons.py', 'scripts/build-managed.py', 'tests/test_build_managed.py', 'tests/test_discovery_candidate_project.py',
    'scripts/mac_native_build.py', 'tests/test_mac_native_build.py', 'tests/test_mac_native_input_validation.py',
    'tests/NamedPasteboardProbe.swift', 'tests/test_fileless_packaging.py',
    'tests/test_native_research_paths.py', 'tests/test_research_fixture_portability.py',
    'docs/Mac原生组件源码与构建说明.md',
    'scripts/mac_wifi_bridge_build.py', 'tests/test_mac_wifi_bridge_build.py', 'docs/Mac-WiFi桥隔离构建说明.md',
    'docs/托管依赖与许可核查.md', 'docs/Deskflow多设备源码核验.md',
    'scripts/prepare_deskflow_source.py', 'tests/test_deskflow_source_bundle.py',
    'tests/test_ci_workflow.py',
    'scripts/build-mac-test.ps1', 'scripts/build-multidevice-windows-test.ps1', 'scripts/build-multidevice-installer-test.ps1',
    'scripts/DiscoveryPortablePaths.psm1', 'scripts/discovery_portable_policy.py',
    'scripts/WindowsTestNotices.psm1', 'tests/test_windows_test_notices.py',
    'scripts/WindowsTestKeyboardPayload.psm1', 'tests/test_windows_test_keyboard_payload.py',
    'tests/test_discovery_portable_paths.py', 'tests/test_discovery_portable_policy.py',
    'scripts/BuildArtifactLock.psm1', 'scripts/FilelessBuildMetadata.psm1', 'scripts/FilelessArtifactCache.psm1', 'tests/test_fileless_artifact_cache.py', 'scripts/DiscoveryManagedRetention.psm1', 'scripts/maintain-discovery-retention.ps1', 'tests/test_discovery_managed_retention.py', 'scripts/verify_mac_fileless_candidate.py', 'tests/test_mac_fileless_candidate_verifier.py', 'scripts/public_source_candidate.py',
    'scripts/MacFilelessManagedRetention.psm1', 'scripts/maintain-mac-fileless-retention.ps1', 'tests/test_mac_fileless_managed_retention.py',
    'scripts/verify_multidevice_candidate.py', 'tests/test_multidevice_candidate_verifier.py',
    'tests/test_multidevice_theme_isolation.py',
    'tests/test_build_artifact_lock.py', 'tests/test_public_source_candidate.py',
    'scripts/build-mac-export-test.ps1', 'scripts/build-mac-export-test.py',
    'scripts/build-mac-blue-delivery.py', 'scripts/mac-blue-test-bootstrap.py', 'tests/test_mac_blue_package.py',
    'tests/test_mac_export_test.py', 'tests/test_mac_package_identity.py', 'tests/test_mac_discovery_project.py',
    'scripts/stage-mac-test.py', 'tests/test_mac_discovery_package.py',
    'tests/test_mac_wifi_probe_project.py', 'tests/test_mac_wifi_bridge_project.py', 'tests/test_mac_bridge_pin_build.py',
    'scripts/build-windows-installer.ps1', 'scripts/stage-windows-dependencies.ps1',
    'scripts/verify-mac-export-test.py',
)
SKIP_DIRS = {'bin', 'obj', '__pycache__', 'test-profiles', 'TestResults',
             '.work', '.artifacts', '.git', 'releases', 'profiles'}
TEXT_EXTENSIONS = {'.cs', '.csproj', '.swift', '.txt', '.md', '.css', '.js', '.mjs',
                   '.axaml', '.py', '.ps1', '.psm1', '.sh', '.json', '.props', '.html',
                   '.manifest', '.xaml', '.svg', '.plist', '.iss', '.isl', '.patch', '.rtf', '.yml', '.yaml', '.cpp', '.c', '.h', '.m'}
BINARY_ALLOW = {
    'assets/brand/nian-link-logo-approved.png', 'assets/brand/NianLink.ico',
    'assets/brand/NianLinkBrand.png', 'src/DesktopToolbox.Mac/Assets/NianDeskLink.icns',
    'src/DesktopToolbox.Mac/Assets/NianLinkBrand.png', 'src/DesktopToolbox.Mac/Assets/desk-link.png',
    'src/DesktopToolbox.Mac/Tests/UiFixture/fixture-desktop.jpg',
}
SCREENSHOT_HASHES = {
    'docs/screenshots/mac-devices.png': '8e044ed160bc7691639a71e2f9f1c579fc6be2d0735dd00029a5f2878ad75f7e',
    'docs/screenshots/mac-discovery.png': '2257c0f059ca4c155c3b32d887861aca5f2fab06b1cd7edb7ea5a0e6b2e7e26b',
    'docs/screenshots/mac-files.png': 'c382f0b044fda0964c7f72c075589621c0c8d65ad7b44b8052d4748bd2b0c9fc',
    'docs/screenshots/mac-keyboard.png': '412fc10cad9c3db1e457462d25503499c88945b3a80cf37ba1a981fd2fe44b79',
    'docs/screenshots/mac-screen.png': '0ec7a510d21dec6f65d89981d86909d8bfade3a857e4a5c5e3edbe0fa35b6e48',
    'docs/screenshots/windows-controlled.png': 'a09d1d4c9c695505a4083bcbbef43dd0b672017d7a1a4edfef750ae2b8efd124',
    'docs/screenshots/windows-devices.png': '2ace6782b75f6ef13d1020362810b9e814a9d29512a572a05a7916a497e6c595',
    'docs/screenshots/windows-screen.png': '6fb1229289c277305fced3b98bc64b92baee7f339b87b15b4f5102bbdabd6f3a',
}
BINARY_ALLOW |= set(SCREENSHOT_HASHES)
TEXT_FILES = {'LICENSE', '.gitignore', '.gitattributes', 'vendor/deskflow/original/settings/Deskflow.conf'}
TEXT_FILES |= {'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/src/3rdparty/litehtml/LICENSE', 'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/src/3rdparty/litehtml/src/gumbo/LICENSE'}
REQUIRED_FILES = {
    'docs/多设备测试版使用与已知问题-发布准备.md',
    'docs/Windows测试版独立入口方案.md',
    'THIRD-PARTY-REVIEW.md', 'docs/使用说明.md', 'docs/构建与测试.md',
    'docs/许可与发布状态.md', 'docs/项目进度.md',
    'docs/进度技能树/index.html', 'docs/进度技能树/progress.js',
    'docs/进度技能树/style.css', 'docs/进度技能树/tree.js',
    'tests/experiments/VaultQuarantine/VaultQuarantine.Tests.csproj',
    'tests/experiments/VaultQuarantine/VaultQuarantineTests.cs',
    # Documented package, dependency and brand regeneration entry points.
    'scripts/build-brand-icons.py',
    'scripts/build-mac-test.ps1',
    'scripts/build-mac-export-test.ps1',
    'scripts/build-mac-export-test.py',
    'scripts/verify-mac-export-test.py',
    'scripts/build-windows-installer.ps1',
    'scripts/stage-windows-dependencies.ps1',
    'docs/screenshots/mac-devices.png',
    'docs/screenshots/mac-discovery.png',
    'docs/screenshots/mac-files.png',
    'docs/screenshots/mac-keyboard.png',
    'docs/screenshots/mac-screen.png',
    'docs/screenshots/windows-controlled.png',
    'docs/screenshots/windows-devices.png',
    'docs/screenshots/windows-screen.png',
    'docs/screenshots/README.md',
    'tests/test_github_screenshots.py',
    'docs/功能详解.md',
    'docs/Windows原生源码本机全链证据.md', 'docs/windows-native-local-evidence.json',
    'docs/OpenSSL运行路径与provider核验.md', 'docs/openssl-runtime-evidence.json',
    'scripts/qt_config_summary.py', 'tests/test_qt_config_summary.py', 'docs/Qt构建配置摘要说明.md',
    'scripts/github_native_clean_build.py', 'scripts/print_native_clean_evidence.py', 'tests/test_github_native_clean_build.py', 'docs/Windows原生源码干净构建.md',
    '.github/workflows/native-clean-manual.yml',
    'docs/Conditional隔离测试源码缺口.md', 'docs/conditional-pdb-evidence.json',
    'scripts/build_deskflow_core.py', 'tests/test_native_core_build.py', 'docs/Deskflow四补丁隔离构建.md',
    'scripts/verify_qt_official_archive.py',
    'scripts/qt_pe_unsigned_hash.py',
    'tests/test_verify_qt_official_archive.py',
    'tests/test_qt_pe_unsigned_hash.py',
    'docs/Qt历史文件来源核验.md', 'docs/Mac历史Deskflow镜像来源核验.md',
    'docs/OpenSSL源码签名核验.md', 'docs/Mac历史组合包依赖与许可核验.md', 'docs/托管构建入口隔离核验.md', 'docs/Windows运行时分发依赖核验.md', 'docs/Windows原生构建入口本机核验.md',
    'tests/experiments/QtSourceProvenance/source-tree-comparison.json',
    'tests/experiments/DeskflowPeProvenance/compare_pe.py',
    'tests/experiments/DeskflowPeProvenance/test_compare_pe.py',
    'tests/experiments/DeskflowPeProvenance/comparison-evidence.json',
    'tests/experiments/DeskflowPeProvenance/link-input-manifest.json',
    'tests/experiments/DeskflowPeProvenance/README.md',
    'tests/test_managed_runtime_notices.py',
    'scripts/fetch_upstream_sources.py', 'tests/test_fetch_upstream_sources.py', 'docs/上游源码获取说明.md',
    'tests/TestSupport/SyntheticWorkDirectory.cs', 'src/DesktopToolbox.Mac/Tests/SyntheticWorkDirectoryTests.cs',
    'scripts/SdkBaseline.psm1', 'tests/test_sdk_baseline.py', 'tests/test_source_portability_inclusion.py',
    '.gitattributes', 'tests/test_source_bytes_policy.py',
    'tests/experiments/DeskflowCoffEvidence/compare_coff.py',
    'tests/experiments/DeskflowCoffEvidence/test_compare_coff.py',
    'tests/experiments/DeskflowCoffEvidence/README.md',
    'vendor/deskflow/original/settings/Deskflow.conf',
    'vendor/notices/mac-runtime-packages/HarfBuzzSharp.NativeAssets.macOS-8.3.1.3/LICENSE.txt',
    'vendor/notices/mac-runtime-packages/HarfBuzzSharp.NativeAssets.macOS-8.3.1.3/THIRD-PARTY-NOTICES.txt',
    'vendor/notices/mac-runtime-packages/SkiaSharp.NativeAssets.macOS-3.119.4/LICENSE.txt',
    'vendor/notices/mac-runtime-packages/SkiaSharp.NativeAssets.macOS-3.119.4/THIRD-PARTY-NOTICES.txt',
    'vendor/notices/mac-runtime-packages/Microsoft.NETCore.App.Runtime.osx-arm64-10.0.13/LICENSE.TXT',
    'vendor/notices/mac-runtime-packages/Microsoft.NETCore.App.Runtime.osx-arm64-10.0.13/THIRD-PARTY-NOTICES.TXT',
    'vendor/deskflow/metadata/licenses/Deskflow-LICENSE.txt',
    'vendor/deskflow/metadata/licenses/LicenseRef-OpenSSL-Exception.txt',
    'vendor/deskflow/metadata/licenses/SOURCES.md',
    'tests/PeerControlOwner.Tests/ResearchResultFileTests.cs',
    'tests/experiments/FenceRealServer/README.md',
    'tests/experiments/NativeFenceQt/run-tests.ps1',
    'tests/experiments/NativePreviewFence/run-tests.ps1',
    'tests/PeerControlOwner.UiFixture/PeerControlOwner.UiFixture.csproj',
    'tests/PeerControlOwner.UiRaceFixture/PeerControlOwner.UiRaceFixture.csproj',
    'tests/MacMulticastProbe/probe.py', 'tests/MacMulticastProbe/test_probe.py',
    'tests/experiments/EmergencyReturn/EmergencyReturn.Experimental.Tests.csproj',
    'tests/experiments/MacRecovery/MacRecovery.Experimental.Tests.csproj',
    'tests/experiments/MacReturnUiRace/MacReturnUiRace.csproj',
    'tests/experiments/PreviewFenceModel/PreviewFenceModel.Tests.csproj',
    'tests/experiments/PreviewFenceTls/PreviewFenceTls.Tests.csproj',
    'tests/experiments/PreviewOwnerGate/PreviewOwnerGate.Tests.csproj',
    'tests/experiments/VaultGrantIdentity/VaultGrantIdentity.Tests.csproj',
    'vendor/notices/avalonia-12.1.4-license.txt',
    'vendor/notices/skiasharp-3.119.4-managed-license.txt',
    'vendor/notices/harfbuzzsharp-8.3.1.3-managed-license.txt',
    'vendor/notices/avalonia-angle-windows-natives-2.1.27548.20260419-license.txt',
    'src/NianLink.MultiDevice.Shared/PeerControlOwner.cs',
    'tests/PeerControlOwner.Tests/PeerControlOwner.Tests.csproj',
    'tests/PeerControlOwner.Tests/PeerControlOwnerTests.cs',
    'tests/PeerControlOwner.Tests/GuardedPeerTlsTests.cs',
    '.gitignore', 'README.md', 'README.en.md', 'CONTRIBUTING.md', 'LICENSE',
    'LICENSE-NOTICE.md', 'THIRD-PARTY-NOTICES.md', 'docs/BUILDING.md',
    'docs/USER-GUIDE.md', 'docs/多设备版使用说明-内部测试.md', 'docs/免文件配对内测使用说明.md',
    'docs/GitHub上传与项目状态指南.md', 'docs/开源准备计划.md', 'docs/开源许可核对.md',
    'docs/Deskflow对应源码.md', 'docs/Deskflow手动构建.md',
    'scripts/build-managed.py', 'tests/test_build_managed.py',
    'scripts/mac_native_build.py', 'tests/test_mac_native_build.py', 'tests/test_mac_native_input_validation.py',
    'tests/NamedPasteboardProbe.swift', 'tests/test_fileless_packaging.py',
    'tests/test_native_research_paths.py', 'tests/test_research_fixture_portability.py',
    'docs/Mac原生组件源码与构建说明.md',
    'scripts/mac_wifi_bridge_build.py', 'tests/test_mac_wifi_bridge_build.py', 'docs/Mac-WiFi桥隔离构建说明.md',
    'docs/托管依赖与许可核查.md', 'docs/Deskflow多设备源码核验.md',
    'src/DesktopToolbox.DeskLink/DiscoveryNetworkBinding.cs',
    'src/DesktopToolbox.DeskLink/DiscoveryProtocolSessions.cs',
    'src/DesktopToolbox.DeskLink/DiscoveryReviewLease.cs',
    'src/NianLink.MacDiscoveryBoundary/NianLink.MacDiscoveryBoundary.csproj',
    'src/NianLink.MacDiscoveryBoundary/SyntheticMacMapping.cs',
    'src/NianLink.MacDiscoveryBoundary/MacSystemEvidence.cs',
    'src/NianLink.MacDiscoveryNative/WifiEvidenceCore.h',
    'src/NianLink.MacDiscoveryNative/WifiEvidenceCore.c',
    'src/NianLink.MacDiscoveryNative/WifiEvidenceBridge.h',
    'src/NianLink.MacDiscoveryNative/WifiEvidenceBridge.m',
    'tests/MacWifiBridge/WifiEvidenceCoreTests.c', 'tests/MacWifiBridge/WifiEvidenceBridgeSmoke.c',
    'tests/MacWifiBridge/WifiEvidenceBridgeNameTests.m', 'tests/test_mac_wifi_bridge_project.py',
    'docs/Mac-WiFi原生证据桥接计划.md',
    'src/NianLink.MacCandidateDiscovery/NianLink.MacCandidateDiscovery.csproj',
    'src/NianLink.MacDiscoveryCandidate/NianLink.MacDiscoveryCandidate.csproj',
    'src/DesktopToolbox.Mac/Tests/MacDiscoveryTestStartupTests.cs',
    'scripts/stage-mac-test.py', 'tests/test_mac_discovery_package.py',
    'src/NianLink.MacCandidateDiscovery/MacCandidateNetworkBinding.cs',
    'src/NianLink.MacCandidateDiscovery/MacWifiEvidenceReader.cs',
    'src/NianLink.MacCandidateDiscovery/MacReadOnlyMappingProbe.cs',
    'src/NianLink.MacCandidateDiscovery/MacBindingRevalidator.cs',
    'tests/MacDiscoveryBoundary.Tests/MacDiscoveryBoundary.Tests.csproj',
    'tests/MacDiscoveryBoundary.Tests/MacCandidateBindingTests.cs',
    'tests/MacDiscoveryBoundary.Tests/MacWifiEvidenceReaderTests.cs',
    'tests/MacDiscoveryBoundary.Tests/MacWifiEvidenceTestAssemblyInfo.cs',
    'tests/MacDiscoveryBoundary.Tests/MacManagedWifiProbeTests.cs',
    'tests/MacDiscoveryBoundary.Tests/MacReadOnlyMappingProbeTests.cs',
    'tests/MacDiscoveryBoundary.Tests/MacBindingRevalidatorTests.cs',
    'tests/MacDiscoveryBoundary.Tests/MacManagedMappingProbeTests.cs',
    'tests/MacManagedWifiProbe/MacManagedWifiProbe.csproj',
    'tests/MacManagedWifiProbe/Program.cs',
    'tests/MacManagedWifiProbe/ProbeDigest.cs',
    'tests/MacManagedMappingProbe/MacManagedMappingProbe.csproj',
    'tests/MacManagedMappingProbe/Program.cs',
    'docs/Mac发现候选界面计划.md',
    'src/NianLink.MacDiscoveryCandidate/DiscoveryUiController.cs',
    'src/NianLink.MacDiscoveryCandidate/LiveMacDiscoveryBackend.cs',
    'src/DesktopToolbox.Mac/Tests/MacDiscoveryUiControllerTests.cs',
    'tests/test_mac_bridge_pin_build.py',
    'tests/MacDiscoveryBoundary.Tests/SyntheticMappingTests.cs',
    'tests/MacDiscoveryBoundary.Tests/MacSystemEvidenceTests.cs',
    'tests/MacDiscoveryBoundary.Tests/MacProbeTests.cs',
    'tests/MacSystemProbe/MacSystemProbe.csproj',
    'tests/MacSystemProbe/Probe.cs', 'tests/MacSystemProbe/Program.cs',
    'tests/MacWifiProbe/WifiProbeMain.swift', 'tests/MacWifiProbe/WifiProbeRules.swift',
    'tests/MacWifiProbe/WifiProbeRulesTests.swift', 'tests/test_mac_wifi_probe_project.py',
    'docs/Mac局域网只读探针计划.md', 'docs/Mac发现网络绑定候选计划.md',
    'tests/test_mac_discovery_project.py',
    'src/NianLink.CandidateDiscovery/NianLink.CandidateDiscovery.csproj',
    'src/NianLink.DiscoveryPortable/NianLink.DiscoveryPortable.csproj',
    'src/NianLink.MultiDevice/DiscoveryVolumePolicy.cs', 'tests/StandaloneSettings.Tests/DiscoveryVolumePolicyTests.cs',
    'src/NianLink.CandidateDiscovery/CandidateDiscoverySessions.cs',
    'src/DesktopToolbox.DeskLink/WindowsHardwareInterface.cs',
    'src/NianLink.CandidateDiscovery/CandidateDiscoveryRun.cs',
    'src/NianLink.CandidateDiscovery/WindowsDiscoveryDatagramSocket.cs',
    'src/NianLink.CandidateDiscovery/CandidateSyntheticRules.cs',
    'tests/DeskLink.Tests/DiscoveryNetworkBindingTests.cs',
    'tests/DeskLink.Tests/DiscoveryProtocolSessionTests.cs', 'tests/test_discovery_candidate_project.py',
    'tests/MultiDevice.Windows.Tests/WindowsHardwareInterfaceTests.cs', 'tests/native-hardware-interface/layout.cpp',
    'tests/ConditionalCommitIsolation.Tests/HardwareInterfaceIsolationTests.cs',
    'tests/ConditionalCommitIsolation.Tests/CandidateDiscoveryRunTests.cs',
    'tests/ConditionalCommitIsolation.Tests/CandidateSyntheticRulesTests.cs',
    'tests/ConditionalCommitIsolation.Tests/CandidateProbeExecutionPolicyTests.cs',
    'tests/ConditionalCommitIsolation.Tests/DiscoveryReviewLeaseTests.cs',
    'tests/CandidateSocketProbe/CandidateSocketProbe.csproj',
    'tests/CandidateSocketProbe/Program.cs', 'tests/CandidateSocketProbe/ProbeExecutionPolicy.cs',
    'src/NianLink.MultiDevice/PairingReviewGuard.cs',
    'src/NianLink.MultiDevice/DiscoveryUiLifetime.cs', 'src/NianLink.MultiDevice/MainWindow.Discovery.cs',
    'tests/MultiDevice.Windows.Tests/DiscoveryUiLifetimeTests.cs',
    'tests/MultiPeerTrust.Tests/PairingReviewLifecycleTests.cs', 'tests/MultiDevice.Windows.Tests/PairingReviewGuardTests.cs',
    'tests/ConditionalCommitIsolation.Tests/ConditionalCommitIsolation.Tests.csproj',
    'tests/ConditionalCommitIsolation.Tests/Historical/SecurePairingPeer.cs',
    'tests/ConditionalCommitIsolation.Tests/VaultVariantV3CompatibilityTests.cs',
    'tests/ConditionalCommitIsolation.Tests/HistoricalSecurePairingPeerTests.cs',
    'tests/ConditionalCommitIsolation.Tests/IsolatedCommitExperiment.cs',
    'tests/ConditionalCommitIsolation.Tests/IsolatedCommitTests.cs',
    'tests/ConditionalCommitIsolation.Tests/PairingTlsConsentSession.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/LiveTlsConsentCapabilityTests.cs',
    'tests/ConditionalCommitIsolation.Tests/LiveTlsVaultExperiment.cs',
    'tests/ConditionalCommitIsolation.Tests/VaultVariants/MultiDeviceCredentialVault.cs',
    'tests/ConditionalCommitIsolation.Tests/VaultVariants/MultiDeviceCredentialVault.PendingGrant.cs',
    'tests/ConditionalCommitIsolation.Tests/VaultVariants/MultiDeviceCredentialVault.PendingFreeze.cs',
    'tests/ConditionalCommitIsolation.Tests/PeerVariants/PeerInvitationStore.Pending.cs',
    'tests/ConditionalCommitIsolation.Tests/PeerVariants/PeerInvitationStore.cs',
    'tests/ConditionalCommitIsolation.Tests/PeerInvitationStore.LiveStage.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/PeerInvitationStore.V3.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/PeerStageV3Tests.cs',
    'tests/ConditionalCommitIsolation.Tests/PeerPrepareStageTests.cs',
    'tests/ConditionalCommitIsolation.Tests/LivePrepareProtocol.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/IsolatedTestPathGuard.cs',
    'tests/ConditionalCommitIsolation.Tests/LoopbackTlsProxy.cs',
    'tests/ConditionalCommitIsolation.Tests/PrepareTransportFailureTests.cs',
    'tests/ConditionalCommitIsolation.Tests/PrepareChildProcessTests.cs',
    'tests/ConditionalCommitIsolation.Tests/OfflineRecoverySnapshots.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/OfflineRecoverySnapshotTests.cs',
    'tests/ConditionalCommitIsolation.Tests/IsolatedRecoveryQuery.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/LiveRecoveryQueryTests.cs',
    'tests/ConditionalCommitIsolation.Tests/LivePrepareV3RecoveryTests.cs',
    'tests/ConditionalCommitIsolation.Tests/PrepareV3ChildProcessTests.cs',
    'tests/ConditionalCommitIsolation.Tests/PrepareMarkerV2Tests.cs',
    'tests/ConditionalCommitIsolation.Tests/DiscoverySocketConfigurationTests.cs',
    'tests/ConditionalCommitIsolation.Tests/LiveReadyCommit.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/ReadyCommitTests.cs',
    'tests/ConditionalCommitIsolation.Tests/ReadyWireControls.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/ReadyWireTests.cs',
    'tests/ConditionalCommitIsolation.Tests/CommittedTicket.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/CommittedTicketTests.cs',
    'tests/ConditionalCommitIsolation.Tests/PeerPromotion.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/PeerPromotionTests.cs',
    'tests/ConditionalCommitIsolation.Tests/PeerPromotionFailureTests.cs',
    'tests/ConditionalCommitIsolation.Tests/AppliedObservation.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/AppliedObservationTests.cs',
    'tests/ConditionalCommitIsolation.Tests/AppliedObservationFailureTests.cs',
    'tests/ConditionalCommitIsolation.Tests/PromotedRecoverySnapshots.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/PromotedRecoveryTests.cs',
    'tests/ConditionalCommitIsolation.Tests/PromotedRecoveryFailureTests.cs',
    'tests/ConditionalCommitIsolation.Tests/RecoveryActivationSession.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/RecoveryActivationStorage.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/RecoveryActivationExperiment.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/RecoveryActivationFaults.TestOnly.cs',
    'tests/ConditionalCommitIsolation.Tests/RecoveryActivationTests.cs',
    'tests/ConditionalCommitIsolation.Tests/RecoveryActivationFailureTests.cs',
    'tests/ConditionalCommitIsolation.Tests/RecoveryActivationWireTests.cs',
    'tests/ConditionalCommitIsolation.Tests/PrivateUdpProbeRulesTests.cs',
    'tests/PrivateUdpProbe/PrivateUdpProbe.csproj', 'tests/PrivateUdpProbe/Program.cs',
    'tests/PrivateUdpProbe/ProbeSocket.cs', 'tests/PrivateUdpProbe/ProbeEnvironment.cs', 'tests/PrivateUdpProbe/ProbeRules.cs',
    'tests/PrivateUdpProbe/ProbeParentIdentity.cs', 'tests/PrivateUdpProbe/ProbeChildCleanup.cs',
    'scripts/build-multidevice-windows-test.ps1', 'scripts/BuildArtifactLock.psm1',
    'scripts/build-multidevice-installer-test.ps1', 'installer/NianLinkMultiDeviceTest.iss',
    'installer/NianLinkMultiDeviceBetaTest.iss', 'docs/Windows多设备Beta使用说明.md',
    'src/NianLink.MultiDevice/BetaPathPolicy.cs', 'tests/StandaloneSettings.Tests/BetaPathPolicyTests.cs',
    'scripts/verify_multidevice_candidate.py', 'tests/test_multidevice_candidate_verifier.py',
    'scripts/DiscoveryPortablePaths.psm1', 'scripts/discovery_portable_policy.py',
    'scripts/WindowsTestNotices.psm1', 'tests/test_windows_test_notices.py',
    'scripts/WindowsTestKeyboardPayload.psm1', 'tests/test_windows_test_keyboard_payload.py',
    'tests/test_discovery_portable_paths.py', 'tests/test_discovery_portable_policy.py',
    'installer/languages/ChineseSimplified.LICENSE.txt',
    'scripts/public_source_candidate.py', 'tests/test_public_source_candidate.py', 'tests/test_build_artifact_lock.py',
    'scripts/FilelessBuildMetadata.psm1', 'scripts/FilelessArtifactCache.psm1', 'tests/test_fileless_artifact_cache.py', 'scripts/DiscoveryManagedRetention.psm1', 'scripts/maintain-discovery-retention.ps1', 'tests/test_discovery_managed_retention.py', 'scripts/verify_mac_fileless_candidate.py', 'tests/test_mac_fileless_candidate_verifier.py',
    'scripts/MacFilelessManagedRetention.psm1', 'scripts/maintain-mac-fileless-retention.ps1', 'tests/test_mac_fileless_managed_retention.py',
    'scripts/build-mac-blue-delivery.py', 'scripts/mac-blue-test-bootstrap.py', 'tests/test_mac_blue_package.py',
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
    'tests/test_mac_export_test.py', 'tests/test_mac_package_identity.py',
    'scripts/prepare_deskflow_source.py', 'tests/test_deskflow_source_bundle.py',
    '.github/workflows/managed-ci.yml', 'tests/test_ci_workflow.py',
    'src/NianLink.Windows/NianLink.Windows.csproj',
    'src/DesktopToolbox.Mac/DesktopToolbox.Mac.csproj',
    'assets/brand/nian-link-logo-approved.png', 'assets/brand/NianLink.ico',
    'src/DesktopToolbox.Mac/Assets/NianDeskLink.icns',
    'src/DesktopToolbox.Collaboration.Ui/assets/lucide.min.js',
    'THIRD-PARTY-NOTICES/lucide-1.8.0.txt',
    'vendor/deskflow/metadata/0001-current-user-native-control.patch',
    'vendor/deskflow/metadata/0002-preview-input-isolation-experimental.patch',
    'vendor/deskflow/metadata/0003-multidevice-conditional-switch-experimental.patch',
    'vendor/deskflow/metadata/0004-multidevice-identity-binding-experimental.patch',
}
# Fixed audit inventory: missing any reviewed research source must fail completeness.
RESEARCH_REQUIRED_FILES = {
    'tests/experiments/TestPackageRetention/RetentionFixture.psm1',
    'tests/experiments/TestPackageRetention/test_retention_fixture.py',
    'tests/experiments/TestPackageRetention/README.md',
    'docs/测试包两代保留方案.md',
    'tests/experiments/LegacyDuplicateVault/LegacyDuplicateVault.Tests.csproj',
    'tests/experiments/LegacyDuplicateVault/LegacyDuplicateVaultTests.cs',
    'tests/experiments/LegacyDuplicateVault/README.md',
    'tests/MacMulticastProbe/probe.py',
    'tests/MacMulticastProbe/test_probe.py',
    'tests/NamedPasteboardProbe.swift',
    'tests/PeerControlOwner.UiFixture/PeerControlOwner.UiFixture.csproj',
    'tests/PeerControlOwner.UiFixture/Program.cs',
    'tests/PeerControlOwner.UiFixture/ResearchResultFile.cs',
    'tests/PeerControlOwner.UiRaceFixture/PeerControlOwner.UiRaceFixture.csproj',
    'tests/PeerControlOwner.UiRaceFixture/Program.cs',
    'tests/PeerControlOwner.UiRaceFixture/ProgramRecovery.cs',
    'tests/PeerControlOwner.UiRaceFixture/ProgramCloseOwnership.cs',
    'tests/PeerControlOwner.UiRaceFixture/README.md',
    'tests/experiments/EmergencyReturn/EmergencyManagerCandidateTests.cs',
    'tests/experiments/EmergencyReturn/EmergencyManagerDefaultBuildTests.cs',
    'tests/experiments/EmergencyReturn/EmergencyManagerResearch.cs',
    'tests/experiments/EmergencyReturn/EmergencyReturn.Experimental.Tests.csproj',
    'tests/experiments/EmergencyReturn/EmergencyReturnRedTests.cs',
    'tests/experiments/EmergencyReturn/ReadParticipationTests.cs',
    'tests/experiments/EmergencyReturn/EmergencyStateResearchTests.cs',
    'tests/experiments/EmergencyReturn/README.md',
    'tests/experiments/EmergencyReturn/ResearchDevices.cs',
    'tests/experiments/FenceRealServer/README.md',
    'tests/experiments/MacRecovery/MacCollaborationRecoveryTests.cs',
    'tests/experiments/MacRecovery/MacMultiPreviewControl.cs',
    'tests/experiments/MacRecovery/MacOwnerTlsReorderTests.cs',
    'tests/experiments/MacRecovery/MacPreviewSessionTlsReorderTests.cs',
    'tests/experiments/MacRecovery/MacPreviewTlsReorderTests.cs',
    'tests/experiments/MacRecovery/MacRecovery.Experimental.Tests.csproj',
    'tests/experiments/MacRecovery/README.md',
    'tests/experiments/MacRecovery/UI归还入口候选测试.txt',
    'tests/experiments/MacRecovery/reference/MacCollaborationLeaseRaceTests.cs.txt',
    'tests/experiments/MacRecovery/reference/MacMultiLivePreviewSource.cs.txt',
    'tests/experiments/MacRecovery/reference/MacMultiLivePreviewSourceTests.cs.txt',
    'tests/experiments/MacRecovery/reference/MacMultiPreviewControlTests.cs.txt',
    'tests/experiments/MacReturnUiRace/MacReturnUiRace.csproj',
    'tests/experiments/MacReturnUiRace/Program.cs',
    'tests/experiments/MacReturnUiRace/ProgramResearch.cs',
    'tests/experiments/MacReturnUiRace/ProgramCrossOwner.cs',
    'tests/experiments/MacReturnUiRace/ProgramDynamic.cs',
    'tests/experiments/MacReturnUiRace/ProgramCloseOwnership.cs',
    'tests/experiments/MacReturnUiRace/ProgramStoppedEntry.cs',
    'tests/experiments/MacReturnUiRace/ProgramRecovery.cs',
    'tests/experiments/MacReturnUiRace/README.md',
    'tests/experiments/MacReturnUiRace/StopFixture.cs',
    'tests/experiments/MacReturnUiRace/start-dispose-evidence.md',
    'tests/experiments/NativeFenceQt/0006-native-qt-fence-test-only.patch',
    'tests/experiments/NativeFenceQt/0007-partial-frame-barrier-test-only.patch',
    'tests/experiments/NativeFenceQt/CMakeLists.txt',
    'tests/experiments/NativeFenceQt/README.md',
    'tests/experiments/NativeFenceQt/harness.cpp',
    'tests/experiments/NativeFenceQt/run-tests.ps1',
    'tests/experiments/NativePreviewFence/0005-preview-fence-state-experimental.patch',
    'tests/experiments/NativePreviewFence/CMakeLists.txt',
    'tests/experiments/NativePreviewFence/PreviewFenceState.cpp',
    'tests/experiments/NativePreviewFence/PreviewFenceState.h',
    'tests/experiments/NativePreviewFence/README.md',
    'tests/experiments/NativePreviewFence/async_queue_harness.cpp',
    'tests/experiments/NativePreviewFence/fence_tests.cpp',
    'tests/experiments/NativePreviewFence/queue_harness.cpp',
    'tests/experiments/NativePreviewFence/run-tests.ps1',
    'tests/experiments/PreviewFenceModel/FenceTests.cs',
    'tests/experiments/PreviewFenceModel/PreviewFenceModel.Tests.csproj',
    'tests/experiments/PreviewFenceModel/PreviewFenceModel.cs',
    'tests/experiments/PreviewFenceModel/README.md',
    'tests/experiments/PreviewFenceModel/RevocationTests.cs',
    'tests/experiments/PreviewFenceModel/TestFence.cs',
    'tests/experiments/PreviewFenceTls/PreviewFenceTls.Tests.csproj',
    'tests/experiments/PreviewFenceTls/README.md',
    'tests/experiments/PreviewFenceTls/TestTlsServer.cs',
    'tests/experiments/PreviewFenceTls/TlsFenceTests.cs',
    'tests/experiments/PreviewOwnerGate/OwnerGateTests.cs',
    'tests/experiments/PreviewOwnerGate/OwnerPreviewGate.cs',
    'tests/experiments/PreviewOwnerGate/PreviewOwnerGate.Tests.csproj',
    'tests/experiments/PreviewOwnerGate/README.md',
    'tests/experiments/VaultGrantIdentity/GrantIdentityFence.cs',
    'tests/experiments/VaultGrantIdentity/GrantIdentityFenceTests.cs',
    'tests/experiments/VaultGrantIdentity/README.md',
    'tests/experiments/VaultGrantIdentity/VaultGrantIdentity.Tests.csproj',
}
REQUIRED_FILES |= RESEARCH_REQUIRED_FILES
# Exact upstream notice inventory prevents silently omitting audited source licenses.
DOTNET_MANIFESTS = {'vendor/notices/dotnet-runtime-10.0.14/source-manifest.json': 'd183cacd1be75c2674c13fc59c703a0be9d79e823db3d5eea2ee89c2dde3d8e9', 'vendor/notices/dotnet-runtime-10.0.15/source-manifest.json': '8d0a90555d855eab88298548896c5e4644e87aade4551e0be9e66d2cdd1f2a60', 'vendor/notices/windows-runtime-packages/source-manifest.json': '179b7f931e64adf117a2a3d855ed88104f101438025cba9f322c327cdfb6a28c'}
DOTNET_NOTICE_PATHS = {
    'vendor/notices/dotnet-runtime-10.0.14/microsoft.netcore.app.runtime.osx-arm64/LICENSE.TXT',
    'vendor/notices/dotnet-runtime-10.0.14/microsoft.netcore.app.runtime.osx-arm64/THIRD-PARTY-NOTICES.TXT',
    'vendor/notices/dotnet-runtime-10.0.14/microsoft.netcore.app.runtime.osx-arm64/package.nuspec.txt',
    'vendor/notices/dotnet-runtime-10.0.14/microsoft.netcore.app.runtime.win-x64/LICENSE.TXT',
    'vendor/notices/dotnet-runtime-10.0.14/microsoft.netcore.app.runtime.win-x64/THIRD-PARTY-NOTICES.TXT',
    'vendor/notices/dotnet-runtime-10.0.14/microsoft.netcore.app.runtime.win-x64/package.nuspec.txt',
    'vendor/notices/dotnet-runtime-10.0.14/microsoft.windowsdesktop.app.runtime.win-x64/LICENSE.txt',
    'vendor/notices/dotnet-runtime-10.0.14/microsoft.windowsdesktop.app.runtime.win-x64/package.nuspec.txt',
    'vendor/notices/dotnet-runtime-10.0.15/microsoft.netcore.app.runtime.osx-arm64/LICENSE.TXT',
    'vendor/notices/dotnet-runtime-10.0.15/microsoft.netcore.app.runtime.osx-arm64/THIRD-PARTY-NOTICES.TXT',
    'vendor/notices/dotnet-runtime-10.0.15/microsoft.netcore.app.runtime.osx-arm64/package.nuspec.txt',
    'vendor/notices/dotnet-runtime-10.0.15/microsoft.netcore.app.runtime.win-x64/LICENSE.TXT',
    'vendor/notices/dotnet-runtime-10.0.15/microsoft.netcore.app.runtime.win-x64/THIRD-PARTY-NOTICES.TXT',
    'vendor/notices/dotnet-runtime-10.0.15/microsoft.netcore.app.runtime.win-x64/package.nuspec.txt',
    'vendor/notices/dotnet-runtime-10.0.15/microsoft.windowsdesktop.app.runtime.win-x64/LICENSE.txt',
    'vendor/notices/dotnet-runtime-10.0.15/microsoft.windowsdesktop.app.runtime.win-x64/package.nuspec.txt',
    'vendor/notices/windows-runtime-packages/Microsoft.NETCore.App.Runtime.win-x64-10.0.13/LICENSE.TXT',
    'vendor/notices/windows-runtime-packages/Microsoft.NETCore.App.Runtime.win-x64-10.0.13/THIRD-PARTY-NOTICES.TXT',
    'vendor/notices/windows-runtime-packages/Microsoft.NETCore.App.Runtime.win-x64-10.0.13/package.nuspec.txt',
    'vendor/notices/windows-runtime-packages/Microsoft.WindowsDesktop.App.Runtime.win-x64-10.0.13/LICENSE.txt',
    'vendor/notices/windows-runtime-packages/Microsoft.WindowsDesktop.App.Runtime.win-x64-10.0.13/package.nuspec.txt',
}
DOTNET_READMES = {name.rsplit('/', 1)[0] + '/README.md' for name in DOTNET_MANIFESTS}
DOTNET_NOTICE_REQUIRED_FILES = DOTNET_NOTICE_PATHS | set(DOTNET_MANIFESTS) | DOTNET_READMES | {
    'docs/二进制载荷依赖与许可审查.md', 'tests/test_dotnet_notice_integrity.py',
}
REQUIRED_FILES |= DOTNET_NOTICE_REQUIRED_FILES

UPSTREAM_NOTICE_REQUIRED_FILES = {
    'vendor/notices/openssl-3.5.9/LICENSE.txt',
    'vendor/notices/openssl-3.5.9/README.md',
    'vendor/notices/qtbase-6.8.3/LICENSES/AFL-2.1.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/Apache-2.0.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/BSD-2-Clause.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/BSD-3-Clause.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/BSD-4-Clause.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/BSL-1.0.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/Bitstream-Vera.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/CC0-1.0.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/FTL.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/GFDL-1.3-no-invariants-only.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/GPL-2.0-only.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/GPL-2.0-or-later.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/GPL-3.0-only.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/HPND.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/IJG.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/IPL-1.0.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/Imlib2.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/LGPL-3.0-only.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/Libpng.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/LicenseRef-BSD-3-Clause-with-PCRE2-Binary-Like-Packages-Exception.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/LicenseRef-ICC-License.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/LicenseRef-Lcs-Telegraphics.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/LicenseRef-Qt-Commercial.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/LicenseRef-SHA1-Public-Domain.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/Linux-syscall-note.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/MIT-Khronos-old.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/MIT-open-group.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/MIT.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/MPL-2.0.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/Qt-GPL-exception-1.0.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/SPL-1.0.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/Unicode-3.0.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/X11.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/Xerox.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/Zlib.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/blessing.txt',
    'vendor/notices/qtbase-6.8.3/LICENSES/libpng-2.0.txt',
    'vendor/notices/qtbase-6.8.3/README.md',
    'vendor/notices/qtbase-6.8.3/attribution/cmake/3rdparty/extra-cmake-modules/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/cmake/3rdparty/kwin/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/D3D12MemoryAllocator/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/VulkanMemoryAllocator/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/android/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/blake2/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/double-conversion/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/easing/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/forkfd/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/freetype/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/gradle/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/harfbuzz-ng/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/iaccessible2/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/icc/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/libjpeg/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/libpng/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/libpsl/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/md4/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/md4c/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/md5/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/pcre2/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/pixman/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/rfc6234/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/sha1/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/sha3/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/siphash/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/sqlite/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/tinycbor/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/wasm/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/wintab/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/xcb/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/3rdparty/zlib/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/corelib/kernel/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/corelib/mimetypes/3rdparty/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/corelib/text/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/dbus/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/gui/opengl/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/gui/painting/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/gui/rhi/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/gui/text/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/gui/vulkan/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/plugins/platforms/cocoa/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/testinternal/3rdparty/cmake/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/testlib/3rdparty/catch2/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/testlib/3rdparty/cycle/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/testlib/3rdparty/linux/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/src/testlib/3rdparty/valgrind/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/attribution/util/gradientgen/qt_attribution.json',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/cmake/3rdparty/extra-cmake-modules/COPYING-CMAKE-SCRIPTS.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/cmake/3rdparty/kwin/COPYING-CMAKE-SCRIPTS.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/D3D12MemoryAllocator/LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/VulkanMemoryAllocator/LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/android/LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/blake2/COPYING.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/double-conversion/LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/easing/LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/forkfd/LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/freetype/BDF-LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/freetype/LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/freetype/PCF-LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/freetype/ZLIB-LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/gradle/LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/harfbuzz-ng/COPYING.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/iaccessible2/LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/icc/LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/libpng/LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/libpsl/PSL-LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/libpsl/src/LICENSE.chromium.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/md4c/LICENSE.md.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/pcre2/LICENCE-SLJIT.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/pcre2/LICENCE.md.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/pixman/LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/rfc6234/LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/sha3/BRG_ENDIAN_LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/sha3/CC0_LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/tinycbor/LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/wasm/DEJAVU-LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/xcb/LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/3rdparty/zlib/LICENSE.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/corelib/kernel/QEVENTDISPATCHER_CF_LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/dbus/LIBDBUS-1-LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/gui/opengl/KHRONOS_LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/gui/painting/QIMAGETRANSFORM_LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/gui/painting/XCONSORTIUM_LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/gui/rhi/MiniEngine_LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/gui/text/AGLFN_LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/gui/vulkan/LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/plugins/platforms/cocoa/COCOA_LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/testinternal/3rdparty/cmake/Copyright.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/testlib/3rdparty/catch2/LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/testlib/3rdparty/cycle/LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/testlib/3rdparty/linux/LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/src/testlib/3rdparty/valgrind/LICENSE.txt.txt',
    'vendor/notices/qtbase-6.8.3/referenced-license-texts/util/gradientgen/WEBGRADIENTS_LICENSE.txt.txt',
    'vendor/notices/upstream-license-source-manifest.json',
}
REQUIRED_FILES |= UPSTREAM_NOTICE_REQUIRED_FILES

MANAGED_RUNTIME_MANIFEST = 'vendor/notices/managed-runtime-license-source-manifest.json'
MANAGED_RUNTIME_MANIFEST_SHA256 = '8a9f03475b9fa3d4b0f6b7a2c87700d5b97c48447f8310c52ae5c8704c6e505c'
MANAGED_RUNTIME_NOTICE_PATHS = {
    'vendor/notices/MicroCom.Runtime-0.11.6/LICENSE.txt',
    'vendor/notices/MicroCom.Runtime-0.11.6/package.nuspec.txt',
    'vendor/notices/Tmds.DBus.Protocol-0.94.1/LICENSE.txt',
    'vendor/notices/Tmds.DBus.Protocol-0.94.1/package.nuspec.txt',
}
REQUIRED_FILES |= MANAGED_RUNTIME_NOTICE_PATHS | {MANAGED_RUNTIME_MANIFEST, 'vendor/notices/managed-runtime-publish-review.md'}

QTTOOLS_MANIFEST = 'vendor/notices/qttools-license-source-manifest.json'
QTTOOLS_MANIFEST_SHA256 = '63a45b087dfa9f3f08c35b616882a08b576e80b7846f0a9f592b634048057664'
QTTOOLS_README = 'vendor/notices/qttools-6.8.3/README.md'
QTTOOLS_NOTICE_PATHS = {
    'vendor/notices/qttools-6.8.3/LICENSES/Apache-2.0.txt',
    'vendor/notices/qttools-6.8.3/LICENSES/BSD-3-Clause.txt',
    'vendor/notices/qttools-6.8.3/LICENSES/BSL-1.0.txt',
    'vendor/notices/qttools-6.8.3/LICENSES/CC0-1.0.txt',
    'vendor/notices/qttools-6.8.3/LICENSES/GFDL-1.3-no-invariants-only.txt',
    'vendor/notices/qttools-6.8.3/LICENSES/GPL-2.0-only.txt',
    'vendor/notices/qttools-6.8.3/LICENSES/GPL-3.0-only.txt',
    'vendor/notices/qttools-6.8.3/LICENSES/LGPL-3.0-only.txt',
    'vendor/notices/qttools-6.8.3/LICENSES/LLVM-exception.txt',
    'vendor/notices/qttools-6.8.3/LICENSES/LicenseRef-Qt-Commercial.txt',
    'vendor/notices/qttools-6.8.3/LICENSES/Qt-GPL-exception-1.0.txt',
    'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/LICENSES/Apache-2.0.txt',
    'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/LICENSES/BSD-3-Clause.txt',
    'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/LICENSES/GPL-3.0-only.txt',
    'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/LICENSES/LicenseRef-Qt-Commercial.txt',
    'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/LICENSES/MIT.txt',
    'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/LICENSES/Qt-GPL-exception-1.0.txt',
    'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/src/3rdparty/GUMBO-AUTHORS.txt',
    'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/src/3rdparty/litehtml/LICENSE',
    'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/src/3rdparty/litehtml/src/gumbo/LICENSE',
    'vendor/notices/qttools-6.8.3/src/assistant/qlitehtml/src/3rdparty/qt_attribution.json',
    'vendor/notices/qttools-6.8.3/src/qdoc/catch/CATCH_LICENSE.txt',
    'vendor/notices/qttools-6.8.3/src/qdoc/catch/qt_attribution.json',
    'vendor/notices/qttools-6.8.3/src/qdoc/qdoc/src/qdoc/clang/AST/LLVM_LICENSE.txt',
    'vendor/notices/qttools-6.8.3/src/qdoc/qdoc/src/qdoc/clang/AST/qt_attribution.json',
}
QTTOOLS_REQUIRED_FILES = QTTOOLS_NOTICE_PATHS | {QTTOOLS_MANIFEST, QTTOOLS_README, 'tests/test_qttools_notice_integrity.py'}
REQUIRED_FILES |= QTTOOLS_REQUIRED_FILES
UPSTREAM_MANIFEST = 'vendor/notices/upstream-license-source-manifest.json'
UPSTREAM_MANIFEST_SHA256 = '9507ecd04da22074cc832e88b0b5eff05dc0960390f16630b49c6f2cbcb25590'
PORTABLE_MARKER = 'vendor/deskflow/original/settings/Deskflow.conf'
PRIVATE = (
    re.compile(rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    re.compile(rb'\bgh[pousr]_[A-Za-z0-9]{30,}\b'),
    re.compile(rb'\bsk-[A-Za-z0-9_-]{20,}\b'),
    re.compile(rb'(?i)wxid_[a-z0-9_]{8,}'),
    re.compile(rb'\b192\.168\.10\.\d{1,3}\b'),
    re.compile(rb'\b192\.168\.31\.\d{1,3}\b'),
    re.compile(re.escape(b'198.18.' + b'0.1')),
    re.compile(rb'/Users/' + b'nian' + b'nian(?:/|\b)'),
)
NOTICE = ("INTERNAL REVIEW CANDIDATE - DO NOT PUBLISH\n"
          "This archive is not a complete corresponding source distribution. "
          "Third-party source/licensing, personal-data review, portable build "
          "instructions and clean-environment rebuild remain unverified.\n").encode()


def _link(path: Path) -> bool:
    info = path.lstat()
    return stat.S_ISLNK(info.st_mode) or bool(getattr(info, 'st_file_attributes', 0) & 0x400)


def _safe_relative(value: str) -> bool:
    p = PurePosixPath(value)
    return bool(value) and not p.is_absolute() and '\\' not in value and ':' not in value and all(
        part not in ('', '.', '..') and not part.endswith(('.', ' ')) for part in p.parts)


def _sensitive(value: bytes) -> bool:
    # Check the original bytes and both UTF-16 byte orders; decoded data is used
    # only for detection, never to modify the bytes written to the candidate.
    if any(pattern.search(value) for pattern in PRIVATE):
        return True
    if b'\x00' not in value:
        return False
    for encoding in ('utf-16-le', 'utf-16-be'):
        normalized = value.decode(encoding, errors='ignore').encode('utf-8')
        if any(pattern.search(normalized) for pattern in PRIVATE):
            return True
    return False


def payload(root: Path, relative: str) -> bytes:
    if not _safe_relative(relative):
        raise ValueError('unsafe candidate path')
    base = root.resolve(strict=True)
    path = root.joinpath(*PurePosixPath(relative).parts)
    for part in [path, *path.parents]:
        if part == root.parent:
            break
        if _link(part):
            raise ValueError('link inside candidate tree')
    if not path.is_file() or base not in path.resolve(strict=True).parents:
        raise ValueError('missing or out-of-tree candidate file')
    ext = path.suffix.lower()
    if ext not in TEXT_EXTENSIONS and relative not in TEXT_FILES and relative not in BINARY_ALLOW:
        raise ValueError('unexpected binary in candidate tree: ' + relative)
    value = path.read_bytes()
    if relative in SCREENSHOT_HASHES and hashlib.sha256(value).hexdigest() != SCREENSHOT_HASHES[relative]:
        raise ValueError('screenshot content does not match reviewed image: ' + relative)
    if len(value) > 8 * 1024 * 1024:
        raise ValueError('candidate file exceeds review limit: ' + relative)
    if (ext in TEXT_EXTENSIONS or relative in TEXT_FILES) and value and (
            (ext == '.md' and b'\x00' in value) or not value.strip(b'\x00')):
        raise ValueError('NUL-corrupted candidate text: ' + relative)
    if relative == PORTABLE_MARKER and value != b' ':
        raise ValueError('portable marker content mismatch: ' + relative)
    if (ext in TEXT_EXTENSIONS or relative in TEXT_FILES) and _sensitive(value):
        raise ValueError('sensitive pattern requires manual review: ' + relative)
    return value


def collect(root: Path) -> list[str]:
    root = root.resolve(strict=True)
    result: set[str] = set()
    for relative in SINGLE_FILES:
        path = root.joinpath(*PurePosixPath(relative).parts)
        if path.exists():
            payload(root, relative)
            result.add(relative)
    for relative in TREES:
        start = root.joinpath(*PurePosixPath(relative).parts)
        if not start.exists():
            continue
        if _link(start) or not start.is_dir():
            raise ValueError('link or invalid candidate root: ' + relative)
        for current, dirs, files in os.walk(start, topdown=True, followlinks=False):
            directory = Path(current)
            for name in dirs:
                if name in SKIP_DIRS or name.startswith('.'):
                    continue
                if _link(directory / name):
                    raise ValueError('link inside candidate tree')
            dirs[:] = [name for name in dirs if name not in SKIP_DIRS and not name.startswith('.')]
            for name in files:
                if name.startswith('.'):
                    continue
                path = directory / name
                name_relative = path.relative_to(root).as_posix()
                payload(root, name_relative)
                result.add(name_relative)
    folded: set[str] = set()
    for name in result:
        if name.casefold() in folded:
            raise ValueError('case-insensitive candidate path collision')
        folded.add(name.casefold())
    for name in sorted(result):
        if not name.endswith('.md'):
            continue
        text = payload(root, name).decode('utf-8-sig', errors='strict')
        for raw in re.findall(r'(?<!!)\[[^\]]+\]\(([^)]+)\)', text):
            target = raw.strip().split(' ', 1)[0].strip('<>')
            uri = urllib.parse.urlsplit(target)
            if uri.scheme:
                if uri.scheme.lower() not in ('https', 'http', 'mailto'):
                    raise ValueError('unsafe documentation link in: ' + name)
                continue
            path = urllib.parse.unquote(uri.path)
            if not path:
                continue
            if path.startswith('/') or '\\' in path:
                raise ValueError('unsafe documentation link in: ' + name)
            resolved = posixpath.normpath(posixpath.join(posixpath.dirname(name), path))
            if resolved == '..' or resolved.startswith('../') or not (
                resolved in result or any(item.startswith(resolved.rstrip('/') + '/') for item in result)
            ):
                raise ValueError('missing local documentation referenced by: ' + name)
    return sorted(result)


def _put(archive: zipfile.ZipFile, name: str, data: bytes) -> None:
    item = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
    item.compress_type = zipfile.ZIP_DEFLATED
    item.create_system = 3
    item.external_attr = (0o100755 if name.endswith('.sh') else 0o100644) << 16
    archive.writestr(item, data)


def validate_complete(paths: list[str]) -> None:
    missing = sorted(REQUIRED_FILES.difference(paths))
    if missing:
        raise ValueError('required candidate files are missing: ' + ', '.join(missing))


def validate_upstream_notice_contents(contents: list[tuple[str, bytes]]) -> None:
    """Verify exact audited manifest and all covered upstream text bytes."""
    by_path = dict(contents)
    if len(by_path) != len(contents):
        raise ValueError('duplicate input path')
    raw = by_path.get(UPSTREAM_MANIFEST)
    if raw is None or hashlib.sha256(raw).hexdigest() != UPSTREAM_MANIFEST_SHA256:
        raise ValueError('upstream license manifest mismatch')
    try:
        records = json.loads(raw.decode('utf-8'))['records']
        if not isinstance(records, list):
            raise ValueError('upstream records are not a list')
        expected = UPSTREAM_NOTICE_REQUIRED_FILES - {
            UPSTREAM_MANIFEST, 'vendor/notices/qtbase-6.8.3/README.md',
            'vendor/notices/openssl-3.5.9/README.md',
        }
        seen = set()
        for record in records:
            path, digest = record['destination'], record['sha256']
            if not isinstance(path, str) or path not in expected or path in seen:
                raise ValueError('unexpected or duplicate upstream license')
            if not isinstance(digest, str) or re.fullmatch('[0-9a-f]{64}', digest) is None:
                raise ValueError('invalid upstream license hash')
            seen.add(path)
            if path not in by_path or hashlib.sha256(by_path[path]).hexdigest() != digest:
                raise ValueError('upstream license content mismatch: ' + path)
        if seen != expected:
            raise ValueError('upstream license manifest is incomplete')
    except (KeyError, TypeError, UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError('invalid upstream license manifest') from exc


def validate_dotnet_notice_contents(contents: list[tuple[str, bytes]]) -> None:
    """Verify the exact audited .NET manifests and all 21 original package files."""
    by_path = dict(contents)
    if len(by_path) != len(contents):
        raise ValueError('duplicate input path')
    seen = set()
    for manifest_name, pinned_digest in DOTNET_MANIFESTS.items():
        raw = by_path.get(manifest_name)
        if raw is None or hashlib.sha256(raw).hexdigest() != pinned_digest:
            raise ValueError('.NET license manifest mismatch: ' + manifest_name)
        try:
            records = json.loads(raw.decode('utf-8'))['records']
            if not isinstance(records, list):
                raise ValueError('.NET license records are not a list')
            group = manifest_name.rsplit('/', 1)[0] + '/'
            expected = {name for name in DOTNET_NOTICE_PATHS if name.startswith(group)}
            group_seen = set()
            for record in records:
                path = record.get('destination', record.get('path'))
                digest = record['sha256']
                if not isinstance(path, str) or path not in expected or path in group_seen:
                    raise ValueError('unexpected or duplicate .NET license')
                if not isinstance(digest, str) or re.fullmatch('[0-9a-f]{64}', digest) is None:
                    raise ValueError('invalid .NET license hash')
                group_seen.add(path)
                if path not in by_path or hashlib.sha256(by_path[path]).hexdigest() != digest:
                    raise ValueError('.NET license content mismatch: ' + path)
            if group_seen != expected:
                raise ValueError('.NET license manifest is incomplete')
            seen |= group_seen
        except (KeyError, TypeError, AttributeError, UnicodeError, json.JSONDecodeError) as exc:
            raise ValueError('invalid .NET license manifest') from exc
    if seen != DOTNET_NOTICE_PATHS:
        raise ValueError('.NET license inventory is incomplete')


def validate_qttools_notice_contents(contents: list[tuple[str, bytes]]) -> None:
    """Verify the pinned manifest and each of 25 QtTools notice files."""
    by_path = dict(contents)
    if len(by_path) != len(contents):
        raise ValueError('duplicate input path')
    raw = by_path.get(QTTOOLS_MANIFEST)
    if raw is None or hashlib.sha256(raw).hexdigest() != QTTOOLS_MANIFEST_SHA256:
        raise ValueError('QtTools license manifest mismatch')
    try:
        records = json.loads(raw.decode('utf-8'))['records']
        if not isinstance(records, list):
            raise ValueError('QtTools records are not a list')
        seen = set()
        for record in records:
            path, digest = record['destination'], record['sha256']
            if not isinstance(path, str) or path not in QTTOOLS_NOTICE_PATHS or path in seen:
                raise ValueError('unexpected or duplicate QtTools license')
            if not isinstance(digest, str) or re.fullmatch('[0-9a-f]{64}', digest) is None:
                raise ValueError('invalid QtTools license hash')
            seen.add(path)
            if path not in by_path or hashlib.sha256(by_path[path]).hexdigest() != digest:
                raise ValueError('QtTools license content mismatch: ' + path)
        if seen != QTTOOLS_NOTICE_PATHS:
            raise ValueError('QtTools license manifest is incomplete')
    except (KeyError, TypeError, UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError('invalid QtTools license manifest') from exc


def validate_managed_runtime_notice_contents(contents: list[tuple[str, bytes]]) -> None:
    """Verify the pinned two-package inventory and original notice bytes."""
    by_path = dict(contents)
    if len(by_path) != len(contents):
        raise ValueError('duplicate input path')
    raw = by_path.get(MANAGED_RUNTIME_MANIFEST)
    if raw is None or hashlib.sha256(raw).hexdigest() != MANAGED_RUNTIME_MANIFEST_SHA256:
        raise ValueError('managed runtime notice manifest mismatch')
    try:
        records = json.loads(raw.decode('utf-8'))['records']
        if not isinstance(records, list) or len(records) != 2:
            raise ValueError('invalid managed runtime notice inventory')
        seen = set()
        for record in records:
            if not isinstance(record['notice_files'], list):
                raise ValueError('invalid managed runtime notice list')
            for notice in record['notice_files']:
                path, digest = notice['destination'], notice['sha256']
                if not isinstance(path, str) or path not in MANAGED_RUNTIME_NOTICE_PATHS or path in seen:
                    raise ValueError('unexpected or duplicate managed runtime notice')
                if not isinstance(digest, str) or re.fullmatch('[0-9a-f]{64}', digest) is None:
                    raise ValueError('invalid managed runtime notice hash')
                seen.add(path)
                if path not in by_path or hashlib.sha256(by_path[path]).hexdigest() != digest:
                    raise ValueError('managed runtime notice content mismatch: ' + path)
        if seen != MANAGED_RUNTIME_NOTICE_PATHS:
            raise ValueError('managed runtime notice inventory incomplete')
    except (KeyError, TypeError, UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError('invalid managed runtime notice manifest') from exc


def write_archive(root: Path, output: Path) -> None:
    paths = collect(root)
    validate_complete(paths)
    contents = [(name, payload(root, name)) for name in paths]
    validate_upstream_notice_contents(contents)
    validate_dotnet_notice_contents(contents)
    validate_qttools_notice_contents(contents)
    validate_managed_runtime_notice_contents(contents)
    records = [{'path': name, 'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
               for name, data in contents]
    # Exclusive creation refuses existing files; remove only the exact file we created on failure.
    created = False
    try:
        with output.open('xb') as stream:
            created = True
            with zipfile.ZipFile(stream, 'w') as archive:
                _put(archive, 'CANDIDATE-NOT-FOR-PUBLICATION.txt', NOTICE)
                for name, data in contents:
                    _put(archive, name, data)
                _put(archive, 'candidate-manifest.json', json.dumps(
                    {'publicReleaseReady': False, 'files': records}, ensure_ascii=False,
                    sort_keys=True, indent=2).encode('utf-8'))
    except Exception:
        if created:
            output.unlink(missing_ok=True)
        raise


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, help='Create new candidate ZIP under .artifacts/source-candidates')
    args = parser.parse_args()
    paths = collect(ROOT)
    validate_complete(paths)
    contents = [(name, payload(ROOT, name)) for name in paths]
    validate_upstream_notice_contents(contents)
    validate_dotnet_notice_contents(contents)
    validate_qttools_notice_contents(contents)
    validate_managed_runtime_notice_contents(contents)
    print(json.dumps({'reviewOnly': True, 'count': len(paths), 'files': paths}, ensure_ascii=False, indent=2))
    if args.output:
        allowed = ROOT / '.artifacts' / 'source-candidates'
        if (not allowed.is_dir() or any(_link(path) for path in (ROOT, ROOT / '.artifacts', allowed))
                or args.output.resolve().parent != allowed.resolve()):
            raise SystemExit('Output must be a new file inside the existing source-candidates directory.')
        if args.output.suffix.lower() != '.zip':
            raise SystemExit('Output must be a .zip file.')
        write_archive(ROOT, args.output)
        print('Created internal review candidate; do not publish.')


if __name__ == '__main__':
    main()
