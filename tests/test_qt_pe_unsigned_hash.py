import contextlib
import io
import tempfile
import hashlib
import importlib.util
from pathlib import Path
import struct
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("pe", ROOT / "scripts/qt_pe_unsigned_hash.py")
pe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pe)

def synthetic():
    # Minimal synthetic PE32+ x64 with one section and one aligned certificate.
    p = bytearray(0x500)
    p[0:2] = b"MZ"
    struct.pack_into("<I", p, 0x3c, 0x80)
    p[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HH", p, 0x84, 0x8664, 1)
    struct.pack_into("<H", p, 0x94, 240)
    opt = 0x98
    struct.pack_into("<H", p, opt, 0x20b)
    struct.pack_into("<I", p, opt + 60, 0x200)
    struct.pack_into("<I", p, opt + 64, 0x12345678)
    struct.pack_into("<I", p, opt + 108, 16)
    struct.pack_into("<II", p, opt + 144, 0x500, 16)
    struct.pack_into("<II", p, opt + 240 + 16, 0x200, 0x200)
    cert = struct.pack("<IHH", 16, 0x200, 2) + b"12345678"
    return bytes(p) + cert

class PeTests(unittest.TestCase):
    def test_cli_missing_file_does_not_disclose_local_path(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "sensitive-private-path" / "missing.dll"
            error = io.StringIO()
            with contextlib.redirect_stderr(error):
                code = pe.main([str(path)])
            self.assertEqual(code, 2)
            self.assertIn("Cannot inspect PE", error.getvalue())
            self.assertNotIn("sensitive-private-path", error.getvalue())

    def test_unsigned_hash_zeroes_only_fields_and_removes_certificate(self):
        b = synthetic(); expected = bytearray(b[:0x500]); expected[0x98 + 64:0x98 + 68] = bytes(4); expected[0x98 + 144:0x98 + 152] = bytes(8)
        self.assertEqual(pe.normalized_sha1(b), hashlib.sha1(expected).hexdigest())
    def test_rejects_truncated_and_non_pe_data(self):
        for b in (b"MZ", bytes(512), synthetic()[:120], synthetic()[:0x500]):
            with self.subTest(length=len(b)), self.assertRaises(ValueError): pe.normalized_sha1(b)
    def test_rejects_certificate_trailing_overlay_and_overlap(self):
        for mode in ("trailing", "overlap", "unaligned"):
            b = bytearray(synthetic())
            if mode == "trailing": b.extend(b"X")
            else: struct.pack_into("<I", b, 0x98 + 144, 0x200 if mode == "overlap" else 0x4ff)
            with self.subTest(mode=mode), self.assertRaises(ValueError): pe.normalized_sha1(bytes(b))
    def test_rejects_invalid_certificate_and_directory(self):
        for mode in ("length", "revision", "type", "directory_count", "optional_header"):
            b = bytearray(synthetic())
            if mode == "length": struct.pack_into("<I", b, 0x500, 7)
            if mode == "revision": struct.pack_into("<H", b, 0x504, 0x100)
            if mode == "type": struct.pack_into("<H", b, 0x506, 1)
            if mode == "directory_count": struct.pack_into("<I", b, 0x98 + 108, 4)
            if mode == "optional_header": struct.pack_into("<H", b, 0x94, 100)
            with self.subTest(mode=mode), self.assertRaises(ValueError): pe.normalized_sha1(bytes(b))
    def test_rejects_overlapping_sections(self):
        b = bytearray(synthetic())
        struct.pack_into("<H", b, 0x86, 2)
        struct.pack_into("<II", b, 0x98 + 240 + 40 + 16, 0x100, 0x200)
        with self.assertRaises(ValueError): pe.normalized_sha1(bytes(b))
    def test_rejects_wrong_architecture_and_section_oob(self):
        for mode in ("arch", "section_oob"):
            b = bytearray(synthetic())
            if mode == "arch": struct.pack_into("<H", b, 0x84, 0x14c)
            else: struct.pack_into("<I", b, 0x98 + 240 + 20, 0x500)
            with self.subTest(mode=mode), self.assertRaises(ValueError): pe.normalized_sha1(bytes(b))

if __name__ == "__main__": unittest.main()
