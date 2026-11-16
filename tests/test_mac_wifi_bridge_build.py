"""Offline tests for the isolated macOS Wi-Fi bridge build orchestration."""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts/mac_wifi_bridge_build.py"
spec = importlib.util.spec_from_file_location("wifi_build", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class WifiBridgeBuildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for relative in module.SOURCES:
            path = self.root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("synthetic " + path.name, encoding="utf-8")
        self.output = self.root / ".artifacts/mac-discovery-native/test-1234567890abcdef1234567890abcdef"
        self.calls = []

    def runner(self, args, **kwargs):
        self.calls.append(list(args))
        if args[:3] == ["/usr/bin/xcrun", "clang", "--version"]:
            return subprocess.CompletedProcess(args, 0, "synthetic clang", "")
        if args[0] == "/usr/bin/nm":
            return subprocess.CompletedProcess(args, 0, "00000000 T _nian_wifi_evidence_v1\n", "")
        if "-o" in args:
            dest = Path(args[args.index("-o") + 1])
            if dest.name == "libNianWifiEvidence.dylib":
                dest.write_bytes(struct.pack("<IIII", 0xfeedfacf, 0x0100000c, 0, 6) + bytes(16))
            else:
                dest.write_bytes(b"synthetic executable or object")
        return subprocess.CompletedProcess(args, 0, "", "")

    def test_builds_synthetic_tests_and_signed_bridge_without_network_probe(self):
        module.build(self.root, self.output, run=self.runner, system="Darwin", machine="arm64")
        evidence = json.loads((self.output / "wifi-build-evidence.json").read_text(encoding="utf-8"))
        self.assertEqual(evidence["schema"], "mac-wifi-bridge-build-v1")
        self.assertEqual(evidence["signing"], "ad-hoc")
        self.assertEqual(evidence["syntheticTests"], ["WifiEvidenceCoreTests", "WifiEvidenceBridgeNameTests"])
        self.assertFalse(evidence["networkObservation"])
        self.assertEqual(evidence["bridgeSha256"], hashlib.sha256((self.output / "libNianWifiEvidence.dylib").read_bytes()).hexdigest())
        self.assertEqual({x["path"] for x in evidence["sources"]}, set(module.SOURCES))
        self.assertTrue(any(args[0].endswith("WifiEvidenceCoreTests") for args in self.calls))
        self.assertTrue(any(args[0].endswith("WifiEvidenceBridgeNameTests") for args in self.calls))
        self.assertTrue(any(args[:2] == ["/usr/bin/codesign", "--verify"] for args in self.calls))
        for args in self.calls:
            if args[:2] == ["/usr/bin/xcrun", "clang"] and "--version" not in args:
                self.assertIn("-target", args)
                self.assertEqual(args[args.index("-target") + 1], "arm64-apple-macosx14.0")
        self.assertFalse(any("--run" in args for args in self.calls))
        self.assertFalse(any("WifiEvidenceBridgeSmoke" in " ".join(args) for args in self.calls))

    def test_refuses_unsafe_location_overwrite_and_non_mac(self):
        with self.assertRaises(ValueError): module.build(self.root, self.output, run=self.runner, system="Windows", machine="AMD64")
        with self.assertRaises(ValueError): module.build(self.root, self.root / "elsewhere", run=self.runner, system="Darwin", machine="arm64")
        self.output.mkdir(parents=True)
        with self.assertRaises(ValueError): module.build(self.root, self.output, run=self.runner, system="Darwin", machine="arm64")

    def test_wrong_symbol_list_cannot_create_evidence(self):
        def wrong_symbols(args, **kwargs):
            result = self.runner(args, **kwargs)
            if args[0] == "/usr/bin/nm": return subprocess.CompletedProcess(args, 0, "0000 T _unrelated\n", "")
            return result
        with self.assertRaises(ValueError): module.build(self.root, self.output, run=wrong_symbols, system="Darwin", machine="arm64")
        self.assertFalse((self.output / "wifi-build-evidence.json").exists())

    def test_changed_input_fails_closed(self):
        def changed(args, **kwargs):
            result = self.runner(args, **kwargs)
            if args[0] == "/usr/bin/nm": (self.root / module.SOURCES[0]).write_text("changed", encoding="utf-8")
            return result
        with self.assertRaises(ValueError): module.build(self.root, self.output, run=changed, system="Darwin", machine="arm64")
        self.assertFalse((self.output / "wifi-build-evidence.json").exists())


    def test_wrong_architecture_rejected_without_evidence(self):
        def wrong_arch(args, **kwargs):
            result = self.runner(args, **kwargs)
            if args[0] == "/usr/bin/nm":
                path = self.output / "libNianWifiEvidence.dylib"
                path.write_bytes(struct.pack("<IIII", 0xfeedfacf, 0x01000007, 0, 6) + bytes(16))
            return result
        with self.assertRaises(ValueError): module.build(self.root, self.output, run=wrong_arch, system="Darwin", machine="arm64")
        self.assertFalse((self.output / "wifi-build-evidence.json").exists())

    def test_failed_synthetic_test_cannot_create_evidence(self):
        def failed_test(args, **kwargs):
            if args[0].endswith("WifiEvidenceCoreTests"):
                raise subprocess.CalledProcessError(1, args, stderr="synthetic failure")
            return self.runner(args, **kwargs)
        with self.assertRaises(subprocess.CalledProcessError): module.build(self.root, self.output, run=failed_test, system="Darwin", machine="arm64")
        self.assertFalse((self.output / "wifi-build-evidence.json").exists())


if __name__ == "__main__": unittest.main()
