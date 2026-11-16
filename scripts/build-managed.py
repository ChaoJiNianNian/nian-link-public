#!/usr/bin/env python3
"""Build managed source and optional tests, without preparing a release package.

Requires Python 3.9+ and a .NET 10 SDK. All commands run in this checkout. This
is intentionally independent of the developer's other workspaces and caches.
Do not run builds concurrently in the same checkout.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import re
import shlex
import subprocess
import sys


SHARED_TESTS = (
    "tests/DeskLink.Tests/DeskLink.Tests.csproj",
    "tests/StandaloneSettings.Tests/StandaloneSettings.Tests.csproj",
    "tests/PortOwnership.Tests/PortOwnership.Tests.csproj",
    "tests/PairingExport.Tests/PairingExport.Tests.csproj",
    "tests/LanAddress.Tests/LanAddress.Tests.csproj",
    "tests/Shutdown.Tests/Shutdown.Tests.csproj",
)
WINDOWS_ONLY_TESTS = (
    "tests/MultiPeerTrust.Tests/MultiPeerTrust.Tests.csproj",
    "tests/ConditionalCommitIsolation.Tests/ConditionalCommitIsolation.Tests.csproj",
    "tests/NianLink.Smoke.Tests/NianLink.Smoke.Tests.csproj",
    "tests/MultiDevice.Windows.Tests/MultiDevice.Windows.Tests.csproj",
)


def create_commands(root: Path, dotnet: str, target: str, with_tests: bool,
                    packages: Path | None, *, mac_tests: bool = False, multi_device: bool = False) -> list[list[str]]:
    if target not in ("windows", "mac"):
        raise ValueError("Target must be windows or mac.")
    if mac_tests and target != "mac":
        raise ValueError("Mac-specific tests require --target mac.")
    common = ["--configuration", "Release", "--nologo"]
    cache = [f"-p:RestorePackagesPath={packages}"] if packages is not None else []
    if target == "windows":
        project = "src/NianLink.MultiDevice/NianLink.MultiDevice.csproj" if multi_device else "src/NianLink.Windows/NianLink.Windows.csproj"
        build = [dotnet, "build", str(root / project),
                 *common, "--runtime", "win-x64", "--self-contained", "false", *cache]
    else:
        build = [dotnet, "build", str(root / "src/DesktopToolbox.Mac/DesktopToolbox.Mac.csproj"),
                 *common, "--runtime", "osx-arm64", "--self-contained", "false",
                 f"-p:PreviewControlEngine={'false' if multi_device else 'true'}", "-p:PreviewControlCandidate=false",
                 "-p:NativeInputEngine=false", "-p:NativeInputCandidate=false", *cache]
        if multi_device:
            build.append("-p:MultiDeviceCandidate=true")
    commands = [build]
    projects = list(SHARED_TESTS) if with_tests else []
    if with_tests and target == "windows":
        projects.extend(WINDOWS_ONLY_TESTS)
    if mac_tests:
        projects.append("src/DesktopToolbox.Mac/Tests/DesktopToolbox.Mac.Tests.csproj")
    for project in projects:
        commands.append([dotnet, "test", str(root / project), *common, *cache])
    return commands


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Compile managed source; this is not an installer or a full release build.")
    parser.add_argument("--target", choices=("windows", "mac"), required=True)
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable or path (default: dotnet on PATH)")
    parser.add_argument("--packages", type=Path, help="optional NuGet package-cache directory")
    parser.add_argument("--with-tests", action="store_true", help="run shared tests and the Windows-only trust tests when targeting Windows")
    parser.add_argument("--with-mac-tests", action="store_true", help="also run the full Mac managed test project; intended for macOS")
    parser.add_argument("--multi-device", action="store_true", help="build the isolated multi-device candidate, not the normal application")
    parser.add_argument("--dry-run", action="store_true", help="print commands without executing them")
    args = parser.parse_args(argv)
    root = Path(__file__).resolve().parents[1]
    try:
        commands = create_commands(root, args.dotnet, args.target, args.with_tests, args.packages,
                                   mac_tests=args.with_mac_tests, multi_device=args.multi_device)
    except ValueError as error:
        parser.error(str(error))
    for command in commands:
        if not Path(command[2]).is_file():
            parser.error("Required source or test project is missing: " + command[2])
    print("Managed source only: Deskflow, native Mac helpers, installers, signatures and notarization are NOT built.", flush=True)
    if args.multi_device:
        print("Isolated multi-device candidate only; no existing installation is updated.", flush=True)
    if args.target == "mac":
        print("Mac managed build is incomplete without native components; it does not prove the Mac app runs.", flush=True)
    if args.with_mac_tests and sys.platform != "darwin":
        print("Mac-specific tests on this host are not macOS verification and may fail on platform permissions.", flush=True)
    print("Run builds serially in this checkout; do not use real user profiles for tests.", flush=True)
    if not args.dry_run:
        try:
            result = subprocess.run([args.dotnet, "--version"], cwd=root, text=True, capture_output=True, check=False)
        except FileNotFoundError:
            print("Cannot find dotnet. Install the .NET 10 SDK or pass --dotnet.", file=sys.stderr)
            return 1
        if result.returncode != 0 or not re.match(r"^10\.\d+\.\d+(?:[-+].*)?$", result.stdout.strip()):
            print("A .NET 10 SDK is required; dotnet --version did not report a 10.x SDK.", file=sys.stderr)
            return 1
        print("SDK:", result.stdout.strip(), flush=True)
    for command in commands:
        print("+", shlex.join(command), flush=True)
        if not args.dry_run:
            try:
                result = subprocess.run(command, cwd=root, check=False)
            except OSError as error:
                print(f"Could not execute dotnet: {error}", file=sys.stderr)
                return 1
            if result.returncode:
                print("Managed build/test failed; no release has been produced.", file=sys.stderr)
                return result.returncode if result.returncode > 0 else 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
