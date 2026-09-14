import AppKit
import UserNotifications

/// One bounded, native, read-only cache query for all active budgets.
final class BudgetQueryReader {
    private var child: Process?
    private var generation = 0
    private var deadline: DispatchWorkItem?
    private var files: [URL] = []
    func cancel() {
        generation += 1; deadline?.cancel(); deadline = nil
        if let process = child, process.isRunning {
            process.terminate()
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                if process.isRunning { kill(process.processIdentifier, SIGKILL) }
            }
        }
        child = nil
        for file in files { try? FileManager.default.removeItem(at: file) }; files = []
    }
    func read(cache: URL, requests: [Object], completion: @escaping (Object?, String?) -> Void) {
        cancel(); let ticket = generation
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("codex-budgets-\(UUID().uuidString)")
        let input = root.appendingPathExtension("in.json"), output = root.appendingPathExtension("out.json")
        files = [input, output]
        do {
            let data = try JSONSerialization.data(withJSONObject: ["requests": requests])
            guard data.count <= 65536 else { throw NSError(domain: "Budget", code: 1, userInfo: [NSLocalizedDescriptionKey: "预算查询过大"]) }
            for (url, contents) in [(input, data), (output, Data())] {
                guard FileManager.default.createFile(atPath: url.path, contents: contents, attributes: [.posixPermissions: 0o600]) else {
                    throw NSError(domain: "Budget", code: 2, userInfo: [NSLocalizedDescriptionKey: "无法创建预算查询缓存"])
                }
            }
            let handle = try FileHandle(forWritingTo: output)
            let process = Process(); child = process
            process.executableURL = usageMacOSURL.appendingPathComponent("CodexSummary")
            process.arguments = ["--cache-path", cache.path, "--budgets-file", input.path]
            process.standardOutput = handle; process.standardError = FileHandle.nullDevice
            process.terminationHandler = { [weak self] finished in
                DispatchQueue.main.async {
                    guard let self = self, self.generation == ticket else { return }
                    self.deadline?.cancel(); self.deadline = nil; self.child = nil
                    self.files = []
                    defer { for file in [input, output] { try? FileManager.default.removeItem(at: file) } }
                    guard finished.terminationStatus == 0,
                          let size = (try? output.resourceValues(forKeys: [.fileSizeKey]))?.fileSize, size <= 4194304,
                          let data = try? Data(contentsOf: output),
                          let result = try? JSONSerialization.jsonObject(with: data) as? Object else {
                        completion(nil, "预算数据尚未就绪，请刷新 Token 数据"); return
                    }
                    completion(result, nil)
                }
            }
            try process.run(); try? handle.close()
            let timeout = DispatchWorkItem { [weak self] in
                guard let self = self, self.generation == ticket else { return }
                self.cancel(); completion(nil, "预算查询超时，请重试")
            }
            deadline = timeout; DispatchQueue.main.asyncAfter(deadline: .now() + 8, execute: timeout)
        } catch { cancel(); completion(nil, error.localizedDescription) }
    }
}

