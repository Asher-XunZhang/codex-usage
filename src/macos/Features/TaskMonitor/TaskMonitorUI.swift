import AppKit

/// Native table/selection/scroll behavior. Updates preserve the user's selected
/// IDs, and an operation finishes only after a host acknowledgement.
final class TaskMonitorPage: NSView, NSTableViewDataSource, NSTableViewDelegate, NSSearchFieldDelegate {
    let action: (String, MonitorObject) -> Void
    private let selecting: Bool
    private var selectorPage: TaskMonitorPage?
    private var selectorWindow: NSWindow?
    private let sections = FeedbackSegmentedControl(labels: ["监控中", "未读", "历史"], trackingMode: .selectOne, target: nil, action: nil)
    private let search = NSSearchField()
    private let allHistory = FeedbackButton(title: "返回全部历史", target: nil, action: nil)
    private var messageFilter: [String]?
    private let table = NSTableView()
    private let status = NSTextField(wrappingLabelWithString: "正在连接宿主…")
    private let summary = NSTextField(labelWithString: "任务监控")
    private let pagination = NSTextField(labelWithString: "")
    private let mode = NSPopUpButton()
    private let primary = FeedbackButton(title: "停止提醒", target: nil, action: nil)
    private let readAll = FeedbackButton(title: "全部标为已读", target: nil, action: nil)
    private let focus = FeedbackButton(title: "设为关注", target: nil, action: nil)
    private let clear = FeedbackButton(title: "清理已结束", target: nil, action: nil)
    private let pause = FeedbackButton(title: "暂停通知", target: nil, action: nil)
    private let recovery = FeedbackButton(title: "备份并重建…", target: nil, action: nil)
    private var rows: [MonitorObject] = []
    private var state: MonitorObject = [:]
    private var page = 0
    private var pendingID: String?
    private var timeout: DispatchWorkItem?
    private var searchWork: DispatchWorkItem?
    private var initializedMode = false
    private var section: String { selecting ? "tasks" : ["watches", "unread", "messages"][max(0, sections.selectedSegment)] }
    private var messageSection: Bool { section == "messages" || section == "unread" }
    private var selected: [MonitorObject] { table.selectedRowIndexes.compactMap { rows.indices.contains($0) ? rows[$0] : nil } }
    init(selecting: Bool = false, action: @escaping (String, MonitorObject) -> Void) {
        self.selecting = selecting; self.action = action; super.init(frame: .zero)
        sections.target = self; sections.action = #selector(sectionChanged); sections.selectedSegment = 0
        sections.setAccessibilityLabel("任务监控列表类型"); sections.isHidden = selecting
        search.placeholderString = "搜索任务或项目"; search.delegate = self; search.setAccessibilityLabel("搜索监控任务")
        summary.font = .systemFont(ofSize: 17, weight: .semibold)
        status.font = .systemFont(ofSize: 12); status.textColor = .secondaryLabelColor; status.isSelectable = true
        pagination.font = .systemFont(ofSize: 12); pagination.textColor = .secondaryLabelColor
        mode.target = self; mode.action = #selector(modeChanged)
        mode.addItems(withTitles: ["仅所选本轮", "持续每一轮"]); mode.setAccessibilityLabel("提醒策略")
        for (identifier, title, width) in [("title", "任务 / 项目", 320.0), ("status", "状态", 160.0), ("detail", "提醒与来源", 240.0)] {
            let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(identifier)); column.title = title; column.width = width; column.minWidth = 100
            table.addTableColumn(column)
        }
        table.target = self; table.doubleAction = #selector(showDetails)
        table.delegate = self; table.dataSource = self; table.allowsMultipleSelection = true; table.rowHeight = 54
        table.style = .inset; table.columnAutoresizingStyle = .lastColumnOnlyAutoresizingStyle
        table.setAccessibilityLabel("任务监控记录"); table.usesAlternatingRowBackgroundColors = false
        let scroll = NSScrollView(); scroll.documentView = table; scroll.hasVerticalScroller = true; scroll.drawsBackground = false
        func button(_ title: String, _ selector: Selector) -> NSButton { let b = FeedbackButton(title: title, target: self, action: selector); b.bezelStyle = .rounded; return b }
        allHistory.target = self; allHistory.action = #selector(clearMessageFilter); allHistory.bezelStyle = .rounded; allHistory.isHidden = true
        readAll.target = self; readAll.action = #selector(readAllAction)
        primary.target = self; primary.action = #selector(primaryAction)
        focus.target = self; focus.action = #selector(focusAction)
        clear.target = self; clear.action = #selector(clearAction)
        pause.target = self; pause.action = #selector(pauseAction)
        recovery.target = self; recovery.action = #selector(recoverAction); recovery.isHidden = true
        for button in [primary, focus, clear, pause, recovery, readAll] { button.bezelStyle = .rounded }
        let choose = button(selecting ? "完成选择" : "选择任务…", #selector(chooseTasks))
        let top = NSStackView(views: [sections, search, choose, button("重新核对", #selector(refreshAction))]); top.spacing = 12
        let tools = NSStackView(views: [mode, primary, readAll, focus, clear, pause, recovery]); tools.spacing = 10
        let bottom = NSStackView(views: [button("上一页", #selector(previousPage)), pagination, button("下一页", #selector(nextPage)), allHistory]); bottom.spacing = 12
        let stack = NSStackView(views: [summary, top, tools, scroll, status, bottom]); stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 14
        stack.translatesAutoresizingMaskIntoConstraints = false; addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: leadingAnchor), stack.trailingAnchor.constraint(equalTo: trailingAnchor), stack.topAnchor.constraint(equalTo: topAnchor), stack.bottomAnchor.constraint(equalTo: bottomAnchor),
            top.widthAnchor.constraint(equalTo: stack.widthAnchor), search.widthAnchor.constraint(greaterThanOrEqualToConstant: 180),
            scroll.widthAnchor.constraint(equalTo: stack.widthAnchor), scroll.heightAnchor.constraint(greaterThanOrEqualToConstant: 120),
            status.widthAnchor.constraint(equalTo: stack.widthAnchor)
        ])
        scroll.setContentHuggingPriority(.defaultLow, for: .vertical)
        updateControls()
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    deinit { timeout?.cancel(); searchWork?.cancel() }
    func showSection(_ name: String) {
        guard !selecting, let index = ["watches", "unread", "messages"].firstIndex(of: name) else { return }
        sections.selectedSegment = index; sectionChanged()
    }
    func showMessages(_ ids: [String]) {
        guard !selecting else { return }
        searchWork?.cancel(); search.stringValue = ""; sections.selectedSegment = 2
        messageFilter = MonitorJSON.notificationIDs(ids); allHistory.isHidden = false
        page = 0; rows = []; table.reloadData(); request(); updateControls()
    }
    @objc private func clearMessageFilter() { showSection("messages") }
    func request() {
        var payload: MonitorObject = ["section": section, "page": page, "query": search.stringValue]
        if let ids = messageFilter { payload["messageIDs"] = ids }
        action("snapshot", payload)
    }
    func update(_ state: MonitorObject) {
        self.state = state; selectorPage?.update(state)
        if selecting && !initializedMode, let settings = state["settings"] as? MonitorObject {
            mode.selectItem(at: settings["defaultMode"] as? String == "each" ? 1 : 0); initializedMode = true
        }
        let counts = state["summary"] as? MonitorObject ?? [:]
        summary.stringValue = selecting ? "选择正在执行的任务" : "\(counts["active"] as? Int ?? 0) 项监控 · \(counts["unread"] as? Int ?? 0) 条未读"
        sections.setLabel("未读 \(counts["unread"] as? Int ?? 0)", forSegment: 1)
        let source = state["sourceStatus"] as? MonitorObject ?? [:]
        if pendingID == nil {
            let error = state["error"] as? String ?? "", sourceError = source["error"] as? String ?? ""
            status.stringValue = !error.isEmpty ? error : !sourceError.isEmpty ? sourceError : source["scanning"] as? Bool == true ? "正在分批核对日志，尚未确认的任务不会被视为已结束。" : "只监控所选任务的明确轮次事件；停止提醒不会停止 Codex。"
            status.textColor = error.isEmpty && sourceError.isEmpty ? .secondaryLabelColor : .systemOrange
        }
        recovery.isHidden = (state["recovery"] as? MonitorObject)?["required"] as? Bool != true
        if state["section"] as? String == section && state["query"] as? String == search.stringValue && state["messageIDs"] as? [String] == messageFilter {
            let ids = Set(selected.compactMap { $0["id"] as? String })
            let incoming = state["rows"] as? [MonitorObject] ?? []; page = state["page"] as? Int ?? 0
            if !(rows as NSArray).isEqual(to: incoming) {
                rows = incoming; table.reloadData()
                table.selectRowIndexes(IndexSet(rows.indices.filter { ids.contains(rows[$0]["id"] as? String ?? "") }), byExtendingSelection: false)
            }
            if messageFilter != nil && pendingID == nil {
                status.stringValue = rows.isEmpty ? "通知关联消息已清理或不符合搜索条件；可返回全部历史。" : "仅显示本次通知关联消息；打开列表不会自动标为已读。"
            }
            pagination.stringValue = (messageFilter == nil ? "" : "通知消息 · ") + "第 \(page + 1) / \(state["pages"] as? Int ?? 1) 页 · \(state["total"] as? Int ?? 0) 项"
        }
        updateControls()
    }
    func result(_ reply: MonitorObject) {
        selectorPage?.result(reply)
        guard let pending = pendingID, reply["requestID"] as? String == pending else { return }
        pendingID = nil; timeout?.cancel(); timeout = nil
        status.stringValue = reply["error"] as? String ?? reply["message"] as? String ?? "已更新"
        if let items = reply["items"] as? [MonitorObject], let failure = items.first(where: { $0["ok"] as? Bool != true }) { status.stringValue += "；" + (failure["error"] as? String ?? "部分项目未成功") }
        status.textColor = reply["ok"] as? Bool == true ? .secondaryLabelColor : .systemOrange
        updateControls()
    }
    private func perform(_ operation: String, _ payload: MonitorObject = [:]) {
        guard pendingID == nil else { return }
        let id = UUID().uuidString; pendingID = id
        var payload = payload; payload["requestID"] = id
        status.stringValue = "正在保存…"; updateControls(); action(operation, payload)
        let work = DispatchWorkItem { [weak self] in
            guard let self = self, self.pendingID == id else { return }
            self.pendingID = nil; self.status.stringValue = "尚未收到宿主确认，选择已保留。请重新核对后重试。"; self.updateControls()
        }
        timeout = work; DispatchQueue.main.asyncAfter(deadline: .now() + 10, execute: work)
    }
    private func updateControls() {
        primary.title = section == "tasks" ? "开启 \(selected.count) 项提醒" : messageSection ? "标为已读" : "停止提醒"
        primary.isEnabled = pendingID == nil && !selected.isEmpty && (section != "tasks" || selected.allSatisfy { $0["selectable"] as? Bool == true })
        mode.isHidden = messageSection; mode.isEnabled = pendingID == nil && (selecting || selected.count == 1 && selected.first?["active"] as? Bool == true)
        if !selecting { mode.selectItem(at: selected.first?["mode"] as? String == "each" ? 1 : 0) }
        focus.isHidden = section != "watches"
        focus.title = selected.count == 1 && selected.first?["id"] as? String == (state["settings"] as? MonitorObject)?["focusID"] as? String ? "取消关注" : "设为关注"
        focus.isEnabled = pendingID == nil && selected.count == 1
        clear.isHidden = section == "tasks" || messageFilter != nil; clear.title = messageSection ? "清理已读历史" : "清理已结束"
        readAll.title = messageFilter == nil ? "全部标为已读" : "本通知全部标为已读"
        readAll.isHidden = !messageSection; readAll.isEnabled = pendingID == nil && ((state["summary"] as? MonitorObject)?["unread"] as? Int ?? 0) > 0
        clear.isEnabled = pendingID == nil; pause.isEnabled = pendingID == nil; pause.isHidden = selecting
        let until = ((state["settings"] as? MonitorObject)?["pausedUntil"] as? NSNumber)?.doubleValue ?? 0
        pause.title = until < 0 || until > Date().timeIntervalSince1970 ? "恢复任务通知" : "暂停通知 30 分钟"
    }
    func numberOfRows(in tableView: NSTableView) -> Int { rows.count }
    func tableViewSelectionDidChange(_ notification: Notification) { updateControls() }
    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        let item = rows[row], key = tableColumn?.identifier.rawValue ?? "title"
        let field = NSTextField(wrappingLabelWithString: ""); field.font = .systemFont(ofSize: 12); field.maximumNumberOfLines = 2; field.lineBreakMode = .byTruncatingTail
        let code = item["status"] as? String ?? "unknown"
        if key == "title" { field.stringValue = (item["title"] as? String ?? "未命名任务") + "\n" + (item["project"] as? String ?? ""); field.toolTip = field.stringValue }
        else if key == "status" {
            field.stringValue = ["running": "执行中", "completed": "本轮已结束", "interrupted": "本轮已中断", "idle": "等待下一轮", "unknown": "状态待确认"][code] ?? "状态待确认"
            if messageSection { field.stringValue += item["read"] as? Bool == true ? " · 已读" : " · 未读" }
            field.textColor = code == "unknown" || code == "interrupted" ? .systemOrange : .labelColor
        } else {
            let error = item["sourceError"] as? String ?? ""
            field.stringValue = !error.isEmpty ? error : messageSection ? (item["reason"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? "已记录任务结果" : item["mode"] as? String == "each" ? "持续每一轮" : section == "tasks" ? (item["selectable"] as? Bool == true ? "可选择正在执行的本轮" : "当前不可选择，请等待新的执行轮次") : "仅所选本轮"
            field.textColor = .secondaryLabelColor; field.toolTip = field.stringValue
        }
        return field
    }
    @objc private func chooseTasks() {
        if selecting { action("dismiss-selector", [:]); return }
        guard selectorWindow == nil, let window = window else { return }
        let selector = TaskMonitorPage(selecting: true) { [weak self] operation, payload in
            guard let self = self else { return }
            if operation == "dismiss-selector" {
                if let sheet = self.selectorWindow { window.endSheet(sheet); sheet.orderOut(nil) }
                self.selectorWindow = nil; self.selectorPage = nil; self.request()
            } else { self.action(operation, payload) }
        }
        let sheet = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 880, height: 560), styleMask: [.titled, .resizable], backing: .buffered, defer: false)
        sheet.title = "选择任务"; sheet.minSize = NSSize(width: 760, height: 460)
        let content = NSView(); sheet.contentView = content; selector.translatesAutoresizingMaskIntoConstraints = false; content.addSubview(selector)
        NSLayoutConstraint.activate([selector.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 20), selector.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -20), selector.topAnchor.constraint(equalTo: content.topAnchor, constant: 20), selector.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -20)])
        selector.update(state); selectorPage = selector; selectorWindow = sheet; window.beginSheet(sheet); selector.request()
    }
    @objc private func sectionChanged() {
        messageFilter = nil; allHistory.isHidden = true
        page = 0; rows = []; table.reloadData(); request(); updateControls()
    }
    func controlTextDidChange(_ obj: Notification) {
        searchWork?.cancel(); let work = DispatchWorkItem { [weak self] in self?.page = 0; self?.request() }; searchWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: work)
    }
    @objc private func primaryAction() {
        if section == "tasks" { perform("add", ["mode": mode.indexOfSelectedItem == 1 ? "each" : "once", "selections": selected.map { ["id": $0["id"] ?? "", "turnID": $0["turnID"] ?? ""] }]) }
        else if messageSection { perform("read", ["ids": selected.compactMap { $0["id"] as? String }]) }
        else { perform("stop", ["ids": selected.compactMap { $0["id"] as? String }]) }
    }
    @objc private func focusAction() { if let id = selected.first?["id"] as? String { perform("focus", ["id": (state["settings"] as? MonitorObject)?["focusID"] as? String == id ? "" : id]) } }
    @objc private func modeChanged() {
        if !selecting, let id = selected.first?["id"] as? String { perform("mode", ["id": id, "mode": mode.indexOfSelectedItem == 1 ? "each" : "once"]) }
    }
    @objc private func showDetails() {
        guard let item = selected.first, let window = window else { return }
        let alert = NSAlert(); alert.messageText = item["title"] as? String ?? "任务详情"
        let stamp = (item["updatedAt"] as? NSNumber)?.doubleValue ?? (item["createdAt"] as? NSNumber)?.doubleValue
        let when = stamp.map { DateFormatter.localizedString(from: Date(timeIntervalSince1970: $0), dateStyle: .medium, timeStyle: .medium) } ?? "待确认"
        let detail = NSTextField(wrappingLabelWithString: "项目：\(item["project"] as? String ?? "—")\n任务：\(item["taskID"] as? String ?? item["id"] as? String ?? "—")\n轮次：\(item["turnID"] as? String ?? "—")\n最近事件：\(when)\n\(item["sourceError"] as? String ?? item["reason"] as? String ?? "")")
        detail.isSelectable = true; detail.frame = NSRect(x: 0, y: 0, width: 480, height: 120); alert.accessoryView = detail
        alert.addButton(withTitle: "完成"); alert.beginSheetModal(for: window)
    }
    @objc private func readAllAction() {
        if let ids = messageFilter { perform("read", ["ids": ids]) }
        else { perform("read", ["all": true]) }
    }
    @objc private func clearAction() { perform(messageSection ? "clear-history" : "clear-ended") }
    @objc private func pauseAction() {
        let until = ((state["settings"] as? MonitorObject)?["pausedUntil"] as? NSNumber)?.doubleValue ?? 0
        perform("settings", ["patch": ["pausedUntil": until < 0 || until > Date().timeIntervalSince1970 ? 0 : Date().timeIntervalSince1970 + 1800]])
    }
    @objc private func recoverAction() {
        guard let window = window else { return }
        let alert = NSAlert(); alert.messageText = "备份损坏的监控记录并重新开始？"; alert.informativeText = "原文件会保留为备份。监控与消息列表将重新建立，Codex 任务日志不受影响。"
        alert.addButton(withTitle: "备份并重建"); alert.addButton(withTitle: "取消")
        alert.beginSheetModal(for: window) { [weak self] result in if result == .alertFirstButtonReturn { self?.perform("recover", ["confirm": true]) } }
    }
    @objc private func refreshAction() { action("refresh", [:]); request() }
    @objc private func previousPage() { guard page > 0 else { return }; page -= 1; request() }
    @objc private func nextPage() { guard page + 1 < state["pages"] as? Int ?? 1 else { return }; page += 1; request() }
}
