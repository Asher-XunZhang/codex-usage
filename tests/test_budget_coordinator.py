"""Run the complete production coordinator against controlled native API doubles.

No AppKit/UserNotifications framework is imported, no system permission is changed,
and no notification can leave this test process. Foundation timers are inspected at
registration rather than sleeping until a real budget boundary.
"""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import unittest


ROOT = Path(__file__).parents[1]
DOUBLES = r'''
import Foundation
typealias Object = [String: Any]

final class BudgetQueryReader {
    struct Call { let cache: URL; let requests: [Object]; var completion: ((Object?, String?) -> Void)? }
    var calls: [Call] = []
    var cancelCount = 0
    func cancel() { cancelCount += 1 }
    func read(cache: URL, requests: [Object], completion: @escaping (Object?, String?) -> Void) {
        calls.append(Call(cache: cache, requests: requests, completion: completion))
    }
    func complete(_ index: Int, used: [String: Double], stamp: Double, error: String?) {
        guard calls.indices.contains(index), let callback = calls[index].completion else { return }
        calls[index].completion = nil
        let rows: [Object] = calls[index].requests.map { request in
            var result = request
            let total = used[request["id"] as? String ?? ""] ?? 95
            result["scope_valid"] = true
            result["rows"] = [["model": "fixture-model", "requests": 1, "input_tokens": total, "output_tokens": 0,
                               "total_tokens": total, "cached_input_tokens": 0, "cache_write_input_tokens": 0]]
            return result
        }
        callback(error == nil ? ["generated_at": stamp, "has_rows": true, "coverage_complete": true, "results": rows] : nil, error)
    }
}

struct QuotaWindow { let durationMinutes: Int; let remaining: Double; let resetsAt: Date? }
struct QuotaSnapshot { let updated: Date?; let stale: Bool; let error: String?; let windows: [QuotaWindow] }
protocol UNUserNotificationCenterDelegate: AnyObject {}
struct UNAuthorizationOptions: OptionSet {
    let rawValue: Int
    static let alert = Self(rawValue: 1)
    static let sound = Self(rawValue: 2)
}
struct UNNotificationPresentationOptions: OptionSet {
    let rawValue: Int
    static let banner = Self(rawValue: 1)
    static let list = Self(rawValue: 2)
    static let sound = Self(rawValue: 4)
}
struct UNNotificationAction { let identifier: String; let title: String; let options: [String] }
final class UNNotificationCategory: NSObject {
    let identifier: String
    let actions: [UNNotificationAction]
    init(identifier: String, actions: [UNNotificationAction], intentIdentifiers: [String], options: [String]) {
        self.identifier = identifier; self.actions = actions
    }
}
enum UNAuthorizationStatus: String { case authorized, denied, provisional, notDetermined }
struct UNNotificationSettings { let authorizationStatus: UNAuthorizationStatus }
final class UNMutableNotificationContent {
    var title = "", body = "", categoryIdentifier = ""
    var sound: String?
    var userInfo: Object = [:]
}
struct UNNotificationRequest { let identifier: String; let content: UNMutableNotificationContent; let trigger: String? }
struct UNNotification { let request: UNNotificationRequest }
struct UNNotificationResponse { let notification: UNNotification; let actionIdentifier: String }
let UNNotificationDefaultActionIdentifier = "default"
final class UNUserNotificationCenter {
    static let instance = UNUserNotificationCenter()
    static func current() -> UNUserNotificationCenter { instance }
    weak var delegate: UNUserNotificationCenterDelegate?
    var categories: Set<UNNotificationCategory> = []
    var authorizationRequests: [Int] = []
    var settingsCallbacks: [((UNNotificationSettings) -> Void)?] = []
    var notifications: [UNNotificationRequest] = []
    var completions: [((Error?) -> Void)?] = []
    func setNotificationCategories(_ categories: Set<UNNotificationCategory>) { self.categories = categories }
    func requestAuthorization(options: UNAuthorizationOptions, completionHandler: @escaping (Bool, Error?) -> Void) {
        authorizationRequests.append(options.rawValue); completionHandler(true, nil)
    }
    func getNotificationSettings(completionHandler: @escaping (UNNotificationSettings) -> Void) {
        settingsCallbacks.append(completionHandler)
    }
    func add(_ request: UNNotificationRequest, withCompletionHandler completionHandler: @escaping (Error?) -> Void) {
        notifications.append(request); completions.append(completionHandler)
    }
    func resolveSettings(_ index: Int, status: UNAuthorizationStatus) {
        guard settingsCallbacks.indices.contains(index), let callback = settingsCallbacks[index] else { return }
        settingsCallbacks[index] = nil; callback(UNNotificationSettings(authorizationStatus: status))
    }
    func completeAdd(_ index: Int, failed: Bool) {
        guard completions.indices.contains(index), let callback = completions[index] else { return }
        completions[index] = nil
        callback(failed ? NSError(domain: "SyntheticNotificationFailure", code: 1) : nil)
    }
}
'''
HARNESS = r'''
let url = URL(fileURLWithPath: CommandLine.arguments[1])
let host = BudgetCoordinator(url: url)
let center = UNUserNotificationCenter.current()
var source = "/fixture"
var visible: String?
var navigated: [String] = []
host.visibleBudget = { visible }
host.navigate = { navigated.append($0 ?? "") }
let commands = try JSONSerialization.jsonObject(with: FileHandle.standardInput.readDataToEndOfFile()) as! [Object]
var outputs: [Object] = []
func pump() {
    let until = Date().addingTimeInterval(0.025)
    while Date() < until { _ = RunLoop.current.run(mode: .default, before: until) }
}
func field(_ key: String) -> Any? { Mirror(reflecting: host).children.first { $0.label == key }?.value }
for command in commands {
    var item: Object = [:]
    do {
        switch command["op"] as? String ?? "" {
        case "seed": try host.store.apply(action: "save", payload: command["rule"] as! Object, source: command["source"] as? String ?? source)
        case "start":
            source = command["source"] as? String ?? source
            host.start(source: source, cache: URL(fileURLWithPath: "/fixture/index.sqlite"))
        case "apply": try host.apply(command["action"] as! String, payload: command["payload"] as? Object ?? [:])
        case "refresh": host.refresh(stamp: command["stamp"] as? String, force: command["force"] as? Bool ?? false)
        case "source":
            source = command["source"] as! String
            host.setSource(source, cache: URL(fileURLWithPath: source + "/index.sqlite"))
        case "query":
            host.reader.complete((command["index"] as? NSNumber)?.intValue ?? 0,
                                 used: command["used"] as? [String: Double] ?? [:],
                                 stamp: (command["stamp"] as? NSNumber)?.doubleValue ?? Date().timeIntervalSince1970,
                                 error: command["error"] as? String)
        case "settings": center.resolveSettings((command["index"] as? NSNumber)?.intValue ?? 0, status: UNAuthorizationStatus(rawValue: command["status"] as? String ?? "authorized")!)
        case "add": center.completeAdd((command["index"] as? NSNumber)?.intValue ?? 0, failed: command["failed"] as? Bool ?? false)
        case "visible": visible = command["id"] as? String
        case "quota":
            let windows = (command["windows"] as? [Object] ?? []).map { row in
                QuotaWindow(durationMinutes: (row["minutes"] as! NSNumber).intValue,
                            remaining: (row["remaining"] as! NSNumber).doubleValue,
                            resetsAt: (row["reset"] as? NSNumber).map { Date(timeIntervalSince1970: $0.doubleValue) })
            }
            host.updateQuota(QuotaSnapshot(updated: Date(), stale: false, error: nil, windows: windows))
        case "response":
            let content = UNMutableNotificationContent(); content.userInfo = ["ruleIDs": command["ids"] as? [String] ?? []]
            let request = UNNotificationRequest(identifier: "synthetic-action", content: content, trigger: nil)
            host.userNotificationCenter(center, didReceive: UNNotificationResponse(notification: UNNotification(request: request), actionIdentifier: command["action"] as! String), withCompletionHandler: {})
        case "present":
            let content = UNMutableNotificationContent()
            host.userNotificationCenter(center, willPresent: UNNotification(request: UNNotificationRequest(identifier: "preview", content: content, trigger: nil))) { item["presentationOptions"] = $0.rawValue }
        case "stop": host.stop()
        default: break
        }
    } catch { item["error"] = error.localizedDescription }
    pump()
    item["state"] = host.state
    item["pending"] = host.store.pendingAlerts
    item["queryCount"] = host.reader.calls.count
    item["queryCalls"] = host.reader.calls.map { ["cache": $0.cache.path, "requests": $0.requests] }
    item["querying"] = field("querying") as? Bool ?? false
    item["queued"] = field("queued") as? Bool ?? false
    // A real registered Foundation timer is observed; no timer is replaced or fired.
    if let timer = field("timer") as? Timer { item["boundary"] = timer.fireDate.timeIntervalSince1970; item["timerValid"] = timer.isValid }
    item["notifications"] = center.notifications.map { request -> Object in
        ["id": request.identifier, "title": request.content.title, "body": request.content.body,
         "sound": request.content.sound ?? NSNull() as Any, "category": request.content.categoryIdentifier,
         "userInfo": request.content.userInfo]
    }
    item["settingsCount"] = center.settingsCallbacks.count
    item["settingsPending"] = center.settingsCallbacks.compactMap { $0 }.count
    item["authorizationOptions"] = center.authorizationRequests
    item["navigated"] = navigated
    outputs.append(item)
}
host.stop()
FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: outputs, options: [.sortedKeys]))
'''


