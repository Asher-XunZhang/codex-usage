"""Behavioral tests compile Foundation-only BudgetCore and exercise persisted JSON commands."""
from tools.common.paths import ROOT, BACKEND, macos_source
import json
from datetime import datetime, timezone
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


SOURCE = macos_source('BudgetCore.swift')
HARNESS = r'''
import Foundation
let arguments = CommandLine.arguments
let url = URL(fileURLWithPath: arguments[1])
var store = BudgetStore(url: url)
let commands = try JSONSerialization.jsonObject(with: FileHandle.standardInput.readDataToEndOfFile()) as! [[String: Any]]
var output: [[String: Any]] = []
for command in commands {
    let date = Date(timeIntervalSince1970: (command["now"] as? NSNumber)?.doubleValue ?? 0)
    let source = command["source"] as? String ?? "/fixture"
    var item: [String: Any] = [:]
    do {
        switch command["op"] as? String ?? "" {
        case "apply": try store.apply(action: command["action"] as! String, payload: command["payload"] as? [String: Any] ?? [:], source: source, now: date)
        case "evaluate": item["alerts"] = store.evaluate(result: command["result"] as? [String: Any], quota: command["quota"] as? [String: Any], source: source, now: date)
        case "reload": store = BudgetStore(url: url)
        default: break
        }
    } catch { item["error"] = error.localizedDescription }
    item["rules"] = store.rules; item["summaries"] = store.summaries
    item["requests"] = store.requests(source: source, now: date)
    item["pending"] = store.pendingAlerts
    item["persistenceError"] = store.persistenceError ?? NSNull() as Any
    output.append(item)
}
let data = try JSONSerialization.data(withJSONObject: output, options: [.sortedKeys])
FileHandle.standardOutput.write(data)
'''


def epoch(value):
    return datetime.fromisoformat(value.replace('Z', '+00:00')).timestamp()


NOW = epoch('2026-09-11T12:00:00Z')


def rule(kind='token', **values):
    result = dict(id='r1', revision=0, name='日常开发', kind=kind, amount=100,
                  tokenMetric='total', model='all', task='all', currency='USD', fx=1,
                  prices=[], period=dict(type='day', timezone='UTC'),
                  thresholds=[20, 10, 0], enabled=True)
    result.update(values)
    return result


def apply(action='save', payload=None, **values):
    return dict(op='apply', action=action, payload=payload or rule(), now=NOW, **values)


def usage(request, used, generated=NOW, *, complete=True, rows=None, has_rows=True):
    row = dict(request, scope_valid=True, rows=rows if rows is not None else [dict(
        model='m', requests=1, input_tokens=used-10, output_tokens=10,
        total_tokens=used, cached_input_tokens=0, reasoning_output_tokens=0,
        cache_write_input_tokens=0)])
    return dict(generated_at=generated, has_rows=has_rows, coverage_complete=complete, results=[row])


