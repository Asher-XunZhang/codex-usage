import AppKit

/// Owns only docking and its short-lived delays. Expanded geometry stays with
/// the window controller; no periodic mouse-position sampling is installed.
final class CapsuleDocking {
    weak var panel: NSPanel?
    weak var surface: CapsuleSurface?
    let preferences: UserDefaults
    var expand: (() -> Void)?
    var collapse: ((Bool) -> Void)?
    var currentCompact: (() -> NSRect)?
    var didDropExpanded: ((NSRect, NSRect) -> Void)?
    private(set) var edge: CapsuleDockEdge?
    private(set) var compact: NSRect = .zero
    private var freeOrigin: NSPoint = .zero
    private var delay: DispatchWorkItem?
    private var animation: CapsuleAnimation?
    private var generation = 0
    private var targetTab = false
    private var awaitingMotion: NSPoint?
    private var suppressUntilExit = false
    private var suppressionPointer = NSPoint.zero
    private var observers: [NSObjectProtocol] = []
    private(set) var movingFrame = false

    init(panel: NSPanel, surface: CapsuleSurface, preferences: UserDefaults) {
        self.panel = panel; self.surface = surface; self.preferences = preferences
        compact = panel.frame; freeOrigin = compact.origin
        surface.state.autoHide = preferences.object(forKey: "capsuleAutoHide") as? Bool ?? true
        if let data = preferences.data(forKey: "capsulePositionV1"),
           let saved = try? JSONDecoder().decode(CapsuleSavedPosition.self, from: data),
           let restored = saved.restored(screens: Self.screens()) {
            compact = restored.0; edge = restored.1; freeOrigin = NSPoint(x: saved.freeX, y: saved.freeY)
            panel.setFrame(compact, display: false)
        }
        // Legacy origin migration is deliberately free, even if it touches an
        // edge. Upgrading must not hide a previously visible window immediately.
        persist()
        observers.append(NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in self?.reconcileScreen() })
        for name in [NSWorkspace.didWakeNotification, NSWorkspace.activeSpaceDidChangeNotification] {
            observers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in self?.reconcileScreen() })
        }
    }
    deinit {
        cancel()
        for observer in observers {
            NotificationCenter.default.removeObserver(observer)
            NSWorkspace.shared.notificationCenter.removeObserver(observer)
        }
    }
    static func screens() -> [CapsuleScreenGeometry] {
        NSScreen.screens.map { screen in
            let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber
            return CapsuleScreenGeometry(id: number?.stringValue ?? String(describing: screen.frame), frame: screen.frame, visibleFrame: screen.visibleFrame)
        }
    }
    private func cancel() {
        generation += 1; delay?.cancel(); delay = nil
        animation?.step = nil; animation?.stop(); animation = nil
    }
    func pauseInteraction() { cancel() }
    func prepareToHide() {
        cancel(); targetTab = false; awaitingMotion = nil; suppressUntilExit = false
        guard let panel = panel, let surface = surface else { return }
        if surface.docking > 0 { movingFrame = true; panel.setFrame(compact, display: false); movingFrame = false }
        surface.docking = 0
    }
    func handlePointer(inside: Bool) -> Bool {
        guard let surface = surface, panel?.isVisible == true else { return false }
        if surface.state.interactionActive { return true }
        if suppressUntilExit {
            // Tracking-area exits can be caused by the window shrinking under a
            // stationary pointer. In particular, the footer may overlap the orb.
            // Only physical movement outside the current shape releases a
            // deliberate collapse; an exit callback alone cannot reopen it.
            let point = NSEvent.mouseLocation
            let moved = hypot(point.x - suppressionPointer.x, point.y - suppressionPointer.y) >= 2
            if moved && !surface.containsScreenPoint(point) { suppressUntilExit = false }
            else { return true }
        }
        if targetTab || surface.docking > 0 {
            if inside, delay == nil, animation == nil {
                schedule(after: 0.12) { [weak self] in
                    guard let self = self else { return }
                    self.awaitingMotion = NSEvent.mouseLocation
                    self.transition(tab: false)
                }
            } else if !inside { delay?.cancel(); delay = nil }
            return true
        }
        if awaitingMotion != nil {
            if !inside { awaitingMotion = nil; scheduleHide() }
            return true
        }
        if inside { delay?.cancel(); delay = nil }
        else if surface.expansion == 0 { scheduleHide() }
        return false
    }
    func pointerMoved(_ point: NSPoint) {
        guard let initial = awaitingMotion, animation == nil,
              hypot(point.x - initial.x, point.y - initial.y) >= 2,
              surface?.containsScreenPoint(point) == true else { return }
        awaitingMotion = nil; explicitExpand()
    }
    func explicitExpand() {
        cancel(); suppressUntilExit = false; awaitingMotion = nil
        if surface?.docking ?? 0 > 0 { transition(tab: false, completed: { [weak self] in self?.expand?() }) }
        else { expand?() }
    }
    func manuallyCollapse() {
        cancel(); suppressUntilExit = true; suppressionPointer = NSEvent.mouseLocation
        awaitingMotion = nil; surface?.state.keepsExpanded = false
        // A previous keyboard/text interaction must not immediately reopen a
        // deliberately collapsed panel when the pointer next leaves it.
        surface?.state.keyboardInteracting = false
        panel?.resignKey(); collapse?(true)
    }
    func didCollapse() {
        if let frame = panel?.frame { compact = frame }
        persist(); scheduleHide()
    }
    func scheduleHide() {
        guard delay == nil, animation == nil, edge != nil, let surface = surface, let panel = panel,
              panel.isVisible, !(panel.isKeyWindow && surface.state.keyboardInteracting), surface.state.autoHide,
              !surface.state.interactionActive, !surface.state.keepsExpanded, surface.expansion == 0,
              !surface.containsScreenPoint(NSEvent.mouseLocation) else { return }
        schedule(after: 0.55) { [weak self] in
            guard let self = self, let surface = self.surface, self.panel?.isVisible == true, !(self.panel?.isKeyWindow == true && surface.state.keyboardInteracting),
                  !surface.state.interactionActive, !surface.state.keepsExpanded,
                  !surface.containsScreenPoint(NSEvent.mouseLocation) else { return }
            self.transition(tab: true)
        }
    }
    private func schedule(after seconds: Double, action: @escaping () -> Void) {
        let ticket = generation
        let work = DispatchWorkItem { [weak self] in
            guard let self = self, self.generation == ticket else { return }
            self.delay = nil; action()
        }
        delay = work; DispatchQueue.main.asyncAfter(deadline: .now() + seconds, execute: work)
    }
    private func transition(tab: Bool, completed: (() -> Void)? = nil) {
        guard let panel = panel, let surface = surface,
              let screen = CapsulePlacement.screen(for: compact, screens: Self.screens()) else { return }
        cancel(); let ticket = generation; targetTab = tab
        if tab { suppressUntilExit = false }
        let target = tab && edge != nil ? CapsulePlacement.tab(compact: compact, edge: edge!, work: screen.visibleFrame) : compact
        let from = panel.frame, fraction = surface.docking
        surface.dockEdge = edge?.rawValue
        let apply: (CGFloat) -> Void = { [weak self, weak panel, weak surface] progress in
            guard let self = self, self.generation == ticket, let panel = panel, let surface = surface else { return }
            let mix: (CGFloat, CGFloat) -> CGFloat = { $0 + ($1 - $0) * progress }
            self.movingFrame = true
            surface.docking = mix(fraction, tab ? 1 : 0)
            panel.setFrame(NSRect(x: mix(from.minX, target.minX), y: mix(from.minY, target.minY), width: mix(from.width, target.width), height: mix(from.height, target.height)), display: true)
            self.movingFrame = false
            if progress >= 1 { self.animation = nil; completed?() }
        }
        if NSWorkspace.shared.accessibilityDisplayShouldReduceMotion { apply(1); return }
        let animation = CapsuleAnimation(duration: tab ? 0.22 : 0.16, animationCurve: .easeInOut)
        animation.animationBlockingMode = .nonblocking; animation.frameRate = 60; animation.step = apply
        self.animation = animation; animation.start()
    }
    func finishDrag(at pointer: NSPoint) {
        cancel(); awaitingMotion = nil; suppressUntilExit = true; suppressionPointer = pointer
        let proposed = currentCompact?() ?? compact
        guard let screen = CapsulePlacement.screen(for: proposed, pointer: pointer, screens: Self.screens()) else { return }
        compact = CapsulePlacement.fitted(NSRect(origin: proposed.origin, size: CapsuleSurface.small), in: screen.visibleFrame)
        if let panel = panel, let surface = surface, surface.expansion >= 0.999, surface.docking == 0 {
            let original = panel.frame
            let placed = CapsulePlacement.fitted(original, in: screen.visibleFrame)
            compact = CapsulePlacement.fitted(proposed.offsetBy(dx: placed.minX - original.minX, dy: placed.minY - original.minY), in: screen.visibleFrame)
            freeOrigin = compact.origin
            edge = CapsulePlacement.expandedDropEdge(panel: original, pointer: pointer, screen: screen)
            if let edge = edge { compact = CapsulePlacement.docked(compact, edge: edge, work: screen.visibleFrame) }
            targetTab = false; suppressUntilExit = false
            movingFrame = true; panel.setFrame(placed, display: true); movingFrame = false
            // Keep this session's grip/direction and the user's keep-open choice.
            // The next expansion will resolve its direction again from compact.
            didDropExpanded?(compact, placed); persist(); return
        }
        freeOrigin = compact.origin
        edge = CapsulePlacement.dockingEdge(compact: compact, screen: screen)
        if let edge = edge { compact = CapsulePlacement.docked(compact, edge: edge, work: screen.visibleFrame) }
        targetTab = false; surface?.docking = 0
        let destination = compact
        collapse?(false); compact = destination
        movingFrame = true; panel?.setFrame(compact, display: true); movingFrame = false
        persist(); scheduleHide()
    }
    func toggleAutoHide() {
        guard let surface = surface else { return }
        surface.state.autoHide.toggle(); preferences.set(surface.state.autoHide, forKey: "capsuleAutoHide")
        if !surface.state.autoHide {
            cancel()
            if surface.docking > 0 { transition(tab: false) }
        } else { scheduleHide() }
    }
    private func persist() {
        guard let screen = CapsulePlacement.screen(for: compact, screens: Self.screens()) else { return }
        let saved = CapsuleSavedPosition(compact: compact, screen: screen, edge: edge, freeOrigin: freeOrigin)
        if let data = try? JSONEncoder().encode(saved) { preferences.set(data, forKey: "capsulePositionV1") }
    }
    private func reconcileScreen() {
        guard let data = preferences.data(forKey: "capsulePositionV1"), let saved = try? JSONDecoder().decode(CapsuleSavedPosition.self, from: data),
              let restored = saved.restored(screens: Self.screens()) else { return }
        prepareToHide(); collapse?(false); compact = restored.0; edge = restored.1
        movingFrame = true; panel?.setFrame(compact, display: true); movingFrame = false
        persist(); scheduleHide()
    }
}
