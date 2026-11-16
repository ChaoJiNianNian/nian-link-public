#!/usr/bin/env python3
"""Read-only comparison of six historical Qt files with a pinned official archive."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

ARCHIVE_SHA256 = "41688269fac0565db956c66d9eecae777d16197e0c02cd81b88640c1f5d73d3f"
SPDX_SHA256 = "73b4dee169c8f3a1c0c2e2f904d0679cc156ce7e9320f7c866bb723dd4faac68"
SPDX = "sbom/qtbase-6.8.3.spdx.json"
MEMBERS = {
    "Qt6Core.dll": "bin/Qt6Core.dll",
    "Qt6Gui.dll": "bin/Qt6Gui.dll",
    "Qt6Network.dll": "bin/Qt6Network.dll",
    "Qt6Widgets.dll": "bin/Qt6Widgets.dll",
    "platforms/qoffscreen.dll": "plugins/platforms/qoffscreen.dll",
    "platforms/qwindows.dll": "plugins/platforms/qwindows.dll",
}

def file_sha256(path):
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()

def extract_member(archive, member, executable):
    # `tar` prints one fixed known member to stdout; it never extracts to disk.
    return subprocess.check_output([executable, "-xOf", str(archive), member], stderr=subprocess.PIPE, timeout=120)

def compare(archive, keyboard, executable):
    archive, keyboard = Path(archive), Path(keyboard)
    if file_sha256(archive) != ARCHIVE_SHA256:
        raise ValueError("archive SHA-256 does not match fixed official Qt package")
    spdx = extract_member(archive, SPDX, executable)
    if hashlib.sha256(spdx).hexdigest() != SPDX_SHA256:
        raise ValueError("SPDX SHA-256 mismatch")
    raw = json.loads(spdx)
    entries = {}
    for item in raw.get("files", []):
        name = item.get("fileName", "").removeprefix("./")
        if name not in MEMBERS.values():
            continue
        hashes = [c.get("checksumValue", "") for c in item.get("checksums", []) if c.get("algorithm") == "SHA1"]
        if name in entries or len(hashes) != 1 or not re.fullmatch("[0-9a-f]{40}", hashes[0]):
            raise ValueError("SPDX duplicate entry or invalid checksum")
        entries[name] = hashes[0]
    if set(entries) != set(MEMBERS.values()):
        raise ValueError("SPDX does not contain all six unique entries")
    records = []
    for relative, member in MEMBERS.items():
        official = extract_member(archive, member, executable)
        historical = (keyboard / relative).read_bytes()
        actual_sha1 = hashlib.sha1(official).hexdigest()
        records.append({"member": member, "byte_equal": historical == official,
                        "sha256": hashlib.sha256(official).hexdigest(),
                        "official_sha1": actual_sha1, "sbom_sha1": entries[member],
                        "matches_sbom_sha1": actual_sha1 == entries[member]})
    return {"archive_sha256": ARCHIVE_SHA256, "spdx_sha256": SPDX_SHA256,
            "all_equal": all(x["byte_equal"] for x in records),
            "all_match_sbom_sha1": all(x["matches_sbom_sha1"] for x in records), "files": records}

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path, help="Official QtBase 6.8.3 MSVC x64 .7z archive")
    parser.add_argument("keyboard", type=Path, help="Read-only historical Keyboard directory")
    parser.add_argument("--tar", default="tar", help="Tar executable that can read 7z archives")
    args = parser.parse_args(argv)
    try:
        result = compare(args.archive, args.keyboard, args.tar)
    except (OSError, ValueError, subprocess.SubprocessError):
        print("Comparison failed: unreadable input, invalid archive, or extraction failure.", file=sys.stderr)
        return 2
    print(json.dumps(result, indent=2))
    # SPDX differences are reported separately; equality is not a license conclusion.
    return 0 if result["all_equal"] else 1

if __name__ == "__main__":
    raise SystemExit(main())
