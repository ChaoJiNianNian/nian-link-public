"""Exact MicroCom and Tmds upstream notices in the review candidate."""
import contextlib, hashlib, importlib.util, io, json, tempfile, unittest
from pathlib import Path
from unittest.mock import patch
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("candidate_managed", ROOT / "scripts/public_source_candidate.py")
candidate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(candidate)

class ManagedRuntimeNoticeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.paths = candidate.collect(ROOT)
        cls.contents = [(name, candidate.payload(ROOT, name)) for name in cls.paths]

    def test_required_files_and_exact_contents(self):
        self.assertEqual(len(candidate.MANAGED_RUNTIME_NOTICE_PATHS), 4)
        for name in candidate.MANAGED_RUNTIME_NOTICE_PATHS | {candidate.MANAGED_RUNTIME_MANIFEST, "vendor/notices/managed-runtime-publish-review.md"}:
            with self.subTest(name=name):
                self.assertIn(name, self.paths)
                self.assertIn(name, candidate.REQUIRED_FILES)
                with self.assertRaises(ValueError):
                    candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))
        candidate.validate_managed_runtime_notice_contents(self.contents)

    def test_tampered_or_missing_notices_rejected(self):
        for target in candidate.MANAGED_RUNTIME_NOTICE_PATHS | {candidate.MANAGED_RUNTIME_MANIFEST}:
            for mode in ("modify", "remove"):
                data = [(n, b+b"changed" if n==target else b) for n,b in self.contents if not (mode=="remove" and n==target)]
                with self.subTest(target=target,mode=mode), self.assertRaises(ValueError):
                    candidate.validate_managed_runtime_notice_contents(data)

    def test_manifest_missing_or_duplicate_entry_rejected_after_repin(self):
        raw = dict(self.contents)[candidate.MANAGED_RUNTIME_MANIFEST]
        for mode in ("missing", "duplicate"):
            doc = json.loads(raw)
            if mode=="missing": doc["records"][0]["notice_files"].pop()
            else: doc["records"][0]["notice_files"].append(doc["records"][0]["notice_files"][0])
            mutated=json.dumps(doc).encode()
            data=[(n,mutated if n==candidate.MANAGED_RUNTIME_MANIFEST else b) for n,b in self.contents]
            with self.subTest(mode=mode), patch.object(candidate,"MANAGED_RUNTIME_MANIFEST_SHA256",hashlib.sha256(mutated).hexdigest()), self.assertRaises(ValueError):
                candidate.validate_managed_runtime_notice_contents(data)

    def test_cli_and_archive_refuse_tampered_notices(self):
        target=next(iter(candidate.MANAGED_RUNTIME_NOTICE_PATHS)); original=candidate.payload
        def tampered(root,path):
            data=original(root,path)
            return data+b"bad" if path==target else data
        output=io.StringIO()
        with patch.object(candidate,"payload",tampered), patch("sys.argv",["collector"]), contextlib.redirect_stdout(output):
            with self.assertRaises(ValueError):candidate.main()
        self.assertEqual(output.getvalue(),"")
        with tempfile.TemporaryDirectory() as temp, patch.object(candidate,"payload",tampered):
            path=Path(temp)/"candidate.zip"
            with self.assertRaises(ValueError):candidate.write_archive(ROOT,path)
            self.assertFalse(path.exists())

if __name__ == "__main__": unittest.main()
