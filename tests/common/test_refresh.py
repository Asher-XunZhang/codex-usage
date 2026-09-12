import json
import os
from contextlib import closing
from datetime import datetime, timedelta
import sqlite3
from pathlib import Path
import tempfile
import threading
import time
import tracemalloc
import unittest
from unittest.mock import patch
from urllib.request import Request, build_opener, ProxyHandler
from urllib.error import HTTPError

from dashboard_data import UsageIndex
from dashboard_server import LocalHTTPServer, make_handler
from tests.common.test_token_usage import meta, start, record, u


class RefreshTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.home = Path(self.temp.name)
        (self.home / 'sessions').mkdir()
        self.index = UsageIndex(self.home, refresh_seconds=0)
        self.thread = None

    def tearDown(self):
        self.index.stop.set()
        if self.thread:
            self.thread.join(timeout=3)
            self.assertFalse(self.thread.is_alive())
        self.temp.cleanup()

    def wait(self, predicate, timeout=3):
        end = time.monotonic() + timeout
        while not predicate():
            if time.monotonic() >= end:
                self.fail('refresh did not finish in time')
            time.sleep(.01)

    def launch(self):
        self.thread = threading.Thread(target=self.index.run)
        self.thread.start()

    def write(self, amount):
        events = [meta('refresh-task'), *start('refresh-turn'), record('refresh-task', 'refresh-turn', 'response', value=u(amount, 20, 80, 10))]
        path = self.home / 'sessions/refresh.jsonl'
        previous = path.stat().st_mtime_ns if path.exists() else None
        path.write_text(''.join(json.dumps(e)+'\n' for e in events))
        if previous is not None:
            # Same-length writes can share one filesystem clock tick on Windows.
            # These tests exercise refreshed snapshots, not clock resolution.
            current = path.stat()
            os.utime(path, ns=(current.st_atime_ns, max(current.st_mtime_ns, previous + 1_000_000_000)))

    def test_manual_refresh_reads_new_file_while_auto_is_off(self):
        self.launch(); self.wait(lambda: not self.index.loading)
        self.assertIsNone(self.index.query(days='all')['summary']['total_tokens'])
        self.write(300)
        started = time.monotonic()
        ticket = self.index.request_refresh()
        self.wait(lambda: self.index.refresh_completed >= ticket)
        self.assertLess(time.monotonic() - started, 2)
        self.assertEqual(self.index.query(days='all')['summary']['total_tokens'], 320)
        self.assertIsNone(self.index.refresh_error)

    def test_summary_matches_full_query_without_detail_payload(self):
        self.write(400); self.index.scan()
        for filters in [{'days':'all'}, {'days':'1','task':'refresh-task'}]:
            full = self.index.query(**filters)
            compact = self.index.query(**filters, summary_only=True)
            self.assertEqual(compact['summary'], full['summary'])
            self.assertEqual(compact['filters']['selected_task'], full['filters']['selected_task'])
            self.assertNotIn('groups', compact)
            self.assertNotIn('timeline', compact)
            self.assertNotIn('tasks', compact['filters'])
        empty = UsageIndex(self.home/'missing'); empty.scan()
        self.assertIsNone(empty.query(summary_only=True)['summary']['total_tokens'])

    def test_unchanged_scan_reuses_aggregates_but_reports_new_scan_time(self):
        self.write(100); self.index.scan()
        previous_rows = self.index.rows
        stamp = self.index.updated
        # OS wall clocks may return the same timestamp for consecutive quick scans.
        # Advance the test clock deterministically to test publication, not precision.
        later = datetime.fromisoformat(stamp) + timedelta(seconds=1)
        with patch('dashboard_data.sqlite3.connect', side_effect=AssertionError('must not reread database')), \
                patch('dashboard_data.datetime') as clock:
            clock.now.return_value = later
            self.index.scan()
        self.assertIs(self.index.rows, previous_rows)
        self.assertNotEqual(self.index.updated, stamp)
        self.write(300); self.index.scan()
        self.assertEqual(self.index.query(days='all')['summary']['total_tokens'], 320)

    def test_modern_log_does_not_retain_unused_legacy_copies(self):
        events = [meta('refresh-task'), *start('refresh-turn')]
        for i in range(1, 101):
            events.append({'type':'event_msg','timestamp':'2026-09-10T01:00:00Z','payload':{'type':'token_count','info':{
                'total_token_usage':u(100*i,20*i,80*i,10*i),'last_token_usage':u(100,20,80,10)}}})
        events.append(record('refresh-task','refresh-turn','modern',value=u(100,20,80,10)))
        path = self.home/'sessions/refresh.jsonl'
        path.write_text(''.join(json.dumps(e)+'\n' for e in events))
        self.index.scan()
        self.assertEqual(len(self.index.files[path].legacy_rows), 0)
        self.assertEqual(self.index.query(days='all')['summary']['total_tokens'], 120)

    def test_published_rows_remain_unchanged_after_next_scan(self):
        self.write(100); self.index.scan()
        old = self.index.rows[0]
        self.write(300); self.index.scan()
        self.assertEqual(old['usage']['total_tokens'], 120)
        self.assertEqual(self.index.rows[0]['usage']['total_tokens'], 320)

    def test_very_long_database_title_has_bounded_python_allocation(self):
        self.write(100)
        with closing(sqlite3.connect(self.home/'state_5.sqlite')) as database:
            with database:
                database.execute('CREATE TABLE threads(id TEXT, title TEXT, archived INTEGER)')
                database.execute('INSERT INTO threads VALUES (?,?,0)', ('refresh-task', 'LongTitle ' * 200000))
        tracemalloc.start()
        try:
            self.index.scan()
            _, peak = tracemalloc.get_traced_memory()
        finally:
            tracemalloc.stop()
        self.assertLess(peak, 1024 * 1024, 'A multi-MB title must not be materialized in Python')
        self.assertTrue(self.index.tasks[0]['label'].startswith('LongTitle'))
        self.assertLessEqual(len(self.index.tasks[0]['label']), 160)

    def test_interval_change_wakes_sleep_and_pause_does_not_look_stalled(self):
        self.launch(); self.wait(lambda: not self.index.loading)
        self.write(200)
        self.index.configure_refresh(1)
        self.wait(lambda: bool(self.index.rows))
        self.assertEqual(self.index.query(days='all')['summary']['total_tokens'], 220)
        self.index.configure_refresh(0)
        self.wait(lambda: not self.index.scanning)
        before = self.index.updated
        self.write(500)
        time.sleep(1.2)
        self.assertEqual(self.index.updated, before)
        self.index.last_success_monotonic = time.monotonic() - 10000
        self.assertEqual(self.index.health()['status'], 'healthy')
        self.assertEqual(self.index.query()['meta']['refresh_seconds'], 0)

    def test_request_during_scan_requires_followup_scan_and_never_overlaps(self):
        entered = threading.Event(); release = threading.Event()
        second_entered = threading.Event(); second_release = threading.Event()
        scans = []
        real_scan = self.index.scan
        def slow_scan():
            scans.append(len(scans)+1)
            if len(scans) == 1:
                entered.set(); release.wait(2)
            elif len(scans) == 2:
                second_entered.set(); second_release.wait(2)
            real_scan()
        with patch.object(self.index, 'scan', side_effect=slow_scan):
            self.launch(); self.assertTrue(entered.wait(2))
            first = self.index.request_refresh()
            second = self.index.request_refresh()
            self.assertGreater(second, first)
            release.set(); self.assertTrue(second_entered.wait(2))
            self.assertLess(self.index.refresh_completed, first)
            second_release.set()
            self.wait(lambda: self.index.refresh_completed >= second)
            self.assertEqual(len(scans), 2)

    def test_refresh_error_is_visible_and_next_request_recovers(self):
        self.write(100); self.launch(); self.wait(lambda: not self.index.loading)
        before = self.index.updated
        with patch.object(self.index, 'scan', side_effect=OSError('test failure')):
            ticket = self.index.request_refresh()
            self.wait(lambda: self.index.refresh_completed >= ticket)
            self.assertIsNotNone(self.index.health()['refresh_error'])
            self.assertEqual(self.index.updated, before)
        ticket = self.index.request_refresh()
        self.wait(lambda: self.index.refresh_completed >= ticket)
        self.assertIsNone(self.index.health()['refresh_error'])

    def test_http_refresh_and_settings_require_instance_and_validate_values(self):
        self.launch(); self.wait(lambda: not self.index.loading)
        server = LocalHTTPServer(('127.0.0.1', 0), make_handler(self.index, 'refresh-test'))
        worker = threading.Thread(target=server.serve_forever, daemon=True); worker.start()
        opener = build_opener(ProxyHandler({}))
        base = f'http://127.0.0.1:{server.server_port}'
        def post(path, body, headers=None):
            req = Request(base + path, data=json.dumps(body).encode(), headers=headers or {'X-Codex-Instance': 'refresh-test'})
            with opener.open(req, timeout=2) as response:
                return json.load(response)
        try:
            for headers in [{'X-Codex-Instance': 'wrong'}, {'X-Codex-Instance':'refresh-test','Origin':'https://example.com'}, {'X-Codex-Instance':'refresh-test','Host':'evil.example'}]:
                with self.assertRaises(HTTPError) as error:
                    post('/api/refresh', {}, headers)
                self.assertEqual(error.exception.code, 403); error.exception.close()
            for value in [-1, 3601, 1.5, True, '5', None]:
                with self.assertRaises(HTTPError) as error:
                    post('/api/settings', {'refresh_seconds': value})
                self.assertEqual(error.exception.code, 400); error.exception.close()
                self.assertEqual(self.index.refresh_seconds, 0)
            self.assertEqual(post('/api/settings', {'refresh_seconds': 7})['refresh_seconds'], 7)
            self.write(400)
            ticket = post('/api/refresh', {})['ticket']
            self.wait(lambda: self.index.refresh_completed >= ticket)
            with opener.open(base + '/api/usage?days=all') as response:
                snapshot = json.load(response)
            self.assertEqual(snapshot['summary']['total_tokens'], 420)
            self.assertEqual(snapshot['meta']['refresh_seconds'], 7)
            self.write(500)
            completed = post('/api/refresh', {'wait_ms': 2000})
            self.assertGreaterEqual(completed['completed'], completed['ticket'])
            self.assertIsNotNone(completed['generated_at'])
            self.assertEqual(self.index.query(days='all')['summary']['total_tokens'], 520)
            for invalid_wait in [-1, 5001, True, '500']:
                with self.assertRaises(HTTPError) as error:
                    post('/api/refresh', {'wait_ms': invalid_wait})
                self.assertEqual(error.exception.code, 400); error.exception.close()
        finally:
            server.shutdown(); server.server_close(); worker.join(2)

if __name__ == '__main__':
    unittest.main()
