import contextlib
import io
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("qtverify", ROOT / "scripts/verify_qt_official_archive.py")
qt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(qt)

class QtProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.archive = self.root / "source.7z"
        self.archive.write_bytes(b"synthetic archive")
        self.keyboard = self.root / "keyboard"
        self.keyboard.mkdir()
        self.files = {}
        for historical, member in qt.MEMBERS.items():
            data = ("synthetic " + historical).encode()
            path = self.keyboard / historical
            path.parent.mkdir(exist_ok=True)
            path.write_bytes(data)
            self.files[member] = data
        self.files[qt.SPDX] = json.dumps({"files": [{"fileName": "./" + member, "checksums": [{"algorithm":"SHA1", "checksumValue": "a" * 40}]} for member in qt.MEMBERS.values()]}).encode()
        self.pin = hashlib.sha256(self.archive.read_bytes()).hexdigest()
        self.spdx_pin = hashlib.sha256(self.files[qt.SPDX]).hexdigest()
    def reader(self, archive, member, executable):
        return self.files[member]
    def test_cli_missing_archive_does_not_disclose_local_path(self):
        path = self.root / "sensitive-private-path" / "missing.7z"
        error = io.StringIO()
        with contextlib.redirect_stderr(error):
            code = qt.main([str(path), str(self.keyboard)])
        self.assertEqual(code, 2)
        self.assertIn("Comparison failed", error.getvalue())
        self.assertNotIn("sensitive-private-path", error.getvalue())

    def test_valid_source_matches_and_explicitly_reports_sbom_mismatch(self):
        with patch.object(qt, "ARCHIVE_SHA256", self.pin), patch.object(qt, "SPDX_SHA256", hashlib.sha256(self.files[qt.SPDX]).hexdigest()), patch.object(qt, "extract_member", self.reader):
            result = qt.compare(self.archive, self.keyboard, "tar")
        self.assertTrue(result["all_equal"])
        self.assertEqual(len(result["files"]), 6)
        self.assertFalse(result["all_match_sbom_sha1"])
    def test_changed_historical_file_is_detected(self):
        (self.keyboard / "Qt6Core.dll").write_bytes(b"changed")
        with patch.object(qt, "ARCHIVE_SHA256", self.pin), patch.object(qt, "SPDX_SHA256", hashlib.sha256(self.files[qt.SPDX]).hexdigest()), patch.object(qt, "extract_member", self.reader):
            result = qt.compare(self.archive, self.keyboard, "tar")
        self.assertFalse(result["all_equal"])
        self.assertFalse(result["files"][0]["byte_equal"])
    def test_archive_hash_is_checked_before_extraction(self):
        with patch.object(qt, "extract_member", side_effect=AssertionError("must not extract")):
            with self.assertRaisesRegex(ValueError, "archive SHA-256"):
                qt.compare(self.archive, self.keyboard, "tar")
    def test_missing_sbom_entry_fails(self):
        self.files[qt.SPDX] = b'{"files":[]}'
        with patch.object(qt, "ARCHIVE_SHA256", self.pin), patch.object(qt, "SPDX_SHA256", hashlib.sha256(self.files[qt.SPDX]).hexdigest()), patch.object(qt, "extract_member", self.reader):
            with self.assertRaisesRegex(ValueError, "SPDX"):
                qt.compare(self.archive, self.keyboard, "tar")
    def test_duplicate_sbom_entry_is_rejected(self):
        spdx = json.loads(self.files[qt.SPDX])
        spdx["files"].append(spdx["files"][0])
        self.files[qt.SPDX] = json.dumps(spdx).encode()
        with patch.object(qt, "ARCHIVE_SHA256", self.pin), patch.object(qt, "SPDX_SHA256", hashlib.sha256(self.files[qt.SPDX]).hexdigest()), patch.object(qt, "extract_member", self.reader):
            with self.assertRaisesRegex(ValueError, "SPDX"):
                qt.compare(self.archive, self.keyboard, "tar")
    def test_spdx_hash_mismatch_fails(self):
        self.files[qt.SPDX] = b"changed"
        with patch.object(qt, "ARCHIVE_SHA256", self.pin), patch.object(qt, "extract_member", self.reader):
            with self.assertRaisesRegex(ValueError, "SPDX SHA-256"):
                qt.compare(self.archive, self.keyboard, "tar")
    def test_missing_historical_file_fails(self):
        (self.keyboard / "Qt6Core.dll").unlink()
        with patch.object(qt, "ARCHIVE_SHA256", self.pin), patch.object(qt, "SPDX_SHA256", hashlib.sha256(self.files[qt.SPDX]).hexdigest()), patch.object(qt, "extract_member", self.reader):
            with self.assertRaises(FileNotFoundError):
                qt.compare(self.archive, self.keyboard, "tar")

if __name__ == "__main__": unittest.main()
