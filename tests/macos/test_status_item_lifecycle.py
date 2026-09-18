"""Actual host/helper controller methods with deterministic window/process I/O.

No installed preferences, GUI window, status item or subprocess are created.
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
    depth, end = 1, opening + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end] + '\n'


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class StatusItemLifecycleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = (macos_source('Main.swift')).read_text()
        methods = ''.join(declaration(source, marker) for marker in [
            '    func buildStatusItem()', '    func updateStatusTitle()',
            '    func sendHost(', '    func persistHostWindowMode()',
            '    func openMainWindow()', '    func closeMainWindow()',
            '    func hideFloating()', '    @objc func closeFloating()',
            '    @objc func hideDashboard()', '    @objc func onlyFloating()',
            '    @objc func onlyStatusBar()', '    @objc func showDashboard()',
            '    func windowWillClose(', '    func applicationDidHide(',
            '    func applicationShouldTerminateAfterLastWindowClosed(',
            '    @objc func quitApplication()', '    func applicationShouldTerminate(',
        ]).replace('usagePreferences', 'preferences')
        numbers = source[source.index('private func numeric('):source.index('private func label(')]
        termination = declaration((macos_source('Termination.swift')).read_text(), 'final class AsyncTermination')
        fixture = r'''import AppKit
import Darwin
typealias Object = [String: Any]
var isMainWindowProcess = false
let suite = "local.codex-usage.test-independent-windows." + UUID().uuidString
let preferences = UserDefaults(suiteName: suite)!
defer { preferences.removePersistentDomain(forName: suite) }
func check(_ value: @autoclosure () -> Bool, _ why: String) { precondition(value(), why) }
func drain() { RunLoop.main.run(until: Date().addingTimeInterval(0.03)) }
''' + numbers + r'''
final class FakeStatusButton {
    var toolTip: String?
    enum Position { case noImage, imageLeading }
    var title = "", image: NSImage?, font: NSFont?, imagePosition = Position.noImage
    var target: AnyObject?, action: Selector?
    func setAccessibilityLabel(_ value: String) {}
    func sendAction(on events: NSEvent.EventTypeMask) {}
}
final class NSStatusItem {
    static let variableLength: CGFloat = -1
    let button: FakeStatusButton? = FakeStatusButton()
    var autosaveName: String?, isVisible = false
}
final class NSStatusBar {
    static let system = NSStatusBar()
    var items: [NSStatusItem] = []
    func statusItem(withLength length: CGFloat) -> NSStatusItem {
        let item = NSStatusItem(); items.append(item); return item
    }
}
final class NSApplication {
    enum Policy { case regular, accessory }
    enum TerminateReply { case terminateNow, terminateCancel }
    var terminations = 0
    func setActivationPolicy(_ policy: Policy) {}
    func activate(ignoringOtherApps: Bool) {}
    func terminate(_ sender: Any?) { terminations += 1 }
}
let NSApp = NSApplication()
final class NSWindow: NSObject {
    var isVisible = true, isMiniaturized = false
    var delegate: AnyObject?, contentView: NSObject? = NSObject()
    func orderOut(_ sender: Any?) { isVisible = false }
    func close() { isVisible = false }
    func deminiaturize(_ sender: Any?) { isMiniaturized = false; isVisible = true }
    func makeKeyAndOrderFront(_ sender: Any?) { isVisible = true }
}
final class FakeButton { var title = "" }
final class FakeRequest { var cancelled = false; func cancel() { cancelled = true } }
final class FakeSession { func invalidateAndCancel() {} }
final class FakeAnimation { func stop() {} }
final class FakeDocking { func prepareToHide() {} }
final class FakePopover { func performClose(_ sender: Any?) {} }
final class FakeArcEditor { var closes = 0; func close() { closes += 1 } }
final class FakeCapsuleState { var arcStylePreview: String? = "unsaved preview" }
final class FakeWorker {
    let snapshot = (capsuleCompact: "周余 42%", detail: "合成额度")
    var stopCount = 0, url: URL? = URL(string: "http://127.0.0.1:1234")
    var completions: [() -> Void] = []
    func stop(_ done: @escaping () -> Void) { stopCount += 1; completions.append(done) }
    func finishStops() { let work = completions; completions = []; work.forEach { $0() } }
}
final class FakeCoordinator {
    var mainIsRunning = false, shows = 0, closes = 0, stops = 0
    var sent: [String] = [], completions: [(Bool) -> Void] = []
    func showMain() { shows += 1 }
    func closeMain(completion: ((Bool) -> Void)? = nil) { closes += 1; if !mainIsRunning { completion?(true) } else if let done = completion { completions.append(done) } }
    func sendToHost(_ action: String, payload: Object) { sent.append(action) }
    func stop() { stops += 1 }
    func finishClose(success: Bool = true) { mainIsRunning = !success; let work = completions; completions = []; work.forEach { $0(success) } }
}
''' + termination + r'''
final class Fixture: NSObject {
    var arcColorEditor: FakeArcEditor? = FakeArcEditor()
    let capsuleState = FakeCapsuleState()
    func updateStatusDetail() {}
    var floatPlacement: NSRect?, floatLastFrame: NSRect?
    var floatDocking: FakeDocking?
    var notificationRouter: FakeAnimation?, taskMonitor: FakeAnimation?
    var appearanceObservation: NSObject?
    var backendScanDeadline: DispatchWorkItem?
    var statusPopover: FakePopover?
    var waitingForMainClose = false, allowClose = true
    var mainWindowIntent = 0
    func prepareMainClose() -> Bool { allowClose }
    func stopBudgetClients() {}
    var savedMainFrames = 0
    func saveMainWindowFrame() { savedMainFrames += 1 }
    var statusItem: NSStatusItem?, floating: NSWindow? = NSWindow(), window: NSWindow!
    var dashboard: NSObject?, capsule: NSObject? = NSObject()
    var mainWindowOpen = false, terminating = false, compactMode = true, trayOnly = false
    var floatingButton: FakeButton? = FakeButton()
    var floatCollapse: DispatchWorkItem?, floatAnimation: FakeAnimation?, floatAnchor: NSPoint?
    var statusSingleClick: DispatchWorkItem?, trimWork: DispatchWorkItem?, hostRefreshDeadline: DispatchWorkItem?
    var floatingRequest: FakeRequest?, request: FakeRequest?, refreshCommand: FakeRequest?, summaryRequest: FakeRequest?, settingsCommand: FakeRequest?
    var activeSession: FakeSession?, floatingRequestID = 0
    var timer: Timer?, compactTimer: Timer?, manualTimer: Timer?, compactManualQueued = false
    var todaySnapshot: Object = ["summary": ["total_tokens": 12345]]
    let quotaReader = FakeWorker(), backend = FakeWorker(), collector = FakeWorker()
    let floatingChoices = FakeRequest(), nativeSummary = FakeRequest()
    let windowProcesses = FakeCoordinator(), termination = AsyncTermination()
    var nativeSummaryBusy = false, days = "7", model = "model-a", task = "all", group = "model"
    var publications = 0, collections = 0, windowBuilds = 0, workerStarts = 0
    @objc func statusClicked() {}
    func resetFloatInteraction() {}
    func stopFloatMouseMonitoring() {}
    func setFloatExpanded(_ value: Bool, animated: Bool) {}
    func stopCompactMonitoring() {}
    func fetchCompact(manual: Bool) { collections += 1 }
    func publishHostState() { publications += 1 }
    func releaseDetailData() {}
    func loadCompact() {}
    func loadUsage() {}
    func finishRefreshing() {}
    func poll() {}
    func start() { workerStarts += 1 }
    func reportHelperRefresh(success: Bool, message: String) {}
    func buildWindow() { windowBuilds += 1; dashboard = NSObject(); window = NSWindow() }
    func showFloating() {
        if isMainWindowProcess { sendHost("showFloating"); return }
        if floating == nil { floating = NSWindow() }
        floating?.isVisible = true; preferences.set(true, forKey: "floatingVisible"); persistHostWindowMode()
    }
''' + methods + r'''
}
func fresh(helper: Bool = false, floating: Bool = true) -> Fixture {
    preferences.removePersistentDomain(forName: suite)
    isMainWindowProcess = helper; NSStatusBar.system.items = []; NSApp.terminations = 0
    let value = Fixture()
    if helper { value.buildWindow(); value.compactMode = false; value.floating = nil; value.capsule = nil }
    else { value.floating?.isVisible = floating; preferences.set(floating, forKey: "floatingVisible") }
    value.buildStatusItem(); value.updateStatusTitle(); return value
}
func stable(_ value: Fixture, _ item: NSStatusItem) {
    check(value.statusItem === item && NSStatusBar.system.items.count == 1, "Ordinary actions preserve the exact host status item")
    check(NSApp.terminations == 0, "Ordinary actions must not terminate the host")
    check(value.dashboard == nil && value.windowBuilds == 0 && value.workerStarts == 0, "Host must never construct Main or start its persistent worker")
}
switch CommandLine.arguments[1] {
case "creation":
    let host = fresh(), item = host.statusItem!
    check(item.autosaveName == "CodexUsageStatusItem" && item.isVisible, "Host uses a stable status registration")
    check(item.button!.target === host && item.button!.title.contains("周余 42%"), "Status item preserves its action target and quota title")
    let helper = fresh(helper: true)
    check(helper.statusItem == nil && NSStatusBar.system.items.isEmpty, "Main helper cannot create a second status item")
    check(helper.applicationShouldTerminateAfterLastWindowClosed(NSApp), "The last Main window releases its process")
    isMainWindowProcess = false
    check(!host.applicationShouldTerminateAfterLastWindowClosed(NSApp), "No host windows still leaves the menu process alive")
case "independent-windows":
    for visible in [false, true] {
        let host = fresh(floating: visible), item = host.statusItem!, panel = host.floating!
        host.mainWindowOpen = true; host.windowProcesses.mainIsRunning = true
        host.hideDashboard()
        check(host.windowProcesses.closes == 1 && host.mainWindowOpen, "Hiding Main waits for confirmation")
        host.windowProcesses.finishClose()
        check(!host.mainWindowOpen, "Confirmed close updates window state")
        check(host.floating === panel && panel.isVisible == visible && preferences.bool(forKey: "floatingVisible") == visible, "Main close preserves the floating instance and visibility")
        check(host.collector.stopCount == 0 && host.quotaReader.stopCount == 0, "Main close does not stop host data or quota collection")
        stable(host, item)
        host.mainWindowOpen = true; host.closeFloating()
        check(host.windowProcesses.closes == 1 && host.mainWindowOpen, "Float close does not close Main")
        check(!preferences.bool(forKey: "floatingVisible") && host.floating?.isVisible == false, "Float close persists only its visibility")
        stable(host, item)
    }
case "open-main":
    let host = fresh(), item = host.statusItem!, panel = host.floating!
    host.showDashboard()
    check(host.mainWindowOpen && host.collector.stopCount == 1 && host.windowProcesses.shows == 0, "Launch waits for the short-lived host writer to stop")
    host.collector.finishStops()
    check(host.windowProcesses.shows == 1 && host.floating === panel && panel.isVisible, "Helper launch preserves the exact float")
    check(preferences.string(forKey: "displayMode") == "main" && host.quotaReader.stopCount == 0, "Main intent preserves quota ownership")
    host.windowProcesses.mainIsRunning = true; host.showDashboard()
    check(host.windowProcesses.shows == 2 && host.collector.stopCount == 1, "Reopening a running helper only focuses it")
    stable(host, item)
case "open-close-race":
    let host = fresh(), item = host.statusItem!
    host.showDashboard(); host.hideDashboard(); host.collector.finishStops()
    check(host.windowProcesses.shows == 0 && !host.mainWindowOpen, "Close during scanner shutdown cancels deferred helper launch")
    stable(host, item)
    host.showDashboard(); host.hideDashboard(); host.showDashboard(); host.collector.finishStops()
    check(host.mainWindowOpen && host.windowProcesses.shows > 0, "The latest open intent survives an earlier close")
    stable(host, item)
    let quitting = fresh(); quitting.showDashboard(); quitting.terminating = true; quitting.collector.finishStops()
    check(quitting.windowProcesses.shows == 0, "Quit wins over an unfinished helper-launch transition")
case "exclusive":
    let host = fresh(), item = host.statusItem!
    host.mainWindowOpen = true; host.onlyFloating()
    check(host.floating?.isVisible == true && host.windowProcesses.closes == 0 && host.mainWindowOpen && !item.isVisible, "Only-float changes resident entries without closing Main")
    host.onlyStatusBar()
    check(host.floating == nil && host.windowProcesses.closes == 0 && host.mainWindowOpen && item.isVisible, "Only-menu removes float while preserving Main")
    stable(host, item)
    let helper = fresh(helper: true), main = helper.window!
    helper.onlyStatusBar(); helper.onlyFloating(); helper.closeFloating()
    check(helper.windowProcesses.sent == ["onlyStatusBar", "onlyFloating", "hideFloating"], "Main routes host-owned controls through the bridge")
    check(helper.window === main && main.isVisible && NSApp.terminations == 0, "Sending a host command does not itself close Main")
case "helper-hide":
    let helper = fresh(helper: true), main = helper.window!
    helper.hideDashboard()
    check(main.isVisible && NSApp.terminations == 1 && helper.windowProcesses.sent.isEmpty, "Main hide requests termination before hiding, allowing save refusal")
    let hidden = fresh(helper: true); hidden.applicationDidHide(Notification(name: Notification.Name("hidden")))
    check(NSApp.terminations == 1, "Dock/system hide releases the helper like the shortcut")
    let closing = fresh(helper: true), old = closing.window!
    closing.windowWillClose(Notification(name: Notification.Name("closed"), object: old)); old.close(); drain()
    check(NSApp.terminations == 1 && closing.windowProcesses.sent.isEmpty, "Red-button close exits only the helper")
    let reopened = fresh(helper: true), original = reopened.window!
    reopened.windowWillClose(Notification(name: Notification.Name("closed"), object: original)); original.close(); reopened.showDashboard(); drain()
    check(reopened.window === original && original.isVisible && NSApp.terminations == 0, "A queued close callback cannot close a reopened window")
case "quit":
    let host = fresh(), item = host.statusItem!
    let editor = host.arcColorEditor!
    host.mainWindowOpen = true; host.windowProcesses.mainIsRunning = true
    check(host.applicationShouldTerminate(NSApp) == .terminateCancel && !host.terminating, "Host waits for Main save confirmation before cleanup")
    check(host.windowProcesses.closes == 1 && host.collector.stopCount == 0, "Host remains usable while Main can refuse close")
    host.windowProcesses.finishClose(success: false); drain()
    check(!host.terminating && !host.waitingForMainClose && NSApp.terminations == 0, "A rejected close leaves host usable")
    check(editor.closes == 0 && host.capsuleState.arcStylePreview != nil, "A refused quit preserves the color draft")
    _ = host.applicationShouldTerminate(NSApp)
    host.windowProcesses.finishClose(); drain()
    check(NSApp.terminations == 1, "Confirmed Main close requests host termination again")
    _ = host.applicationShouldTerminate(NSApp)
    check(host.terminating && host.collector.stopCount == 1 && host.quotaReader.stopCount == 1 && host.backend.stopCount == 1, "Confirmed Quit cleans owned processes")
    check(editor.closes == 1 && host.arcColorEditor == nil && host.capsuleState.arcStylePreview == nil, "Confirmed quit closes the editor and discards live preview")
    host.collector.finishStops(); host.quotaReader.finishStops(); host.backend.finishStops(); drain()
    check(NSApp.terminations == 2 && host.windowProcesses.stops == 1 && host.statusItem === item, "Completed cleanup resumes termination")
    check(host.applicationShouldTerminate(NSApp) == .terminateNow, "Resumed Quit is idempotent")
    let helper = fresh(helper: true)
    check(helper.applicationShouldTerminate(NSApp) == .terminateCancel, "Helper close waits for its Python worker")
    check(helper.savedMainFrames == 1 && host.savedMainFrames == 0, "Only Main persists the shared window frame at close")
    check(helper.backend.stopCount == 1 && helper.collector.stopCount == 0 && helper.quotaReader.stopCount == 0 && helper.windowProcesses.closes == 0, "Helper cleanup cannot stop host processes")
    check(preferences.string(forKey: "filterDays") == "7" && preferences.string(forKey: "filterModel") == "model-a", "Helper saves independent Main filters")
    helper.backend.finishStops(); drain(); check(NSApp.terminations == 1, "Main exits after its worker is gone")
    let explicit = fresh(helper: true); explicit.quitApplication()
    check(explicit.windowProcesses.sent == ["quit"] && NSApp.terminations == 1, "Explicit Quit alone asks the host to exit too")
default: fatalError("Unknown case")
}
print(CommandLine.arguments[1] + " passed")
'''
        cls.directory = tempfile.TemporaryDirectory(prefix='independent status windows ')
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

    def test_only_host_registers_status(self): self.run_case('creation')
    def test_hiding_each_window_preserves_the_other(self): self.run_case('independent-windows')
    def test_opening_main_waits_for_writer_and_preserves_float(self): self.run_case('open-main')
    def test_pending_open_close_and_quit_intent(self): self.run_case('open-close-race')
    def test_explicit_exclusive_actions_are_bridged(self): self.run_case('exclusive')
    def test_helper_hide_and_close_leave_host_untouched(self): self.run_case('helper-hide')
    def test_quit_waits_for_correct_owned_processes(self): self.run_case('quit')


if __name__ == '__main__':
    unittest.main()
