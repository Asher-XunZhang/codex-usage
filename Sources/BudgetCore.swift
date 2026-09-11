import Foundation

/// One host owns this store. UI processes submit revision-checked commands to it.
/// JSON values at the boundary keep the query helper and UI independent of AppKit.
final class BudgetStore {
    private(set) var rules: [[String: Any]] = []
    private(set) var summaries: [[String: Any]] = []
    private(set) var persistenceError: String?
    private var runtime: [String: [String: Any]] = [:]
    private var ledger: [[String: Any]] = []
    private let url: URL
    private var unreadable = false
    private let deferredFields = ["kind", "tokenMetric", "model", "task", "currency", "fx", "prices", "period", "windowMinutes", "quotaCondition"]

    var pendingAlerts: [[String: Any]] {
        ledger.filter { ($0["acknowledged"] as? Bool) != true }
    }

    var events: [[String: Any]] {
        Array(ledger.filter { $0["suppressed"] as? Bool != true }
            .sorted { (Self.number($0["createdAt"]) ?? 0) > (Self.number($1["createdAt"]) ?? 0) }.prefix(20))
    }

    init(url: URL) {
        self.url = url
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        do {
            let data = try Data(contentsOf: url)
            guard data.count <= 2_097_152,
                  let object = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  Self.integer(object["version"]) == 1,
                  let savedRules = object["rules"] as? [[String: Any]], savedRules.count <= 50,
                  let savedRuntime = object["runtime"] as? [String: [String: Any]],
                  let savedLedger = object["ledger"] as? [[String: Any]], savedLedger.count <= 1000,
                  let savedSummaries = object["summaries"] as? [[String: Any]], savedSummaries.count <= 50 else {
                throw BudgetError.invalid("预算文件格式不正确")
            }
            var ids = Set<String>()
            var canonicalRules: [[String: Any]] = []
            for row in savedRules {
                guard let id = row["id"] as? String, ids.insert(id).inserted,
                      let source = row["source"] as? String, !source.isEmpty,
                      let revision = Self.integer(row["revision"]), revision > 0 else {
                    throw BudgetError.invalid("预算文件包含重复或无效规则")
                }
                var canonical = try normalize(row, source: source)
                canonical["revision"] = revision
                for key in ["createdAt", "modifiedAt"] { if let value = Self.number(row[key]) { canonical[key] = value } }
                canonicalRules.append(canonical)
            }
            var checkedRuntime: [String: [String: Any]] = [:]
            for row in canonicalRules {
                let id = row["id"] as! String
                guard let state = savedRuntime[id] else { continue }
                var checked: [String: Any] = [:]
                if let fields = state["deferred"] as? [String: Any], let until = Self.number(state["deferredUntil"]) {
                    var merged = row
                    for key in deferredFields + ["amount"] { if let value = fields[key] { merged[key] = value } }
                    let normalized = try normalize(merged, source: row["source"] as! String)
                    var validated: [String: Any] = [:]
                    for key in deferredFields + ["amount"] where fields[key] != nil { validated[key] = normalized[key] }
                    checked["deferred"] = validated; checked["deferredUntil"] = until
                }
                if let until = Self.number(state["pausedUntil"]), let periodID = state["pausePeriod"] as? String, periodID.count <= 256 {
                    checked["pausedUntil"] = until; checked["pausePeriod"] = periodID
                }
                checkedRuntime[id] = checked
            }
            // Cached display values are replaceable, unlike configuration and notification history.
            let checkedSummaries = savedSummaries.filter { row in
                guard let id = row["id"] as? String, ids.contains(id), Self.integer(row["revision"]) != nil,
                      row["periodID"] is String, row["kind"] is String, row["name"] is String,
                      row["used"] != nil, row["remaining"] != nil, row["amount"] != nil else { return false }
                return true
            }
            rules = canonicalRules; runtime = checkedRuntime; ledger = savedLedger; summaries = checkedSummaries
        } catch {
            unreadable = true
            persistenceError = "预算配置无法读取；为保护原文件，未覆盖：\(error.localizedDescription)"
        }
    }

