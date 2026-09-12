"""Run the production floating controllers against isolated preferences and I/O.

No app is launched, no installed defaults are touched, and no network is used.
Only worker/HTTP/UI dependencies are replaced; selection, query construction,
publication, stale-result handling and rendering are extracted unchanged.
"""
from tools.common.paths import ROOT, BACKEND, macos_source
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


def declaration(source, marker):
    start = source.index(marker)
    opening = source.index('{', start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end] + '\n'


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class FloatingPeriodTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        main_source = (macos_source('Main.swift')).read_text()
        shared = declaration(main_source, 'struct FloatingUsageQuery:')
        numbers = main_source[main_source.index('private func numeric('):main_source.index('private func label(')]
        main_methods = ''.join(declaration(main_source, marker) for marker in [
            '    var floatingQuery:', '    func endpoint(', '    @objc func filterChanged()',
            '    func applyFloatingPeriod(', '    func loadFloating()',
            '    func renderFloating(', '    func fetchCompact(manual:',
            '    func applyFloatingFilter(', '    func resetFloatingFilters()',
            '    func refreshFloatingSelection()', '    func loadFloatingChoices(',
        ]).replace('UserDefaults.standard', 'preferences').replace('usagePreferences', 'preferences')
        fixture = r'''import AppKit
let suite = "local.codex-usage.test-floating-period." + UUID().uuidString
let preferences = UserDefaults(suiteName: suite)!
defer { preferences.removePersistentDomain(forName: suite) }
if CommandLine.arguments[1] != "migration" {
    preferences.set(true, forKey: "floatingFollowFilters")
    preferences.set("model-a", forKey: "filterModel"); preferences.set("task-a", forKey: "filterTask")
}
typealias Object = [String: Any]
func smallClock(_ date: Date) -> String { "12:00:00" }
func pump() { RunLoop.main.run(until: Date().addingTimeInterval(0.03)) }
func check(_ condition: @autoclosure () -> Bool, _ reason: String) { precondition(condition(), reason) }
''' + shared + numbers + r'''
final class FixtureState {
    var scope = 1
    var selectedModel = "all", selectedTask = "all", selectedTaskLabel = "全部任务"
    var rangeDays = "1", total = "—", exact = "", input = "", output = "", cache = "", context = "", status = "", indicator = ""
    var enabled = true
}
final class FixturePanel { var isVisible = true }
final class FixtureButton { var selectedSegment = 1 }
final class FixtureItem { var representedObject: Any? = "all" }
final class FixturePopup { var selectedItem: FixtureItem? = FixtureItem() }
final class FixtureSearch { var stringValue = "" }
final class FixtureBackend {
    var url: URL? = URL(string: "http://127.0.0.1:54321"), generation = 1, stopping = false
    func cachePath(_ home: URL) -> URL { home.appendingPathComponent("synthetic.sqlite") }
}
final class FixtureChoices {
    var requests: [(FloatingUsageQuery, String, ([(String,String)]?, String?) -> Void)] = []
    func cancel() {}
    func load(cache: URL, query: FloatingUsageQuery, kind: String, completion: @escaping ([(String,String)]?, String?) -> Void) {
        requests.append((query, kind, completion))
    }
}
final class FixtureChangeMonitor { var isWatching = true }
final class FixtureTask {
    let url: URL, completion: (Data?, URLResponse?, Error?) -> Void
    var resumed = false, cancelled = false
    init(_ url: URL, _ completion: @escaping (Data?, URLResponse?, Error?) -> Void) { self.url = url; self.completion = completion }
    func resume() { resumed = true }
    func cancel() { cancelled = true }
    func finish(_ data: Object) {
        completion(try! JSONSerialization.data(withJSONObject: data), HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: nil), nil)
        pump()
    }
    var parameters: [String: String] {
        Dictionary(uniqueKeysWithValues: URLComponents(url: url, resolvingAgainstBaseURL: false)!.queryItems!.map { ($0.name, $0.value!) })
    }
}
final class FixtureSession {
    var tasks: [FixtureTask] = []
    func dataTask(with url: URL, completionHandler: @escaping (Data?, URLResponse?, Error?) -> Void) -> FixtureTask {
        let task = FixtureTask(url, completionHandler); tasks.append(task); return task
    }
}
final class FixtureCollector {
    var busy = false, stopping = false
    var queries: [FloatingUsageQuery] = []
    var completion: ((Result<Object, Error>) -> Void)?
    func collect(home: URL, days: String, model: String, task: String, seconds: Int, completion: @escaping (Result<Object, Error>) -> Void) {
        check(!busy, "Only one collector may run")
        busy = true; queries.append(FloatingUsageQuery(days: days, model: model, task: task)); self.completion = completion
    }
    func finish(_ data: Object) { busy = false; let pending = completion; completion = nil; pending?(.success(data)) }
}
func payload(_ count: Int, task: String = "fixture task") -> Object {
    ["summary": ["total_tokens": count, "input_tokens": count - 2, "output_tokens": 2, "cached_input_tokens": 3, "requests": 1],
     "filters": ["selected_task": ["label": task]], "meta": ["generated_at": "2026-09-11T12:00:00+08:00"]]
}
func collected(_ count: Int, reset: Bool = false) -> Object { ["today": payload(10), "filtered": payload(count), "filter_reset": reset] }
final class MainFixture: NSObject {
    func refreshBudgets(manual: Bool) {}
    let capsuleState = FixtureState(), backend = FixtureBackend(), session = FixtureSession(), collector = FixtureCollector()
    var days = preferences.string(forKey: "filterDays") ?? "30"
    var floatingDays = FloatingUsageQuery.restoredDays(preferences)
    var floatingModel = FloatingUsageQuery.restoredFilter(preferences, key: "Model")
    var floatingTask = FloatingUsageQuery.restoredFilter(preferences, key: "Task")
    let floatingChoices = FixtureChoices()
    var model = "model-a", task = "task-a", group = "model"
    var dashboard: NSObject? = NSObject(), mainVisible = true
    let period = FixtureButton(), grouping = FixtureButton(), models = FixturePopup(), tasks = FixturePopup(), search = FixtureSearch()
    var floating: FixturePanel? = FixturePanel(), floatingRequest: FixtureTask?, floatingRequestID = 0
    var compactMode = false, terminating = false, compactManualQueued = false, compactSelectionQueued = false
    var nativeSummaryBusy = false
    var compactDirty = true, compactDirtyGeneration = 0, compactEpoch = 0, compactLastScan = Date.distantPast
    let compactMonitor = FixtureChangeMonitor()
    var filteredSnapshot: Object = [:], todaySnapshot: Object = [:], snapshot: Object = [:]
    var filteredSnapshotQuery: FloatingUsageQuery?
    var compactTimer: Timer?, autoSeconds = 0, pendingRefresh: Int?, manualExpectedStamp: String?, manualBeganAt: Date?
    var statusItem: NSStatusItem?, statusText = "", scheduleCount = 0
    let codexHome = URL(fileURLWithPath: "/synthetic-codex-home")
    var intervalDescription: String { "自动刷新已关闭" }
    func refreshPresented(_ json: Object) {}
    func finishRefreshing() {}
    func updateStatusTitle() {}
    func trimIdleMemory() {}
    func scheduleCompact() { scheduleCount += 1 }
    func loadUsage() {}
    func collectCompactSnapshot(query: FloatingUsageQuery, home: URL, completion: @escaping (Result<Object, Error>) -> Void) {
        collector.collect(home: home, days: query.days, model: query.model, task: query.task, seconds: autoSeconds, completion: completion)
    }
''' + main_methods + r'''
}
switch CommandLine.arguments[1] {
case "migration":
    check(FloatingUsageQuery.restoredDays(preferences) == "1", "Default migration preserves the old unfiltered today mode")
    check(FloatingUsageQuery.restoredFilter(preferences, key: "Model") == "all" && FloatingUsageQuery.restoredFilter(preferences, key: "Task") == "all", "Old unfollowed mode migrates both filters to all")
    preferences.removeObject(forKey: "floatingDays")
    preferences.removeObject(forKey: "floatingModel"); preferences.removeObject(forKey: "floatingTask")
    preferences.set(true, forKey: "floatingFollowFilters"); preferences.set("7", forKey: "filterDays")
    preferences.set("model-a", forKey: "filterModel"); preferences.set("task-a", forKey: "filterTask")
    check(FloatingUsageQuery.restoredDays(preferences) == "7", "Old follow mode migrates its selected date once")
    check(FloatingUsageQuery.restoredFilter(preferences, key: "Model") == "model-a" && FloatingUsageQuery.restoredFilter(preferences, key: "Task") == "task-a", "Follow mode migrates both main choices once")
    preferences.set("90", forKey: "filterDays")
    preferences.set("main-new-model", forKey: "filterModel"); preferences.set("main-new-task", forKey: "filterTask")
    check(FloatingUsageQuery.restoredDays(preferences) == "7", "Later main-page dates must not repeat the migration")
    check(FloatingUsageQuery.restoredFilter(preferences, key: "Model") == "model-a" && FloatingUsageQuery.restoredFilter(preferences, key: "Task") == "task-a", "Later main choices must not repeat migration")
    let main = MainFixture(); main.applyFloatingPeriod("30")
    check(main.days == "90" && preferences.string(forKey: "filterDays") == "90", "Floating choice must not write main preferences")
    let restarted = MainFixture()
    check(restarted.days == "90" && restarted.floatingDays == "30", "Restarting a host controller restores separate periods")
    check(restarted.capsuleState.scope == 1 && restarted.floatingQuery.model == "model-a", "Explicit period retains model and task filters")
    restarted.applyFloatingPeriod("all")
    let mainAgain = MainFixture()
    check(mainAgain.days == "90" && mainAgain.floatingDays == "all", "Recreating the controller preserves both selections")
    restarted.applyFloatingPeriod("bogus")
    check(restarted.floatingDays == "all", "Invalid period actions must not become query parameters")
case "http":
    preferences.set("7", forKey: "filterDays")
    let main = MainFixture()
    main.snapshot = payload(7)
    main.applyFloatingPeriod("30")
    check(main.days == "7" && main.capsuleState.rangeDays == "30" && main.capsuleState.total == "—", "A new range must not label old numbers")
    let first = main.session.tasks.last!
    check(first.parameters == ["days": "30", "model": "model-a", "task": "task-a"], "HTTP must use the floating period even while main is visible")
    check(URLComponents(url: main.endpoint("api/usage")!, resolvingAgainstBaseURL: false)!.queryItems!.first!.value == "7", "Main endpoint keeps seven days")
    first.finish(payload(300))
    check(main.capsuleState.total == "300" && main.capsuleState.context.hasPrefix("30 天"), "Floating display must use its own returned count")
    main.period.selectedSegment = 3; main.filterChanged()
    check(main.days == "90" && main.floatingDays == "30", "Actual main filter action must not change the floating range")
    main.loadFloating()
    check(main.session.tasks.last!.parameters["days"] == "30", "A main filter redraw still requests the floating period")
    let obsolete = main.session.tasks.last!
    main.applyFloatingPeriod("all"); let latest = main.session.tasks.last!
    obsolete.finish(payload(900))
    check(main.capsuleState.total == "—", "Late cancelled HTTP results must not overwrite a new period")
    latest.finish(payload(999))
    check(main.capsuleState.total == "999" && main.capsuleState.rangeDays == "all", "Latest HTTP result wins")
case "compact-main":
    preferences.set("7", forKey: "filterDays")
    let main = MainFixture(); main.compactMode = true
    main.applyFloatingPeriod("30")
    check(main.collector.queries.last!.days == "30", "Main controller compact collection must use floating days")
    main.applyFloatingPeriod("90")
    main.collector.finish(collected(300, reset: true))
    check(main.collector.queries.last!.days == "90" && main.collector.queries.count == 2, "Changing range during a collection queues the new range without a timer")
    check(main.capsuleState.total == "—" && main.model == "model-a", "Stale counts and filter-reset flags must both be rejected")
    main.collector.finish(collected(900))
    check(main.capsuleState.total == "900" && main.capsuleState.rangeDays == "90", "Compact rendering must match the latest collection")
    check(main.days == "7", "Compact range must not overwrite main days")
case "compact-paused":
    preferences.set("7", forKey: "filterDays")
    let compact = MainFixture(); compact.compactMode = true
    compact.applyFloatingPeriod("30")
    check(compact.collector.queries.last! == FloatingUsageQuery(days: "30", model: "model-a", task: "task-a"), "Compact worker must receive independent period and existing model/task")
    compact.applyFloatingPeriod("all")
    compact.collector.finish(collected(300, reset: true))
    check(compact.collector.queries.last!.days == "all" && compact.collector.queries.count == 2, "Paused refresh cannot defer an explicit period change")
    check(compact.capsuleState.total == "—" && compact.model == "model-a", "Compact host must not present the superseded selection")
    compact.collector.finish(collected(999))
    check(compact.capsuleState.total == "999" && compact.capsuleState.rangeDays == "all" && compact.capsuleState.context.hasPrefix("全部"), "Compact label and numbers must come from the same query")
    check(compact.days == "7" && preferences.string(forKey: "filterDays") == "7", "Returning to main must retain its saved seven-day selection")
    compact.applyFloatingPeriod("1")
    check(compact.capsuleState.total == "—", "Changing period clears values from the earlier selection")
    compact.collector.finish(collected(10))
    check(compact.capsuleState.total == "10" && compact.floatingQuery.model == "model-a", "Explicit today also preserves selected model/task")
    compact.applyFloatingPeriod("30")
    compact.collector.finish(collected(300, reset: true))
    check(compact.floatingModel == "all" && compact.floatingTask == "all" && compact.capsuleState.total == "300", "A matching reset must publish its valid fallback summary")
    check(compact.model == "model-a" && compact.task == "task-a" && preferences.string(forKey: "filterModel") == "model-a" && preferences.string(forKey: "filterTask") == "task-a", "Floating fallback must never reset main filters")
case "independent-filters":
    let main = MainFixture()
    main.applyFloatingPeriod("7")
    main.applyFloatingFilter("model", value: "model-b")
    let obsolete = main.session.tasks.last!
    main.applyFloatingFilter("task", value: "task-b")
    let newest = main.session.tasks.last!
    check(newest.parameters == ["days": "7", "model": "model-b", "task": "task-b"], "All three floating fields independently enter the query")
    obsolete.finish(payload(900))
    check(main.capsuleState.total == "—", "Changing task rejects an earlier model-only response")
    newest.finish(payload(123, task: "Task B"))
    check(main.capsuleState.total == "123" && main.capsuleState.selectedTaskLabel == "Task B", "Selected task title follows matching data")
    check(main.days == "30" && main.model == "model-a" && main.task == "task-a", "Floating triple does not mutate any main filter")
    main.period.selectedSegment = 3
    main.models.selectedItem!.representedObject = "main-model-new"
    main.tasks.selectedItem!.representedObject = "main-task-new"
    main.filterChanged()
    check(main.floatingQuery == FloatingUsageQuery(days: "7", model: "model-b", task: "task-b"), "Main filter actions leave all floating fields untouched")
    let compact = MainFixture(); compact.compactMode = true
    check(compact.floatingQuery == main.floatingQuery, "A new host controller restores all independent floating fields")
    compact.applyFloatingFilter("model", value: "model-c")
    check(compact.autoSeconds == 0 && compact.collector.queries.last! == FloatingUsageQuery(days: "7", model: "model-c", task: "task-b"), "Changing model while paused refreshes immediately and preserves task and range")
    compact.applyFloatingFilter("task", value: "all")
    compact.collector.finish(collected(500, reset: true))
    check(compact.floatingTask == "all" && compact.floatingModel == "model-c" && compact.capsuleState.total == "—", "Stale reset cannot overwrite a newer independent selection")
    compact.collector.finish(collected(600))
    check(compact.capsuleState.total == "600", "Queued explicit selection finishes without an auto timer")
    main.loadFloating()
    var fallback = payload(321); fallback["filter_reset"] = true
    main.session.tasks.last!.finish(fallback)
    check(main.floatingModel == "all" && main.floatingTask == "all" && main.floatingDays == "7", "Matching HTTP fallback resets floating model and task only")
    check(main.model == "main-model-new" && main.task == "main-task-new" && main.days == "90", "HTTP fallback preserves the whole main triple")
case "choice-results":
    let main = MainFixture()
    var mainChoices: [(String,String)]? = nil
    main.loadFloatingChoices("task") { choices, _ in mainChoices = choices }
    check(main.floatingChoices.requests.count == 1 && main.session.tasks.isEmpty && main.collector.queries.isEmpty, "Opening a selector only requests cached choices")
    main.applyFloatingPeriod("7")
    main.floatingChoices.requests[0].2([("late", "Late task")], nil)
    check(mainChoices == nil, "Main rejects choices captured for the old range")
    main.loadFloatingChoices("task") { choices, _ in mainChoices = choices }
    main.floatingChoices.requests[1].2([("current", "Current task")], nil)
    check(mainChoices?.first?.0 == "current", "Matching choice result is delivered")
    let compact = MainFixture(); compact.compactMode = true
    var compactChoices: [(String,String)]? = nil
    compact.loadFloatingChoices("task") { choices, _ in compactChoices = choices }
    compact.applyFloatingFilter("model", value: "model-b")
    compact.floatingChoices.requests[0].2([("late", "Wrong model task")], nil)
    check(compactChoices == nil, "Compact host rejects task choices from an older model")
    compact.loadFloatingChoices("model") { choices, _ in compactChoices = choices }
    compact.floatingChoices.requests[1].2([("model-b", "model-b")], nil)
    check(compactChoices?.first?.0 == "model-b", "Compact host delivers matching choices")
default: fatalError("Unknown test")
}
print(CommandLine.arguments[1] + " passed")
'''
        cls.directory = tempfile.TemporaryDirectory(prefix='floating period ')
        cls.addClassCleanup(cls.directory.cleanup)
        root = Path(cls.directory.name)
        main = root / 'main.swift'
        main.write_text(fixture)
        cls.binary = root / 'check'
        compiled = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), '-o', str(cls.binary)], capture_output=True, text=True, timeout=120)
        if compiled.returncode:
            raise AssertionError(compiled.stderr)

    def run_case(self, name):
        ran = subprocess.run([str(self.binary), name], capture_output=True, text=True, timeout=10)
        self.assertEqual(ran.returncode, 0, ran.stderr)
        self.assertIn(name + ' passed', ran.stdout)

    def test_once_only_migration_and_restart_restore(self):
        self.run_case('migration')

    def test_main_http_period_independence_and_out_of_order_results(self):
        self.run_case('http')

    def test_main_compact_queue_and_results(self):
        self.run_case('compact-main')

    def test_compact_paused_refresh_selection_and_stale_publication(self):
        self.run_case('compact-paused')

    def test_all_floating_filters_independent_with_main_and_each_other(self):
        self.run_case('independent-filters')

    def test_cached_choices_queries_reject_stale_range_and_model_results(self):
        self.run_case('choice-results')


if __name__ == '__main__':
    unittest.main()
