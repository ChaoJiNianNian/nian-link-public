"""Build a *test-only* Mac export bundle from explicitly verified inputs.

The caller must hold this project's build-artifact lock. This does not sign,
notarize, install, publish, or certify license compliance.
"""
import argparse
import copy
import hashlib
import io
import json
import plistlib
import stat
import shutil
import zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[1]
DMG_SHA = 'dd21e79e566314394214906d3338c505d337af536fc580c022ee3fadffad0c97'
OFFICIAL_MANIFEST_SHA = '2c5b9ec650dc9f8eb4144f92d3bd25d7bf9356735f2e8a9223131a38a4765e7f'
APP_MANIFEST = 'NianDeskLink.app/Contents/Resources/build-manifest.json'
APP_PLIST = 'NianDeskLink.app/Contents/Info.plist'
OUTFILE = 'NianDeskLink-macOS-arm64.zip'
DMG_NAME = 'deskflow-1.27.0-macos-arm64.dmg'
README = '''年年互联 Mac 安装说明（仅供内部测试）

适用：Apple 芯片 Mac，同一局域网。此测试版尚未完成完整双机验收，不要作为正式版传播。

1. 将本 ZIP 解压到普通文件夹。打开 Deskflow 安装镜像，将其中的 Deskflow 拖到“应用程序”。如果已经安装 Deskflow，请先确认版本和现有配置，不要直接覆盖。
2. 解压 NianDeskLink 客户端 ZIP，将独立测试 App 拖到合适位置。此 App 未使用 Apple Developer ID 签名、未公证，macOS 可能阻止打开；请遵循系统提示处理，不要关闭系统安全功能。
3. 在 Mac 系统设置的“隐私与安全性”中，为当前独立 App 允许“辅助功能”和“屏幕与系统音频录制”。系统若要求重启，重启后确认仍打开同一份 App。
4. 两台设备在同一局域网时，在 Windows 独立版启动共享、导出配对文件，并私下发送到 Mac；在 Mac 独立版导入，核对完整安全指纹后本人确认。配对文件包含凭证，不要公开。
5. Mac 开始共享后，按 Windows 和 Mac 两端界面提示连接。第一次测试时不要同时启用旧版键鼠共享；遇到问题先停止测试，原配置不要删除。

本包包含 Deskflow 1.27.0 官方 ARM64 安装镜像及项目许可文本；依赖许可及完整对应源码仍需核验，不能据此认为开源分发义务已完成。
'''
README_MULTI = '''年年互联 Mac 多设备安装说明（仅供内部测试）

适用 Apple 芯片 Mac 和同一可信局域网内的 Windows 主控；此包不是正式发行版。

一、安装
1. 打开本文件夹里的 Deskflow .dmg，把 Deskflow 拖入“应用程序”。如已有 Deskflow，先确认版本和原有配置，不要盲目覆盖。
2. 把本文件夹里的 NianDeskLink.app 放到合适位置并打开。App 未使用 Apple Developer ID 签名或公证，按 macOS 提示处理，不要关闭系统安全功能。
3. 在“系统设置 → 隐私与安全性”中，为当前这份测试 App 打开“辅助功能”和“屏幕与系统音频录制”；如果系统要求重开，请确认打开的是同一份 App。

二、连接 Windows
1. Windows 接好键鼠，打开年年互联，选择“主控电脑”并点“准备主控”。
2. Mac 点“生成身份卡”，私下发给 Windows；身份卡 10 分钟内有效。Windows 导入后，两端对照完整键鼠指纹，一致才确认。
3. Windows 生成配对文件，私下发回 Mac；Mac 导入后与 Windows 对照完整控制通道指纹，一致才确认。配对文件含控制凭证，不要公开。
4. Windows 点“启动共享”，Mac 点“开始连接”；若要求核对键鼠指纹，再与主控对照。先停用其他键鼠共享，避免冲突。

多设备测试版尚不支持共用显示器自动切屏。

本包含 Deskflow 官方 ARM64 镜像和项目许可文本；完整来源及许可仍待核验，不能视为开源分发义务已完成。
'''
README_MULTI_NESTED = README_MULTI.replace('把本文件夹里的 NianDeskLink.app 放到合适位置并打开。', '双击 NianDeskLink 客户端 .zip，把解压出的年年互联测试 App 放到合适位置并打开。')
README_MULTI_LEGACY = '''年年互联 Mac 多设备安装说明（仅供内部测试）

适用 Apple 芯片 Mac 和同一局域网中的 Windows 主控；此包不是正式发行版。
1. 解压本包，将 Deskflow 镜像里的 Deskflow 安装到“应用程序”。如已有 Deskflow，先核对版本和原有配置。
2. 解压 NianDeskLink 客户端 ZIP，将多设备测试 App 放到合适位置。App 未使用 Apple Developer ID 签名或公证，请遵循 macOS 系统提示。
3. 在 Mac“隐私与安全性”中允许当前测试 App 使用“辅助功能”和“屏幕与系统音频录制”。
4. 在 Mac 测试 App 生成身份卡，私下发给 Windows 主控；两端核对设备指纹后，由主控生成配对文件，私下发回 Mac 并确认主控指纹。配对文件包含凭证，不要公开。
5. 两端开始连接，按提示确认键鼠指纹。先停用其他键鼠共享，避免抢占；本多设备候选尚不支持共用显示器自动切屏。

本包含 Deskflow 官方 ARM64 镜像和项目许可文本；完整来源及许可仍待核验，不能视为开源分发义务已完成。
'''
NOTICE = '''第三方信息（内部测试草案，非完整许可合规声明）

Deskflow 1.27.0 官方 Apple 芯片安装镜像来源：
https://github.com/deskflow/deskflow/releases/download/v1.27.0/deskflow-1.27.0-macos-arm64.dmg
SHA-256: DD21E79E566314394214906D3338C505D337AF536FC580C022EE3FADFFAD0C97
Deskflow 上游源码版本及许可文件见 https://github.com/deskflow/deskflow/tree/v1.27.0 。
本目录附带 Deskflow 项目许可证及例外文本；镜像内其他依赖、许可证和完整对应源码仍需核查。该哈希与本工程记录一致，不代表已验证发行签名。
独立 App 自身及其依赖的第三方声明位于其 ZIP 内的 Contents/Resources；完整来源与许可仍待复核。
'''


