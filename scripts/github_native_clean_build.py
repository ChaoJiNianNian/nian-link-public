#!/usr/bin/env python3
"""Isolated Windows x64 source build of OpenSSL, QtBase, QtTools and the four-patch Deskflow core.

Manual CI use only: no runtime tests, packaging, installation to the host, publishing or cache reuse.
"""
from __future__ import annotations
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
TARS = {
    'openssl-3.5.9.tar.gz': ('openssl-3.5.9',6078,'603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a'),
    'qtbase-everywhere-src-6.8.3.tar.xz': ('qtbase-everywhere-src-6.8.3',25704,'56001b905601bb9023d399f3ba780d7fa940f3e4861e496a7c490331f49e0b80'),
    'qttools-everywhere-src-6.8.3.tar.xz': ('qttools-everywhere-src-6.8.3',5475,'02a4e219248b94f1333df843d25763f35251c1074cdc4fb5bda67d340f8c8b3a'),
}
DESKFLOW_HASH = '8493dfa58dbeac161a8b25d5b3cb7cb8546b5f4d99a216fcd65296b220a864f0'
class BuildError(Exception): pass

def digest(path):
    h=hashlib.sha256()
    with Path(path).open('rb') as f:
        for block in iter(lambda:f.read(1024*1024),b''):h.update(block)
    return h.hexdigest()

def no_links(path):
    path=Path(os.path.abspath(path))
    for p in reversed((path,*path.parents)):
        try:i=p.lstat()
        except FileNotFoundError:continue
        if stat.S_ISLNK(i.st_mode) or getattr(i,'st_file_attributes',0)&0x400:raise BuildError('Linked path or reparse point refused')
    return path

def safe_members(members, root, expected_count):
    if len(members)!=expected_count:raise BuildError('Unexpected source member count')
    seen=set();total=0
    for info in members:
        raw=info.name.rstrip('/')
        if not raw or '\\' in raw or ':' in raw or any(ord(c)<32 for c in raw):raise BuildError('Unsafe source member')
        pieces=raw.split('/')
        if pieces[0]!=root or any(x in ('','.','..') or x.endswith(('.', ' ')) for x in pieces):raise BuildError('Unsafe source member')
        for x in pieces:
            if re.match(r'^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)',x,re.I):raise BuildError('Reserved source member')
        key=raw.casefold()
        if key in seen:raise BuildError('Duplicate source member')
        seen.add(key)
        if not (info.isfile() or info.isdir()):raise BuildError('Archive link or unsupported member refused')
        if info.size<0:raise BuildError('Invalid source size')
        total+=info.size
        if total>1024*1024*1024:raise BuildError('Expanded source size exceeds limit')
    return total

def extract_tar(path,dest,root,count,expected_hash):
    path=no_links(path);dest=no_links(dest)
    if digest(path)!=expected_hash:raise BuildError('Source SHA-256 mismatch')
    with path.open('rb') as original:
        with tarfile.open(fileobj=original,mode='r:*') as t:
            members=t.getmembers();safe_members(members,root,count)
            for info in members:
                out=dest.joinpath(*PurePosixPath(info.name).parts)
                if info.isdir():out.mkdir(parents=True,exist_ok=True);continue
                out.parent.mkdir(parents=True,exist_ok=True)
                src=t.extractfile(info)
                if src is None:raise BuildError('Cannot read source member')
                with src, out.open('xb') as dst:shutil.copyfileobj(src,dst,1024*1024)
        original.seek(0)
        h=hashlib.sha256()
        for block in iter(lambda:original.read(1024*1024),b''):h.update(block)
        if h.hexdigest()!=expected_hash:raise BuildError('Source archive changed during extraction')
    return dest/root

# Observed long QtBase MSVC object from the verified 6.8.3 source build.
# This guard is conservative for that observed input, not a guarantee for future paths.
KNOWN_QT_OBJECT = Path('src/plugins/platforms/direct2d/CMakeFiles/QWindowsDirect2DIntegrationPlugin.dir/QWindowsDirect2DIntegrationPlugin_autogen/mocs_compilation.cpp.obj')

