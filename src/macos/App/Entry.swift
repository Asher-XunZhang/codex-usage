import AppKit

// Exercise the identical offline bootstrap without opening a window.
if CommandLine.arguments.contains("--check-runtime") {
    guard ProcessInfo.processInfo.environment["CODEX_USAGE_DESKTOP_BASE"] != nil else {
        fputs("CODEX_USAGE_DESKTOP_BASE is required for runtime validation\n", stderr); exit(2)
    }
    do {
        // Exercise bundle-specific shared preference initialization as well as
        // resource paths; command-line unit fixtures have a different identity.
        _ = usagePreferences.object(forKey: "displayMode")
        let python = try Backend().prepareRuntime()
        let check = Process(); check.executableURL = python
        check.arguments = ["-E", "-s", "-B", "-c", "import ssl, sqlite3, zlib, platform, json; print(json.dumps({'python':platform.python_version(),'arch':platform.machine(),'ssl':ssl.OPENSSL_VERSION,'sqlite':sqlite3.sqlite_version}))"]
        try check.run(); check.waitUntilExit(); exit(check.terminationStatus)
    } catch { fputs("\(error.localizedDescription)\n", stderr); exit(1) }
}

let app = NSApplication.shared
// The stable host and on-demand main panel each have one instance.
let bundleIdentifier = Bundle.main.bundleIdentifier ?? "local.codex-usage.desktop"
if let other = NSRunningApplication.runningApplications(withBundleIdentifier: bundleIdentifier)
    .first(where: { $0.processIdentifier != getpid() && !$0.isTerminated && kill($0.processIdentifier, 0) == 0 }) {
    other.activate(options: [.activateIgnoringOtherApps]); exit(0)
}
// Only the main-panel helper owns a Dock entry and dashboard resources.
// The menu/float host keeps its PID and status-item registration throughout.
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(isMainWindowProcess ? .regular : .accessory)
withExtendedLifetime(delegate) { app.run() }
