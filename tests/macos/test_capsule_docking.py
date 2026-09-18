"""Run the production docking owner with deterministic native boundary doubles."""
from tools.common.paths import macos_source
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


HARNESS = r'''
import AppKit
func check(_ yes: @autoclosure () -> Bool, _ why: String) { precondition(yes(), why) }
func pump(_ seconds: Double) { RunLoop.main.run(until: Date().addingTimeInterval(seconds)) }
final class CapsuleState {
    var showsDockedMonitor = false
    var autoHide = true, interactionActive = false, keepsExpanded = false, keyboardInteracting = false
}
final class CapsuleSurface {
    static let small = NSSize(width: 76, height: 76)
    let state = CapsuleState()
    var docking: CGFloat = 0, expansion: CGFloat = 0
    var dockEdge: String?
    var dockedContentChanged: (() -> Void)?
    weak var panel: NSPanel?
    func containsScreenPoint(_ point: NSPoint) -> Bool { panel?.frame.contains(point) ?? false }
}
final class NSPanel {
    var frame = NSRect(x: 10, y: 300, width: 76, height: 76)
    var isVisible = true, isKeyWindow = false
    func setFrame(_ frame: NSRect, display: Bool) { self.frame = frame }
    func resignKey() { isKeyWindow = false }
}
final class NSEvent { static var mouseLocation = NSPoint(x: 600, y: 700) }
final class NSScreen {
    static var screens = [NSScreen()]
    var number = 1
    var frame = NSRect(x: 0, y: 0, width: 1000, height: 800)
    var visibleFrame = NSRect(x: 0, y: 40, width: 1000, height: 736)
    var deviceDescription: [NSDeviceDescriptionKey: Any] { [NSDeviceDescriptionKey("NSScreenNumber"): NSNumber(value: number)] }
}
final class NSWorkspace {
    static let shared = NSWorkspace()
    static let didWakeNotification = Notification.Name("wake"), activeSpaceDidChangeNotification = Notification.Name("space")
    let notificationCenter = NotificationCenter()
    var accessibilityDisplayShouldReduceMotion = false
}
final class CapsuleAnimation {
    enum Curve { case easeInOut }; enum Blocking { case nonblocking }
    var step: ((CGFloat) -> Void)?, frameRate = 0.0, animationBlockingMode = Blocking.nonblocking
    init(duration: TimeInterval, animationCurve: Curve) {}
    func start() { step?(1) }
    func stop() {}
}
let suite = "test-docking-" + UUID().uuidString
let prefs = UserDefaults(suiteName: suite)!
defer { prefs.removePersistentDomain(forName: suite) }
let panel = NSPanel(), surface = CapsuleSurface(); surface.panel = panel
if CommandLine.arguments[1].hasPrefix("seam-") {
    let neighbor = NSScreen(); neighbor.number = 2
    neighbor.frame.origin.x = 1000; neighbor.visibleFrame.origin.x = 1000
    NSScreen.screens.append(neighbor)
}
let dock = CapsuleDocking(panel: panel, surface: surface, preferences: prefs)
var expansions = 0, collapses = 0
dock.expand = { expansions += 1; surface.expansion = 1 }
dock.collapse = { _ in collapses += 1; surface.expansion = 0; dock.didCollapse() }
dock.currentCompact = { panel.frame }
switch CommandLine.arguments[1] {
case "seam-compact-left", "seam-compact-right", "seam-expanded-left", "seam-expanded-right":
    let destinationRight = CommandLine.arguments[1].hasSuffix("right")
    let expanded = CommandLine.arguments[1].contains("expanded")
    let expected: CapsuleDockEdge = destinationRight ? .left : .right
    let owner = NSScreen.screens[destinationRight ? 1 : 0].visibleFrame
    let release = NSPoint(x: destinationRight ? 1010 : 990, y: 350)
    NSEvent.mouseLocation = release
    panel.frame = expanded ? NSRect(x: 800, y: 190, width: 336, height: 410) : NSRect(x: 950, y: 320, width: 76, height: 76)
    surface.expansion = expanded ? 1 : 0
    var anchor = NSRect(x: 950, y: 320, width: 76, height: 76)
    dock.currentCompact = { anchor }
    dock.didDropExpanded = { anchor = $0; check($1 == panel.frame, "expanded callback receives placed panel") }
    dock.finishDrag(at: release)
    check(dock.edge == expected && owner.contains(panel.frame), "release pointer chooses shared-edge owner on either screen")
    if expanded {
        pump(0.62)
        check(surface.expansion == 1 && collapses == 0 && surface.docking == 0, "expanded shared-edge drop stays open while pointer is inside")
        dock.collapse = { _ in collapses += 1; surface.expansion = 0; panel.frame = anchor; dock.didCollapse() }
        dock.manuallyCollapse()
    }
    NSEvent.mouseLocation = NSPoint(x: 500, y: 700)
    _ = dock.handlePointer(inside: false)
    dock.scheduleHide(); pump(0.62)
    check(surface.docking == 1 && panel.frame.width == 44 && owner.contains(panel.frame), "pointer exit hides within its own screen at shared seam")
    check(destinationRight ? panel.frame.minX == 1000 : panel.frame.maxX == 1000, "side tab stays attached to seam")
    let restoredPanel = NSPanel(), restoredSurface = CapsuleSurface(); restoredSurface.panel = restoredPanel
    let restored = CapsuleDocking(panel: restoredPanel, surface: restoredSurface, preferences: prefs)
    check(restored.edge == expected && owner.contains(restoredPanel.frame), "restart preserves shared-edge ownership")
    NSEvent.mouseLocation = NSPoint(x: panel.frame.midX, y: panel.frame.midY)
    _ = dock.handlePointer(inside: true); pump(0.16)
    check(surface.docking == 0 && expansions == 0 && owner.contains(panel.frame), "shared-edge hover restores reachable ring without instant expansion")
case "two-stage":
    dock.finishDrag(at: NSPoint(x: 25, y: 320)); check(dock.edge == .left, "drag release docks")
    pump(0.62); check(surface.docking == 1 && panel.frame.size == NSSize(width: 44, height: 68), "outside delay hides to readable edge tab")
    NSEvent.mouseLocation = NSPoint(x: panel.frame.midX, y: panel.frame.midY)
    let compactTab = panel.frame
    surface.state.showsDockedMonitor = true; surface.dockedContentChanged?(); pump(0.02)
    check(panel.frame.height == 96 && panel.frame.midY == compactTab.midY && panel.frame.minX == compactTab.minX, "new task grows tab without shifting edge or center")
    surface.state.showsDockedMonitor = false; surface.dockedContentChanged?(); pump(0.02)
    check(panel.frame == compactTab && surface.docking == 1, "last task read restores compact centered tab")
    check(dock.handlePointer(inside: true), "tab owns hover")
    pump(0.16); check(surface.docking == 0 && expansions == 0, "tab restores compact only")
    _ = dock.handlePointer(inside: true); pump(0.2)
    check(expansions == 0, "stationary cursor never auto expands")
    NSEvent.mouseLocation.x += 3; dock.pointerMoved(NSEvent.mouseLocation)
    check(expansions == 1, "real movement completes the second stage")
case "cancel":
    dock.finishDrag(at: NSPoint(x: 25, y: 320)); surface.state.interactionActive = true; dock.pauseInteraction()
    pump(0.62); check(surface.docking == 0, "menu or drag cancels delayed hide")
    surface.state.interactionActive = false; panel.isKeyWindow = true; surface.state.keyboardInteracting = true; dock.scheduleHide(); pump(0.62)
    check(surface.docking == 0, "keyboard focus protects visibility")
    panel.isKeyWindow = false; dock.scheduleHide(); panel.isVisible = false; pump(0.62)
    check(surface.docking == 0, "closed window cannot animate on stale timeout")
case "manual":
    NSEvent.mouseLocation = NSPoint(x: 60, y: 320); surface.expansion = 1
    dock.manuallyCollapse(); check(collapses == 1, "explicit collapse runs once")
    for _ in 0..<20 { check(dock.handlePointer(inside: true), "static pointer remains suppressed") }
    check(expansions == 0, "no reopen after explicit collapse")
    // A shrinking tracking area may emit an exit without physical motion.
    // This is especially visible when the footer collapses into the old orb.
    check(dock.handlePointer(inside: false), "Geometry-generated exit cannot cancel manual-collapse suppression")
    check(dock.handlePointer(inside: true), "The overlapping compact orb cannot immediately reopen")
    // Even moving geometry out from under a stationary pointer is not a real exit.
    panel.frame.origin.x += 200
    check(dock.handlePointer(inside: false), "Stationary pointer remains suppressed after geometry changes")
    panel.frame.origin.x -= 200
    NSEvent.mouseLocation = NSPoint(x: 600, y: 700); _ = dock.handlePointer(inside: false)
    check(!dock.handlePointer(inside: true), "real exit resets ordinary entry")
case "explicit-open":
    dock.explicitExpand()
    check(expansions == 1 && !surface.state.keepsExpanded && !surface.state.keyboardInteracting, "Explicit opening never silently enables keep-expanded or keyboard retention")
case "expanded-drag":
    surface.expansion = 1; surface.state.keepsExpanded = true
    panel.frame = NSRect(x: -20, y: 190, width: 336, height: 410)
    dock.currentCompact = { NSRect(x: 240, y: 524, width: 76, height: 76) }
    var dropped: (NSRect, NSRect)?
    dock.didDropExpanded = { dropped = ($0, $1) }
    dock.finishDrag(at: NSPoint(x: 10, y: 350))
    check(surface.expansion == 1 && collapses == 0 && surface.state.keepsExpanded, "expanded drag never collapses or changes keep-open")
    check(panel.frame == NSRect(x: 0, y: 190, width: 336, height: 410) && dropped?.1 == panel.frame, "release recovers panel without flipping its grip")
    check(dock.edge == .left && dropped?.0.minX == 0, "expanded panel boundary selects docking even when the compact anchor is far from it")
    panel.isKeyWindow = true; dock.manuallyCollapse()
    check(!panel.isKeyWindow && !surface.state.keepsExpanded && collapses == 1, "manual collapse clears keyboard retention and keep-open")
    check(dock.handlePointer(inside: true), "stationary pointer cannot reverse manual collapse")
case "restart":
    dock.finishDrag(at: NSPoint(x: 25, y: 320)); pump(0.62)
    let restoredPanel = NSPanel(), restoredSurface = CapsuleSurface(); restoredSurface.panel = restoredPanel
    let restored = CapsuleDocking(panel: restoredPanel, surface: restoredSurface, preferences: prefs)
    check(restored.edge == .left && restoredPanel.frame.size == CapsuleSurface.small && restoredSurface.docking == 0, "restart restores a reachable compact anchor")
    dock.toggleAutoHide(); check(!surface.state.autoHide && surface.docking == 0, "turning off hide restores compact")
    check(prefs.object(forKey: "capsuleAutoHide") as? Bool == false, "toggle persists")
    dock.toggleAutoHide(); surface.expansion = 1
    panel.frame = NSRect(x: 0, y: 150, width: 336, height: 410)
    let detailFrame = panel.frame; dock.toggleAutoHide()
    check(panel.frame == detailFrame && surface.expansion == 1, "changing hide preference must not shrink an open detail panel")
default: fatalError("unknown case")
}
dock.prepareToHide()
print("passed")
'''


@unittest.skipUnless(shutil.which('xcrun'), 'Requires macOS Swift')
class CapsuleDockingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='capsule docking ')
        cls.addClassCleanup(cls.directory.cleanup)
        main = Path(cls.directory.name) / 'main.swift'
        main.write_text(HARNESS)
        cls.binary = main.with_name('docking')
        result = subprocess.run(['xcrun', 'swiftc', str(macos_source('CapsulePlacement.swift')), str(macos_source('CapsuleDocking.swift')), str(main), '-o', str(cls.binary)], capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise AssertionError(result.stderr)

    def test_docking_state_transitions(self):
        for case in ['two-stage', 'cancel', 'manual', 'restart', 'expanded-drag', 'explicit-open',
                     'seam-compact-left', 'seam-compact-right', 'seam-expanded-left', 'seam-expanded-right']:
            with self.subTest(case=case):
                result = subprocess.run([str(self.binary), case], capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 0, result.stderr)
