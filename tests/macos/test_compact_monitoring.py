"""Production compact scheduling with synthetic workers and no application UI."""
from tools.common.paths import ROOT, BACKEND, macos_source
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from tests.macos.test_floating_period import declaration


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class CompactMonitoringTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        main = (macos_source('Main.swift')).read_text()
        query = declaration(main, 'struct FloatingUsageQuery:')
        methods = ''.join(declaration(main, name) for name in [
            '    func enterCompactMode()', '    func startCompactMonitoring()',
            '    func stopCompactMonitoring()', '    func markCompactDirty()',
            '    func scheduleCompact()', '    func fetchCompact(manual:',
        ])
        fixture = r'''import AppKit
import CoreServices
typealias Object = [String: Any]
func exact(_ value: Any?) -> String { String(describing: value ?? "?") }
func check(_ condition: @autoclosure () -> Bool, _ reason: String) { precondition(condition(), reason) }
''' + query + r'''
final class Cancel { func cancel() {} }
final class Session { func invalidateAndCancel() {} }
final class Monitor {
    var home: URL?, isWatching = true
    func start(home: URL) { self.home = home }
    func stop() { home = nil }
}
final class Backend {
    var stopping = false, stops: [() -> Void] = []
    func stop(_ done: @escaping () -> Void) { stopping = true; stops.append(done) }
    func finishStop() { stopping = false; let callbacks = stops; stops = []; callbacks.forEach { $0() } }
}
final class Collector {
    var busy = false, stopping = false, requests: [FloatingUsageQuery] = [], stops: [() -> Void] = []
    var pending: ((Result<Object, Error>) -> Void)?
    func collect(home: URL, days: String, model: String, task: String, seconds: Int, completion: @escaping (Result<Object, Error>) -> Void) {
        check(!busy && !stopping, "No overlapping compact workers")
        busy = true; requests.append(FloatingUsageQuery(days: days, model: model, task: task)); pending = completion
    }
    func finish(_ count: Int = 123) {
        busy = false; let callback = pending; pending = nil
        callback?(.success(["today": ["summary": ["total_tokens": 10]], "filtered": ["summary": ["total_tokens": count]], "filter_reset": false]))
    }
    func stop(_ done: @escaping () -> Void) { stopping = true; stops.append(done) }
    func finishStop() { stopping = false; busy = false; pending = nil; let callbacks = stops; stops = []; callbacks.forEach { $0() } }
}
final class State { var status = "" }
final class Fixture {
    func startTaskMonitoring(home: URL) {}
    func reportLocalUpdate(busy: Bool, error: String? = nil, stamp: String? = nil) {}
    func refreshBudgets(manual: Bool) {}
    var compactMode = true, terminating = false, compactDirty = false, compactManualQueued = false, compactSelectionQueued = false
    var nativeSummaryBusy = false
    var compactDirtyGeneration = 0, compactEpoch = 0, autoSeconds = 1
    var compactLastScan = Date(), compactTimer: Timer?, timer: Timer?
    let compactMonitor = Monitor(), backend = Backend(), collector = Collector(), floatingChoices = Cancel(), capsuleState = State()
    var settingsID = 0, requestID = 0, summaryRequestID = 0, floatingRequestID = 0
    var settingsCommand: Cancel?, request: Cancel?, summaryRequest: Cancel?, floatingRequest: Cancel?, refreshCommand: Cancel?
    var activeSession: Session?, manualExpectedStamp: String?, manualBeganAt: Date?
    let codexHome = URL(fileURLWithPath: "/synthetic-usage-home")
    var floatingQuery = FloatingUsageQuery(days: "7", model: "all", task: "all")
    var filteredSnapshotQuery: FloatingUsageQuery? = FloatingUsageQuery(days: "7", model: "all", task: "all")
    var todaySnapshot: Object = ["summary": ["total_tokens": 10]], filteredSnapshot: Object = [:]
    var statusItem: NSStatusItem?, starts = 0, renders = 0, trims = 0
    var intervalDescription: String { "test" }
    func finishRefreshing() {}
    func start() { starts += 1 }
    func trimIdleMemory() { trims += 1 }
    func updateStatusTitle() {}
    func loadFloating() { renders += 1 }
    func refreshPresented(_ data: Object) {}
    func resetFloatingFilters() { floatingQuery = FloatingUsageQuery(days: floatingQuery.days, model: "all", task: "all") }
    func collectCompactSnapshot(query: FloatingUsageQuery, home: URL, completion: @escaping (Result<Object, Error>) -> Void) {
        collector.collect(home: home, days: query.days, model: query.model, task: query.task, seconds: autoSeconds, completion: completion)
    }
''' + methods + r'''
}
switch CommandLine.arguments[1] {
case "idle":
    let app = Fixture(); app.startCompactMonitoring(); app.compactDirty = false
    app.scheduleCompact(); app.fetchCompact(manual: false)
    check(app.compactTimer == nil && app.collector.requests.isEmpty, "Idle watched data must not schedule or launch Python")
    app.markCompactDirty()
    check(app.compactTimer != nil, "A relevant file event schedules a throttled update")
    app.compactTimer!.fire()
    check(app.collector.requests.count == 1, "One dirty event produces one worker")
    app.markCompactDirty(); app.markCompactDirty()
    check(app.compactTimer == nil, "Events during a worker coalesce without a competing timer")
    app.collector.finish()
    check(app.compactDirty && app.compactTimer != nil, "A file change during collection must not be lost")
    app.compactTimer!.fire(); app.collector.finish()
    check(!app.compactDirty && app.compactTimer == nil && app.collector.requests.count == 2, "Clean completion returns to timer-free idle")
case "pause":
    let app = Fixture(); app.startCompactMonitoring(); app.autoSeconds = 0
    app.markCompactDirty(); app.scheduleCompact(); app.fetchCompact(manual: false)
    check(app.compactTimer == nil && app.collector.requests.isEmpty, "Paused refresh ignores watcher and reconciliation notifications")
    app.fetchCompact(manual: true); app.markCompactDirty(); app.collector.finish()
    check(app.collector.requests.count == 1 && app.compactTimer == nil, "Manual refresh still works without enabling an automatic timer")
    app.floatingQuery = FloatingUsageQuery(days: "30", model: "model-b", task: "task-b")
    app.compactSelectionQueued = true; app.fetchCompact(manual: false)
    check(app.collector.requests.count == 2 && app.collector.requests.last! == app.floatingQuery, "Explicit selection updates immediately while paused")
    app.collector.finish(); app.fetchCompact(manual: false)
    check(app.compactTimer == nil && app.collector.requests.count == 2, "Explicit work does not unpause polling")
case "fallback":
    let app = Fixture(); app.compactMonitor.isWatching = false
    app.scheduleCompact(); check(app.compactTimer != nil, "Unavailable FSEvents falls back to the selected interval")
    app.compactTimer!.fire(); app.collector.finish()
    check(app.compactTimer != nil && app.collector.requests.count == 1, "Fallback polling continues after a clean result")
    app.autoSeconds = 0; app.scheduleCompact(); app.fetchCompact(manual: false)
    check(app.compactTimer == nil && app.collector.requests.count == 1, "Fallback must respect pause too")
case "transition":
    let app = Fixture(); app.compactMode = false
    app.enterCompactMode()
    check(app.backend.stopping && app.collector.requests.isEmpty, "Closing waits for resident backend cleanup")
    app.compactMode = false; app.stopCompactMonitoring(); app.backend.finishStop()
    check(app.starts == 0 && app.collector.requests.isEmpty, "Late close completion must not restart main backend or compact worker")
    app.enterCompactMode(); app.backend.finishStop()
    check(app.collector.stopping && app.collector.requests.isEmpty, "A rapid close joins any previous compact worker shutdown")
    app.collector.finishStop()
    check(app.collector.requests.count == 1, "The joined cleanup starts exactly one collection")
    let oldSnapshot = app.todaySnapshot["summary"] as! Object
    app.compactMode = false; app.stopCompactMonitoring(); app.collector.finish(999)
    check(app.renders == 0 && (app.todaySnapshot["summary"] as! Object)["total_tokens"] as! Int == oldSnapshot["total_tokens"] as! Int, "Old compact results cannot overwrite reopened main state")
    app.enterCompactMode(); app.terminating = true; app.backend.finishStop()
    check(app.starts == 0 && app.collector.requests.count == 1, "Quit wins over delayed mode transitions")
case "paths":
    let root = "/synthetic-usage-home"
    for path in [root, root + "/sessions/2026/test.jsonl", root + "/archived_sessions/task.jsonl", root + "/session_index.jsonl", root + "/state_5.sqlite-wal"] {
        check(UsageChangeMonitor.isUsagePath(path, root: root, flags: 0), "Known usage paths must mark dirty")
    }
    for path in [root + "/auth.json", root + "/config.toml", root + "/logs/other.log"] {
        check(!UsageChangeMonitor.isUsagePath(path, root: root, flags: 0), "Unrelated settings must not trigger a scan")
    }
    check(UsageChangeMonitor.isUsagePath(root + "/unknown", root: root, flags: UInt32(kFSEventStreamEventFlagMustScanSubDirs)), "Overflow/root-change events require reconciliation")
default: fatalError("Unknown fixture")
}
print(CommandLine.arguments[1] + " passed")
'''
        cls.directory = tempfile.TemporaryDirectory(prefix='compact monitoring ')
        cls.addClassCleanup(cls.directory.cleanup)
        root = Path(cls.directory.name)
        swift = root / 'main.swift'
        swift.write_text(fixture)
        cls.binary = root / 'check'
        built = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(macos_source('UsageChangeMonitor.swift')), str(swift), '-o', str(cls.binary)], capture_output=True, text=True, timeout=90)
        if built.returncode:
            raise AssertionError(built.stderr)

    def run_case(self, name):
        ran = subprocess.run([str(self.binary), name], capture_output=True, text=True, timeout=10)
        self.assertEqual(ran.returncode, 0, ran.stderr)
        self.assertIn(name + ' passed', ran.stdout)

    def test_idle_and_changes_arriving_during_collection(self):
        self.run_case('idle')

    def test_pause_still_allows_manual_and_explicit_selection(self):
        self.run_case('pause')

    def test_monitor_unavailable_fallback_respects_pause(self):
        self.run_case('fallback')

    def test_close_reopen_and_quit_reject_old_async_callbacks(self):
        self.run_case('transition')

    def test_only_relevant_paths_and_overflow_trigger_dirty(self):
        self.run_case('paths')
