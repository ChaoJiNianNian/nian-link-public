"""Build an offline, isolated macOS blue test delivery; never a public release."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import zipfile

ROOT = Path(__file__).resolve().parents[1]

COMMAND = '''#!/bin/sh
cd "$(dirname "$0")" || exit 1
if [ ! -x /usr/bin/python3 ] || ! /usr/bin/python3 -c 'import sys' >/dev/null 2>&1; then
  printf '%s\\n' '系统 Python 3 不可用。请先按系统提示安装后再运行；此启动包不会自动下载。'
  printf '%s' '按回车关闭此窗口…'; read answer
  exit 1
fi
/usr/bin/python3 ./mac-blue-test-bootstrap.py
status=$?
if [ "$status" -ne 0 ]; then
  printf '%s\\n' '内部测试版未启动，请查看上面的提示。正式版未被修改。'
fi
printf '%s' '按回车关闭此窗口…'; read answer
exit "$status"
'''

README = '''年年互联 · Mac 清透蓝内部测试版

这不是正式发行包，内含的 app 未签名、未公证。
需要 Apple 芯片 Mac、macOS 14 或更高、系统 Python 3，以及已经安装的 Deskflow。
Deskflow 1.27.0 官方下载：https://github.com/deskflow/deskflow/releases/tag/v1.27.0
本包不含 Deskflow；如已安装，请先检查版本和配置，不要覆盖原有安装。
请先正常退出之前的年年互联测试版，保留正式版和原有资料。
在 Mac 解压本包后，双击“安装并启动.command”。首次启动会在 ~/Projects 创建新的私有测试目录，
校验测试包、对新测试 app 临时签名并在桌面创建带标识的新启动器，不覆盖已有应用和启动器。
如果提示权限，请按系统界面自行决定；本工具不会下载安装软件、修改系统安全设置或传送配对文件。
之后可用桌面上的“启动年年互联清透蓝测试-标识.command”再次打开。
测试界面、系统权限和双机连接尚需在 Mac 原生环境实测；失败时请保留提示以便排查。
'''

def _read_file(path):
    if path.is_symlink() or not path.is_file():
        raise ValueError('Missing or linked input refused.')
    return path.read_bytes()

def _put(z, name, data, executable=False):
    entry = zipfile.ZipInfo(name)
    entry.create_system = 3
    entry.external_attr = ((stat.S_IFREG | (0o755 if executable else 0o644)) << 16)
    entry.compress_type = zipfile.ZIP_STORED
    z.writestr(entry, data)

def build_archive(archive, stage, boot, output, expected):
    if not re.fullmatch(r'[0-9a-f]{64}', expected):
        raise ValueError('Invalid expected SHA-256.')
    app_bytes = _read_file(archive)
    stage_bytes = _read_file(stage)
    boot_bytes = _read_file(boot)
    if hashlib.sha256(app_bytes).hexdigest() != expected:
        raise ValueError('Test archive SHA-256 mismatch.')
    manifest = {'channel': 'mac-blue-internal-test', 'archiveSha256': expected,
                'stageSha256': hashlib.sha256(stage_bytes).hexdigest()}
    created = None
    try:
        with output.open('xb') as stream:
            info = os.fstat(stream.fileno())
            created = (info.st_dev, info.st_ino)
            with zipfile.ZipFile(stream, 'w') as z:
                _put(z, 'NianDeskLink-test.zip', app_bytes)
                _put(z, 'stage-mac-test.py', stage_bytes)
                _put(z, 'mac-blue-test-bootstrap.py', boot_bytes)
                _put(z, 'package-manifest.json', json.dumps(manifest, ensure_ascii=False, indent=2).encode('utf-8'))
                _put(z, '安装并启动.command', COMMAND.encode('utf-8'), executable=True)
                _put(z, '使用说明.txt', README.encode('utf-8'))
    except BaseException:
        if created is not None:
            try:
                info = output.lstat()
                if stat.S_ISREG(info.st_mode) and (info.st_dev, info.st_ino) == created:
                    output.unlink()
            except FileNotFoundError:
                pass
        raise

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive', required=True, type=Path)
    parser.add_argument('--sha256', required=True)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    allowed = ROOT / '.artifacts' / 'mac-blue-delivery'
    if (not allowed.is_dir() or any(p.is_symlink() for p in (ROOT, ROOT / '.artifacts', allowed)) or
            args.output.resolve().parent != allowed.resolve() or args.output.suffix.lower() != '.zip'):
        raise SystemExit('Output must be a new ZIP inside .artifacts/mac-blue-delivery.')
    build_archive(args.archive, ROOT / 'scripts/stage-mac-test.py', ROOT / 'scripts/mac-blue-test-bootstrap.py',
                  args.output, args.sha256.lower())
    print('Internal Mac test delivery created; not a public release.')

if __name__ == '__main__': main()
