#!/usr/bin/env python3
"""Fetch pinned macOS Python archives; downloads stay in a private local cache."""
import argparse
import json
from pathlib import Path
import re
from urllib.parse import quote
from urllib.request import urlopen

from tools.common.paths import ROOT, MACOS_RESOURCES
from tools.common.downloads import digest_file, verify_runtime
from tools.common.downloads import fetch_runtime as _fetch_runtime

ARCHITECTURES = {'arm64': 'aarch64', 'x86_64': 'x86_64'}
RELEASE = '20260901'
VERSION = '3.12.14'
BASE_URL = f'https://github.com/astral-sh/python-build-standalone/releases/download/{RELEASE}/'


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


def fetch_runtime(directory, entry, opener=None):
    # Keep the public injection point used by existing download callers/tests.
    return _fetch_runtime(directory, entry, opener or urlopen)


def main(argv=None, *, legacy=False):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--arch', choices=[*ARCHITECTURES, 'universal'], default='universal')
    parser.add_argument('--resources-dir', type=Path,
                        help='Directory containing runtimes/manifest.json (default: resources/macos)')
    parser.add_argument('--cache-dir', type=Path,
                        help='Archive cache (default: .local/macos-runtime; legacy entry: resources/runtimes)')
    args = parser.parse_args(argv)
    resources = args.resources_dir if args.resources_dir is not None else MACOS_RESOURCES
    entries = load_manifest(resources / 'runtimes/manifest.json', require_all=True)
    directory = args.cache_dir
    if directory is None:
        directory = (args.resources_dir or ROOT / 'resources') / 'runtimes' if legacy else ROOT / '.local/macos-runtime'
    for entry in selected_entries(entries, args.arch):
        path, downloaded = fetch_runtime(directory, entry)
        print(('Downloaded and verified: ' if downloaded else 'Verified cache: ') + str(path))


if __name__ == '__main__':
    main()
