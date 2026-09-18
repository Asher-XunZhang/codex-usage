import AppKit
import Foundation

private let accent = NSColor(calibratedRed: 0.06, green: 0.55, blue: 0.46, alpha: 1)
private let violet = NSColor(calibratedRed: 0.48, green: 0.40, blue: 0.82, alpha: 1)
private let bundleID = "local.codex-usage.desktop"
#if arch(arm64)
private let nativeArchitecture = "Apple Silicon 原生"
#else
private let nativeArchitecture = "Intel 原生"
#endif
typealias Object = [String: Any]

/// The floating window owns all three filters independently of the main page.
struct FloatingUsageQuery: Equatable {
    static let periods = ["1", "7", "30", "90", "all"]
    let days: String
    let model: String
    let task: String
    init(days: String, model: String, task: String) {
        self.days = days
        self.model = model
        self.task = task
    }
    static func restoredDays(_ preferences: UserDefaults) -> String {
        if let saved = preferences.string(forKey: "floatingDays"), periods.contains(saved) { return saved }
        let previous = preferences.string(forKey: "filterDays") ?? "30"
        let migrated = preferences.bool(forKey: "floatingFollowFilters") ? (periods.contains(previous) ? previous : "30") : "1"
        // Freeze the old followed period once, before the main page can change it.
        preferences.set(migrated, forKey: "floatingDays")
        return migrated
    }
    static func restoredFilter(_ preferences: UserDefaults, key: String) -> String {
        precondition(key == "Model" || key == "Task")
        if let saved = preferences.string(forKey: "floating" + key), !saved.isEmpty { return saved }
        let previous = preferences.string(forKey: "filter" + key) ?? "all"
        let migrated = preferences.bool(forKey: "floatingFollowFilters") && !previous.isEmpty ? previous : "all"
        preferences.set(migrated, forKey: "floating" + key)
        return migrated
    }
    var queryItems: [URLQueryItem] {
        [URLQueryItem(name: "days", value: days), URLQueryItem(name: "model", value: model), URLQueryItem(name: "task", value: task)]
    }
}

private func numeric(_ value: Any?) -> Double? {
    guard let value = value as? NSNumber else { return nil }
    return value.doubleValue
}
private func exact(_ value: Any?) -> String {
    guard let n = numeric(value) else { return "未知" }
    let formatter = NumberFormatter()
    formatter.numberStyle = .decimal
    formatter.maximumFractionDigits = 0
    return formatter.string(from: NSNumber(value: n)) ?? "未知"
}
private func compact(_ value: Any?) -> String {
    guard let n = numeric(value) else { return "—" }
    if n >= 1_000_000_000 { return String(format: "%.2fB", n / 1_000_000_000) }
    if n >= 1_000_000 { return String(format: "%.2fM", n / 1_000_000) }
    if n >= 1_000 { return String(format: "%.1fK", n / 1_000) }
    return exact(value)
}
private func label(_ text: String, _ size: CGFloat = 13, _ weight: NSFont.Weight = .regular,
                   color: NSColor = .labelColor) -> NSTextField {
    let field = NSTextField(labelWithString: text)
    field.font = .systemFont(ofSize: size, weight: weight)
    field.textColor = color
    field.lineBreakMode = .byTruncatingTail
    field.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
    return field
}
private func stack(_ views: [NSView], vertical: Bool = false, spacing: CGFloat = 12) -> NSStackView {
    let s = NSStackView(views: views)
    s.orientation = vertical ? .vertical : .horizontal
    s.alignment = vertical ? .leading : .centerY
    s.spacing = spacing
    s.distribution = .fill
    return s
}
private func spacer() -> NSView { NSView() }

final class Panel: NSView {
    override func draw(_ dirtyRect: NSRect) {
        NSColor.controlBackgroundColor.setFill()
        let path = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 14, yRadius: 14)
        path.fill()
        NSColor.separatorColor.withAlphaComponent(0.4).setStroke()
        path.lineWidth = 1
        path.stroke()
    }
}

final class MetricCard: NSView {
    let value = label("—", 29, .semibold)
    let detail = label("等待本机统计", 11, color: .secondaryLabelColor)
    init(_ title: String, color: NSColor = .labelColor) {
        super.init(frame: .zero)
        let panel = Panel()
        panel.translatesAutoresizingMaskIntoConstraints = false
        addSubview(panel)
        value.textColor = color
        let content = stack([label(title, 12, .medium, color: .secondaryLabelColor), value, detail], vertical: true, spacing: 8)
        content.translatesAutoresizingMaskIntoConstraints = false
        addSubview(content)
        NSLayoutConstraint.activate([
            panel.topAnchor.constraint(equalTo: topAnchor), panel.bottomAnchor.constraint(equalTo: bottomAnchor),
            panel.leadingAnchor.constraint(equalTo: leadingAnchor), panel.trailingAnchor.constraint(equalTo: trailingAnchor),
            content.topAnchor.constraint(equalTo: topAnchor, constant: 18), content.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 18),
            content.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14), heightAnchor.constraint(equalToConstant: 114)
        ])
    }
    required init?(coder: NSCoder) { fatalError() }
    func update(_ n: Any?, suffix: String = "tokens") {
        value.stringValue = compact(n)
        detail.stringValue = "\(exact(n)) \(suffix)"
        toolTip = detail.stringValue
    }
}

