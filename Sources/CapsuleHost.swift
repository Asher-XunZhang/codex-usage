import AppKit

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
