"""Exercise the current host/helper settings callbacks with isolated dependencies."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from test_floating_period import declaration


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class CompactSettingsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = (Path(__file__).parents[1] / 'Sources/Main.swift').read_text()
        methods = ''.join(declaration(source, marker) for marker in [
            '    func applyInterval(', '    func scheduleCompact()',
            '    func applyCapsuleTheme(', '    func rememberRefreshInterval(',
            '    @objc func toggleAutoRefresh()', '    func receiveIntervalRollback(',
            '    func receiveHostState(',
        ])
        interval_source = (Path(__file__).parents[1] / 'Sources/RefreshInterval.swift').read_text()
        preference = declaration(interval_source, 'enum RefreshIntervalPreference')
        fixture = r'''import AppKit
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
var isMainWindowProcess = false
let suite = "local.codex-usage.test-compact-settings." + UUID().uuidString
let usagePreferences = UserDefaults(suiteName: suite)!
defer { usagePreferences.removePersistentDomain(forName: suite) }
typealias Object = [String: Any]
enum CapsuleTheme: String {
    case dark, light
    init(storedValue: String) { self = CapsuleTheme(rawValue: storedValue) ?? .dark }
}
''' + preference + r'''
final class State { var theme = CapsuleTheme.light, refreshSeconds = 5, lastPositiveRefreshSeconds = 5 }
final class Cancel { var cancelled = false; func cancel() { cancelled = true } }
final class Backend { var url: URL?, stopping = false }
final class Collector { var busy = false, stopping = false }
final class Monitor { var isWatching = true }
final class Label { var stringValue = "" }
final class Dashboard { let quotaText = Label(), quotaCards = Label() }
final class Button { var title = "" }
final class Fixture: NSObject {
    let capsuleState = State(), backend = Backend(), collector = Collector(), compactMonitor = Monitor()
    var autoSeconds = 5 { didSet { capsuleState.refreshSeconds = autoSeconds } }
    var lastPositiveRefreshSeconds = 5, intervalChangeID = "initial"
    var compactMode = true, compactDirty = true, terminating = false, applyingHostState = false
    var hostFloatingVisible = false, hostQuotaDetail = "", hostQuotaResetLabel = ""
    var dashboard: Dashboard? = Dashboard(), floatingButton: Button? = Button()
    var settingsID = 0, menuUpdates = 0, broadcasts = 0, scans = 0, manualScans = 0
    var compactLastScan = Date(), compactTimer: Timer?, settingsCommand: Cancel?
    var statusMenu: NSMenu?, statusText = "", snapshot: Object = [:]
    var sent: [(String, Object)] = []
    var posts: [(Result<Object, Error>) -> Void] = []
    var postBodies: [Object] = []
    // There are intentionally no window creation, teardown or process restart
    // methods: changing these settings must leave their owners alive.
    func rebuildIntervalPicker() {}
    func sendHost(_ action: String, payload: Object = [:]) { sent.append((action, payload)) }
    func publishHostState() { broadcasts += 1 }
    func manualRefresh() { manualScans += 1 }
    func fetchCompact(manual: Bool) { scans += 1 }
    func loadFloating() {}
    func loadCompact() {}
    func handleMainRequest() {}
    func render(_ data: Object) {}
    func updateCapsuleThemeChecks(_ menu: NSMenu?) { menuUpdates += 1 }
    func post(_ path: String, body: Object, completion: @escaping (Result<Object, Error>) -> Void) -> Cancel? {
        precondition(path == "api/settings")
        postBodies.append(body); posts.append(completion); return Cancel()
    }
''' + methods + r'''
}
let delegate = Fixture()
switch CommandLine.arguments[1] {
case "interval":
    delegate.applyInterval(60, refreshAfterChange: false)
    let scheduled = delegate.compactTimer!
    precondition(scheduled.isValid && delegate.capsuleState.refreshSeconds == 60)
    let pending = Cancel(); delegate.settingsCommand = pending
    delegate.applyInterval(0)
    precondition(!scheduled.isValid && delegate.compactTimer == nil && pending.cancelled)
    precondition(delegate.autoSeconds == 0 && delegate.capsuleState.refreshSeconds == 0)
    precondition(usagePreferences.integer(forKey: "refreshSeconds") == 0)
    delegate.scheduleCompact()
    RunLoop.main.run(until: Date().addingTimeInterval(0.03))
    precondition(delegate.scans == 0 && delegate.manualScans == 0 && delegate.compactTimer == nil)
    delegate.applyInterval(2)
    precondition(delegate.autoSeconds == 2 && delegate.manualScans == 1 && delegate.compactTimer != nil)
    let count = delegate.broadcasts
    for invalid in [-1, 3601] { delegate.applyInterval(invalid) }
    precondition(delegate.autoSeconds == 2 && delegate.broadcasts == count)
    isMainWindowProcess = true
    delegate.compactMode = false
    delegate.backend.url = URL(string: "http://127.0.0.1:1")
    delegate.applyInterval(30, refreshAfterChange: false)
    precondition(delegate.sent.last!.0 == "interval" && delegate.sent.last!.1["seconds"] as! Int == 30)
    precondition(delegate.posts.count == 1)
    let sent = delegate.sent.count
    delegate.applyingHostState = true
    delegate.applyInterval(0, refreshAfterChange: false)
    precondition(delegate.sent.count == sent, "Incoming host settings must not echo back over IPC")
    delegate.posts[0](.failure(NSError(domain: "test", code: 1)))
    precondition(delegate.autoSeconds == 0, "A stale settings failure must not revert a newer choice")
    delegate.posts[1](.success([:]))
    precondition(delegate.autoSeconds == 0 && delegate.manualScans == 1)
case "pause-restore":
    delegate.applyInterval(17, refreshAfterChange: false)
    precondition(delegate.lastPositiveRefreshSeconds == 17 && delegate.capsuleState.lastPositiveRefreshSeconds == 17)
    delegate.toggleAutoRefresh()
    precondition(delegate.autoSeconds == 0 && delegate.compactTimer == nil && delegate.manualScans == 0)
    precondition(delegate.lastPositiveRefreshSeconds == 17 && usagePreferences.integer(forKey: RefreshIntervalPreference.lastPositiveKey) == 17)
    precondition(RefreshIntervalPreference.remembered(preferences: usagePreferences, current: 0) == 17,
                 "A restart while paused must preserve the custom interval")
    delegate.toggleAutoRefresh()
    precondition(delegate.autoSeconds == 17 && delegate.compactTimer != nil && delegate.manualScans == 1)
    delegate.toggleAutoRefresh(); delegate.toggleAutoRefresh()
    precondition(delegate.autoSeconds == 17 && delegate.lastPositiveRefreshSeconds == 17 && delegate.manualScans == 2)
    precondition(usagePreferences.integer(forKey: "refreshSeconds") == 17)
    for invalid in [-1, 0, 3601] { delegate.rememberRefreshInterval(invalid) }
    precondition(delegate.lastPositiveRefreshSeconds == 17 && delegate.capsuleState.lastPositiveRefreshSeconds == 17)
case "failure-restore":
    isMainWindowProcess = true; delegate.compactMode = false
    delegate.backend.url = URL(string: "http://127.0.0.1:1")
    delegate.autoSeconds = 0; delegate.rememberRefreshInterval(17)
    // The other process can write the shared preferences before this process
    // handles a change. Rollback must use its own previous effective pair.
    usagePreferences.set(60, forKey: "refreshSeconds")
    usagePreferences.set(60, forKey: RefreshIntervalPreference.lastPositiveKey)
    delegate.applyInterval(60, refreshAfterChange: false, changeID: "change-b")
    precondition(delegate.sent.count == 1 && delegate.sent[0].0 == "interval")
    precondition(delegate.sent[0].1["seconds"] as? Int == 60 && delegate.sent[0].1["lastPositiveRefreshSeconds"] as? Int == 60)
    precondition(delegate.sent[0].1["intervalChangeID"] as? String == "change-b")
    delegate.posts[0](.failure(NSError(domain: "test", code: 1)))
    precondition(delegate.autoSeconds == 0 && delegate.lastPositiveRefreshSeconds == 17 && delegate.capsuleState.lastPositiveRefreshSeconds == 17,
                 "A failed change restores both pause and the remembered custom interval")
    precondition(usagePreferences.integer(forKey: "refreshSeconds") == 0 && usagePreferences.integer(forKey: RefreshIntervalPreference.lastPositiveKey) == 17)
    precondition(delegate.sent.count == 2 && delegate.sent[1].0 == "intervalRollback")
    let rollback = delegate.sent[1].1
    precondition(rollback["seconds"] as? Int == 0 && rollback["lastPositiveRefreshSeconds"] as? Int == 17 && rollback["expectedChangeID"] as? String == "change-b")
    precondition(delegate.postBodies.count == 2 && delegate.postBodies[0]["refresh_seconds"] as? Int == 60 && delegate.postBodies[1]["refresh_seconds"] as? Int == 0,
                 "A possibly committed failed request is followed by the effective rollback value")
    precondition(delegate.postBodies.map { $0["refresh_revision"] as! Int } == [1, 2])
    precondition(delegate.manualScans == 0 && delegate.statusText.contains("设置失败"))
    let rollbackCommand = delegate.settingsCommand!
    delegate.applyInterval(30, refreshAfterChange: false, changeID: "change-c")
    precondition(rollbackCommand.cancelled && delegate.postBodies.map { $0["refresh_revision"] as! Int } == [1, 2, 3],
                 "A later user choice cancels rollback I/O and has a newer worker revision")
    delegate.posts[1](.success([:]))
    precondition(delegate.autoSeconds == 30 && delegate.lastPositiveRefreshSeconds == 30,
                 "Rollback acknowledgement cannot replace a later user choice")
case "rollback-identity":
    delegate.applyInterval(0, refreshAfterChange: false, rememberedSeconds: 17, changeID: "change-a")
    delegate.applyInterval(60, refreshAfterChange: false, changeID: "change-b")
    let late: Object = ["seconds": 0, "lastPositiveRefreshSeconds": 17, "expectedChangeID": "change-b"]
    delegate.applyInterval(30, refreshAfterChange: false, changeID: "change-c")
    func checkLateRollback(current: Int, change: String) {
        let settings = delegate.settingsID, broadcasts = delegate.broadcasts
        usagePreferences.set(0, forKey: "refreshSeconds")
        usagePreferences.set(17, forKey: RefreshIntervalPreference.lastPositiveKey)
        delegate.receiveIntervalRollback(late)
        precondition(delegate.autoSeconds == current && delegate.lastPositiveRefreshSeconds == current && delegate.intervalChangeID == change,
                     "An obsolete failure cannot overwrite the host's current choice")
        precondition(delegate.settingsID == settings && delegate.broadcasts == broadcasts + 1,
                     "Rejecting a stale rollback republishes authority without applying another change")
        precondition(usagePreferences.integer(forKey: "refreshSeconds") == current && usagePreferences.integer(forKey: RefreshIntervalPreference.lastPositiveKey) == current,
                     "A rejected rollback repairs shared defaults already overwritten by the helper")
    }
    checkLateRollback(current: 30, change: "change-c")
    delegate.applyInterval(60, refreshAfterChange: false, changeID: "change-d")
    checkLateRollback(current: 60, change: "change-d")
    let valid: Object = ["seconds": 0, "lastPositiveRefreshSeconds": 17, "expectedChangeID": "change-d"]
    delegate.receiveIntervalRollback(valid)
    precondition(delegate.autoSeconds == 0 && delegate.lastPositiveRefreshSeconds == 17 && delegate.compactTimer == nil,
                 "The matching change may restore the paused custom interval")
    precondition(delegate.intervalChangeID != "change-d" && delegate.manualScans == 0)
    let broadcasts = delegate.broadcasts, settings = delegate.settingsID
    isMainWindowProcess = true
    delegate.receiveIntervalRollback(["seconds": 90, "lastPositiveRefreshSeconds": 90, "expectedChangeID": delegate.intervalChangeID])
    precondition(delegate.autoSeconds == 0 && delegate.broadcasts == broadcasts && delegate.settingsID == settings,
                 "Only the host consumes rollback IPC")
case "late-response":
    isMainWindowProcess = true; delegate.compactMode = false
    delegate.backend.url = URL(string: "http://127.0.0.1:1")
    delegate.applyInterval(60, refreshAfterChange: false, changeID: "change-b")
    let oldCommand = delegate.settingsCommand!
    delegate.applyInterval(30, refreshAfterChange: false, changeID: "change-c")
    precondition(oldCommand.cancelled)
    delegate.applyInterval(60, refreshAfterChange: false, changeID: "change-d")
    let active = delegate.settingsCommand!, sent = delegate.sent.count
    delegate.posts[0](.failure(NSError(domain: "test", code: 1)))
    delegate.posts[1](.success([:]))
    precondition(delegate.autoSeconds == 60 && delegate.lastPositiveRefreshSeconds == 60 && delegate.intervalChangeID == "change-d")
    precondition(delegate.sent.count == sent && delegate.posts.count == 3 && delegate.settingsCommand === active,
                 "Late callbacks, including ABA values, cannot roll back, clear active I/O or emit rollback IPC")
    precondition(delegate.postBodies.map { $0["refresh_revision"] as! Int } == [1, 2, 3])
    delegate.posts[2](.success([:]))
    precondition(delegate.settingsCommand == nil && delegate.autoSeconds == 60 && delegate.manualScans == 0)
case "host-state-revision":
    isMainWindowProcess = true
    for choices in [[60], [30, 60]] {
        let helper = Fixture(); helper.compactMode = false
        helper.backend.url = URL(string: "http://127.0.0.1:1")
        helper.applyInterval(60, refreshAfterChange: false, changeID: "change-b")
        let original = helper.settingsCommand!
        let sent = helper.sent.count
        for (index, seconds) in choices.enumerated() {
            helper.receiveHostState(["refreshSeconds": seconds, "lastPositiveRefreshSeconds": seconds,
                                     "intervalChangeID": "host-\(index)"])
        }
        let newest = helper.settingsCommand!
        precondition(original.cancelled && helper.posts.count == choices.count + 1,
                     "A new host change requires a new worker request even for equal seconds or ABA")
        precondition(helper.postBodies.map { $0["refresh_revision"] as! Int } == Array(1...(choices.count + 1)))
        precondition(helper.sent.count == sent && !helper.applyingHostState,
                     "Host settings never echo back and leave synchronization mode on return")
        for index in 0..<choices.count {
            helper.posts[index](.failure(NSError(domain: "test", code: 1)))
        }
        precondition(helper.autoSeconds == 60 && helper.lastPositiveRefreshSeconds == 60 && helper.settingsCommand === newest,
                     "Discarding old failures must leave a real active request for the latest setting")
        precondition(helper.sent.count == sent && helper.posts.count == choices.count + 1,
                     "Superseded failures do not emit rollback or corrective HTTP writes")
        helper.posts.last!(.success([:]))
        precondition(helper.settingsCommand == nil && helper.manualScans == 0,
                     "The newest host setting completes without an unsolicited manual scan")
    }
case "host-state-metadata":
    isMainWindowProcess = true; delegate.compactMode = false
    delegate.backend.url = URL(string: "http://127.0.0.1:1")
    delegate.autoSeconds = 0; delegate.rememberRefreshInterval(17)
    delegate.receiveHostState(["refreshSeconds": 0, "lastPositiveRefreshSeconds": 23, "intervalChangeID": "initial",
                              "floatingVisible": true, "theme": "dark", "quotaDetail": "fixture quota", "quotaResetLabel": "fixture reset"])
    precondition(delegate.lastPositiveRefreshSeconds == 23 && delegate.capsuleState.lastPositiveRefreshSeconds == 23)
    precondition(usagePreferences.integer(forKey: RefreshIntervalPreference.lastPositiveKey) == 23)
    precondition(delegate.hostFloatingVisible && delegate.floatingButton?.title == "隐藏浮窗" && delegate.capsuleState.theme == .dark)
    precondition(delegate.dashboard?.quotaText.stringValue == "fixture quota" && delegate.dashboard?.quotaCards.stringValue == "fixture reset")
    delegate.receiveHostState(["refreshSeconds": 0, "lastPositiveRefreshSeconds": 29])
    precondition(delegate.lastPositiveRefreshSeconds == 29 && delegate.intervalChangeID == "initial",
                 "Metadata-only synchronization may update remembered seconds without inventing a change identity")
    delegate.autoSeconds = 30; delegate.rememberRefreshInterval(30)
    delegate.receiveHostState(["refreshSeconds": 30, "lastPositiveRefreshSeconds": 99, "intervalChangeID": "initial"])
    precondition(delegate.lastPositiveRefreshSeconds == 30, "An active interval remains its own remembered value")
    precondition(delegate.posts.isEmpty && delegate.settingsID == 0 && delegate.sent.isEmpty && delegate.manualScans == 0,
                 "Same identity or metadata-only host state causes neither worker I/O nor IPC echo")
    precondition(!delegate.applyingHostState)
case "theme":
    for _ in 0..<250 {
        delegate.applyCapsuleTheme(.dark)
        precondition(delegate.capsuleState.theme == .dark)
        precondition(usagePreferences.string(forKey: "capsuleTheme") == "dark")
        delegate.applyCapsuleTheme(.light)
        precondition(delegate.capsuleState.theme == .light)
        precondition(usagePreferences.string(forKey: "capsuleTheme") == "light")
    }
    precondition(delegate.menuUpdates == 1000 && delegate.broadcasts == 500)
    precondition(delegate.scans == 0 && delegate.manualScans == 0 && delegate.posts.isEmpty)
    isMainWindowProcess = true
    delegate.applyCapsuleTheme(.dark)
    precondition(delegate.sent.count == 1 && delegate.sent[0].0 == "theme")
    precondition(delegate.sent[0].1["value"] as! String == "dark")
    delegate.applyingHostState = true
    delegate.applyCapsuleTheme(.light)
    precondition(delegate.sent.count == 1 && delegate.capsuleState.theme == .light,
                 "Applying incoming host theme must not produce an IPC loop")
default: fatalError("Unknown settings test")
}
delegate.compactTimer?.invalidate()
print(CommandLine.arguments[1] + " passed")
'''
        cls.directory = tempfile.TemporaryDirectory(prefix='compact settings ')
        cls.addClassCleanup(cls.directory.cleanup)
        root = Path(cls.directory.name)
        source = root / 'main.swift'
        source.write_text(fixture)
        cls.binary = root / 'check'
        compiled = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(source), '-o', str(cls.binary)],
                                  capture_output=True, text=True, timeout=90)
        if compiled.returncode:
            raise AssertionError(compiled.stderr)

    def run_case(self, name):
        result = subprocess.run([str(self.binary), name], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(name + ' passed', result.stdout)

    def test_pause_cancels_scans_and_settings_sync_does_not_echo(self):
        self.run_case('interval')

    def test_repeated_theme_switch_does_not_restart_or_scan(self):
        self.run_case('theme')

    def test_pause_and_resume_remember_a_custom_seventeen_second_interval(self):
        self.run_case('pause-restore')

    def test_failed_change_restores_local_pause_and_custom_interval_over_ipc(self):
        self.run_case('failure-restore')

    def test_rollback_identity_protects_newer_host_choices_including_aba(self):
        self.run_case('rollback-identity')

    def test_late_settings_responses_cannot_override_a_newer_revision(self):
        self.run_case('late-response')

    def test_same_value_or_aba_host_change_gets_a_new_worker_request(self):
        self.run_case('host-state-revision')

    def test_host_metadata_sync_does_not_post_settings_or_echo(self):
        self.run_case('host-state-metadata')


if __name__ == '__main__':
    unittest.main()
