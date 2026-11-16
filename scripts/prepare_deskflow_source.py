#!/usr/bin/env python3
"""Collect *recorded* Deskflow source materials without executing old builds.

The fixed catalog covers the locally recorded 0.1.9 Windows engine, not an
assertion that the resulting materials reproduce its binary or satisfy every
third-party distribution condition. This script does not access the network.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import stat
import uuid
import zipfile


# Paths are relative to the explicitly chosen archived build-material root.
# Only these exact files, hashes and destinations are collected by the CLI.
CATALOG = (
    ("third-party/deskflow/downloads/deskflow-v1.27.0-source.zip", "upstream/deskflow-v1.27.0-source.zip", "8493dfa58dbeac161a8b25d5b3cb7cb8546b5f4d99a216fcd65296b220a864f0"),
    ("third-party/deskflow/dependency-sources/qtbase-everywhere-src-6.8.3.tar.xz", "dependencies/qtbase-everywhere-src-6.8.3.tar.xz", "56001b905601bb9023d399f3ba780d7fa940f3e4861e496a7c490331f49e0b80"),
    ("third-party/deskflow/dependency-sources/openssl-3.5.9.tar.gz", "dependencies/openssl-3.5.9.tar.gz", "603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a"),
    ("third-party/deskflow/patches/0001-current-user-native-control.patch", "patches/0001-current-user-native-control.patch", "4553424fcf97b368c4356d647d5ed25b7e529daba1b00fe4cebd6a8f047e5324"),
    ("third-party/deskflow/patches/0002-preview-input-isolation.patch", "patches/0002-preview-input-isolation.patch", "0c0f7937ddf61311415806da3f2fa37c310b3e089a98a7b5933e0daa16f4ffce"),
    ("third-party/deskflow/patches/toolchain-qt-lock.json", "historical/toolchain-qt-lock.json", "0030beec987310713a9a2952d81470c0fc73bf0302ce05a245a54b9688064471"),
    ("third-party/deskflow/patches/toolchain-openssl-lock.json", "historical/toolchain-openssl-lock.json", "1ac76e41e003c4786000ddb175056a8b25e21241d9c45dcad11c4cd7357fe227"),
    ("third-party/deskflow/patches/toolchain-msvc-lock.json", "historical/toolchain-msvc-lock.json", "d6d77874ce4052de1f53de35c253955b995ffaa00d8138e126cd60b9f92ff2d9"),
    ("scripts/build-native-deskflow.ps1", "historical/build-native-deskflow.ps1", "5a747471119dc857689672f5615f6d994d111cd71334df0f57599c70d6a5cbae"),
    ("scripts/native-deskflow-env.ps1", "historical/native-deskflow-env.ps1", "f3716c1ef2dc30abfb16ea3d4e4dd18684439859be2a9082c210826c911a8b81"),
    ("scripts/BuildArtifactLock.psm1", "historical/BuildArtifactLock.psm1", "c1c1b5f3216709f3d152516d9f836dc5ce34bbb7bded54fff2f8ac5efbef2935"),
)

NOTICE = """Deskflow source materials for the recorded 0.1.9 Windows test core

Upstream: Deskflow 1.27.0, commit 081f6478e654a112168879e67a41ce7d7e4ae3af.
The recorded control patch is 0001. Patch 0002 is retained as a historical
supplement and is NOT asserted to be part of the shipped test core.
Upstream archives retain their own copyright, licence texts and exceptions.