/// Host-owned store, one boundary timer, and a coalesced query. UI processes
/// receive snapshots; neither a hidden view nor Python is needed to monitor.
final class BudgetCoordinator: NSObject, UNUserNotificationCenterDelegate {
    let store: BudgetStore
    let reader = BudgetQueryReader()
    var changed: (() -> Void)?
    var navigate: ((String?) -> Void)?
    var visibleBudget: (() -> String?)?
    private var source = ""
    private var cache: URL?
    private var quota: Object?
    private var timer: Timer?
    private var querying = false
    private var queued = false
    private var stopped = false
    private var delivering = Set<String>()
    private var eligibleAlerts: [Object] = []
    private var lastKey: Data?
    private(set) var queryError: String?
    init(url: URL) { store = BudgetStore(url: url); super.init() }
    func start(source: String, cache: URL) {
        self.source = source; self.cache = cache
        refresh(force: true)
    }
    func stop() { stopped = true; timer?.invalidate(); timer = nil; reader.cancel() }
    var state: Object {
        var result: Object = ["rules": store.rules, "summaries": store.summaries, "events": store.events, "source": source, "recovery": store.recovery]
        if let quota = quota { result["quota"] = quota }
        if let error = store.persistenceError ?? queryError { result["error"] = error }
        return result
    }
    func setSource(_ value: String, cache: URL) {
        if source != value { reader.cancel(); querying = false; queued = false; lastKey = nil }
        source = value; self.cache = cache; evaluate(nil); refresh(force: true)
    }
    func updateQuota(_ snapshot: QuotaSnapshot) {
        quota = ["updated_at": snapshot.updated?.timeIntervalSince1970 ?? 0,
                 "stale": snapshot.stale || snapshot.error != nil,
                 "windows": snapshot.windows.map { item -> Object in
                    var value: Object = ["duration_minutes": item.durationMinutes, "remaining": item.remaining]
                    if let date = item.resetsAt { value["resets_at"] = date.timeIntervalSince1970 }; return value
                 }]
        evaluate(nil)
    }
    func apply(_ action: String, payload: Object) throws {
        try store.apply(action: action, payload: payload, source: source)
        if action == "save" || action == "upsert" || action == "enable" || action == "toggle" {
            if store.rules.contains(where: { $0["enabled"] as? Bool == true }) {
                UNUserNotificationCenter.current().requestAuthorization(options: [.alert]) { [weak self] _, _ in
                    DispatchQueue.main.async { self?.deliverPending() }
                }
            }
        }
        lastKey = nil; evaluate(nil); refresh(force: true)
    }
    func refresh(stamp: String? = nil, force: Bool = false) {
        guard !stopped else { return }
        let requests = store.requests(source: source)
        scheduleBoundary(requests)
        guard !requests.isEmpty, let cache = cache else { evaluate(nil); return }
        let key = try? JSONSerialization.data(withJSONObject: ["requests": requests, "stamp": stamp ?? ""], options: [.sortedKeys])
        if !force, key == lastKey { return }
        if querying { queued = true; return }
        lastKey = key; querying = true
        let requestedSource = source
        reader.read(cache: cache, requests: requests) { [weak self] result, error in
            guard let self = self, !self.stopped, self.source == requestedSource else { return }
            self.querying = false; self.queryError = error
            if error != nil { self.lastKey = nil }
            self.evaluate(result)
            if self.queued { self.queued = false; self.refresh(force: true) }
        }
    }
    private func scheduleBoundary(_ requests: [Object]) {
        timer?.invalidate(); timer = nil
        let now = Date().timeIntervalSince1970
        let dates = (requests + store.summaries).flatMap { row in [row["start"], row["end"], row["pausedUntil"]].compactMap { ($0 as? NSNumber)?.doubleValue } }
        guard let next = dates.filter({ $0 > now + 0.01 }).min() else { return }
        timer = Timer(timeInterval: max(0.1, next - now + 0.05), repeats: false) { [weak self] _ in self?.refresh(force: true) }
        if let timer = timer { RunLoop.main.add(timer, forMode: .common) }
    }
    private func evaluate(_ result: Object?) {
        guard !stopped else { return }
        eligibleAlerts = store.evaluate(result: result, quota: quota, source: source)
        scheduleBoundary(store.requests(source: source))
        changed?(); deliverPending()
    }
    private func acknowledge(_ alerts: [Object]) {
        let ids = alerts.compactMap { $0["id"] as? String }
        do { try store.apply(action: "acknowledge", payload: ["ids": ids], source: source) }
        catch { queryError = error.localizedDescription }
        delivering.subtract(ids); changed?()
        eligibleAlerts.removeAll { ids.contains($0["id"] as? String ?? "") }
    }
    private func deliverPending() {
        guard !stopped, store.persistenceError == nil else { return }
        let alerts = eligibleAlerts.filter { !delivering.contains($0["id"] as? String ?? "") }
        guard !alerts.isEmpty else { return }
        let inline = alerts.filter { ($0["ruleID"] as? String) == visibleBudget?() }
        if !inline.isEmpty { acknowledge(inline) }
        let pending = alerts.filter { row in !inline.contains { ($0["id"] as? String) == (row["id"] as? String) } }
        guard !pending.isEmpty else { return }
        let ids = pending.compactMap { $0["id"] as? String }; delivering.formUnion(ids)
        UNUserNotificationCenter.current().getNotificationSettings { [weak self] settings in
            DispatchQueue.main.async {
                guard let self = self, !self.stopped else { return }
                let stillEligible = Set(self.eligibleAlerts.compactMap { $0["id"] as? String })
                guard ids.allSatisfy({ stillEligible.contains($0) }) else {
                    self.delivering.subtract(ids); self.deliverPending(); return
                }
                let nowInline = pending.filter { ($0["ruleID"] as? String) == self.visibleBudget?() }
                if !nowInline.isEmpty {
                    self.acknowledge(nowInline); self.delivering.subtract(ids); self.deliverPending(); return
                }
                if settings.authorizationStatus == .notDetermined { self.delivering.subtract(ids); return }
                // Static summaries remain visible when banners are unavailable.
                guard settings.authorizationStatus == .authorized || settings.authorizationStatus == .provisional else {
                    self.acknowledge(pending); return
                }
                let content = UNMutableNotificationContent()
                content.title = pending.count == 1 ? "预算提醒 · \(pending[0]["name"] as? String ?? "预算")" : "\(pending.count) 项预算需要关注"
                content.body = pending.prefix(3).map { row in
                    let prefix = pending.count > 1 ? (row["name"] as? String ?? "预算") + "：" : ""
                    return prefix + (row["message"] as? String ?? "预算已达到提醒阈值")
                }.joined(separator: "\n")
                content.sound = nil; content.categoryIdentifier = "budget"
                content.userInfo = ["ruleIDs": pending.compactMap { $0["ruleID"] as? String }]
                let request = UNNotificationRequest(identifier: "budget-" + ids.sorted().joined(separator: "-"), content: content, trigger: nil)
                UNUserNotificationCenter.current().add(request) { error in
                    DispatchQueue.main.async {
                        if let error = error { self.queryError = "通知暂未送达：\(error.localizedDescription)"; self.delivering.subtract(ids); self.changed?() }
                        else { self.acknowledge(pending) }
                    }
                }
            }
        }
    }
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list])
    }
    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        DispatchQueue.main.async {
            let ids = response.notification.request.content.userInfo["ruleIDs"] as? [String] ?? []
            if response.actionIdentifier == "pause30" || response.actionIdentifier == "pauseCycle" {
                for id in ids {
                    do { try self.apply("pause", payload: ["id": id, "mode": response.actionIdentifier == "pauseCycle" ? "cycle" : "duration", "durationSeconds": 1800]) }
                    catch { self.queryError = error.localizedDescription; self.changed?() }
                }
            } else if response.actionIdentifier == UNNotificationDefaultActionIdentifier { self.navigate?(ids.first) }
            completionHandler()
        }
    }
}

