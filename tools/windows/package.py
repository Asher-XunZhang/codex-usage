#!/usr/bin/env python3
"""Package the Windows app and explicitly allowed source, never local usage data."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import struct
import sys
import tempfile
import zipfile

from tools.common.paths import ROOT
from tools.common.distribution import METADATA_FILES, source_files
from tools.windows.build import BACKEND_FILES, SDK_VERSION, build_sources, regular_files, version
from tools.common.downloads import digest_file
from tools.windows.fetch_runtime import load_manifest, PTH

PRIVATE_NAMES = {'auth.json', 'config.toml', 'settings.json', 'worker.json', 'budget.json', '.env'}
PRIVATE_SUFFIXES = {'.jsonl', '.sqlite', '.sqlite3', '.db', '.csv', '.log', '.pyc', '.pyo', '.pdb'}
RESERVED = {'con', 'prn', 'aux', 'nul', *(f'com{i}' for i in range(1, 10)), *(f'lpt{i}' for i in range(1, 10))}


def check_path(name):
    pure = PurePosixPath(name)
    metadata = False
    if any(part.startswith('.') for part in pure.parts):
        # Development metadata is allowed only at the exact curated source paths.
        # ZIP members add one known release root; arbitrary hidden ancestors stay forbidden.
        prefixes = ('', 'source/', f'CodexUsage-{version()}-windows-x64/source/')
        metadata = any(name.startswith(prefix) and name[len(prefix):] in METADATA_FILES
                       for prefix in prefixes)
    if (not name or pure.is_absolute() or '\\' in name or ':' in name
            or any(part in ('', '.', '..') or part.startswith('.') and not metadata for part in name.split('/'))
            or pure.name.casefold() in PRIVATE_NAMES or pure.suffix.casefold() in PRIVATE_SUFFIXES
            or any(part.casefold() in ('__pycache__', 'node_modules', 'obj', 'bin')
                   or part.rstrip(' .') != part or part.split('.')[0].casefold() in RESERVED for part in pure.parts)):
        raise ValueError(f'Unsafe or private release path: {name}')


def verify_pe_x64(path):
    with Path(path).open('rb') as source:
        header = source.read(64)
        if len(header) != 64 or header[:2] != b'MZ':
            raise ValueError(f'Not a Windows PE executable: {path}')
        offset = struct.unpack_from('<I', header, 60)[0]
        if not 64 <= offset <= 1024 * 1024:
            raise ValueError(f'Invalid PE header offset: {path}')
        source.seek(offset)
        signature = source.read(6)
        if signature != b'PE\0\0\x64\x86':
            raise ValueError(f'Not a Windows x64 binary: {path}')


def validate_app(root, check_sources=True, extra_files=()):
    root = Path(root)
    files = {p.relative_to(root).as_posix(): p for p in regular_files(root)}
    if 'BUILD-MANIFEST.json' not in files:
        raise ValueError('Missing BUILD-MANIFEST.json; run python -m tools.windows.build first')
    manifest = json.loads(files['BUILD-MANIFEST.json'].read_text(encoding='utf-8'))
    if (manifest.get('architecture') != 'win-x64' or manifest.get('python') != load_manifest()
            or manifest.get('version') != version() or manifest.get('dotnet_sdk') != SDK_VERSION):
        raise ValueError('Unexpected app version, architecture, or Python runtime')
    inventory = manifest.get('files')
    if (not isinstance(inventory, dict)
            or set(files) != set(inventory) | {'BUILD-MANIFEST.json'} | set(extra_files)):
        raise ValueError('Build file inventory differs: output may contain local data or stale files')
    files = {name: path for name, path in files.items() if name not in extra_files}
    for name, expected in inventory.items():
        check_path(name)
        if not isinstance(expected, str) or not re.fullmatch('[0-9a-f]{64}', expected) or digest_file(files[name]) != expected:
            raise ValueError(f'Build checksum mismatch: {name}')
    required = {'CodexUsage.exe', 'CodexUsage.dll', 'CodexUsage.deps.json', 'CodexUsage.runtimeconfig.json',
                'coreclr.dll', 'PresentationFramework.dll', 'python/python.exe', 'python/python314.zip',
                'python/python314._pth', 'python-runtime.json', 'licenses/DOTNET-LICENSE.txt',
                'licenses/DOTNET-ThirdPartyNotices.txt', 'licenses/PYTHON-LICENSE.txt', 'THIRD-PARTY.md',
                *('backend/' + name for name in BACKEND_FILES)}
    if not required <= files.keys():
        raise ValueError(f'Missing required app files: {sorted(required - files.keys())}')
    for name in ('CodexUsage.exe', 'coreclr.dll', 'python/python.exe', 'python/python314.dll'):
        verify_pe_x64(root / name)
    if (root / 'python/python314._pth').read_text(encoding='utf-8') != PTH:
        raise ValueError('Python search path is not isolated to the bundled runtime/backend')
    frameworks = json.loads((root / 'CodexUsage.runtimeconfig.json').read_text(encoding='utf-8'))['runtimeOptions'].get('includedFrameworks')
    if (frameworks != manifest.get('dotnet_frameworks') or not frameworks
            or {x['name'] for x in frameworks} != {'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App'}):
        raise ValueError('.NET runtime is not a self-contained Windows Desktop distribution')
    if check_sources:
        if manifest.get('sources') != build_sources():
            raise ValueError('C# or backend sources changed since this build; rebuild before packaging')
        for name in BACKEND_FILES:
            if digest_file(ROOT / 'src/backend' / name) != digest_file(root / 'backend' / name):
                raise ValueError(f'Backend changed since this build: {name}')
    return manifest, files


def windows_sources(root=ROOT):
    root = Path(root)
    # The shared allowlist carries both platforms and excludes build intermediates.
    # Do not append another Windows tree: each source has one canonical archive path.
    files = list(source_files(root))
    for path in files:
        check_path(path.relative_to(root).as_posix())
        if path.is_symlink() or not path.is_file():
            raise ValueError(f'Not a regular source file: {path}')
    return files


def release_readme():
    return ('# Codex 用量 · Windows\n\n'
            '解压完整目录后运行 `CodexUsage.exe`，无需另装 Python 或 .NET。\n\n'
            '- [Windows 使用、构建与验证说明](source/docs/windows/README.md)\n'
            '- [完整文档索引](source/docs/README.md)\n'
            '- [许可证](LICENSE)\n\n'
            '文档和图片集中保存在 `source/docs/`，可离线查看。\n').encode('utf-8')


def package(app, output_dir):
    app, output_dir = Path(app), Path(output_dir)
    manifest, app_files = validate_app(app)
    payload = dict(app_files)
    payload['README.md'] = release_readme()
    payload['LICENSE'] = ROOT / 'LICENSE'
    # Keep one canonical documentation tree inside the distributable source.
    # Its relative links remain valid both offline and when rebuilding that source.
    payload.update({'source/' + p.relative_to(ROOT).as_posix(): p for p in windows_sources()})
    hashes = {}
    seen = set()
    for name, path in payload.items():
        check_path(name)
        if name.casefold() in seen:
            raise ValueError(f'Case-insensitive path collision: {name}')
        seen.add(name.casefold())
        if isinstance(path, bytes):
            hashes[name] = hashlib.sha256(path).hexdigest()
            continue
        if not path.is_file() or path.is_symlink():
            raise ValueError(f'Missing regular payload file: {path}')
        hashes[name] = digest_file(path)
    output_dir.mkdir(parents=True, exist_ok=True)
    prefix = f'CodexUsage-{manifest["version"]}-windows-x64'
    target = output_dir / f'codex-usage-desktop-v{manifest["version"]}-windows-x64.zip'
    with tempfile.NamedTemporaryFile(dir=output_dir, prefix='.' + target.name, suffix='.part', delete=False) as handle:
        temporary = Path(handle.name)
    try:
        with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for name, path in sorted(payload.items()):
                info = zipfile.ZipInfo(prefix + '/' + name)
                info.create_system = 3
                info.external_attr = (stat.S_IFREG | 0o644) << 16
                info.compress_type = zipfile.ZIP_DEFLATED
                if isinstance(path, bytes):
                    archive.writestr(info, path)
                    continue
                with archive.open(info, 'w', force_zip64=True) as dest, path.open('rb') as source:
                    import shutil
                    shutil.copyfileobj(source, dest)
            info = zipfile.ZipInfo(prefix + '/SHA256SUMS.json')
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            archive.writestr(info, json.dumps(hashes, indent=2) + '\n')
        with zipfile.ZipFile(temporary) as archive:
            if archive.testzip() is not None:
                raise ValueError('Packaged ZIP failed CRC verification')
            for name, expected in hashes.items():
                digest = hashlib.sha256()
                with archive.open(prefix + '/' + name) as source:
                    for chunk in iter(lambda: source.read(1024 * 1024), b''):
                        digest.update(chunk)
                if digest.hexdigest() != expected:
                    raise ValueError(f'Payload changed during packaging: {name}')
        os.replace(temporary, target)
    finally:
        temporary.unlink(missing_ok=True)
    digest = digest_file(target)
    target.with_suffix('.zip.sha256').write_text(f'{digest}  {target.name}\n', encoding='utf-8')
    result = dict(file=str(target), bytes=target.stat().st_size, sha256=digest, files=len(payload) + 1)
    print(json.dumps(result, indent=2))
    return target


def main(*, legacy=False):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, default=ROOT / ('build/windows-x64' if legacy else 'build/windows/x64'))
    parser.add_argument('--output-dir', type=Path, default=ROOT / ('dist' if legacy else 'dist/windows'))
    args = parser.parse_args()
    package(args.app, args.output_dir)


if __name__ == '__main__':
    main()
