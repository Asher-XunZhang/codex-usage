import Foundation
import AppKit

/// Choice lists exist only while a selector is open. This native cache reader
/// does not start Python, scan logs, or retain historical task metadata.
final class FloatingChoicesReader {
    private var process: Process?
    private var output: URL?
    private var generation = 0
    private var pending: (([(String, String)]?, String?) -> Void)?
    private var timeout: DispatchWorkItem?
    func cancel() {
        generation += 1
        timeout?.cancel(); timeout = nil
        if let child = process, child.isRunning {
            child.terminate()
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                if child.isRunning { kill(child.processIdentifier, SIGKILL) }
            }
        }
        process = nil
        if let path = output { try? FileManager.default.removeItem(at: path) }; output = nil
        let callback = pending; pending = nil; callback?(nil, nil)
    }
    func load(cache: URL, query: FloatingUsageQuery, kind: String,
              completion: @escaping ([(String, String)]?, String?) -> Void) {
        cancel()
        guard kind == "model" || kind == "task" else { completion(nil, "未知筛选类型"); return }
        let ticket = generation
        let path = FileManager.default.temporaryDirectory.appendingPathComponent("codex-usage-choices-\(UUID().uuidString).json")
        guard FileManager.default.createFile(atPath: path.path, contents: nil, attributes: [.posixPermissions: 0o600]) else { completion(nil, "无法创建筛选缓存"); return }
        guard let handle = try? FileHandle(forWritingTo: path) else {
            try? FileManager.default.removeItem(at: path); completion(nil, "无法创建筛选缓存"); return
        }
        let child = Process()
        child.executableURL = usageMacOSURL.appendingPathComponent("CodexSummary")
        child.arguments = ["--cache-path", cache.path, "--choices", kind, "--days", query.days, "--model", query.model, "--task", query.task]
        child.standardOutput = handle; child.standardError = FileHandle.nullDevice
        child.terminationHandler = { [weak self] finished in
            DispatchQueue.main.async {
                defer { try? FileManager.default.removeItem(at: path) }
                guard let self = self, self.generation == ticket else { return }
                self.process = nil; self.output = nil; self.timeout?.cancel(); self.timeout = nil
                let callback = self.pending; self.pending = nil
                // Delivery can enter native menu tracking. Retire the JSON and
                // file buffers before that nested run loop keeps this frame alive.
                let choices: [(String, String)]? = autoreleasepool {
                    guard finished.terminationStatus == 0,
                          let size = (try? FileManager.default.attributesOfItem(atPath: path.path)[.size]) as? NSNumber, size.intValue <= 8 * 1024 * 1024,
                          let bytes = try? Data(contentsOf: path), let json = try? JSONSerialization.jsonObject(with: bytes) as? Object,
                          let rows = json["choices"] as? [Object] else { return nil }
                    return rows.compactMap { row -> (String, String)? in
                        guard let id = row["id"] as? String, let label = row["label"] as? String else { return nil }
                        // Keep IDs in task titles so equal names remain distinguishable.
                        return (id, kind == "task" && id != "all" ? "\(label) · \(id)" : label)
                    }
                }
                guard let choices = choices else { callback?(nil, "筛选项暂不可读，请稍后重试"); return }
                callback?(choices, nil)
            }
        }
        process = child; output = path; pending = completion
        do {
            try child.run(); try? handle.close()
            let work = DispatchWorkItem { [weak self] in
                guard let self = self, self.generation == ticket else { return }
                let callback = self.pending; self.pending = nil; self.cancel()
                callback?(nil, "读取筛选项超时，请重试")
            }
            timeout = work; DispatchQueue.main.asyncAfter(deadline: .now() + 4, execute: work)
        } catch {
            try? handle.close(); pending = nil; cancel(); completion(nil, "筛选组件启动失败")
        }
    }
}

