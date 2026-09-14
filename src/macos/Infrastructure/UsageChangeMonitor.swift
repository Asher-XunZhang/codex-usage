import Foundation
import CoreServices
import AppKit

/// Compact mode watches metadata/log paths, not their contents. The callback
/// only marks data dirty; the controller applies the user's refresh interval.
final class UsageChangeMonitor {
    private var stream: FSEventStreamRef?
    private var reconciliation: Timer?
    private var wakeObserver: NSObjectProtocol?
    private(set) var home: URL?
    var isWatching: Bool { stream != nil }
    /// A second consumer shares this stream rather than opening another watcher.
    var events: (([String], Bool) -> Void)?
    private let changed: () -> Void
    init(changed: @escaping () -> Void) { self.changed = changed }
    deinit { stop() }

    static func isUsagePath(_ path: String, root: String, flags: FSEventStreamEventFlags) -> Bool {
        flags & UInt32(kFSEventStreamEventFlagMustScanSubDirs | kFSEventStreamEventFlagRootChanged | kFSEventStreamEventFlagUserDropped | kFSEventStreamEventFlagKernelDropped) != 0 ||
        path == root || path == root + "/sessions" || path == root + "/archived_sessions" ||
        path.hasPrefix(root + "/sessions/") || path.hasPrefix(root + "/archived_sessions/") ||
        path.hasPrefix(root + "/state_5.sqlite") || path == root + "/session_index.jsonl"
    }

    func start(home: URL) {
        let home = home.standardizedFileURL
        guard self.home != home else { return }
        stop(); self.home = home
        var context = FSEventStreamContext(version: 0, info: Unmanaged.passUnretained(self).toOpaque(), retain: nil, release: nil, copyDescription: nil)
        let callback: FSEventStreamCallback = { _, info, count, paths, flags, _ in
            guard let info = info else { return }
            let owner = Unmanaged<UsageChangeMonitor>.fromOpaque(info).takeUnretainedValue()
            guard let root = owner.home?.path else { return }
            let names = unsafeBitCast(paths, to: CFArray.self) as! [String]
            let relevant = (0..<count).filter { UsageChangeMonitor.isUsagePath(names[$0], root: root, flags: flags[$0]) }
            if !relevant.isEmpty {
                owner.changed()
                let dropped = relevant.contains { flags[$0] & UInt32(kFSEventStreamEventFlagMustScanSubDirs | kFSEventStreamEventFlagRootChanged | kFSEventStreamEventFlagUserDropped | kFSEventStreamEventFlagKernelDropped) != 0 }
                let directoryChange = relevant.contains { flags[$0] & UInt32(kFSEventStreamEventFlagItemIsDir) != 0 }
                owner.events?(relevant.map { names[$0] }, dropped || directoryChange)
            }
        }
        stream = FSEventStreamCreate(nil, callback, &context, [home.path] as CFArray,
            FSEventStreamEventId(kFSEventStreamEventIdSinceNow), 1,
            FSEventStreamCreateFlags(kFSEventStreamCreateFlagUseCFTypes | kFSEventStreamCreateFlagFileEvents | kFSEventStreamCreateFlagWatchRoot))
        if let stream = stream {
            FSEventStreamSetDispatchQueue(stream, DispatchQueue.main)
            if !FSEventStreamStart(stream) { FSEventStreamInvalidate(stream); FSEventStreamRelease(stream); self.stream = nil }
        }
        wakeObserver = NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            self?.changed(); self?.events?([], true)
        }
        let timer = Timer(timeInterval: 600, repeats: true) { [weak self] _ in self?.changed(); self?.events?([], true) }
        timer.tolerance = 30; reconciliation = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    func stop() {
        if let observer = wakeObserver { NSWorkspace.shared.notificationCenter.removeObserver(observer); wakeObserver = nil }
        reconciliation?.invalidate(); reconciliation = nil
        if let stream = stream {
            FSEventStreamStop(stream); FSEventStreamInvalidate(stream); FSEventStreamRelease(stream)
        }
        stream = nil; home = nil
    }
}
