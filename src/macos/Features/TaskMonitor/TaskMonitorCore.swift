import Foundation
import CryptoKit

typealias MonitorObject = [String: Any]

enum MonitorJSON {
    static func text(_ value: Any?, limit: Int = 256) -> String {
        String((value as? String ?? "").unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }.prefix(limit))
    }
    static func validID(_ value: String) -> Bool {
        !value.isEmpty && value.utf8.count <= 128 && value.utf8.allSatisfy { (48...57).contains($0) || (65...90).contains($0) || (97...122).contains($0) || $0 == 45 || $0 == 95 }
    }
    static func notificationIDs(_ value: Any?) -> [String] {
        var seen = Set<String>()
        return Array((value as? [String] ?? []).prefix(20).filter { validID($0) && seen.insert($0).inserted })
    }
    static func timestamp(_ value: Any?) -> Double? {
        if let number = value as? NSNumber, number.doubleValue.isFinite, number.doubleValue > 0 {
            return number.doubleValue > 100_000_000_000 ? number.doubleValue / 1000 : number.doubleValue
        }
        guard let string = value as? String else { return nil }
        let formatter = ISO8601DateFormatter(); formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: string) { return date.timeIntervalSince1970 }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: string)?.timeIntervalSince1970
    }
    static func digest(_ value: String) -> String { SHA256.hash(data: Data(value.utf8)).map { String(format: "%02x", $0) }.joined() }
}

struct MonitorTurn: Codable, Equatable {
    var id: String
    var status: String
    var startedAt: Double?
    var updatedAt: Double
    var durationMs: Double?
    var terminal: Bool { status == "completed" || status == "interrupted" }
    var object: MonitorObject {
        var result: MonitorObject = ["turnID": id, "status": status, "updatedAt": updatedAt]
        if let at = startedAt { result["startedAt"] = at }
        if let duration = durationMs { result["durationMs"] = duration }
        return result
    }
}

struct MonitoredTask: Codable {
    var id: String
    var home: String
    var path: String
    var parentID: String
    var title: String
    var project: String
    var turnID = ""
    var turns: [String: MonitorTurn] = [:]
    var error = ""
    var current: MonitorTurn? { turns[turnID] }
    var object: MonitorObject {
        var result = current?.object ?? ["turnID": "", "status": "unknown", "updatedAt": 0]
        result.merge(["id": id, "title": title, "project": project, "parentID": parentID, "sourceError": error,
                      "selectable": parentID.isEmpty && error.isEmpty && current?.status == "running"]) { _, new in new }
        if !error.isEmpty { result["lastConfirmedStatus"] = result["status"]; result["status"] = "unknown" }
        return result
    }
    /// Metadata time never gates a lifecycle event. Only explicit start events
    /// choose newer execution, so delayed terminals cannot rewind another turn.
    mutating func consume(_ record: MonitorObject) -> MonitorTurn? {
        guard parentID.isEmpty, record["type"] as? String == "event_msg", let payload = record["payload"] as? MonitorObject,
              let kind = payload["type"] as? String, ["task_started", "task_complete", "turn_aborted"].contains(kind) else { return nil }
        let id = payload["turn_id"] as? String ?? ""
        guard MonitorJSON.validID(id) else { error = "生命周期记录缺少轮次标识，状态待确认"; return nil }
        guard kind != "turn_aborted" || payload["reason"] as? String == "interrupted" else { error = "尚不支持的中止原因，状态待确认"; return nil }
        let start = kind == "task_started"
        guard let at = MonitorJSON.timestamp(payload[start ? "started_at" : "completed_at"]) ?? MonitorJSON.timestamp(record["timestamp"]) else {
            error = "生命周期记录缺少有效时间，状态待确认"; return nil
        }
        let old = turns[id]
        if start && old?.terminal == true {
            if turns[id]?.startedAt == nil { turns[id]?.startedAt = at }
            return nil
        }
        if let old = old, at < old.updatedAt { return nil }
        let status = start ? "running" : (kind == "task_complete" ? "completed" : "interrupted")
        let duration = (payload["duration_ms"] as? NSNumber)?.doubleValue
        let next = MonitorTurn(id: id, status: status, startedAt: start ? at : (MonitorJSON.timestamp(payload["started_at"]) ?? old?.startedAt),
                               updatedAt: at, durationMs: duration.flatMap { $0.isFinite && $0 >= 0 ? $0 : nil })
        turns[id] = next
        if turnID.isEmpty || turnID == id || (start && at >= (current?.startedAt ?? current?.updatedAt ?? 0)) {
            turnID = id; error = ""
        }
        return next
    }
}

