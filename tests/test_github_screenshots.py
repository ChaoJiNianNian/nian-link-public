"""Verify the explicitly reviewed screenshots included in source snapshots."""
import importlib.util
import posixpath
import re
from urllib.parse import unquote, urlsplit
import struct
import tempfile
import unittest
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('candidate', ROOT / 'scripts/public_source_candidate.py')
candidate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(candidate)

class ScreenshotCandidateTests(unittest.TestCase):
    def test_reviewed_screenshots_and_provenance_are_required(self):
        expected = {f'docs/screenshots/{name}.png' for name in (
            'windows-devices','windows-screen','windows-controlled','mac-devices',
            'mac-discovery','mac-screen','mac-keyboard','mac-files')}
        self.assertEqual(set(candidate.SCREENSHOT_HASHES), expected)
        paths = set(candidate.collect(ROOT))
        for name in expected | {'docs/screenshots/README.md', 'tests/test_github_screenshots.py'}:
            with self.subTest(name=name):
                self.assertIn(name, paths)
                self.assertIn(name, candidate.REQUIRED_FILES)
                with self.assertRaises(ValueError):
                    candidate.validate_complete(list(candidate.REQUIRED_FILES - {name}))

    def test_png_is_valid_and_has_no_freeform_metadata(self):
        for name in candidate.SCREENSHOT_HASHES:
            with self.subTest(name=name):
                data = candidate.payload(ROOT, name)
                self.assertEqual(data[:8], b'\x89PNG\r\n\x1a\n')
                offset = 8; tags = []
                while offset < len(data):
                    self.assertLessEqual(offset + 12, len(data))
                    size = struct.unpack_from('>I', data, offset)[0]
                    end = offset + size + 12
                    self.assertLessEqual(end, len(data))
                    tag = data[offset+4:offset+8]
                    self.assertEqual(zlib.crc32(data[offset+4:offset+8+size]) & 0xffffffff,
                                     struct.unpack_from('>I', data, offset+8+size)[0])
                    tags.append(tag); offset = end
                    if tag == b'IEND': break
                self.assertEqual(offset, len(data))
                self.assertEqual(tags[0], b'IHDR'); self.assertEqual(tags[-1], b'IEND')
                self.assertFalse({b'tEXt',b'zTXt',b'iTXt',b'eXIf'} & set(tags))

    def test_readme_local_images_resolve_to_collected_files(self):
        text = (ROOT / 'README.md').read_text(encoding='utf-8')
        included = set(candidate.collect(ROOT))
        screenshot_links = set()
        for target in re.findall(r'!\[[^\]]*\]\(([^)]+)\)', text):
            uri = urlsplit(target)
            if uri.scheme or not uri.path:
                continue
            path = posixpath.normpath(unquote(uri.path))
            self.assertFalse(path.startswith('../') or path.startswith('/'))
            self.assertIn(path, included, f'image is not in source candidate: {path}')
            if path.startswith('docs/screenshots/'):
                screenshot_links.add(path)
        self.assertEqual(screenshot_links, set(candidate.SCREENSHOT_HASHES))

    def test_modified_screenshot_is_refused(self):
        name = next(iter(candidate.SCREENSHOT_HASHES))
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); p = root / name; p.parent.mkdir(parents=True)
            p.write_bytes((ROOT / name).read_bytes() + b'modified')
            with self.assertRaisesRegex(ValueError, 'screenshot'):
                candidate.payload(root, name)

if __name__ == '__main__': unittest.main()
