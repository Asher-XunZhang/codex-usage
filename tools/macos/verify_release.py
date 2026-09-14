#!/usr/bin/env python3
"""Verify release ZIP content and signatures on macOS without installing or launching it."""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import plistlib
import shutil
import stat
import subprocess
import sys
import tempfile
import zipfile

from tools.common.paths import ROOT
from tools.common.distribution import DOCUMENTS, METADATA_FILES, check_path, documentation_files, source_files
from tools.macos.package import RELEASES, VERSION, release_readme
from tools.common.downloads import digest_file, verify_runtime
from tools.macos.fetch_runtime import load_manifest, selected_entries


def require(condition, message):
    if not condition:
        raise ValueError(message)


def inspect_archive(archive_path, destination, label):
    """Validate paths, CRCs, permissions, and checksums before safe extraction."""
    prefix = f'Codex用量-{VERSION}-{label}'
    with zipfile.ZipFile(archive_path) as archive:
        infos = archive.infolist()
        names = [info.filename for info in infos]
        require(len(names) == len(set(names)), 'Duplicate ZIP members')
        relative = {}
        for info in infos:
            pure = PurePosixPath(info.filename)
            require(not pure.is_absolute() and '\\' not in info.filename and
                    all(part not in ('', '.', '..') for part in info.filename.split('/')),
                    f'Unsafe ZIP path: {info.filename}')
            require(pure.parts[0] == prefix and len(pure.parts) > 1, f'Unexpected ZIP root: {info.filename}')
            name = '/'.join(pure.parts[1:])
            check_path(name)
            mode = info.external_attr >> 16
            require(stat.S_ISREG(mode), f'Non-regular ZIP member: {name}')
            require(not mode & (stat.S_ISUID | stat.S_ISGID | stat.S_IWOTH), f'Unsafe ZIP permissions: {name}')
            relative[name] = info
        require('SHA256SUMS.json' in relative, 'Missing SHA256SUMS.json')
        sums = json.loads(archive.read(relative['SHA256SUMS.json']))
        require(isinstance(sums, dict) and set(relative) == set(sums) | {'SHA256SUMS.json'}, 'Checksum inventory mismatch')
        for name, info in relative.items():
            output = destination / name
            output.parent.mkdir(parents=True, exist_ok=True)
            digest = hashlib.sha256()
            with archive.open(info) as source, output.open('xb') as target:
                for chunk in iter(lambda: source.read(1024 * 1024), b''):
                    digest.update(chunk)
                    target.write(chunk)
            if name != 'SHA256SUMS.json':
                require(digest.hexdigest() == sums[name], f'ZIP checksum mismatch: {name}')
            output.chmod((info.external_attr >> 16) & 0o777)
        return set(relative)


def verify_app(root, arch):
    app = root / 'Codex用量.app'
    contents = app / 'Contents'
    resources = contents / 'Resources'
    info = plistlib.loads((contents / 'Info.plist').read_bytes())
    require(info['CFBundleShortVersionString'] == VERSION, 'Unexpected app version')
    require(info['CFBundleIdentifier'] == 'local.codex-usage.desktop', 'Unexpected app bundle identifier')
    require(info['CFBundleExecutable'] == 'CodexUsage', 'Unexpected app executable')
    require(info['LSMinimumSystemVersion'] == '11.0', 'Unexpected macOS deployment target')
    binaries = ['CodexUsage', 'CodexSummary', 'CodexQuota']
    require({path.name for path in (contents / 'MacOS').iterdir()} == set(binaries), 'Unexpected host executables')
    helper = contents / 'Helpers/CodexUsageMain.app'
    helper_contents = helper / 'Contents'
    helper_info = plistlib.loads((helper_contents / 'Info.plist').read_bytes())
    require(helper_info['CFBundleIdentifier'] == 'local.codex-usage.desktop.main', 'Unexpected Main helper identifier')
    require(helper_info['CFBundleExecutable'] == 'CodexUsage' and helper_info['CFBundleShortVersionString'] == VERSION,
            'Unexpected Main helper executable/version')
    require({p.name for p in (helper_contents / 'MacOS').iterdir()} == {'CodexUsage'}, 'Unexpected Main helper executables')
    require({p.name for p in (helper_contents / 'Resources').iterdir()} == {'AppIcon.icns'}, 'Main helper must share host resources')
    for binary in [*(contents / 'MacOS' / name for name in binaries), helper_contents / 'MacOS/CodexUsage']:
        require(os.access(binary, os.X_OK), f'Executable permission lost: {binary}')
        found = subprocess.check_output(['lipo', '-archs', str(binary)], text=True).strip().split()
        require(found == [arch], f'Unexpected architecture for {binary}: {found}')
    require(digest_file(resources / 'AppIcon.icns') == digest_file(helper_contents / 'Resources/AppIcon.icns'),
            'Host and Main helper icons differ')
    for signed in (helper, app):
        subprocess.run(['codesign', '--verify', '--deep', '--strict', str(signed)], check=True)
    source_resources = root / 'source/resources/macos'
    source_manifest = load_manifest(source_resources / 'runtimes/manifest.json', require_all=True)
    manifest = load_manifest(resources / 'runtimes/manifest.json')
    require(manifest == selected_entries(source_manifest, arch), 'Packaged runtime manifest does not match pinned source')
    require({p.name for p in (resources / 'runtimes').iterdir()} == {'manifest.json', manifest[0]['file']}, 'Unexpected runtime files')
    verify_runtime(resources / 'runtimes' / manifest[0]['file'], manifest[0])
    build_info = json.loads((resources / 'BUILD-INFO.json').read_text(encoding='utf-8'))
    require(build_info['architecture'] == arch, 'Build provenance architecture mismatch')
    source_root = root / 'source'
    source_paths = [p for p in (source_root / 'src').rglob('*') if p.is_file() and p.suffix in ('.swift', '.c', '.h', '.py') and '__pycache__' not in p.parts]
    source_paths.extend([source_root / 'tools/macos/build.py', source_root / 'tools/common/paths.py'])
    fingerprints = {str(p.relative_to(source_root)): digest_file(p) for p in sorted(source_paths)}
    require(build_info['sources'] == fingerprints, 'Compiled source fingerprints differ from ZIP source')
    require(build_info['sourceDigest'] == hashlib.sha256(json.dumps(fingerprints, sort_keys=True).encode()).hexdigest(),
            'Build source digest mismatch')
    expected = {'AppIcon.icns', 'BUILD-INFO.json', 'THIRD-PARTY.md', 'runtimes/manifest.json', 'runtimes/' + manifest[0]['file']}
    for directory, source in [('backend', root / 'source/src/backend'), ('third-party-licenses', source_resources / 'third-party-licenses')]:
        for path in source.rglob('*'):
            if path.is_file():
                name = directory + '/' + path.relative_to(source).as_posix()
                expected.add(name)
                require(digest_file(path) == digest_file(resources / name), f'App and source resource differ: {name}')
    actual = {p.relative_to(resources).as_posix() for p in resources.rglob('*') if p.is_file()}
    require(actual == expected, 'App resource inventory differs from published source and pinned runtime')
    app_expected = {'Contents/Info.plist', 'Contents/_CodeSignature/CodeResources',
                    *(f'Contents/MacOS/{name}' for name in binaries),
                    'Contents/Helpers/CodexUsageMain.app/Contents/Info.plist',
                    'Contents/Helpers/CodexUsageMain.app/Contents/_CodeSignature/CodeResources',
                    'Contents/Helpers/CodexUsageMain.app/Contents/MacOS/CodexUsage',
                    'Contents/Helpers/CodexUsageMain.app/Contents/Resources/AppIcon.icns',
                    *('Contents/Resources/' + name for name in expected)}
    app_actual = {p.relative_to(app).as_posix() for p in app.rglob('*') if p.is_file()}
    require(app_actual == app_expected, 'Unexpected app bundle files outside the resource allowlist')
    require(digest_file(resources / 'THIRD-PARTY.md') == digest_file(source_resources / 'THIRD-PARTY.md'), 'License notice differs')
    return manifest[0]['file']


