"""Exercise native animation and accessibility without opening a visible window."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class NativeUITests(unittest.TestCase):
    def test_shutdown_from_main_dispatch_action_does_not_deadlock(self):
        with tempfile.TemporaryDirectory(prefix='native shutdown ') as directory:
            root = Path(directory)
            main = root / 'main.swift'
            main.write_text('''import AppKit
final class Delegate: NSObject, NSApplicationDelegate {
    let termination = AsyncTermination()
    func applicationDidFinishLaunching(_ notification: Notification) {
        DispatchQueue.main.async { NSApp.terminate(nil) }
    }
    func applicationShouldTerminate(_ app: NSApplication) -> NSApplication.TerminateReply {
        termination.request(app) { done in
            DispatchQueue.global().async {
                DispatchQueue.main.async {
                    print("cleanup completed"); fflush(stdout); done()
                }
            }
        }
    }
}
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
let delegate = Delegate(); app.delegate = delegate
withExtendedLifetime(delegate) { app.run() }
''')
            binary = root / 'check'
            source = Path(__file__).parents[1] / 'Sources/Termination.swift'
            compiled = subprocess.run(['xcrun','swiftc','-swift-version','5',str(main),str(source),'-o',str(binary)],capture_output=True,text=True,timeout=90)
            self.assertEqual(compiled.returncode,0,compiled.stderr)
            ran = subprocess.run([str(binary)],capture_output=True,text=True,timeout=10)
            self.assertEqual(ran.returncode,0,ran.stderr)
            self.assertIn('cleanup completed',ran.stdout)

    def test_capsule_animation_and_accessible_actions(self):
        with tempfile.TemporaryDirectory(prefix='native capsule ') as directory:
            root = Path(directory)
            main = root / 'main.swift'
            main.write_text('''import AppKit
let app = NSApplication.shared
var progress: [CGFloat] = []
let animation = CapsuleAnimation(duration: 0.12, animationCurve: .easeInOut)
animation.animationBlockingMode = .nonblocking
animation.frameRate = 60
animation.step = { progress.append($0) }
animation.start()
RunLoop.main.run(until: Date().addingTimeInterval(0.3))
precondition(progress.contains { $0 > 0 && $0 < 1 }, "Animation must include intermediate frames")
precondition(progress.last == 1 && !animation.isAnimating, "Animation must finish and stop its timer")
precondition(zip(progress, progress.dropFirst()).allSatisfy { $0 <= $1 }, "Animation must be monotonic")
let state = CapsuleState()
state.scope = 1; state.rangeDays = "7"
precondition(state.scopeTitle == "7天", "A selected period must be named explicitly")
state.rangeDays = "all"
precondition(state.scopeTitle == "全部")
state.scope = 0
precondition(state.scopeTitle == "今日", "Today mode must not inherit the previous filter period")
precondition(state.normalizedQuota == nil)
state.quotaFraction = .nan
precondition(state.normalizedQuota == nil, "Invalid quota must not draw an empty battery")
state.quotaFraction = -1
precondition(state.normalizedQuota == 0)
state.quotaFraction = 2
precondition(state.normalizedQuota == 1)
state.quotaFraction = 0.18
let surface = CapsuleSurface(state: state)
precondition(surface.liquidFraction == 0.18 && !surface.isLiquidAnimating, "Initial quota draws immediately without an idle animation")
let window = NSPanel(contentRect: surface.frame, styleMask: .borderless, backing: .buffered, defer: true)
let host = CapsuleHost(surface: surface)
window.contentView = host
precondition(host.subviews.count == 1 && host.subviews[0] === surface, "Dark theme must not allocate a glass effect")
state.quotaFraction = 0
precondition(surface.liquidFraction == 0 && !surface.isLiquidAnimating, "A hidden capsule must not animate")
state.quotaFraction = nil
precondition(surface.liquidFraction == nil, "Unknown quota must clear the last liquid level")
surface.updateTrackingAreas()
let tracking = surface.trackingAreas.first!
surface.frame.size = CapsuleSurface.large
surface.updateTrackingAreas()
precondition(surface.trackingAreas.count == 1 && surface.trackingAreas.first === tracking, "Resizing must keep the same tracking area so mouse exit is not lost")
let first = surface.accessibilityChildren()!.map { $0 as! NSAccessibilityElement }
let second = surface.accessibilityChildren()!.map { $0 as! NSAccessibilityElement }
precondition(first.count == 2 && first[0] === second[0], "Accessibility identities must remain stable")
var invoked = ""
surface.action = { invoked = $0 }
precondition(first[0].accessibilityPerformPress() && invoked == "details")
state.enabled = false
surface.expansion = 1
let expanded = surface.accessibilityChildren()!.map { $0 as! NSAccessibilityElement }
let refresh = expanded.first { $0.accessibilityLabel() == "正在刷新" }!
_ = refresh.accessibilityPerformPress()
precondition(invoked == "details", "Disabled refresh must not execute")
precondition(expanded.count == 14, "Expanded content, period, themes, and refresh interval must be accessible")
state.scope = 1; state.rangeDays = "30"
precondition(surface.accessibilityChildren()!.contains { ($0 as! NSAccessibilityElement).accessibilityLabel() == "浮窗统计范围，30天" }, "Period picker must announce the selected range")
state.refreshSeconds = 0
precondition(surface.accessibilityChildren()!.contains { ($0 as! NSAccessibilityElement).accessibilityLabel() == "自动刷新间隔，关闭" }, "Refresh interval must announce the actual selected value")
precondition(CapsuleTheme(storedValue: "glass") == .light, "Old glass preference migrates to light")
precondition(CapsuleTheme(storedValue: nil) == .dark)
precondition(CapsuleTheme(storedValue: "unknown") == .dark)
for _ in 0..<100 {
    state.theme = .light
    precondition(host.subviews.count == 1 && host.subviews[0] === surface && surface.window === window)
    precondition(host.appearance?.name == .aqua)
    state.total = "122K"
    precondition(host.subviews[0] === surface, "Data updates must reuse the same surface")
    state.theme = .dark
    precondition(host.subviews.count == 1 && host.subviews[0] === surface && surface.window === window)
    precondition(host.appearance?.name == .darkAqua)
}
state.theme = .light
let light = surface.accessibilityChildren()!.map { $0 as! NSAccessibilityElement }.first { $0.accessibilityLabel() == "浅色主题，已选中" }!
precondition(light.accessibilityPerformPress() && invoked == "themeLight", "Theme selection must be exposed to accessibility")
state.theme = .dark
state.total = "123.4K"
surface.expansion = 0
precondition((surface.accessibilityValue() as? String)?.contains("123.4K") == true)
precondition(surface.accessibilityChildren()!.count == 2, "Collapsed detail actions must not remain exposed")
surface.expansion = 1
let newRefresh = surface.accessibilityChildren()!.map { $0 as! NSAccessibilityElement }.first { $0.accessibilityLabel() == "正在刷新" }!
precondition(newRefresh !== refresh, "Hidden detail action cache must be released when collapsing")
surface.expansion = 0
print("Native animation and accessibility checks passed")
''')
            binary = root / 'native-check'
            source = Path(__file__).parents[1] / 'Sources/Capsule.swift'
            host_source = source.with_name('CapsuleHost.swift')
            built = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), str(source), str(host_source), '-o', str(binary)], capture_output=True, text=True, timeout=90)
            self.assertEqual(built.returncode, 0, built.stderr)
            checked = subprocess.run([str(binary)], capture_output=True, text=True, timeout=10)
            self.assertEqual(checked.returncode, 0, checked.stderr)
            self.assertIn('checks passed', checked.stdout)
