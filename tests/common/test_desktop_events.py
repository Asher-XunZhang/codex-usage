"""Opt-in native event transport, with isolated server processes and bounded IO."""
from tools.common.paths import BACKEND
import importlib.util
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from urllib.request import Request, build_opener, ProxyHandler

spec = importlib.util.spec_from_file_location('desktop_events_fixture', BACKEND / 'desktop_events.py')
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)


class DesktopEventWriterTests(unittest.TestCase):
    def test_slow_reader_retains_only_latest_state(self):
        read, write = os.pipe()
        writer = module.DesktopEvents(write, 'fixture')
        try:
            began = time.monotonic()
            for ticket in range(20000):
                writer.publish(dict(refresh_completed=ticket, refresh_error='x' * 512))
            self.assertLess(time.monotonic() - began, 3)
            with writer.condition:
                self.assertLessEqual(len(writer.pending), 1)
                self.assertLessEqual(sum(map(len, writer.pending.values())), 4096)
        finally:
            os.close(read); writer.close(); writer.thread.join(2); os.close(write)
        self.assertFalse(writer.thread.is_alive())

    def test_wire_omits_unrelated_data_and_bounds_errors(self):
        read, write = os.pipe(); writer = module.DesktopEvents(write, 'fixture')
        try:
            writer.publish(dict(url='http://127.0.0.1:12345', private='DO-NOT-PUBLISH'), ready=True)
            data = os.read(read, 4096); row = json.loads(data)
            self.assertEqual(row['event'], 'desktop_ready')
            self.assertEqual(row['protocol'], 1)
            self.assertNotIn('private', row)
            self.assertEqual(row['instance_id'], 'fixture')
        finally:
            writer.close(); writer.thread.join(2); os.close(read); os.close(write)


@unittest.skipUnless(sys.platform in ('darwin', 'win32'), 'Native event mode requires macOS or Windows')
class DesktopEventServerTests(unittest.TestCase):
    def test_handshake_scan_and_manual_receipt(self):
        with tempfile.TemporaryDirectory(prefix='desktop event server ') as folder:
            home = Path(folder) / 'home'; home.mkdir()
            environment = dict(os.environ, CODEX_USAGE_BACKEND_CONTROL_TOKEN='a' * 64)
            process = subprocess.Popen([sys.executable, '-E', '-s', '-B', str(BACKEND / 'dashboard_server.py'), '--desktop-events', '--port', '0', '--instance-id', 'fixture-events', '--codex-home', str(home), '--cache-path', str(Path(folder) / 'index.sqlite'), '--refresh-seconds', '0'], stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=environment)
            pending = queue.Queue()
            def read_events():
                while True:
                    line = process.stdout.readline(4097)
                    pending.put(line)
                    if not line: return
            reader = threading.Thread(target=read_events, daemon=True); reader.start()
            def wait_for(predicate):
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    try: line = pending.get(timeout=max(0, deadline - time.monotonic()))
                    except queue.Empty: break
                    if not line: break
                    self.assertLessEqual(len(line), 4096)
                    row = json.loads(line)
                    self.assertEqual(row['protocol'], 1)
                    self.assertEqual(row['instance_id'], 'fixture-events')
                    self.assertEqual(row['pid'], process.pid)
                    if predicate(row): return row
                self.fail('Timed out waiting for event')
            try:
                ready = wait_for(lambda row: row['event'] == 'desktop_ready')
                initial = wait_for(lambda row: row['event'] == 'desktop_state' and row['ready'] and not row['scanning'])
                self.assertEqual(initial['refresh_completed'], 0)
                # Zero automatic refresh has no periodic event heartbeat.
                with self.assertRaises(queue.Empty): pending.get(timeout=.25)
                request = Request(ready['url'] + '/api/refresh', data=b'{"wait_ms":0}', headers={'Content-Type':'application/json', 'X-Codex-Instance':'fixture-events'}, method='POST')
                with build_opener(ProxyHandler({})).open(request, timeout=5) as response: receipt = json.load(response)
                done = wait_for(lambda row: row['event'] == 'desktop_state' and row['refresh_completed'] >= receipt['ticket'])
                self.assertFalse(done['scanning']); self.assertIsNone(done['refresh_error'])
                if os.name == 'nt':
                    # No --parent-pid: HTTP shutdown must still wake the idle
                    # server and preserve the private control-token boundary.
                    shutdown = Request(ready['url'] + '/api/shutdown', data=b'{}', headers={'Content-Type': 'application/json', 'X-Codex-Instance': 'fixture-events', 'X-Codex-Control': 'a' * 64}, method='POST')
                    with build_opener(ProxyHandler({})).open(shutdown, timeout=5) as response:
                        self.assertEqual(response.status, 200)
                    self.assertEqual(process.wait(timeout=5), 0)
            finally:
                if process.poll() is None: process.terminate()
                process.wait(timeout=5); reader.join(2)
                process.stdout.close(); process.stderr.close()