    /// save/upsert accepts {rule, expectedRevision, recalculateCurrent}, or a rule directly.
    /// Structural and price edits take effect next cycle unless explicitly recalculated.
    func apply(action: String, payload: [String: Any], source: String, now: Date = Date()) throws {
        guard !unreadable else { throw BudgetError.invalid(persistenceError ?? "预算文件无法读取") }
        let before = snapshot()
        do {
            switch action {
            case "save", "upsert":
                let input = payload["rule"] as? [String: Any] ?? payload
                let id = input["id"] as? String ?? UUID().uuidString
                let index = rules.firstIndex { $0["id"] as? String == id }
                let old = index.map { rules[$0] }
                if let old = old {
                    try checkRevision(payload, input: input, existing: old)
                    guard old["source"] as? String == source else {
                        throw BudgetError.invalid("预算绑定的数据目录不同；请在原目录编辑或另建规则")
                    }
                } else {
                    guard rules.count < 50 else { throw BudgetError.invalid("最多保存 50 条预算") }
                    if let revision = Self.integer(payload["expectedRevision"] ?? input["revision"]), revision != 0 {
                        throw BudgetError.conflict
                    }
                }
                var normalized = try normalize(input, source: source)
                normalized["id"] = id
                normalized["revision"] = (Self.integer(old?["revision"]) ?? 0) + 1
                normalized["createdAt"] = old?["createdAt"] ?? now.timeIntervalSince1970
                normalized["modifiedAt"] = now.timeIntervalSince1970
                if let old = old {
                    let effectiveOld = effective(old, now: now)
                    let changed = deferredFields.contains { !Self.equal(normalized[$0], effectiveOld[$0]) }
                    if changed && payload["recalculateCurrent"] as? Bool != true,
                       let period = period(effectiveOld, now: now), now < period.end {
                        var state = runtime[id] ?? [:]
                        var fields: [String: Any] = [:]
                        for key in deferredFields { fields[key] = effectiveOld[key] }
                        if !Self.equal(normalized["kind"], effectiveOld["kind"]) || !Self.equal(normalized["currency"], effectiveOld["currency"]) { fields["amount"] = effectiveOld["amount"] }
                        state["deferred"] = fields
                        state["deferredUntil"] = period.end.timeIntervalSince1970
                        runtime[id] = state
                    } else {
                        runtime[id]?["deferred"] = nil; runtime[id]?["deferredUntil"] = nil
                    }
                }
                if let index = index { rules[index] = normalized } else { rules.append(normalized) }
                summaries.removeAll { $0["id"] as? String == id }
            case "delete":
                let (index, row) = try locate(payload)
                try checkRevision(payload, input: payload, existing: row)
                let id = row["id"] as! String
                rules.remove(at: index); runtime[id] = nil
                summaries.removeAll { $0["id"] as? String == id }
                ledger.removeAll { $0["ruleID"] as? String == id }
            case "enable", "disable", "toggle":
                let (index, row) = try locate(payload)
                try checkRevision(payload, input: payload, existing: row)
                rules[index]["enabled"] = action == "toggle" ? !(row["enabled"] as? Bool ?? true) : action == "enable"
                rules[index]["revision"] = (Self.integer(row["revision"]) ?? 0) + 1
                summaries.removeAll { $0["id"] as? String == row["id"] as? String }
            case "pause":
                let (_, row) = try locate(payload)
                let id = row["id"] as! String
                guard let period = period(effective(row, now: now), now: now) else { throw BudgetError.invalid("预算周期无效") }
                let seconds = Self.number(payload["durationSeconds"] ?? payload["seconds"]) ?? 1800
                guard seconds > 0 && seconds <= 86_400 else { throw BudgetError.invalid("暂停时间必须在 1 秒到 24 小时之间") }
                var state = runtime[id] ?? [:]
                state["pausePeriod"] = period.id
                state["pausedUntil"] = (payload["mode"] as? String == "cycle" || payload["period"] as? Bool == true) ? period.end.timeIntervalSince1970 : min(now.timeIntervalSince1970 + seconds, period.end.timeIntervalSince1970)
                runtime[id] = state
            case "resume":
                let (_, row) = try locate(payload)
                let id = row["id"] as! String
                runtime[id]?["pausedUntil"] = nil; runtime[id]?["pausePeriod"] = nil
            case "acknowledge":
                let ids = Set(payload["ids"] as? [String] ?? [])
                for index in ledger.indices where ids.contains(ledger[index]["id"] as? String ?? "") {
                    ledger[index]["acknowledged"] = true
                }
            default: throw BudgetError.invalid("未知预算操作")
            }
            trimLedger()
            try persist()
        } catch {
            restore(before)
            if !(error is BudgetError) { persistenceError = error.localizedDescription }
            throw error
        }
    }

    /// Current-cycle queries only. The helper must echo all identity/boundary fields.
    func requests(source: String, now: Date = Date()) -> [[String: Any]] {
        rules.compactMap { original in
            let rule = effective(original, now: now)
            guard rule["enabled"] as? Bool != false, rule["source"] as? String == source,
                  rule["kind"] as? String != "quota", let period = period(rule, now: now), now >= period.start else { return nil }
            return ["id": rule["id"]!, "revision": rule["revision"]!, "periodID": period.id,
                    "start": period.start.timeIntervalSince1970, "end": period.end.timeIntervalSince1970,
                    "model": rule["model"]!, "task": rule["task"]!]
        }
    }

