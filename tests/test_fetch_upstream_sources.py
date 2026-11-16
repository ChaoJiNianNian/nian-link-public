import hashlib
import http.client
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import sys

MODULE_PATH = Path(__file__).resolve().parents[1] / "scripts" / "fetch_upstream_sources.py"
spec = importlib.util.spec_from_file_location("upstream_fetch", MODULE_PATH)
fetch = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = fetch
spec.loader.exec_module(fetch)

class SocketBytes:
    def __init__(self, data): self.data = data
    def makefile(self, *args, **kwargs): return io.BytesIO(self.data)

class LocalResponse:
    def __init__(self, payload, url, declared=None):
        length = len(payload) if declared is None else declared
        wire = f"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\n\r\n".encode() + payload
        self.response = http.client.HTTPResponse(SocketBytes(wire))
        self.response.begin()
        self.headers = self.response.headers
        self.url = url
    def read(self, size=-1): return self.response.read(size)
    def geturl(self): return self.url
    def __enter__(self): return self
    def __exit__(self, *args): self.response.close()

class UpstreamFetchTests(unittest.TestCase):
    def test_catalog_includes_verified_qt_tools_source(self):
        matches = [item for item in fetch.CATALOG if item.name == "qttools-everywhere-src-6.8.3.tar.xz"]
        self.assertEqual(len(matches), 1)
        self.assertEqual(matches[0].size, 10301984)
        self.assertEqual(matches[0].sha256, "02a4e219248b94f1333df843d25763f35251c1074cdc4fb5bda67d340f8c8b3a")

    def test_qt_official_redirect_mirror_is_accepted_but_only_over_https(self):
        fetch.validate_url("https://qt.mirror.constant.com/archive/qt/6.8/6.8.3/submodules/qtbase-everywhere-src-6.8.3.tar.xz")
        with self.assertRaises(fetch.FetchError):
            fetch.validate_url("http://qt.mirror.constant.com/archive/qt/6.8/6.8.3/submodules/qtbase-everywhere-src-6.8.3.tar.xz")

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.output = self.root / ".artifacts" / "upstream-sources" / ("source-" + "a" * 32)
        self.payloads = [b"deskflow fake source", b"qt fake source", b"openssl fake source"]
        self.entries = tuple(fetch.Source(f"fixture-{i}.tar.gz", f"https://github.com/example/{i}", hashlib.sha256(p).hexdigest(), len(p)) for i, p in enumerate(self.payloads))
        self.calls = []
    def opener(self, request, timeout):
        index = int(request.full_url.rsplit("/", 1)[1])
        self.calls.append(index)
        return LocalResponse(self.payloads[index], request.full_url)
    def run_fetch(self, opener=None):
        return fetch.collect(self.root, self.output, entries=self.entries, opener=opener or self.opener)
    def test_synthetic_http_success_commits_all_verified_sources(self):
        verified = self.run_fetch()
        manifest = json.loads((verified / "manifest.json").read_text(encoding="utf-8"))
        self.assertTrue(manifest["complete"])
        self.assertEqual(self.calls, [0, 1, 2])
        for entry, payload in zip(self.entries, self.payloads):
            self.assertEqual((verified / entry.name).read_bytes(), payload)
        self.assertTrue((self.output / ".pending").exists())
    def test_wrong_hash_does_not_expose_any_source(self):
        self.payloads[1] = b"X" * len(self.payloads[1])
        with self.assertRaises(fetch.FetchError): self.run_fetch()
        self.assertFalse((self.output / "verified").exists())
        self.assertTrue((self.output / ".pending").exists())
    def test_network_failure_does_not_expose_any_source(self):
        def fails(request, timeout): raise OSError("offline")
        with self.assertRaises(fetch.FetchError): self.run_fetch(fails)
        self.assertFalse((self.output / "verified").exists())
    def test_existing_destination_is_refused_before_network(self):
        self.output.mkdir(parents=True)
        with self.assertRaises(fetch.FetchError): self.run_fetch()
        self.assertEqual(self.calls, [])
    def test_destination_outside_isolation_is_refused(self):
        self.output = self.root / "elsewhere" / ("source-" + "b" * 32)
        with self.assertRaises(fetch.FetchError): self.run_fetch()
        self.assertEqual(self.calls, [])
    def test_wrong_content_length_and_extra_content_fail(self):
        def wrong(request, timeout): return LocalResponse(b"excessive", request.full_url, 999)
        with self.assertRaises(fetch.FetchError): self.run_fetch(wrong)
        self.assertFalse((self.output / "verified").exists())
    def test_redirect_must_stay_https_and_on_approved_host(self):
        handler = fetch.SafeRedirect()
        for url in ("http://github.com/file", "https://evil.invalid/file", "https://github.com.evil.invalid/file"):
            with self.subTest(url=url), self.assertRaises(fetch.FetchError):
                handler.redirect_request(None, None, 302, "Found", {}, url)
    def test_competing_final_directory_is_not_overwritten(self):
        def compete(request, timeout):
            response = self.opener(request, timeout)
            if len(self.calls) == len(self.entries):
                occupied = self.output / "verified"
                occupied.mkdir()
                (occupied / "other-user-file.txt").write_bytes(b"keep")
            return response
        with self.assertRaises(fetch.FetchError): self.run_fetch(compete)
        self.assertEqual((self.output / "verified" / "other-user-file.txt").read_bytes(), b"keep")
        self.assertFalse((self.output / "verified" / "manifest.json").exists())
    def test_competing_file_is_not_overwritten_and_bundle_is_incomplete(self):
        original_open = Path.open
        target = self.output / "verified" / self.entries[1].name
        def competing_open(path, *args, **kwargs):
            if path == target and args and args[0] == "xb":
                with original_open(path, "wb") as out: out.write(b"keep")
            return original_open(path, *args, **kwargs)
        with mock.patch.object(Path, "open", competing_open):
            with self.assertRaises(fetch.FetchError): self.run_fetch()
        self.assertEqual(target.read_bytes(), b"keep")
        self.assertFalse((self.output / "verified" / "manifest.json").exists())
    def test_final_response_url_must_also_be_trusted(self):
        def unsafe(request, timeout): return LocalResponse(self.payloads[0], "https://evil.invalid/")
        with self.assertRaises(fetch.FetchError): self.run_fetch(unsafe)
        self.assertFalse((self.output / "verified").exists())
    def test_reparse_point_is_refused_before_network(self):
        source_dir = self.root / ".artifacts"
        source_dir.mkdir()
        real_lstat = fetch.os.lstat
        def fake_lstat(path, *args, **kwargs):
            result = real_lstat(path, *args, **kwargs)
            if Path(path) == source_dir:
                return mock.Mock(st_mode=result.st_mode, st_file_attributes=0x400)
            return result
        with mock.patch.object(fetch.os, "lstat", side_effect=fake_lstat):
            with self.assertRaises(fetch.FetchError): self.run_fetch()
        self.assertEqual(self.calls, [])

if __name__ == "__main__": unittest.main()
