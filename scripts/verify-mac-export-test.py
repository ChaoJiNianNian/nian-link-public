"""Validate a candidate internal Mac export bundle before embedding it."""
import argparse
import importlib.util
import json
from pathlib import Path

script = Path(__file__).with_name('build-mac-export-test.py')
spec = importlib.util.spec_from_file_location('nian_mac_export_verify', script)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

parser = argparse.ArgumentParser()
parser.add_argument('--directory', required=True, type=Path)
parser.add_argument('--version', required=True)
parser.add_argument('--multi-device-testing', action='store_true')
args = parser.parse_args()
manifest = module.verify_bundle(args.directory, args.version, multi_device=args.multi_device_testing)
print(json.dumps({'verified': True, 'testingOnly': True, **manifest}, ensure_ascii=True))