    /// Returns persisted pending alerts. Acknowledge only after successful visual delivery.
    /// nil results preserve same-cycle values, never carry them into a different period.
    func evaluate(result: [String: Any]?, quota: [String: Any]?, source: String, now: Date = Date()) -> [[String: Any]] {
        guard !unreadable else { return [] }
        let before = snapshot()
        let oldSummaries = summaries
        var next: [[String: Any]] = []
        let generated = Self.number(result?["generated_at"])
        let results = result?["results"] as? [[String: Any]] ?? []
        for original in rules {
            let rule = effective(original, now: now)
            guard let interval = period(rule, now: now), let id = rule["id"] as? String else { continue }
            let old = oldSummaries.first { $0["id"] as? String == id && $0["periodID"] as? String == interval.id && Self.integer($0["revision"]) == Self.integer(rule["revision"]) }
            var summary = old ?? baseSummary(rule, period: interval)
            summary["scheduledChange"] = (Self.number(runtime[id]?["deferredUntil"]) ?? 0) > now.timeIntervalSince1970
            let pausedUntil = Self.number(runtime[id]?["pausedUntil"]) ?? 0
            let paused = runtime[id]?["pausePeriod"] as? String == interval.id && now.timeIntervalSince1970 < pausedUntil
            summary["paused"] = paused; summary["pausedUntil"] = paused ? pausedUntil : NSNull()
            let enabled = rule["enabled"] as? Bool != false
            let sourceMatches = rule["source"] as? String == source
            if !sourceMatches {
                invalidate(&summary, status: "source_invalid", reason: "数据目录已切换；预算仍绑定原目录")
            } else if !enabled {
                summary["status"] = "disabled"; summary["message"] = "预算已停用"
            } else if now < interval.start {
                invalidate(&summary, status: "scheduled", reason: "尚未开始")
            } else if rule["kind"] as? String == "quota" {
                if let quota = quota { evaluateQuota(rule, quota: quota, summary: &summary, now: now) }
                else if let updated = Self.number(summary["updatedAt"]), now.timeIntervalSince1970 - updated > 180 {
                    invalidate(&summary, status: "unknown", reason: "官方额度已过期，等待更新")
                }
            } else if let generated = generated, generated <= now.timeIntervalSince1970 + 5, generated >= interval.start.timeIntervalSince1970,
                      generated >= (Self.number(summary["updatedAt"]) ?? -Double.greatestFiniteMagnitude),
                      let row = results.first(where: { matches($0, rule: rule, period: interval) }) {
                evaluateUsage(rule, root: result!, row: row, generated: generated, summary: &summary)
            }
            if now >= interval.end { summary["status"] = "ended"; summary["message"] = "预算已结束" }
            let active = enabled && sourceMatches && now >= interval.start && now < interval.end
            if active && !paused { createAlerts(rule, period: interval, summary: summary, now: now) }
            next.append(summary)
        }
        summaries = next
        // Pending notifications from old periods, disabled rules, invalid data or pauses are not delivered.
        let eligible = Set(summaries.filter { row in
            let status = row["status"] as? String ?? "unknown"
            return ["warning", "exhausted", "exceeded"].contains(status) && row["paused"] as? Bool != true
        }.map { "\($0["id"] ?? "")|\($0["periodID"] ?? "")" })
        trimLedger()
        do {
            if !Self.equal(before, snapshot()) { try persist() }
            return pendingAlerts.filter { alert in
                guard eligible.contains("\(alert["ruleID"] ?? "")|\(alert["periodID"] ?? "")"),
                      let summary = summaries.first(where: { $0["id"] as? String == alert["ruleID"] as? String }),
                      let remaining = Self.number(summary["remainingPercent"]), let threshold = Self.number(alert["threshold"]), Self.reached(remaining, threshold) else { return false }
                return (alert["alertPeriodID"] as? String ?? alert["periodID"] as? String) == (summary["alertPeriodID"] as? String ?? summary["periodID"] as? String)
            }
        } catch {
            restore(before); persistenceError = "预算状态未能保存，暂未发送提醒：\(error.localizedDescription)"
            return []
        }
    }

    private struct Interval {
        let start: Date
        let end: Date
        var id: String { String(format: "%.3f:%.3f", start.timeIntervalSince1970, end.timeIntervalSince1970) }
    }