@unittest.skipUnless(shutil.which('xcrun'), 'Requires Foundation Swift toolchain')
class BudgetCoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory(prefix='budget core build ')
        harness = Path(cls.build.name) / 'main.swift'
        harness.write_text(HARNESS)
        cls.binary = Path(cls.build.name) / 'BudgetHarness'
        result = subprocess.run(['xcrun', 'swiftc', '-O', str(SOURCE), str(harness), '-o', str(cls.binary)], capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError(result.stderr)

    @classmethod
    def tearDownClass(cls):
        cls.build.cleanup()

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='预算 persistence ')
        self.path = Path(self.directory.name) / 'budgets.json'

    def tearDown(self):
        self.directory.cleanup()

    def run_commands(self, *commands, path=None):
        completed = subprocess.run([str(self.binary), str(path or self.path)], input=json.dumps(commands), text=True, capture_output=True, timeout=10)
        self.assertEqual(completed.returncode, 0, completed.stderr)
        return json.loads(completed.stdout)

    def create(self, item=None, now=NOW):
        response = self.run_commands(dict(op='apply', action='save', payload=item or rule(), now=now))[0]
        self.assertNotIn('error', response)
        return response['requests'][0] if response['requests'] else response

    def test_corrupt_config_recovery_preserves_bytes_and_requires_confirmation(self):
        damaged = b'{ damaged budget document\n\x00'
        self.path.write_bytes(damaged)
        responses = self.run_commands(apply(), apply('recover', {'confirm': False}))
        self.assertTrue(all('error' in item for item in responses))
        self.assertEqual(self.path.read_bytes(), damaged)
        self.assertEqual(list(self.path.parent.glob('*.damaged-*')), [])
        response = self.run_commands(apply('recover', {'confirm': True}))[0]
        self.assertNotIn('error', response)
        backups = list(self.path.parent.glob('*.damaged-*'))
        self.assertEqual(len(backups), 1)
        self.assertEqual(backups[0].read_bytes(), damaged)
        self.assertEqual(json.loads(self.path.read_text())['rules'], [])
        self.create()

    def evaluate(self, result=None, now=NOW, **values):
        return self.run_commands(dict(op='evaluate', result=result, now=now, **values))[0]

    def test_exact_threshold_jump_ack_restart_and_late_record_correction(self):
        request = self.create()
        result = self.evaluate(usage(request, 95))
        self.assertEqual([item['threshold'] for item in result['alerts']], [10])
        alert_id = result['alerts'][0]['id']
        restarted = self.run_commands(dict(op='reload', now=NOW), dict(op='evaluate', now=NOW))[1]
        self.assertEqual(restarted['alerts'][0]['id'], alert_id)
        self.run_commands(apply('acknowledge', {'ids': [alert_id]}))
        self.assertEqual(self.evaluate(usage(request, 95))['alerts'], [])
        self.assertEqual(self.evaluate(usage(request, 60, generated=NOW + 1), now=NOW + 1)['alerts'], [])
        self.assertEqual(self.evaluate(usage(request, 85, generated=NOW + 2), now=NOW + 2)['alerts'], [])
        result = self.evaluate(usage(request, 100, generated=NOW + 3), now=NOW + 3)
        self.assertEqual([item['threshold'] for item in result['alerts']], [0])
        self.assertEqual(result['summaries'][0]['status'], 'exhausted')

    def test_pause_resume_does_not_consume_thresholds_or_stop_evaluation(self):
        request = self.create()
        self.run_commands(apply('pause', {'id': 'r1'}))
        result = self.evaluate(usage(request, 101))
        self.assertTrue(result['summaries'][0]['paused'])
        self.assertEqual(result['summaries'][0]['used'], 101)
        self.assertEqual(result['alerts'], [])
        resumed = self.evaluate(usage(request, 101, generated=NOW + 1801), now=NOW + 1801)
        self.assertEqual([item['threshold'] for item in resumed['alerts']], [0])

    def test_revision_and_query_identity_reject_stale_scope_and_old_period(self):
        request = self.create()
        changed = rule(revision=1, amount=200)
        response = self.run_commands(apply(payload=changed), apply(payload=changed))
        self.assertEqual(response[0]['rules'][0]['revision'], 2)
        self.assertIn('另一界面', response[1]['error'])
        stale = self.evaluate(usage(request, 200))
        self.assertIsNone(stale['summaries'][0]['remaining'])
        current = stale['requests'][0]
        valid = self.evaluate(usage(current, 50))
        self.assertEqual(valid['summaries'][0]['remaining'], 150)
        tomorrow = NOW + 86400
        rollover = self.evaluate(usage(current, 200, generated=tomorrow), now=tomorrow)
        self.assertEqual(rollover['summaries'][0]['status'], 'unknown')
        self.assertNotEqual(rollover['requests'][0]['periodID'], current['periodID'])
        self.assertEqual(rollover['alerts'], [])

    def test_nil_results_preserve_same_cycle_values_and_source_changes_invalidate(self):
        request = self.create()
        self.evaluate(usage(request, 50))
        unchanged = self.evaluate(now=NOW+60)
        self.assertEqual(unchanged['summaries'][0]['used'], 50)
        switched = self.evaluate(source='/another', now=NOW+60)
        self.assertEqual(switched['requests'], [])
        self.assertEqual(switched['summaries'][0]['status'], 'source_invalid')
        self.assertIsNone(switched['summaries'][0]['remaining'])
        modified = self.run_commands(apply(payload=rule(revision=1), source='/another'))[0]
        self.assertIn('数据目录不同', modified['error'])

    def test_old_generated_result_never_overwrites_latest_summary(self):
        request = self.create()
        self.evaluate(usage(request, 50, generated=NOW+10), now=NOW+10)
        old = self.evaluate(usage(request, 99), now=NOW+11)
        self.assertEqual(old['summaries'][0]['used'], 50)
        self.assertEqual(old['alerts'], [])

    def test_partial_lower_bound_exceeds_but_never_fabricates_remaining(self):
        request = self.create()
        row = dict(model='m', requests=2, total_tokens=None, known_total_tokens=1, lower_bound_total_tokens=50)
        result = self.evaluate(usage(request, 0, rows=[row], complete=False))
        self.assertEqual(result['summaries'][0]['used'], 50)
        self.assertEqual(result['summaries'][0]['status'], 'partial')
        self.assertIsNone(result['summaries'][0]['remaining'])
        row['lower_bound_total_tokens'] = 110
        exceeded = self.evaluate(usage(request, 0, rows=[row], complete=False))
        self.assertEqual(exceeded['summaries'][0]['status'], 'exceeded')
        self.assertEqual(len(exceeded['alerts']), 1)
        empty = self.evaluate(usage(request, 0, has_rows=False))
        self.assertEqual(empty['summaries'][0]['status'], 'unknown')
        self.assertEqual(empty['alerts'], [])

    def test_metrics_do_not_double_count_cached_input_or_reasoning(self):
        rows = [dict(model='m', requests=1, input_tokens=80, output_tokens=20, total_tokens=100,
                     cached_input_tokens=60, reasoning_output_tokens=15, cache_write_input_tokens=0)]
        for metric, expected in [('total', 100), ('noncached', 40), ('output', 20)]:
            with self.subTest(metric=metric):
                path = Path(self.directory.name) / (metric + '.json')
                request = self.run_commands(apply(payload=rule(tokenMetric=metric)), path=path)[0]['requests'][0]
                result = self.run_commands(dict(op='evaluate', result=usage(request, 0, rows=rows), now=NOW), path=path)[0]
                self.assertEqual(result['summaries'][0]['used'], expected)

    def test_money_model_price_cache_fx_and_unknown_models(self):
        prices = [dict(model='m', input=2, cachedInput=0.5, output=10)]
        request = self.create(rule(kind='money', currency='CNY', fx=7, prices=prices, amount=100))
        rows = [dict(model='m', requests=1, input_tokens=1_000_000, output_tokens=100_000,
                     total_tokens=1_100_000, cached_input_tokens=800_000, cache_write_input_tokens=0)]
        result = self.evaluate(usage(request, 0, rows=rows))
        self.assertAlmostEqual(result['summaries'][0]['used'], 12.6)
        self.assertTrue(result['summaries'][0]['estimated'])
        rows.append(dict(rows[0], model='unknown'))
        result = self.evaluate(usage(request, 0, rows=rows))
        self.assertEqual(result['summaries'][0]['status'], 'partial')
        self.assertAlmostEqual(result['summaries'][0]['used'], 12.6)
        self.assertIsNone(result['summaries'][0]['remaining'])
        diagnostic = result['summaries'][0]['priceDiagnostics'][0]
        self.assertEqual(diagnostic['model'], 'unknown')
        self.assertEqual(set(diagnostic['missingPrices']), {'input', 'cachedInput', 'output'})
        self.assertFalse(diagnostic['cacheClassificationUnknown'])
        self.assertTrue(result['summaries'][0]['knownAmountIsLowerBound'])

    def test_missing_cache_split_cannot_be_treated_as_full_price_input(self):
        prices = [dict(model='m', input=2, cachedInput=0.5, output=10)]
        request = self.create(rule(kind='money', prices=prices))
        rows = [dict(model='m', requests=1, input_tokens=1_000_000, output_tokens=100_000,
                     total_tokens=1_100_000, cached_input_tokens=None, known_cached_input_tokens=0)]
        result = self.evaluate(usage(request, 0, rows=rows))
        self.assertEqual(result['summaries'][0]['used'], 1)
        self.assertEqual(result['summaries'][0]['status'], 'partial')

    def test_official_floor_is_selected_window_and_freshness_checked(self):
        self.create(rule(kind='quota', amount=20, windowMinutes=10080))
        quota = dict(updated_at=NOW, stale=False, windows=[dict(duration_minutes=300, remaining=3), dict(duration_minutes=10080, remaining=21)])
        result = self.evaluate(quota=quota)
        self.assertEqual(result['summaries'][0]['remaining'], 21)
        self.assertEqual(result['alerts'], [])
        quota['windows'][1]['remaining'] = 20
        result = self.evaluate(quota=quota)
        self.assertEqual([item['threshold'] for item in result['alerts']], [20])
        self.assertEqual(result['summaries'][0]['amount'], 100)
        stale = self.evaluate(quota=quota, now=NOW+181)
        self.assertEqual(stale['summaries'][0]['status'], 'unknown')
        self.assertEqual(stale['alerts'], [])
        rejected = self.run_commands(apply(payload=rule(id='q2', kind='quota', quotaCondition='consumption')))[0]
        self.assertIn('稳定账号与窗口身份', rejected['error'])

    def test_calendar_periods_exact_midnight_week_and_month_end(self):
        for spec, date, start, end in [
            (dict(type='day', timezone='Asia/Shanghai'), '2026-09-11T16:00:00Z', '2026-09-11T16:00:00Z', '2026-09-12T16:00:00Z'),
            (dict(type='week', timezone='UTC', weekday=2, hour=9), '2026-09-11T12:00:00Z', '2026-09-07T09:00:00Z', '2026-09-14T09:00:00Z'),
            (dict(type='month', timezone='UTC', day=31), '2026-02-28T00:00:00Z', '2026-02-28T00:00:00Z', '2026-03-31T00:00:00Z'),
            (dict(type='month', timezone='UTC', day=31), '2026-02-27T23:59:59Z', '2026-01-31T00:00:00Z', '2026-02-28T00:00:00Z'),
        ]:
            with self.subTest(spec=spec, date=date):
                path = Path(self.directory.name) / f'{epoch(date)}.json'
                command = dict(op='apply', action='save', payload=rule(period=spec), now=epoch(date))
                request = self.run_commands(command, path=path)[0]['requests'][0]
                self.assertEqual(request['start'], epoch(start))
                self.assertEqual(request['end'], epoch(end))

    def test_dst_uses_local_calendar_and_first_repeated_wall_time(self):
        for day, hours in [('2026-03-08T12:00:00Z', 23), ('2026-11-01T12:00:00Z', 25)]:
            path = Path(self.directory.name) / f'{hours}.json'
            item = rule(period=dict(type='day', timezone='America/New_York'))
            request = self.run_commands(dict(op='apply', action='save', payload=item, now=epoch(day)), path=path)[0]['requests'][0]
            self.assertEqual(request['end'] - request['start'], hours * 3600)
        path = Path(self.directory.name) / 'repeat.json'
        item = rule(period=dict(type='day', timezone='America/New_York', hour=1, minute=30))
        request = self.run_commands(dict(op='apply', action='save', payload=item, now=epoch('2026-11-01T06:15:00Z')), path=path)[0]['requests'][0]
        self.assertEqual(request['start'], epoch('2026-11-01T05:30:00Z'))

    def test_once_and_fixed_interval_have_nonoverlapping_boundaries(self):
        item = rule(period=dict(type='once', timezone='UTC', start=NOW+3600, end=NOW+7200))
        self.create(item)
        self.assertEqual(self.evaluate()['summaries'][0]['status'], 'scheduled')
        active = self.evaluate(now=NOW+3600)
        request = active['requests'][0]
        ended = self.evaluate(usage(request, 101, generated=NOW+7200), now=NOW+7200)
        self.assertEqual(ended['summaries'][0]['status'], 'ended')
        self.assertEqual(ended['alerts'], [])
        other = Path(self.directory.name) / 'interval.json'
        item = rule(period=dict(type='interval', timezone='America/New_York', start=NOW, seconds=3600))
        outputs = self.run_commands(apply(payload=item), dict(op='inspect', now=NOW+3600), path=other)
        self.assertEqual(outputs[0]['requests'][0]['end'], outputs[1]['requests'][0]['start'])

    def test_structural_and_price_edits_default_next_cycle_but_explicit_recompute_works(self):
        self.create()
        changed = rule(revision=1, model='m')
        result = self.run_commands(apply(payload=changed))[0]
        self.assertEqual(result['rules'][0]['model'], 'm')
        self.assertEqual(result['requests'][0]['model'], 'all')
        next_day = self.run_commands(dict(op='inspect', now=NOW+86400))[0]
        self.assertEqual(next_day['requests'][0]['model'], 'm')
        changed['revision'] = 2
        result = self.run_commands(apply(payload=dict(rule=changed, recalculateCurrent=True)))[0]
        self.assertEqual(result['requests'][0]['model'], 'm')

    def test_corrupt_file_is_not_overwritten_and_failed_atomic_save_rolls_back(self):
        self.path.write_text('{broken')
        result = self.run_commands(apply())[0]
        self.assertIsNotNone(result['persistenceError'])
        self.assertEqual(self.path.read_text(), '{broken')
        parent_file = Path(self.directory.name) / 'not-directory'
        parent_file.write_text('retain')
        result = self.run_commands(apply(), path=parent_file / 'budgets.json')[0]
        self.assertIn('error', result)
        self.assertEqual(result['rules'], [])
        self.assertEqual(parent_file.read_text(), 'retain')

    def test_new_cycle_cannot_turn_an_old_index_into_full_remaining_budget(self):
        self.create()
        next_cycle = self.run_commands(dict(op='inspect', now=NOW + 86400))[0]['requests'][0]
        result = self.evaluate(usage(next_cycle, 0, rows=[], generated=NOW), now=NOW + 86400)
        self.assertEqual(result['summaries'][0]['status'], 'unknown')
        self.assertIsNone(result['summaries'][0]['remaining'])

    def test_pending_corrected_alert_is_not_delivered_and_nil_results_preserve_latest_value(self):
        request = self.create()
        first = self.evaluate(usage(request, 95))
        self.assertEqual(first['alerts'][0]['threshold'], 10)
        corrected = self.evaluate(usage(request, 85, generated=NOW + 1), now=NOW + 1)
        self.assertEqual(corrected['alerts'], [])
        restored = self.evaluate(usage(request, 96, generated=NOW + 2), now=NOW + 2)
        self.assertEqual(restored['alerts'][0]['remainingPercent'], 4)

    def test_unknown_price_fields_and_cache_write_are_never_reported_complete(self):
        request = self.create(rule(kind='money', prices=[dict(model='m', output=10)]))
        rows = [dict(model='m', requests=1, input_tokens=1_000_000, output_tokens=100_000,
                     total_tokens=1_100_000, cached_input_tokens=0, cache_write_input_tokens=0)]
        result = self.evaluate(usage(request, 0, rows=rows))
        self.assertEqual(result['summaries'][0]['status'], 'partial')
        self.assertEqual(result['summaries'][0]['used'], 1)
        rows[0]['cache_write_input_tokens'] = None
        revised = rule(kind='money', revision=1, prices=[dict(model='m', input=2, cachedInput=0, output=10)])
        self.run_commands(apply(payload=dict(rule=revised, recalculateCurrent=True)))
        request['revision'] = 2
        result = self.evaluate(usage(request, 0, rows=rows))
        self.assertEqual(result['summaries'][0]['status'], 'partial')
        self.assertEqual(result['summaries'][0]['used'], 1)

    def test_official_reset_starts_new_alert_cycle_without_changing_monitor_period(self):
        self.create(rule(kind='quota', amount=0, windowMinutes=300))
        quota = dict(updated_at=NOW, stale=False, windows=[dict(duration_minutes=300, remaining=0, resets_at=NOW + 60)])
        result = self.evaluate(quota=quota)
        first = result['alerts'][0]
        self.run_commands(apply('acknowledge', dict(ids=[first['id']])))
        quota['updated_at'] = NOW + 100
        quota['windows'][0]['resets_at'] = NOW + 18100
        result = self.evaluate(quota=quota, now=NOW + 100)
        self.assertNotEqual(result['alerts'][0]['id'], first['id'])
        self.assertEqual(result['alerts'][0]['periodID'], first['periodID'])

    def test_maximum_current_cycle_thresholds_survive_bounded_history_pruning(self):
        commands = [apply(payload=rule(id=f'r{i}', thresholds=list(range(10)))) for i in range(50)]
        requests = self.run_commands(*commands)[-1]['requests']
        rows = [dict(request, scope_valid=True, rows=[dict(model='m', requests=1, total_tokens=100)]) for request in requests]
        data = dict(generated_at=NOW, has_rows=True, coverage_complete=True, results=rows)
        result = self.evaluate(data)
        self.assertEqual(len(result['alerts']), 50)
        self.run_commands(apply('acknowledge', dict(ids=[alert['id'] for alert in result['alerts']])))
        self.assertEqual(self.evaluate(data)['alerts'], [])
        state = json.loads(self.path.read_text())
        self.assertEqual(len(state['ledger']), 500)
        for step in (1, 2):
            now = NOW + step * 86400
            requests = self.run_commands(dict(op='inspect', now=now))[0]['requests']
            data = dict(generated_at=now, has_rows=True, coverage_complete=True,
                        results=[dict(request, scope_valid=True, rows=[dict(model='m', requests=1, total_tokens=100)]) for request in requests])
            self.assertEqual(len(self.evaluate(data, now=now)['alerts']), 50)
        self.assertEqual(len(json.loads(self.path.read_text())['ledger']), 1000)

    def test_restored_rules_are_canonicalized_and_invalid_deferred_state_is_protected(self):
        self.create()
        saved = json.loads(self.path.read_text())
        del saved['rules'][0]['currency']
        self.path.write_text(json.dumps(saved))
        result = self.evaluate()
        self.assertEqual(result['rules'][0]['currency'], 'USD')
        self.assertEqual(result['summaries'][0]['status'], 'unknown')
        saved = json.loads(self.path.read_text())
        saved['runtime']['r1'] = dict(deferredUntil=NOW + 1000, deferred=dict(period=dict(type='once', timezone='UTC')))
        self.path.write_text(json.dumps(saved))
        result = self.evaluate()
        self.assertIsNotNone(result['persistenceError'])
        self.assertEqual(json.loads(self.path.read_text())['runtime']['r1'], saved['runtime']['r1'])

    def test_fractional_percentage_boundary_is_not_lost_to_binary_rounding(self):
        request = self.create(rule(amount=0.1))
        result = self.evaluate(usage(request, 0.08))
        self.assertEqual(result['summaries'][0]['status'], 'warning')
        self.assertEqual([alert['threshold'] for alert in result['alerts']], [20])

    def test_validation_does_not_accept_nan_invalid_tz_or_duplicate_nodes(self):
        for values in [dict(amount=0), dict(amount=-1), dict(period=dict(type='day', timezone='wrong-zone')),
                       dict(thresholds=[20, 20]), dict(period=dict(type='once', timezone='UTC', start=10, end=9)),
                       dict(prices=[dict(model='m', input=-1, cachedInput=0, output=1)])]:
            with self.subTest(values=values):
                response = self.run_commands(apply(payload=rule(**values)))[0]
                self.assertIn('error', response)
                self.assertEqual(response['rules'], [])


if __name__ == '__main__':
    unittest.main()
