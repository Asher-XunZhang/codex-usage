#!/usr/bin/env python3
"""Fetch only the pinned Python runtimes, verifying bytes before atomic publication."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
from urllib.parse import quote
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]
ARCHITECTURES = {'arm64': 'aarch64', 'x86_64': 'x86_64'}
RELEASE = '20260901'
VERSION = '3.12.14'
BASE_URL = f'https://github.com/astral-sh/python-build-standalone/releases/download/{RELEASE}/'


def digest_file(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def load_manifest(path, require_all=False):
    entries = json.loads(Path(path).read_text(encoding='utf-8'))
    if not isinstance(entries, list) or not entries or len(entries) > 2:
        raise ValueError('Runtime manifest must contain one or both pinned macOS runtimes')
    allowed = {f'cpython-{VERSION}+{RELEASE}-{value}-apple-darwin-install_only.tar.gz'
               for value in ARCHITECTURES.values()}
    seen = set()
    for entry in entries:
        if not isinstance(entry, dict) or set(entry) != {'file', 'url', 'sha256', 'bytes'}:
            raise ValueError('Unexpected runtime manifest fields')
        name = entry['file']
        if name not in allowed or name in seen:
            raise ValueError(f'Unexpected or duplicated runtime asset: {name}')
        if entry['url'] != BASE_URL + quote(name, safe=''):
            raise ValueError(f'Runtime URL is not the pinned official release: {name}')
        if not isinstance(entry['sha256'], str) or not re.fullmatch('[0-9a-f]{64}', entry['sha256']):
            raise ValueError(f'Invalid runtime checksum: {name}')
        if type(entry['bytes']) is not int or entry['bytes'] <= 0:
            raise ValueError(f'Invalid runtime size: {name}')
        seen.add(name)
    if require_all and seen != allowed:
        raise ValueError('The source manifest must contain both pinned macOS runtimes')
    return entries


def selected_entries(entries, arch):
    requested = tuple(ARCHITECTURES) if arch == 'universal' else (arch,)
    result = []
    for selected in requested:
        marker = '-' + ARCHITECTURES[selected] + '-apple-darwin-'
        matching = [entry for entry in entries if marker in entry['file']]
        if len(matching) != 1:
            raise ValueError(f'Runtime source is missing architecture {selected}')
        result.extend(matching)
    return result


def verify_runtime(path, entry):
    path = Path(path)
    if path.is_symlink() or not path.is_file():
        raise ValueError(f'Runtime must be a regular file: {path}')
    if path.stat().st_size != entry['bytes'] or digest_file(path) != entry['sha256']:
        raise ValueError(f'Runtime size or SHA-256 mismatch; existing file was left untouched: {path}')


def fetch_runtime(directory, entry, opener=None):
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / entry['file']
    if target.exists() or target.is_symlink():
        verify_runtime(target, entry)
        return target, False
    opener = opener or urlopen
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=directory, prefix='.' + entry['file'] + '.', suffix='.part', delete=False) as output:
            temporary = Path(output.name)
            received = 0
            with opener(entry['url'], timeout=60) as response:
                while True:
                    chunk = response.read(1024 * 1024)
                    if not chunk:
                        break
                    received += len(chunk)
                    if received > entry['bytes']:
                        raise ValueError(f'Runtime download exceeds pinned size: {entry["file"]}')
                    output.write(chunk)
            output.flush()
            os.fsync(output.fileno())
        verify_runtime(temporary, entry)
        temporary.chmod(0o644)
        try:
            # link() is atomic and never replaces an existing target, including a
            # mismatched file concurrently created by a different download.
            os.link(temporary, target)
        except FileExistsError:
            verify_runtime(target, entry)
            return target, False
        return target, True
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--arch', choices=[*ARCHITECTURES, 'universal'], default='universal')
    parser.add_argument('--resources-dir', type=Path, default=ROOT / 'resources',
                        help='Directory containing runtimes/manifest.json (default: resources)')
    args = parser.parse_args()
    directory = args.resources_dir / 'runtimes'
    entries = load_manifest(directory / 'manifest.json', require_all=True)
    for entry in selected_entries(entries, args.arch):
        path, downloaded = fetch_runtime(directory, entry)
        print(('Downloaded and verified: ' if downloaded else 'Verified cache: ') + str(path))


if __name__ == '__main__':
    main()
