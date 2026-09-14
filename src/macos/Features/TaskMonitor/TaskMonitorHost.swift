import AppKit

extension AppDelegate {
    func startTaskMonitoring(home: URL) {
        guard !isMainWindowProcess else { return }
        if taskMonitor == nil {
            let service = TaskMonitorService(url: backend.root.appendingPathComponent("desktop/task-monitor.json")); taskMonitor = service
            service.changed = { [weak self] state in
                guard let self = self, !self.terminating,
                      (state["sourceStatus"] as? Object)?["home"] as? String == self.codexHome.standardizedFileURL.path else { return }
                self.taskMonitorState = state; self.publishTaskMonitorState(); self.usageSettings?.update(usagePreferences, monitor: state)
                let summary = state["summary"] as? Object ?? [:]
                let unread = summary["unread"] as? Int ?? 0, active = summary["active"] as? Int ?? 0
                self.capsuleState.monitorSummary = active == 0 && unread == 0 ? "任务监控" : "监控 \(active) · 未读 \(unread)"
                self.capsuleState.monitorUnread = unread
                self.capsuleState.monitorStatus = summary["status"] as? String ?? "none"
                let focused = summary["focus"] as? Object
                self.capsuleState.monitorTitle = focused?["title"] as? String ?? (active > 0 ? "全部已选任务" : "尚未选择关注任务")
                self.capsuleState.monitorDetail = focused == nil ? "在监控列表中可指定常驻关注任务" : "常驻关注 · 其他订阅继续监测"
                self.capsuleState.monitorRows = (summary["preview"] as? [Object] ?? []).map { row in
                    (id: row["id"] as? String ?? "", title: row["title"] as? String ?? "未命名任务",
                     status: ["running": "执行中", "completed": "本轮已结束", "interrupted": "本轮已中断", "idle": "等待下一轮"][row["status"] as? String ?? ""] ?? "状态待确认", active: row["active"] as? Bool == true)
                }
                let source = state["sourceStatus"] as? Object ?? [:]
                self.capsuleState.monitorChecking = source["scanning"] as? Bool == true
                let issue = state["error"] as? String ?? "", sourceIssue = source["error"] as? String ?? ""
                self.capsuleState.monitorSource = !issue.isEmpty ? issue : !sourceIssue.isEmpty ? sourceIssue : source["scanning"] as? Bool == true ? "正在分批核对任务来源…" : "已核对 " + DateFormatter.localizedString(from: Date(timeIntervalSince1970: (source["checkedAt"] as? NSNumber)?.doubleValue ?? 0), dateStyle: .none, timeStyle: .medium)
                self.updateStatusDetail()
            }
            let router = UsageNotificationRouter(); notificationRouter = router
            router.budgets = budgetCoordinator; router.tasks = service; router.openMonitor = { [weak self] ids in
                self?.routeTaskMonitor(section: "messages", messageIDs: ids)
            }; router.start()
            service.deliver = { [weak router] messages, settings in router?.deliverTasks(messages, settings: settings) }
            compactMonitor.events = { [weak service] paths, rescan in service?.receive(paths: paths, rescan: rescan) }
        }
        taskMonitor?.start(home: home, watching: compactMonitor.isWatching)
    }
    func publishTaskMonitorState() {
        guard !isMainWindowProcess, !taskMonitorState.isEmpty else { return }
        windowProcesses.publishState(["taskMonitor": taskMonitorState])
    }
    @objc func openTaskMonitor() {
        if isMainWindowProcess { showDashboard(); switchMainPage("monitor"); return }
        routeTaskMonitor(section: "watches")
    }
    func routeTaskMonitor(section: String, messageIDs: [String]? = nil) {
        pendingTaskMonitorSection = section; pendingTaskMonitorMessageIDs = messageIDs
        pendingTaskMonitorRoute = true; pendingTaskMonitorRouteID = UUID().uuidString
        pendingTaskMonitorRouteSequence += 1
        openMainWindow(); sendTaskMonitorRoute()
    }
    func floatingMonitorAction(_ action: String) -> Bool {
        if action == "monitorMessages" {
            routeTaskMonitor(section: capsuleState.monitorUnread > 0 ? "unread" : "messages"); return true
        }
        if action == "monitorSettings" { showSettingsPage("reminders"); return true }
        if action.hasPrefix("monitorView:") {
            let id = String(action.dropFirst("monitorView:".count))
            guard let row = ((taskMonitorState["summary"] as? Object)?["preview"] as? [Object])?.first(where: { $0["id"] as? String == id }) else { return true }
            let alert = NSAlert(); alert.messageText = row["title"] as? String ?? "任务详情"
            alert.informativeText = "项目：" + (row["project"] as? String ?? "—") + "\n任务标识：" + id + "\n停止提醒只影响本工具，Codex 继续执行。"
            alert.addButton(withTitle: "完成")
            capsuleState.menuPresented = true; floatInteractionChanged(true)
            if let panel = floating { alert.beginSheetModal(for: panel) { [weak self] _ in self?.capsuleState.menuPresented = false; self?.floatInteractionChanged(false) } }
            else { capsuleState.menuPresented = false; floatInteractionChanged(false) }
            return true
        }
        let operation: String, payload: Object
        if action == "monitorClear" { operation = "clear-ended"; payload = [:] }
        else if action.hasPrefix("monitorStop:") { operation = "stop"; payload = ["ids": [String(action.dropFirst("monitorStop:".count))]] }
        else { return false }
        taskMonitor?.command(operation, payload: payload) { [weak self] reply in
            self?.capsuleState.monitorSource = reply["error"] as? String ?? reply["message"] as? String ?? "已更新"
        }
        return true
    }
    func sendTaskMonitorRoute() {
        if pendingTaskMonitorRoute {
            var payload: Object = ["section": pendingTaskMonitorSection, "routeID": pendingTaskMonitorRouteID, "routeSequence": pendingTaskMonitorRouteSequence]
            if let ids = pendingTaskMonitorMessageIDs { payload["messageIDs"] = ids }
            windowProcesses.sendToMain("taskMonitorRoute", payload: payload)
        }
    }
    func taskMonitorAction(_ action: String, payload: Object) {
        sendHost("taskMonitorAction", payload: ["action": action, "payload": payload])
    }
    func receiveTaskMonitorAction(_ action: String, payload: Object) -> Bool {
        if isMainWindowProcess {
            if action == "taskMonitorResult" { taskMonitorPage?.result(payload); return true }
            if action == "taskMonitorRoute" {
                let id = payload["routeID"] as? String ?? ""
                let sequence = payload["routeSequence"] as? Int ?? 0
                if id.isEmpty || sequence > handledTaskMonitorRouteSequence {
                    openTaskMonitor()
                    if payload["messageIDs"] != nil { taskMonitorPage?.showMessages(MonitorJSON.notificationIDs(payload["messageIDs"])) }
                    else { taskMonitorPage?.showSection(payload["section"] as? String ?? "watches") }
                    handledTaskMonitorRouteSequence = sequence
                }
                sendHost("taskMonitorRouteAck", payload: ["routeID": id]); return true
            }
            return false
        }
        if action == "taskMonitorRouteAck" {
            if payload["routeID"] as? String == pendingTaskMonitorRouteID {
                pendingTaskMonitorRoute = false; pendingTaskMonitorSection = "watches"; pendingTaskMonitorMessageIDs = nil
            }
            return true
        }
        guard action == "taskMonitorAction" else { return false }
        let operation = payload["action"] as? String ?? "", body = payload["payload"] as? Object ?? [:]
        guard ["snapshot", "refresh", "add", "stop", "mode", "read", "focus", "settings", "clear-ended", "clear-history", "recover"].contains(operation) else { return true }
        taskMonitor?.command(operation, payload: body) { [weak self] result in
            guard let self = self else { return }
            var reply = result; reply["requestID"] = body["requestID"]
            self.windowProcesses.sendToMain("taskMonitorResult", payload: reply)
            if operation == "add" && result["ok"] as? Bool == true { self.notificationRouter?.requestTaskAuthorization() }
        }
        return true
    }
}
