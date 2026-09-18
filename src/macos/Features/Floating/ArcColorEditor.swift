import AppKit

private final class ArcEditorRoot: NSView {
    override var isFlipped: Bool { true }
    override func draw(_ dirtyRect: NSRect) { NSColor.windowBackgroundColor.setFill(); bounds.fill() }
}
private final class ArcPreviewContainer: NSView {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func accessibilityChildren() -> [Any]? { [] }
}

final class ArcColorSwatch: NSButton {
    var colors: [NSColor] = [.clear] { didSet { needsDisplay = true } }
    override func draw(_ dirtyRect: NSRect) {
        let rect = bounds.insetBy(dx: 4, dy: 4), path = NSBezierPath(roundedRect: rect, xRadius: 5, yRadius: 5)
        if colors.count > 1 { NSGradient(colors: colors)?.draw(in: path, angle: 0) }
        else { (colors.first ?? .clear).setFill(); path.fill() }
        if isHighlighted { NSColor.labelColor.withAlphaComponent(0.12).setFill(); path.fill() }
        if state == .on || window?.firstResponder === self {
            NSColor.keyboardFocusIndicatorColor.setStroke()
            let border = NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 1), xRadius: 7, yRadius: 7)
            border.lineWidth = 2; border.stroke()
        }
    }
}

/// An embedded wheel: no shared NSColorPanel, popup, polling, or extra window.
final class ArcColorWheel: NSControl {
    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func becomeFirstResponder() -> Bool { needsDisplay = true; return true }
    override func resignFirstResponder() -> Bool { needsDisplay = true; return true }
    private(set) var hue: CGFloat = 0
    private(set) var saturation: CGFloat = 0
    private(set) var brightness: CGFloat = 1
    private static let wheelImage: NSImage = {
        let size = 320
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                                      samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                      bytesPerRow: size * 4, bitsPerPixel: 32)!
        let data = bitmap.bitmapData!
        for y in 0..<size { for x in 0..<size {
            let dx = (CGFloat(x) + 0.5) / CGFloat(size) * 2 - 1, dy = 1 - (CGFloat(y) + 0.5) / CGFloat(size) * 2
            let distance = hypot(dx, dy), index = (y * size + x) * 4
            guard distance <= 1 else { data[index] = 0; data[index+1] = 0; data[index+2] = 0; data[index+3] = 0; continue }
            let color = rgb(hue: (atan2(dy, dx) / (.pi * 2) + 1).truncatingRemainder(dividingBy: 1), saturation: distance, brightness: 1)
            data[index] = UInt8((color.redComponent * 255).rounded()); data[index+1] = UInt8((color.greenComponent * 255).rounded())
            data[index+2] = UInt8((color.blueComponent * 255).rounded()); data[index+3] = UInt8(min(1, (1 - distance) * CGFloat(size) / 2) * 255)
        } }
        let image = NSImage(size: NSSize(width: size, height: size)); image.addRepresentation(bitmap); return image
    }()
    static func rgb(hue: CGFloat, saturation: CGFloat, brightness: CGFloat) -> NSColor {
        let h = hue * 6, c = brightness * saturation, x = c * (1 - abs(h.truncatingRemainder(dividingBy: 2) - 1)), m = brightness - c
        let components: (CGFloat, CGFloat, CGFloat)
        switch h { case ..<1: components = (c,x,0); case ..<2: components = (x,c,0); case ..<3: components = (0,c,x)
        case ..<4: components = (0,x,c); case ..<5: components = (x,0,c); default: components = (c,0,x) }
        return NSColor(srgbRed: components.0 + m, green: components.1 + m, blue: components.2 + m, alpha: 1)
    }
    var color: NSColor { Self.rgb(hue: hue, saturation: saturation, brightness: brightness) }
    func setColor(_ color: NSColor) {
        let color = color.usingColorSpace(.sRGB) ?? .black
        let r = color.redComponent, g = color.greenComponent, b = color.blueComponent, high = max(r, g, b), low = min(r, g, b), delta = high - low
        if delta > 0 {
            let h = high == r ? (g-b)/delta : high == g ? (b-r)/delta + 2 : (r-g)/delta + 4
            hue = (h / 6 + 1).truncatingRemainder(dividingBy: 1)
        }
        if high > 0 { saturation = delta / high }
        brightness = high; needsDisplay = true; updateAccessibility()
    }
    func setBrightness(_ value: CGFloat) { brightness = min(1, max(0, value)); changed() }
    override func draw(_ dirtyRect: NSRect) {
        Self.wheelImage.draw(in: bounds, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
        NSColor.black.withAlphaComponent(1 - brightness).setFill(); NSBezierPath(ovalIn: bounds).fill()
        let angle = hue * 2 * .pi
        let point = NSPoint(x: bounds.midX + cos(angle) * saturation * bounds.width / 2,
                            y: bounds.midY - sin(angle) * saturation * bounds.height / 2)
        let marker = NSBezierPath(ovalIn: NSRect(x: point.x - 5, y: point.y - 5, width: 10, height: 10))
        NSColor.black.setStroke(); marker.lineWidth = 4; marker.stroke(); NSColor.white.setStroke(); marker.lineWidth = 2; marker.stroke()
        if window?.firstResponder === self {
            NSColor.keyboardFocusIndicatorColor.setStroke(); let focus = NSBezierPath(ovalIn: bounds.insetBy(dx: 2, dy: 2)); focus.lineWidth = 2; focus.stroke()
        }
    }
    override func mouseDown(with event: NSEvent) { window?.makeFirstResponder(self); pick(event) }
    override func mouseDragged(with event: NSEvent) { pick(event) }
    private func pick(_ event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil), x = (p.x - bounds.midX) / (bounds.width / 2), y = (bounds.midY - p.y) / (bounds.height / 2)
        hue = (atan2(y, x) / (2 * .pi) + 1).truncatingRemainder(dividingBy: 1); saturation = min(1, hypot(x, y)); changed()
    }
    override func keyDown(with event: NSEvent) {
        let step: CGFloat = event.modifierFlags.contains(.shift) ? 0.05 : 0.01
        switch event.keyCode {
        case 123: hue = (hue - step + 1).truncatingRemainder(dividingBy: 1)
        case 124: hue = (hue + step).truncatingRemainder(dividingBy: 1)
        case 125: saturation = max(0, saturation - step)
        case 126: saturation = min(1, saturation + step)
        default: super.keyDown(with: event); return
        }
        changed()
    }
    override func accessibilityPerformIncrement() -> Bool { hue = (hue + 0.02).truncatingRemainder(dividingBy: 1); changed(); return true }
    override func accessibilityPerformDecrement() -> Bool { hue = (hue + 0.98).truncatingRemainder(dividingBy: 1); changed(); return true }
    private func changed() { needsDisplay = true; updateAccessibility(); sendAction(action, to: target) }
    private func updateAccessibility() { setAccessibilityValue("色相 \(Int((hue*360).rounded())) 度，饱和度 \(Int((saturation*100).rounded()))%，明暗 \(Int((brightness*100).rounded()))%") }
}

