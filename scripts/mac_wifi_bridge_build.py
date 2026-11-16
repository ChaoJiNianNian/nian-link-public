"""Build and ad-hoc sign an isolated macOS Wi-Fi evidence bridge.

Only synthetic C/Objective-C tests are run; this tool does not query network
interfaces or launch the opt-in bridge smoke probe. This is not a release build.
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
    "src/NianLink.MacDiscoveryNative/WifiEvidenceCore.h",
    "src/NianLink.MacDiscoveryNative/WifiEvidenceCore.c",
    "src/NianLink.MacDiscoveryNative/WifiEvidenceBridge.h",
    "src/NianLink.MacDiscoveryNative/WifiEvidenceBridge.m",
    "tests/MacWifiBridge/WifiEvidenceCoreTests.c",
    "tests/MacWifiBridge/WifiEvidenceBridgeNameTests.m",
    "tests/MacWifiBridge/WifiEvidenceBridgeSmoke.c",
)


def _read(path: Path) -> bytes:
    if path.is_symlink() or not path.is_file():
        raise ValueError("Missing or linked source/output: " + path.name)
    return path.read_bytes()


def build(root: Path, output: Path, *, run=subprocess.run, system=None, machine=None):
    if (system if system is not None else platform.system()) != "Darwin" or (machine if machine is not None else platform.machine()) != "arm64":
        raise ValueError("Requires an Apple Silicon Mac with Xcode command-line tools")
    root = Path(root).absolute()
    output = Path(output).absolute()
    build_root = root / ".artifacts/mac-discovery-native"
    if output.parent != build_root or not re.fullmatch(r"test-[0-9a-f]{32}", output.name):
        raise ValueError("Output must be a new test-<32 lowercase hex> directory in .artifacts/mac-discovery-native")
    for directory in (root, root / ".artifacts", build_root):
        if directory.is_symlink(): raise ValueError("Linked build path refused")
        if directory != root: directory.mkdir(exist_ok=True)
        if not directory.is_dir(): raise ValueError("Missing project directory")
    for relative in SOURCES:
        current = root
        for part in Path(relative).parts[:-1]:
            current = current / part
            if current.is_symlink(): raise ValueError("Linked source path refused")
    snapshot = {name: _read(root / name) for name in SOURCES}
    try: output.mkdir()
    except FileExistsError as exc: raise ValueError("Output already exists") from exc
    frozen = output / "sources"; frozen.mkdir()
    for name, data in snapshot.items():
        with (frozen / Path(name).name).open("xb") as stream: stream.write(data)
    env = os.environ.copy(); env["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin"
    def call(args):
        return run(args, check=True, capture_output=True, text=True, env=env, cwd=str(frozen))
    compiler = ["/usr/bin/xcrun", "clang"]
    version = call(compiler + ["--version"]).stdout.strip()
    if not version: raise ValueError("Clang version was not reported")
    clang = compiler + ["-target", "arm64-apple-macosx14.0"]
    warnings = ["-std=c11", "-Wall", "-Wextra", "-Werror"]
    core = str(output / "WifiEvidenceCore.o")
    core_tests = str(output / "WifiEvidenceCoreTests")
    name_obj = str(output / "WifiEvidenceBridgeNameTests.o")
    name_tests = str(output / "WifiEvidenceBridgeNameTests")
    bridge_obj = str(output / "WifiEvidenceBridge.o")
    bridge = output / "libNianWifiEvidence.dylib"
    frameworks = ["-framework", "Foundation", "-framework", "CoreWLAN", "-framework", "SystemConfiguration"]
    call(clang + warnings + ["-fvisibility=hidden", "-c", "WifiEvidenceCore.c", "-o", core])
    call(clang + warnings + ["WifiEvidenceCoreTests.c", core, "-o", core_tests])
    call([core_tests])
    call(clang + warnings + ["-fobjc-arc", "-fvisibility=hidden", "-c", "WifiEvidenceBridgeNameTests.m", "-o", name_obj])
    call(clang + [name_obj, core] + frameworks + ["-o", name_tests])
    call([name_tests])
    call(clang + warnings + ["-fobjc-arc", "-fvisibility=hidden", "-c", "WifiEvidenceBridge.m", "-o", bridge_obj])
    call(clang + ["-dynamiclib", bridge_obj, core] + frameworks + ["-Wl,-exported_symbol,_nian_wifi_evidence_v1", "-install_name", "@rpath/libNianWifiEvidence.dylib", "-o", str(bridge)])
    call(["/usr/bin/codesign", "--force", "--sign", "-", "--timestamp=none", str(bridge)])
    call(["/usr/bin/codesign", "--verify", "--strict", str(bridge)])
    symbols = call(["/usr/bin/nm", "-gU", str(bridge)]).stdout.splitlines()
    names = [line.split()[-1] for line in symbols if line.strip()]
    if names != ["_nian_wifi_evidence_v1"]: raise ValueError("Unexpected exported symbol list")
    binary = _read(bridge)
    if len(binary) < 32 or struct.unpack("<IIII", binary[:16])[:2] != (0xFEEDFACF, 0x0100000C) or struct.unpack("<I", binary[12:16])[0] != 6:
        raise ValueError("Bridge is not an arm64 Mach-O dynamic library")
    if any(_read(root / name) != data or _read(frozen / Path(name).name) != data for name, data in snapshot.items()):
        raise ValueError("Source files changed during the build")
    evidence = {"schema": "mac-wifi-bridge-build-v1", "label": output.name,
                "builtAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(), "clangVersion": version,
                "signing": "ad-hoc", "networkObservation": False,
                "syntheticTests": ["WifiEvidenceCoreTests", "WifiEvidenceBridgeNameTests"],
                "sources": [{"path": name, "sha256": hashlib.sha256(snapshot[name]).hexdigest()} for name in SOURCES],
                "bridgeSha256": hashlib.sha256(binary).hexdigest(), "exportedSymbols": names}
    with (output / "wifi-build-evidence.json").open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(evidence, stream, ensure_ascii=False, indent=2); stream.write("\n")
    return evidence


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    output = args.output or ROOT / ".artifacts/mac-discovery-native" / ("test-" + uuid.uuid4().hex)
    try: evidence = build(ROOT, output)
    except (OSError, ValueError, subprocess.CalledProcessError) as exc:
        detail = exc.stderr if isinstance(exc, subprocess.CalledProcessError) and exc.stderr else str(exc)
        parser.exit(1, "Bridge build failed; no success evidence was written. " + detail[-4000:] + "\n")
    print("Isolated bridge directory:", output)
    print("Signed bridge SHA-256:", evidence["bridgeSha256"])
    print("Synthetic tests only; no Wi-Fi/network observation; ad-hoc signature only.")


if __name__ == "__main__": main()
