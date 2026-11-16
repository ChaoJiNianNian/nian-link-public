#!/usr/bin/env python3
"""Build the four-patch Windows Deskflow core in a new isolated directory.

Qt and OpenSSL development prefixes and the Windows compiler are external inputs.
This is not a full release build and never runs the resulting core.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import sys
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
ARCHIVE_HASH = '8493dfa58dbeac161a8b25d5b3cb7cb8546b5f4d99a216fcd65296b220a864f0'
ARCHIVE_ROOT = 'deskflow-081f6478e654a112168879e67a41ce7d7e4ae3af'
PATCHES = (
 ('0001-current-user-native-control.patch', '4553424fcf97b368c4356d647d5ed25b7e529daba1b00fe4cebd6a8f047e5324'),
 ('0002-preview-input-isolation-experimental.patch', '0c0f7937ddf61311415806da3f2fa37c310b3e089a98a7b5933e0daa16f4ffce'),
 ('0003-multidevice-conditional-switch-experimental.patch', 'c25888d4f6d952dcb85872173b82414ac8e3d1abd20e2ebf6f59c671118e9d1c'),
 ('0004-multidevice-identity-binding-experimental.patch', '67bfc277bd5be9cd95eeeea664988aeccb9d01e8a9d3b34b4383a70339c0d58c'),
)
KNOWN_ICON_LINKS = {}
for theme in ('deskflow-dark', 'deskflow-light'):
    base = f'src/apps/res/icons/{theme}/actions/'
    for size in ('16','22','24','32'):
        for scale in ('2x','3x'):
            KNOWN_ICON_LINKS[base + size + '@' + scale] = size.encode()
    KNOWN_ICON_LINKS[base + '32/dialog-ok.svg'] = b'dialog-ok-apply.svg'

class BuildError(Exception):
    pass

def sha256(stream):
    h = hashlib.sha256()
    for data in iter(lambda: stream.read(1024 * 1024), b''): h.update(data)
    return h.hexdigest()

def no_links(path):
    path = Path(os.path.abspath(path))
    for part in reversed((path, *path.parents)):
        try: info = part.lstat()
        except FileNotFoundError: continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise BuildError('Linked path or reparse point refused')
    return path

def checked_sources(archive, patch_root):
    archive = no_links(archive)
    if not archive.is_file(): raise BuildError('Source archive missing')
    with archive.open('rb') as f:
        if sha256(f) != ARCHIVE_HASH: raise BuildError('Source archive SHA-256 mismatch')
    result = []
    for name, expected in PATCHES:
        path = no_links(patch_root / name)
        raw = path.read_bytes()
        if hashlib.sha256(raw).hexdigest() != expected: raise BuildError('Patch SHA-256 mismatch: ' + name)
        result.append((name, raw))
    return archive, result

def check_entry(info, seen):
    name = info.filename
    if '\\' in name or ':' in name or any(ord(x) < 32 for x in name): raise BuildError('Unsafe archive member')
    parts = PurePosixPath(name.rstrip('/')).parts
    if not parts or parts[0] != ARCHIVE_ROOT or any(x in ('', '.', '..') or x.endswith(('.', ' ')) for x in parts):
        raise BuildError('Unsafe archive member')
    for part in parts:
        if re.match(r'^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)', part, re.I): raise BuildError('Unsafe archive member')
    key = '/'.join(parts).casefold()
    if key in seen: raise BuildError('Duplicate archive member')
    seen.add(key)
    mode = (info.external_attr >> 16) & 0xffff
    kind = stat.S_IFMT(mode)
    if kind not in (0, stat.S_IFREG, stat.S_IFDIR, stat.S_IFLNK): raise BuildError('Unsupported archive member')
    relative = '/'.join(parts[1:])
    if kind == stat.S_IFLNK and relative not in KNOWN_ICON_LINKS: raise BuildError('Unapproved archive link')
    if kind != stat.S_IFLNK and relative in KNOWN_ICON_LINKS: raise BuildError('Expected icon link missing')
    return parts, relative, kind == stat.S_IFLNK

def validate_icon_target(relative, data):
    if relative not in KNOWN_ICON_LINKS or data != KNOWN_ICON_LINKS[relative]:
        raise BuildError('Icon link target mismatch')

def extract_source(archive, destination):
    no_links(destination)
    seen, links, total = set(), set(), 0
    with archive.open('rb') as raw:
        if sha256(raw) != ARCHIVE_HASH: raise BuildError('Source archive changed')
        raw.seek(0)
        with zipfile.ZipFile(raw) as z:
            if len(z.infolist()) != 921: raise BuildError('Unexpected source inventory')
            for info in z.infolist():
                parts, relative, is_link = check_entry(info, seen)
                total += info.file_size
                if total > 100 * 1024 * 1024: raise BuildError('Source archive too large')
                out = destination.joinpath(*parts)
                if info.is_dir():
                    out.mkdir(parents=True, exist_ok=True)
                    continue
                out.parent.mkdir(parents=True, exist_ok=True)
                data = z.read(info)  # CRC checked by zipfile.
                if is_link:
                    validate_icon_target(relative, data)
                    links.add(relative)
                with out.open('xb') as target: target.write(data)
        raw.seek(0)
        if sha256(raw) != ARCHIVE_HASH: raise BuildError('Source archive changed during extraction')
    if links != set(KNOWN_ICON_LINKS): raise BuildError('Incomplete icon-link inventory')
    return destination / ARCHIVE_ROOT

def prepare_output(root):
    root = no_links(root)
    if not root.is_dir(): raise BuildError('Project root missing')
    artifacts = root / '.artifacts'; no_links(artifacts); artifacts.mkdir(exist_ok=True)
    parent = artifacts / 'native-core-build'; no_links(parent); parent.mkdir(exist_ok=True)
    no_links(parent)
    work = parent / ('build-' + uuid.uuid4().hex)
    work.mkdir()  # exclusive; no pre-existing directory is reused or deleted.
    return work

def require_inputs(qt, openssl):
    qt, openssl = no_links(qt), no_links(openssl)
    for base, names in ((qt, ('bin/windeployqt.exe','lib/cmake/Qt6/Qt6Config.cmake','lib/cmake/Qt6LinguistTools/Qt6LinguistToolsConfig.cmake')),
                        (openssl, ('include/openssl/ssl.h','lib/libssl.lib','lib/libcrypto.lib'))):
        for name in names:
            path = no_links(base / name)
            if not path.is_file(): raise BuildError('External development input missing: ' + name)
    return qt, openssl

def verify_cmake_cache(cache, qt, openssl):
    required_qt = {'Qt6_DIR','Qt6Core_DIR','Qt6LinguistTools_DIR','Qt6Network_DIR','Qt6Widgets_DIR'}
    required_ssl = {'OPENSSL_INCLUDE_DIR','OPENSSL_APPLINK_SOURCE','LIB_EAY_RELEASE','SSL_EAY_RELEASE'}
    optional_ssl = {'LIB_EAY_DEBUG','SSL_EAY_DEBUG'}
    entries = {}
    for line in cache.read_text(encoding='utf-8').splitlines():
        if line.startswith(('#','//')) or '=' not in line or ':' not in line.split('=',1)[0]: continue
        left,value = line.split('=',1); name=left.split(':',1)[0]
        relevant = name in required_ssl or name in optional_ssl or (name.startswith('Qt6') and name.endswith('_DIR'))
        if not relevant: continue
        if name in entries: raise BuildError('Duplicate dependency entry in CMake cache')
        entries[name] = value
    if not required_qt.issubset(entries) or not required_ssl.issubset(entries):
        raise BuildError('Required dependency entries missing in CMake cache')
    qt_root, ssl_root = no_links(qt).resolve(strict=True), no_links(openssl).resolve(strict=True)
    for name, value in entries.items():
        actual = no_links(Path(value)).resolve(strict=True)
        expected = ssl_root if name in required_ssl or name in optional_ssl else qt_root
        if actual != expected and expected not in actual.parents:
            raise BuildError('CMake selected a dependency outside its supplied prefix: ' + name)
    return len(entries)

def run_step(label, args, work, env):
    with (work / (label + '.log')).open('xb') as log:
        result = subprocess.run(args, cwd=work, env=env, stdout=log, stderr=subprocess.STDOUT, check=False)
    if result.returncode: raise BuildError(label + ' failed; isolated output retained')

def build(archive, qt, openssl, root=ROOT):
    if sys.platform != 'win32': raise BuildError('Windows x64 environment required')
    if os.environ.get('VSCMD_ARG_TGT_ARCH','').lower() != 'x64': raise BuildError('x64 MSVC developer environment required')
    archive, patches = checked_sources(archive, root / 'vendor/deskflow/metadata')
    qt, openssl = require_inputs(qt, openssl)
    tools = {name: shutil.which(name) for name in ('git','cmake','ninja','cl')}
    if not all(tools.values()): raise BuildError('Required Git, CMake, Ninja or MSVC tool missing')
    work = prepare_output(root)
    src = extract_source(archive, work)
    temp = work / 'tmp'; temp.mkdir()
    env = os.environ.copy(); env['PATH'] = str(qt / 'bin') + os.pathsep + env.get('PATH','')
    env['TEMP'] = str(temp); env['TMP'] = str(temp)
    for i, (name, raw) in enumerate(patches, 1):
        p = work / name
        with p.open('xb') as f: f.write(raw)
        run_step(f'patch-{i}-check', [tools['git'],'-C',str(src),'-c','core.autocrlf=false','apply','--check','--',str(p)], work, env)
        run_step(f'patch-{i}-apply', [tools['git'],'-C',str(src),'-c','core.autocrlf=false','apply','--',str(p)], work, env)
    build_dir = work / 'build'
    run_step('configure',[tools['cmake'],'-S',str(src),'-B',str(build_dir),'-G','Ninja',
        '-DCMAKE_C_COMPILER='+tools['cl'], '-DCMAKE_CXX_COMPILER='+tools['cl'], '-DCMAKE_MAKE_PROGRAM='+tools['ninja'],
        '-DCMAKE_BUILD_TYPE=Release','-DCMAKE_RC_FLAGS=/c65001','-DBUILD_TESTS=OFF','-DBUILD_INSTALLER=OFF',
        '-DCMAKE_PREFIX_PATH='+str(qt),'-DOPENSSL_ROOT_DIR='+str(openssl)],work,env)
    checked_cache_entries = verify_cmake_cache(build_dir / 'CMakeCache.txt', qt, openssl)
    run_step('build',[tools['cmake'],'--build',str(build_dir),'--target','deskflow-core','--parallel','4'],work,env)
    exe = build_dir / 'bin/deskflow-core.exe'
    no_links(exe)
    if not exe.is_file(): raise BuildError('Expected core not produced')
    with exe.open('rb') as f: digest = sha256(f)
    report = {'complete':True, 'checked_cmake_dependency_entries':checked_cache_entries, 'scope':'isolated Windows core build; external Qt/OpenSSL/toolchain, executable not run',
              'source_sha256':ARCHIVE_HASH, 'patches':[{'name':name,'sha256':hashlib.sha256(raw).hexdigest()} for name,raw in patches],
              'icon_links':'18 fixed icon link targets stored as plain files; GUI resources not validated',
              'core_sha256':digest, 'core_size':exe.stat().st_size}
    with (work / 'build-evidence.json').open('x',encoding='utf-8',newline='\n') as f: json.dump(report,f,ensure_ascii=False,indent=2); f.write('\n')
    return work

def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--archive',type=Path,required=True);ap.add_argument('--qt',type=Path,required=True);ap.add_argument('--openssl',type=Path,required=True)
    args = ap.parse_args()
    try: work = build(args.archive,args.qt,args.openssl)
    except (BuildError, OSError, ValueError, zipfile.BadZipFile, subprocess.SubprocessError) as e:
        print('Build failed:',e,file=sys.stderr);return 1
    print('Isolated build evidence:',work / 'build-evidence.json');return 0
if __name__ == '__main__': raise SystemExit(main())
