"""Native draft navigation and fixed footer checks using isolated state and hidden windows."""
from tools.common.paths import macos_source
from pathlib import Path
import os
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class AdaptationUITests(unittest.TestCase):
    def test_multiple_drafts_receipts_and_visible_footer(self):
        harness = r'''import AppKit
typealias Object = [String: Any]
let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
let usageMacOSURL = URL(fileURLWithPath: "/fixture-no-executable")
var actions: [(String, Object)] = []
let page = BudgetPage { actions.append(($0, $1)) }
let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 960, height: 620), styleMask: [.titled], backing: .buffered, defer: true)
window.contentView = page
func descendants(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap { descendants($0) } }
func field(_ name: String) -> NSTextField { descendants(page).compactMap { $0 as? NSTextField }.first { $0.identifier?.rawValue == name }! }
func button(_ name: String) -> NSButton { descendants(page).compactMap { $0 as? NSButton }.first { $0.title == name }! }
func click(_ name: String) { let b = button(name); _ = b.sendAction(b.action, to: b.target) }
func rule(_ id: String) -> Object { ["id": id, "revision": 1, "name": id, "kind": "token", "amount": 100,
    "tokenMetric": "total", "model": "all", "task": "all", "prices": [], "currency": "USD", "fx": 1,
    "period": ["type": "day", "timezone": "UTC"], "thresholds": [20, 10, 0], "enabled": true, "source": "/fixture"] }
let state: Object = ["rules": [rule("A"), rule("B")], "source": "/fixture"]
page.update(state); page.navigate(budgetID: "A"); click("编辑")
field("name").stringValue = "A 的未保存输入"
page.navigate(budgetID: "B"); click("编辑")
field("name").stringValue = "B 的未保存输入"
page.navigate(budgetID: "A")
precondition(page.editing && field("name").stringValue == "A 的未保存输入")
let book = page.snapshotDraftBook()!
precondition((book["drafts"] as! [String: Object]).count == 2)
let restored = BudgetPage { _, _ in }
var newer = rule("A"); newer["revision"] = 7
restored.update(["rules": [newer, rule("B")], "source": "/fixture"])
restored.restoreDraftData(try JSONSerialization.data(withJSONObject: book))
precondition(restored.snapshotDraft()!["expectedRevision"] as? Int == 1, "Restoration cannot rebase stale fields onto a newer revision")
let restoredBook = restored.snapshotDraftBook()!
precondition((restoredBook["drafts"] as! [String: Object]).count == 2)
window.contentView = page; page.layoutSubtreeIfNeeded()
let save = button("保存并启用")
let frame = save.convert(save.bounds, to: page)
precondition(page.bounds.contains(frame) && frame.width > 0, "Save frame \(frame), page \(page.bounds), native insets \(save.alignmentRectInsets)")
for title in ["保留草稿并返回", "另存为新预算", "取消"] {
    let control = button(title), rect = button(title).convert(button(title).bounds, to: page)
    precondition(page.bounds.contains(rect) && rect.width > 0, "Footer \(title): \(rect) outside \(page.bounds), insets \(control.alignmentRectInsets)")
}
var ancestor = save.superview
while let view = ancestor { precondition(!(view is NSScrollView), "Save must remain outside the scrolling form"); ancestor = view.superview }
click("保存并启用")
let payload = actions.last { $0.0 == "save" }!.1
let ticket = payload["requestID"] as! String
page.acknowledgeSave(id: "A", revision: 2, error: nil, requestID: "older-request")
precondition(page.saveInFlight)
page.update(["rules": [newer, rule("B")], "source": "/fixture"])
precondition(page.saveInFlight, "Unrelated published changes are not save receipts")
page.acknowledgeSave(id: "A", revision: nil, error: "版本冲突", requestID: ticket)
precondition(page.editing && !page.saveInFlight)
click("另存为新预算")
precondition(page.snapshotDraft()!["expectedRevision"] as? Int == 0)
precondition(field("name").stringValue == "A 的未保存输入")
let corrupt = BudgetPage { _, _ in }; corrupt.restoreDraftData(Data("{bad".utf8))
precondition(corrupt.snapshotDraftBook() == nil && !corrupt.draftBlocksClosing, "Unedited corrupt data is preserved without trapping the user in the app")
precondition(descendants(corrupt).compactMap { $0 as? NSButton }.contains { $0.title.contains("备份损坏草稿") })
if let directory = ProcessInfo.processInfo.environment["CODEX_USAGE_ADAPTATION_PREVIEW_DIR"] {
    func render(_ view: NSView, _ name: String) throws {
        view.layoutSubtreeIfNeeded()
        let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds)!
        view.cacheDisplay(in: view.bounds, to: bitmap)
        try bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: directory).appendingPathComponent(name))
    }
    for (name, theme) in [("light", NSAppearance.Name.aqua), ("dark", NSAppearance.Name.darkAqua)] {
        window.appearance = NSAppearance(named: theme)
        try render(page, "budget-editor-" + name + ".png")
    }
    let updates = UpdateStatusController()
    updates.update(local: ["stamp": "2026-09-14 10:00:00", "error": "日志读取失败，上次快照已保留"],
        quota: QuotaSnapshot(error: "额度暂不可用"), enabled: true, busy: false, seconds: 30, settingsError: "刷新间隔尚未保存，修改已保留")
    updates.window!.contentView!.needsLayout = true
    updates.window!.contentView!.layoutSubtreeIfNeeded()
    precondition(updates.local.frame.height > 0 && updates.quotaRetry.frame.width > 0)
    try render(updates.window!.contentView!, "updates.png")
}
precondition(!window.isVisible)
print("native drafts, receipt identity and footer passed")
'''
        with tempfile.TemporaryDirectory(prefix='macos adaptation ui ') as directory:
            root = Path(directory)
            source = root / 'main.swift'; source.write_text(harness)
            binary = root / 'check'
            files = ['BudgetUI.swift', 'ControlFeedback.swift', 'Quota.swift', 'UpdateStatus.swift']
            result = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(source), *map(str, map(macos_source, files)), '-o', str(binary)], capture_output=True, text=True, timeout=90)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = subprocess.run([str(binary)], capture_output=True, text=True, timeout=20, env=os.environ.copy())
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('passed', result.stdout)
            if os.environ.get('CODEX_USAGE_ADAPTATION_PREVIEW_DIR'): print(result.stdout)
