import Foundation

// These two fixed numeric formats do not need a locale formatter/cache in the
// resident process. localtime_r preserves the user's current local time zone.
func smallClock(_ date: Date, includeDay: Bool = false) -> String {
    var epoch = time_t(date.timeIntervalSince1970)
    var parts = tm()
    guard localtime_r(&epoch, &parts) != nil else { return "—" }
    if includeDay {
        return "\(parts.tm_mon + 1)/\(parts.tm_mday) " + String(format: "%02d:%02d", parts.tm_hour, parts.tm_min)
    }
    return String(format: "%02d:%02d:%02d", parts.tm_hour, parts.tm_min, parts.tm_sec)
}

struct QuotaWindow {
    let label: String
    let remaining: Double
    let resetsAt: Date?
    var durationMinutes: Int = 0
    var compact: String { "\(label)余 \(Int(remaining.rounded(.down)))%" }
}
struct QuotaSnapshot {
    var windows: [QuotaWindow] = []
    var resetCount: Int?
    var updated: Date?
    var error: String?
    var stale: Bool { updated.map { Date().timeIntervalSince($0) > 180 } ?? true }
    var compact: String { windows.isEmpty ? "额度 —" : windows.map(\.compact).joined(separator: " / ") + (stale || error != nil ? "*" : "") }
    var capsuleWindow: QuotaWindow? { windows.min(by: { $0.remaining < $1.remaining }) }
    var capsuleCompact: String { capsuleWindow.map { $0.compact + (stale || error != nil ? "*" : "") } ?? "额度 —" }
    var detail: String {
        guard !windows.isEmpty else { return error ?? "正在读取账号额度…" }
        let text = windows.map { item in item.compact + (item.resetsAt.map { " · \(smallClock($0, includeDay: true)) 重置" } ?? "") }.joined(separator: "   ")
        return text + (stale || error != nil ? " · 上次记录" : "")
    }
    var resetLabel: String { resetCount.map { "重置卡 \($0) 张" } ?? "重置卡数量未知" }
    static func parse(_ object: [String: Any]) -> QuotaSnapshot {
        var snapshot = QuotaSnapshot()
        snapshot.updated = (object["updated_at"] as? NSNumber).map { Date(timeIntervalSince1970: $0.doubleValue) }
        snapshot.error = object["error"] as? String
        if let count = object["reset_count"] as? NSNumber, count.intValue >= 0 { snapshot.resetCount = count.intValue }
        for row in object["windows"] as? [[String: Any]] ?? [] {
            guard let used = row["used_percent"] as? NSNumber, used.doubleValue.isFinite,
                  let minutes = row["duration_minutes"] as? NSNumber else { continue }
            let n = minutes.intValue
            let label = n == 10080 ? "周" : (n == 300 ? "5h" : (n % 60 == 0 && n > 0 ? "\(n / 60)h" : "\(n)分钟"))
            let reset = (row["resets_at"] as? NSNumber).map { Date(timeIntervalSince1970: $0.doubleValue) }
            snapshot.windows.append(QuotaWindow(label: label, remaining: min(100, max(0, 100 - used.doubleValue)), resetsAt: reset, durationMinutes: n))
        }
        return snapshot
    }
}

final class QuotaReader {
    let root: URL
    var changed: ((QuotaSnapshot) -> Void)?
    private var child: Process?
    private var timer: Timer?
    private var deadline: DispatchWorkItem?
    private var generation = 0
    private var lastAttempt = Date.distantPast
    var snapshot = QuotaSnapshot()
    var cacheURL: URL { root.appendingPathComponent("quota.json") }
    init(root: URL) { self.root = root }
    func loadCache() {
        guard let data = try? Data(contentsOf: cacheURL), data.count <= 65536,
              let value = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
        snapshot = QuotaSnapshot.parse(value); changed?(snapshot)
    }
    func start() {
        loadCache()
        if let updated = snapshot.updated, snapshot.error == nil,
           Date().timeIntervalSince(updated) >= 0, Date().timeIntervalSince(updated) < 55 {
            lastAttempt = updated
        }
        refresh()
    }
    func refresh(force: Bool = false) {
        guard child == nil else { return }
        if !force && Date().timeIntervalSince(lastAttempt) < 55 { schedule(); return }
        timer?.invalidate(); timer = nil
        lastAttempt = Date(); generation += 1; let ticket = generation
        let worker = Process()
        worker.executableURL = usageMacOSURL.appendingPathComponent("CodexQuota")
        worker.arguments = ["--output", cacheURL.path, "--parent-pid", String(getpid())]
        worker.standardOutput = FileHandle.nullDevice; worker.standardError = FileHandle.nullDevice
        worker.terminationHandler = { [weak self] finished in
            DispatchQueue.main.async {
                guard let self = self, ticket == self.generation else { return }
                self.child = nil; self.deadline?.cancel(); self.deadline = nil
                self.loadCache()
                if finished.terminationStatus != 0 { self.snapshot.error = "额度暂不可用"; self.changed?(self.snapshot) }
                self.schedule()
            }
        }
        do {
            child = worker; try worker.run()
            let timeout = DispatchWorkItem { [weak self, weak worker] in
                guard let self = self, ticket == self.generation, worker?.isRunning == true else { return }
                worker?.terminate()
            }
            deadline = timeout; DispatchQueue.main.asyncAfter(deadline: .now() + 25, execute: timeout)
        } catch {
            child = nil; snapshot.error = "额度读取组件未能启动"; changed?(snapshot); schedule()
        }
    }
    private func schedule() {
        timer?.invalidate()
        timer = Timer(timeInterval: max(1, 60 - Date().timeIntervalSince(lastAttempt)), repeats: false) { [weak self] _ in self?.refresh() }
        timer?.tolerance = 10
        if let timer = timer { RunLoop.main.add(timer, forMode: .common) }
    }
    func stop(_ completion: @escaping () -> Void = {}) {
        generation += 1; timer?.invalidate(); timer = nil; deadline?.cancel(); deadline = nil
        let current = child; child = nil; current?.terminationHandler = nil
        DispatchQueue.global(qos: .utility).async {
            if let current = current, current.isRunning {
                current.terminate()
                for _ in 0..<40 { if !current.isRunning { break }; Thread.sleep(forTimeInterval: 0.05) }
                if current.isRunning { kill(current.processIdentifier, SIGKILL) }
                current.waitUntilExit()
            }
            DispatchQueue.main.async(execute: completion)
        }
    }
}
