import hashlib
import importlib.util
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('core_build', ROOT / 'scripts/build_deskflow_core.py')
core = importlib.util.module_from_spec(spec)
spec.loader.exec_module(core)

class NativeCoreBuildTests(unittest.TestCase):
    def zipentry(self, name, mode=stat.S_IFREG):
        entry = zipfile.ZipInfo(name)
        entry.external_attr = (mode | 0o644) << 16
        return entry

    def test_rejects_traversal_drives_reserved_and_unapproved_links(self):
        root = core.ARCHIVE_ROOT
        for name in ('../evil.cs', root + '/../escape.cs', root + '/C:bad.cs', root + '/NUL.cs', root + '/a/b.cs/../z.cs'):
            with self.subTest(name=name), self.assertRaises(core.BuildError):
                core.check_entry(self.zipentry(name), set())
        from types import SimpleNamespace
        with self.assertRaises(core.BuildError):
            core.check_entry(SimpleNamespace(filename=root + '/a\\b.cs', external_attr=stat.S_IFREG << 16), set())
        with self.assertRaises(core.BuildError):
            core.check_entry(self.zipentry(root + '/src/link', stat.S_IFLNK), set())

    def test_expected_link_is_never_a_disk_link_and_missing_link_rejected(self):
        relative = next(iter(core.KNOWN_ICON_LINKS))
        info = self.zipentry(core.ARCHIVE_ROOT + '/' + relative, stat.S_IFLNK)
        _, got, islink = core.check_entry(info, set())
        self.assertTrue(islink); self.assertEqual(relative, got)
        core.validate_icon_target(relative, core.KNOWN_ICON_LINKS[relative])
        with self.assertRaises(core.BuildError):core.validate_icon_target(relative, b'../../escape')
        with self.assertRaises(core.BuildError):
            core.check_entry(self.zipentry(core.ARCHIVE_ROOT + '/' + relative), set())

    def test_casefold_collision_rejected(self):
        seen = set();root = core.ARCHIVE_ROOT
        core.check_entry(self.zipentry(root + '/src/Foo.c'), seen)
        with self.assertRaises(core.BuildError): core.check_entry(self.zipentry(root + '/src/foo.c'), seen)

    def test_wrong_archive_and_wrong_patch_hash_fail_before_output(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d); archive=root/'a.zip';archive.write_bytes(b'synthetic')
            with self.assertRaisesRegex(core.BuildError,'archive SHA-256'):core.checked_sources(archive,root)
            with patch.object(core,'ARCHIVE_HASH',hashlib.sha256(b'synthetic').hexdigest()), \
                 patch.object(core,'PATCHES',(('one.patch','0'*64),)):
                (root/'one.patch').write_bytes(b'changed')
                with self.assertRaisesRegex(core.BuildError,'Patch SHA-256'):core.checked_sources(archive,root)
            self.assertFalse((root/'.artifacts').exists())

    def test_output_is_new_and_refuses_reuse(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)
            class Id: hex='0'*32
            with patch.object(core.uuid,'uuid4',return_value=Id()):
                first=core.prepare_output(root);(first/'sentinel').write_text('untouched')
                with self.assertRaises(FileExistsError):core.prepare_output(root)
                self.assertEqual((first/'sentinel').read_text(),'untouched')

    def test_patch_check_and_apply_order_before_configure(self):
        with tempfile.TemporaryDirectory() as d:
            work=Path(d);src=work/'source';src.mkdir();calls=[]
            patches=[(name,name.encode()) for name,_ in core.PATCHES]
            def run(label,args,working,env):
                calls.append((label,args))
                if label=='build':
                    exe=work/'build/bin/deskflow-core.exe';exe.parent.mkdir(parents=True);exe.write_bytes(b'synthetic')
            with patch.object(core.sys,'platform','win32'), \
                 patch.dict(core.os.environ,{'VSCMD_ARG_TGT_ARCH':'x64'}), \
                 patch.object(core,'checked_sources',return_value=(work/'archive.zip',patches)), \
                 patch.object(core,'require_inputs',return_value=(work/'qt',work/'ssl')), \
                 patch.object(core,'prepare_output',return_value=work), \
                 patch.object(core,'extract_source',return_value=src), \
                 patch.object(core,'verify_cmake_cache',return_value=8), \
                 patch.object(core.shutil,'which',side_effect=lambda x:x), \
                 patch.object(core,'run_step',side_effect=run):
                core.build(work/'archive.zip',work/'qt',work/'ssl',root=work)
            expected=[]
            for i,(name,_) in enumerate(patches,1):expected.extend([f'patch-{i}-check',f'patch-{i}-apply'])
            self.assertEqual([x[0] for x in calls],expected+['configure','build'])
            for i,(name,_) in enumerate(patches):
                self.assertEqual(Path(calls[2*i][1][-1]).name,name)
                self.assertEqual(Path(calls[2*i+1][1][-1]).name,name)
                self.assertIn('--check',calls[2*i][1]);self.assertNotIn('--check',calls[2*i+1][1])

    def test_cmake_cache_rejects_external_or_missing_dependencies(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);qt=root/'qt';ssl=root/'ssl';outside=root/'outside';qt.mkdir();ssl.mkdir();outside.mkdir()
            paths={'Qt6_DIR':qt/'qt6', 'Qt6Core_DIR':qt/'core', 'Qt6LinguistTools_DIR':qt/'linguist',
                   'Qt6Network_DIR':qt/'network', 'Qt6Widgets_DIR':qt/'widgets', 'OPENSSL_INCLUDE_DIR':ssl/'include',
                   'LIB_EAY_RELEASE':ssl/'crypto.lib', 'SSL_EAY_RELEASE':ssl/'ssl.lib', 'OPENSSL_APPLINK_SOURCE':ssl/'applink.c',
                   'LIB_EAY_DEBUG':ssl/'crypto-debug.lib', 'SSL_EAY_DEBUG':ssl/'ssl-debug.lib'}
            for key,path in paths.items():
                if path.suffix: path.write_bytes(b'synthetic')
                else:path.mkdir()
            cache=root/'CMakeCache.txt'
            def write(mapping):cache.write_text('\n'.join(k+':PATH='+str(v) for k,v in mapping.items()),encoding='utf-8')
            write(paths);core.verify_cmake_cache(cache,qt,ssl)
            bad=dict(paths);bad['Qt6Core_DIR']=outside;write(bad)
            with self.assertRaises(core.BuildError):core.verify_cmake_cache(cache,qt,ssl)
            bad=dict(paths);bad['OPENSSL_APPLINK_SOURCE']=outside;write(bad)
            with self.assertRaises(core.BuildError):core.verify_cmake_cache(cache,qt,ssl)
            bad=dict(paths);bad['SSL_EAY_DEBUG']=outside;write(bad)
            with self.assertRaises(core.BuildError):core.verify_cmake_cache(cache,qt,ssl)
            bad=dict(paths);del bad['SSL_EAY_RELEASE'];write(bad)
            with self.assertRaises(core.BuildError):core.verify_cmake_cache(cache,qt,ssl)
            write(paths);cache.write_text(cache.read_text(encoding='utf-8')+'\nQt6_DIR:PATH='+str(outside),encoding='utf-8')
            with self.assertRaises(core.BuildError):core.verify_cmake_cache(cache,qt,ssl)

    def test_reparse_attribute_is_rejected(self):
        from types import SimpleNamespace
        p=Path('synthetic')
        with patch.object(Path,'lstat',return_value=SimpleNamespace(st_mode=stat.S_IFDIR,st_file_attributes=0x400)):
            with self.assertRaises(core.BuildError):core.no_links(p)

if __name__=='__main__':unittest.main()
