"""Exercise the production range, export and settings state without AppKit or real user data."""
from tools.common.paths import macos_source
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from tests.macos.test_floating_period import declaration


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class MainStateTests(unittest.TestCase):
    def test_range_export_and_latest_settings(self):
        harness = r'''import Foundation
let generation = UUID()
func identity(_ model: String, source: String = "/fixture") -> MainUsageIdentity {
    MainUsageIdentity(source: source, cache: source + "/cache", generation: generation, days: "7", model: model, task: "all", group: "model")
}
let session = MainUsageSession(), a = identity("A"), b = identity("B")
precondition(session.select(a))
precondition(session.publish(["groups": []], for: a))
let oldSerial = session.serial
precondition(session.matches(a, serial: oldSerial))
precondition(session.select(b) && session.data == nil && session.needsRead)
precondition(!session.publish(["stale": true], for: a))
session.failed()
precondition(session.needsRead && !session.matches(a, serial: oldSerial))
precondition(session.publish(["groups": []], for: b))
let exportedSerial = session.serial
precondition(session.publish(["groups": []], for: b))
precondition(!session.matches(b, serial: exportedSerial), "A refreshed snapshot invalidates prepared exports")
precondition(session.select(identity("B", source: "/other")) && session.data == nil)
precondition(session.publish(["meta": ["loading": true]], for: identity("B", source: "/other")))
precondition(session.needsRead, "An indexing placeholder must be retried even if generated_at remains unchanged")
let input: [[String: Any]] = [
    ["label": "Alpha,\"quoted\"\nname", "total_tokens": NSNumber(value: UInt64(9_007_199_254_740_993))],
    ["label": "Alpha small", "total_tokens": NSNumber(value: UInt64(9_007_199_254_740_992)), "input_tokens": 0],
    ["label": "Beta", "total_tokens": 0]
]
let selected = MainTableSelection(search: "Alpha", sortKey: "total_tokens", ascending: false)
let rows = selected.rows(input)
precondition(rows.count == 2 && rows[0]["label"] as? String == input[0]["label"] as? String)
precondition(selected.rows(input, visible: false).count == 3)
let csv = String(data: MainTableSelection.csv(rows), encoding: .utf8)!
precondition(csv.contains("9007199254740993") && csv.contains("\"Alpha,\"\"quoted\"\"\nname\""))
precondition(csv.contains("不可统计") && csv.contains(",0,"))
let queue = MainSettingsQueue()
queue.enqueue(5); precondition(queue.begin() == 5)
queue.enqueue(30); queue.enqueue(60)
precondition(queue.begin() == nil, "Never overlap backend writes")
queue.finish(error: "offline")
precondition(queue.pending == 60 && queue.error == "offline")
precondition(queue.begin() == 60)
queue.interrupt()
precondition(queue.pending == 60 && queue.inFlight == nil)
precondition(queue.begin() == 60)
queue.finish(error: nil)
precondition(!queue.hasPending && queue.error == nil)
print("range, csv and settings passed")
'''
        update = declaration(macos_source('Main.swift').read_text(), '    func reportLocalUpdate(')
        harness += r'''
typealias Object = [String: Any]
let isMainWindowProcess = true
final class RefreshFixture {
    var codexHome = URL(fileURLWithPath: "/new")
    var localUpdate: Object = ["source": "/old", "stamp": "old-success"]
    var sent: [Object] = []
    func sendHost(_ action: String, payload: Object) { sent.append(payload) }
    func updateRefreshStatus() {}
''' + update + r'''
}
let refresh = RefreshFixture()
refresh.reportLocalUpdate(busy: true)
precondition(refresh.localUpdate["stamp"] == nil && refresh.sent.count == 1)
refresh.reportLocalUpdate(busy: true)
precondition(refresh.sent.count == 1, "Unchanged status does not produce repeated IPC")
refresh.reportLocalUpdate(busy: false, stamp: "new-success")
refresh.reportLocalUpdate(busy: false, error: "offline")
precondition(refresh.localUpdate["stamp"] as? String == "new-success" && refresh.localUpdate["error"] as? String == "offline")
'''
        with tempfile.TemporaryDirectory(prefix='macos main state ') as directory:
            root = Path(directory)
            source = root / 'main.swift'
            source.write_text(harness)
            binary = root / 'check'
            result = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(source), str(macos_source('MainState.swift')), '-o', str(binary)], capture_output=True, text=True, timeout=90)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = subprocess.run([str(binary)], capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('passed', result.stdout)
