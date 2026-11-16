import struct
import tempfile
import unittest
from pathlib import Path
from compare_coff import compare, parse


def make_object(path, code=b'\x90\x90\xc3\0', target=b'symbol'):
    rawptr = 60
    relptr = rawptr + len(code)
    symptr = relptr + 10
    header = struct.pack('<HHLLLHH', 0x8664, 1, 0, symptr, 1, 0, 0)
    section = struct.pack('<8sLLLLLLHHL', b'.text\0\0\0', 0, 0, len(code),
                          rawptr, relptr, 0, 1, 0, 0)
    relocation = struct.pack('<LLH', 0, 0, 4)
    if len(target) > 8:
        symname = b'\0\0\0\0' + struct.pack('<L', 4)
        table = struct.pack('<L', 5 + len(target)) + target + b'\0'
    else:
        symname = target.ljust(8, b'\0')
        table = struct.pack('<L', 4)
    symbol = struct.pack('<8sLhHBB', symname, 0, 1, 0, 2, 0)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(header + section + code + relocation + symbol + table)


class CoffTests(unittest.TestCase):
    def test_changed_code_is_detected(self):
        with tempfile.TemporaryDirectory() as temp:
            a, b = Path(temp) / 'a', Path(temp) / 'b'
            make_object(a / 'one.obj')
            make_object(b / 'one.obj', code=b'\x91\x90\xc3\0')
            entry = compare(a, b)['objects'][0]
            self.assertFalse(entry['textBytesMatch'])

    def test_only_anonymous_namespace_hash_is_normalized(self):
        with tempfile.TemporaryDirectory() as temp:
            a, b = Path(temp) / 'a', Path(temp) / 'b'
            make_object(a / 'one.obj', target=b'?name@?A0x11111111@desk@@')
            make_object(b / 'one.obj', target=b'?name@?A0x22222222@desk@@')
            entry = compare(a, b)['objects'][0]
            self.assertTrue(entry['textBytesMatch'])
            self.assertFalse(entry['textAndRelocationsMatch'])
            self.assertTrue(entry['textAndNormalizedRelocationsMatch'])

    def test_different_relocation_target_is_detected(self):
        with tempfile.TemporaryDirectory() as temp:
            a, b = Path(temp) / 'a', Path(temp) / 'b'
            make_object(a / 'one.obj', target=b'first')
            make_object(b / 'one.obj', target=b'second')
            entry = compare(a, b)['objects'][0]
            self.assertTrue(entry['textBytesMatch'])
            self.assertFalse(entry['textAndNormalizedRelocationsMatch'])

    def test_truncated_object_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'one.obj'
            path.write_bytes(b'not COFF')
            with self.assertRaises(ValueError):
                parse(path, False)


if __name__ == '__main__':
    unittest.main()