/// While Main owns the indexer, the host only reads committed SQLite summaries.
/// Each request runs the tiny native helper and retains no index or history rows.
final class NativeSummaryReader {
    private let executable: URL
    private var process: Process?
    private var output: URL?
    private var generation = 0
    private var pending: ((Result<Object, Error>) -> Void)?
    private var deadline: DispatchWorkItem?
    init(executable: URL = usageMacOSURL.appendingPathComponent("CodexSummary")) { self.executable = executable }
    func cancel() {
        generation += 1; deadline?.cancel(); deadline = nil; pending = nil
        if let child = process, child.isRunning {
            child.terminate()
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { if child.isRunning { kill(child.processIdentifier, SIGKILL) } }
        }
        process = nil
        if let output = output { try? FileManager.default.removeItem(at: output) }; output = nil
    }
    func read(cache: URL, query: FloatingUsageQuery, seconds: Int, completion: @escaping (Result<Object, Error>) -> Void) {
        cancel(); let ticket = generation
        let path = FileManager.default.temporaryDirectory.appendingPathComponent("codex-usage-summary-\(UUID().uuidString).json")
        guard FileManager.default.createFile(atPath: path.path, contents: nil, attributes: [.posixPermissions: 0o600]) else {
            completion(.failure(NSError(domain: "NativeSummary", code: 1))); return
        }
        guard let handle = try? FileHandle(forWritingTo: path) else {
            try? FileManager.default.removeItem(at: path); completion(.failure(NSError(domain: "NativeSummary", code: 1))); return
        }
        let child = Process(); child.executableURL = executable
        child.arguments = ["--cache-path", cache.path, "--days", query.days, "--model", query.model, "--task", query.task, "--refresh-seconds", String(seconds)]
        child.standardOutput = handle; child.standardError = FileHandle.nullDevice
        child.terminationHandler = { [weak self] finished in
            DispatchQueue.main.async {
                defer { try? FileManager.default.removeItem(at: path) }
                guard let self = self, self.generation == ticket else { return }
                self.process = nil; self.output = nil; self.deadline?.cancel(); self.deadline = nil
                let callback = self.pending; self.pending = nil
                guard finished.terminationStatus == 0,
                      let size = (try? FileManager.default.attributesOfItem(atPath: path.path)[.size]) as? NSNumber, size.intValue <= 65_536,
                      let bytes = try? Data(contentsOf: path), let json = try? JSONSerialization.jsonObject(with: bytes) as? Object,
                      json["today"] is Object, json["filtered"] is Object else {
                    callback?(.failure(NSError(domain: "NativeSummary", code: Int(finished.terminationStatus == 0 ? 2 : finished.terminationStatus)))); return
                }
                callback?(.success(json))
            }
        }
        process = child; output = path; pending = completion
        do {
            try child.run(); try? handle.close()
            let timeout = DispatchWorkItem { [weak self] in
                guard let self = self, self.generation == ticket else { return }
                let callback = self.pending; self.cancel()
                callback?(.failure(NSError(domain: "NativeSummary", code: 3)))
            }
            deadline = timeout; DispatchQueue.main.asyncAfter(deadline: .now() + 4, execute: timeout)
        } catch { try? handle.close(); cancel(); completion(.failure(error)) }
    }
}

