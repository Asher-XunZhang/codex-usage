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
    let intervalPicker = NSPopUpButton()
    let status = label("正在准备本机统计…", 12, color: .secondaryLabelColor)
    let coverage = label("仅统计这台 Mac 的已记录用量 · 日界线 UTC+08:00", 11, color: .secondaryLabelColor)
    let period = NSSegmentedControl(labels: ["今天", "7 天", "30 天", "90 天", "全部"], trackingMode: .selectOne, target: nil, action: nil)
    let grouping = NSSegmentedControl(labels: ["按模型", "按任务"], trackingMode: .selectOne, target: nil, action: nil)
    let models = NSPopUpButton()
    let tasks = NSPopUpButton()
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

final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuDelegate, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate {
    var dashboard: DashboardUI?
    var hostFloatingVisible = false
    var mainWindowOpen = false
    var hostQuotaDetail = "正在读取账号额度…"
    var hostQuotaResetLabel = "重置卡数量未知"
    var applyingHostState = false
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
    var lastMemoryTrim = -Double.infinity
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
    var statusItem: NSStatusItem?
    var statusMenuTracking = false
    var statusMenu: NSMenu?
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
    var lastPositiveRefreshSeconds = RefreshIntervalPreference.remembered(
        preferences: usagePreferences, current: usagePreferences.integer(forKey: "refreshSeconds"))
    var intervalPrompt: RefreshIntervalPrompt?
    var intervalChangeID = UUID().uuidString
    var pendingRefresh: Int?
    var refreshStarted: Date?
    var manualBeganAt: Date?
    var manualExpectedStamp: String?
    var refreshCommand: URLSessionDataTask?
    var settingsCommand: URLSessionDataTask?
    var settingsID = 0
    var intervalPicker: NSPopUpButton { dashboard!.intervalPicker }
    var floating: NSPanel?
    var capsule: CapsuleSurface?
    var floatAnimation: CapsuleAnimation?
    var floatAnchor: NSPoint?
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
    var rows: [Object] = []
    var visibleRows: [Object] = []
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
        capsuleState.refreshSeconds = autoSeconds
        capsuleState.lastPositiveRefreshSeconds = lastPositiveRefreshSeconds
        capsuleState.scope = 1
        capsuleState.rangeDays = floatingDays
        capsuleState.selectedModel = floatingModel; capsuleState.selectedTask = floatingTask
        capsuleState.loadChoices = { [weak self] kind, completion in
            self?.loadFloatingChoices(kind, completion: completion)
        }
        installWindowBridge()
        buildMenu()
        backend.onExit = { [weak self] in self?.recover("统计进程已退出") }
        if isMainWindowProcess {
            buildWindow(); start()
            timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in self?.poll() }
            timer?.tolerance = 0.1; RunLoop.main.add(timer!, forMode: .common)
            NSApp.activate(ignoringOtherApps: true)
            sendHost("requestState")
            handleMainRequest()
        } else {
            buildStatusItem()
            quotaReader.changed = { [weak self] snapshot in self?.renderQuota(snapshot) }
            quotaReader.start()
            let initialMode = usagePreferences.string(forKey: "displayMode") ?? "main"
            trayOnly = initialMode == "menu"
            if initialMode == "floating" || (initialMode == "main" && usagePreferences.bool(forKey: "floatingVisible")) { showFloating() }
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
        mainWindowOpen = false; persistHostWindowMode()
        windowProcesses.closeMain()
    }
    func publishHostState() {
        guard !isMainWindowProcess, !terminating else { return }
        let quota = quotaReader.snapshot
        usagePreferences.synchronize()
        windowProcesses.publishState([
            "floatingVisible": floating?.isVisible == true,
            "theme": capsuleState.theme.rawValue, "refreshSeconds": autoSeconds,
            "lastPositiveRefreshSeconds": lastPositiveRefreshSeconds,
            "intervalChangeID": intervalChangeID,
            "quotaDetail": quota.detail, "quotaResetLabel": quota.resetLabel
        ])
    }
    func receiveHostState(_ state: Object) {
        guard isMainWindowProcess, !terminating else { return }
        applyingHostState = true; defer { applyingHostState = false }
        hostFloatingVisible = state["floatingVisible"] as? Bool ?? hostFloatingVisible
        floatingButton?.title = hostFloatingVisible ? "隐藏浮窗" : "显示浮窗"
        hostQuotaDetail = state["quotaDetail"] as? String ?? hostQuotaDetail
        hostQuotaResetLabel = state["quotaResetLabel"] as? String ?? hostQuotaResetLabel
        dashboard?.quotaText.stringValue = hostQuotaDetail; dashboard?.quotaCards.stringValue = hostQuotaResetLabel
        if let value = state["theme"] as? String { applyCapsuleTheme(CapsuleTheme(storedValue: value)) }
        let remembered = state["lastPositiveRefreshSeconds"] as? Int
        let changeID = state["intervalChangeID"] as? String
        if let seconds = state["refreshSeconds"] as? Int,
           seconds != autoSeconds || (changeID != nil && changeID != intervalChangeID) {
            applyInterval(seconds, refreshAfterChange: false, rememberedSeconds: remembered, changeID: changeID)
        } else if let remembered = remembered, (1...3600).contains(remembered) {
            rememberRefreshInterval(autoSeconds > 0 ? autoSeconds : remembered)
            if let changeID = changeID { intervalChangeID = changeID }
        }
        usagePreferences.synchronize(); handleMainRequest()
    }
    func receiveWindowAction(_ action: String, payload: Object) {
        guard !terminating else { return }
        if isMainWindowProcess {
            if action == "close" { hideDashboard() }
            else if action == "refresh" {
                if let id = payload["requestID"] as? String { helperRefreshIDs.insert(id) }
                if pendingRefresh != nil { return }
                if backend.url == nil || backend.starting || restarting {
                    helperRefreshQueued = true
                    if !backend.starting && !backend.stopping && !restarting { start() }
                } else { manualRefresh() }
            }
            else if action == "focus" { showDashboard(); handleMainRequest() }
            return
        }
        usagePreferences.synchronize()
        switch action {
        case "requestState", "helperReady":
            mainWindowOpen = true; persistHostWindowMode()
            if hostRefreshID == nil { finishRefreshing() }
            collector.stop { [weak self] in
                guard let self = self, !self.terminating else { return }
                self.publishHostState(); self.compactSelectionQueued = true; self.fetchCompact(manual: false)
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
            if let seconds = payload["seconds"] as? Int {
                applyInterval(seconds, refreshAfterChange: false, rememberedSeconds: payload["lastPositiveRefreshSeconds"] as? Int,
                              changeID: payload["intervalChangeID"] as? String)
            }
        case "intervalRollback": receiveIntervalRollback(payload)
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
        if success { compactSelectionQueued = true; fetchCompact(manual: false) }
    }
    func reportHelperRefresh(success: Bool, message: String) {
        guard isMainWindowProcess else { return }
        let ids = helperRefreshIDs; helperRefreshIDs = []; helperRefreshQueued = false
        for id in ids { sendHost("refreshResult", payload: ["requestID": id, "success": success, "message": message]) }
    }
    func buildMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem(); main.addItem(appItem)
        let appMenu = NSMenu(); appItem.submenu = appMenu
        appMenu.addItem(withTitle: "关于 Codex 用量", action: #selector(about), keyEquivalent: "")
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "选择 Codex 数据文件夹…", action: #selector(chooseHome), keyEquivalent: ",")
        appMenu.addItem(withTitle: "打开支持文件夹", action: #selector(showSupport), keyEquivalent: "")
        appMenu.addItem(capsuleThemeMenuItem())
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "隐藏主面板", action: #selector(hideDashboard), keyEquivalent: "h")
        appMenu.addItem(withTitle: "退出 Codex 用量", action: #selector(quitApplication), keyEquivalent: "q")
        let fileItem = NSMenuItem(); main.addItem(fileItem)
        let file = NSMenu(title: "文件"); fileItem.submenu = file
        file.addItem(withTitle: "刷新", action: #selector(manualRefresh), keyEquivalent: "r")
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
        usagePreferences.set(theme.rawValue, forKey: "capsuleTheme")
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
    func buildWindow() {
        guard isMainWindowProcess, dashboard == nil else { return }
        usedDashboard = true
        dashboard = DashboardUI()
        dashboard?.status.stringValue = statusText
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1220, height: 840), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Codex 用量"
        window.subtitle = "本机统计 · \(nativeArchitecture)"
        window.minSize = NSSize(width: 1040, height: 740)
        window.center()
        window.setFrameAutosaveName("CodexUsageMain")
        window.isReleasedWhenClosed = false
        window.delegate = self
        let root = window.contentView!
        let title = stack([label("Codex 用量", 27, .bold), spacer(), status])
        status.setAccessibilityIdentifier("connectionStatus")
        intervalPicker.target = self; intervalPicker.action = #selector(intervalChanged(_:))
        intervalPicker.setAccessibilityLabel("自动刷新间隔")
        intervalPicker.widthAnchor.constraint(equalToConstant: 130).isActive = true
        rebuildIntervalPicker()
        floatingButton = NSButton(title: (isMainWindowProcess ? hostFloatingVisible : floating?.isVisible == true) ? "隐藏浮窗" : "显示浮窗", target: self, action: #selector(toggleFloating))
        floatingButton?.bezelStyle = .rounded
        let trayButton = NSButton(title: "仅状态栏", target: self, action: #selector(onlyStatusBar)); trayButton.bezelStyle = .rounded
        let subtitle = stack([label("看清每次思考的用量。", 13, color: .secondaryLabelColor), spacer(), label("自动刷新", 11, color: .secondaryLabelColor), intervalPicker, floatingButton, trayButton])
        period.selectedSegment = ["1", "7", "30", "90", "all"].firstIndex(of: days) ?? 2; period.target = self; period.action = #selector(filterChanged)
        period.setAccessibilityLabel("时间范围")
        models.addItem(withTitle: "全部模型"); models.target = self; models.action = #selector(filterChanged); models.setAccessibilityLabel("模型筛选")
        tasks.addItem(withTitle: "全部任务"); tasks.target = self; tasks.action = #selector(filterChanged); tasks.setAccessibilityLabel("任务筛选")
        models.widthAnchor.constraint(equalToConstant: 170).isActive = true
        tasks.widthAnchor.constraint(equalToConstant: 260).isActive = true
        refreshButton = NSButton(title: "刷新", target: self, action: #selector(manualRefresh))
        refreshButton?.bezelStyle = .rounded
        exportButton = NSButton(title: "导出 CSV…", target: self, action: #selector(exportCSV)); exportButton?.bezelStyle = .rounded
        exportButton?.isEnabled = false
        let filters = stack([period, models, tasks, spacer(), refreshButton, exportButton], spacing: 8)
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
        let detailHeader = stack([label("用量明细", 14, .semibold), grouping, rowCount, spacer(), search])
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
        let details = NSButton(title: "统计说明", target: self, action: #selector(showCoverage)); details.bezelStyle = .rounded
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
        content.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(content)
        NSLayoutConstraint.activate([
            content.topAnchor.constraint(equalTo: root.topAnchor, constant: 24), content.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -18),
            content.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 26), content.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -26)
        ])
        for v in [title, subtitle, quotaRow, filters, cards, chartPanel, detailHeader, scroll, footer] { v.widthAnchor.constraint(equalTo: content.widthAnchor).isActive = true }
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
        updateStatusTitle()
        publishHostState()
    }
    func updateStatusTitle() {
        guard !isMainWindowProcess else { return }
        let summary = todaySnapshot["summary"] as? Object ?? [:]
        statusItem?.button?.title = " \(compact(summary["total_tokens"])) · \(quotaReader.snapshot.capsuleCompact)"
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
                if self.helperRefreshQueued { self.helperRefreshQueued = false; self.manualRefresh() }
            case .failure(let error):
                self.reportHelperRefresh(success: false, message: "主面板统计启动失败")
                self.refreshButton?.title = "刷新"; self.refreshButton?.isEnabled = true
                self.statusText = "启动失败 · 点击刷新重试"; self.alert("无法启动统计", error.localizedDescription)
            }
        }
    }
    var mainVisible: Bool { isMainWindowProcess && window?.isVisible == true && window?.isMiniaturized != true && !NSApp.isHidden }
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
                    self.capsuleState.status = self.statusText
                    return
                }
                if let ticket = self.pendingRefresh {
                    self.statusText = "正在重新读取日志…"
                    self.capsuleState.status = "正在刷新…"
                    if ticket >= 0 && Int(numeric(json["refresh_completed"]) ?? -1) >= ticket {
                        self.manualExpectedStamp = json["generated_at"] as? String
                        self.finishRefreshing()
                        if let error = json["refresh_error"] as? String {
                            self.reportHelperRefresh(success: false, message: error)
                            self.manualBeganAt = nil; self.manualExpectedStamp = nil
                            self.statusText = error; self.capsuleState.status = "刷新失败 · 保留旧快照"
                            return
                        }
                        self.refreshVisibleData(); return
                    }
                    if let began = self.refreshStarted, Date().timeIntervalSince(began) > 120 {
                        self.finishRefreshing()
                        self.manualBeganAt = nil; self.manualExpectedStamp = nil
                        self.statusText = "扫描耗时较长，仍在后台进行"
                        self.reportHelperRefresh(success: false, message: self.statusText)
                    }
                    return
                }
                if json["ready"] as? Bool != true {
                    self.failures = 0
                    self.statusText = "正在索引本机记录…"
                    self.capsuleState.status = self.statusText
                    return
                }
                self.failures = 0
                if let error = json["refresh_error"] as? String {
                    self.reportHelperRefresh(success: false, message: error)
                    self.statusText = error; self.capsuleState.status = "读取失败 · 保留旧快照"
                    return
                }
                let previous = self.mainVisible ? (self.snapshot["meta"] as? Object)?["generated_at"] as? String : self.compactStamp
                if previous == nil || json["generated_at"] as? String != previous { self.refreshVisibleData() }
            }
        }
        request?.resume()
    }
    func endpoint(_ path: String) -> URL? {
        guard let base = backend.url else { return nil }
        var parts = URLComponents(url: base.appendingPathComponent(path), resolvingAgainstBaseURL: false)!
        parts.queryItems = [URLQueryItem(name: "days", value: days), URLQueryItem(name: "model", value: model), URLQueryItem(name: "task", value: task), URLQueryItem(name: "group", value: group)]
        return parts.url
    }
    func loadUsage() {
        guard mainVisible else { loadCompact(); return }
        guard let url = endpoint("api/usage"), !terminating else { return }
        requestID += 1; let id = requestID; let generation = backend.generation
        request?.cancel()
        request = session.dataTask(with: url) { [weak self] data, response, error in
            DispatchQueue.main.async {
                guard let self = self, self.requestID == id, self.backend.generation == generation, !self.terminating else { return }
                self.request = nil
                if (response as? HTTPURLResponse)?.statusCode == 400 {
                    self.model = "all"; self.task = "all"
                    self.statusText = "原筛选已不可用，显示全部记录"
                    self.loadUsage(); return
                }
                guard error == nil, (response as? HTTPURLResponse)?.statusCode == 200, let data = data,
                      let json = try? JSONSerialization.jsonObject(with: data) as? Object,
                      json["summary"] is Object, json["meta"] is Object else {
                    self.failures += 1; self.statusText = "读取暂时失败，正在重试…"
                    if self.failures >= 3 { self.recover("用量读取失败") }
                    return
                }
                self.failures = 0
                if self.mainVisible { self.snapshot = json; self.render(json) } else { self.loadCompact() }
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
        exportButton?.isEnabled = !loading && pendingRefresh == nil
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
        search.stringValue = ""
        statusText = "正在更新筛选…"
        loadUsage()
    }
    func controlTextDidChange(_ obj: Notification) { filterRows() }
    func filterRows() {
        guard dashboard != nil else { return }
        let query = search.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        visibleRows = rows.filter { query.isEmpty || ($0["label"] as? String ?? "").localizedCaseInsensitiveContains(query) }
        let descriptor = table.sortDescriptors.first
        let key = descriptor?.key ?? "total_tokens", ascending = descriptor?.ascending ?? false
        visibleRows.sort {
            if key == "label" {
                let comparison = ($0[key] as? String ?? "").localizedStandardCompare($1[key] as? String ?? "")
                return ascending ? comparison == .orderedAscending : comparison == .orderedDescending
            }
            let a = numeric($0[key]), b = numeric($1[key])
            if a == nil { return false }; if b == nil { return true }
            return ascending ? a! < b! : a! > b!
        }
        rowCount.stringValue = "\(visibleRows.count) 项"
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
        rebuildIntervalPicker()
        statusCustomInterval()
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
    func rememberRefreshInterval(_ seconds: Int) {
        guard (1...3600).contains(seconds) else { return }
        lastPositiveRefreshSeconds = seconds
        capsuleState.lastPositiveRefreshSeconds = seconds
        usagePreferences.set(seconds, forKey: RefreshIntervalPreference.lastPositiveKey)
    }
    @objc func toggleAutoRefresh() {
        applyInterval(autoSeconds > 0 ? 0 : lastPositiveRefreshSeconds)
    }
    func receiveIntervalRollback(_ payload: Object) {
        guard !isMainWindowProcess else { return }
        if let expected = payload["expectedChangeID"] as? String, expected == intervalChangeID,
           let seconds = payload["seconds"] as? Int, (0...3600).contains(seconds) {
            applyInterval(seconds, refreshAfterChange: false, rememberedSeconds: payload["lastPositiveRefreshSeconds"] as? Int)
        } else {
            // A late failure must not overwrite a newer choice, even if its
            // value happens to match again. Restore authoritative preferences.
            usagePreferences.set(autoSeconds, forKey: "refreshSeconds")
            rememberRefreshInterval(lastPositiveRefreshSeconds)
            publishHostState()
        }
    }
    func applyInterval(_ seconds: Int, refreshAfterChange: Bool = true, rememberedSeconds: Int? = nil, changeID: String? = nil) {
        guard (0...3600).contains(seconds) else { return }
        let previous = autoSeconds, previousRemembered = lastPositiveRefreshSeconds
        intervalChangeID = changeID ?? UUID().uuidString
        let change = intervalChangeID
        rememberRefreshInterval(seconds > 0 ? seconds : (rememberedSeconds ?? lastPositiveRefreshSeconds))
        autoSeconds = seconds; usagePreferences.set(seconds, forKey: "refreshSeconds"); rebuildIntervalPicker()
        if isMainWindowProcess {
            if !applyingHostState { sendHost("interval", payload: ["seconds": seconds, "lastPositiveRefreshSeconds": lastPositiveRefreshSeconds, "intervalChangeID": change]) }
        } else { publishHostState() }
        settingsID += 1; let id = settingsID
        settingsCommand?.cancel()
        if compactMode {
            scheduleCompact()
            if refreshAfterChange && seconds > 0 { manualRefresh() }
            loadFloating()
            return
        }
        guard backend.url != nil else { return }
        settingsCommand = post("api/settings", body: ["refresh_seconds": seconds, "refresh_revision": id]) { [weak self] result in
            guard let self = self, self.settingsID == id, self.intervalChangeID == change else { return }
            self.settingsCommand = nil
            switch result {
            case .success:
                if !self.snapshot.isEmpty { self.render(self.snapshot) }
                // A paused index will not publish another snapshot to update this text.
                self.loadCompact()
                if refreshAfterChange && seconds > 0 { self.manualRefresh() }
            case .failure:
                self.rememberRefreshInterval(previousRemembered)
                self.autoSeconds = previous; usagePreferences.set(previous, forKey: "refreshSeconds"); self.rebuildIntervalPicker()
                self.sendHost("intervalRollback", payload: ["seconds": previous, "lastPositiveRefreshSeconds": previousRemembered, "expectedChangeID": change])
                // The failed response may have followed a successful write.
                // A newer revision makes the rollback effective at the worker,
                // while an even newer user choice will supersede it normally.
                self.settingsID += 1
                self.settingsCommand = self.post("api/settings", body: ["refresh_seconds": previous, "refresh_revision": self.settingsID]) { _ in }
                self.statusText = "刷新间隔设置失败，请重试"
            }
        }
    }
    @objc func toggleFloating() {
        if isMainWindowProcess { sendHost("toggleFloating"); return }
        if floating?.isVisible == true { hideFloating() } else { showFloating() }
    }
    func hideFloating() {
        if isMainWindowProcess { sendHost("hideFloating"); return }
        intervalPrompt?.cancel()
        resetFloatInteraction()
        floatingChoices.cancel()
        floatCollapse?.cancel(); floatCollapse = nil
        stopFloatMouseMonitoring()
        setFloatExpanded(false, animated: false)
        saveCapsuleOrigin()
        // Explicit hiding ends the window's lifetime. Keep only its display
        // state so showing it again restores the same values and preferences.
        floating?.orderOut(nil)
        floating?.delegate = nil; floating?.contentView = nil; floating?.close()
        floating = nil; capsule = nil
        floatAnimation?.step = nil; floatAnimation?.stop(); floatAnimation = nil; floatAnchor = nil
        trimIdleMemory()
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
        floatCollapse?.cancel(); floatCollapse = nil
        setFloatExpanded(capsuleState.keepsExpanded || surface.containsScreenPoint(NSEvent.mouseLocation))
    }
    func toggleFloatKeepsExpanded() {
        capsuleState.keepsExpanded.toggle(); checkFloatPointer()
    }
    func floatInteractionChanged(_ active: Bool) {
        if active {
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
            let panel = NSPanel(contentRect: NSRect(origin: .zero, size: CapsuleSurface.small), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
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
                switch action {
                case "refresh": self.manualRefresh()
                case "details": self.toggleFloatKeepsExpanded()
                case "main": self.showDashboard()
                case "only": self.onlyFloating()
                case "close": self.closeFloating()
                case "menu": self.onlyStatusBar()
                case "quit": self.quitApplication()
                case "pin": self.toggleCapsulePin()
                case "themeDark": self.applyCapsuleTheme(.dark)
                case "themeLight": self.applyCapsuleTheme(.light)
                case "interval:custom": self.statusCustomInterval()
                case "toggleRefresh": self.toggleAutoRefresh()
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
        let anchor = floatAnchor!
        floatExpanded = expanded
        floatCollapse?.cancel(); floatCollapse = nil
        if expanded || surface.expansion > 0 { startFloatMouseMonitoring() } else { stopFloatMouseMonitoring() }
        let size = expanded ? CapsuleSurface.large : CapsuleSurface.small
        let screen = panel.screen?.visibleFrame ?? NSScreen.main!.visibleFrame
        let target = NSRect(x: min(max(anchor.x - size.width, screen.minX), screen.maxX - size.width),
                            y: min(max(anchor.y - size.height, screen.minY), screen.maxY - size.height), width: size.width, height: size.height)
        let startExpansion = surface.expansion
        let finish: () -> Void = { [weak self] in
            guard let self = self else { return }
            self.floatAnimation = nil
            if !expanded { self.stopFloatMouseMonitoring(); self.floatAnchor = nil; self.saveCapsuleOrigin() }
            else { self.checkFloatPointer() }
        }
        if !animated || NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            floatMovingFrame = true; surface.expansion = expanded ? 1 : 0; panel.setFrame(target, display: true); floatMovingFrame = false; finish(); return
        }
        let animation = CapsuleAnimation(duration: expanded ? 0.18 : 0.14, animationCurve: .easeInOut)
        animation.animationBlockingMode = .nonblocking; animation.frameRate = 60
        animation.step = { [weak self, weak panel, weak surface] progress in
            guard let self = self, !self.capsuleState.interactionActive, let panel = panel, let surface = surface else { return }
            self.floatMovingFrame = true
            let lerp: (CGFloat, CGFloat) -> CGFloat = { $0 + ($1 - $0) * progress }
            surface.expansion = lerp(startExpansion, expanded ? 1 : 0)
            panel.setFrame(NSRect(x: lerp(old.origin.x, target.origin.x), y: lerp(old.origin.y, target.origin.y),
                                 width: lerp(old.width, target.width), height: lerp(old.height, target.height)), display: true)
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
        guard !floatMovingFrame else { return }
        if let moved = notification.object as? NSWindow, moved === floating {
            if floatAnimation == nil { floatAnchor = NSPoint(x: moved.frame.maxX, y: moved.frame.maxY) }
            saveCapsuleOrigin()
        }
    }
    func trimIdleMemory() {
        trimWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self = self else { return }
            self.trimWork = nil
            guard self.compactMode, !self.terminating, !self.collector.busy, !self.nativeSummaryBusy,
                  self.pendingRefresh == nil, !self.statusMenuTracking, !self.capsuleState.interactionActive,
                  !self.floatExpanded, self.floatAnimation == nil, !self.floatMovingFrame else { return }
            self.lastMemoryTrim = ProcessInfo.processInfo.systemUptime
            malloc_zone_pressure_relief(nil, 0)
        }
        trimWork = work
        // Coalesce release events and leave interaction/animation uninterrupted.
        // This asks malloc for free pages; it cannot discard live AppKit caches.
        let delay = max(0.5, 2 - (ProcessInfo.processInfo.systemUptime - lastMemoryTrim))
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
    }
    @objc func closeFloating() { hideFloating() }
    func toggleCapsulePin() {
        capsuleState.pinned.toggle()
        floating?.level = capsuleState.pinned ? .floating : .normal
        usagePreferences.set(capsuleState.pinned, forKey: "floatingPinned")
    }
    func releaseDetailData() {
        request?.cancel(); request = nil; requestID += 1
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
        if isMainWindowProcess { window?.orderOut(nil); NSApp.terminate(nil) }
        else { closeMainWindow() }
    }
    @objc func onlyFloating() {
        guard !terminating else { return }
        if isMainWindowProcess { sendHost("onlyFloating"); return }
        showFloating(); hideDashboard()
    }
    @objc func onlyStatusBar() {
        guard !terminating else { return }
        if isMainWindowProcess { sendHost("onlyStatusBar"); return }
        hideFloating()
        hideDashboard()
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
            if timer == nil {
                timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in self?.poll() }
                timer?.tolerance = 0.1; RunLoop.main.add(timer!, forMode: .common)
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
        collectCompactSnapshot(query: query, home: home) { [weak self] result in
            guard let self = self, self.compactMode, self.compactEpoch == epoch, self.codexHome == home, !self.terminating else { return }
            self.compactLastScan = Date()
            switch result {
            case .success(let json):
                self.compactDirty = self.compactDirtyGeneration != dirtyGeneration
                self.todaySnapshot = json["today"] as? Object ?? [:]
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
                self.loadFloating()
                if manual { self.refreshPresented(self.todaySnapshot) }
                self.trimIdleMemory()
            case .failure:
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
            for (i, height) in [6.0, 13.0, 9.0].enumerated() {
                NSBezierPath(roundedRect: NSRect(x: 1 + CGFloat(i) * 5, y: 2, width: 3, height: height), xRadius: 1, yRadius: 1).fill()
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
        if (event?.clickCount ?? 0) >= 2 { showDashboard(); return }
        let work = DispatchWorkItem { [weak self] in self?.openStatusMenu() }
        statusSingleClick = work
        DispatchQueue.main.asyncAfter(deadline: .now() + NSEvent.doubleClickInterval, execute: work)
    }
    @objc func openStatusMenu() {
        if isMainWindowProcess { sendHost("statusMenu"); return }
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
        menu.addItem(.separator())
        for (title, action) in [("立即刷新", #selector(manualRefresh)), ("显示主面板", #selector(showDashboard)), ("显示 / 隐藏浮窗", #selector(toggleFloating)), ("仅浮窗", #selector(onlyFloating)), ("仅状态栏", #selector(onlyStatusBar))] {
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
        let toggle = NSMenuItem(title: autoSeconds > 0 ? "暂停自动刷新" : "恢复自动刷新（每 \(lastPositiveRefreshSeconds) 秒）", action: #selector(toggleAutoRefresh), keyEquivalent: "")
        toggle.target = self; menu.addItem(toggle)
        menu.addItem(.separator())
        let quit = NSMenuItem(title: "退出 Codex 用量", action: #selector(quitApplication), keyEquivalent: "q")
        quit.target = self; menu.addItem(quit)
    }
    @objc func statusInterval(_ sender: NSMenuItem) { applyInterval(sender.tag) }
    @objc func statusCustomInterval() {
        guard !terminating else { return }
        if let prompt = intervalPrompt { prompt.focus(); return }
        let prompt = RefreshIntervalPrompt()
        intervalPrompt = prompt
        capsuleState.dialogPresented = true; floatInteractionChanged(true)
        prompt.completion = { [weak self] value in
            guard let self = self else { return }
            self.intervalPrompt = nil; self.capsuleState.dialogPresented = false
            guard !self.terminating else { return }
            if let seconds = value { self.applyInterval(seconds) }
            self.floatInteractionChanged(false); self.trimIdleMemory()
        }
        prompt.show(initialSeconds: autoSeconds > 0 ? autoSeconds : lastPositiveRefreshSeconds,
                    near: isMainWindowProcess ? window : floating)
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
                let summary = json["summary"] as? Object ?? [:]
                self.updateStatusTitle()
                self.statusItem?.button?.toolTip = "今日 \(exact(summary["total_tokens"])) tokens · \(self.intervalDescription)"
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
        manualBeganAt = nil; manualExpectedStamp = nil
        let elapsed = Date().timeIntervalSince(began)
        let feedback = String(format: "已核对日志 · %.2f 秒", elapsed)
        reportHelperRefresh(success: true, message: feedback)
        statusText = feedback + " · " + intervalDescription
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
        guard pendingRefresh == nil, !terminating else { return }
        if isMainWindowProcess { sendHost("refreshQuota") }
        else { quotaReader.refresh(force: true) }
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
                        self.manualTimer = Timer(timeInterval: 0.15, repeats: true) { [weak self] _ in self?.poll(force: true) }
                        RunLoop.main.add(self.manualTimer!, forMode: .common)
                        self.poll(force: true)
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
        exportButton?.isEnabled = !snapshot.isEmpty
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
        refreshCommand?.cancel(); refreshCommand = nil; finishRefreshing()
        floatingRequest?.cancel(); floatingRequest = nil; floatingRequestID += 1
        summaryRequest?.cancel(); summaryRequest = nil; summaryRequestID += 1; compactStamp = nil
        settingsCommand?.cancel(); settingsID += 1
        snapshot = [:]
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
        guard exportButton?.isEnabled == true, let url = endpoint("api/export.csv") else { return }
        let panel = NSSavePanel()
        panel.title = "导出当前筛选结果"; panel.nameFieldStringValue = "codex-usage-\(days)-\(group).csv"
        panel.allowedFileTypes = ["csv"]; panel.canCreateDirectories = true
        panel.message = "导出所选时间、模型和任务的完整分组数据；明细搜索仅影响窗口显示。"
        panel.beginSheetModal(for: window) { [weak self] result in
            guard result == .OK, let destination = panel.url, let self = self else { return }
            self.session.dataTask(with: url) { data, response, error in
                guard error == nil, (response as? HTTPURLResponse)?.statusCode == 200, let data = data else {
                    DispatchQueue.main.async { self.alert("导出失败", "统计服务暂时不可用，请刷新后重试。") }; return
                }
                do {
                    try data.write(to: destination, options: .atomic)
                    DispatchQueue.main.async { self.statusText = "已导出 \(destination.lastPathComponent)" }
                } catch { DispatchQueue.main.async { self.alert("无法保存文件", error.localizedDescription) } }
            }.resume()
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
            self.restartWorker()
        }
    }
    @objc func showSupport() { NSWorkspace.shared.open(backend.root) }
    @objc func about() {
        alert("Codex 用量 1.0.0", "原生 macOS 用量面板\n本地 Token 统计与账号剩余额度。\n额度由 Codex 的只读接口提供，重置卡仅显示数量。\n可调自动刷新和桌面胶囊。\n按 ⌘Q 退出会停止本工具的采集进程。\n\n当前运行：\(nativeArchitecture) · macOS 11+\n独立工具，与 OpenAI 官方无隶属关系。")
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
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        return termination.request(sender) { finished in
        floatingChoices.cancel()
        if isMainWindowProcess {
            for (key, value) in [("filterDays", days), ("filterModel", model), ("filterTask", task), ("filterGroup", group)] { usagePreferences.set(value, forKey: key) }
            usagePreferences.synchronize()
        } else { mainWindowOpen = false; persistHostWindowMode() }
        terminating = true; intervalPrompt?.cancel(); stopCompactMonitoring(); resetFloatInteraction()
        hostRefreshDeadline?.cancel(); hostRefreshDeadline = nil
        stopFloatMouseMonitoring(); statusSingleClick?.cancel(); manualTimer?.invalidate(); timer?.invalidate(); request?.cancel(); floatingRequest?.cancel(); summaryRequest?.cancel(); refreshCommand?.cancel(); settingsCommand?.cancel(); activeSession?.invalidateAndCancel()
        compactTimer?.invalidate(); floatCollapse?.cancel(); floatAnimation?.stop(); trimWork?.cancel()
        let group = DispatchGroup()
        if !isMainWindowProcess {
            nativeSummary.cancel(); nativeSummaryBusy = false
            group.enter(); windowProcesses.closeMain { self.windowProcesses.stop(); group.leave() }
            group.enter(); collector.stop { group.leave() }
            group.enter(); quotaReader.stop { group.leave() }
        } else { windowProcesses.stop() }
        group.enter(); backend.stop { group.leave() }
        group.notify(queue: .main, execute: finished)
        }
    }
}
