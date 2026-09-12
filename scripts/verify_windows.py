#!/usr/bin/env python3
"""Verify ZIP inventory, hashes and isolated offline execution of the Windows release."""
import argparse
from datetime import datetime
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import struct
import subprocess
import sys
import tempfile
import time
from urllib.error import HTTPError
from urllib.parse import unquote, urlsplit
from urllib.request import ProxyHandler, Request, build_opener
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from scripts.build_windows import SDK_VERSION, version
from scripts.fetch_runtime import digest_file
from scripts.package_windows import check_path, validate_app, windows_sources, verify_pe_x64, documentation_images

CONTROL_CHECKS = {
    'segments-continuous', 'segments-keyboard', 'combo-popup', 'combo-scroll', 'combo-escape',
    'combo-keyboard', 'menu-popup', 'menu-check-disabled', 'submenu-popup', 'menu-enter',
    'menu-scroll', 'checkbox-themed',
}
CONTROL_IMAGES = {f'{view}-{theme}.png' for theme in ('light', 'dark') for view in (
    'controls', 'dropdown', 'dropdown-scrolled', 'menu', 'menu-scrolled', 'submenu')}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def verify_documentation_images(root):
    root = Path(root).resolve()
    for name in ('README.md', 'docs/WINDOWS.md', 'source/docs/WINDOWS.md'):
        document = root / name
        markup = document.read_text(encoding='utf-8')
        references = re.findall(r'!\[[^\]]*\]\(([^\s)]+)', markup)
        references += re.findall(r'<img\b[^>]*\bsrc=[\"\']([^\"\']+)', markup, flags=re.IGNORECASE)
        for reference in references:
            url = urlsplit(reference)
            if url.scheme in ('http', 'https'):
                continue
            require(not url.scheme and not url.netloc, f'Unexpected documentation image URI: {reference}')
            image = (document.parent / unquote(url.path)).resolve()
            require(image.is_relative_to(root) and image.is_file(),
                    f'Broken or escaping documentation image: {name} -> {reference}')


