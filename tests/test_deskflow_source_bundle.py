"""Safety checks for the exact Deskflow source-material bundle.

These tests use only synthetic files under this project's test workspace.
"""
import hashlib
import importlib.util
import json
from pathlib import Path
import stat
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("prepare_deskflow_source", ROOT / "scripts/prepare_deskflow_source.py")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class DeskflowBundleTests(unittest.TestCase):
    def setUp(self):
        work = ROOT / "tests/.work"
        work.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="deskflow-source-test-", dir=work)
        self.root = Path(self.temporary.name)
        self.source = self.root / "input"
        self.source.mkdir()
        (self.source / "sample.bin").write_bytes(b"synthetic\x00source")
        digest = hashlib.sha256(b"synthetic\x00source").hexdigest()
        self.entries = [("sample.bin", "sources/sample.bin", digest)]

    def tearDown(self):
        self.temporary.cleanup()

    def test_valid_bundle_only_contains_whitelisted_files_and_manifest(self):
        out = self.root / "candidate.zip"
        MODULE.write_bundle(self.source, out, self.entries)
        with zipfile.ZipFile(out) as z:
            self.assertEqual(set(z.namelist()), {"sources/sample.bin", "MANIFEST.json", "README.txt"})
            manifest = json.loads(z.read("MANIFEST.json"))
            self.assertEqual(manifest["files"][0]["sha256"], self.entries[0][2])
            self.assertNotIn(str(self.source), z.read("MANIFEST.json").decode())
            self.assertEqual(z.read("sources/sample.bin"), b"synthetic\x00source")

    def test_digest_mismatch_never_creates_final_bundle(self):
        out = self.root / "candidate.zip"
        with self.assertRaises(ValueError):
            MODULE.write_bundle(self.source, out, [("sample.bin", "sources/sample.bin", "0" * 64)])
        self.assertFalse(out.exists())

    def test_reverification_detects_tampered_zip(self):
        out = self.root / "candidate.zip"
        MODULE.write_bundle(self.source, out, self.entries)
        with zipfile.ZipFile(out, "a") as z:
            z.writestr("unexpected.txt", "not whitelisted")
        with self.assertRaises(ValueError):
            MODULE.verify_bundle(out, self.entries)

    def test_manifest_cannot_claim_another_core(self):
        out = self.root / "candidate.zip"
        MODULE.write_bundle(self.source, out, self.entries)
        with zipfile.ZipFile(out) as z:
            content = {item.filename: z.read(item.filename) for item in z.infolist()}
        manifest = json.loads(content["MANIFEST.json"])
        manifest["recordedWindowsCoreSha256"] = "0" * 64
        content["MANIFEST.json"] = json.dumps(manifest).encode()
        forged = self.root / "forged.zip"
        with zipfile.ZipFile(forged, "w") as z:
            for name, data in content.items():
                z.writestr(name, data, compress_type=zipfile.ZIP_STORED)
        with self.assertRaises(ValueError):
            MODULE.verify_bundle(forged, self.entries)

    def test_existing_output_is_never_overwritten(self):
        out = self.root / "candidate.zip"
        out.write_bytes(b"existing")
        with self.assertRaises(FileExistsError):
            MODULE.write_bundle(self.source, out, self.entries)
        self.assertEqual(out.read_bytes(), b"existing")

    def test_traversal_and_duplicate_archive_names_refused(self):
        for entry in [
            ("../sample.bin", "sources/s.bin", "0" * 64),
            ("sample.bin", "../evil", "0" * 64),
        ]:
            with self.subTest(entry=entry), self.assertRaises(ValueError):
                MODULE.write_bundle(self.source, self.root / "candidate.zip", [entry])
        with self.assertRaises(ValueError):
            MODULE.write_bundle(self.source, self.root / "candidate.zip", self.entries * 2)

    def test_output_inside_source_tree_refused(self):
        with self.assertRaises(ValueError):
            MODULE.write_bundle(self.source, self.source / "candidate.zip", self.entries)

    def test_windows_reparse_point_is_rejected(self):
        target = self.source / "sample.bin"
        real_lstat = Path.lstat
        def fake_lstat(path, *args, **kwargs):
            if path == target:
                return SimpleNamespace(st_mode=stat.S_IFREG, st_file_attributes=0x400)
            return real_lstat(path, *args, **kwargs)
        with patch.object(Path, "lstat", autospec=True, side_effect=fake_lstat):
            with self.assertRaises(ValueError):
                MODULE.write_bundle(self.source, self.root / "candidate.zip", self.entries)


if __name__ == "__main__":
    unittest.main()
