"""Offscreen rendering-state contract using actual host formatters and Capsule.

No windows are shown. The fixture supplies only the host snapshot, refresh setting
and real CapsuleState; the render method and Capsule presentation remain original.
"""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).parents[1]
FIXTURE = r'''
import AppKit
typealias Object = [String: Any]
final class AppDelegate {
    var budgetHostState: Object = [:]
    var autoSeconds = 60
    let capsuleState = CapsuleState()
}
'''
DRIVER = r'''
NSApplication.shared.setActivationPolicy(.prohibited)
let commands = try JSONSerialization.jsonObject(with: FileHandle.standardInput.readDataToEndOfFile()) as! [Object]
var output: [Object] = []
for command in commands {
    let host = AppDelegate()
    host.budgetHostState = command["state"] as? Object ?? [:]
    host.autoSeconds = command["autoSeconds"] as? Int ?? 60
    host.capsuleState.budgetID = command["id"] as? String ?? "budget-one"
    host.capsuleState.budgetMode = true
    let surface = CapsuleSurface(state: host.capsuleState)
    host.renderBudgetState()
    let state = host.capsuleState
    surface.setFrameSize(CapsuleSurface.large)
    surface.expansion = 1
    let expanded = surface.accessibilityValue() as? String ?? ""
    surface.expansion = 0
    surface.setFrameSize(CapsuleSurface.small)
    output.append([
        "name": state.budgetName, "used": state.budgetUsed,
        "remaining": state.budgetRemaining, "amount": state.budgetAmount,
        "amountLabel": state.budgetAmountLabel, "caption": state.budgetCaption,
        "status": state.budgetStatus, "scope": state.budgetScope,
        "period": state.budgetPeriod, "stale": state.budgetStale,
        "displayName": state.displayName, "displayTotal": state.displayTotal,
        "displayScope": state.displayScope, "displayStale": state.displayStale,
        "fraction": state.normalizedQuota.map { Double($0) } as Any? ?? NSNull(),
        "expandedAccessibility": expanded,
        "compactAccessibility": surface.accessibilityValue() as? String ?? ""
    ])
}
FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: output, options: [.sortedKeys]))
'''


def extract_function(source, name, indent=''):
    begin = source.index(indent + 'func ' + name + '(')
    closing = '\n' + indent + '}\n'
    end = source.index(closing, begin) + len(closing)
    return source[begin:end]


def summary(**values):
    return dict(id='budget-one', name='我的预算', kind='token', currency='USD', amount=100,
                used=40, remaining=60, remainingPercent=60, status='healthy', dataStatus='updated',
                message='预算剩余 60%', model='all', task='all', paused=False,
                start=1789056000, end=1789142400, updatedAt=1789099200) | values


