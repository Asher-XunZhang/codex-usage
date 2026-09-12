#!/usr/bin/env python3
"""Build an offline, universal AppKit application with local Apple command line tools."""
import argparse
import json
from pathlib import Path
import plistlib
import platform
import shutil
import subprocess

from tools.common.paths import ROOT, BACKEND, MACOS_RESOURCES, macos_source
from tools.macos.fetch_runtime import load_manifest, selected_entries, verify_runtime


def checked_build_path(path, *, allow_container=False):
    """Validate every existing path component before writing or removing build output."""
    container = ROOT.resolve() / 'build'
    path = Path(path).absolute()
    if '..' in path.parts or not path.is_relative_to(container):
        raise ValueError(f'Build output must stay inside {container}: {path}')
    if not allow_container and path in (container, container / 'macos'):
        raise ValueError(f'Refusing to use a build container as an output target: {path}')
    current = container
    for part in (None, *path.relative_to(container).parts):
        if part is not None:
            current /= part
        if current.is_symlink() or (current.exists() and getattr(current.lstat(), 'st_file_attributes', 0) & 0x400):
            raise ValueError(f'Build output cannot contain symlinks or reparse points: {current}')
    if not path.resolve().is_relative_to(container):
        raise ValueError(f'Resolved build output escapes {container}: {path}')
    return path


def run(*args):
    subprocess.run([str(arg) for arg in args], check=True)