/// Owns exactly one worker. All callbacks and mutable state stay on the main queue.
final class DashboardUI {
    let intervalPicker = FeedbackPopUpButton()
    let status = label("正在准备本机统计…", 12, color: .secondaryLabelColor)
    let coverage = label("仅统计这台 Mac 的已记录用量 · 日界线 UTC+08:00", 11, color: .secondaryLabelColor)
    let period = FeedbackSegmentedControl(labels: ["今天", "7 天", "30 天", "90 天", "全部"], trackingMode: .selectOne, target: nil, action: nil)
    let grouping = FeedbackSegmentedControl(labels: ["按模型", "按任务"], trackingMode: .selectOne, target: nil, action: nil)
    let models = FeedbackPopUpButton()
    let tasks = FeedbackPopUpButton()
    let search = NSSearchField()
    let table = NSTableView()
    let trend = TrendView()
    let totalCard = MetricCard("总 Token", color: accent)
    let inputCard = MetricCard("输入 Token")
    let outputCard = MetricCard("输出 Token", color: violet)
    let cacheCard = MetricCard("缓存输入")
    let callsCard = MetricCard("模型调用")
    let quotaText = label("正在读取账号额度…", 12, .medium)
    let quotaCards = label("重置卡数量未知", 12, .semibold)
    let rowCount = label("", 11, color: .secondaryLabelColor)
    var floatingButton: NSButton!
    var exportButton: NSButton!
    var refreshButton: NSButton!
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuDelegate, NSPopoverDelegate, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate {
    var dashboard: DashboardUI?
    var hostFloatingVisible = false
    var mainWindowOpen = false
    var mainWindowIntent = 0
    var hostQuotaDetail = "正在读取账号额度…"
    var hostQuotaResetLabel = "重置卡数量未知"
    var applyingHostState = false
    var budgetCoordinator: BudgetCoordinator?
    var taskMonitor: TaskMonitorService?
    var taskMonitorState: Object = [:]
    var taskMonitorPage: TaskMonitorPage?
    var notificationRouter: UsageNotificationRouter?
    var pendingTaskMonitorRoute = false
    var pendingTaskMonitorSection = "watches"
    var pendingTaskMonitorMessageIDs: [String]?
    var pendingTaskMonitorRouteID = ""
    var pendingTaskMonitorRouteSequence = 0, handledTaskMonitorRouteSequence = 0
    var budgetState: Object = [:]
    var budgetPage: BudgetPage?
    var usagePageView: NSView?
    var mainPageContainer: MainPageContainer?
    var pagePicker: NSSegmentedControl?
    var mainPage = "usage"
    var pendingBudgetRoute: Object?
    var lastBudgetRouteID: String?
    var budgetPacketID: String?
    var budgetPackets: [Int: Data] = [:]
    var budgetPublishSequence = 0
    var budgetPacketSequence = 0
    var budgetPacketCount = 0
    var budgetPacketComplete = false
    var budgetMainViewing: String?
    var budgetMainVisible = false
    let budgetModelChoices = FloatingChoicesReader()
    let budgetTaskChoices = FloatingChoicesReader()
    var publishedUsageStamp: String?
    lazy var windowProcesses = WindowProcessCoordinator()
    let nativeSummary = NativeSummaryReader()
    var nativeSummaryBusy = false
    var hostRefreshID: String?
    var hostRefreshDeadline: DispatchWorkItem?
    var helperRefreshIDs = Set<String>()
    var helperRefreshQueued = false
    var usedDashboard = false
    var recycling = false
    var statusText = "正在准备本机统计…" { didSet { dashboard?.status.stringValue = statusText } }
    var window: NSWindow!
    let backend = Backend()
    lazy var quotaReader = QuotaReader(root: backend.root.appendingPathComponent("desktop"))
    lazy var collector = CompactCollector(backend)
    let floatingChoices = FloatingChoicesReader()
    var compactMode = false
    var compactTimer: Timer?
    var compactManualQueued = false
    var compactSelectionQueued = false
    var compactDirty = true
    var compactDirtyGeneration = 0
    var compactEpoch = 0
    var compactLastScan = Date.distantPast
    lazy var compactMonitor = UsageChangeMonitor { [weak self] in self?.markCompactDirty() }
    var trimWork: DispatchWorkItem?
    var filteredSnapshot: Object = [:]
    var filteredSnapshotQuery: FloatingUsageQuery?
    private var activeSession: URLSession?
    var session: URLSession {
        if let current = activeSession { return current }
        let config = URLSessionConfiguration.ephemeral
        config.connectionProxyDictionary = [:]
        config.timeoutIntervalForRequest = 8
        config.timeoutIntervalForResource = 15
        config.requestCachePolicy = .reloadIgnoringLocalCacheData
        let created = URLSession(configuration: config)
        activeSession = created; return created
    }
    var timer: Timer?
    var backendFallbackDelay: TimeInterval = 5
    var backendScanDeadline: DispatchWorkItem?
    var statusItem: NSStatusItem?
    var statusMenuTracking = false
    var statusMenu: NSMenu?
    var statusPopover: NSPopover?
    var statusSingleClick: DispatchWorkItem?
    var menuOutsideMonitor: Any?
    var menuLocalMonitor: Any?
    var menuDeactivateObserver: NSObjectProtocol?
    var manualTimer: Timer?
    var trayOnly = false
    var todaySnapshot: Object = [:]
    var compactStamp: String?
    var summaryRequest: URLSessionDataTask?
    var summaryRequestID = 0
    var lastPoll = Date.distantPast
    var autoSeconds: Int = {
        let saved = usagePreferences.object(forKey: "refreshSeconds") as? Int ?? 5
        return (0...3600).contains(saved) ? saved : 5
    }() { didSet { capsuleState.refreshSeconds = autoSeconds } }
    var pendingRefresh: Int?
    var refreshStarted: Date?
    var manualBeganAt: Date?
    var manualExpectedStamp: String?
    var refreshCommand: URLSessionDataTask?
    var settingsCommand: URLSessionDataTask?
    var settingsID = 0
    let settingsQueue = MainSettingsQueue()
    var updateStatusController: UpdateStatusController?
    var usageSettings: UsageSettingsController?
    var arcColorEditor: ArcColorEditor?
    var appearanceObservation: NSKeyValueObservation?
    var localUpdate: Object = [:]
    var hostSettingsError = ""
    var intervalPicker: NSPopUpButton { dashboard!.intervalPicker }
    var floating: NSPanel?
    var capsule: CapsuleSurface?
    var floatAnimation: CapsuleAnimation?
    var floatAnchor: NSPoint?
    var floatPlacement: CapsulePlacement?
    var floatLastFrame: NSRect?
    var floatDocking: CapsuleDocking?
    var floatExpanded = false
    var floatMovingFrame = false
    var floatResettingInteraction = false
    var floatCollapse: DispatchWorkItem?
    var floatMouseMonitor: Any?
    var floatLocalMouseMonitor: Any?
    var floatingRequest: URLSessionDataTask?
    var floatingRequestID = 0
    var floatingButton: NSButton! { get { dashboard?.floatingButton } set { dashboard?.floatingButton = newValue } }
    let capsuleState = CapsuleState()
    var failures = 0
    var retries = 0
    var terminating = false
    var restarting = false
    var requestID = 0
    var request: URLSessionDataTask?
    var days = usagePreferences.string(forKey: "filterDays") ?? "30"
    var floatingDays = FloatingUsageQuery.restoredDays(usagePreferences)
    var floatingModel = FloatingUsageQuery.restoredFilter(usagePreferences, key: "Model")
    var floatingTask = FloatingUsageQuery.restoredFilter(usagePreferences, key: "Task")
    var floatingQuery: FloatingUsageQuery {
        FloatingUsageQuery(days: floatingDays, model: floatingModel, task: floatingTask)
    }
    var model = usagePreferences.string(forKey: "filterModel") ?? "all"
    var task = usagePreferences.string(forKey: "filterTask") ?? "all"
    var group = usagePreferences.string(forKey: "filterGroup") ?? "model"
    var snapshot: Object = [:]
    let usageSession = MainUsageSession()
    var usageIdentity: MainUsageIdentity {
        MainUsageIdentity(source: codexHome.standardizedFileURL.path, cache: backend.cachePath(codexHome).path,
                          generation: backend.generation, days: days, model: model, task: task, group: group)
    }
    var tableSelection: MainTableSelection {
        MainTableSelection(search: search.stringValue, sortKey: table.sortDescriptors.first?.key ?? "total_tokens",
                           ascending: table.sortDescriptors.first?.ascending ?? false)
    }
    var rows: [Object] = []
    var visibleRows: [Object] = []
    var tableRevision = 0
    var lastTableSelection: MainTableSelection?
    var codexHome: URL {
        if let path = usagePreferences.string(forKey: "codexHome") { return URL(fileURLWithPath: path) }
        if let path = ProcessInfo.processInfo.environment["CODEX_HOME"], !path.isEmpty { return URL(fileURLWithPath: path) }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
    }
    var status: NSTextField { dashboard!.status }
    var coverage: NSTextField { dashboard!.coverage }
    var period: NSSegmentedControl { dashboard!.period }
    var grouping: NSSegmentedControl { dashboard!.grouping }
    var models: NSPopUpButton { dashboard!.models }
    var tasks: NSPopUpButton { dashboard!.tasks }
    var search: NSSearchField { dashboard!.search }
    var table: NSTableView { dashboard!.table }
    var trend: TrendView { dashboard!.trend }
    var totalCard: MetricCard { dashboard!.totalCard }
    var inputCard: MetricCard { dashboard!.inputCard }
    var outputCard: MetricCard { dashboard!.outputCard }
    var cacheCard: MetricCard { dashboard!.cacheCard }
    var callsCard: MetricCard { dashboard!.callsCard }
    var rowCount: NSTextField { dashboard!.rowCount }
    var exportButton: NSButton! { get { dashboard?.exportButton } set { dashboard?.exportButton = newValue } }
    var refreshButton: NSButton! { get { dashboard?.refreshButton } set { dashboard?.refreshButton = newValue } }

    func applicationDidFinishLaunching(_ notification: Notification) {
        capsuleState.theme = CapsuleTheme(storedValue: usagePreferences.string(forKey: "capsuleTheme"))
        capsuleState.arcStyle = CapsuleArcStyle.load(usagePreferences).style
        capsuleState.refreshSeconds = autoSeconds
        capsuleState.scope = 1
        capsuleState.rangeDays = floatingDays
        capsuleState.selectedModel = floatingModel; capsuleState.selectedTask = floatingTask
        capsuleState.loadChoices = { [weak self] kind, completion in
            self?.loadFloatingChoices(kind, completion: completion)
        }
        applyAppAppearance()
        appearanceObservation = NSApp.observe(\.effectiveAppearance, options: [.new]) { [weak self] _, _ in
            DispatchQueue.main.async { self?.applyAppAppearance() }
        }
        installWindowBridge()
        buildMenu()
        backend.onExit = { [weak self] in self?.recover("统计进程已退出") }
        backend.onState = { [weak self] state in
            guard let self = self, isMainWindowProcess, !self.terminating else { return }
            self.timer?.invalidate(); self.timer = nil; self.backendFallbackDelay = 5
            self.handleBackendState(state)
        }
        backend.onEventsUnavailable = { [weak self] in self?.scheduleBackendFallback() }
        if isMainWindowProcess {
            buildWindow(); applyAppAppearance(); start()
            NSApp.activate(ignoringOtherApps: true)
            sendHost("requestState")
            handleMainRequest()
        } else {
            buildStatusItem()
            startBudgets()
            quotaReader.changed = { [weak self] snapshot in self?.renderQuota(snapshot) }
            quotaReader.setEnabled(usagePreferences.object(forKey: "quotaEnabled") as? Bool ?? true)
            quotaReader.start()
            let initialMode = usagePreferences.string(forKey: "displayMode") ?? "main"
            let residentMode = usagePreferences.string(forKey: "residentMode")
            trayOnly = initialMode == "menu"
            if initialMode == "floating" || (initialMode == "main" && usagePreferences.bool(forKey: "floatingVisible")) { showFloating() }
            if residentMode == "floating", floating?.isVisible == true { statusItem?.isVisible = false }
            enterCompactMode()
            if initialMode == "main" { openMainWindow() }
        }
    }
    func installWindowBridge() {
        windowProcesses.actionReceived = { [weak self] action, payload in self?.receiveWindowAction(action, payload: payload) }
        windowProcesses.stateReceived = { [weak self] state in self?.receiveHostState(state) }
        windowProcesses.mainClosed = { [weak self] in
            guard let self = self, !isMainWindowProcess, !self.terminating else { return }
            self.mainWindowOpen = false
            self.budgetMainVisible = false; self.budgetMainViewing = nil
            self.pendingBudgetRoute = nil
            self.persistHostWindowMode()
            if let id = self.hostRefreshID { self.completeHostRefresh(id: id, success: false, message: "主面板已关闭，已恢复后台采集") }
            self.compactDirty = true; self.compactDirtyGeneration += 1
            self.fetchCompact(manual: false); self.publishHostState()
        }
        windowProcesses.launchFailed = { [weak self] message in
            guard let self = self else { return }
            if !self.windowProcesses.mainIsRunning { self.mainWindowOpen = false; self.persistHostWindowMode() }
            self.capsuleState.status = message
        }
        windowProcesses.start()
    }
    func sendHost(_ action: String, payload: Object = [:]) {
        guard isMainWindowProcess else { return }
        usagePreferences.synchronize()
        windowProcesses.sendToHost(action, payload: payload)
    }
    func persistHostWindowMode() {
        guard !isMainWindowProcess else { return }
        trayOnly = !mainWindowOpen && floating?.isVisible != true
        usagePreferences.set(mainWindowOpen ? "main" : (trayOnly ? "menu" : "floating"), forKey: "displayMode")
    }
    func openMainWindow() {
        guard !isMainWindowProcess, !terminating else { return }
        mainWindowIntent += 1
        mainWindowOpen = true; persistHostWindowMode()
        if windowProcesses.mainIsRunning {
            windowProcesses.showMain(); publishHostState(); return
        }
        // The host stops only its short-lived scanner; its windows and quota
        // reader remain alive while the helper takes over log indexing.
        finishRefreshing(); compactManualQueued = false
        collector.stop { [weak self] in
            guard let self = self, self.mainWindowOpen, !self.terminating else { return }
            self.windowProcesses.showMain(); self.publishHostState()
            self.fetchCompact(manual: false)
        }
    }
    func closeMainWindow() {
        guard !isMainWindowProcess else { return }
        mainWindowIntent += 1; let intent = mainWindowIntent
        windowProcesses.closeMain { [weak self] closed in
            guard let self = self, self.mainWindowIntent == intent else { return }
            self.mainWindowOpen = !closed; self.persistHostWindowMode(); self.publishHostState()
        }
    }
    func publishHostState() {
        guard !isMainWindowProcess, !terminating else { return }
        let quota = quotaReader.snapshot
        usagePreferences.synchronize()
        windowProcesses.publishState([
            "floatingVisible": floating?.isVisible == true,
            "theme": capsuleState.theme.rawValue, "refreshSeconds": autoSeconds,
            "quotaDetail": quota.detail, "quotaResetLabel": quota.resetLabel
        ])
        publishBudgetState(); publishTaskMonitorState(); updateStatusDetail()
    }
    func receiveHostState(_ state: Object) {
        guard isMainWindowProcess, !terminating else { return }
        if let budgets = state["budgets"] as? Object { receiveBudgetState(budgets) }
        if let monitor = state["taskMonitor"] as? Object { taskMonitorState = monitor; taskMonitorPage?.update(monitor) }
        applyingHostState = true; defer { applyingHostState = false }
        if state["appearanceChanged"] as? Bool == true { applyAppAppearance() }
        hostFloatingVisible = state["floatingVisible"] as? Bool ?? hostFloatingVisible
        floatingButton?.title = hostFloatingVisible ? "隐藏浮窗" : "显示浮窗"
        hostQuotaDetail = state["quotaDetail"] as? String ?? hostQuotaDetail
        hostQuotaResetLabel = state["quotaResetLabel"] as? String ?? hostQuotaResetLabel
        dashboard?.quotaText.stringValue = hostQuotaDetail; dashboard?.quotaCards.stringValue = hostQuotaResetLabel
        if let value = state["theme"] as? String { applyCapsuleTheme(CapsuleTheme(storedValue: value)) }
        if let seconds = state["refreshSeconds"] as? Int, seconds != autoSeconds { applyInterval(seconds, refreshAfterChange: false) }
        usagePreferences.synchronize(); handleMainRequest()
    }
    func receiveWindowAction(_ action: String, payload: Object) {
        guard !terminating else { return }
        if receiveBudgetAction(action, payload: payload) { return }
        if receiveTaskMonitorAction(action, payload: payload) { return }
        if isMainWindowProcess {
            if action == "retrySettings" { retrySettings(); return }
            if action == "close" { hideDashboard() }
            else if action == "refresh" {
                if let id = payload["requestID"] as? String { helperRefreshIDs.insert(id) }
                if pendingRefresh != nil { return }
                if backend.url == nil || backend.starting || restarting {
                    helperRefreshQueued = true
                    if !backend.starting && !backend.stopping && !restarting { start() }
                } else { refreshLocal() }
            }
            else if action == "focus" { showDashboard(); handleMainRequest() }
            return
        }
        usagePreferences.synchronize()
        switch action {
        case "settingsWindow": showSettingsPage(payload["page"] as? String ?? "appearance")
        case "arcColors": showArcColors()
        case "updateStatus": showUpdateStatus()
        case "localUpdate":
            guard payload["source"] as? String == codexHome.standardizedFileURL.path else { return }
            localUpdate = payload; updateRefreshStatus()
        case "settingsResult": hostSettingsError = payload["error"] as? String ?? ""; updateRefreshStatus()
        case "requestState", "helperReady":
            mainWindowOpen = true; persistHostWindowMode()
            if hostRefreshID == nil { finishRefreshing() }
            collector.stop { [weak self] in
                guard let self = self, !self.terminating else { return }
                self.publishHostState(); self.sendBudgetRoute(); self.sendTaskMonitorRoute(); self.compactSelectionQueued = true; self.fetchCompact(manual: false)
                if let id = self.hostRefreshID {
                    self.windowProcesses.sendToMain("refresh", payload: ["requestID": id])
                }
            }
        case "showFloating": showFloating()
        case "hideFloating": hideFloating()
        case "toggleFloating": toggleFloating()
        case "onlyFloating": onlyFloating()
        case "onlyStatusBar": onlyStatusBar()
        case "statusMenu": openStatusMenu()
        case "theme":
            if let value = payload["value"] as? String { applyCapsuleTheme(CapsuleTheme(storedValue: value)) }
        case "interval":
            if let seconds = payload["seconds"] as? Int { applyInterval(seconds, refreshAfterChange: false) }
        case "refreshQuota": quotaReader.refresh(force: true)
        case "dataChanged":
            compactDirty = true; compactDirtyGeneration += 1; compactSelectionQueued = true
            fetchCompact(manual: false)
        case "refreshResult":
            if let id = payload["requestID"] as? String {
                completeHostRefresh(id: id, success: payload["success"] as? Bool == true,
                                    message: payload["message"] as? String ?? "刷新已结束")
            }
        case "homeChanged":
            localUpdate = [:]; updateRefreshStatus()
            budgetSourceChanged()
            stopCompactMonitoring(); compactMode = false; enterCompactMode()
        case "quit": quitApplication()
        default: break
        }
    }
    func handleMainRequest() {
        guard isMainWindowProcess, !terminating, dashboard != nil, window?.attachedSheet == nil else { return }
        if usagePreferences.bool(forKey: "openCustomInterval") {
            usagePreferences.set(false, forKey: "openCustomInterval")
            intervalPicker.selectItem(withTag: -1); intervalChanged()
        } else if usagePreferences.bool(forKey: "openChooseHome") {
            usagePreferences.set(false, forKey: "openChooseHome"); chooseHome()
        }
    }
    func requestHelperRefresh() {
        let id = UUID().uuidString
        hostRefreshID = id; pendingRefresh = -2; refreshStarted = Date()
        capsuleState.enabled = false; capsuleState.indicator = "busy"
        capsuleState.status = "正在通过主面板重新读取日志…"
        hostRefreshDeadline?.cancel()
        let timeout = DispatchWorkItem { [weak self] in
            self?.completeHostRefresh(id: id, success: false, message: "主面板扫描耗时较长，请稍后重试")
        }
        hostRefreshDeadline = timeout
        DispatchQueue.main.asyncAfter(deadline: .now() + 125, execute: timeout)
        windowProcesses.sendToMain("refresh", payload: ["requestID": id])
    }
    func completeHostRefresh(id: String, success: Bool, message: String) {
        guard !isMainWindowProcess, hostRefreshID == id else { return }
        hostRefreshID = nil; hostRefreshDeadline?.cancel(); hostRefreshDeadline = nil
        finishRefreshing(); capsuleState.status = message
        reportLocalUpdate(busy: false, error: success ? nil : message)
        if success { compactSelectionQueued = true; fetchCompact(manual: false) }
    }
    func reportHelperRefresh(success: Bool, message: String) {
        reportLocalUpdate(busy: false, error: success ? nil : message, stamp: success ? manualExpectedStamp : nil)
        guard isMainWindowProcess else { return }
        let ids = helperRefreshIDs; helperRefreshIDs = []; helperRefreshQueued = false
        for id in ids { sendHost("refreshResult", payload: ["requestID": id, "success": success, "message": message]) }
    }
    func reportLocalUpdate(busy: Bool, error: String? = nil, stamp: String? = nil) {
        let source = codexHome.standardizedFileURL.path
        var next = localUpdate["source"] as? String == source ? localUpdate : [:]
        next["source"] = source; next["busy"] = busy; next["error"] = error
        if let stamp = stamp { next["stamp"] = stamp }
        guard !NSDictionary(dictionary: next).isEqual(to: localUpdate) else { return }
        localUpdate = next
        if isMainWindowProcess { sendHost("localUpdate", payload: next) }
        else { updateRefreshStatus() }
    }
    func updateRefreshStatus() {
        updateStatusController?.update(local: localUpdate, quota: quotaReader.snapshot, enabled: quotaReader.enabled,
            busy: quotaReader.isRefreshing, seconds: autoSeconds, settingsError: hostSettingsError)
    }
    @objc func showUpdateStatus() { showSettingsPage("data") }
    func ensureUpdateStatus() {
        guard !isMainWindowProcess else { return }
        if updateStatusController == nil {
            let controller = UpdateStatusController(embedded: true); updateStatusController = controller
            controller.action = { [weak self] action, value in
                guard let self = self else { return }
                switch action {
                case "local": self.refreshLocal()
                case "quota": self.quotaReader.refresh(force: true)
                case "quotaEnabled":
                    usagePreferences.set(value == 1, forKey: "quotaEnabled"); self.quotaReader.setEnabled(value == 1)
                case "interval": self.applyInterval(value)
                case "settings": self.windowProcesses.sendToMain("retrySettings")
                default: break
                }
                self.updateRefreshStatus()
            }
        }
        updateRefreshStatus()
    }
    func buildMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem(); main.addItem(appItem)
        let appMenu = NSMenu(); appItem.submenu = appMenu
        appMenu.addItem(withTitle: "关于 Codex 用量", action: #selector(about), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "设置…", action: #selector(showSettings), keyEquivalent: ",")
        appMenu.addItem(withTitle: "选择 Codex 数据文件夹…", action: #selector(chooseHome), keyEquivalent: "")
        appMenu.addItem(withTitle: "打开支持文件夹", action: #selector(showSupport), keyEquivalent: "")
        appMenu.addItem(capsuleThemeMenuItem())
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "隐藏主面板", action: #selector(hideDashboard), keyEquivalent: "h")
        appMenu.addItem(withTitle: "退出 Codex 用量", action: #selector(quitApplication), keyEquivalent: "q")
        let fileItem = NSMenuItem(); main.addItem(fileItem)
        let file = NSMenu(title: "文件"); fileItem.submenu = file
        file.addItem(withTitle: "刷新", action: #selector(manualRefresh), keyEquivalent: "r")
        file.addItem(withTitle: "数据与更新…", action: #selector(showUpdateStatus), keyEquivalent: "")
        file.addItem(withTitle: "导出 CSV…", action: #selector(exportCSV), keyEquivalent: "e")
        file.addItem(withTitle: "重启统计", action: #selector(restart), keyEquivalent: "")
        file.addItem(.separator())
        file.addItem(withTitle: "显示 / 隐藏浮窗", action: #selector(toggleFloating), keyEquivalent: "m")
        file.addItem(withTitle: "显示主面板", action: #selector(showDashboard), keyEquivalent: "1")
        file.addItem(withTitle: "仅状态栏", action: #selector(onlyStatusBar), keyEquivalent: "2")
        file.addItem(withTitle: "显示状态栏菜单", action: #selector(openStatusMenu), keyEquivalent: "3")
        let editItem = NSMenuItem(); main.addItem(editItem)
        let edit = NSMenu(title: "编辑"); editItem.submenu = edit
        edit.addItem(withTitle: "剪切", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "复制", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "粘贴", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "全选", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        NSApp.mainMenu = main
        for menu in [appMenu, file] {
            for item in menu.items { item.target = self }
        }
    }
    func capsuleThemeMenuItem() -> NSMenuItem {
        let item = NSMenuItem(title: "胶囊主题", action: nil, keyEquivalent: "")
        let options = NSMenu(); item.submenu = options
        for theme in CapsuleTheme.allCases {
            let row = NSMenuItem(title: theme.title, action: #selector(selectCapsuleTheme(_:)), keyEquivalent: "")
            row.target = self; row.representedObject = theme.rawValue
            row.state = capsuleState.theme == theme ? .on : .off; options.addItem(row)
        }
        return item
    }
    @objc func selectCapsuleTheme(_ sender: NSMenuItem) {
        guard let value = sender.representedObject as? String, let theme = CapsuleTheme(rawValue: value) else { return }
        applyCapsuleTheme(theme)
    }
    func applyCapsuleTheme(_ theme: CapsuleTheme) {
        capsuleState.theme = theme
        arcColorEditor?.updateTheme(theme)
        if !applyingHostState { usagePreferences.set(theme.rawValue, forKey: "capsuleTheme"); usagePreferences.set(theme.rawValue, forKey: "appearanceFloating") }
        updateCapsuleThemeChecks(NSApp.mainMenu); updateCapsuleThemeChecks(statusMenu)
        if isMainWindowProcess {
            if !applyingHostState { sendHost("theme", payload: ["value": theme.rawValue]) }
        } else { publishHostState() }
    }
    func updateCapsuleThemeChecks(_ menu: NSMenu?) {
        guard let menu = menu else { return }
        for row in menu.items {
            if row.action == #selector(selectCapsuleTheme(_:)) {
                row.state = (row.representedObject as? String) == capsuleState.theme.rawValue ? .on : .off
            }
            if let submenu = row.submenu { updateCapsuleThemeChecks(submenu) }
        }
    }

    @objc func showArcColors() {
        if isMainWindowProcess { sendHost("arcColors"); return }
        if let editor = arcColorEditor, editor.window?.isVisible == true {
            editor.showWindow(nil); NSApp.activate(ignoringOtherApps: true); return
        }
        let stored = CapsuleArcStyle.load(usagePreferences)
        let editor = ArcColorEditor(style: capsuleState.arcStyle, theme: capsuleState.theme,
                                    fraction: capsuleState.normalizedQuota, warning: stored.error)
        arcColorEditor = editor
        editor.preview = { [weak self] style in self?.capsuleState.arcStylePreview = style }
        editor.save = { [weak self] style in
            guard let self = self, !self.terminating else { return "应用正在退出，配色未保存。" }
            if let error = style.save(usagePreferences) { return error }
            self.capsuleState.arcStyle = style
            return nil
        }
        editor.closed = { [weak self, weak editor] in
            DispatchQueue.main.async {
                guard let self = self, self.arcColorEditor === editor else { return }
                self.arcColorEditor = nil
            }
        }
        editor.showWindow(nil); NSApp.activate(ignoringOtherApps: true)
    }
    func buildWindow() {
        guard isMainWindowProcess, dashboard == nil else { return }
        usedDashboard = true
        dashboard = DashboardUI()
        dashboard?.status.stringValue = statusText
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 780), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Codex 用量"
        window.subtitle = "本机统计 · \(nativeArchitecture)"
        window.contentMinSize = NSSize(width: 1040, height: 718)
        window.center()
        window.isReleasedWhenClosed = false
        window.delegate = self
        restoreMainWindowFrame()
        let root = window.contentView!
        let title = stack([label("Codex 用量", 27, .bold), spacer(), status])
        status.setAccessibilityIdentifier("connectionStatus")
        intervalPicker.target = self; intervalPicker.action = #selector(intervalChanged(_:))
        intervalPicker.setAccessibilityLabel("自动刷新间隔")
        intervalPicker.widthAnchor.constraint(equalToConstant: 130).isActive = true
        rebuildIntervalPicker()
        floatingButton = FeedbackButton(title: (isMainWindowProcess ? hostFloatingVisible : floating?.isVisible == true) ? "隐藏浮窗" : "显示浮窗", target: self, action: #selector(toggleFloating))
        floatingButton?.bezelStyle = .rounded
        let trayButton = FeedbackButton(title: "仅状态栏", target: self, action: #selector(onlyStatusBar)); trayButton.bezelStyle = .rounded
        let subtitle = stack([label("看清每次思考的用量。", 13, color: .secondaryLabelColor), spacer(), label("自动刷新", 11, color: .secondaryLabelColor), intervalPicker, floatingButton, trayButton])
        period.selectedSegment = ["1", "7", "30", "90", "all"].firstIndex(of: days) ?? 2; period.target = self; period.action = #selector(filterChanged)
        period.setAccessibilityLabel("时间范围")
        models.addItem(withTitle: "全部模型"); models.target = self; models.action = #selector(filterChanged); models.setAccessibilityLabel("模型筛选")
        tasks.addItem(withTitle: "全部任务"); tasks.target = self; tasks.action = #selector(filterChanged); tasks.setAccessibilityLabel("任务筛选")
        models.widthAnchor.constraint(equalToConstant: 170).isActive = true
        tasks.widthAnchor.constraint(equalToConstant: 260).isActive = true
        refreshButton = FeedbackButton(title: "刷新", target: self, action: #selector(manualRefresh))
        refreshButton?.bezelStyle = .rounded
        exportButton = FeedbackButton(title: "导出 CSV…", target: self, action: #selector(exportCSV)); exportButton?.bezelStyle = .rounded
        exportButton?.isEnabled = false
        let reset = FeedbackButton(title: "重置筛选", target: self, action: #selector(resetMainFilters)); reset.bezelStyle = .rounded
        let filters = stack([period, models, tasks, reset, spacer(), refreshButton], spacing: 8)
        let cards = stack([totalCard, inputCard, outputCard, cacheCard, callsCard])
        cards.distribution = .fillEqually
        let chartPanel = Panel()
        let chartHeight = chartPanel.heightAnchor.constraint(equalToConstant: 217)
        chartHeight.priority = .defaultLow; chartHeight.isActive = true
        chartPanel.heightAnchor.constraint(greaterThanOrEqualToConstant: 100).isActive = true
        let chartTitle = stack([label("每日趋势", 14, .semibold), spacer(), label("● 输入", 11, color: accent), label("● 输出", 11, color: violet)])
        let chartContent = stack([chartTitle, trend], vertical: true, spacing: 8)
        chartContent.translatesAutoresizingMaskIntoConstraints = false; chartPanel.addSubview(chartContent)
        NSLayoutConstraint.activate([
            chartContent.leadingAnchor.constraint(equalTo: chartPanel.leadingAnchor, constant: 18), chartContent.trailingAnchor.constraint(equalTo: chartPanel.trailingAnchor, constant: -18),
            chartContent.topAnchor.constraint(equalTo: chartPanel.topAnchor, constant: 16), chartContent.bottomAnchor.constraint(equalTo: chartPanel.bottomAnchor, constant: -12),
            chartTitle.widthAnchor.constraint(equalTo: chartContent.widthAnchor), trend.widthAnchor.constraint(equalTo: chartContent.widthAnchor)
        ])
        grouping.selectedSegment = group == "task" ? 1 : 0; grouping.target = self; grouping.action = #selector(filterChanged)
        search.placeholderString = "搜索明细"; search.delegate = self; search.sendsSearchStringImmediately = true
        search.setAccessibilityLabel("搜索明细")
        search.widthAnchor.constraint(equalToConstant: 200).isActive = true
        let detailHeader = stack([label("用量明细", 14, .semibold), grouping, rowCount, spacer(), search, exportButton])
        table.dataSource = self; table.delegate = self
        table.usesAlternatingRowBackgroundColors = true; table.rowHeight = 32
        table.style = .inset; table.columnAutoresizingStyle = .lastColumnOnlyAutoresizingStyle
        table.setAccessibilityLabel("Token 用量明细")
        let columns = [("label", "模型 / 任务", 265.0), ("input_tokens", "输入", 146.0), ("cached_input_tokens", "缓存输入", 146.0),
                       ("noncached_input_tokens", "非缓存输入", 146.0), ("output_tokens", "输出（含推理）", 126.0), ("total_tokens", "总计", 146.0)]
        for (key, title, width) in columns {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(key)); column.title = title
            column.width = CGFloat(width); column.minWidth = key == "label" ? 170 : 100
            column.sortDescriptorPrototype = NSSortDescriptor(key: key, ascending: key == "label")
            table.addTableColumn(column)
        }
        table.sortDescriptors = [NSSortDescriptor(key: "total_tokens", ascending: false)]
        let scroll = NSScrollView(); scroll.documentView = table; scroll.hasVerticalScroller = true; scroll.hasHorizontalScroller = true
        scroll.borderType = .noBorder; scroll.autohidesScrollers = true
        scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 100).isActive = true
        let details = FeedbackButton(title: "统计说明", target: self, action: #selector(showCoverage)); details.bezelStyle = .rounded
        let footer = stack([coverage, spacer(), details])
        let quotaRow = stack([dashboard!.quotaText, spacer(), dashboard!.quotaCards], spacing: 12)
        quotaRow.heightAnchor.constraint(equalToConstant: 28).isActive = true
        dashboard!.quotaText.setAccessibilityLabel("账号剩余额度与重置时间")
        dashboard!.quotaCards.setAccessibilityLabel("可用重置卡数量，只读")
        if isMainWindowProcess {
            dashboard?.quotaText.stringValue = hostQuotaDetail
            dashboard?.quotaCards.stringValue = hostQuotaResetLabel
        } else { renderQuota(quotaReader.snapshot) }
        let content = stack([title, subtitle, quotaRow, filters, cards, chartPanel, detailHeader, scroll, footer], vertical: true, spacing: 12)
        for v in [title, subtitle, quotaRow, filters, cards, chartPanel, detailHeader, scroll, footer] { v.widthAnchor.constraint(equalTo: content.widthAnchor).isActive = true }
        installBudgetNavigation(root: root, usage: content)
        window.makeKeyAndOrderFront(nil)
    }
    func renderQuota(_ snapshot: QuotaSnapshot) {
        guard !isMainWindowProcess else { return }
        dashboard?.quotaText.stringValue = snapshot.detail
        dashboard?.quotaCards.stringValue = snapshot.resetLabel
        dashboard?.quotaCards.toolTip = "只读显示，不提供兑换或使用重置卡的操作"
        capsuleState.quotaCompact = snapshot.capsuleCompact
        capsuleState.quotaName = snapshot.capsuleWindow.map { "\($0.label)剩余" } ?? "剩余额度"
        capsuleState.quotaStale = snapshot.stale || snapshot.error != nil
        capsuleState.quotaFraction = snapshot.capsuleWindow.map { $0.remaining / 100 }
        capsuleState.quotaDetail = snapshot.compact
        budgetCoordinator?.updateQuota(snapshot)
        updateStatusTitle()
        updateRefreshStatus()
        publishHostState()
    }
    func updateStatusTitle() {
        updateStatusDetail()
        guard !isMainWindowProcess else { return }
        let summary = todaySnapshot["summary"] as? Object ?? [:]
        statusItem?.button?.title = " \(compact(summary["total_tokens"])) · \(quotaReader.snapshot.capsuleCompact)"
        statusItem?.button?.toolTip = "今日 \(exact(summary["total_tokens"])) Token · \(quotaReader.snapshot.capsuleCompact)\n单击查看详情，双击打开主面板，右键打开菜单"
    }
    func start() {
        guard isMainWindowProcess, !terminating, !compactMode, dashboard != nil else { return }
        statusText = "正在准备本机统计…"
        refreshButton?.title = "初始化…"; refreshButton?.isEnabled = false
        exportButton?.isEnabled = false
        backend.start(home: codexHome, refreshSeconds: autoSeconds) { [weak self] result in
            guard let self = self, !self.terminating, !self.compactMode, self.dashboard != nil else { return }
            switch result {
            case .success:
                self.failures = 0; self.applyInterval(self.autoSeconds, refreshAfterChange: false); self.poll()
                if self.helperRefreshQueued { self.helperRefreshQueued = false; self.refreshLocal() }
            case .failure(let error):
                self.reportHelperRefresh(success: false, message: "主面板统计启动失败")
                self.refreshButton?.title = "刷新"; self.refreshButton?.isEnabled = true
                self.statusText = "启动失败 · 点击刷新重试"; self.alert("无法启动统计", error.localizedDescription)
            }
        }
    }
    var mainVisible: Bool { isMainWindowProcess && mainPage == "usage" && window?.isVisible == true && window?.isMiniaturized != true && !NSApp.isHidden }
    func refreshVisibleData() { if mainVisible { loadUsage() } else { loadCompact() } }
    func poll(force: Bool = false) {
        guard !compactMode, !terminating, !restarting else { return }
        if backend.process == nil && !backend.starting && !backend.stopping {
            start(); return
        }
        guard request == nil, let base = backend.url, let child = backend.process else { return }
        let cadence = pendingRefresh != nil || mainVisible ? 1 : max(1, min(5, autoSeconds == 0 ? 5 : autoSeconds))
        guard force || Date().timeIntervalSince(lastPoll) >= Double(cadence) - 0.1 else { return }
        lastPoll = Date()
        requestID += 1; let id = requestID
        let expectedID = backend.identity
        let generation = backend.generation
        let expectedPID = child.processIdentifier
        request = session.dataTask(with: base.appendingPathComponent("health")) { [weak self] data, response, error in
            DispatchQueue.main.async {
                guard let self = self, self.requestID == id, self.backend.generation == generation, !self.terminating, !self.restarting else { return }
                self.request = nil
                guard error == nil, let data = data, let json = try? JSONSerialization.jsonObject(with: data) as? Object,
                      json["application"] as? String == "codex-token-usage-dashboard", json["instance_id"] as? String == expectedID,
                      numeric(json["pid"]) == Double(expectedPID), (response as? HTTPURLResponse)?.statusCode == 200 else {
                    self.failures += 1; self.statusText = "连接恢复中（\(self.failures)/3）…"
                    if self.failures >= 3 { self.recover("统计暂时无响应") }
                    else if self.backend.eventsAvailable {
                        let work = DispatchWorkItem { [weak self] in self?.poll(force: true) }
                        self.backendScanDeadline?.cancel(); self.backendScanDeadline = work
                        DispatchQueue.main.asyncAfter(deadline: .now() + 5, execute: work)
                    }
                    self.capsuleState.status = self.statusText
                    return
                }
                self.handleBackendState(json)
            }
        }
        request?.resume()
    }
    func scheduleBackendFallback() {
        guard isMainWindowProcess, !terminating, !backend.eventsAvailable, timer == nil else { return }
        statusText = "更新通知暂不可用，正在退避核对"
        timer = Timer(timeInterval: backendFallbackDelay, repeats: false) { [weak self] _ in
            guard let self = self else { return }
            self.timer = nil; self.poll(force: true)
            self.backendFallbackDelay = min(60, self.backendFallbackDelay * 2); self.scheduleBackendFallback()
        }
        timer?.tolerance = min(5, backendFallbackDelay / 5)
        RunLoop.main.add(timer!, forMode: .common)
    }
    func handleBackendState(_ json: Object) {
        guard !terminating, !restarting else { return }
        backendScanDeadline?.cancel(); backendScanDeadline = nil
        if json["scanning"] as? Bool == true {
            let generation = backend.generation
            let work = DispatchWorkItem { [weak self] in
                guard let self = self, self.backend.generation == generation else { return }
                self.backendScanDeadline = nil; self.poll(force: true)
            }
            backendScanDeadline = work; DispatchQueue.main.asyncAfter(deadline: .now() + 120, execute: work)
        }
                if let ticket = pendingRefresh {
                    statusText = "正在重新读取日志…"
                    capsuleState.status = "正在刷新…"
                    if ticket >= 0 && Int(numeric(json["refresh_completed"]) ?? -1) >= ticket {
                        manualExpectedStamp = json["generated_at"] as? String
                        finishRefreshing()
                        if let error = json["refresh_error"] as? String {
                            reportHelperRefresh(success: false, message: error)
                            manualBeganAt = nil; manualExpectedStamp = nil
                            statusText = error; capsuleState.status = "刷新失败 · 保留旧快照"
                            return
                        }
                        refreshVisibleData(); return
                    }
                    if let began = refreshStarted, Date().timeIntervalSince(began) > 120 {
                        finishRefreshing()
                        manualBeganAt = nil; manualExpectedStamp = nil
                        statusText = "扫描耗时较长，仍在后台进行"
                        reportHelperRefresh(success: false, message: statusText)
                    }
                    return
                }
                if json["ready"] as? Bool != true {
                    failures = 0
                    statusText = "正在索引本机记录…"
                    capsuleState.status = statusText
                    return
                }
                failures = 0
                if let error = json["refresh_error"] as? String {
                    reportHelperRefresh(success: false, message: error)
                    statusText = error; capsuleState.status = "读取失败 · 保留旧快照"
                    return
                }
                reportLocalUpdate(busy: json["scanning"] as? Bool == true, stamp: json["generated_at"] as? String)
                let previous = mainVisible ? (snapshot["meta"] as? Object)?["generated_at"] as? String : compactStamp
                if mainVisible && usageSession.needsRead || previous == nil || json["generated_at"] as? String != previous { refreshVisibleData() }
    }
    func endpoint(_ path: String) -> URL? {
        guard let base = backend.url else { return nil }
        var parts = URLComponents(url: base.appendingPathComponent(path), resolvingAgainstBaseURL: false)!
        parts.queryItems = [URLQueryItem(name: "days", value: days), URLQueryItem(name: "model", value: model), URLQueryItem(name: "task", value: task), URLQueryItem(name: "group", value: group)]
        return parts.url
    }
    func loadUsage() {
        guard mainVisible else { loadCompact(); return }
        let identity = usageIdentity
        if usageSession.select(identity) { clearUsageDisplay() }
        guard let url = endpoint("api/usage"), !terminating else { return }
        requestID += 1; let id = requestID; let generation = backend.generation
        request?.cancel()
        request = session.dataTask(with: url) { [weak self] data, response, error in
            DispatchQueue.main.async {
                guard let self = self, self.requestID == id, self.backend.generation == generation,
                      self.usageIdentity == identity, !self.terminating else { return }
                self.request = nil
                if (response as? HTTPURLResponse)?.statusCode == 400 {
                    self.usageSession.failed(); self.exportButton?.isEnabled = false
                    self.statusText = "所选范围不可用，请调整或重置筛选"; return
                }
                guard error == nil, (response as? HTTPURLResponse)?.statusCode == 200, let data = data,
                      let json = try? JSONSerialization.jsonObject(with: data) as? Object,
                      json["summary"] is Object, json["meta"] is Object else {
                    self.failures += 1; self.statusText = "读取暂时失败，正在重试…"
                    self.usageSession.failed(); self.exportButton?.isEnabled = false
                    if self.failures >= 3 { self.recover("用量读取失败") }
                    return
                }
                self.failures = 0
                if self.mainVisible, self.usageSession.publish(json, for: identity) { self.snapshot = json; self.render(json) } else { self.loadCompact() }
            }
        }
        request?.resume()
    }
    func render(_ json: Object) {
        guard isMainWindowProcess, !terminating, !compactMode, dashboard != nil else { return }
        let summary = json["summary"] as? Object ?? [:]
        totalCard.update(summary["total_tokens"]); inputCard.update(summary["input_tokens"])
        outputCard.update(summary["output_tokens"]); cacheCard.update(summary["cached_input_tokens"])
        callsCard.update(summary["requests"], suffix: "次 · \(exact(summary["active_tasks"])) 个任务")
        let meta = json["meta"] as? Object ?? [:]
        if let stamp = meta["generated_at"] as? String, stamp != publishedUsageStamp {
            publishedUsageStamp = stamp; sendHost("dataChanged")
        }
        let loading = meta["loading"] as? Bool ?? false
        if pendingRefresh == nil { refreshButton?.title = loading ? "初始化…" : "刷新"; refreshButton?.isEnabled = !loading }
        let generated = meta["generated_at"] as? String ?? ""
        let time = generated.count >= 19 ? String(generated.dropFirst(11).prefix(8)) : ""
        statusText = pendingRefresh != nil ? "正在重新读取日志…" : (loading ? "正在索引本机记录…" : "● \(time) 已更新 · \(intervalDescription)")
        dashboard?.status.textColor = loading ? .secondaryLabelColor : accent
        let exclusions = Int(numeric(meta["excluded_legacy_threads"]) ?? 0)
        let issues = meta["issues"] as? [String] ?? []
        let quality = meta["coverage"] as? Object ?? [:]
        let gaps = quality["cumulative_gaps"] as? [Object] ?? []
        var hints = ["本机 \(exact(meta["scanned_files"])) 个日志", "UTC+08:00"]
        if exclusions > 0 { hints.append("\(exclusions) 个旧任务未计入") }
        if !issues.isEmpty || !gaps.isEmpty { hints.append("部分记录存在缺口 · 查看统计说明") }
        coverage.stringValue = hints.joined(separator: " · ")
        coverage.toolTip = coverage.stringValue
        trend.days = json["timeline"] as? [Object] ?? []
        rows = json["groups"] as? [Object] ?? []; filterRows()
        let filters = json["filters"] as? Object ?? [:]
        updatePopup(models, options: (filters["models"] as? [String] ?? []).map { ($0, $0) }, all: "全部模型", selected: model)
        var options = (filters["tasks"] as? [Object] ?? []).compactMap { item -> (String, String)? in
            guard let id = item["id"] as? String else { return nil }; return (id, item["label"] as? String ?? id)
        }
        if task != "all", !options.contains(where: { $0.0 == task }), let selected = filters["selected_task"] as? Object {
            options.insert((task, selected["label"] as? String ?? task), at: 0)
        }
        updatePopup(tasks, options: options, all: "全部任务", selected: task)
        exportButton?.isEnabled = !loading && pendingRefresh == nil && !usageSession.needsRead && usageSession.identity == usageIdentity
        refreshPresented(json)
        loadCompact()
    }
    func updatePopup(_ popup: NSPopUpButton, options: [(String, String)], all: String, selected: String) {
        let desired = [("all", all)] + options
        // Avoid replacing an open menu when an unchanged snapshot arrives.
        if popup.itemArray.map({ $0.representedObject as? String ?? "" }) != desired.map({ $0.0 }) || popup.itemTitles != desired.map({ $0.1 }) {
            popup.removeAllItems()
            for (id, title) in desired {
                // Distinct task IDs may share the same title. NSPopUpButton's title-based
                // insertion can replace an existing entry, so insert menu items directly.
                let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
                item.representedObject = id; item.toolTip = title
                popup.menu?.addItem(item)
            }
        }
        popup.selectItem(at: desired.firstIndex(where: { $0.0 == selected }) ?? 0)
    }
    @objc func filterChanged() {
        guard dashboard != nil, mainVisible else { return }
        days = ["1", "7", "30", "90", "all"][max(0, period.selectedSegment)]
        model = models.selectedItem?.representedObject as? String ?? "all"
        task = tasks.selectedItem?.representedObject as? String ?? "all"
        group = grouping.selectedSegment == 0 ? "model" : "task"
        statusText = "正在更新筛选…"
        loadUsage()
    }
    @objc func resetMainFilters() {
        model = "all"; task = "all"
        models.selectItem(at: 0); tasks.selectItem(at: 0)
        statusText = "正在更新筛选…"; loadUsage()
    }
    func clearUsageDisplay() {
        snapshot = [:]; rows = []; visibleRows = []
        guard dashboard != nil else { return }
        for card in [totalCard, inputCard, outputCard, cacheCard, callsCard] { card.update(nil) }
        trend.days = []; table.reloadData(); rowCount.stringValue = "待读取"
        coverage.stringValue = "正在读取所选范围"; exportButton?.isEnabled = false
    }
    func controlTextDidChange(_ obj: Notification) { filterRows() }
    func filterRows() {
        guard dashboard != nil else { return }
        let selection = tableSelection
        if lastTableSelection != selection { tableRevision += 1; lastTableSelection = selection }
        visibleRows = selection.rows(rows)
        rowCount.stringValue = "\(visibleRows.count) / \(rows.count) 项"
        table.reloadData()
    }
    func numberOfRows(in tableView: NSTableView) -> Int { visibleRows.count }
    func tableView(_ tableView: NSTableView, sortDescriptorsDidChange oldDescriptors: [NSSortDescriptor]) { filterRows() }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard let column = tableColumn, row < visibleRows.count else { return nil }
        let key = column.identifier.rawValue
        let text = key == "label" ? (visibleRows[row][key] as? String ?? "未命名") : exact(visibleRows[row][key])
        let cell = NSTableCellView()
        let field = label(text, 12, key == "total_tokens" ? .semibold : .regular)
        if key != "label" { field.font = .monospacedDigitSystemFont(ofSize: 12, weight: key == "total_tokens" ? .semibold : .regular); field.alignment = .right }
        field.translatesAutoresizingMaskIntoConstraints = false; cell.addSubview(field); cell.textField = field; cell.toolTip = text
        NSLayoutConstraint.activate([field.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 6), field.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -10), field.centerYAnchor.constraint(equalTo: cell.centerYAnchor)])
        return cell
    }
    var intervalDescription: String { autoSeconds == 0 ? "自动刷新已关闭" : "每 \(autoSeconds) 秒刷新" }
    func rebuildIntervalPicker() {
        guard dashboard != nil else { return }
        intervalPicker.removeAllItems()
        var values = [0, 1, 2, 5, 10, 30, 60]
        if !values.contains(autoSeconds) { values.append(autoSeconds); values.sort() }
        for seconds in values {
            intervalPicker.addItem(withTitle: seconds == 0 ? "关闭" : "每 \(seconds) 秒")
            intervalPicker.lastItem?.tag = seconds
        }
        intervalPicker.addItem(withTitle: "自定义…"); intervalPicker.lastItem?.tag = -1
        intervalPicker.selectItem(withTag: autoSeconds)
    }
    @objc func intervalChanged(_ sender: Any? = nil) {
        // Popup actions may arrive after a shortcut has already released the main
        // window. Read the originating control, not a force-unwrapped current UI.
        guard let selected = (sender as? NSPopUpButton)?.selectedTag() ?? dashboard?.intervalPicker.selectedTag() else { return }
        guard selected == -1 else { applyInterval(selected); return }
        guard let activeWindow = window, dashboard != nil else { return }
        rebuildIntervalPicker()
        let prompt = NSAlert(); prompt.messageText = "设置自动刷新间隔"
        prompt.informativeText = "输入 1–3600 秒。扫描完成后开始计算下一次间隔；手动刷新始终立即触发。"
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 25))
        field.stringValue = String(max(1, autoSeconds)); field.placeholderString = "秒"
        field.setAccessibilityLabel("自定义刷新秒数")
        prompt.accessoryView = field; prompt.addButton(withTitle: "应用"); prompt.addButton(withTitle: "取消")
        prompt.beginSheetModal(for: activeWindow) { [weak self] response in
            guard response == .alertFirstButtonReturn, let self = self else { return }
            guard let seconds = Int(field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)), (1...3600).contains(seconds) else {
                self.alert("间隔无效", "请输入 1 到 3600 之间的整数秒数。"); return
            }
            self.applyInterval(seconds)
        }
        activeWindow.attachedSheet?.makeFirstResponder(field)
    }
    func post(_ path: String, body: Object, completion: @escaping (Result<Object, Error>) -> Void) -> URLSessionDataTask? {
        guard let base = backend.url else { return nil }
        let generation = backend.generation
        var request = URLRequest(url: base.appendingPathComponent(path))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue(backend.identity, forHTTPHeaderField: "X-Codex-Instance")
        request.httpBody = try? JSONSerialization.data(withJSONObject: body)
        let command = session.dataTask(with: request) { [weak self] data, response, error in
            DispatchQueue.main.async {
                guard let self = self, self.backend.generation == generation, !self.terminating, !self.restarting else { return }
                guard error == nil, (response as? HTTPURLResponse)?.statusCode == 200, let data = data,
                      let json = try? JSONSerialization.jsonObject(with: data) as? Object else {
                    completion(.failure(error ?? NSError(domain: "Desktop", code: 6))); return
                }
                completion(.success(json))
            }
        }
        command.resume(); return command
    }
    func applyInterval(_ seconds: Int, refreshAfterChange: Bool = true) {
        guard (0...3600).contains(seconds) else { return }
        autoSeconds = seconds; usagePreferences.set(seconds, forKey: "refreshSeconds"); rebuildIntervalPicker()
        if isMainWindowProcess {
            if !applyingHostState { sendHost("interval", payload: ["seconds": seconds]) }
        } else { publishHostState() }
        if compactMode {
            scheduleCompact()
            if refreshAfterChange && seconds > 0 { manualRefresh() }
            loadFloating()
            return
        }
        settingsQueue.enqueue(seconds)
        usagePreferences.set(seconds, forKey: "pendingRefreshSeconds")
        flushIntervalSettings(refreshAfterChange: refreshAfterChange)
    }
    func flushIntervalSettings(refreshAfterChange: Bool = false) {
        guard backend.url != nil else { return }
        guard let seconds = settingsQueue.begin() else { return }
        settingsID += 1; let id = settingsID
        settingsCommand = post("api/settings", body: ["refresh_seconds": seconds]) { [weak self] result in
            guard let self = self, self.settingsID == id else { return }
            self.settingsCommand = nil
            switch result {
            case .success:
                self.settingsQueue.finish(error: nil)
                self.sendHost("settingsResult", payload: ["error": ""])
                if !self.settingsQueue.hasPending { usagePreferences.removeObject(forKey: "pendingRefreshSeconds") }
                if !self.snapshot.isEmpty { self.render(self.snapshot) }
                // A paused index will not publish another snapshot to update this text.
                self.loadCompact()
                if self.settingsQueue.hasPending { self.flushIntervalSettings(refreshAfterChange: refreshAfterChange) }
                else if refreshAfterChange && seconds > 0 { self.manualRefresh() }
            case .failure:
                self.settingsQueue.finish(error: "刷新间隔尚未保存，已保留修改；请重试。")
                self.statusText = self.settingsQueue.error!
                self.sendHost("settingsResult", payload: ["error": self.statusText])
            }
        }
    }
    @objc func retrySettings() {
        saveBudgetDraft()
        if settingsQueue.pending == nil, let value = usagePreferences.object(forKey: "pendingRefreshSeconds") as? Int { settingsQueue.enqueue(value) }
        flushIntervalSettings()
    }
    @objc func toggleFloating() {
        if isMainWindowProcess { sendHost("toggleFloating"); return }
        if floating?.isVisible == true { hideFloating() } else { showFloating() }
    }
    func hideFloating() {
        statusItem?.isVisible = true
        if !isMainWindowProcess { usagePreferences.set("menu", forKey: "residentMode") }
        if isMainWindowProcess { sendHost("hideFloating"); return }
        resetFloatInteraction()
        floatDocking?.prepareToHide()
        floatingChoices.cancel()
        floatCollapse?.cancel(); floatCollapse = nil
        stopFloatMouseMonitoring()
        setFloatExpanded(false, animated: false)
        floating?.orderOut(nil)
        usagePreferences.set(false, forKey: "floatingVisible")
        floatingButton?.title = "显示浮窗"
        floatingRequest?.cancel(); floatingRequest = nil; floatingRequestID += 1
        if !mainWindowOpen {
            trayOnly = true
            usagePreferences.set("menu", forKey: "displayMode")
        }
        publishHostState()
    }
    func stopFloatMouseMonitoring() {
        if let monitor = floatMouseMonitor { NSEvent.removeMonitor(monitor) }; floatMouseMonitor = nil
        if let monitor = floatLocalMouseMonitor { NSEvent.removeMonitor(monitor) }; floatLocalMouseMonitor = nil
    }
    func checkFloatPointer() {
        guard !capsuleState.interactionActive, floating?.isVisible == true, let surface = capsule else { return }
        if floatDocking?.handlePointer(inside: surface.containsScreenPoint(NSEvent.mouseLocation)) == true { return }
        floatCollapse?.cancel(); floatCollapse = nil
        setFloatExpanded(capsuleState.keepsExpanded || (capsuleState.keyboardInteracting && floating?.isKeyWindow == true) || surface.containsScreenPoint(NSEvent.mouseLocation))
    }
    func toggleFloatKeepsExpanded() {
        capsuleState.keepsExpanded.toggle(); checkFloatPointer()
    }
    func floatInteractionChanged(_ active: Bool) {
        if active {
            floatDocking?.pauseInteraction()
            floatCollapse?.cancel(); floatCollapse = nil; stopFloatMouseMonitoring()
            floatAnimation?.step = nil; floatAnimation?.stop(); floatAnimation = nil
        } else if !floatResettingInteraction { checkFloatPointer() }
    }
    func resetFloatInteraction() {
        capsuleState.keepsExpanded = false; floatResettingInteraction = true
        capsule?.cancelInteraction()
        capsuleState.pointerPressed = false; capsuleState.menuPresented = false; floatResettingInteraction = false
    }
    func startFloatMouseMonitoring() {
        guard floatMouseMonitor == nil, floatLocalMouseMonitor == nil, !capsuleState.interactionActive else { return }
        let mask: NSEvent.EventTypeMask = [.mouseMoved, .leftMouseDragged, .rightMouseDragged]
        floatMouseMonitor = NSEvent.addGlobalMonitorForEvents(matching: mask) { [weak self] _ in self?.checkFloatPointer() }
        floatLocalMouseMonitor = NSEvent.addLocalMonitorForEvents(matching: mask) { [weak self] event in self?.checkFloatPointer(); return event }
    }
    func showFloating() {
        guard !terminating else { return }
        if isMainWindowProcess { sendHost("showFloating"); return }
        if floating == nil {
            let panel = CapsulePanel(contentRect: NSRect(origin: .zero, size: CapsuleSurface.small), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            floating = panel; panel.title = "Token 胶囊"; panel.delegate = self
            panel.isFloatingPanel = true; panel.hidesOnDeactivate = false; panel.becomesKeyOnlyIfNeeded = true
            panel.isReleasedWhenClosed = false; panel.isMovableByWindowBackground = false; panel.acceptsMouseMovedEvents = true
            panel.isOpaque = false; panel.backgroundColor = .clear; panel.hasShadow = true
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            let pinned = usagePreferences.object(forKey: "floatingPinned") as? Bool ?? true
            panel.level = pinned ? .floating : .normal
            let surface = CapsuleSurface(state: capsuleState)
            capsule = surface; panel.contentView = CapsuleHost(surface: surface)
            surface.setAccessibilityLabel("Token 用量胶囊，悬停展开详情")
            surface.hover = { [weak self] inside in
                guard let self = self, !self.capsuleState.interactionActive else { return }
                if self.floatDocking?.handlePointer(inside: inside) == true { return }
                if inside {
                    self.floatCollapse?.cancel(); self.floatCollapse = nil
                    self.setFloatExpanded(true)
                } else { self.checkFloatPointer() }
            }
            surface.interactionChanged = { [weak self] active in self?.floatInteractionChanged(active) }
            capsuleState.rangeDays = floatingDays
            capsuleState.pinned = pinned
            surface.action = { [weak self] action in
                guard let self = self else { return }
                if self.floatingMonitorAction(action) || self.floatingBudgetAction(action) { return }
                switch action {
                case "refresh":
                    if self.capsuleState.monitorMode { self.taskMonitor?.refresh() }
                    else { self.manualRefresh() }
                case "details": self.toggleFloatKeepsExpanded()
                case "expand": self.floatDocking?.explicitExpand()
                case "collapse": self.floatDocking?.manuallyCollapse()
                case "autoHide": self.floatDocking?.toggleAutoHide()
                case "edgeMetric":
                    self.capsuleState.edgeShowsUsed.toggle()
                    usagePreferences.set(self.capsuleState.edgeShowsUsed ? "used" : "remaining", forKey: "floatingEdgeMetric")
                    self.usageSettings?.update(usagePreferences, monitor: self.taskMonitorState)
                case "main": self.showDashboard()
                case "monitor": self.openTaskMonitor()
                case "keyboard":
                    self.capsuleState.keyboardInteracting = true
                    self.floatDocking?.explicitExpand(); self.floating?.makeKey(); self.floating?.makeFirstResponder(self.capsule)
                case "only": self.onlyFloating()
                case "close": self.closeFloating()
                case "menu": self.onlyStatusBar()
                case "quit": self.quitApplication()
                case "pin": self.toggleCapsulePin()
                case "themeDark": self.applyCapsuleTheme(.dark)
                case "themeLight": self.applyCapsuleTheme(.light)
                case "updates": self.showUpdateStatus()
                case "settings": self.showSettings()
                case "arcColors": self.showArcColors()
                case "interval:custom": self.statusCustomInterval()
                default:
                    if action.hasPrefix("interval:"), let seconds = Int(action.dropFirst("interval:".count)) { self.applyInterval(seconds) }
                    else if action.hasPrefix("period:") { self.applyFloatingPeriod(String(action.dropFirst("period:".count))) }
                    else if action.hasPrefix("model:") { self.applyFloatingFilter("model", value: String(action.dropFirst("model:".count))) }
                    else if action.hasPrefix("task:") { self.applyFloatingFilter("task", value: String(action.dropFirst("task:".count))) }
                }
            }
            if let screen = NSScreen.main {
                let visible = screen.visibleFrame
                let saved = usagePreferences.array(forKey: "capsuleOrigin") as? [Double]
                var point = NSPoint(x: visible.maxX - 214, y: visible.maxY - 70)
                if let saved = saved, saved.count == 2 { point = NSPoint(x: saved[0], y: saved[1]) }
                let screenFrame = NSScreen.screens.first(where: { $0.visibleFrame.intersects(NSRect(origin: point, size: CapsuleSurface.small)) })?.visibleFrame ?? visible
                point.x = min(max(point.x, screenFrame.minX), screenFrame.maxX - CapsuleSurface.small.width)
                point.y = min(max(point.y, screenFrame.minY), screenFrame.maxY - CapsuleSurface.small.height)
                panel.setFrameOrigin(point)
            }
            let docking = CapsuleDocking(panel: panel, surface: surface, preferences: usagePreferences)
            floatDocking = docking
            docking.currentCompact = { [weak self] in
                guard let self = self else { return .zero }
                return self.floatPlacement?.compact ?? (self.capsule?.docking ?? 0 > 0 ? self.floatDocking?.compact : self.floating?.frame) ?? .zero
            }
            docking.didDropExpanded = { [weak self] compact, detail in
                guard let self = self else { return }
                let previous = self.floatPlacement
                self.floatPlacement = CapsulePlacement(compact: compact, detail: detail,
                    opensRight: previous?.opensRight ?? false, opensUp: previous?.opensUp ?? false)
                self.floatLastFrame = detail; self.floatAnchor = NSPoint(x: detail.maxX, y: detail.maxY)
                self.floatExpanded = true
            }
            docking.expand = { [weak self] in self?.setFloatExpanded(true) }
            docking.collapse = { [weak self] animated in self?.setFloatExpanded(false, animated: animated) }
            surface.dragReleased = { [weak docking] point in docking?.finishDrag(at: point) }
            surface.pointerMoved = { [weak docking] point in docking?.pointerMoved(point) }
        }
        floating?.orderFrontRegardless()
        usagePreferences.set(true, forKey: "floatingVisible")
        if !mainWindowOpen {
            trayOnly = false
            usagePreferences.set("floating", forKey: "displayMode")
        }
        floatingButton?.title = "隐藏浮窗"
        loadFloating()
        publishHostState()
    }
    func setFloatExpanded(_ expanded: Bool, animated: Bool = true) {
        guard !capsuleState.interactionActive, let panel = floating, let surface = capsule else { return }
        let end: CGFloat = expanded ? 1 : 0
        if expanded == floatExpanded {
            if animated, floatAnimation?.isAnimating == true { return }
            if floatAnimation == nil, abs(surface.expansion - end) < 0.000001 {
                if expanded { startFloatMouseMonitoring() } else { stopFloatMouseMonitoring() }
                return
            }
        }
        let old = panel.frame
        floatAnimation?.step = nil; floatAnimation?.stop(); floatAnimation = nil
        if floatAnchor == nil { floatAnchor = NSPoint(x: old.maxX, y: old.maxY) }
        if floatPlacement == nil {
            guard let screen = panel.screen ?? NSScreen.main else { return }
            floatPlacement = CapsulePlacement.resolve(compact: old, work: screen.visibleFrame, size: CapsuleSurface.large)
        }
        floatLastFrame = old
        floatExpanded = expanded
        floatCollapse?.cancel(); floatCollapse = nil
        if expanded || surface.expansion > 0 { startFloatMouseMonitoring() } else { stopFloatMouseMonitoring() }
        let target = expanded ? floatPlacement!.detail : floatPlacement!.compact
        let startExpansion = surface.expansion
        surface.morphCompactFrame = floatPlacement!.compact; surface.morphDetailFrame = floatPlacement!.detail
        let finish: () -> Void = { [weak self] in
            guard let self = self else { return }
            self.floatAnimation = nil
            if !expanded { self.stopFloatMouseMonitoring(); self.floatAnchor = nil; self.floatPlacement = nil; self.saveCapsuleOrigin(); self.floatDocking?.didCollapse() }
            // A completed explicit reveal waits for the next actual pointer
            // event. Completing an animation is not a synthetic mouse exit.
        }
        if !animated || NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            floatMovingFrame = true; surface.expansion = expanded ? 1 : 0; panel.setFrame(target, display: true); floatLastFrame = target; floatMovingFrame = false; finish(); return
        }
        let animation = CapsuleAnimation(duration: (expanded ? 0.28 : 0.22) * Double(abs(end - startExpansion)), animationCurve: .linear)
        animation.animationBlockingMode = .nonblocking; animation.frameRate = 60
        animation.step = { [weak self, weak panel, weak surface] progress in
            guard let self = self, !self.capsuleState.interactionActive, let panel = panel, let surface = surface else { return }
            self.floatMovingFrame = true
            let lerp: (CGFloat, CGFloat) -> CGFloat = { $0 + ($1 - $0) * progress }
            surface.expansion = lerp(startExpansion, expanded ? 1 : 0)
            let frame = CapsuleMorph.frame(compact: surface.morphCompactFrame!, panel: surface.morphDetailFrame!, progress: surface.expansion).shape
            panel.setFrame(frame, display: true)
            self.floatLastFrame = panel.frame
            self.floatMovingFrame = false
            if progress >= 1 { finish() }
        }
        floatAnimation = animation; animation.start()
    }
    func saveCapsuleOrigin() {
        guard let frame = floating?.frame, !floatExpanded, floatAnimation == nil, capsule?.expansion == 0, !capsuleState.interactionActive else { return }
        usagePreferences.set([Double(frame.origin.x), Double(frame.origin.y)], forKey: "capsuleOrigin")
    }
    func windowDidMove(_ notification: Notification) {
        guard !floatMovingFrame, floatDocking?.movingFrame != true else { return }
        if let moved = notification.object as? NSWindow, moved === floating {
            if let previous = floatLastFrame {
                floatPlacement = floatPlacement?.translated(x: moved.frame.minX - previous.minX, y: moved.frame.minY - previous.minY)
            }
            floatLastFrame = moved.frame
            if floatAnimation == nil { floatAnchor = NSPoint(x: moved.frame.maxX, y: moved.frame.maxY) }
            saveCapsuleOrigin()
        }
    }
    func trimIdleMemory() {
        trimWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self = self, self.compactMode, !self.collector.busy else { return }
            malloc_zone_pressure_relief(nil, 0)
        }
        trimWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: work)
    }
    @objc func closeFloating() { hideFloating() }
    func toggleCapsulePin() {
        capsuleState.pinned.toggle()
        floating?.level = capsuleState.pinned ? .floating : .normal
        usagePreferences.set(capsuleState.pinned, forKey: "floatingPinned")
    }
    func releaseDetailData() {
        request?.cancel(); request = nil; requestID += 1
        usageSession.invalidate()
        snapshot = [:]; rows = []; visibleRows = []
        dashboard?.trend.days = []; dashboard?.table.reloadData()
        compactStamp = nil
    }
    func discardDashboard() {
        let old = window
        window = nil
        old?.delegate = nil
        old?.orderOut(nil)
        old?.close()
        old?.contentView = nil
        dashboard = nil
        DispatchQueue.main.async { malloc_zone_pressure_relief(nil, 0) }
    }
    @objc func hideDashboard() {
        guard !terminating else { return }
        if isMainWindowProcess { NSApp.terminate(nil) }
        else { closeMainWindow() }
    }
    @objc func onlyFloating() {
        guard !terminating else { return }
        if isMainWindowProcess { sendHost("onlyFloating"); return }
        showFloating()
        statusItem?.isVisible = false; usagePreferences.set("floating", forKey: "residentMode")
    }
    @objc func onlyStatusBar() {
        guard !terminating else { return }
        if isMainWindowProcess { sendHost("onlyStatusBar"); return }
        hideFloating()
        floating?.delegate = nil; floating?.contentView = nil; floating?.close()
        floating = nil; capsule = nil; floatAnimation?.stop(); floatAnimation = nil; floatAnchor = nil; floatPlacement = nil; floatLastFrame = nil; floatDocking = nil
        publishHostState()
    }
    @objc func showDashboard() {
        guard !terminating else { return }
        if !isMainWindowProcess { openMainWindow(); return }
        let wasCompact = compactMode
        compactMode = false; compactTimer?.invalidate(); compactTimer = nil; compactManualQueued = false
        stopCompactMonitoring()
        if dashboard == nil { buildWindow() }
        trayOnly = false
        usagePreferences.set("main", forKey: "displayMode")
        NSApp.setActivationPolicy(.regular)
        if window?.isMiniaturized == true { window.deminiaturize(nil) }
        window?.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true)
        if wasCompact {
            finishRefreshing()
            collector.stop { [weak self] in
                guard let self = self, !self.compactMode, !self.terminating else { return }
                self.backend.stop { [weak self] in
                    guard let self = self, !self.compactMode, !self.terminating, self.dashboard != nil else { return }
                    self.start()
                }
            }

        } else if backend.url == nil {
            backend.stop { [weak self] in
                guard let self = self, !self.compactMode, !self.terminating else { return }
                self.start()
            }
        } else { loadUsage() }
    }
    func enterCompactMode() {
        guard !terminating else { return }
        guard !compactMode else { startCompactMonitoring(); fetchCompact(manual: false); return }
        compactMode = true; timer?.invalidate(); timer = nil
        startCompactMonitoring()
        let epoch = compactEpoch
        settingsID += 1; settingsCommand?.cancel(); settingsCommand = nil
        request?.cancel(); request = nil; requestID += 1
        summaryRequest?.cancel(); summaryRequest = nil; summaryRequestID += 1
        floatingRequest?.cancel(); floatingRequest = nil; floatingRequestID += 1
        refreshCommand?.cancel(); refreshCommand = nil; finishRefreshing()
        activeSession?.invalidateAndCancel(); activeSession = nil
        backend.stop { [weak self] in
            // A close-window completion cannot restart a subsequently reopened
            // main backend. The opening action owns that transition itself.
            guard let self = self, self.compactMode, self.compactEpoch == epoch, !self.terminating else { return }
            // A rapid reopen/close may still have the previous compact worker
            // stopping. Join that cleanup so the next collection cannot be lost.
            self.collector.stop { [weak self] in
                guard let self = self, self.compactMode, self.compactEpoch == epoch, !self.terminating else { return }
                self.fetchCompact(manual: false); self.trimIdleMemory()
            }
        }
    }
    func startCompactMonitoring() {
        guard compactMode, !terminating else { return }
        let home = codexHome.standardizedFileURL
        guard compactMonitor.home != home else { return }
        compactEpoch += 1; compactDirty = true; compactDirtyGeneration += 1
        compactLastScan = .distantPast
        compactMonitor.start(home: home)
        startTaskMonitoring(home: home)
    }
    func stopCompactMonitoring() {
        compactEpoch += 1
        compactMonitor.stop(); compactTimer?.invalidate(); compactTimer = nil
        compactManualQueued = false; compactSelectionQueued = false
    }
    func markCompactDirty() {
        guard compactMode, !terminating else { return }
        compactDirty = true; compactDirtyGeneration += 1; scheduleCompact()
    }
    func scheduleCompact() {
        compactTimer?.invalidate(); compactTimer = nil
        guard compactMode, autoSeconds > 0, !terminating, compactDirty || !compactMonitor.isWatching,
              !collector.busy, !collector.stopping, !backend.stopping else { return }
        let delay = max(0.15, Double(autoSeconds) - Date().timeIntervalSince(compactLastScan))
        compactTimer = Timer(timeInterval: delay, repeats: false) { [weak self] _ in self?.fetchCompact(manual: false) }
        compactTimer?.tolerance = min(1, delay * 0.1)
        RunLoop.main.add(compactTimer!, forMode: .common)
    }
    func fetchCompact(manual: Bool) {
        guard compactMode, !terminating else { return }
        if collector.busy || collector.stopping || backend.stopping || nativeSummaryBusy {
            if manual { compactManualQueued = true }
            return
        }
        let selectionChanged = compactSelectionQueued || filteredSnapshotQuery != floatingQuery
        guard manual || selectionChanged || todaySnapshot.isEmpty ||
              (autoSeconds > 0 && (compactDirty || !compactMonitor.isWatching)) else { scheduleCompact(); return }
        compactTimer?.invalidate(); compactTimer = nil
        compactSelectionQueued = false
        let query = floatingQuery
        let epoch = compactEpoch, dirtyGeneration = compactDirtyGeneration, home = codexHome
        reportLocalUpdate(busy: true)
        collectCompactSnapshot(query: query, home: home) { [weak self] result in
            guard let self = self, self.compactMode, self.compactEpoch == epoch, self.codexHome == home, !self.terminating else { return }
            self.compactLastScan = Date()
            switch result {
            case .success(let json):
                self.compactDirty = self.compactDirtyGeneration != dirtyGeneration
                self.todaySnapshot = json["today"] as? Object ?? [:]
                self.reportLocalUpdate(busy: false, stamp: (self.todaySnapshot["meta"] as? Object)?["generated_at"] as? String)
                if self.floatingQuery == query {
                    if json["filter_reset"] as? Bool == true { self.resetFloatingFilters() }
                    self.filteredSnapshot = json["filtered"] as? Object ?? [:]
                    self.filteredSnapshotQuery = self.floatingQuery
                }
                if manual {
                    self.manualExpectedStamp = (self.todaySnapshot["meta"] as? Object)?["generated_at"] as? String
                    self.finishRefreshing()
                }
                let summary = self.todaySnapshot["summary"] as? Object ?? [:]
                self.updateStatusTitle()
                self.statusItem?.button?.toolTip = "今日 \(exact(summary["total_tokens"])) tokens · \(self.intervalDescription) · 双击打开主面板"
                self.refreshBudgets(manual: manual)
                self.loadFloating()
                if manual { self.refreshPresented(self.todaySnapshot) }
                self.trimIdleMemory()
            case .failure:
                self.reportLocalUpdate(busy: false, error: "本地日志读取失败，请重试；上次快照已保留。")
                self.compactDirty = true
                if manual { self.finishRefreshing(); self.manualBeganAt = nil; self.manualExpectedStamp = nil }
                self.capsuleState.status = "读取失败 · 点击刷新重试"
                self.statusItem?.button?.toolTip = self.capsuleState.status
            }
            if self.compactManualQueued {
                self.compactManualQueued = false; self.fetchCompact(manual: true)
            } else if self.compactSelectionQueued || self.floatingQuery != query {
                self.fetchCompact(manual: false)
            } else { self.scheduleCompact() }
        }
    }
    func collectCompactSnapshot(query: FloatingUsageQuery, home: URL, completion: @escaping (Result<Object, Error>) -> Void) {
        guard !isMainWindowProcess else { return }
        if mainWindowOpen || windowProcesses.mainIsRunning {
            nativeSummaryBusy = true
            nativeSummary.read(cache: backend.cachePath(home), query: query, seconds: autoSeconds) { [weak self] result in
                self?.nativeSummaryBusy = false; completion(result)
            }
        } else {
            collector.collect(home: home, days: query.days, model: query.model, task: query.task, seconds: autoSeconds, completion: completion)
        }
    }
    func windowDidMiniaturize(_ notification: Notification) { releaseDetailData(); loadCompact() }
    func windowDidDeminiaturize(_ notification: Notification) { loadUsage() }
    func applicationDidHide(_ notification: Notification) {
        if isMainWindowProcess { hideDashboard() }
        else { releaseDetailData(); loadCompact() }
    }
    func applicationDidUnhide(_ notification: Notification) { refreshVisibleData() }
    func buildStatusItem() {
        guard !isMainWindowProcess else { return }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem = item
        item.autosaveName = "CodexUsageStatusItem"
        item.isVisible = true
        item.button?.image = NSImage(size: NSSize(width: 16, height: 16), flipped: false) { _ in
            NSColor.black.setFill()
            let heights: [CGFloat] = [6, 13, 9]
            for (i, height) in heights.enumerated() {
                let x: CGFloat = 1 + CGFloat(i) * 5
                NSBezierPath(roundedRect: NSRect(x: x, y: 2, width: 3, height: height), xRadius: 1, yRadius: 1).fill()
            }
            return true
        }
        item.button?.image?.isTemplate = true
        item.button?.imagePosition = .imageLeading
        item.button?.font = .monospacedDigitSystemFont(ofSize: 11, weight: .medium)
        item.button?.title = " —"
        item.button?.setAccessibilityLabel("Codex 用量状态栏")
        item.button?.target = self; item.button?.action = #selector(statusClicked)
        item.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
    }
    /// Release AppKit's registration before replacing this process image. If
    /// exec fails, immediately restore the status-bar entry in the same process.

    @objc func statusClicked() {
        statusSingleClick?.cancel(); statusSingleClick = nil
        if statusMenuTracking { statusMenu?.cancelTracking(); return }
        let event = NSApp.currentEvent
        if event?.type == .rightMouseUp { openStatusMenu(); return }
        if (event?.clickCount ?? 0) >= 2 { statusPopover?.performClose(nil); showDashboard(); return }
        let work = DispatchWorkItem { [weak self] in self?.toggleStatusDetail() }
        statusSingleClick = work
        DispatchQueue.main.asyncAfter(deadline: .now() + NSEvent.doubleClickInterval, execute: work)
    }
    @objc func openStatusMenu() {
        if isMainWindowProcess { sendHost("statusMenu"); return }
        statusPopover?.performClose(nil)
        guard !statusMenuTracking, let button = statusItem?.button else { return }
        let menu = NSMenu(); menu.delegate = self; statusMenu = menu
        rebuildStatusMenu(menu)
        NSApp.activate(ignoringOtherApps: true)
        popUpStatusMenu(menu, from: button)
    }
    func confinementRect(for menu: NSMenu, on screen: NSScreen?) -> NSRect {
        menu === statusMenu ? statusMenuConfinement(from: statusItem?.button, on: screen) : .zero
    }
    func menuWillOpen(_ menu: NSMenu) {
        guard menu === statusMenu else { return }
        statusMenuTracking = true
        // NSMenu normally handles outside clicks while active. These scoped monitors
        // also cover accessory mode and clicks into our nonactivating capsule.
        menuOutsideMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown]) { [weak menu] _ in menu?.cancelTracking() }
        menuLocalMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown]) { [weak self, weak menu] event in
            if let self = self, let target = event.window, target === self.window || target === self.floating { menu?.cancelTracking() }
            return event
        }
        menuDeactivateObserver = NotificationCenter.default.addObserver(forName: NSApplication.didResignActiveNotification, object: NSApp, queue: .main) { [weak menu] _ in menu?.cancelTracking() }
    }
    func menuDidClose(_ menu: NSMenu) {
        guard menu === statusMenu else { return }
        statusMenuTracking = false
        trimIdleMemory()
        if let monitor = menuOutsideMonitor { NSEvent.removeMonitor(monitor) }; menuOutsideMonitor = nil
        if let monitor = menuLocalMonitor { NSEvent.removeMonitor(monitor) }; menuLocalMonitor = nil
        if let observer = menuDeactivateObserver { NotificationCenter.default.removeObserver(observer) }; menuDeactivateObserver = nil
        // The popup invocation owns this menu until tracking returns. Dropping our
        // root reference avoids retaining its renderer and item tree while idle.
        statusMenu = nil; menu.delegate = nil
    }
    func rebuildStatusMenu(_ menu: NSMenu) {
        menu.removeAllItems()
        let summary = todaySnapshot["summary"] as? Object ?? [:]
        for text in ["今日总 Token：\(exact(summary["total_tokens"]))", "输入：\(exact(summary["input_tokens"])) · 输出：\(exact(summary["output_tokens"]))", "缓存输入：\(exact(summary["cached_input_tokens"]))", intervalDescription] {
            let row = NSMenuItem(title: text, action: nil, keyEquivalent: ""); row.isEnabled = false; menu.addItem(row)
        }
        for text in [quotaReader.snapshot.detail] {
            let row = NSMenuItem(title: text, action: nil, keyEquivalent: ""); row.isEnabled = false; menu.addItem(row)
        }
        appendBudgetMenu(menu)
        let monitor = taskMonitorState["summary"] as? Object ?? [:]
        let monitoring = NSMenuItem(title: "任务监控 · \(monitor["active"] as? Int ?? 0) 项 · \(monitor["unread"] as? Int ?? 0) 条未读", action: #selector(openTaskMonitor), keyEquivalent: "")
        monitoring.target = self; menu.addItem(monitoring)
        menu.addItem(.separator())
        for (title, action) in [("立即刷新", #selector(manualRefresh)), ("设置…", #selector(showSettings)), ("数据与更新…", #selector(showUpdateStatus)), ("显示主面板", #selector(showDashboard)), ("显示 / 隐藏浮窗", #selector(toggleFloating)), ("仅浮窗", #selector(onlyFloating)), ("仅状态栏", #selector(onlyStatusBar))] {
            let row = NSMenuItem(title: title, action: action, keyEquivalent: ""); row.target = self; menu.addItem(row)
        }
        menu.addItem(capsuleThemeMenuItem())
        let settings = NSMenuItem(title: "自动刷新", action: nil, keyEquivalent: "")
        let options = NSMenu(); settings.submenu = options; menu.addItem(settings)
        var intervals = [0, 1, 2, 5, 10, 30, 60]
        if !intervals.contains(autoSeconds) { intervals.append(autoSeconds); intervals.sort() }
        for seconds in intervals {
            let row = NSMenuItem(title: seconds == 0 ? "关闭" : "每 \(seconds) 秒", action: #selector(statusInterval(_:)), keyEquivalent: "")
            row.tag = seconds; row.target = self; row.state = autoSeconds == seconds ? .on : .off; options.addItem(row)
        }
        let custom = NSMenuItem(title: "自定义…", action: #selector(statusCustomInterval), keyEquivalent: ""); custom.target = self; options.addItem(custom)
        menu.addItem(.separator())
        let quit = NSMenuItem(title: "退出 Codex 用量", action: #selector(quitApplication), keyEquivalent: "q")
        quit.target = self; menu.addItem(quit)
    }
    @objc func statusInterval(_ sender: NSMenuItem) { applyInterval(sender.tag) }
    @objc func statusCustomInterval() {
        if !isMainWindowProcess { usagePreferences.set(true, forKey: "openCustomInterval"); openMainWindow(); return }
        showDashboard()
        guard dashboard != nil else { return }
        intervalPicker.selectItem(withTag: -1); intervalChanged()
    }
    func loadCompact() {
        if compactMode { fetchCompact(manual: false); return }
        guard let base = backend.url, summaryRequest == nil, !terminating else { return }
        summaryRequestID += 1; let id = summaryRequestID; let generation = backend.generation
        var components = URLComponents(url: base.appendingPathComponent("api/summary"), resolvingAgainstBaseURL: false)!
        components.queryItems = [URLQueryItem(name: "days", value: "1")]
        summaryRequest = session.dataTask(with: components.url!) { [weak self] data, response, error in
            DispatchQueue.main.async {
                guard let self = self, self.summaryRequestID == id, self.backend.generation == generation, !self.terminating else { return }
                self.summaryRequest = nil
                guard error == nil, (response as? HTTPURLResponse)?.statusCode == 200, let data = data,
                      let json = try? JSONSerialization.jsonObject(with: data) as? Object else {
                    self.capsuleState.status = "摘要读取失败 · 点击刷新重试"
                    self.statusItem?.button?.toolTip = self.capsuleState.status
                    return
                }
                self.todaySnapshot = json
                self.compactStamp = (json["meta"] as? Object)?["generated_at"] as? String
                if self.compactStamp != self.publishedUsageStamp {
                    self.publishedUsageStamp = self.compactStamp; self.sendHost("dataChanged")
                }
                let summary = json["summary"] as? Object ?? [:]
                self.updateStatusTitle()
                self.statusItem?.button?.toolTip = "今日 \(exact(summary["total_tokens"])) tokens · \(self.intervalDescription)"
                self.refreshPresented(json)
                self.loadFloating()
            }
        }
        summaryRequest?.resume()
    }
    func applyFloatingPeriod(_ value: String) {
        guard !terminating, FloatingUsageQuery.periods.contains(value) else { return }
        floatingDays = value; capsuleState.scope = 1; capsuleState.rangeDays = value
        usagePreferences.set(value, forKey: "floatingDays")
        refreshFloatingSelection()
    }
    func applyFloatingFilter(_ kind: String, value: String) {
        guard !terminating, !value.isEmpty else { return }
        if kind == "model" { floatingModel = value; usagePreferences.set(value, forKey: "floatingModel") }
        else if kind == "task" { floatingTask = value; usagePreferences.set(value, forKey: "floatingTask") }
        else { return }
        refreshFloatingSelection()
    }
    func resetFloatingFilters() {
        floatingModel = "all"; floatingTask = "all"
        usagePreferences.set("all", forKey: "floatingModel"); usagePreferences.set("all", forKey: "floatingTask")
    }
    func refreshFloatingSelection() {
        floatingChoices.cancel()
        filteredSnapshot = [:]; filteredSnapshotQuery = nil
        floatingRequest?.cancel(); floatingRequest = nil; floatingRequestID += 1
        // Retire old numbers immediately, including when automatic refresh is paused.
        renderFloating([:]); capsuleState.status = "正在更新浮窗筛选…"
        if compactMode { compactSelectionQueued = true; fetchCompact(manual: false) }
        else { loadFloating() }
    }
    func loadFloatingChoices(_ kind: String, completion: @escaping ([(String, String)]?, String?) -> Void) {
        guard !terminating else { completion(nil, nil); return }
        let query = floatingQuery, home = codexHome
        floatingChoices.load(cache: backend.cachePath(home), query: query, kind: kind) { [weak self] choices, error in
            guard let self = self, !self.terminating, self.floatingQuery == query, self.codexHome == home else { completion(nil, nil); return }
            completion(choices, error)
        }
    }
    func loadFloating() {
        guard floating?.isVisible == true else { return }
        if compactMode {
            if filteredSnapshotQuery == floatingQuery { renderFloating(filteredSnapshot) }
            return
        }
        floatingRequestID += 1; let id = floatingRequestID
        floatingRequest?.cancel(); floatingRequest = nil
        guard let base = backend.url else { return }
        let query = floatingQuery
        var components = URLComponents(url: base.appendingPathComponent("api/summary"), resolvingAgainstBaseURL: false)!
        components.queryItems = query.queryItems
        let generation = backend.generation
        floatingRequest = session.dataTask(with: components.url!) { [weak self] data, response, error in
            DispatchQueue.main.async {
                guard let self = self, self.floatingRequestID == id, self.backend.generation == generation, self.floatingQuery == query, !self.terminating else { return }
                self.floatingRequest = nil
                guard error == nil, (response as? HTTPURLResponse)?.statusCode == 200, let data = data,
                      let json = try? JSONSerialization.jsonObject(with: data) as? Object else {
                    self.capsuleState.status = "读取失败 · 点击刷新重试"; return
                }
                if json["filter_reset"] as? Bool == true { self.resetFloatingFilters() }
                self.renderFloating(json)
            }
        }
        floatingRequest?.resume()
    }
    func renderFloating(_ json: Object) {
        let summary = json["summary"] as? Object ?? [:]
        let meta = json["meta"] as? Object ?? [:]
        let filters = json["filters"] as? Object ?? [:]
        let selectedTask = filters["selected_task"] as? Object
        let range = ["1": "今天", "7": "7 天", "30": "30 天", "90": "90 天", "all": "全部时间"][floatingDays] ?? floatingDays
        capsuleState.rangeDays = floatingDays
        capsuleState.selectedModel = floatingModel; capsuleState.selectedTask = floatingTask
        capsuleState.selectedTaskLabel = floatingTask == "all" ? "全部任务" : (selectedTask?["label"] as? String ?? floatingTask)
        capsuleState.context = "\(range) · \(floatingModel == "all" ? "全部模型" : floatingModel) · \(capsuleState.selectedTaskLabel)"
        capsuleState.total = compact(summary["total_tokens"])
        capsuleState.exact = "\(exact(summary["total_tokens"])) tokens"
        capsuleState.input = compact(summary["input_tokens"])
        capsuleState.output = compact(summary["output_tokens"])
        capsuleState.cache = "缓存输入 \(compact(summary["cached_input_tokens"])) · \(exact(summary["requests"])) 次调用"
        let stamp = meta["generated_at"] as? String ?? ""
        let time = stamp.count >= 19 ? String(stamp.dropFirst(11).prefix(8)) : ""
        capsuleState.status = pendingRefresh != nil ? "正在刷新…" : (time.isEmpty ? "等待可核实的记录…" : "\(time) 已更新 · \(intervalDescription)")
        capsuleState.enabled = pendingRefresh == nil
        refreshPresented(json)
    }
    func refreshPresented(_ json: Object) {
        guard pendingRefresh == nil, let began = manualBeganAt, let required = manualExpectedStamp,
              let stamp = (json["meta"] as? Object)?["generated_at"] as? String, stamp >= required else { return }
        manualBeganAt = nil; manualExpectedStamp = stamp
        let elapsed = Date().timeIntervalSince(began)
        let feedback = String(format: "已核对日志 · %.2f 秒", elapsed)
        reportHelperRefresh(success: true, message: feedback)
        manualExpectedStamp = nil
        statusText += " · " + feedback
        capsuleState.status = feedback
        capsuleState.indicator = "check"
        statusItem?.button?.toolTip = feedback + " · 双击打开主面板"
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.3) { [weak self] in
            guard let self = self, self.pendingRefresh == nil else { return }
            self.capsuleState.indicator = "refresh"
        }
        let directory = backend.root.appendingPathComponent("logs")
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let file = directory.appendingPathComponent("refresh-events.jsonl")
        if !FileManager.default.fileExists(atPath: file.path) { FileManager.default.createFile(atPath: file.path, contents: nil, attributes: [.posixPermissions: 0o600]) }
        if let bytes = try? JSONSerialization.data(withJSONObject: ["event": "manual_refresh_presented", "elapsed_ms": Int(elapsed * 1000), "mode": compactMode ? "compact" : "main"]),
           let handle = try? FileHandle(forWritingTo: file) {
            handle.seekToEndOfFile(); handle.write(bytes + Data([10])); try? handle.close()
        }
    }
    @objc func manualRefresh() {
        guard !terminating else { return }
        if isMainWindowProcess { sendHost("refreshQuota") }
        else { quotaReader.refresh(force: true) }
        refreshLocal()
    }
    @objc func refreshLocal() {
        guard pendingRefresh == nil, !terminating else { return }
        reportLocalUpdate(busy: true)
        if !isMainWindowProcess, mainWindowOpen || windowProcesses.mainIsRunning {
            requestHelperRefresh()
            return
        }
        manualBeganAt = Date(); manualExpectedStamp = nil
        if compactMode {
            pendingRefresh = -1; refreshStarted = Date()
            capsuleState.enabled = false
            capsuleState.indicator = "busy"
            capsuleState.status = "正在刷新…"
            fetchCompact(manual: true); return
        }
        if backend.url == nil && !backend.starting && !restarting { retries = 0; start() }
        else if backend.url != nil {
            // Retire reads begun before this request so they cannot publish an old
            // summary after the manual scan finishes.
            summaryRequest?.cancel(); summaryRequest = nil; summaryRequestID += 1
            floatingRequest?.cancel(); floatingRequest = nil; floatingRequestID += 1
            pendingRefresh = -1; refreshStarted = Date()
            capsuleState.indicator = "busy"
            refreshButton?.title = "刷新中…"; refreshButton?.isEnabled = false
            capsuleState.enabled = false; exportButton?.isEnabled = false
            statusText = "正在重新读取日志…"; capsuleState.status = "正在刷新…"
            refreshCommand = post("api/refresh", body: ["wait_ms": 2000]) { [weak self] result in
                guard let self = self else { return }
                self.refreshCommand = nil
                switch result {
                case .success(let json):
                    guard let ticket = numeric(json["ticket"]) else {
                        self.reportHelperRefresh(success: false, message: "刷新请求失败，请重试")
                        self.finishRefreshing(); self.statusText = "刷新请求失败，请重试"; return
                    }
                    self.pendingRefresh = Int(ticket)
                    self.manualExpectedStamp = json["generated_at"] as? String
                    self.request?.cancel(); self.request = nil; self.requestID += 1
                    if (numeric(json["completed"]) ?? -1) >= ticket {
                        self.finishRefreshing()
                        if let error = json["refresh_error"] as? String {
                            self.reportHelperRefresh(success: false, message: error)
                            self.manualBeganAt = nil; self.manualExpectedStamp = nil
                            self.statusText = error; self.capsuleState.status = "刷新失败 · 保留旧快照"
                        } else { self.refreshVisibleData() }
                    } else {
                        self.manualTimer?.invalidate()
                        self.manualTimer = Timer(timeInterval: 120, repeats: false) { [weak self] _ in
                            guard let self = self, self.pendingRefresh != nil else { return }
                            self.finishRefreshing(); self.manualBeganAt = nil; self.manualExpectedStamp = nil
                            self.reportHelperRefresh(success: false, message: "扫描耗时较长，仍在后台进行")
                        }
                        RunLoop.main.add(self.manualTimer!, forMode: .common)
                        if self.backend.eventsAvailable { self.handleBackendState(self.backend.latestEvent) }
                        else { self.scheduleBackendFallback(); self.poll(force: true) }
                    }
                case .failure:
                    self.reportHelperRefresh(success: false, message: "刷新失败，请重试")
                    self.finishRefreshing(); self.manualBeganAt = nil; self.manualExpectedStamp = nil
                    self.statusText = "刷新失败，请重试"
                    self.capsuleState.status = self.statusText
                }
            }
        }
    }
    func finishRefreshing() {
        manualTimer?.invalidate(); manualTimer = nil
        pendingRefresh = nil; refreshStarted = nil
        capsuleState.indicator = "refresh"
        refreshButton?.title = "刷新"; refreshButton?.isEnabled = true; capsuleState.enabled = true
        exportButton?.isEnabled = !snapshot.isEmpty && !usageSession.needsRead && usageSession.identity == usageIdentity
    }
    func recover(_ reason: String) {
        guard isMainWindowProcess, !terminating, !restarting, !compactMode, dashboard != nil else { return }
        if retries >= 3 {
            reportHelperRefresh(success: false, message: "主面板统计恢复失败")
            statusText = "恢复失败 · 点击刷新重试"
            restarting = true; request?.cancel(); request = nil; requestID += 1
            backend.stop { self.restarting = false }
            exportButton?.isEnabled = false
            return
        }
        retries += 1; statusText = "\(reason) · 正在重新连接…"
        restartWorker()
    }
    @objc func restart() { retries = 0; restartWorker() }
    func restartWorker() {
        guard isMainWindowProcess, !restarting, !terminating, !compactMode, dashboard != nil else { return }
        restarting = true; request?.cancel(); request = nil; requestID += 1
        backendScanDeadline?.cancel(); backendScanDeadline = nil
        timer?.invalidate(); timer = nil; backendFallbackDelay = 5
        refreshCommand?.cancel(); refreshCommand = nil; finishRefreshing()
        floatingRequest?.cancel(); floatingRequest = nil; floatingRequestID += 1
        summaryRequest?.cancel(); summaryRequest = nil; summaryRequestID += 1; compactStamp = nil
        settingsCommand?.cancel(); settingsID += 1
        settingsCommand = nil; settingsQueue.interrupt()
        usageSession.invalidate(); clearUsageDisplay()
        exportButton?.isEnabled = false
        backend.stop { [weak self] in
            guard let self = self else { return }
            self.restarting = false
            guard !self.terminating else { return }
            if self.compactMode { self.fetchCompact(manual: false) }
            else if self.dashboard != nil { self.start() }
        }
    }
    @objc func exportCSV() {
        guard mainVisible else { showDashboard(); return }
        guard exportButton?.isEnabled == true, usageSession.data != nil, !usageSession.needsRead else { return }
        let panel = NSSavePanel()
        panel.title = "导出当前筛选结果"; panel.nameFieldStringValue = "codex-usage-\(days)-\(group).csv"
        panel.allowedFileTypes = ["csv"]; panel.canCreateDirectories = true
        let scope = NSPopUpButton(frame: NSRect(x: 0, y: 0, width: 260, height: 28))
        scope.addItems(withTitles: ["当前显示行（含搜索与排序）", "当前范围全部行"])
        scope.setAccessibilityLabel("导出范围"); panel.accessoryView = scope
        panel.message = "导出已显示的统计快照；写入期间范围或显示结果改变时请重新导出。"
        panel.beginSheetModal(for: window) { [weak self] result in
            guard result == .OK, let destination = panel.url, let self = self else { return }
            let identity = self.usageIdentity, serial = self.usageSession.serial, selection = self.tableSelection
            let tableRevision = self.tableRevision
            guard self.usageSession.matches(identity, serial: serial) else { self.alert("请重新导出", "当前范围尚未读取成功。"); return }
            let visible = scope.indexOfSelectedItem == 0
            let rows = selection.rows(self.rows, visible: visible)
            let temporary = destination.deletingLastPathComponent().appendingPathComponent(".codex-export-\(UUID().uuidString)")
            DispatchQueue.global(qos: .userInitiated).async {
                do {
                    try MainTableSelection.csv(rows).write(to: temporary, options: .withoutOverwriting)
                    DispatchQueue.main.async {
                        defer { try? FileManager.default.removeItem(at: temporary) }
                        guard !self.terminating, self.usageIdentity == identity,
                              self.usageSession.matches(identity, serial: serial),
                              !visible || (self.tableSelection == selection && self.tableRevision == tableRevision) else {
                            if !self.terminating { self.alert("请重新导出", "范围或显示结果已经改变，原文件未被替换。") }; return
                        }
                        // Commit and validation share the main queue; range changes cannot interleave.
                        if rename(temporary.path, destination.path) == 0 { self.statusText = "已导出 \(destination.lastPathComponent)" }
                        else { self.alert("无法保存文件", String(cString: strerror(errno))) }
                    }
                } catch {
                    try? FileManager.default.removeItem(at: temporary)
                    DispatchQueue.main.async { if !self.terminating { self.alert("无法保存文件", error.localizedDescription) } }
                }
            }
        }
    }
    @objc func chooseHome() {
        if !isMainWindowProcess { usagePreferences.set(true, forKey: "openChooseHome"); openMainWindow(); return }
        showDashboard()
        let panel = NSOpenPanel(); panel.canChooseFiles = false; panel.canChooseDirectories = true; panel.allowsMultipleSelection = false
        panel.showsHiddenFiles = true; panel.directoryURL = codexHome; panel.message = "选择包含 sessions 或 archived_sessions 的 Codex 数据文件夹。"
        panel.beginSheetModal(for: window) { [weak self] result in
            guard result == .OK, let url = panel.url, let self = self else { return }
            usagePreferences.set(url.path, forKey: "codexHome")
            self.sendHost("homeChanged")
            self.model = "all"; self.task = "all"; self.retries = 0
            self.models.selectItem(at: 0); self.tasks.selectItem(at: 0)
            self.restartWorker()
        }
    }
    @objc func showSupport() { NSWorkspace.shared.open(backend.root) }
    @objc func about() {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "未知版本"
        let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "—"
        let provenance = (try? Data(contentsOf: usageResourcesURL.appendingPathComponent("BUILD-INFO.json"))).flatMap { try? JSONSerialization.jsonObject(with: $0) as? Object }
        let revision = String((provenance?["revision"] as? String ?? "未知源码").prefix(12)) + (provenance?["dirty"] as? Bool == true ? " + 工作区修改" : "")
        alert("Codex 用量 \(version) (\(build))", "原生 macOS 用量面板\n本地 Token 统计与账号剩余额度。\n额度由 Codex 的只读接口提供，重置卡仅显示数量。\n可调自动刷新和桌面胶囊。\n按 ⌘Q 退出会停止本工具的采集进程。\n\n当前运行：\(nativeArchitecture) · macOS 11+\n源码：\(revision)\n独立工具，与 OpenAI 官方无隶属关系。")
    }
    @objc func showCoverage() {
        let meta = snapshot["meta"] as? Object ?? [:]
        let quality = meta["coverage"] as? Object ?? [:]
        let gaps = quality["cumulative_gaps"] as? [Object] ?? []
        let issues = meta["issues"] as? [String] ?? []
        var text = "数据文件夹：\(codexHome.path)\n\n输入包含缓存输入；输出包含推理。缓存不再重复加入总计。日界线使用 UTC+08:00。这些是日志记录的 Token，不代表订阅剩余额度或费用。\n\n"
        text += "扫描日志：\(exact(meta["scanned_files"]))\n未计入的旧任务：\(exact(meta["excluded_legacy_threads"]))\n累计计数不一致：\(gaps.count) 个任务\n"
        text += "旧日志可能缺少可归属的逐轮数据；无法可靠还原的记录不会按零值补齐。当前：\(intervalDescription)。手动刷新立即请求重新扫描，完成后更新窗口。统计只能读取 Codex 已写入磁盘的记录。"
        if !issues.isEmpty { text += "\n\n记录问题：\n" + issues.prefix(8).joined(separator: "\n") }
        if !gaps.isEmpty { text += "\n\n不一致任务：\n" + gaps.prefix(5).map { "\($0["label"] as? String ?? "未命名")：差值 \(exact($0["difference"]))" }.joined(separator: "\n") }
        alert("统计说明与覆盖情况", text)
    }
    func alert(_ title: String, _ message: String) {
        guard !terminating else { return }
        let alert = NSAlert(); alert.messageText = title; alert.informativeText = message; alert.addButton(withTitle: "好")
        if let window = window, window.attachedSheet == nil { alert.beginSheetModal(for: window) }
    }
    @objc func quitApplication() {
        if isMainWindowProcess { sendHost("quit") }
        NSApp.terminate(nil)
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { isMainWindowProcess }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if isMainWindowProcess { showDashboard(); handleMainRequest(); return false }
        if trayOnly {
            // Return the reopen event before entering the menu's tracking loop.
            if !statusMenuTracking { DispatchQueue.main.async { self.openStatusMenu() } }
        } else { showDashboard() }
        return false
    }
    func windowWillClose(_ notification: Notification) {
        guard let closed = notification.object as? NSWindow, !terminating else { return }
        if closed === floating {
            hideFloating()
        } else if closed === window {
            releaseDetailData()
            DispatchQueue.main.async { [weak self, weak closed] in
                guard let self = self, let closed = closed, !self.terminating,
                      self.window === closed, !closed.isVisible else { return }
                self.hideDashboard()
            }
        }
    }
    let termination = AsyncTermination()
    var waitingForMainClose = false
    func prepareMainClose() -> Bool {
        guard isMainWindowProcess else { return true }
        saveBudgetDraft(); saveMainWindowFrame()
        let failure: String?
        if budgetPage?.saveInFlight == true { failure = "预算正在保存，请等待保存结果后关闭。" }
        else if budgetPage?.draftBlocksClosing == true { failure = budgetPage?.draftPersistenceError }
        else if !usagePreferences.synchronize() { failure = "设置尚未写入本机，请重试后关闭。" }
        else { failure = nil }
        if let error = failure {
            statusText = error; window?.makeKeyAndOrderFront(nil)
            sendHost("closeBlocked", payload: ["message": error]); return false
        }
        return true
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        sender !== window || prepareMainClose()
    }
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard prepareMainClose() else { return .terminateCancel }
        if !isMainWindowProcess, windowProcesses.mainIsRunning, !terminating {
            if !waitingForMainClose {
                waitingForMainClose = true
                windowProcesses.closeMain { [weak self] closed in
                    guard let self = self else { return }; self.waitingForMainClose = false
                    if closed { DispatchQueue.main.async { sender.terminate(nil) } }
                    else { self.mainWindowOpen = true; self.persistHostWindowMode(); self.publishHostState() }
                }
            }
            return .terminateCancel
        }
        return termination.request(sender) { finished in
        stopBudgetClients()
        notificationRouter?.stop(); taskMonitor?.stop(); appearanceObservation = nil; backendScanDeadline?.cancel(); backendScanDeadline = nil; statusPopover?.performClose(nil); statusPopover = nil
        floatingChoices.cancel()
        if isMainWindowProcess {
            saveMainWindowFrame()
            for (key, value) in [("filterDays", days), ("filterModel", model), ("filterTask", task), ("filterGroup", group)] { usagePreferences.set(value, forKey: key) }
            usagePreferences.synchronize()
        } else { mainWindowOpen = false; persistHostWindowMode() }
        terminating = true; stopCompactMonitoring(); resetFloatInteraction()
        arcColorEditor?.close(); arcColorEditor = nil; capsuleState.arcStylePreview = nil
        hostRefreshDeadline?.cancel(); hostRefreshDeadline = nil
        stopFloatMouseMonitoring(); statusSingleClick?.cancel(); manualTimer?.invalidate(); timer?.invalidate(); request?.cancel(); floatingRequest?.cancel(); summaryRequest?.cancel(); refreshCommand?.cancel(); settingsCommand?.cancel(); activeSession?.invalidateAndCancel()
        compactTimer?.invalidate(); floatCollapse?.cancel(); floatAnimation?.stop(); trimWork?.cancel()
        let group = DispatchGroup()
        if !isMainWindowProcess {
            nativeSummary.cancel(); nativeSummaryBusy = false
            windowProcesses.stop()
            group.enter(); collector.stop { group.leave() }
            group.enter(); quotaReader.stop { group.leave() }
        } else { windowProcesses.stop() }
        group.enter(); backend.stop { group.leave() }
        group.notify(queue: .main, execute: finished)
        }
    }
}