def windows_path_units(path):
    return len(str(path).encode('utf-16-le')) // 2

def validate_work_path(root):
    representative = root/'.artifacts/native-src'/('run-'+'0'*32)/'qtbase-build'/KNOWN_QT_OBJECT
    if windows_path_units(representative)>250:
        raise BuildError('Checkout path too long for observed Qt MSVC object; use a shorter checkout')

def create_work(root):
    root=no_links(root)
    if not root.is_dir():raise BuildError('Source root missing')
    validate_work_path(root)
    if shutil.disk_usage(root).free<8*1024**3:raise BuildError('At least 8 GiB of free space required')
    a=root/'.artifacts';no_links(a);a.mkdir(exist_ok=True)
    p=a/'native-src';no_links(p);p.mkdir(exist_ok=True)
    no_links(p);w=p/('run-'+uuid.uuid4().hex);w.mkdir();return w

def tools_and_versions():
    names=('git','cmake','ninja','perl','nmake','cl')
    found={n:shutil.which(n) for n in names}
    if not all(found.values()):raise BuildError('Git, CMake, Ninja, Perl, NMake and MSVC cl are required')
    versions={}
    for n,args in (('git',['--version']),('cmake',['--version']),('ninja',['--version']),('perl',['-e','print $^V']),('nmake',['/?']),('cl',[])):
        r=subprocess.run([found[n],*args],capture_output=True,text=True,errors='replace',timeout=30)
        # MSVC's version banner is on stderr; help/usage is on stdout.  Other
        # tools can emit warnings on stderr while printing their version to stdout.
        first,second=(r.stderr,r.stdout) if n in ('nmake','cl') else (r.stdout,r.stderr)
        output=(first+'\n'+second).strip()
        if not output:raise BuildError('Tool did not identify itself: '+n)
        versions[n]={'output':output[:2048],'executable_sha256':digest(found[n])}
    return found,versions

def run_step(label,cmd,cwd,work,env):
    print('Starting',label,flush=True)
    with (work/(label+'.log')).open('xb') as log:
        r=subprocess.run(cmd,cwd=cwd,env=env,stdout=log,stderr=subprocess.STDOUT,check=False)
    if r.returncode:
        raw=(work/(label+'.log')).read_bytes()[-32768:]
        print('Last output from',label+':',raw.decode('utf-8','replace'),file=sys.stderr,flush=True)
        raise BuildError(label+' failed; isolated logs retained')

def prefix_cache(cache,qt,ssl):
    keys={'OPENSSL_INCLUDE_DIR','OPENSSL_APPLINK_SOURCE','LIB_EAY_RELEASE','SSL_EAY_RELEASE'}
    seen={}
    for line in cache.read_text(encoding='utf-8').splitlines():
        if line.startswith(('#','//')) or '=' not in line or ':' not in line.split('=',1)[0]:continue
        a,value=line.split('=',1);name=a.split(':',1)[0]
        if name not in keys:continue
        if name in seen:raise BuildError('Duplicate OpenSSL cache entry')
        seen[name]=value
    if not {'OPENSSL_INCLUDE_DIR','LIB_EAY_RELEASE','SSL_EAY_RELEASE'}.issubset(seen):raise BuildError('OpenSSL cache entries missing')
    expected=no_links(ssl).resolve(strict=True)
    for name,value in seen.items():
        resolved=no_links(Path(value)).resolve(strict=True)
        if resolved!=expected and expected not in resolved.parents:raise BuildError('OpenSSL cache outside expected prefix: '+name)
    return len(seen)