def rule(identifier='r1', **values):
    result = dict(id=identifier, revision=0, name='同名预算', kind='token', amount=100,
                  tokenMetric='total', model='all', task='all', currency='USD', fx=1,
                  prices=[], period=dict(type='day', timezone='UTC'), thresholds=[20, 10, 0], enabled=True)
    result.update(values)
    return result


def seed(item=None, **values):
    return dict(op='seed', rule=item or rule(), **values)


@unittest.skipUnless(shutil.which('xcrun'), 'Requires Foundation Swift toolchain')
class BudgetCoordinatorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory(prefix='budget coordinator build ')
        host_source = (ROOT / 'Sources/BudgetHost.swift').read_text()
        start = host_source.index('final class BudgetCoordinator:')
        end = host_source.index('\nfunc budgetDisplayNumber(', start)
        coordinator = host_source[start:end]
        harness = Path(cls.build.name) / 'main.swift'
        harness.write_text(DOUBLES + '\n' + coordinator + '\n' + HARNESS)
        cls.binary = Path(cls.build.name) / 'CoordinatorHarness'
        compiled = subprocess.run(['xcrun', 'swiftc', '-O', str(ROOT / 'Sources/BudgetCore.swift'), str(harness), '-o', str(cls.binary)], capture_output=True, text=True)
        if compiled.returncode:
            raise RuntimeError(compiled.stderr)

    @classmethod
    def tearDownClass(cls):
        cls.build.cleanup()

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='budget coordinator fixtures ')
        self.path = Path(self.directory.name) / 'state.json'

    def tearDown(self):
        self.directory.cleanup()

    def run_commands(self, *commands):
        result = subprocess.run([str(self.binary), str(self.path)], input=json.dumps(commands), text=True, capture_output=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def test_refresh_in_flight_coalesces_to_exactly_one_followup(self):
        outputs = self.run_commands(seed(), dict(op='start'), dict(op='refresh', stamp='first'),
                                    dict(op='refresh', stamp='second'), dict(op='query', index=0, used={'r1': 10}),
                                    dict(op='query', index=1, used={'r1': 30}))
        self.assertEqual(outputs[3]['queryCount'], 1)
        self.assertTrue(outputs[3]['queued'])
        self.assertEqual(outputs[4]['queryCount'], 2)
        self.assertTrue(outputs[4]['querying'])
        self.assertEqual(outputs[5]['queryCount'], 2)
        self.assertFalse(outputs[5]['querying'])
        self.assertEqual(outputs[5]['state']['summaries'][0]['used'], 30)

    def test_old_source_callback_cannot_consume_new_source_query_or_data(self):
        outputs = self.run_commands(seed(), dict(op='start'), dict(op='source', source='/other'),
                                    dict(op='apply', action='save', payload=rule('r2')),
                                    dict(op='query', index=0, used={'r1': 95}), dict(op='query', index=1, used={'r2': 40}))
        after_old = outputs[4]
        self.assertTrue(after_old['querying'])
        summaries = {row['id']: row for row in after_old['state']['summaries']}
        self.assertEqual(summaries['r1']['status'], 'source_invalid')
        self.assertIsNone(summaries['r2']['remaining'])
        summaries = {row['id']: row for row in outputs[5]['state']['summaries']}
        self.assertEqual(summaries['r2']['used'], 40)
        self.assertEqual(outputs[5]['notifications'], [])

    def test_pause_rechecks_pending_settings_callback_before_banner(self):
        outputs = self.run_commands(seed(), dict(op='start'), dict(op='query'),
                                    dict(op='apply', action='pause', payload=dict(id='r1', seconds=1800)),
                                    dict(op='settings'))
        self.assertEqual(outputs[2]['settingsPending'], 1)
        self.assertTrue(outputs[3]['state']['summaries'][0]['paused'])
        self.assertEqual(outputs[4]['notifications'], [])
        self.assertEqual(len(outputs[4]['pending']), 1, 'Paused pending threshold is retained for later re-evaluation')

    def test_disable_and_source_switch_invalidate_pending_settings(self):
        for command in [dict(op='apply', action='disable', payload=dict(id='r1', expectedRevision=1)),
                        dict(op='source', source='/other')]:
            with self.subTest(command=command):
                self.path.unlink(missing_ok=True)
                outputs = self.run_commands(seed(), dict(op='start'), dict(op='query'), command, dict(op='settings'))
                self.assertEqual(outputs[-1]['notifications'], [])

    def test_successful_ack_prevents_duplicate_banners_across_refreshes(self):
        outputs = self.run_commands(seed(), dict(op='start'), dict(op='query'), dict(op='settings'),
                                    dict(op='refresh', force=True), dict(op='query', index=1), dict(op='add'),
                                    dict(op='refresh', force=True), dict(op='query', index=2))
        self.assertEqual(len(outputs[3]['notifications']), 1)
        self.assertEqual(len(outputs[-1]['notifications']), 1)
        self.assertEqual(outputs[-1]['pending'], [])
        self.assertTrue(outputs[-1]['state']['events'][0]['acknowledged'])

    def test_same_names_preserve_distinct_ids_and_merge_one_silent_notification(self):
        outputs = self.run_commands(seed(rule('r1')), seed(rule('r2')), dict(op='start'), dict(op='query'),
                                    dict(op='settings'), dict(op='present'))
        notifications = outputs[4]['notifications']
        self.assertEqual(len(notifications), 1)
        self.assertEqual(notifications[0]['title'], '2 项预算需要关注')
        self.assertEqual(set(notifications[0]['userInfo']['ruleIDs']), {'r1', 'r2'})
        self.assertIsNone(notifications[0]['sound'])
        self.assertEqual(outputs[5]['presentationOptions'], 3, 'Presentation asks for banner/list only, never sound')

    def test_permission_request_is_alert_only_and_denial_keeps_visual_state(self):
        outputs = self.run_commands(dict(op='start'), dict(op='apply', action='save', payload=rule()),
                                    dict(op='query'), dict(op='settings', status='denied'))
        self.assertEqual(outputs[1]['authorizationOptions'], [1])
        self.assertEqual(outputs[-1]['notifications'], [])
        self.assertEqual(outputs[-1]['pending'], [])
        self.assertEqual(outputs[-1]['state']['summaries'][0]['status'], 'warning')

    def test_future_once_registers_boundary_without_external_refresh(self):
        start = time.time() + 300
        item = rule(period=dict(type='once', timezone='UTC', start=start, end=start+600))
        output = self.run_commands(seed(item), dict(op='start'))[-1]
        self.assertEqual(output['queryCount'], 0)
        self.assertEqual(output['state']['summaries'][0]['status'], 'scheduled')
        self.assertTrue(output['timerValid'])
        self.assertAlmostEqual(output['boundary'], start + 0.05, delta=0.1)

    def test_pause_expiry_registers_earlier_timer_than_period_end(self):
        now = time.time()
        item = rule(period=dict(type='interval', timezone='UTC', start=now-60, seconds=86400))
        outputs = self.run_commands(seed(item), dict(op='start'), dict(op='query'),
                                    dict(op='apply', action='pause', payload=dict(id='r1', seconds=1800)))
        final = outputs[-1]
        summary = final['state']['summaries'][0]
        self.assertTrue(summary['paused'])
        self.assertLess(summary['pausedUntil'], summary['end'])
        self.assertAlmostEqual(final['boundary'], summary['pausedUntil'] + 0.05, delta=0.1)

    def test_active_inline_budget_is_acknowledged_without_system_banner(self):
        outputs = self.run_commands(seed(), dict(op='visible', id='r1'), dict(op='start'), dict(op='query'))
        self.assertEqual(outputs[-1]['settingsCount'], 0)
        self.assertEqual(outputs[-1]['notifications'], [])
        self.assertEqual(outputs[-1]['pending'], [])
        self.assertEqual(outputs[-1]['state']['summaries'][0]['status'], 'warning')

    def test_becoming_visible_while_settings_are_pending_uses_inline_instead(self):
        outputs = self.run_commands(seed(), dict(op='start'), dict(op='query'),
                                    dict(op='visible', id='r1'), dict(op='settings'))
        self.assertEqual(outputs[-1]['notifications'], [])
        self.assertEqual(outputs[-1]['pending'], [])

    def test_quota_refresh_preserves_existing_token_summary_without_extra_query(self):
        outputs = self.run_commands(seed(), seed(rule('q1', kind='quota', amount=20, windowMinutes=300)),
                                    dict(op='start'), dict(op='query', used={'r1': 40}),
                                    dict(op='quota', windows=[dict(minutes=300, remaining=19)]))
        summaries = {row['id']: row for row in outputs[-1]['state']['summaries']}
        self.assertEqual(summaries['r1']['used'], 40)
        self.assertEqual(summaries['r1']['status'], 'healthy')
        self.assertEqual(summaries['q1']['remaining'], 19)
        self.assertEqual(outputs[-1]['queryCount'], 1)

    def test_failed_delivery_remains_pending_and_retries_on_later_evaluation(self):
        outputs = self.run_commands(seed(), dict(op='start'), dict(op='query'), dict(op='settings'),
                                    dict(op='add', failed=True), dict(op='refresh', force=True),
                                    dict(op='query', index=1), dict(op='settings', index=1), dict(op='add', index=1))
        self.assertEqual(len(outputs[4]['pending']), 1)
        self.assertEqual(len(outputs[7]['notifications']), 2)
        self.assertEqual(outputs[7]['notifications'][0]['id'], outputs[7]['notifications'][1]['id'])
        self.assertEqual(outputs[-1]['pending'], [])

    def test_notification_actions_use_rule_ids_and_only_pause_selected_rule(self):
        outputs = self.run_commands(seed(rule('r1')), seed(rule('r2')), dict(op='start'), dict(op='query'),
                                    dict(op='response', action='pause30', ids=['r2']),
                                    dict(op='response', action='default', ids=['r2']))
        summaries = {row['id']: row for row in outputs[-1]['state']['summaries']}
        self.assertFalse(summaries['r1']['paused'])
        self.assertTrue(summaries['r2']['paused'])
        self.assertEqual(outputs[-1]['navigated'], ['r2'])


if __name__ == '__main__':
    unittest.main()
