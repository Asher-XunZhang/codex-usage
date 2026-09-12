#!/usr/bin/env python3
"""Build a portable Windows x64 app with private .NET and Python runtimes."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from scripts.fetch_runtime import digest_file, fetch_runtime
from scripts.fetch_windows_runtime import extract_runtime, load_manifest

SDK_VERSION = '10.0.401'
BACKEND_FILES = ('compact_snapshot.py', 'dashboard_data.py', 'dashboard_server.py',
                 'disk_index.py', 'parent_watch.py', 'token_usage.py', 'windows_job.py')


def version(root=ROOT):
    value = ET.parse(root / 'windows/CodexUsage.csproj').findtext('./PropertyGroup/Version')
    if not value or any(c not in '0123456789.' for c in value):
        raise ValueError('The Windows project needs a numeric release Version')
    return value


def regular_files(directory):
    """Reject symlinks and Windows junctions before traversing release inputs."""
    directory = Path(directory)
    if directory.is_symlink() or getattr(directory.lstat(), 'st_file_attributes', 0) & 0x400:
        raise ValueError(f'Reparse points are not release inputs: {directory}')
    for current, directories, names in os.walk(directory):
        for name in sorted(directories + names):
            path = Path(current) / name
            if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
                raise ValueError(f'Reparse points are not release inputs: {path}')
        for name in sorted(names):
            path = Path(current) / name
            if not path.is_file():
                raise ValueError(f'Not a regular release file: {path}')
            yield path


def owned_output(path):
    path = Path(path).absolute()
    allowed = (ROOT / 'build').resolve()
    if path.is_symlink() or path.resolve().parent != allowed:
        raise ValueError('Build output must be a direct, non-linked directory inside this repository build/')
    if path.exists() and getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
        raise ValueError('Build output cannot be a Windows reparse point')
    return path


def build_sources(root=ROOT):
    result = {'backend/' + name: digest_file(root / 'backend' / name) for name in BACKEND_FILES}
    windows = root / 'windows'
    if windows.is_symlink() or getattr(windows.lstat(), 'st_file_attributes', 0) & 0x400:
        raise ValueError('Windows source directory cannot be a reparse point')
    for current, directories, names in os.walk(windows):
        directories[:] = sorted(d for d in directories if d not in ('bin', 'obj') and not d.startswith('.'))
        for name in directories + names:
            path = Path(current) / name
            if path.is_symlink() or getattr(path.lstat(), 'st_file_attributes', 0) & 0x400:
                raise ValueError(f'Reparse source: {path}')
        for name in sorted(names):
            path = Path(current) / name
            if path.suffix in {'.cs', '.csproj', '.xaml', '.manifest', '.ico'}:
                result[path.relative_to(root).as_posix()] = digest_file(path)
    return result


def build(output, dotnet=None):
    if os.name != 'nt':
        raise ValueError('Build the Windows desktop application on Windows')
    output = owned_output(output)
    sdk = Path(dotnet or ROOT / '.local/dotnet/dotnet.exe')
    if not sdk.is_file():
        found = shutil.which('dotnet')
        if dotnet or not found:
            raise ValueError(f'.NET SDK {SDK_VERSION} is required; pass --dotnet or extract it under .local/dotnet')
        sdk = Path(found)
    env = dict(os.environ, DOTNET_CLI_HOME=str(ROOT / '.local/dotnet-home'),
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_CLI_TELEMETRY_OPTOUT='1',
               DOTNET_GENERATE_ASPNET_CERTIFICATE='false', DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false',
               NUGET_PACKAGES=str(ROOT / '.local/nuget-packages'))
    found = subprocess.check_output([str(sdk), '--version'], cwd=ROOT, env=env, text=True).strip()
    if found != SDK_VERSION:
        raise ValueError(f'Expected .NET SDK {SDK_VERSION}, found {found}')
    entry = load_manifest()
    runtime, _ = fetch_runtime(ROOT / '.local/windows-runtime', entry)
    sources = build_sources()
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.windows-build-', dir=output.parent) as temporary:
        stage = Path(temporary) / 'app'
        nuget = Path(temporary) / 'NuGet.Config'
        nuget.write_text('<?xml version="1.0" encoding="utf-8"?>\n<configuration><packageSources>'
                         '<clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" />'
                         '</packageSources></configuration>\n', encoding='utf-8')
        subprocess.run([str(sdk), 'publish', str(ROOT / 'windows/CodexUsage.csproj'), '-c', 'Release',
                        '-r', 'win-x64', '--self-contained', 'true', '-o', str(stage),
                        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None',
                        '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true',
                        '-p:RestoreConfigFile=' + str(nuget)],
                       cwd=ROOT, env=env, check=True)
        (stage / 'backend').mkdir()
        for name in BACKEND_FILES:
            shutil.copy2(ROOT / 'backend' / name, stage / 'backend' / name)
        extract_runtime(runtime, stage / 'python', entry)
        licenses = stage / 'licenses'
        licenses.mkdir()
        for source, target in ((sdk.parent / 'LICENSE.txt', 'DOTNET-LICENSE.txt'),
                               (sdk.parent / 'ThirdPartyNotices.txt', 'DOTNET-ThirdPartyNotices.txt'),
                               (ROOT / 'LICENSE', 'CodexUsage-LICENSE.txt'),
                               (stage / 'python/LICENSE.txt', 'PYTHON-LICENSE.txt')):
            if not source.is_file():
                raise ValueError(f'Required distribution license is missing: {source}')
            shutil.copy2(source, licenses / target)
        shutil.copy2(ROOT / 'resources/runtimes/windows-manifest.json', stage / 'python-runtime.json')
        (stage / 'THIRD-PARTY.md').write_text(
            '# Windows runtime notices\n\nThis app bundles Microsoft .NET 10 Windows Desktop and '
            'PSF CPython 3.14.7 (x64). No system installation or pip packages are required.\n\n'
            '- .NET: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf\n'
            '- Python: https://www.python.org/downloads/release/python-3147/\n'
            '- Original license and notices: `licenses/`; Python also includes `python/LICENSE.txt`.\n'
            '- The Python archive is SHA-256 verified before extraction. Its `_pth` only adds `../backend`.\n'
            '- This build has not been Authenticode signed. Checksums verify integrity, not publisher identity.\n',
            encoding='utf-8')
        frameworks = json.loads((stage / 'CodexUsage.runtimeconfig.json').read_text(encoding='utf-8'))['runtimeOptions']['includedFrameworks']
        if build_sources() != sources:
            raise ValueError('Source changed during the build; build again after edits finish')
        manifest = dict(version=version(), architecture='win-x64', dotnet_sdk=SDK_VERSION,
                        dotnet_frameworks=frameworks, sources=sources,
                        python=entry, files={p.relative_to(stage).as_posix(): digest_file(p)
                                             for p in regular_files(stage)})
        (stage / 'BUILD-MANIFEST.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
        # Check all final targets before recursive removal; never delete a computed external path.
        if output.exists():
            owned_output(output)
            list(regular_files(output))
            shutil.rmtree(output)
        stage.rename(output)
    print(json.dumps({'output': str(output), 'version': version(), 'files': len(manifest['files'])}, indent=2))
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'build/windows-x64')
    parser.add_argument('--dotnet', type=Path)
    args = parser.parse_args()
    build(args.output, args.dotnet)


if __name__ == '__main__':
    main()
