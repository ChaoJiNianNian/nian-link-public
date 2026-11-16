"""Isolated contract tests for the macOS native build entry point."""
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts/mac_native_build.py"
spec = importlib.util.spec_from_file_location("mac_native_build", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class NativeBuildTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.sources = self.root / "src/DesktopToolbox.Mac/Native"
        self.sources.mkdir(parents=True)
        for name in module.SOURCES:
            (self.sources / name).write_text("synthetic " + name, encoding="utf-8")
        self.output = self.root / ".artifacts/mac-native/native-1234567890abcdef1234567890abcdef"

    def runner(self, args, **kwargs):
        if args[0] == "/usr/bin/xcrun":
            return subprocess.CompletedProcess(args, 0, "Apple Swift synthetic toolchain", "")
        self.assertEqual(args[0], "/bin/sh")
        self.assertEqual(Path(args[1]).parent, self.output / "sources")
        name = Path(args[2]).name
        kind = {"DeskLinkMedia": 2, "libDeskLinkWeb.dylib": 6}[name]
        Path(args[2]).write_bytes(struct.pack("<IIII", 0xfeedfacf, 0x0100000c, 0, kind) + bytes(16))
        return subprocess.CompletedProcess(args, 0, "", "")

    def test_builds_both_from_frozen_sources_and_records_manifest(self):
        module.build(self.root, self.output, run=self.runner, system="Darwin", machine="arm64")
        evidence = json.loads((self.output / "build-evidence.json").read_text(encoding="utf-8"))
        self.assertEqual(evidence["schema"], "mac-native-build-v1")
        self.assertEqual(evidence["label"], self.output.name)
        self.assertEqual({x["name"] for x in evidence["sourceFiles"]}, set(module.SOURCES))
        self.assertEqual({x["name"] for x in evidence["binaries"]}, {"DeskLinkMedia", "libDeskLinkWeb.dylib"})
        for entry in evidence["sourceFiles"]:
            name = entry["name"]
            source_hash = hashlib.sha256((self.sources / name).read_bytes()).hexdigest().upper()
            self.assertEqual(entry["sha256"], source_hash)
            self.assertEqual((self.output / "sources" / name).read_bytes(), (self.sources / name).read_bytes())
        for entry in evidence["binaries"]:
            binary_hash = hashlib.sha256((self.output / entry["name"]).read_bytes()).hexdigest().upper()
            self.assertEqual(entry["sha256"], binary_hash)
            self.assertEqual(entry["architecture"], "arm64")

    def test_rejects_overwriting_and_output_outside_project(self):
        self.output.mkdir(parents=True)
        with self.assertRaises(ValueError):
            module.build(self.root, self.output, run=self.runner, system="Darwin", machine="arm64")
        with self.assertRaises(ValueError):
            module.build(self.root, self.root / "elsewhere", run=self.runner, system="Darwin", machine="arm64")

    def test_refuses_non_mac_and_wrong_binary(self):
        with self.assertRaises(ValueError):
            module.build(self.root, self.output, run=self.runner, system="Windows", machine="AMD64")
        def bad_runner(args, **kwargs):
            result = self.runner(args, **kwargs)
            if args[0] == "/bin/sh":
                Path(args[2]).write_bytes(b"not a Mach-O binary")
            return result
        with self.assertRaises(ValueError):
            module.build(self.root, self.output, run=bad_runner, system="Darwin", machine="arm64")
        self.assertFalse((self.output / "build-evidence.json").exists())

    def test_changed_sources_fail_without_claiming_evidence(self):
        def changing_runner(args, **kwargs):
            result = self.runner(args, **kwargs)
            if args[0] == "/bin/sh":
                (self.sources / module.SOURCES[0]).write_text("changed", encoding="utf-8")
            return result
        with self.assertRaises(ValueError):
            module.build(self.root, self.output, run=changing_runner, system="Darwin", machine="arm64")
        self.assertFalse((self.output / "build-evidence.json").exists())


if __name__ == "__main__":
    unittest.main()
