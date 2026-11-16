#!/usr/bin/env python3
"""Compare PE64 files after normalizing only two validated timestamp fields.

Outputs hashes and counts only; never runs, modifies, or writes the input files.
The strict parser intentionally rejects unsupported or ambiguous layouts.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys

MAX_BYTES = 64 * 1024 * 1024

def _u16(data, offset):
    return struct.unpack_from("<H", data, offset)[0]

def _u32(data, offset):
    return struct.unpack_from("<I", data, offset)[0]

def parse(data):
    if not 64 <= len(data) <= MAX_BYTES or data[:2] != b"MZ":
        raise ValueError("invalid or unsupported DOS header")
    try:
        pe = _u32(data, 0x3c)
        if pe < 64 or pe + 24 > len(data) or data[pe:pe+4] != b"PE\0\0":
            raise ValueError("invalid PE header")
        if _u16(data, pe+4) != 0x8664:
            raise ValueError("only x64 is supported")
        count, optional_size = _u16(data, pe+6), _u16(data, pe+20)
        if not 1 <= count <= 96 or optional_size < 168:
            raise ValueError("invalid section or optional-header count")
        opt = pe + 24
        if opt + optional_size + 40 * count > len(data) or _u16(data, opt) != 0x20b:
            raise ValueError("only complete PE32+ headers are supported")
        if _u32(data,opt+108) < 7:
            raise ValueError("missing debug directory")
        rva, size = struct.unpack_from("<II",data,opt+112+6*8)
        if size != 28 or not rva:
            raise ValueError("requires exactly one debug directory entry")
        section_table=opt+optional_size
        mappings=[]; raw_ranges=[]; virtual_ranges=[]
        for i in range(count):
            h=section_table+40*i
            virtual_size, address, raw_size, raw_offset=struct.unpack_from("<IIII",data,h+8)
            if raw_offset+raw_size>len(data) or address+max(virtual_size,raw_size)>0x100000000:
                raise ValueError("section extends past end of file or address space")
            if raw_size:
                if raw_offset < section_table+40*count:
                    raise ValueError("section overlaps headers")
                raw_ranges.append((raw_offset,raw_offset+raw_size))
            if max(virtual_size,raw_size):
                virtual_ranges.append((address,address+max(virtual_size,raw_size)))
            if address<=rva and rva+size<=address+raw_size:
                mappings.append(raw_offset+rva-address)
        for ranges in (raw_ranges,virtual_ranges):
            ranges.sort()
            if any(left[1]>right[0] for left,right in zip(ranges,ranges[1:])):
                raise ValueError("overlapping sections")
        if len(mappings)!=1 or mappings[0] < section_table+40*count:
            raise ValueError("debug directory has no unique section mapping")
        debug=mappings[0]
        debug_type,payload_size,payload_ptr=_u32(data,debug+12),_u32(data,debug+16),_u32(data,debug+24)
        if not debug_type or (payload_size and (not payload_ptr or payload_ptr+payload_size>len(data))):
            raise ValueError("invalid debug entry payload")
        header_stamp, debug_stamp = pe+8, debug+4
        if _u32(data,header_stamp)!=_u32(data,debug_stamp):
            raise ValueError("PE header and debug timestamps do not agree")
        return header_stamp,debug_stamp
    except (struct.error, OverflowError, IndexError) as exc:
        raise ValueError("truncated or malformed PE") from exc

def compare(first, second):
    first_offsets, second_offsets=parse(first),parse(second)
    a,b=bytearray(first),bytearray(second)
    for buf, offsets in ((a,first_offsets),(b,second_offsets)):
        for pos in offsets:
            buf[pos:pos+4]=bytes(4)
    def digest(value): return hashlib.sha256(value).hexdigest()
    return {"schema":"deskflow-pe-timestamp-comparison-v1",
            "scope":"Only x64 PE32+ with exactly one debug entry; not source, runtime or license proof.",
            "lengths":[len(first),len(second)],
            "raw_sha256":[digest(first),digest(second)],"raw_equal":first==second,
            "timestamp_offsets":[list(first_offsets),list(second_offsets)],
            "different_byte_count":sum(x!=y for x,y in zip(first,second))+abs(len(first)-len(second)),
            "normalized_sha256":[digest(a),digest(b)],"normalized_equal":a==b}

def main(argv=None):
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("first",type=Path); parser.add_argument("second",type=Path)
    args=parser.parse_args(argv)
    try:
        if any(p.stat().st_size > MAX_BYTES for p in (args.first,args.second)):
            raise ValueError("file exceeds size limit")
        result=compare(args.first.read_bytes(),args.second.read_bytes())
    except (ValueError,OSError) as exc:
        print("Comparison failed: invalid, unsupported or unreadable input.",file=sys.stderr)
        return 2
    print(json.dumps(result,ensure_ascii=False,indent=2))
    return 0 if result["normalized_equal"] else 1

if __name__=="__main__":
    sys.exit(main())
