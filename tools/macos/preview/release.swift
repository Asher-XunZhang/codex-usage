import AppKit
let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
let output = URL(fileURLWithPath: CommandLine.arguments[1])
final class Board: NSView {
    override var isFlipped: Bool { true }
    override func draw(_ rect: NSRect) { NSColor.white.setFill(); bounds.fill() }
}
let board = Board(frame: NSRect(x: 0, y: 0, width: 720, height: 720))
let window = NSPanel(contentRect: board.frame, styleMask: .borderless, backing: .buffered, defer: false)
window.isReleasedWhenClosed = false; window.contentView = board
var surfaces: [CapsuleSurface] = []
for (row, theme) in CapsuleTheme.allCases.enumerated() {
    for (column, edge) in CapsuleDockEdge.allCases.enumerated() {
        for (mode, spec) in [("none", ("none", 0)), ("running", ("running", 0)), ("unread", ("completed", 100))].enumerated() {
            let state = CapsuleState(); state.theme = theme; state.quotaFraction = mode == 2 ? 1 : 0.78
            state.quotaStale = mode == 2
            state.monitorStatus = spec.1.0; state.monitorUnread = spec.1.1
            let size = CapsulePlacement.tab(compact: NSRect(x: 200, y: 200, width: 76, height: 76), edge: edge, work: NSRect(x: 0, y: 0, width: 1000, height: 1000), showsMonitor: state.showsDockedMonitor).size
            let label = NSTextField(labelWithString: "\(theme == .light ? "浅色" : "深色") · \(["left":"左", "right":"右", "top":"上", "bottom":"下"][edge.rawValue]!) · \(["none":"无任务", "running":"执行中", "unread":"未读"][spec.0]!)")
            label.font = .systemFont(ofSize: 10); label.textColor = .darkGray
            let x = CGFloat(column) * 180 + 10, y = CGFloat(row) * 360 + CGFloat(mode) * 120
            label.frame = NSRect(x: x, y: y + 4, width: 170, height: 14); board.addSubview(label)
            let surface = CapsuleSurface(state: state, managesWindowShadow: false)
            surface.frame = NSRect(x: x + (160 - size.width) / 2, y: y + 20, width: size.width, height: size.height)
            surface.dockEdge = edge.rawValue; surface.docking = 1; board.addSubview(surface); surfaces.append(surface)
        }
    }
}
let bitmap = board.bitmapImageRepForCachingDisplay(in: board.bounds)!
board.cacheDisplay(in: board.bounds, to: bitmap)
try! bitmap.representation(using: .png, properties: [:])!.write(to: output.appendingPathComponent("v1.0.3-docked-states.png"))
print("native docked board rendered")

func capture(_ view: NSView, name: String) {
    let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds)!
    view.cacheDisplay(in: view.bounds, to: bitmap)
    try! bitmap.representation(using: .png, properties: [:])!.write(to: output.appendingPathComponent(name + ".png"))
}
for theme in CapsuleTheme.allCases {
    let editor = ArcColorEditor(style: CapsuleArcStyle(), theme: theme, fraction: 0.78)
    editor.window!.appearance!.performAsCurrentDrawingAppearance {
        capture(editor.window!.contentView!, name: "v1.0.3-arc-editor-" + theme.rawValue)
    }
    editor.close()
}
let steps = Board(frame: NSRect(x: 0, y: 0, width: 820, height: 490))
let stepsWindow = NSPanel(contentRect: steps.frame, styleMask: .borderless, backing: .buffered, defer: false)
stepsWindow.isReleasedWhenClosed = false; stepsWindow.contentView = steps
func label(_ value: String, _ rect: NSRect, _ size: CGFloat = 13) {
    let label = NSTextField(labelWithString: value); label.font = .systemFont(ofSize: size)
    label.textColor = .darkGray; label.frame = rect; steps.addSubview(label)
}
label("v1.0.3 · 原生视图与交互状态（演示数据）", NSRect(x: 20, y: 12, width: 760, height: 28), 20)
label("移出后贴边收起", NSRect(x: 20, y: 55, width: 200, height: 20))
for (i, progress) in ([0, 0.25, 0.5, 0.75, 1] as [CGFloat]).enumerated() {
    let state = CapsuleState(); state.theme = .light; state.quotaFraction = 0.78; state.total = "12.34M"
    let view = CapsuleSurface(state: state, managesWindowShadow: false)
    let width: CGFloat = 76 + 12 * progress, height: CGFloat = 76 - 48 * progress
    view.frame = NSRect(x: 20 + CGFloat(i) * 91, y: 88 + 76 - height, width: width, height: height)
    view.dockEdge = "bottom"; view.docking = progress; steps.addSubview(view)
    label("\(Int(progress * 100))%", NSRect(x: view.frame.minX + 18, y: 177, width: 55, height: 20), 11)
}
label("点击侧签展开；拖动恢复圆环", NSRect(x: 20, y: 230, width: 440, height: 22))
label("任务状态决定宽度，内容整体居中", NSRect(x: 20, y: 275, width: 440, height: 22))
for (i, status) in ["none", "running", "completed"].enumerated() {
    let state = CapsuleState(); state.theme = .light; state.quotaFraction = 0.78; state.total = "12.34M"
    state.monitorStatus = status; state.monitorUnread = status == "completed" ? 2 : 0
    let view = CapsuleSurface(state: state, managesWindowShadow: false)
    view.frame = NSRect(x: 20 + CGFloat(i) * 150, y: 315, width: state.showsDockedMonitor ? 124 : 88, height: 28)
    view.dockEdge = "bottom"; view.docking = 1; steps.addSubview(view)
    label(["无任务", "执行中", "2 条未读"][i], NSRect(x: view.frame.minX, y: 360, width: 130, height: 20))
}
label("浅色 / 深色 · 四边适配 · 减少动态效果", NSRect(x: 20, y: 420, width: 440, height: 22), 12)
let state = CapsuleState(); state.theme = .light; state.quotaFraction = 0.78; state.total = "12.34M"
state.total = "12.34M"; state.exact = "12,340,000 tokens"; state.input = "11.20M"; state.output = "1.14M"
state.cache = "缓存输入 8.40M · 120 次调用"; state.status = "演示数据 · 每 60 秒刷新"
state.refreshSeconds = 60; state.quotaDetail = "周剩余 78%"
let detail = CapsuleSurface(state: state, managesWindowShadow: false)
detail.frame = NSRect(x: 480, y: 55, width: 336, height: 410); detail.expansion = 1
steps.addSubview(detail)
capture(steps, name: "v1.0.3-interactions")
print("Synthetic native AppKit views rendered; no installed app or user data accessed")
