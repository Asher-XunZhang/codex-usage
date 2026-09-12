import AppKit

enum CapsuleTheme: String, CaseIterable {
    case dark, light
    var title: String { self == .dark ? "深色" : "浅色" }
    init(storedValue: String?) {
        self = storedValue == "glass" ? .light : (Self(rawValue: storedValue ?? "") ?? .dark)
    }
}

/// Approved dark/light quota colors, interpolated across the remaining fraction.
/// Fixed stops are shared; drawing uses the existing bounded level animation.
enum CapsuleQuotaColors {
    private struct Stop {
        let fraction: CGFloat
        let red, green, blue: CGFloat
        init(_ fraction: CGFloat, _ hex: UInt32) {
            self.fraction = fraction
            red = CGFloat((hex >> 16) & 255) / 255
            green = CGFloat((hex >> 8) & 255) / 255
            blue = CGFloat(hex & 255) / 255
        }
    }
    private static let dark = [Stop(0.05, 0xEA6565), Stop(0.10, 0xEE786E),
        Stop(0.25, 0xF09858), Stop(0.50, 0xE9BC60), Stop(0.75, 0xA5D76D), Stop(1, 0x35DE94)]
    private static let light = [Stop(0.05, 0xD4474F), Stop(0.10, 0xD76053),
        Stop(0.25, 0xC97432), Stop(0.50, 0xAF811B), Stop(0.75, 0x708F30), Stop(1, 0x009E68)]

    static func color(for fraction: CGFloat, theme: CapsuleTheme) -> NSColor {
        let stops = theme == .light ? light : dark
        let value = fraction.isFinite ? min(1, max(0, fraction)) : 0
        var lower = stops[0]
        for upper in stops.dropFirst() {
            if value <= upper.fraction {
                let t = min(1, max(0, (value - lower.fraction) / (upper.fraction - lower.fraction)))
                return NSColor(srgbRed: lower.red + (upper.red - lower.red) * t,
                    green: lower.green + (upper.green - lower.green) * t,
                    blue: lower.blue + (upper.blue - lower.blue) * t, alpha: 1)
            }
            lower = upper
        }
        return NSColor(srgbRed: lower.red, green: lower.green, blue: lower.blue, alpha: 1)
    }
}

/// Only display values live here: no hidden controls, layout tree or history rows.
final class CapsuleState {
    var changed: (() -> Void)?
    var quotaCompact = "额度 —" { didSet { changed?() } }
    var quotaDetail = "正在读取账号额度…" { didSet { changed?() } }
    var quotaFraction: Double? { didSet { changed?() } }
    var quotaName = "剩余额度" { didSet { changed?() } }
    var quotaStale = false { didSet { changed?() } }
    var theme: CapsuleTheme = .dark { didSet { changed?() } }
    var budgetMode = false { didSet { changed?() } }
    var budgetID = "" { didSet { changed?() } }
    var budgetOptions: [(String, String)] = []
    var budgetName = "选择预算" { didSet { changed?() } }
    var budgetFraction: Double? { didSet { changed?() } }
    var budgetUsed = "—" { didSet { changed?() } }
    var budgetRemaining = "—" { didSet { changed?() } }
    var budgetAmount = "—" { didSet { changed?() } }
    var budgetAmountLabel = "限额" { didSet { changed?() } }
    var budgetStatus = "请选择预算" { didSet { changed?() } }
    var budgetPeriod = "" { didSet { changed?() } }
    var budgetScope = "" { didSet { changed?() } }
    var budgetStale = true { didSet { changed?() } }
    var budgetCaption = "预算已用" { didSet { changed?() } }
    var displayName: String { budgetMode ? budgetName : quotaName }
    var displayTotal: String { budgetMode ? budgetUsed : total }
    var displayScope: String { budgetMode ? budgetCaption : scopeTitle }
    var displayStale: Bool { budgetMode ? budgetStale : quotaStale }
    var normalizedQuota: CGFloat? {
        guard let value = budgetMode ? budgetFraction : quotaFraction, value.isFinite else { return nil }
        return CGFloat(min(1, max(0, value)))
    }
    var total = "—" { didSet { changed?() } }
    var exact = "等待本机统计" { didSet { changed?() } }
    var context = "今天 · 全部模型 / 任务" { didSet { changed?() } }
    var input = "—" { didSet { changed?() } }
    var output = "—" { didSet { changed?() } }
    var cache = "缓存输入 —" { didSet { changed?() } }
    var status = "正在连接…" { didSet { changed?() } }
    var scope = 0 { didSet { changed?() } }
    var rangeDays = "1" { didSet { changed?() } }
    var scopeTitle: String { scope == 0 || rangeDays == "1" ? "今日" : (rangeDays == "all" ? "全部" : "\(rangeDays)天") }
    var pinned = true { didSet { changed?() } }
    var enabled = true { didSet { changed?() } }
    var indicator = "refresh" { didSet { changed?() } }
    var refreshSeconds = 5 { didSet { changed?() } }
    var menuPresented = false
    var pointerPressed = false
    var keepsExpanded = false
    var interactionActive: Bool { pointerPressed || menuPresented }
    var selectedModel = "all" { didSet { changed?() } }
    var selectedTask = "all" { didSet { changed?() } }
    var selectedTaskLabel = "" { didSet { changed?() } }
    var modelTitle: String { selectedModel == "all" ? "全部模型" : selectedModel }
    var taskTitle: String { selectedTask == "all" ? "全部任务" : (selectedTaskLabel.isEmpty ? selectedTask : selectedTaskLabel) }
    var loadChoices: ((String, @escaping ([(String, String)]?, String?) -> Void) -> Void)?
}

private final class CapsuleAction: NSAccessibilityElement {
    var perform: (() -> Void)?
    override func accessibilityPerformPress() -> Bool { perform?(); return perform != nil }
}

/// Exists only while an expanded capsule's native picker menu is tracking.
private final class CapsuleMenuSelection: NSObject {
    var value: String?
    @objc func select(_ sender: NSMenuItem) { value = sender.representedObject as? String }
}

final class CapsuleAnimation: NSAnimation {
    var step: ((CGFloat) -> Void)?
    override var currentProgress: NSAnimation.Progress {
        didSet { step?(CGFloat(currentValue)) }
    }
}

