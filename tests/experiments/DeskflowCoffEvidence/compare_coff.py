"""Read-only comparison of x64 COFF .text and relocations in two build trees.

This is provenance evidence, not proof that a source tree corresponds to a
release binary. It deliberately does not compare data sections or linked
libraries and does not infer behavioral equivalence.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct


def parse(path: Path, normalize_anonymous: bool):
    data = path.read_bytes()
    if len(data) < 20:
        raise ValueError('truncated COFF header')
    machine, count, _, symptr, nsyms, opt, _ = struct.unpack_from('<HHLLLHH', data)
    if machine != 0x8664 or 20 + opt + count * 40 > len(data):
        raise ValueError('unsupported or malformed COFF object')
    string_base = symptr + 18 * nsyms
    if string_base + 4 > len(data):
        raise ValueError('missing COFF string table')
    string_size = struct.unpack_from('<L', data, string_base)[0]
    string_end = string_base + string_size
    if string_size < 4 or string_end > len(data):
        raise ValueError('invalid COFF string table')

    def symbol(index):
        if index >= nsyms:
            raise ValueError('invalid COFF symbol index')
        offset = symptr + 18 * index
        raw = data[offset:offset + 8]
        if raw[:4] == b'\0' * 4:
            start = string_base + struct.unpack_from('<L', raw, 4)[0]
            end = data.find(b'\0', start, string_end)
            if start < string_base + 4 or end < 0:
                raise ValueError('invalid COFF symbol name')
            name = data[start:end]
        else:
            name = raw.split(b'\0', 1)[0]
        if normalize_anonymous:
            name = re.sub(rb'\?A0x[0-9a-fA-F]{8}@', b'?A0xANON@', name)
        return name

    sections = []
    for i in range(count):
        offset = 20 + opt + i * 40
        name = data[offset:offset + 8].split(b'\0', 1)[0]
        if name != b'.text' and not name.startswith(b'.text$'):
            continue
        size, raw, rel = struct.unpack_from('<LLL', data, offset + 16)
        numrel = struct.unpack_from('<H', data, offset + 32)[0]
        if numrel == 65535 or raw + size > len(data) or rel + 10 * numrel > len(data):
            raise ValueError('unsupported or malformed COFF section')
        relocations = []
        for j in range(numrel):
            loc, index, kind = struct.unpack_from('<LLH', data, rel + 10 * j)
            relocations.append((loc, kind, symbol(index)))
        sections.append((name, hashlib.sha256(data[raw:raw + size]).digest(), size,
                         tuple(relocations)))
    return sections


def compare(old: Path, new: Path):
    old_files = {p.relative_to(old).as_posix(): p for p in old.rglob('*.obj') if p.is_file()}
    new_files = {p.relative_to(new).as_posix(): p for p in new.rglob('*.obj') if p.is_file()}
    common = sorted(old_files.keys() & new_files.keys())
    results = []
    for name in common:
        a, b = old_files[name], new_files[name]
        original_a, original_b = parse(a, False), parse(b, False)
        normalized_a, normalized_b = parse(a, True), parse(b, True)
        code_a = [(x[0], x[1], x[2]) for x in original_a]
        code_b = [(x[0], x[1], x[2]) for x in original_b]
        results.append({'path': name, 'textSectionCount': len(code_a),
                        'relocationCount': sum(len(x[3]) for x in original_a),
                        'textBytesMatch': code_a == code_b,
                        'textAndRelocationsMatch': original_a == original_b,
                        'textAndNormalizedRelocationsMatch': normalized_a == normalized_b})
    return {'oldObjects': len(old_files), 'newObjects': len(new_files),
            'oldOnly': sorted(old_files.keys() - new_files.keys()),
            'newOnly': sorted(new_files.keys() - old_files.keys()),
            'objects': results,
            'scope': 'COFF .text section bytes and relocation descriptors; not data, link, full binaries, runtime or correspondence proof'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('old', type=Path)
    parser.add_argument('new', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    result = compare(args.old.resolve(strict=True), args.new.resolve(strict=True))
    with args.output.open('x', encoding='utf-8', newline='\n') as f:
        json.dump(result, f, ensure_ascii=False, indent=2)
        f.write('\n')
    print('Compared', len(result['objects']), 'matching object paths; result:', args.output)


if __name__ == '__main__':
    main()
