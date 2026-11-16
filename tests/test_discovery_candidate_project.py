"""Candidate discovery must remain outside the normal product build graph."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
CANDIDATE = (ROOT / 'src/NianLink.CandidateDiscovery/NianLink.CandidateDiscovery.csproj').resolve()
SHARED = (ROOT / 'src/DesktopToolbox.DeskLink/DesktopToolbox.DeskLink.csproj').resolve()


def graph(project):
    result, pending = set(), [project.resolve()]
    while pending:
        current = pending.pop()
        if current in result:
            continue
        result.add(current)
        for item in ET.parse(current).getroot().iter('ProjectReference'):
            if item.get('Condition') or item.get('AdditionalProperties') or item.find('AdditionalProperties') is not None:
                raise AssertionError('Conditional or different-property references require graph review')
            reference = item.get('Include')
            if not reference:
                raise AssertionError('Project reference has no path')
            pending.append((current.parent / reference.replace('\\', '/')).resolve())
    return result


class CandidateDiscoveryGraphTests(unittest.TestCase):
    def test_peer_guard_reference_is_candidate_only(self):
        shared = '../NianLink.MultiDevice.Shared/PeerControlOwner.cs'
        candidate = ET.parse(ROOT / 'src/NianLink.DiscoveryPortable/NianLink.DiscoveryPortable.csproj').getroot()
        ordinary = ET.parse(ROOT / 'src/NianLink.MultiDevice/NianLink.MultiDevice.csproj').getroot()
        self.assertIn(shared, [x.get('Include') for x in candidate.iter('Compile')])
        self.assertNotIn(shared, [x.get('Include') for x in ordinary.iter('Compile')])
        # The mini backend is compiled in both products. It must use the owner
        # abstraction, not directly reference the candidate-only guard type.
        mini = (ROOT / 'src/NianLink.MultiDevice/MainWindow.PeerMini.cs').read_text(encoding='utf-8')
        self.assertNotIn('PeerControlOwner.', mini)

    def test_only_multi_device_project_references_candidate(self):
        multi = graph(ROOT / 'src/NianLink.MultiDevice/NianLink.MultiDevice.csproj')
        self.assertIn(CANDIDATE, multi)
        self.assertIn(SHARED, multi)
        for project in ('src/NianLink.Windows/NianLink.Windows.csproj',
                        'src/DesktopToolbox.Mac/DesktopToolbox.Mac.csproj'):
            with self.subTest(project=project):
                self.assertNotIn(CANDIDATE, graph(ROOT / project))

    def test_candidate_reuses_one_normal_shared_reference(self):
        root = ET.parse(CANDIDATE).getroot()
        refs = list(root.iter('ProjectReference'))
        self.assertEqual(1, len(refs))
        self.assertEqual(SHARED, (CANDIDATE.parent / refs[0].get('Include').replace('\\', '/')).resolve())
        self.assertIsNone(refs[0].get('AdditionalProperties'))
        self.assertIsNone(refs[0].find('AdditionalProperties'))


if __name__ == '__main__':
    unittest.main()
