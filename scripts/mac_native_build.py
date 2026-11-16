"""Build macOS arm64 native helpers in a new isolated project directory.

Produces hash evidence for build-mac-test.ps1, not proof of release readiness or
binary equivalence to any earlier build. Run on an Apple Silicon Mac only.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import struct
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]
SOURCES = (
    "DeskLinkMedia.swift", "RealtimeCapture.swift", "RealtimeCaptureOutput.swift",
    "build-media.sh", "media-sources.json", "DeskLinkWeb.swift", "build-web.sh",
)
BINARIES = {"DeskLinkMedia": 2, "libDeskLinkWeb.dylib": 6}


def _hash(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def _read_regular(path: Path) -> bytes:
    if path.is_symlink() or not path.is_file():
        raise ValueError("Missing or linked file: " + path.name)
    return path.read_bytes()


def _assert_macho(data: bytes, kind: int) -> None:
    if len(data) < 32:
        raise ValueError("Native output is too short")
    magic, cpu, _subtype, filetype = struct.unpack("<IIII", data[:16])
    if (magic, cpu, filetype) != (0xFEEDFACF, 0x0100000C, kind):
        raise ValueError("Native output is not an arm64 Mach-O of the expected kind")


def build(root: Path, output: Path, *, run=subprocess.run, system=None, machine=None):
    if (system if system is not None else platform.system()) != "Darwin" or (machine if machine is not None else platform.machine()) != "arm64":
        raise ValueError("Requires an Apple Silicon Mac with Xcode command-line tools")
    root = Path(root).absolute()
    output = Path(output).absolute()
    native_root = root / ".artifacts" / "mac-native"
    if output.parent != native_root or not re.fullmatch(r"native-[0-9a-f]{32}", output.name):
        raise ValueError("Output must be a new native-<32 lowercase hex> directory in .artifacts/mac-native")
    for directory in (root, root / ".artifacts", native_root):
        if directory.is_symlink():
            raise ValueError("Linked build path refused")
        if directory != root:
            directory.mkdir(exist_ok=True)
        if not directory.is_dir():
            raise ValueError("Missing project directory")
    source = root / "src/DesktopToolbox.Mac/Native"
    for directory in (root / "src", root / "src/DesktopToolbox.Mac", source):
        if directory.is_symlink() or not directory.is_dir():
            raise ValueError("Missing or linked source directory")
    snapshot = {name: _read_regular(source / name) for name in SOURCES}
    # Exclusive directory creation prevents overwriting an existing test or app.
    try:
        output.mkdir()
    except FileExistsError as exc:
        raise ValueError("Output already exists") from exc
    frozen = output / "sources"
    frozen.mkdir()
    for name, data in snapshot.items():
        with (frozen / name).open("xb") as stream:
            stream.write(data)
    env = os.environ.copy()
    env["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin"
    version = run(["/usr/bin/xcrun", "swiftc", "--version"], check=True,
                  capture_output=True, text=True, env=env).stdout.strip()
    if not version:
        raise ValueError("Swift version was not reported")
    for script, name in (("build-media.sh", "DeskLinkMedia"), ("build-web.sh", "libDeskLinkWeb.dylib")):
        run(["/bin/sh", str(frozen / script), str(output / name)], check=True,
            capture_output=True, text=True, env=env)
    binaries = []
    for name, kind in BINARIES.items():
        data = _read_regular(output / name)
        _assert_macho(data, kind)
        binaries.append({"name": name, "sha256": _hash(data), "architecture": "arm64"})
    if any(_read_regular(source / name) != data or _read_regular(frozen / name) != data
           for name, data in snapshot.items()):
        raise ValueError("Source files changed during the build")
    evidence = {
        "schema": "mac-native-build-v1", "label": output.name,
        "swiftVersion": version, "builtAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "sourceFiles": [{"name": name, "sha256": _hash(snapshot[name])} for name in SOURCES],
        "binaries": binaries,
    }
    with (output / "build-evidence.json").open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(evidence, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    return evidence


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="New native-<32 lowercase hex> folder under .artifacts/mac-native")
    args = parser.parse_args()
    output = args.output or ROOT / ".artifacts/mac-native" / ("native-" + uuid.uuid4().hex)
    try:
        build(ROOT, output)
    except (OSError, ValueError, subprocess.CalledProcessError) as exc:
        detail = exc.stderr if isinstance(exc, subprocess.CalledProcessError) and exc.stderr else str(exc)
        parser.exit(1, "Native build failed; no success evidence was written. " + detail[-4000:] + "\n")
    print("Isolated native build and evidence:", output)
    print("Internal build only; signing, notarization and release verification remain separate.")


if __name__ == "__main__":
    main()
