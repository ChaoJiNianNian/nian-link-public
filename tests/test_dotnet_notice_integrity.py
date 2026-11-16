"""Pinned .NET runtime provenance and byte-preservation regression checks."""
import hashlib
import importlib.util
import json
import io
import contextlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("candidate_dotnet_test", ROOT / "scripts/public_source_candidate.py")
candidate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(candidate)


class DotnetNoticesTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.paths = candidate.collect(ROOT)
        cls.contents = [(name, candidate.payload(ROOT, name)) for name in cls.paths]

    def test_required_inventory_and_doc(self):
        doc = "docs/\u4e8c\u8fdb\u5236\u8f7d\u8377\u4f9d\u8d56\u4e0e\u8bb8\u53ef\u5ba1\u67e5.md"
        self.assertIn(doc, candidate.SINGLE_FILES)
        self.assertIn(doc, candidate.REQUIRED_FILES)
        self.assertEqual(len(candidate.DOTNET_NOTICE_PATHS), 21)
        self.assertEqual(len(candidate.DOTNET_MANIFESTS), 3)
        self.assertEqual(len(candidate.DOTNET_READMES), 3)
        for name in set(candidate.DOTNET_NOTICE_PATHS) | set(candidate.DOTNET_MANIFESTS) | candidate.DOTNET_READMES | {doc}:
            with self.subTest(name=name):
                self.assertIn(name, candidate.REQUIRED_FILES)
                self.assertIn(name, self.paths)
                with self.assertRaises(ValueError):
                    candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))
        candidate.validate_dotnet_notice_contents(self.contents)

    def test_each_original_license_modification_is_rejected(self):
        for target in candidate.DOTNET_NOTICE_PATHS:
            with self.subTest(target=target):
                changed = [(name, data + b"modified" if name == target else data) for name, data in self.contents]
                with self.assertRaises(ValueError):
                    candidate.validate_dotnet_notice_contents(changed)

    def test_each_manifest_mutation_or_removal_is_rejected_even_if_required_set_shrinks(self):
        for target in candidate.DOTNET_MANIFESTS:
            for mode in ("mutate", "remove"):
                with self.subTest(target=target, mode=mode):
                    changed = [(name, data + b" " if name == target else data) for name, data in self.contents if not (mode == "remove" and name == target)]
                    with patch.object(candidate, "REQUIRED_FILES", set()):
                        with self.assertRaises(ValueError):
                            candidate.validate_dotnet_notice_contents(changed)

    def test_incomplete_manifest_rejected_even_with_matching_manifest_hash(self):
        for target in candidate.DOTNET_MANIFESTS:
            with self.subTest(target=target):
                raw = dict(self.contents)[target]
                obj = json.loads(raw)
                obj["records"].pop()
                altered = json.dumps(obj).encode()
                changed = [(name, altered if name == target else data) for name, data in self.contents]
                pinned = dict(candidate.DOTNET_MANIFESTS)
                pinned[target] = hashlib.sha256(altered).hexdigest()
                with patch.object(candidate, "DOTNET_MANIFESTS", pinned):
                    with self.assertRaises(ValueError):
                        candidate.validate_dotnet_notice_contents(changed)

    def test_cli_dry_run_rejects_corrupted_license_before_printing_inventory(self):
        target = next(iter(candidate.DOTNET_NOTICE_PATHS))
        original = candidate.payload
        def mutated(root, path):
            data = original(root, path)
            return data + b"bad" if path == target else data
        output = io.StringIO()
        with patch.object(candidate, "payload", mutated), patch("sys.argv", ["collector"]), contextlib.redirect_stdout(output):
            with self.assertRaises(ValueError):
                candidate.main()
        self.assertEqual(output.getvalue(), "")

    def test_archive_does_not_create_output_on_corrupted_license(self):
        target = next(iter(candidate.DOTNET_NOTICE_PATHS))
        original = candidate.payload
        def mutated(root, path):
            data = original(root, path)
            return data + b"bad" if path == target else data
        with tempfile.TemporaryDirectory() as temp, patch.object(candidate, "payload", mutated):
            out = Path(temp) / "candidate.zip"
            with self.assertRaises(ValueError):
                candidate.write_archive(ROOT, out)
            self.assertFalse(out.exists())


if __name__ == "__main__":
    unittest.main()
