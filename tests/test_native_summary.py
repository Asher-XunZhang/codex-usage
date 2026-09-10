"""Native SQLite summaries must match the existing accounting implementation."""
import json
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from disk_index import DiskUsageIndex
from test_dashboard import NOW
from test_token_usage import meta, start, record, u, event


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS C toolchain')
class NativeSummaryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory(prefix='native summary build ')
        cls.binary = Path(cls.build.name) / 'summary'
        subprocess.run(['xcrun', 'clang', '-Os', str(Path(__file__).parents[1] / 'Sources/Summary.c'), '-lsqlite3', '-o', str(cls.binary)], check=True, capture_output=True)

    @classmethod
    def tearDownClass(cls):
        cls.build.cleanup()

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix='native summary 数据 ')
        self.home = Path(self.tmp.name)
        self.index = DiskUsageIndex(self.home, self.home / 'index.sqlite')

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, name, rows):
        path = self.home / 'sessions' / (name + '.jsonl')
        path.parent.mkdir(exist_ok=True)
        path.write_text(''.join(json.dumps(row, ensure_ascii=False) + '\n' for row in rows))

    def native(self, **filters):
        args = [str(self.binary), '--cache-path', str(self.index.cache), '--now', str(int(NOW.timestamp()))]
        for key, value in filters.items():
            args += ['--' + key, value]
        result = subprocess.run(args, capture_output=True, text=True, timeout=5)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def test_matches_reference_for_periods_models_tasks_unknown_and_nulls(self):
        self.write('root', [meta('root'), *start('t1'), event('turn_context', {'turn_id': 't1', 'model': 'model-a'}), record('root', 't1', 'r1')])
        count = u(200, 40); count['cached_input_tokens'] = None
        self.write('other', [meta('other'), *start('t2'), event('turn_context', {'turn_id': 't2', 'model': 'model-b'}), record('other', 't2', 'r2', count)])
        self.write('child', [meta('child', parent='root'), *start('t3'), record('child', 't3', 'r3')])
        self.index.scan()
        for days in ['1', '7', '30', '90', 'all']:
            for model in ['all', 'model-a', 'model-b']:
                for task in ['all', 'root', 'other']:
                    with self.subTest(days=days, model=model, task=task):
                        actual = self.native(days=days, model=model, task=task)
                        expected = self.index.query(days=days, model=model, task=task, summary_only=True, now=NOW)
                        self.assertEqual(actual['filtered']['summary'], expected['summary'])
                        self.assertEqual(actual['filtered']['filters'], expected['filters'])
                        self.assertFalse(actual['filter_reset'])
        reset = self.native(days='all', model='deleted-model', task='deleted-task')
        self.assertTrue(reset['filter_reset'])
        self.assertEqual(reset['filtered']['summary'], self.index.query(days='all', summary_only=True, now=NOW)['summary'])

    def test_empty_store_and_missing_cache_never_fabricate_usage(self):
        self.index.scan()
        self.assertEqual(self.native(days='all')['filtered']['summary'], self.index.query(days='all', summary_only=True, now=NOW)['summary'])
        absent = self.home / 'absent.sqlite'
        result = subprocess.run([str(self.binary), '--cache-path', str(absent)], capture_output=True)
        self.assertEqual(result.returncode, 75)
        self.assertFalse(absent.exists())

    def test_choices_match_main_date_model_scope_without_loading_records_or_scanning(self):
        self.write('root', [meta('root'), *start('t1'), event('turn_context', {'turn_id': 't1', 'model': 'model-a'}), record('root', 't1', 'r1')])
        self.write('other', [meta('other'), *start('t2'), event('turn_context', {'turn_id': 't2', 'model': 'model-b'}), record('other', 't2', 'r2')])
        self.index.scan()
        # Existing raw logs become unavailable after the cache is committed.
        # Choice queries must continue to return the same cached IDs/titles.
        shutil.rmtree(self.home / 'sessions')
        with sqlite3.connect(self.index.cache) as db:
            stored = json.loads(db.execute("SELECT value FROM kv WHERE key='snapshot'").fetchone()[0])
            for task in stored['tasks']:
                task['label'] = '同名任务 "中文"'
            db.execute("UPDATE kv SET value=? WHERE key='snapshot'", (json.dumps(stored, ensure_ascii=False),))
            before = db.execute("SELECT value FROM kv WHERE key='snapshot'").fetchone()[0]
        for days in ['1', '7', '30', '90', 'all']:
            for model in ['all', 'model-a', 'model-b']:
                with self.subTest(days=days, model=model):
                    expected = self.index.query(days=days, model=model, now=NOW)['filters']
                    models = self.native(choices='model', days=days, model=model)
                    self.assertEqual(models, {'choices': [{'id': 'all', 'label': '全部模型'}] + [{'id': m, 'label': m} for m in expected['models']]})
                    tasks = self.native(choices='task', days=days, model=model)
                    self.assertEqual(tasks, {'choices': [{'id': 'all', 'label': '全部任务'}] + [{'id': t['id'], 'label': t['label']} for t in expected['tasks']]})
        scoped = self.native(choices='task', days='all', model='model-a', task='other')['choices']
        self.assertEqual([t['id'] for t in scoped], ['all', 'root', 'other'], 'Selected out-of-scope task remains available like the main picker')
        self.assertEqual(scoped[1]['label'], scoped[2]['label'])
        self.assertNotEqual(scoped[1]['id'], scoped[2]['id'], 'Equal names retain separate stable IDs')
        with sqlite3.connect(self.index.cache) as db:
            self.assertEqual(db.execute("SELECT value FROM kv WHERE key='snapshot'").fetchone()[0], before)
        self.assertFalse((self.home / 'sessions').exists())
        summary = self.native(days='all')
        self.assertNotIn('choices', summary)
        self.assertEqual(set(summary['filtered']['filters']), {'selected_task'}, 'Ordinary summaries must not retain complete menus')

    def test_choices_reject_invalid_kind_period_and_missing_cache(self):
        self.index.scan()
        for kind, days in [('task', 'invalid'), ('record', 'all')]:
            result = subprocess.run([str(self.binary), '--cache-path', str(self.index.cache), '--choices', kind, '--days', days], capture_output=True, timeout=5)
            self.assertNotEqual(result.returncode, 0)
        absent = self.home / 'missing.sqlite'
        result = subprocess.run([str(self.binary), '--cache-path', str(absent), '--choices', 'task'], capture_output=True, timeout=5)
        self.assertEqual(result.returncode, 75)
        self.assertFalse(absent.exists())
