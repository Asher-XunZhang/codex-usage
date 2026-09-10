#!/usr/bin/env python3
"""Build an offline, universal AppKit application with local Apple command line tools."""
import argparse
import json
from pathlib import Path
import plistlib
import platform
import shutil
import subprocess

from scripts.fetch_runtime import load_manifest, selected_entries, verify_runtime

ROOT = Path(__file__).resolve().parent

def run(*args):
    subprocess.run([str(arg) for arg in args], check=True)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--runtime-source', type=Path, default=ROOT / 'resources',
                        help='Directory with runtimes/ and third-party licenses (default: resources)')
    parser.add_argument('--arch', choices=['arm64', 'x86_64', 'universal'], default='universal')
    args = parser.parse_args()
    vendor = args.runtime_source
    manifest = selected_entries(load_manifest(vendor / 'runtimes/manifest.json'), args.arch)
    # Validate offline inputs before touching a previous build or invoking Xcode.
    for entry in manifest:
        runtime = vendor / 'runtimes' / entry['file']
        if not runtime.exists():
            raise SystemExit(f'Missing {runtime}. Run python3 scripts/fetch_runtime.py --arch {args.arch}')
        verify_runtime(runtime, entry)
    if not (vendor / 'third-party-licenses').is_dir() or not (vendor / 'THIRD-PARTY.md').is_file():
        raise SystemExit('Runtime source is missing third-party license files')
    build = ROOT / 'build'
    build.mkdir(exist_ok=True)
    app = build / 'Codex用量.app' if args.arch == 'universal' else build / args.arch / 'Codex用量.app'
    if app.exists():
        shutil.rmtree(app)
    contents = app / 'Contents'
    resources = contents / 'Resources'
    macos = contents / 'MacOS'
    macos.mkdir(parents=True)
    resources.mkdir()
    sdk = subprocess.check_output(['xcrun', '--show-sdk-path'], text=True).strip()
    architectures = ('arm64', 'x86_64') if args.arch == 'universal' else (args.arch,)
    binaries = ['CodexUsage', 'CodexQuota', 'CodexSummary']
    for arch in architectures:
        target = f'{arch}-apple-macosx11.0'
        stage = build / ('objects-' + arch)
        stage.mkdir(exist_ok=True)
        native = stage / 'UsageNative.o'
        run('xcrun', 'clang', '-Os', '-Wno-deprecated-declarations', '-target', target,
            '-c', ROOT / 'Sources/UsageNative.c', '-o', native)
        source_dir = stage / 'CodexUsage'
        source_dir.mkdir(exist_ok=True)
        shutil.copy2(ROOT / 'Sources/Entry.swift', source_dir / 'main.swift')
        run('xcrun', 'swiftc', '-swift-version', '5', '-Osize', '-whole-module-optimization',
            '-sdk', sdk, '-target', target, '-module-cache-path', build / 'module-cache',
            '-import-objc-header', ROOT / 'Sources/UsageNative.h', source_dir / 'main.swift',
            *[ROOT / 'Sources' / name for name in ['Main.swift', 'Chart.swift', 'Capsule.swift',
              'CapsuleHost.swift', 'StatusMenu.swift', 'Quota.swift', 'Runtime.swift', 'RefreshInterval.swift', 'WindowProcess.swift', 'UsageChangeMonitor.swift', 'Termination.swift']],
            native, '-o', stage / 'CodexUsage.bin')
        run('xcrun', 'swiftc', '-swift-version', '5', '-Osize', '-sdk', sdk, '-target', target,
            ROOT / 'Sources/QuotaHelper.swift', '-o', stage / 'CodexQuota.bin')
        run('xcrun', 'clang', '-Os', '-target', target, ROOT / 'Sources/Summary.c', '-lsqlite3', '-o', stage / 'CodexSummary.bin')
    for executable in binaries:
        if args.arch == 'universal':
            run('lipo', '-create', *[build / ('objects-' + arch) / (executable + '.bin') for arch in architectures],
                '-output', macos / executable)
        else:
            shutil.copy2(build / ('objects-' + args.arch) / (executable + '.bin'), macos / executable)
        run('codesign', '--force', '--sign', '-', '--timestamp=none', macos / executable)
    run('xcrun', 'swiftc', '-target', 'arm64-apple-macosx11.0' if platform.machine() == 'arm64' else 'x86_64-apple-macosx11.0',
        '-module-cache-path', build / 'module-cache', ROOT / 'Sources/Icon.swift', '-o', build / 'make-icon')
    run(build / 'make-icon', build / 'AppIcon.iconset')
    run('iconutil', '-c', 'icns', build / 'AppIcon.iconset', '-o', resources / 'AppIcon.icns')
    shutil.copytree(ROOT / 'backend', resources / 'backend', ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
    (resources / 'runtimes').mkdir()
    for entry in manifest:
        shutil.copy2(vendor / 'runtimes' / entry['file'], resources / 'runtimes' / entry['file'])
    (resources / 'runtimes/manifest.json').write_text(json.dumps(manifest, indent=2)+'\n')
    shutil.copytree(vendor / 'third-party-licenses', resources / 'third-party-licenses')
    shutil.copy2(vendor / 'THIRD-PARTY.md', resources / 'THIRD-PARTY.md')
    info = {
        'CFBundleName': 'Codex 用量', 'CFBundleDisplayName': 'Codex 用量',
        'CFBundleExecutable': 'CodexUsage', 'CFBundleIdentifier': 'local.codex-usage.desktop',
        'CFBundlePackageType': 'APPL', 'CFBundleShortVersionString': '1.0.0', 'CFBundleVersion': '100',
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
