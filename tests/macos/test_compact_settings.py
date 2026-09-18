"""Exercise the current host/helper settings callbacks with isolated dependencies."""
from tools.common.paths import ROOT, BACKEND, macos_source
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from tests.macos.test_floating_period import declaration


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class CompactSettingsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = (macos_source('Main.swift')).read_text()
        methods = ''.join(declaration(source, marker) for marker in [
            '    func applyInterval(', '    func flushIntervalSettings(', '    func scheduleCompact()',
            '    func applyCapsuleTheme(',
        ])
        fixture = r'''import AppKit
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
var isMainWindowProcess = false
let suite = "local.codex-usage.test-compact-settings." + UUID().uuidString
let usagePreferences = UserDefaults(suiteName: suite)!
defer { usagePreferences.removePersistentDomain(forName: suite) }
typealias Object = [String: Any]
enum CapsuleTheme: String { case dark, light }
final class ArcColorEditor { func updateTheme(_ theme: CapsuleTheme) {} }
final class State { var theme = CapsuleTheme.light, refreshSeconds = 5 }
final class Cancel { var cancelled = false; func cancel() { cancelled = true } }
final class Backend { var url: URL?, stopping = false }
final class Collector { var busy = false, stopping = false }
final class Monitor { var isWatching = true }
final class Fixture {
    var arcColorEditor: ArcColorEditor?
    let settingsQueue = MainSettingsQueue()
    let capsuleState = State(), backend = Backend(), collector = Collector(), compactMonitor = Monitor()
    var autoSeconds = 5 { didSet { capsuleState.refreshSeconds = autoSeconds } }
    var compactMode = true, compactDirty = true, terminating = false, applyingHostState = false
    var settingsID = 0, menuUpdates = 0, broadcasts = 0, scans = 0, manualScans = 0
    var compactLastScan = Date(), compactTimer: Timer?, settingsCommand: Cancel?
    var statusMenu: NSMenu?, statusText = "", snapshot: Object = [:]
    var sent: [(String, Object)] = []
    var posts: [(Result<Object, Error>) -> Void] = []
    // There are intentionally no window creation, teardown or process restart
    // methods: changing these settings must leave their owners alive.
    func rebuildIntervalPicker() {}
    func sendHost(_ action: String, payload: Object = [:]) { sent.append((action, payload)) }
    func publishHostState() { broadcasts += 1 }
    func manualRefresh() { manualScans += 1 }
    func fetchCompact(manual: Bool) { scans += 1 }
    func loadFloating() {}
    func loadCompact() {}
    func render(_ data: Object) {}
    func updateCapsuleThemeChecks(_ menu: NSMenu?) { menuUpdates += 1 }
    func post(_ path: String, body: Object, completion: @escaping (Result<Object, Error>) -> Void) -> Cancel? {
        posts.append(completion); return Cancel()
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
    precondition(!scheduled.isValid && delegate.compactTimer == nil)
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
    precondition(delegate.settingsQueue.pending == 0 && delegate.posts.count == 1)
    delegate.flushIntervalSettings()
    delegate.posts[1](.success([:]))
    precondition(delegate.autoSeconds == 0 && delegate.manualScans == 1)
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
        compiled = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(source), str(macos_source('MainState.swift')), '-o', str(cls.binary)],
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


if __name__ == '__main__':
    unittest.main()
