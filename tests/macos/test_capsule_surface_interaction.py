"""Synthetic mouse input against the real Surface/Host; never shows a window/menu."""
from tools.common.paths import ROOT, BACKEND, macos_source
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class CapsuleSurfaceInteractionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='capsule surface interaction ')
        cls.addClassCleanup(cls.directory.cleanup)
        root = Path(cls.directory.name)
        main = root / 'main.swift'
        main.write_text(r'''import AppKit
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
func check(_ condition: @autoclosure () -> Bool, _ reason: String) { precondition(condition(), reason) }
func pump() { RunLoop.main.run(until: Date().addingTimeInterval(0.02)) }
final class Fixture {
    let state = CapsuleState(), window: NSPanel, surface: CapsuleSurface
    var actions: [String] = [], interactions: [Bool] = [], menuCount = 0
    init(origin: NSPoint = NSPoint(x: 600, y: 400)) {
        surface = CapsuleSurface(state: state)
        window = NSPanel(contentRect: NSRect(origin: origin, size: CapsuleSurface.small), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.isMovableByWindowBackground = false; window.acceptsMouseMovedEvents = true
        window.contentView = CapsuleHost(surface: surface)
        surface.action = { [weak self] name in
            guard let self = self else { return }
            check(!self.state.interactionActive, "A released action must run after pointer/menu tracking ends")
            self.actions.append(name)
        }
        surface.interactionChanged = { [weak self] in self?.interactions.append($0) }
        surface.menuTrackingOverride = { [weak self] _, _ in self?.menuCount += 1 }
        check(!window.isVisible, "All tests must keep their own window hidden")
    }
    func screen(_ point: NSPoint) -> NSPoint { window.convertPoint(toScreen: surface.convert(point, to: nil)) }
    func event(_ type: NSEvent.EventType, at point: NSPoint, clicks: Int = 1, flags: NSEvent.ModifierFlags = []) -> NSEvent {
        NSEvent.mouseEvent(with: type, location: window.convertPoint(fromScreen: point), modifierFlags: flags, timestamp: 0, windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: clicks, pressure: 0)!
    }
    func clickScreen(_ point: NSPoint, clicks: Int = 1) {
        surface.mouseDown(with: event(.leftMouseDown, at: point, clicks: clicks))
        surface.mouseUp(with: event(.leftMouseUp, at: point, clicks: clicks))
    }
    func click(_ point: NSPoint, clicks: Int = 1) { clickScreen(screen(point), clicks: clicks) }
    func expand(clampLeft: Bool = false) {
        let previous = window.frame
        let size = CapsuleSurface.large
        let frame = NSRect(x: clampLeft ? max(0, previous.maxX - size.width) : previous.maxX - size.width,
                           y: previous.maxY - size.height, width: size.width, height: size.height)
        window.setFrame(frame, display: false)
        window.contentView?.layoutSubtreeIfNeeded()
        surface.expansion = 1
        check(surface.bounds.size == size, "Actual Host autoresizing must follow the window")
    }
    func choose(_ value: String, in menu: NSMenu) {
        guard let row = menu.items.first(where: { ($0.representedObject as? String) == value }), row.isEnabled, let selector = row.action else { fatalError("No enabled menu choice " + value) }
        check(NSApp.sendAction(selector, to: row.target, from: row), "Dispatch the actual native item target/action")
    }
    func dispose() { surface.cancelInteraction(); window.contentView = nil; window.close() }
}
switch CommandLine.arguments[1] {
case "threshold":
    let f = Fixture(); defer { f.dispose() }
    let start = f.screen(NSPoint(x: 38, y: 38)), origin = f.window.frame.origin
    f.surface.mouseDown(with: f.event(.leftMouseDown, at: start))
    check(f.state.pointerPressed && f.interactions == [true], "Mouse down begins an interaction before release")
    let short = NSPoint(x: start.x + 2.9, y: start.y)
    f.surface.mouseDragged(with: f.event(.leftMouseDragged, at: short))
    check(f.window.frame.origin == origin, "Subthreshold movement must not drag the window")
    f.surface.mouseUp(with: f.event(.leftMouseUp, at: short))
    check(f.actions == ["main"] && f.interactions == [true, false], "A short header click opens main after ending interaction")
    f.actions = []
    f.surface.mouseDown(with: f.event(.leftMouseDown, at: start))
    let boundary = NSPoint(x: start.x + 3, y: start.y)
    f.surface.mouseDragged(with: f.event(.leftMouseDragged, at: boundary))
    check(abs(f.window.frame.minX - origin.x - 3) < 0.01, "Exactly three points crosses the drag threshold")
    f.surface.mouseUp(with: f.event(.leftMouseUp, at: boundary))
    check(f.actions.isEmpty && !f.state.interactionActive, "Dragging must never also click")
case "drag-return":
    let f = Fixture(); defer { f.dispose() }
    let start = f.screen(NSPoint(x: 38, y: 38)), origin = f.window.frame.origin
    f.surface.mouseDown(with: f.event(.leftMouseDown, at: start))
    f.surface.mouseDragged(with: f.event(.leftMouseDragged, at: NSPoint(x: start.x + 24, y: start.y - 15)))
    f.surface.mouseDragged(with: f.event(.leftMouseDragged, at: start))
    f.surface.mouseUp(with: f.event(.leftMouseUp, at: start))
    check(f.window.frame.origin == origin && f.actions.isEmpty, "A drag returning to its origin remains a drag")
    f.surface.mouseDown(with: f.event(.leftMouseDown, at: start))
    f.surface.mouseUp(with: f.event(.leftMouseUp, at: NSPoint(x: start.x + 4, y: start.y)))
    check(f.actions.isEmpty, "A displaced release without a delivered drag event must still suppress click")
case "click-and-buttons":
    let f = Fixture(); defer { f.dispose() }
    f.click(NSPoint(x: 38, y: 38)); f.click(NSPoint(x: 38, y: 38), clicks: 2)
    check(f.actions == ["main"], "Double click dispatches the header primary action only once")
    f.expand(); f.actions = []
    f.click(NSPoint(x: 150, y: 25))
    check(f.actions == ["main"], "Expanded numerical header keeps left-click main behavior")
    f.actions = []
    let down = f.screen(NSPoint(x: 16.4, y: CapsuleSurface.large.height - 25))
    let outside = f.screen(NSPoint(x: 15.6, y: CapsuleSurface.large.height - 25))
    f.surface.mouseDown(with: f.event(.leftMouseDown, at: down))
    f.surface.mouseUp(with: f.event(.leftMouseUp, at: outside))
    check(f.actions.isEmpty, "Releasing just outside a button cancels, even below the drag threshold")
    let button = f.screen(NSPoint(x: 59, y: CapsuleSurface.large.height - 25)), origin = f.window.frame.origin
    f.surface.mouseDown(with: f.event(.leftMouseDown, at: button))
    f.surface.mouseDragged(with: f.event(.leftMouseDragged, at: NSPoint(x: button.x + 20, y: button.y)))
    f.surface.mouseDragged(with: f.event(.leftMouseDragged, at: button))
    f.surface.mouseUp(with: f.event(.leftMouseUp, at: button))
    check(f.window.frame.origin == origin && f.actions.isEmpty, "Dragging a control neither moves the window nor clicks on return")
    f.state.enabled = false
    f.click(NSPoint(x: 29, y: 65))
    check(f.actions.isEmpty && !f.state.pointerPressed, "Disabled refresh has no active gesture")
    f.state.enabled = true; f.click(NSPoint(x: 29, y: 65))
    check(f.actions == ["refresh"], "Ordinary refresh remains independently clickable")
case "shape-hotspot":
    for clamp in [false, true] {
        let f = Fixture(origin: NSPoint(x: clamp ? 10 : 600, y: 400)); defer { f.dispose() }
        check(f.surface.containsScreenPoint(f.screen(NSPoint(x: 38, y: 38))), "Circle center is inside")
        check(!f.surface.containsScreenPoint(f.screen(NSPoint(x: 2, y: 2))), "Transparent rectangular circle corners are not hover/click targets")
        f.click(NSPoint(x: 2, y: 2)); check(f.actions.isEmpty, "A transparent corner must not dispatch main")
        let local = clamp ? NSPoint(x: 20, y: 60) : NSPoint(x: 38, y: 60)
        let originalScreenPoint = f.screen(local)
        f.surface.mouseEntered(with: f.event(.mouseMoved, at: originalScreenPoint))
        f.expand(clampLeft: clamp)
        f.clickScreen(originalScreenPoint)
        check(f.actions == ["main"], "Hover resizing and screen clamping must preserve the original circular primary hotspot")
        check(!f.surface.containsScreenPoint(f.screen(NSPoint(x: 1, y: 1))), "Expanded rounded corners are also outside the Surface")
        f.actions = []
        f.surface.mouseMoved(with: f.event(.mouseMoved, at: f.screen(NSPoint(x: 150, y: 180))))
        f.click(NSPoint(x: 29, y: 65))
        check(f.actions == ["refresh"], "Leaving the old hotspot must release its override so controls work normally")
    }
case "context":
    let f = Fixture(); defer { f.dispose() }
    f.state.enabled = false
    f.surface.menuTrackingOverride = { menu, point in
        f.menuCount += 1
        check(f.state.menuPresented && !f.state.pointerPressed, "Context tracking freezes geometry without a left-button press")
        check(menu.items.first { ($0.representedObject as? String) == "refresh" }?.isEnabled == false, "Disabled refresh also stays disabled in context menu")
        check(menu.items.contains { ($0.representedObject as? String) == "quit" }, "Context exposes an exit path")
        check(!menu.items.contains { ["model", "task"].contains($0.representedObject as? String ?? "") }, "Async selectors are opened from expanded controls, without nested context tracking")
        f.choose("menu", in: menu)
    }
    let point = f.screen(NSPoint(x: 38, y: 38))
    f.surface.rightMouseDown(with: f.event(.rightMouseDown, at: point))
    check(f.menuCount == 1 && f.actions == ["menu"] && !f.state.interactionActive, "Right click dispatches its selected action only after tracking")
    f.surface.mouseDown(with: f.event(.leftMouseDown, at: point, flags: .control))
    f.surface.mouseUp(with: f.event(.leftMouseUp, at: point))
    check(f.menuCount == 2 && f.actions == ["menu", "menu"], "Control-click opens context and must not also dispatch a left click")
    f.surface.rightMouseDown(with: f.event(.rightMouseDown, at: f.screen(NSPoint(x: 1, y: 1))))
    check(f.menuCount == 2, "Right click outside the actual shape does nothing")
    f.surface.menuTrackingOverride = { _, _ in f.surface.cancelInteraction() }
    f.surface.rightMouseDown(with: f.event(.rightMouseDown, at: point))
    check(f.actions == ["menu", "menu"] && !f.state.interactionActive, "Cancel invalidates an active native menu selection")
case "async-choices":
    let f = Fixture(); defer { f.dispose() }; f.expand()
    var pending: [([(String, String)]?, String?) -> Void] = []
    var kinds: [String] = []
    f.state.loadChoices = { kind, complete in kinds.append(kind); pending.append(complete) }
    f.click(NSPoint(x: 100, y: 121))
    check(kinds == ["model"] && f.state.menuPresented, "Model picker holds interaction while loading options")
    f.surface.cancelInteraction(); pending[0]([("late", "Late model")], nil)
    check(!f.state.interactionActive && f.menuCount == 0 && f.actions.isEmpty, "A late result after cancel must not create a menu or perform an action")
    f.click(NSPoint(x: 100, y: 153))
    check(kinds == ["model", "task"] && f.state.menuPresented, "Task choices use a fresh menu generation")
    pending[0](nil, "stale error")
    check(f.state.menuPresented && f.state.status != "stale error", "An old callback cannot finish the current interaction or replace its status")
    f.surface.menuTrackingOverride = { menu, _ in f.menuCount += 1; f.choose("task-id", in: menu) }
    pending[1]([("all", "全部任务"), ("task-id", "Selected task")], nil)
    check(f.actions == ["task:task-id"] && f.menuCount == 1 && !f.state.interactionActive, "Latest async choices dispatch the chosen id after tracking closes")
    f.click(NSPoint(x: 100, y: 121)); pending[2](nil, "读取失败")
    check(f.state.status == "读取失败" && !f.state.interactionActive, "Current load error must release interaction")
    f.click(NSPoint(x: 100, y: 121)); f.window.contentView = nil
    pending[3]([("after-detach", "After detach")], nil)
    check(!f.state.interactionActive && f.menuCount == 1, "Detaching the view cancels pending choices")
default: fatalError("Unknown test")
}
print(CommandLine.arguments[1] + " passed")
''')
        cls.binary = root / 'check'
        result = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), str(macos_source('Capsule.swift')), str(macos_source('CapsuleHost.swift')), '-o', str(cls.binary)], capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise AssertionError(result.stderr)

    def run_case(self, name):
        result = subprocess.run([str(self.binary), name], capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(name + ' passed', result.stdout)

    def test_three_point_drag_threshold(self):
        self.run_case('threshold')

    def test_drag_out_and_back_and_displaced_release_do_not_click(self):
        self.run_case('drag-return')

    def test_header_click_dedup_buttons_and_disabled_refresh(self):
        self.run_case('click-and-buttons')

    def test_shape_and_original_hotspot_during_resize_and_clamp(self):
        self.run_case('shape-hotspot')

    def test_context_right_control_click_and_cancel(self):
        self.run_case('context')

    def test_cancelled_and_replaced_async_choice_callbacks(self):
        self.run_case('async-choices')


if __name__ == '__main__':
    unittest.main()
