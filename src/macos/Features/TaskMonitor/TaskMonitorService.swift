import Foundation

/// Host-owned service. All file IO and reducers run on one utility queue. The
/// existing host FSEventStream supplies paths; this service never owns another.
final class TaskMonitorService {
    var changed: ((MonitorObject) -> Void)?
    var deliver: (([MonitorObject], MonitorObject) -> Void)?
    private let queue = DispatchQueue(label: "local.codex-usage.task-monitor", qos: .utility)
    private let store: TaskMonitorStore
    private var home = ""
    private var readers: [String: TaskLogReader] = [:]
    private var candidates = Set<String>()
    private var discovery: FileManager.DirectoryEnumerator?
    private var discoveryRoots: [URL] = []
    private var scheduled: DispatchWorkItem?
    private var fallback: DispatchSourceTimer?
    private var generation = 0
    private var stopped = false
    private var watchAvailable = true
    private var sourceError = ""
    private var checkedAt = 0.0
    private var view: MonitorObject = ["section": "watches", "page": 0, "query": ""]
    private var lastPublished: Data?
    private var titles: [String: String] = [:]
    private var titlesDirty = true
    private var dirty = false
    private var forceRead = false
    private var lastSaved = 0.0
    private var fallbackArmed = false
    init(url: URL) { store = TaskMonitorStore(url: url) }

