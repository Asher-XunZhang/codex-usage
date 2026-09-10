"""Exercise actual refresh completion without launching the app or its backend."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from test_floating_period import declaration


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class RefreshFeedbackTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='refresh feedback ')
        cls.addClassCleanup(cls.directory.cleanup)
        root = Path(cls.directory.name)
        production = (Path(__file__).parents[1] / 'Sources' / 'Main.swift').read_text()
        method = declaration(production, '    func refreshPresented(_ json: Object)')
        source = root / 'main.swift'
        source.write_text(r'''import Foundation
typealias Object = [String: Any]
func check(_ value: @autoclosure () -> Bool, _ reason: String) { precondition(value(), reason) }
final class FixtureState { var status = "waiting", indicator = "busy" }
final class FixtureButton { var toolTip: String? = "waiting" }
final class FixtureItem { var button: FixtureButton? = FixtureButton() }
final class FixtureBackend {
    let root: URL
    init(_ root: URL) { self.root = root }
}
final class Fixture {
    let backend: FixtureBackend, capsuleState = FixtureState()
    var statusItem: FixtureItem? = FixtureItem()
    var pendingRefresh: Int?, manualBeganAt: Date?, manualExpectedStamp: String?
    var statusText = "initial status", intervalDescription = "自动刷新 30 秒", compactMode = true
    var reports = 0, lastReport = ""
    init(_ root: URL) { backend = FixtureBackend(root) }
    func reportHelperRefresh(success: Bool, message: String) {
        check(success, "Presented feedback reports success")
        reports += 1; lastReport = message
    }
''' + method + r'''
    var eventsURL: URL { backend.root.appendingPathComponent("logs/refresh-events.jsonl") }
    func events() -> [Object] {
        guard let data = try? Data(contentsOf: eventsURL) else { return [] }
        return data.split(separator: 10).map { try! JSONSerialization.jsonObject(with: Data($0)) as! Object }
    }
    func begin() {
        manualBeganAt = Date().addingTimeInterval(-0.25)
        manualExpectedStamp = "2026-09-11T12:00:01+08:00"
    }
}
func snapshot(_ stamp: String) -> Object { ["meta": ["generated_at": stamp]] }
let current = snapshot("2026-09-11T12:00:01+08:00")
let fixture = Fixture(URL(fileURLWithPath: CommandLine.arguments[2], isDirectory: true))
switch CommandLine.arguments[1] {
case "repeated":
    for index in 0..<300 {
        fixture.compactMode = index % 2 == 0
        fixture.intervalDescription = fixture.compactMode ? "自动刷新 30 秒" : "自动刷新已关闭"
        fixture.begin()
        fixture.refreshPresented(current)
        check(fixture.manualBeganAt == nil && fixture.manualExpectedStamp == nil, "A confirmed request is consumed once")
        check(fixture.reports == index + 1, "Every new valid manual request is acknowledged")
        let feedback = fixture.capsuleState.status
        check(feedback.hasPrefix("已核对日志 · ") && feedback.hasSuffix(" 秒"), "Elapsed feedback remains visible")
        check(fixture.statusText == feedback + " · " + fixture.intervalDescription, "Only current feedback and current interval are retained")
        check(fixture.statusText.components(separatedBy: "已核对日志").count == 2, "Status contains exactly one completion message")
        check(fixture.statusText.utf8.count < 128, "Repeated completions cannot grow status indefinitely")
        check(fixture.lastReport == feedback && fixture.capsuleState.indicator == "check", "Helper and capsule reflect the confirmation")
        check(fixture.statusItem?.button?.toolTip == feedback + " · 双击打开主面板", "The menu bar exposes the same feedback")
        let before = try! Data(contentsOf: fixture.eventsURL)
        fixture.refreshPresented(current)
        check(fixture.reports == index + 1 && (try! Data(contentsOf: fixture.eventsURL)) == before, "Duplicate snapshots do not duplicate acknowledgements or logs")
    }
    let events = fixture.events()
    check(events.count == 300, "Bounded display text must not discard the per-refresh event history")
    for (index, event) in events.enumerated() {
        check(event["event"] as? String == "manual_refresh_presented", "Every line is a refresh event")
        check(event["mode"] as? String == (index % 2 == 0 ? "compact" : "main"), "Each event preserves its presentation mode")
        check((event["elapsed_ms"] as? Int ?? -1) >= 0, "Elapsed time remains recorded")
    }
case "guards":
    fixture.begin()
    func remainsPending(_ payload: Object, _ reason: String) {
        let began = fixture.manualBeganAt, expected = fixture.manualExpectedStamp
        fixture.refreshPresented(payload)
        check(fixture.manualBeganAt == began && fixture.manualExpectedStamp == expected, reason + ": keep request bookkeeping")
        check(fixture.statusText == "initial status" && fixture.capsuleState.status == "waiting" && fixture.capsuleState.indicator == "busy", reason + ": no early visible success")
        check(fixture.statusItem?.button?.toolTip == "waiting" && fixture.reports == 0 && fixture.events().isEmpty, reason + ": no tooltip, helper or event success")
    }
    remainsPending(snapshot("2026-09-11T12:00:00+08:00"), "Stale snapshot")
    remainsPending([:], "Absent metadata")
    remainsPending(["meta": ["generated_at": 123]], "Invalid timestamp")
    fixture.pendingRefresh = 7
    remainsPending(current, "Collector still pending")
    check(fixture.pendingRefresh == 7, "Rendering cannot consume the pending collector")
    fixture.pendingRefresh = nil
    let required = fixture.manualExpectedStamp
    fixture.manualExpectedStamp = nil
    remainsPending(current, "The collector has not supplied its expected stamp")
    fixture.manualExpectedStamp = required
    fixture.manualBeganAt = nil
    remainsPending(current, "No manual request")
    fixture.begin()
    fixture.refreshPresented(current)
    check(fixture.reports == 1 && fixture.events().count == 1 && fixture.manualBeganAt == nil && fixture.manualExpectedStamp == nil, "A matching completed snapshot confirms exactly once")
    fixture.refreshPresented(snapshot("2026-09-11T12:00:02+08:00"))
    check(fixture.reports == 1 && fixture.events().count == 1, "Later background snapshots do not become manual successes")
case "pending-indicator":
    fixture.begin(); fixture.refreshPresented(current)
    check(fixture.capsuleState.indicator == "check", "Successful refresh initially shows confirmation")
    fixture.pendingRefresh = 8; fixture.capsuleState.indicator = "busy"
    RunLoop.main.run(until: Date().addingTimeInterval(1.45))
    check(fixture.capsuleState.indicator == "busy", "An old feedback timer cannot overwrite a new pending refresh")
    check(fixture.reports == 1 && fixture.events().count == 1, "The feedback timer adds no duplicate success")
default: fatalError("Unknown fixture")
}
print(CommandLine.arguments[1] + " passed")
''')
        cls.binary = root / 'check'
        result = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(source), '-o', str(cls.binary)],
                                capture_output=True, text=True, timeout=90)
        if result.returncode:
            raise AssertionError(result.stderr)

    def run_case(self, name):
        with tempfile.TemporaryDirectory(prefix='refresh events ') as directory:
            result = subprocess.run([str(self.binary), name, directory], capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(name + ' passed', result.stdout)

    def test_repeated_completions_keep_bounded_status_and_append_each_event(self):
        self.run_case('repeated')

    def test_stale_or_pending_snapshots_do_not_acknowledge_refresh(self):
        self.run_case('guards')

    def test_previous_feedback_timer_preserves_a_new_pending_indicator(self):
        self.run_case('pending-indicator')


if __name__ == '__main__':
    unittest.main()
