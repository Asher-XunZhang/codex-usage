#!/usr/bin/env python3
"""Sample an isolated Windows host and panel without querying an account or real logs."""
import argparse
import ctypes
from ctypes import wintypes
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

from tools.windows.test_lifecycle import descendants, processes


def process_sample(pid):
    class Memory(ctypes.Structure):
        _fields_ = [('size', wintypes.DWORD), ('faults', wintypes.DWORD)] + [
            (name, ctypes.c_size_t) for name in ('peak_working_set', 'working_set', 'peak_paged_pool',
                                               'paged_pool', 'peak_nonpaged_pool', 'nonpaged_pool',
                                               'pagefile', 'peak_pagefile', 'private_bytes')]
    api = ctypes.WinDLL('kernel32', use_last_error=True)
    api.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    api.OpenProcess.restype = wintypes.HANDLE
    api.CloseHandle.argtypes = [wintypes.HANDLE]
    api.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    api.K32GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Memory), wintypes.DWORD]
    api.GetProcessHandleCount.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    handle = api.OpenProcess(0x0410, False, pid)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        memory = Memory(); memory.size = ctypes.sizeof(memory)
        created, exited, kernel, user = [wintypes.FILETIME() for _ in range(4)]
        if not api.K32GetProcessMemoryInfo(handle, ctypes.byref(memory), memory.size):
            raise ctypes.WinError(ctypes.get_last_error())
        if not api.GetProcessTimes(handle, ctypes.byref(created), ctypes.byref(exited), ctypes.byref(kernel), ctypes.byref(user)):
            raise ctypes.WinError(ctypes.get_last_error())
        handles = wintypes.DWORD()
        if not api.GetProcessHandleCount(handle, ctypes.byref(handles)):
            raise ctypes.WinError(ctypes.get_last_error())
        def seconds(value):
            return ((value.dwHighDateTime << 32) + value.dwLowDateTime) / 10_000_000
        return {'pid': pid, 'working_set_bytes': memory.working_set, 'private_bytes': memory.private_bytes,
                'cpu_seconds': seconds(kernel) + seconds(user), 'handles': handles.value}
    finally:
        api.CloseHandle(handle)


def summarize(rows, duration):
    def memory(key):
        values = [row[key] for row in rows]
        return {'start_mib': round(values[0] / 1048576, 3), 'end_mib': round(values[-1] / 1048576, 3),
                'min_mib': round(min(values) / 1048576, 3), 'max_mib': round(max(values) / 1048576, 3),
                'change_mib': round((values[-1] - values[0]) / 1048576, 3)}
    cpu = rows[-1]['cpu_seconds'] - rows[0]['cpu_seconds']
    return {'working_set': memory('working_set_bytes'), 'private_bytes': memory('private_bytes'),
            'cpu_seconds_delta': round(cpu, 6), 'cpu_percent_of_one_core': round(cpu / duration * 100, 3),
            'handles_start': rows[0]['handles'], 'handles_end': rows[-1]['handles']}


def measure(host_pid, duration, application):
    samples = []
    started = time.monotonic()
    for index in range(duration + 1):
        delay = started + index - time.monotonic()
        if delay > 0:
            time.sleep(delay)
        owned = {host_pid: application.name, **descendants(host_pid)}
        values = []
        for pid, name in sorted(owned.items()):
            entry = process_sample(pid)
            entry['role'] = 'host' if pid == host_pid else 'panel' if name.lower() == application.name.lower() else 'collector' if name.lower().startswith('python') else name
            values.append(entry)
        samples.append({'elapsed_seconds': round(time.monotonic() - started, 6),
                        'child_process_count': len(owned) - 1, 'processes': values})
    elapsed = samples[-1]['elapsed_seconds'] - samples[0]['elapsed_seconds']
    all_pids = sorted({row['pid'] for sample in samples for row in sample['processes']})
    per_process = []
    for pid in all_pids:
        rows = [row for sample in samples for row in sample['processes'] if row['pid'] == pid]
        per_process.append({'pid': pid, 'role': rows[0]['role'], 'samples_present': len(rows), **summarize(rows, elapsed)})
    totals = [{key: sum(row[key] for row in sample['processes'])
               for key in ('working_set_bytes', 'private_bytes', 'cpu_seconds', 'handles')} for sample in samples]
    return {'duration_seconds': round(elapsed, 3), 'sample_interval_seconds': 1, 'sample_count': len(samples),
            'child_process_count_start': samples[0]['child_process_count'],
            'child_process_count_end': samples[-1]['child_process_count'],
            'stable_process_set': all(row['samples_present'] == len(samples) for row in per_process),
            'processes': per_process, 'aggregate': summarize(totals, elapsed), 'samples': samples}


