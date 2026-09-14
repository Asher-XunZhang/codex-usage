import AppKit
import UserNotifications

/// One host-owned settings window. All entry points select a page in it.
final class UsageSettingsController: NSWindowController, NSWindowDelegate, NSTabViewDelegate {
    var action: ((String, MonitorObject) -> Void)?
    private let tabs = NSTabView()
    private var controls: [String: NSControl] = [:]
    private let notice = NSTextField(wrappingLabelWithString: "")
    private let source = NSTextField(wrappingLabelWithString: "")
    private let permission = NSTextField(wrappingLabelWithString: "正在读取通知权限…")
    private var dataPage = NSView()
    init() {
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 680, height: 500), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "设置"; window.isReleasedWhenClosed = false
        super.init(window: window); window.delegate = self; tabs.delegate = self
        let root = NSView(); window.contentView = root
        tabs.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(tabs)
        notice.font = .systemFont(ofSize: 12); notice.textColor = .secondaryLabelColor; notice.isSelectable = true
        notice.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(notice)
        NSLayoutConstraint.activate([tabs.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 20), tabs.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -20), tabs.topAnchor.constraint(equalTo: root.topAnchor, constant: 20), tabs.bottomAnchor.constraint(equalTo: notice.topAnchor, constant: -12), notice.leadingAnchor.constraint(equalTo: tabs.leadingAnchor), notice.trailingAnchor.constraint(equalTo: tabs.trailingAnchor), notice.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -16), notice.heightAnchor.constraint(greaterThanOrEqualToConstant: 30)])
        let appearance = page("外观", id: "appearance")
        appearance.addArrangedSubview(label("选择整体外观，也可让某个窗口使用独立主题。"))
        for (id, title, inherit) in [("appearanceGlobal", "整体外观", false), ("appearanceMain", "主面板", true), ("appearanceFloating", "悬浮窗", true), ("appearancePopover", "菜单栏详情", true)] {
            appearance.addArrangedSubview(row(title, popup(id, choices: inherit ? [("inherit", "跟随整体外观"), ("light", "浅色"), ("dark", "深色")] : [("system", "跟随系统"), ("light", "浅色"), ("dark", "深色")])))
        }
        appearance.addArrangedSubview(label("菜单栏图标随系统对比度变化；减少动态效果由系统辅助功能设置决定。"))
        let display = page("显示与浮窗", id: "display")
        display.addArrangedSubview(check("capsuleAutoHide", "靠边后自动收起为侧签"))
        display.addArrangedSubview(check("floatingPinned", "悬浮窗置顶"))
        display.addArrangedSubview(row("侧签额度数字", popup("floatingEdgeMetric", choices: [("remaining", "剩余比例"), ("used", "已用比例")])))
        display.addArrangedSubview(label("拖动松手后判断停靠；仅可见侧签响应悬停。主动收起会解除保持展开。"))
        display.addArrangedSubview(NSStackView(views: [button("显示／隐藏浮窗", "toggleFloating"), button("仅菜单栏", "onlyStatusBar"), button("仅浮窗", "onlyFloating")]))
        display.addArrangedSubview(button("打开任务监控", "monitor"))
        let data = NSTabViewItem(identifier: "data"); data.label = "数据与更新"; data.view = dataPage; tabs.addTabViewItem(data)
        let reminders = page("提醒与恢复", id: "reminders")
        reminders.addArrangedSubview(row("任务提醒方式", popup("delivery", choices: [("system", "系统通知与应用内标记"), ("markers", "仅应用内标记")])))
        reminders.addArrangedSubview(row("默认提醒范围", popup("defaultMode", choices: [("once", "仅所选本轮"), ("each", "持续每一轮")])))
        reminders.addArrangedSubview(NSStackView(views: [check("sound", "声音"), check("hideNames", "隐藏通知中的任务名称")]))
        reminders.addArrangedSubview(NSStackView(views: [check("notifyCompleted", "本轮结束通知"), check("notifyFailures", "本轮中断通知")]))
        reminders.addArrangedSubview(row("已读历史保留", popup("retentionDays", choices: [("7", "7 天"), ("30", "30 天"), ("90", "90 天"), ("365", "365 天")])) )
        reminders.addArrangedSubview(NSStackView(views: [button("暂停 30 分钟", "pauseTasks"), button("恢复任务通知", "resumeTasks"), button("系统通知设置…", "systemNotifications")]))
        permission.font = .systemFont(ofSize: 12); permission.textColor = .secondaryLabelColor; permission.isSelectable = true
        reminders.addArrangedSubview(permission)
        source.font = .systemFont(ofSize: 12); source.textColor = .secondaryLabelColor; source.isSelectable = true
        reminders.addArrangedSubview(source)
        reminders.addArrangedSubview(NSStackView(views: [button("重新核对任务", "refreshTasks"), button("监控与恢复…", "monitor"), button("预算与提醒…", "budgets")]))
        window.center()
    }
    required init?(coder: NSCoder) { fatalError() }
    func tabView(_ tabView: NSTabView, didSelect tabViewItem: NSTabViewItem?) {
        if let id = tabViewItem?.identifier as? String { action?("page", ["page": id]) }
    }
    func windowWillClose(_ notification: Notification) { action?("closed", [:]) }
    private func label(_ value: String) -> NSTextField { let label = NSTextField(wrappingLabelWithString: value); label.font = .systemFont(ofSize: 12); label.textColor = .secondaryLabelColor; return label }
    private func page(_ title: String, id: String) -> NSStackView {
        let stack = NSStackView(); stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 16
        let container = NSView(); stack.translatesAutoresizingMaskIntoConstraints = false; container.addSubview(stack)
        NSLayoutConstraint.activate([stack.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 20), stack.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -20), stack.topAnchor.constraint(equalTo: container.topAnchor, constant: 22), stack.bottomAnchor.constraint(lessThanOrEqualTo: container.bottomAnchor, constant: -16)])
        let item = NSTabViewItem(identifier: id); item.label = title; item.view = container; tabs.addTabViewItem(item); return stack
    }
    private func row(_ label: String, _ control: NSControl) -> NSStackView {
        let text = NSTextField(labelWithString: label); text.widthAnchor.constraint(equalToConstant: 130).isActive = true
        let row = NSStackView(views: [text, control]); row.spacing = 12; return row
    }
    private func popup(_ id: String, choices: [(String, String)]) -> NSPopUpButton {
        let popup = NSPopUpButton(); popup.identifier = .init(id); popup.target = self; popup.action = #selector(changed(_:)); popup.setAccessibilityLabel(id)
        for (value, label) in choices { popup.addItem(withTitle: label); popup.lastItem?.representedObject = value }
        controls[id] = popup; return popup
    }
    private func check(_ id: String, _ label: String) -> NSButton {
        let result = NSButton(checkboxWithTitle: label, target: self, action: #selector(changed(_:))); result.identifier = .init(id); controls[id] = result; return result
    }
    private func button(_ label: String, _ id: String) -> NSButton {
        let result = FeedbackButton(title: label, target: self, action: #selector(run(_:))); result.identifier = .init(id); result.bezelStyle = .rounded; return result
    }
    @objc private func changed(_ sender: NSControl) {
        guard let id = sender.identifier?.rawValue else { return }
        let value: Any = (sender as? NSPopUpButton)?.selectedItem?.representedObject ?? ((sender as? NSButton)?.state == .on)
        action?("setting", ["key": id, "value": value])
    }
    @objc private func run(_ sender: NSButton) { action?(sender.identifier?.rawValue ?? "", [:]) }
    func mountData(_ view: NSView) {
        guard view.superview !== dataPage else { return }
        view.removeFromSuperview(); view.translatesAutoresizingMaskIntoConstraints = false; dataPage.addSubview(view)
        NSLayoutConstraint.activate([view.leadingAnchor.constraint(equalTo: dataPage.leadingAnchor), view.trailingAnchor.constraint(equalTo: dataPage.trailingAnchor), view.topAnchor.constraint(equalTo: dataPage.topAnchor), view.bottomAnchor.constraint(equalTo: dataPage.bottomAnchor)])
    }
    func select(_ page: String) { tabs.selectTabViewItem(withIdentifier: page); showWindow(nil) }
    func update(_ preferences: UserDefaults, monitor: MonitorObject) {
        let settings = monitor["settings"] as? MonitorObject ?? [:]
        for (id, control) in controls {
            let value: Any?
            if id.hasPrefix("appearance") { value = preferences.string(forKey: id) ?? (id == "appearanceGlobal" ? "system" : "inherit") }
            else if ["capsuleAutoHide", "floatingPinned"].contains(id) { value = preferences.object(forKey: id) ?? true }
            else if id == "floatingEdgeMetric" { value = preferences.string(forKey: id) ?? "remaining" }
            else { value = settings[id] }
            if let popup = control as? NSPopUpButton, let value = value {
                let text = (value as? NSNumber)?.stringValue ?? value as? String ?? ""
                if let item = popup.itemArray.first(where: { $0.representedObject as? String == text }) { popup.select(item) }
            } else if let button = control as? NSButton { button.state = value as? Bool == true ? .on : .off }
        }
        let info = monitor["sourceStatus"] as? MonitorObject ?? [:]
        source.stringValue = (info["error"] as? String ?? "") + ((monitor["error"] as? String).flatMap { $0.isEmpty ? nil : "\n" + $0 } ?? "")
        let paused = ((settings["pausedUntil"] as? NSNumber)?.doubleValue ?? 0)
        if paused < 0 || paused > Date().timeIntervalSince1970 { source.stringValue += "\n任务通知已暂停，未读消息仍会记录。" }
    }
    func showResult(_ value: MonitorObject) { notice.stringValue = value["error"] as? String ?? value["message"] as? String ?? "已更新"; notice.textColor = value["ok"] as? Bool == false ? .systemOrange : .secondaryLabelColor }
    func updatePermission() {
        UNUserNotificationCenter.current().getNotificationSettings { [weak self] settings in
            DispatchQueue.main.async {
                self?.permission.stringValue = settings.authorizationStatus == .authorized || settings.authorizationStatus == .provisional ? "系统通知已授权；横幅与声音仍受系统设置和专注模式控制。" : "系统通知尚未授权或已关闭；结果仍保留在应用内。"
            }
        }
    }
}

extension AppDelegate {
    @objc func showSettings() { showSettingsPage(usagePreferences.string(forKey: "settingsPage") ?? "appearance") }
    func showSettingsPage(_ page: String) {
        if isMainWindowProcess { sendHost("settingsWindow", payload: ["page": page]); return }
        if usageSettings == nil {
            let controller = UsageSettingsController(); usageSettings = controller
            controller.action = { [weak self] action, payload in self?.settingsAction(action, payload: payload) }
        }
        ensureUpdateStatus(); if let content = updateStatusController?.content { usageSettings?.mountData(content) }
        applyAppAppearance(); updateRefreshStatus(); usageSettings?.update(usagePreferences, monitor: taskMonitorState); usageSettings?.updatePermission()
        usageSettings?.select(page); NSApp.activate(ignoringOtherApps: true)
    }
    func settingsAction(_ action: String, payload: Object) {
        switch action {
        case "page": usagePreferences.set(payload["page"], forKey: "settingsPage")
        case "closed":
            DispatchQueue.main.async { [weak self] in
                guard let self = self, self.usageSettings?.window?.isVisible == false else { return }
                self.usageSettings = nil; self.updateStatusController = nil
            }
        case "setting":
            guard let key = payload["key"] as? String, let value = payload["value"] else { return }
            if key.hasPrefix("appearance") {
                let choices = key == "appearanceGlobal" ? ["system", "light", "dark"] : ["inherit", "light", "dark"]
                guard ["appearanceGlobal", "appearanceMain", "appearanceFloating", "appearancePopover"].contains(key), choices.contains(value as? String ?? "") else { return }
                usagePreferences.set(value, forKey: key); applyAppAppearance(); windowProcesses.publishState(["appearanceChanged": true])
            } else if key == "capsuleAutoHide" {
                let wanted = value as? Bool == true
                if floatDocking != nil && capsuleState.autoHide != wanted { floatDocking?.toggleAutoHide() }
                else { usagePreferences.set(wanted, forKey: key); capsuleState.autoHide = wanted }
            } else if key == "floatingEdgeMetric" {
                guard ["remaining", "used"].contains(value as? String ?? "") else { return }
                usagePreferences.set(value, forKey: key); capsuleState.edgeShowsUsed = value as? String == "used"
            } else if key == "floatingPinned" {
                capsuleState.pinned = value as? Bool == true; usagePreferences.set(capsuleState.pinned, forKey: key); floating?.level = capsuleState.pinned ? .floating : .normal
            } else {
                let stored: Any = key == "retentionDays" ? Int(value as? String ?? "") ?? 30 : value
                taskMonitor?.command("settings", payload: ["patch": [key: stored]]) { [weak self] reply in self?.usageSettings?.showResult(reply); if key == "delivery" && stored as? String == "system" { self?.notificationRouter?.requestTaskAuthorization() } }; return
            }
            usageSettings?.update(usagePreferences, monitor: taskMonitorState); usageSettings?.showResult(["ok": true, "message": "已更新"])
        case "toggleFloating": toggleFloating()
        case "onlyFloating": onlyFloating()
        case "onlyStatusBar": onlyStatusBar()
        case "monitor": openTaskMonitor()
        case "budgets": openBudget(nil)
        case "refreshTasks": taskMonitor?.refresh()
        case "pauseTasks", "resumeTasks": taskMonitor?.command("settings", payload: ["patch": ["pausedUntil": action == "pauseTasks" ? Date().timeIntervalSince1970 + 1800 : 0]]) { [weak self] in self?.usageSettings?.showResult($0) }
        case "systemNotifications": if let url = URL(string: "x-apple.systempreferences:com.apple.preference.notifications") { NSWorkspace.shared.open(url) }
        default: break
        }
    }
    func applyAppAppearance() {
        if usagePreferences.object(forKey: "appearanceFloating") == nil {
            let legacy = usagePreferences.string(forKey: "capsuleTheme")
            usagePreferences.set(legacy.map { CapsuleTheme(storedValue: $0).rawValue } ?? "inherit", forKey: "appearanceFloating")
        }
        func appearance(_ value: String?) -> NSAppearance? {
            value == "dark" ? NSAppearance(named: .darkAqua) : value == "light" ? NSAppearance(named: .aqua) : nil
        }
        let global = appearance(usagePreferences.string(forKey: "appearanceGlobal"))
        if NSApp.appearance?.name != global?.name { NSApp.appearance = global }
        window?.appearance = appearance(usagePreferences.string(forKey: "appearanceMain"))
        let floating = usagePreferences.string(forKey: "appearanceFloating") ?? "inherit"
        let resolved = appearance(floating) ?? NSApp.effectiveAppearance
        let theme: CapsuleTheme = resolved.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? .dark : .light
        if capsuleState.theme != theme { capsuleState.theme = theme }
        usageSettings?.window?.appearance = global; updateStatusDetail()
    }
}
