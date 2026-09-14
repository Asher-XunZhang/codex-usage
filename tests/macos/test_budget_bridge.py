"""Execute real budget bridge methods with inert AppDelegate collaborators.

The fixture captures messages/navigation/preferences in memory. It opens no app,
uses no real user defaults, and does not replace the production method bodies.
"""
from tools.common.paths import ROOT, BACKEND, macos_source
import base64
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


FIXTURE = r'''
import Foundation
typealias Object = [String: Any]
final class VisibleFixture { var isVisible = true }
final class WindowFixture {
    var mainIsRunning = true
    var sent: [Object] = []
    func sendToMain(_ action: String, payload: Object) { sent.append(["action": action, "payload": payload]) }
}
final class PageFixture {
    var updates: [Object] = []
    var navigations: [Object] = []
    func update(_ state: Object) { updates.append(state) }
    func navigate(budgetID: String?, create: Bool) { navigations.append(["id": budgetID as Any? ?? NSNull(), "create": create]) }
    func acknowledgePin(id: String, requestID: String, error: String?) {}
    func acknowledgeSave(id: String, revision: Int?, error: String?, requestID: String? = nil) {}
}
final class PreferenceFixture {
    var values: Object = [:]
    func set(_ value: Any?, forKey key: String) { values[key] = value }
}
final class CapsuleFixture { var budgetID = "original"; var budgetMode = false; var monitorMode = false }
final class StoreFixture { var rules: [Object] = [["id": "pinned-budget"]] }
final class CoordinatorFixture {
    let store = StoreFixture()
    func apply(_ action: String, payload: Object) throws {}
}
final class AppDelegate {
    var isMainWindowProcess: Bool
    let windowProcesses = WindowFixture()
    var budgetHostState: Object = [:]
    var budgetState: Object = ["models": [["id": "model-kept", "label": "Existing model"]], "tasks": [["id": "task-kept", "label": "Existing task"]]]
    var budgetPage: PageFixture? = PageFixture()
    var budgetPacketID: String?
    var budgetPackets: [Int: Data] = [:]
    var budgetPublishSequence = 0
    var budgetPacketSequence = 0
    var budgetPacketCount = 0
    var budgetPacketComplete = false
    var lastBudgetRouteID: String?
    var pendingBudgetRoute: Object?
    var budgetMainVisible = false
    var budgetMainViewing: String?
    var budgetCoordinator: CoordinatorFixture? = CoordinatorFixture()
    let usagePreferences = PreferenceFixture()
    let capsuleState = CapsuleFixture()
    var mainFilters: Object = ["days": "7", "model": "existing-main-model", "task": "existing-main-task"]
    var mainPage = "usage"
    var openedDashboard = 0
    var openedMain = 0
    var floating: VisibleFixture? = VisibleFixture()
    var shownFloating = 0
    var renderedBudget = 0
    var outgoing: [Object] = []
    init(main: Bool) { isMainWindowProcess = main }
    func showDashboard() { openedDashboard += 1 }
    func switchBudgetPage(_ value: Bool) { mainPage = value ? "budgets" : "usage" }
    func reportBudgetViewing() {}
    func openMainWindow() { openedMain += 1 }
    func showFloating() { shownFloating += 1 }
    func renderBudgetState() { renderedBudget += 1 }
    func saveBudgetDraft() {}
    func publishHostState() {}
    func sendHost(_ action: String, payload: Object) { outgoing.append(["action": action, "payload": payload]) }
}
'''
DRIVER = r'''
let commands = try JSONSerialization.jsonObject(with: FileHandle.standardInput.readDataToEndOfFile()) as! [Object]
let host = AppDelegate(main: false), main = AppDelegate(main: true)
var output: [Object] = []
for command in commands {
    let target = command["target"] as? String == "host" ? host : main
    switch command["op"] as? String ?? "" {
    case "publish":
        host.budgetHostState = command["state"] as? Object ?? [:]
        host.windowProcesses.sent = []
        host.windowProcesses.mainIsRunning = command["running"] as? Bool ?? true
        host.publishBudgetState()
    case "deliver":
        let captured = host.windowProcesses.sent
        let indexes = command["indexes"] as? [Int] ?? Array(captured.indices)
        for index in indexes where captured.indices.contains(index) {
            let message = captured[index]
            _ = main.receiveBudgetAction(message["action"] as! String, payload: message["payload"] as! Object)
        }
    case "receive":
        _ = target.receiveBudgetAction(command["action"] as? String ?? "", payload: command["payload"] as? Object ?? [:])
    case "open": target.openBudget(command["id"] as? String, create: command["create"] as? Bool == true)
    default: break
    }
    output.append([
        "sent": host.windowProcesses.sent,
        "updates": main.budgetPage?.updates.count ?? 0,
        "state": main.budgetState,
        "packetCount": main.budgetPackets.count,
        "packetBytes": main.budgetPackets.values.reduce(0) { $0 + $1.count },
        "packetSequence": main.budgetPacketSequence,
        "navigations": main.budgetPage?.navigations ?? [],
        "acks": main.outgoing,
        "mainPage": main.mainPage,
        "dashboardOpenCount": main.openedDashboard,
        "mainFilters": main.mainFilters,
        "hostFilters": host.mainFilters,
        "floatingID": host.capsuleState.budgetID,
        "floatingMode": host.capsuleState.budgetMode,
        "floatingShows": host.shownFloating,
        "hostOpenedMain": host.openedMain,
        "preferences": host.usagePreferences.values,
        "pendingRoute": host.pendingBudgetRoute as Any? ?? NSNull()
    ])
}
FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: output, options: [.sortedKeys]))
'''