def qttools_cache(cache,qt):
    required={'Qt6_DIR','Qt6Core_DIR','Qt6Gui_DIR','Qt6Network_DIR','Qt6Widgets_DIR'}
    seen={}
    for line in cache.read_text(encoding='utf-8').splitlines():
        if line.startswith(('#','//')) or '=' not in line or ':' not in line.split('=',1)[0]:continue
        left,value=line.split('=',1);name=left.split(':',1)[0]
        if not (name.startswith('Qt6') and name.endswith('_DIR')):continue
        if name in seen:raise BuildError('Duplicate QtTools cache entry')
        seen[name]=value
    if not required.issubset(seen):raise BuildError('QtTools required cache entries missing')
    expected=no_links(qt).resolve(strict=True);count=0
    for name,value in seen.items():
        if value.endswith('-NOTFOUND'):
            if name in required:raise BuildError('QtTools required dependency not found: '+name)
            continue
        actual=no_links(Path(value)).resolve(strict=True)
        if actual!=expected and expected not in actual.parents:raise BuildError('QtTools cache outside expected prefix: '+name)
        count+=1
    return count

def execute(root=ROOT):
    if sys.platform!='win32' or os.environ.get('VSCMD_ARG_TGT_ARCH','').lower()!='x64':raise BuildError('Windows x64 MSVC developer environment required')
    tools,versions=tools_and_versions();work=create_work(root)
    temp=work/'tmp';temp.mkdir();env=os.environ.copy();env['TEMP']=str(temp);env['TMP']=str(temp);env['DOTNET_GENERATE_ASPNET_CERTIFICATE']='0'
    spec=importlib.util.spec_from_file_location('verified_source_fetch',root/'scripts/fetch_upstream_sources.py');fetch=importlib.util.module_from_spec(spec);sys.modules[spec.name]=fetch;spec.loader.exec_module(fetch)
    try: bundle=fetch.collect(root,root/'.artifacts/upstream-sources'/('source-'+uuid.uuid4().hex))
    except fetch.FetchError as e: raise BuildError('Fixed-source download failed; no completed bundle') from e
    expected={item.name:item.sha256 for item in fetch.CATALOG}
    if expected!={**{name:value[2] for name,value in TARS.items()},'deskflow-v1.27.0-source.zip':DESKFLOW_HASH}:raise BuildError('Unexpected pinned source catalog')
    inputs={name:digest(bundle/name) for name in expected}
    if inputs!=expected:raise BuildError('Verified source bundle mismatch')
    src=work/'src';src.mkdir();sources={}
    for name,(subdir,count,sha) in TARS.items():sources[name]=extract_tar(bundle/name,src,subdir,count,sha)
    openssl=work/'openssl-prefix';qt=work/'qt-prefix'
    os_env=env.copy();os_env['CL']='/FS'
    os_src=sources['openssl-3.5.9.tar.gz']
    run_step('openssl-configure',[tools['perl'],'Configure','VC-WIN64A','no-asm','no-tests','shared','--prefix='+str(openssl),'--openssldir='+str(openssl/'ssl')],os_src,work,os_env)
    run_step('openssl-build',[tools['nmake']],os_src,work,os_env)
    run_step('openssl-install',[tools['nmake'],'install_sw'],os_src,work,os_env)
    qt_env=env.copy();qt_env['PATH']=str(openssl/'bin')+os.pathsep+env.get('PATH','')
    qtbase=sources['qtbase-everywhere-src-6.8.3.tar.xz'];qbuild=work/'qtbase-build'
    run_step('qtbase-configure',[tools['cmake'],'-S',str(qtbase),'-B',str(qbuild),'-G','Ninja','-DCMAKE_C_COMPILER='+tools['cl'],'-DCMAKE_CXX_COMPILER='+tools['cl'],'-DCMAKE_MAKE_PROGRAM='+tools['ninja'],'-DCMAKE_BUILD_TYPE=Release','-DQT_BUILD_TESTS=OFF','-DQT_BUILD_EXAMPLES=OFF','-DCMAKE_INSTALL_PREFIX='+str(qt),'-DOPENSSL_ROOT_DIR='+str(openssl),'-DINPUT_openssl=runtime'],work,work,qt_env)
    count=prefix_cache(qbuild/'CMakeCache.txt',qt,openssl)
    run_step('qtbase-build',[tools['cmake'],'--build',str(qbuild),'--parallel','2'],work,work,qt_env)
    run_step('qtbase-install',[tools['cmake'],'--install',str(qbuild)],work,work,qt_env)
    qt_env['PATH']=str(qt/'bin')+os.pathsep+qt_env['PATH']
    qttools=sources['qttools-everywhere-src-6.8.3.tar.xz'];tbuild=work/'qttools-build'
    run_step('qttools-configure',[tools['cmake'],'-S',str(qttools),'-B',str(tbuild),'-G','Ninja','-DCMAKE_TOOLCHAIN_FILE='+str(qt/'lib/cmake/Qt6/qt.toolchain.cmake'),'-DCMAKE_C_COMPILER='+tools['cl'],'-DCMAKE_CXX_COMPILER='+tools['cl'],'-DCMAKE_MAKE_PROGRAM='+tools['ninja'],'-DCMAKE_INSTALL_PREFIX='+str(qt),'-DCMAKE_PREFIX_PATH='+str(qt),'-DQT_BUILD_EXAMPLES=OFF','-DQT_BUILD_TESTS=OFF','-DCMAKE_BUILD_TYPE=Release'],work,work,qt_env)
    qttools_count=qttools_cache(tbuild/'CMakeCache.txt',qt)
    run_step('qttools-build',[tools['cmake'],'--build',str(tbuild),'--parallel','2'],work,work,qt_env)
    run_step('qttools-install',[tools['cmake'],'--install',str(tbuild)],work,work,qt_env)
    core_parent=root/'.artifacts/native-core-build';no_links(core_parent)
    before=set(core_parent.iterdir()) if core_parent.is_dir() else set()
    try:
        run_step('deskflow-core',[sys.executable,str(root/'scripts/build_deskflow_core.py'),'--archive',str(bundle/'deskflow-v1.27.0-source.zip'),'--qt',str(qt),'--openssl',str(openssl)],work,work,qt_env)
    except BuildError:
        new_dirs=set(core_parent.iterdir())-before if core_parent.is_dir() else set()
        if len(new_dirs)==1:
            logs=sorted(next(iter(new_dirs)).glob('*.log'),key=lambda p:p.stat().st_mtime)
            if logs: print('Last core-step log:',logs[-1].read_bytes()[-32768:].decode('utf-8','replace'),file=sys.stderr,flush=True)
        raise
    after=set(core_parent.iterdir());new=after-before
    if len(new)!=1:raise BuildError('Cannot uniquely identify core build evidence')
    core=next(iter(new));no_links(core);data=json.loads((core/'build-evidence.json').read_text(encoding='utf-8'))
    if data.get('complete') is not True or digest(core/'build/bin/deskflow-core.exe')!=data.get('core_sha256'):raise BuildError('Core evidence or hash mismatch')
    report={'complete':True,'scope':'isolated Windows x64 native core source build; does not install/run/package/establish historical binary equivalence',
            'sources_sha256':inputs,'tool_versions_and_executable_hashes':versions,'platform':platform.platform(),
            'msvc_environment':{k:os.environ.get(k,'') for k in ('VSCMD_VER','VSCMD_ARG_TGT_ARCH','WindowsSDKVersion','VisualStudioVersion')},
            'qtbase_openssl_cache_entries':count,'qttools_qt_cache_entries':qttools_count,'core':data,
            'limitations':['system toolchain supplied by the execution environment','OpenSSL built no-asm no-tests','Qt and QtTools configurations must be reviewed','no device/runtime/installer validation','historical binary equivalence and redistribution not established']}
    with (work/'clean-build-evidence.json').open('x',encoding='utf-8',newline='\n') as f:json.dump(report,f,ensure_ascii=False,indent=2);f.write('\n')
    print('Native source build succeeded; core SHA-256',data['core_sha256'],flush=True)
    print('Evidence only valid if process exits zero and JSON complete is true.',flush=True)
    return work

def main():
    try:execute()
    except (BuildError,OSError,ValueError,tarfile.TarError,subprocess.SubprocessError) as e:
        print('Native build failed:',e,file=sys.stderr);return 1
    return 0
if __name__=='__main__':raise SystemExit(main())
