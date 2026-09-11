"""Budget mode reuses the real floating surface and its native mouse/menu state machine."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "Requires macOS developer tools")
class BudgetCapsuleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix="budget capsule ")
        cls.addClassCleanup(cls.directory.cleanup)
        root = Path(cls.directory.name)
        main = root / "main.swift"
        main.write_text(r'''import AppKit
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
func check(_ condition: @autoclosure () -> Bool, _ message: String) { precondition(condition(), message) }
final class Fixture {
    let state = CapsuleState(), window: NSPanel, surface: CapsuleSurface, host: CapsuleHost
    var actions: [String] = [], hovers: [Bool] = [], menus = 0
    init() {
        state.scope = 1; state.rangeDays = "30"; state.selectedModel = "saved-model"
        state.selectedTask = "saved-task"; state.total = "300K"
        state.quotaFraction = 0.72; state.quotaName = "周余"
        state.budgetID = "daily"; state.budgetName = "日常开发"; state.budgetFraction = 0.10
        state.budgetUsed = "18.00M"; state.budgetRemaining = "2.00M"; state.budgetAmount = "20.00M"
        state.budgetScope = "本周 · 预算模型 · 全部任务"; state.budgetPeriod = "09-07 — 09-14"
        state.budgetStatus = "剩余 10% · 视觉提醒已提示"; state.budgetStale = false
        state.budgetOptions = [("daily", "日常开发"), ("project", "项目金额")]
        surface = CapsuleSurface(state: state); host = CapsuleHost(surface: surface)
        window = NSPanel(contentRect: NSRect(x: 600, y: 400, width: 76, height: 76), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false; window.isMovableByWindowBackground = false; window.contentView = host
        surface.action = { [weak self] action in
            guard let self = self else { return }
            check(!self.state.interactionActive, "Actions must run after menu/press tracking")
            self.actions.append(action)
            if action == "content:budget" { self.state.budgetMode = true }
            if action == "content:usage" { self.state.budgetMode = false }
        }
        surface.hover = { [weak self] in self?.hovers.append($0) }
        surface.interactionChanged = { _ in }
        check(!window.isVisible, "Do not open a real floating window")
    }
    func screen(_ point: NSPoint) -> NSPoint { window.convertPoint(toScreen: surface.convert(point, to: nil)) }
    func event(_ type: NSEvent.EventType, _ point: NSPoint, flags: NSEvent.ModifierFlags = []) -> NSEvent {
        NSEvent.mouseEvent(with: type, location: window.convertPoint(fromScreen: point), modifierFlags: flags, timestamp: 0, windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: 1, pressure: 0)!
    }
    func click(_ point: NSPoint) {
        let at = screen(point); surface.mouseDown(with: event(.leftMouseDown, at)); surface.mouseUp(with: event(.leftMouseUp, at))
    }
    func expand() {
        window.setFrame(NSRect(x: 340, y: 66, width: 336, height: 410), display: false)
        host.layoutSubtreeIfNeeded(); surface.expansion = 1
        check(surface.bounds.size == CapsuleSurface.large, "Budget expanded size stays 336×410")
    }
    func choose(_ id: String, _ menu: NSMenu) {
        let item = menu.items.first { $0.representedObject as? String == id }!
        check(item.isEnabled, "Selected menu item must be enabled")
        check(NSApp.sendAction(item.action!, to: item.target, from: item), "Use real native target/action")
    }
    func cleanup() { surface.cancelInteraction(); window.contentView = nil; window.close() }
}
switch CommandLine.arguments[1] {
case "mode":
    let f = Fixture(); defer { f.cleanup() }
    let originalSurface = f.host.subviews[0]
    check(f.surface.liquidFraction == 0.72 && f.state.displayTotal == "300K", "Original usage remains the default")
    for _ in 0..<30 {
        f.state.budgetMode = true
        check(f.state.normalizedQuota == 0.1 && f.surface.liquidFraction == 0.1, "Budget ring must use budget remainder")
        check(f.state.displayName == "日常开发" && f.state.displayTotal == "18.00M" && f.state.displayScope == "预算已用", "Ring and core text use same budget")
        let value = f.surface.accessibilityValue() as! String
        check(value.contains("日常开发") && value.contains("2.00M") && value.contains("18.00M") && !value.contains("300K"), "Accessible values must not mix usage and budget scopes")
        f.state.budgetMode = false
        check(f.state.scope == 1 && f.state.rangeDays == "30" && f.state.selectedModel == "saved-model" && f.state.selectedTask == "saved-task", "Switching content must preserve all usage filters")
        check(f.state.normalizedQuota == 0.72 && f.surface.liquidFraction == 0.72 && f.state.displayTotal == "300K", "Returning to usage restores its own quota and amount")
        check(f.host.subviews.count == 1 && f.host.subviews[0] === originalSurface, "Mode toggles reuse the same native surface")
    }
    f.state.budgetMode = true; f.state.budgetFraction = nil; f.state.budgetStale = true
    check(f.state.normalizedQuota == nil && f.surface.liquidFraction == nil && f.state.displayStale, "Unknown budget must not fall back to official quota")
    check(f.surface.bounds.size == NSSize(width: 76, height: 76), "Collapsed budget size stays 76×76")
case "selectors":
    let f = Fixture(); defer { f.cleanup() }; f.expand()
    f.surface.menuTrackingOverride = { menu, _ in
        f.menus += 1; check(f.state.menuPresented, "Content menu freezes hover geometry")
        f.choose("budget", menu)
    }
    f.click(NSPoint(x: 50, y: 90))
    check(f.actions == ["content:budget"] && f.state.budgetMode, "Content menu switches only floating content")
    f.surface.menuTrackingOverride = { menu, _ in
        f.menus += 1
        check(menu.items.contains { $0.representedObject as? String == "manage" }, "Budget selector preserves management entry")
        f.state.budgetOptions = [("replacement", "刷新后的选项")]
        f.choose("project", menu)
    }
    f.click(NSPoint(x: 150, y: 90))
    check(f.actions.last == "budget:project", "Selection uses stable ID despite a concurrent options refresh")
    f.surface.menuTrackingOverride = { menu, _ in f.choose("usage", menu) }
    f.click(NSPoint(x: 50, y: 90))
    check(f.actions.last == "content:usage" && !f.state.budgetMode && f.state.rangeDays == "30", "Returning to usage must restore saved range")
    f.surface.menuTrackingOverride = { menu, _ in f.choose("7", menu) }
    f.click(NSPoint(x: 150, y: 90))
    check(f.actions.last == "period:7", "Separate range selector still acts on usage when budget mode ends")
case "readonly":
    let f = Fixture(); defer { f.cleanup() }; f.state.budgetMode = true; f.expand()
    f.click(NSPoint(x: 150, y: 154))
    check(f.actions.isEmpty && !f.state.interactionActive, "Read-only budget scope must not dispatch a mouse action")
    let children = f.surface.accessibilityChildren()!.map { $0 as! NSAccessibilityElement }
    let readonly = children.first { $0.accessibilityLabel() == "预算范围，只读" }!
    check(readonly.isAccessibilityEnabled() == false, "Scope is announced disabled")
    _ = readonly.accessibilityPerformPress()
    check(f.actions.isEmpty, "Programmatic accessibility press must respect read-only scope")
    let edit = children.first { $0.accessibilityLabel() == "编辑预算范围" }!
    _ = edit.accessibilityPerformPress()
    check(f.actions == ["budgetEdit"], "Explicit edit entry is the only scope-changing navigation")
    check(!children.contains { ($0.accessibilityLabel() ?? "").hasPrefix("浮窗模型") || ($0.accessibilityLabel() ?? "").hasPrefix("浮窗任务") }, "Budget mode must not expose independent model/task selectors")
case "interaction":
    let f = Fixture(); defer { f.cleanup() }; f.state.budgetMode = true
    let center = f.screen(NSPoint(x: 38, y: 38))
    f.surface.mouseEntered(with: f.event(.mouseMoved, center))
    check(f.hovers == [true] && f.actions.isEmpty, "Hover opens preview without selecting or editing budget")
    let origin = f.window.frame.origin
    f.surface.mouseDown(with: f.event(.leftMouseDown, center))
    let moved = NSPoint(x: center.x + 3, y: center.y)
    f.surface.mouseDragged(with: f.event(.leftMouseDragged, moved)); f.surface.mouseUp(with: f.event(.leftMouseUp, moved))
    check(f.window.frame.minX == origin.x + 3 && f.actions.isEmpty, "Budget numerical header retains drag threshold without click")
    f.surface.cancelInteraction(); f.expand()
    let selector = f.screen(NSPoint(x: 150, y: 90)), before = f.window.frame.origin
    f.surface.mouseDown(with: f.event(.leftMouseDown, selector))
    f.surface.mouseDragged(with: f.event(.leftMouseDragged, NSPoint(x: selector.x + 15, y: selector.y)))
    f.surface.mouseUp(with: f.event(.leftMouseUp, selector))
    check(f.actions.isEmpty && f.window.frame.origin == before, "Dragging a budget picker neither moves window nor selects an option")
    f.surface.menuTrackingOverride = { menu, _ in
        check(f.state.menuPresented && !f.state.pointerPressed, "Right click is a menu, not a left-click press")
        let before = f.hovers
        f.surface.mouseExited(with: f.event(.mouseMoved, NSPoint(x: -100, y: -100)))
        check(f.hovers == before, "Pointer exit during menu tracking cannot collapse preview")
        check(menu.items.contains { $0.representedObject as? String == "budgetPause" }, "Budget menu exposes pause without opening main")
        f.choose("budgetEdit", menu)
    }
    let header = f.screen(NSPoint(x: 150, y: 25))
    f.surface.rightMouseDown(with: f.event(.rightMouseDown, header))
    check(f.actions == ["budgetEdit"] && !f.state.interactionActive, "Right menu dispatches once after tracking ends")
    f.surface.mouseDown(with: f.event(.leftMouseDown, header, flags: .control)); f.surface.mouseUp(with: f.event(.leftMouseUp, header))
    check(f.actions == ["budgetEdit", "budgetEdit"], "Control-click must not also open main")
    f.surface.mouseExited(with: f.event(.mouseMoved, NSPoint(x: -100, y: -100)))
    check(f.hovers.last == false, "Hover exit resumes after context menu closes")
default: fatalError("Unknown test case")
}
print(CommandLine.arguments[1] + " passed")
''')
        cls.binary = root / "check"
        sources = Path(__file__).parents[1] / "Sources"
        result = subprocess.run(["xcrun", "swiftc", "-swift-version", "5", str(main), str(sources / "Capsule.swift"), str(sources / "CapsuleHost.swift"), "-o", str(cls.binary)], capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise AssertionError(result.stderr)

    def run_case(self, name):
        result = subprocess.run([str(self.binary), name], capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(name + " passed", result.stdout)

    def test_modes_preserve_scope_metrics_and_surface(self):
        self.run_case("mode")

    def test_content_and_budget_selectors_have_stable_targets(self):
        self.run_case("selectors")

    def test_budget_scope_is_readonly_for_mouse_and_accessibility(self):
        self.run_case("readonly")

    def test_hover_context_click_and_drag_do_not_conflict(self):
        self.run_case("interaction")
