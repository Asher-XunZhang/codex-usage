import csv
from datetime import datetime
import io
import itertools
import json
import sqlite3
import subprocess
import time
import os
import sys
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
from http.server import ThreadingHTTPServer
from urllib.request import Request, urlopen
from urllib.error import HTTPError

from dashboard_data import UsageIndex, TZ
from dashboard_server import make_handler, supervise, probe_health, LocalHTTPServer
from test_token_usage import meta, record, u, start, event, legacy

NOW = datetime(2026, 9, 10, 12, tzinfo=TZ)


class DashboardTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.home = Path(self.tmp.name)
        self.index = UsageIndex(self.home)

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, name, items, folder="sessions"):
        p = self.home / folder / (name + ".jsonl")
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text("".join(json.dumps(x)+"\n" for x in items), encoding="utf-8")
        return p

    def query(self, **kwargs):
        return self.index.query(now=NOW, **kwargs)

    def test_global_dedupe_fork_and_child_grouping(self):
        r = record("root", "t1", "r1")
        self.write("root", [meta("root"), *start("t1"), r])
        self.write("root-copy", [meta("root"), *start("t1"), r], "archived_sessions")
        self.write("child", [meta("child", parent="root", fork="root"), r,
                             *start("t2", "t1"), record("child", "t2", "r2", root="t1")])
        self.index.scan()
        out = self.query(group="task")
        self.assertEqual(out["summary"]["total_tokens"], 240)
        self.assertEqual(out["summary"]["requests"], 2)
        self.assertEqual(out["summary"]["active_tasks"], 1)
        self.assertEqual(out["groups"][0]["id"], "root")

    def test_incremental_partial_tail_and_truncation(self):
        p = self.write("root", [meta("root"), *start("t1"), record("root", "t1", "r1")])
        self.index.scan()
        r2 = record("root", "t1", "r2", thread_total=u(200, 40, 80, 10))
        raw = json.dumps(r2)
        with p.open("a", encoding="utf-8") as f:
            f.write(raw[:20])
        self.index.scan()
        self.assertEqual(self.query()["summary"]["requests"], 1)
        with p.open("a", encoding="utf-8") as f:
            f.write(raw[20:]+"\n")
        self.index.scan()
        self.assertEqual(self.query()["summary"]["requests"], 2)
        self.write("root", [meta("root")])
        self.index.scan()
        self.assertIsNone(self.query()["summary"]["requests"])

    def test_timezone_model_and_period_filters(self):
        r = record("root", "t1", "r1")
        r["timestamp"] = "2026-09-09T17:00:00Z"
        model = event("turn_context", {"turn_id": "t1", "model": "test-model"})
        self.write("root", [meta("root"), model, r])
        self.index.scan()
        out = self.query(days="1", model="test-model", task="root")
        self.assertEqual(out["timeline"][0]["date"], "2026-09-10")
        self.assertEqual(out["summary"]["requests"], 1)
        self.assertEqual(len(self.query(days="7")["timeline"]), 7)
        with self.assertRaises(ValueError):
            self.query(days="10000000000")

    def test_missing_cache_and_legacy_are_not_fabricated(self):
        count = u()
        count["cached_input_tokens"] = None
        self.write("root", [meta("root"), *start("t1"), record("root", "t1", "r1", count)])
        self.write("old", [meta("old"), *start("old-turn"), legacy(u())])
        self.index.scan()
        out = self.query()
        self.assertIsNone(out["summary"]["noncached_input_tokens"])
        self.assertIsNone(out["summary"]["cache_hit_rate"])
        self.assertEqual(out["summary"]["total_tokens"], 120)
        self.assertEqual(out["meta"]["excluded_legacy_threads"], 1)
        self.assertEqual(out["summary"]["requests"], 1)

    def test_no_message_bodies_in_payload(self):
        self.write("root", [meta("root"), *start("t1"), record("root", "t1", "r1"),
                            event("response_item", {"content": "SECRET_VALUE"})])
        self.index.scan()
        self.assertNotIn("SECRET_VALUE", json.dumps(self.query()))

    def old_call(self, total, last=None):
        e = legacy(total)
        e["payload"]["info"]["last_token_usage"] = last or total
        return e

    def test_verified_legacy_recovery_duplicates_and_incremental(self):
        a = self.old_call(u())
        p = self.write("old", [meta("old"), *start("t1"), a, a])
        self.index.scan()
        self.assertEqual(self.query()["summary"]["requests"], 1)
        with p.open("a") as f:
            f.write(json.dumps(self.old_call(u(200,40,80,10), u()))+"\n")
        self.index.scan()
        out = self.query()
        self.assertEqual(out["summary"]["total_tokens"], 240)
        self.assertEqual(out["summary"]["requests"], 2)
        self.assertEqual(out["meta"]["excluded_legacy_threads"], 0)
        self.assertEqual(out["meta"]["coverage"]["recovered_legacy_threads"], 1)

    def test_legacy_fork_reset_and_multiple_logs_stay_excluded(self):
        a = self.old_call(u())
        self.write("fork", [meta("fork", fork="root"), a])
        self.write("reset", [meta("reset"), a, self.old_call(u(10,2,4,1))])
        self.write("copies", [meta("copies"), a])
        self.write("copies-archive", [meta("copies"), a], "archived_sessions")
        self.index.scan()
        out = self.query()
        self.assertIsNone(out["summary"]["total_tokens"])
        self.assertEqual(out["meta"]["excluded_legacy_threads"], 3)

    def test_legacy_skips_unverified_gap_and_never_overlaps_modern(self):
        self.write("partial", [meta("partial"), self.old_call(u(200,40,80,10), u()),
                               self.old_call(u(300,60,120,15), u())])
        self.write("mixed", [meta("mixed"), self.old_call(u()), record("mixed","t","r")])
        self.index.scan()
        out = self.query()
        self.assertEqual(out["summary"]["total_tokens"], 240)
        self.assertEqual(out["meta"]["coverage"]["partial_legacy_threads"], 1)

    def test_cumulative_gap_is_diagnostic_not_extra_tokens(self):
        self.write("root", [meta("root"), record("root", "t", "r", thread_total=u(300,60,120,15))])
        self.index.scan()
        out = self.query()
        self.assertEqual(out["summary"]["total_tokens"], 120)
        self.assertEqual(out["meta"]["coverage"]["cumulative_gaps"][0]["difference"], 240)

    def test_task_titles_from_read_only_app_metadata(self):
        self.write("root", [meta("root"), record("root", "t", "r")])
        database = self.home / "state_5.sqlite"
        db = sqlite3.connect(database)
        db.execute('CREATE TABLE threads (id TEXT, name TEXT, title TEXT, archived INTEGER, first_user_message TEXT)')
        db.execute('INSERT INTO threads VALUES (?,?,?,?,?)', ('root','用户设置的标题','自动标题',1,'PRIVATE_PROMPT'))
        db.commit(); db.close()
        before = database.read_bytes()
        self.index.scan()
        out = self.query(group="task")
        self.assertEqual(out['filters']['tasks'][0]['label'], '用户设置的标题')
        self.assertTrue(out['filters']['tasks'][0]['archived'])
        self.assertEqual(out['groups'][0]['label'], '用户设置的标题')
        self.assertNotIn('PRIVATE_PROMPT', json.dumps(out))
        self.assertEqual(before, database.read_bytes())

    def test_task_choices_follow_date_model_and_recent_usage(self):
        for tid, stamp, model in [('a','2026-09-10T01:00:00Z','x'),
                                  ('b','2026-09-10T02:00:00Z','x'),
                                  ('c','2026-09-01T01:00:00Z','x'),
                                  ('d','2026-09-10T03:00:00Z','y')]:
            r = record(tid,'t','r'); r['timestamp'] = stamp
            self.write(tid,[meta(tid),event('turn_context',{'turn_id':'t','model':model}),r])
        self.index.scan()
        out = self.query(days='1',model='x',task='c')
        self.assertEqual([t['id'] for t in out['filters']['tasks']], ['b','a'])
        self.assertEqual(out['filters']['selected_task']['id'], 'c')
        self.assertEqual(out['summary']['total_tokens'], 0)
        self.assertTrue(out['filters']['tasks'][0]['label'].startswith('未命名任务 · '))

    def test_background_review_is_named_and_kept_in_totals(self):
        self.write('review', [meta('review'), event('turn_context', {'model':'codex-auto-review'}), record('review','t','r')])
        self.index.scan()
        out = self.query(group='task')
        self.assertTrue(out['filters']['tasks'][0]['system'])
        self.assertTrue(out['groups'][0]['label'].startswith('后台审批检查 · '))
        self.assertEqual(out['summary']['total_tokens'],120)

    def test_supervisor_retries_even_zero_exit_and_is_bounded(self):
        with patch('dashboard_server.subprocess.Popen') as spawn, patch('dashboard_server.threading.Event.wait', return_value=False) as sleep, patch('builtins.print'):
            spawn.return_value.pid = 123
            spawn.return_value.wait.side_effect = [0,1,-1,0]
            self.assertEqual(supervise(['python','server.py']),1)
            self.assertEqual(spawn.call_count,4)
            self.assertEqual(sleep.call_count,3)

    def test_supervisor_interrupt_stops_child_without_restart(self):
        with patch('dashboard_server.subprocess.Popen') as spawn, patch('dashboard_server.threading.Event.wait', return_value=False) as sleep:
            spawn.return_value.wait.side_effect = [KeyboardInterrupt(),0]
            self.assertEqual(supervise(['python','server.py']),0)
            spawn.return_value.terminate.assert_called_once()
            self.assertEqual(spawn.call_count,1)
            sleep.assert_not_called()

    def test_health_initialization_freshness_and_stall(self):
        self.assertEqual(self.index.health()['status'],'initializing')
        self.index.started_monotonic = time.monotonic()-301
        self.assertEqual(self.index.health()['status'],'stalled')
        self.index.scan()
        self.assertTrue(self.index.health()['ready'])
        self.index.last_success_monotonic = time.monotonic()-121
        self.assertEqual(self.index.health()['status'],'stalled')

    def test_supervisor_restarts_hung_worker_after_three_failed_probes(self):
        expired = subprocess.TimeoutExpired('worker',5)
        with patch('dashboard_server.subprocess.Popen') as spawn, patch('dashboard_server.probe_health',return_value=False) as probe, patch('dashboard_server.time.monotonic', side_effect=itertools.count(0, 10)), patch('builtins.print'):
            spawn.return_value.pid = 123
            spawn.return_value.wait.side_effect = [expired,expired,expired,1]
            self.assertEqual(supervise(['python','server.py'], max_restarts=0, health_url='http://127.0.0.1:8766/health'),1)
            self.assertEqual(probe.call_count,3)
            spawn.return_value.terminate.assert_called_once()

    def test_successful_probe_resets_failure_streak(self):
        expired = subprocess.TimeoutExpired('worker',5)
        with patch('dashboard_server.subprocess.Popen') as spawn, patch('dashboard_server.probe_health',side_effect=[False,False,True,False]), patch('dashboard_server.time.monotonic', side_effect=itertools.count(0, 10)), patch('builtins.print'):
            spawn.return_value.pid = 123
            spawn.return_value.wait.side_effect = [expired,expired,expired,expired,0]
            self.assertEqual(supervise(['python','server.py'],max_restarts=0,health_url='http://127.0.0.1:8766/health'),1)
            spawn.return_value.terminate.assert_not_called()

    @unittest.skipUnless(os.name == 'nt', 'Windows job lifecycle')
    def test_job_close_terminates_owned_child(self):
        from windows_job import WindowsChildJob
        child = subprocess.Popen([sys.executable,'-c','import time;time.sleep(30)'], creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            with WindowsChildJob() as job:
                job.assign(child.pid)
            child.wait(timeout=5)
            self.assertIsNotNone(child.returncode)
        finally:
            if child.poll() is None:
                child.kill(); child.wait()

    def test_port_cannot_be_shared_with_second_dashboard(self):
        server = LocalHTTPServer(('127.0.0.1',0),make_handler(self.index))
        try:
            with self.assertRaises(OSError):
                other = LocalHTTPServer(server.server_address,make_handler(self.index))
                other.server_close()
        finally:
            server.server_close()

    def test_http_scope_filters_and_csv_formula_escaping(self):
        self.write("root", [meta("root"), *start("t1"), record("root", "t1", "r1")])
        (self.home / "session_index.jsonl").write_text(json.dumps({"id": "root", "thread_name": "=1+1"})+"\n")
        self.index.scan()
        server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(self.index))
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        url = f"http://127.0.0.1:{server.server_port}"
        try:
            with urlopen(url+'/health') as r:
                health = json.load(r)
                self.assertEqual(health['status'],'healthy')
                self.assertNotIn('tasks',health)
            self.assertTrue(probe_health(url+'/health',health['pid']))
            self.assertFalse(probe_health(url+'/health',-1))
            self.index.last_success_monotonic = time.monotonic()-121
            with self.assertRaises(HTTPError) as stale:
                urlopen(url+'/health')
            self.assertEqual(stale.exception.code,503)
            stale.exception.close()
            self.index.last_success_monotonic = time.monotonic()
            with urlopen(url+"/api/usage?days=all") as r:
                self.assertEqual(json.load(r)["summary"]["requests"], 1)
            with urlopen(url+"/api/summary?days=all&model=removed-model&task=removed-task") as r:
                fallback = json.load(r)
                self.assertTrue(fallback['filter_reset'])
                self.assertEqual(fallback['summary']['requests'], 1)
                self.assertNotIn('tasks', fallback['filters'])
            # Summary recovery never weakens validation for the main page or
            # accepts invalid periods/groups as an all-time fallback.
            for suffix in ['/api/usage?model=removed-model', '/api/summary?days=bad&model=removed-model', '/api/summary?group=bad&task=removed-task']:
                with self.assertRaises(HTTPError) as invalid:
                    urlopen(url + suffix)
                self.assertEqual(invalid.exception.code, 400)
                invalid.exception.close()
            with urlopen(url+"/api/export.csv?days=all&group=task") as r:
                rows = list(csv.reader(io.StringIO(r.read().decode("utf-8-sig"))))
                self.assertEqual(rows[1][0], "'=1+1")
            for path, headers, status in [("/../auth.json", {}, 404), ("/api/usage?days=bad", {}, 400),
                                          ("/api/usage", {"Origin": "https://example.com"}, 403),
                                          ("/api/usage", {"Host": "evil.example"}, 403)]:
                with self.assertRaises(HTTPError) as error:
                    urlopen(Request(url+path, headers=headers))
                self.assertEqual(error.exception.code, status)
                error.exception.close()
        finally:
            server.shutdown()
            server.server_close()
            thread.join()


if __name__ == "__main__":
    unittest.main()
