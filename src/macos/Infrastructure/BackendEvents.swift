import Foundation
import Darwin

/// The pipe contains only versioned bounded JSON. Ordinary logs use stderr.
/// Parsing is serial and background; published state changes enter AppKit's queue.
final class BackendEventChannel {
    var event: (([String: Any]) -> Void)?
    var failed: (() -> Void)?
    private let handle: FileHandle
    private let queue = DispatchQueue(label: "local.codex-usage.backend-events", qos: .utility)
    private var buffer = Data()
    private var closed = false
    init(handle: FileHandle) { self.handle = handle }
    func start() {
        handle.readabilityHandler = { [weak self] handle in
            // Foundation read(upToCount:) can wait to fill the requested count.
            // A single POSIX read returns the bytes currently available in the pipe.
            var bytes = [UInt8](repeating: 0, count: 16384)
            let count = Darwin.read(handle.fileDescriptor, &bytes, bytes.count)
            if count >= 0 {
                let data = Data(bytes.prefix(count))
                self?.queue.async { [weak self] in self?.consume(data) }
            } else if errno != EINTR && errno != EAGAIN {
                self?.queue.async { [weak self] in self?.end(report: true) }
            }
        }
    }
    func close() {
        handle.readabilityHandler = nil
        queue.async { [weak self] in self?.end(report: false) }
    }
    deinit { handle.readabilityHandler = nil; try? handle.close() }
    private func end(report: Bool) {
        guard !closed else { return }; closed = true; buffer = Data(); handle.readabilityHandler = nil
        try? handle.close()
        if report { DispatchQueue.main.async { [weak self] in self?.failed?() } }
    }
    private func consume(_ data: Data) {
        guard !closed else { return }
        guard !data.isEmpty else { end(report: true); return }
        buffer.append(data)
        while let newline = buffer.firstIndex(of: 10) {
            let line = Data(buffer[..<newline]); buffer.removeSubrange(...newline)
            guard line.count <= 4096,
                  let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                  object["protocol"] as? Int == 1,
                  ["desktop_ready", "desktop_state"].contains(object["event"] as? String ?? "") else { end(report: true); return }
            DispatchQueue.main.async { [weak self] in self?.event?(object) }
        }
        if buffer.count > 4096 { end(report: true) }
    }
}