def packet(data, *, identifier='fixture-packet', sequence=1, index=0, count=1):
    return dict(id=identifier, sequence=sequence, index=index, count=count,
                data=base64.b64encode(data).decode())


def receive(payload, action='budgetStatePart', target='main'):
    return dict(op='receive', target=target, action=action, payload=payload)


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS Swift toolchain')
class BudgetBridgeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory(prefix='budget bridge build ')
        source = (macos_source('BudgetHost.swift')).read_text()
        methods = []
        for name in ['receiveBudgetState', 'publishBudgetState', 'openBudget', 'sendBudgetRoute',
                     'receiveBudgetAction', 'selectFloatingBudget']:
            begin = source.index('    func ' + name + '(')
            end = source.index('\n    }\n', begin) + len('\n    }\n')
            methods.append(source[begin:end])
        harness = Path(cls.build.name) / 'main.swift'
        harness.write_text(FIXTURE + '\nextension AppDelegate {\n' + '\n'.join(methods) + '\n}\n' + DRIVER)
        cls.binary = Path(cls.build.name) / 'BridgeHarness'
        result = subprocess.run(['xcrun', 'swiftc', '-O', str(harness), '-o', str(cls.binary)], capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError(result.stderr)

    @classmethod
    def tearDownClass(cls):
        cls.build.cleanup()

    def run_commands(self, *commands):
        result = subprocess.run([str(self.binary)], input=json.dumps(commands), capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def test_large_state_round_trips_only_after_all_small_messages_arrive(self):
        state = dict(rules=[dict(id='r1', name='Large synthetic budget', priceNote='p' * 170_000)],
                     summaries=[dict(id='r1', remaining=123)], events=[])
        output = self.run_commands(dict(op='publish', state=state), dict(op='deliver', indexes=[0, 2]),
                                   dict(op='deliver', indexes=[3, 1]), dict(op='deliver'))
        messages = output[0]['sent']
        self.assertGreater(len(json.dumps(state)), 131072)
        self.assertEqual(len(messages), 4)
        self.assertTrue(all(len(json.dumps(message).encode()) < 131072 for message in messages))
        self.assertEqual([item['updates'] for item in output], [0, 0, 1, 1])
        self.assertEqual(output[2]['state']['rules'], state['rules'])
        self.assertEqual(output[2]['state']['summaries'], state['summaries'])
        self.assertEqual(output[2]['packetCount'], 0)
        self.assertEqual(output[2]['state']['models'][0]['id'], 'model-kept')
        self.assertEqual(output[2]['state']['tasks'][0]['id'], 'task-kept')

    def test_invalid_and_incomplete_chunks_do_not_apply_state(self):
        valid = packet(b'{"rules":[]}')
        invalid = [dict(valid, count=0), dict(valid, count=12), dict(valid, index=-1),
                   dict(valid, index=1), dict(valid, sequence=0), dict(valid, data='not-base64'),
                   dict(valid, data='x' * 65537), dict(valid, id=5)]
        output = self.run_commands(*(receive(value) for value in invalid),
                                   receive(packet(b'{"rules":', count=2)))
        self.assertTrue(all(row['updates'] == 0 for row in output))
        self.assertEqual(output[-1]['packetCount'], 1)
        self.assertEqual(output[-1]['packetBytes'], len(b'{"rules":'))
        bad_json = self.run_commands(receive(packet(b'not-json')))
        self.assertEqual(bad_json[-1]['updates'], 0)
        self.assertEqual(bad_json[-1]['packetCount'], 0)

    def test_inconsistent_count_cannot_replace_an_incomplete_snapshot(self):
        output = self.run_commands(receive(packet(b'{"rules":', count=2)),
                                   receive(packet(b'{"wrong":true}', count=1)),
                                   receive(packet(b'[]}', index=1, count=2)))
        self.assertEqual([row['updates'] for row in output], [0, 0, 1])
        self.assertNotIn('wrong', output[-1]['state'])
        self.assertEqual(output[-1]['state']['rules'], [])

    def test_cache_replacement_is_bounded_and_old_sequence_never_rolls_back(self):
        commands = [receive(packet(b' ' * 49152, count=11, index=i)) for i in range(10)]
        commands += [receive(packet(b'{"rules":["new"]}', identifier='new', sequence=2)),
                     receive(packet(b'{"rules":["old"]}', identifier='old', sequence=1))]
        output = self.run_commands(*commands)
        self.assertTrue(all(row['packetBytes'] <= 10 * 49152 for row in output))
        self.assertTrue(all(row['packetCount'] <= 10 for row in output))
        self.assertEqual(output[9]['packetBytes'], 10 * 49152)
        self.assertEqual(output[10]['packetBytes'], 0)
        self.assertEqual(output[-1]['updates'], 1)
        self.assertEqual(output[-1]['state']['rules'], ['new'])
        self.assertEqual(output[-1]['packetSequence'], 2)

    def test_total_snapshot_limit_is_enforced_by_sender_and_receiver(self):
        state = dict(padding='x' * 524288)
        published = self.run_commands(dict(op='publish', state=state))
        self.assertEqual(published[0]['sent'], [])
        data = json.dumps(state).encode()
        parts = [data[i:i+49152] for i in range(0, len(data), 49152)]
        self.assertEqual(len(parts), 11)
        received = self.run_commands(*(receive(packet(part, index=i, count=len(parts))) for i, part in enumerate(parts)))
        self.assertEqual(received[-1]['updates'], 0)
        self.assertEqual(received[-1]['packetCount'], 0)

    def test_route_navigates_and_acknowledges_without_repeating_same_request(self):
        route = dict(requestID='request-one', id='budget-one', create=False)
        output = self.run_commands(receive(route, action='budgetRoute'), receive(route, action='budgetRoute'),
                                   receive(dict(requestID='request-two', id='', create=True), action='budgetRoute'))
        self.assertEqual([row['dashboardOpenCount'] for row in output], [1, 1, 2])
        self.assertEqual(output[1]['mainPage'], 'budgets')
        self.assertEqual(output[1]['navigations'], [dict(id='budget-one', create=False)])
        self.assertEqual([row['payload']['requestID'] for row in output[-1]['acks']], ['request-one', 'request-one', 'request-two'])
        self.assertEqual(output[-1]['navigations'][-1], dict(id=None, create=True))
        self.assertEqual(output[-1]['mainFilters']['days'], '7')

    def test_pin_changes_floating_selection_without_navigating_or_changing_filters(self):
        output = self.run_commands(receive(dict(action='pin', payload=dict(id='pinned-budget')),
                                           action='budgetAction', target='host'))[-1]
        self.assertEqual(output['floatingID'], 'pinned-budget')
        self.assertTrue(output['floatingMode'])
        self.assertEqual(output['floatingShows'], 1)
        self.assertEqual(output['preferences'], dict(floatingBudgetID='pinned-budget', floatingBudgetMode=True, floatingMonitorMode=False))
        self.assertEqual(output['hostOpenedMain'], 0)
        self.assertEqual(output['navigations'], [])
        self.assertEqual(output['mainPage'], 'usage')
        self.assertEqual(output['hostFilters'], output['mainFilters'])
        self.assertEqual(output['hostFilters'], dict(days='7', model='existing-main-model', task='existing-main-task'))


if __name__ == '__main__':
    unittest.main()
