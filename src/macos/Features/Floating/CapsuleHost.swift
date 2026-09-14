import AppKit

/// Hover and mouse controls remain non-activating; an explicit keyboard action
/// can grant this same panel key status without becoming the main app window.
final class CapsulePanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
    override func sendEvent(_ event: NSEvent) {
        // The native text field editor can own first responder. Escape still
        // belongs to the enclosing floating panel rather than being swallowed.
        if event.type == .keyDown, event.keyCode == 53, let host = contentView as? CapsuleHost {
            host.surface.keyDown(with: event); return
        }
        super.sendEvent(event)
    }
}

/// Both palettes share one input/drawing surface, with no material view to retain.
final class CapsuleHost: NSView {
    let surface: CapsuleSurface
    private var appliedTheme: CapsuleTheme?
    override var isFlipped: Bool { true }

    init(surface: CapsuleSurface) {
        self.surface = surface
        super.init(frame: surface.frame)
        autoresizingMask = [.width, .height]
        surface.autoresizingMask = [.width, .height]
        setAccessibilityElement(false)
        addSubview(surface)
        surface.appearanceChanged = { [weak self] in self?.updateAppearance() }
        updateAppearance()
    }
    required init?(coder: NSCoder) { fatalError() }

    private func updateAppearance() {
        let theme = surface.state.theme
        guard appliedTheme != theme else { return }
        appliedTheme = theme
        appearance = NSAppearance(named: theme == .light ? .aqua : .darkAqua)
        surface.needsDisplay = true
    }
    override func layout() {
        super.layout()
        surface.frame = bounds
    }
    override func accessibilityChildren() -> [Any]? { [surface] }
}
