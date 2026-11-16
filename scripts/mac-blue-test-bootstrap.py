"""Prepare one isolated Mac blue test instance from the local delivery folder."""
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import uuid

def _read_regular(path):
    if path.is_symlink() or not path.is_file():
        raise ValueError('测试包文件不存在或是链接。')
    return path.read_bytes()

def install(package, home, python, run, make_id, engine_exists):
    manifest_path = package / 'package-manifest.json'
    manifest = json.loads(_read_regular(manifest_path).decode('utf-8'))
    if manifest.get('channel') != 'mac-blue-internal-test':
        raise ValueError('测试包标识不正确。')
    archive = package / 'NianDeskLink-test.zip'
    stage = package / 'stage-mac-test.py'
    expected = manifest.get('archiveSha256')
    stage_hash = manifest.get('stageSha256')
    if not isinstance(expected, str) or not re.fullmatch(r'[0-9a-f]{64}', expected):
        raise ValueError('测试包校验信息无效。')
    if not isinstance(stage_hash, str) or not re.fullmatch(r'[0-9a-f]{64}', stage_hash):
        raise ValueError('启动工具校验信息无效。')
    app_bytes = _read_regular(archive)
    if hashlib.sha256(app_bytes).hexdigest() != expected:
        raise ValueError('测试包校验失败，请重新传输。')
    if hashlib.sha256(_read_regular(stage)).hexdigest() != stage_hash:
        raise ValueError('启动工具校验失败，请重新传输。')
    engines = (home / 'Applications/Deskflow.app/Contents/MacOS/deskflow-core',
               Path('/Applications/Deskflow.app/Contents/MacOS/deskflow-core'))
    if not any(engine_exists(path) for path in engines):
        raise FileNotFoundError('未找到 Deskflow，请先安装 Apple 芯片版 Deskflow；本工具不会自动下载。')
    suffix = make_id()
    if not isinstance(suffix, str) or not re.fullmatch(r'[0-9a-f]{32}', suffix):
        raise ValueError('测试目录标识无效。')
    projects = home / 'Projects'
    if projects.is_symlink() or any(parent.is_symlink() for parent in projects.parents):
        raise ValueError('测试目录不能是链接。')
    projects.mkdir(mode=0o700, exist_ok=True)
    label = 'nian-link-test-' + suffix
    target = projects / label
    base = [python, str(stage), label]
    run(base + ['prepare-discovery'])
    if target.is_symlink() or not target.is_dir():
        raise ValueError('未能创建独立测试目录。')
    # Exclusive creation protects any existing data; the staging helper verifies
    # the copied archive again before it extracts and signs the new app.
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_NOFOLLOW', 0)
    fd = os.open(target / 'NianDeskLink-test.zip', flags, 0o600)
    with os.fdopen(fd, 'wb') as output:
        output.write(app_bytes)
    run(base + ['extract', expected, '--fileless-test'])
    run(base + ['create-discovery-profile'])
    run(base + ['create-blue-fileless-launcher'])
    target_helper = target / 'launch-fileless-helper.py'
    if hashlib.sha256(_read_regular(target_helper)).hexdigest() != stage_hash:
        raise ValueError('独立启动工具校验失败。')
    return {'label': label, 'target': str(target), 'helper': str(target_helper)}

def verify_runtime(system, machine, version, tool_exists):
    if (system != 'darwin' or machine != 'arm64' or
            not re.fullmatch(r'\d+(?:\.\d+){0,2}', version or '') or int(version.split('.')[0]) < 14):
        raise RuntimeError('此测试包需要 macOS 14 或更新版本的 Apple 芯片 Mac。')
    if any(not tool_exists(Path(path)) for path in ('/usr/bin/codesign', '/usr/bin/open', '/usr/bin/pgrep')):
        raise RuntimeError('系统缺少启动或签名工具，无法准备测试版。')

def main():
    try:
        verify_runtime(sys.platform, platform.machine(), platform.mac_ver()[0], lambda path: path.is_file())
        package = Path(__file__).resolve().parent
        home = Path.home()
        result = install(package, home, sys.executable,
                         lambda args: subprocess.run(args, check=True),
                         lambda: uuid.uuid4().hex, lambda path: path.is_file())
        print('测试版准备完成，桌面已创建带标识的清透蓝测试启动器。', flush=True)
        print('如果之前的测试版仍在运行，请先正常关闭。', flush=True)
        subprocess.run([sys.executable, result['helper'], result['label'], 'launch-blue-fileless'], check=True)
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError) as error:
        print('无法准备或启动内部测试版：' + str(error), file=sys.stderr)
        raise SystemExit(1)

if __name__ == '__main__': main()
