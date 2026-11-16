#!/usr/bin/env python3
"""Hash a PE32+ x64 image after a documented, in-memory certificate normalization.

This does not verify a signature, reconstruct a proven original, or modify a file.
"""
import argparse
import hashlib
from pathlib import Path
import struct
import sys


def normalized_sha1(data):
    def fail(): raise ValueError("unsupported or malformed signed PE32+ x64 image")
    if len(data) < 64 or data[:2] != b"MZ": fail()
    pe = struct.unpack_from("<I", data, 60)[0]
    if pe < 64 or pe + 24 > len(data) or data[pe:pe+4] != b"PE\0\0": fail()
    machine, sections = struct.unpack_from("<HH", data, pe+4)
    opt_size = struct.unpack_from("<H", data, pe+20)[0]
    opt = pe + 24
    if machine != 0x8664 or not 1 <= sections <= 96 or opt_size < 152 or opt + opt_size > len(data): fail()
    if struct.unpack_from("<H", data, opt)[0] != 0x20b: fail()
    if struct.unpack_from("<I", data, opt+108)[0] < 5: fail()
    section_table = opt + opt_size
    headers = struct.unpack_from("<I", data, opt+60)[0]
    if section_table + 40*sections > len(data) or not section_table + 40*sections <= headers <= len(data): fail()
    cert, cert_size = struct.unpack_from("<II", data, opt+144)
    if cert % 8 or cert_size < 8 or cert < headers or cert + cert_size != len(data): fail()
    ranges = []
    for index in range(sections):
        raw_size, raw_offset = struct.unpack_from("<II", data, section_table + 40*index + 16)
        if raw_size:
            if raw_offset < headers or raw_offset + raw_size > cert: fail()
            ranges.append((raw_offset, raw_offset + raw_size))
    ranges.sort()
    if any(a[1] > b[0] for a, b in zip(ranges, ranges[1:])): fail()
    cursor = cert
    while cursor < len(data):
        if cursor + 8 > len(data): fail()
        length, revision, kind = struct.unpack_from("<IHH", data, cursor)
        if length < 8 or revision != 0x200 or kind != 2 or cursor + length > len(data): fail()
        padded = (length + 7) & ~7
        if cursor + padded > len(data): fail()
        cursor += padded
    if cursor != len(data): fail()
    normalized = bytearray(data[:cert])
    struct.pack_into("<I", normalized, opt+64, 0)
    struct.pack_into("<II", normalized, opt+144, 0, 0)
    return hashlib.sha1(normalized).hexdigest()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file", type=Path, help="PE image to inspect without modification")
    args = parser.parse_args(argv)
    try: print(normalized_sha1(args.file.read_bytes()))
    except (OSError, ValueError):
        print("Cannot inspect PE: input is unreadable or unsupported.", file=sys.stderr); return 2
    return 0

if __name__ == "__main__": raise SystemExit(main())