    func start(home: URL, watching: Bool) {
        queue.async { [weak self] in
            guard let self = self else { return }
            self.generation += 1; self.stopped = false; self.scheduled?.cancel(); self.scheduled = nil
            self.home = home.standardizedFileURL.path; self.watchAvailable = watching
            self.readers = [:]; self.candidates = Set(self.store.watches.filter { $0["home"] as? String == self.home }.compactMap { $0["path"] as? String })
            self.titlesDirty = true; self.titles = [:]; self.beginDiscovery(); self.schedule(delay: 0); self.configureFallback()
        }
    }
    func stop() {
        queue.sync {
            stopped = true; generation += 1; scheduled?.cancel(); scheduled = nil
            fallback?.cancel(); fallback = nil; fallbackArmed = false; discovery = nil; discoveryRoots = []
            persist(); readers = [:]; candidates = []
        }
    }
    func receive(paths: [String], rescan: Bool = false) {
        queue.async { [weak self] in
            guard let self = self, !self.stopped else { return }
            for path in paths {
                if path == self.home + "/session_index.jsonl" { self.titlesDirty = true }
                if self.allowed(path) && path.hasSuffix(".jsonl") { self.candidates.insert(path) }
            }
            if rescan { self.beginDiscovery(); self.forceRead = true }
            self.schedule(delay: 0.12)
        }
    }
    func refresh() { receive(paths: [], rescan: true) }
    func command(_ operation: String, payload: MonitorObject, completion: @escaping (MonitorObject) -> Void) {
        queue.async { [weak self] in
            guard let self = self, !self.stopped else { DispatchQueue.main.async { completion(["ok": false, "error": "任务监控尚未启动"]) }; return }
            var result: MonitorObject
            if operation == "snapshot" {
                self.view = ["section": ["tasks", "watches", "messages", "unread"].contains(payload["section"] as? String ?? "") ? payload["section"]! : "watches",
                             "page": max(0, min(1000, payload["page"] as? Int ?? 0)), "query": MonitorJSON.text(payload["query"], limit: 120)]
                if payload["messageIDs"] != nil { self.view["messageIDs"] = MonitorJSON.notificationIDs(payload["messageIDs"]) }
                result = ["ok": true]
            } else if operation == "refresh" { self.beginDiscovery(); self.forceRead = true; self.schedule(delay: 0); result = ["ok": true, "message": "正在重新核对"] }
            else {
                // Re-read selected files before resolving a selection. A task
                // ending between selection and click retains that exact turn.
                if operation == "add" { self.readCandidates(force: true, priorities: Set((payload["selections"] as? [MonitorObject] ?? []).compactMap { $0["id"] as? String })); self.schedule(delay: 0) }
                result = self.store.apply(operation, payload: payload, tasks: self.readers.values.map { $0.task }, home: self.home, now: Date().timeIntervalSince1970)
                self.configureFallback()
            }
            self.publish(force: true)
            DispatchQueue.main.async { completion(result) }
        }
    }
    private func allowed(_ path: String) -> Bool {
        let root = URL(fileURLWithPath: home).resolvingSymlinksInPath().path
        let source = URL(fileURLWithPath: path).resolvingSymlinksInPath().path
        return source.hasPrefix(root + "/sessions/") || source.hasPrefix(root + "/archived_sessions/")
    }
    private func beginDiscovery() {
        discovery = nil; discoveryRoots = [URL(fileURLWithPath: home).appendingPathComponent("sessions"), URL(fileURLWithPath: home).appendingPathComponent("archived_sessions")]
        sourceError = FileManager.default.isReadableFile(atPath: home) ? "" : "Codex 目录不可读取，请检查目录与访问权限"
        titlesDirty = true
    }
    private func discover() {
        // Bounded entries per batch, including directories and unrelated files.
        for _ in 0..<128 {
            if discovery == nil {
                guard !discoveryRoots.isEmpty else { return }
                let root = discoveryRoots.removeFirst()
                if !FileManager.default.fileExists(atPath: root.path) { continue }
                discovery = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey, .contentModificationDateKey], options: [.skipsHiddenFiles], errorHandler: { [weak self] _, error in
                    self?.sourceError = "部分任务目录不可读取：" + error.localizedDescription; return true
                })
            }
            guard let url = discovery?.nextObject() as? URL else { discovery = nil; continue }
            guard url.pathExtension == "jsonl", let values = try? url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .contentModificationDateKey]), values.isRegularFile == true, values.isSymbolicLink != true else { continue }
            if readers[url.path] != nil { continue }
            if readers.count >= 512 {
                let watched = Set(store.watches.compactMap { $0["id"] as? String })
                guard let oldest = readers.values.filter({ !watched.contains($0.task.id) }).min(by: { ($0.task.current?.updatedAt ?? 0) < ($1.task.current?.updatedAt ?? 0) }),
                      (values.contentModificationDate?.timeIntervalSince1970 ?? 0) > (oldest.task.current?.updatedAt ?? 0) else { continue }
                readers.removeValue(forKey: oldest.path)
            }
            candidates.insert(url.path)
        }
    }
    private func schedule(delay: Double) {
        guard !stopped, scheduled == nil else { return }
        let ticket = generation
        let work = DispatchWorkItem { [weak self] in
            guard let self = self, !self.stopped, self.generation == ticket else { return }
            self.scheduled = nil; self.tick()
        }
        scheduled = work; queue.asyncAfter(deadline: .now() + delay, execute: work)
    }
    private func readTitles() {
        guard titlesDirty else { return }; titlesDirty = false
        let url = URL(fileURLWithPath: home).appendingPathComponent("session_index.jsonl")
        guard let handle = try? FileHandle(forReadingFrom: url) else { return }
        defer { try? handle.close() }
        do {
            let size = try handle.seekToEnd(), offset = size > 2_097_152 ? size - 2_097_152 : 0
            try handle.seek(toOffset: offset)
            let data = try handle.read(upToCount: 2_097_152) ?? Data()
            let lines = Array(data).split(separator: UInt8(10), omittingEmptySubsequences: false)
            for (index, line) in lines.enumerated() where !(offset > 0 && index == 0) && index < lines.count - 1 && line.count <= 16384 {
                guard let row = try? JSONSerialization.jsonObject(with: Data(line)) as? MonitorObject,
                      let id = row["id"] as? String, MonitorJSON.validID(id) else { continue }
                let name = MonitorJSON.text(row["thread_name"], limit: 160)
                if !name.isEmpty { titles[id] = name }
            }
            if titles.count > 4096 { titles = Dictionary(uniqueKeysWithValues: titles.prefix(4096).map { ($0.key, $0.value) }) }
        } catch { sourceError = "任务标题索引暂时不可读取" }
    }
    private func readCandidates(force: Bool, priorities: Set<String> = []) {
        var budget = 8 * 1024 * 1024
        let selected = Set(candidates.map { URL(fileURLWithPath: $0).resolvingSymlinksInPath().path }).sorted(); candidates = []
        for path in selected.prefix(128) where allowed(path) {
            if readers[path] == nil, let reader = TaskLogReader(url: URL(fileURLWithPath: path), home: home) {
                if let checkpoint = store.checkpoints.first(where: { $0["home"] as? String == home && ($0["task"] as? MonitorObject)?["id"] as? String == reader.task.id }) { reader.restore(checkpoint) }
                // An archive move replaces the old reader by stable session ID.
                for old in readers.values.filter({ $0.task.id == reader.task.id && $0.path != path }) {
                    reader.restore(old.checkpoint, offline: old.offline); readers.removeValue(forKey: old.path)
                }
                if reader.task.parentID.isEmpty { readers[path] = reader }
                budget -= min(budget, reader.bytesRead)
            }
        }
        candidates.formUnion(selected.dropFirst(128))
        let watched = Set(store.watches.compactMap { $0["id"] as? String })
        let ordered = readers.values.sorted {
            let left = priorities.contains($0.task.id) ? 2 : watched.contains($0.task.id) ? 1 : 0
            let right = priorities.contains($1.task.id) ? 2 : watched.contains($1.task.id) ? 1 : 0
            return left == right ? $0.path < $1.path : left > right
        }
        for reader in ordered {
            if let title = titles[reader.task.id] { reader.task.title = title }
            let oldCheckpoint = reader.position
            let wasOffline = reader.offline
            for (turn, offline) in reader.read(budget: &budget, force: force) { store.observe(reader.task, turn: turn, offline: offline, now: Date().timeIntervalSince1970); dirty = true }
            store.reconcile(reader.task, offline: wasOffline, now: Date().timeIntervalSince1970)
            if reader.position != oldCheckpoint { dirty = true }
            // Retain the selected turn plus recent explicit turns; raw history
            // remains on disk and cannot grow this resident model indefinitely.
            let protected = Set(store.watches.filter { $0["id"] as? String == reader.task.id }.compactMap { $0["turnID"] as? String } + [reader.task.turnID])
            let recent = Set(reader.task.turns.values.sorted { $0.updatedAt > $1.updatedAt }.prefix(64).map { $0.id }).union(protected)
            reader.task.turns = reader.task.turns.filter { recent.contains($0.key) }
        }
    }
    private func tick() {
        readTitles(); discover(); readCandidates(force: forceRead); forceRead = false
        checkedAt = Date().timeIntervalSince1970
        if readers.count > 512 {
            let watched = Set(store.watches.compactMap { $0["id"] as? String })
            let excess = readers.values.filter { !watched.contains($0.task.id) }.sorted { ($0.task.current?.updatedAt ?? 0) < ($1.task.current?.updatedAt ?? 0) }.prefix(readers.count - 512)
            for reader in excess { readers.removeValue(forKey: reader.path) }
        }
        let busy = discovery != nil || !discoveryRoots.isEmpty || !candidates.isEmpty || readers.values.contains(where: { $0.pending })
        if dirty && (checkedAt - lastSaved >= 1 || !busy) { persist() }
        configureFallback(); publish()
        if busy { schedule(delay: 0.03) }
    }
    private func persist() {
        let watched = Set(store.watches.compactMap { $0["id"] as? String })
        let current = readers.values.sorted {
            if watched.contains($0.task.id) != watched.contains($1.task.id) { return watched.contains($0.task.id) }
            return ($0.task.current?.updatedAt ?? 0) > ($1.task.current?.updatedAt ?? 0)
        }.map { $0.checkpoint }
        let retained = store.checkpoints.filter { $0["home"] as? String != home && watched.contains(($0["task"] as? MonitorObject)?["id"] as? String ?? "") }
        store.checkpoints = Array((retained + current).prefix(512))
        store.cleanup(now: Date().timeIntervalSince1970)
        dirty = !store.save(); lastSaved = Date().timeIntervalSince1970
    }
    private func configureFallback() {
        let active = store.watches.contains { $0["active"] as? Bool == true }
        guard active != fallbackArmed else { return }
        fallback?.cancel(); fallback = nil; fallbackArmed = active
        guard active else { return }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        // FSEvents handles the normal path. Reconciliation is minute-scale and
        // only armed while there are active watches, including source failures.
        timer.schedule(deadline: .now() + 60, repeating: 60, leeway: .seconds(10))
        timer.setEventHandler { [weak self] in self?.beginDiscovery(); self?.forceRead = true; self?.schedule(delay: 0) }
        fallback = timer; timer.resume()
    }
    private func snapshot() -> MonitorObject {
        let section = view["section"] as? String ?? "watches", query = view["query"] as? String ?? ""
        let tasks = readers.values.map { $0.task.object }.sorted { (($0["updatedAt"] as? NSNumber)?.doubleValue ?? 0) > (($1["updatedAt"] as? NSNumber)?.doubleValue ?? 0) }
        let watches = store.watches.map { watch -> MonitorObject in
            var row = watch
            if watch["active"] as? Bool == true && watch["home"] as? String != home { row["status"] = "unknown"; row["sourceError"] = "监控来自其他 Codex 目录，请切回原目录或停止提醒" }
            if watch["active"] as? Bool == true && !readers.values.contains(where: { $0.task.id == watch["id"] as? String }) { row["status"] = "unknown"; row["sourceError"] = "源日志暂时不可用，未推断任务结束" }
            return row
        }
        let source = section == "tasks" ? tasks : section == "messages" ? Array(store.messages.reversed()) : section == "unread" ? store.messages.reversed().filter { $0["read"] as? Bool != true } : watches
        let messageIDs = view["messageIDs"] as? [String]
        let scoped = messageIDs.map { ids in source.filter { ids.contains($0["id"] as? String ?? "") } } ?? source
        let filtered = scoped.filter { query.isEmpty || ($0["title"] as? String ?? "").localizedCaseInsensitiveContains(query) || ($0["project"] as? String ?? "").localizedCaseInsensitiveContains(query) }
        let size = 30, pages = max(1, (filtered.count + size - 1) / size), page = min(pages - 1, view["page"] as? Int ?? 0)
        var rows = Array(filtered.dropFirst(page * size).prefix(size))
        // Keep the small distributed-notification protocol bounded. Detailed
        // source paths stay host-side; title/project are bounded UI metadata.
        for index in rows.indices {
            rows[index].removeValue(forKey: "home"); rows[index].removeValue(forKey: "path")
            rows[index]["title"] = MonitorJSON.text(rows[index]["title"], limit: 160)
            rows[index]["project"] = MonitorJSON.text(rows[index]["project"], limit: 256)
        }
        let active = watches.filter { $0["active"] as? Bool == true }
        let focused = watches.first { $0["id"] as? String == store.settings["focusID"] as? String }
        var summary: MonitorObject = ["active": active.count, "running": active.filter { $0["status"] as? String == "running" }.count,
            "unknown": active.filter { $0["status"] as? String == "unknown" }.count, "unread": store.messages.filter { $0["read"] as? Bool != true }.count]
        let represented = focused.map { [$0] } ?? watches
        let priority = ["unknown", "interrupted", "running", "idle", "completed"]
        summary["status"] = priority.first { status in represented.contains { $0["status"] as? String == status } } ?? "none"
        if !store.error.isEmpty || !sourceError.isEmpty, !watches.isEmpty { summary["status"] = "unknown" }
        if let focused = focused { summary["focus"] = ["id": focused["id"] ?? "", "title": MonitorJSON.text(focused["title"], limit: 80), "status": focused["status"] ?? "unknown"] }
        summary["preview"] = Array(watches.prefix(3)).map { row -> MonitorObject in
            ["id": row["id"] ?? "", "title": MonitorJSON.text(row["title"], limit: 80), "project": MonitorJSON.text(row["project"], limit: 160),
             "status": row["status"] ?? "unknown", "active": row["active"] ?? false]
        }
        var result: MonitorObject = ["section": section, "query": query, "page": page, "pages": pages, "total": filtered.count, "rows": rows,
            "summary": summary, "settings": store.settings, "error": store.error, "recovery": ["required": store.corrupt, "backupPath": store.recoveryBackup],
            "sourceStatus": ["home": home, "error": sourceError, "watching": watchAvailable, "scanning": discovery != nil || !discoveryRoots.isEmpty || readers.values.contains { $0.pending }, "checkedAt": checkedAt, "knownTasks": tasks.count]]
        if let ids = messageIDs { result["messageIDs"] = ids }
        return result
    }
    private func publish(force: Bool = false) {
        let state = snapshot()
        var comparable = state
        if var source = comparable["sourceStatus"] as? MonitorObject { source.removeValue(forKey: "checkedAt"); comparable["sourceStatus"] = source }
        let data = try? JSONSerialization.data(withJSONObject: comparable, options: [.sortedKeys])
        if force || data != lastPublished {
            lastPublished = data
            DispatchQueue.main.async { [weak self] in self?.changed?(state) }
        }
        let pending = store.pending, settings = store.settings
        if !pending.isEmpty { DispatchQueue.main.async { [weak self] in self?.deliver?(pending, settings) } }
    }
}
