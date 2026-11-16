#!/usr/bin/env python3
"""Explicitly download pinned upstream source archives into a new isolated directory."""
from dataclasses import dataclass
import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import stat
import sys
import urllib.parse
import urllib.request
import uuid

class FetchError(Exception):
    pass

@dataclass(frozen=True)
class Source:
    name: str
    url: str
    sha256: str
    size: int

CATALOG = (
    Source("deskflow-v1.27.0-source.zip", "https://codeload.github.com/deskflow/deskflow/zip/081f6478e654a112168879e67a41ce7d7e4ae3af", "8493dfa58dbeac161a8b25d5b3cb7cb8546b5f4d99a216fcd65296b220a864f0", 1813623),
    Source("qtbase-everywhere-src-6.8.3.tar.xz", "https://download.qt.io/archive/qt/6.8/6.8.3/submodules/qtbase-everywhere-src-6.8.3.tar.xz", "56001b905601bb9023d399f3ba780d7fa940f3e4861e496a7c490331f49e0b80", 48426536),
    Source("qttools-everywhere-src-6.8.3.tar.xz", "https://download.qt.io/archive/qt/6.8/6.8.3/submodules/qttools-everywhere-src-6.8.3.tar.xz", "02a4e219248b94f1333df843d25763f35251c1074cdc4fb5bda67d340f8c8b3a", 10301984),
    Source("openssl-3.5.9.tar.gz", "https://github.com/openssl/openssl/releases/download/openssl-3.5.9/openssl-3.5.9.tar.gz", "603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a", 53279637),
)
ALLOWED_HOSTS = frozenset({"codeload.github.com", "download.qt.io", "qt.mirror.constant.com", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"})

def validate_url(url):
    parsed = urllib.parse.urlsplit(url)
    try: port = parsed.port
    except ValueError as exc: raise FetchError("Invalid URL port") from exc
    if (parsed.scheme != "https" or parsed.hostname not in ALLOWED_HOSTS or
        parsed.username is not None or parsed.password is not None or port not in (None, 443)):
        raise FetchError("Untrusted download or redirect destination")

class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        validate_url(newurl)
        return super().redirect_request(req, fp, code, msg, headers, newurl)

def _check_path(path):
    """Reject symlinks and Windows junctions/reparse points on every existing component."""
    path = Path(os.path.abspath(path))
    for part in reversed((path, *path.parents)):
        try: info = os.lstat(part)
        except FileNotFoundError: continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
            raise FetchError("Symlink or reparse point in destination path")

def _mkdir_parent(path):
    _check_path(path)
    try: path.mkdir(exist_ok=True)
    except OSError as exc: raise FetchError("Cannot prepare isolated parent directory") from exc
    _check_path(path)
    if not path.is_dir(): raise FetchError("Isolated parent is not a directory")

def collect(root, output, *, entries=None, opener=None):
    root, output = Path(os.path.abspath(root)), Path(os.path.abspath(output))
    allowed = root / ".artifacts" / "upstream-sources"
    if output.parent != allowed or not re.fullmatch(r"source-[0-9a-f]{32}", output.name):
        raise FetchError("Destination must be a new source-<32 lowercase hex> directory under .artifacts/upstream-sources")
    chosen = CATALOG if entries is None else tuple(entries)
    if not chosen or len({x.name for x in chosen}) != len(chosen): raise FetchError("Invalid source catalog")
    for item in chosen:
        validate_url(item.url)
        if (not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", item.name) or
            item.name == "manifest.json" or not re.fullmatch(r"[0-9a-f]{64}", item.sha256) or
            not isinstance(item.size, int) or item.size <= 0):
            raise FetchError("Invalid source catalog entry")
    _check_path(root)
    if not root.is_dir(): raise FetchError("Project root is not a directory")
    _mkdir_parent(root / ".artifacts")
    _mkdir_parent(allowed)
    _check_path(output)
    try: output.mkdir()
    except OSError as exc: raise FetchError("Destination already exists or cannot be created") from exc
    pending = output / ".pending"
    try: pending.mkdir()
    except OSError as exc: raise FetchError("Cannot create isolated staging directory") from exc
    open_url = opener or urllib.request.build_opener(SafeRedirect()).open
    manifest = []
    try:
        for item in chosen:
            _check_path(pending / item.name)
            request = urllib.request.Request(item.url, headers={"User-Agent": "nian-link-source-fetch/1", "Accept-Encoding": "identity"})
            with open_url(request, timeout=60) as response:
                validate_url(response.geturl())
                declared = response.headers.get("Content-Length")
                if declared is not None and (not declared.isdecimal() or int(declared) != item.size):
                    raise FetchError("Unexpected Content-Length for " + item.name)
                total, digest = 0, hashlib.sha256()
                with (pending / item.name).open("xb") as target:
                    while True:
                        chunk = response.read(min(1024 * 1024, item.size + 1 - total))
                        if not chunk: break
                        total += len(chunk)
                        if total > item.size: raise FetchError("Source exceeds pinned size: " + item.name)
                        digest.update(chunk)
                        target.write(chunk)
                    target.flush()
                    os.fsync(target.fileno())
            if total != item.size or digest.hexdigest() != item.sha256:
                raise FetchError("Size or SHA-256 mismatch: " + item.name)
            manifest.append({"name": item.name, "url": item.url, "sha256": item.sha256, "size": item.size})
        # Claim the final directory exclusively. Never rename over an existing
        # directory: POSIX rename may replace an existing empty directory.
        _check_path(output)
        verified = output / "verified"
        try: verified.mkdir()
        except OSError as exc: raise FetchError("Verified destination already exists or cannot be created") from exc
        _check_path(verified)
        for item in chosen:
            source, destination = pending / item.name, verified / item.name
            _check_path(source)
            _check_path(destination)
            digest, total = hashlib.sha256(), 0
            with source.open("rb") as src, destination.open("xb") as dst:
                while True:
                    chunk = src.read(1024 * 1024)
                    if not chunk: break
                    total += len(chunk)
                    if total > item.size: raise FetchError("Staged file changed: " + item.name)
                    digest.update(chunk)
                    dst.write(chunk)
                dst.flush()
                os.fsync(dst.fileno())
            if total != item.size or digest.hexdigest() != item.sha256:
                raise FetchError("Staged file changed: " + item.name)
        _check_path(verified / "manifest.json")
        # The manifest is the completion marker and is written last.
        with (verified / "manifest.json").open("x", encoding="utf-8", newline="\n") as target:
            json.dump({"complete": True, "sources": manifest}, target, indent=2, ensure_ascii=False)
            target.write("\n")
            target.flush()
            os.fsync(target.fileno())
        return verified
    except FetchError:
        raise
    except (OSError, ValueError, http.client.HTTPException, urllib.error.URLError) as exc:
        raise FetchError("Download failed; no verified bundle was published") from exc

def main():
    parser = argparse.ArgumentParser(description="Download pinned upstream source archives into a new isolated project directory.")
    parser.add_argument("--output", help="New .artifacts/upstream-sources/source-<32 lowercase hex> directory")
    args = parser.parse_args()
    project_root = Path(__file__).resolve().parents[1]
    output = Path(args.output) if args.output else project_root / ".artifacts" / "upstream-sources" / ("source-" + uuid.uuid4().hex)
    try: result = collect(project_root, output)
    except FetchError as exc:
        print("Source fetch failed:", exc, file=sys.stderr)
        return 1
    print("Verified sources:", result)
    return 0

if __name__ == "__main__": raise SystemExit(main())
