"""Preview bundle identity helpers extracted from pc-toolbox native integration.

The v2 digest format remains compatible with the source project's preview payload
verification. This module contains only the offline Mach-O/identity operations.
"""
import hashlib
import struct
import uuid


PREVIEW_UUID_SCHEME = 'preview-payload-v2'


def macho_uuid(data):
    if len(data) < 32:
        raise ValueError('Truncated Mach-O header.')
    magic, cpu, _, file_type, count, table_bytes, _, _ = struct.unpack_from('<8I', data)
    end = 32 + table_bytes
    if magic != 0xFEEDFACF or cpu != 0x0100000C or file_type != 2 or count == 0 or table_bytes < 8 or table_bytes % 8 or count > table_bytes // 8 or end > len(data):
        raise ValueError('The apphost must be a bounded ARM64 Mach-O executable.')
    offset, found = 32, None
    for _ in range(count):
        if end - offset < 8:
            raise ValueError('Truncated Mach-O load command.')
        command, size = struct.unpack_from('<2I', data, offset)
        if size < 8 or size % 8 or offset + size > end:
            raise ValueError('Invalid Mach-O load command size.')
        if command == 27:
            if size != 24 or found is not None:
                raise ValueError('The apphost requires exactly one LC_UUID.')
            found = offset + 8
        offset += size
    if offset != end or found is None:
        raise ValueError('Invalid Mach-O command table or missing LC_UUID.')
    return found, str(uuid.UUID(bytes=data[found:found + 16])).upper()


def preview_payload_sha256(rows, app_name, host_bytes):
    offset, _ = macho_uuid(host_bytes)
    normalized = bytearray(host_bytes); normalized[offset:offset + 16] = bytes(16)
    normalized_sha = hashlib.sha256(normalized).hexdigest().upper()
    prefix = app_name + '/'
    digest = hashlib.sha256((PREVIEW_UUID_SCHEME + '\n').encode('utf-8'))
    for name in sorted(rows, key=lambda value: value[len(prefix):].encode('utf-8')):
        row = rows[name]
        path = name[len(prefix):]
        sha = normalized_sha if path == 'Contents/MacOS/NianDeskLink' else row['sha256']
        digest.update((path + '\0' + row['unixMode'] + '\0' + str(row['bytes']) + '\0' + sha + '\n').encode('utf-8'))
    return digest.hexdigest().upper()


def derive_preview_executable_uuid(bundle_id, payload_sha):
    digest = bytearray(hashlib.sha256((PREVIEW_UUID_SCHEME + '\n' + bundle_id + '\n' + payload_sha).encode('utf-8')).digest()[:16])
    digest[6] = (digest[6] & 15) | 128; digest[8] = (digest[8] & 63) | 128
    return str(uuid.UUID(bytes=bytes(digest))).upper()
