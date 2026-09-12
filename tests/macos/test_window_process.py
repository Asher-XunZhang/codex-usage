"""Real coordinator code with isolated defaults and synthetic workspace/IPC."""
from tools.common.paths import ROOT, BACKEND, macos_source
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS toolchain')
class WindowProcessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix='window coordinator ')
        cls.addClassCleanup(cls.temp.cleanup)
        root = Path(cls.temp.name)
        swift = root / 'main.swift'
        swift.write_text(r'''import AppKit
func check(_ value: @autoclosure () -> Bool, _ message: String) { precondition(value(), message) }
func pump() { RunLoop.main.run(until: Date().addingTimeInterval(0.03)) }
final class NSRunningApplication {
    struct Options: OptionSet { let rawValue: Int; static let activateAllWindows = Options(rawValue: 1), activateIgnoringOtherApps = Options(rawValue: 2) }
    static var apps: [NSRunningApplication] = []
    let bundleIdentifier: String?, processIdentifier: Int32
    var isTerminated = false, activations = 0, terminations = 0
    init(_ id: String, _ pid: Int32) { bundleIdentifier = id; processIdentifier = pid }
    static func runningApplications(withBundleIdentifier id: String) -> [NSRunningApplication] { apps.filter { $0.bundleIdentifier == id } }
    func activate(options: Options) { activations += 1 }
    func terminate() { terminations += 1 }
    func forceTerminate() { terminations += 1; NSWorkspace.shared.end(self) }
}
final class NSWorkspace {
    final class OpenConfiguration { var activates = false }
    static let shared = NSWorkspace()
    static let didTerminateApplicationNotification = Notification.Name("test.workspace.terminated")
    static let didLaunchApplicationNotification = Notification.Name("test.workspace.launched")
    static let applicationUserInfoKey = "application"
    let notificationCenter = NotificationCenter()
    var opens: [(URL, Bool, (NSRunningApplication?, Error?) -> Void)] = []
    func openApplication(at url: URL, configuration: OpenConfiguration, completionHandler: @escaping (NSRunningApplication?, Error?) -> Void) { opens.append((url, configuration.activates, completionHandler)) }
    func complete(_ app: NSRunningApplication?) {
        let next = opens.removeFirst()
        if let app = app { NSRunningApplication.apps.append(app); notificationCenter.post(name: Self.didLaunchApplicationNotification, object: nil, userInfo: [Self.applicationUserInfoKey: app]) }
        next.2(app, nil); pump()
    }
    func end(_ app: NSRunningApplication) { app.isTerminated = true; notificationCenter.post(name: Self.didTerminateApplicationNotification, object: nil, userInfo: [Self.applicationUserInfoKey: app]); pump() }
}
final class DistributedNotificationCenter {
    static let shared = DistributedNotificationCenter()
    static func `default`() -> DistributedNotificationCenter { shared }
    let center = NotificationCenter()
    func addObserver(forName name: Notification.Name?, object: Any?, queue: OperationQueue?, using callback: @escaping (Notification) -> Void) -> NSObjectProtocol { center.addObserver(forName: name, object: object, queue: queue, using: callback) }
    func removeObserver(_ observer: Any) { center.removeObserver(observer) }
    func postNotificationName(_ name: Notification.Name, object: String?, userInfo: [AnyHashable: Any]?, deliverImmediately: Bool) { center.post(name: name, object: object, userInfo: userInfo) }
}
let suite = "local.codex-usage.test.window-process." + UUID().uuidString
let prefs = UserDefaults(suiteName: suite)!
defer { prefs.removePersistentDomain(forName: suite) }
let appURL = URL(fileURLWithPath: "/synthetic/Codex Usage.app")
let hostApp = NSRunningApplication("local.codex-usage.desktop", 100)
NSRunningApplication.apps = [hostApp]
func host() -> WindowProcessCoordinator { WindowProcessCoordinator(isMain: false, preferences: prefs, namespace: suite, processID: 100, appURL: appURL) }
func helper() -> WindowProcessCoordinator { WindowProcessCoordinator(isMain: true, preferences: prefs, namespace: suite, processID: 200, appURL: appURL) }
func closingMessage(_ pid: Int32, at time: TimeInterval) {
    let body = try! JSONSerialization.data(withJSONObject: ["at": time])
    DistributedNotificationCenter.default().postNotificationName(Notification.Name(suite + ".host"), object: suite, userInfo: ["sender": NSNumber(value: pid), "action": "mainClosing", "body": body], deliverImmediately: true)
}
switch CommandLine.arguments[1] {
case "paths":
    let nested = appURL.appendingPathComponent("Contents/Helpers/CodexUsageMain.app")
    check(usageHostBundleURL(bundleURL: nested, isMain: true).path == appURL.path, "Nested helper resources resolve to the real parent app")
    check(usageHostBundleURL(bundleURL: appURL, isMain: false) == appURL, "Host path stays unchanged")
case "messages":
    let host = host(), main = helper()
    var hostActions: [String] = [], mainActions: [String] = [], states: [[String: Any]] = []
    host.actionReceived = { action, _ in hostActions.append(action); if action == "requestState" { host.publishState(["refreshSeconds": 0, "theme": "light", "nullable": NSNull()]) } }
    main.actionReceived = { action, _ in mainActions.append(action) }
    main.stateReceived = { states.append($0) }
    host.start(); main.start()
    check(hostActions == ["requestState"] && states.count == 1 && states[0]["refreshSeconds"] as? Int == 0, "Helper asks for a small JSON state, including null values")
    main.sendToHost("dataChanged"); host.sendToMain("refresh"); main.publishState([:]); host.sendToHost("wrong")
    check(hostActions == ["requestState", "dataChanged"] && mainActions == ["refresh"] && states.count == 1, "Directions cannot echo actions or snapshots")
    host.stop(); main.sendToHost("late")
    check(hostActions.count == 2, "Stopped coordinator does not receive messages")
    main.stop()
case "launch":
    let host = host(); var closed = 0; host.mainClosed = { closed += 1 }; host.start()
    host.showMain(); host.showMain()
    check(host.mainIsRunning && NSWorkspace.shared.opens.count == 1, "A pending launch counts as running and cannot duplicate")
    NSWorkspace.shared.complete(nil)
    check(!host.mainIsRunning && closed == 1, "Failed open releases the host's native-only intent")
    host.showMain(); let app = NSRunningApplication("local.codex-usage.desktop.main", 200); NSWorkspace.shared.complete(app)
    host.showMain(); check(app.activations == 1 && NSWorkspace.shared.opens.isEmpty, "Existing Main focuses without a new process")
    NSWorkspace.shared.end(app)
    check(closed == 2 && NSWorkspace.shared.opens.isEmpty, "User closing Main does not automatically reopen it")
    host.stop()
case "close-reopen":
    let host = host(); var closed = 0, done = 0; host.mainClosed = { closed += 1 }; host.start(); host.showMain()
    let app = NSRunningApplication("local.codex-usage.desktop.main", 200); NSWorkspace.shared.complete(app)
    host.closeMain { done += 1 }; host.showMain()
    closingMessage(200, at: ProcessInfo.processInfo.systemUptime)
    check(done == 0 && app.terminations == 1 && app.activations == 0, "Cannot focus a helper that is already exiting")
    NSWorkspace.shared.end(app)
    check(done == 1 && closed == 0 && NSWorkspace.shared.opens.count == 1 && host.mainIsRunning, "Reopen intent launches only after old Main really exited")
    let replacement = NSRunningApplication("local.codex-usage.desktop.main", 201); NSWorkspace.shared.complete(replacement)
    host.closeMain { done += 1 }; NSWorkspace.shared.end(replacement)
    check(done == 2 && closed == 1 && !host.mainIsRunning, "Quit completion waits for actual Main termination")
    host.stop()
case "self-close-reopen":
    let host = host(); var closed = 0; host.mainClosed = { closed += 1 }; host.start(); host.showMain()
    let app = NSRunningApplication("local.codex-usage.desktop.main", 200); NSWorkspace.shared.complete(app)
    let main = helper(); main.start(); main.stop()
    host.showMain()
    check(app.activations == 0 && NSWorkspace.shared.opens.isEmpty, "A helper cleaning up after X must not be focused as if still usable")
    NSWorkspace.shared.end(app)
    check(closed == 0 && NSWorkspace.shared.opens.count == 1, "Clicking during helper cleanup reopens after its true exit")
    let replacement = NSRunningApplication("local.codex-usage.desktop.main", 201); NSWorkspace.shared.complete(replacement)
    closingMessage(201, at: ProcessInfo.processInfo.systemUptime)
    NSWorkspace.shared.end(replacement)
    check(closed == 1 && NSWorkspace.shared.opens.isEmpty, "An ordinary self-close remains closed without a new click")
    host.showMain(); let delayed = NSRunningApplication("local.codex-usage.desktop.main", 202); NSWorkspace.shared.complete(delayed)
    let began = ProcessInfo.processInfo.systemUptime
    pump(); host.showMain()
    closingMessage(202, at: began)
    NSWorkspace.shared.end(delayed)
    check(closed == 1 && NSWorkspace.shared.opens.count == 1, "Late closing messages preserve a newer open request")
    let latest = NSRunningApplication("local.codex-usage.desktop.main", 203); NSWorkspace.shared.complete(latest)
    host.closeMain(); NSWorkspace.shared.end(latest); host.stop()
case "duplicate-main":
    let host = host(); var closed = 0; host.mainClosed = { closed += 1 }; host.start(); host.showMain()
    let app = NSRunningApplication("local.codex-usage.desktop.main", 200); NSWorkspace.shared.complete(app)
    let duplicate = NSRunningApplication("local.codex-usage.desktop.main", 201)
    NSRunningApplication.apps.append(duplicate)
    NSWorkspace.shared.notificationCenter.post(name: NSWorkspace.didLaunchApplicationNotification, object: nil, userInfo: [NSWorkspace.applicationUserInfoKey: duplicate])
    closingMessage(201, at: ProcessInfo.processInfo.systemUptime)
    NSWorkspace.shared.end(duplicate)
    host.showMain()
    check(closed == 0 && host.mainIsRunning && app.activations == 1 && NSWorkspace.shared.opens.isEmpty, "A duplicate helper registering then exiting cannot replace or close the real Main")
    NSWorkspace.shared.end(app); check(closed == 1, "The real Main remains tracked until its own exit")
    host.stop()
case "pending-close":
    let host = host(); var done = 0; host.start(); host.showMain(); host.closeMain { done += 1 }
    let app = NSRunningApplication("local.codex-usage.desktop.main", 200); NSWorkspace.shared.complete(app)
    check(done == 0 && app.terminations == 1, "Close during pending open terminates the eventual owned helper")
    NSWorkspace.shared.end(app); check(done == 1, "Pending close completes only after termination")
    host.showMain(); host.closeMain { done += 1 }; host.showMain()
    let oldPending = NSRunningApplication("local.codex-usage.desktop.main", 201); NSWorkspace.shared.complete(oldPending)
    check(oldPending.terminations == 1 && done == 1, "Reopen cannot silently abandon an in-flight close deadline")
    NSWorkspace.shared.end(oldPending)
    check(done == 2 && NSWorkspace.shared.opens.count == 1, "Pending close-then-reopen starts a fresh helper after cleanup")
    let latest = NSRunningApplication("local.codex-usage.desktop.main", 202); NSWorkspace.shared.complete(latest)
    host.closeMain(); NSWorkspace.shared.end(latest)
    host.stop()
case "standalone":
    NSRunningApplication.apps = []
    let main = helper(); var commands: [String] = []; main.actionReceived = { commands.append($0) }; main.start()
    check(NSWorkspace.shared.opens.count == 1 && NSWorkspace.shared.opens[0].0 == appURL && !NSWorkspace.shared.opens[0].1, "Finder-started Main starts its accessory host without activation")
    NSWorkspace.shared.complete(hostApp)
    let duplicate = NSRunningApplication("local.codex-usage.desktop", 101); NSWorkspace.shared.end(duplicate)
    check(commands.isEmpty, "An unrelated duplicate host exiting must not close Main")
    NSWorkspace.shared.end(hostApp)
    check(commands == ["close"], "Unexpected host death closes Main through normal cleanup")
    main.stop()
default: fatalError("Unknown case")
}
print(CommandLine.arguments[1] + " passed")
''')
        # Correct two-argument callback while keeping the fixture compact.
        swift.write_text(swift.read_text().replace('main.actionReceived = { commands.append($0) }', 'main.actionReceived = { action, _ in commands.append(action) }'))
        cls.binary = root / 'check'
        built = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(macos_source('WindowProcess.swift')), str(swift), '-o', str(cls.binary)], capture_output=True, text=True, timeout=90)
        if built.returncode:
            raise AssertionError(built.stderr)

    def run_case(self, case):
        result = subprocess.run([str(self.binary), case], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_parent_paths(self): self.run_case('paths')
    def test_ipc_direction_and_release(self): self.run_case('messages')
    def test_launch_failure_focus_and_user_close(self): self.run_case('launch')
    def test_close_then_reopen_intent_waits_for_old_helper_exit(self): self.run_case('close-reopen')
    def test_self_close_and_late_notification_preserve_new_open_intent(self): self.run_case('self-close-reopen')
    def test_duplicate_helper_cannot_replace_real_instance(self): self.run_case('duplicate-main')
    def test_close_while_launch_pending(self): self.run_case('pending-close')
    def test_standalone_helper_and_host_death(self): self.run_case('standalone')
