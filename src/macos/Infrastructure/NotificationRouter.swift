import AppKit
import UserNotifications

/// The application has one notification delegate and one category registry.
/// Budget and task actions retain independent stores and pause policies.
final class UsageNotificationRouter: NSObject, UNUserNotificationCenterDelegate {
    weak var budgets: BudgetCoordinator?
    weak var tasks: TaskMonitorService?
    var openMonitor: (([String]) -> Void)?
    private var delivering = Set<String>()
    private var stopped = false
    func start() {
        let center = UNUserNotificationCenter.current(); center.delegate = self
        center.setNotificationCategories([
            UNNotificationCategory(identifier: "budget", actions: [
                UNNotificationAction(identifier: "pause30", title: "暂停提醒 30 分钟", options: []),
                UNNotificationAction(identifier: "pauseCycle", title: "本周期不再弹出", options: [])], intentIdentifiers: [], options: []),
            UNNotificationCategory(identifier: "task-monitor", actions: [
                UNNotificationAction(identifier: "taskRead", title: "标为已读", options: []),
                UNNotificationAction(identifier: "taskPause30", title: "暂停任务通知 30 分钟", options: [])], intentIdentifiers: [], options: [])
        ])
    }
    func stop() { stopped = true; delivering = []; UNUserNotificationCenter.current().delegate = nil }
    func requestTaskAuthorization() {
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }
    func deliverTasks(_ messages: [MonitorObject], settings: MonitorObject) {
        guard !stopped else { return }
        let ids = messages.compactMap { $0["id"] as? String }.filter { !delivering.contains($0) }.prefix(20)
        guard !ids.isEmpty else { return }
        let selected = Array(ids); delivering.formUnion(selected)
        UNUserNotificationCenter.current().getNotificationSettings { [weak self] permission in
            DispatchQueue.main.async {
                guard let self = self, !self.stopped else { return }
                self.tasks?.command("prepare-delivery", payload: ["ids": selected]) { [weak self] reply in
                    guard let self = self, !self.stopped else { return }
                    guard reply["ok"] as? Bool == true else { self.delivering.subtract(selected); return }
                    let eligible = reply["eligible"] as? [MonitorObject] ?? [], settings = reply["settings"] as? MonitorObject ?? [:]
                    guard !eligible.isEmpty else { self.delivering.subtract(selected); return }
                    let eligibleIDs = eligible.compactMap { $0["id"] as? String }
                    guard permission.authorizationStatus == .authorized || permission.authorizationStatus == .provisional else {
                        self.finish(selected, eligibleIDs: eligibleIDs, status: "suppressed", reason: "系统通知未授权，结果保留在消息列表"); return
                    }
                    let content = UNMutableNotificationContent()
                    let hide = settings["hideNames"] as? Bool == true
                    content.title = eligible.count == 1 ? (hide ? "任务提醒" : MonitorJSON.text(eligible[0]["title"], limit: 80)) : "\(eligible.count) 项任务已有结果"
                    content.body = eligible.prefix(3).map { (hide || eligible.count == 1 ? "" : MonitorJSON.text($0["title"], limit: 50) + "：") + ($0["text"] as? String ?? "本轮状态已更新") }.joined(separator: "\n")
                    content.categoryIdentifier = "task-monitor"; content.userInfo = ["messageIDs": eligibleIDs]
                    content.sound = settings["sound"] as? Bool == true ? .default : nil
                    let request = UNNotificationRequest(identifier: "task-" + MonitorJSON.digest(eligibleIDs.sorted().joined(separator: "\n")).prefix(32), content: content, trigger: nil)
                    UNUserNotificationCenter.current().add(request) { error in
                        DispatchQueue.main.async { self.finish(selected, eligibleIDs: eligibleIDs, status: error == nil ? "sent" : "failed", reason: error.map { "系统投递失败：" + $0.localizedDescription } ?? "已交给系统；横幅显示受系统通知与专注模式控制") }
                    }
                }
            }
        }
    }
    private func finish(_ ids: [String], eligibleIDs: [String], status: String, reason: String) {
        guard !stopped else { return }
        tasks?.command("delivery", payload: ["ids": eligibleIDs, "status": status, "reason": reason]) { [weak self] _ in self?.delivering.subtract(ids) }
    }
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification, withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler(notification.request.content.sound == nil ? [.banner, .list] : [.banner, .list, .sound])
    }
    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse, withCompletionHandler completionHandler: @escaping () -> Void) {
        if response.notification.request.content.categoryIdentifier == "budget", let budgets = budgets {
            budgets.userNotificationCenter(center, didReceive: response, withCompletionHandler: completionHandler); return
        }
        DispatchQueue.main.async {
            guard response.notification.request.content.categoryIdentifier == "task-monitor" else { completionHandler(); return }
            self.handleTaskAction(response.actionIdentifier,
                messageIDs: MonitorJSON.notificationIDs(response.notification.request.content.userInfo["messageIDs"]),
                completion: completionHandler)
        }
    }
    /// Completion follows the store acknowledgement, so background actions are
    /// not reported complete while their durable write is still queued.
    func handleTaskAction(_ action: String, messageIDs: [String], completion: @escaping () -> Void) {
        let ids = MonitorJSON.notificationIDs(messageIDs)
        if action == UNNotificationDefaultActionIdentifier { openMonitor?(ids); completion(); return }
        guard let tasks = tasks, !stopped else { completion(); return }
        if action == "taskRead" { tasks.command("read", payload: ["ids": ids]) { _ in completion() } }
        else if action == "taskPause30" { tasks.command("settings", payload: ["patch": ["pausedUntil": Date().timeIntervalSince1970 + 1800]]) { _ in completion() } }
        else { completion() }
    }
}