def inspect_archive(path, destination):
    destination = Path(destination)
    prefix = f'CodexUsage-{version()}-windows-x64'
    with zipfile.ZipFile(path) as archive:
        infos = archive.infolist()
        relative = {}
        seen = set()
        require(len(infos) <= 10000, 'Unexpectedly large release inventory')
        require(sum(i.file_size for i in infos) <= 1024 * 1024 * 1024, 'Release exceeds 1 GiB extraction limit')
        for info in infos:
            pure = PurePosixPath(info.filename)
            require(len(pure.parts) > 1 and pure.parts[0] == prefix, 'Unexpected archive root')
            check_path(info.filename)
            name = '/'.join(pure.parts[1:])
            require(name.casefold() not in seen, f'Duplicate ZIP member: {name}')
            require(stat.S_ISREG(info.external_attr >> 16), f'Non-regular ZIP member: {name}')
            require(not ((info.external_attr >> 16) & (stat.S_ISUID | stat.S_ISGID | stat.S_IWOTH)),
                    f'Unsafe ZIP permissions: {name}')
            seen.add(name.casefold())
            relative[name] = info
        require('SHA256SUMS.json' in relative, 'Missing SHA256SUMS.json')
        sums = json.loads(archive.read(relative['SHA256SUMS.json']))
        require(isinstance(sums, dict) and set(relative) == set(sums) | {'SHA256SUMS.json'}, 'ZIP inventory mismatch')
        for name, info in relative.items():
            target = destination / name
            target.parent.mkdir(parents=True, exist_ok=True)
            digest = hashlib.sha256()
            with archive.open(info) as source, target.open('xb') as output:
                for chunk in iter(lambda: source.read(1024 * 1024), b''):
                    output.write(chunk)
                    digest.update(chunk)
            if name != 'SHA256SUMS.json':
                require(digest.hexdigest() == sums[name], f'ZIP checksum mismatch: {name}')
        manifest = json.loads((destination / 'BUILD-MANIFEST.json').read_text(encoding='utf-8'))
        app_names = set(manifest['files']) | {'BUILD-MANIFEST.json'}
        allowed_extra = {'README.md', 'LICENSE', 'docs/WINDOWS.md', 'SHA256SUMS.json'}
        for path in documentation_images():
            allowed_extra.update({'images/' + path.name, 'docs/images/' + path.name})
        allowed_sources = {p.relative_to(ROOT).as_posix() for p in windows_sources()}
        allowed_sources.add('windows-ci.yml')
        require(set(relative) == app_names | allowed_extra | {'source/' + p for p in allowed_sources},
                'Archive contains files outside the app/source/documentation allowlist')
        # validate_app expects only the build payload. Validate hashes explicitly here
        # because README, source and checksums are intentionally adjacent in the ZIP.
        for name, expected in manifest['files'].items():
            require(digest_file(destination / name) == expected, f'Build manifest mismatch: {name}')
        for name in ('version', 'architecture', 'python', 'dotnet_sdk'):
            require(name in manifest, f'Missing build metadata: {name}')
        require(manifest['version'] == version() and manifest['architecture'] == 'win-x64'
                and manifest['dotnet_sdk'] == SDK_VERSION, 'Unexpected build identity')
        from scripts.fetch_windows_runtime import load_manifest, PTH
        require(manifest['python'] == load_manifest(), 'Unpinned Python runtime')
        require((destination / 'python/python314._pth').read_text(encoding='utf-8') == PTH, 'Python isolation changed')
        require(isinstance(manifest.get('sources'), dict) and manifest['sources'], 'Missing build source manifest')
        for name, expected in manifest['sources'].items():
            check_path(name)
            require(digest_file(destination / 'source' / name) == expected,
                    f'Published source does not match build inputs: {name}')
        for name in ('CodexUsage.exe', 'coreclr.dll', 'python/python.exe', 'python/python314.dll'):
            verify_pe_x64(destination / name)
        for name in manifest['files']:
            if name.startswith('backend/'):
                require(digest_file(destination / name) == digest_file(destination / 'source' / name),
                        f'Published backend differs from published source: {name}')
        for path in documentation_images():
            expected = digest_file(destination / 'source/docs/images' / path.name)
            for directory in ('images', 'docs/images'):
                require(digest_file(destination / directory / path.name) == expected,
                        f'Documentation preview differs from published source: {directory}/{path.name}')
        validate_app(destination, check_sources=False,
                     extra_files=allowed_extra | {'source/' + name for name in allowed_sources})
        verify_documentation_images(destination)
        return len(relative)


