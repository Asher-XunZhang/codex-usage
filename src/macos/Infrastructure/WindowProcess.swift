import AppKit
import Foundation

// init(suiteName:) does not accept the current application's own bundle ID.
// Standard defaults already target that domain; only the helper needs a suite.
let usagePreferences: UserDefaults = {
    if Bundle.main.bundleIdentifier == "local.codex-usage.desktop" { return .standard }
    guard let shared = UserDefaults(suiteName: "local.codex-usage.desktop") else {
        fputs("Cannot access the shared Codex Usage preferences domain\n", stderr)
        exit(1)
    }
    return shared
}()
let isMainWindowProcess = Bundle.main.bundleIdentifier == "local.codex-usage.desktop.main"

func usageHostBundleURL(bundleURL: URL, isMain: Bool) -> URL {
    isMain ? bundleURL.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent() : bundleURL
}
let usageAppURL = usageHostBundleURL(bundleURL: Bundle.main.bundleURL, isMain: isMainWindowProcess)
let usageResourcesURL = usageAppURL.appendingPathComponent("Contents/Resources")
let usageMacOSURL = usageAppURL.appendingPathComponent("Contents/MacOS")

/// The host owns status/float/quota. The nested application owns only Main.
/// Notifications contain small JSON payloads; directions and sender IDs prevent
/// snapshot/action echoes. No conversation records or credentials cross here.
final class WindowProcessCoordinator {
    var actionReceived: ((String, [String: Any]) -> Void)?
    var stateReceived: (([String: Any]) -> Void)?
    var mainClosed: (() -> Void)?
    var launchFailed: ((String) -> Void)?
    private let isMain: Bool
    private let preferences: UserDefaults
    private let center: DistributedNotificationCenter
    private let namespace: String
    private let processID: Int32
    private let appURL: URL
    private var observers: [NSObjectProtocol] = []
    private var workspaceObservers: [NSObjectProtocol] = []
    private var running: NSRunningApplication?
    private var hostPID: Int32?
    private var openPending = false
    private var wanted = false
    private var lastShowTime: TimeInterval = 0
    private var closing = false
    private var closeGeneration = 0
    private var started = false
    private var launchGeneration = 0
    private var closeCallbacks: [(Bool) -> Void] = []
    private var closeDeadline: DispatchWorkItem?
    var mainIsRunning: Bool { openPending || running.map { !$0.isTerminated } == true }
    var mainPID: Int32? { running.flatMap { $0.isTerminated ? nil : $0.processIdentifier } }

    init(isMain: Bool = isMainWindowProcess, preferences: UserDefaults = usagePreferences,
         center: DistributedNotificationCenter = .default(), namespace: String = "local.codex-usage.desktop",
         processID: Int32 = getpid(), appURL: URL = usageAppURL) {
        self.isMain = isMain; self.preferences = preferences; self.center = center
        self.namespace = namespace; self.processID = processID; self.appURL = appURL
    }
    private var hostID: String { "local.codex-usage.desktop" }
    private var mainID: String { hostID + ".main" }
    private func application(_ bundle: String) -> NSRunningApplication? {
        NSRunningApplication.runningApplications(withBundleIdentifier: bundle).first { !$0.isTerminated }
    }
    func start() {
        guard !started else { return }; started = true
        let direction = isMain ? "main" : "host"
        observers.append(center.addObserver(forName: Notification.Name(namespace + "." + direction), object: namespace, queue: .main) { [weak self] in self?.receive($0, state: false) })
        if isMain {
            observers.append(center.addObserver(forName: Notification.Name(namespace + ".state"), object: namespace, queue: .main) { [weak self] in self?.receive($0, state: true) })
            hostPID = application(hostID)?.processIdentifier
        } else { running = application(mainID) }
        let workspace = NSWorkspace.shared.notificationCenter
        workspaceObservers.append(workspace.addObserver(forName: NSWorkspace.didTerminateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            guard let self = self, self.started, let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication else { return }
            if self.isMain && app.bundleIdentifier == self.hostID && app.processIdentifier == self.hostPID {
                self.actionReceived?("close", [:])
            } else if !self.isMain && app.bundleIdentifier == self.mainID,
                      self.running?.processIdentifier == app.processIdentifier {
                let reopen = self.closing && self.wanted
                self.running = nil; self.closing = false; self.finishClose()
                if reopen && self.started { self.showMain() } else { self.mainClosed?() }
            }
        })
        if !isMain {
            workspaceObservers.append(workspace.addObserver(forName: NSWorkspace.didLaunchApplicationNotification, object: nil, queue: .main) { [weak self] note in
                guard let self = self, self.started, let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication,
                      app.bundleIdentifier == self.mainID else { return }
                // A duplicate helper briefly registers before its singleton
                // check exits. It must not replace the real Main's identity.
                if self.running == nil || self.running?.isTerminated == true { self.running = app }
            })
        } else if application(hostID) != nil { sendToHost("requestState") }
        else {
            // A Finder launch of the nested app also brings up its stable host.
            let config = NSWorkspace.OpenConfiguration(); config.activates = false
            NSWorkspace.shared.openApplication(at: appURL, configuration: config) { [weak self] app, error in
                DispatchQueue.main.async {
                    guard let self = self, self.started else { return }
                    if let app = app, !app.isTerminated { self.hostPID = app.processIdentifier; self.sendToHost("requestState") }
                    else { self.launchFailed?(error?.localizedDescription ?? "常驻组件启动失败"); self.actionReceived?("close", [:]) }
                }
            }
        }
    }
    private func receive(_ notification: Notification, state: Bool) {
        guard started, let info = notification.userInfo, let sender = info["sender"] as? NSNumber,
              sender.int32Value != processID, let bytes = info["body"] as? Data, bytes.count <= 131_072,
              let payload = try? JSONSerialization.jsonObject(with: bytes) as? [String: Any] else { return }
        preferences.synchronize()
        if state { stateReceived?(payload) }
        else if let action = info["action"] as? String, action.count <= 80 {
            if !isMain && action == "closeBlocked", running?.processIdentifier == sender.int32Value {
                closing = false; wanted = true; finishClose(success: false)
                launchFailed?(payload["message"] as? String ?? "主面板尚有未保存内容"); return
            }
            if !isMain && action == "mainClosing", running?.processIdentifier == sender.int32Value {
                if !closing {
                    closing = true
                    // A new click can arrive before the queued notification.
                    // Compare boot-relative clocks shared by both processes.
                    let began = (payload["at"] as? NSNumber)?.doubleValue
                    wanted = began.map { $0.isFinite && lastShowTime > $0 } ?? false
                }
                return
            }
            if !isMain && action == "requestState" { running = application(mainID) ?? running }
            actionReceived?(action, payload)
        }
    }
    private func post(_ direction: String, action: String, payload: [String: Any]) {
        guard started, JSONSerialization.isValidJSONObject(payload),
              let bytes = try? JSONSerialization.data(withJSONObject: payload), bytes.count <= 131_072 else { return }
        preferences.synchronize()
        center.postNotificationName(Notification.Name(namespace + "." + direction), object: namespace,
            userInfo: ["sender": NSNumber(value: processID), "action": action, "body": bytes], deliverImmediately: true)
    }
    func sendToHost(_ action: String, payload: [String: Any] = [:]) { guard isMain else { return }; post("host", action: action, payload: payload) }
    func sendToMain(_ action: String, payload: [String: Any] = [:]) { guard !isMain else { return }; post("main", action: action, payload: payload) }
    func publishState(_ state: [String: Any]) { guard !isMain else { return }; post("state", action: "state", payload: state) }