def run(application, duration=15):
    if os.name != 'nt':
        raise RuntimeError('This probe requires Windows')
    application = Path(application).resolve()
    if not application.is_file():
        raise FileNotFoundError(application)
    startup = subprocess.STARTUPINFO(); startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW; startup.wShowWindow = 0
    manifest = json.loads((application.parent / 'BUILD-MANIFEST.json').read_text(encoding='utf-8'))
    with tempfile.TemporaryDirectory(prefix='CodexUsage-ResourceTests-') as temporary:
        root = Path(temporary); home = root / 'home'; desktop = root / 'desktop'
        (home / 'sessions').mkdir(parents=True); desktop.mkdir()
        settings = {'mode': 'tray', 'refresh': 0, 'filterDays': '30', 'filterModel': 'all', 'filterTask': 'all',
                    'floating': {'days': '1', 'model': 'all', 'task': 'all'}}
        (desktop / 'settings.json').write_text(json.dumps(settings), encoding='utf-8')
        env = dict(os.environ, CODEX_HOME=str(home), CODEX_USAGE_DESKTOP_BASE=str(desktop))
        pipe = 'CodexUsage-' + hashlib.sha256(str(desktop.resolve()).rstrip('\\').upper().encode()).hexdigest()[:24]
        now = datetime.now(timezone.utc)
        for index in range(100):
            thread = f'synthetic-{index:03}'; stamp = (now - timedelta(days=index % 14)).isoformat()
            def event(kind, payload):
                return {'timestamp': stamp, 'type': kind, 'payload': payload}
            rows = [event('session_meta', {'id': thread, 'source': 'vscode'}),
                    event('event_msg', {'type': 'task_started', 'turn_id': 'turn'}),
                    event('turn_context', {'turn_id': 'turn', 'root_turn_id': 'turn', 'model': f'model-{index % 4}'})]
            for number in range(1, 4):
                def counts(multiplier):
                    return {'input_tokens': 2000 * multiplier, 'output_tokens': 400 * multiplier,
                            'total_tokens': 2400 * multiplier, 'cached_input_tokens': 1400 * multiplier,
                            'reasoning_output_tokens': 100 * multiplier, 'cache_write_input_tokens': 0}
                rows.append(event('token_usage_record', {'thread_id': thread, 'turn_id': 'turn', 'root_turn_id': 'turn',
                            'response_id': f'response-{number}', 'usage': counts(1),
                            'turn_token_usage': counts(number), 'thread_token_usage': counts(number)}))
            (home / 'sessions' / f'{thread}.jsonl').write_text(''.join(json.dumps(row) + '\n' for row in rows), encoding='utf-8')
        sequence = 0
        def send(payload, target=None):
            nonlocal sequence
            sequence += 1; request, response = root / f'in-{sequence}.json', root / f'out-{sequence}.json'
            request.write_text(json.dumps(payload), encoding='utf-8')
            args = [str(application), '--send', '--input', str(request), '--output', str(response)]
            if target:
                args += ['--pipe', target]
            result = subprocess.run(args, env=env, startupinfo=startup, creationflags=subprocess.CREATE_NO_WINDOW,
                                    capture_output=True, timeout=20)
            value = json.loads(response.read_text(encoding='utf-8')) if response.exists() else {}
            if result.returncode or value.get('error'):
                raise RuntimeError(f'IPC {payload["action"]}: {value}')
            return value
        def until(predicate, description, timeout=25):
            end = time.monotonic() + timeout
            while time.monotonic() < end:
                try:
                    if value := predicate():
                        return value
                except (RuntimeError, ValueError, OSError):
                    pass
                time.sleep(.15)
            raise RuntimeError(description)
        host = subprocess.Popen([str(application), '--tray', '--no-quota'], env=env, startupinfo=startup,
                                creationflags=subprocess.CREATE_NO_WINDOW, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            until(lambda: not (value := send({'action': 'state'})).get('busy') and value.get('today', {}).get('summary', {}).get('total_tokens') is not None, 'Host did not finish its initial scan')
            send({'action': 'main'})
            until(lambda: send({'action': 'inspect'}, pipe + '-main').get('summary', {}).get('total_tokens') == 720000, 'Panel did not load the synthetic logs')
            send({'action': 'close'}, pipe + '-main')
            until(lambda: send({'action': 'state'}).get('mainPID') == 0 and not descendants(host.pid), 'Panel or collector did not exit')
            time.sleep(2)
            closed_panel = measure(host.pid, duration, application)
            send({'action': 'main'})
            until(lambda: send({'action': 'inspect'}, pipe + '-main').get('summary', {}).get('total_tokens') == 720000, 'Panel did not reopen')
            time.sleep(2)
            open_panel = measure(host.pid, duration, application)
            owned = descendants(host.pid)
            send({'action': 'quit'}); host.wait(timeout=10)
            until(lambda: not set(owned).intersection(processes()), 'Owned processes survived graceful quit')
            return {'success': True, 'conditions': {
                'captured_at_utc': datetime.now(timezone.utc).isoformat(), 'app_version': manifest['version'],
                'application': str(application), 'logical_processors': os.cpu_count(),
                'fixture': '100 synthetic session logs, 300 usage records, 4 models, 14 days, 720000 tokens',
                'fixture_bytes': sum(path.stat().st_size for path in (home / 'sessions').iterdir()),
                'quota_queries': False, 'refresh_seconds': 0, 'mode': 'tray', 'floating_window': 'hidden',
                'warmup': f'Initial indexing, open and close panel; wait two seconds before each {duration}-second sample',
                'sampling': 'Win32 process working set/private bytes and kernel+user CPU; one sample per second',
                'cpu_normalization': '100% means one fully occupied logical CPU core',
                'performance_thresholds': 'None; measurements describe this machine and fixture only',
                'scope': 'No real user logs or account data; no frame-presentation tracing',
                'sources': {key: value for key, value in manifest['sources'].items()
                            if key in {'src/windows/App/Host.cs', 'src/windows/Features/Usage/MainWindow.cs',
                                       'src/windows/Infrastructure/NativeIndex.cs'}}},
                'panel_closed': closed_panel, 'panel_open': open_panel, 'cleanup': 'Host and all observed owned processes exited'}
        finally:
            if host.poll() is None:
                host.kill(); host.wait(timeout=10)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=15)
    args = parser.parse_args()
    if not 5 <= args.seconds <= 60:
        parser.error('--seconds must be between 5 and 60')
    try:
        result = run(args.app, args.seconds)
    except Exception as exc:
        result = {'success': False, 'error': f'{type(exc).__name__}: {exc}'}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: value for key, value in result.items() if key not in {'panel_closed', 'panel_open'}}, ensure_ascii=False, indent=2))
    for key in ('panel_closed', 'panel_open'):
        if key in result:
            print(key, json.dumps({k: v for k, v in result[key].items() if k != 'samples'}, ensure_ascii=False))
    raise SystemExit(0 if result['success'] else 1)


if __name__ == '__main__':
    main()