def verify_control_previews(app, workspace, env, output=None):
    """Exercise actual WPF Popup templates and require deterministic interaction assertions."""
    app, workspace = Path(app).resolve(), Path(workspace).resolve()
    directory = Path(output).resolve() if output is not None else workspace / 'control-previews'
    require(not directory.is_relative_to(app), 'Control previews must be written outside the application directory')
    require(not directory.exists(), 'Control preview output must be a new directory so stale results cannot pass')
    directory.mkdir(parents=True)
    process = subprocess.run([str(app / 'CodexUsage.exe'), '--preview-controls', '--output', str(directory)],
                             cwd=workspace, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                             timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
    report_path = directory / 'result.json'
    require(report_path.is_file() and report_path.stat().st_size <= 65536,
            'Control preview did not produce a bounded result.json')
    report = json.loads(report_path.read_text(encoding='utf-8-sig'))
    require(isinstance(report, dict), 'Control preview report must be an object')
    require(process.returncode == 0 and report.get('success') is True,
            f'Control preview failed: {str(report.get("error", process.returncode))[:8192]}')
    require(type(report.get('version')) is int and report['version'] == 1,
            'Unexpected control preview report version')
    themes = report.get('themes')
    require(isinstance(themes, list) and len(themes) == 2
            and all(isinstance(theme, str) for theme in themes) and set(themes) == {'light', 'dark'},
            'Control previews must exercise both light and dark themes')
    checks = report.get('checks')
    require(isinstance(checks, list) and all(isinstance(item, str) for item in checks)
            and CONTROL_CHECKS <= set(checks), 'Control previews omitted required Popup or keyboard assertions')
    images = report.get('images')
    require(isinstance(images, list) and 2 <= len(images) <= 64
            and all(isinstance(name, str) for name in images), 'Control preview image inventory is invalid')
    require(len({name.casefold() for name in images}) == len(images), 'Duplicate control preview image names')
    require(CONTROL_IMAGES <= set(images), 'Control previews omitted required theme or open Popup images')
    dimensions = {}
    for name in images:
        check_path(name)
        image_path = directory / name
        require(not image_path.is_symlink() and not (getattr(image_path.lstat(), 'st_file_attributes', 0) & 0x400),
                f'Linked control preview image: {name}')
        image_path = image_path.resolve()
        require(image_path.is_relative_to(directory) and image_path.suffix.lower() == '.png'
                and image_path.is_file(), f'Missing or unsafe control preview image: {name}')
        with image_path.open('rb') as image:
            header = image.read(24)
        require(len(header) == 24 and header[:8] == b'\x89PNG\r\n\x1a\n'
                and header[8:16] == b'\x00\x00\x00\rIHDR', f'Control preview is not a PNG image: {name}')
        width, height = struct.unpack('>II', header[16:24])
        require(16 <= width <= 8192 and 16 <= height <= 8192, f'Invalid control preview dimensions: {name}')
        dimensions[name] = [width, height]
    report['verified_image_dimensions'] = dimensions
    if output is not None:
        report['artifact_directory'] = str(directory)
    return report


def smoke(app, workspace, controls_output=None, background=False):
    require(os.name == 'nt', 'Offline runtime execution requires Windows')
    app, workspace = Path(app), Path(workspace)
    workspace.mkdir(parents=True)
    python = app / 'python/python.exe'
    env = dict(os.environ, PYTHONHOME=str(workspace / 'invalid-python-home'),
               PYTHONPATH=str(workspace / 'untrusted-path'),
               CODEX_HOME=str(workspace / 'invalid-codex-home'),
               HTTP_PROXY='http://127.0.0.1:1', HTTPS_PROXY='http://127.0.0.1:1')
    def run(args):
        return subprocess.run([str(python), '-E', '-s', '-B', *map(str, args)], cwd=workspace,
                              env=env, capture_output=True, text=True, encoding='utf-8', timeout=30, check=True)
    probe = json.loads(run(['-c', 'import json,sys,sqlite3; print(json.dumps(dict(version=sys.version_info[:3],'
                               'isolated=sys.flags.isolated,site="site" in sys.modules,path=sys.path)))']).stdout)
    require(probe['version'] == [3, 14, 7] and probe['isolated'] == 1 and not probe['site'], 'Python isolation/version mismatch')
    require(all(str(workspace).casefold() not in p.casefold() for p in probe['path']), 'Python inherited an untrusted search path')
    home = workspace / '合成 数据'
    logs = home / 'sessions'
    logs.mkdir(parents=True)
    def event(kind, payload):
        return dict(timestamp='2026-09-10T01:00:00Z', type=kind, payload=payload)
    usage = dict(input_tokens=100, output_tokens=20, total_tokens=120, cached_input_tokens=40,
                 reasoning_output_tokens=5, cache_write_input_tokens=0)
    events = [event('session_meta', dict(id='offline-task', source='vscode')),
              event('event_msg', dict(type='task_started', turn_id='offline-turn')),
              event('turn_context', dict(turn_id='offline-turn', root_turn_id='offline-turn')),
              event('response_item', dict(content='PRIVATE_MESSAGE_SENTINEL')),
              event('token_usage_record', dict(thread_id='offline-task', turn_id='offline-turn',
                    root_turn_id='offline-turn', response_id='response-1', usage=usage,
                    turn_token_usage=usage, thread_token_usage=usage))]
    (logs / 'one.jsonl').write_text(''.join(json.dumps(e) + '\n' for e in events), encoding='utf-8')
    cache, output, state = workspace / 'index.sqlite', workspace / 'result.json', workspace / 'worker.json'
    command = [app / 'backend/compact_snapshot.py', '--codex-home', home, '--cache-path', cache,
               '--output', output, '--parent-pid', os.getpid(), '--days', 'all', '--refresh-seconds', '0']
    for _ in range(2):
        run(command)
        data = json.loads(output.read_text(encoding='utf-8'))
        require(data['filtered']['summary']['total_tokens'] == 120, 'Offline accounting/cache reuse mismatch')
    require(b'PRIVATE_MESSAGE_SENTINEL' not in cache.read_bytes(), 'Cache retained transcript text')
    server_command = [str(python), '-E', '-s', '-B', str(app / 'backend/dashboard_server.py'),
                      '--codex-home', str(home), '--cache-path', str(cache), '--state-file', str(state),
                      '--instance-id', 'offline-verification', '--parent-pid', str(os.getpid()), '--port', '0',
                      '--refresh-seconds', '0']
    import secrets
    control_token = secrets.token_hex(32)
    server_env = dict(env, CODEX_USAGE_BACKEND_CONTROL_TOKEN=control_token)
    process = subprocess.Popen(server_command, cwd=workspace, env=server_env, stdout=subprocess.DEVNULL,
                               stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    opener = build_opener(ProxyHandler({}))
    try:
        deadline = time.monotonic() + 15
        base = None
        while time.monotonic() < deadline:
            require(process.poll() is None, 'Packaged dashboard exited during startup')
            try:
                base = json.loads(state.read_text(encoding='utf-8'))['url']
                with opener.open(base + '/health', timeout=1) as response:
                    if json.load(response)['ready']:
                        break
            except (OSError, ValueError):
                pass
            time.sleep(.05)
        else:
            raise ValueError('Packaged dashboard did not become healthy')
        with opener.open(base + '/api/usage?days=all', timeout=2) as response:
            require(json.load(response)['summary']['total_tokens'] == 120, 'Packaged HTTP summary mismatch')
        with opener.open(base + '/api/export.csv?days=all', timeout=2) as response:
            require(response.read().startswith(b'\xef\xbb\xbf'), 'CSV is missing its UTF-8 BOM')
        try:
            opener.open(Request(base + '/api/shutdown', data=b'{}'), timeout=2)
        except HTTPError as error:
            require(error.code == 403, 'Unauthenticated shutdown returned unexpected status')
            error.close()
        else:
            raise ValueError('Unauthenticated shutdown was accepted')
        headers = {'X-Codex-Instance': 'offline-verification'}
        with opener.open(Request(base + '/api/settings', data=b'{"refresh_seconds":0}', headers=headers), timeout=2) as response:
            require(json.load(response)['refresh_seconds'] == 0, 'Packaged settings update failed')
        try:
            opener.open(Request(base + '/api/shutdown', data=b'{}', headers=headers), timeout=2)
        except HTTPError as error:
            require(error.code == 403, 'Public identity must not authorize shutdown')
            error.close()
        else:
            raise ValueError('Public identity authorized shutdown')
        require(control_token not in state.read_text(encoding='utf-8'), 'State file exposed control credential')
        with opener.open(base + '/health', timeout=2) as response:
            require(control_token not in response.read().decode(), 'Health exposed control credential')
        headers['X-Codex-Control'] = control_token
        with opener.open(Request(base + '/api/shutdown', data=b'{}', headers=headers), timeout=2) as response:
            require(json.load(response)['stopping'], 'Packaged shutdown failed')
        require(process.wait(timeout=5) == 0, 'Packaged dashboard did not exit cleanly')
        require(not state.exists(), 'Packaged dashboard did not remove its state file')
    finally:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=5)
        process.stderr.close()
    require(not list(app.rglob('__pycache__')), 'Packaged Python wrote bytecode inside the application')
    native_reports = {}
    native_env = dict(env, CODEX_USAGE_DESKTOP_BASE=str(workspace / 'native-private-state'))
    if background:
        native_env['CODEX_USAGE_TEST_BACKGROUND'] = '1'
    for flag in ('--check-runtime', '--self-test', '--monitor-tests', '--monitor-ui-tests', '--monitor-floating-tests', '--reliability-tests', '--layout-tests', '--worker-tests', '--hover-tests', '--dock-tests', '--morph-tests', '--tray-hover-tests', '--tray-interaction-tests', '--capsule-keyboard-tests', '--capsule-drag-tests', '--capsule-drop-tests', '--capsule-drop-render-tests', '--capsule-departure-tests'):
        if background and flag in ('--capsule-drag-tests', '--capsule-drop-tests', '--capsule-departure-tests'):
            native_reports[flag[2:]] = {'skipped': 'Requires a visible window or mouse capture; run without --background for full input acceptance.'}
            continue
        report_path = workspace / (flag[2:] + '.json')
        result = subprocess.run([str(app / 'CodexUsage.exe'), flag, '--output', str(report_path)],
                                cwd=workspace, env=native_env, stdout=subprocess.DEVNULL,
                                stderr=subprocess.PIPE, timeout=60, creationflags=subprocess.CREATE_NO_WINDOW)
        require(report_path.is_file(), f'Native {flag} did not publish its report')
        native_reports[flag[2:]] = json.loads(report_path.read_text(encoding='utf-8-sig'))
        failure = native_reports[flag[2:]].get('error', '')
        require(result.returncode == 0, f'Native {flag} failed with exit {result.returncode}: {failure}')
        if flag != '--check-runtime':
            require(native_reports[flag[2:]].get('success') is True, f'Native {flag} reported a failure')
    native_input, native_output = workspace / 'native-input.json', workspace / 'native-output.json'
    start = datetime.fromisoformat('2026-09-10T00:00:00+00:00').timestamp()
    native_input.write_text(json.dumps(dict(filters={'days': 'all'}, choices=True, now=start + 43200,
        requests=[dict(id='offline-budget', revision=1, periodID='2026-09-10', start=start,
                       end=start + 86400, model='all', task='all')])), encoding='utf-8')
    result = subprocess.run([str(app / 'CodexUsage.exe'), '--native-read', '--cache', str(cache),
                             '--input', str(native_input), '--output', str(native_output)],
                            cwd=workspace, env=native_env, stdout=subprocess.DEVNULL,
                            stderr=subprocess.PIPE, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
    require(result.returncode == 0, 'Packaged native SQLite reader failed')
    native_data = json.loads(native_output.read_text(encoding='utf-8-sig'))
    require(native_data['filtered']['summary']['total_tokens'] == 120, 'Native SQLite summary differs from Python')
    require(native_data['budgetResult']['results'][0]['rows'][0]['total_tokens'] == 120,
            'Native budget query differs from Python')
    native_reports['native-index'] = dict(summary_tokens=120, budget_tokens=120, choices='verified')
    native_reports['preview-controls'] = ({'skipped': 'Background validation does not open or focus native Popup menus; run without --background for full input acceptance.'}
        if background else verify_control_previews(app, workspace, native_env, controls_output))
    return dict(python_version='3.14.7', isolation='verified', accounting='120 tokens',
                cache_reuse='verified', transcript_privacy='verified', http_api='verified',
                csv_export='verified', shutdown_auth='verified', graceful_shutdown='verified',
                native=native_reports)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--archive', type=Path, default=ROOT / 'dist' / f'codex-usage-desktop-v{version()}-windows-x64.zip')
    parser.add_argument('--app', type=Path, help='Verify a build directory instead of a ZIP')
    parser.add_argument('--skip-smoke', action='store_true', help='Inspect only; do not execute Windows runtime')
    parser.add_argument('--background', action='store_true', help='Avoid activation and visible menu tests; reports skipped foreground-only checks')
    parser.add_argument('--report', type=Path)
    parser.add_argument('--controls-output', type=Path,
                        help='Retain real WPF Popup previews in this new directory (default: temporary verification workspace)')
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='codex Windows 离线 验证 ') as temporary:
        temporary = Path(temporary)
        if args.app:
            manifest, files = validate_app(args.app)
            app = args.app.resolve()
            report = dict(app=str(app), version=manifest['version'], files=len(files), checksums='verified')
        else:
            digest = digest_file(args.archive)
            require(args.archive.with_suffix('.zip.sha256').read_text(encoding='utf-8').split() == [digest, args.archive.name],
                    'Archive SHA-256 sidecar mismatch')
            app = temporary / '解压 应用'
            count = inspect_archive(args.archive, app)
            report = dict(archive=str(args.archive), bytes=args.archive.stat().st_size, sha256=digest,
                          files=count, checksums='verified', source_boundary='verified')
        if not args.skip_smoke:
            report['offline_smoke'] = smoke(app, temporary / '隔离 场景', args.controls_output, args.background)
    payload = json.dumps(report, ensure_ascii=False, indent=2) + '\n'
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(payload, encoding='utf-8')
    print(json.dumps(report, ensure_ascii=True, indent=2))


if __name__ == '__main__':
    main()
