"""Fail-closed inventory validation for a new isolated discovery portable candidate."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat

REQUIRED = {'App/NianLink.DiscoveryPortable.exe', 'App/NianLink.DiscoveryPortable.dll',
            'App/NianLink.DiscoveryPortable.deps.json', 'App/NianLink.DiscoveryPortable.runtimeconfig.json',
            'App/NianLink.CandidateDiscovery.dll', 'Keyboard/deskflow-core.exe',
            'Keyboard/settings/Deskflow.conf', 'App/discovery-portable-id.txt',
            '笔记本-D盘启动.cmd', '台式机-H盘启动.cmd'}
REQUIRED_SOURCE = {'src/NianLink.CandidateDiscovery/NianLink.CandidateDiscovery.csproj',
                   'src/NianLink.CandidateDiscovery/CandidateDiscoveryRun.cs',
                   'src/NianLink.MultiDevice/NianLink.MultiDevice.csproj',
                   'src/NianLink.DiscoveryPortable/NianLink.DiscoveryPortable.csproj',
                   'src/NianLink.MultiDevice.Shared/PeerControlOwner.cs'}
SOURCE_DIRS = ('src/NianLink.CandidateDiscovery', 'src/NianLink.MultiDevice', 'src/NianLink.DiscoveryPortable', 'src/DesktopToolbox.DeskLink', 'src/NianLink.MultiDevice.Shared')


def expected_launcher(run_id, drive):
    if drive not in ('D', 'H'): raise ValueError('unsupported discovery drive')
    return ('@echo off\r\nstart "" "%~dp0App\\NianLink.DiscoveryPortable.exe" '
            '"' + drive + ':\\Projects\\nian-link\\test-profiles\\discovery-portable\\test-' + run_id + '" '
            '"%~dp0Keyboard\\deskflow-core.exe" --discovery-portable\r\n')


def reject_links(path):
    for item in (path, *path.parents):
        try: info = item.lstat()
        except FileNotFoundError: continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError('linked path refused')


def check_rows(base, rows, forbid_private):
    seen = set()
    for row in rows:
        rel = row.get('path')
        if not isinstance(rel, str) or not rel or '\\' in rel or ':' in rel or any(ord(c) < 32 for c in rel):
            raise ValueError('unsafe path')
        parts = rel.split('/')
        if any(x in ('', '.', '..') or x.endswith(('.', ' ')) for x in parts): raise ValueError('unsafe path')
        if rel.casefold() in seen: raise ValueError('duplicate path')
        seen.add(rel.casefold())
        if forbid_private and (re.search(r'(?i)\.(pem|pfx|p12|key)$', rel) or
                               any(x.casefold() in ('profile', 'profiles', 'test-profiles', 'trusted-clients') for x in parts)):
            raise ValueError('private payload refused')
        path = base.joinpath(*parts)
        reject_links(path)
        if not path.is_file() or path.stat().st_size != row.get('bytes') or hashlib.sha256(path.read_bytes()).hexdigest().lower() != str(row.get('sha256', '')).lower():
            raise ValueError('file missing or hash differs: ' + rel)
    return seen


def tree_files(base, source=False):
    result = set()
    for root, dirs, files in os.walk(base, followlinks=False):
        root = Path(root)
        reject_links(root)
        if source: dirs[:] = [d for d in dirs if d.casefold() not in ('bin', 'obj')]
        for d in dirs: reject_links(root / d)
        for f in files:
            path = root / f; reject_links(path)
            if not source or path.suffix.lower() in ('.cs', '.csproj', '.xaml'):
                result.add(path.relative_to(base).as_posix().casefold())
    return result


def validate(bundle, source, manifest):
    reject_links(bundle); reject_links(source)
    run_id = manifest.get('runId', '')
    if manifest.get('channel') != 'discovery-portable-v1' or not re.fullmatch(r'[a-f0-9]{32}', run_id): raise ValueError('invalid candidate identity')
    files = check_rows(bundle, manifest.get('files', []), True)
    if {'app/nianlink.multidevice.exe', 'app/nianlink.multidevice.dll', 'app/nianlink.multidevice.deps.json', 'app/nianlink.multidevice.runtimeconfig.json'} & files: raise ValueError('ordinary entry point refused')
    if not {x.casefold() for x in REQUIRED} <= files or files != tree_files(bundle): raise ValueError('incomplete or extra payload')
    sources = check_rows(source, manifest.get('sources', []), False)
    expected = set()
    for name in SOURCE_DIRS:
        directory = source / name
        if not directory.is_dir(): raise ValueError('source directory missing')
        expected |= {(name + '/' + x).casefold() for x in tree_files(directory, True)}
    if not {x.casefold() for x in REQUIRED_SOURCE} <= sources or sources != expected: raise ValueError('incomplete source snapshot')
    if (bundle / 'App/discovery-portable-id.txt').read_bytes() != run_id.encode('ascii'): raise ValueError('package identity differs')
    beta = manifest.get('betaPortable', False)
    if type(beta) is not bool: raise ValueError('invalid beta marker')
    beta_name = 'Start-NianLink-Beta.cmd'
    if beta:
        expected = b'@echo off\r\nstart "" "%~dp0App\\NianLink.DiscoveryPortable.exe" --beta-portable\r\n'
        if beta_name.casefold() not in files or (bundle / beta_name).read_bytes() != expected: raise ValueError('incorrect beta launcher')
    elif beta_name.casefold() in files:
        raise ValueError('unexpected beta launcher')
    for filename, drive in (('笔记本-D盘启动.cmd', 'D'), ('台式机-H盘启动.cmd', 'H')):
        if (bundle / filename).read_bytes() != expected_launcher(run_id, drive).encode('ascii'): raise ValueError('incorrect isolated launcher')
    deps = json.loads((bundle / 'App/NianLink.DiscoveryPortable.deps.json').read_text(encoding='utf-8'))
    if not any(name.startswith('NianLink.CandidateDiscovery/') and 'NianLink.CandidateDiscovery.dll' in lib.get('runtime', {})
               for target in deps.get('targets', {}).values() for name, lib in target.items()): raise ValueError('discovery dependency missing')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--bundle', type=Path, required=True)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    args = parser.parse_args()
    reject_links(args.manifest)
    validate(args.bundle, args.source, json.loads(args.manifest.read_text(encoding='utf-8-sig')))
    print('Discovery portable inventory verified; no devices accessed.')

if __name__ == '__main__': main()
