import Foundation

enum QuotaExecutable {
    static func candidates(home: URL) -> [String] {
        let applications = ["/Applications/ChatGPT.app", "/Applications/Codex.app",
                            home.appendingPathComponent("Applications/ChatGPT.app").path,
                            home.appendingPathComponent("Applications/Codex.app").path]
        return applications.flatMap { app in
            [app + "/Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex",
             app + "/Contents/Resources/codex"]
        } + [home.appendingPathComponent(".local/bin/codex").path,
             "/opt/homebrew/bin/codex", "/usr/local/bin/codex"]
    }
}

/// Serial request state shared by the helper and protocol regression tests.
/// Only an authorization rejection triggers one managed token refresh.
final class QuotaRequest {
    private var expectedID = 1
    private(set) var completed = false
    var send: ([String: Any]) -> Void = { _ in }
    var finish: ([String: Any]?, String?) -> Void = { _, _ in }

    private func end(_ result: [String: Any]? = nil, error: String? = nil) {
        guard !completed else { return }
        completed = true; finish(result, error)
    }
    private func request(_ id: Int, _ method: String, params: [String: Any]? = nil) {
        expectedID = id
        var value: [String: Any] = ["id": id, "method": method]
        if let params = params { value["params"] = params }
        send(value)
    }
    private func unauthorized(_ error: [String: Any]) -> Bool {
        if (error["code"] as? Int) == 401 { return true }
        if let data = error["data"] as? [String: Any], (data["status"] as? Int) == 401 { return true }
        let message = (error["message"] as? String ?? "").lowercased()
        return message.range(of: #"\b401\b|\bunauthorized\b|\btoken_expired\b"#, options: .regularExpression) != nil
    }
    private func describe(_ error: [String: Any], refreshing: Bool = false) -> String {
        // Never publish a raw server message: it may contain URLs or credentials.
        let message = (error["message"] as? String ?? "").lowercased()
        if (error["code"] as? Int) == -32601 || (error["code"] as? Int) == -32602 {
            return "Codex 版本不兼容，请更新 Codex 后重试"
        }
        if ["timed out", "timeout", "deadline exceeded"].contains(where: message.contains) {
            return "额度查询超时，请检查网络后重试"
        }
        if ["error sending request", "connection refused", "connection reset", "dns", "tls", "connect error", "network"].contains(where: message.contains) {
            return "无法连接额度服务，请检查网络或代理后重试"
        }
        if unauthorized(error) || refreshing {
            return "登录已失效，请在 Codex 中重新登录后重试"
        }
        if message.range(of: #"\b403\b|\bforbidden\b"#, options: .regularExpression) != nil {
            return "账号无权读取额度，请检查 Codex 账号与订阅"
        }
        if message.contains("chatgpt") && ["auth", "login", "log in", "sign in"].contains(where: message.contains) {
            return "请先在 Codex 中登录订阅账号后重试"
        }
        return "额度查询失败，请稍后重试或检查 Codex 连接"
    }
    func receive(_ value: [String: Any]) {
        guard !completed, value["method"] == nil, let id = value["id"] as? Int, id == expectedID else { return }
        if let error = value["error"] as? [String: Any] {
            if id == 2 && unauthorized(error) {
                request(3, "account/read", params: ["refreshToken": true])
            } else { end(error: describe(error, refreshing: id == 3)) }
            return
        }
        guard let raw = value["result"] as? [String: Any] else {
            end(error: "Codex 返回的额度响应无效，请更新 Codex 后重试"); return
        }
        if id == 1 {
            send(["method": "initialized"])
            request(2, "account/rateLimits/read")
        } else if id == 3 {
            guard let account = raw["account"] as? [String: Any], account["type"] as? String == "chatgpt" else {
                end(error: "登录已失效，请在 Codex 中重新登录后重试"); return
            }
            request(4, "account/rateLimits/read")
        } else {
            let buckets = raw["rateLimitsByLimitId"] as? [String: [String: Any]]
            let bucket = buckets?["codex"] ?? raw["rateLimits"] as? [String: Any] ?? [:]
            var windows: [[String: Any]] = []
            for key in ["primary", "secondary"] {
                guard let window = bucket[key] as? [String: Any], let used = window["usedPercent"] as? NSNumber,
                      used.doubleValue.isFinite, let duration = window["windowDurationMins"] as? NSNumber,
                      duration.intValue > 0 else { continue }
                var entry: [String: Any] = ["used_percent": used, "duration_minutes": duration]
                if let resets = window["resetsAt"] as? NSNumber { entry["resets_at"] = resets }
                windows.append(entry)
            }
            guard !windows.isEmpty else {
                end(error: "当前账号未返回订阅额度，请检查 Codex 登录账号"); return
            }
            var clean: [String: Any] = ["windows": windows, "updated_at": Date().timeIntervalSince1970]
            if let credits = raw["rateLimitResetCredits"] as? [String: Any], let count = credits["availableCount"] as? NSNumber { clean["reset_count"] = count }
            end(clean)
        }
    }
}

// MARK: - Process entry point
signal(SIGPIPE, SIG_IGN)

// Fixed quota protocol; token renewal is delegated to Codex. No model turns or reset redemption.
let arguments = CommandLine.arguments
func argument(_ name: String) -> String? { guard let i = arguments.firstIndex(of: name), i + 1 < arguments.count else { return nil }; return arguments[i+1] }
guard let output = argument("--output"), let parentText = argument("--parent-pid"), let parent = Int32(parentText), parent > 1, getppid() == parent else { exit(2) }
let destination = URL(fileURLWithPath: output)
let fm = FileManager.default
let home = fm.homeDirectoryForCurrentUser
let locations = QuotaExecutable.candidates(home: home)
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
var failureMessage: String?
let request = QuotaRequest()
request.send = { send($0) }
request.finish = { value, error in
    result = value; failureMessage = error; completed = true; semaphore.signal()
}
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
        guard let value = try? JSONSerialization.jsonObject(with: line) as? [String: Any] else { continue }
        request.receive(value)
        if completed { return }
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
    send(["id": 1, "method": "initialize", "params": ["clientInfo": ["name": "codex_usage_readonly", "version": "1.0.0"], "capabilities": ["experimentalApi": true, "requestAttestation": false]]])
    if semaphore.wait(timeout: .now() + 35) == .timedOut {
        queue.sync { completed = true; failureMessage = "额度查询超时，请检查网络后重试" }
    }
} catch { failureMessage = "Codex 未能启动，请检查安装或更新 Codex" }
outputPipe.fileHandleForReading.readabilityHandler = nil
parentWatch.cancel(); termination.cancel(); interrupt.cancel()
try? input.fileHandleForWriting.close()
if child.isRunning {
    child.terminate()
    for _ in 0..<40 { if !child.isRunning { break }; Thread.sleep(forTimeInterval: 0.05) }
    if child.isRunning { kill(child.processIdentifier, SIGKILL) }
    child.waitUntilExit()
}
var final: [String: Any]?, finalError: String?
queue.sync { completed = true; final = result; finalError = failureMessage }
if getppid() != parent { exit(1) }
if let final = final { publish(final); exit(0) }
failure(finalError ?? "Codex 额度连接已中断，请重试或更新 Codex")
exit(1)