final class CapsuleSurface: NSView {
    static let small = NSSize(width: 76, height: 76)
    static let large = NSSize(width: 336, height: 410)
    var cornerRadius: CGFloat { Self.small.height / 2 + (22 - Self.small.height / 2) * min(1, max(0, expansion)) }
    private var headerHeight: CGFloat { Self.small.height + (52 - Self.small.height) * min(1, max(0, expansion)) }
    let state: CapsuleState
    var hover: ((Bool) -> Void)?
    var action: ((String) -> Void)?
    var interactionChanged: ((Bool) -> Void)?
    /// Tests replace only the blocking menu tracker; item actions remain native.
    var menuTrackingOverride: ((NSMenu, NSPoint) -> Void)?
    var appearanceChanged: (() -> Void)?
    var expansion: CGFloat = 0 {
        didSet {
            if expansion > 0 { finishLiquidAnimation() }
            if expansion <= 0.99 { setButtonFeedback(nil) }
            if expansion == 0, oldValue > 0 {
                // Hidden detail actions need no retained labels/closures. Keep the
                // compact action stable for accessibility focus across transitions.
                accessibleActions = accessibleActions.filter { $0.key == "details" || $0.key == "context" }
            }
            if (oldValue == 0) != (expansion == 0) { window?.hasShadow = expansion > 0 }
            needsDisplay = true
        }
    }
    private var levelAnimation: CapsuleAnimation?
    private var targetFraction: CGFloat?
    private(set) var liquidFraction: CGFloat?
    var isLiquidAnimating: Bool { levelAnimation?.isAnimating == true }
    private var tracking: NSTrackingArea?
    private var accessibleActions: [String: CapsuleAction] = [:]
    private struct Palette {
        let background, primary, secondary, accent, track, border: NSColor
        init(_ background: UInt32, _ primary: UInt32, _ secondary: UInt32, _ accent: UInt32, _ track: UInt32, _ border: UInt32) {
            func color(_ hex: UInt32) -> NSColor {
                NSColor(srgbRed: CGFloat((hex >> 16) & 255) / 255, green: CGFloat((hex >> 8) & 255) / 255, blue: CGFloat(hex & 255) / 255, alpha: 1)
            }
            self.background = color(background); self.primary = color(primary)
            self.secondary = color(secondary); self.accent = color(accent)
            self.track = color(track); self.border = color(border)
        }
    }
    private static let darkPalette = Palette(0x17191B, 0xF5F7F6, 0xADB6B2, 0x57E6B2, 0x343D38, 0x343A37)
    private static let lightPalette = Palette(0xF5F5F2, 0x202823, 0x626C67, 0x047857, 0xDCE3DD, 0xD6DDD7)
    private static let darkButtonGlow = NSColor(srgbRed: 57.0 / 255, green: 233.0 / 255, blue: 183.0 / 255, alpha: 1)
    private static let lightButtonGlow = NSColor(srgbRed: 8.0 / 255, green: 172.0 / 255, blue: 123.0 / 255, alpha: 1)
    private var palette: Palette { state.theme == .light ? Self.lightPalette : Self.darkPalette }
    private var mint: NSColor { palette.accent }
    private var ink: NSColor { palette.primary }
    private var secondary: NSColor { palette.secondary }
    // The two fixed gradients are shared across all redraws and theme switches.
    private static let darkLiquid = CGGradient(colorSpace: CGColorSpaceCreateDeviceRGB(), colorComponents: [0.055,0.38,0.26,1, 0.018,0.20,0.15,1], locations: [0,1], count: 2)!
    private static let lightLiquid = CGGradient(colorSpace: CGColorSpaceCreateDeviceRGB(), colorComponents: [0.82,0.94,0.87,1, 0.74,0.88,0.80,1], locations: [0,1], count: 2)!
    override var isFlipped: Bool { true }