final class ArcColorEditor: NSWindowController, NSWindowDelegate, NSTextFieldDelegate {
    var preview: ((CapsuleArcStyle?) -> Void)?
    var save: ((CapsuleArcStyle) -> String?)?
    var closed: (() -> Void)?
    private(set) var draft: CapsuleArcStyle
    private let original: CapsuleArcStyle
    private var theme: CapsuleTheme
    private var highSelected = false
    private var finished = false
    private var rendering = false
    private var invalidFields = Set<Int>()
    private var feedback: String?
    private let previewState = CapsuleState()
    private let previewContainer = ArcPreviewContainer()
    private let mode = NSPopUpButton()
    private let low = ArcColorSwatch(), high = ArcColorSwatch(), strip = ArcColorSwatch()
    private let lowField = NSTextField(), highField = NSTextField()
    private let lowLabel = NSTextField(labelWithString: "低额度 · 0%"), highLabel = NSTextField(labelWithString: "满额度 · 100%")
    private let swap = NSButton(), wheel = ArcColorWheel(), brightness = NSSlider(value: 1, minValue: 0, maxValue: 1, target: nil, action: nil)
    private let fraction = NSSlider(value: 68, minValue: 0, maxValue: 100, target: nil, action: nil)
    private let quotaLabel = NSTextField(labelWithString: "预览额度 68%"), targetLabel = NSTextField(labelWithString: ""), brightnessLabel = NSTextField(labelWithString: "")
    private let hint = NSTextField(wrappingLabelWithString: ""), notice = NSTextField(wrappingLabelWithString: "")
    private let apply = NSButton(), reset = NSButton()
    private var presets: [ArcColorSwatch] = []
    private static let solidPresets = ["#35DE94", "#28B8CE", "#3B82F6", "#A78BFA", "#F472B6", "#E9BC60"]
    private static let gradientPresets = [("#35DE94", "#3B82F6"), ("#28B8CE", "#A78BFA"), ("#A78BFA", "#F472B6"), ("#E9BC60", "#EA6565"), ("#3B82F6", "#A78BFA"), ("#009E68", "#A5D76D")]