    private func period(_ rule: [String: Any], now: Date) -> Interval? {
        guard let spec = rule["period"] as? [String: Any], let type = spec["type"] as? String,
              let zone = TimeZone(identifier: spec["timezone"] as? String ?? "") else { return nil }
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = zone
        let hour = Self.integer(spec["hour"]) ?? 0, minute = Self.integer(spec["minute"]) ?? 0
        func boundary(_ date: Date) -> Date? {
            calendar.date(bySettingHour: hour, minute: minute, second: 0, of: date,
                          matchingPolicy: .nextTime, repeatedTimePolicy: .first, direction: .forward)
        }
        switch type {
        case "once":
            return Interval(start: Date(timeIntervalSince1970: Self.number(spec["start"])!), end: Date(timeIntervalSince1970: Self.number(spec["end"])!))
        case "interval":
            let anchor = Self.number(spec["start"])!, seconds = Self.number(spec["seconds"])!
            let index = max(0, floor((now.timeIntervalSince1970 - anchor) / seconds))
            let start = anchor + index * seconds
            return Interval(start: Date(timeIntervalSince1970: start), end: Date(timeIntervalSince1970: start + seconds))
        case "day", "week":
            let days = type == "day" ? 1 : 7
            let offset = type == "day" ? 0 : (calendar.component(.weekday, from: now) - (Self.integer(spec["weekday"]) ?? 2) + 7) % 7
            guard var anchor = calendar.date(byAdding: .day, value: -offset, to: calendar.startOfDay(for: now)),
                  var start = boundary(anchor) else { return nil }
            if start > now {
                guard let previous = calendar.date(byAdding: .day, value: -days, to: anchor), let previousStart = boundary(previous) else { return nil }
                anchor = previous; start = previousStart
            }
            guard let next = calendar.date(byAdding: .day, value: days, to: anchor), let end = boundary(next) else { return nil }
            return Interval(start: start, end: end)
        case "month":
            guard let month = calendar.dateInterval(of: .month, for: now)?.start else { return nil }
            func monthBoundary(_ first: Date) -> Date? {
                guard let range = calendar.range(of: .day, in: .month, for: first),
                      let day = calendar.date(byAdding: .day, value: min(Self.integer(spec["day"]) ?? 1, range.count) - 1, to: first) else { return nil }
                return boundary(day)
            }
            guard var start = monthBoundary(month) else { return nil }
            var first = month
            if start > now {
                guard let previous = calendar.date(byAdding: .month, value: -1, to: month), let previousStart = monthBoundary(previous) else { return nil }
                first = previous; start = previousStart
            }
            guard let next = calendar.date(byAdding: .month, value: 1, to: first), let end = monthBoundary(next) else { return nil }
            return Interval(start: start, end: end)
        default: return nil
        }
    }

    private func effective(_ rule: [String: Any], now: Date) -> [String: Any] {
        guard let id = rule["id"] as? String, let state = runtime[id],
              let until = Self.number(state["deferredUntil"]), until > now.timeIntervalSince1970,
              let fields = state["deferred"] as? [String: Any] else { return rule }
        var result = rule
        for (key, value) in fields { result[key] = value }
        return result
    }

