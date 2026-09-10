import AppKit
import CoreFoundation

private let trendInputColor = NSColor(calibratedRed: 0.06, green: 0.55, blue: 0.46, alpha: 1)
private let trendOutputColor = NSColor(calibratedRed: 0.48, green: 0.40, blue: 0.82, alpha: 1)

private enum TrendNumbers {
    static let formatter: NumberFormatter = {
        let value = NumberFormatter(); value.locale = Locale(identifier: "en_US_POSIX")
        value.numberStyle = .decimal; value.maximumFractionDigits = 0
        return value
    }()
    static func count(_ raw: Any?) -> NSNumber? {
        guard let value = raw as? NSNumber, CFGetTypeID(value) != CFBooleanGetTypeID(),
              value.doubleValue.isFinite, value.doubleValue >= 0,
              value.doubleValue.rounded(.towardZero) == value.doubleValue else { return nil }
        return value
    }
    static func exact(_ value: NSNumber?) -> String {
        guard let value = value else { return "未知" }
        // NumberFormatter can round large integer NSNumbers internally. Format
        // their integer storage directly; Double is only for graph proportions.
        let type = String(cString: value.objCType)
        let digits: String?
        if ["c", "s", "i", "l", "q"].contains(type) { digits = String(value.int64Value) }
        else if ["C", "S", "I", "L", "Q"].contains(type) { digits = String(value.uint64Value) }
        else { digits = nil }
        if let digits = digits {
            var grouped = ""
            for (index, digit) in digits.reversed().enumerated() {
                if index > 0 && index % 3 == 0 { grouped.append(",") }; grouped.append(digit)
            }
            return String(grouped.reversed())
        }
        return formatter.string(from: value) ?? "未知"
    }
    static func compact(_ value: Double) -> String {
        if value >= 1_000_000_000 { return String(format: "%.2fB", value / 1_000_000_000) }
        if value >= 1_000_000 { return String(format: "%.2fM", value / 1_000_000) }
        if value >= 1_000 { return String(format: "%.1fK", value / 1_000) }
        return exact(NSNumber(value: value))
    }
}

struct TrendDatum {
    let date: String
    let input: NSNumber?, output: NSNumber?, total: NSNumber?
    init(_ row: [String: Any]) {
        let label = (row["date"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        date = label.isEmpty ? "日期未知" : label
        input = TrendNumbers.count(row["input_tokens"])
        output = TrendNumbers.count(row["output_tokens"])
        total = TrendNumbers.count(row["total_tokens"])
    }
    var values: [(String, String)] {
        [("输入", TrendNumbers.exact(input)), ("输出", TrendNumbers.exact(output)), ("合计", TrendNumbers.exact(total))]
    }
    var tooltipText: String { ([date] + values.map { "\($0.0)：\($0.1) tokens" }).joined(separator: "\n") }
}

enum TrendGeometry {
    static func plot(in bounds: NSRect) -> NSRect {
        NSRect(x: bounds.minX + 48, y: bounds.minY + 12, width: max(0, bounds.width - 64), height: max(0, bounds.height - 40))
    }
    static func index(at point: NSPoint, in bounds: NSRect, count: Int) -> Int? {
        let area = plot(in: bounds)
        guard count > 0, !area.isEmpty, point.x >= area.minX, point.x < area.maxX,
              point.y >= area.minY, point.y < area.maxY else { return nil }
        return min(count - 1, Int((point.x - area.minX) / area.width * CGFloat(count)))
    }
    static func tooltipFrame(size: NSSize, near cursor: NSPoint, visible: NSRect) -> NSRect {
        let margin: CGFloat = 8, gap: CGFloat = 14
        let area = visible.insetBy(dx: min(margin, visible.width / 2), dy: min(margin, visible.height / 2))
        let width = min(size.width, area.width), height = min(size.height, area.height)
        var x = cursor.x + gap, y = cursor.y - height - gap
        if x + width > area.maxX { x = cursor.x - width - gap }
        if y < area.minY { y = cursor.y + gap }
        return NSRect(x: min(max(x, area.minX), area.maxX - width),
                      y: min(max(y, area.minY), area.maxY - height), width: width, height: height)
    }
}

private final class TrendTooltipView: NSView {
    var datum: TrendDatum { didSet { needsDisplay = true } }
    override var isFlipped: Bool { true }
    init(_ datum: TrendDatum) { self.datum = datum; super.init(frame: .zero); setAccessibilityElement(false) }
    required init?(coder: NSCoder) { fatalError() }
    var preferredSize: NSSize {
        let font = NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .medium)
        let values = datum.values.map { ($0.1 as NSString).size(withAttributes: [.font: font]).width + 66 }
        let heading = (datum.date as NSString).size(withAttributes: [.font: NSFont.systemFont(ofSize: 12, weight: .semibold)]).width + 24
        return NSSize(width: max(190, heading, values.max() ?? 0), height: 100)
    }
    override func draw(_ dirtyRect: NSRect) {
        let shape = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 10, yRadius: 10)
        NSColor.controlBackgroundColor.setFill(); shape.fill()
        NSColor.separatorColor.withAlphaComponent(0.7).setStroke(); shape.stroke()
        let paragraph = NSMutableParagraphStyle(); paragraph.lineBreakMode = .byTruncatingTail
        (datum.date as NSString).draw(in: NSRect(x: 12, y: 10, width: bounds.width - 24, height: 18), withAttributes: [.font: NSFont.systemFont(ofSize: 12, weight: .semibold), .foregroundColor: NSColor.labelColor, .paragraphStyle: paragraph])
        for (index, item) in datum.values.enumerated() {
            let y = 34 + CGFloat(index) * 20
            (item.0 as NSString).draw(in: NSRect(x: 12, y: y, width: 38, height: 17), withAttributes: [.font: NSFont.systemFont(ofSize: 11), .foregroundColor: NSColor.secondaryLabelColor])
            let alignment = NSMutableParagraphStyle(); alignment.alignment = .right; alignment.lineBreakMode = .byTruncatingTail
            (item.1 as NSString).draw(in: NSRect(x: 54, y: y, width: bounds.width - 66, height: 17), withAttributes: [.font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .medium), .foregroundColor: NSColor.labelColor, .paragraphStyle: alignment])
        }
    }
}

