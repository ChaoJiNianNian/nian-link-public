#!/usr/bin/env python3
"""Validate and print a path-free summary of exactly one successful native build."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import sys

ROOT=Path(__file__).resolve().parents[1]
HEX=re.compile(r'^[0-9a-f]{64}$')
VERSION=re.compile(r'(?<!\d)\d+\.\d+(?:\.\d+){0,2}(?!\d)')
ALLOWED_VERSION=re.compile(r'^\d+(?:\.\d+){1,3}$')
TOOLS=('git','cmake','ninja','perl','nmake','cl')

class EvidenceError(Exception):pass

def ipv4_like(value):
    pieces=value.split('.')
    def octet(part):
        if not part.isascii() or not part.isdecimal():return False
        canonical=part.lstrip('0') or '0'
        return len(canonical)<=3 and int(canonical)<=255
    return len(pieces)==4 and all(octet(part) for part in pieces)

def report_summary(data,expected,expected_patches,*,local_manual=False):
    if not isinstance(data,dict) or data.get('complete') is not True:raise EvidenceError('Incomplete build evidence')
    if data.get('sources_sha256')!=expected:raise EvidenceError('Unexpected source hashes')
    raw=data.get('tool_versions_and_executable_hashes')
    if not isinstance(raw,dict) or set(raw)!=set(TOOLS):raise EvidenceError('Unexpected tool inventory')
    tools={}
    for name in TOOLS:
        item=raw[name]
        if not isinstance(item,dict):raise EvidenceError('Invalid tool entry')
        h=item.get('executable_sha256');text=item.get('output')
        if not isinstance(h,str) or not HEX.fullmatch(h) or not isinstance(text,str) or len(text)>2048:raise EvidenceError('Invalid tool evidence')
        # Never print raw tool output: only a numeric version from its first line.
        match=VERSION.search(text.splitlines()[0] if text else '')
        if not match or ipv4_like(match.group()):raise EvidenceError('Tool version unavailable or ambiguous: '+name)
        tools[name]={'version':match.group(),'executable_sha256':h}
    source_env=data.get('msvc_environment')
    if not isinstance(source_env,dict):raise EvidenceError('Invalid toolchain environment')
    env={}
    for k in ('VSCMD_VER','VSCMD_ARG_TGT_ARCH','WindowsSDKVersion','VisualStudioVersion'):
        v=source_env.get(k)
        if not isinstance(v,str):raise EvidenceError('Missing MSVC environment: '+k)
        v=v.rstrip('\\/')
        if k=='VSCMD_ARG_TGT_ARCH':
            if v.lower()!='x64':raise EvidenceError('Not x64')
            env[k]='x64'
        else:
            if local_manual and v=='':
                env[k]=None
            else:
                if not ALLOWED_VERSION.fullmatch(v) or ipv4_like(v):raise EvidenceError('Invalid MSVC environment: '+k)
                env[k]=v
    counts={}
    for key in ('qtbase_openssl_cache_entries','qttools_qt_cache_entries'):
        value=data.get(key)
        if type(value) is not int or value<=0 or value>1000:raise EvidenceError('Invalid cache evidence')
        counts[key]=value
    core=data.get('core')
    if not isinstance(core,dict) or core.get('complete') is not True:raise EvidenceError('Incomplete core evidence')
    if core.get('source_sha256')!=expected['deskflow-v1.27.0-source.zip'] or core.get('patches')!=expected_patches:
        raise EvidenceError('Unexpected core source or patches')
    digest,size=core.get('core_sha256'),core.get('core_size')
    if not isinstance(digest,str) or not HEX.fullmatch(digest) or type(size) is not int or not (0<size<100_000_000):raise EvidenceError('Invalid core evidence')
    reported_only='reported JSON only; core and patches not rehashed, process exit not independently verified'
    ipv4_rejection='IPv4-like four-part numeric versions are conservatively rejected'
    limitations=['execution-environment-supplied toolchain',reported_only,ipv4_rejection,'core not run or packaged','historical equivalence and release compliance not established']
    if local_manual:limitations=['manually initialized toolchain','x64 environment marker supplied by caller; not proof of VsDevCmd','not a clean VM or GitHub runner',reported_only,ipv4_rejection,'core not run or packaged','historical equivalence and release compliance not established']
    return {'schema':'nian-native-local-summary-v1' if local_manual else 'nian-native-clean-summary-v1','complete':True,'sources_sha256':expected,'tools':tools,
            'msvc_environment':env,'dependency_path_checks':counts,'patches':expected_patches,'core_sha256':digest,'core_size':size,
            'limitations':limitations}

def main(argv=()):
    if list(argv) not in ([], ['--local-manual']):
        print('Unknown arguments.',file=sys.stderr);return 1
    local_manual=list(argv)==['--local-manual']
    if local_manual and os.environ.get('GITHUB_ACTIONS','').lower()=='true':
        print('Local manual mode is not available in GitHub Actions.',file=sys.stderr);return 1
    try:
        spec=importlib.util.spec_from_file_location('native_builder',ROOT/'scripts/github_native_clean_build.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
        expected={**{name:value[2] for name,value in m.TARS.items()},'deskflow-v1.27.0-source.zip':m.DESKFLOW_HASH}
        core_spec=importlib.util.spec_from_file_location('core_builder',ROOT/'scripts/build_deskflow_core.py');c=importlib.util.module_from_spec(core_spec);core_spec.loader.exec_module(c)
        expected_patches=[{'name':name,'sha256':digest} for name,digest in c.PATCHES]
        parent=m.no_links(ROOT/'.artifacts/native-src')
        candidates=sorted(p for p in parent.glob('run-*') if p.is_dir())
        if len(candidates)!=1:raise EvidenceError('Expected exactly one fresh build directory')
        file=m.no_links(candidates[0]/'clean-build-evidence.json')
        if not file.is_file() or file.stat().st_size>256*1024:raise EvidenceError('Build evidence missing or too large')
        data=json.loads(file.read_text(encoding='utf-8'));summary=report_summary(data,expected,expected_patches,local_manual=local_manual)
    except Exception:
        print('Cannot print native evidence.',file=sys.stderr);return 1
    print(json.dumps(summary,ensure_ascii=True,sort_keys=True,indent=2));return 0
if __name__=='__main__':raise SystemExit(main(sys.argv[1:]))