    private func normalize(_ input: [String: Any], source: String) throws -> [String: Any] {
        guard !source.isEmpty, source.count <= 4096 else { throw BudgetError.invalid("预算需要绑定有效数据目录") }
        if let id = input["id"] as? String, id.isEmpty || id.count > 128 { throw BudgetError.invalid("预算标识无效") }
        let kind = input["kind"] as? String ?? "token"
        guard ["token", "money", "quota"].contains(kind) else { throw BudgetError.invalid("未知预算口径") }
        let name = (input["name"] as? String ?? "新预算").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, name.count <= 100 else { throw BudgetError.invalid("预算名称需要 1–100 个字符") }
        guard let amount = Self.number(input["amount"]), (kind == "quota" ? amount >= 0 : amount > 0), amount <= 1e18 else { throw BudgetError.invalid("预算额度必须为大于零的有限数字") }
        let metric = input["tokenMetric"] as? String ?? "total"
        guard ["total", "noncached", "output"].contains(metric) else { throw BudgetError.invalid("未知 Token 统计口径") }
        let model = input["model"] as? String ?? "all", task = input["task"] as? String ?? "all"
        guard !model.isEmpty, model.count <= 512, !task.isEmpty, task.count <= 512 else { throw BudgetError.invalid("模型或任务范围无效") }
        let currency = input["currency"] as? String ?? "USD", fx = Self.number(input["fx"]) ?? 1
        guard ["USD", "CNY"].contains(currency), fx > 0, fx <= 1e6 else { throw BudgetError.invalid("币种或固定汇率无效") }
        let prices = input["prices"] as? [[String: Any]] ?? []
        guard prices.count <= 100 else { throw BudgetError.invalid("单条预算最多设置 100 个模型价格") }
        var normalizedPrices: [[String: Any]] = [], models = Set<String>()
        for price in prices {
            guard let model = price["model"] as? String, !model.isEmpty, model != "all", model.count <= 512, models.insert(model).inserted else {
                throw BudgetError.invalid("价格需指定唯一的模型")
            }
            var normalized: [String: Any] = ["model": model]
            for key in ["input", "cachedInput", "output"] {
                if price[key] == nil || price[key] is NSNull { continue }
                guard let value = Self.number(price[key]), value >= 0, value <= 1e9 else {
                    throw BudgetError.invalid("已填写的价格需为非负的每百万 Token 美元单价；留空表示未知")
                }
                normalized[key] = value
            }
            normalizedPrices.append(normalized)
        }
        let condition = input["quotaCondition"] as? String ?? "floor"
        let window = Self.integer(input["windowMinutes"]) ?? 10080
        if kind == "quota" {
            guard condition == "floor" else { throw BudgetError.invalid("期间消耗暂不可用：官方接口尚无稳定账号与窗口身份，无法可靠比较跨快照消耗") }
            guard amount <= 100, window > 0, window <= 525600, model == "all", task == "all" else {
                throw BudgetError.invalid("官方提醒需选择有效窗口、0–100% 下限，且不能按模型或任务筛选")
            }
        }
        let rawThresholds = input["thresholds"] as? [Any] ?? [20, 10, 0]
        let thresholds = rawThresholds.compactMap(Self.number)
        guard !thresholds.isEmpty, thresholds.count <= 10, thresholds.count == rawThresholds.count,
              thresholds.allSatisfy({ $0 >= 0 && $0 <= 100 }), Set(thresholds).count == thresholds.count else {
            throw BudgetError.invalid("提醒节点需要 1–10 个不重复的 0–100% 数值")
        }
        var spec = input["period"] as? [String: Any] ?? ["type": "day", "timezone": TimeZone.current.identifier]
        let type = spec["type"] as? String ?? "day", zone = spec["timezone"] as? String ?? TimeZone.current.identifier
        guard ["day", "week", "month", "once", "interval"].contains(type), TimeZone(identifier: zone) != nil else {
            throw BudgetError.invalid("请选择有效的周期和 IANA 时区")
        }
        let hour = Self.integer(spec["hour"]) ?? 0, minute = Self.integer(spec["minute"]) ?? 0
        let weekday = Self.integer(spec["weekday"]) ?? 2, day = Self.integer(spec["day"]) ?? 1
        guard (0...23).contains(hour), (0...59).contains(minute), (1...7).contains(weekday), (1...31).contains(day) else {
            throw BudgetError.invalid("周期重置日期或时刻无效")
        }
        spec = ["type": type, "timezone": zone, "hour": hour, "minute": minute, "weekday": weekday, "day": day]
        if type == "once" || type == "interval" {
            guard let original = input["period"] as? [String: Any], let start = Self.number(original["start"]), abs(start) < 253_402_214_400 else {
                throw BudgetError.invalid("请输入有效开始时间")
            }
            spec["start"] = start
            if type == "once" {
                guard let end = Self.number(original["end"]), end > start, abs(end) < 253_402_214_400 else { throw BudgetError.invalid("结束时间必须晚于开始时间") }
                spec["end"] = end
            } else {
                guard let seconds = Self.number(original["seconds"]), seconds >= 60, seconds <= 315_576_000 else { throw BudgetError.invalid("重复间隔需为 1 分钟到 10 年") }
                spec["seconds"] = seconds
            }
        }
        return ["id": input["id"] as? String ?? UUID().uuidString, "revision": Self.integer(input["revision"]) ?? 0,
                "name": name, "kind": kind, "amount": amount, "tokenMetric": metric, "model": model, "task": task,
                "currency": currency, "fx": fx, "prices": normalizedPrices, "period": spec,
                "thresholds": thresholds.sorted(by: >), "enabled": input["enabled"] as? Bool ?? true,
                "source": source, "windowMinutes": window, "quotaCondition": condition]
    }

    private func baseSummary(_ rule: [String: Any], period: Interval) -> [String: Any] {
        var result: [String: Any] = ["id": rule["id"]!, "revision": rule["revision"]!, "name": rule["name"]!, "kind": rule["kind"]!,
            "amount": rule["kind"] as? String == "quota" ? 100.0 : rule["amount"]!, "currency": rule["currency"]!,
            "periodID": period.id, "start": period.start.timeIntervalSince1970, "end": period.end.timeIntervalSince1970,
            "periodStart": period.start.timeIntervalSince1970, "periodEnd": period.end.timeIntervalSince1970,
            "model": rule["model"]!, "task": rule["task"]!, "tokenMetric": rule["tokenMetric"]!, "period": rule["period"]!,
            "source": rule["source"]!, "windowMinutes": rule["windowMinutes"]!, "quotaFloor": rule["amount"]!,
            "used": NSNull(), "remaining": NSNull(), "remainingPercent": NSNull(), "remainingFraction": NSNull(),
            "overage": NSNull(), "updatedAt": NSNull(), "paused": false, "pausedUntil": NSNull()]
        invalidate(&result, status: "unknown", reason: "等待本周期数据")
        return result
    }