final class TrendView: NSView {
    var days: [[String: Any]] = [] {
        didSet { data = days.map(TrendDatum.init); updateHover(at: nil); updateAccessibility(); needsDisplay = true }
    }
    private var data: [TrendDatum] = []
    private var tracking: NSTrackingArea?
    private var tooltip: NSPanel?
    private weak var tooltipOwner: NSWindow?
    private var windowObservers: [NSObjectProtocol] = []
    private(set) var hoveredDayIndex: Int?
    var hoverText: String? { hoveredDayIndex.map { data[$0].tooltipText } }
    override var isFlipped: Bool { true }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        guard tracking == nil else { return }
        let area = NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .mouseMoved, .activeAlways, .inVisibleRect], owner: self)
        addTrackingArea(area); tracking = area
    }
    override func mouseEntered(with event: NSEvent) { updateHover(at: convert(event.locationInWindow, from: nil)) }
    override func mouseMoved(with event: NSEvent) { updateHover(at: convert(event.locationInWindow, from: nil)) }
    override func mouseExited(with event: NSEvent) { updateHover(at: nil) }
    override func setFrameSize(_ newSize: NSSize) { super.setFrameSize(newSize); updateHover(at: nil) }
    override func viewDidHide() { super.viewDidHide(); updateHover(at: nil) }
    override func viewWillMove(toWindow newWindow: NSWindow?) {
        updateHover(at: nil); removeWindowObservers(); super.viewWillMove(toWindow: newWindow)
    }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard let window = window else { return }
        for name in [NSWindow.willCloseNotification, NSWindow.didMiniaturizeNotification, NSWindow.didResignKeyNotification] {
            windowObservers.append(NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) { [weak self] _ in self?.updateHover(at: nil) })
        }
    }
    deinit { dismissTooltip(); removeWindowObservers() }
    private func removeWindowObservers() {
        windowObservers.forEach { NotificationCenter.default.removeObserver($0) }; windowObservers.removeAll()
    }
    private func updateAccessibility() {
        setAccessibilityElement(true); setAccessibilityRole(.image)
        setAccessibilityLabel("每日 Token 趋势，\(data.count) 天；悬停可查看日期、输入、输出和合计")
    }
    func updateHover(at point: NSPoint?) {
        let index = point.flatMap { TrendGeometry.index(at: $0, in: bounds, count: data.count) }
        if hoveredDayIndex != index { hoveredDayIndex = index; needsDisplay = true }
        setAccessibilityValue(hoverText)
        guard let index = index, let point = point, let owner = window, owner.isVisible else { dismissTooltip(); return }
        let datum = data[index]
        let content: TrendTooltipView
        if let existing = tooltip?.contentView as? TrendTooltipView { content = existing; content.datum = datum }
        else {
            content = TrendTooltipView(datum)
            let panel = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
            panel.isReleasedWhenClosed = false; panel.isOpaque = false; panel.backgroundColor = .clear; panel.hasShadow = true
            panel.ignoresMouseEvents = true; panel.hidesOnDeactivate = false; panel.contentView = content
            tooltip = panel; tooltipOwner = owner; owner.addChildWindow(panel, ordered: .above)
        }
        guard let panel = tooltip else { return }
        let cursor = owner.convertToScreen(NSRect(origin: convert(point, to: nil), size: .zero)).origin
        let screen = NSScreen.screens.first(where: { $0.frame.contains(cursor) }) ?? owner.screen
        guard let screen = screen else { dismissTooltip(); return }
        panel.appearance = effectiveAppearance
        panel.setFrame(TrendGeometry.tooltipFrame(size: content.preferredSize, near: cursor, visible: screen.visibleFrame), display: true)
        panel.orderFront(nil)
    }
    private func dismissTooltip() {
        guard let panel = tooltip else { return }
        tooltipOwner?.removeChildWindow(panel); tooltipOwner = nil
        panel.orderOut(nil); panel.contentView = nil; panel.close(); tooltip = nil
    }
    override func draw(_ dirtyRect: NSRect) {
        let plot = TrendGeometry.plot(in: bounds)
        guard !plot.isEmpty else { return }
        let maximum = max(1, data.compactMap { $0.total?.doubleValue }.max() ?? 0)
        let ceiling = min(Double.greatestFiniteMagnitude / 1.08, maximum) * 1.08
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .regular), .foregroundColor: NSColor.secondaryLabelColor]
        for i in 0...3 {
            let fraction = Double(i) / 3, y = plot.maxY - CGFloat(Double(i) / 3) * plot.height
            NSColor.separatorColor.withAlphaComponent(0.45).setStroke()
            let p = NSBezierPath(); p.move(to: NSPoint(x: plot.minX, y: y)); p.line(to: NSPoint(x: plot.maxX, y: y)); p.stroke()
            (TrendNumbers.compact(ceiling * fraction) as NSString).draw(at: NSPoint(x: bounds.minX, y: y - 7), withAttributes: attrs)
        }
        guard !data.isEmpty else {
            ("该范围暂无可计入的记录" as NSString).draw(at: NSPoint(x: plot.midX - 75, y: plot.midY - 10), withAttributes: attrs); return
        }
        let stride = plot.width / CGFloat(data.count), width = max(0.5, min(24, stride * 0.68))
        if let index = hoveredDayIndex {
            NSColor.labelColor.withAlphaComponent(0.045).setFill()
            NSRect(x: plot.minX + CGFloat(index) * stride, y: plot.minY, width: stride, height: plot.height).fill()
        }
        for (index, datum) in data.enumerated() {
            guard let total = datum.total?.doubleValue, total > 0 else { continue }
            let input = datum.input?.doubleValue ?? 0, output = datum.output?.doubleValue ?? 0
            let x = plot.minX + (CGFloat(index) + 0.5) * stride - width / 2
            let h = CGFloat(total / maximum / 1.08) * plot.height
            let denominator = max(total, input + output)
            let ih = h * CGFloat(input / denominator), oh = h * CGFloat(output / denominator)
            NSColor.secondaryLabelColor.withAlphaComponent(0.35).setFill(); NSRect(x: x, y: plot.maxY - h, width: width, height: h).fill()
            trendInputColor.setFill(); NSRect(x: x, y: plot.maxY - ih, width: width, height: ih).fill()
            trendOutputColor.setFill(); NSRect(x: x, y: plot.maxY - ih - oh, width: width, height: oh).fill()
        }
        for index in Set([0, data.count / 2, data.count - 1]).sorted() {
            let date = String(data[index].date.suffix(5))
            let x = min(plot.maxX - 34, max(plot.minX, plot.minX + (CGFloat(index) + 0.5) * stride - 15))
            (date as NSString).draw(at: NSPoint(x: x, y: plot.maxY + 10), withAttributes: attrs)
        }
    }
}
