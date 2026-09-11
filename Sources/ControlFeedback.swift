import AppKit

/// A passive, temporary drawing surface lets the glow extend outside a control
/// without extending its tracking or hit-test bounds. It requests no layer.
private final class ControlGlowView: NSView {
    weak var control: NSControl?
    var shapeRect = NSRect.zero
    var radius: CGFloat = 6
    var pressed = false
    override var wantsDefaultClipping: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override func accessibilityChildren() -> [Any]? { [] }

    init(control: NSControl) {
        self.control = control
        super.init(frame: .zero)
        setAccessibilityElement(false)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    private func exclude(_ rect: NSRect, radius: CGFloat = 0) {
        guard bounds.intersects(rect) else { return }
        let mask = NSBezierPath(rect: bounds)
        mask.append(NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius))
        mask.windingRule = .evenOdd; mask.addClip()
    }
    private func softenGlow(over rect: NSRect, context: CGContext) {
        guard bounds.intersects(rect), rect.width > 0, rect.height > 0 else { return }
        // Let ambient light cross a neighbour's edge, then fade before its
        // content. Erase only the isolated glow group, never the window pixels.
        let feather = min(8, min(rect.width, rect.height) / 4)
        context.saveGState(); defer { context.restoreGState() }
        context.setBlendMode(.destinationOut)
        var previous: CGFloat = 0
        for step in 1...16 {
            let t = CGFloat(step) / 16
            let opacity = t * t * (3 - 2 * t)
            let alpha = (opacity - previous) / (1 - previous)
            NSColor.black.withAlphaComponent(alpha).setFill()
            let inset = feather * t
            let inner = rect.insetBy(dx: inset, dy: inset)
            NSBezierPath(roundedRect: inner, xRadius: max(0, 6 - inset), yRadius: max(0, 6 - inset)).fill()
            previous = opacity
        }
    }
    override func draw(_ dirtyRect: NSRect) {
        guard let control = control, let root = superview, let window = control.window,
              control.isEnabled, !control.isHiddenOrHasHiddenAncestor else { return }
        let point = control.convert(window.mouseLocationOutsideOfEventStream, from: nil)
        guard control.bounds.contains(point), control.visibleRect.contains(point) else { return }
        let shape = control.convert(shapeRect, to: self)
        let dark = control.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
        let color = (control as? NSButton)?.contentTintColor ?? NSColor(srgbRed: dark ? 0.224 : 0.031,
            green: dark ? 0.914 : 0.675, blue: dark ? 0.718 : 0.482, alpha: 1)
        let scale = min(1, max(0.55, shape.height / 56))
        let blur = 25 * scale * (pressed ? 0.70 : 1)
        let strength: CGFloat = dark ? 0.79 : 0.59
        NSGraphicsContext.saveGraphicsState(); defer { NSGraphicsContext.restoreGraphicsState() }
        NSBezierPath(rect: bounds).addClip()
        // A scrolling button must not glow over the fixed page navigation.
        var ancestor = control.superview
        while let view = ancestor, view !== root {
            if view is NSClipView { NSBezierPath(rect: view.convert(view.bounds, to: self)).addClip() }
            ancestor = view.superview
        }
        // A segmented control is one glow shape. Keep its entire face intact,
        // including the unselected segments and the separators between them.
        exclude(shape, radius: radius)
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        context.beginTransparencyLayer(auxiliaryInfo: nil)
        let path = NSBezierPath(roundedRect: shape, xRadius: radius, yRadius: radius)
        for (width, alpha) in [(blur, strength), (blur * 0.30, strength * 0.65)] {
            NSGraphicsContext.saveGraphicsState()
            let shadow = NSShadow(); shadow.shadowColor = color.withAlphaComponent(alpha)
            shadow.shadowBlurRadius = width; shadow.shadowOffset = .zero; shadow.set()
            color.setFill(); path.fill()
            NSGraphicsContext.restoreGraphicsState()
        }
        color.withAlphaComponent(dark ? 0.88 : 0.72).setStroke()
        path.lineWidth = 1; path.stroke()
        func protectContent(in view: NSView) {
            guard view !== self, view !== control, !view.isHiddenOrHasHiddenAncestor else { return }
            if view is NSControl {
                softenGlow(over: view.convert(view.bounds, to: self), context: context)
                return
            }
            for child in view.subviews { protectContent(in: child) }
        }
        protectContent(in: root)
        context.endTransparencyLayer()
    }
}

