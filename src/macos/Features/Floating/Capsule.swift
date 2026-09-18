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

/// Only the collapsed quota arc uses this preference. Nil colors retain the
/// theme-aware defaults; editing the two endpoints selects a custom gradient.
struct CapsuleArcStyle: Codable, Equatable {
    enum Mode: String, Codable { case solid, gradient }
    var version = 1
    var mode: Mode = .gradient
    var solidHex: String?
    var lowHex: String?
    var highHex: String?
    static let preferenceKey = "capsuleArcStyle"
    var isBuiltinGradient: Bool { lowHex == nil && highHex == nil }
    var isValid: Bool {
        version == 1 && [solidHex, lowHex, highHex].compactMap { $0 }.allSatisfy { Self.parse($0) != nil }
            && ((lowHex == nil) == (highHex == nil))
    }
    static func parse(_ hex: String) -> NSColor? {
        guard hex.count == 7, hex.first == "#", hex.dropFirst().allSatisfy({ $0.isASCII && $0.isHexDigit }),
              let value = UInt32(hex.dropFirst(), radix: 16) else { return nil }
        return NSColor(srgbRed: CGFloat((value >> 16) & 255) / 255, green: CGFloat((value >> 8) & 255) / 255,
                       blue: CGFloat(value & 255) / 255, alpha: 1)
    }
    static func hex(_ color: NSColor) -> String {
        let rgb = color.usingColorSpace(.sRGB) ?? .black
        return String(format: "#%02X%02X%02X", Int((rgb.redComponent * 255).rounded()),
                      Int((rgb.greenComponent * 255).rounded()), Int((rgb.blueComponent * 255).rounded()))
    }
    func endpoint(high: Bool, theme: CapsuleTheme) -> NSColor {
        (high ? highHex : lowHex).flatMap(Self.parse) ?? CapsuleQuotaColors.color(for: high ? 1 : 0, theme: theme)
    }
    func solidColor(theme: CapsuleTheme) -> NSColor {
        solidHex.flatMap(Self.parse) ?? CapsuleQuotaColors.color(for: 1, theme: theme)
    }
    func color(for fraction: CGFloat, theme: CapsuleTheme) -> NSColor {
        guard isValid else { return CapsuleQuotaColors.color(for: fraction, theme: theme) }
        if mode == .solid { return solidColor(theme: theme) }
        guard !isBuiltinGradient else { return CapsuleQuotaColors.color(for: fraction, theme: theme) }
        let low = endpoint(high: false, theme: theme).usingColorSpace(.sRGB)!, high = endpoint(high: true, theme: theme).usingColorSpace(.sRGB)!
        let t = fraction.isFinite ? min(1, max(0, fraction)) : 0
        return NSColor(srgbRed: low.redComponent + (high.redComponent - low.redComponent) * t,
                       green: low.greenComponent + (high.greenComponent - low.greenComponent) * t,
                       blue: low.blueComponent + (high.blueComponent - low.blueComponent) * t, alpha: 1)
    }
    mutating func setColor(_ color: NSColor, high: Bool, theme: CapsuleTheme) {
        if mode == .solid { solidHex = Self.hex(color); return }
        // Materialize both defaults before replacing one endpoint.
        let low = Self.hex(endpoint(high: false, theme: theme)), upper = Self.hex(endpoint(high: true, theme: theme))
        lowHex = high ? low : Self.hex(color); highHex = high ? Self.hex(color) : upper
    }
    mutating func resetColors() {
        if mode == .solid { solidHex = nil } else { lowHex = nil; highHex = nil }
    }
    static func load(_ preferences: UserDefaults) -> (style: Self, error: String?) {
        guard let stored = preferences.object(forKey: preferenceKey) else { return (Self(), nil) }
        guard let data = stored as? Data, let style = try? JSONDecoder().decode(Self.self, from: data), style.isValid else {
            return (Self(), "已保存的配色无法读取，暂用默认颜色；原配置保留，应用后才会替换。")
        }
        return (style, nil)
    }
    func save(_ preferences: UserDefaults) -> String? {
        guard isValid, let data = try? JSONEncoder().encode(self) else { return "配色无效，请重新选择颜色。" }
        let previous = preferences.object(forKey: Self.preferenceKey)
        preferences.set(data, forKey: Self.preferenceKey)
        guard preferences.synchronize(), preferences.data(forKey: Self.preferenceKey) == data else {
            if let previous = previous { preferences.set(previous, forKey: Self.preferenceKey) }
            else { preferences.removeObject(forKey: Self.preferenceKey) }
            return "配色未能保存，修改已保留，请重试应用。"
        }
        return nil
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
    var arcStyle = CapsuleArcStyle() { didSet { changed?() } }
    var arcStylePreview: CapsuleArcStyle? { didSet { changed?() } }
    var monitorRows: [(id: String, title: String, status: String, active: Bool)] = [] { didSet { changed?() } }
    var monitorChecking = false { didSet { changed?() } }
    var canRefresh: Bool { monitorMode ? !monitorChecking : enabled }
    var monitorSummary = "任务监控" { didSet { changed?() } }
    var monitorUnread = 0 { didSet { changed?() } }
    var edgeShowsUsed = false { didSet { changed?() } }
    var monitorMode = false { didSet { changed?() } }
    var monitorStatus = "none" { didSet { changed?() } }
    var monitorTitle = "尚未选择关注任务" { didSet { changed?() } }
    var monitorDetail = "在任务监控页选择正在执行的任务" { didSet { changed?() } }
    var monitorSource = "正在核对任务来源" { didSet { changed?() } }
    var showsDockedMonitor: Bool {
        monitorUnread > 0 || monitorRows.contains(where: { $0.active }) || ["running", "unknown", "idle"].contains(monitorStatus)
    }
    var monitorStatusLabel: String { ["running": "执行中", "completed": "本轮已结束", "interrupted": "本轮已中断", "unknown": "状态待确认", "idle": "等待下一轮"][monitorStatus] ?? "暂无监控" }
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
    var keyboardInteracting = false
    var autoHide = true { didSet { changed?() } }
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

/// Same staged morph as Windows: one reversible progress value owns geometry
/// and visibility. Coordinates remain AppKit screen points (bottom-left origin).
struct CapsuleMorph {
    let shape: NSRect
    let vertical: CGFloat
    let radius: CGFloat
    let arcAlpha: CGFloat
    let detailsAlpha: CGFloat
    static func smooth(_ value: CGFloat) -> CGFloat {
        let p = value.isFinite ? min(1, max(0, value)) : 0
        return p * p * (3 - 2 * p)
    }
    static func frame(compact: NSRect, panel: NSRect, progress: CGFloat) -> CapsuleMorph {
        let p = progress.isFinite ? min(1, max(0, progress)) : 0
        let x = smooth((p - 0.14) / 0.62), y = smooth((p - 0.14) / 0.70)
        func mix(_ a: CGFloat, _ b: CGFloat, _ t: CGFloat) -> CGFloat { a + (b - a) * t }
        let left = mix(compact.minX, panel.minX, x), right = mix(compact.maxX, panel.maxX, x)
        let bottom = mix(compact.minY, panel.minY, y), top = mix(compact.maxY, panel.maxY, y)
        let shape = NSRect(x: left, y: bottom, width: max(0, right - left), height: max(0, top - bottom))
        return CapsuleMorph(shape: shape, vertical: y, radius: min(38 - 16 * y, shape.width / 2, shape.height / 2),
                            arcAlpha: 1 - smooth(p / 0.12), detailsAlpha: smooth((p - 0.86) / 0.14))
    }
    static func safeText(_ preferred: NSRect, inside shape: NSRect, radius: CGFloat) -> NSRect? {
        let interior = shape.insetBy(dx: 2, dy: 2), r = max(0, radius - 2)
        guard preferred.width > 0, preferred.height > 0, interior.width >= preferred.width, interior.height >= preferred.height else { return nil }
        func fits(_ rect: NSRect) -> Bool {
            for point in [NSPoint(x: rect.minX, y: rect.minY), NSPoint(x: rect.maxX, y: rect.minY),
                          NSPoint(x: rect.minX, y: rect.maxY), NSPoint(x: rect.maxX, y: rect.maxY)] {
                guard point.x >= interior.minX, point.x <= interior.maxX, point.y >= interior.minY, point.y <= interior.maxY else { return false }
                let x = min(max(point.x, min(interior.minX + r, interior.midX)), max(interior.maxX - r, interior.midX))
                let y = min(max(point.y, min(interior.minY + r, interior.midY)), max(interior.maxY - r, interior.midY))
                if pow(point.x - x, 2) + pow(point.y - y, 2) > r * r + 1e-9 { return false }
            }
            return true
        }
        if fits(preferred) { return preferred }
        let center = NSRect(x: shape.midX - preferred.width / 2, y: shape.midY - preferred.height / 2, width: preferred.width, height: preferred.height)
        guard fits(center) else { return nil }
        var low: CGFloat = 0, high: CGFloat = 1, best = center
        for _ in 0..<40 {
            let t = (low + high) / 2
            let candidate = center.offsetBy(dx: (preferred.minX - center.minX) * t, dy: (preferred.minY - center.minY) * t)
            if fits(candidate) { low = t; best = candidate } else { high = t }
        }
        return best
    }
}

final class CapsuleAnimation: NSAnimation {
    var step: ((CGFloat) -> Void)?
    override var currentProgress: NSAnimation.Progress {
        didSet { step?(CGFloat(currentValue)) }
    }
}

private final class CapsuleScroller: NSScroller {
    var trackingChanged: ((Bool) -> Void)?
    override func mouseDown(with event: NSEvent) {
        trackingChanged?(true); defer { trackingChanged?(false) }; super.mouseDown(with: event)
    }
}

final class CapsuleCopyField: NSTextField {
    override var needsPanelToBecomeKey: Bool { false }
    var trackingChanged: ((Bool) -> Void)?
    weak var dragSurface: CapsuleSurface?
    override func mouseDown(with event: NSEvent) {
        // A single press belongs to the same window gesture as every other
        // detail region. Double-click explicitly enters native text selection.
        if event.clickCount == 1, !event.modifierFlags.contains(.option) {
            dragSurface?.mouseDown(with: event); return
        }
        trackingChanged?(true); defer { trackingChanged?(false) }
        window?.makeKey(); super.mouseDown(with: event)
    }
    override func mouseDragged(with event: NSEvent) { dragSurface?.mouseDragged(with: event) }
    override func mouseUp(with event: NSEvent) { dragSurface?.mouseUp(with: event) }
}

final class CapsuleSurface: NSView {
    static let small = NSSize(width: 76, height: 76)
    static let large = NSSize(width: 336, height: 410)
    var morphCompactFrame: NSRect?, morphDetailFrame: NSRect?
    // Morph anchors are screen coordinates. An embedded preview occupies only
    // part of its window, so the window frame is not the surface frame.
    private var screenFrame: NSRect { window.map { $0.convertToScreen(convert(bounds, to: nil)) } ?? bounds }
    private let managesWindowShadow: Bool
    private var morph: CapsuleMorph {
        let current = screenFrame
        let compact = expansion == 0 ? current : morphCompactFrame ?? NSRect(x: current.maxX - 76, y: current.maxY - 76, width: 76, height: 76)
        let panel = expansion == 1 ? current : morphDetailFrame ?? current
        return CapsuleMorph.frame(compact: compact, panel: panel, progress: expansion)
    }
    var cornerRadius: CGFloat { min(bounds.width / 2, bounds.height / 2, (38 - 16 * morph.vertical) * (1 - docking) + 12 * docking) }
    var docking: CGFloat = 0 { didSet { needsDisplay = true } }
    var dockEdge: String?
    private var contentOffset = NSPoint.zero
    private var keyboardAction: String?
    private let verticalScroller = CapsuleScroller(), horizontalScroller = CapsuleScroller()
    private var copyField: CapsuleCopyField?
    private var bodyWidth: CGFloat { max(Self.large.width, bounds.width) }
    private var bodyViewport: NSRect {
        let width = max(0, bounds.width - (bounds.height < Self.large.height ? 12 : 0))
        return NSRect(x: 0, y: 52, width: width, height: max(0, bounds.height - 100 - (width < Self.large.width ? 12 : 0)))
    }
    private var headerHeight: CGFloat { Self.small.height + (52 - Self.small.height) * morph.vertical }
    let state: CapsuleState
    var hover: ((Bool) -> Void)?
    var action: ((String) -> Void)?
    var interactionChanged: ((Bool) -> Void)?
    var dragReleased: ((NSPoint) -> Void)?
    var pointerMoved: ((NSPoint) -> Void)?
    /// Tests replace only the blocking menu tracker; item actions remain native.
    var menuTrackingOverride: ((NSMenu, NSPoint) -> Void)?
    var appearanceChanged: (() -> Void)?
    var dockedContentChanged: (() -> Void)?
    var expansion: CGFloat = 0 {
        didSet {
            if expansion > 0 { finishLiquidAnimation() }
            if expansion <= 0.99 { setButtonFeedback(nil) }
            if expansion == 0, oldValue > 0 {
                // Hidden detail actions need no retained labels/closures. Keep the
                // compact action stable for accessibility focus across transitions.
                accessibleActions = accessibleActions.filter { $0.key == "details" || $0.key == "context" }
            }
            if managesWindowShadow, (oldValue == 0) != (expansion == 0) { window?.hasShadow = expansion > 0 }
            updateScrollers(); needsDisplay = true
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

    init(state: CapsuleState, managesWindowShadow: Bool = true) {
        self.state = state
        self.managesWindowShadow = managesWindowShadow
        targetFraction = state.normalizedQuota; liquidFraction = state.normalizedQuota
        super.init(frame: NSRect(origin: .zero, size: Self.small))
        state.changed = { [weak self] in self?.stateChanged() }
        setAccessibilityElement(true)
        setAccessibilityRole(.group)
        setAccessibilityLabel("Token 用量胶囊，悬停展开详情")
    }
    required init?(coder: NSCoder) { fatalError() }
    private func stateChanged() {
        updateCopyField()
        if let name = feedbackButton,
           !isActionEnabled(name) || !regions().contains(where: { $0.0 == name }) {
            setButtonFeedback(nil)
        }
        needsDisplay = true
        appearanceChanged?()
        dockedContentChanged?()
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
        if managesWindowShadow { window?.hasShadow = expansion > 0 }
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
    private var compactHoverPoint: NSPoint?
    private var activeMenu: NSMenu?
    private var menuGeneration = 0
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override var needsPanelToBecomeKey: Bool { false }
    override func becomeFirstResponder() -> Bool { needsDisplay = true; return true }
    override func resignFirstResponder() -> Bool { keyboardAction = nil; needsDisplay = true; return true }
    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            if activeMenu != nil { activeMenu?.cancelTracking() }
            else { cancelInteraction(); keyboardAction = nil; action?("collapse"); window?.resignKey() }
            return
        }
        if [48, 36, 49, 125, 126].contains(event.keyCode) { state.keyboardInteracting = true }
        if event.keyCode == 48 {
            let actions = regions(includeOffscreen: true).filter { $0.0 != "context" && isActionEnabled($0.0) }
            guard !actions.isEmpty else { return }
            let previous = actions.firstIndex { $0.0 == keyboardAction }
            let backwards = event.modifierFlags.contains(.shift)
            let next = previous.map { ($0 + (backwards ? actions.count - 1 : 1)) % actions.count } ?? (backwards ? actions.count - 1 : 0)
            keyboardAction = actions[next].0
            if !["main", "monitor", "collapse", "details"].contains(actions[next].0) {
                let frame = actions[next].2, viewport = bodyViewport
                if frame.minY < viewport.minY { contentOffset.y += frame.minY - viewport.minY }
                else if frame.maxY > viewport.maxY { contentOffset.y += frame.maxY - viewport.maxY }
                if frame.minX < viewport.minX { contentOffset.x += frame.minX - viewport.minX }
                else if frame.maxX > viewport.maxX { contentOffset.x += frame.maxX - viewport.maxX }
                updateScrollers()
            }
            needsDisplay = true; return
        }
        if event.keyCode == 36 || event.keyCode == 49 {
            if let name = keyboardAction { performAction(name == "details" ? "main" : name) }
            else { performAction(expansion == 0 ? "expand" : "main") }
            return
        }
        if event.keyCode == 125 || event.keyCode == 126 {
            contentOffset.y += event.keyCode == 125 ? 36 : -36; updateScrollers(); needsDisplay = true; return
        }
        super.keyDown(with: event)
    }
    override func scrollWheel(with event: NSEvent) {
        guard expansion > 0.99, !state.interactionActive else { return }
        let scale: CGFloat = event.hasPreciseScrollingDeltas ? 1 : 12
        contentOffset.x -= event.scrollingDeltaX * scale; contentOffset.y -= event.scrollingDeltaY * scale
        updateScrollers(); needsDisplay = true
    }
    override func layout() { super.layout(); updateScrollers() }
    private func updateScrollers() {
        let viewport = bodyViewport
        let maxX = max(0, bodyWidth - viewport.width), maxY = max(0, 356 - viewport.maxY)
        contentOffset.x = min(max(0, contentOffset.x), maxX); contentOffset.y = min(max(0, contentOffset.y), maxY)
        for scroller in [verticalScroller, horizontalScroller] {
            let needed = expansion > 0.99 && (scroller === verticalScroller ? maxY > 0 : maxX > 0)
            if !needed { scroller.removeFromSuperview(); continue }
            guard scroller.superview == nil else { continue }
            scroller.controlSize = .small; scroller.scrollerStyle = .overlay; scroller.target = self; scroller.action = #selector(scrolled(_:))
            scroller.trackingChanged = { [weak self] active in self?.state.pointerPressed = active; self?.interactionChanged?(active) }
            addSubview(scroller)
        }
        verticalScroller.frame = NSRect(x: bounds.width - 12, y: 52, width: 12, height: viewport.height)
        horizontalScroller.frame = NSRect(x: 0, y: viewport.maxY, width: viewport.width, height: 12)
        verticalScroller.isHidden = expansion <= 0.99 || maxY == 0
        horizontalScroller.isHidden = expansion <= 0.99 || maxX == 0
        verticalScroller.knobProportion = min(1, viewport.height / 304); horizontalScroller.knobProportion = min(1, viewport.width / bodyWidth)
        verticalScroller.doubleValue = maxY > 0 ? Double(contentOffset.y / maxY) : 0
        horizontalScroller.doubleValue = maxX > 0 ? Double(contentOffset.x / maxX) : 0
        updateCopyField()
    }
    private func updateCopyField() {
        let rect = NSRect(x: 48, y: 55, width: max(0, bodyWidth - 80), height: 20).offsetBy(dx: -contentOffset.x, dy: -contentOffset.y)
        guard expansion > 0.99, bodyViewport.contains(rect) else { copyField?.removeFromSuperview(); copyField = nil; return }
        let field = copyField ?? CapsuleCopyField(frame: rect)
        if copyField == nil {
            field.dragSurface = self
            field.isEditable = false; field.isSelectable = true; field.isBordered = false; field.drawsBackground = false
            field.font = .monospacedDigitSystemFont(ofSize: 10, weight: .regular)
            field.lineBreakMode = .byTruncatingTail; field.setAccessibilityLabel("当前摘要，双击选择并复制，按住移动可拖动浮窗")
            field.trackingChanged = { [weak self] active in
                if active { self?.state.keyboardInteracting = true }
                self?.state.pointerPressed = active; self?.interactionChanged?(active)
            }
            field.nextKeyView = self; field.nextResponder = self; copyField = field; addSubview(field)
        }
        field.frame = rect; field.textColor = secondary
        let value = state.monitorMode ? state.monitorTitle + " · " + state.monitorStatusLabel : state.budgetMode ? state.budgetName + " · " + state.budgetRemaining : state.exact
        if field.stringValue != value { field.stringValue = value }; field.toolTip = value
    }
    @objc private func scrolled(_ sender: NSScroller) {
        let vertical = sender === verticalScroller, span = vertical ? max(0, 356 - bodyViewport.maxY) : max(0, bodyWidth - bodyViewport.width)
        var value = vertical ? contentOffset.y : contentOffset.x
        switch sender.hitPart {
        case .decrementLine: value -= 24
        case .incrementLine: value += 24
        case .decrementPage: value -= 100
        case .incrementPage: value += 100
        default: value = CGFloat(sender.doubleValue) * span
        }
        if vertical { contentOffset.y = value } else { contentOffset.x = value }
        updateScrollers(); needsDisplay = true
    }
    func containsScreenPoint(_ point: NSPoint) -> Bool {
        guard let window = window else { return false }
        return containsSurfacePoint(convert(window.convertPoint(fromScreen: point), from: nil))
    }
    private func containsSurfacePoint(_ point: NSPoint) -> Bool {
        surfaceOutline(in: bounds).contains(point)
    }
    private func screenPoint(_ event: NSEvent) -> NSPoint {
        (event.window ?? window)?.convertPoint(toScreen: event.locationInWindow) ?? NSEvent.mouseLocation
    }
    private func inHotspot(_ point: NSPoint) -> Bool {
        guard let frame = compactHotspot else { return false }
        return NSBezierPath(roundedRect: frame, xRadius: Self.small.height / 2, yRadius: Self.small.height / 2).contains(point)
    }
    private func updateHover(_ event: NSEvent) {
        guard !state.interactionActive else { return }
        let point = screenPoint(event), inside = containsScreenPoint(point)
        if expansion == 0, inside { compactHotspot = window?.convertToScreen(convert(bounds, to: nil)); compactHoverPoint = point }
        else if !inHotspot(point) { compactHotspot = nil }
        setButtonFeedback(feedbackAction(point))
        hover?(inside)
    }
    override func mouseEntered(with event: NSEvent) { updateHover(event) }
    override func mouseMoved(with event: NSEvent) {
        let point = screenPoint(event)
        if expansion > 0, let initial = compactHoverPoint, hypot(point.x - initial.x, point.y - initial.y) >= 3 {
            compactHotspot = nil; compactHoverPoint = nil
        }
        pointerMoved?(point); updateHover(event)
    }
    override func mouseExited(with event: NSEvent) {
        setButtonFeedback(nil)
        guard !state.interactionActive else { return }
        if !inHotspot(screenPoint(event)) { compactHotspot = nil }
        hover?(false)
    }
    private func hitAction(_ point: NSPoint) -> String? {
        guard containsScreenPoint(point), let window = window else { return nil }
        if docking > 0 { return "details" }
        // Preserve the original circular target while hover expands beneath the
        // stationary pointer, including when screen-edge clamping moves the view.
        let local = convert(window.convertPoint(fromScreen: point), from: nil)
        // A visible collapse control wins over a retained compact hotspot and
        // is actionable while the opening animation is still in flight.
        if regions().contains(where: { $0.0 == "collapse" && $0.2.contains(local) }) { return "collapse" }
        if inHotspot(point) { return "details" }
        if let name = regions().first(where: { $0.0 != "context" && $0.0 != "budgetScope" && $0.2.contains(local) })?.0 { return name }
        // All visible non-action content is a grip, including data and disabled rows.
        if expansion > 0 { return "drag" }
        return nil
    }
    private func isActionEnabled(_ name: String) -> Bool {
        name != "budgetScope" && (name != "refresh" || state.canRefresh)
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
              let name = hitAction(screenPoint(event)) else { return }
        // A double click must not dispatch a second primary action.
        guard name != "details" || event.clickCount < 2 else { return }
        state.keyboardInteracting = false; window.resignKey()
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
        if value.dragged {
            window?.setFrameOrigin(NSPoint(x: value.origin.x + dx, y: value.origin.y + dy))
            compactHotspot = value.hotspot?.offsetBy(dx: dx, dy: dy)
        }
    }
    override func mouseUp(with event: NSEvent) {
        // AppKit can coalesce the last move; the release must take the exact
        // same irreversible drag path before deciding whether to activate.
        mouseDragged(with: event)
        guard let value = press else { return }
        let point = screenPoint(event)
        let moved = value.dragged
        let selected = !moved && hitAction(point) == value.name && isActionEnabled(value.name) ? value.name : nil
        setButtonFeedback(!moved ? feedbackAction(point) : nil)
        press = nil; state.pointerPressed = false
        if moved { dragReleased?(point) }
        interactionChanged?(false)
        if let name = selected, name != "drag" { performAction(name == "details" ? (docking > 0 ? "expand" : "main") : name) }
    }
    override func rightMouseDown(with event: NSEvent) {
        guard !state.interactionActive, containsScreenPoint(screenPoint(event)) else { return }
        presentContextMenu(at: screenPoint(event))
    }
    func cancelInteraction() {
        setButtonFeedback(nil)
        menuGeneration += 1
        activeMenu?.cancelTracking(); activeMenu = nil; press = nil; compactHotspot = nil
        state.keyboardInteracting = false
        let active = state.interactionActive
        state.pointerPressed = false; state.menuPresented = false
        if active { interactionChanged?(false) }
    }
    private func performAction(_ name: String) {
        guard isActionEnabled(name) else { return }
        if name == "content" {
            presentMenu([("usage", "用量统计"), ("budget", "预算提醒"), ("monitor", "任务监控")], selected: state.monitorMode ? "monitor" : state.budgetMode ? "budget" : "usage", trigger: NSRect(x: 16, y: 78, width: 70, height: 24), action: "content")
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
        let frame = window.convertToScreen(convert(trigger.offsetBy(dx: -contentOffset.x, dy: -contentOffset.y).intersection(bodyViewport), to: nil))
        trackMenu(options, selected: selected, point: NSPoint(x: frame.minX, y: frame.minY - 4), prefix: prefix, generation: generation)
    }
    private func trackMenu(_ options: [(String, String)], selected: String, point: NSPoint, prefix: String, generation: Int) {
        let menu = NSMenu(), selection = CapsuleMenuSelection()
        menu.autoenablesItems = false; activeMenu = menu
        for (value, title) in options {
            if value.isEmpty { menu.addItem(.separator()); continue }
            let row = NSMenuItem(title: title, action: #selector(CapsuleMenuSelection.select(_:)), keyEquivalent: "")
            row.representedObject = value; row.target = selection; row.state = selected == value ? .on : .off
            row.isEnabled = value != "refresh" || state.canRefresh
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
        trackMenu(budgetActions + [("main", "打开主面板"), ("monitor", "任务监控…"), ("refresh", state.monitorMode ? "检查任务" : "立即刷新"), ("", ""),
                   ("details", state.keepsExpanded ? "解除保持展开" : "保持展开"), ("keyboard", "用键盘操作浮窗"),
                   ("autoHide", state.autoHide ? "关闭靠边自动隐藏" : "开启靠边自动隐藏"),
                   ("edgeMetric", state.edgeShowsUsed ? "贴边显示剩余额度" : "贴边显示已用额度"),
                ("pin", state.pinned ? "取消置顶" : "置顶浮窗"),
                   ("arcColors", "弧线配色…"), ("themeDark", "深色主题"), ("themeLight", "浅色主题"), ("settings", "设置…"), ("updates", "数据与更新…"), ("", ""),
                   ("only", "仅浮窗"), ("menu", "仅状态栏"), ("close", "隐藏浮窗"), ("quit", "退出 Codex 用量")],
                  selected: state.theme == .dark ? "themeDark" : "themeLight", point: point, prefix: "", generation: generation)
    }
    private func regions(includeOffscreen: Bool = false) -> [(String, String, NSRect)] {
        if docking > 0 { return [("expand", "展开贴边额度详情", bounds), ("context", "浮窗功能菜单", bounds)] }
        var result = [("details", "打开主面板", NSRect(x: 0, y: 0, width: bounds.width, height: headerHeight))]
        if expansion > 0.99 {
            result += [
                ("refresh", state.monitorMode ? (state.monitorChecking ? "正在检查任务" : "检查任务") : state.canRefresh ? "刷新胶囊" : "正在刷新", NSRect(x: 16, y: 53, width: 26, height: 24)),
                ("content", "浮窗内容，" + (state.monitorMode ? "任务监控" : state.budgetMode ? "预算提醒" : "用量统计"), NSRect(x: 16, y: 78, width: 70, height: 24)),
                (state.budgetMode ? "budget" : "period", state.budgetMode ? "选择预算，" + state.budgetName : "浮窗统计范围，" + state.scopeTitle, NSRect(x: 94, y: 78, width: 134, height: 24)),
                (state.budgetMode ? "budgetEdit" : "model", state.budgetMode ? "编辑预算范围" : "浮窗模型，" + state.modelTitle, NSRect(x: 16, y: 110, width: bodyWidth - 32, height: 24)),
                (state.budgetMode ? "budgetScope" : "task", state.budgetMode ? "预算范围，只读" : "浮窗任务，" + state.taskTitle, NSRect(x: 16, y: 142, width: bodyWidth - 32, height: 24)),
                ("pin", state.pinned ? "取消置顶" : "置顶胶囊", NSRect(x: bodyWidth - 60, y: 78, width: 44, height: 24)),
                ("themeDark", "深色主题" + (state.theme == .dark ? "，已选中" : ""), NSRect(x: bodyWidth - 162, y: 300, width: 72, height: 24)),
                ("themeLight", "浅色主题" + (state.theme == .light ? "，已选中" : ""), NSRect(x: bodyWidth - 90, y: 300, width: 74, height: 24)),
                ("interval", "自动刷新间隔，" + (state.refreshSeconds == 0 ? "关闭" : "每 \(state.refreshSeconds) 秒"), NSRect(x: bodyWidth - 162, y: 332, width: 146, height: 24)),
                ("main", "打开主面板", NSRect(x: 16, y: bounds.height - 37, width: 86, height: 25)),
                ("monitor", state.monitorSummary, NSRect(x: 110, y: bounds.height - 37, width: bounds.width - 188, height: 25)),
                ("collapse", "收起详情", NSRect(x: bounds.width - 70, y: bounds.height - 37, width: 54, height: 25))]
        }
        if morph.detailsAlpha > 0 && expansion <= 0.99 {
            result.append(("collapse", "收起详情", NSRect(x: bounds.width - 70, y: bounds.height - 37, width: 54, height: 25)))
        }
        if state.monitorMode && expansion > 0.99 {
            result.removeAll { ["period", "budget", "model", "task", "budgetEdit", "budgetScope"].contains($0.0) }
            result.append(("monitorMessages", "查看未读消息与历史", NSRect(x: 94, y: 78, width: 134, height: 24)))
            if state.monitorRows.isEmpty {
                result.append(("monitorOpen", "选择任务和查看消息", NSRect(x: 28, y: 214, width: bodyWidth - 56, height: 26)))
            } else {
                for (index, row) in state.monitorRows.enumerated() {
                    let y = CGFloat(146 + index * 33)
                    result.append(("monitorView:" + row.id, "查看任务：" + row.title, NSRect(x: 28, y: y, width: bodyWidth - 118, height: 28)))
                    if row.active { result.append(("monitorStop:" + row.id, "停止提醒：" + row.title, NSRect(x: bodyWidth - 84, y: y, width: 56, height: 28))) }
                }
            }
            result.append(("monitorClear", "清除已结束结果，保留消息", NSRect(x: 16, y: 260, width: 148, height: 22)))
            result.append(("monitorSettings", "任务提醒设置", NSRect(x: 178, y: 260, width: 142, height: 22)))
        }
        result.append(("context", "浮窗功能菜单", NSRect(x: 0, y: 0, width: bounds.width, height: headerHeight)))
        return result.compactMap { name, label, frame in
            if name == "monitor" && bounds.width < 280 { return nil }
            if !["main", "monitor", "collapse", "details", "context"].contains(name) {
                let shifted = frame.offsetBy(dx: -contentOffset.x, dy: -contentOffset.y)
                let clipped = includeOffscreen ? shifted : shifted.intersection(bodyViewport)
                return clipped.isEmpty ? nil : (name, label, clipped)
            }
            return (name, label, frame)
        }
    }
    override func accessibilityValue() -> Any? {
        if state.monitorMode { return state.monitorTitle + "，" + state.monitorStatusLabel + "，" + state.monitorSummary + "，" + state.monitorSource }
        let monitoring = state.monitorStatus == "none" && state.monitorUnread == 0 ? "" : "，监控：" + state.monitorStatusLabel + "，" + state.monitorSummary
        if state.budgetMode { return "预算 \(state.budgetName)，剩余 \(state.budgetRemaining)，已用 \(state.budgetUsed)，\(state.budgetStatus)，\(state.budgetScope)" + monitoring }
        return (expansion > 0.99 ? "\(state.exact)，\(state.context)，输入 \(state.input)，输出 \(state.output)，\(state.cache)，\(state.status)，\(state.quotaDetail)" : "\(state.scopeTitle)总 Token \(state.total)，\(state.quotaCompact)") + monitoring
    }
    override func accessibilityChildren() -> [Any]? {
        guard let window = window else { return [] }
        let actions: [Any] = regions().map { name, label, frame in
            let element = accessibleActions[name] ?? CapsuleAction()
            accessibleActions[name] = element
            element.setAccessibilityRole(.button)
            element.setAccessibilityLabel(label)
            element.setAccessibilityParent(self)
            element.setAccessibilityFrame(window.convertToScreen(convert(frame, to: nil)))
            element.setAccessibilityEnabled(name != "budgetScope" && (name != "refresh" || state.canRefresh))
            element.perform = { [weak self] in
                guard let self = self, name != "budgetScope", name != "refresh" || self.state.canRefresh else { return }
                if ["content", "budget", "period", "model", "task", "interval", "context"].contains(name) {
                    DispatchQueue.main.async { [weak self] in self?.performAction(name) }
                } else { self.performAction(name == "details" ? "main" : name) }
            }
            return element
        }
        return actions + (copyField.map { [$0] } ?? [])
    }
    private func font(size: CGFloat, weight: NSFont.Weight, mono: Bool, rounded: Bool) -> NSFont {
        let base = mono ? NSFont.monospacedDigitSystemFont(ofSize: size, weight: weight) : NSFont.systemFont(ofSize: size, weight: weight)
        guard rounded, let descriptor = base.fontDescriptor.withDesign(.rounded) else { return base }
        return NSFont(descriptor: descriptor, size: size) ?? base
    }
    private var deferredText: [() -> Void] = []
    private var deferText = false, drawingBodyText = false
    private func text(_ value: String, _ rect: NSRect, size: CGFloat, color: NSColor, weight: NSFont.Weight = .regular, mono: Bool = false, rounded: Bool = false, truncation: NSLineBreakMode = .byTruncatingTail, alignment: NSTextAlignment = .left) {
        let paragraph = NSMutableParagraphStyle(); paragraph.lineBreakMode = truncation
        paragraph.alignment = alignment
        let attributes: [NSAttributedString.Key: Any] = [
            .font: font(size: size, weight: weight, mono: mono, rounded: rounded),
            .foregroundColor: color, .paragraphStyle: paragraph]
        if deferText {
            let target = drawingBodyText ? rect.offsetBy(dx: -contentOffset.x, dy: -contentOffset.y) : rect
            let clip = drawingBodyText ? bodyViewport : bounds
            deferredText.append {
                NSGraphicsContext.saveGraphicsState(); NSBezierPath(rect: clip).addClip()
                (value as NSString).draw(in: target, withAttributes: attributes)
                NSGraphicsContext.restoreGraphicsState()
            }
        } else { (value as NSString).draw(in: rect, withAttributes: attributes) }
    }
    private func fill(_ rect: NSRect, radius: CGFloat, color: NSColor) {
        color.setFill(); NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius).fill()
    }
    private func buttonGlowBlur(_ rect: NSRect) -> CGFloat { 25 * min(1, rect.height / 40) }
    private func isThemeButton(_ name: String) -> Bool { name == "themeDark" || name == "themeLight" }
    private func buttonOutline(_ name: String, _ rect: NSRect) -> NSRect {
        // Theme choices share one segmented-control outline while retaining
        // separate pointer/accessibility actions and the selected inner segment.
        isThemeButton(name) ? NSRect(x: bodyWidth - 162 - contentOffset.x, y: 300 - contentOffset.y, width: 146, height: 24).intersection(bodyViewport) : rect
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
        context.endTransparencyLayer()
        NSGraphicsContext.restoreGraphicsState()
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
    private func drawOrb(includePrimary: Bool = true) {
        let center = NSPoint(x: bounds.midX, y: Self.small.height / 2)
        let radius: CGFloat = Self.small.width / 2 - 4.5
        let ring = NSBezierPath(ovalIn: NSRect(x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2))
        palette.track.setStroke()
        ring.lineWidth = 5; ring.stroke()
        if let fraction = liquidFraction, fraction > 0 {
            let remaining = NSBezierPath()
            remaining.appendArc(withCenter: center, radius: radius, startAngle: -90, endAngle: -90 + 360 * fraction, clockwise: false)
            remaining.lineWidth = 5; remaining.lineCapStyle = .round
            (state.arcStylePreview ?? state.arcStyle).color(for: fraction, theme: state.theme).setStroke(); remaining.stroke()
        }
        let name = state.displayName == "剩余额度" ? "额度" : state.displayName.replacingOccurrences(of: "剩余", with: "余")
        text(name, NSRect(x: center.x - 23, y: 11, width: 46, height: 13), size: 8.5, color: secondary, weight: .medium, alignment: .center)
        let digits = state.normalizedQuota.map { "\(Int(($0 * 100 + 1e-9).rounded(.down)))" } ?? "—"
        let unit = (state.normalizedQuota == nil ? "" : "%") + (state.displayStale && state.normalizedQuota != nil ? "*" : "")
        let valueFont = font(size: 18, weight: .medium, mono: true, rounded: true)
        let unitFont = font(size: 10, weight: .medium, mono: false, rounded: true)
        let valueWidth = (digits as NSString).size(withAttributes: [.font: valueFont]).width
        let unitWidth = (unit as NSString).size(withAttributes: [.font: unitFont]).width
        let x = center.x - (valueWidth + unitWidth + (unit.isEmpty ? 0 : 1)) / 2
        let baseline: CGFloat = 40
        if includePrimary {
        text(digits, NSRect(x: x, y: baseline - valueFont.ascender, width: valueWidth + 1, height: 27), size: 18, color: ink, weight: .medium, mono: true, rounded: true)
        text(unit, NSRect(x: x + valueWidth + 1, y: baseline - unitFont.ascender, width: unitWidth + 1, height: 17), size: 10, color: ink, weight: .medium, rounded: true)
        }
        let tokenWidth = (state.displayTotal as NSString).size(withAttributes: [.font: font(size: 10, weight: .medium, mono: true, rounded: true)]).width
        let tokenSize = max(8, min(10, 480 / max(1, tokenWidth)))
        text(state.displayTotal, NSRect(x: center.x - 25, y: 43, width: 50, height: 15), size: tokenSize, color: ink, weight: .medium, mono: true, rounded: true, truncation: .byTruncatingMiddle, alignment: .center)
        if state.monitorStatus != "none" || state.monitorUnread > 0 {
            drawMonitorBadge(in: NSRect(x: center.x - 18, y: 55, width: 36, height: 13))
        } else {
            text(state.displayScope, NSRect(x: center.x - 20, y: 57, width: 40, height: 13), size: 8.5, color: secondary, weight: .medium, alignment: .center)
        }
    }
    private func drawBattery(includePrimary: Bool = true) {
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
        if includePrimary { drawHeader(quotaDigits, x: inset, width: digitWidth + 1, size: valueSize, color: ink, mono: true) }
        let unit = (percent == nil ? "" : "%") + (state.displayStale && state.normalizedQuota != nil ? "*" : "")
        let unitWidth = width(unit, unitSize)
        if includePrimary { drawHeader(unit, x: inset + digitWidth + 1, width: unitWidth + 1, size: unitSize, color: ink.withAlphaComponent(0.86)) }
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
    /// A static status capsule sits inside the quota ring, never on its arc.
    /// Running is an ellipsis, not a play button or an invented progress meter.
    private func drawMonitorBadge(in rect: NSRect) {
        let color: NSColor = state.monitorStatus == "unknown" || state.monitorStatus == "interrupted" ? .systemOrange : .systemBlue
        let glyph = ["running": "•••", "completed": "✓", "interrupted": "■", "unknown": "?", "idle": "◷"][state.monitorStatus] ?? "·"
        let label = state.monitorUnread > 0 ? glyph + " " + (state.monitorUnread > 99 ? "99+" : String(state.monitorUnread)) : glyph
        fill(rect, radius: rect.height / 2, color: color.withAlphaComponent(state.theme == .light ? 0.09 : 0.16))
        text(label, rect.offsetBy(dx: 0, dy: -0.5), size: 9, color: color, weight: .medium, mono: true, alignment: .center)
    }
    // A single contour drives paint and hit testing, including intermediate frames.
    // Canonical coordinates describe bottom docking in this flipped view.
    func surfaceOutline(in rect: NSRect) -> NSBezierPath {
        guard docking > 0, let edge = dockEdge else {
            return NSBezierPath(roundedRect: rect, xRadius: cornerRadius, yRadius: cornerRadius)
        }
        let t = min(1, max(0, docking))
        func point(_ x: CGFloat, _ y: CGFloat) -> NSPoint {
            let p: NSPoint
            switch edge {
            case "top": p = NSPoint(x: x, y: 1 - y)
            case "left": p = NSPoint(x: 1 - y, y: x)
            case "right": p = NSPoint(x: y, y: x)
            default: p = NSPoint(x: x, y: y)
            }
            return NSPoint(x: rect.minX + p.x * rect.width, y: rect.minY + p.y * rect.height)
        }
        // Six matching cubic segments morph the orb into a low, broad crest.
        // The two outer feet meet the screen edge tangentially, without a neck.
        let ends: [(CGFloat, CGFloat)] = [(0.20, 0.28), (0.5, 0), (0.80, 0.28), (1, 1), (0.5, 1), (0, 1)]
        let controls: [(CGFloat, CGFloat, CGFloat, CGFloat)] = [
            (0.10, 1, 0.11, 0.54), (0.29, 0.02, 0.39, 0),
            (0.61, 0, 0.71, 0.02), (0.89, 0.54, 0.90, 1),
            (0.84, 1, 0.67, 1), (0.33, 1, 0.16, 1)]
        let angles: [CGFloat] = [.pi, .pi * 1.25, .pi * 1.5, .pi * 1.75, .pi * 2, .pi * 2.5, .pi * 3]
        func mixed(_ x: CGFloat, _ y: CGFloat, _ targetX: CGFloat, _ targetY: CGFloat) -> NSPoint {
            point(x + (targetX - x) * t, y + (targetY - y) * t)
        }
        let path = NSBezierPath(); path.move(to: mixed(0, 0.5, 0, 1))
        for i in 0..<6 {
            let a = angles[i], b = angles[i + 1], k = 4 / 3 * tan((b - a) / 4)
            let c = controls[i], e = ends[i]
            path.curve(to: mixed(0.5 + cos(b) / 2, 0.5 + sin(b) / 2, e.0, e.1),
                       controlPoint1: mixed(0.5 + (cos(a) - k * sin(a)) / 2, 0.5 + (sin(a) + k * cos(a)) / 2, c.0, c.1),
                       controlPoint2: mixed(0.5 + (cos(b) + k * sin(b)) / 2, 0.5 + (sin(b) - k * cos(b)) / 2, c.2, c.3))
        }
        path.close(); return path
    }
    private func drawDockedSummary() {
        let vertical = dockEdge == "left" || dockEdge == "right"
        let percent = state.normalizedQuota.map { "\(Int(((state.edgeShowsUsed ? 1 - $0 : $0) * 100 + 1e-9).rounded(.down)))%" } ?? "—"
        let value = percent + (state.displayStale && state.normalizedQuota != nil ? "*" : "")
        let showsMonitor = state.showsDockedMonitor
        let size: CGFloat = vertical ? 11 : 12
        let valueFont = font(size: size, weight: .medium, mono: true, rounded: false)
        let valueWidth = ceil((value as NSString).size(withAttributes: [.font: valueFont]).width) + 2
        // Place the visible capital/digit height around the optical center.
        let centerY = bounds.midY + (vertical && showsMonitor ? -9 : vertical ? 0 : dockEdge == "top" ? -1 : 1)
        let badgeWidth: CGFloat = state.monitorUnread > 0 ? 36 : 20
        let groupWidth = valueWidth + (showsMonitor && !vertical ? badgeWidth + 6 : 0)
        let valueX = bounds.midX - (vertical ? valueWidth : groupWidth) / 2
        text(value, NSRect(x: valueX, y: centerY - valueFont.ascender + valueFont.capHeight / 2,
                          width: valueWidth, height: ceil(valueFont.ascender - valueFont.descender + 3)),
             size: size, color: ink, weight: .medium, mono: true, alignment: .center)
        if showsMonitor {
            let badge = vertical
                ? NSRect(x: bounds.midX - badgeWidth / 2, y: bounds.midY + 4, width: badgeWidth, height: 13)
                : NSRect(x: valueX + valueWidth + 6, y: centerY - 6.5, width: badgeWidth, height: 13)
            drawMonitorBadge(in: badge)
        }
    }
    private func drawSummaryHeader() {
        if docking > 0 {
            if docking < 0.5 {
                NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current?.cgContext.setAlpha(1 - docking * 2); drawOrb(); NSGraphicsContext.restoreGraphicsState()
            } else {
                NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current?.cgContext.setAlpha((docking - 0.5) * 2); drawDockedSummary(); NSGraphicsContext.restoreGraphicsState()
            }
        } else {
            if morph.arcAlpha > 0 {
                NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current?.cgContext.setAlpha(morph.arcAlpha)
                drawOrb(includePrimary: false); NSGraphicsContext.restoreGraphicsState()
            }
            if morph.detailsAlpha > 0 {
                NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current?.cgContext.setAlpha(morph.detailsAlpha)
                drawBattery(includePrimary: false); NSGraphicsContext.restoreGraphicsState()
            }
        }
    }
    private func drawMorphPrimary() {
        guard docking == 0 else { return }
        let current = screenFrame
        let compact = expansion == 0 ? current : morphCompactFrame ?? NSRect(x: current.maxX - 76, y: current.maxY - 76, width: 76, height: 76)
        let panel = expansion == 1 ? current : morphDetailFrame ?? current
        let p = morph.vertical, scale = 0.9 + 0.1 * p
        let digits = state.normalizedQuota.map { "\(Int(($0 * 100 + 1e-9).rounded(.down)))" } ?? "—"
        let unit = (state.normalizedQuota == nil ? "" : "%") + (state.displayStale && state.normalizedQuota != nil ? "*" : "")
        let valueFont = font(size: 20, weight: .medium, mono: true, rounded: true)
        let unitFont = font(size: 11, weight: .medium, mono: false, rounded: true)
        let valueWidth = (digits as NSString).size(withAttributes: [.font: valueFont]).width
        let unitWidth = (unit as NSString).size(withAttributes: [.font: unitFont]).width
        let width = valueWidth + (unit.isEmpty ? 0 : unitWidth + 1)
        let fromX = compact.midX - width * 0.9 / 2, toX = panel.minX + 20
        let fromBaseline = compact.maxY - 40, toBaseline = panel.maxY - (26 + valueFont.capHeight / 2)
        let x = fromX + (toX - fromX) * p - current.minX
        let y = current.maxY - (fromBaseline + (toBaseline - fromBaseline) * p) - valueFont.ascender * scale
        let wanted = NSRect(x: x, y: y, width: (width + 1) * scale, height: ceil(valueFont.ascender - valueFont.descender + 3) * scale)
        guard let safe = CapsuleMorph.safeText(wanted, inside: bounds, radius: cornerRadius) else { return }
        NSGraphicsContext.saveGraphicsState(); NSGraphicsContext.current?.cgContext.setAlpha(1)
        let transform = NSAffineTransform(); transform.translateX(by: safe.minX, yBy: safe.minY); transform.scale(by: scale); transform.concat()
        // Two stable fonts are scaled through the animation, avoiding per-frame
        // font creation and keeping one percentage visible for the whole morph.
        text(digits, NSRect(x: 0, y: 0, width: valueWidth + 1, height: 30), size: 20, color: ink, weight: .medium, mono: true, rounded: true)
        text(unit, NSRect(x: valueWidth + 1, y: valueFont.ascender - unitFont.ascender, width: unitWidth + 1, height: 20), size: 11, color: ink.withAlphaComponent(1 - 0.14 * p), weight: .medium, rounded: true)
        NSGraphicsContext.restoreGraphicsState()
    }

    override func draw(_ dirtyRect: NSRect) {
        let light = state.theme == .light
        let shape = surfaceOutline(in: bounds.insetBy(dx: 0.5, dy: 0.5))
        NSGraphicsContext.saveGraphicsState(); shape.addClip()
        palette.background.setFill(); shape.fill()
        // At full expansion every label, including the quota header, stays above
        // the continuous glow. Only the outer window shape clips the halo.
        deferText = morph.detailsAlpha > 0; drawingBodyText = false
        drawSummaryHeader()
        palette.border.setStroke(); shape.lineWidth = NSWorkspace.shared.accessibilityDisplayShouldIncreaseContrast ? 2 : 0.6; shape.stroke()
        let detailOpacity = morph.detailsAlpha
        if detailOpacity > 0 {
            NSGraphicsContext.current?.cgContext.setAlpha(detailOpacity)
            palette.border.setStroke(); shape.lineWidth = 1; shape.stroke()
            deferText = true; drawingBodyText = true
            NSGraphicsContext.saveGraphicsState()
            NSBezierPath(rect: bodyViewport).addClip()
            let scrollTransform = NSAffineTransform(); scrollTransform.translateX(by: -contentOffset.x, yBy: -contentOffset.y); scrollTransform.concat()
            if copyField == nil { text(state.monitorMode ? "监控状态来自明确的轮次事件" : state.budgetMode ? "预算范围独立 · 点击周期可编辑" : state.exact, NSRect(x: 48, y: 57, width: max(0, bodyWidth - 132), height: 16), size: 10, color: secondary, mono: true) }
            let symbol = state.monitorMode ? (state.monitorChecking ? "…" : "↻") : state.indicator == "busy" ? "…" : (state.indicator == "check" ? "✓" : "↻")
            buttonBackground("refresh", NSRect(x: 16, y: 53, width: 26, height: 24))
            text(symbol, NSRect(x: 19, y: 52, width: 22, height: 25), size: 18, color: state.canRefresh ? mint : secondary)
            buttonBackground("content", NSRect(x: 16, y: 78, width: 70, height: 24), base: ink.withAlphaComponent(0.065))
            text(state.monitorMode ? "监控 ⌄" : state.budgetMode ? "预算 ⌄" : "用量 ⌄", NSRect(x: 29, y: 83, width: 54, height: 15), size: 10, color: mint, weight: .medium)
            if state.monitorMode {
                buttonBackground("monitorMessages", NSRect(x: 94, y: 78, width: 134, height: 24), base: ink.withAlphaComponent(0.065))
                text(state.monitorUnread > 0 ? "未读消息 · \(state.monitorUnread)" : "消息历史…", NSRect(x: 105, y: 83, width: 118, height: 15), size: 10, color: mint)
            }
            if !state.monitorMode {
            buttonBackground(state.budgetMode ? "budget" : "period", NSRect(x: 94, y: 78, width: 134, height: 24), base: ink.withAlphaComponent(0.065))
            text(state.budgetMode ? state.budgetName : "范围 · " + state.scopeTitle, NSRect(x: 105, y: 83, width: 102, height: 15), size: 10, color: mint, weight: .medium)
            text("⌄", NSRect(x: 210, y: 81, width: 14, height: 17), size: 12, color: secondary)
            }
            buttonBackground("pin", NSRect(x: bodyWidth - 60, y: 78, width: 44, height: 24))
            text(state.pinned ? "● 置顶" : "○ 置顶", NSRect(x: bodyWidth - 58, y: 83, width: 44, height: 15), size: 10, color: state.pinned ? mint : secondary)
            if state.monitorMode {
                fill(NSRect(x: 16, y: 110, width: bodyWidth - 32, height: 140), radius: 12, color: ink.withAlphaComponent(0.045))
                drawMonitorBadge(in: NSRect(x: 28, y: 122, width: 36, height: 15))
                text(state.monitorStatusLabel, NSRect(x: 74, y: 122, width: bodyWidth - 100, height: 18), size: 12, color: ink, weight: .medium)
                if state.monitorRows.isEmpty {
                    text(state.monitorTitle, NSRect(x: 28, y: 154, width: bodyWidth - 56, height: 22), size: 14, color: ink, weight: .medium)
                    text(state.monitorDetail, NSRect(x: 28, y: 185, width: bodyWidth - 56, height: 17), size: 10, color: secondary)
                    buttonBackground("monitorOpen", NSRect(x: 28, y: 214, width: bodyWidth - 56, height: 26), base: mint.withAlphaComponent(0.1))
                    text("选择任务和查看消息…", NSRect(x: 38, y: 220, width: bodyWidth - 76, height: 17), size: 11, color: mint)
                } else {
                    for (index, row) in state.monitorRows.enumerated() {
                        let y = CGFloat(146 + index * 33)
                        buttonBackground("monitorView:" + row.id, NSRect(x: 28, y: y, width: bodyWidth - 118, height: 28))
                        text(row.title, NSRect(x: 30, y: y, width: bodyWidth - 126, height: 15), size: 10, color: ink, weight: .medium)
                        text(row.status + " · 查看", NSRect(x: 30, y: y + 14, width: bodyWidth - 126, height: 13), size: 8, color: secondary)
                        if row.active {
                            buttonBackground("monitorStop:" + row.id, NSRect(x: bodyWidth - 84, y: y, width: 56, height: 28))
                            text("停止提醒", NSRect(x: bodyWidth - 80, y: y + 7, width: 52, height: 15), size: 9, color: secondary)
                        }
                    }
                }
                buttonBackground("monitorClear", NSRect(x: 16, y: 260, width: 148, height: 22))
                text("清除已结束结果", NSRect(x: 20, y: 264, width: 140, height: 15), size: 10, color: secondary)
                buttonBackground("monitorSettings", NSRect(x: 178, y: 260, width: 142, height: 22))
                text("提醒设置…", NSRect(x: 188, y: 264, width: 128, height: 15), size: 10, color: secondary)
                text(state.monitorSource, NSRect(x: 20, y: 282, width: bodyWidth - 40, height: 17), size: 9, color: secondary)
            } else if state.budgetMode {
                buttonBackground("budgetEdit", NSRect(x: 16, y: 110, width: bodyWidth - 32, height: 24))
                text(state.budgetPeriod, NSRect(x: 20, y: 114, width: bodyWidth - 40, height: 17), size: 11, color: ink)
                text(state.budgetScope, NSRect(x: 20, y: 146, width: bodyWidth - 40, height: 17), size: 10, color: secondary)
                fill(NSRect(x: 16, y: 174, width: bodyWidth - 32, height: 76), radius: 12, color: ink.withAlphaComponent(0.05))
                text("预算剩余", NSRect(x: 29, y: 185, width: 160, height: 15), size: 10, color: secondary)
                text(state.budgetRemaining, NSRect(x: 29, y: 207, width: bodyWidth - 58, height: 30), size: 22, color: mint, weight: .medium, mono: true)
                text(state.budgetAmountLabel + " " + state.budgetAmount, NSRect(x: 20, y: 260, width: bodyWidth - 40, height: 17), size: 10, color: secondary)
                text(state.budgetStatus, NSRect(x: 20, y: 282, width: bodyWidth - 40, height: 16), size: 9, color: secondary)
            } else {
            for (y, label, value) in [(CGFloat(110), "模型", state.modelTitle), (CGFloat(142), "任务", state.taskTitle)] {
                let width = max(0, bodyWidth - 32)
                buttonBackground(y == 110 ? "model" : "task", NSRect(x: 16, y: y, width: width, height: 24), base: ink.withAlphaComponent(0.065))
                text(label, NSRect(x: 28, y: y + 5, width: 48, height: 15), size: 10, color: secondary)
                text(value, NSRect(x: 92, y: y + 5, width: max(0, width - 112), height: 15), size: 10, color: mint, weight: .medium)
                text("⌄", NSRect(x: bodyWidth - 36, y: y + 3, width: 14, height: 17), size: 12, color: secondary)
            }
            fill(NSRect(x: 16, y: 174, width: bodyWidth - 32, height: 28), radius: 8, color: ink.withAlphaComponent(0.035))
            text(state.quotaDetail, NSRect(x: 26, y: 182, width: bodyWidth - 52, height: 16), size: 10, color: mint, weight: .medium)
            let cardWidth = (bodyWidth - 42) / 2
            for (i, item) in [("输入", state.input), ("输出", state.output)].enumerated() {
                let x: CGFloat = 16 + CGFloat(i) * (cardWidth + 10)
                fill(NSRect(x: x, y: 212, width: cardWidth, height: 44), radius: 10, color: ink.withAlphaComponent(0.05))
                text(item.0, NSRect(x: x + 12, y: 218, width: cardWidth - 24, height: 13), size: 9, color: secondary)
                text(item.1, NSRect(x: x + 12, y: 232, width: cardWidth - 24, height: 20), size: 14, color: ink, weight: .medium, mono: true)
            }
            text(state.cache, NSRect(x: 20, y: 264, width: bodyWidth - 40, height: 15), size: 10, color: secondary)
            text(state.status, NSRect(x: 20, y: 282, width: bodyWidth - 40, height: 15), size: 9, color: secondary)
            }
            text("胶囊主题", NSRect(x: 20, y: 306, width: 110, height: 15), size: 10, color: secondary)
            let themeX = bodyWidth - 162
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
            NSGraphicsContext.restoreGraphicsState()
            drawingBodyText = false
            // Keep footer controls in their final layout while the window reveals
            // the content. Following the animated bottom would cross other rows.
            let buttonY = bounds.height - 37
            buttonBackground("main", NSRect(x: 16, y: buttonY, width: 86, height: 25), base: mint.withAlphaComponent(0.13))
            if bounds.width >= 280 {
                buttonBackground("monitor", NSRect(x: 110, y: buttonY, width: bounds.width - 188, height: 25))
                text(state.monitorSummary, NSRect(x: 117, y: buttonY + 6, width: bounds.width - 202, height: 15), size: 10, color: secondary)
            }
            buttonBackground("collapse", NSRect(x: bounds.width - 70, y: buttonY, width: 54, height: 25))
            text("打开主面板", NSRect(x: 29, y: buttonY + 6, width: 70, height: 15), size: 10, color: mint, weight: .medium)
            text("收起", NSRect(x: bounds.width - 51, y: buttonY + 6, width: 32, height: 15), size: 10, color: secondary)
            drawButtonOuterGlow()
            // Neighboring faces never punch holes in the halo; all labels are
            // drawn on top so their contrast and antialiasing stay crisp.
            deferText = false
            deferredText.forEach { $0() }; deferredText.removeAll(keepingCapacity: true)
            if window?.firstResponder === self, let name = keyboardAction, let frame = regions().first(where: { $0.0 == name })?.2 {
                NSColor.keyboardFocusIndicatorColor.setStroke(); let path = NSBezierPath(roundedRect: frame.insetBy(dx: 1, dy: 1), xRadius: 6, yRadius: 6); path.lineWidth = 2; path.stroke()
            }
        }
        drawMorphPrimary()
        NSGraphicsContext.restoreGraphicsState()
    }
}