    private func matches(_ row: [String: Any], rule: [String: Any], period: Interval) -> Bool {
        row["id"] as? String == rule["id"] as? String && Self.integer(row["revision"]) == Self.integer(rule["revision"]) &&
        row["periodID"] as? String == period.id && Self.number(row["start"]) == period.start.timeIntervalSince1970 &&
        Self.number(row["end"]) == period.end.timeIntervalSince1970 && row["model"] as? String == rule["model"] as? String &&
        row["task"] as? String == rule["task"] as? String
    }

    private func evaluateUsage(_ rule: [String: Any], root: [String: Any], row: [String: Any], generated: Double, summary: inout [String: Any]) {
        guard row["scope_valid"] as? Bool == true else { invalidate(&summary, status: "scope_invalid", reason: "所选模型或任务不存在；未扩大统计范围"); return }
        guard root["has_rows"] as? Bool == true else { invalidate(&summary, status: "unknown", reason: "尚无可确认的用量记录"); return }
        guard let rows = row["rows"] as? [[String: Any]] else { invalidate(&summary, status: "unknown", reason: "用量结果不完整"); return }
        var complete = root["coverage_complete"] as? Bool != false
        if !(root["issues"] as? [Any] ?? []).isEmpty || !(row["issues"] as? [Any] ?? []).isEmpty { complete = false }
        var used = 0.0
        let metric = rule["tokenMetric"] as? String ?? "total", kind = rule["kind"] as? String ?? "token"
        let prices = rule["prices"] as? [[String: Any]] ?? []
        for usage in rows {
            guard let requests = Self.number(usage["requests"]), requests >= 0 else { complete = false; continue }
            func amount(_ key: String) -> (Double, Bool) {
                let value = Self.number(usage[key])
                let known = Self.number(usage["known_" + key])
                let valid = value != nil && (known == nil || known == requests)
                let lower = value ?? Self.number(usage["lower_bound_" + key]) ?? 0
                return (max(0, lower), valid && lower >= 0)
            }
            if kind == "token" {
                if metric == "total" || metric == "output" {
                    let (value, known) = amount(metric == "total" ? "total_tokens" : "output_tokens")
                    used += value; complete = complete && known
                } else {
                    let (input, inputKnown) = amount("input_tokens"), (output, outputKnown) = amount("output_tokens"), (cached, cachedKnown) = amount("cached_input_tokens")
                    // Without a complete cache split, known input is not a noncached lower bound.
                    used += output + (inputKnown && cachedKnown && cached <= input ? input - cached : 0)
                    complete = complete && inputKnown && outputKnown && cachedKnown && cached <= input
                }
            } else {
                guard let price = prices.first(where: { $0["model"] as? String == usage["model"] as? String }) else { complete = false; continue }
                let (input, inputKnown) = amount("input_tokens"), (output, outputKnown) = amount("output_tokens"), (cached, cachedKnown) = amount("cached_input_tokens")
                let (cacheWrite, cacheWriteKnown) = amount("cache_write_input_tokens")
                let inputPrice = Self.number(price["input"]), cachedPrice = Self.number(price["cachedInput"]), outputPrice = Self.number(price["output"])
                // Unknown cache-write classification cannot be assumed to be ordinary input.
                let splitKnown = inputKnown && cachedKnown && cacheWriteKnown && cached <= input && cacheWrite == 0
                let noncached = max(0, input - cached)
                let inputPriced = splitKnown && (noncached == 0 || inputPrice != nil) && (cached == 0 || cachedPrice != nil)
                var inputCost = 0.0
                if splitKnown {
                    if let value = inputPrice { inputCost += noncached * value }
                    if let value = cachedPrice { inputCost += cached * value }
                }
                let outputCost = outputPrice.map { output * $0 } ?? 0
                used += (inputCost + outputCost) / 1_000_000
                complete = complete && inputPriced && outputKnown && (output == 0 || outputPrice != nil)

            }
        }
        if kind == "money", rule["currency"] as? String == "CNY" { used *= Self.number(rule["fx"])! }
        guard used.isFinite else { invalidate(&summary, status: "unknown", reason: "用量数值超出可计算范围"); return }
        let amount = Self.number(rule["amount"])!
        let remaining = max(0, amount - used), ratio = (amount - used) / amount * 100
        summary["used"] = used; summary["updatedAt"] = generated; summary["overage"] = max(0, used - amount)
        summary["coverage"] = complete ? "complete" : "partial"
        summary["dataStatus"] = complete ? "updated" : "partial"
        summary["estimated"] = kind == "money"
        if complete || used >= amount {
            summary["remaining"] = remaining; summary["remainingPercent"] = max(0, ratio); summary["remainingFraction"] = max(0, ratio / 100)
            let threshold = (rule["thresholds"] as? [Double] ?? [20, 10, 0]).max() ?? 20
            summary["status"] = used > amount ? "exceeded" : (used == amount ? "exhausted" : (Self.reached(ratio, threshold) ? "warning" : "healthy"))
            summary["reason"] = complete ? "" : "仅已知用量已达到预算，实际消耗可能更高"
            summary["message"] = used > amount ? "预算已超出" : (used == amount ? "预算已用尽" : "预算剩余 \(Self.display(max(0, ratio)))%")
        } else {
            summary["remaining"] = NSNull(); summary["remainingPercent"] = NSNull(); summary["remainingFraction"] = NSNull()
            summary["status"] = "partial"
            summary["reason"] = kind == "money" ? "部分价格、缓存分类或用量缺失；剩余无法确认" : "部分用量或缓存分类缺失；剩余无法确认"
            summary["message"] = summary["reason"]
        }
    }

