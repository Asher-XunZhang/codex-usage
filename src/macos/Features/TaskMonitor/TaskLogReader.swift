import Foundation

/// Incremental JSONL reader. Only complete metadata/lifecycle records enter the
/// reducer. Forward and reverse work have caller-supplied byte budgets.
final class TaskLogReader {
    static let lineLimit = 256 * 1024
    static let tailLimit = 2 * 1024 * 1024
    var task: MonitoredTask
    private(set) var position: UInt64
    private var boundary: UInt64
    private var headerEnd: UInt64
    private var headerHash: String
    private var checkpointHash = ""
    private var partial = Data()
    private var discarding = false
    private var prefix = Data()
    private var backfillPosition: UInt64
    private var reversePartial = Data()
    private var reverseDiscarding = false
    private var forwardSkip = false
    private var fileID = ""
    private var modified: Double = 0
    private var lastSize: UInt64 = 0
    private(set) var offline = true
    private(set) var pending = true
    private(set) var bytesRead = 0
    private(set) var backfillBytes = 0
    var path: String { task.path }

    private static func header(_ handle: FileHandle) throws -> (MonitorObject, Data, UInt64)? {
        try handle.seek(toOffset: 0)
        var data = Data()
        while data.count < lineLimit && !data.contains(10) {
            let chunk = try handle.read(upToCount: min(1024, lineLimit - data.count)) ?? Data()
            if chunk.isEmpty { break }
            data.append(chunk)
        }
        guard let end = data.firstIndex(of: 10) else { return nil }
        var line = Data(data[..<end])
        if line.starts(with: [0xef, 0xbb, 0xbf]) { line.removeFirst(3) }
        guard let root = try? JSONSerialization.jsonObject(with: line) as? MonitorObject,
              root["type"] as? String == "session_meta", let meta = root["payload"] as? MonitorObject,
              MonitorJSON.validID(meta["id"] as? String ?? "") else { return nil }
        return (meta, line, UInt64(end + 1))
    }
    private static func hash(_ data: Data) -> String { MonitorJSON.digest(data.base64EncodedString()) }
    init?(url: URL, home: String, title: String? = nil) {
        do {
            let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
            guard attributes[.type] as? FileAttributeType == .typeRegular else { return nil }
            let handle = try FileHandle(forReadingFrom: url); defer { try? handle.close() }
            guard let (meta, data, end) = try Self.header(handle) else { return nil }
            let id = meta["id"] as! String
            let source = meta["source"] as? MonitorObject
            let subagent = source?["subagent"] as? MonitorObject
            let spawn = subagent?["thread_spawn"] as? MonitorObject
            let parent = meta["parent_thread_id"] as? String ?? spawn?["parent_thread_id"] as? String ?? ""
            task = MonitoredTask(id: id, home: home, path: url.path, parentID: parent,
                                 title: title ?? "未命名任务 · " + id.prefix(8), project: MonitorJSON.text(meta["cwd"], limit: 1024))
            headerEnd = end; position = end; boundary = end; backfillPosition = end; headerHash = Self.hash(data)
            let size = (attributes[.size] as? NSNumber)?.uint64Value ?? end
            if size > end + UInt64(Self.tailLimit) {
                position = size - UInt64(Self.tailLimit); boundary = position
                backfillPosition = position; forwardSkip = true
            }
            bytesRead += data.count
        } catch { return nil }
    }
    /// Restore only checkpoints whose source and metadata identity still match.
    func restore(_ checkpoint: MonitorObject, offline: Bool = true) {
        guard checkpoint["headerHash"] as? String == headerHash,
              let saved = checkpoint["task"] as? MonitorObject,
              let data = try? JSONSerialization.data(withJSONObject: saved),
              let task = try? JSONDecoder().decode(MonitoredTask.self, from: data),
              task.id == self.task.id, task.home == self.task.home, task.current != nil,
              let offset = (checkpoint["offset"] as? NSNumber)?.uint64Value, offset >= headerEnd else { return }
        let actualPath = self.task.path
        self.task = task; self.task.path = actualPath; self.task.error = "正在重新核对日志"
        position = offset; boundary = offset; checkpointHash = checkpoint["checkpointHash"] as? String ?? ""
        backfillPosition = (checkpoint["backfillPosition"] as? NSNumber)?.uint64Value ?? headerEnd
        if self.task.current == nil { backfillPosition = max(backfillPosition, offset) }
        partial = Data(); forwardSkip = false; pending = true; self.offline = offline
    }
    var checkpoint: MonitorObject {
        let encoded = (try? JSONEncoder().encode(task)).flatMap { try? JSONSerialization.jsonObject(with: $0) } ?? [:]
        return ["path": path, "home": task.home, "offset": boundary, "headerHash": headerHash, "checkpointHash": checkpointHash,
                "backfillPosition": backfillPosition, "task": encoded]
    }
    private func relevant(_ data: Data) -> Bool {
        let text = String(decoding: data, as: UTF8.self)
        return text.contains("event_msg") && (text.contains("task_started") || text.contains("task_complete") || text.contains("turn_aborted"))
    }
    private func consume(_ line: Data) -> MonitorTurn? {
        // Ordinary response/token records are ignored without materializing
        // their potentially large JSON trees or retaining their contents.
        guard relevant(line) else { return nil }
        guard let object = try? JSONSerialization.jsonObject(with: line) as? MonitorObject else {
            task.error = "部分生命周期记录无法解析，状态待确认"; return nil
        }
        return task.consume(object)
    }
    private func reset(at end: UInt64) {
        task.turns = [:]; task.turnID = ""; task.error = "日志已重写，正在重新核对"
        position = end; boundary = end; backfillPosition = end; checkpointHash = ""
        partial = Data(); prefix = Data(); reversePartial = Data(); discarding = false; reverseDiscarding = false; forwardSkip = false; offline = true
    }
    /// Returns lifecycle events plus their offline provenance. Caller serializes
    /// store updates and only publishes once a batch has completed.
    func read(budget: inout Int, force: Bool = false) -> [(MonitorTurn, Bool)] {
        guard task.parentID.isEmpty else { pending = false; return [] }
        guard budget > 0 else { pending = true; task.error = "正在分批核对任务日志"; return [] }
        var events: [(MonitorTurn, Bool)] = []
        do {
            let attributes = try FileManager.default.attributesOfItem(atPath: path)
            guard attributes[.type] as? FileAttributeType == .typeRegular else { throw TaskMonitorStore.Failure(text: "日志路径不再是普通文件") }
            let size = (attributes[.size] as? NSNumber)?.uint64Value ?? 0
            let stamp = (attributes[.modificationDate] as? Date)?.timeIntervalSince1970 ?? 0
            if !force && !pending && size == lastSize && stamp == modified { return [] }
            let handle = try FileHandle(forReadingFrom: URL(fileURLWithPath: path)); defer { try? handle.close() }
            // Recheck only the known metadata line; large response bodies do
            // not increase the cost of verifying an existing reader.
            guard budget >= Int(headerEnd) + 128 else { pending = true; task.error = "正在分批核对任务日志"; return [] }
            let headerBytes = try handle.read(upToCount: Int(headerEnd)) ?? Data()
            budget -= headerBytes.count; bytesRead += headerBytes.count
            var header = Data(headerBytes.dropLast())
            if header.starts(with: [0xef, 0xbb, 0xbf]) { header.removeFirst(3) }
            guard headerBytes.last == 10,
                  let root = try? JSONSerialization.jsonObject(with: header) as? MonitorObject,
                  root["type"] as? String == "session_meta",
                  let metadata = root["payload"] as? MonitorObject, metadata["id"] as? String == task.id else {
                throw TaskMonitorStore.Failure(text: "日志身份或元数据已变化，请重新发现此文件")
            }
            let end = headerEnd
            let identity = "\(attributes[.systemNumber] ?? ""):\(attributes[.systemFileNumber] ?? "")"
            let newHash = Self.hash(header)
            var rewritten = size < position || newHash != headerHash || (!fileID.isEmpty && fileID != identity)
            if !checkpointHash.isEmpty, boundary >= 1, size >= boundary {
                let count = min(UInt64(64), boundary); try handle.seek(toOffset: boundary - count)
                let fingerprint = try handle.read(upToCount: Int(count)) ?? Data()
                budget -= fingerprint.count; bytesRead += fingerprint.count
                rewritten = rewritten || Self.hash(fingerprint) != checkpointHash
            }
            if rewritten { reset(at: end) }
            headerEnd = end; headerHash = newHash; fileID = identity
            let wasOffline = offline
            try handle.seek(toOffset: position)
            let amount = min(max(0, budget - 64), Self.tailLimit)
            let chunk = try handle.read(upToCount: amount) ?? Data(); budget -= chunk.count; bytesRead += chunk.count
            for byte in chunk {
                position += 1
                if byte == 10 {
                    if !forwardSkip && !discarding, let turn = consume(partial) { events.append((turn, wasOffline)) }
                    else if discarding && relevant(prefix) { task.error = "生命周期记录过长，状态待确认" }
                    if forwardSkip {
                        // Preserve the suffix crossing the tail cut so reverse
                        // reads can reconstruct that exact lifecycle record.
                        reversePartial = partial; reverseDiscarding = discarding
                    }
                    partial.removeAll(keepingCapacity: false); prefix.removeAll(keepingCapacity: false)
                    discarding = false; forwardSkip = false; boundary = position
                } else {
                    if prefix.count < 512 { prefix.append(byte) }
                    if !discarding {
                        if partial.count < Self.lineLimit { partial.append(byte) }
                        else { partial.removeAll(keepingCapacity: false); discarding = true }
                    }
                }
            }
            // Tail discovery needs reverse work only until its newest explicit
            // lifecycle is found. Restored active watches read from their saved
            // forward checkpoint, so offline terminal events are not skipped.
            if position >= size && task.current == nil && backfillPosition > headerEnd && budget > 64 {
                let count = min(budget - 64, Self.lineLimit, Int(backfillPosition - headerEnd))
                let start = backfillPosition - UInt64(count); try handle.seek(toOffset: start)
                let chunk = try handle.read(upToCount: count) ?? Data()
                budget -= chunk.count; bytesRead += chunk.count; backfillBytes += chunk.count
                var fragments = Array(chunk).split(separator: UInt8(10), omittingEmptySubsequences: false).map { Data($0) }
                if let last = fragments.indices.last {
                    if !reverseDiscarding && fragments[last].count + reversePartial.count <= Self.lineLimit { fragments[last].append(reversePartial) }
                    else { reverseDiscarding = true }
                }
                reversePartial = Data()
                for index in fragments.indices.reversed() {
                    let complete = index > 0 || start == headerEnd
                    if complete {
                        if !reverseDiscarding, let turn = consume(fragments[index]) { events.append((turn, true)) }
                        else if reverseDiscarding && relevant(fragments[index]) { task.error = "回溯遇到过长生命周期记录，状态待确认" }
                        reverseDiscarding = false
                        if task.current != nil { break }
                    } else if fragments[index].count <= Self.lineLimit && !reverseDiscarding { reversePartial = fragments[index] }
                    else { reverseDiscarding = true }
                }
                backfillPosition = task.current == nil ? start : headerEnd
            }
            if task.current != nil { backfillPosition = headerEnd; reversePartial = Data(); reverseDiscarding = false }
            pending = position < size || (task.current == nil && backfillPosition > headerEnd)
            let hasPartialLifecycle = (forwardSkip || discarding || !partial.isEmpty) && relevant(prefix)
            if pending { task.error = "正在分批核对任务日志" }
            else if hasPartialLifecycle { task.error = "生命周期记录尚未完整写入，状态待确认" }
            else if task.current == nil { task.error = "尚未发现可识别的轮次事件" }
            else if ["正在重新核对日志", "正在分批核对任务日志", "生命周期记录尚未完整写入，状态待确认", "日志已重写，正在重新核对"].contains(task.error) { task.error = "" }
            if !pending { offline = false }
            modified = stamp; lastSize = size
            if boundary > 0, boundary <= size {
                let count = min(UInt64(64), boundary); try handle.seek(toOffset: boundary - count)
                let fingerprint = try handle.read(upToCount: Int(count)) ?? Data()
                budget -= fingerprint.count; bytesRead += fingerprint.count
                checkpointHash = Self.hash(fingerprint)
            }
        } catch { task.error = error.localizedDescription; pending = false }
        return events
    }
}