func budgetDisplayNumber(_ value: Any?, compact: Bool = false) -> String {
    guard let n = value as? NSNumber, n.doubleValue.isFinite else { return "—" }
    let v = n.doubleValue
    if compact {
        for (unit, divisor) in [("B", 1e9), ("M", 1e6), ("K", 1e3)] where abs(v) >= divisor { return String(format: "%.1f%@", v / divisor, unit) }
    }
    return v.rounded() == v ? String(format: "%.0f", v) : String(format: "%.2f", v)
}

extension AppDelegate {
    func refreshBudgets(manual: Bool) { budgetCoordinator?.refresh(stamp: (todaySnapshot["meta"] as? Object)?["generated_at"] as? String, force: manual) }
    func budgetSourceChanged() { budgetCoordinator?.setSource(codexHome.standardizedFileURL.path, cache: backend.cachePath(codexHome)) }
    func stopBudgetClients() {
        budgetModelChoices.cancel(); budgetTaskChoices.cancel(); budgetCoordinator?.stop()
        if isMainWindowProcess { saveBudgetDraft(); budgetMainVisible = false; sendHost("budgetViewing", payload: ["visible": false]) }
    }

    func startBudgets() {
        guard !isMainWindowProcess else { return }
        let coordinator = BudgetCoordinator(url: backend.root.appendingPathComponent("desktop/budgets.json"))
        budgetCoordinator = coordinator
        coordinator.changed = { [weak self] in self?.renderBudgetState(); self?.publishHostState() }
        coordinator.navigate = { [weak self] id in self?.openBudget(id) }
        coordinator.visibleBudget = { [weak self] in
            guard let self = self, self.windowProcesses.mainIsRunning,
                  let pid = self.windowProcesses.mainPID,
                  NSWorkspace.shared.frontmostApplication?.processIdentifier == pid,
                  self.budgetMainVisible else { return nil }
            return self.budgetMainViewing
        }
        coordinator.start(source: codexHome.standardizedFileURL.path, cache: backend.cachePath(codexHome))
        capsuleState.budgetMode = usagePreferences.bool(forKey: "floatingBudgetMode")
        capsuleState.monitorMode = usagePreferences.bool(forKey: "floatingMonitorMode")
        capsuleState.edgeShowsUsed = usagePreferences.string(forKey: "floatingEdgeMetric") == "used"
        capsuleState.budgetID = usagePreferences.string(forKey: "floatingBudgetID") ?? ""
        renderBudgetState()
    }
    var budgetHostState: Object {
        var state = budgetCoordinator?.state ?? budgetState
        state["refreshSeconds"] = autoSeconds
        return state
    }
    func installBudgetNavigation(root: NSView, usage: NSView) {
        usagePageView = usage
        let container = MainPageContainer()
        mainPageContainer = container
        container.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(container)
        let picker = FeedbackSegmentedControl(labels: ["用量统计", "预算与提醒", "任务监控"], trackingMode: .selectOne, target: self, action: #selector(budgetPageChanged(_:)))
        picker.setAccessibilityLabel("主面板页面"); pagePicker = picker
        picker.segmentStyle = .rounded
        picker.setWidth(110, forSegment: 0); picker.setWidth(110, forSegment: 1); picker.setWidth(100, forSegment: 2)
        picker.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(picker)
        let updates = FeedbackButton(title: "数据与更新…", target: self, action: #selector(showUpdateStatus)); updates.bezelStyle = .rounded
        updates.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(updates)
        NSLayoutConstraint.activate([
            updates.centerYAnchor.constraint(equalTo: picker.centerYAnchor), updates.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -26),
            picker.topAnchor.constraint(equalTo: root.topAnchor, constant: 16), picker.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 26),
            picker.heightAnchor.constraint(equalToConstant: 28),
            container.topAnchor.constraint(equalTo: picker.bottomAnchor, constant: 20), container.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -18),
            container.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 26), container.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -26)
        ])
        switchMainPage(usagePreferences.string(forKey: "mainPage") ?? "usage")
    }
    @objc func budgetPageChanged(_ sender: NSSegmentedControl) { switchMainPage(["usage", "budgets", "monitor"][max(0, min(2, sender.selectedSegment))]) }
    func switchBudgetPage(_ budgets: Bool) { switchMainPage(budgets ? "budgets" : "usage") }
    func switchMainPage(_ requested: String) {
        guard isMainWindowProcess, let container = mainPageContainer, let usage = usagePageView else { return }
        let page = ["usage", "budgets", "monitor"].contains(requested) ? requested : "usage"
        let changed = mainPage != page
        if page == "budgets", budgetPage == nil {
            let view = BudgetPage { [weak self] action, payload in self?.budgetPageAction(action, payload: payload) }
            budgetPage = view; view.update(budgetState)
            if let data = usagePreferences.data(forKey: "budgetDraftBook") ?? usagePreferences.data(forKey: "budgetDraft") { view.restoreDraftData(data) }
        }
        if page == "monitor", taskMonitorPage == nil {
            let view = TaskMonitorPage { [weak self] action, payload in self?.taskMonitorAction(action, payload: payload) }
            taskMonitorPage = view; view.update(taskMonitorState)
        }
        mainPage = page; pagePicker?.selectedSegment = ["usage", "budgets", "monitor"].firstIndex(of: page) ?? 0
        container.show(page == "budgets" ? budgetPage! : page == "monitor" ? taskMonitorPage! : usage)
        usagePreferences.set(mainPage, forKey: "mainPage")
        if changed && page != "usage" { releaseDetailData() }
        else if changed, backend.url != nil { loadUsage() }
        if page == "monitor" { taskMonitorPage?.request() }
        reportBudgetViewing()
    }
    func saveMainWindowFrame() {
        guard isMainWindowProcess, let window = window, !window.styleMask.contains(.fullScreen) else { return }
        usagePreferences.set(NSStringFromRect(window.frame), forKey: "mainWindowFrame")
    }
    // Restore once when the main window is created. Page navigation must never
    // resize, reposition or recreate the shared window.
    func restoreMainWindowFrame() {
        guard let window = window else { return }
        guard !window.styleMask.contains(.fullScreen) else { return }
        var frame = window.frame
        let previousPage = usagePreferences.string(forKey: "mainPage") == "budgets" ? "budgets" : "usage"
        var saved = usagePreferences.string(forKey: "mainWindowFrame")
            ?? usagePreferences.string(forKey: "mainPageFrame." + previousPage)
        if saved == nil, let legacy = usagePreferences.string(forKey: "NSWindow Frame CodexUsageMain") {
            let values = legacy.split(whereSeparator: { $0.isWhitespace }).compactMap { Double($0) }
            if values.count >= 4, values.prefix(4).allSatisfy({ $0.isFinite }), values[2] > 0, values[3] > 0 {
                saved = NSStringFromRect(NSRect(x: values[0], y: values[1], width: values[2], height: values[3]))
            }
        }
        if let saved = saved {
            let stored = NSRectFromString(saved)
            if stored.width.isFinite, stored.height.isFinite, stored.origin.x.isFinite, stored.origin.y.isFinite,
               stored.width > 0, stored.height > 0 { frame = stored }
        }
        frame.size.width = max(frame.width, window.minSize.width)
        frame.size.height = max(frame.height, window.minSize.height)
        let matchingScreen = NSScreen.screens.filter { $0.visibleFrame.intersects(frame) }.max { left, right in
            let a = left.visibleFrame.intersection(frame), b = right.visibleFrame.intersection(frame)
            return a.width * a.height < b.width * b.height
        }
        if let visible = (matchingScreen ?? window.screen ?? NSScreen.main)?.visibleFrame {
            frame.size.width = min(frame.width, visible.width); frame.size.height = min(frame.height, visible.height)
            frame.origin.x = max(visible.minX, min(frame.minX, visible.maxX - frame.width))
            frame.origin.y = max(visible.minY, min(frame.minY, visible.maxY - frame.height))
        }
        window.setFrame(frame, display: true)
        if saved != nil { usagePreferences.set(NSStringFromRect(frame), forKey: "mainWindowFrame") }
    }
    func saveBudgetDraft() {
        guard let page = budgetPage else { return }
        guard let draft = page.snapshotDraftBook() else { return }
        guard let data = try? JSONSerialization.data(withJSONObject: draft), data.count <= 524288 else {
            page.persistenceFailed("草稿过大，未覆盖已保存草稿；请减少模型价格或草稿数量后重试。"); return
        }
        usagePreferences.set(data, forKey: "budgetDraftBook")
        if usagePreferences.synchronize() { page.persistenceSucceeded() }
        else { page.persistenceFailed("草稿尚未写入本机，请重试保存草稿后关闭窗口。") }
    }
    func budgetPageAction(_ action: String, payload: Object) {
        switch action {
        case "recoverDrafts":
            let confirmation = NSAlert(); confirmation.messageText = "备份损坏草稿并重新开始？"
            confirmation.informativeText = "已保存的预算不会改变。原始草稿会保存到应用支持目录。"
            confirmation.addButton(withTitle: "备份并恢复"); confirmation.addButton(withTitle: "取消")
            confirmation.beginSheetModal(for: window) { [weak self] result in
                guard result == .alertFirstButtonReturn, let self = self else { return }
                do {
                    let directory = self.backend.root.appendingPathComponent("desktop")
                    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                    let backup = directory.appendingPathComponent("budget-drafts-damaged-\(UUID().uuidString).json")
                    guard let data = usagePreferences.data(forKey: "budgetDraftBook") ?? usagePreferences.data(forKey: "budgetDraft") else { return }
                    try data.write(to: backup, options: .withoutOverwriting)
                    self.budgetPage?.resetDraftBook(); self.saveBudgetDraft()
                    self.statusText = "原草稿已备份：\(backup.lastPathComponent)"
                } catch { self.budgetPage?.persistenceFailed("草稿备份失败：\(error.localizedDescription)") }
            }
        case "draftChanged": saveBudgetDraft()
        case "viewing": reportBudgetViewing()
        case "requestChoices": loadBudgetChoices()
        case "refresh": manualRefresh()
        default: sendHost("budgetAction", payload: ["action": action, "payload": payload])
        }
    }
    func loadBudgetChoices() {
        let query = FloatingUsageQuery(days: "all", model: "all", task: "all")
        for kind in ["model", "task"] {
            let reader = kind == "model" ? budgetModelChoices : budgetTaskChoices
            reader.load(cache: backend.cachePath(codexHome), query: query, kind: kind) { [weak self] choices, error in
                guard let self = self, !self.terminating else { return }
                if let choices = choices { self.budgetState[kind == "model" ? "models" : "tasks"] = choices.map { ["id": $0.0, "label": $0.1] } }
                if let error = error { self.budgetState["error"] = error }
                self.budgetPage?.update(self.budgetState)
            }
        }
    }
    func reportBudgetViewing() {
        guard isMainWindowProcess else { return }
        let visible = mainPage == "budgets" && window?.isVisible == true && window?.isKeyWindow == true && NSApp.isActive && window?.isMiniaturized != true && budgetPage?.editing != true
        sendHost("budgetViewing", payload: ["visible": visible, "id": budgetPage?.selectedBudgetID ?? ""])
    }
    func receiveBudgetState(_ state: Object) {
        let models = budgetState["models"], tasks = budgetState["tasks"]
        budgetState = state; budgetState["models"] = models; budgetState["tasks"] = tasks
        budgetPage?.update(budgetState)
        reportBudgetViewing()
    }
    // The existing bridge keeps its per-message bound. A versioned snapshot is
    // applied only after all bounded chunks arrive, never as a partial rule list.
    func publishBudgetState() {
        guard !isMainWindowProcess, windowProcesses.mainIsRunning,
              let data = try? JSONSerialization.data(withJSONObject: budgetHostState), data.count <= 524288 else { return }
        let id = UUID().uuidString, count = max(1, (data.count + 49151) / 49152)
        budgetPublishSequence += 1
        for index in 0..<count {
            let part = data.subdata(in: (index * 49152)..<min(data.count, (index + 1) * 49152))
            windowProcesses.sendToMain("budgetStatePart", payload: ["id": id, "sequence": budgetPublishSequence, "index": index, "count": count, "data": part.base64EncodedString()])
        }
    }
    func openBudget(_ id: String?, create: Bool = false) {
        if isMainWindowProcess {
            showDashboard(); switchBudgetPage(true); budgetPage?.navigate(budgetID: id, create: create); reportBudgetViewing(); return
        }
        pendingBudgetRoute = ["requestID": UUID().uuidString, "id": id ?? "", "create": create]
        openMainWindow(); sendBudgetRoute()
    }
    func sendBudgetRoute() {
        guard !isMainWindowProcess, windowProcesses.mainIsRunning, let route = pendingBudgetRoute else { return }
        windowProcesses.sendToMain("budgetRoute", payload: route)
    }
    func receiveBudgetAction(_ action: String, payload: Object) -> Bool {
        if isMainWindowProcess {
            if action == "budgetStatePart" {
                guard let id = payload["id"] as? String, let index = payload["index"] as? Int,
                      let sequence = payload["sequence"] as? Int, sequence > 0, sequence >= budgetPacketSequence,
                      let count = payload["count"] as? Int, (1...11).contains(count), (0..<count).contains(index),
                      let encoded = payload["data"] as? String, encoded.utf8.count <= 65536,
                      let data = Data(base64Encoded: encoded), data.count <= 49152 else { return true }
                if sequence > budgetPacketSequence {
                    budgetPackets = [:]; budgetPacketID = id; budgetPacketSequence = sequence; budgetPacketCount = count; budgetPacketComplete = false
                }
                guard !budgetPacketComplete, id == budgetPacketID, count == budgetPacketCount else { return true }
                budgetPackets[index] = data
                if budgetPackets.count == count {
                    var all = Data(); for n in 0..<count { guard let part = budgetPackets[n] else { return true }; all.append(part) }
                    budgetPackets = [:]; budgetPacketComplete = true
                    if all.count <= 524288, let state = try? JSONSerialization.jsonObject(with: all) as? Object { receiveBudgetState(state) }
                }
                return true
            }
            if action == "budgetRoute" {
                guard let id = payload["requestID"] as? String else { return true }
                if lastBudgetRouteID != id {
                    // An external navigation never discards an open form.
                    lastBudgetRouteID = id; openBudget((payload["id"] as? String).flatMap { $0.isEmpty ? nil : $0 }, create: payload["create"] as? Bool == true)
                }
                sendHost("budgetRouteAck", payload: ["requestID": id]); return true
            }
            if action == "budgetPinResult" {
                budgetPage?.acknowledgePin(id: payload["id"] as? String ?? "", requestID: payload["requestID"] as? String ?? "", error: payload["error"] as? String)
                return true
            }
            if action == "budgetResult" {
                budgetPage?.acknowledgeSave(id: payload["id"] as? String ?? "", revision: payload["revision"] as? Int,
                                             error: payload["error"] as? String, requestID: payload["requestID"] as? String)
                saveBudgetDraft(); return true
            }
            return false
        }
        switch action {
        case "budgetViewing": budgetMainVisible = payload["visible"] as? Bool == true; budgetMainViewing = payload["id"] as? String
        case "budgetRouteAck": if payload["requestID"] as? String == pendingBudgetRoute?["requestID"] as? String { pendingBudgetRoute = nil }
        case "budgetAction":
            let operation = payload["action"] as? String ?? "", body = payload["payload"] as? Object ?? [:]
            if operation == "pin", let id = body["id"] as? String {
                var reply: Object = ["id": id, "requestID": body["requestID"] ?? ""]
                if budgetCoordinator?.store.rules.contains(where: { $0["id"] as? String == id }) != true { reply["error"] = "预算已不存在，请刷新列表" }
                else {
                    selectFloatingBudget(id); showFloating()
                    if floating?.isVisible != true { reply["error"] = "窗口当前无法显示，请稍后重试" }
                }
                windowProcesses.sendToMain("budgetPinResult", payload: reply); return true
            }
            let id = (body["rule"] as? Object)?["id"] as? String ?? body["id"] as? String ?? ""
            var reply: Object = ["id": id]
            reply["requestID"] = body["requestID"]
            do {
                try budgetCoordinator?.apply(operation, payload: body)
                reply["revision"] = budgetCoordinator?.store.rules.first { $0["id"] as? String == id }?["revision"]
            } catch { reply["error"] = error.localizedDescription }
            publishHostState(); windowProcesses.sendToMain("budgetResult", payload: reply)
        default: return false
        }
        return true
    }
    func renderBudgetState() {
        let state = budgetHostState, summaries = state["summaries"] as? [Object] ?? []
        capsuleState.budgetOptions = (state["rules"] as? [Object] ?? []).map { ($0["id"] as? String ?? "", $0["name"] as? String ?? "预算") }
        let row = summaries.first { $0["id"] as? String == capsuleState.budgetID }
        capsuleState.budgetName = row?["name"] as? String ?? (capsuleState.budgetID.isEmpty ? "选择预算" : "预算已删除")
        capsuleState.budgetFraction = (row?["remainingPercent"] as? NSNumber).map { $0.doubleValue / 100 }
        let kind = row?["kind"] as? String ?? "token", currency = row?["currency"] as? String ?? "USD"
        let suffix = kind == "money" ? " \(currency)" : (kind == "quota" ? "%" : " Token")
        capsuleState.budgetUsed = (kind == "money" ? (currency == "CNY" ? "¥" : "$") : "") + budgetDisplayNumber(row?["used"], compact: true) + (kind == "quota" ? "%" : "")
        capsuleState.budgetRemaining = budgetDisplayNumber(row?["remaining"]) + suffix
        capsuleState.budgetAmount = budgetDisplayNumber(kind == "quota" ? row?["quotaFloor"] : row?["amount"]) + suffix
        capsuleState.budgetAmountLabel = kind == "quota" ? "提醒下限" : "限额"
        capsuleState.budgetStatus = row?["message"] as? String ?? row?["reason"] as? String ?? "在主面板新建或选择预算"
        capsuleState.budgetStale = row?["dataStatus"] as? String != "updated"
        let status = row?["status"] as? String ?? "unknown"
        capsuleState.budgetCaption = ["disabled": "已停用", "ended": "已结束", "scheduled": "待开始", "source_invalid": "目录变化", "scope_invalid": "范围失效", "unknown": "待更新", "partial": "部分数据"][status] ?? (kind == "quota" ? "窗口已用" : "预算已用")
        if row?["paused"] as? Bool == true { capsuleState.budgetStatus = "提醒已暂停 · " + capsuleState.budgetStatus }
        if kind != "quota", autoSeconds == 0 { capsuleState.budgetStatus = "自动更新暂停 · " + capsuleState.budgetStatus }
        else if let updated = row?["updatedAt"] as? NSNumber { capsuleState.budgetStatus += " · " + smallClock(Date(timeIntervalSince1970: updated.doubleValue)) }
        capsuleState.budgetPeriod = [row?["start"], row?["end"]].compactMap { ($0 as? NSNumber).map { smallClock(Date(timeIntervalSince1970: $0.doubleValue), includeDay: true) } }.joined(separator: " — ")
        let rule = row ?? (state["rules"] as? [Object] ?? []).first { $0["id"] as? String == capsuleState.budgetID }
        capsuleState.budgetScope = "\((rule?["model"] as? String).flatMap { $0 == "all" ? nil : $0 } ?? "全部模型") · \((rule?["task"] as? String).flatMap { $0 == "all" ? nil : $0 } ?? "全部任务")"
        if kind == "quota" { capsuleState.budgetScope = "账号级 · \(budgetDisplayNumber(row?["windowMinutes"])) 分钟官方窗口" }
        if let error = state["error"] as? String { capsuleState.budgetStatus = error; capsuleState.budgetStale = true }
    }
    func selectFloatingBudget(_ id: String) {
        capsuleState.budgetID = id; capsuleState.budgetMode = true; capsuleState.monitorMode = false
        usagePreferences.set(false, forKey: "floatingMonitorMode")
        usagePreferences.set(id, forKey: "floatingBudgetID"); usagePreferences.set(true, forKey: "floatingBudgetMode")
        renderBudgetState()
    }
    func floatingBudgetAction(_ action: String) -> Bool {
        if action.hasPrefix("content:") {
            capsuleState.monitorMode = action == "content:monitor"
            usagePreferences.set(capsuleState.monitorMode, forKey: "floatingMonitorMode")
            if !capsuleState.monitorMode { capsuleState.budgetMode = action == "content:budget" }; usagePreferences.set(capsuleState.budgetMode, forKey: "floatingBudgetMode"); renderBudgetState(); return true
        }
        if action.hasPrefix("budget:") {
            let id = String(action.dropFirst(7)); if id == "manage" { openBudget(nil) } else { selectFloatingBudget(id) }; return true
        }
        if action == "budgetEdit" { openBudget(capsuleState.budgetID); return true }
        if action == "budgetPause" {
            do { try budgetCoordinator?.apply("pause", payload: ["id": capsuleState.budgetID, "durationSeconds": 1800]) }
            catch { capsuleState.budgetStatus = error.localizedDescription }; return true
        }
        if action == "monitorOpen" || (action == "main" && capsuleState.monitorMode) { openTaskMonitor(); return true }
        if action == "main", capsuleState.budgetMode { openBudget(capsuleState.budgetID); return true }
        return false
    }
    func appendBudgetMenu(_ menu: NSMenu) {
        menu.addItem(.separator())
        let summaries = budgetCoordinator?.store.summaries ?? []
        for row in summaries.prefix(3) {
            let name = row["name"] as? String ?? "预算"
            let remaining = budgetDisplayNumber(row["remainingPercent"])
            let status = row["status"] as? String ?? "unknown"
            let caption = ["healthy", "warning", "exhausted", "exceeded"].contains(status) ? "剩余 \(remaining)%" : row["message"] as? String ?? "待更新"
            let item = NSMenuItem(title: "\(name) · \(caption)", action: #selector(budgetMenuSelected(_:)), keyEquivalent: "")
            item.target = self; item.representedObject = row["id"]; menu.addItem(item)
        }
        let manage = NSMenuItem(title: "预算与提醒…", action: #selector(budgetMenuSelected(_:)), keyEquivalent: "")
        manage.target = self; menu.addItem(manage)
    }
    @objc func budgetMenuSelected(_ sender: NSMenuItem) { openBudget(sender.representedObject as? String) }
    func windowDidBecomeKey(_ notification: Notification) { reportBudgetViewing() }
    func windowDidResignKey(_ notification: Notification) { reportBudgetViewing(); if notification.object as? NSWindow === floating { checkFloatPointer() } }
    func applicationDidBecomeActive(_ notification: Notification) { reportBudgetViewing(); if usageSettings?.window?.isVisible == true { usageSettings?.updatePermission() } }
    func applicationDidResignActive(_ notification: Notification) { reportBudgetViewing() }
}