    func showMain() {
        guard started, !isMain else { return }; wanted = true
        lastShowTime = ProcessInfo.processInfo.systemUptime
        if closing { return }
        if let app = running, !app.isTerminated { app.activate(options: [.activateAllWindows, .activateIgnoringOtherApps]); return }
        guard !openPending else { return }
        openPending = true; launchGeneration += 1; let ticket = launchGeneration
        let config = NSWorkspace.OpenConfiguration(); config.activates = true
        let helper = appURL.appendingPathComponent("Contents/Helpers/CodexUsageMain.app")
        NSWorkspace.shared.openApplication(at: helper, configuration: config) { [weak self] app, error in
            DispatchQueue.main.async {
                guard let self = self, self.started, self.launchGeneration == ticket else { app?.terminate(); return }
                self.openPending = false
                guard let app = app, !app.isTerminated else {
                    self.running = nil; self.wanted = false; self.closing = false; self.finishClose()
                    self.launchFailed?(error?.localizedDescription ?? "主面板启动失败"); self.mainClosed?(); return
                }
                self.running = app
                if self.closing || !self.wanted { self.closing = true; self.sendToMain("close"); app.terminate() }
            }
        }
    }
    func closeMain(completion: ((Bool) -> Void)? = nil) {
        guard !isMain else { completion?(true); return }
        if let completion = completion { closeCallbacks.append(completion) }
        wanted = false
        guard mainIsRunning else { closing = false; finishClose(); return }
        closing = true; closeGeneration += 1; let ticket = closeGeneration
        sendToMain("close"); running?.terminate()
        closeDeadline?.cancel()
        let deadline = DispatchWorkItem { [weak self] in
            guard let self = self, self.closeGeneration == ticket else { return }
            if let app = self.running, !app.isTerminated {
                self.closing = false; self.wanted = true; self.finishClose(success: false)
                self.launchFailed?("主面板未确认保存和关闭，请重试。")
            } else { self.closing = false; self.finishClose() }
        }
        closeDeadline = deadline; DispatchQueue.main.asyncAfter(deadline: .now() + 8, execute: deadline)
    }
    private func finishClose(success: Bool = true) {
        closeGeneration += 1
        closeDeadline?.cancel(); closeDeadline = nil
        let callbacks = closeCallbacks; closeCallbacks = []; callbacks.forEach { $0(success) }
    }
    func stop() {
        guard started else { return }
        if isMain { sendToHost("mainClosing", payload: ["at": ProcessInfo.processInfo.systemUptime]) }
        started = false; wanted = false; launchGeneration += 1
        observers.forEach { center.removeObserver($0) }; observers = []
        workspaceObservers.forEach { NSWorkspace.shared.notificationCenter.removeObserver($0) }; workspaceObservers = []
        closeDeadline?.cancel(); closeDeadline = nil
        finishClose()
    }
}
