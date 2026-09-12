import AppKit

// Synthetic, offscreen drawing only. No installed app, logs or account data are read.
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
let output = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
precondition(CapsuleSurface.small == NSSize(width: 76, height: 76), "The preview fixture expects a 76x76 collapsed circle")
precondition(CapsuleSurface.large == NSSize(width: 336, height: 410), "The expanded layout must include automatic refresh controls")

var rendered: [[String: Any]] = []
func capture(_ view: CapsuleSurface, name: String, theme: CapsuleTheme, quota: Double?, expanded: Bool) throws {
    view.needsDisplay = true
    guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { fatalError("Cannot allocate offscreen bitmap") }
    view.cacheDisplay(in: view.bounds, to: bitmap)
    guard let bytes = bitmap.representation(using: .png, properties: [:]) else { fatalError("Cannot encode PNG") }
    let path = output.appendingPathComponent(name + ".png")
    try bytes.write(to: path)
    rendered.append([
        "file": path.path, "theme": theme.rawValue,
        "quota_fraction": quota.map { $0 as Any } ?? NSNull(),
        "expanded": expanded, "expansion": view.expansion, "native_glass": false,
        "scope": view.state.scope, "range_days": view.state.rangeDays, "scope_title": view.state.scopeTitle,
        "width_points": view.bounds.width, "height_points": view.bounds.height,
        "width_pixels": bitmap.pixelsWide, "height_pixels": bitmap.pixelsHigh
    ])
}

for theme in CapsuleTheme.allCases {
    for fraction: Double? in [0, 0.15, 0.5, 1, nil] {
        try autoreleasepool {
            let state = CapsuleState()
            state.theme = theme
            state.total = "338.51M"; state.exact = "338,512,123 tokens"
            state.input = "336.12M"; state.output = "2.39M"
            state.selectedModel = "gpt-6-astra"
            state.selectedTask = "task-example"; state.selectedTaskLabel = "示例任务"
            state.context = "今天 · 示例模型 / 示例任务"
            if fraction == 0.15 {
                state.scope = 1; state.rangeDays = "7"
                state.context = "最近 7 天 · 示例模型 / 示例任务"
            } else if fraction == 0.5 {
                state.scope = 1; state.rangeDays = "all"
                state.context = "全部时间 · 示例模型 / 示例任务"
            }
            state.cache = "缓存输入 318.12M · 2,345 次调用"
            state.quotaName = fraction == nil ? "剩余额度" : "周剩余"; state.quotaFraction = fraction
            let value = fraction.map { String(Int(($0 * 100).rounded())) }
            state.quotaCompact = value.map { "周余 \($0)%" } ?? "额度 —"
            state.quotaDetail = value.map { "周余 \($0)% / 5h余 80%" } ?? "额度暂不可用"
            state.refreshSeconds = 30
            state.status = "12:34:56 已更新 · 最多每 30 秒更新"
            let view = CapsuleSurface(state: state)
            let window = NSPanel(contentRect: view.frame, styleMask: .borderless, backing: .buffered, defer: true)
            window.isReleasedWhenClosed = false
            window.contentView = view
            let material = theme == .light ? "light" : "dark"
            try capture(view, name: "compact-\(material)-\(value ?? "unknown")", theme: theme, quota: fraction, expanded: false)
            if fraction == 0.15 {
                for progress: CGFloat in [0.25, 0.5, 0.75] {
                    let size = NSSize(width: CapsuleSurface.small.width + (CapsuleSurface.large.width - CapsuleSurface.small.width) * progress,
                                      height: CapsuleSurface.small.height + (CapsuleSurface.large.height - CapsuleSurface.small.height) * progress)
                    window.setContentSize(size); view.expansion = progress
                    try capture(view, name: "transition-\(material)-\(Int(progress * 100))", theme: theme, quota: fraction, expanded: false)
                }
                window.setContentSize(CapsuleSurface.large)
                view.expansion = 1
                try capture(view, name: "expanded-\(material)", theme: theme, quota: fraction, expanded: true)
            }
            window.contentView = nil
        }
    }
    for (name, total, fraction, quotaName, stale) in [
        ("decimal", "0.01K", 0.5, "周剩余", false),
        ("stale-long-label", "999.99M", 1.0, "每 5 小时剩余", true),
        ("long-token", "99999.99B", 1.0, "每 5 小时剩余", true),
        ("zero-token", "0", 0.0, "周剩余", false)
    ] {
        try autoreleasepool {
            let state = CapsuleState(); state.theme = theme
            state.total = total; state.quotaFraction = fraction; state.quotaName = quotaName
            state.quotaStale = stale; state.scope = 1
            state.rangeDays = name == "long-token" ? "all" : "7"
            let view = CapsuleSurface(state: state)
            let window = NSPanel(contentRect: view.frame, styleMask: .borderless, backing: .buffered, defer: true)
            window.isReleasedWhenClosed = false; window.contentView = view
            let material = theme == .light ? "light" : "dark"
            try capture(view, name: "compact-\(material)-\(name)", theme: theme, quota: fraction, expanded: false)
            window.contentView = nil
        }
    }
}

final class ContactSheet: NSView {
    let rows: [[String: Any]]
    override var isFlipped: Bool { true }
    init(rows: [[String: Any]]) {
        self.rows = rows
        super.init(frame: NSRect(x: 0, y: 0, width: 520, height: CGFloat(rows.count) * 96 + 22))
    }
    required init?(coder: NSCoder) { fatalError() }
    override func draw(_ dirtyRect: NSRect) {
        NSColor(calibratedWhite: 0.12, alpha: 1).setFill(); bounds.fill()
        for (index, row) in rows.enumerated() {
            let path = row["file"] as! String
            let title = URL(fileURLWithPath: path).deletingPathExtension().lastPathComponent
            let y = 12 + CGFloat(index) * 96
            (title as NSString).draw(at: NSPoint(x: 14, y: y + 29), withAttributes: [.font: NSFont.systemFont(ofSize: 10), .foregroundColor: NSColor.lightGray])
            NSImage(contentsOfFile: path)?.draw(in: NSRect(x: 350, y: y, width: 76, height: 76), from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
        }
    }
}
let compactRows = rendered.filter { URL(fileURLWithPath: $0["file"] as! String).lastPathComponent.hasPrefix("compact-") }
let sheet = ContactSheet(rows: compactRows)
let sheetWindow = NSPanel(contentRect: sheet.frame, styleMask: .borderless, backing: .buffered, defer: true)
sheetWindow.isReleasedWhenClosed = false; sheetWindow.contentView = sheet
let sheetBitmap = sheet.bitmapImageRepForCachingDisplay(in: sheet.bounds)!
sheet.cacheDisplay(in: sheet.bounds, to: sheetBitmap)
try sheetBitmap.representation(using: .png, properties: [:])!.write(to: output.appendingPathComponent("compact-contact-sheet.png"))
sheetWindow.contentView = nil
let manifest: [String: Any] = [
    "synthetic_data_only": true,
    "glass_note": "Both palettes use CapsuleSurface directly; no system glass material is used.",
    "images": rendered
]
try JSONSerialization.data(withJSONObject: manifest, options: [.prettyPrinted, .sortedKeys]).write(to: output.appendingPathComponent("render-manifest.json"))
print("Rendered \(rendered.count) offscreen images; both palettes use the same plain surface.")
