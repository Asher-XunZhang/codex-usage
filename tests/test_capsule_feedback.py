"""Real pointer events and offscreen drawing for floating button feedback."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "Requires macOS developer tools")
class CapsuleFeedbackTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix="capsule feedback ")
        cls.addClassCleanup(cls.directory.cleanup)
        root = Path(cls.directory.name)
        main = root / "main.swift"
        main.write_text(r'''import AppKit
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
func check(_ condition: @autoclosure () -> Bool, _ reason: String) { precondition(condition(), reason) }
final class Fixture {
    let state = CapsuleState(), window: NSPanel, surface: CapsuleSurface
    var actions: [String] = [], hovers: [Bool] = []
    init(_ theme: CapsuleTheme = .dark) {
        state.theme = theme
        surface = CapsuleSurface(state: state)
        window = NSPanel(contentRect: NSRect(x: 500, y: 200, width: 336, height: 410), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false; window.isMovableByWindowBackground = false
        window.contentView = CapsuleHost(surface: surface)
        window.contentView?.layoutSubtreeIfNeeded(); surface.expansion = 1
        surface.action = { [weak self] name in
            guard let self = self else { return }
            check(!self.state.interactionActive, "Actions dispatch only after tracking ends")
            self.actions.append(name)
        }
        surface.hover = { [weak self] in self?.hovers.append($0) }
        surface.interactionChanged = { _ in }
        surface.menuTrackingOverride = { _, _ in }
        check(!window.isVisible, "Offscreen tests never display a window")
    }
    func event(_ type: NSEvent.EventType, _ point: NSPoint, clicks: Int = 1) -> NSEvent {
        NSEvent.mouseEvent(with: type, location: surface.convert(point, to: nil), modifierFlags: [], timestamp: 0,
            windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: clicks, pressure: 0)!
    }
    func move(_ point: NSPoint) { surface.mouseMoved(with: event(.mouseMoved, point)) }
    func down(_ point: NSPoint, clicks: Int = 1) { surface.mouseDown(with: event(.leftMouseDown, point, clicks: clicks)) }
    func up(_ point: NSPoint, clicks: Int = 1) { surface.mouseUp(with: event(.leftMouseUp, point, clicks: clicks)) }
    func exit() { surface.mouseExited(with: event(.mouseMoved, NSPoint(x: -20, y: -20))) }
    func pixels(_ rect: NSRect? = nil) -> Data {
        let bitmap = surface.bitmapImageRepForCachingDisplay(in: surface.bounds)!
        bitmap.bitmapData!.initialize(repeating: 0, count: bitmap.bytesPerRow * bitmap.pixelsHigh)
        surface.cacheDisplay(in: surface.bounds, to: bitmap)
        let frame = rect ?? surface.bounds
        let sx = CGFloat(bitmap.pixelsWide) / surface.bounds.width
        let sy = CGFloat(bitmap.pixelsHigh) / surface.bounds.height
        let x0 = max(0, Int(frame.minX * sx)), x1 = min(bitmap.pixelsWide, Int(frame.maxX * sx))
        let y0 = max(0, Int(frame.minY * sy)), y1 = min(bitmap.pixelsHigh, Int(frame.maxY * sy))
        var data = Data()
        for y in y0..<y1 {
            data.append(bitmap.bitmapData! + y * bitmap.bytesPerRow + x0 * bitmap.samplesPerPixel, count: (x1-x0) * bitmap.samplesPerPixel)
        }
        return data
    }
    func dispose() { surface.cancelInteraction(); window.contentView = nil; window.close() }
}
let mainButton = NSPoint(x: 59, y: 385)
switch CommandLine.arguments[1] {
case "visual":
    for theme in CapsuleTheme.allCases {
        let f = Fixture(theme); defer { f.dispose() }
        let normal = f.pixels()
        let originalLayer = f.surface.layer
        let inside = NSRect(x: 24, y: 377, width: 70, height: 17)
        let outside = NSRect(x: 42, y: 363, width: 24, height: 3)
        let adjacent = NSRect(x: 118, y: 381, width: 72, height: 9)
        let far = NSRect(x: 42, y: 325, width: 24, height: 7)
        let normalInside = f.pixels(inside), normalOutside = f.pixels(outside)
        let normalAdjacent = f.pixels(adjacent), normalFar = f.pixels(far)
        f.move(mainButton)
        let hovered = f.pixels()
        check(hovered != normal && f.pixels(inside) == normalInside, "Both themes keep the hovered button face exactly unchanged")
        let hoverOutside = f.pixels(outside)
        check(hoverOutside != normalOutside, "C glow spreads visibly seven to ten points beyond the button border")
        check(f.pixels(adjacent) == normalAdjacent, "Outer glow preserves the adjacent button's text and core")
        check(f.pixels(far) == normalFar, "Outer glow remains local instead of tinting distant content")
        f.move(mainButton)
        check(f.pixels() == hovered && !f.surface.isLiquidAnimating, "Stationary feedback does not animate")
        f.down(mainButton)
        check(f.pixels(inside) != normalInside && f.actions.isEmpty && f.state.pointerPressed, "Only press changes the button face before action dispatch")
        func difference(_ pixels: Data) -> Int { zip(pixels, normalOutside).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) } }
        check(difference(f.pixels(outside)) < difference(hoverOutside), "Press tightens the outer glow")
        check(f.pixels(adjacent) == normalAdjacent && f.pixels(far) == normalFar, "Pressed feedback also preserves neighboring cores and distant content")
        f.up(mainButton)
        check(f.pixels() == hovered && f.actions == ["main"] && !f.state.interactionActive, "Release restores hover and dispatches exactly once")
        f.up(mainButton)
        check(f.actions == ["main"], "An extra release cannot duplicate the action")
        let haloOnly = NSPoint(x: 59, y: 363)
        f.move(haloOnly); f.down(haloOnly); f.up(haloOnly)
        check(f.pixels() == normal && f.actions == ["main"], "The outer glow does not enlarge hover or click targets")
        f.exit()
        check(f.pixels() == normal && f.hovers.last == false, "Pointer exit immediately removes all feedback")
        check(f.surface.subviews.isEmpty && f.surface.layer === originalLayer, "Feedback reuses the existing view and backing layer")
    }
case "buttons":
    let f = Fixture(); defer { f.dispose() }
    for budgetMode in [false, true] {
        f.state.budgetMode = budgetMode
        let normal = f.pixels()
        let buttons = f.surface.accessibilityChildren()!.map { $0 as! NSAccessibilityElement }
        for button in buttons {
            let label = button.accessibilityLabel() ?? ""
            if !button.isAccessibilityEnabled() || label.contains("保持胶囊详情") || label == "浮窗功能菜单" { continue }
            let frame = button.accessibilityFrame()
            let screen = NSPoint(x: frame.midX, y: frame.midY)
            let point = f.surface.convert(f.window.convertPoint(fromScreen: screen), from: nil)
            let face = NSRect(x: point.x - frame.width / 2 + 8, y: point.y - frame.height / 2 + 8, width: frame.width - 16, height: frame.height - 16)
            let normalFace = f.pixels(face)
            f.move(point)
            check(f.pixels() != normal, "Every enabled detail button must give feedback: " + label)
            check(f.pixels(face) == normalFace, "Hover does not tint the interior, including transparent buttons: " + label)
            f.exit()
            check(f.pixels() == normal && f.actions.isEmpty, "Moving across buttons must not invoke actions: " + label)
        }
    }
case "adjacent":
    for theme in CapsuleTheme.allCases {
        let f = Fixture(theme); defer { f.dispose() }
        for (target, neighbor, vertical, touching) in [
            (NSPoint(x: 50, y: 90), NSRect(x: 94, y: 78, width: 134, height: 24), false, false),
            (NSPoint(x: 100, y: 122), NSRect(x: 16, y: 142, width: 304, height: 24), true, false),
            (NSPoint(x: 210, y: 312), NSRect(x: 174, y: 332, width: 146, height: 24), true, false),
            (mainButton, NSRect(x: 110, y: 373, width: 88, height: 25), false, false)] {
            let core = neighbor.insetBy(dx: 8, dy: 8)
            let normalCore = f.pixels(core)
            // Sample a clean straight edge, away from text and rounded corners.
            func strip(_ distance: CGFloat) -> NSRect {
                vertical ? NSRect(x: 220, y: neighbor.minY + distance, width: 2, height: 1)
                    : NSRect(x: neighbor.minX + distance, y: neighbor.midY - 1, width: 1, height: 2)
            }
            let distances: [CGFloat] = [-1, 0.5, 2, 3.5, 5, 6.5]
            let baseline = distances.map { f.pixels(strip($0)) }
            f.move(target)
            let glow = distances.enumerated().map { index, distance in
                zip(f.pixels(strip(distance)), baseline[index]).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) }
            }
            check(glow[1] > 0, "Foreground glow continues over the neighboring edge")
            if !touching {
                check(glow[0] > 0 && Double(glow[1]) / Double(glow[0]) > 0.20, "A neighboring button no longer creates an abrupt rectangular cutoff")
            }
            check(glow[1] > glow[2] && glow[2] >= glow[3] && glow[3] >= glow[4] && glow[5] == 0,
                  "The neighboring glow smoothly fades inward to zero within six points")
            check(f.pixels(core) == normalCore, "The neighboring text and core remain unchanged")
            f.down(target)
            check(f.pixels(core) == normalCore, "Press also keeps neighboring text and core intact")
            f.surface.cancelInteraction(); f.exit()
        }
        let header = NSRect(x: 0, y: 0, width: 336, height: 52), normalHeader = f.pixels(NSRect(x: 0, y: 0, width: 336, height: 52))
        f.move(NSPoint(x: 29, y: 65))
        check(f.pixels(header) == normalHeader, "The numerical header stays fully protected from button glow")
    }
case "theme-group":
    for theme in CapsuleTheme.allCases {
        let f = Fixture(theme); defer { f.dispose() }
        let left = NSPoint(x: 210, y: 312), right = NSPoint(x: 280, y: 312)
        let leftFace = NSRect(x: 182, y: 308, width: 56, height: 8)
        let rightFace = NSRect(x: 254, y: 308, width: 58, height: 8)
        let normalLeft = f.pixels(leftFace), normalRight = f.pixels(rightFace)
        let farSideHalo = NSRect(x: 277, y: 292, width: 20, height: 3)
        let normalHalo = f.pixels(farSideHalo)
        f.move(left)
        let leftHover = f.pixels()
        check(f.pixels(farSideHalo) != normalHalo, "Hovering the left segment lights the full group outline, including its far side")
        f.move(right)
        check(f.pixels() == leftHover, "Hovering either theme segment gives exactly the same outer halo")
        check(f.pixels(leftFace) == normalLeft && f.pixels(rightFace) == normalRight, "The whole segmented face keeps its normal selected/unselected appearance on hover")
        let previous = f.surface.action
        f.surface.action = { value in
            previous?(value)
            if value == "themeDark" { f.state.theme = .dark }
            if value == "themeLight" { f.state.theme = .light }
        }
        f.down(left)
        check(f.pixels(leftFace) != normalLeft && f.pixels(rightFace) == normalRight, "Press changes only the targeted segment inside the common outline")
        f.up(left)
        check(f.actions == ["themeDark"] && f.state.theme == .dark, "Left segment dispatches its original theme action once")
        var labels = f.surface.accessibilityChildren()!.map { ($0 as! NSAccessibilityElement).accessibilityLabel() ?? "" }
        check(labels.contains("深色主题，已选中") && labels.contains("浅色主题"), "Independent accessibility segments retain the selected theme")
        f.move(right); f.down(right); f.up(right)
        check(f.actions == ["themeDark", "themeLight"] && f.state.theme == .light, "Right segment remains independently selectable")
        labels = f.surface.accessibilityChildren()!.map { ($0 as! NSAccessibilityElement).accessibilityLabel() ?? "" }
        check(labels.contains("深色主题") && labels.contains("浅色主题，已选中"), "Selection moves to the right segment without merging actions")
        f.exit()
        let inactive = f.pixels()
        f.move(left); f.move(right); f.exit()
        check(f.pixels() == inactive, "Leaving either segment clears the whole group halo")
    }
case "cancel":
    let f = Fixture(); defer { f.dispose() }
    let normal = f.pixels(), origin = f.window.frame.origin
    let edge = NSPoint(x: 16.4, y: 385), outside = NSPoint(x: 15.6, y: 385)
    f.move(edge); f.down(edge)
    f.surface.mouseDragged(with: f.event(.leftMouseDragged, outside))
    check(f.pixels() == normal, "Moving just outside cancels the pressed appearance below the drag threshold")
    f.up(outside)
    check(f.actions.isEmpty && !f.state.interactionActive, "Release outside does not click")
    f.move(mainButton); f.down(mainButton)
    f.surface.mouseDragged(with: f.event(.leftMouseDragged, NSPoint(x: 80, y: 385)))
    f.surface.mouseDragged(with: f.event(.leftMouseDragged, mainButton))
    check(f.pixels() == normal, "Dragging out and back never restores pressed feedback")
    f.up(mainButton)
    check(f.actions.isEmpty && f.pixels() == normal && f.window.frame.origin == origin, "A button drag neither dispatches nor moves the window")
    f.move(mainButton); f.down(mainButton); f.exit()
    check(f.pixels() == normal && f.state.pointerPressed, "Exit clears pixels while existing gesture tracking still prevents collapse")
    f.surface.cancelInteraction(); f.up(mainButton)
    check(f.actions.isEmpty && !f.state.interactionActive, "Cancelling tracking also cancels the eventual release")
case "disabled":
    let f = Fixture(); defer { f.dispose() }
    let refresh = NSPoint(x: 29, y: 65)
    f.state.enabled = false
    let disabled = f.pixels()
    f.move(refresh); f.down(refresh); f.up(refresh)
    check(f.pixels() == disabled && f.actions.isEmpty && !f.state.interactionActive, "Disabled refresh has no hover/press feedback or action")
    f.state.enabled = true; f.move(refresh); f.down(refresh)
    f.state.enabled = false
    check(f.pixels() == disabled, "An asynchronous disable clears a button already being pressed")
    f.up(refresh)
    check(f.actions.isEmpty && !f.state.interactionActive, "Disable between down and up prevents dispatch")
    f.state.budgetMode = true
    let readonly = f.pixels()
    let scope = NSPoint(x: 150, y: 154)
    f.move(scope); f.down(scope); f.up(scope)
    check(f.pixels() == readonly && f.actions.isEmpty, "Read-only budget scope never looks interactive")
case "menus":
    let f = Fixture(); defer { f.dispose() }
    let normal = f.pixels(), content = NSPoint(x: 50, y: 90)
    var menus = 0
    f.surface.menuTrackingOverride = { menu, _ in
        menus += 1
        check(f.pixels() == normal && f.state.menuPresented, "Native menu tracking clears its opener feedback")
        f.move(mainButton); f.exit()
        check(f.pixels() == normal, "Pointer events during a menu cannot leave a hovered background")
        let item = menu.items.first { $0.representedObject as? String == "budget" }!
        check(NSApp.sendAction(item.action!, to: item.target, from: item), "Dispatch real menu item target/action")
    }
    f.move(content); f.down(content); f.up(content)
    check(menus == 1 && f.actions == ["content:budget"] && f.pixels() == normal && !f.state.interactionActive, "A menu selection clears feedback and dispatches once")
    f.up(content)
    check(menus == 1 && f.actions.count == 1, "Menu opening never leaves a second pending press")
    f.surface.menuTrackingOverride = { _, _ in menus += 1; check(f.pixels() == normal, "Context menu also clears pointer feedback") }
    f.move(mainButton); f.surface.rightMouseDown(with: f.event(.rightMouseDown, mainButton))
    check(menus == 2 && f.actions.count == 1 && f.pixels() == normal, "Cancelling right-click menu does not perform a left-click action")
case "lifecycle":
    let f = Fixture(); defer { f.dispose() }
    let normal = f.pixels()
    f.move(mainButton)
    f.surface.expansion = 0.5
    f.surface.expansion = 1
    check(f.pixels() == normal, "Hiding details clears hover before the next expansion")
    f.state.budgetMode = false; f.move(NSPoint(x: 100, y: 122))
    f.state.budgetMode = true
    let switched = f.pixels()
    f.exit()
    check(f.pixels() == switched, "Changing mode cannot retain feedback from a removed selector")
    f.state.budgetMode = false
    f.move(mainButton); f.surface.cancelInteraction()
    check(f.pixels() == normal, "Lifecycle cancellation clears hover even without an active press")
    for _ in 0..<40 {
        f.state.theme = .light; f.move(mainButton); f.exit()
        f.state.theme = .dark; f.move(mainButton); f.exit()
    }
    check(f.pixels() == normal && f.surface.subviews.isEmpty && !f.surface.isLiquidAnimating, "Repeated theme/hover changes leave no persistent visual state or animation")
default: fatalError("Unknown case")
}
print(CommandLine.arguments[1] + " passed")
''')
        cls.binary = root / "check"
        sources = Path(__file__).parents[1] / "Sources"
        result = subprocess.run(["xcrun", "swiftc", "-swift-version", "5", str(main), str(sources / "Capsule.swift"), str(sources / "CapsuleHost.swift"), "-o", str(cls.binary)], capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise AssertionError(result.stderr)

    def run_case(self, name):
        result = subprocess.run([str(self.binary), name], capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(name + " passed", result.stdout)

    def test_both_themes_hover_glow_pressed_fill_and_no_idle_animation(self):
        self.run_case("visual")

    def test_exit_and_drag_cancel_feedback_without_duplicate_actions(self):
        self.run_case("cancel")

    def test_all_enabled_usage_and_budget_buttons_give_hover_feedback(self):
        self.run_case("buttons")

    def test_glow_crosses_neighbor_edges_and_fades_before_text_and_header(self):
        self.run_case("adjacent")

    def test_theme_segments_share_one_halo_and_keep_separate_actions(self):
        self.run_case("theme-group")

    def test_disabled_and_asynchronously_disabled_buttons_and_readonly_scope(self):
        self.run_case("disabled")

    def test_native_menu_open_close_and_right_click_clear_feedback(self):
        self.run_case("menus")

    def test_collapse_mode_change_cancel_and_repeated_themes_clear_feedback(self):
        self.run_case("lifecycle")
