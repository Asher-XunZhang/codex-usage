"""Offscreen checks of the real AppKit budget page, editor and host callbacks."""
from tools.common.paths import ROOT, BACKEND, macos_source
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "Requires macOS developer tools")
class BudgetUITests(unittest.TestCase):
    def test_budget_editor_state_and_callbacks(self):
        with tempfile.TemporaryDirectory(prefix="budget native ui ") as directory:
            root = Path(directory)
            source = root / "main.swift"
            source.write_text(r'''import AppKit
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
typealias Object = [String: Any]
var actions: [(String, Object)] = []
let page = BudgetPage { actions.append(($0, $1)) }
let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 960, height: 620), styleMask: [.titled], backing: .buffered, defer: true)
window.contentView = page
func descendants(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap { descendants($0) } }
func field(_ id: String) -> NSTextField { descendants(page).compactMap { $0 as? NSTextField }.first { $0.identifier?.rawValue == id }! }
func popup(_ id: String) -> NSPopUpButton { descendants(page).compactMap { $0 as? NSPopUpButton }.first { $0.identifier?.rawValue == id }! }
func button(_ title: String) -> NSButton { descendants(page).compactMap { $0 as? NSButton }.first { $0.title == title }! }
func choose(_ id: String, _ value: String) {
    let control = popup(id)
    control.select(control.itemArray.first { $0.representedObject as? String == value }!)
    _ = control.sendAction(control.action, to: control.target)
}
func click(_ title: String) { button(title).performClick(nil) }
func lastSave() -> Object { actions.last { $0.0 == "save" }!.1 }
let rule: Object = ["id": "daily", "revision": 1, "name": "日常开发", "kind": "token", "amount": 20_000_000,
    "tokenMetric": "total", "model": "old-model", "task": "old-task", "currency": "USD", "fx": 1,
    "period": ["type": "day", "timezone": "Asia/Shanghai", "hour": 0, "minute": 0], "thresholds": [20,10,0], "enabled": true]
let summary: Object = ["id": "daily", "status": "healthy", "remaining": 10_000_000, "used": 10_000_000,
    "remainingFraction": 0.5, "remainingPercent": 50, "coverage": "complete", "periodStart": 1789056000, "periodEnd": 1789142400]
page.update(["rules": [rule], "summaries": [summary]])
page.navigate(budgetID: "daily")
precondition(page.selectedBudgetID == "daily" && !page.editing)
precondition(descendants(page).compactMap { $0 as? NSTextField }.contains { $0.stringValue == "10.00M" }, "Compact Token value must scale by one million")
click("在浮窗显示")
precondition(actions.last!.0 == "pin" && actions.last!.1["id"] as? String == "daily")
click("暂停 30 分钟")
precondition(actions.last!.0 == "pause" && actions.last!.1["durationSeconds"] as? Int == 1800)
click("本周期不再弹出")
precondition(actions.last!.1["mode"] as? String == "cycle")
click("编辑")
precondition(page.editing)
field("name").stringValue = "我的未保存草稿"
field("amount").stringValue = "12.5"
page.controlTextDidChange(Notification(name: NSControl.textDidChangeNotification, object: field("name")))
precondition(actions.last!.0 == "draftChanged")
page.update(["rules": [rule], "summaries": [summary], "models": [["id": "new-model", "label": "新模型"]], "tasks": []])
precondition(field("name").stringValue == "我的未保存草稿", "Background data must not overwrite typing")
precondition(popup("model").selectedItem?.representedObject as? String == "old-model", "A missing saved scope must remain selected")
precondition(popup("task").selectedItem?.representedObject as? String == "old-task")
page.update(["rules": [rule], "summaries": [summary], "tasks": [["id": "task-a", "label": "同名任务"], ["id": "task-b", "label": "同名任务"]]])
precondition(popup("task").itemArray.filter { $0.title == "同名任务" }.count == 2, "Same display title must preserve both task identities")
precondition(Set(popup("task").itemArray.compactMap { $0.representedObject as? String }).isSuperset(of: ["task-a", "task-b", "old-task"]))
let trackedMenu = popup("model").menu!
page.menuWillOpen(trackedMenu)
page.update(["rules": [rule], "summaries": [summary], "models": [["id": "later-model", "label": "延迟模型"]]])
precondition(!popup("model").itemArray.contains { $0.representedObject as? String == "later-model" }, "Tracking menus must not mutate under the pointer")
page.menuDidClose(trackedMenu)
page.update(["rules": [rule], "summaries": [summary], "models": [["id": "later-model", "label": "延迟模型"]]])
precondition(popup("model").itemArray.contains { $0.representedObject as? String == "later-model" })
let draft = page.snapshotDraft()!
let restored = BudgetPage { _, _ in }
restored.restoreDraft(draft)
precondition(restored.editing && ((restored.snapshotDraft()!["values"] as! Object)["name"] as? String) == "我的未保存草稿")
precondition(((restored.snapshotDraft()!["values"] as! Object)["amount"] as? String) == "12.5")
field("thresholds").stringValue = "10,10"
let savesBefore = actions.filter { $0.0 == "save" }.count
click("保存并启用")
precondition(actions.filter { $0.0 == "save" }.count == savesBefore, "Invalid thresholds must not reach host")
field("thresholds").stringValue = "20,10,0"
click("保存并启用")
let saved = lastSave()["rule"] as! Object
precondition(saved["amount"] as? Double == 12_500_000)
precondition(saved["model"] as? String == "old-model")
precondition(page.editing, "Sending a save must not imply success")
page.update(["rules": [rule], "summaries": [summary], "error": "索引暂时不可用"])
precondition(page.editing && !button("正在保存…").isEnabled, "An unrelated query error must not reject or unlock a pending save")
precondition(lastSave()["expectedRevision"] as? Int == 1)
page.acknowledgeSave(id: "daily", revision: nil, error: "模拟宿主冲突")
precondition(page.editing && field("name").stringValue == "我的未保存草稿")
click("保存并启用")
var committed = saved; committed["revision"] = 2
page.update(["rules": [committed], "summaries": [summary], "error": "索引暂时不可用"])
precondition(!page.editing && page.snapshotDraft() == nil)

// A known lower bound above the budget must not be rendered as an exact overage.
var partial = summary
partial["status"] = "exceeded"; partial["coverage"] = "partial"; partial["used"] = 21_000_000; partial["remaining"] = 0; partial["overage"] = 1_000_000
page.update(["rules": [rule], "summaries": [partial]])
precondition(descendants(page).compactMap { $0 as? NSTextField }.contains { $0.stringValue == "≥ 1.00M" })

// Money prices preserve unknown cells, accept explicit zero, and retain a fixed conversion rate.
page.navigate(budgetID: nil, create: true)
field("name").stringValue = "金额预算"
choose("kind", "money"); choose("currency", "CNY")
field("amount").stringValue = "50"; field("fx").stringValue = "7.1"
click("＋ 添加模型单价")
let priceFields = descendants(page).compactMap { $0 as? NSTextField }.filter { ($0.accessibilityLabel() ?? "").hasPrefix("模型价格") }
priceFields.first { $0.accessibilityLabel() == "模型价格 model" }!.stringValue = "example-model"
priceFields.first { $0.accessibilityLabel() == "模型价格 input" }!.stringValue = "1.23"
priceFields.first { $0.accessibilityLabel() == "模型价格 output" }!.stringValue = "0"
page.layoutSubtreeIfNeeded()
precondition(page.frame.width == 960)
for textField in priceFields { precondition(textField.convert(textField.bounds, to: page).maxX <= page.bounds.maxX + 1, "Price fields must fit minimum main window") }
click("保存并启用")
let money = lastSave()["rule"] as! Object
let prices = money["prices"] as! [Object]
precondition(money["currency"] as? String == "CNY" && money["fx"] as? Double == 7.1)
precondition(prices[0]["cachedInput"] == nil, "Missing price must not become zero")
precondition(prices[0]["output"] as? Double == 0)
page.acknowledgeSave(id: money["id"] as! String, revision: 1, error: nil)

// Official consumption cannot be selected/saved; floor is account-wide with explicit window.
page.navigate(budgetID: nil, create: true)
field("name").stringValue = "官方周余"
choose("kind", "quota")
precondition(!popup("quotaCondition").itemArray.last!.isEnabled)
precondition(!popup("model").isEnabled && !popup("task").isEnabled)
field("amount").stringValue = "15"
click("保存并启用")
let official = lastSave()["rule"] as! Object
precondition(official["amount"] as? Double == 15 && official["quotaCondition"] as? String == "floor")
precondition(official["model"] as? String == "all" && official["windowMinutes"] as? Double == 10080)
page.acknowledgeSave(id: official["id"] as! String, revision: 1, error: nil)

// Native custom date range rejects an empty period; deferred navigation does not drop a draft.
page.navigate(budgetID: nil, create: true)
field("name").stringValue = "自定义时间"
choose("period", "once")
let dates = descendants(page).compactMap { $0 as? NSDatePicker }
let start = dates.first { $0.identifier?.rawValue == "start" }!
let end = dates.first { $0.identifier?.rawValue == "end" }!
end.dateValue = start.dateValue
let beforeDates = actions.filter { $0.0 == "save" }.count
click("保存并启用")
precondition(actions.filter { $0.0 == "save" }.count == beforeDates)
page.navigate(budgetID: "daily")
precondition(page.editing && field("name").stringValue == "自定义时间")
click("取消")
precondition(!page.editing && page.selectedBudgetID == "daily")
click("删除预算…")
precondition(!actions.contains { $0.0 == "delete" }, "Delete needs deliberate second confirmation")
click("确认删除此预算")
precondition(actions.contains { $0.0 == "delete" && $0.1["id"] as? String == "daily" })
precondition(!actions.contains { $0.0 == "notify" || $0.0 == "sound" }, "Main page must never be the notification authority")
print("Budget native UI checks passed")
''')
            binary = root / "budget-ui-check"
            budget_source = macos_source('BudgetUI.swift')
            compiled = subprocess.run(
                ["xcrun", "swiftc", "-swift-version", "5", str(source), str(budget_source), str(macos_source("ControlFeedback.swift")), "-o", str(binary)],
                capture_output=True, text=True, timeout=90,
            )
            self.assertEqual(compiled.returncode, 0, compiled.stderr)
            ran = subprocess.run([str(binary)], capture_output=True, text=True, timeout=20)
            self.assertEqual(ran.returncode, 0, ran.stderr + ran.stdout)
            self.assertIn("Budget native UI checks passed", ran.stdout)
            self.assertNotIn("Unable to simultaneously satisfy constraints", ran.stderr)
