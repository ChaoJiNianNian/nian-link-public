import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('qt_config_summary',ROOT/'scripts/qt_config_summary.py');m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)

class QtConfigSummaryTests(unittest.TestCase):
    def test_only_allowlisted_features_and_hash_are_printed(self):
        raw=b'  OpenSSL .............. yes\n  Using system zlib .... no\nC:\\Users\\private ghp_secret ........... yes\n'
        out=m.parse_config(raw,m.BASE_LABELS)
        self.assertIs(out['features']['OpenSSL'],True);self.assertIs(out['features']['Using system zlib'],False)
        self.assertIsNone(out['features']['PCRE2']);self.assertEqual(out['sha256'],hashlib.sha256(raw).hexdigest())
        txt=json.dumps(out);self.assertNotIn('private',txt);self.assertNotIn('ghp_secret',txt)

    def test_duplicate_invalid_encoding_and_nul_fail(self):
        for raw in (b'  OpenSSL ... yes\n  OpenSSL ... no\n',b'  OpenSSL ... yes\n  OpenSSL ... maybe\n',b'  OpenSSL ... maybe\n  OpenSSL ... yes\n',b'  OpenSSL ... yes\n  OpenSSL .. no\n',b'  OpenSSL ... yes\n  OpenSSL ...\n',b'\xff',b'abc\x00def'):
            with self.subTest(data=raw),self.assertRaises(m.EvidenceError):m.parse_config(raw,m.BASE_LABELS)

    def test_non_boolean_and_unknown_are_not_inferred(self):
        with self.assertRaises(m.EvidenceError):m.parse_config(b'  OpenSSL ... maybe\n',m.BASE_LABELS)
        out=m.parse_config(b'Qt Network:\n  Unknown ... yes\n',m.BASE_LABELS)
        self.assertIsNone(out['features']['OpenSSL']);self.assertNotIn('Unknown',out['features'])

    def test_missing_modules_fail_without_path_or_sensitive_error_text(self):
        with tempfile.TemporaryDirectory() as d,patch.object(m,'ROOT',Path(d)),contextlib.redirect_stderr(io.StringIO()) as out:
            self.assertEqual(m.main(),1)
            self.assertNotIn(str(d),out.getvalue());self.assertNotIn('Traceback',out.getvalue())

    def test_cli_requires_exactly_one_completed_build_and_prints_no_path(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);(root/'scripts').mkdir()
            for name in ('github_native_clean_build.py','build_deskflow_core.py','print_native_clean_evidence.py'):shutil.copyfile(ROOT/'scripts'/name,root/'scripts'/name)
            spec=importlib.util.spec_from_file_location('native',root/'scripts/github_native_clean_build.py');n=importlib.util.module_from_spec(spec);spec.loader.exec_module(n)
            spec=importlib.util.spec_from_file_location('core',root/'scripts/build_deskflow_core.py');c=importlib.util.module_from_spec(spec);spec.loader.exec_module(c)
            expected={**{name:item[2] for name,item in n.TARS.items()},'deskflow-v1.27.0-source.zip':n.DESKFLOW_HASH}
            patches=[{'name':name,'sha256':digest} for name,digest in c.PATCHES]
            evidence={'complete':True,'sources_sha256':expected,'tool_versions_and_executable_hashes':{name:{'output':'version 1.2','executable_sha256':'a'*64} for name in ('git','cmake','ninja','perl','nmake','cl')},
              'msvc_environment':{'VSCMD_VER':'17.1','VSCMD_ARG_TGT_ARCH':'x64','WindowsSDKVersion':'10.0','VisualStudioVersion':'17.0'},'qtbase_openssl_cache_entries':4,'qttools_qt_cache_entries':5,
              'core':{'complete':True,'source_sha256':n.DESKFLOW_HASH,'patches':patches,'core_sha256':'b'*64,'core_size':1}}
            base=root/'.artifacts/native-src';work=base/('run-'+'a'*32);work.mkdir(parents=True)
            (work/'clean-build-evidence.json').write_text(json.dumps(evidence),encoding='utf-8')
            for name in ('qtbase-build','qttools-build'):
                p=work/name;p.mkdir()
                entries='  Building shared libraries ... yes\n  OpenSSL ... yes\n  OpenSSL 3.0 ... yes\n  Qt Network ... yes\n  Qt Widgets ... yes\n' if name=='qtbase-build' else '  Qt Linguist ... yes\n'
                (p/'config.summary').write_text(entries+'C:\\Users\\private token_secret\n',encoding='utf-8')
            with patch.object(m,'ROOT',root),contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(m.main(),0);obj=json.loads(out.getvalue());self.assertEqual(obj['schema'],'nian-qt-config-summary-v1')
                self.assertNotIn('private',out.getvalue());self.assertNotIn('token_secret',out.getvalue())
                self.assertIn('core/patches not rehashed and process exit not independently verified',obj['scope'])
            for key in ('VSCMD_VER','WindowsSDKVersion','VisualStudioVersion'): evidence['msvc_environment'][key]=''
            (work/'clean-build-evidence.json').write_text(json.dumps(evidence),encoding='utf-8')
            with patch.object(m,'ROOT',root),contextlib.redirect_stderr(io.StringIO()):self.assertEqual(m.main(),1)
            with patch.object(m,'ROOT',root),patch.dict(os.environ,{'GITHUB_ACTIONS':'false'}),contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(m.main(['--local-manual']),0)
                result=json.loads(out.getvalue())
                self.assertEqual(result['schema'],'nian-qt-config-local-summary-v1')
                self.assertIn('core/patches not rehashed and process exit not independently verified',result['scope'])
            with patch.object(m,'ROOT',root),patch.dict(os.environ,{'GITHUB_ACTIONS':'true'}),contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(m.main(['--local-manual']),1)
            evidence['msvc_environment'].update({'VSCMD_VER':'17.1','WindowsSDKVersion':'10.0','VisualStudioVersion':'17.0'})
            (work/'clean-build-evidence.json').write_text(json.dumps(evidence),encoding='utf-8')
            evidence['tool_versions_and_executable_hashes']['git']['output']='version ' + '.'.join(('192','0','2','42'))
            (work/'clean-build-evidence.json').write_text(json.dumps(evidence),encoding='utf-8')
            for args in ([],['--local-manual']):
                with self.subTest(args=args),patch.object(m,'ROOT',root),patch.dict(os.environ,{'GITHUB_ACTIONS':'false'}),contextlib.redirect_stdout(io.StringIO()) as out,contextlib.redirect_stderr(io.StringIO()) as err:
                    self.assertEqual(m.main(args),1)
                    self.assertNotIn('.'.join(('192','0','2','42')),out.getvalue()+err.getvalue())
            evidence['tool_versions_and_executable_hashes']['git']['output']='version 1.2'
            (work/'clean-build-evidence.json').write_text(json.dumps(evidence),encoding='utf-8')
            with patch.object(m,'ROOT',root),patch.object(Path,'read_bytes',side_effect=OSError('C:\\Users\\private token_secret')),contextlib.redirect_stderr(io.StringIO()) as err:
                self.assertEqual(m.main(),1);self.assertNotIn('private',err.getvalue());self.assertNotIn('token_secret',err.getvalue())
            (work/'qttools-build/config.summary').write_text('  Unknown ... yes',encoding='utf-8')
            with patch.object(m,'ROOT',root),contextlib.redirect_stderr(io.StringIO()):self.assertEqual(m.main(),1)
            (work/'qttools-build/config.summary').write_text('  Qt Linguist ... no',encoding='utf-8')
            with patch.object(m,'ROOT',root),contextlib.redirect_stderr(io.StringIO()):self.assertEqual(m.main(),1)
            evidence['core']['patches']=patches[:-1]
            (work/'clean-build-evidence.json').write_text(json.dumps(evidence),encoding='utf-8')
            with patch.object(m,'ROOT',root),contextlib.redirect_stderr(io.StringIO()):self.assertEqual(m.main(),1)
            (base/('run-'+'b'*32)).mkdir()
            with patch.object(m,'ROOT',root),contextlib.redirect_stderr(io.StringIO()):self.assertEqual(m.main(),1)

if __name__=='__main__':unittest.main()
