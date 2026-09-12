#!/usr/bin/env python3
"""Package signed apps and an explicit source allowlist, excluding local data."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import tempfile
import zipfile

from scripts.fetch_runtime import digest_file

ROOT = Path(__file__).resolve().parent
VERSION = '1.0.1'
RELEASES = [('AppleSilicon', 'arm64'), ('Intel', 'x86_64')]
DOCUMENTS = ['README.md', 'LICENSE', 'THIRD-PARTY.md', 'CHANGELOG.md']
PRIVATE_SUFFIXES = ('.jsonl', '.sqlite', '.sqlite3', '.db', '.csv', '.log', '.pyc', '.pyo')
PRIVATE_NAMES = {'auth.json', 'config.toml', '.DS_Store', '.env'}
SOURCE_TREES = {'windows': {'.cs', '.csproj', '.xaml', '.manifest', '.ico'}, 'Sources': {'.swift', '.c', '.h'}, 'backend': {'.py'},
                'tests': {'.py'}, 'scripts': {'.py', '.swift', '.sh'}, 'docs': {'.md', '.png'},
                'skill': {'.md', '.yaml', '.py'}}


def check_path(name):
    path = Path(name)
    metadata = path.as_posix().removeprefix('source/') == '.github/workflows/windows.yml'
    if path.is_absolute() or any(part in ('..', '__pycache__', 'bin', 'obj') or part.startswith('.') and not metadata for part in path.parts):
        raise ValueError(f'Unsafe or hidden release path: {name}')
    if name.lower().endswith(PRIVATE_SUFFIXES) or path.name in PRIVATE_NAMES:
        raise ValueError(f'Local/private data is not permitted in a release: {name}')


def regular_files(directory):
    import os
    directory = Path(directory)
    if not directory.exists():
        return
    for current, directories, names in os.walk(directory):
        for name in directories + names:
            path = Path(current) / name
            if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
                raise ValueError(f'Links are not permitted in release inputs: {path}')
        directories[:] = sorted(d for d in directories if d not in ('bin', 'obj', '__pycache__', 'node_modules') and not d.startswith('.'))
        for name in sorted(names):
            path = Path(current) / name
            if path.is_file():
                yield path


def source_files(root):
    root = Path(root)
    result = []
    for name in DOCUMENTS + ['build.py', 'test.py', 'package.py', '.github/workflows/windows.yml']:
        path = root / name
        if not path.is_file() or path.is_symlink():
            raise ValueError(f'Missing regular source file: {path}')
        result.append(path)
    pytest_config = root / 'pytest.ini'
    if pytest_config.exists() or pytest_config.is_symlink():
        if not pytest_config.is_file() or pytest_config.is_symlink():
            raise ValueError(f'Invalid pytest configuration: {pytest_config}')
        result.append(pytest_config)
    for directory, suffixes in SOURCE_TREES.items():
        for path in regular_files(root / directory):
            relative = path.relative_to(root)
            if '__pycache__' in relative.parts:
                continue
            if path.suffix in suffixes:
                check_path(relative.as_posix())
                result.append(path)
    for name in ['resources/runtimes/manifest.json', 'resources/runtimes/windows-manifest.json', 'resources/THIRD-PARTY.md']:
        path = root / name
        if not path.is_file() or path.is_symlink():
            raise ValueError(f'Missing regular resource file: {path}')
        result.append(path)
    for path in regular_files(root / 'resources/third-party-licenses'):
        if path.name == 'LICENSE' or path.name.startswith('LICENSE.') or path.name == 'python-licenses.rst':
            result.append(path)
        else:
            raise ValueError(f'Unexpected third-party license file: {path}')
    for path in regular_files(root / 'resources/licenses'):
        check_path(path.relative_to(root).as_posix())
        result.append(path)
    return result


def package(label, arch, output_dir=None, root=ROOT):
    root = Path(root)
    output_dir = Path(output_dir) if output_dir is not None else root / 'dist'
    output_dir.mkdir(parents=True, exist_ok=True)
    prefix = f'Codex用量-{VERSION}-{label}'
    target = output_dir / f'codex-usage-desktop-v{VERSION}-{label}.zip'
    app = root / 'build' / arch / 'Codex用量.app'
    if not (app / 'Contents/Info.plist').is_file():
        raise ValueError(f'Build {arch} before packaging: {app}')
    files = [(path, f'Codex用量.app/{path.relative_to(app).as_posix()}') for path in regular_files(app)]
    for name in DOCUMENTS:
        files.append((root / name, name))
    for path in regular_files(root / 'docs'):
        if path.suffix in {'.md', '.png'}:
            files.append((path, path.relative_to(root).as_posix()))
    published_sources = source_files(root)
    files.extend((path, 'source/' + path.relative_to(root).as_posix()) for path in published_sources)
    # Keep the same README links valid in the repository, ZIP root, and source/.
    # This duplicates small manifests/licenses, never the binary runtime archive.
    files.extend((path, path.relative_to(root).as_posix()) for path in published_sources
                 if path.relative_to(root).parts[0] == 'resources')
    checksums = {}
    for path, name in files:
        check_path(name)
        if path.is_symlink() or not path.is_file():
            raise ValueError(f'Release file is missing or not regular: {path}')
        if name in checksums:
            raise ValueError(f'Duplicate release path: {name}')
        checksums[name] = digest_file(path)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=output_dir, prefix='.' + target.name + '.', suffix='.part', delete=False) as handle:
            temporary = Path(handle.name)
        with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for path, name in files:
                archive.write(path, f'{prefix}/{name}')
            info = zipfile.ZipInfo(f'{prefix}/SHA256SUMS.json')
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            archive.writestr(info, json.dumps(checksums, ensure_ascii=False, indent=2) + '\n')
        with zipfile.ZipFile(temporary) as archive:
            if archive.testzip() is not None:
                raise ValueError('ZIP CRC verification failed')
            for name, expected in checksums.items():
                with archive.open(f'{prefix}/{name}') as member:
                    digest = hashlib.sha256()
                    for chunk in iter(lambda: member.read(1024 * 1024), b''):
                        digest.update(chunk)
                if digest.hexdigest() != expected:
                    raise ValueError(f'Packaged checksum mismatch: {name}')
        temporary.chmod(0o644)
        os.replace(temporary, target)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
    digest = digest_file(target)
    target.with_suffix('.zip.sha256').write_text(f'{digest}  {target.name}\n', encoding='utf-8')
    result = {'file': str(target), 'bytes': target.stat().st_size, 'sha256': digest, 'files': len(files) + 1}
    print(json.dumps(result, ensure_ascii=False))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, default=ROOT / 'dist')
    parser.add_argument('--arch', choices=['arm64', 'x86_64', 'universal'], default='universal',
                        help='Package one architecture, or both per-architecture builds (universal)')
    args = parser.parse_args()
    for label, arch in RELEASES:
        if args.arch == 'universal' or args.arch == arch:
            package(label, arch, args.output_dir)


if __name__ == '__main__':
    main()
