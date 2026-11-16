import importlib.util
import io
import os
import sys
from pathlib import Path
import stat
import tarfile
import tempfile
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('native_clean',ROOT/'scripts/github_native_clean_build.py')
native=importlib.util.module_from_spec(spec);spec.loader.exec_module(native)

class NativeCleanBuildTests(unittest.TestCase):
    def test_completed_local_build_does_not_claim_runner_image_toolchain(self):
        """Exercise emitted evidence using simulated stages, without running a build."""
        import json
        from types import SimpleNamespace
        expected = {name: value[2] for name, value in native.TARS.items()}
        expected['deskflow-v1.27.0-source.zip'] = native.DESKFLOW_HASH
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            work = root / 'work'
            work.mkdir()
            bundle = root / 'download'
            bundle.mkdir()
            core = root / '.artifacts/native-core-build/build-synthetic'
            core_hash = 'b' * 64

            def run_step(name, *args):
                if name == 'deskflow-core':
                    (core / 'build/bin').mkdir(parents=True)
                    (core / 'build/bin/deskflow-core.exe').write_bytes(b'synthetic core')
                    (core / 'build-evidence.json').write_text(json.dumps({
                        'complete': True, 'core_sha256': core_hash,
                    }), encoding='utf-8')

            def digest(path):
                return core_hash if Path(path).name == 'deskflow-core.exe' else expected[Path(path).name]

            fetch = SimpleNamespace(
                CATALOG=[SimpleNamespace(name=k, sha256=v) for k, v in expected.items()],
                collect=lambda *_: bundle, FetchError=RuntimeError,
            )
            spec = SimpleNamespace(name='fake_source_fetch', loader=SimpleNamespace(exec_module=lambda _: None))
            with patch.dict(native.sys.modules, {}), \
                 patch.object(native.sys, 'platform', 'win32'), \
                 patch.dict(native.os.environ, {'VSCMD_ARG_TGT_ARCH': 'x64'}, clear=True), \
                 patch.object(native, 'tools_and_versions', return_value=(
                     {n: n for n in ('perl', 'nmake', 'cmake', 'ninja', 'cl')}, {})), \
                 patch.object(native, 'create_work', return_value=work), \
                 patch.object(native.importlib.util, 'spec_from_file_location', return_value=spec), \
                 patch.object(native.importlib.util, 'module_from_spec', return_value=fetch), \
                 patch.object(native, 'digest', side_effect=digest), \
                 patch.object(native, 'extract_tar', side_effect=lambda _p, s, subdir, *_: s / subdir), \
                 patch.object(native, 'run_step', side_effect=run_step), \
                 patch.object(native, 'prefix_cache', return_value=4), \
                 patch.object(native, 'qttools_cache', return_value=5), \
                 patch.object(native, 'no_links'):
                native.execute(root)

            evidence = json.loads((work / 'clean-build-evidence.json').read_text(encoding='utf-8'))
            self.assertTrue(evidence['complete'])
            self.assertIn('system toolchain supplied by the execution environment', evidence['limitations'])
            self.assertNotIn('system toolchain comes from runner image', evidence['limitations'])

    def test_tool_versions_prioritize_msvc_stderr_without_hiding_other_stdout(self):
        from types import SimpleNamespace
        def fake_run(args,**kwargs):
            n=Path(args[0]).name
            if n in ('cl','nmake'):
                return SimpleNamespace(stdout='Usage: no version\n' + 'x'*3000, stderr='Microsoft Tool Version 19.44.35229\n')
            return SimpleNamespace(stdout='Tool version 5.34.3\n', stderr='Warning: no version\n')
        with patch.object(native.shutil,'which',side_effect=lambda n:n),patch.object(native.subprocess,'run',side_effect=fake_run),patch.object(native,'digest',return_value='a'*64):
            _,versions=native.tools_and_versions()
        for name in ('cl','nmake'):
            self.assertEqual(versions[name]['output'].splitlines()[0], 'Microsoft Tool Version 19.44.35229')
        for name in ('git','cmake','ninja','perl'):
            self.assertEqual(versions[name]['output'].splitlines()[0], 'Tool version 5.34.3')

    def item(self,name,typ=tarfile.REGTYPE,size=0):
        m=tarfile.TarInfo(name);m.type=typ;m.size=size;return m

    def test_rejects_traversal_drive_reserved_backslash_and_special_files(self):
        for path in ('../escape','source/../escape','source/C:escape','source/NUL.txt','source/a\\b'):
            with self.subTest(path=path),self.assertRaises(native.BuildError):native.safe_members([self.item(path)],'source',1)
        for typ in (tarfile.SYMTYPE,tarfile.LNKTYPE,tarfile.CHRTYPE,tarfile.FIFOTYPE):
            with self.subTest(typ=typ),self.assertRaises(native.BuildError):native.safe_members([self.item('source/x',typ)],'source',1)

    def test_rejects_duplicate_paths_count_and_size_limits(self):
        with self.assertRaises(native.BuildError):native.safe_members([self.item('source/A'),self.item('source/a')],'source',2)
        with self.assertRaises(native.BuildError):native.safe_members([self.item('source/a')],'source',2)
        with self.assertRaises(native.BuildError):native.safe_members([self.item('source/a',size=1024**3+1)],'source',1)
        self.assertEqual(native.safe_members([self.item('source/a',size=8)],'source',1),8)

    def test_hash_mismatch_prevents_extraction(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);p=root/'bad.tar';p.write_bytes(b'not source')
            with self.assertRaisesRegex(native.BuildError,'SHA-256'):native.extract_tar(p,root,'source',1,'0'*64)
            self.assertEqual(sorted(x.name for x in root.iterdir()),['bad.tar'])

    def test_overlong_checkout_fails_before_creating_output(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/('long-folder-'+'a'*25)/('long-folder-'+'b'*25);root.mkdir(parents=True)
            class Disk:free=9*1024**3
            with patch.object(native.shutil,'disk_usage',return_value=Disk()):
                with self.assertRaisesRegex(native.BuildError,'path too long'):native.create_work(root)
            self.assertFalse((root/'.artifacts').exists())

    def test_known_path_boundary_accepts_250_and_rejects_251(self):
        root=Path('D:/x')
        probe=root/'.artifacts/native-src'/('run-'+'0'*32)/'qtbase-build'/native.KNOWN_QT_OBJECT
        n=250-native.windows_path_units(probe)-1
        self.assertGreater(n,0)
        accepted=root/('a'*n);native.validate_work_path(accepted)
        rejected=root/('a'*(n+1))
        with self.assertRaisesRegex(native.BuildError,'path too long'):native.validate_work_path(rejected)

    def test_windows_path_limit_counts_utf16_code_units(self):
        self.assertEqual(native.windows_path_units('a\U0001f600'),3)

    def test_new_directory_not_reused_and_no_recursive_cleanup(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)
            class Id:hex='0'*32
            class Disk:free=9*1024**3
            with patch.object(native.uuid,'uuid4',return_value=Id()),patch.object(native.shutil,'disk_usage',return_value=Disk()),patch.object(native,'validate_work_path'):
                work=native.create_work(root);(work/'sentinel').write_text('untouched')
                with self.assertRaises(FileExistsError):native.create_work(root)
                self.assertEqual((work/'sentinel').read_text(),'untouched')

    def test_links_and_low_disk_space_fail_closed(self):
        from types import SimpleNamespace
        with patch.object(Path,'lstat',return_value=SimpleNamespace(st_mode=stat.S_IFDIR,st_file_attributes=0x400)):
            with self.assertRaises(native.BuildError):native.no_links(Path('synthetic'))
        with tempfile.TemporaryDirectory() as d:
            class Disk:free=1024
            with patch.object(native.shutil,'disk_usage',return_value=Disk()),patch.object(native,'validate_work_path'):
                with self.assertRaisesRegex(native.BuildError,'free space'):native.create_work(Path(d))
            self.assertFalse((Path(d)/'.artifacts').exists())

    def test_qttools_prefix_rejects_system_or_missing_required(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);qt=root/'qt';outside=root/'outside';qt.mkdir();outside.mkdir();names=['Qt6_DIR','Qt6Core_DIR','Qt6Gui_DIR','Qt6Network_DIR','Qt6Widgets_DIR']
            dirs={n:qt/n for n in names}
            for p in dirs.values():p.mkdir()
            cache=root/'CMakeCache.txt'
            def write(v):cache.write_text('\n'.join(k+':PATH='+str(p) for k,p in v.items()),encoding='utf-8')
            write(dirs);self.assertEqual(native.qttools_cache(cache,qt),5)
            bad=dict(dirs);bad['Qt6Core_DIR']=outside;write(bad)
            with self.assertRaises(native.BuildError):native.qttools_cache(cache,qt)
            bad=dict(dirs);bad['Qt6Core_DIR']='Qt6Core_DIR-NOTFOUND';write(bad)
            with self.assertRaises(native.BuildError):native.qttools_cache(cache,qt)
            write(dirs);cache.write_text(cache.read_text(encoding='utf-8')+'\nQt6Qml_DIR:PATH=Qt6Qml_DIR-NOTFOUND',encoding='utf-8')
            self.assertEqual(native.qttools_cache(cache,qt),5)

    def test_openssl_prefix_rejects_external_inputs(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);ssl=root/'ssl';outside=root/'outside';ssl.mkdir();outside.mkdir()
            names=['OPENSSL_INCLUDE_DIR','LIB_EAY_RELEASE','SSL_EAY_RELEASE','OPENSSL_APPLINK_SOURCE'];paths={n:ssl/n for n in names}
            for p in paths.values():p.mkdir()
            cache=root/'CMakeCache.txt'
            def write(v):cache.write_text('\n'.join(k+':PATH='+str(p) for k,p in v.items()),encoding='utf-8')
            write(paths);self.assertEqual(native.prefix_cache(cache,root,ssl),4)
            bad=dict(paths);bad['OPENSSL_APPLINK_SOURCE']=outside;write(bad)
            with self.assertRaises(native.BuildError):native.prefix_cache(cache,root,ssl)

    def test_manual_workflow_is_read_only_and_never_uses_cache_or_publishes(self):
        s=(ROOT/'.github/workflows/native-clean-manual.yml').read_text(encoding='utf-8')
        self.assertIn('workflow_dispatch:',s);self.assertNotIn('push:',s);self.assertNotIn('pull_request:',s)
        self.assertIn('contents: read',s);self.assertIn('persist-credentials: false',s)
        self.assertIn('DOTNET_GENERATE_ASPNET_CERTIFICATE:',s)
        self.assertIn('github_native_clean_build.py',s)
        self.assertIn('NIAN_VS_PATH_FILE',s)
        self.assertIn('set /p "NIAN_VS_ROOT="<',s)
        self.assertNotIn('for /f',s.lower())
        self.assertIn('print_native_clean_evidence.py',s)
        for bad in ('actions/cache','upload-artifact','gh release','workflow_run'):self.assertNotIn(bad,s)

    @unittest.skipUnless(sys.platform=='win32' and os.environ.get('COMSPEC'), 'Windows cmd is required')
    def test_actual_workflow_bootstrap_with_spaced_paths_and_errors(self):
        import os,subprocess
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);tool=root/'Tool With Spaces (x86)'/'fake-where.exe';tool.parent.mkdir()
            vs=root/'Visual Studio (x86)';dev=vs/'Common7/Tools/VsDevCmd.bat';dev.parent.mkdir(parents=True)
            dev.write_bytes(b'@echo off\r\nif "%NIAN_FAKE_DEV_MODE%"=="fail" exit /b 9\r\nif not "%*"=="-arch=x64 -host_arch=x64" exit /b 8\r\nexit /b 0\r\n')
            missing=root/'Missing Visual Studio (x86)'
            csc=Path(os.environ['WINDIR'])/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
            if not csc.is_file():self.skipTest('Framework C# compiler unavailable')
            source=root/'fake.cs'
            code=r'''using System; class Program { static int Main(string[] a) {
                string mode=Environment.GetEnvironmentVariable("NIAN_FAKE_WHERE_MODE");
                if(mode=="fail") return 7; if(mode=="empty") return 0;
                int i=Array.IndexOf(a,"-version"); if(i<0 || i+1>=a.Length || a[i+1]!="[17.0,18.0)") return 8;
                Console.WriteLine(mode=="missing_dev" ? @"MISSING_PATH" : @"VS_PATH"); return 0; } }'''
            code=code.replace('MISSING_PATH',str(missing)).replace('VS_PATH',str(vs))
            source.write_text(code,encoding='utf-8')
            compiled=subprocess.run([str(csc),'/nologo','/out:'+str(tool),str(source)],capture_output=True,text=True,timeout=30)
            self.assertEqual(compiled.returncode,0,compiled.stderr+compiled.stdout)
            workflow=(ROOT/'.github/workflows/native-clean-manual.yml').read_text(encoding='utf-8')
            start=workflow.index('          echo [native-ci] Checking runner temp')
            end=workflow.index('          python -B scripts/github_native_clean_build.py',start)
            snippet='\r\n'.join(line[10:] for line in workflow[start:end].splitlines())+'\r\n'
            token=r'"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"'
            self.assertEqual(snippet.count(token),2)
            temp=root/'Runner Temp (x86)';temp.mkdir()
            cases=(('good','',False,False,'PATH_OK'),('tool_failure','fail',False,False,'vswhere returned a nonzero exit code'),
                   ('empty','empty',False,False,'no matching Visual Studio 2022'),('existing','',True,False,'VS discovery file already exists'),
                   ('tool_missing','',False,False,'vswhere executable missing'),('missing_dev','missing_dev',False,False,'VsDevCmd.bat missing'),
                   ('dev_failure','',False,True,'VsDevCmd initialization failed'),
                   ('temp_missing','',False,False,'RUNNER_TEMP missing'),('run_missing','',False,False,'GITHUB_RUN_ID missing'),
                   ('attempt_missing','',False,False,'GITHUB_RUN_ATTEMPT missing'))
            for index,(name,mode,preexisting,devfail,expect) in enumerate(cases,1):
                with self.subTest(case=name):
                    env=os.environ.copy();env.update({'RUNNER_TEMP':str(temp),'GITHUB_RUN_ID':str(index),'GITHUB_RUN_ATTEMPT':'1',
                        'NIAN_FAKE_WHERE_MODE':mode,'NIAN_FAKE_DEV_MODE':'fail' if devfail else ''})
                    missing_env={'temp_missing':'RUNNER_TEMP','run_missing':'GITHUB_RUN_ID','attempt_missing':'GITHUB_RUN_ATTEMPT'}
                    if name in missing_env:env.pop(missing_env[name],None)
                    output=temp/('nian-vs-path-'+str(index)+'-1.txt')
                    if preexisting:output.write_text('sentinel',encoding='ascii')
                    path=tool if name!='tool_missing' else root/'Missing (x86)'/'fake-where.exe'
                    body=snippet.replace(token,'"'+str(path)+'"')
                    batch=root/(name+'.cmd');batch.write_bytes(('@echo off\r\n'+body+'echo PATH_OK\r\nexit /b 0\r\n').encode('utf-8'))
                    ran=subprocess.run([os.environ['COMSPEC'],'/d','/c',str(batch)],env=env,capture_output=True,text=True,timeout=30)
                    if name=='good':self.assertEqual(ran.returncode,0,ran.stdout+ran.stderr)
                    else:self.assertNotEqual(ran.returncode,0)
                    self.assertIn(expect,ran.stdout)
                    if preexisting:self.assertEqual(output.read_text(encoding='ascii'),'sentinel')

class NativeEvidenceSummaryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        sp=importlib.util.spec_from_file_location('summary',ROOT/'scripts/print_native_clean_evidence.py');cls.m=importlib.util.module_from_spec(sp);sp.loader.exec_module(cls.m)
        sp=importlib.util.spec_from_file_location('core',ROOT/'scripts/build_deskflow_core.py');c=importlib.util.module_from_spec(sp);sp.loader.exec_module(c)
        cls.patches=[{'name':name,'sha256':digest} for name,digest in c.PATCHES]
        cls.expected={**{name:item[2] for name,item in native.TARS.items()},'deskflow-v1.27.0-source.zip':native.DESKFLOW_HASH}

    def fixture(self):
        tools={name:{'output':'Tool 19.44.35229 C:\\Users\\private ghp_secret', 'executable_sha256':'a'*64} for name in self.m.TOOLS}
        return {'complete':True,'sources_sha256':self.expected,'tool_versions_and_executable_hashes':tools,
                'msvc_environment':{'VSCMD_VER':'17.14.0','VSCMD_ARG_TGT_ARCH':'x64','WindowsSDKVersion':'10.0.26100.0\\','VisualStudioVersion':'17.0'},
                'qtbase_openssl_cache_entries':4,'qttools_qt_cache_entries':17,
                'core':{'complete':True,'source_sha256':native.DESKFLOW_HASH,'patches':self.patches,'core_sha256':'b'*64,'core_size':1118208}}

    def test_summary_omits_paths_tokens_and_raw_outputs(self):
        import json
        out=self.m.report_summary(self.fixture(),self.expected,self.patches)
        raw=json.dumps(out)
        self.assertNotIn('private',raw);self.assertNotIn('ghp_secret',raw);self.assertNotIn('Users',raw)
        self.assertEqual(out['tools']['cl']['version'],'19.44.35229')
        self.assertEqual(out['patches'],self.patches)
        self.assertIn('execution-environment-supplied toolchain',out['limitations'])
        self.assertNotIn('runner-supplied system toolchain',out['limitations'])
        self.assertIn('reported JSON only; core and patches not rehashed, process exit not independently verified',out['limitations'])
        self.assertIn('IPv4-like four-part numeric versions are conservatively rejected',out['limitations'])

    def test_wrong_or_missing_patch_and_incomplete_source_fail(self):
        f=self.fixture();f['core']['patches']=self.patches[:-1]
        with self.assertRaises(self.m.EvidenceError):self.m.report_summary(f,self.expected,self.patches)
        f=self.fixture();f['core']['source_sha256']='0'*64
        with self.assertRaises(self.m.EvidenceError):self.m.report_summary(f,self.expected,self.patches)
        f=self.fixture();f['complete']=False
        with self.assertRaises(self.m.EvidenceError):self.m.report_summary(f,self.expected,self.patches)

    def test_cli_requires_exactly_one_complete_evidence_and_prints_safely(self):
        import contextlib,io,json,shutil
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);(root/'scripts').mkdir()
            for name in ('github_native_clean_build.py','build_deskflow_core.py'):
                shutil.copyfile(ROOT/'scripts'/name,root/'scripts'/name)
            parent=root/'.artifacts/native-src';candidate=parent/('run-'+'a'*32);candidate.mkdir(parents=True)
            evidence=candidate/'clean-build-evidence.json';evidence.write_text(json.dumps(self.fixture()),encoding='utf-8')
            with patch.object(self.m,'ROOT',root),contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(self.m.main(),0)
                self.assertTrue(json.loads(out.getvalue())['complete']);self.assertNotIn('private',out.getvalue())
            (parent/('run-'+'b'*32)).mkdir()
            with patch.object(self.m,'ROOT',root),contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(self.m.main(),1)

    def test_environment_path_and_unparseable_version_rejected(self):
        f=self.fixture();f['msvc_environment']['WindowsSDKVersion']='C:\\Users\\private'
        with self.assertRaises(self.m.EvidenceError):self.m.report_summary(f,self.expected,self.patches)
        f=self.fixture();f['msvc_environment']['VSCMD_VER']='ghp_secret'
        with self.assertRaises(self.m.EvidenceError):self.m.report_summary(f,self.expected,self.patches)
        f=self.fixture();f['tool_versions_and_executable_hashes']['git']['output']='unknown'
        with self.assertRaises(self.m.EvidenceError):self.m.report_summary(f,self.expected,self.patches)

    def test_ipv4_like_versions_are_rejected_in_tool_and_environment(self):
        for local in (False,True):
            f=self.fixture();f['tool_versions_and_executable_hashes']['git']['output']='Tool ' + '.'.join(('192','0','2','42'))
            with self.subTest(local=local,where='tool'),self.assertRaises(self.m.EvidenceError):
                self.m.report_summary(f,self.expected,self.patches,local_manual=local)
            for name in ('VSCMD_VER','WindowsSDKVersion','VisualStudioVersion'):
                f=self.fixture();f['msvc_environment'][name]='.'.join(('192','0','2','42'))
                with self.subTest(local=local,where=name),self.assertRaises(self.m.EvidenceError):
                    self.m.report_summary(f,self.expected,self.patches,local_manual=local)
        # Conservatively reject any four numeric parts in IPv4 range, even if
        # they could also represent a legitimate version number.
        f=self.fixture();f['msvc_environment']['VSCMD_VER']='.'.join(('1','2','3','4'))
        with self.assertRaises(self.m.EvidenceError):self.m.report_summary(f,self.expected,self.patches)

    def test_manual_local_requires_explicit_mode_and_never_invents_versions(self):
        f=self.fixture()
        for key in ('VSCMD_VER','WindowsSDKVersion','VisualStudioVersion'):
            f['msvc_environment'][key]=''
        with self.assertRaises(self.m.EvidenceError):self.m.report_summary(f,self.expected,self.patches)
        out=self.m.report_summary(f,self.expected,self.patches,local_manual=True)
        self.assertEqual(out['schema'],'nian-native-local-summary-v1')
        self.assertEqual(out['msvc_environment']['VSCMD_ARG_TGT_ARCH'],'x64')
        for key in ('VSCMD_VER','WindowsSDKVersion','VisualStudioVersion'):
            self.assertIsNone(out['msvc_environment'][key])
        self.assertIn('manually initialized toolchain',out['limitations'])
        self.assertIn('x64 environment marker supplied by caller; not proof of VsDevCmd',out['limitations'])
        self.assertIn('reported JSON only; core and patches not rehashed, process exit not independently verified',out['limitations'])
        f['msvc_environment']['WindowsSDKVersion']='C:\\Users\\private'
        with self.assertRaises(self.m.EvidenceError):self.m.report_summary(f,self.expected,self.patches,local_manual=True)
        f=self.fixture();f['msvc_environment']['VSCMD_ARG_TGT_ARCH']='x86'
        with self.assertRaises(self.m.EvidenceError):self.m.report_summary(f,self.expected,self.patches,local_manual=True)

    def test_local_cli_rejected_on_github_actions(self):
        import contextlib,io
        with patch.dict(os.environ, {'GITHUB_ACTIONS':'true'}),contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(self.m.main(['--local-manual']),1)

    def test_cli_never_prints_untrusted_exception_content(self):
        import contextlib,io,json,shutil
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);(root/'scripts').mkdir()
            for name in ('github_native_clean_build.py','build_deskflow_core.py'):
                shutil.copyfile(ROOT/'scripts'/name,root/'scripts'/name)
            candidate=root/'.artifacts/native-src'/('run-'+'a'*32);candidate.mkdir(parents=True)
            (candidate/'clean-build-evidence.json').write_text(json.dumps(self.fixture()),encoding='utf-8')
            with patch.object(self.m,'ROOT',root),patch.dict(os.environ,{'GITHUB_ACTIONS':'false'}),patch.object(Path,'read_text',side_effect=OSError('C:\\Users\\private token_secret')),contextlib.redirect_stderr(io.StringIO()) as err:
                self.assertEqual(self.m.main(['--local-manual']),1)
                self.assertIn('Cannot print native evidence.',err.getvalue())
                self.assertNotIn('private',err.getvalue())
                self.assertNotIn('token_secret',err.getvalue())

    def test_import_failure_never_prints_untrusted_exception_content(self):
        import contextlib,io
        with patch.dict(os.environ,{'GITHUB_ACTIONS':'false'}),patch.object(self.m.importlib.util,'spec_from_file_location',side_effect=OSError('C:\\Users\\private token_secret')),contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertEqual(self.m.main(['--local-manual']),1)
            self.assertIn('Cannot print native evidence.',err.getvalue())
            self.assertNotIn('private',err.getvalue())
            self.assertNotIn('token_secret',err.getvalue())

if __name__=='__main__':unittest.main()
