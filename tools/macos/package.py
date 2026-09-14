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

from tools.common.paths import ROOT
from tools.common.downloads import digest_file
from tools.common.distribution import (DOCUMENTS, ROOT_SOURCES, SOURCE_TREES, DOCUMENTATION_MANIFESTS, METADATA_FILES,
                                       PRIVATE_SUFFIXES, PRIVATE_NAMES, check_path, regular_files,
                                       documentation_files, source_files)

VERSION = '1.0.2'
RELEASES = [('AppleSilicon', 'arm64'), ('Intel', 'x86_64')]


def release_readme():
    return ('# Codex 用量 · macOS\n\n'
            '打开同目录的 `Codex用量.app`；也可将应用复制到用户的 Applications 文件夹。\n\n'
            '- [macOS 使用与安装说明](source/docs/macos/README.md)\n'
            '- [全部文档](source/docs/README.md)\n'
            '- [源码与开发](source/CONTRIBUTING.md)\n'
            '- [第三方组件声明](source/THIRD-PARTY.md)\n'
            '- [许可证](LICENSE)\n\n'
            '源码、文档与图片集中保存在 `source/`，可离线查看。\n').encode('utf-8')


def package(label, arch, output_dir=None, root=ROOT, *, build_dir=None):
    root = Path(root)
    output_dir = Path(output_dir) if output_dir is not None else root / 'dist/macos'
    output_dir.mkdir(parents=True, exist_ok=True)
    prefix = f'Codex用量-{VERSION}-{label}'
    target = output_dir / f'codex-usage-desktop-v{VERSION}-{label}.zip'
    app = (Path(build_dir) if build_dir is not None else root / 'build/macos') / arch / 'Codex用量.app'
    if not (app / 'Contents/Info.plist').is_file():
        raise ValueError(f'Build {arch} before packaging: {app}')
    files = [(path, f'Codex用量.app/{path.relative_to(app).as_posix()}') for path in regular_files(app)]
    files.extend([(release_readme(), 'README.md'), (root / 'LICENSE', 'LICENSE')])
    published_sources = source_files(root)
    files.extend((path, 'source/' + path.relative_to(root).as_posix()) for path in published_sources)
    checksums = {}
    for path, name in files:
        check_path(name)
        if name in checksums:
            raise ValueError(f'Duplicate release path: {name}')
        if isinstance(path, bytes):
            checksums[name] = hashlib.sha256(path).hexdigest()
            continue
        if path.is_symlink() or not path.is_file():
            raise ValueError(f'Release file is missing or not regular: {path}')
        checksums[name] = digest_file(path)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=output_dir, prefix='.' + target.name + '.', suffix='.part', delete=False) as handle:
            temporary = Path(handle.name)
        with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for path, name in files:
                if isinstance(path, bytes):
                    info = zipfile.ZipInfo(f'{prefix}/{name}')
                    info.create_system = 3
                    info.external_attr = (stat.S_IFREG | 0o644) << 16
                    info.compress_type = zipfile.ZIP_DEFLATED
                    archive.writestr(info, path)
                else:
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


def main(argv=None, *, legacy=False):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output-dir', type=Path, default=ROOT / ('dist' if legacy else 'dist/macos'))
    parser.add_argument('--arch', choices=['arm64', 'x86_64', 'universal'], default='universal',
                        help='Package one architecture, or both per-architecture builds (universal)')
    args = parser.parse_args(argv)
    for label, arch in RELEASES:
        if args.arch == 'universal' or args.arch == arch:
            package(label, arch, args.output_dir, build_dir=ROOT / ('build' if legacy else 'build/macos'))


if __name__ == '__main__':
    main()