def verify_release(archive_path, label, arch):
    archive_path = Path(archive_path)
    digest = digest_file(archive_path)
    sidecar = archive_path.with_suffix('.zip.sha256').read_text(encoding='utf-8').split()
    require(sidecar == [digest, archive_path.name], 'Archive checksum sidecar mismatch')
    with tempfile.TemporaryDirectory(prefix='codex-usage-release-') as temporary:
        root = Path(temporary)
        actual = inspect_archive(archive_path, root, label)
        for name in ['README.md', 'LICENSE', 'source/docs/macos/VALIDATION.md',
                     'source/docs/macos/README.md', 'source/docs/README.md', 'source/CONTRIBUTING.md',
                     'source/README.md', 'source/LICENSE', 'source/THIRD-PARTY.md',
                     'source/build.py', 'source/package.py', 'source/test.py',
                     'source/tools/common/paths.py', 'source/tools/common/distribution.py',
                     'source/tools/macos/fetch_runtime.py', 'source/tools/macos/verify_release.py']:
            require(name in actual, f'Missing distributable source/documentation: {name}')
        require(not list((root / 'source/resources/macos/runtimes').glob('*.tar.gz')), 'Source must not duplicate binary runtime archives')
        runtime = verify_app(root, arch)
        source_root = root / 'source'
        expected = {'SHA256SUMS.json', 'README.md', 'LICENSE'}
        published_sources = source_files(source_root)
        expected.update('source/' + path.relative_to(source_root).as_posix() for path in published_sources)
        expected.update(path.relative_to(root).as_posix() for path in (root / 'Codex用量.app').rglob('*') if path.is_file())
        require(actual == expected, 'ZIP contains a file outside the explicit source/app/documentation allowlist')
        require((root / 'README.md').read_bytes() == release_readme(), 'Unexpected macOS release README or documentation entry points')
        require(digest_file(root / 'LICENSE') == digest_file(source_root / 'LICENSE'), 'Published source license differs')
        return {'archive': str(archive_path), 'bytes': archive_path.stat().st_size, 'sha256': digest,
                'arch': arch, 'files': len(actual), 'checksums': 'verified', 'executable_permissions': 'verified',
                'signatures': 'verified (not notarization)', 'helper': 'separate signed bundle',
                'runtime_archive': runtime, 'resource_inventory': 'matched published source'}


def main(argv=None, *, legacy=False):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist-dir', type=Path, default=ROOT / ('dist' if legacy else 'dist/macos'))
    parser.add_argument('--arch', choices=['arm64', 'x86_64', 'universal'], default='universal')
    parser.add_argument('--report', type=Path, help='Optionally save the verification JSON')
    args = parser.parse_args(argv)
    for command in ('lipo', 'codesign'):
        if shutil.which(command) is None:
            parser.error(f'{command} is required; run release verification on macOS with Xcode Command Line Tools')
    results = []
    for label, arch in RELEASES:
        if args.arch == 'universal' or args.arch == arch:
            archive = args.dist_dir / f'codex-usage-desktop-v{VERSION}-{label}.zip'
            results.append(verify_release(archive, label, arch))
    payload = json.dumps(results, ensure_ascii=False, indent=2) + '\n'
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(payload, encoding='utf-8')
    print(payload, end='')


if __name__ == '__main__':
    main()
