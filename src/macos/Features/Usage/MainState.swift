import Foundation

/// One in-flight batch. A failed older save must never replace a newer edit.
final class MainSettingsQueue {
    private(set) var pending: Int?
    private(set) var inFlight: Int?
    private(set) var error: String?
    func enqueue(_ value: Int) { pending = value }
    func begin() -> Int? {
        guard inFlight == nil, let value = pending else { return nil }
        pending = nil; inFlight = value; return value
    }
    func finish(error: String?) {
        if error != nil, pending == nil { pending = inFlight }
        inFlight = nil; self.error = error
    }
    func interrupt() { finish(error: "设置等待重新连接后保存") }
    var hasPending: Bool { pending != nil || inFlight != nil }
}

struct MainUsageIdentity: Equatable {
    let source: String
    let cache: String
    let generation: UUID
    let days: String
    let model: String
    let task: String
    let group: String
}

/// Main-thread owned. A result is usable only with the range that produced it.
final class MainUsageSession {
    private(set) var identity: MainUsageIdentity?
    private(set) var serial = 0
    private(set) var data: [String: Any]?
    private(set) var needsRead = true
    @discardableResult func select(_ value: MainUsageIdentity) -> Bool {
        guard identity != value else { return false }
        identity = value; invalidate(); return true
    }
    func invalidate() { serial += 1; data = nil; needsRead = true }
    func failed() { needsRead = true }
    @discardableResult func publish(_ result: [String: Any], for expected: MainUsageIdentity) -> Bool {
        guard identity == expected else { return false }
        data = result; serial += 1
        needsRead = (result["meta"] as? [String: Any])?["loading"] as? Bool == true
        return true
    }
    func matches(_ expected: MainUsageIdentity, serial: Int) -> Bool {
        identity == expected && self.serial == serial && data != nil && !needsRead
    }
}

struct MainTableSelection: Equatable {
    let search: String
    let sortKey: String
    let ascending: Bool
    func rows(_ rows: [[String: Any]], visible: Bool = true) -> [[String: Any]] {
        guard visible else { return rows }
        let query = search.trimmingCharacters(in: .whitespacesAndNewlines)
        return rows.filter { query.isEmpty || ($0["label"] as? String ?? "").localizedCaseInsensitiveContains(query) }.sorted { a, b in
            let label = (a["label"] as? String ?? "").localizedStandardCompare(b["label"] as? String ?? "")
            if sortKey == "label" { return ascending ? label == .orderedAscending : label == .orderedDescending }
            // NSNumber.compare preserves integer precision beyond Double's 53 bits.
            guard let left = a[sortKey] as? NSNumber else { return false }
            guard let right = b[sortKey] as? NSNumber else { return true }
            let order = left.compare(right)
            return order == .orderedSame ? label == .orderedAscending : ascending ? order == .orderedAscending : order == .orderedDescending
        }
    }
    static func csv(_ rows: [[String: Any]]) -> Data {
        func escape(_ text: String) -> String { "\"" + text.replacingOccurrences(of: "\"", with: "\"\"") + "\"" }
        let keys = ["input_tokens", "cached_input_tokens", "noncached_input_tokens", "output_tokens", "total_tokens", "requests"]
        var lines = ["范围,输入,其中缓存输入,非缓存输入,输出（含推理）,总数,模型调用"]
        for row in rows {
            let values = keys.map { (row[$0] as? NSNumber)?.stringValue ?? "不可统计" }
            lines.append(([escape(row["label"] as? String ?? "未命名")] + values).joined(separator: ","))
        }
        return Data(("\u{FEFF}" + lines.joined(separator: "\r\n") + "\r\n").utf8)
    }
}