    init(style: CapsuleArcStyle, theme: CapsuleTheme, fraction: CGFloat?, warning: String? = nil) {
        self.draft = style; self.original = style; self.theme = theme; self.feedback = warning
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 460, height: 674), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "弧线配色"; window.isReleasedWhenClosed = false
        super.init(window: window); window.delegate = self
        let root = ArcEditorRoot(frame: NSRect(x: 0, y: 0, width: 460, height: 674)); window.contentView = root
        func add(_ view: NSView, _ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat) { view.frame = NSRect(x: x, y: y, width: w, height: h); root.addSubview(view) }
        func text(_ value: String, _ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat = 20) {
            let label = NSTextField(labelWithString: value); label.font = .systemFont(ofSize: 12); label.textColor = .secondaryLabelColor; add(label, x, y, w, h)
        }
        func button(_ control: NSButton, _ title: String, _ action: Selector, _ id: String) {
            control.title = title; control.bezelStyle = .rounded; control.target = self; control.action = action; control.identifier = .init(id); control.setAccessibilityLabel(title)
        }
        previewState.quotaFraction = Double(fraction ?? 0.68); previewState.arcStyle = style; previewState.theme = theme
        self.fraction.doubleValue = floor(Double(fraction ?? 0.68) * 100 + 1e-9)
        let orb = CapsuleSurface(state: previewState, managesWindowShadow: false)
        previewContainer.setAccessibilityElement(true); previewContainer.setAccessibilityRole(.image); previewContainer.setAccessibilityLabel("折叠浮窗预览")
        orb.frame = NSRect(origin: .zero, size: CapsuleSurface.small); previewContainer.addSubview(orb); add(previewContainer, 22, 12, 76, 76)
        text("折叠浮窗 · 实时预览", 120, 17, 300)
        add(quotaLabel, 120, 42, 300, 19); add(self.fraction, 118, 64, 318, 24)
        self.fraction.target = self; self.fraction.action = #selector(quotaChanged); self.fraction.isContinuous = true; self.fraction.setAccessibilityLabel("预览剩余额度")
        text("配色方式", 22, 109, 100)
        mode.addItems(withTitles: ["单色", "渐变"]); mode.target = self; mode.action = #selector(modeChanged); mode.identifier = .init("arcMode"); mode.setAccessibilityLabel("配色方式"); add(mode, 270, 101, 172, 32)
        add(lowLabel, 22, 148, 180, 19); add(highLabel, 264, 148, 174, 19)
        for (index, swatch, field, x) in [(0, low, lowField, CGFloat(20)), (1, high, highField, CGFloat(262))] {
            swatch.tag = index; swatch.target = self; swatch.action = #selector(selectEndpoint(_:)); swatch.setButtonType(.momentaryPushIn); swatch.identifier = .init(index == 0 ? "arcLow" : "arcHigh")
            add(swatch, x, 168, 38, 38)
            field.delegate = self; field.tag = index; field.font = .monospacedSystemFont(ofSize: 12, weight: .regular); field.identifier = .init(index == 0 ? "arcLowHex" : "arcHighHex")
            field.setAccessibilityLabel(index == 0 ? "低额度颜色十六进制值" : "满额度颜色十六进制值"); add(field, x + 44, 174, 130, 25)
        }
        button(swap, "⇄", #selector(swapColors), "arcSwap"); swap.setAccessibilityLabel("交换低额度和满额度颜色"); add(swap, 209, 173, 43, 29)
        strip.isEnabled = false; add(strip, 20, 213, 420, 15); strip.setAccessibilityLabel("低额度到满额度的渐变")
        add(targetLabel, 22, 242, 265, 20)
        wheel.target = self; wheel.action = #selector(wheelChanged); wheel.setAccessibilityElement(true); wheel.setAccessibilityRole(.slider)
        wheel.setAccessibilityLabel("色轮，左右键调整色相，上下键调整饱和度"); wheel.identifier = .init("arcWheel"); add(wheel, 36, 275, 200, 200)
        text("常用配色", 310, 242, 125)
        for index in 0..<6 {
            let swatch = ArcColorSwatch(); swatch.tag = index; swatch.target = self; swatch.action = #selector(selectPreset(_:)); swatch.setButtonType(.momentaryPushIn)
            add(swatch, 306 + CGFloat(index % 2) * 62, 273 + CGFloat(index / 2) * 51, 54, 39); presets.append(swatch)
        }
        add(brightnessLabel, 22, 495, 410, 20); brightness.target = self; brightness.action = #selector(brightnessChanged); brightness.isContinuous = true
        brightness.identifier = .init("arcBrightness"); brightness.setAccessibilityLabel("颜色明暗"); add(brightness, 20, 519, 420, 24)
        hint.font = .systemFont(ofSize: 12); hint.textColor = .secondaryLabelColor; add(hint, 22, 553, 416, 36)
        notice.font = .systemFont(ofSize: 12); notice.textColor = .secondaryLabelColor; notice.setAccessibilityRole(.staticText); add(notice, 22, 592, 416, 34)
        button(reset, "重置默认颜色", #selector(resetColors), "arcReset"); add(reset, 16, 632, 132, 30)
        let cancel = NSButton(); button(cancel, "取消", #selector(cancelEditing), "arcCancel"); cancel.keyEquivalent = "\u{1b}"; add(cancel, 277, 632, 75, 30)
        button(apply, "应用", #selector(applyColors), "arcApply"); apply.keyEquivalent = "\r"; add(apply, 361, 632, 82, 30)
        render()
        window.center()
    }
    required init?(coder: NSCoder) { fatalError() }
    func updateTheme(_ value: CapsuleTheme) { theme = value; render() }
    private func selectedColor() -> NSColor { draft.mode == .solid ? draft.solidColor(theme: theme) : draft.endpoint(high: highSelected, theme: theme) }
    private func render(keepWheel: Bool = false) {
        rendering = true; defer { rendering = false }
        window?.appearance = NSAppearance(named: theme == .light ? .aqua : .darkAqua)
        let gradient = draft.mode == .gradient
        if !gradient { highSelected = false }
        mode.selectItem(at: gradient ? 1 : 0)
        high.isHidden = !gradient; highField.isHidden = !gradient; highLabel.isHidden = !gradient; swap.isHidden = !gradient; strip.isHidden = !gradient
        lowLabel.stringValue = gradient ? "低额度 · 0%" : "颜色"
        low.setAccessibilityLabel(gradient ? "选择低额度颜色" : "选择单色"); high.setAccessibilityLabel("选择满额度颜色")
        lowField.setAccessibilityLabel(gradient ? "低额度颜色十六进制值" : "单色十六进制值")
        low.colors = [gradient ? draft.endpoint(high: false, theme: theme) : draft.solidColor(theme: theme)]; high.colors = [draft.endpoint(high: true, theme: theme)]
        low.state = highSelected ? .off : .on; high.state = highSelected ? .on : .off; low.needsDisplay = true; high.needsDisplay = true
        if !invalidFields.contains(0) { lowField.stringValue = CapsuleArcStyle.hex(low.colors[0]) }
        if !invalidFields.contains(1) { highField.stringValue = CapsuleArcStyle.hex(high.colors[0]) }
        // Sampling the same color function also preserves the built-in stops.
        strip.colors = (0...100).map { draft.color(for: CGFloat($0) / 100, theme: theme) }
        targetLabel.stringValue = gradient ? (highSelected ? "正在调节满额度颜色" : "正在调节低额度颜色") : "正在调节单色"
        if !keepWheel { wheel.setColor(selectedColor()) }
        brightness.doubleValue = Double(wheel.brightness); brightnessLabel.stringValue = "明暗  \(Int((wheel.brightness * 100).rounded()))%"
        for (index, swatch) in presets.enumerated() {
            let pair = Self.gradientPresets[index]
            swatch.colors = gradient ? [CapsuleArcStyle.parse(pair.0)!, CapsuleArcStyle.parse(pair.1)!] : [CapsuleArcStyle.parse(Self.solidPresets[index])!]
            swatch.setAccessibilityLabel(gradient ? "渐变 \(pair.0) 到 \(pair.1)" : "单色 \(Self.solidPresets[index])")
        }
        hint.stringValue = gradient ? "额度由低到高时，整段弧线在颜色间逐渐过渡。" : "整段弧线保持同色，默认使用满额度时的颜色。"
        notice.stringValue = invalidFields.isEmpty ? (feedback ?? (gradient ? (draft.isBuiltinGradient ? "内置渐变" : "自定义渐变") : (draft.solidHex == nil ? "满额度默认色" : "自定义单色"))) : "请输入 6 位十六进制颜色，如 #35DE94。"
        notice.textColor = invalidFields.isEmpty && feedback == nil ? .secondaryLabelColor : .systemOrange
        apply.isEnabled = invalidFields.isEmpty
        previewState.theme = theme; previewState.arcStyle = draft; quotaChanged()
    }
    private func changed(keepWheel: Bool = false) { feedback = nil; render(keepWheel: keepWheel); preview?(draft) }
    @objc private func quotaChanged() {
        // The orb truncates real quota to whole percentages. Quantize the
        // synthetic slider value so its label and the orb always agree.
        let percent = fraction.doubleValue.rounded()
        fraction.doubleValue = percent
        previewState.quotaFraction = percent / 100; quotaLabel.stringValue = "预览额度  \(Int(percent))%"
        previewContainer.setAccessibilityValue("剩余额度 \(Int(percent))%，\(draft.mode == .solid ? "单色" : "渐变")")
    }
    @objc private func modeChanged() {
        // Other-mode colors stay in the draft, so toggling never loses a choice.
        invalidFields.removeAll(); draft.mode = mode.indexOfSelectedItem == 0 ? .solid : .gradient; changed()
    }
    @objc private func selectEndpoint(_ sender: NSButton) { highSelected = sender.tag == 1; render() }
    @objc private func wheelChanged() {
        invalidFields.remove(highSelected ? 1 : 0); draft.setColor(wheel.color, high: highSelected, theme: theme); changed(keepWheel: true)
    }
    @objc private func brightnessChanged() { wheel.setBrightness(CGFloat(brightness.doubleValue)) }
    @objc private func selectPreset(_ sender: NSButton) {
        invalidFields.removeAll()
        if draft.mode == .solid { draft.solidHex = Self.solidPresets[sender.tag] }
        else { let pair = Self.gradientPresets[sender.tag]; draft.lowHex = pair.0; draft.highHex = pair.1 }
        changed()
    }
    @objc private func swapColors() {
        let low = CapsuleArcStyle.hex(draft.endpoint(high: false, theme: theme)), high = CapsuleArcStyle.hex(draft.endpoint(high: true, theme: theme))
        invalidFields.removeAll(); draft.lowHex = high; draft.highHex = low; changed()
    }
    @objc private func resetColors() { invalidFields.removeAll(); draft.resetColors(); changed() }
    func controlTextDidChange(_ notification: Notification) {
        guard !rendering, let field = notification.object as? NSTextField else { return }
        var value = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        if !value.hasPrefix("#") { value = "#" + value }
        guard let color = CapsuleArcStyle.parse(value) else { invalidFields.insert(field.tag); render(); return }
        invalidFields.remove(field.tag); highSelected = draft.mode == .gradient && field.tag == 1
        draft.setColor(color, high: highSelected, theme: theme); changed()
    }
    @objc private func applyColors() {
        guard invalidFields.isEmpty, let save = save else { return }
        if let error = save(draft) { feedback = error; render(); return }
        finished = true; preview?(nil); close()
    }
    @objc private func cancelEditing() { close() }
    func windowWillClose(_ notification: Notification) {
        if !finished { draft = original; preview?(nil) }
        closed?()
    }
}
