"""Differential checks of the real Windows SQLite reader against shared accounting."""
from tools.common.paths import ROOT, BACKEND, macos_source
from contextlib import closing
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import threading
import unittest

sys.path.insert(0, str(BACKEND))
from disk_index import DiskUsageIndex
from tests.common.test_dashboard import NOW
from tests.common.test_token_usage import meta, start, record, u, event, legacy
from tests.macos import test_budget_query as original_budget

EXE = os.environ.get('CODEX_USAGE_WINDOWS_EXE')


@unittest.skipUnless(os.name == 'nt' and EXE, 'Set CODEX_USAGE_WINDOWS_EXE to run the built Windows SQLite reader')
class WindowsNativeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='Windows SQLite 差分 ')
        self.home = Path(self.temp.name)
        self.index = DiskUsageIndex(self.home, self.home / 'index.sqlite')
        self.exe = Path(EXE).resolve()
        self.assertTrue(self.exe.is_file(), 'Build the Windows executable before native tests')

    def tearDown(self):
        self.temp.cleanup()

    def invoke(self, request, cache=None, success=True):
        input_path, output_path = self.home / 'native-input.json', self.home / 'native-output.json'
        input_path.write_text(json.dumps(request), encoding='utf-8')
        output_path.unlink(missing_ok=True)
        result = subprocess.run([str(self.exe), '--native-read', '--cache', str(cache or self.index.cache),
                                 '--input', str(input_path), '--output', str(output_path)],
                                env=dict(os.environ, CODEX_USAGE_DESKTOP_BASE=str(self.home / 'private-state')),
                                capture_output=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
        payload = json.loads(output_path.read_text(encoding='utf-8-sig')) if output_path.is_file() else {}
        if success:
            self.assertEqual(result.returncode, 0, payload)
            self.assertTrue(payload)
        else:
            self.assertNotEqual(result.returncode, 0, payload)
        return payload

    def native(self, choices=False, **filters):
        return self.invoke(dict(filters=filters, requests=[], choices=choices, now=NOW.timestamp()))

    def write_rows(self, name, rows):
        folder = self.home / 'sessions'
        folder.mkdir(exist_ok=True)
        path = folder / (name + '.jsonl')
        path.write_text(''.join(json.dumps(row) + '\n' for row in rows), encoding='utf-8')
        return path

    # Keep the original macOS fixture builder and accounting assertions intact.
    write = original_budget.BudgetQueryTests.write
    request = original_budget.BudgetQueryTests.request

    def run_query(self, requests=None, cache=None, expected=0):
        request = dict(filters={'days': 'all'}, requests=[self.request()] if requests is None else requests,
                       now=NOW.timestamp())
        payload = self.invoke(request, cache=cache, success=expected == 0)
        return payload['budgetResult'] if expected == 0 else payload

    test_half_open_instants_cross_offsets_midnight_and_arbitrary_hours = original_budget.BudgetQueryTests.test_half_open_instants_cross_offsets_midnight_and_arbitrary_hours
    test_fractional_boundary_and_end_exclusion = original_budget.BudgetQueryTests.test_fractional_boundary_and_end_exclusion
    test_models_grouped_and_nested_children_attributed_once = original_budget.BudgetQueryTests.test_models_grouped_and_nested_children_attributed_once
    test_optional_unknowns_preserve_counts_and_known_lower_bounds = original_budget.BudgetQueryTests.test_optional_unknowns_preserve_counts_and_known_lower_bounds
    test_missing_scope_stays_invalid_without_falling_back_to_all = original_budget.BudgetQueryTests.test_missing_scope_stays_invalid_without_falling_back_to_all
    test_empty_interval_is_zero_only_with_a_usable_existing_index = original_budget.BudgetQueryTests.test_empty_interval_is_zero_only_with_a_usable_existing_index
    test_invalid_record_time_and_global_coverage_are_visible = original_budget.BudgetQueryTests.test_invalid_record_time_and_global_coverage_are_visible
    test_only_initializer_migrates_expression_index_and_reader_never_creates_cache = original_budget.BudgetQueryTests.test_only_initializer_migrates_expression_index_and_reader_never_creates_cache

    def test_summary_matches_shared_reference_for_45_period_model_task_combinations(self):
        self.write_rows('root', [meta('root'), *start('t1'), event('turn_context', {'turn_id': 't1', 'model': 'model-a'}), record('root', 't1', 'r1')])
        count = u(200, 40)
        count['cached_input_tokens'] = None
        self.write_rows('other', [meta('other'), *start('t2'), event('turn_context', {'turn_id': 't2', 'model': 'model-b'}), record('other', 't2', 'r2', count)])
        self.write_rows('child', [meta('child', parent='root'), *start('t3'), record('child', 't3', 'r3')])
        self.write_rows('legacy', [meta('legacy'), *start('old'), legacy(u())])
        self.index.scan()
        for days in ('1', '7', '30', '90', 'all'):
            for model in ('all', 'model-a', 'model-b'):
                for task in ('all', 'root', 'other'):
                    with self.subTest(days=days, model=model, task=task):
                        actual = self.native(days=days, model=model, task=task)
                        expected = self.index.query(days=days, model=model, task=task, summary_only=True, now=NOW)
                        self.assertEqual(actual['filtered']['summary'], expected['summary'])
                        self.assertEqual(actual['filtered']['filters'], expected['filters'])
                        self.assertFalse(actual['filter_reset'])
        reset = self.native(days='all', model='deleted-model', task='deleted-task')
        self.assertTrue(reset['filter_reset'])
        self.assertEqual(reset['filtered']['summary'], self.index.query(days='all', summary_only=True, now=NOW)['summary'])

    def test_empty_summary_and_missing_cache_never_fabricate_usage(self):
        self.index.scan()
        actual = self.native(days='all')
        self.assertEqual(actual['filtered']['summary'], self.index.query(days='all', summary_only=True, now=NOW)['summary'])
        absent = self.home / 'not-created' / 'missing.sqlite'
        self.invoke(dict(filters={'days': 'all'}), cache=absent, success=False)
        self.assertFalse(absent.parent.exists())

    def test_choices_preserve_scoped_task_ids_and_read_only_cache(self):
        self.write(entries=[('2026-09-10T01:00:00Z', u(), 'model-a')])
        self.write('other', [('2026-09-10T01:00:00Z', u(), 'model-b')])
        self.index.scan()
        shutil.rmtree(self.home / 'sessions')
        with closing(sqlite3.connect(self.index.cache)) as database, database:
            snapshot = json.loads(database.execute("SELECT value FROM kv WHERE key='snapshot'").fetchone()[0])
            for row in snapshot['tasks']:
                row['label'] = '同名任务 "中文"'
            database.execute("UPDATE kv SET value=? WHERE key='snapshot'", (json.dumps(snapshot),))
        before = self.index.cache.read_bytes()
        for days in ('1', '7', '30', '90', 'all'):
            for model in ('all', 'model-a', 'model-b'):
                with self.subTest(days=days, model=model):
                    expected = self.index.query(days=days, model=model, now=NOW)['filters']
                    actual = self.native(choices=True, days=days, model=model)['choices']
                    self.assertEqual(actual['models'], expected['models'])
                    self.assertEqual(actual['tasks'], expected['tasks'])
        scoped = self.native(choices=True, days='all', model='model-a', task='other')['choices']['tasks']
        self.assertEqual([r['id'] for r in scoped], ['root', 'other'])
        self.assertEqual(scoped[0]['label'], scoped[1]['label'])
        self.assertEqual(self.index.cache.read_bytes(), before, 'Native reader changed the persisted cache')
        self.assertFalse((self.home / 'sessions').exists())

    def test_budget_batch_reads_one_snapshot_while_writer_commits(self):
        self.write()
        self.index.scan()
        stop = threading.Event()
        def writer():
            with closing(sqlite3.connect(self.index.cache)) as database:
                value = 120
                while not stop.wait(.002):
                    value = 999 if value == 120 else 120
                    with database:
                        database.execute('UPDATE usage SET total_tokens=?', (value,))
        thread = threading.Thread(target=writer)
        thread.start()
        try:
            result = self.run_query([self.request(id=str(i)) for i in range(50)])
        finally:
            stop.set()
            thread.join(timeout=3)
        values = [r['rows'][0]['total_tokens'] for r in result['results']]
        self.assertEqual(len(values), 50)
        self.assertEqual(len(set(values)), 1)
        self.assertIn(values[0], (120, 999))
        self.assertLess(len(json.dumps(result)), 2 * 1024 * 1024)

    def test_budget_rejects_invalid_rule_types_bounds_and_duplicate_ids(self):
        self.index.scan()
        invalid = [None, True, [], {}, {'id': 'missing'}, self.request(start=True), self.request(end=False),
                   self.request(revision=True), self.request(revision=1.5), self.request(revision=-1),
                   self.request(start=1e309), self.request(end=-1e309), self.request(extra=True),
                   self.request(start='123'), self.request(start=10, end=10), self.request(start=10, end=9),
                   self.request(end=1e20), self.request(id=''), self.request(id='a' * 129),
                   self.request(id='nul\x00value'), self.request(periodID='x' * 257),
                   self.request(model='m' * 513), self.request(task=[])]
        for rule in invalid:
            with self.subTest(rule=rule):
                self.run_query([rule], expected=2)
        self.run_query([self.request(), self.request()], expected=2)
        self.run_query([self.request(id=str(i)) for i in range(51)], expected=2)
        self.assertEqual(self.run_query([])['results'], [])

    def test_budget_excessive_model_groups_fail_with_bounded_error_output(self):
        self.write()
        self.index.scan()
        with closing(sqlite3.connect(self.index.cache)) as database, database:
            template = database.execute('SELECT * FROM usage').fetchone()
            columns = [row[1] for row in database.execute('PRAGMA table_info(usage)')]
            rows = []
            for number in range(1025):
                value = list(template)
                value[columns.index('response')] = f'model-{number}'
                value[columns.index('model')] = f'model-{number}'
                rows.append(value)
            database.executemany('INSERT INTO usage VALUES (' + ','.join('?' for _ in columns) + ')', rows)
        result = self.run_query(expected=75)
        self.assertLess(len(json.dumps(result)), 65536)


if __name__ == '__main__':
    unittest.main()
