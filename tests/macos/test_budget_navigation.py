"""Exercise the real main-window navigation and layout without displaying an app.

Only launch identity/preferences and visible-window ordering are replaced in
temporary source copies. No startup delegate, backend, host bridge, user index,
or installed application's preferences are used.
"""
from tools.common.paths import ROOT, BACKEND, macos_source
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


SOURCES = [
    "Main.swift", "MainState.swift", "Settings.swift", "UpdateStatus.swift", "Chart.swift", "Capsule.swift", "CapsuleHost.swift",
    "CapsulePlacement.swift", "CapsuleDocking.swift", "TaskMonitorCore.swift", "TaskLogReader.swift", "TaskMonitorService.swift",
    "TaskMonitorUI.swift", "TaskMonitorHost.swift", "NotificationRouter.swift",
    "StatusDetail.swift", "StatusMenu.swift", "Quota.swift", "BackendEvents.swift", "Runtime.swift", "WindowProcess.swift",
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
            "monitorVisible": delegate.taskMonitorPage?.window === window && !(delegate.taskMonitorPage?.isHiddenOrHasHiddenAncestor ?? true),
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
    picker.selectedSegment = 2
    _ = picker.sendAction(picker.action, to: picker.target)
    states.append(snapshot("monitor-cycle-\(cycle)"))
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

// Native visual QA for the newly shared settings and snapshot-only popover.
func review(_ name: String, _ view: NSView) {
    guard let folder = ProcessInfo.processInfo.environment["CODEX_USAGE_REVIEW_OUT"] else { return }
    view.layoutSubtreeIfNeeded()
    let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds)!
    bitmap.bitmapData!.initialize(repeating: 0, count: bitmap.bytesPerRow * bitmap.pixelsHigh)
    view.cacheDisplay(in: view.bounds, to: bitmap)
    let canvas = NSImage(size: view.bounds.size); canvas.lockFocus()
    NSColor(calibratedWhite: 0.96, alpha: 1).setFill(); NSRect(origin: .zero, size: view.bounds.size).fill()
    let foreground = NSImage(size: view.bounds.size); foreground.addRepresentation(bitmap)
    foreground.draw(in: NSRect(origin: .zero, size: view.bounds.size), from: .zero, operation: .sourceOver, fraction: 1)
    canvas.unlockFocus()
    let composed = NSBitmapImageRep(data: canvas.tiffRepresentation!)!
    try! composed.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: folder).appendingPathComponent(name + ".png"))
}
let settings = UsageSettingsController()
let dataSettings = UpdateStatusController(embedded: true)
settings.mountData(dataSettings.content)
settings.update(usagePreferences, monitor: ["settings": ["delivery": "system", "defaultMode": "once", "retentionDays": 30, "notifyCompleted": true, "notifyFailures": true]])
let settingsRoot = settings.window!.contentView!
let tabs = descendants(settingsRoot).compactMap { $0 as? NSTabView }.first!
precondition(tabs.tabViewItems.count == 4, "Settings exposes four pages")
let reminderPage = tabs.tabViewItems.first { $0.identifier as? String == "reminders" }!.view!
let retention = descendants(reminderPage).compactMap { $0 as? NSPopUpButton }.first { $0.identifier?.rawValue == "retentionDays" }!
precondition(retention.selectedItem?.representedObject as? String == "30", "Settings must show the stored retention rather than a popup default")
for page in tabs.tabViewItems {
    tabs.selectTabViewItem(page); settingsRoot.layoutSubtreeIfNeeded()
    precondition(!settingsRoot.hasAmbiguousLayout, "Settings outer layout must be determined")
    review("settings-" + (page.identifier as! String), settingsRoot)
}
precondition(dataSettings.window == nil && !settings.window!.isVisible, "Embedded status creates no extra window and tests show no UI")
let popover = StatusDetailController()
popover.update(summary: ["total_tokens": 1280000, "input_tokens": 1120000, "cached_input_tokens": 1050000, "output_tokens": 160000],
    quota: QuotaSnapshot(windows: [QuotaWindow(label: "每周", remaining: 68, resetsAt: Date().addingTimeInterval(3600))], updated: Date()),
    monitor: ["active": 3, "unread": 2], stamp: "演示数据 · 14:30")
let popoverWindow = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 360, height: 390), styleMask: [.borderless], backing: .buffered, defer: false)
popoverWindow.isReleasedWhenClosed = false; popoverWindow.contentView = popover.view
review("menu-detail", popover.view)
let selectable = descendants(popover.view).compactMap { $0 as? NSTextField }.filter { $0.isSelectable }
precondition(selectable.count >= 5, "Detail fields are selectable without read-triggered queries")
delegate.switchMainPage("monitor")
delegate.taskMonitorPage?.update(["section": "watches", "query": "", "page": 0, "pages": 1, "total": 3,
    "summary": ["active": 3, "unread": 2], "sourceStatus": ["scanning": false],
    "rows": [["id": "demo-1", "title": "修复预算筛选后的统计范围", "project": "codex-usage", "status": "running", "mode": "once", "active": true],
             ["id": "demo-2", "title": "检查发布打包流程", "project": "desktop-tools", "status": "idle", "mode": "each", "active": true],
             ["id": "demo-3", "title": "任务日志暂时不可读取", "project": "sample-project", "status": "unknown", "sourceError": "等待来源恢复，未推断为结束", "active": true]]])
review("monitor-native", root)
let selector = TaskMonitorPage(selecting: true) { _, _ in }
let selectorWindow = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 840, height: 520), styleMask: [.borderless], backing: .buffered, defer: false)
selectorWindow.isReleasedWhenClosed = false; selectorWindow.contentView = selector
selector.update(["section": "tasks", "query": "", "page": 0, "pages": 1, "total": 1, "rows": [["id": "demo-1", "title": "示例：正在执行的本轮", "turnID": "turn-1", "status": "running", "selectable": true]]])
review("monitor-selector", selector)
selectorWindow.contentView = nil; selectorWindow.close(); popoverWindow.contentView = nil; popoverWindow.close()
settings.window?.close()

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
                text = (macos_source(name)).read_text()
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
                 str(macos_source('UsageNative.c')), "-o", str(native)],
                check=True, capture_output=True, text=True, timeout=30,
            )
            binary = root / "navigation-check"
            compiled = subprocess.run(
                ["xcrun", "swiftc", "-swift-version", "5", "-whole-module-optimization", "-import-objc-header",
                 str(macos_source('UsageNative.h')), str(entry / "main.swift"),
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
                page = "usage" if state["page"].startswith("usage") else "monitor" if state["page"].startswith("monitor") else "budgets"
                with self.subTest(page=state["page"]):
                    self.assertEqual(state["mainPage"], page, evidence)
                    self.assertTrue(state["sameWindow"], evidence)
                    self.assertEqual(state["usageVisible"], page == "usage", evidence)
                    self.assertEqual(state["budgetVisible"], page == "budgets", evidence)
                    self.assertEqual(state["monitorVisible"], page == "monitor", evidence)
                    self.assertFalse(state["pickerHidden"], evidence)
                    self.assertTrue(state["pickerInside"], evidence)
                    self.assertTrue(state["pickerHit"], evidence)
                    self.assertFalse(state["pickerAmbiguous"], evidence)
                    self.assertFalse(state["rootAmbiguous"], evidence)
                    expected = report["manualFrame"] if "cycle-" in state["page"] else report["beforeLayout"]
                    self.assertEqual(state["window"], expected, evidence)