/// Event-driven state only. The control remains clipped and interactive only
/// within its bounds; the separate glow exists only while it is active.
private final class ControlFeedback {
    static let accent = NSColor(calibratedRed: 0.04, green: 0.59, blue: 0.49, alpha: 1)
    weak var control: NSControl?
    private var tracking: NSTrackingArea?
    private var glow: ControlGlowView?
    private var customRect: NSRect?
    private var radius: CGFloat = 6
    private(set) var hovered = false
    var pressed = false {
        didSet {
            if pressed != oldValue { control?.needsDisplay = true; updateGlow(pressed: pressed) }
        }
    }

    init(_ control: NSControl) {
        self.control = control
        // Keep inVisibleRect tracking inside the control on macOS 14 and later.
        control.clipsToBounds = true
    }
    func track() {
        guard let control = control, control.window != nil, tracking == nil else { return }
        let area = NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: control, userInfo: nil)
        tracking = area; control.addTrackingArea(area)
    }
    private func removeGlow() {
        guard let view = glow else { return }
        let parent = view.superview, oldFrame = view.frame
        view.removeFromSuperview(); glow = nil
        parent?.setNeedsDisplay(oldFrame)
    }
    private func updateGlow(pressed appearancePressed: Bool? = nil) {
        guard let control = control, control.isEnabled, hovered || pressed,
              !control.isHiddenOrHasHiddenAncestor, !control.visibleRect.isEmpty,
              let root = control.window?.contentView, root !== control else { removeGlow(); return }
        let shape = customRect ?? control.bounds.insetBy(dx: 3, dy: 3)
        let padding = ceil(30 * min(1, max(0.55, shape.height / 56)))
        let frame = control.convert(shape, to: root).insetBy(dx: -padding, dy: -padding).intersection(root.bounds)
        guard !frame.isEmpty else { removeGlow(); return }
        let view = glow ?? ControlGlowView(control: control)
        var changed = false
        if view.frame != frame {
            view.superview?.setNeedsDisplay(view.frame)
            view.frame = frame; changed = true
        }
        if view.shapeRect != shape { view.shapeRect = shape; changed = true }
        if view.radius != radius { view.radius = radius; changed = true }
        if let value = appearancePressed, view.pressed != value { view.pressed = value; changed = true }
        if view.superview !== root {
            view.removeFromSuperview(); root.addSubview(view, positioned: .above, relativeTo: nil)
            changed = true
        }
        glow = view
        if changed { view.needsDisplay = true }
    }
    func hover(_ value: Bool) {
        let value = value && control?.isEnabled == true
        guard value != hovered else { return }
        hovered = value; control?.needsDisplay = true; updateGlow()
    }
    func syncPointer() {
        guard let control = control, let window = control.window, !control.isHiddenOrHasHiddenAncestor else { hover(false); removeGlow(); return }
        let point = control.convert(window.mouseLocationOutsideOfEventStream, from: nil)
        hover(control.bounds.contains(point) && control.visibleRect.contains(point))
        updateGlow()
    }
    func appearanceChanged() { glow?.needsDisplay = true }
    func reset(detach: Bool = false) {
        hover(false); pressed = false; removeGlow()
        if detach, let area = tracking { control?.removeTrackingArea(area); tracking = nil }
    }
    func draw(pressed: Bool, rect: NSRect? = nil, radius: CGFloat = 6, fillPressedFace: Bool = true) {
        customRect = rect; self.radius = radius
        updateGlow(pressed: pressed)
        guard let control = control, control.isEnabled, pressed, fillPressedFace else { return }
        let dark = control.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
        let color = (control as? NSButton)?.contentTintColor ?? Self.accent
        let shape = NSBezierPath(roundedRect: rect ?? control.bounds.insetBy(dx: 3, dy: 3), xRadius: radius, yRadius: radius)
        NSGraphicsContext.saveGraphicsState(); defer { NSGraphicsContext.restoreGraphicsState() }
        NSBezierPath(rect: control.bounds).addClip()
        color.withAlphaComponent(dark ? 0.29 : 0.19).setFill(); shape.fill()
    }
}

