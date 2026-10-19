"""Assemble a fresh redistributable from validated runtimes and clean source."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import zipfile

parser = argparse.ArgumentParser()
for name in ('runtime', 'licenses', 'native-sources', 'source-zip', 'launcher', 'output'):
    parser.add_argument('--' + name, type=Path, required=True)
args = parser.parse_args()
repo = Path(__file__).resolve().parents[1]
destination = args.output.resolve()
if destination.exists():
    raise SystemExit('Use a new output directory; existing output is never overwritten.')
destination.mkdir(parents=True)


def copy_runtime(source, target):
    target.mkdir(parents=True, exist_ok=True)
    for item in source.iterdir():
        if item.is_file() and (item.suffix.lower() in ('.dll', '.exe', '.ini', '.config')
                or item.name in ('Ryujinx.deps.json', 'Ryujinx.runtimeconfig.json', 'LICENSE.txt', 'THIRDPARTY.md')):
            shutil.copy2(item, target / item.name)
        elif item.is_dir() and re.fullmatch(r'[a-z]{2,3}(?:-[A-Za-z0-9]+)*', item.name):
            for resource in item.glob('*.resources.dll'):
                (target / item.name).mkdir(exist_ok=True)
                shutil.copy2(resource, target / item.name / resource.name)


copy_runtime(args.runtime, destination)
copy_runtime(args.runtime / 'engines/zelda', destination / 'engines/zelda')
shutil.copytree(repo / 'profiles', destination / 'profiles')
(destination / 'portable').mkdir()
shutil.copy2(repo / 'release-tools/Config.clean.json', destination / 'portable/Config.json')
shutil.copy2(args.launcher, destination / 'Ryujinx Stereo.exe')
for name in ('README.md', 'CHANGELOG.md', 'LICENSE.txt', 'VALIDATION.txt'):
    shutil.copy2(repo / name, destination / name)
shutil.copytree(args.licenses, destination / 'licenses')
(destination / 'source').mkdir()
shutil.copy2(args.source_zip, destination / 'source/Ryujinx-Stereo-source.zip')
shutil.copy2(repo / 'release-tools/BUILDING.txt', destination / 'source/BUILDING.txt')
shutil.copytree(args.native_sources, destination / 'source/native')

config = json.loads((destination / 'portable/Config.json').read_text(encoding='utf-8'))
assert config['game_dirs'] == [] and config['autoload_dirs'] == []
assert config['input_config'][0]['backend'] == 'WindowKeyboard'
assert config['show_console'] is False and config['start_fullscreen'] is False
assert config['graphics_backend'] == 'OpenGl' and config['update_checker_type'] == 'Off'
assert list((destination / 'portable').iterdir()) == [destination / 'portable/Config.json']
for item in destination.rglob('*'):
    if item.is_file():
        assert item.suffix.lower() not in ('.keys', '.nsp', '.xci', '.nca', '.pdb', '.log', '.mp4', '.wav'), item
        assert not any(p.lower() in ('saves', 'sdcard', 'bis', 'logs', 'shader-cache') for p in item.relative_to(destination).parts), item

files = {p.relative_to(destination).as_posix(): {'bytes': p.stat().st_size,
         'sha256': hashlib.sha256(p.read_bytes()).hexdigest()}
         for p in sorted(destination.rglob('*')) if p.is_file()}
manifest = {'name': 'Ryujinx Stereo', 'version': '0.2.0-experimental', 'platform': 'Windows x64',
            'games': ['Super Mario Odyssey', 'The Legend of Zelda: Breath of the Wild'],
            'baseline_commit': '80ab0f96b95dc13f5bdb9f365a3363d25040e236',
            'external_dependencies': ['LeiaSR display runtime for native output', 'FFmpeg with h264_nvenc for recording'],
            'files': files}
(destination / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
archive = destination.with_suffix('.zip')
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as output:
    for p in sorted(destination.rglob('*')):
        if p.is_file():
            output.write(p, destination.name + '/' + p.relative_to(destination).as_posix())
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix('.zip.sha256').write_text(digest + '  ' + archive.name + '\n', encoding='utf-8')
print(json.dumps({'archive': str(archive), 'sha256': digest, 'files': len(files) + 1,
                  'bytes': archive.stat().st_size}, indent=2))
