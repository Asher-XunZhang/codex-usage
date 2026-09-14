"""Native message scope and notification completion boundaries; no notifications sent."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from tools.common.paths import macos_source


@unittest.skipUnless(shutil.which('xcrun'), 'Requires macOS Swift')
class TaskNotificationRouteTests(unittest.TestCase):
    def run_swift(self, code, files):
        with tempfile.TemporaryDirectory(prefix='notification route ') as folder:
            source = Path(folder) / 'main.swift'; source.write_text(code)
            binary = source.with_name('check')
            result = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', *(str(macos_source(f)) for f in files), str(source), '-o', str(binary)], capture_output=True, text=True, timeout=120)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = subprocess.run([str(binary)], capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_notification_actions_wait_for_ack_and_route_exact_ids(self):
        self.run_swift(r'''import AppKit
import UserNotifications
final class BudgetCoordinator {
    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse, withCompletionHandler completion: @escaping () -> Void) { completion() }
}
final class TaskMonitorService {
    var pending: [(String, MonitorObject, (MonitorObject) -> Void)] = []
    func command(_ operation: String, payload: MonitorObject, completion: @escaping (MonitorObject) -> Void) { pending.append((operation, payload, completion)) }
}
let router = UsageNotificationRouter(), service = TaskMonitorService()
router.tasks = service
var completed = 0, routes: [[String]] = []
router.openMonitor = { routes.append($0) }
router.handleTaskAction("taskRead", messageIDs: ["message-1", "message-1", "bad id"]) { completed += 1 }
precondition(completed == 0 && service.pending.count == 1)
precondition(service.pending[0].0 == "read" && service.pending[0].1["ids"] as? [String] == ["message-1"])
service.pending.removeFirst().2(["ok": true]); precondition(completed == 1)
router.handleTaskAction("taskPause30", messageIDs: []) { completed += 1 }
precondition(completed == 1 && service.pending[0].0 == "settings")
service.pending.removeFirst().2(["ok": false, "error": "fixture write failure"]); precondition(completed == 2)
router.handleTaskAction(UNNotificationDefaultActionIdentifier, messageIDs: ["message-2", "message-1"]) { completed += 1 }
precondition(routes == [["message-2", "message-1"]] && completed == 3 && service.pending.isEmpty)
router.tasks = nil
router.handleTaskAction("taskRead", messageIDs: ["message-1"]) { completed += 1 }
router.handleTaskAction(UNNotificationDismissActionIdentifier, messageIDs: []) { completed += 1 }
precondition(completed == 5 && routes.count == 1)
''', ['TaskMonitorCore.swift', 'NotificationRouter.swift'])

    def test_native_notification_scope_rejects_stale_rows_and_never_bulk_reads(self):
        self.run_swift(r'''import AppKit
let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
var actions: [(String, MonitorObject)] = []
let page = TaskMonitorPage { actions.append(($0, $1)) }
func descendants(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap { descendants($0) } }
let table = descendants(page).compactMap { $0 as? NSTableView }.first!
func button(_ title: String) -> NSButton { descendants(page).compactMap { $0 as? NSButton }.first { $0.title == title }! }
func snapshot(_ ids: [String]?) -> MonitorObject {
    var s: MonitorObject = ["section": "messages", "query": "", "rows": [["id": "message-1", "title": "test", "read": false]], "page": 0, "pages": 1, "total": 1, "summary": ["unread": 4]]
    if let ids = ids { s["messageIDs"] = ids }; return s
}
page.showMessages(["message-1"])
precondition(actions.last!.1["messageIDs"] as? [String] == ["message-1"])
page.update(snapshot(nil)); precondition(table.numberOfRows == 0, "Unscoped earlier history must not populate notification scope")
page.update(snapshot(["message-2"])); precondition(table.numberOfRows == 0)
page.update(snapshot(["message-1"])); precondition(table.numberOfRows == 1)
precondition(!actions.contains { $0.0 == "read" }, "Opening notification messages never marks them read")
let read = button("本通知全部标为已读"); _ = read.sendAction(read.action, to: read.target)
precondition(actions.last!.0 == "read" && actions.last!.1["ids"] as? [String] == ["message-1"] && actions.last!.1["all"] == nil)
page.result(["requestID": actions.last!.1["requestID"]!, "ok": true])
let all = button("返回全部历史"); _ = all.sendAction(all.action, to: all.target)
precondition(actions.last!.0 == "snapshot" && actions.last!.1["messageIDs"] == nil && all.isHidden)
page.showMessages([]); var empty = snapshot([]); empty["rows"] = [MonitorObject](); empty["total"] = 0
page.update(empty); precondition(table.numberOfRows == 0, "Missing notification IDs cannot silently open all history")
precondition(descendants(page).compactMap { $0 as? NSTextField }.contains { $0.stringValue.contains("通知关联消息已清理") })
''', ['TaskMonitorCore.swift', 'TaskMonitorUI.swift', 'ControlFeedback.swift'])

    def test_route_order_and_old_ack_cannot_replace_new_notification(self):
        from tests.macos.test_capsule_controller_interaction import declaration
        source = macos_source('TaskMonitorHost.swift').read_text()
        methods = ''.join(declaration(source, marker) for marker in [
            '    func routeTaskMonitor(', '    func sendTaskMonitorRoute()', '    func receiveTaskMonitorAction('])
        code = r'''import Foundation
var isMainWindowProcess = false
typealias Object = MonitorObject
final class Page {
    var scopes: [[String]] = []
    func showMessages(_ ids: [String]) { scopes.append(ids) }
    func showSection(_ name: String) {}
    func result(_ body: Object) {}
}
final class Windows {
    var sent: [(String, Object)] = []
    func sendToMain(_ action: String, payload: Object) { sent.append((action, payload)) }
}
final class Monitor {
    func command(_ operation: String, payload: Object, completion: @escaping (Object) -> Void) { completion(["ok": true]) }
}
final class Router { func requestTaskAuthorization() {} }
final class Host {
    var pendingTaskMonitorSection = "watches", pendingTaskMonitorRouteID = "", handledTaskMonitorRouteID = ""
    var pendingTaskMonitorRouteSequence = 0, handledTaskMonitorRouteSequence = 0
    var pendingTaskMonitorMessageIDs: [String]?, pendingTaskMonitorRoute = false
    var taskMonitorPage: Page? = Page(), taskMonitor: Monitor?, notificationRouter: Router?
    let windowProcesses = Windows()
    var sent: [(String, Object)] = []
    func openMainWindow() {}
    func openTaskMonitor() {}
    func sendHost(_ action: String, payload: Object = [:]) { sent.append((action, payload)) }
''' + methods + r'''
}
let host = Host()
host.routeTaskMonitor(section: "messages", messageIDs: ["message-A"])
let a = host.windowProcesses.sent.last!.1
host.routeTaskMonitor(section: "messages", messageIDs: ["message-B"])
let b = host.windowProcesses.sent.last!.1
_ = host.receiveTaskMonitorAction("taskMonitorRouteAck", payload: ["routeID": a["routeID"]!])
precondition(host.pendingTaskMonitorRoute && host.pendingTaskMonitorMessageIDs == ["message-B"])
_ = host.receiveTaskMonitorAction("taskMonitorRouteAck", payload: ["routeID": b["routeID"]!])
precondition(!host.pendingTaskMonitorRoute && host.pendingTaskMonitorMessageIDs == nil)
isMainWindowProcess = true
let main = Host()
_ = main.receiveTaskMonitorAction("taskMonitorRoute", payload: b)
_ = main.receiveTaskMonitorAction("taskMonitorRoute", payload: b)
_ = main.receiveTaskMonitorAction("taskMonitorRoute", payload: a)
precondition(main.taskMonitorPage!.scopes == [["message-B"]], "Late or duplicate routes cannot replace the latest notification scope")
precondition(main.sent.count == 3, "Even ignored routes acknowledge their own request ID")
'''
        self.run_swift(code, ['TaskMonitorCore.swift'])