    init(state: CapsuleState) {
        self.state = state
        targetFraction = state.normalizedQuota; liquidFraction = state.normalizedQuota
        super.init(frame: NSRect(origin: .zero, size: Self.small))
        state.changed = { [weak self] in self?.stateChanged() }
        setAccessibilityElement(true)
        setAccessibilityRole(.group)
        setAccessibilityLabel("Token 用量胶囊，悬停展开详情")
    }
    required init?(coder: NSCoder) { fatalError() }
    private func stateChanged() {
        if let name = feedbackButton,
           !isActionEnabled(name) || !regions().contains(where: { $0.0 == name }) {
            setButtonFeedback(nil)
        }
        needsDisplay = true
        appearanceChanged?()
        let next = state.normalizedQuota
        guard next != targetFraction else { return }
        targetFraction = next
        levelAnimation?.stop(); levelAnimation = nil
        guard let from = liquidFraction, let to = next, from != to,
              window?.isVisible == true, expansion == 0,
              !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion else {
            liquidFraction = next; return
        }
        // A single bounded transition on an actual quota change. No idle wave timer.
        let animation = CapsuleAnimation(duration: 0.22, animationCurve: .easeInOut)
        animation.animationBlockingMode = .nonblocking; animation.frameRate = 60
        animation.step = { [weak self] t in
            self?.liquidFraction = from + (to - from) * t; self?.needsDisplay = true
            if t >= 1 { self?.levelAnimation = nil }
        }
        levelAnimation = animation; animation.start()
    }
    private func finishLiquidAnimation() {
        levelAnimation?.stop(); levelAnimation = nil; liquidFraction = targetFraction
    }
    override func viewWillMove(toWindow newWindow: NSWindow?) {
        if newWindow == nil { finishLiquidAnimation(); cancelInteraction() }
        super.viewWillMove(toWindow: newWindow)
    }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        window?.hasShadow = expansion > 0
    }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        // inVisibleRect follows bounds automatically. Replacing the tracking area
        // during each animation frame can discard the pending mouse-exit event.
        guard tracking == nil else { return }
        let area = NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .mouseMoved, .activeAlways, .inVisibleRect], owner: self)
        addTrackingArea(area); tracking = area
    }
    private struct Press {
        let name: String
        let start: NSPoint
        let origin: NSPoint
        let hotspot: NSRect?
        var dragged = false
    }
    private var press: Press?
    // Pointer feedback shares the existing gesture state and drawing surface.
    // Only a changed target/style invalidates pixels; no idle animation or timer.
    private var feedbackButton: String?
    private var feedbackPressed = false
    private var compactHotspot: NSRect?
    private var activeMenu: NSMenu?
    private var menuGeneration = 0
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    func containsScreenPoint(_ point: NSPoint) -> Bool {
        guard let window = window else { return false }
        return containsSurfacePoint(convert(window.convertPoint(fromScreen: point), from: nil))
    }
    private func containsSurfacePoint(_ point: NSPoint) -> Bool {
        NSBezierPath(roundedRect: bounds, xRadius: cornerRadius, yRadius: cornerRadius).contains(point)
    }
    private func screenPoint(_ event: NSEvent) -> NSPoint {
        (event.window ?? window)?.convertPoint(toScreen: event.locationInWindow) ?? NSEvent.mouseLocation
    }
    private func inHotspot(_ point: NSPoint) -> Bool {
        guard let frame = compactHotspot else { return false }
        return NSBezierPath(ovalIn: frame).contains(point)
    }
    private func updateHover(_ event: NSEvent) {
        guard !state.interactionActive else { return }
        let point = screenPoint(event), inside = containsScreenPoint(point)
        if expansion == 0, inside { compactHotspot = window?.convertToScreen(convert(bounds, to: nil)) }
        else if !inHotspot(point) { compactHotspot = nil }
        setButtonFeedback(feedbackAction(point))
        hover?(inside)
    }
    override func mouseEntered(with event: NSEvent) { updateHover(event) }
    override func mouseMoved(with event: NSEvent) { updateHover(event) }
    override func mouseExited(with event: NSEvent) {
        setButtonFeedback(nil)
        guard !state.interactionActive else { return }
        if !inHotspot(screenPoint(event)) { compactHotspot = nil }
        hover?(false)
    }
    private func hitAction(_ point: NSPoint) -> String? {
        guard containsScreenPoint(point), let window = window else { return nil }
        // Preserve the original circular target while hover expands beneath the
        // stationary pointer, including when screen-edge clamping moves the view.
        if inHotspot(point) { return "details" }
        let local = convert(window.convertPoint(fromScreen: point), from: nil)
        return regions().first { $0.0 != "context" && $0.0 != "budgetScope" && $0.2.contains(local) }?.0
    }
    private func isActionEnabled(_ name: String) -> Bool {
        name != "budgetScope" && (name != "refresh" || state.enabled)
    }
    private func feedbackAction(_ point: NSPoint) -> String? {
        guard expansion > 0.99, !state.menuPresented,
              let name = hitAction(point), name != "details", isActionEnabled(name) else { return nil }
        return name
    }
    private func setButtonFeedback(_ name: String?, pressed: Bool = false) {
        let pressed = name != nil && pressed
        guard feedbackButton != name || feedbackPressed != pressed else { return }
        let previous = feedbackButton
        feedbackButton = name; feedbackPressed = pressed
        for (key, _, rect) in regions() where key == previous || key == name {
            setNeedsDisplay(buttonGlowBounds(buttonOutline(key, rect)))
        }
    }
    override func mouseDown(with event: NSEvent) {
        if event.modifierFlags.contains(.control) { rightMouseDown(with: event); return }
        guard !state.interactionActive, let window = window,
              let name = hitAction(screenPoint(event)), name != "refresh" || state.enabled else { return }
        // A double click must not dispatch a second primary action.
        guard name != "details" || event.clickCount < 2 else { return }
        press = Press(name: name, start: screenPoint(event), origin: window.frame.origin, hotspot: compactHotspot)
        setButtonFeedback(feedbackAction(screenPoint(event)), pressed: true)
        state.pointerPressed = true; interactionChanged?(true)
    }
    override func mouseDragged(with event: NSEvent) {
        guard var value = press else { return }
        let point = screenPoint(event), dx = point.x - value.start.x, dy = point.y - value.start.y
        if hypot(dx, dy) >= 3 { value.dragged = true }
        press = value
        let target = !value.dragged && feedbackAction(point) == value.name ? value.name : nil
        setButtonFeedback(target, pressed: true)
        if value.dragged, value.name == "details" {
            window?.setFrameOrigin(NSPoint(x: value.origin.x + dx, y: value.origin.y + dy))
            compactHotspot = value.hotspot?.offsetBy(dx: dx, dy: dy)
        }
    }
    override func mouseUp(with event: NSEvent) {
        guard let value = press else { return }
        let point = screenPoint(event)
        let moved = value.dragged || hypot(point.x - value.start.x, point.y - value.start.y) >= 3
        let selected = !moved && hitAction(point) == value.name && isActionEnabled(value.name) ? value.name : nil
        setButtonFeedback(!moved ? feedbackAction(point) : nil)
        press = nil; state.pointerPressed = false; interactionChanged?(false)
        if let name = selected { performAction(name == "details" ? "main" : name) }
    }
    override func rightMouseDown(with event: NSEvent) {
        guard !state.interactionActive, containsScreenPoint(screenPoint(event)) else { return }
        presentContextMenu(at: screenPoint(event))
    }
    func cancelInteraction() {
        setButtonFeedback(nil)
        menuGeneration += 1
        activeMenu?.cancelTracking(); activeMenu = nil; press = nil; compactHotspot = nil
        let active = state.interactionActive
        state.pointerPressed = false; state.menuPresented = false
        if active { interactionChanged?(false) }
    }
    private func performAction(_ name: String) {
        guard isActionEnabled(name) else { return }
        if name == "content" {
            presentMenu([("usage", "用量统计"), ("budget", "预算提醒")], selected: state.budgetMode ? "budget" : "usage", trigger: NSRect(x: 16, y: 78, width: 70, height: 24), action: "content")
        }
        else if name == "budget" {
            presentMenu(state.budgetOptions + [("", ""), ("manage", "管理预算…")], selected: state.budgetID, trigger: NSRect(x: 94, y: 78, width: 134, height: 24), action: "budget")
        }
        else if name == "interval" { presentIntervalMenu() }
        else if name == "period" { presentPeriodMenu() }
        else if name == "model" || name == "task" { presentChoiceMenu(name) }
        else if name == "context" { presentContextMenu(at: window?.frame.origin ?? NSEvent.mouseLocation) }
        else { action?(name) }
    }
    private func intervalOptions() -> [(String, String)] {
        var values = [0, 1, 2, 5, 10, 30, 60]
        if (0...3600).contains(state.refreshSeconds), !values.contains(state.refreshSeconds) { values.append(state.refreshSeconds); values.sort() }
        return values.map { (String($0), $0 == 0 ? "关闭" : "每 \($0) 秒") } + [("", ""), ("custom", "自定义…")]
    }
    private func presentIntervalMenu() {
        presentMenu(intervalOptions(), selected: String(state.refreshSeconds), trigger: NSRect(x: bounds.width - 162, y: 332, width: 146, height: 24), action: "interval")
    }
    private func presentPeriodMenu() {
        presentMenu([("1", "今天"), ("7", "最近 7 天"), ("30", "最近 30 天"), ("90", "最近 90 天"), ("all", "全部时间")],
                    selected: state.scope == 0 ? "1" : state.rangeDays,
                    trigger: NSRect(x: 94, y: 78, width: 134, height: 24), action: "period")
    }
    private func beginMenu() -> Int? {
        guard !state.interactionActive, window != nil else { return nil }
        setButtonFeedback(nil)
        menuGeneration += 1; state.menuPresented = true; interactionChanged?(true)
        return menuGeneration
    }
    private func finishMenu(_ generation: Int) -> Bool {
        guard generation == menuGeneration else { return false }
        setButtonFeedback(nil)
        activeMenu = nil; state.menuPresented = false
        if let changed = interactionChanged { changed(false) } else { hover?(false) }
        return true
    }
    private func presentChoiceMenu(_ kind: String) {
        guard expansion > 0.99, let loader = state.loadChoices, let generation = beginMenu() else { return }
        loader(kind) { [weak self] options, error in
            guard let self = self, self.menuGeneration == generation, self.state.menuPresented else { return }
            guard let options = options else {
                self.state.status = error ?? "选项读取失败，请重试"
                _ = self.finishMenu(generation); return
            }
            let trigger = NSRect(x: 16, y: kind == "model" ? 110 : 142, width: self.bounds.width - 32, height: 24)
            self.trackMenu(options, selected: kind == "model" ? self.state.selectedModel : self.state.selectedTask,
                           trigger: trigger, action: kind, generation: generation)
        }
    }
    private func presentMenu(_ options: [(String, String)], selected: String, trigger: NSRect, action prefix: String) {
        guard expansion > 0.99, let generation = beginMenu() else { return }
        trackMenu(options, selected: selected, trigger: trigger, action: prefix, generation: generation)
    }
    private func trackMenu(_ options: [(String, String)], selected: String, trigger: NSRect, action prefix: String, generation: Int) {
        guard let window = window else { _ = finishMenu(generation); return }
        let frame = window.convertToScreen(convert(trigger, to: nil))
        trackMenu(options, selected: selected, point: NSPoint(x: frame.minX, y: frame.minY - 4), prefix: prefix, generation: generation)
    }
    private func trackMenu(_ options: [(String, String)], selected: String, point: NSPoint, prefix: String, generation: Int) {
        let menu = NSMenu(), selection = CapsuleMenuSelection()
        menu.autoenablesItems = false; activeMenu = menu
        for (value, title) in options {
            if value.isEmpty { menu.addItem(.separator()); continue }
            let row = NSMenuItem(title: title, action: #selector(CapsuleMenuSelection.select(_:)), keyEquivalent: "")
            row.representedObject = value; row.target = selection; row.state = selected == value ? .on : .off
            row.isEnabled = value != "refresh" || state.enabled
            menu.addItem(row)
        }
        if let track = menuTrackingOverride { withExtendedLifetime(selection) { track(menu, point) } }
        else { _ = withExtendedLifetime(selection) { menu.popUp(positioning: nil, at: point, in: nil) } }
        // Tracking ends before a mode switch, query, or settings action. The list
        // and its labels are local and released on return, including cancellation.
        guard finishMenu(generation), let value = selection.value else { return }
        action?(prefix.isEmpty ? value : prefix + ":" + value)
    }
    private func presentContextMenu(at point: NSPoint) {
        guard let generation = beginMenu() else { return }
        let budgetActions: [(String, String)] = state.budgetMode ? [("budgetEdit", "查看 / 编辑预算…"), ("budgetPause", "暂停提醒 30 分钟"), ("", "")] : []
        trackMenu(budgetActions + [("main", "打开主面板"), ("refresh", "立即刷新"), ("", ""),
                   ("details", state.keepsExpanded ? "解除保持展开" : "保持展开"),
                ("pin", state.pinned ? "取消置顶" : "置顶浮窗"),
                   ("themeDark", "深色主题"), ("themeLight", "浅色主题"), ("", ""),
                   ("menu", "仅状态栏"), ("close", "隐藏浮窗"), ("quit", "退出 Codex 用量")],
                  selected: state.theme == .dark ? "themeDark" : "themeLight", point: point, prefix: "", generation: generation)
    }
    private func regions() -> [(String, String, NSRect)] {
        var result = [("details", state.keepsExpanded ? "解除保持展开" : "展开并保持胶囊详情", NSRect(x: 0, y: 0, width: bounds.width, height: headerHeight))]
        if expansion > 0.99 {
            result += [
                ("refresh", state.enabled ? "刷新胶囊" : "正在刷新", NSRect(x: 16, y: 53, width: 26, height: 24)),
                ("content", "浮窗内容，" + (state.budgetMode ? "预算提醒" : "用量统计"), NSRect(x: 16, y: 78, width: 70, height: 24)),
                (state.budgetMode ? "budget" : "period", state.budgetMode ? "选择预算，" + state.budgetName : "浮窗统计范围，" + state.scopeTitle, NSRect(x: 94, y: 78, width: 134, height: 24)),
                (state.budgetMode ? "budgetEdit" : "model", state.budgetMode ? "编辑预算范围" : "浮窗模型，" + state.modelTitle, NSRect(x: 16, y: 110, width: bounds.width - 32, height: 24)),
                (state.budgetMode ? "budgetScope" : "task", state.budgetMode ? "预算范围，只读" : "浮窗任务，" + state.taskTitle, NSRect(x: 16, y: 142, width: bounds.width - 32, height: 24)),
                ("pin", state.pinned ? "取消置顶" : "置顶胶囊", NSRect(x: bounds.width - 60, y: 78, width: 44, height: 24)),
                ("themeDark", "深色主题" + (state.theme == .dark ? "，已选中" : ""), NSRect(x: bounds.width - 162, y: 300, width: 72, height: 24)),
                ("themeLight", "浅色主题" + (state.theme == .light ? "，已选中" : ""), NSRect(x: bounds.width - 90, y: 300, width: 74, height: 24)),
                ("interval", "自动刷新间隔，" + (state.refreshSeconds == 0 ? "关闭" : "每 \(state.refreshSeconds) 秒"), NSRect(x: bounds.width - 162, y: 332, width: 146, height: 24)),
                ("main", "打开主面板", NSRect(x: 16, y: bounds.height - 37, width: 86, height: 25)),
                ("only", "仅保留胶囊", NSRect(x: 110, y: bounds.height - 37, width: 88, height: 25)),
                ("close", "关闭胶囊", NSRect(x: bounds.width - 70, y: bounds.height - 37, width: 54, height: 25))]
        }
        result.append(("context", "浮窗功能菜单", NSRect(x: 0, y: 0, width: bounds.width, height: headerHeight)))
        return result
    }
    override func accessibilityValue() -> Any? {
        if state.budgetMode { return "预算 \(state.budgetName)，剩余 \(state.budgetRemaining)，已用 \(state.budgetUsed)，\(state.budgetStatus)，\(state.budgetScope)" }
        return expansion > 0.99 ? "\(state.exact)，\(state.context)，输入 \(state.input)，输出 \(state.output)，\(state.cache)，\(state.status)，\(state.quotaDetail)" : "\(state.scopeTitle)总 Token \(state.total)，\(state.quotaCompact)"
    }
    override func accessibilityChildren() -> [Any]? {
        guard let window = window else { return [] }
        return regions().map { name, label, frame in
            let element = accessibleActions[name] ?? CapsuleAction()
            accessibleActions[name] = element
            element.setAccessibilityRole(.button)
            element.setAccessibilityLabel(label)
            element.setAccessibilityParent(self)
            element.setAccessibilityFrame(window.convertToScreen(convert(frame, to: nil)))
            element.setAccessibilityEnabled(name != "budgetScope" && (name != "refresh" || state.enabled))
            element.perform = { [weak self] in
                guard let self = self, name != "budgetScope", name != "refresh" || self.state.enabled else { return }
                if ["content", "budget", "period", "model", "task", "interval", "context"].contains(name) {
                    DispatchQueue.main.async { [weak self] in self?.performAction(name) }
                } else { self.performAction(name) }
            }
            return element
        }
    }
    private func font(size: CGFloat, weight: NSFont.Weight, mono: Bool, rounded: Bool) -> NSFont {
        let base = mono ? NSFont.monospacedDigitSystemFont(ofSize: size, weight: weight) : NSFont.systemFont(ofSize: size, weight: weight)
        guard rounded, let descriptor = base.fontDescriptor.withDesign(.rounded) else { return base }
        return NSFont(descriptor: descriptor, size: size) ?? base
    }
    private func text(_ value: String, _ rect: NSRect, size: CGFloat, color: NSColor, weight: NSFont.Weight = .regular, mono: Bool = false, rounded: Bool = false, truncation: NSLineBreakMode = .byTruncatingTail, alignment: NSTextAlignment = .left) {
        let paragraph = NSMutableParagraphStyle(); paragraph.lineBreakMode = truncation
        paragraph.alignment = alignment
        (value as NSString).draw(in: rect, withAttributes: [
            .font: font(size: size, weight: weight, mono: mono, rounded: rounded),
            .foregroundColor: color, .paragraphStyle: paragraph])
    }
    private func fill(_ rect: NSRect, radius: CGFloat, color: NSColor) {
        color.setFill(); NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
    }
    private func buttonGlowBlur(_ rect: NSRect) -> CGFloat { 25 * min(1, rect.height / 40) }
    private func isThemeButton(_ name: String) -> Bool { name == "themeDark" || name == "themeLight" }
    private func buttonOutline(_ name: String, _ rect: NSRect) -> NSRect {
        // Theme choices share one segmented-control outline while retaining
        // separate pointer/accessibility actions and the selected inner segment.
        isThemeButton(name) ? NSRect(x: bounds.width - 162, y: 300, width: 146, height: 24) : rect
    }
    private func buttonGlowBounds(_ rect: NSRect) -> NSRect {
        // Always invalidate the wider hover footprint, including when press
        // tightens the glow. The same bound clips drawing, so exit leaves no rim.
        let padding = ceil(buttonGlowBlur(rect) * 2) + 2
        return rect.insetBy(dx: -padding, dy: -padding).intersection(bounds)
    }
    private func drawButtonOuterGlow() {
        guard expansion > 0.99, let name = feedbackButton, isActionEnabled(name), !state.menuPresented,
              let context = NSGraphicsContext.current?.cgContext else { return }
        let buttons = regions()
        guard let button = buttons.first(where: { $0.0 == name })?.2 else { return }
        let rect = buttonOutline(name, button)
        let light = state.theme == .light
        let color = light ? Self.lightButtonGlow : Self.darkButtonGlow
        let strength: CGFloat = light ? 0.59 : 0.79
        let blur = buttonGlowBlur(rect) * (feedbackPressed ? 0.70 : 1)
        let source = NSBezierPath(roundedRect: rect, xRadius: 8, yRadius: 8)
        let glowBounds = buttonGlowBounds(rect)
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(rect: glowBounds).addClip()
        let exterior = NSBezierPath(rect: bounds)
        exterior.windingRule = .evenOdd
        exterior.append(source)
        if let header = buttons.first(where: { $0.0 == "details" })?.2 { exterior.appendRect(header) }
        exterior.addClip()
        // Composite only the glow into a short-lived drawing layer. Neighbors
        // must not cut rectangular holes in it or cover it with their own fill.
        context.beginTransparencyLayer(in: glowBounds, auxiliaryInfo: nil)
        for (radius, alpha) in [(blur, strength), (blur * 0.30, strength * 0.65)] {
            NSGraphicsContext.saveGraphicsState()
            let shadow = NSShadow()
            shadow.shadowColor = color.withAlphaComponent(alpha)
            shadow.shadowBlurRadius = radius; shadow.shadowOffset = .zero; shadow.set()
            color.setFill(); source.fill()
            NSGraphicsContext.restoreGraphicsState()
        }
        context.saveGState()
        context.setBlendMode(.destinationOut)
        for (key, _, frame) in buttons where key != name && key != "context" && key != "details" && key != "themeLight" {
            if isThemeButton(name) && isThemeButton(key) { continue }
            let neighbor = buttonOutline(key, frame)
            if neighbor.intersects(glowBounds) { fadeButtonGlowInside(neighbor) }
        }
        context.restoreGState()
        context.endTransparencyLayer()
        NSGraphicsContext.restoreGraphicsState()
    }
    private func fadeButtonGlowInside(_ rect: NSRect) {
        // A continuous falloff lets light cross a neighbor's edge, then protects
        // its text and core within six points. Incremental alpha produces the
        // intended smoothstep mask when these nested fills are composited.
        let width = min(CGFloat(6), rect.height / 4)
        let steps = 16
        var previous: CGFloat = 0
        for step in 1...steps {
            let t = CGFloat(step) / CGFloat(steps)
            let coverage = t * t * (3 - 2 * t)
            let alpha = (coverage - previous) / (1 - previous)
            let inset = width * t
            fill(rect.insetBy(dx: inset, dy: inset), radius: max(0, 8 - inset), color: NSColor.black.withAlphaComponent(alpha))
            previous = coverage
        }
    }
    private func buttonBackground(_ name: String, _ rect: NSRect, base: NSColor = .clear) {
        if base.alphaComponent > 0 { fill(rect, radius: 8, color: base) }
        guard expansion > 0.99, feedbackButton == name, feedbackPressed, isActionEnabled(name), !state.menuPresented else { return }
        let light = state.theme == .light
        fill(rect, radius: 8, color: mint.withAlphaComponent(light ? 0.19 : 0.25))
    }
    private func liquidPath(edge: CGFloat, height: CGFloat, bend: CGFloat) -> NSBezierPath {
        let path = NSBezierPath()
        path.move(to: NSPoint(x: 0, y: 0)); path.line(to: NSPoint(x: edge, y: 0))
        path.curve(to: NSPoint(x: edge, y: height * 0.5), controlPoint1: NSPoint(x: edge - bend, y: height * 0.16), controlPoint2: NSPoint(x: edge - bend, y: height * 0.34))
        path.curve(to: NSPoint(x: edge, y: height), controlPoint1: NSPoint(x: edge + bend, y: height * 0.66), controlPoint2: NSPoint(x: edge + bend, y: height * 0.84))
        path.line(to: NSPoint(x: 0, y: height)); path.close(); return path
    }
    private func drawOrb() {
        let center = NSPoint(x: bounds.midX, y: Self.small.height / 2)
        let radius: CGFloat = Self.small.width / 2 - 4.5
        let ring = NSBezierPath(ovalIn: NSRect(x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2))
        palette.track.setStroke()
        ring.lineWidth = 5; ring.stroke()
        if let fraction = liquidFraction, fraction > 0 {
            let remaining = NSBezierPath()
            remaining.appendArc(withCenter: center, radius: radius, startAngle: -90, endAngle: -90 + 360 * fraction, clockwise: false)
            remaining.lineWidth = 5; remaining.lineCapStyle = .round
            CapsuleQuotaColors.color(for: fraction, theme: state.theme).setStroke(); remaining.stroke()
        }
        let name = state.displayName == "剩余额度" ? "额度" : state.displayName.replacingOccurrences(of: "剩余", with: "余")
        text(name, NSRect(x: center.x - 23, y: 11, width: 46, height: 13), size: 8.5, color: secondary, weight: .medium, alignment: .center)
        let digits = state.normalizedQuota.map { "\(Int(($0 * 100 + 1e-9).rounded(.down)))" } ?? "—"
        let unit = (state.normalizedQuota == nil ? "" : "%") + (state.displayStale ? "*" : "")
        let valueFont = font(size: 18, weight: .medium, mono: true, rounded: true)
        let unitFont = font(size: 10, weight: .medium, mono: false, rounded: true)
        let valueWidth = (digits as NSString).size(withAttributes: [.font: valueFont]).width
        let unitWidth = (unit as NSString).size(withAttributes: [.font: unitFont]).width
        let x = center.x - (valueWidth + unitWidth + (unit.isEmpty ? 0 : 1)) / 2
        let baseline: CGFloat = 40
        text(digits, NSRect(x: x, y: baseline - valueFont.ascender, width: valueWidth + 1, height: 27), size: 18, color: ink, weight: .medium, mono: true, rounded: true)
        text(unit, NSRect(x: x + valueWidth + 1, y: baseline - unitFont.ascender, width: unitWidth + 1, height: 17), size: 10, color: ink, weight: .medium, rounded: true)
        let tokenWidth = (state.displayTotal as NSString).size(withAttributes: [.font: font(size: 10, weight: .medium, mono: true, rounded: true)]).width
        let tokenSize = max(8, min(10, 480 / max(1, tokenWidth)))
        text(state.displayTotal, NSRect(x: center.x - 25, y: 43, width: 50, height: 15), size: tokenSize, color: ink, weight: .medium, mono: true, rounded: true, truncation: .byTruncatingMiddle, alignment: .center)
        text(state.displayScope, NSRect(x: center.x - 20, y: 57, width: 40, height: 13), size: 8.5, color: secondary, weight: .medium, alignment: .center)
    }
    private func drawBattery() {
        let light = state.theme == .light
        let height = headerHeight
        let body = NSRect(x: 2.5, y: 2.5, width: bounds.width - 5, height: height - 5)
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(roundedRect: body, xRadius: body.height / 2, yRadius: body.height / 2).addClip()
        if let fraction = liquidFraction, fraction > 0, let context = NSGraphicsContext.current?.cgContext {
            let edge = body.minX + body.width * fraction
            let bend = min(2, body.width * min(fraction, 1 - fraction) * 0.3)
            let path = liquidPath(edge: edge, height: height, bend: bend)
            NSGraphicsContext.saveGraphicsState(); path.addClip()
            context.drawLinearGradient(light ? Self.lightLiquid : Self.darkLiquid,
                start: CGPoint(x: 0, y: 2.5), end: CGPoint(x: 0, y: height - 2.5), options: [])
            NSGraphicsContext.restoreGraphicsState()
            // A quiet meniscus, no detached waves, bubbles or perpetual shine.
            if fraction < 1 {
                let rim = NSBezierPath()
                rim.move(to: NSPoint(x: edge, y: 3))
                rim.curve(to: NSPoint(x: edge, y: height * 0.5), controlPoint1: NSPoint(x: edge - bend, y: height * 0.17), controlPoint2: NSPoint(x: edge - bend, y: height * 0.35))
                rim.curve(to: NSPoint(x: edge, y: height - 3), controlPoint1: NSPoint(x: edge + bend, y: height * 0.65), controlPoint2: NSPoint(x: edge + bend, y: height * 0.83))
                // Static optical edge: depth without an always-running shine.
                mint.withAlphaComponent(0.07).setStroke(); rim.lineWidth = 4; rim.stroke()
                mint.withAlphaComponent(0.15).setStroke(); rim.lineWidth = 2; rim.stroke()
                mint.withAlphaComponent(light ? 0.32 : 0.58).setStroke(); rim.lineWidth = 0.7; rim.stroke()
            }
        }
        NSGraphicsContext.restoreGraphicsState()
        // One optical baseline and two fixed reading groups in the compact strip.
        // Rounded system digits stay stable as values change without a heavy bold face.
        // Keep font instances stable while the container changes size. Requesting
        // a new fractional point size on every frame grows system glyph caches.
        let inset: CGFloat = 20
        let valueSize: CGFloat = 20, tokenSize: CGFloat = 18
        let unitSize: CGFloat = 11, labelSize: CGFloat = 10
        func width(_ value: String, _ size: CGFloat, mono: Bool = false) -> CGFloat {
            (value as NSString).size(withAttributes: [.font: font(size: size, weight: .medium, mono: mono, rounded: true)]).width
        }
        // Align the smaller units/labels to the value's typographic baseline.
        func drawHeader(_ value: String, x: CGFloat, width: CGFloat, size: CGFloat, color: NSColor, mono: Bool = false) {
            let f = font(size: size, weight: .medium, mono: mono, rounded: true)
            let baseline = height / 2 + (font(size: valueSize, weight: .medium, mono: true, rounded: true).capHeight / 2)
            let y = baseline - f.ascender
            text(value, NSRect(x: x, y: y, width: width, height: ceil(f.ascender - f.descender) + 3), size: size, color: color, weight: .medium, mono: mono, rounded: true)
        }
        let percent = state.normalizedQuota.map { "\(Int(($0 * 100 + 1e-9).rounded(.down)))" }
        let quotaDigits = percent ?? "—", digitWidth = width(quotaDigits, valueSize, mono: true)
        drawHeader(quotaDigits, x: inset, width: digitWidth + 1, size: valueSize, color: ink, mono: true)
        let unit = (percent == nil ? "" : "%") + (state.displayStale ? "*" : "")
        let unitWidth = width(unit, unitSize)
        drawHeader(unit, x: inset + digitWidth + 1, width: unitWidth + 1, size: unitSize, color: ink.withAlphaComponent(0.86))
        let quotaLabelX = inset + digitWidth + unitWidth + 7
        let shortName = state.displayName == "剩余额度" ? "额度" : state.displayName.replacingOccurrences(of: "剩余", with: "余")
        let hasUnit = state.displayTotal.last.map { "KMB".contains($0) } ?? false
        let tokenDigits = hasUnit ? String(state.displayTotal.dropLast()) : state.displayTotal
        let tokenUnit = hasUnit ? String(state.displayTotal.suffix(1)) : ""
        let tokenUnitWidth = width(tokenUnit, unitSize)
        let availableDigits = max(30, bounds.width * 0.43 - tokenUnitWidth - 2)
        let tokenWidth = min(width(tokenDigits, tokenSize, mono: true) + 1, availableDigits)
        let tokenX = bounds.width - inset - tokenWidth - tokenUnitWidth - (hasUnit ? 2 : 0)
        let caption = state.indicator == "busy" ? "更新" : state.displayScope
        let captionWidth = width(caption, labelSize) + 2
        let captionX = tokenX - captionWidth - 6
        drawHeader(shortName, x: quotaLabelX, width: max(0, captionX - quotaLabelX - 10), size: labelSize, color: secondary)
        drawHeader(caption, x: captionX, width: captionWidth, size: labelSize, color: secondary)
        drawHeader(tokenDigits, x: tokenX, width: tokenWidth, size: tokenSize, color: ink, mono: true)
        drawHeader(tokenUnit, x: bounds.width - inset - tokenUnitWidth, width: tokenUnitWidth + 1, size: unitSize, color: secondary)
    }
    override func draw(_ dirtyRect: NSRect) {
        let light = state.theme == .light
        let radius = cornerRadius
        let shape = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: radius, yRadius: radius)
        NSGraphicsContext.saveGraphicsState(); shape.addClip()
        palette.background.setFill(); shape.fill()
        let detailOpacity = min(1, max(0, (expansion - 0.5) / 0.5))
        if expansion < 0.6 {
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current?.cgContext.setAlpha(max(0, 1 - expansion / 0.6))
            drawOrb()
            NSGraphicsContext.restoreGraphicsState()
        }
        if detailOpacity > 0 {
            NSGraphicsContext.current?.cgContext.setAlpha(detailOpacity)
            drawBattery()
            palette.border.setStroke(); shape.lineWidth = 1; shape.stroke()
            text(state.budgetMode ? "预算范围独立 · 点击周期可编辑" : state.exact, NSRect(x: 48, y: 57, width: max(0, bounds.width - 132), height: 16), size: 10, color: secondary, mono: true)
            let symbol = state.indicator == "busy" ? "…" : (state.indicator == "check" ? "✓" : "↻")
            buttonBackground("refresh", NSRect(x: 16, y: 53, width: 26, height: 24))
            text(symbol, NSRect(x: 19, y: 52, width: 22, height: 25), size: 18, color: state.enabled ? mint : secondary)
            buttonBackground("content", NSRect(x: 16, y: 78, width: 70, height: 24), base: ink.withAlphaComponent(0.065))
            text(state.budgetMode ? "预算 ⌄" : "用量 ⌄", NSRect(x: 29, y: 83, width: 54, height: 15), size: 10, color: mint, weight: .medium)
            buttonBackground(state.budgetMode ? "budget" : "period", NSRect(x: 94, y: 78, width: 134, height: 24), base: ink.withAlphaComponent(0.065))
            text(state.budgetMode ? state.budgetName : "范围 · " + state.scopeTitle, NSRect(x: 105, y: 83, width: 102, height: 15), size: 10, color: mint, weight: .medium)
            text("⌄", NSRect(x: 210, y: 81, width: 14, height: 17), size: 12, color: secondary)
            buttonBackground("pin", NSRect(x: bounds.width - 60, y: 78, width: 44, height: 24))
            text(state.pinned ? "● 置顶" : "○ 置顶", NSRect(x: bounds.width - 58, y: 83, width: 44, height: 15), size: 10, color: state.pinned ? mint : secondary)
            if state.budgetMode {
                buttonBackground("budgetEdit", NSRect(x: 16, y: 110, width: bounds.width - 32, height: 24))
                text(state.budgetPeriod, NSRect(x: 20, y: 114, width: bounds.width - 40, height: 17), size: 11, color: ink)
                text(state.budgetScope, NSRect(x: 20, y: 146, width: bounds.width - 40, height: 17), size: 10, color: secondary)
                fill(NSRect(x: 16, y: 174, width: bounds.width - 32, height: 76), radius: 12, color: ink.withAlphaComponent(0.05))
                text("预算剩余", NSRect(x: 29, y: 185, width: 160, height: 15), size: 10, color: secondary)
                text(state.budgetRemaining, NSRect(x: 29, y: 207, width: bounds.width - 58, height: 30), size: 22, color: mint, weight: .medium, mono: true)
                text(state.budgetAmountLabel + " " + state.budgetAmount, NSRect(x: 20, y: 260, width: bounds.width - 40, height: 17), size: 10, color: secondary)
                text(state.budgetStatus, NSRect(x: 20, y: 282, width: bounds.width - 40, height: 16), size: 9, color: secondary)
            } else {
            for (y, label, value) in [(CGFloat(110), "模型", state.modelTitle), (CGFloat(142), "任务", state.taskTitle)] {
                let width = max(0, bounds.width - 32)
                buttonBackground(y == 110 ? "model" : "task", NSRect(x: 16, y: y, width: width, height: 24), base: ink.withAlphaComponent(0.065))
                text(label, NSRect(x: 28, y: y + 5, width: 48, height: 15), size: 10, color: secondary)
                text(value, NSRect(x: 92, y: y + 5, width: max(0, width - 112), height: 15), size: 10, color: mint, weight: .medium)
                text("⌄", NSRect(x: bounds.width - 36, y: y + 3, width: 14, height: 17), size: 12, color: secondary)
            }
            fill(NSRect(x: 16, y: 174, width: bounds.width - 32, height: 28), radius: 8, color: ink.withAlphaComponent(0.035))
            text(state.quotaDetail, NSRect(x: 26, y: 182, width: bounds.width - 52, height: 16), size: 10, color: mint, weight: .medium)
            let cardWidth = (bounds.width - 42) / 2
            for (i, item) in [("输入", state.input), ("输出", state.output)].enumerated() {
                let x: CGFloat = 16 + CGFloat(i) * (cardWidth + 10)
                fill(NSRect(x: x, y: 212, width: cardWidth, height: 44), radius: 10, color: ink.withAlphaComponent(0.05))
                text(item.0, NSRect(x: x + 12, y: 218, width: cardWidth - 24, height: 13), size: 9, color: secondary)
                text(item.1, NSRect(x: x + 12, y: 232, width: cardWidth - 24, height: 20), size: 14, color: ink, weight: .medium, mono: true)
            }
            text(state.cache, NSRect(x: 20, y: 264, width: bounds.width - 40, height: 15), size: 10, color: secondary)
            text(state.status, NSRect(x: 20, y: 282, width: bounds.width - 40, height: 15), size: 9, color: secondary)
            }
            text("胶囊主题", NSRect(x: 20, y: 306, width: 110, height: 15), size: 10, color: secondary)
            let themeX = bounds.width - 162
            fill(NSRect(x: themeX, y: 300, width: 146, height: 24), radius: 8, color: ink.withAlphaComponent(0.05))
            fill(NSRect(x: themeX + (light ? 73 : 1), y: 301, width: 72, height: 22), radius: 7, color: ink.withAlphaComponent(0.11))
            buttonBackground("themeDark", NSRect(x: themeX, y: 300, width: 72, height: 24))
            buttonBackground("themeLight", NSRect(x: themeX + 72, y: 300, width: 74, height: 24))
            text("深色", NSRect(x: themeX + 23, y: 305, width: 40, height: 15), size: 10, color: light ? secondary : ink, weight: .medium)
            text("浅色", NSRect(x: themeX + 96, y: 305, width: 40, height: 15), size: 10, color: light ? ink : secondary, weight: .medium)
            text("Token 自动刷新", NSRect(x: 20, y: 338, width: 110, height: 15), size: 10, color: secondary)
            buttonBackground("interval", NSRect(x: themeX, y: 332, width: 146, height: 24), base: ink.withAlphaComponent(0.065))
            let intervalLabel = state.refreshSeconds == 0 ? "关闭" : "每 \(state.refreshSeconds) 秒"
            text(intervalLabel, NSRect(x: themeX + 12, y: 337, width: 113, height: 15), size: 10, color: ink, weight: .medium)
            text("⌄", NSRect(x: themeX + 127, y: 335, width: 14, height: 17), size: 12, color: secondary)
            // Keep footer controls in their final layout while the window reveals
            // the content. Following the animated bottom would cross other rows.
            let buttonY = Self.large.height - 37
            buttonBackground("main", NSRect(x: 16, y: buttonY, width: 86, height: 25), base: mint.withAlphaComponent(0.13))
            buttonBackground("only", NSRect(x: 110, y: buttonY, width: 88, height: 25), base: ink.withAlphaComponent(0.05))
            buttonBackground("close", NSRect(x: bounds.width - 70, y: buttonY, width: 54, height: 25))
            text("打开主面板", NSRect(x: 29, y: buttonY + 6, width: 70, height: 15), size: 10, color: mint, weight: .medium)
            text("仅保留胶囊", NSRect(x: 123, y: buttonY + 6, width: 70, height: 15), size: 10, color: secondary)
            text("关闭", NSRect(x: bounds.width - 51, y: buttonY + 6, width: 32, height: 15), size: 10, color: secondary)
            drawButtonOuterGlow()
        }
        NSGraphicsContext.restoreGraphicsState()
    }
}
