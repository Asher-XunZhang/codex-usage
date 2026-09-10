import AppKit

enum RefreshIntervalPreference {
    static let lastPositiveKey = "lastPositiveRefreshSeconds"
    static let defaultSeconds = 5

    static func validatedValue(input: String) -> Int? {
        guard let value = Int(input.trimmingCharacters(in: .whitespacesAndNewlines)),
              (1...3600).contains(value) else { return nil }
        return value
    }

    static func remembered(preferences: UserDefaults, current: Int) -> Int {
        if (1...3600).contains(current) { return current }
        if let saved = preferences.object(forKey: lastPositiveKey) as? Int,
           (1...3600).contains(saved) { return saved }
        return defaultSeconds
    }
}

private final class RefreshIntervalPanel: NSPanel {
    var applyAction: (() -> Void)?
    var cancelAction: (() -> Void)?
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    override func cancelOperation(_ sender: Any?) { cancelAction?() }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection([.command, .control, .option])
        guard modifiers.isEmpty else { return super.performKeyEquivalent(with: event) }
        if event.keyCode == 53 { cancelAction?(); return true }
        if event.keyCode == 36 || event.keyCode == 76 {
            // Let an input method finish composing before treating Return as Apply.
            if let editor = firstResponder as? NSTextView, editor.hasMarkedText() {
                return super.performKeyEquivalent(with: event)
            }
            applyAction?(); return true
        }
        return super.performKeyEquivalent(with: event)
    }
}

/// A transient native editor. It owns no settings, statistics, service or timer.
/// The only injection replaces window presentation so tests need no visible UI.
final class RefreshIntervalPrompt: NSObject, NSWindowDelegate, NSTextFieldDelegate {
    var completion: ((Int?) -> Void)?
    private(set) var window: NSPanel?
    private(set) var field: NSTextField?
    private(set) var errorLabel: NSTextField?
    var isPresented: Bool { window != nil }

    private var applyButton: NSButton?
    private var cancelButton: NSButton?
    private let presentWindow: (NSPanel) -> Void

    init(presentWindow: @escaping (NSPanel) -> Void = { panel in
        panel.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }) {
        self.presentWindow = presentWindow
        super.init()
    }

