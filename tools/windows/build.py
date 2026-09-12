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

from tools.common.paths import ROOT
from tools.common.downloads import digest_file, fetch_runtime
from tools.windows.fetch_runtime import extract_runtime, load_manifest

SDK_VERSION = '10.0.401'
BACKEND_FILES = ('compact_snapshot.py', 'dashboard_data.py', 'dashboard_server.py',
                 'disk_index.py', 'parent_watch.py', 'token_usage.py', 'windows_job.py')
SDK_NOTICES = (
    ('WINDOWS-SDK-LICENSE.rtf', 'dd07eb178e00c6bba4148457fc00ff77cd4887eb521d504186fe59c9ec8bbe62'),
    ('CSWINRT-LICENSE.txt', '9906940f61b1f0b533fa7d99baf55178b2808fbe113ea51dfbfad8572ccd5f2b'),
)


def dependency_notices(stage, assets_path, package_cache, root=ROOT):
    """Copy licenses for the actual published dependency graph, without network access.

    Windows SDK runtime DLLs come from a downloadDependency/runtimepack and are not
    listed in project.assets.json's ordinary package libraries.
    """
    stage, package_cache = Path(stage), Path(package_cache).resolve()
    assets = json.loads(Path(assets_path).read_text(encoding='utf-8'))
    deps = json.loads((stage / 'CodexUsage.deps.json').read_text(encoding='utf-8'))
    targets = deps['targets'][deps['runtimeTarget']['name']]
    licenses = stage / 'licenses'
    licenses.mkdir(exist_ok=True)
    reports = []
    for identity, library in sorted(deps['libraries'].items()):
        kind = library.get('type')
        if kind == 'project':
            continue
        package, release = identity.rsplit('/', 1)
        if package.startswith('runtimepack.'):
            package = package.removeprefix('runtimepack.')
            if package.startswith(('Microsoft.NETCore.App.Runtime.', 'Microsoft.WindowsDesktop.App.Runtime.',
                                   'Microsoft.AspNetCore.App.Runtime.')):
                continue  # Covered by the bundled .NET license and third-party notices.
        elif kind != 'package':
            raise ValueError(f'Unrecognized published dependency kind: {identity}')
        if any(not value or any(c not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_' for c in value)
               for value in (package, release)) or package in ('.', '..') or release in ('.', '..'):
            raise ValueError(f'Unsafe dependency identity: {identity}')
        selected = targets.get(identity, {})
        binaries = sorted({Path(name).name for group in ('runtime', 'native', 'runtimeTargets')
                           for name in selected.get(group, {}) if name.lower().endswith(('.dll', '.exe'))})
        if not binaries:
            continue
        for name in binaries:
            if not (stage / name).is_file():
                raise ValueError(f'Published dependency binary is missing: {name}')
        directory = package_cache / package.lower() / release.lower()
        if not directory.resolve().is_relative_to(package_cache):
            raise ValueError(f'Package escapes the NuGet cache: {identity}')
        package_files = list(regular_files(directory))
        nuspec = directory / (package.lower() + '.nuspec')
        metadata = ET.parse(nuspec).getroot().find('{*}metadata')
        if metadata is None or metadata.findtext('{*}id') != package or metadata.findtext('{*}version') != release:
            raise ValueError(f'Package metadata does not match published dependency: {identity}')
        declaration = metadata.find('{*}license')
        license_url = metadata.findtext('{*}licenseUrl') or ''
        expression = declaration.text if declaration is not None and declaration.get('type') == 'expression' else ''
        copied = []
        if package == 'Microsoft.Windows.SDK.NET.Ref':
            downloads = [item for framework in assets.get('project', {}).get('frameworks', {}).values()
                         for item in framework.get('downloadDependencies', [])]
            if not any(item.get('name') == package and item.get('version') == f'[{release}, {release}]' for item in downloads):
                raise ValueError('Published Windows SDK runtime pack does not match restored assets')
            for name, expected in SDK_NOTICES:
                source = root / 'resources/windows/licenses' / name
                if not source.is_file() or digest_file(source) != expected:
                    raise ValueError(f'Required pinned SDK distribution license is missing or changed: {name}')
                shutil.copy2(source, licenses / name)
                copied.append(name)
        else:
            restored = assets.get('libraries', {}).get(identity)
            if not restored or restored.get('type') != 'package':
                raise ValueError(f'Published package is absent from restored assets: {identity}')
            candidates = [file for file in package_files if file.parent == directory and
                          (file.name.lower().startswith(('license', 'notice', 'third-party', 'thirdparty')))]
            if declaration is not None and declaration.get('type') == 'file':
                declared = directory / (declaration.text or '')
                if declared.resolve() not in {file.resolve() for file in package_files}:
                    raise ValueError(f'Declared package license is unavailable: {identity}')
                if declared not in candidates:
                    candidates.append(declared)
            if not any(file.name.lower().startswith('license') or
                       declaration is not None and declaration.get('type') == 'file' and file == declared for file in candidates):
                raise ValueError(f'Published package has no bundled license; review before distribution: {identity}')
            for source in candidates:
                target = f'{package}-{release}-{source.name}'
                shutil.copy2(source, licenses / target)
                copied.append(target)
        # Preserve the publisher's original copyright, license declaration and repository metadata.
        metadata_target = f'{package}-{release}.nuspec.xml'
        shutil.copy2(nuspec, licenses / metadata_target)
        reports.append(dict(package=package, version=release, binaries=binaries, licenses=copied,
                            license_expression=expression, license_url=license_url, metadata=metadata_target))
    return reports


def dependency_notice_text(reports):
    lines = ['\n## Additional Windows components\n']
    for item in reports:
        files = ', '.join(f'`{name}`' for name in item['binaries'])
        notices = ', '.join(f'`licenses/{name}`' for name in item['licenses'])
        terms = item['license_expression'] or 'publisher license terms'
        lines.append(f"- {item['package']} {item['version']}: {files}. {terms}; {notices}. "
                     f"Publisher metadata: `licenses/{item['metadata']}`. {item['license_url']}\n")
    lines.append('- `WinRT.Runtime.dll` is the C#/WinRT support runtime (MIT); its license is included separately. '
                 'The Windows SDK package license also accompanies `Microsoft.Windows.SDK.NET.dll`.\n')
    return ''.join(lines)


def version(root=ROOT):
    value = ET.parse(root / 'src/windows/CodexUsage.csproj').findtext('./PropertyGroup/Version')
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
    resolved = path.resolve()
    if not resolved.is_relative_to(allowed) or resolved == allowed:
        raise ValueError('Build output must be a non-linked directory inside this repository build/')
    # Validate every ancestor before mkdir/rmtree, including the new platform folder.
    # Resolving only the final component would accept a junction hidden in its parent.
    for current in (path, *path.parents):
        try:
            attributes = getattr(current.lstat(), 'st_file_attributes', 0)
        except FileNotFoundError:
            attributes = 0
        if current.is_symlink() or attributes & 0x400:
            raise ValueError('Build output and its parents cannot be reparse points')
        if current == allowed:
            break
    if path.exists() and not path.is_dir():
        raise ValueError('Build output must be a directory')
    if resolved == allowed / 'windows':
        raise ValueError('Build output cannot replace the Windows platform output container')
    return path


def build_sources(root=ROOT):
    result = {'src/backend/' + name: digest_file(root / 'src/backend' / name) for name in BACKEND_FILES}
    for source in regular_files(root / 'resources/windows/licenses'):
        result[source.relative_to(root).as_posix()] = digest_file(source)
    windows = root / 'src/windows'
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
        subprocess.run([str(sdk), 'publish', str(ROOT / 'src/windows/CodexUsage.csproj'), '-c', 'Release',
                        '-r', 'win-x64', '--self-contained', 'true', '-o', str(stage),
                        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None',
                        '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true',
                        '-p:RestoreConfigFile=' + str(nuget)],
                       cwd=ROOT, env=env, check=True)
        (stage / 'backend').mkdir()
        for name in BACKEND_FILES:
            shutil.copy2(ROOT / 'src/backend' / name, stage / 'backend' / name)
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
        shutil.copy2(ROOT / 'resources/windows/runtimes/manifest.json', stage / 'python-runtime.json')
        dependencies = dependency_notices(stage, ROOT / 'src/windows/obj/project.assets.json',
                                         ROOT / '.local/nuget-packages')
        (stage / 'THIRD-PARTY.md').write_text(
            '# Windows runtime notices\n\nThis app bundles Microsoft .NET 10 Windows Desktop and '
            'PSF CPython 3.14.7 (x64). No system installation or pip packages are required.\n\n'
            '- .NET: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf\n'
            '- Python: https://www.python.org/downloads/release/python-3147/\n'
            '- Original license and notices: `licenses/`; Python also includes `python/LICENSE.txt`.\n'
            '- The Python archive is SHA-256 verified before extraction. Its `_pth` only adds `../backend`.\n'
            '- This build has not been Authenticode signed. Checksums verify integrity, not publisher identity.\n'
            + dependency_notice_text(dependencies),
            encoding='utf-8')
        frameworks = json.loads((stage / 'CodexUsage.runtimeconfig.json').read_text(encoding='utf-8'))['runtimeOptions']['includedFrameworks']
        if build_sources() != sources:
            raise ValueError('Source changed during the build; build again after edits finish')
        manifest = dict(version=version(), architecture='win-x64', dotnet_sdk=SDK_VERSION,
                        dotnet_frameworks=frameworks, dependencies=dependencies, sources=sources,
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


def main(*, legacy=False):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / ('build/windows-x64' if legacy else 'build/windows/x64'))
    parser.add_argument('--dotnet', type=Path)
    args = parser.parse_args()
    build(args.output, args.dotnet)


if __name__ == '__main__':
    main()
