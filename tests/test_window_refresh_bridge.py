"""Compile production refresh bridge methods with deterministic IPC/worker I/O."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


def declaration(source, marker):
    start = source.index(marker)
    opening = source.index('{', start)
    depth, end = 1, opening + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end] + '\n'


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class WindowRefreshBridgeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = (Path(__file__).parents[1] / 'Sources/Main.swift').read_text()
        methods = ''.join(declaration(source, marker) for marker in [
            '    func requestHelperRefresh()', '    func completeHostRefresh(',
            '    func reportHelperRefresh(', '    func receiveWindowAction(', '    func start()',
        ])
        fixture = r'''import Foundation
typealias Object = [String: Any]
let isMainWindowProcess = CommandLine.arguments[1].hasPrefix("helper")
func check(_ condition: @autoclosure () -> Bool, _ message: String) { precondition(condition(), message) }
final class Preferences { func synchronize() {} }
let usagePreferences = Preferences()
struct CapsuleTheme { init(storedValue: String) {} }
final class State { var enabled = true, indicator = "refresh", status = "" }
final class Button { var title = "", isEnabled = true }
final class Bridge {
    var sent: [(String, Object)] = []
    func sendToMain(_ action: String, payload: Object = [:]) { sent.append((action, payload)) }
}
final class Worker {
    var url: URL?, starting = false, stopping = false, starts = 0
    var completion: ((Result<URL, Error>) -> Void)?
    func start(home: URL, refreshSeconds: Int, completion: @escaping (Result<URL, Error>) -> Void) {
        starts += 1; starting = true; self.completion = completion
    }
    func finish(_ result: Result<URL, Error>) { starting = false; let done = completion; completion = nil; done?(result) }
    func stop(_ done: @escaping () -> Void) { done() }
}
final class Quota { func refresh(force: Bool) {} }
final class Fixture {
    let capsuleState = State(), windowProcesses = Bridge(), backend = Worker(), collector = Worker(), quotaReader = Quota()
    var hostRefreshID: String?, hostRefreshDeadline: DispatchWorkItem?, pendingRefresh: Int?, refreshStarted: Date?
    var helperRefreshIDs = Set<String>(), helperRefreshQueued = false
    var terminating = false, restarting = false, compactMode = false, mainWindowOpen = false
    var compactDirty = false, compactDirtyGeneration = 0, compactSelectionQueued = false
    var dashboard: NSObject? = NSObject()
    var autoSeconds = 5, failures = 0, statusText = "", refreshButton: Button? = Button(), exportButton: Button? = Button()
    var sent: [(String, Object)] = [], scans = 0, manuals = 0, polls = 0, alerts = 0, states = 0
    let codexHome = URL(fileURLWithPath: "/synthetic-home")
    func sendHost(_ action: String, payload: Object = [:]) { sent.append((action, payload)) }
    func finishRefreshing() { pendingRefresh = nil; capsuleState.enabled = true; capsuleState.indicator = "refresh" }
    func fetchCompact(manual: Bool) { scans += 1 }
    func manualRefresh() { manuals += 1; pendingRefresh = 5 }
    func applyInterval(_ seconds: Int, refreshAfterChange: Bool, rememberedSeconds: Int? = nil, changeID: String? = nil) {}
    func receiveIntervalRollback(_ payload: Object) {}
    func poll() { polls += 1 }
    func alert(_ title: String, _ message: String) { alerts += 1 }
    func hideDashboard() {}; func showDashboard() {}; func handleMainRequest() {}
    func persistHostWindowMode() {}; func publishHostState() { states += 1 }
    func showFloating() {}; func hideFloating() {}; func toggleFloating() {}
    func onlyFloating() {}; func onlyStatusBar() {}; func openStatusMenu() {}
    func applyCapsuleTheme(_ theme: CapsuleTheme) {}
    func stopCompactMonitoring() {}; func enterCompactMode() {}; func quitApplication() {}
''' + methods + r'''
}
let value = Fixture()
switch CommandLine.arguments[1] {
case "host-result":
    value.requestHelperRefresh()
    let id = value.hostRefreshID!
    check(!value.capsuleState.enabled && value.pendingRefresh == -2 && value.windowProcesses.sent.count == 1, "Host must expose a busy state while awaiting the helper")
    check(value.windowProcesses.sent[0].0 == "refresh" && value.windowProcesses.sent[0].1["requestID"] as? String == id, "The IPC refresh must carry its request identity")
    value.receiveWindowAction("helperReady", payload: [:])
    check(value.windowProcesses.sent.count == 2 && value.windowProcesses.sent[1].1["requestID"] as? String == id && !value.capsuleState.enabled, "A helper that registered after the first send must receive the same pending request identity")
    value.scans = 0
    value.completeHostRefresh(id: "stale-id", success: true, message: "old")
    check(value.hostRefreshID == id && !value.capsuleState.enabled && value.scans == 0, "A stale reply must not finish the current refresh")
    value.receiveWindowAction("refreshResult", payload: ["requestID": id, "success": true, "message": "verified"])
    check(value.hostRefreshID == nil && value.hostRefreshDeadline == nil && value.capsuleState.enabled && value.pendingRefresh == nil && value.scans == 1, "Matching success must release busy/deadline and fetch the native summary")
    check(value.capsuleState.status == "verified", "Completion must publish the helper's result")
case "host-error-timeout":
    value.pendingRefresh = -1; value.capsuleState.enabled = false
    value.receiveWindowAction("helperReady", payload: [:])
    check(value.pendingRefresh == nil && value.capsuleState.enabled, "Helper indexer takeover must retire an interrupted local scan without clearing a remote request")
    value.scans = 0
    value.requestHelperRefresh(); let id = value.hostRefreshID!
    value.receiveWindowAction("refreshResult", payload: ["requestID": id, "success": false, "message": "read failed"])
    check(value.capsuleState.enabled && value.hostRefreshID == nil && value.scans == 0 && value.capsuleState.status == "read failed", "An error must release busy state without claiming a fresh summary")
    value.requestHelperRefresh(); let timeout = value.hostRefreshDeadline!
    timeout.perform()
    check(value.capsuleState.enabled && value.hostRefreshID == nil && value.pendingRefresh == nil && value.capsuleState.status.contains("耗时较长"), "A lost reply must have a bounded timeout that restores interaction")
case "helper-startup-queue":
    value.start()
    value.receiveWindowAction("refresh", payload: ["requestID": "one"])
    check(value.helperRefreshQueued && value.helperRefreshIDs == ["one"] && value.manuals == 0 && value.backend.starts == 1, "A refresh during startup must queue without launching another worker")
    value.backend.url = URL(string: "http://127.0.0.1:1234")
    value.backend.finish(.success(value.backend.url!))
    check(value.manuals == 1 && !value.helperRefreshQueued && value.pendingRefresh == 5, "Successful startup must execute the queued refresh")
    value.receiveWindowAction("refresh", payload: ["requestID": "two"])
    check(value.manuals == 1 && value.helperRefreshIDs == ["one", "two"], "An existing refresh must accept another waiter without starting another scan")
    value.reportHelperRefresh(success: true, message: "verified")
    let ids = Set(value.sent.compactMap { $0.1["requestID"] as? String })
    check(ids == ["one", "two"] && value.sent.allSatisfy { $0.0 == "refreshResult" && $0.1["success"] as? Bool == true } && value.helperRefreshIDs.isEmpty, "Completion must resolve every waiter exactly once")
case "helper-startup-failure":
    value.start(); value.receiveWindowAction("refresh", payload: ["requestID": "failed"])
    value.backend.finish(.failure(NSError(domain: "fixture", code: 1)))
    check(value.manuals == 0 && value.helperRefreshIDs.isEmpty && !value.helperRefreshQueued, "Startup failure must retire queued refreshes")
    check(value.sent.count == 1 && value.sent[0].1["requestID"] as? String == "failed" && value.sent[0].1["success"] as? Bool == false, "Startup failure must report an error to the waiting host")
default: fatalError("Unknown fixture")
}
print(CommandLine.arguments[1] + " passed")
'''
        cls.directory = tempfile.TemporaryDirectory(prefix='window refresh bridge ')
        cls.addClassCleanup(cls.directory.cleanup)
        path = Path(cls.directory.name)
        main = path / 'main.swift'; main.write_text(fixture)
        cls.binary = path / 'check'
        compiled = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), '-o', str(cls.binary)], capture_output=True, text=True, timeout=120)
        if compiled.returncode:
            raise AssertionError(compiled.stderr)

    def run_case(self, name):
        ran = subprocess.run([str(self.binary), name], capture_output=True, text=True, timeout=10)
        self.assertEqual(ran.returncode, 0, ran.stderr)
        self.assertIn(name + ' passed', ran.stdout)

    def test_host_matches_reply_and_releases_busy_state(self): self.run_case('host-result')
    def test_host_error_and_timeout_restore_interaction(self): self.run_case('host-error-timeout')
    def test_helper_queues_startup_and_merges_existing_scan(self): self.run_case('helper-startup-queue')
    def test_helper_startup_error_replies_to_waiter(self): self.run_case('helper-startup-failure')


if __name__ == '__main__':
    unittest.main()
