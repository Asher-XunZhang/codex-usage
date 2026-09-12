"""Precise, bounded, read-only budget aggregation using synthetic accounting."""
from tools.common.paths import ROOT, BACKEND, macos_source
from contextlib import closing
from datetime import datetime
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest

from disk_index import DiskUsageIndex
from tests.common.test_token_usage import meta, start, record, u, event


def epoch(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS C toolchain')
class BudgetQueryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory(prefix='native budget build ')
        cls.binary = Path(cls.build.name) / 'summary'
        subprocess.run(['xcrun', 'clang', '-Os', '-Wall', '-Wextra',
                        str(macos_source('Summary.c')),
                        '-lsqlite3', '-o', str(cls.binary)], check=True, capture_output=True)

    @classmethod
    def tearDownClass(cls):
        cls.build.cleanup()

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='budget 数据 ')
        self.home = Path(self.tmp.name)
        self.index = DiskUsageIndex(self.home, self.home / 'index.sqlite')

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, tid='root', entries=None, parent=None, archive=False):
        entries = entries or [('2026-09-10T01:00:00Z', u(), 'model-a')]
        rows = [meta(tid, parent=parent), *start('turn-' + tid)]
        totals = dict.fromkeys(u(), 0)
        for i, (stamp, value, model) in enumerate(entries):
            for key in totals:
                totals[key] = totals[key] + value[key] if totals[key] is not None and value[key] is not None else None
            rows.append(event('turn_context', {'turn_id': 'turn-' + tid, 'model': model}))
            row = record(tid, 'turn-' + tid, f'{tid}-{i}', value, thread_total=totals.copy())
            row['timestamp'] = stamp
            rows.append(row)
        folder = self.home / ('archived_sessions' if archive else 'sessions')
        folder.mkdir(exist_ok=True)
        path = folder / (tid + '.jsonl')
        path.write_text(''.join(json.dumps(row) + '\n' for row in rows))
        return path

    def request(self, **overrides):
        return dict(id='budget-one', revision=1, periodID='2026-09-10/Asia/Shanghai',
                    start=epoch('2026-09-10T00:00:00Z'), end=epoch('2026-09-11T00:00:00Z'),
                    model='all', task='all') | overrides

    def run_query(self, requests=None, raw=None, cache=None, expected=0):
        payload = self.home / 'requests.json'
        payload.write_text(raw if raw is not None else json.dumps({'requests': requests if requests is not None else [self.request()]}))
        result = subprocess.run([str(self.binary), '--cache-path', str(cache or self.index.cache),
                                 '--budgets-file', str(payload)], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, expected, result.stderr)
        return json.loads(result.stdout) if expected == 0 else result

    def test_half_open_instants_cross_offsets_midnight_and_arbitrary_hours(self):
        self.write(entries=[
            ('2026-09-09T23:59:59Z', u(10, 1, 0, 0), 'model-a'),
            ('2026-09-10T08:00:00+08:00', u(20, 2, 0, 0), 'model-a'),
            ('2026-09-09T20:30:00-04:00', u(30, 3, 0, 0), 'model-a'),
            ('2026-09-10T09:00:00+08:00', u(40, 4, 0, 0), 'model-a'),
        ])
        self.index.scan()
        # Preserve explicit mixed offsets in the synthetic index as well: the
        # normal parser often normalizes them before persistence.
        with closing(sqlite3.connect(self.index.cache)) as db, db:
            db.execute("UPDATE usage SET timestamp='2026-09-09T20:30:00-04:00' WHERE response='root-2'")
        result = self.run_query([
            self.request(end=epoch('2026-09-10T01:00:00Z')),
            self.request(id='later', start=epoch('2026-09-10T00:30:00Z'), end=epoch('2026-09-10T01:00:00Z')),
            self.request(id='boundary', start=epoch('2026-09-10T01:00:00Z'), end=epoch('2026-09-10T01:01:00Z')),
        ])
        self.assertEqual([r['rows'][0]['total_tokens'] for r in result['results']], [55, 33, 44])
        self.assertEqual([r['rows'][0]['requests'] for r in result['results']], [2, 1, 1])
        self.assertIsInstance(result['generated_at'], (int, float))
        self.assertTrue(result['coverage_complete'], result)

    def test_fractional_boundary_and_end_exclusion(self):
        self.write(entries=[('2026-09-10T01:00:00.100Z', u(), 'model-a'),
                            ('2026-09-10T01:00:00.900Z', u(), 'model-a')])
        self.index.scan()
        result = self.run_query([self.request(start=epoch('2026-09-10T01:00:00.100Z'),
                                              end=epoch('2026-09-10T01:00:00.900Z'))])
        self.assertEqual(result['results'][0]['rows'][0]['requests'], 1)

    def test_models_grouped_and_nested_children_attributed_once(self):
        root = self.write()
        self.write('child', [('2026-09-10T01:00:00Z', u(200, 40), 'model-b')], parent='root')
        self.write('grandchild', [('2026-09-10T01:00:00Z', u(300, 60), 'model-b')], parent='child')
        self.write('unrelated', [('2026-09-10T01:00:00Z', u(900, 90), 'model-a')])
        (self.home / 'archived_sessions').mkdir()
        shutil.copy(root, self.home / 'archived_sessions' / root.name)
        self.index.scan()
        result = self.run_query([self.request(task='root')])['results'][0]
        self.assertTrue(result['scope_valid'])
        self.assertEqual([(row['model'], row['requests'], row['total_tokens']) for row in result['rows']],
                         [('model-a', 1, 120), ('model-b', 2, 600)])

    def test_optional_unknowns_preserve_counts_and_known_lower_bounds(self):
        missing = u(200, 40)
        missing['cached_input_tokens'] = None
        missing['reasoning_output_tokens'] = None
        missing['cache_write_input_tokens'] = None
        self.write(entries=[('2026-09-10T01:00:00Z', u(), 'model-a'),
                            ('2026-09-10T02:00:00Z', missing, 'model-a')])
        self.index.scan()
        row = self.run_query()['results'][0]['rows'][0]
        self.assertEqual(row['total_tokens'], 360)
        self.assertEqual(row['known_total_tokens'], 2)
        self.assertEqual(row['lower_bound_total_tokens'], 360)
        for field, known in [('cached_input_tokens', 40), ('reasoning_output_tokens', 5), ('cache_write_input_tokens', 0)]:
            self.assertIsNone(row[field])
            self.assertEqual(row['known_' + field], 1)
            self.assertEqual(row['lower_bound_' + field], known)
        # A future/partial schema record must not turn a missing core metric into
        # zero either, even though today's parser rejects such incomplete rows.
        with closing(sqlite3.connect(self.index.cache)) as db, db:
            db.execute("UPDATE usage SET total_tokens=NULL WHERE response='root-1'")
        row = self.run_query()['results'][0]['rows'][0]
        self.assertIsNone(row['total_tokens'])
        self.assertEqual(row['known_total_tokens'], 1)
        self.assertEqual(row['lower_bound_total_tokens'], 120)

    def test_missing_scope_stays_invalid_without_falling_back_to_all(self):
        self.write()
        self.write('other', [('2026-09-10T01:00:00Z', u(), 'model-b')])
        self.index.scan()
        result = self.run_query([
            self.request(model='deleted-model'),
            self.request(id='deleted-task', task='deleted-task'),
            self.request(id='empty-combination', model='model-a', task='other'),
            self.request(id='sql-filter', model="x' OR 1=1 --"),
        ])['results']
        self.assertEqual([r['scope_valid'] for r in result], [False, False, True, False])
        self.assertTrue(all(not r['rows'] for r in result))
        self.assertTrue(result[0]['issues'])
        self.assertFalse(result[2]['issues'])

    def test_empty_interval_is_zero_only_with_a_usable_existing_index(self):
        self.index.scan()
        result = self.run_query()
        self.assertFalse(result['has_rows'])
        self.assertFalse(result['coverage_complete'])
        self.assertFalse(result['results'][0]['rows'])
        self.assertTrue(result['results'][0]['issues'])
        self.write()
        self.index.scan()
        result = self.run_query([self.request(start=epoch('2026-09-12T00:00:00Z'), end=epoch('2026-09-13T00:00:00Z'))])
        self.assertTrue(result['coverage_complete'])
        self.assertEqual(result['results'][0]['rows'], [])

    def test_invalid_record_time_and_global_coverage_are_visible(self):
        self.write()
        self.index.scan()
        with closing(sqlite3.connect(self.index.cache)) as db, db:
            db.execute("UPDATE usage SET timestamp='not-an-instant'")
            snapshot = json.loads(db.execute("SELECT value FROM kv WHERE key='snapshot'").fetchone()[0])
            snapshot['issues'] = ['partial log'] * 40
            snapshot['coverage']['partial_legacy_threads'] = 2
            db.execute("UPDATE kv SET value=? WHERE key='snapshot'", (json.dumps(snapshot),))
        result = self.run_query()
        self.assertFalse(result['coverage_complete'])
        self.assertEqual(result['coverage']['invalid_timestamp_rows'], 1)
        self.assertEqual(result['coverage']['partial_legacy_threads'], 2)
        self.assertTrue(result['coverage']['issues_truncated'])
        self.assertEqual(len(result['issues']), 32)
        self.assertFalse(result['results'][0]['coverage_complete'])

    def test_only_initializer_migrates_expression_index_and_reader_never_creates_cache(self):
        self.write()
        self.index.scan()
        with closing(sqlite3.connect(self.index.cache)) as db, db:
            plan = str(db.execute("EXPLAIN QUERY PLAN SELECT * FROM usage WHERE julianday(timestamp)>=julianday(?,'unixepoch') AND julianday(timestamp)<julianday(?,'unixepoch')", (0, 9999999999)).fetchall())
            self.assertIn('usage_budget_time', plan)
            db.execute('DROP INDEX usage_budget_time')
            before = db.execute("SELECT value FROM kv WHERE key='snapshot'").fetchone()[0]
        shutil.rmtree(self.home / 'sessions')
        self.assertEqual(self.run_query()['results'][0]['rows'][0]['total_tokens'], 120)
        with closing(sqlite3.connect(self.index.cache)) as db, db:
            self.assertFalse(db.execute("SELECT 1 FROM sqlite_master WHERE name='usage_budget_time'").fetchone())
            self.assertEqual(db.execute("SELECT value FROM kv WHERE key='snapshot'").fetchone()[0], before)
        DiskUsageIndex(self.home, self.index.cache)
        with closing(sqlite3.connect(self.index.cache)) as db, db:
            self.assertTrue(db.execute("SELECT 1 FROM sqlite_master WHERE name='usage_budget_time'").fetchone())
        absent = self.home / 'does-not-exist' / 'missing.sqlite'
        self.run_query(cache=absent, expected=75)
        self.assertFalse(absent.parent.exists())

    def test_bad_json_shapes_types_ids_bounds_and_duplicate_rules_are_rejected(self):
        self.index.scan()
        invalid = [None, True, [], {}, {'id': 'missing'}, self.request(start=True), self.request(end=False),
                   self.request(revision=True), self.request(revision=1.5), self.request(revision=-1),
                   self.request(start=1e309), self.request(end=-1e309), self.request(start='123'),
                   self.request(start=10, end=10), self.request(start=10, end=9), self.request(end=1e20),
                   self.request(id=''), self.request(id='a' * 129), self.request(id='nul\x00value'),
                   self.request(periodID='x' * 257), self.request(model='m' * 513), self.request(task=[])]
        for rule in invalid:
            with self.subTest(rule=rule):
                self.run_query([rule], expected=2)
        for raw in ['invalid', '[]', '{}', '{"requests":null}', '{"requests":[],"requests":[]}',
                    '{"requests":[],"extra":true}', '{"requests":[NaN]}', '{"requests":[]}\x00tail']:
            with self.subTest(raw=raw):
                self.run_query(raw=raw, expected=2)
        self.run_query([self.request(), self.request()], expected=2)
        self.run_query([self.request(id=str(i)) for i in range(51)], expected=2)
        raw = json.dumps({'requests': [self.request()]})
        self.run_query(raw=raw.replace('"revision": 1', '"revision": 1, "revision": 2'), expected=2)
        self.run_query(raw=' ' * 65537, expected=2)
        self.assertEqual(self.run_query([])['results'], [])

    def test_nul_escapes_are_rejected_without_rejecting_literal_backslashes(self):
        self.index.scan()
        # JSON1 in older system SQLite truncates at a decoded NUL. Check all
        # identifiers before that loss, including an escaped backslash prefix.
        for field in ('id', 'periodID', 'model', 'task'):
            for value in ('nul\x00value', '\\' + '\x00', '\\\\' + '\x00'):
                with self.subTest(field=field, value=repr(value)):
                    self.run_query([self.request(**{field: value})], expected=2)
        for value in (r'literal\u0000', r'escaped\\u0000', r'quote"\u0000'):
            with self.subTest(literal=value):
                result = self.run_query([self.request(id=value, periodID=value)])
                self.assertEqual(result['results'][0]['id'], value)
                self.assertEqual(result['results'][0]['periodID'], value)
        # A backslash may itself be expressed as a Unicode escape; it must not
        # make the following literal characters into a second JSON escape.
        raw = json.dumps({'requests': [self.request(id='placeholder')]})
        raw = raw.replace('"placeholder"', r'"literal\u005cu0000"')
        self.assertEqual(self.run_query(raw=raw)['results'][0]['id'], r'literal\u0000')

    def test_batch_is_one_snapshot_while_writer_commits_and_has_bounded_output(self):
        self.write()
        self.index.scan()
        requests = [self.request(id=str(i)) for i in range(50)]
        payload = self.home / 'requests.json'
        payload.write_text(json.dumps({'requests': requests}))
        process = subprocess.Popen([str(self.binary), '--cache-path', str(self.index.cache),
                                    '--budgets-file', str(payload)], stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
        try:
            first = process.stdout.read(1)  # Output means its read snapshot is pinned.
            with closing(sqlite3.connect(self.index.cache)) as db, db:
                db.execute('UPDATE usage SET total_tokens=999')
            remainder, error = process.communicate(timeout=10)
            self.assertEqual(process.returncode, 0, error)
            output = first + remainder
            results = json.loads(output)['results']
            self.assertEqual([r['rows'][0]['total_tokens'] for r in results], [120] * 50)
            self.assertLess(len(output), 2 * 1024 * 1024)
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()

    def test_excessive_group_output_and_nonregular_request_file_fail_closed(self):
        self.write()
        self.index.scan()
        with closing(sqlite3.connect(self.index.cache)) as db, db:
            template = db.execute('SELECT * FROM usage').fetchone()
            columns = [row[1] for row in db.execute('PRAGMA table_info(usage)')]
            rows = []
            for i in range(1025):
                value = list(template)
                value[columns.index('response')] = f'large-model-{i}'
                value[columns.index('model')] = f'large-model-{i}'
                rows.append(value)
            db.executemany('INSERT INTO usage VALUES (' + ','.join('?' for _ in columns) + ')', rows)
        result = self.run_query(expected=75)
        self.assertLess(len(result.stdout), 2 * 1024 * 1024)
        fifo = self.home / 'request-pipe'
        os.mkfifo(fifo)
        result = subprocess.run([str(self.binary), '--cache-path', str(self.index.cache),
                                 '--budgets-file', str(fifo)], capture_output=True, timeout=2)
        self.assertEqual(result.returncode, 2)


if __name__ == '__main__':
    unittest.main()
