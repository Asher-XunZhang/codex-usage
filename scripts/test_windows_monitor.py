#!/usr/bin/env python3
"""Exercise the packaged monitoring host through IPC and synthetic lifecycle logs."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time


def run(application):
    application = Path(application).resolve()
    checks = []
    startup = subprocess.STARTUPINFO()
    startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
    startup.wShowWindow = 0
    with tempfile.TemporaryDirectory(prefix='CodexUsage-MonitorAcceptance-') as temporary:
        root = Path(temporary)
        home, desktop = root / 'home', root / 'desktop'
        (home / 'sessions').mkdir(parents=True)
        desktop.mkdir()
        (desktop / 'settings.json').write_text(json.dumps({'mode': 'tray', 'refresh': 0,
            'floating': {'days': '30', 'model': 'unrelated-model', 'task': 'unrelated-task'}}), encoding='utf-8')
        env = dict(os.environ, CODEX_HOME=str(home), CODEX_USAGE_DESKTOP_BASE=str(desktop), CODEX_USAGE_TEST_BACKGROUND='1')
        sequence = 0
        host = None

        def check(value, label):
            if not value:
                raise AssertionError(label)
            checks.append(label)

        def append(task, kind, turn):
            now = time.time()
            payload = {'id': task, 'cwd': 'D:/Synthetic Monitor Project'} if kind == 'session_meta' else {
                'type': kind, 'turn_id': turn, 'started_at' if kind == 'task_started' else 'completed_at': now,
                'reason': 'interrupted'}
            with (home / 'sessions' / f'rollout-{task}.jsonl').open('a', encoding='utf-8') as stream:
                stream.write(json.dumps({'timestamp': now, 'type': kind if kind == 'session_meta' else 'event_msg', 'payload': payload}) + '\n')

        def send(payload):
            nonlocal sequence
            sequence += 1
            request, response = root / f'input-{sequence}.json', root / f'output-{sequence}.json'
            request.write_text(json.dumps(payload), encoding='utf-8')
            result = subprocess.run([str(application), '--send', '--input', str(request), '--output', str(response)],
                env=env, startupinfo=startup, creationflags=subprocess.CREATE_NO_WINDOW, capture_output=True, timeout=20)
            data = json.loads(response.read_text(encoding='utf-8-sig')) if response.exists() else {}
            if result.returncode or data.get('error'):
                raise RuntimeError(f'{payload.get("action")}: {data or result.stderr!r}')
            return data

        def operation(name, payload=None):
            return send({'action': 'monitor', 'operation': name, 'payload': payload or {}})

        def state():
            return send({'action': 'state'})

        def until(predicate, label):
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                if predicate():
                    checks.append(label)
                    return
                time.sleep(.15)
            raise AssertionError(label)

        def launch():
            nonlocal host
            host = subprocess.Popen([str(application), '--tray', '--no-quota'], env=env,
                startupinfo=startup, creationflags=subprocess.CREATE_NO_WINDOW, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            state()

        try:
            for task in ('monitor-a', 'monitor-b'):
                append(task, 'session_meta', '')
                append(task, 'task_started', 'turn-1')
            launch()
            snapshot = operation('check')
            check(len(snapshot['monitor']['tasks']) == 2, 'zero-token tasks discovered by packaged host')
            until(lambda: not state()['busy'], 'initial usage snapshot settles before filter comparison')
            original_filter = state()['settings']['floating']
            for task, mode in (('monitor-a', 'once'), ('monitor-b', 'each')):
                snapshot = operation('add', {'selections': [{'id': task, 'turnID': 'turn-1'}], 'mode': mode})
                check(snapshot['monitorResult']['ok'], f'{mode} monitoring can be added through IPC')
            check(snapshot['settings']['floating'] == original_filter, 'monitor selection preserves usage filters')
            snapshot = operation('settings', {'patch': {'pausedUntil': -1}})
            check(snapshot['monitor']['settings']['pausedUntil'] == -1, 'notification settings persist through the real host')
            append('monitor-a', 'task_complete', 'turn-1')
            until(lambda: len(state()['monitor']['messages']) == 1, 'lifecycle polling continues while usage auto-refresh is paused')
            snapshot = state()
            message = snapshot['monitor']['messages'][0]
            check(message['delivery'] == 'suppressed' and not message['read'], 'pause records unread completion without a toast')
            snapshot = send({'action': 'monitor-notification', 'operation': 'read', 'ids': [message['id']]})
            check(snapshot['mainPID'] == 0 and snapshot['monitor']['messages'][0]['read'], 'notification read action does not open the main panel')
            check(any(x['id'] == 'monitor-a' and x['status'] == 'completed' for x in snapshot['monitor']['watches']), 'reading does not erase completion status')
            operation('settings', {'patch': {'pausedUntil': 0}})
            check(state()['monitor']['messages'][0]['delivery'] == 'suppressed', 'resuming does not replay an old message')
            operation('stop', {'id': 'monitor-b'})
            append('monitor-b', 'task_complete', 'turn-1')
            operation('check')
            check(len(state()['monitor']['messages']) == 1, 'stopping before completion produces no late reminder')
            append('monitor-b', 'task_started', 'turn-2')
            operation('check')
            operation('add', {'selections': [{'id': 'monitor-b', 'turnID': 'turn-2'}], 'mode': 'each'})
            append('monitor-b', 'turn_aborted', 'turn-2')
            until(lambda: len(state()['monitor']['messages']) == 2, 'explicit interruption is reported independently of completion')
            snapshot = state()
            check(not snapshot['monitor']['capabilities']['waiting'] and not snapshot['monitor']['capabilities']['failure'], 'unsupported states remain unavailable')
            send({'action': 'quit'})
            check(host.wait(timeout=10) == 0, 'host exits cleanly with monitoring active')
            append('monitor-b', 'task_started', 'turn-3')
            append('monitor-b', 'task_complete', 'turn-3')
            launch()
            snapshot = operation('check')
            check(len(snapshot['monitor']['messages']) == 3, 'restart catches up one offline turn without losing history')
            offline = next(x for x in snapshot['monitor']['messages'] if x['turnID'] == 'turn-3')
            check(offline['offline'] and offline['delivery'] == 'suppressed', 'offline catch-up stays in application history')
            for _ in range(3):
                operation('check')
            check(len(state()['monitor']['messages']) == 3, 'repeated checks and restart preserve message deduplication')
        finally:
            if host is not None and host.poll() is None:
                try:
                    send({'action': 'quit'})
                    host.wait(timeout=10)
                finally:
                    if host.poll() is None:
                        host.kill()
                        host.wait(timeout=5)
    return {'success': True, 'checks': checks}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = run(args.app)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result, ensure_ascii=True))