def render(row, *, rule=None, auto_seconds=60, error=None):
    state = dict(rules=[rule or dict(id='budget-one', name='我的预算', model='all', task='all')],
                 summaries=[row])
    if error is not None:
        state['error'] = error
    return dict(state=state, autoSeconds=auto_seconds)


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS AppKit/Swift toolchain')
class BudgetRenderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory(prefix='budget capsule render ')
        host = (ROOT / 'Sources/BudgetHost.swift').read_text()
        quota = (ROOT / 'Sources/Quota.swift').read_text()
        source = FIXTURE + extract_function(host, 'budgetDisplayNumber') + extract_function(quota, 'smallClock')
        source += '\nextension AppDelegate {\n' + extract_function(host, 'renderBudgetState', '    ') + '\n}\n' + DRIVER
        harness = Path(cls.build.name) / 'main.swift'
        harness.write_text(source)
        cls.binary = Path(cls.build.name) / 'BudgetRenderHarness'
        result = subprocess.run(['xcrun', 'swiftc', '-O', str(ROOT / 'Sources/Capsule.swift'), str(harness),
                                 '-o', str(cls.binary)], capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError(result.stderr)

    @classmethod
    def tearDownClass(cls):
        cls.build.cleanup()

    def render(self, *commands):
        result = subprocess.run([str(self.binary)], input=json.dumps(commands), capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def test_official_floor_is_distinct_from_full_window_and_token_refresh(self):
        result = self.render(render(summary(kind='quota', amount=100, quotaFloor=20, used=40, remaining=60,
                                             remainingPercent=60, windowMinutes=10080,
                                             message='官方额度剩余 60%'), auto_seconds=0))[0]
        self.assertEqual(result['amountLabel'], '提醒下限')
        self.assertEqual(result['amount'], '20%')
        self.assertEqual(result['remaining'], '60%')
        self.assertEqual(result['used'], '40%')
        self.assertEqual(result['caption'], '窗口已用')
        self.assertEqual(result['displayScope'], '窗口已用')
        self.assertEqual(result['displayTotal'], '40%')
        self.assertAlmostEqual(result['fraction'], 0.6)
        self.assertEqual(result['scope'], '账号级 · 10080 分钟官方窗口')
        self.assertNotIn('自动更新暂停', result['status'])
        self.assertFalse(result['stale'])
        self.assertIn('已用 40%', result['expandedAccessibility'])
        self.assertIn('剩余 60%', result['compactAccessibility'])

    def test_money_places_currency_symbol_before_compact_used_amount(self):
        dollars, yuan = self.render(render(summary(kind='money', currency='USD', used=20)),
                                    render(summary(kind='money', currency='CNY', used=1500)))
        self.assertEqual(dollars['used'], '$20')
        self.assertEqual(yuan['used'], '¥1.5K')
        self.assertEqual(dollars['displayTotal'], '$20')
        self.assertEqual(yuan['displayTotal'], '¥1.5K')
        self.assertEqual(dollars['amountLabel'], '限额')
        self.assertEqual(yuan['caption'], '预算已用')

    def test_deferred_scope_uses_current_summary_instead_of_next_cycle_rule(self):
        result = self.render(render(summary(model='current-model', task='current-task', scheduledChange=True),
                                    rule=dict(id='budget-one', name='我的预算', model='next-model', task='next-task')))[0]
        self.assertEqual(result['scope'], 'current-model · current-task')
        self.assertNotIn('next-', result['expandedAccessibility'])
        self.assertIn('current-model · current-task', result['compactAccessibility'])

    def test_unknown_and_partial_keep_unconfirmed_ring_empty(self):
        for status, caption in [('unknown', '待更新'), ('partial', '部分数据')]:
            with self.subTest(status=status):
                result = self.render(render(summary(status=status, dataStatus=status, used=None, remaining=None,
                                                     remainingPercent=None, message='剩余额度无法确认')))[0]
                self.assertEqual(result['used'], '—')
                self.assertEqual(result['remaining'], '— Token')
                self.assertIsNone(result['fraction'])
                self.assertEqual(result['displayScope'], caption)
                self.assertTrue(result['displayStale'])
                self.assertIn('剩余额度无法确认', result['expandedAccessibility'])

    def test_paused_and_auto_off_are_both_visible_without_changing_usage(self):
        result = self.render(render(summary(paused=True), auto_seconds=0))[0]
        self.assertTrue(result['status'].startswith('自动更新暂停 · 提醒已暂停 · '))
        self.assertEqual(result['used'], '40')
        self.assertEqual(result['remaining'], '60 Token')
        self.assertAlmostEqual(result['fraction'], 0.6)
        self.assertIn('提醒已暂停', result['compactAccessibility'])

    def test_query_error_is_visible_and_marks_display_stale(self):
        result = self.render(render(summary(), error='预算查询暂未完成'))[0]
        self.assertEqual(result['status'], '预算查询暂未完成')
        self.assertTrue(result['stale'])
        self.assertTrue(result['displayStale'])


if __name__ == '__main__':
    unittest.main()