def main(argv=None, *, legacy=False):
    parser = argparse.ArgumentParser()
    parser.add_argument('--runtime-source', type=Path, default=None,
                        help='Offline directory with runtimes/ and third-party licenses (default: resources/macos plus .local/macos-runtime cache)')
    parser.add_argument('--arch', choices=['arm64', 'x86_64', 'universal'], default='universal')
    args = parser.parse_args(argv)
    vendor = args.runtime_source if args.runtime_source is not None else MACOS_RESOURCES
    manifest = selected_entries(load_manifest(vendor / 'runtimes/manifest.json'), args.arch)
    # Validate offline inputs before touching a previous build or invoking Xcode.
    runtimes = {}
    for entry in manifest:
        candidates = [vendor / 'runtimes' / entry['file']]
        if args.runtime_source is None:
            candidates.extend([ROOT / '.local/macos-runtime' / entry['file'], ROOT / 'resources/runtimes' / entry['file']])
        runtime = next((path for path in candidates if path.exists() or path.is_symlink()), candidates[0])
        if not runtime.exists():
            raise SystemExit(f'Missing {runtime}. Run python3 -m tools.macos.fetch_runtime --arch {args.arch}')
        verify_runtime(runtime, entry)
        runtimes[entry['file']] = runtime
    if not (vendor / 'third-party-licenses').is_dir() or not (vendor / 'THIRD-PARTY.md').is_file():
        raise SystemExit('Runtime source is missing third-party license files')
    build = ROOT / 'build' if legacy else ROOT / 'build/macos'
    app = build / 'Codex用量.app' if legacy and args.arch == 'universal' else build / args.arch / 'Codex用量.app'
    checked_build_path(build, allow_container=True)
    checked_build_path(app)
    architectures = ('arm64', 'x86_64') if args.arch == 'universal' else (args.arch,)
    outputs = [build / 'module-cache', build / 'make-icon', build / 'AppIcon.iconset']
    for arch in architectures:
        stage = build / ('objects-' + arch)
        outputs.extend([stage, stage / 'UsageNative.o', stage / 'CodexUsage', stage / 'CodexUsage/main.swift',
                        *(stage / (name + '.bin') for name in ('CodexUsage', 'CodexQuota', 'CodexSummary'))])
    for path in outputs:
        checked_build_path(path)
    build.mkdir(parents=True, exist_ok=True)
    if app.exists():
        checked_build_path(app)
        shutil.rmtree(app)
    contents = app / 'Contents'
    resources = contents / 'Resources'
    macos = contents / 'MacOS'
    macos.mkdir(parents=True)
    resources.mkdir()
    sdk = subprocess.check_output(['xcrun', '--show-sdk-path'], text=True).strip()
    binaries = ['CodexUsage', 'CodexQuota', 'CodexSummary']
    for arch in architectures:
        target = f'{arch}-apple-macosx11.0'
        stage = build / ('objects-' + arch)
        stage.mkdir(exist_ok=True)
        native = stage / 'UsageNative.o'
        run('xcrun', 'clang', '-Os', '-Wno-deprecated-declarations', '-target', target,
            '-c', macos_source('UsageNative.c'), '-o', native)
        source_dir = stage / 'CodexUsage'
        source_dir.mkdir(exist_ok=True)
        shutil.copy2(macos_source('Entry.swift'), source_dir / 'main.swift')
        run('xcrun', 'swiftc', '-swift-version', '5', '-Osize', '-whole-module-optimization',
            '-sdk', sdk, '-target', target, '-module-cache-path', build / 'module-cache',
            '-import-objc-header', macos_source('UsageNative.h'), source_dir / 'main.swift',
            *[macos_source(name) for name in ['Main.swift', 'Chart.swift', 'Capsule.swift',
              'CapsuleHost.swift', 'StatusMenu.swift', 'Quota.swift', 'Runtime.swift', 'WindowProcess.swift', 'UsageChangeMonitor.swift', 'Termination.swift', 'BudgetCore.swift', 'ControlFeedback.swift', 'BudgetUI.swift', 'BudgetHost.swift']],
            native, '-o', stage / 'CodexUsage.bin')
        run('xcrun', 'swiftc', '-swift-version', '5', '-Osize', '-sdk', sdk, '-target', target,
            macos_source('QuotaHelper.swift'), '-o', stage / 'CodexQuota.bin')
        run('xcrun', 'clang', '-Os', '-target', target, macos_source('Summary.c'), '-lsqlite3', '-o', stage / 'CodexSummary.bin')
    for executable in binaries:
        if args.arch == 'universal':
            run('lipo', '-create', *[build / ('objects-' + arch) / (executable + '.bin') for arch in architectures],
                '-output', macos / executable)
        else:
            shutil.copy2(build / ('objects-' + args.arch) / (executable + '.bin'), macos / executable)
        run('codesign', '--force', '--sign', '-', '--timestamp=none', macos / executable)
    run('xcrun', 'swiftc', '-target', 'arm64-apple-macosx11.0' if platform.machine() == 'arm64' else 'x86_64-apple-macosx11.0',
        '-module-cache-path', build / 'module-cache', ROOT / 'tools/macos/Icon.swift', '-o', build / 'make-icon')
    run(build / 'make-icon', build / 'AppIcon.iconset')
    run('iconutil', '-c', 'icns', build / 'AppIcon.iconset', '-o', resources / 'AppIcon.icns')
    shutil.copytree(BACKEND, resources / 'backend', ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
    (resources / 'runtimes').mkdir()
    for entry in manifest:
        shutil.copy2(runtimes[entry['file']], resources / 'runtimes' / entry['file'])
    (resources / 'runtimes/manifest.json').write_text(json.dumps(manifest, indent=2)+'\n')
    shutil.copytree(vendor / 'third-party-licenses', resources / 'third-party-licenses')
    shutil.copy2(vendor / 'THIRD-PARTY.md', resources / 'THIRD-PARTY.md')
    info = {
        'CFBundleName': 'Codex 用量', 'CFBundleDisplayName': 'Codex 用量',
        'CFBundleExecutable': 'CodexUsage', 'CFBundleIdentifier': 'local.codex-usage.desktop',
        'CFBundlePackageType': 'APPL', 'CFBundleShortVersionString': '1.0.1', 'CFBundleVersion': '101',
        'CFBundleIconFile': 'AppIcon', 'LSMinimumSystemVersion': '11.0', 'NSHighResolutionCapable': True,
        'NSPrincipalClass': 'NSApplication', 'NSHumanReadableCopyright': 'Independent local usage tool. Not affiliated with OpenAI.',
        'NSAppTransportSecurity': {'NSAllowsLocalNetworking': True},
    }
    (contents / 'Info.plist').write_bytes(plistlib.dumps(info))
    # A distinct bundle identity prevents Launch Services from reopening the host.
    # Both roles share the compiled code; the helper reads data/runtime resources
    # from the enclosing app, so the runtime archive is packaged only once.
    helper = contents / 'Helpers' / 'CodexUsageMain.app'
    helper_contents = helper / 'Contents'
    (helper_contents / 'MacOS').mkdir(parents=True)
    (helper_contents / 'Resources').mkdir()
    shutil.copy2(macos / 'CodexUsage', helper_contents / 'MacOS' / 'CodexUsage')
    shutil.copy2(resources / 'AppIcon.icns', helper_contents / 'Resources' / 'AppIcon.icns')
    helper_info = dict(info, CFBundleName='Codex 用量主面板', CFBundleDisplayName='Codex 用量',
                       CFBundleIdentifier='local.codex-usage.desktop.main')
    (helper_contents / 'Info.plist').write_bytes(plistlib.dumps(helper_info))
    run('codesign', '--force', '--sign', '-', '--timestamp=none', helper)
    run('codesign', '--force', '--sign', '-', '--timestamp=none', app)
    run('codesign', '--verify', '--deep', '--strict', '--verbose=2', app)
    print(app)

if __name__ == '__main__':
    main()
