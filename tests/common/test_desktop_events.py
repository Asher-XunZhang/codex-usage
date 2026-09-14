"""Opt-in native event transport, with isolated server processes and bounded IO."""
from tools.common.paths import BACKEND
import importlib.util
import json
import os
from pathlib import Path
import selectors
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


@unittest.skipUnless(sys.platform == 'darwin', 'Native event mode is macOS only')
class DesktopEventServerTests(unittest.TestCase):
    def test_handshake_scan_and_manual_receipt(self):
        with tempfile.TemporaryDirectory(prefix='desktop event server ') as folder:
            home = Path(folder) / 'home'; home.mkdir()
            process = subprocess.Popen([sys.executable, '-E', '-s', '-B', str(BACKEND / 'dashboard_server.py'), '--desktop-events', '--port', '0', '--instance-id', 'fixture-events', '--codex-home', str(home), '--cache-path', str(Path(folder) / 'index.sqlite'), '--refresh-seconds', '0'], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            selector = selectors.DefaultSelector(); selector.register(process.stdout, selectors.EVENT_READ)
            buffer = bytearray(); pending = []
            def wait_for(predicate):
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    while pending:
                        row = pending.pop(0)
                        self.assertEqual(row['protocol'], 1)
                        self.assertEqual(row['instance_id'], 'fixture-events')
                        if predicate(row): return row
                    if not selector.select(max(0, deadline - time.monotonic())): break
                    data = os.read(process.stdout.fileno(), 65536)
                    if not data: break
                    buffer.extend(data)
                    while b'\n' in buffer:
                        line, _, rest = buffer.partition(b'\n'); buffer[:] = rest
                        self.assertLessEqual(len(line), 4096); pending.append(json.loads(line))
                self.fail('Timed out waiting for event')
            try:
                ready = wait_for(lambda row: row['event'] == 'desktop_ready')
                initial = wait_for(lambda row: row['event'] == 'desktop_state' and row['ready'] and not row['scanning'])
                self.assertEqual(initial['refresh_completed'], 0)
                # Zero automatic refresh has no periodic event heartbeat.
                self.assertFalse(selector.select(0.25))
                request = Request(ready['url'] + '/api/refresh', data=b'{"wait_ms":0}', headers={'Content-Type':'application/json', 'X-Codex-Instance':'fixture-events'}, method='POST')
                with build_opener(ProxyHandler({})).open(request, timeout=5) as response: receipt = json.load(response)
                done = wait_for(lambda row: row['event'] == 'desktop_state' and row['refresh_completed'] >= receipt['ticket'])
                self.assertFalse(done['scanning']); self.assertIsNone(done['refresh_error'])
            finally:
                process.terminate(); process.wait(timeout=5); selector.close()
                process.stdout.close(); process.stderr.close()
