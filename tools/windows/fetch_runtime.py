#!/usr/bin/env python3
"""Fetch PSF's pinned Windows embedded Python without installing system software."""
import argparse
import json
from pathlib import Path, PurePosixPath
import stat
import sys
import zipfile

from tools.common.paths import ROOT
from tools.common.downloads import fetch_runtime, verify_runtime

VERSION = '3.14.7'
FILENAME = f'python-{VERSION}-embed-amd64.zip'
URL = f'https://www.python.org/ftp/python/{VERSION}/{FILENAME}'
SHA256 = 'd297e5ff019966817ad8502465176139f2d3d840fa4ed84b13bed399a6ab1f15'
BYTES = 12673227
PTH = 'python314.zip\n.\n../backend\n\n# Deliberately do not import site or add the current directory.\n'


def load_manifest(path=ROOT / 'resources/windows/runtimes/manifest.json'):
    entry = json.loads(Path(path).read_text(encoding='utf-8'))
    expected = dict(version=VERSION, architecture='amd64', file=FILENAME, url=URL,
                    sha256=SHA256, bytes=BYTES,
                    source='https://www.python.org/downloads/release/python-3147/')
    if entry != expected or type(entry.get('bytes')) is not int:
        raise ValueError('Windows runtime manifest does not match the pinned PSF release')
    return entry


def extract_runtime(archive_path, destination, entry):
    """Extract only regular flat files from the verified upstream archive."""
    verify_runtime(archive_path, entry)
    destination = Path(destination)
    destination.mkdir(parents=True, exist_ok=False)
    with zipfile.ZipFile(archive_path) as archive:
        names = set()
        for info in archive.infolist():
            name = info.filename
            pure = PurePosixPath(name)
            if (pure.name != name or name.startswith('.') or ':' in name or '\\' in name
                    or info.is_dir() or name.casefold() in names
                    or stat.S_ISLNK(info.external_attr >> 16)):
                raise ValueError(f'Unexpected runtime ZIP member: {name}')
            names.add(name.casefold())
            with archive.open(info) as source, (destination / name).open('xb') as output:
                import shutil
                shutil.copyfileobj(source, output)
    for required in ('python.exe', 'pythonw.exe', 'python314.dll', 'python314.zip',
                     'python314._pth', 'LICENSE.txt', '_sqlite3.pyd', 'sqlite3.dll'):
        if not (destination / required).is_file():
            raise ValueError(f'Embedded Python is missing {required}')
    (destination / 'python314._pth').write_text(PTH, encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--cache-dir', type=Path, default=ROOT / '.local/windows-runtime')
    args = parser.parse_args()
    path, downloaded = fetch_runtime(args.cache_dir, load_manifest())
    print(('Downloaded and verified: ' if downloaded else 'Verified cache: ') + str(path))


if __name__ == '__main__':
    main()
