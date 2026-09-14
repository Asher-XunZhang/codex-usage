import AppKit

/// A snapshot-only native popover. Selectable text uses the normal macOS field
/// editor; opening or hovering this view never starts a backend query.
final class StatusDetailController: NSViewController {
    var action: ((String) -> Void)?
    private let heading = NSTextField(labelWithString: "今日用量")
    private let total = NSTextField(labelWithString: "—")
    private let tokens = NSTextField(wrappingLabelWithString: "")
    private let quota = NSTextField(wrappingLabelWithString: "")
    private let reset = NSTextField(wrappingLabelWithString: "")
    private let monitor = FeedbackButton(title: "任务监控", target: nil, action: nil)
    private let freshness = NSTextField(wrappingLabelWithString: "")
    override func loadView() {
        let root = NSView(frame: NSRect(x: 0, y: 0, width: 360, height: 390)); view = root
        heading.font = .systemFont(ofSize: 12, weight: .medium); heading.textColor = .secondaryLabelColor
        total.font = .monospacedDigitSystemFont(ofSize: 28, weight: .semibold)
        tokens.font = .monospacedDigitSystemFont(ofSize: 12, weight: .regular)
        quota.font = .systemFont(ofSize: 13, weight: .medium); reset.font = .systemFont(ofSize: 12)
        reset.textColor = .secondaryLabelColor; freshness.font = .systemFont(ofSize: 11); freshness.textColor = .secondaryLabelColor
        for field in [total, tokens, quota, reset, freshness] { field.isSelectable = true }
        monitor.target = self; monitor.action = #selector(run(_:)); monitor.identifier = .init("monitor"); monitor.bezelStyle = .rounded
        func button(_ text: String, _ id: String) -> NSButton {
            let button = FeedbackButton(title: text, target: self, action: #selector(run(_:))); button.identifier = .init(id); button.bezelStyle = .rounded; return button
        }
        let separator = NSBox(); separator.boxType = .separator
        let controls = NSStackView(views: [button("打开主面板", "main"), button("刷新", "refresh"), button("设置…", "settings")]); controls.spacing = 10
        let stack = NSStackView(views: [heading, total, tokens, separator, quota, reset, monitor, freshness, controls]); stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 12
        stack.translatesAutoresizingMaskIntoConstraints = false; root.addSubview(stack)
        NSLayoutConstraint.activate([stack.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 20), stack.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -20), stack.topAnchor.constraint(equalTo: root.topAnchor, constant: 20), stack.bottomAnchor.constraint(lessThanOrEqualTo: root.bottomAnchor, constant: -16)])
        for item in [tokens, separator, quota, reset, freshness] { item.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true }
    }
    @objc private func run(_ sender: NSControl) { action?(sender.identifier?.rawValue ?? "") }
    func update(summary: [String: Any], quota snapshot: QuotaSnapshot, monitor state: [String: Any], stamp: String?) {
        _ = view
        func number(_ key: String) -> String {
            guard let value = summary[key] as? NSNumber else { return "—" }
            let formatter = NumberFormatter(); formatter.numberStyle = .decimal; formatter.maximumFractionDigits = 0
            return formatter.string(from: value) ?? value.stringValue
        }
        total.stringValue = number("total_tokens") + " Token"
        tokens.stringValue = "输入  " + number("input_tokens") + "\n缓存  " + number("cached_input_tokens") + "\n输出  " + number("output_tokens")
        quota.stringValue = snapshot.detail; reset.stringValue = snapshot.resetLabel
        monitor.title = "任务监控 · \(state["active"] as? Int ?? 0) 项 · \(state["unread"] as? Int ?? 0) 条未读"
        freshness.stringValue = "本地快照 · " + (stamp ?? "尚未读取") + (snapshot.stale ? "\n账号额度为上次成功结果，请留意更新时间。" : "")
    }
}

extension AppDelegate {
    func toggleStatusDetail() {
        if statusPopover?.isShown == true { statusPopover?.performClose(nil); return }
        guard let button = statusItem?.button else { return }
        let controller = StatusDetailController(), popover = NSPopover()
        popover.contentViewController = controller; popover.contentSize = NSSize(width: 360, height: 390)
        popover.behavior = .transient; popover.animates = !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion; popover.delegate = self
        controller.action = { [weak self, weak popover] action in
            guard let self = self else { return }
            if action != "refresh" { popover?.performClose(nil) }
            switch action {
            case "main": self.showDashboard()
            case "monitor": self.openTaskMonitor()
            case "settings": self.showSettings()
            case "refresh": self.manualRefresh()
            default: break
            }
        }
        statusPopover = popover; updateStatusDetail()
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
    }
    func updateStatusDetail() {
        guard let controller = statusPopover?.contentViewController as? StatusDetailController else { return }
        let value = usagePreferences.string(forKey: "appearancePopover")
        controller.view.appearance = value == "dark" ? NSAppearance(named: .darkAqua) : value == "light" ? NSAppearance(named: .aqua) : nil
        controller.update(summary: todaySnapshot["summary"] as? Object ?? [:], quota: quotaReader.snapshot,
            monitor: taskMonitorState["summary"] as? Object ?? [:], stamp: localUpdate["stamp"] as? String ?? compactStamp)
    }
    func popoverDidClose(_ notification: Notification) {
        if notification.object as? NSPopover === statusPopover { statusPopover = nil }
    }
}
