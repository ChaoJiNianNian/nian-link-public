#!/usr/bin/env python3
"""Print a path-free, allowlisted snapshot of Qt configure feature evidence."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import sys

ROOT=Path(__file__).resolve().parents[1]
BASE_LABELS=(
 'Building shared libraries','Qt D-Bus directly linked to libdbus','OpenSSL','Qt directly linked to OpenSSL','OpenSSL 1.1','OpenSSL 3.0',
 'Using system zlib','Zstandard support','DoubleConversion','Using system DoubleConversion','PCRE2','Using system PCRE2',
 'FreeType','Using system FreeType','HarfBuzz','Using system HarfBuzz','JPEG','Using system libjpeg','PNG','Using system libpng',
 'MarkdownReader','Using system libmd4c','SQLite','Using system provided SQLite','Qt Network','Qt Widgets',
)
TOOLS_LABELS=('Qt Assistant','libclang found','Clang-based lupdate parser','Qt Widgets Designer','Qt Linguist','qdbus','QDoc',
              'Qt Attributions Scanner','qtdiag','qtplugininfo')
ENTRY=re.compile(r'^\s*(.*?)\s+(\.+)(?:\s+(.*?))?\s*$')
class EvidenceError(Exception):pass

def parse_config(raw,labels):
    if len(raw)>2*1024*1024 or b'\x00' in raw:raise EvidenceError('Invalid configure summary')
    try:text=raw.decode('utf-8')
    except UnicodeDecodeError as e:raise EvidenceError('Invalid configure encoding') from e
    values={name:None for name in labels};seen=set()
    for line in text.splitlines():
        found=ENTRY.fullmatch(line)
        if not found:continue
        label,dots,value=found.groups()
        if label not in values:continue
        if label in seen:raise EvidenceError('Duplicate configure feature')
        seen.add(label)
        if len(dots)<3 or value not in ('yes','no'):raise EvidenceError('Invalid configure feature value')
        values[label]=(value=='yes')
    return {'sha256':hashlib.sha256(raw).hexdigest(),'features':values}

def main(argv=()):
    if list(argv) not in ([], ['--local-manual']):
        print('Unknown arguments.',file=sys.stderr);return 1
    local_manual=list(argv)==['--local-manual']
    if local_manual and os.environ.get('GITHUB_ACTIONS','').lower()=='true':
        print('Local manual mode is not available in GitHub Actions.',file=sys.stderr);return 1
    stage='modules'
    try:
        sp=importlib.util.spec_from_file_location('native_builder',ROOT/'scripts/github_native_clean_build.py');m=importlib.util.module_from_spec(sp);sp.loader.exec_module(m)
        sp=importlib.util.spec_from_file_location('core_builder',ROOT/'scripts/build_deskflow_core.py');c=importlib.util.module_from_spec(sp);sp.loader.exec_module(c)
        sp=importlib.util.spec_from_file_location('native_evidence',ROOT/'scripts/print_native_clean_evidence.py');e=importlib.util.module_from_spec(sp);sp.loader.exec_module(e)
        expected={**{name:value[2] for name,value in m.TARS.items()},'deskflow-v1.27.0-source.zip':m.DESKFLOW_HASH}
        expected_patches=[{'name':name,'sha256':digest} for name,digest in c.PATCHES]
        stage='build evidence'
        parent=m.no_links(ROOT/'.artifacts/native-src');builds=sorted(p for p in parent.glob('run-*') if p.is_dir())
        if len(builds)!=1:raise EvidenceError('Expected exactly one build directory')
        build=m.no_links(builds[0]);evidence=m.no_links(build/'clean-build-evidence.json')
        if not evidence.is_file() or evidence.stat().st_size>256*1024:raise EvidenceError('Build evidence missing or too large')
        data=json.loads(evidence.read_text(encoding='utf-8'))
        validated=e.report_summary(data,expected,expected_patches,local_manual=local_manual)
        result={'schema':'nian-qt-config-local-summary-v1' if local_manual else 'nian-qt-config-summary-v1','core_sha256':validated['core_sha256'],
                'scope':('allowlisted configure summaries; core/patches not rehashed and process exit not independently verified; not an SBOM or license conclusion' + ('; manual local environment, not GitHub CI' if local_manual else ''))}
        for key,rel,labels,required in (
            ('qtbase','qtbase-build/config.summary',BASE_LABELS,('Building shared libraries','OpenSSL','OpenSSL 3.0','Qt Network','Qt Widgets')),
            ('qttools','qttools-build/config.summary',TOOLS_LABELS,('Qt Linguist',))):
            stage=key+' configure summary'
            path=m.no_links(build/rel)
            if not path.is_file() or path.stat().st_size>2*1024*1024:raise EvidenceError('Configure summary missing or too large')
            parsed=parse_config(path.read_bytes(),labels)
            if any(parsed['features'][name] is not True for name in required):raise EvidenceError('Required configure feature missing or disabled')
            result[key]=parsed
    except Exception:
        print('Cannot print Qt configure evidence at '+stage+'.',file=sys.stderr);return 1
    print(json.dumps(result,sort_keys=True,ensure_ascii=True,indent=2));return 0
if __name__=='__main__':raise SystemExit(main(sys.argv[1:]))