    private func evaluateQuota(_ rule: [String: Any], quota: [String: Any], summary: inout [String: Any], now: Date) {
        guard let updated = Self.number(quota["updated_at"]), updated <= now.timeIntervalSince1970 + 5,
              now.timeIntervalSince1970 - updated <= 180, quota["stale"] as? Bool != true,
              quota["error"] == nil || quota["error"] is NSNull,
              let window = (quota["windows"] as? [[String: Any]])?.first(where: { Self.integer($0["duration_minutes"]) == Self.integer(rule["windowMinutes"]) }),
              let remaining = Self.number(window["remaining"]), remaining >= 0 && remaining <= 100 else {
            invalidate(&summary, status: "unknown", reason: "官方窗口缺失或额度已过期，等待更新"); return
        }
        guard updated >= (Self.number(summary["updatedAt"]) ?? -Double.greatestFiniteMagnitude) else { return }
        summary["remaining"] = remaining; summary["remainingPercent"] = remaining; summary["remainingFraction"] = remaining / 100
        summary["used"] = 100 - remaining; summary["overage"] = 0; summary["updatedAt"] = updated
        summary["resetsAt"] = window["resets_at"] ?? NSNull()
        let monitoring = summary["periodID"] as? String ?? ""
        if let reset = Self.number(window["resets_at"]) {
            summary["alertPeriodID"] = monitoring + ":quota:" + String(Self.integer(rule["windowMinutes"])!) + ":" + String(reset)
        } else { summary["alertPeriodID"] = monitoring }
        summary["coverage"] = "complete"; summary["dataStatus"] = "updated"; summary["reason"] = ""
        summary["status"] = remaining == 0 ? "exhausted" : (Self.reached(remaining, Self.number(rule["amount"])!) ? "warning" : "healthy")
        summary["message"] = "官方额度剩余 \(Self.display(remaining))%"
    }

    private func createAlerts(_ rule: [String: Any], period: Interval, summary: [String: Any], now: Date) {
        guard ["warning", "exhausted", "exceeded"].contains(summary["status"] as? String ?? ""),
              let remaining = Self.number(summary["remainingPercent"]), let id = rule["id"] as? String else { return }
        let thresholds = rule["kind"] as? String == "quota" ? [Self.number(rule["amount"])!] : rule["thresholds"] as? [Double] ?? [20, 10, 0]
        let alertPeriod = summary["alertPeriodID"] as? String ?? period.id
        let crossed = thresholds.filter { Self.reached(remaining, $0) }.sorted()
        guard let severity = crossed.first else { return }
        // Mark skipped, less severe levels consumed, so a later correction never replays them.
        for threshold in crossed {
            let alertID = "budget:\(id):\(alertPeriod):\(String(threshold))"
            if let index = ledger.firstIndex(where: { $0["id"] as? String == alertID }) {
                if ledger[index]["acknowledged"] as? Bool != true && threshold == severity {
                    for key in ["used", "remaining", "remainingPercent", "message", "reason"] { ledger[index][key] = summary[key] }
                }
                continue
            }
            if threshold == severity {
                for index in ledger.indices where ledger[index]["ruleID"] as? String == id && ledger[index]["alertPeriodID"] as? String == alertPeriod {
                    ledger[index]["acknowledged"] = true
                }
            }
            ledger.append(["id": alertID, "ruleID": id, "periodID": period.id, "alertPeriodID": alertPeriod, "threshold": threshold,
                           "createdAt": now.timeIntervalSince1970, "acknowledged": threshold != severity, "suppressed": threshold != severity,
                           "name": rule["name"]!, "kind": rule["kind"]!, "currency": rule["currency"]!,
                           "amount": summary["amount"]!, "used": summary["used"]!, "remaining": summary["remaining"]!,
                           "remainingPercent": remaining, "periodEnd": period.end.timeIntervalSince1970,
                           "message": summary["message"] ?? "预算提醒", "reason": summary["reason"] ?? ""])
        }
    }

