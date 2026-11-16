"""QtTools exact upstream notice and source-candidate integrity regression."""
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("candidate_qttools", ROOT / "scripts/public_source_candidate.py")
candidate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(candidate)

class QtToolsNoticesTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.paths = candidate.collect(ROOT)
        cls.contents = [(name, candidate.payload(ROOT, name)) for name in cls.paths]

    def test_fixed_inventory_is_collected_and_required(self):
        self.assertEqual(len(candidate.QTTOOLS_NOTICE_PATHS), 25)
        for name in candidate.QTTOOLS_NOTICE_PATHS | {candidate.QTTOOLS_MANIFEST, candidate.QTTOOLS_README}:
            with self.subTest(name=name):
                self.assertIn(name, candidate.REQUIRED_FILES)
                self.assertIn(name, self.paths)
                with self.assertRaises(ValueError):
                    candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))
        candidate.validate_qttools_notice_contents(self.contents)

    def test_each_original_notice_modification_or_removal_is_rejected(self):
        for target in candidate.QTTOOLS_NOTICE_PATHS:
            for mode in ("modify", "remove"):
                with self.subTest(target=target, mode=mode):
                    data = [(n, b + b"changed" if n == target else b) for n, b in self.contents if not (mode == "remove" and n == target)]
                    with self.assertRaises(ValueError):
                        candidate.validate_qttools_notice_contents(data)

    def test_manifest_mutation_or_removal_rejected_even_with_required_set_shrunk(self):
        for mode in ("modify", "remove"):
            data = [(n, b + b" " if n == candidate.QTTOOLS_MANIFEST else b) for n, b in self.contents if not (mode == "remove" and n == candidate.QTTOOLS_MANIFEST)]
            with self.subTest(mode=mode), patch.object(candidate, "REQUIRED_FILES", set()):
                with self.assertRaises(ValueError):
                    candidate.validate_qttools_notice_contents(data)

    def test_missing_or_duplicate_manifest_record_rejected_even_when_hash_re_pinned(self):
        raw = dict(self.contents)[candidate.QTTOOLS_MANIFEST]
        for mode in ("missing", "duplicate"):
            obj = json.loads(raw)
            if mode == "missing": obj["records"].pop()
            else: obj["records"].append(obj["records"][0])
            altered = json.dumps(obj).encode()
            data = [(n, altered if n == candidate.QTTOOLS_MANIFEST else b) for n, b in self.contents]
            with self.subTest(mode=mode), patch.object(candidate, "QTTOOLS_MANIFEST_SHA256", hashlib.sha256(altered).hexdigest()):
                with self.assertRaises(ValueError):
                    candidate.validate_qttools_notice_contents(data)

    def test_exact_license_allowlist_does_not_allow_arbitrary_extensionless(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            p = root / "vendor/notices/random/LICENSE"
            p.parent.mkdir(parents=True)
            p.write_bytes(b"test")
            with self.assertRaises(ValueError):
                candidate.payload(root, "vendor/notices/random/LICENSE")

    def test_cli_dry_run_and_archive_reject_tampered_qttools_notice(self):
        target = next(iter(candidate.QTTOOLS_NOTICE_PATHS))
        original = candidate.payload
        def mutated(root, path):
            data = original(root, path)
            return data + b"bad" if path == target else data
        output = io.StringIO()
        with patch.object(candidate, "payload", mutated), patch("sys.argv", ["collector"]), contextlib.redirect_stdout(output):
            with self.assertRaises(ValueError): candidate.main()
        self.assertEqual(output.getvalue(), "")
        with tempfile.TemporaryDirectory() as temp, patch.object(candidate, "payload", mutated):
            out = Path(temp) / "candidate.zip"
            with self.assertRaises(ValueError): candidate.write_archive(ROOT, out)
            self.assertFalse(out.exists())

if __name__ == "__main__": unittest.main()