def sha(path):
    digest = hashlib.sha256()
    if hasattr(path, 'read') and hasattr(path, 'seek'):
        path.seek(0)
        for block in iter(lambda: path.read(1024 * 1024), b''):
            digest.update(block)
        path.seek(0)
    else:
        with Path(path).open('rb') as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b''):
                digest.update(block)
    return digest.hexdigest()


def safe_name(name):
    p = PurePosixPath(name)
    return bool(name) and not name.startswith('/') and '\\' not in name and ':' not in name and all(x not in ('', '.', '..') for x in name.split('/')) and str(p) == name


def write_flat_app(output, archive):
    with zipfile.ZipFile(archive) as source:
        entries = source.infolist()
        names = [item.filename for item in entries]
        if len(names) != len(set(names)) or any(not safe_name(i.filename) or not i.filename.startswith('NianDeskLink.app/') or i.is_dir() or (i.flag_bits & 1) or stat.S_ISLNK(i.external_attr >> 16) for i in entries):
            raise ValueError('Unexpected app entry')
        for info in entries:
            with source.open(info) as src, output.open(copy.copy(info), 'w') as dst:
                shutil.copyfileobj(src, dst)


def verify_app(archive, version, expected_sha, root=ROOT, *, multi_device=False):
    if sha(archive).lower() != expected_sha.lower():
        raise ValueError('Mac app archive digest mismatch')
    with zipfile.ZipFile(archive) as z:
        entries = z.infolist()
        names = [i.filename for i in entries]
        if len(names) != len(set(names)) or any(not safe_name(i.filename) or i.is_dir() or (i.flag_bits & 1) or stat.S_ISLNK(i.external_attr >> 16) or not i.filename.startswith('NianDeskLink.app/') for i in entries):
            raise ValueError('Unexpected, linked, encrypted or duplicate app archive entry')
        if any(Path(n).suffix.lower() in {'.pem', '.key', '.pfx', '.p12'} or PurePosixPath(n).name.lower() in {'deskflow.conf', 'paired-control.json', 'connection.json', 'trusted-clients', 'trusted-servers'} for n in names):
            raise ValueError('App archive contains forbidden profile material')
        if sum(i.file_size for i in entries) > 512 * 1024 * 1024 or z.getinfo(APP_MANIFEST).file_size > 4 * 1024 * 1024:
            raise ValueError('App archive too large')
        manifest = json.loads(z.read(APP_MANIFEST))
        plist = plistlib.loads(z.read(APP_PLIST))
        expected_identity = 'com.niannian.nianlink.multidevice-test' if multi_device else 'com.niannian.nianlink'
        if (manifest.get('version') != version or manifest.get('target') != 'osx-arm64' or manifest.get('signed') is not False or manifest.get('notarized') is not False or plist.get('CFBundleIdentifier') != expected_identity or plist.get('CFBundleShortVersionString') != version):
            raise ValueError('Unexpected Mac app identity or signature state')
        sources = manifest.get('sources')
        if not isinstance(sources, list) or not any(s.get('path', '').startswith('src/Nian.Input.Mac/') for s in sources):
            raise ValueError('Mac app source inventory is missing')
        seen = set()
        for row in sources:
            name = row.get('path', '')
            if not safe_name(name) or name in seen:
                raise ValueError('Invalid source inventory path')
            seen.add(name)
            file = root.joinpath(*PurePosixPath(name).parts)
            if not file.is_file() or sha(file) != row.get('sha256'):
                raise ValueError('App source differs from the recorded inventory: ' + name)
        rows = manifest.get('files')
        if not isinstance(rows, list):
            raise ValueError('App file inventory is missing')
        records = {r.get('path'): r for r in rows}
        if len(records) != len(rows) or set(records) != set(names) - {APP_MANIFEST}:
            raise ValueError('App file inventory differs from archive')
        for name, row in records.items():
            info = z.getinfo(name)
            with z.open(info) as stream:
                digest = hashlib.file_digest(stream, 'sha256').hexdigest()
            if info.file_size != row.get('bytes') or digest != row.get('sha256'):
                raise ValueError('App file inventory checksum mismatch: ' + name)