    private func invalidate(_ summary: inout [String: Any], status: String, reason: String) {
        summary["status"] = status; summary["reason"] = reason; summary["message"] = reason
        summary["coverage"] = "unknown"; summary["dataStatus"] = "unknown"
        summary["remaining"] = NSNull(); summary["remainingPercent"] = NSNull(); summary["remainingFraction"] = NSNull()
    }

    private func locate(_ payload: [String: Any]) throws -> (Int, [String: Any]) {
        guard let id = payload["id"] as? String, let index = rules.firstIndex(where: { $0["id"] as? String == id }) else { throw BudgetError.invalid("预算不存在") }
        return (index, rules[index])
    }

    private func checkRevision(_ payload: [String: Any], input: [String: Any], existing: [String: Any]) throws {
        guard let expected = Self.integer(payload["expectedRevision"] ?? input["revision"]), expected == Self.integer(existing["revision"]) else { throw BudgetError.conflict }
    }

    private func snapshot() -> [String: Any] {
        ["version": 1, "rules": rules, "runtime": runtime, "ledger": ledger, "summaries": summaries]
    }

    private func restore(_ state: [String: Any]) {
        rules = state["rules"] as! [[String: Any]]; runtime = state["runtime"] as! [String: [String: Any]]
        ledger = state["ledger"] as! [[String: Any]]; summaries = state["summaries"] as! [[String: Any]]
    }

    private func persist() throws {
        let configuration = try JSONSerialization.data(withJSONObject: rules)
        guard configuration.count <= 65_536 else { throw BudgetError.invalid("预算配置过大，请减少规则或重复模型价格") }
        let data = try JSONSerialization.data(withJSONObject: snapshot(), options: [.sortedKeys])
        guard data.count <= 2_097_152 else { throw BudgetError.invalid("预算配置超出存储上限") }
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url, options: [.atomic])
        persistenceError = nil
    }

    private func trimLedger() {
        let ids = Set(rules.compactMap { $0["id"] as? String })
        var keptPeriods: [String: Set<String>] = [:]
        ledger = ledger.filter { ids.contains($0["ruleID"] as? String ?? "") }
            .sorted { (Self.number($0["createdAt"]) ?? 0) > (Self.number($1["createdAt"]) ?? 0) }
            .filter { entry in
                let id = entry["ruleID"] as? String ?? "", period = entry["alertPeriodID"] as? String ?? entry["periodID"] as? String ?? ""
                var periods = keptPeriods[id] ?? []
                guard periods.contains(period) || periods.count < 2 else { return false }
                periods.insert(period); keptPeriods[id] = periods
                return true
            }

    }

    private static func number(_ value: Any?) -> Double? {
        guard let number = value as? NSNumber, String(cString: number.objCType) != "c", number.doubleValue.isFinite else { return nil }
        return number.doubleValue
    }
    private static func integer(_ value: Any?) -> Int? {
        guard let value = number(value), value.rounded(.towardZero) == value, value > Double(Int.min), value < Double(Int.max) else { return nil }
        return Int(value)
    }
    private static func equal(_ lhs: Any?, _ rhs: Any?) -> Bool {
        guard let lhs = lhs, let rhs = rhs else { return lhs == nil && rhs == nil }
        return NSDictionary(dictionary: ["value": lhs]).isEqual(to: ["value": rhs])
    }
    private static func reached(_ remaining: Double, _ threshold: Double) -> Bool {
        remaining <= threshold + 8 * Double.ulpOfOne * max(100, abs(threshold))
    }
    private static func display(_ value: Double) -> String {
        String(format: "%.4f", value).replacingOccurrences(of: #"\.?0+$"#, with: "", options: .regularExpression)
    }
}

enum BudgetError: LocalizedError {
    case invalid(String)
    case conflict
    var errorDescription: String? {
        switch self {
        case .invalid(let message): return message
        case .conflict: return "预算已被另一界面修改，请刷新后重试"
        }
    }
}
