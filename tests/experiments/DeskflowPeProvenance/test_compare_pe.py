import contextlib
import io
import json
from pathlib import Path
import struct
import tempfile
import unittest

import compare_pe

def fixture(timestamp=100):
    data = bytearray(0x600)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3c, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", data, 0x84, 0x8664, 1, timestamp, 0, 0, 0xF0, 0)
    opt = 0x98
    struct.pack_into("<H", data, opt, 0x20b)
    struct.pack_into("<I", data, opt + 108, 16)
    struct.pack_into("<II", data, opt + 112 + 6 * 8, 0x1020, 28)
    sh = opt + 0xF0
    data[sh:sh + 8] = b".rdata\0\0"
    struct.pack_into("<IIII", data, sh + 8, 0x200, 0x1000, 0x200, 0x400)
    struct.pack_into("<I", data, 0x424, timestamp)
    struct.pack_into("<I", data, 0x42c, 2)
    return bytes(data)

class CompareTests(unittest.TestCase):
    def test_only_two_verified_timestamps_are_normalized(self):
        a,b=fixture(1),fixture(0x1020304)
        result=compare_pe.compare(a,b)
        self.assertTrue(result["normalized_equal"])
        self.assertFalse(result["raw_equal"])
        self.assertEqual(result["different_byte_count"], 8)
        self.assertEqual(result["normalized_sha256"][0], result["normalized_sha256"][1])
        self.assertEqual(a,fixture(1)); self.assertEqual(b,fixture(0x1020304))

    def test_non_timestamp_change_remains_visible(self):
        b=bytearray(fixture(2)); b[0x450]=0x42
        result=compare_pe.compare(fixture(1),bytes(b))
        self.assertFalse(result["normalized_equal"])
        self.assertNotEqual(*result["normalized_sha256"])

    def test_reject_bad_pe_and_truncated(self):
        for b in (b"", b"MZ", b"N"+fixture()[1:], fixture()[:0x100]):
            with self.subTest(length=len(b)), self.assertRaises(ValueError):compare_pe.compare(b,fixture())

    def test_reject_bad_debug_directory(self):
        for offset,value in ((0x98+112+6*8, 0x999999), (0x98+112+6*8+4, 56), (0x424, 222)):
            b=bytearray(fixture());struct.pack_into("<I",b,offset,value)
            with self.subTest(offset=offset), self.assertRaises(ValueError):compare_pe.compare(bytes(b),fixture())

    def test_reject_non_x64_or_non_pe32plus(self):
        for offset,value in ((0x84,0x14c),(0x98,0x10b)):
            b=bytearray(fixture());struct.pack_into("<H",b,offset,value)
            with self.subTest(offset=offset), self.assertRaises(ValueError):compare_pe.compare(bytes(b),fixture())

    def test_reject_truncated_or_invalid_debug_payload(self):
        variants = []
        no_type=bytearray(fixture());struct.pack_into("<I",no_type,0x42c,0);variants.append(no_type)
        missing=bytearray(fixture());struct.pack_into("<I",missing,0x430,0x100);variants.append(missing)
        past_end=bytearray(fixture());struct.pack_into("<I",past_end,0x430,0x100);struct.pack_into("<I",past_end,0x438,0x580);variants.append(past_end)
        for b in variants:
            with self.assertRaises(ValueError):compare_pe.compare(bytes(b),fixture())

    def test_reject_overlapping_section_ranges(self):
        b=bytearray(fixture());struct.pack_into("<H",b,0x86,2)
        h=0x98+0xf0+40;b[h:h+8]=b".other\0\0";struct.pack_into("<IIII",b,h+8,0x80,0x2000,0x80,0x400)
        with self.assertRaises(ValueError):compare_pe.compare(bytes(b),fixture())

    def test_cli_reports_match_without_disclosing_input_paths(self):
        with tempfile.TemporaryDirectory() as t:
            one=Path(t)/"secret-name-one.exe";two=Path(t)/"secret-name-two.exe"
            one.write_bytes(fixture(1));two.write_bytes(fixture(2));stream=io.StringIO()
            with contextlib.redirect_stdout(stream):code=compare_pe.main([str(one),str(two)])
            body=stream.getvalue(); self.assertEqual(code,0);self.assertNotIn("secret-name",body);self.assertTrue(json.loads(body)["normalized_equal"])

    def test_cli_returns_failure_for_difference_and_invalid_file(self):
        with tempfile.TemporaryDirectory() as t:
            one=Path(t)/"a";two=Path(t)/"b";one.write_bytes(fixture());v=bytearray(fixture());v[0x450]=1;two.write_bytes(v)
            with contextlib.redirect_stdout(io.StringIO()),contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(compare_pe.main([str(one),str(two)]),1)
                two.write_bytes(b"MZ");self.assertEqual(compare_pe.main([str(one),str(two)]),2)

if __name__=="__main__":unittest.main()