/// Serial-queue owned authoritative watch/message store. It contains lifecycle
/// metadata only; conversation text and raw JSONL bytes are never persisted.
final class TaskMonitorStore {
    struct Failure: LocalizedError { let text: String; var errorDescription: String? { text } }
    let url: URL
    private(set) var watches: [MonitorObject] = []
    private(set) var messages: [MonitorObject] = []
    private(set) var settings: MonitorObject = ["delivery": "system", "sound": false, "hideNames": false, "notifyCompleted": true,
        "notifyFailures": true, "pausedUntil": 0.0, "defaultMode": "once", "retentionDays": 30, "focusID": ""]
    private(set) var error = ""
    private(set) var corrupt = false
    private(set) var recoveryBackup = ""
    private var seen = Set<String>()
    private var capacityError = ""
    var checkpoints: [MonitorObject] = []
    init(url: URL) {
        self.url = url
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        do {
            let size = (try url.resourceValues(forKeys: [.fileSizeKey])).fileSize ?? 0
            guard size <= 32 * 1024 * 1024,
                  let saved = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? MonitorObject,
                  saved["version"] as? Int == 1, let watches = saved["watches"] as? [MonitorObject], watches.count <= 200,
                  let messages = saved["messages"] as? [MonitorObject], messages.count <= 20000,
                  let checkpoints = saved["checkpoints"] as? [MonitorObject], checkpoints.count <= 512,
                  let settings = saved["settings"] as? MonitorObject else { throw Failure(text: "监控文件格式不正确") }
            guard Set(watches.compactMap { $0["id"] as? String }).count == watches.count,
                  watches.allSatisfy({ MonitorJSON.validID($0["id"] as? String ?? "") && MonitorJSON.validID($0["turnID"] as? String ?? "") && ["once", "each"].contains($0["mode"] as? String ?? "") && ($0["home"] as? String)?.isEmpty == false && $0["active"] is Bool }),
                  Set(messages.compactMap { $0["id"] as? String }).count == messages.count,
                  messages.allSatisfy({ MonitorJSON.validID($0["taskID"] as? String ?? "") && MonitorJSON.validID($0["turnID"] as? String ?? "") && $0["read"] is Bool }) else { throw Failure(text: "监控记录无效或重复") }
            self.settings = try validatedSettings(settings)
            self.watches = watches; self.messages = messages; self.checkpoints = checkpoints
            let recorded = saved["seen"] as? [String] ?? []
            guard recorded.count <= 100000, recorded.allSatisfy({ $0.utf8.count <= 80 }) else { throw Failure(text: "监控去重记录超过安全读取上限") }
            seen = Set(recorded); seen.formUnion(messages.compactMap { $0["id"] as? String })
            for i in self.messages.indices where ["pending", "sending"].contains(self.messages[i]["delivery"] as? String ?? "") {
                self.messages[i]["delivery"] = "suppressed"; self.messages[i]["offline"] = true; self.messages[i]["reason"] = "应用重启后仅保留消息"
            }
            for i in self.watches.indices where self.watches[i]["active"] as? Bool == true {
                self.watches[i]["lastConfirmedStatus"] = self.watches[i]["status"]
                self.watches[i]["status"] = "unknown"; self.watches[i]["sourceError"] = "正在重新核对已保存的任务状态"
            }
        } catch { corrupt = true; self.error = "监控文件无法读取，原文件已保留：" + error.localizedDescription }
    }
    var pending: [MonitorObject] { error.isEmpty ? messages.filter { $0["delivery"] as? String == "pending" } : [] }
    func save() -> Bool {
        guard !corrupt else { return false }
        do {
            let saved: MonitorObject = ["version": 1, "settings": settings, "watches": watches, "messages": messages, "checkpoints": checkpoints, "seen": Array(seen)]
            let data = try JSONSerialization.data(withJSONObject: saved, options: [.sortedKeys])
            guard data.count <= 32 * 1024 * 1024 else { throw Failure(text: "监控文件已达上限，请清理已读历史") }
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try data.write(to: url, options: [.atomic])
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            error = capacityError; return true
        } catch { self.error = "状态尚未可靠保存，操作意图已保留：" + error.localizedDescription; return false }
    }
    private func suppression(status: String, now: Double) -> String {
        if settings["delivery"] as? String == "markers" { return "仅应用内标记" }
        let pause = (settings["pausedUntil"] as? NSNumber)?.doubleValue ?? 0
        if pause < 0 || pause > now { return "用户已暂停任务通知" }
        if settings[status == "completed" ? "notifyCompleted" : "notifyFailures"] as? Bool == false { return "此类任务通知已关闭" }
        return ""
    }
    func observe(_ task: MonitoredTask, turn: MonitorTurn, offline: Bool, now: Double) {
        guard task.parentID.isEmpty, let i = watches.firstIndex(where: { $0["id"] as? String == task.id && $0["home"] as? String == task.home && $0["active"] as? Bool == true }) else { return }
        let same = watches[i]["turnID"] as? String == turn.id
        let each = watches[i]["mode"] as? String == "each"
        let subscribed = (watches[i]["subscribedAt"] as? NSNumber)?.doubleValue ?? now
        guard same || (each && turn.updatedAt >= subscribed) else { return }
        if turn.terminal {
            let id = "turn-" + MonitorJSON.digest(task.home + "\n" + task.id + "\n" + turn.id).prefix(32)
            if !seen.contains(id) {
                guard messages.count < 20000, seen.count < 100000 else {
                    capacityError = messages.count >= 20000 ? "消息达到存储上限，请先标记并清理已读历史；未确认结果将重新核对" : "去重记录已达安全上限，监控结果暂未确认；请备份监控文件后重建监控存储"
                    error = capacityError; return
                }
                capacityError = ""
                let reason = offline ? "离线期间发生，仅补记消息" : suppression(status: turn.status, now: now)
                var message: MonitorObject = ["id": id, "taskID": task.id, "turnID": turn.id, "title": task.title, "project": task.project,
                    "status": turn.status, "text": turn.status == "completed" ? "所选任务的本轮已结束" : "所选任务的本轮已中断",
                    "createdAt": turn.updatedAt, "receivedAt": now, "read": false, "offline": offline,
                    "delivery": reason.isEmpty ? "pending" : "suppressed", "reason": reason]
                if let duration = turn.durationMs { message["durationMs"] = duration }
                messages.append(message); seen.insert(id)
            }
        }
        // Keep a continuous watch on the new turn when an old terminal arrives.
        if same || turn.id == task.turnID {
            watches[i].merge(turn.object) { _, new in new }
            watches[i]["title"] = task.title; watches[i]["project"] = task.project; watches[i]["sourceError"] = task.error
            if turn.terminal { watches[i]["active"] = each; if each { watches[i]["status"] = "idle" } }
        }
    }
    func reconcile(_ task: MonitoredTask, offline: Bool, now: Double) {
        guard let i = watches.firstIndex(where: { $0["id"] as? String == task.id && $0["home"] as? String == task.home }) else { return }
        watches[i]["title"] = task.title; watches[i]["project"] = task.project; watches[i]["path"] = task.path
        guard watches[i]["active"] as? Bool == true else { return }
        if !task.error.isEmpty {
            if watches[i]["status"] as? String != "unknown" { watches[i]["lastConfirmedStatus"] = watches[i]["status"] }
            watches[i]["status"] = "unknown"; watches[i]["sourceError"] = task.error
        } else if let turn = task.turns[watches[i]["turnID"] as? String ?? ""] { observe(task, turn: turn, offline: offline, now: now) }
    }
    func apply(_ operation: String, payload: MonitorObject, tasks: [MonitoredTask], home: String, now: Double) -> MonitorObject {
        do {
            if operation == "recover" { return try recover(confirm: payload["confirm"] as? Bool == true) }
            guard !corrupt else { throw Failure(text: error) }
            var reply: MonitorObject = ["ok": true, "message": "已更新"]
            switch operation {
            case "add":
                let selections = payload["selections"] as? [MonitorObject] ?? []
                let mode = payload["mode"] as? String ?? settings["defaultMode"] as? String ?? "once"
                guard (1...100).contains(selections.count), ["once", "each"].contains(mode) else { throw Failure(text: "请选择 1 至 100 项任务及有效提醒策略") }
                var results: [MonitorObject] = []
                for selection in selections {
                    let id = selection["id"] as? String ?? "", turnID = selection["turnID"] as? String ?? ""
                    guard let task = tasks.first(where: { $0.id == id && $0.home == home && $0.parentID.isEmpty }), task.error.isEmpty, let turn = task.turns[turnID] else {
                        results.append(["id": id, "ok": false, "error": "任务或轮次来源不可用，请重新选择"]); continue
                    }
                    guard turn.terminal || (task.turnID == turnID && turn.status == "running") else {
                        results.append(["id": id, "ok": false, "error": "所选轮次已变化，请重新选择"]); continue
                    }
                    if let existing = watches.first(where: { $0["id"] as? String == id && $0["active"] as? Bool == true }) {
                        let same = existing["turnID"] as? String == turnID && existing["home"] as? String == home
                        results.append(["id": id, "ok": same, "error": same ? "" : "已有不同轮次或目录的活动订阅"]); continue
                    }
                    guard watches.count < 100 || watches.contains(where: { $0["id"] as? String == id }) else { results.append(["id": id, "ok": false, "error": "最多保留 100 项监控"]); continue }
                    watches.removeAll { $0["id"] as? String == id }
                    var watch = turn.object
                    watch.merge(["id": id, "home": home, "path": task.path, "title": task.title, "project": task.project,
                                 "mode": turn.terminal ? "once" : mode, "active": true, "subscribedAt": now, "sourceError": ""]) { _, new in new }
                    watches.append(watch)
                    if turn.terminal { observe(task, turn: turn, offline: true, now: now) }
                    results.append(["id": id, "ok": true, "ended": turn.terminal])
                }
                let success = results.filter { $0["ok"] as? Bool == true }.count
                reply = ["ok": success > 0, "partial": success > 0 && success < selections.count, "items": results, "message": "已处理 \(success) 项提醒"]
            case "stop":
                let ids = Set((payload["ids"] as? [String] ?? []) + [payload["id"] as? String ?? ""])
                watches.removeAll { ids.contains($0["id"] as? String ?? "") }
                for i in messages.indices where ids.contains(messages[i]["taskID"] as? String ?? "") && messages[i]["delivery"] as? String == "pending" {
                    messages[i]["delivery"] = "suppressed"; messages[i]["reason"] = "已停止此任务提醒"
                }
                if ids.contains(settings["focusID"] as? String ?? "") { settings["focusID"] = "" }
                reply["message"] = "已停止提醒，Codex 继续执行"
            case "read":
                let ids = Set(payload["ids"] as? [String] ?? [])
                for i in messages.indices where ids.contains(messages[i]["id"] as? String ?? "") || payload["all"] as? Bool == true {
                    messages[i]["read"] = true
                    if messages[i]["delivery"] as? String == "pending" { messages[i]["delivery"] = "suppressed"; messages[i]["reason"] = "已在应用内阅读" }
                }
            case "mode":
                let id = payload["id"] as? String ?? "", mode = payload["mode"] as? String ?? ""
                guard ["once", "each"].contains(mode), let index = watches.firstIndex(where: { $0["id"] as? String == id && $0["active"] as? Bool == true }) else { throw Failure(text: "请选择活动监控及有效提醒策略") }
                guard mode != "once" || watches[index]["status"] as? String != "idle" else { throw Failure(text: "正在等待下一轮，请在开始后选择仅本轮") }
                watches[index]["mode"] = mode
            case "focus":
                let id = payload["id"] as? String ?? ""
                guard id.isEmpty || watches.contains(where: { $0["id"] as? String == id }) else { throw Failure(text: "关注对象不在监控列表中") }
                settings["focusID"] = id
            case "settings":
                settings = try validatedSettings(payload["patch"] as? MonitorObject ?? [:])
                for i in messages.indices where messages[i]["delivery"] as? String == "pending" {
                    let reason = suppression(status: messages[i]["status"] as? String ?? "", now: now)
                    if !reason.isEmpty { messages[i]["delivery"] = "suppressed"; messages[i]["reason"] = reason }
                }
            case "clear-ended":
                watches.removeAll { $0["active"] as? Bool != true }
                if !watches.contains(where: { $0["id"] as? String == settings["focusID"] as? String }) { settings["focusID"] = "" }
            case "clear-history":
                messages.removeAll { $0["read"] as? Bool == true }
                if messages.count < 20000 && seen.count < 100000 { capacityError = "" }
            case "prepare-delivery":
                let ids = Set(payload["ids"] as? [String] ?? [])
                var eligible: [MonitorObject] = []
                for i in messages.indices where ids.contains(messages[i]["id"] as? String ?? "") && messages[i]["delivery"] as? String == "pending" {
                    let reason = suppression(status: messages[i]["status"] as? String ?? "", now: now)
                    if reason.isEmpty { messages[i]["delivery"] = "sending"; eligible.append(messages[i]) }
                    else { messages[i]["delivery"] = "suppressed"; messages[i]["reason"] = reason }
                }
                reply["eligible"] = eligible; reply["settings"] = settings
            case "delivery":
                let status = payload["status"] as? String ?? ""
                guard ["sent", "suppressed", "failed"].contains(status) else { throw Failure(text: "无效通知投递状态") }
                let ids = Set(payload["ids"] as? [String] ?? [])
                for i in messages.indices where ids.contains(messages[i]["id"] as? String ?? "") && ["pending", "sending"].contains(messages[i]["delivery"] as? String ?? "") {
                    messages[i]["delivery"] = status; messages[i]["reason"] = MonitorJSON.text(payload["reason"])
                }
            default: throw Failure(text: "未知监控操作")
            }
            cleanup(now: now)
            if !save() {
                reply["ok"] = false; reply["error"] = error
                // No system submission occurred: preserve retry eligibility in memory.
                if operation == "prepare-delivery" {
                    let ids = Set((reply["eligible"] as? [MonitorObject] ?? []).compactMap { $0["id"] as? String })
                    for i in messages.indices where ids.contains(messages[i]["id"] as? String ?? "") { messages[i]["delivery"] = "pending" }
                    reply["eligible"] = []
                }
            }
            return reply
        } catch { return ["ok": false, "error": error.localizedDescription] }
    }
    private func validatedSettings(_ patch: MonitorObject) throws -> MonitorObject {
        var result = settings
        for (key, value) in patch where settings[key] != nil {
            switch key {
            case "delivery": guard ["system", "markers"].contains(value as? String ?? "") else { throw Failure(text: "无效通知方式") }
            case "defaultMode": guard ["once", "each"].contains(value as? String ?? "") else { throw Failure(text: "无效提醒策略") }
            case "pausedUntil": guard let n = value as? NSNumber, n.doubleValue.isFinite, n.doubleValue >= -1 else { throw Failure(text: "无效暂停时间") }
            case "retentionDays": guard let n = value as? Int, (1...365).contains(n) else { throw Failure(text: "保留时间应为 1 至 365 天") }
            case "focusID": guard let id = value as? String, id.isEmpty || MonitorJSON.validID(id) else { throw Failure(text: "无效关注任务") }
            default: guard value is Bool else { throw Failure(text: "无效提醒选项") }
            }
            result[key] = value
        }
        return result
    }
    func cleanup(now: Double) {
        let cutoff = now - Double(settings["retentionDays"] as? Int ?? 30) * 86400
        messages.removeAll { $0["read"] as? Bool == true && (($0["createdAt"] as? NSNumber)?.doubleValue ?? now) < cutoff }
        if !(watches.contains { $0["id"] as? String == settings["focusID"] as? String }) { settings["focusID"] = "" }
    }
    private func recover(confirm: Bool) throws -> MonitorObject {
        guard corrupt else { return ["ok": true, "message": "监控文件无需恢复"] }
        guard confirm else { throw Failure(text: "请确认备份损坏文件并重新开始") }
        let backup = url.appendingPathExtension("damaged-" + UUID().uuidString)
        try FileManager.default.copyItem(at: url, to: backup)
        let previous = (watches, messages, checkpoints, seen)
        recoveryBackup = backup.path; watches = []; messages = []; checkpoints = []; seen = []; corrupt = false
        guard save() else {
            // Backup success is not a committed recovery. Keep the recovery
            // entry available and block normal mutations until a retry succeeds.
            (watches, messages, checkpoints, seen) = previous
            corrupt = true
            error = "原文件已备份，但恢复尚未完成，可重试恢复：" + error
            throw Failure(text: error)
        }
        return ["ok": true, "message": "原文件已备份，请重新选择监控任务", "backupPath": backup.path]
    }
}
