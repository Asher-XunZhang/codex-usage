#!/usr/bin/env python3
"""Exercise a packaged Windows host using only an isolated synthetic Codex home."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import ctypes
from ctypes import wintypes
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time


def processes():
    class Entry(ctypes.Structure):
        _fields_ = [('size', wintypes.DWORD), ('usage', wintypes.DWORD), ('pid', wintypes.DWORD),
                    ('heap', ctypes.c_size_t), ('module', wintypes.DWORD), ('threads', wintypes.DWORD),
                    ('parent', wintypes.DWORD), ('priority', wintypes.LONG), ('flags', wintypes.DWORD),
                    ('name', wintypes.WCHAR * 260)]
    api = ctypes.WinDLL('kernel32', use_last_error=True)
    api.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    api.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    api.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
    api.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Entry)]
    api.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = api.CreateToolhelp32Snapshot(2, 0)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    result = {}
    try:
        entry = Entry(); entry.size = ctypes.sizeof(entry)
        more = api.Process32FirstW(handle, ctypes.byref(entry))
        while more:
            result[entry.pid] = (entry.parent, entry.name)
            more = api.Process32NextW(handle, ctypes.byref(entry))
    finally:
        api.CloseHandle(handle)
    return result


def descendants(root):
    snapshot = processes(); owned = {root}
    while True:
        previous = len(owned)
        owned.update(pid for pid, (parent, _) in snapshot.items() if parent in owned)
        if len(owned) == previous:
            return {pid: snapshot[pid][1] for pid in owned if pid != root and pid in snapshot}


def run(application, background=False):
    if os.name != 'nt':
        raise RuntimeError('Windows is required for native lifecycle checks')
    application = Path(application).resolve()
    if not application.is_file():
        raise FileNotFoundError(application)
    checks = []
    def check(condition, name):
        if not condition:
            raise AssertionError(name)
        checks.append(name)
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    with tempfile.TemporaryDirectory(prefix='CodexUsage-HostTests-') as temporary:
        root = Path(temporary); home = root / 'home'; desktop = root / 'desktop'
        (home / 'sessions').mkdir(parents=True); desktop.mkdir()
        settings = {'mode': 'tray', 'refresh': 0, 'filterDays': '30', 'filterModel': 'test-model',
                    'filterTask': 'root', 'filterGroup': 'model',
                    'floating': {'days': '1', 'model': 'all', 'task': 'all', 'theme': 'dark'}}
        (desktop / 'settings.json').write_text(json.dumps(settings), encoding='utf-8')
        env = dict(os.environ, CODEX_HOME=str(home), CODEX_USAGE_DESKTOP_BASE=str(desktop))
        if background:
            env['CODEX_USAGE_TEST_BACKGROUND'] = '1'
        pipe = 'CodexUsage-' + hashlib.sha256(str(desktop.resolve()).rstrip('\\').upper().encode()).hexdigest()[:24]
        number = 0
        message_lock = threading.Lock()
        host = None
        last_inspection = None
        def invoke(args, timeout=20):
            return subprocess.run([str(application), *args], env=env, startupinfo=startup,
                                  creationflags=subprocess.CREATE_NO_WINDOW, capture_output=True, timeout=timeout)
        def send(payload, target=None):
            nonlocal number, last_inspection
            with message_lock:
                number += 1
                request, response = root / f'request-{number}.json', root / f'response-{number}.json'
            request.write_text(json.dumps(payload), encoding='utf-8')
            args = ['--send', '--input', str(request), '--output', str(response)]
            if target:
                args += ['--pipe', target]
            result = invoke(args)
            data = json.loads(response.read_text(encoding='utf-8')) if response.exists() else {}
            if payload.get('action') == 'inspect':
                last_inspection = data
            if result.returncode or data.get('error'):
                raise RuntimeError(f'IPC {payload.get("action")}: {data or result.stderr.decode(errors="replace")}')
            return data
        def until(predicate, description, timeout=20):
            end = time.monotonic() + timeout; last_error = None
            while time.monotonic() < end:
                try:
                    value = predicate()
                    if value:
                        return value
                except (RuntimeError, OSError, ValueError) as exc:
                    last_error = exc
                time.sleep(.15)
            raise AssertionError(f'{description}; last error: {last_error}; last panel: {last_inspection}')
        def state():
            return send({'action': 'state'})
        def count(snapshot):
            return snapshot.get('filtered', {}).get('summary', {}).get('total_tokens')
        def with_worker():
            owned = descendants(host.pid)
            return owned if any(name.lower().startswith('python') for name in owned.values()) else None
        def boot():
            return subprocess.Popen([str(application), '--tray', '--no-quota'], env=env, startupinfo=startup,
                                    creationflags=subprocess.CREATE_NO_WINDOW, stdout=subprocess.DEVNULL,
                                    stderr=subprocess.DEVNULL)
        def event(kind, payload):
            return {'timestamp': datetime.now(timezone.utc).isoformat(), 'type': kind, 'payload': payload}
        def counts(multiplier=1):
            return dict(input_tokens=100 * multiplier, output_tokens=20 * multiplier, total_tokens=120 * multiplier,
                        cached_input_tokens=40 * multiplier, reasoning_output_tokens=5 * multiplier, cache_write_input_tokens=0)
        def record(thread, number):
            return event('token_usage_record', dict(thread_id=thread, turn_id='t1', root_turn_id='t1',
                         response_id=f'r{number}', usage=counts(), turn_token_usage=counts(number), thread_token_usage=counts(number)))
        for thread, model in [('root', 'test-model'), ('other', 'other-model')]:
            rows = [event('session_meta', dict(id=thread, source='vscode')),
                    event('event_msg', dict(type='task_started', turn_id='t1')),
                    event('turn_context', dict(turn_id='t1', root_turn_id='t1', model=model)), record(thread, 1)]
            (home / 'sessions' / f'{thread}.jsonl').write_text(''.join(json.dumps(x) + '\n' for x in rows), encoding='utf-8')
        def append(number):
            with (home / 'sessions' / 'root.jsonl').open('a', encoding='utf-8') as stream:
                stream.write(json.dumps(record('root', number)) + '\n')
        try:
            host = boot()
            initial = until(lambda: (value if not value.get('busy') and count(value) == 240 else None)
                            if (value := state()) else None, 'initial host scan')
            check(initial['mainPID'] == 0 and host.poll() is None, 'tray host starts with only its isolated data')
            until(lambda: not any(name.lower().startswith('python') for name in descendants(host.pid).values()), 'idle scanner release')
            check(True, 'idle host releases Python when the main panel is closed')
            duplicate = invoke(['--tray', '--no-quota'])
            check(duplicate.returncode == 0 and host.poll() is None, 'second launch reuses the existing host')
            opened = until(lambda: value if (value := state()).get('mainPID', 0) > 0 else None, 'main panel open')
            main_pid = opened['mainPID']
            inspected = until(lambda: value if (value := send({'action': 'inspect'}, pipe + '-main')).get('summary', {}).get('total_tokens') == 120 else None, 'main filtered snapshot')
            check(inspected['filters'] == {'days': '30', 'model': 'test-model', 'task': 'root', 'group': 'model'}
                  and count(state()) == 240, 'main and floating filters are independent')
            send({'action': 'floating-settings', 'patch': {'edgeMetric': 'used'}})
            until(lambda: send({'action': 'inspect'}, pipe + '-main').get('floatingEdgeMetric') == 'used', 'host state push')
            check(True, 'host pushes floating-only settings to the main panel without its polling timer')
            for mode in ('both', 'float', 'tray'):
                switched = send({'action': 'mode', 'value': mode})
                check(switched['mainPID'] == main_pid and switched['settings']['mode'] == mode,
                      f'{mode} residency preserves the existing main process')
            opened_settings = send({'action': 'settings-dialog', 'page': 'appearance'})
            check(opened_settings['mainPID'] == main_pid and opened_settings['settings']['filterModel'] == 'test-model'
                  and send({'action': 'inspect'}, pipe + '-main')['filters'] == inspected['filters'],
                  'opening shared settings returns complete state and preserves current main filters')
            send({'action': 'budget', 'operation': 'save', 'payload': {'id': 'layout-budget', 'name': 'Layout fixture',
                  'kind': 'token', 'amount': 10000000, 'tokenMetric': 'total', 'model': 'all', 'task': 'all',
                  'period': {'type': 'day', 'timezone': 'UTC'}, 'enabled': False, 'thresholds': [20, 10, 0]}})
            viewed = send({'action': 'view-budget', 'budgetID': 'layout-budget'})
            check(viewed['mainPID'] == main_pid and viewed['settings']['mode'] == 'both'
                  and viewed['settings']['floating']['content'] == 'budget'
                  and viewed['settings']['floating']['budgetID'] == 'layout-budget',
                  'view budget reveals its floating surface without closing the main panel')
            hidden = send({'action': 'hide-float'})
            check(hidden['mainPID'] == main_pid and hidden['settings']['mode'] == 'tray',
                  'hide floating returns to an available tray while preserving the main panel')
            send({'action': 'floating-settings', 'patch': {'content': 'usage'}})
            workers = until(with_worker, 'main service start')
            check(sum(name.lower() == application.name.lower() for name in workers.values()) == 1,
                  'single host owns exactly one main process')
            append(2); time.sleep(1.3)
            check(count(state()) == 240 and send({'action': 'inspect'}, pipe + '-main')['summary']['total_tokens'] == 120,
                  'zero refresh interval does not scan changed logs')
            send({'action': 'refresh'})
            until(lambda: value if not (value := state()).get('busy') and count(value) == 360 else None, 'manual refresh completion')
            until(lambda: send({'action': 'inspect'}, pipe + '-main')['summary'].get('total_tokens') == 240, 'main refresh completion')
            check(True, 'manual refresh updates the panel and host after scanning completes')
            send({'action': 'close'}, pipe + '-main')
            until(lambda: state().get('mainPID') == 0 and main_pid not in processes(), 'main close')
            until(lambda: not any(name.lower().startswith('python') for name in descendants(host.pid).values()), 'panel worker release')
            check(True, 'closing main releases the panel and its Python worker')
            saved = json.loads((desktop / 'settings.json').read_text(encoding='utf-8'))
            check(saved['filterModel'] == 'test-model' and saved['floating']['model'] == 'all'
                  and saved.get('mainWindow', {}).get('width', 0) >= 760, 'filters and window geometry persist')
            send({'action': 'main'})
            reopened = until(lambda: value if (value := send({'action': 'inspect'}, pipe + '-main')).get('summary', {}).get('total_tokens') == 240 else None, 'main reopen')
            check(reopened['filters'] == inspected['filters'], 'reopening restores the main selection')
            with ThreadPoolExecutor(max_workers=3) as executor:
                transitions = [executor.submit(send, request) for request in [
                    {'action': 'main'}, {'action': 'mode', 'value': 'tray'}, {'action': 'main'}]]
                for transition in transitions:
                    transition.result(timeout=25)
            send({'action': 'main'})
            until(lambda: send({'action': 'inspect'}, pipe + '-main').get('summary', {}).get('total_tokens') == 240,
                  'main settles after interleaved open/close')
            check(sum(name.lower() == application.name.lower() for name in descendants(host.pid).values()) == 1,
                  'interleaved open and close requests settle on one usable main process')
            owned = until(with_worker, 'reopened service')
            host.kill(); host.wait(timeout=10)
            until(lambda: not set(owned).intersection(processes()), 'job cleanup after host termination')
            check(True, 'terminating the host leaves no owned main or Python processes')
            host = boot()
            restored = until(lambda: value if not (value := state()).get('busy') and count(value) == 360 else None, 'host restart')
            check(restored['settings']['refresh'] == 0 and restored['settings']['filterTask'] == 'root', 'settings survive host restart')
            append(3); time.sleep(1.3)
            check(count(state()) == 360, 'idle host also honors disabled automatic scanning')
            send({'action': 'refresh'})
            until(lambda: value if not (value := state()).get('busy') and count(value) == 480 else None, 'headless manual refresh')
            check(True, 'manual refresh works with the main panel closed')
            owned = descendants(host.pid)
            send({'action': 'quit'}); host.wait(timeout=10)
            until(lambda: not set(owned).intersection(processes()), 'graceful host cleanup')
            check(host.returncode == 0, 'graceful quit releases the host and owned processes')
        except Exception as exc:
            exc.passed_checks = list(checks)
            raise
        finally:
            if host is not None and host.poll() is None:
                host.kill(); host.wait(timeout=10)
    return {'success': True, 'checks': checks, 'background': background}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--background', action='store_true', help='Keep synthetic windows invisible and nonactivating')
    args = parser.parse_args()
    try:
        result = run(args.app, args.background)
    except Exception as exc:
        result = {'success': False, 'error': f'{type(exc).__name__}: {exc}', 'checks': getattr(exc, 'passed_checks', [])}
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False, indent=2))
    raise SystemExit(0 if result['success'] else 1)


if __name__ == '__main__':
    main()
