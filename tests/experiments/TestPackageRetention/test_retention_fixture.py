"""Synthetic two-generation retention model; never touches real test packages."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parents[2]
MODULE = HERE / 'RetentionFixture.psm1'
BUILD_LOCK = PROJECT / 'scripts/BuildArtifactLock.psm1'
PWSH = shutil.which('pwsh')


@unittest.skipUnless(PWSH and os.name == 'nt', 'requires Windows PowerShell 7')
class RetentionFixtureTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='nian-retention-fixture-')
        self.root = Path(self.temp.name)
        self.assertEqual(self.call('Initialize-RetentionFixture').returncode, 0)

    def tearDown(self):
        self.temp.cleanup()

    def call(self, command, kind='windows-fileless', digest='', root=None):
        env = os.environ | {'FIXTURE_MODULE': str(MODULE), 'FIXTURE_LOCK': str(BUILD_LOCK),
                            'FIXTURE_ROOT': str(root or self.root), 'FIXTURE_KIND': kind,
                            'FIXTURE_HASH': digest}
        args = { 'Initialize-RetentionFixture': '', 'Start-RetentionFixture': ' -Kind $env:FIXTURE_KIND',
                 'Commit-RetentionFixture': ' -Kind $env:FIXTURE_KIND -ExpectedSha256 $env:FIXTURE_HASH',
                 'Read-RetentionFixture': ' -Kind $env:FIXTURE_KIND' }[command]
        script = ("[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; "
                  "Import-Module $env:FIXTURE_MODULE -DisableNameChecking -ErrorAction Stop; "
                  f'{command} -Root $env:FIXTURE_ROOT -LockModule $env:FIXTURE_LOCK{args}')
        return subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command', script],
                              env=env, capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=20)

    def state(self, kind='windows-fileless'):
        result = self.call('Read-RetentionFixture', kind=kind)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def start(self, kind='windows-fileless'):
        result = self.call('Start-RetentionFixture', kind=kind)
        self.assertEqual(result.returncode, 0, result.stderr)
        return Path(result.stdout.strip())

    def finish(self, path, content=b'synthetic payload', kind='windows-fileless'):
        (path / 'sample.bin').write_bytes(content)
        result = self.call('Commit-RetentionFixture', kind=kind, digest=hashlib.sha256(content).hexdigest())
        self.assertEqual(result.returncode, 0, result.stderr)
        return self.state(kind)

    def test_two_successes_then_refuse_without_creating_third(self):
        first = self.start()
        s1 = self.finish(first, b'one')
        self.assertEqual(s1['current'], first.name)
        self.assertIsNone(s1['previous'])
        second = self.start()
        s2 = self.finish(second, b'two')
        self.assertEqual((s2['current'], s2['previous']), (second.name, first.name))
        before = sorted(p.name for p in first.parent.iterdir())
        self.assertNotEqual(self.call('Start-RetentionFixture').returncode, 0)
        self.assertEqual(self.state(), s2)
        self.assertEqual(sorted(p.name for p in first.parent.iterdir()), before)
        self.assertEqual((first / 'sample.bin').read_bytes(), b'one')

    def test_failed_verification_retains_previous_and_blocks_extra_run(self):
        first = self.start()
        self.finish(first, b'one')
        pending = self.start()
        (pending / 'sample.bin').write_bytes(b'not expected')
        before = self.state()
        self.assertNotEqual(self.call('Commit-RetentionFixture', digest='0'*64).returncode, 0)
        self.assertEqual(self.state(), before)
        self.assertEqual(before['current'], first.name)
        self.assertEqual(before['pending'], pending.name)
        self.assertNotEqual(self.call('Start-RetentionFixture').returncode, 0)

    def test_modes_are_independent(self):
        win, mac = self.start(), self.start('mac-fileless')
        self.finish(win, b'w')
        self.finish(mac, b'm', kind='mac-fileless')
        self.assertEqual(self.state()['current'], win.name)
        self.assertEqual(self.state('mac-fileless')['current'], mac.name)

    def test_refuses_outside_fixture_and_tampered_index(self):
        self.assertNotEqual(self.call('Read-RetentionFixture', root=self.root.parent).returncode, 0)
        one = self.start()
        self.finish(one)
        index = self.root / 'retention-windows-fileless.json'
        before = json.loads(index.read_text(encoding='utf-8-sig'))
        before['current'] = '../../outside'
        index.write_text(json.dumps(before), encoding='utf-8')
        self.assertNotEqual(self.call('Start-RetentionFixture').returncode, 0)
        self.assertEqual(json.loads(index.read_text(encoding='utf-8'))['current'], '../../outside')

    def test_refuses_modified_committed_payload(self):
        first = self.start()
        self.finish(first, b'original')
        (first / 'sample.bin').write_bytes(b'modified')
        before = sorted(p.name for p in first.parent.iterdir())
        self.assertNotEqual(self.call('Start-RetentionFixture').returncode, 0)
        self.assertEqual(sorted(p.name for p in first.parent.iterdir()), before)

    def test_interrupted_index_and_untracked_output_refuse_new_runs(self):
        first = self.start()
        state = self.finish(first, b'original')
        index = self.root / 'retention-windows-fileless.json'
        next_file = Path(str(index) + '.next')
        next_file.write_text('interrupted', encoding='utf-8')
        self.assertNotEqual(self.call('Start-RetentionFixture').returncode, 0)
        self.assertEqual(json.loads(index.read_text(encoding='utf-8-sig')), state)
        # Use the other independent mode to model an orphan without removing
        # the deliberately interrupted index in this test-only fixture.
        parent = self.root / 'payloads' / 'mac-fileless'
        (parent / ('test-' + '1' * 32)).mkdir(parents=True)
        self.assertNotEqual(self.call('Start-RetentionFixture', kind='mac-fileless').returncode, 0)
        self.assertEqual(len(list(parent.iterdir())), 1)

    def test_read_refuses_untracked_same_kind_output(self):
        first = self.start()
        self.finish(first, b'original')
        orphan = first.parent / ('test-' + '1' * 32)
        orphan.mkdir()
        before = sorted(p.name for p in first.parent.iterdir())
        index = self.root / 'retention-windows-fileless.json'
        original_index = index.read_bytes()
        self.assertNotEqual(self.call('Read-RetentionFixture').returncode, 0)
        self.assertEqual(sorted(p.name for p in first.parent.iterdir()), before)
        self.assertEqual(index.read_bytes(), original_index)

    def test_refuses_pending_directory_containing_junction(self):
        pending = self.start()
        outside = self.root / 'outside-test'
        outside.mkdir()
        env = os.environ | {'FIXTURE_LINK': str(pending / 'linked'), 'FIXTURE_TARGET': str(outside)}
        result = subprocess.run([PWSH, '-NoProfile', '-NonInteractive', '-Command',
                                 'New-Item -ItemType Junction -Path $env:FIXTURE_LINK -Target $env:FIXTURE_TARGET -ErrorAction Stop | Out-Null'],
                                env=env, capture_output=True, text=True, encoding='utf-8', errors='replace')
        if result.returncode:
            self.skipTest('test-only junction creation unavailable')
        (pending / 'sample.bin').write_bytes(b'sample')
        before = self.state()
        result = self.call('Commit-RetentionFixture', digest=hashlib.sha256(b'sample').hexdigest())
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.state(), before)
        self.assertEqual(list(outside.iterdir()), [])

    def test_refuses_when_existing_project_mutex_is_held(self):
        ready, done = self.root / 'ready', self.root / 'done'
        env = os.environ | {'FIXTURE_LOCK': str(BUILD_LOCK), 'FIXTURE_ROOT': str(self.root),
                            'READY': str(ready), 'DONE': str(done)}
        script = ("Import-Module $env:FIXTURE_LOCK -ErrorAction Stop; $l=Enter-ToolboxBuildLock -ProjectRoot $env:FIXTURE_ROOT; "
                  "try { [IO.File]::WriteAllText($env:READY,'1'); while (-not [IO.File]::Exists($env:DONE)) { Start-Sleep -Milliseconds 40 } } "
                  "finally { Exit-ToolboxBuildLock -Lock $l }")
        holder = subprocess.Popen([PWSH, '-NoProfile', '-NonInteractive', '-Command', script], env=env,
                                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            deadline = time.monotonic() + 10
            while not ready.exists() and holder.poll() is None and time.monotonic() < deadline:
                time.sleep(.05)
            self.assertTrue(ready.exists())
            self.assertNotEqual(self.call('Start-RetentionFixture').returncode, 0)
            done.touch()
            self.assertEqual(holder.wait(timeout=10), 0)
            self.start()
        finally:
            if holder.poll() is None:
                holder.terminate()
                holder.wait(timeout=10)


if __name__ == '__main__':
    unittest.main()
