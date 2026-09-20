import AppKit

private final class UpdateStatusBackground: NSView {
    override func draw(_ dirtyRect: NSRect) {
        // Non-clipping AppKit views can receive damage outside their bounds.
        // Filling that damage would erase the enclosing settings tab strip.
        NSColor.windowBackgroundColor.setFill(); bounds.fill()
    }
}

/// A native, event-updated window. Viewing status does not initiate a scan.
final class UpdateStatusController: NSWindowController {
    let local = NSTextField(wrappingLabelWithString: "本地日志尚未读取")
    let account = NSTextField(wrappingLabelWithString: "账号额度尚未读取")
    let saving = NSTextField(wrappingLabelWithString: "")
    let quotaEnabled = NSButton(checkboxWithTitle: "读取账号额度", target: nil, action: nil)
    let interval = NSPopUpButton()
    let quotaRetry = NSButton(title: "重试账号额度", target: nil, action: nil)
    var action: ((String, Int) -> Void)?
    let content: NSView
    init(embedded: Bool = false) {
        let window: NSWindow? = embedded ? nil : NSWindow(contentRect: NSRect(x: 0, y: 0, width: 520, height: 340), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window?.title = "数据与更新"; window?.isReleasedWhenClosed = false
        self.content = UpdateStatusBackground(frame: NSRect(x: 0, y: 0, width: 520, height: 340))
        window?.contentView = self.content
        super.init(window: window)
        func button(_ title: String, _ id: String) -> NSButton {
            let result = NSButton(title: title, target: self, action: #selector(runAction(_:)))
            result.identifier = NSUserInterfaceItemIdentifier(id); result.bezelStyle = .rounded; return result
        }
        interval.addItems(withTitles: ["日志自动更新：关闭", "日志自动更新：5 秒", "日志自动更新：30 秒", "日志自动更新：60 秒"])
        for (item, seconds) in zip(interval.itemArray, [0, 5, 30, 60]) { item.tag = seconds }
        interval.target = self; interval.action = #selector(intervalChanged)
        quotaEnabled.target = self; quotaEnabled.action = #selector(quotaChanged)
        quotaRetry.target = self; quotaRetry.action = #selector(runAction(_:)); quotaRetry.identifier = .init("quota"); quotaRetry.bezelStyle = .rounded
        let localActions = NSStackView(views: [interval, button("重试本地日志", "local")]); localActions.spacing = 12
        let quotaActions = NSStackView(views: [quotaEnabled, quotaRetry]); quotaActions.spacing = 12
        let content = NSStackView(views: [local, localActions, NSBox(), account, quotaActions, saving, button("重试保存设置与草稿", "settings")])
        (content.arrangedSubviews[2] as? NSBox)?.boxType = .separator
        content.orientation = .vertical; content.alignment = .leading; content.spacing = 16
        content.translatesAutoresizingMaskIntoConstraints = false; self.content.addSubview(content)
        NSLayoutConstraint.activate([
            content.leadingAnchor.constraint(equalTo: self.content.leadingAnchor, constant: 22),
            content.trailingAnchor.constraint(equalTo: self.content.trailingAnchor, constant: -22),
            content.topAnchor.constraint(equalTo: self.content.topAnchor, constant: 24),
            content.bottomAnchor.constraint(lessThanOrEqualTo: self.content.bottomAnchor, constant: -20)
        ])
        for view in [local, account, saving] { view.widthAnchor.constraint(equalTo: content.widthAnchor).isActive = true }
        window?.center()
    }
    required init?(coder: NSCoder) { fatalError() }
    @objc private func runAction(_ sender: NSButton) { action?(sender.identifier?.rawValue ?? "", 0) }
    @objc private func intervalChanged() { action?("interval", interval.selectedTag()) }
    @objc private func quotaChanged() { action?("quotaEnabled", quotaEnabled.state == .on ? 1 : 0) }
    func update(local state: [String: Any], quota: QuotaSnapshot, enabled: Bool, busy: Bool, seconds: Int, settingsError: String) {
        let stamp = state["stamp"] as? String ?? "尚无成功快照"
        local.stringValue = "本地日志 · " + (state["busy"] as? Bool == true ? "正在读取…" : stamp)
        if let error = state["error"] as? String, !error.isEmpty { local.stringValue += "\n" + error }
        account.stringValue = "账号额度 · " + (!enabled ? "查询已关闭" : busy ? "正在读取…" : quota.updated.map { "上次成功 " + smallClock($0, includeDay: true) } ?? "尚未读取成功")
        if enabled, let error = quota.error { account.stringValue += "\n" + error }
        quotaEnabled.state = enabled ? .on : .off; quotaRetry.isEnabled = enabled && !busy
        if !interval.itemArray.contains(where: { $0.tag == seconds }) { interval.addItem(withTitle: "日志自动更新：\(seconds) 秒"); interval.lastItem?.tag = seconds }
        interval.selectItem(withTag: seconds)
        saving.stringValue = settingsError
    }
}
