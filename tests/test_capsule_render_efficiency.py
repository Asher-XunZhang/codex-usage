"""Unchanged display state must not invalidate native drawing or quota animation."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class CapsuleRenderEfficiencyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='capsule render efficiency ')
        cls.addClassCleanup(cls.directory.cleanup)
        root = Path(cls.directory.name)
        source = root / 'main.swift'
        source.write_text(r'''import AppKit
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
func check(_ value: @autoclosure () -> Bool, _ message: String) { precondition(value(), message) }
switch CommandLine.arguments[1] {
case "observers":
    let state = CapsuleState()
    var notifications = 0
    state.changed = { notifications += 1 }
    func checkChange<T: Equatable>(_ key: ReferenceWritableKeyPath<CapsuleState, T>, _ next: T) {
        let before = notifications, initial = state[keyPath: key]
        state[keyPath: key] = initial
        check(notifications == before, "Assigning an equal display value must be silent")
        check(initial != next, "Each fixture must exercise a real change")
        state[keyPath: key] = next
        check(notifications == before + 1, "A changed display value must notify exactly once")
        state[keyPath: key] = next
        check(notifications == before + 1, "Repeated refresh values must stay silent")
    }
    checkChange(\.quotaCompact, "周余 50%")
    checkChange(\.quotaDetail, "周余 50% / 5h余 80%")
    checkChange(\.quotaFraction, 0.5)
    checkChange(\.quotaName, "周剩余")
    checkChange(\.quotaStale, true)
    checkChange(\.theme, .light)
    checkChange(\.total, "123.4K")
    checkChange(\.exact, "123,400 tokens")
    checkChange(\.context, "最近 7 天 · 示例任务")
    checkChange(\.input, "120K")
    checkChange(\.output, "3.4K")
    checkChange(\.cache, "缓存输入 100K")
    checkChange(\.status, "12:34:56 已更新")
    checkChange(\.scope, 1)
    checkChange(\.rangeDays, "7")
    checkChange(\.pinned, false)
    checkChange(\.enabled, false)
    checkChange(\.indicator, "busy")
    checkChange(\.refreshSeconds, 30)
    checkChange(\.selectedModel, "example-model")
    checkChange(\.selectedTask, "example-task")
    checkChange(\.selectedTaskLabel, "示例任务")
    check(notifications == 22, "All current display observers are covered")
    state.quotaFraction = .nan
    let invalid = notifications
    state.quotaFraction = .nan
    check(notifications == invalid && state.normalizedQuota == nil, "Repeated NaN remains the same unknown display value")
    state.quotaFraction = nil
    check(notifications == invalid + 1, "A change from invalid to absent still publishes state")
case "drawing":
    let state = CapsuleState()
    state.total = "123.4K"; state.quotaFraction = 0.5
    let surface = CapsuleSurface(state: state)
    let window = NSPanel(contentRect: surface.frame, styleMask: .borderless, backing: .buffered, defer: true)
    window.isReleasedWhenClosed = false
    let host = CapsuleHost(surface: surface); window.contentView = host
    let updateAppearance = surface.appearanceChanged
    var invalidations = 0
    surface.appearanceChanged = { invalidations += 1; updateAppearance?() }
    state.total = "123.4K"; state.quotaFraction = 0.5
    check(invalidations == 0 && !surface.isLiquidAnimating, "Equal readouts must not enter the drawing invalidation callback or animate")
    state.total = "124.5K"
    check(invalidations == 1 && surface.needsDisplay, "A changed readout must invalidate the surface")
    check((surface.accessibilityValue() as? String)?.contains("124.5K") == true, "Accessibility reflects changed readouts immediately")
    surface.expansion = 1
    state.rangeDays = "30"; state.scope = 1
    state.selectedModel = "example-model"; state.selectedTask = "example-task"; state.selectedTaskLabel = "新任务"
    state.refreshSeconds = 0
    let labels = surface.accessibilityChildren()!.map { ($0 as! NSAccessibilityElement).accessibilityLabel() ?? "" }
    check(labels.contains("浮窗统计范围，30天") && labels.contains("浮窗模型，example-model") && labels.contains("浮窗任务，新任务"), "Changed selectors must update native accessibility labels")
    check(labels.contains("自动刷新间隔，关闭"), "The actual refresh setting must stay accessible")
    state.theme = .light
    check(host.appearance?.name == .aqua && host.surface === surface, "Changed theme updates the same surface and host")
    let before = invalidations
    state.theme = .light
    check(invalidations == before, "Reselecting the current palette performs no drawing invalidation")
    window.contentView = nil
case "quota":
    // Override visibility only; never order a panel onscreen or activate an app.
    final class VisibleFixture: NSPanel { override var isVisible: Bool { true } }
    let state = CapsuleState(); state.quotaFraction = 0.8
    let surface = CapsuleSurface(state: state)
    let window = VisibleFixture(contentRect: surface.frame, styleMask: .borderless, backing: .buffered, defer: true)
    window.isReleasedWhenClosed = false; window.contentView = surface
    state.quotaFraction = 0.4
    if !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
        check(surface.isLiquidAnimating, "A real visible quota change still animates")
        RunLoop.main.run(until: Date().addingTimeInterval(0.07))
        check(surface.liquidFraction! < 0.8 && surface.liquidFraction! > 0.4, "Quota transition keeps its intermediate frames")
        let progress = surface.liquidFraction
        state.quotaFraction = 0.4
        check(surface.liquidFraction == progress && surface.isLiquidAnimating, "An equal refresh must not restart or finish a running transition")
        RunLoop.main.run(until: Date().addingTimeInterval(0.3))
    }
    check(surface.liquidFraction == 0.4 && !surface.isLiquidAnimating, "The transition reaches the changed value and stops")
    state.quotaFraction = 0.4
    check(!surface.isLiquidAnimating, "A stable quota must not create another timer")
    state.quotaFraction = nil
    check(surface.liquidFraction == nil && !surface.isLiquidAnimating, "Unknown quota clears the old level immediately")
    window.contentView = nil
default: fatalError("Unknown fixture")
}
print(CommandLine.arguments[1] + " passed")
''')
        sources = Path(__file__).parents[1] / 'Sources'
        cls.binary = root / 'check'
        built = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(source),
                                str(sources / 'Capsule.swift'), str(sources / 'CapsuleHost.swift'),
                                '-o', str(cls.binary)], capture_output=True, text=True, timeout=90)
        if built.returncode:
            raise AssertionError(built.stderr)

    def run_case(self, name):
        result = subprocess.run([str(self.binary), name], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(name + ' passed', result.stdout)

    def test_equal_display_values_are_silent_and_changed_values_notify(self):
        self.run_case('observers')

    def test_changed_readouts_theme_and_accessibility_still_update(self):
        self.run_case('drawing')

    def test_quota_transition_is_preserved_without_duplicate_animation(self):
        self.run_case('quota')


if __name__ == '__main__':
    unittest.main()