    func show(initialSeconds: Int, near anchor: NSWindow? = nil) {
        guard window == nil else { focus(); return }
        let panel = RefreshIntervalPanel(contentRect: NSRect(x: 0, y: 0, width: 320, height: 154),
            styleMask: [.titled, .closable], backing: .buffered, defer: false)
        panel.title = "自动刷新间隔"
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.isFloatingPanel = true
        panel.level = anchor?.level ?? .normal
        panel.delegate = self
        panel.appearance = anchor?.contentView?.effectiveAppearance
        let content = NSView(frame: NSRect(x: 0, y: 0, width: 320, height: 154))
        let hint = NSTextField(labelWithString: "输入 1–3600 秒")
        hint.frame = NSRect(x: 20, y: 119, width: 280, height: 18)
        hint.font = .systemFont(ofSize: 12)
        content.addSubview(hint)

        let input = NSTextField(frame: NSRect(x: 20, y: 84, width: 234, height: 25))
        input.bezelStyle = .roundedBezel
        input.stringValue = String((1...3600).contains(initialSeconds) ? initialSeconds : RefreshIntervalPreference.defaultSeconds)
        input.placeholderString = "秒数"
        input.setAccessibilityLabel("自定义刷新秒数")
        input.delegate = self; input.target = self; input.action = #selector(apply(_:))
        content.addSubview(input)
        let unit = NSTextField(labelWithString: "秒")
        unit.frame = NSRect(x: 265, y: 87, width: 30, height: 18)
        content.addSubview(unit)

        let error = NSTextField(labelWithString: "请输入 1 到 3600 之间的整数秒数。")
        error.frame = NSRect(x: 20, y: 54, width: 280, height: 18)
        error.font = .systemFont(ofSize: 11)
        error.textColor = .systemRed; error.isHidden = true
        content.addSubview(error)
        let cancel = NSButton(title: "取消", target: self, action: #selector(cancelClicked(_:)))
        cancel.frame = NSRect(x: 142, y: 14, width: 76, height: 30)
        cancel.bezelStyle = .rounded; cancel.keyEquivalent = "\u{1b}"
        content.addSubview(cancel)
        let apply = NSButton(title: "应用", target: self, action: #selector(apply(_:)))
        apply.frame = NSRect(x: 224, y: 14, width: 76, height: 30)
        apply.bezelStyle = .rounded; apply.keyEquivalent = "\r"
        content.addSubview(apply)
        panel.defaultButtonCell = apply.cell as? NSButtonCell
        panel.contentView = content
        panel.initialFirstResponder = input
        window = panel; field = input; errorLabel = error
        applyButton = apply; cancelButton = cancel
        panel.applyAction = { [weak self, weak panel] in
            guard let self = self, self.window === panel else { return }; self.apply(nil)
        }
        panel.cancelAction = { [weak self, weak panel] in
            guard let self = self, self.window === panel else { return }; self.cancel()
        }
        if let screen = anchor?.screen ?? NSScreen.main {
            let bounds = screen.visibleFrame
            let center = anchor.map { NSPoint(x: $0.frame.midX, y: $0.frame.midY) }
                ?? NSPoint(x: bounds.midX, y: bounds.midY)
            let size = panel.frame.size
            panel.setFrameOrigin(NSPoint(x: min(max(center.x - size.width / 2, bounds.minX), bounds.maxX - size.width),
                                         y: min(max(center.y - size.height / 2, bounds.minY), bounds.maxY - size.height)))
        }
        focus()
    }

    func focus() {
        guard let panel = window else { return }
        presentWindow(panel)
        // Do not start an input session if presentation was refused (or a
        // headless test deliberately kept the panel hidden).
        if panel.isVisible, let field = field { panel.makeFirstResponder(field) }
    }

    func cancel() { finish(nil) }

    @objc private func apply(_ sender: Any?) {
        guard let field = field, window != nil else { return }
        if let control = sender as? NSControl, control !== field && control !== applyButton { return }
        let input = field.currentEditor()?.string ?? field.stringValue
        guard let seconds = RefreshIntervalPreference.validatedValue(input: input) else {
            field.stringValue = input
            errorLabel?.isHidden = false
            if window?.isVisible == true { field.selectText(nil) }
            return
        }
        finish(seconds)
    }

    @objc private func cancelClicked(_ sender: NSButton) {
        guard sender === cancelButton else { return }; cancel()
    }

    func controlTextDidChange(_ notification: Notification) {
        guard notification.object as? NSTextField === field else { return }
        errorLabel?.isHidden = true
    }

    func windowWillClose(_ notification: Notification) {
        guard notification.object as? NSWindow === window else { return }
        finish(nil, closeWindow: false)
    }

    private func finish(_ value: Int?, closeWindow: Bool = true) {
        guard let panel = window else { return }
        let callback = completion
        completion = nil; window = nil
        // End the shared field-editor session before detaching its edit client.
        panel.endEditing(for: nil)
        _ = field?.abortEditing()
        field?.delegate = nil; field?.target = nil
        applyButton?.target = nil; cancelButton?.target = nil
        field = nil; errorLabel = nil; applyButton = nil; cancelButton = nil
        if let panel = panel as? RefreshIntervalPanel { panel.applyAction = nil; panel.cancelAction = nil }
        panel.delegate = nil; panel.initialFirstResponder = nil; panel.defaultButtonCell = nil
        panel.makeFirstResponder(nil)
        panel.contentView = nil; panel.orderOut(nil)
        if closeWindow { panel.close() }
        callback?(value)
    }
}
