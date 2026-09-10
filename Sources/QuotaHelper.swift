import Foundation
signal(SIGPIPE, SIG_IGN)

// This process has a fixed read-only protocol. It has no reset redemption or model-turn operation.
let arguments = CommandLine.arguments
func argument(_ name: String) -> String? { guard let i = arguments.firstIndex(of: name), i + 1 < arguments.count else { return nil }; return arguments[i+1] }
guard let output = argument("--output"), let parentText = argument("--parent-pid"), let parent = Int32(parentText), parent > 1, getppid() == parent else { exit(2) }
let destination = URL(fileURLWithPath: output)
let fm = FileManager.default
let home = fm.homeDirectoryForCurrentUser
var locations = ["/Applications/ChatGPT.app/Contents/Resources/codex", "/Applications/Codex.app/Contents/Resources/codex",
                 home.appendingPathComponent("Applications/ChatGPT.app/Contents/Resources/codex").path,
                 home.appendingPathComponent("Applications/Codex.app/Contents/Resources/codex").path,
                 home.appendingPathComponent(".local/bin/codex").path, "/opt/homebrew/bin/codex", "/usr/local/bin/codex"]
let executable = locations.first { fm.isExecutableFile(atPath: $0) }
func publish(_ value: [String: Any]) {
    do {
        try fm.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys])
        let temporary = destination.appendingPathExtension("\(getpid()).tmp")
        defer { try? fm.removeItem(at: temporary) }
        guard fm.createFile(atPath: temporary.path, contents: data, attributes: [.posixPermissions: 0o600]) else { return }
        if rename(temporary.path, destination.path) != 0 { return }
    } catch {}
}
func failure(_ message: String) {
    var value: [String: Any] = [:]
    if let data = try? Data(contentsOf: destination), data.count <= 65536,
       let old = try? JSONSerialization.jsonObject(with: data) as? [String: Any] { value = old }
    value["error"] = message; value["attempted_at"] = Date().timeIntervalSince1970
    publish(value)
}
guard let executable = executable else { failure("未找到 Codex；请先安装并登录 Codex"); exit(1) }
let child = Process(); child.executableURL = URL(fileURLWithPath: executable)
child.arguments = ["app-server", "--stdio", "-c", "analytics.enabled=false"]
let input = Pipe(), outputPipe = Pipe()
child.standardInput = input; child.standardOutput = outputPipe; child.standardError = FileHandle.nullDevice
let semaphore = DispatchSemaphore(value: 0)
let queue = DispatchQueue(label: "quota.readonly.protocol")
var buffer = Data(), completed = false, result: [String: Any]?
func send(_ value: [String: Any]) {
    guard let data = try? JSONSerialization.data(withJSONObject: value) else { return }
    do { try input.fileHandleForWriting.write(contentsOf: data + Data([10])) } catch { semaphore.signal() }
}
func receive(_ data: Data) {
    guard !completed else { return }
    buffer.append(data)
    guard buffer.count <= 1_048_576 else { completed = true; semaphore.signal(); return }
    while let end = buffer.firstIndex(of: 10) {
        let line = buffer.prefix(upTo: end); buffer.removeSubrange(...end)
        guard let value = try? JSONSerialization.jsonObject(with: line) as? [String: Any], value["method"] == nil, let id = value["id"] as? Int else { continue }
        if id == 1 {
            guard value["error"] == nil else { completed = true; semaphore.signal(); return }
            send(["method": "initialized"])
            send(["id": 2, "method": "account/rateLimits/read"])
        } else if id == 2 {
            if let raw = value["result"] as? [String: Any] {
                let buckets = raw["rateLimitsByLimitId"] as? [String: [String: Any]]
                let bucket = buckets?["codex"] ?? raw["rateLimits"] as? [String: Any] ?? [:]
                var windows: [[String: Any]] = []
                for key in ["primary", "secondary"] {
                    guard let window = bucket[key] as? [String: Any], let used = window["usedPercent"] as? NSNumber,
                          let duration = window["windowDurationMins"] as? NSNumber else { continue }
                    var entry: [String: Any] = ["used_percent": used, "duration_minutes": duration]
                    if let resets = window["resetsAt"] as? NSNumber { entry["resets_at"] = resets }
                    windows.append(entry)
                }
                var clean: [String: Any] = ["windows": windows, "updated_at": Date().timeIntervalSince1970]
                if let credits = raw["rateLimitResetCredits"] as? [String: Any], let count = credits["availableCount"] as? NSNumber { clean["reset_count"] = count }
                if windows.isEmpty { clean["error"] = "当前账号未返回订阅额度" }
                result = clean
            }
            completed = true; semaphore.signal(); return
        }
    }
}
outputPipe.fileHandleForReading.readabilityHandler = { handle in
    let bytes = handle.availableData
    queue.async { if bytes.isEmpty { semaphore.signal() } else { receive(bytes) } }
}
signal(SIGTERM, SIG_IGN); signal(SIGINT, SIG_IGN)
let termination = DispatchSource.makeSignalSource(signal: SIGTERM, queue: queue)
termination.setEventHandler { completed = true; semaphore.signal() }; termination.resume()
let interrupt = DispatchSource.makeSignalSource(signal: SIGINT, queue: queue)
interrupt.setEventHandler { completed = true; semaphore.signal() }; interrupt.resume()
let parentWatch = DispatchSource.makeTimerSource(queue: queue)
parentWatch.schedule(deadline: .now() + 1, repeating: 1)
parentWatch.setEventHandler { if getppid() != parent { completed = true; semaphore.signal() } }; parentWatch.resume()
do {
    try child.run()
    send(["id": 1, "method": "initialize", "params": ["clientInfo": ["name": "codex_usage_readonly", "version": "1.6.0"], "capabilities": ["experimentalApi": true, "requestAttestation": false]]])
    _ = semaphore.wait(timeout: .now() + 18)
} catch {}
outputPipe.fileHandleForReading.readabilityHandler = nil
parentWatch.cancel(); termination.cancel(); interrupt.cancel()
try? input.fileHandleForWriting.close()
if child.isRunning {
    child.terminate()
    for _ in 0..<40 { if !child.isRunning { break }; Thread.sleep(forTimeInterval: 0.05) }
    if child.isRunning { kill(child.processIdentifier, SIGKILL) }
    child.waitUntilExit()
}
var final: [String: Any]?
queue.sync { completed = true; final = result }
if getppid() != parent { exit(1) }
if let final = final { publish(final); exit(0) }
failure("额度读取失败，请确认 Codex 已登录后重试")
exit(1)