class FeedbackButton: NSButton {
    private lazy var feedback = ControlFeedback(self)
    override var wantsDefaultClipping: Bool { true }
    override var isEnabled: Bool { didSet { if !isEnabled { feedback.reset() } else { feedback.syncPointer() } } }
    override func updateTrackingAreas() { super.updateTrackingAreas(); feedback.track(); feedback.syncPointer() }
    override func viewDidMoveToWindow() { super.viewDidMoveToWindow(); feedback.track(); feedback.syncPointer() }
    override func viewWillMove(toWindow newWindow: NSWindow?) { if newWindow == nil { feedback.reset(detach: true) }; super.viewWillMove(toWindow: newWindow) }
    override func mouseEntered(with event: NSEvent) { feedback.syncPointer() }
    override func mouseExited(with event: NSEvent) { feedback.hover(false) }
    override func viewDidChangeEffectiveAppearance() { super.viewDidChangeEffectiveAppearance(); feedback.appearanceChanged() }
    override func viewDidHide() { super.viewDidHide(); feedback.reset() }
    override func viewDidUnhide() { super.viewDidUnhide(); feedback.syncPointer() }
    override func mouseDown(with event: NSEvent) {
        guard isEnabled else { return }
        defer { feedback.syncPointer() }
        super.mouseDown(with: event)
    }
    override func draw(_ dirtyRect: NSRect) { super.draw(dirtyRect); drawInteractionFeedback() }
    func drawInteractionFeedback(in rect: NSRect? = nil, radius: CGFloat = 6) {
        feedback.draw(pressed: cell?.isHighlighted == true, rect: rect, radius: radius)
    }
}

final class FeedbackPopUpButton: NSPopUpButton {
    private lazy var feedback = ControlFeedback(self)
    override var wantsDefaultClipping: Bool { true }
    override var isEnabled: Bool { didSet { if !isEnabled { feedback.reset() } else { feedback.syncPointer() } } }
    override func updateTrackingAreas() { super.updateTrackingAreas(); feedback.track(); feedback.syncPointer() }
    override func viewDidMoveToWindow() { super.viewDidMoveToWindow(); feedback.track(); feedback.syncPointer() }
    override func viewWillMove(toWindow newWindow: NSWindow?) { if newWindow == nil { feedback.reset(detach: true) }; super.viewWillMove(toWindow: newWindow) }
    override func mouseEntered(with event: NSEvent) { feedback.syncPointer() }
    override func mouseExited(with event: NSEvent) { feedback.hover(false) }
    override func viewDidChangeEffectiveAppearance() { super.viewDidChangeEffectiveAppearance(); feedback.appearanceChanged() }
    override func viewDidHide() { super.viewDidHide(); feedback.reset() }
    override func viewDidUnhide() { super.viewDidUnhide(); feedback.syncPointer() }
    override func mouseDown(with event: NSEvent) {
        guard isEnabled else { return }
        feedback.pressed = true
        defer { feedback.pressed = false; feedback.syncPointer() }
        super.mouseDown(with: event)
    }
    override func draw(_ dirtyRect: NSRect) { super.draw(dirtyRect); feedback.draw(pressed: feedback.pressed || cell?.isHighlighted == true) }
}

final class FeedbackSegmentedControl: NSSegmentedControl {
    private lazy var feedback = ControlFeedback(self)
    override var wantsDefaultClipping: Bool { true }
    override var isEnabled: Bool { didSet { if !isEnabled { feedback.reset() } else { feedback.syncPointer() } } }
    override func updateTrackingAreas() { super.updateTrackingAreas(); feedback.track(); feedback.syncPointer() }
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow(); selectedSegmentBezelColor = ControlFeedback.accent
        feedback.track(); feedback.syncPointer()
    }
    override func viewWillMove(toWindow newWindow: NSWindow?) { if newWindow == nil { feedback.reset(detach: true) }; super.viewWillMove(toWindow: newWindow) }
    override func mouseEntered(with event: NSEvent) { feedback.syncPointer() }
    override func mouseExited(with event: NSEvent) { feedback.hover(false) }
    override func viewDidChangeEffectiveAppearance() { super.viewDidChangeEffectiveAppearance(); feedback.appearanceChanged() }
    override func viewDidHide() { super.viewDidHide(); feedback.reset() }
    override func viewDidUnhide() { super.viewDidUnhide(); feedback.syncPointer() }
    override func mouseDown(with event: NSEvent) {
        guard isEnabled else { return }
        feedback.pressed = true
        defer { feedback.pressed = false; feedback.syncPointer() }
        super.mouseDown(with: event)
    }
    override func draw(_ dirtyRect: NSRect) {
        // AppKit highlights only the pressed segment; the shared feedback owns
        // the whole outer outline and must not tint its unpressed neighbours.
        super.draw(dirtyRect)
        feedback.draw(pressed: feedback.pressed && feedback.hovered, radius: 7, fillPressedFace: false)
    }
}
