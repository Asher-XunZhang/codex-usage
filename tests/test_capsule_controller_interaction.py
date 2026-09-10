"""Exercise actual capsule controller state machines without moving the pointer/UI.

Window, animation and mouse-event dependencies are deterministic doubles; the
production controller methods themselves are compiled unchanged.
"""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


def declaration(source, marker):
    start = source.index(marker)
    opening = source.index('{', start)
    depth, end = 1, opening + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end] + '\n'


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class CapsuleControllerInteractionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        root = Path(__file__).parents[1]
        main_source = (root / 'Sources/Main.swift').read_text()
        main_methods = ''.join(declaration(main_source, marker) for marker in [
            '    func hideFloating()', '    func stopFloatMouseMonitoring()',
            '    func checkFloatPointer()', '    func toggleFloatKeepsExpanded()',
            '    func floatInteractionChanged(', '    func resetFloatInteraction()',
            '    func startFloatMouseMonitoring()', '    func setFloatExpanded(',
            '    func saveCapsuleOrigin()', '    func windowDidMove(',
        ]).replace('UserDefaults.standard', 'preferences').replace('usagePreferences', 'preferences')
        fixture = r'''import AppKit
let isMainWindowProcess = false
let suite = "local.codex-usage.test-capsule-controller." + UUID().uuidString
let preferences = UserDefaults(suiteName: suite)!
defer { preferences.removePersistentDomain(forName: suite) }
func check(_ condition: @autoclosure () -> Bool, _ reason: String) { precondition(condition(), reason) }
final class CapsuleState {
    var pointerPressed = false, menuPresented = false, keepsExpanded = false
    var pinned = false
    var interactionActive: Bool { pointerPressed || menuPresented }
}
final class CapsuleSurface {
    static let small = NSSize(width: 76, height: 76), large = NSSize(width: 336, height: 366)
    let state: CapsuleState
    var expansion: CGFloat = 0, cancelCount = 0
    var interactionChanged: ((Bool) -> Void)?
    var hover: ((Bool) -> Void)?, action: ((String) -> Void)?
    var containsPoint: ((NSPoint) -> Bool)?
    init(_ state: CapsuleState) { self.state = state }
    convenience init(state: CapsuleState) { self.init(state) }
    func containsScreenPoint(_ point: NSPoint) -> Bool { containsPoint?(point) ?? false }
    func cancelInteraction() {
        cancelCount += 1; state.pointerPressed = false; state.menuPresented = false
        interactionChanged?(false)
    }
}
final class CapsuleHost: NSObject { init(surface: CapsuleSurface) {} }
final class CapsuleAnimation {
    enum Curve { case easeInOut }; enum Blocking { case nonblocking }
    static var created = 0
    var step: ((CGFloat) -> Void)?, isAnimating = false, frameRate = 0.0
    var animationBlockingMode = Blocking.nonblocking
    init(duration: TimeInterval, animationCurve: Curve) { Self.created += 1 }
    func start() { isAnimating = true }
    func stop() { isAnimating = false }
    func advance(_ value: CGFloat) { guard isAnimating else { return }; step?(value); if value == 1 { isAnimating = false } }
}
final class NSScreen {
    static let main: NSScreen? = NSScreen()
    static var screens: [NSScreen] { [main!] }
    var visibleFrame = NSRect(x: 0, y: 0, width: 1600, height: 1000)
}
final class NSWindow: NSObject {
    static let willCloseNotification = Notification.Name("fixture-window-will-close")
    struct StyleMask: OptionSet {
        let rawValue: Int
        static let borderless = Self(rawValue: 1), nonactivatingPanel = Self(rawValue: 2)
    }
    struct CollectionBehavior: OptionSet {
        let rawValue: Int
        static let canJoinAllSpaces = Self(rawValue: 1), fullScreenAuxiliary = Self(rawValue: 2)
    }
    enum Backing { case buffered }; enum Level { case floating, normal }
    var title = "", isFloatingPanel = false, hidesOnDeactivate = false, becomesKeyOnlyIfNeeded = false
    var isReleasedWhenClosed = false, isMovableByWindowBackground = false, acceptsMouseMovedEvents = false
    var isOpaque = false, hasShadow = false, backgroundColor = NSColor.clear
    var collectionBehavior: CollectionBehavior = [], level = Level.normal
    var frame = NSRect(x: 600, y: 600, width: 76, height: 76), isVisible = true
    var screen = NSScreen.main, delegate: AnyObject?, contentView: AnyObject?
    var moves = 0, didMove: ((NSWindow) -> Void)?
    override init() { super.init() }
    init(contentRect: NSRect, styleMask: StyleMask, backing: Backing, defer: Bool) { super.init(); frame = contentRect }
    func setFrame(_ frame: NSRect, display: Bool) { self.frame = frame; moves += 1; didMove?(self) }
    func setFrameOrigin(_ origin: NSPoint) { frame.origin = origin }
    func orderFrontRegardless() { isVisible = true }
    func orderOut(_ sender: Any?) { isVisible = false }
    func close() { isVisible = false }
}
typealias NSPanel = NSWindow
final class NSWorkspace {
    static let shared = NSWorkspace()
    var accessibilityDisplayShouldReduceMotion = false
}
final class NSEvent {
    struct EventTypeMask: OptionSet {
        let rawValue: Int
        static let mouseMoved = Self(rawValue: 1), leftMouseDragged = Self(rawValue: 2), rightMouseDragged = Self(rawValue: 4)
    }
    static var mouseLocation = NSPoint(x: 638, y: 638), nextMonitor = 0
    static var globalUnavailable = false
    static var monitors = Set<Int>()
    static func addGlobalMonitorForEvents(matching: EventTypeMask, handler: @escaping (NSEvent) -> Void) -> Any? {
        if globalUnavailable { return nil }
        nextMonitor += 1; monitors.insert(nextMonitor); return nextMonitor
    }
    static func addLocalMonitorForEvents(matching: EventTypeMask, handler: @escaping (NSEvent) -> NSEvent?) -> Any? {
        nextMonitor += 1; monitors.insert(nextMonitor); return nextMonitor
    }
    static func removeMonitor(_ value: Any) { monitors.remove(value as! Int) }
}
final class FixtureButton { var title = "" }
final class FixtureRequest { func cancel() {} }
protocol Driver: AnyObject {
    var testState: CapsuleState { get }
    var testWindow: NSWindow { get }
    var testSurface: CapsuleSurface { get }
    var testAnimation: CapsuleAnimation? { get }
    var testAnchor: NSPoint? { get }
    var testTarget: Bool { get }
    func expand(_ value: Bool)
    func pointerChanged()
    func interaction(_ active: Bool)
    func toggleHold()
    func hideForTest()
}
final class MainFixture: NSObject, Driver {
    let capsuleState = CapsuleState()
    var floating: NSWindow? = NSWindow(), capsule: CapsuleSurface?, floatAnimation: CapsuleAnimation?
    var floatAnchor: NSPoint?, floatExpanded = false, floatMovingFrame = false, floatResettingInteraction = false
    var floatCollapse: DispatchWorkItem?, floatMouseMonitor: Any?, floatLocalMouseMonitor: Any?
    var floatingButton: FixtureButton? = FixtureButton(), floatingRequest: FixtureRequest?, floatingRequestID = 0
    var window: NSWindow?, trayOnly = true
    var dashboard: NSObject?
    var mainWindowOpen = false
    let floatingChoices = FixtureRequest()
    func sendHost(_ action: String) { fatalError("The host must not forward its own window actions") }
    func publishHostState() {}
    override init() {
        super.init(); capsule = CapsuleSurface(capsuleState)
        capsule?.containsPoint = { [weak self] in self?.floating?.frame.contains($0) ?? false }
        capsule?.interactionChanged = { [weak self] in self?.floatInteractionChanged($0) }
        floating?.didMove = { [weak self] window in self?.windowDidMove(Notification(name: Notification.Name("moved"), object: window)) }
    }
    var testState: CapsuleState { capsuleState }; var testWindow: NSWindow { floating! }
    var testSurface: CapsuleSurface { capsule! }; var testAnimation: CapsuleAnimation? { floatAnimation }
    var testAnchor: NSPoint? { floatAnchor }; var testTarget: Bool { floatExpanded }
    func expand(_ value: Bool) { setFloatExpanded(value) }
    func pointerChanged() { checkFloatPointer() }
    func interaction(_ active: Bool) { floatInteractionChanged(active) }
    func toggleHold() { toggleFloatKeepsExpanded() }
    func hideForTest() { hideFloating() }
''' + main_methods + r'''
}
func inside(_ driver: Driver) { NSEvent.mouseLocation = NSPoint(x: driver.testWindow.frame.midX, y: driver.testWindow.frame.midY) }
func outside() { NSEvent.mouseLocation = NSPoint(x: -1000, y: -1000) }
for driver: Driver in [MainFixture()] {
    NSEvent.monitors = []; NSEvent.globalUnavailable = false; CapsuleAnimation.created = 0; inside(driver)
    switch CommandLine.arguments[1] {
    case "partial-resume":
        driver.expand(true)
        let opening = driver.testAnimation!
        for _ in 0..<40 { driver.pointerChanged() }
        check(driver.testAnimation === opening && CapsuleAnimation.created == 1, "Same-target mouseMoved must not restart a running animation")
        opening.advance(0.4)
        let partial = driver.testWindow.frame, fraction = driver.testSurface.expansion
        let originalAnchor = driver.testAnchor
        check(originalAnchor == NSPoint(x: 676, y: 676), "Animation setFrame notifications must not change the fixed anchor")
        driver.testState.pointerPressed = true; driver.interaction(true)
        check(driver.testAnimation == nil && !opening.isAnimating && opening.step == nil && NSEvent.monitors.isEmpty, "Press pauses animation and monitors without retaining stale callbacks")
        outside(); driver.pointerChanged(); driver.expand(false); opening.advance(1)
        check(driver.testWindow.frame == partial && driver.testSurface.expansion == fraction, "No hover or animation frame may move a pressed window")
        inside(driver); driver.testState.pointerPressed = false; driver.interaction(false)
        let resumed = driver.testAnimation!
        check(resumed !== opening && driver.testTarget && driver.testSurface.expansion == fraction, "An interrupted expansion must resume even when its target boolean is unchanged")
        resumed.advance(1); outside(); driver.pointerChanged()
        driver.testAnimation!.advance(0.5)
        let closingFraction = driver.testSurface.expansion
        driver.testState.pointerPressed = true; driver.interaction(true)
        driver.testState.pointerPressed = false; driver.interaction(false)
        check(driver.testAnimation != nil && !driver.testTarget && driver.testSurface.expansion == closingFraction, "An interrupted collapse must also resume from the partial frame")
        driver.testAnimation!.advance(1)
        check(driver.testSurface.expansion == 0 && driver.testAnimation == nil && NSEvent.monitors.isEmpty, "Finished collapse must release animation and pointer monitors")
    case "drag-and-menu":
        driver.expand(true); driver.testAnimation!.advance(0.35)
        driver.testState.pointerPressed = true; driver.interaction(true)
        let old = driver.testWindow.frame
        let dragged = NSRect(x: old.minX + 110, y: old.minY - 90, width: old.width, height: old.height)
        driver.testWindow.setFrame(dragged, display: true)
        check(driver.testAnchor == NSPoint(x: dragged.maxX, y: dragged.maxY), "Dragging a paused partial frame must update its anchor")
        outside(); driver.pointerChanged()
        check(driver.testWindow.frame == dragged && driver.testAnimation == nil, "Pointer leave during drag must not collapse")
        inside(driver); driver.testState.pointerPressed = false; driver.interaction(false)
        driver.testAnimation!.advance(1)
        check(driver.testWindow.frame.maxX == dragged.maxX && driver.testWindow.frame.maxY == dragged.maxY, "Resumed animation must stay anchored at the dragged position")
        driver.testState.menuPresented = true; driver.interaction(true)
        let held = driver.testWindow.frame
        outside(); driver.pointerChanged(); driver.expand(false)
        check(driver.testWindow.frame == held && driver.testAnimation == nil && NSEvent.monitors.isEmpty, "Native menu tracking must freeze geometry and monitors")
        driver.testState.menuPresented = false; driver.interaction(false)
        check(driver.testAnimation != nil && !driver.testTarget, "Closing a menu restores actual-pointer behavior")
    case "manual-hold":
        outside(); driver.toggleHold(); driver.testAnimation!.advance(1)
        check(driver.testState.keepsExpanded && driver.testSurface.expansion == 1 && driver.testAnimation == nil, "Manual keep-open must survive animation completion with pointer outside")
        let count = CapsuleAnimation.created
        for _ in 0..<40 { driver.pointerChanged() }
        check(driver.testSurface.expansion == 1 && CapsuleAnimation.created == count, "Leaving a manually kept panel must not animate or collapse")
        inside(driver); driver.toggleHold()
        check(!driver.testState.keepsExpanded && driver.testSurface.expansion == 1 && driver.testAnimation == nil, "Releasing manual hold while pointer is inside keeps hover preview")
        outside(); driver.pointerChanged()
        check(driver.testAnimation != nil && !driver.testTarget, "After hold is removed, ordinary pointer leave collapses")
        driver.testAnimation!.advance(1)
        driver.toggleHold(); driver.testAnimation!.advance(1)
        driver.toggleHold()
        check(!driver.testState.keepsExpanded && driver.testAnimation != nil && !driver.testTarget, "Removing manual hold outside collapses immediately")
    case "hide-cancel":
        driver.expand(true); driver.testAnimation!.advance(0.45)
        driver.testState.pointerPressed = true; driver.testState.menuPresented = true; driver.testState.keepsExpanded = true
        driver.interaction(true); let count = CapsuleAnimation.created
        driver.hideForTest()
        check(driver.testSurface.cancelCount == 1 && !driver.testState.interactionActive && !driver.testState.keepsExpanded, "Hide must cancel the Surface gesture/menu and clear manual hold")
        check(driver.testSurface.expansion == 0 && driver.testAnimation == nil && NSEvent.monitors.isEmpty && !driver.testWindow.isVisible, "Hide leaves no partial geometry, animations, or monitors")
        check(CapsuleAnimation.created == count, "The cancellation callback must not start a new animation while hiding")
    case "partial-monitor":
        NSEvent.globalUnavailable = true
        driver.expand(true); driver.testAnimation!.advance(1)
        let created = NSEvent.nextMonitor
        for _ in 0..<40 { driver.pointerChanged() }
        check(NSEvent.monitors.count == 1 && NSEvent.nextMonitor == created, "A missing global monitor must not recreate and leak the valid local monitor")
    case "surface-shape":
        driver.expand(true); driver.testAnimation!.advance(1)
        inside(driver); driver.testSurface.containsPoint = { _ in false }
        check(driver.testWindow.frame.contains(NSEvent.mouseLocation), "The fixture pointer is inside the rectangular window bounds")
        driver.pointerChanged()
        check(driver.testAnimation != nil && !driver.testTarget, "Controller must use the Surface shape result instead of the enclosing rectangle")
    default: fatalError("Unknown test")
    }
    driver.hideForTest()
}
print(CommandLine.arguments[1] + " passed")
'''
        cls.directory = tempfile.TemporaryDirectory(prefix='capsule controller interaction ')
        cls.addClassCleanup(cls.directory.cleanup)
        path = Path(cls.directory.name)
        main = path / 'main.swift'; main.write_text(fixture)
        cls.binary = path / 'check'
        result = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), '-o', str(cls.binary)], capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise AssertionError(result.stderr)

    def run_case(self, name):
        result = subprocess.run([str(self.binary), name], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(name + ' passed', result.stdout)

    def test_partial_animation_pause_resume_and_no_event_restart(self):
        self.run_case('partial-resume')

    def test_drag_anchor_and_native_menu_preserve_geometry(self):
        self.run_case('drag-and-menu')

    def test_manual_hold_and_pointer_leave(self):
        self.run_case('manual-hold')

    def test_hide_cancels_gesture_menu_and_monitors(self):
        self.run_case('hide-cancel')


    def test_missing_global_monitor_does_not_leak_local_monitors(self):
        self.run_case('partial-monitor')

    def test_pointer_uses_surface_shape_instead_of_window_rectangle(self):
        self.run_case('surface-shape')




if __name__ == '__main__':
    unittest.main()