Files under historical/ are unmodified build records, NOT a portable build
system. They contain environment-specific defaults and may alter files; do not
run them without reviewing and adapting their inputs and output paths. Hashes
only demonstrate agreement with recorded inputs; the engine has NOT been
independently rebuilt from this bundle and licence completeness has NOT been
verified. The Mac Deskflow binary in the separate Mac package needs its own
source/distribution review. See the project's third-party audit for limits.
"""

SOURCE_COMMIT = "081f6478e654a112168879e67a41ce7d7e4ae3af"
RECORDED_CORE = "6d06f16034edad54e6d51aad3df45e1e90e0c1d88b34489a69ff1cd5bd39b751"
APPLICATION_VERSION = "0.1.9-test"


def _relative(value):
    if not isinstance(value, str) or not value or "\\" in value or ":" in value or any(ord(c) < 32 for c in value):
        raise ValueError("Unsafe relative path")
    path = PurePosixPath(value)
    if path.is_absolute() or path.as_posix() != value or any(part in ("", ".", "..") or part.endswith((".", " ")) for part in path.parts):
        raise ValueError("Unsafe relative path")
    return path


def _no_links(path):
    current = Path(os.path.abspath(path))
    while True:
        try:
            item = current.lstat()
        except FileNotFoundError:
            item = None
        if item is not None and (stat.S_ISLNK(item.st_mode) or getattr(item, "st_file_attributes", 0) & 0x400):
            raise ValueError("Linked paths are not accepted")
        if current.parent == current:
            break
        current = current.parent


def _entries(source, entries):
    source = Path(os.path.abspath(source))
    _no_links(source)
    if not source.is_dir():
        raise FileNotFoundError("Source root does not exist")
    names = {"manifest.json", "readme.txt"}
    validated = []
    for rel, destination, digest in entries:
        rel = _relative(rel)
        destination = _relative(destination)
        name = destination.as_posix()
        if name.casefold() in names or len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
            raise ValueError("Duplicate destination or invalid digest")
        names.add(name.casefold())
        source_file = source.joinpath(*rel.parts)
        _no_links(source_file)
        if not source_file.is_file():
            raise FileNotFoundError(f"Source material missing: {rel}")
        validated.append((source_file, name, digest))
    if not validated:
        raise ValueError("Source catalog is empty")
    return source, validated


def _zip_info(name):
    info = zipfile.ZipInfo(name, (2020, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_STORED  # upstream archives are already compressed
    info.external_attr = (stat.S_IFREG | 0o644) << 16
    return info


def verify_bundle(path, entries=CATALOG):
    """Re-read every byte of a generated archive before it is handed over."""
    expected = {}
    for _, name, digest in entries:
        name = _relative(name).as_posix()
        if name.casefold() in {item.casefold() for item in expected}:
            raise ValueError("Duplicate destination")
        expected[name] = digest
    with zipfile.ZipFile(path) as archive:
        members = archive.infolist()
        names = [item.filename for item in members]
        if len(names) != len(set(names)) or set(names) != set(expected) | {"MANIFEST.json", "README.txt"}:
            raise ValueError("Archive members differ from the approved catalog")
        if any(item.is_dir() or item.flag_bits & 1 or item.compress_type != zipfile.ZIP_STORED for item in members):
            raise ValueError("Unexpected archive member type")
        if archive.getinfo("MANIFEST.json").file_size > 65536 or archive.getinfo("README.txt").file_size > 16384:
            raise ValueError("Archive metadata is too large")
        manifest = json.loads(archive.read("MANIFEST.json"))
        if (archive.read("README.txt").decode("utf-8") != NOTICE or manifest.get("schema") != 1
                or manifest.get("recordedApplicationVersion") != APPLICATION_VERSION
                or manifest.get("upstreamCommit") != SOURCE_COMMIT
                or manifest.get("recordedWindowsCoreSha256") != RECORDED_CORE
                or manifest.get("rebuildVerified") is not False or manifest.get("licenceComplianceVerified") is not False):
            raise ValueError("Archive notice or verification status differs")
        listed = manifest.get("files")
        if not isinstance(listed, list) or len(listed) != len(expected):
            raise ValueError("Archive manifest count differs")
        records = {item.get("path"): item for item in listed if isinstance(item, dict)}
        if set(records) != set(expected):
            raise ValueError("Archive manifest paths differ")
        for name, digest in expected.items():
            value = hashlib.sha256()
            count = 0
            with archive.open(name) as stream:
                while chunk := stream.read(1024 * 1024):
                    count += len(chunk)
                    value.update(chunk)
            if value.hexdigest() != digest or records[name].get("sha256") != digest or records[name].get("bytes") != count:
                raise ValueError("Archive file does not match: " + name)
    return manifest


def write_bundle(source, output, entries=CATALOG):
    source, validated = _entries(source, entries)
    output = Path(os.path.abspath(output))
    _no_links(output)
    if not output.parent.is_dir():
        raise FileNotFoundError("Output parent directory does not exist")
    if output == source or source in output.parents:
        raise ValueError("Output must not be inside the source root")
    if output.exists():
        raise FileExistsError("Output already exists; it will not be overwritten")
    temporary = output.with_name("." + output.name + "." + uuid.uuid4().hex + ".partial")
    result = []
    try:
        with open(temporary, "xb") as raw, zipfile.ZipFile(raw, "w", allowZip64=True) as archive:
            for source_file, destination, expected in validated:
                digest = hashlib.sha256()
                count = 0
                with source_file.open("rb") as incoming, archive.open(_zip_info(destination), "w", force_zip64=True) as outgoing:
                    while chunk := incoming.read(1024 * 1024):
                        digest.update(chunk)
                        count += len(chunk)
                        outgoing.write(chunk)
                if digest.hexdigest() != expected:
                    raise ValueError("Input hash mismatch: " + destination)
                result.append({"path": destination, "bytes": count, "sha256": expected})
            manifest = {"schema": 1, "recordedApplicationVersion": APPLICATION_VERSION, "upstreamCommit": SOURCE_COMMIT, "recordedWindowsCoreSha256": RECORDED_CORE, "rebuildVerified": False, "licenceComplianceVerified": False, "files": result}
            archive.writestr(_zip_info("MANIFEST.json"), json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")
            archive.writestr(_zip_info("README.txt"), NOTICE)
        verify_bundle(temporary, entries)
        # Link is atomic and fails if the destination appeared after validation.
        # Never replace an existing user's file. Both paths are on the same volume.
        os.link(temporary, output)
        temporary.unlink()
    except BaseException:
        # A uniquely named partial belongs to this invocation; retain it for
        # inspection instead of deleting anything on an uncertain failure.
        raise
    return manifest


def main():
    parser = argparse.ArgumentParser(description="Collect verified Deskflow source materials (preview by default).")
    parser.add_argument("--source-root", required=True, help="Root of the recorded build materials")
    parser.add_argument("--output", required=True, help="New ZIP path outside source root")
    parser.add_argument("--apply", action="store_true", help="Write the new candidate ZIP")
    args = parser.parse_args()
    _, entries = _entries(args.source_root, CATALOG)
    if not args.apply:
        print(json.dumps({"apply": False, "files": [{"path": name, "sha256": digest} for _, name, digest in entries]}, indent=2))
        return
    manifest = write_bundle(args.source_root, args.output)
    print(json.dumps({"created": True, "fileCount": len(manifest["files"]), "rebuildVerified": False, "licenceComplianceVerified": False}))


if __name__ == "__main__":
    main()
