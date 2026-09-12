"""Exercise the real main-window navigation and layout without displaying an app.

Only launch identity/preferences and visible-window ordering are replaced in
temporary source copies. No startup delegate, backend, host bridge, user index,
or installed application's preferences are used.
"""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import uuid


ROOT = Path(__file__).parents[1]
SOURCES = [
    "Main.swift", "Chart.swift", "Capsule.swift", "CapsuleHost.swift",
    "StatusMenu.swift", "Quota.swift", "Runtime.swift", "WindowProcess.swift",
    "UsageChangeMonitor.swift", "Termination.swift", "BudgetCore.swift",
    "ControlFeedback.swift", "BudgetUI.swift", "BudgetHost.swift",
]
DRIVER = r'''import AppKit
import Foundation

let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
usagePreferences.set("usage", forKey: "mainPage")
usagePreferences.set("7", forKey: "filterDays")
usagePreferences.set("all", forKey: "filterModel")
usagePreferences.set("all", forKey: "filterTask")
let delegate = AppDelegate()
delegate.buildWindow()
let window = delegate.window!
let root = window.contentView!
let picker = delegate.pagePicker!
let beforeLayout = window.frame
func rect(_ r: NSRect) -> [Double] { [Double(r.minX), Double(r.minY), Double(r.width), Double(r.height)] }
func navigationPixels() -> [UInt8] {
    let bitmap = root.bitmapImageRepForCachingDisplay(in: root.bounds)!
    bitmap.bitmapData!.initialize(repeating: 0, count: bitmap.bytesPerRow * bitmap.pixelsHigh)
    root.cacheDisplay(in: root.bounds, to: bitmap)
    let frame = picker.convert(picker.bounds, to: root)
    let sx = CGFloat(bitmap.pixelsWide) / root.bounds.width
    let sy = CGFloat(bitmap.pixelsHigh) / root.bounds.height
    let x0 = max(0, Int(frame.minX * sx)), x1 = min(bitmap.pixelsWide, Int(frame.maxX * sx))
    let y0 = max(0, Int((root.bounds.maxY - frame.maxY) * sy))
    let y1 = min(bitmap.pixelsHigh, Int((root.bounds.maxY - frame.minY) * sy))
    var pixels: [UInt8] = []
    for y in y0..<y1 {
        let start = y * bitmap.bytesPerRow + x0 * bitmap.samplesPerPixel
        pixels.append(contentsOf: UnsafeBufferPointer(start: bitmap.bitmapData! + start, count: (x1-x0) * bitmap.samplesPerPixel))
    }
    return pixels
}
func snapshot(_ name: String) -> [String: Any] {
    root.layoutSubtreeIfNeeded()
    let pickerFrame = picker.convert(picker.bounds, to: root)
    let p = NSPoint(x: pickerFrame.midX, y: pickerFrame.midY)
    let hit = root.hitTest(p)
    return ["page": name, "sameWindow": delegate.window === window && delegate.window?.contentView === root, "window": rect(window.frame), "root": rect(root.bounds),
            "picker": rect(pickerFrame), "pickerIntrinsic": [Double(picker.intrinsicContentSize.width), Double(picker.intrinsicContentSize.height)],
            "pickerAmbiguous": picker.hasAmbiguousLayout, "rootAmbiguous": root.hasAmbiguousLayout,
            "pickerHidden": picker.isHiddenOrHasHiddenAncestor,
            "pickerInside": root.bounds.contains(pickerFrame) && pickerFrame.width > 0 && pickerFrame.height > 0,
            "pickerHit": hit === picker || hit?.isDescendant(of: picker) == true,
            "hitClass": hit.map { String(describing: type(of: $0)) } ?? "nil",
            "usageVisible": delegate.usagePageView?.window === window && !(delegate.usagePageView?.isHiddenOrHasHiddenAncestor ?? true),
            "budgetVisible": delegate.budgetPage?.window === window && !(delegate.budgetPage?.isHiddenOrHasHiddenAncestor ?? true),
            "selectedSegment": picker.selectedSegment, "mainPage": delegate.mainPage]
}
var states = [snapshot("usage-initial")]
picker.selectedSegment = 1
let budgetAction = picker.sendAction(picker.action, to: picker.target)
states.append(snapshot("budgets"))
let visibleNavigation = navigationPixels()
delegate.budgetPage!.isHidden = true
let uncoveredNavigation = navigationPixels()
delegate.budgetPage!.isHidden = false
let navigationPaintPreserved = visibleNavigation == uncoveredNavigation
picker.selectedSegment = 0
let usageAction = picker.sendAction(picker.action, to: picker.target)
states.append(snapshot("usage-returned"))
picker.selectedSegment = 1
_ = picker.sendAction(picker.action, to: picker.target)
let budget = delegate.budgetPage!
budget.navigate(budgetID: nil, create: true)
func descendants(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap { descendants($0) } }
let name = descendants(budget).compactMap { $0 as? NSTextField }.first { $0.identifier?.rawValue == "name" }!
name.stringValue = "保留未保存草稿"
budget.controlTextDidChange(Notification(name: NSControl.textDidChangeNotification, object: name))
window.setContentSize(NSSize(width: 1060, height: 760))
let manuallyResizedFrame = window.frame
var draftPreserved = true
for cycle in 0..<8 {
    picker.selectedSegment = 0
    _ = picker.sendAction(picker.action, to: picker.target)
    states.append(snapshot("usage-cycle-\(cycle)"))
    picker.selectedSegment = 1
    _ = picker.sendAction(picker.action, to: picker.target)
    states.append(snapshot("budgets-cycle-\(cycle)"))
    draftPreserved = draftPreserved && budget.editing &&
        (budget.snapshotDraft()?["values"] as? [String: Any])?["name"] as? String == "保留未保存草稿"
}
// Exact restoration applies to a frame that fits the current screen. The CI
// desktop can be smaller than the intentionally wide layout fixture above.
let restorationVisibleFrame = (window.screen ?? NSScreen.main)!.visibleFrame
let restorableSize = NSSize(width: min(manuallyResizedFrame.width, restorationVisibleFrame.width),
                            height: min(manuallyResizedFrame.height, restorationVisibleFrame.height))
let restorableX: CGFloat = restorationVisibleFrame.midX - restorableSize.width / 2
let restorableY: CGFloat = restorationVisibleFrame.midY - restorableSize.height / 2
let restorableFrame = NSRect(x: restorableX, y: restorableY, width: restorableSize.width, height: restorableSize.height)
window.setFrame(restorableFrame, display: false)
let savedFrame = window.frame
precondition(restorationVisibleFrame.contains(savedFrame), "Exact restoration requires a visible saved frame")
delegate.saveMainWindowFrame()
window.setContentSize(NSSize(width: 1200, height: 800))
delegate.restoreMainWindowFrame()
let restoredFrame = window.frame
let sharedFrameRestored = restoredFrame == savedFrame
// A frame saved on a larger display must be clamped, not restored offscreen.
usagePreferences.set(NSStringFromRect(restorationVisibleFrame.insetBy(dx: -40, dy: -40)), forKey: "mainWindowFrame")
delegate.restoreMainWindowFrame()
let oversizedFrameClamped = window.frame == restorationVisibleFrame
window.setContentSize(NSSize(width: 1040, height: 718))
let kind = descendants(budget).compactMap { $0 as? NSPopUpButton }.first { $0.identifier?.rawValue == "kind" }!
kind.select(kind.itemArray.first { $0.representedObject as? String == "money" }!)
_ = kind.sendAction(kind.action, to: kind.target)
descendants(budget).compactMap { $0 as? NSButton }.first { $0.title == "＋ 添加模型单价" }!.performClick(nil)
root.layoutSubtreeIfNeeded()
let widthBeforeChoices = window.frame.width
let longModel = String(repeating: "超长模型名称", count: 64)
let longTask = String(repeating: "超长任务名称", count: 80)
var choicesLoaded = false
// Keep synthetic choices independent of the missing test helper's cancellation.
delegate.budgetModelChoices.cancel(); delegate.budgetTaskChoices.cancel()
let choiceTimer = Timer.scheduledTimer(withTimeInterval: 0.01, repeats: false) { _ in
    budget.update(["rules": [], "summaries": [],
                   "models": [["id": "long-model", "label": longModel]],
                   "tasks": [["id": "long-task", "label": longTask]]])
    choicesLoaded = true
}
let choicesDeadline = Date().addingTimeInterval(2)
while !choicesLoaded && Date() < choicesDeadline {
    RunLoop.main.run(mode: .default, before: choicesDeadline)
}
precondition(choicesLoaded, "The delayed choice update must run before checking layout")
root.layoutSubtreeIfNeeded()
let widthAfterChoices = window.frame.width
for (key, value) in [("model", "long-model"), ("task", "long-task")] {
    let popup = descendants(budget).compactMap { $0 as? NSPopUpButton }.first { $0.identifier?.rawValue == key }!
    popup.select(popup.itemArray.first { $0.representedObject as? String == value }!)
    _ = popup.sendAction(popup.action, to: popup.target)
}
RunLoop.main.run(until: Date().addingTimeInterval(0.1))
root.layoutSubtreeIfNeeded()
let widthAfterSelection = window.frame.width
let scopeControls = descendants(budget).compactMap { $0 as? NSPopUpButton }.filter { ["model", "task"].contains($0.identifier?.rawValue ?? "") }
let scopeWidths = scopeControls.map { Double($0.alignmentRect(forFrame: $0.frame).width) }
let selectedChoices = scopeControls.compactMap { $0.selectedItem?.representedObject as? String }
let controls = descendants(budget).compactMap { $0 as? NSControl }.filter { !$0.isHiddenOrHasHiddenAncestor }
let outside = controls.compactMap { control -> [String: Any]? in
    // Native text labels draw two optical points beyond their layout edge.
    // Check AppKit's alignment rectangle, not those deliberate frame insets.
    let aligned = control.alignmentRect(forFrame: control.frame)
    let frame = control.superview!.convert(aligned, to: budget)
    guard frame.minX < -1 || frame.maxX > budget.bounds.maxX + 1 else { return nil }
    return ["name": control.identifier?.rawValue ?? control.accessibilityLabel() ?? String(describing: type(of: control)), "frame": rect(frame)]
}
let report: [String: Any] = ["beforeLayout": rect(beforeLayout), "states": states,
                           "budgetAction": budgetAction, "usageAction": usageAction,
                           "draftPreserved": draftPreserved, "manualFrame": rect(manuallyResizedFrame), "sharedFrameRestored": sharedFrameRestored,
                           "restorationVisibleFrame": rect(restorationVisibleFrame), "savedFrame": rect(savedFrame), "restoredFrame": rect(restoredFrame),
                           "oversizedFrameClamped": oversizedFrameClamped,
                           "filtersPreserved": delegate.days == "7" && delegate.model == "all" && delegate.task == "all",
                           "navigationPaintPreserved": navigationPaintPreserved,
                           "minimumWidth": Double(root.bounds.width), "controlsOutsideMinimumWidth": outside,
                           "choicesLoaded": choicesLoaded, "widthBeforeChoices": Double(widthBeforeChoices),
                           "widthAfterChoices": Double(widthAfterChoices), "widthAfterSelection": Double(widthAfterSelection),
                           "scopeWidths": scopeWidths, "selectedChoices": selectedChoices,
                           "backendStarted": delegate.backend.process != nil || delegate.backend.url != nil,
                           "windowVisible": window.isVisible]
let data = try JSONSerialization.data(withJSONObject: report, options: [.sortedKeys])
print(String(data: data, encoding: .utf8)!)
window.delegate = nil
window.close()
usagePreferences.removePersistentDomain(forName: ProcessInfo.processInfo.environment["CODEX_USAGE_TEST_SUITE"]!)
'''


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "Requires macOS developer tools")
class BudgetNavigationTests(unittest.TestCase):
    def test_main_window_navigation_is_visible_clickable_and_bounded(self):
        with tempfile.TemporaryDirectory(prefix="budget navigation ") as directory:
            root = Path(directory)
            suite = "local.codex-usage.tests.navigation." + uuid.uuid4().hex
            for name in SOURCES:
                text = (ROOT / "Sources" / name).read_text()
                if name == "WindowProcess.swift":
                    text, count = re.subn(
                        r"let usagePreferences: UserDefaults = \{.*?\}\(\)",
                        'let usagePreferences = UserDefaults(suiteName: ProcessInfo.processInfo.environment["CODEX_USAGE_TEST_SUITE"]!)!',
                        text, count=1, flags=re.S,
                    )
                    self.assertEqual(count, 1)
                    text, count = re.subn(
                        r'let isMainWindowProcess = [^\n]+',
                        'let isMainWindowProcess = true', text, count=1,
                    )
                    self.assertEqual(count, 1)
                elif name == "Main.swift":
                    text = text.replace('window.makeKeyAndOrderFront(nil)', '/* Test: keep the real window offscreen. */')
                    text = text.replace('window.setFrameAutosaveName("CodexUsageMain")', '/* Test: do not restore a persisted frame. */')
                (root / name).write_text(text)
            entry = root / "Entry"
            entry.mkdir()
            (entry / "main.swift").write_text(DRIVER)
            native = root / "UsageNative.o"
            subprocess.run(
                ["xcrun", "clang", "-Os", "-Wno-deprecated-declarations", "-c",
                 str(ROOT / "Sources/UsageNative.c"), "-o", str(native)],
                check=True, capture_output=True, text=True, timeout=30,
            )
            binary = root / "navigation-check"
            compiled = subprocess.run(
                ["xcrun", "swiftc", "-swift-version", "5", "-whole-module-optimization", "-import-objc-header",
                 str(ROOT / "Sources/UsageNative.h"), str(entry / "main.swift"),
                 *[str(root / name) for name in SOURCES], str(native), "-o", str(binary)],
                capture_output=True, text=True, timeout=180,
            )
            self.assertEqual(compiled.returncode, 0, compiled.stderr)
            env = dict(os.environ, CODEX_USAGE_TEST_SUITE=suite,
                       CODEX_USAGE_DESKTOP_BASE=str(root / "support"), CODEX_HOME=str(root / "codex"))
            result = subprocess.run([str(binary)], env=env, capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            report = json.loads(result.stdout.strip().splitlines()[-1])
            evidence = json.dumps(report, ensure_ascii=False, indent=2)
            print(evidence)
            self.assertFalse(report["backendStarted"], evidence)
            self.assertFalse(report["windowVisible"], evidence)
            self.assertTrue(report["budgetAction"] and report["usageAction"], evidence)
            self.assertTrue(report["draftPreserved"] and report["filtersPreserved"], evidence)
            self.assertTrue(report["navigationPaintPreserved"], evidence)
            self.assertLessEqual(report["minimumWidth"], 1041, evidence)
            self.assertTrue(report["sharedFrameRestored"], evidence)
            self.assertTrue(report["oversizedFrameClamped"], evidence)
            self.assertEqual(report["controlsOutsideMinimumWidth"], [], evidence)
            self.assertTrue(report["choicesLoaded"], evidence)
            self.assertCountEqual(report["selectedChoices"], ["long-model", "long-task"], evidence)
            self.assertTrue(all(width <= 361 for width in report["scopeWidths"]), evidence)
            self.assertAlmostEqual(report["widthBeforeChoices"], report["widthAfterChoices"], delta=1, msg=evidence)
            self.assertAlmostEqual(report["widthBeforeChoices"], report["widthAfterSelection"], delta=1, msg=evidence)
            for state in report["states"]:
                page = "usage" if state["page"].startswith("usage") else "budgets"
                with self.subTest(page=state["page"]):
                    self.assertEqual(state["mainPage"], page, evidence)
                    self.assertTrue(state["sameWindow"], evidence)
                    self.assertEqual(state["usageVisible"], page == "usage", evidence)
                    self.assertEqual(state["budgetVisible"], page == "budgets", evidence)
                    self.assertFalse(state["pickerHidden"], evidence)
                    self.assertTrue(state["pickerInside"], evidence)
                    self.assertTrue(state["pickerHit"], evidence)
                    self.assertFalse(state["pickerAmbiguous"], evidence)
                    self.assertFalse(state["rootAmbiguous"], evidence)
                    expected = report["manualFrame"] if "cycle-" in state["page"] else report["beforeLayout"]
                    self.assertEqual(state["window"], expected, evidence)