def verified_licenses(root=ROOT):
    official_manifest = root / 'vendor/deskflow/metadata/original-manifest.json'
    if sha(official_manifest) != OFFICIAL_MANIFEST_SHA:
        raise ValueError('Deskflow license provenance manifest mismatch')
    license_rows = json.loads(official_manifest.read_text(encoding='utf-8-sig'))['files']
    licenses = {}
    for name in ('LICENSE', 'LICENSE_EXCEPTION'):
        matches = [row for row in license_rows if row['path'] == name]
        file = root / 'vendor/deskflow/original' / name
        if len(matches) != 1 or sha(file).lower() != matches[0]['sha256'].lower():
            raise ValueError('Deskflow license text mismatch')
        licenses['Deskflow-' + name + '.txt'] = file
    return licenses


def verify_bundle(directory, version, root=ROOT, *, multi_device=False):
    directory = Path(directory)
    if not directory.is_dir() or directory.is_symlink():
        raise ValueError('Missing or linked Mac export directory')
    package, manifest_path = directory / OUTFILE, directory / 'manifest.json'
    if any(not p.is_file() or p.is_symlink() for p in (package, manifest_path)) or manifest_path.stat().st_size > 16384:
        raise ValueError('Missing or linked Mac export package or manifest')
    manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    if manifest.get('mode', 'one-to-one') != ('multi-device-test' if multi_device else 'one-to-one'):
        raise ValueError('Mac export mode mismatch')
    if (manifest.get('file') != OUTFILE or manifest.get('version') != version or manifest.get('channel') != 'internal-test' or
            manifest.get('includesDeskflow') is not True or manifest.get('notarized') is not False or
            manifest.get('bytes') != package.stat().st_size or manifest.get('sha256', '').lower() != sha(package)):
        raise ValueError('Mac export manifest, version or digest mismatch')
    app_name = 'NianDeskLink-' + version + '-osx-arm64.zip'
    licenses = verified_licenses(root)
    layout = manifest.get('appLayout', 'nested-v1')
    if layout not in ('nested-v1', 'flat-v1') or (layout == 'flat-v1' and not multi_device):
        raise ValueError('Mac export layout mismatch')
    expected = {DMG_NAME, *licenses, '安装说明.txt', '第三方说明.txt'}
    with zipfile.ZipFile(package) as z:
        entries = z.infolist()
        names = [i.filename for i in entries]
        app_entries = [i for i in entries if i.filename.startswith('NianDeskLink.app/')]
        if layout == 'flat-v1':
            expected.update(i.filename for i in app_entries)
            if not app_entries or sum(i.file_size for i in app_entries) > 512 * 1024 * 1024:
                raise ValueError('Mac app entries missing or too large')
        else:
            expected.add(app_name)
        if len(names) != len(set(names)) or set(names) != expected or any(not safe_name(i.filename) or i.is_dir() or (i.flag_bits & 1) or stat.S_ISLNK(i.external_attr >> 16) for i in entries):
            raise ValueError('Mac export contains unexpected entries')
        if (layout == 'nested-v1' and z.getinfo(app_name).file_size > 256 * 1024 * 1024) or z.getinfo(DMG_NAME).file_size > 256 * 1024 * 1024:
            raise ValueError('Mac export component too large')
        with z.open(DMG_NAME) as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != DMG_SHA:
                raise ValueError('Deskflow image digest mismatch')
        for name, path in licenses.items():
            with z.open(name) as stream:
                if hashlib.file_digest(stream, 'sha256').hexdigest() != sha(path):
                    raise ValueError('Deskflow license text mismatch')
        instructions = [README] if not multi_device else [README_MULTI] if layout == 'flat-v1' else [README_MULTI_NESTED, README_MULTI_LEGACY]
        if z.read('安装说明.txt') not in [text.encode('utf-8') for text in instructions] or z.read('第三方说明.txt') != NOTICE.encode('utf-8'):
            raise ValueError('Mac export instructions mismatch')
        if layout == 'flat-v1':
            app = io.BytesIO()
            with zipfile.ZipFile(app, 'w') as reconstructed:
                for info in app_entries:
                    with z.open(info) as src, reconstructed.open(copy.copy(info), 'w') as dst:
                        shutil.copyfileobj(src, dst)
        else:
            app = io.BytesIO(z.read(app_name))
        verify_app(app, version, sha(app), root, multi_device=multi_device)
    return manifest


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--app-zip', required=True, type=Path)
    parser.add_argument('--app-sha256', required=True)
    parser.add_argument('--dmg', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--version', required=True)
    parser.add_argument('--multi-device-testing', action='store_true')
    args = parser.parse_args()
    output = args.output.resolve()
    export_root = (ROOT / '.artifacts/mac-export').resolve()
    if output.parent != export_root or output.exists() or not output.name.startswith('test-'):
        raise ValueError('Output must be a new test directory directly under the export root')
    for path in (ROOT, ROOT / '.artifacts', export_root, args.app_zip, args.dmg):
        if not path.exists() or path.is_symlink():
            raise ValueError('Missing or linked input: ' + str(path))
    if not args.version or any(c not in '0123456789.' for c in args.version) or len(args.app_sha256) != 64:
        raise ValueError('Invalid app version or digest')
    if args.dmg.name != DMG_NAME or sha(args.dmg) != DMG_SHA:
        raise ValueError('Official Deskflow image name or digest mismatch')
    verify_app(args.app_zip, args.version, args.app_sha256, multi_device=args.multi_device_testing)
    licenses = verified_licenses()
    output.mkdir()
    package = output / OUTFILE
    entries = {DMG_NAME: args.dmg, **licenses}
    if not args.multi_device_testing:
        entries[args.app_zip.name] = args.app_zip
    with zipfile.ZipFile(package, 'x', allowZip64=True) as z:
        if args.multi_device_testing:
            write_flat_app(z, args.app_zip)
        for name, path in entries.items():
            if not safe_name(name):
                raise ValueError('Unsafe bundle entry')
            z.write(path, name, compress_type=zipfile.ZIP_STORED)
        z.writestr('安装说明.txt', (README_MULTI if args.multi_device_testing else README).encode('utf-8'), compress_type=zipfile.ZIP_DEFLATED)
        z.writestr('第三方说明.txt', NOTICE.encode('utf-8'), compress_type=zipfile.ZIP_DEFLATED)
    with zipfile.ZipFile(package) as z:
        with zipfile.ZipFile(args.app_zip) as source:
            app_names = set(source.namelist()) if args.multi_device_testing else set()
        if set(z.namelist()) != set(entries) | app_names | {'安装说明.txt', '第三方说明.txt'} or z.testzip() is not None:
            raise ValueError('Bundle payload verification failed')
        if args.multi_device_testing:
            with zipfile.ZipFile(args.app_zip) as source:
                for info in source.infolist():
                    if z.getinfo(info.filename).external_attr != info.external_attr:
                        raise ValueError('App file permissions changed')
                    with source.open(info) as original, z.open(info.filename) as packed:
                        if hashlib.file_digest(original, 'sha256').digest() != hashlib.file_digest(packed, 'sha256').digest():
                            raise ValueError('App file checksum changed')
        for name, path in entries.items():
            with z.open(name) as stream:
                if hashlib.file_digest(stream, 'sha256').hexdigest() != sha(path):
                    raise ValueError('Bundle entry checksum mismatch')
        if z.read('安装说明.txt') != (README_MULTI if args.multi_device_testing else README).encode('utf-8') or z.read('第三方说明.txt') != NOTICE.encode('utf-8'):
            raise ValueError('Bundle instructions mismatch')
    manifest = {'file': OUTFILE, 'version': args.version, 'bytes': package.stat().st_size, 'sha256': sha(package), 'channel': 'internal-test', 'includesDeskflow': True, 'notarized': False}
    if args.multi_device_testing:
        manifest['mode'] = 'multi-device-test'
        manifest['appLayout'] = 'flat-v1'
    (output / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    verify_bundle(output, args.version, multi_device=args.multi_device_testing)
    print(json.dumps({'output': str(output), **manifest}, ensure_ascii=True))


if __name__ == '__main__':
    main()