final class Backend {
    let root: URL
    let resources = usageResourcesURL
    var process: Process?
    var stateURL: URL?
    var url: URL?
    var identity = ""
    var generation = UUID()
    var starting = false
    var stopping = false
    var onExit: (() -> Void)?
    var logHandle: FileHandle?
    var stopCallbacks: [() -> Void] = []
    init() {
        let home = FileManager.default.homeDirectoryForCurrentUser
        root = ProcessInfo.processInfo.environment["CODEX_USAGE_DESKTOP_BASE"].map { URL(fileURLWithPath: $0) }
            ?? home.appendingPathComponent("Library/Application Support/CodexUsageDashboard")
    }
    func cachePath(_ home: URL) -> URL {
        var hex = [CChar](repeating: 0, count: 65)
        home.standardizedFileURL.path.withCString { usage_sha256($0, &hex) }
        let key = String(cString: hex).prefix(24)
        return root.appendingPathComponent("indexes/usage-v1-" + key + ".sqlite")
    }
    func prepareRuntime() throws -> URL {
        #if arch(arm64)
        let arch = "aarch64"
        let digest = "3ee3ee547cedfeb7c2b16b2b7156039f7b470bb8f857e226fd3d2eb11db83c76"
        #else
        let arch = "x86_64"
        let digest = "2e31b23f3f1319f707d0e620b48847a0046577541d357276821f9f1b5492e0ba"
        #endif
        let fm = FileManager.default
        let parent = root.appendingPathComponent("runtimes")
        let target = parent.appendingPathComponent("3.12.14-20260901-\(arch)-apple-darwin")
        let relative = "python/bin/python3.12"
        let python = target.appendingPathComponent(relative)
        if fm.isExecutableFile(atPath: python.path) { return python }
        try fm.createDirectory(at: parent, withIntermediateDirectories: true)
        let archive = resources.appendingPathComponent("runtimes/cpython-3.12.14+20260901-\(arch)-apple-darwin-install_only.tar.gz")
        var hex = [CChar](repeating: 0, count: 65)
        let checked = archive.path.withCString { usage_sha256_file($0, &hex) }
        guard checked == 0, String(cString: hex) == digest else {
            throw NSError(domain: "Desktop", code: 1, userInfo: [NSLocalizedDescriptionKey: "运行组件校验失败，请重新解压完整的 App。"])
        }
        let staging = parent.appendingPathComponent(".desktop-\(UUID().uuidString)")
        try fm.createDirectory(at: staging, withIntermediateDirectories: false)
        defer { try? fm.removeItem(at: staging) }
        let extractor = Process()
        extractor.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
        extractor.arguments = ["-xzf", archive.path, "-C", staging.path]
        try extractor.run(); extractor.waitUntilExit()
        guard extractor.terminationStatus == 0, fm.isExecutableFile(atPath: staging.appendingPathComponent(relative).path) else {
            throw NSError(domain: "Desktop", code: 2, userInfo: [NSLocalizedDescriptionKey: "运行组件解压失败，请检查磁盘空间及文件夹权限。"])
        }
        // A simultaneous installer may have completed the identical runtime.
        if fm.isExecutableFile(atPath: python.path) { return python }
        if fm.fileExists(atPath: target.path) {
            throw NSError(domain: "Desktop", code: 3, userInfo: [NSLocalizedDescriptionKey: "发现不完整的运行组件，请在支持文件夹中检查 runtimes 目录。"])
        }
        try fm.moveItem(at: staging, to: target)
        return python
    }
    func start(home: URL, refreshSeconds: Int, completion: @escaping (Result<URL, Error>) -> Void) {
        guard !starting, process == nil, !stopping else { return }
        starting = true
        generation = UUID()
        let ticket = generation
        DispatchQueue.global(qos: .userInitiated).async {
            let result = Result { try self.prepareRuntime() }
            DispatchQueue.main.async {
                guard self.generation == ticket, !self.stopping else { return }
                do {
                    let python = try result.get()
                    let fm = FileManager.default
                    let states = self.root.appendingPathComponent("desktop")
                    let logs = self.root.appendingPathComponent("logs")
                    try fm.createDirectory(at: states, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
                    try fm.createDirectory(at: logs, withIntermediateDirectories: true)
                    self.identity = UUID().uuidString
                    self.stateURL = states.appendingPathComponent("worker-\(self.identity).json")
                    let log = logs.appendingPathComponent("desktop.log")
                    if !fm.fileExists(atPath: log.path) { fm.createFile(atPath: log.path, contents: nil, attributes: [.posixPermissions: 0o600]) }
                    let handle = try FileHandle(forWritingTo: log)
                    handle.seekToEndOfFile()
                    self.logHandle = handle
                    let child = Process()
                    child.executableURL = python
                    child.arguments = ["-E", "-s", "-B", self.resources.appendingPathComponent("backend/dashboard_server.py").path,
                                       "--port", "0", "--codex-home", home.path, "--state-file", self.stateURL!.path,
                                       "--instance-id", self.identity, "--parent-pid", String(getpid()),
                                       "--refresh-seconds", String(refreshSeconds),
                                       "--cache-path", self.cachePath(home).path]
                    child.standardOutput = handle; child.standardError = handle
                    child.terminationHandler = { [weak self] _ in
                        DispatchQueue.main.async {
                            guard let self = self, self.generation == ticket, !self.stopping else { return }
                            let wasStarting = self.starting
                            self.process = nil; self.url = nil; self.starting = false
                            try? self.logHandle?.close(); self.logHandle = nil
                            if let state = self.stateURL { try? fm.removeItem(at: state) }
                            if wasStarting {
                                completion(.failure(NSError(domain: "Desktop", code: 4, userInfo: [NSLocalizedDescriptionKey: "统计进程未能启动，可打开支持文件夹查看日志并重试。"])))
                            } else { self.onExit?() }
                        }
                    }
                    self.process = child
                    try child.run()
                    self.waitForState(ticket, attempts: 150, completion: completion)
                } catch {
                    self.process = nil; self.starting = false
                    try? self.logHandle?.close(); self.logHandle = nil
                    completion(.failure(error))
                }
            }
        }
    }
    private func waitForState(_ ticket: UUID, attempts: Int, completion: @escaping (Result<URL, Error>) -> Void) {
        guard generation == ticket, !stopping, starting, let child = process else { return }
        if let path = stateURL, let data = try? Data(contentsOf: path),
           let obj = try? JSONSerialization.jsonObject(with: data) as? Object,
           (obj["pid"] as? NSNumber)?.doubleValue == Double(child.processIdentifier),
           let text = obj["url"] as? String, let parsed = URL(string: text), parsed.host == "127.0.0.1", parsed.scheme == "http", parsed.port != nil {
            url = parsed; starting = false; completion(.success(parsed)); return
        }
        guard attempts > 0 else {
            stop { completion(.failure(NSError(domain: "Desktop", code: 5, userInfo: [NSLocalizedDescriptionKey: "启动超时，请重试或查看支持文件夹内的日志。"]))) }
            return
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { self.waitForState(ticket, attempts: attempts - 1, completion: completion) }
    }
    func stop(_ completion: @escaping () -> Void) {
        stopCallbacks.append(completion)
        guard !stopping else { return }
        stopping = true; starting = false; generation = UUID(); url = nil
        let child = process
        process = nil
        let state = stateURL
        child?.terminationHandler = nil
        if child?.isRunning == true { child?.terminate() }
        DispatchQueue.global(qos: .userInitiated).async {
            if let child = child {
                for _ in 0..<50 {
                    if !child.isRunning { break }
                    Thread.sleep(forTimeInterval: 0.1)
                }
                if child.isRunning { kill(child.processIdentifier, SIGKILL) }
                child.waitUntilExit()
            }
            if let state = state { try? FileManager.default.removeItem(at: state) }
            DispatchQueue.main.async {
                try? self.logHandle?.close(); self.logHandle = nil
                self.stopping = false
                let callbacks = self.stopCallbacks; self.stopCallbacks = []
                callbacks.forEach { $0() }
            }
        }
    }
}

/// Compact mode owns a short-lived process, never a resident HTTP/index server.
final class CompactCollector {
    let backend: Backend
    var process: Process?
    var generation = UUID()
    var busy = false
    var stopping = false
    var outputURL: URL?
    var stops: [() -> Void] = []
    init(_ backend: Backend) { self.backend = backend }
    func collect(home: URL, days: String, model: String, task: String, seconds: Int,
                 completion: @escaping (Result<Object, Error>) -> Void) {
        guard !busy, !stopping else { return }
        busy = true; generation = UUID(); let ticket = generation
        DispatchQueue.global(qos: .utility).async {
            let runtime = Result { try self.backend.prepareRuntime() }
            DispatchQueue.main.async {
                guard self.generation == ticket, !self.stopping else { return }
                do {
                    let python = try runtime.get()
                    let folder = self.backend.root.appendingPathComponent("desktop")
                    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
                    let output = folder.appendingPathComponent("snapshot-\(ticket.uuidString).json")
                    self.outputURL = output
                    let child = Process(); child.executableURL = python
                    child.arguments = ["-E", "-s", "-B", self.backend.resources.appendingPathComponent("backend/compact_snapshot.py").path,
                        "--codex-home", home.path, "--cache-path", self.backend.cachePath(home).path,
                        "--output", output.path, "--parent-pid", String(getpid()), "--days", days, "--model", model,
                        "--task", task, "--refresh-seconds", String(seconds)]
                    child.standardOutput = FileHandle.nullDevice; child.standardError = FileHandle.nullDevice
                    child.terminationHandler = { [weak self] finished in
                        DispatchQueue.main.async {
                            guard let self = self, self.generation == ticket, !self.stopping else { return }
                            self.process = nil; self.busy = false
                            defer { try? FileManager.default.removeItem(at: output); self.outputURL = nil }
                            do {
                                let size = (try FileManager.default.attributesOfItem(atPath: output.path)[.size] as? NSNumber)?.intValue ?? 0
                                guard finished.terminationStatus == 0, size > 0, size <= 65536,
                                      let json = try JSONSerialization.jsonObject(with: Data(contentsOf: output)) as? Object,
                                      json["today"] is Object, json["filtered"] is Object else {
                                    throw NSError(domain: "Collector", code: 1)
                                }
                                completion(.success(json))
                            } catch { completion(.failure(error)) }
                        }
                    }
                    self.process = child; try child.run()
                } catch { self.process = nil; self.busy = false; completion(.failure(error)) }
            }
        }
    }
    func stop(_ completion: @escaping () -> Void) {
        stops.append(completion)
        guard !stopping else { return }
        stopping = true; generation = UUID(); busy = false
        let child = process; process = nil; let output = outputURL; outputURL = nil
        child?.terminationHandler = nil
        if child?.isRunning == true { child?.terminate() }
        DispatchQueue.global(qos: .utility).async {
            if let child = child {
                for _ in 0..<30 { if !child.isRunning { break }; Thread.sleep(forTimeInterval: 0.1) }
                if child.isRunning { kill(child.processIdentifier, SIGKILL) }
                child.waitUntilExit()
            }
            if let output = output { try? FileManager.default.removeItem(at: output); try? FileManager.default.removeItem(at: output.deletingPathExtension().appendingPathExtension("tmp")) }
            DispatchQueue.main.async {
                self.stopping = false
                let callbacks = self.stops; self.stops = []; callbacks.forEach { $0() }
            }
        }
    }
}
