import AppKit
import Foundation

// Keep only the displayed page attached: hidden stack constraints must not
// enlarge the other page or compete with the persistent navigation row.
final class MainPageContainer: NSView {
    override var wantsDefaultClipping: Bool { true }
    private(set) var page: NSView?
    private var pageConstraints: [NSLayoutConstraint] = []
    func show(_ view: NSView) {
        guard page !== view else { return }
        NSLayoutConstraint.deactivate(pageConstraints)
        page?.removeFromSuperview()
        page = view
        view.isHidden = false
        view.translatesAutoresizingMaskIntoConstraints = false
        addSubview(view)
        pageConstraints = [view.topAnchor.constraint(equalTo: topAnchor), view.bottomAnchor.constraint(equalTo: bottomAnchor),
                           view.leadingAnchor.constraint(equalTo: leadingAnchor), view.trailingAnchor.constraint(equalTo: trailingAnchor)]
        NSLayoutConstraint.activate(pageConstraints)
    }
}

// The main-window client owns only its editor draft. Monitoring and writes belong to the host.
private typealias BudgetObject = [String: Any]
private func budgetNumber(_ value: Any?) -> Double? {
    guard let n = value as? NSNumber, n.doubleValue.isFinite else { return nil }
    return n.doubleValue
}
private func budgetText(_ text: String, _ size: CGFloat = 13, _ weight: NSFont.Weight = .regular,
                        color: NSColor = .labelColor) -> NSTextField {
    let field = NSTextField(wrappingLabelWithString: text)
    field.font = .systemFont(ofSize: size, weight: weight); field.textColor = color
    field.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
    return field
}
private func budgetStack(_ views: [NSView] = [], horizontal: Bool = false, spacing: CGFloat = 12) -> NSStackView {
    let view = NSStackView(views: views)
    view.orientation = horizontal ? .horizontal : .vertical
    view.alignment = horizontal ? .centerY : .leading; view.spacing = spacing
    return view
}
private func budgetFormat(_ value: Double?, kind: String, currency: String = "USD") -> String {
    guard let value = value else { return "—" }
    if kind == "quota" { return String(format: "%.0f%%", value) }
    if kind == "money" { return String(format: "≈ %@ %.2f", currency, value) }
    if abs(value) >= 1_000_000 { return String(format: "%.2fM", value / 1_000_000) }
    if abs(value) >= 1_000 { return String(format: "%.1fK", value / 1_000) }
    return String(format: "%.0f", value)
}
private class BudgetActionButton: FeedbackButton {
    var callback: (() -> Void)?
    init(_ title: String, callback: @escaping () -> Void) {
        super.init(frame: .zero); self.title = title; self.callback = callback
        bezelStyle = .rounded; target = self; action = #selector(performCallback)
        font = .systemFont(ofSize: 12)
    }
    @objc private func performCallback() { callback?() }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
private final class BudgetRuleButton: BudgetActionButton {
    override var isFlipped: Bool { false }
    private let nameText: String
    private let subtitle: String
    private let remaining: Double?
    private let selectedRule: Bool
    private let statusText: String
    init(name: String, subtitle: String, remaining: Double?, selected: Bool, status: String, callback: @escaping () -> Void) {
        nameText = name; self.subtitle = subtitle; self.remaining = remaining; selectedRule = selected; statusText = status
        super.init(name, callback: callback); isBordered = false
        setAccessibilityLabel("\(name)，\(budgetFormat(remaining, kind: "quota")) 剩余，\(status)")
    }
    override func draw(_ dirtyRect: NSRect) {
        let shape = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 10, yRadius: 10)
        NSColor.controlBackgroundColor.setFill(); shape.fill()
        if selectedRule { NSColor.systemGreen.withAlphaComponent(0.08).setFill(); shape.fill() }
        (selectedRule ? NSColor.systemGreen.withAlphaComponent(0.7) : NSColor.separatorColor.withAlphaComponent(0.4)).setStroke()
        shape.lineWidth = 1; shape.stroke()
        func drawText(_ text: String, x: CGFloat, y: CGFloat, width: CGFloat, size: CGFloat, weight: NSFont.Weight, color: NSColor) {
            let paragraph = NSMutableParagraphStyle(); paragraph.lineBreakMode = .byTruncatingTail
            (text as NSString).draw(in: NSRect(x: x, y: y, width: width, height: size + 8), withAttributes: [.font: NSFont.systemFont(ofSize: size, weight: weight), .foregroundColor: color, .paragraphStyle: paragraph])
        }
        drawText(nameText, x: 14, y: bounds.height - 30, width: bounds.width - 28, size: 13, weight: .medium, color: .labelColor)
        drawText(subtitle, x: 14, y: bounds.height - 50, width: bounds.width - 28, size: 10, weight: .regular, color: .secondaryLabelColor)
        drawText(budgetFormat(remaining, kind: "quota"), x: 14, y: 22, width: 100, size: 22, weight: .medium, color: .labelColor)
        drawText(statusText, x: 112, y: 28, width: bounds.width - 126, size: 10, weight: .regular, color: .secondaryLabelColor)
        let track = NSRect(x: 14, y: 12, width: max(0, bounds.width - 28), height: 4)
        NSColor.separatorColor.withAlphaComponent(0.5).setFill(); NSBezierPath(roundedRect: track, xRadius: 2, yRadius: 2).fill()
        if let remaining = remaining {
            let color = remaining <= 10 ? NSColor.systemRed : remaining <= 25 ? NSColor.systemOrange : NSColor.systemGreen
            color.setFill(); NSBezierPath(roundedRect: NSRect(x: track.minX, y: track.minY, width: track.width * max(0, min(1, remaining / 100)), height: 4), xRadius: 2, yRadius: 2).fill()
        }
        drawInteractionFeedback(in: bounds.insetBy(dx: 0.5, dy: 0.5), radius: 10)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
private final class BudgetMeter: NSView {
    let fraction: Double?
    override var intrinsicContentSize: NSSize { NSSize(width: NSView.noIntrinsicMetric, height: 6) }
    init(_ fraction: Double?) {
        self.fraction = fraction; super.init(frame: .zero)
        setAccessibilityElement(true); setAccessibilityRole(.progressIndicator)
        setAccessibilityLabel(fraction == nil ? "剩余未知" : "预算剩余比例")
        setAccessibilityValue(fraction.map { String(format: "%.0f%%", $0 * 100) } ?? "未知")
    }
    override func draw(_ dirtyRect: NSRect) {
        NSColor.separatorColor.withAlphaComponent(0.35).setFill()
        NSBezierPath(roundedRect: bounds, xRadius: 3, yRadius: 3).fill()
        if let fraction = fraction {
            (fraction <= 0.1 ? NSColor.systemRed : fraction <= 0.25 ? NSColor.systemOrange : NSColor.systemGreen).setFill()
            NSBezierPath(roundedRect: NSRect(x: bounds.minX, y: bounds.minY, width: bounds.width * max(0, min(1, fraction)), height: bounds.height), xRadius: 3, yRadius: 3).fill()
        }
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
private final class BudgetDocumentView: NSView {
    override var isFlipped: Bool { true }
    override func draw(_ dirtyRect: NSRect) { NSColor.windowBackgroundColor.setFill(); dirtyRect.fill() }
}
private final class BudgetCard: NSView {
    override func draw(_ dirtyRect: NSRect) {
        NSColor.controlBackgroundColor.setFill()
        NSBezierPath(roundedRect: bounds, xRadius: 12, yRadius: 12).fill()
        NSColor.separatorColor.withAlphaComponent(0.5).setStroke()
        let border = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 12, yRadius: 12)
        border.lineWidth = 1; border.stroke()
    }
    init(content: NSView) {
        super.init(frame: .zero); content.translatesAutoresizingMaskIntoConstraints = false; addSubview(content)
        NSLayoutConstraint.activate([
            content.topAnchor.constraint(equalTo: topAnchor, constant: 20),
            content.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 20),
            content.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -20),
            content.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -20)
        ])
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}

final class BudgetPage: NSView {
    override var wantsDefaultClipping: Bool { true }
    override func draw(_ dirtyRect: NSRect) { NSColor.windowBackgroundColor.setFill(); bounds.intersection(dirtyRect).fill() }
    private let actionHandler: (String, [String: Any]) -> Void
    private var state: BudgetObject = [:]
    private var rules: [BudgetObject] { state["rules"] as? [BudgetObject] ?? [] }
    private var summaries: [BudgetObject] { state["summaries"] as? [BudgetObject] ?? [] }
    private(set) var selectedBudgetID: String?
    private(set) var editing = false
    private var editingRule: BudgetObject = [:]
    private var expectedRevision = 0
    private var pendingSave = false
    private var pendingPin = false
    private var controls: [String: NSControl] = [:]
    private var sections: [String: NSView] = [:]
    private var priceFields: [[String: NSTextField]] = []
    private var priceStack: NSStackView?
    private let rootStack = budgetStack(spacing: 16)
    private let message = budgetText("", 12, color: .secondaryLabelColor)
    private var content: NSView?
    private var errorText: NSTextField?
    private var saveButton: NSButton?
    private var deleteArmedID: String?
    private var deferredNavigation: (String?, Bool)?
    private var trackingMenus = Set<ObjectIdentifier>()
    private var deferredChoices = false

    init(action: @escaping (String, [String: Any]) -> Void) {
        self.actionHandler = action
        super.init(frame: .zero)
        rootStack.translatesAutoresizingMaskIntoConstraints = false; addSubview(rootStack)
        NSLayoutConstraint.activate([
            rootStack.leadingAnchor.constraint(equalTo: leadingAnchor), rootStack.trailingAnchor.constraint(equalTo: trailingAnchor),
            rootStack.topAnchor.constraint(equalTo: topAnchor), rootStack.bottomAnchor.constraint(equalTo: bottomAnchor)
        ])
        showList()
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func update(_ state: [String: Any]) {
        self.state = state
        if editing {
            // Only choice menus may be refreshed during typing; draft fields remain untouched.
            refreshChoices()
            // A query/notification error does not mean that a rule write failed.
            // Only the save reply may reject this draft; a committed revision can confirm it.
            if pendingSave, let saved = rules.first(where: { ($0["id"] as? String) == (editingRule["id"] as? String) }),
                      (budgetNumber(saved["revision"]) ?? 0) > Double(expectedRevision) {
                acknowledgeSave(id: saved["id"] as? String ?? "", revision: Int(budgetNumber(saved["revision"]) ?? 0), error: nil)
            }
            return
        }
        if selectedBudgetID == nil { selectedBudgetID = rules.first?["id"] as? String }
        showList()
    }

    func navigate(budgetID: String?, create: Bool = false) {
        if editing {
            deferredNavigation = (budgetID, create)
            message.stringValue = "已保留当前编辑。完成或取消后将打开新请求。"
            return
        }
        if create { beginEditing(nil); return }
        if let id = budgetID { selectedBudgetID = id }
        else if selectedBudgetID == nil { selectedBudgetID = rules.first?["id"] as? String }
        showList(); actionHandler("viewing", ["id": selectedBudgetID ?? "", "editing": false])
    }

    func acknowledgeSave(id: String, revision: Int?, error: String?) {
        guard editing, pendingSave, id == editingRule["id"] as? String else { return }
        if let error = error, !error.isEmpty {
            pendingSave = false; setEditorEnabled(true); errorText?.stringValue = error; return
        }
        guard let revision = revision, revision > expectedRevision else { return }
        selectedBudgetID = id
        if pendingPin { actionHandler("pin", ["id": id]) }
        finishEditing(message: "已保存，将持续监测。")
    }

    /// Store raw text as well as the base rule, so even an unfinished/invalid input survives main-process exit.
    func snapshotDraft() -> [String: Any]? {
        guard editing else { return nil }
        var values: BudgetObject = [:]
        for (key, control) in controls {
            if let popup = control as? NSPopUpButton { values[key] = popup.selectedItem?.representedObject as? String ?? "" }
            else if let date = control as? NSDatePicker { values[key] = date.dateValue.timeIntervalSince1970 }
            else if let button = control as? NSButton { values[key] = button.state == .on }
            else if let field = control as? NSTextField { values[key] = field.stringValue }
        }
        let prices = priceFields.map { row in row.mapValues { $0.stringValue } }
        return ["version": 1, "rule": editingRule, "expectedRevision": expectedRevision, "values": values, "prices": prices]
    }

    func restoreDraft(_ draft: [String: Any]) {
        guard !editing, let rule = draft["rule"] as? BudgetObject, let values = draft["values"] as? BudgetObject else { return }
        beginEditing(rule)
        expectedRevision = Int(budgetNumber(draft["expectedRevision"]) ?? budgetNumber(rule["revision"]) ?? 0)
        for (key, value) in values {
            if let popup = controls[key] as? NSPopUpButton, let value = value as? String { select(popup, value: value, preserveUnknown: true) }
            else if let date = controls[key] as? NSDatePicker, let value = budgetNumber(value) { date.dateValue = Date(timeIntervalSince1970: value) }
            else if let button = controls[key] as? NSButton, let value = value as? Bool { button.state = value ? .on : .off }
            else if let field = controls[key] as? NSTextField, let value = value as? String { field.stringValue = value }
        }
        if let rows = draft["prices"] as? [[String: String]] { replacePriceRows(rows) }
        refreshChoices(); updateEditorVisibility(); message.stringValue = "已恢复未保存草稿，保存后才会影响监测。"; draftChanged()
    }

    private func replaceContent(_ view: NSView, heading: String, subtitle: String, buttons: [NSView]) {
        rootStack.arrangedSubviews.forEach { rootStack.removeArrangedSubview($0); $0.removeFromSuperview() }
        let titleStack = budgetStack([budgetText(heading, 23, .medium), budgetText(subtitle, 12, color: .secondaryLabelColor)], spacing: 6)
        let spacer = NSView()
        let header = budgetStack([titleStack, spacer] + buttons, horizontal: true)
        rootStack.addArrangedSubview(header); rootStack.addArrangedSubview(message); rootStack.addArrangedSubview(view)
        for child in [header, message, view] { child.widthAnchor.constraint(equalTo: rootStack.widthAnchor).isActive = true }
        view.setContentHuggingPriority(.defaultLow, for: .vertical)
        content = view
    }
    private func scroll(_ body: NSView) -> NSScrollView {
        let result = NSScrollView(); result.drawsBackground = false; result.hasVerticalScroller = true
        result.autohidesScrollers = true; result.borderType = .noBorder
        let document = BudgetDocumentView(); document.translatesAutoresizingMaskIntoConstraints = false
        body.translatesAutoresizingMaskIntoConstraints = false; document.addSubview(body); result.documentView = document
        NSLayoutConstraint.activate([
            document.widthAnchor.constraint(equalTo: result.contentView.widthAnchor),
            body.topAnchor.constraint(equalTo: document.topAnchor, constant: 4), body.leadingAnchor.constraint(equalTo: document.leadingAnchor, constant: 4),
            body.trailingAnchor.constraint(equalTo: document.trailingAnchor, constant: -4), body.bottomAnchor.constraint(equalTo: document.bottomAnchor, constant: -12)
        ])
        return result
    }
    private func showList() {
        controls.removeAll(); sections.removeAll(); priceFields.removeAll(); priceStack = nil; errorText = nil; saveButton = nil
        let list = budgetStack(spacing: 10)
        list.addArrangedSubview(budgetText("我的预算", 11, .medium, color: .secondaryLabelColor))
        for rule in rules {
            let id = rule["id"] as? String ?? ""
            let summary = summaries.first { ($0["id"] as? String) == id } ?? [:]
            let kind = summary["kind"] as? String ?? rule["kind"] as? String ?? "token"
            let name = rule["name"] as? String ?? "未命名预算"
            let enabled = rule["enabled"] as? Bool ?? true
            let percent = budgetNumber(summary["remainingPercent"])
            let selected = id == selectedBudgetID
            var effectiveRule = rule
            if let period = summary["period"] { effectiveRule["period"] = period }
            let metric = summary["tokenMetric"] as? String ?? rule["tokenMetric"] as? String ?? "total"
            let button = BudgetRuleButton(name: name, subtitle: "\(kind == "token" ? metricName(metric) : kindName(kind)) · \(periodName(effectiveRule))", remaining: percent, selected: selected,
                status: !enabled ? "已停用" : summary["paused"] as? Bool == true ? "提醒暂停" : kind == "quota" ? "官方余量" : "预算剩余") { [weak self] in
                self?.selectedBudgetID = id; self?.deleteArmedID = nil; self?.showList(); self?.actionHandler("viewing", ["id": id, "editing": false])
            }
            button.heightAnchor.constraint(equalToConstant: 116).isActive = true
            list.addArrangedSubview(button); button.widthAnchor.constraint(equalTo: list.widthAnchor).isActive = true
        }
        list.addArrangedSubview(budgetText("每项预算独立统计。\n切换用量筛选不会修改预算。", 11, color: .secondaryLabelColor))
        let detail = makeDetail()
        let columns = budgetStack([scroll(list), scroll(detail)], horizontal: true, spacing: 20)
        columns.alignment = .top
        columns.arrangedSubviews[0].widthAnchor.constraint(equalToConstant: 262).isActive = true
        columns.arrangedSubviews[1].widthAnchor.constraint(equalTo: columns.widthAnchor, constant: -282).isActive = true
        for view in columns.arrangedSubviews { view.heightAnchor.constraint(equalTo: columns.heightAnchor).isActive = true }
        let newButton = BudgetActionButton("＋ 新建预算") { [weak self] in self?.beginEditing(nil) }
        let refresh = BudgetActionButton("刷新") { [weak self] in self?.actionHandler("refresh", [:]) }
        replaceContent(columns, heading: "预算与提醒", subtitle: "\(rules.filter { $0["enabled"] as? Bool ?? true }.count) 项已启用 · 仅视觉提醒", buttons: [refresh, newButton])
        if let error = state["error"] as? String, !error.isEmpty { message.stringValue = error }
    }
    private func makeDetail() -> NSView {
        guard let rule = rules.first(where: { ($0["id"] as? String) == selectedBudgetID }) else {
            return BudgetCard(content: budgetStack([
                budgetText(selectedBudgetID == nil ? "为你的使用节奏设置预算" : "此预算已删除或暂不可用", 18, .medium),
                budgetText("自定义时间、模型和任务范围。接近额度时通过无声视觉提醒呈现，不会中断任务。", color: .secondaryLabelColor),
                BudgetActionButton("创建预算…") { [weak self] in self?.beginEditing(nil) }
            ]))
        }
        let id = rule["id"] as? String ?? ""
        let summary = summaries.first { ($0["id"] as? String) == id } ?? [:]
        var current = rule
        for key in ["kind", "currency", "period", "model", "task", "tokenMetric", "windowMinutes"] { if let value = summary[key] { current[key] = value } }
        let kind = current["kind"] as? String ?? "token"
        let currency = current["currency"] as? String ?? "USD"
        let body = budgetStack(spacing: 18)
        let edit = BudgetActionButton("编辑") { [weak self] in self?.beginEditing(rule) }
        let header = budgetStack([budgetText(rule["name"] as? String ?? "预算", 18, .medium), NSView(), edit], horizontal: true)
        body.addArrangedSubview(header); header.widthAnchor.constraint(equalTo: body.widthAnchor).isActive = true
        body.addArrangedSubview(budgetText(kind == "token" ? metricName(current["tokenMetric"] as? String ?? "total") : kindName(kind), 12, color: .secondaryLabelColor))
        if summary["scheduledChange"] as? Bool == true { body.addArrangedSubview(budgetText("修改已保存，将于下期生效；以下仍为本期有效范围。", 12, color: .secondaryLabelColor)) }
        let status = summary["status"] as? String ?? "unknown"
        let messageText = summary["message"] as? String ?? summary["reason"] as? String ?? "等待预算数据更新"
        if !messageText.isEmpty { body.addArrangedSubview(budgetText(messageText, 12, color: ["exhausted", "exceeded"].contains(status) ? .systemRed : .secondaryLabelColor)) }
        let overage = budgetNumber(summary["overage"]) ?? 0
        let lowerBound = summary["coverage"] as? String == "partial" && ["exhausted", "exceeded"].contains(status)
        body.addArrangedSubview(budgetText(kind == "quota" ? "官方剩余额度" : lowerBound ? (overage > 0 ? (kind == "money" ? "已知至少超出 · 估算" : "已知至少超出") : "已知用量已达到预算") : overage > 0 ? "已超出预算" : "剩余预算", 12, color: .secondaryLabelColor))
        let known = !["unknown", "partial", "source_invalid", "scope_invalid"].contains(status)
        let balance = known ? (overage > 0 && kind != "quota" ? overage : budgetNumber(summary["remaining"])) : nil
        let formattedBalance = budgetFormat(balance, kind: kind, currency: currency)
        let value = budgetText(lowerBound && overage > 0 ? "≥ " + formattedBalance.replacingOccurrences(of: "≈ ", with: "") : formattedBalance, 38, .medium)
        value.font = .monospacedDigitSystemFont(ofSize: 38, weight: .medium); body.addArrangedSubview(value)
        let meter = BudgetMeter(known ? budgetNumber(summary["remainingFraction"]) : nil)
        body.addArrangedSubview(meter); meter.widthAnchor.constraint(equalTo: body.widthAnchor).isActive = true
        let formattedUsed = budgetFormat(budgetNumber(summary["used"]), kind: kind, currency: currency)
        let used = lowerBound ? formattedUsed.replacingOccurrences(of: "≈ ", with: "") : formattedUsed
        let limit = budgetFormat(budgetNumber(rule["amount"]), kind: kind, currency: currency)
        body.addArrangedSubview(budgetText(kind == "quota" ? "官方余量 ≤ \(limit) 时提醒" : "已用 \(lowerBound ? "≥ " : "")\(used)    额度 \(limit)", 12, color: .secondaryLabelColor))
        addDetail(body, "当前周期", periodDescription(current, summary: summary))
        addDetail(body, "统计范围", kind == "quota" ? "账号级 · \(Int(budgetNumber(current["windowMinutes"]) ?? 10080)) 分钟额度窗口" : "\(choiceLabel("models", id: current["model"] as? String ?? "all")) · \(choiceLabel("tasks", id: current["task"] as? String ?? "all"))")
        let thresholds = (rule["thresholds"] as? [NSNumber] ?? []).map { String(format: "%g%%", $0.doubleValue) }.joined(separator: " / ")
        addDetail(body, "提醒条件", kind == "quota" ? "官方余量下限 · 每期一次 · 无声" : "剩余 \(thresholds) · 每期每级一次 · 无声")
        addDetail(body, "数据依据", kind == "money" ? "本机记录 × 自定义模型单价 · 估算金额，不代表订阅账单" : kind == "quota" ? "官方账号额度快照 · 不按模型或任务拆分" : "本机已落盘记录 · 缓存与推理不重复计算")
        if let seconds = budgetNumber(state["refreshSeconds"]) {
            addDetail(body, "更新节奏", kind == "quota" ? "官方额度约 60 秒更新" : seconds == 0 ? "Token / 金额自动更新暂停；手动刷新后判断" : "Token / 金额每 \(Int(seconds)) 秒更新")
        }
        if let updated = budgetNumber(summary["updatedAt"]) { addDetail(body, "更新于", dateString(updated, zone: (rule["period"] as? BudgetObject)?["timezone"] as? String)) }
        if let coverage = summary["coverage"] as? String, coverage != "complete" { addDetail(body, "数据覆盖", lowerBound ? "以上为已知消费下界；缺失类别仍未计入，实际消费可能更高。" : coverage == "partial" ? "部分数据缺失，剩余额度暂无法确认" : "尚无完整数据") }
        if (summary["paused"] as? Bool ?? false) { addDetail(body, "提醒状态", "视觉提醒暂停，统计继续更新") }
        let actionRow = budgetStack([
            BudgetActionButton("在浮窗显示") { [weak self] in self?.actionHandler("pin", ["id": id]) },
            BudgetActionButton(rule["enabled"] as? Bool ?? true ? "停用预算" : "启用预算") { [weak self] in self?.actionHandler("toggle", ["id": id, "enabled": !(rule["enabled"] as? Bool ?? true), "expectedRevision": rule["revision"] ?? 0]) }
        ], horizontal: true)
        body.addArrangedSubview(actionRow)
        let pauseRow = budgetStack([
            BudgetActionButton("暂停 30 分钟") { [weak self] in self?.actionHandler("pause", ["id": id, "durationSeconds": 1800]) },
            BudgetActionButton("本周期不再弹出") { [weak self] in self?.actionHandler("pause", ["id": id, "mode": "cycle"]) },
            BudgetActionButton("恢复提醒") { [weak self] in self?.actionHandler("resume", ["id": id]) }
        ], horizontal: true, spacing: 6)
        body.addArrangedSubview(pauseRow)
        let delete = BudgetActionButton(deleteArmedID == id ? "确认删除此预算" : "删除预算…") { [weak self] in
            guard let self = self else { return }
            if self.deleteArmedID == id { self.actionHandler("delete", ["id": id, "expectedRevision": rule["revision"] ?? 0]); self.deleteArmedID = nil }
            else { self.deleteArmedID = id; self.showList() }
        }
        delete.contentTintColor = .systemRed; body.addArrangedSubview(delete)
        if deleteArmedID == id { body.addArrangedSubview(budgetText("再次点击确认删除。统计记录不受影响。", 11, color: .secondaryLabelColor)) }
        if let events = state["events"] as? [BudgetObject] {
            let relevant = events.filter { ($0["ruleID"] as? String ?? $0["budgetID"] as? String) == id }.prefix(5)
            if !relevant.isEmpty {
                body.addArrangedSubview(budgetText("最近提醒", 13, .medium))
                for event in relevant { body.addArrangedSubview(budgetText(event["message"] as? String ?? event["title"] as? String ?? "预算视觉提醒", 12, color: .secondaryLabelColor)) }
            }
        }
        return BudgetCard(content: body)
    }
    private func addDetail(_ body: NSStackView, _ key: String, _ value: String) {
        let title = budgetText(key, 12, color: .secondaryLabelColor); title.widthAnchor.constraint(equalToConstant: 70).isActive = true
        let text = budgetText(value, 12)
        let row = budgetStack([title, text], horizontal: true, spacing: 12); row.alignment = .top
        body.addArrangedSubview(row); row.widthAnchor.constraint(equalTo: body.widthAnchor).isActive = true
    }
    private func choiceLabel(_ key: String, id: String) -> String {
        if id == "all" { return key == "models" ? "全部模型" : "全部任务" }
        return (state[key] as? [BudgetObject])?.first { $0["id"] as? String == id }?["label"] as? String ?? id
    }
    private func kindName(_ kind: String) -> String { kind == "money" ? "估算金额" : kind == "quota" ? "官方额度" : "Token 数量" }
    private func metricName(_ metric: String) -> String { metric == "noncached" ? "非缓存输入 + 输出" : metric == "output" ? "仅输出 Token" : "总 Token · 输入 + 输出" }
    private func periodName(_ rule: BudgetObject) -> String {
        let type = (rule["period"] as? BudgetObject)?["type"] as? String ?? "day"
        return ["day": "每日", "week": "每周", "month": "每月", "once": "自定义时段", "interval": "固定时长"][type] ?? type
    }
    private func dateString(_ timestamp: Double, zone: String?) -> String {
        let formatter = DateFormatter(); formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = "yyyy-MM-dd HH:mm"; formatter.timeZone = TimeZone(identifier: zone ?? "") ?? .current
        return formatter.string(from: Date(timeIntervalSince1970: timestamp))
    }
    private func periodDescription(_ rule: BudgetObject, summary: BudgetObject) -> String {
        let period = rule["period"] as? BudgetObject ?? [:], zone = period["timezone"] as? String ?? TimeZone.current.identifier
        if let start = budgetNumber(summary["periodStart"]), let end = budgetNumber(summary["periodEnd"]) {
            return "\(dateString(start, zone: zone)) — \(dateString(end, zone: zone))\n\(zone) · \(periodName(rule))"
        }
        return "\(periodName(rule)) · \(zone) · 等待本期区间"
    }

    private func beginEditing(_ original: BudgetObject?) {
        editing = true; pendingSave = false; pendingPin = false; controls.removeAll(); sections.removeAll(); priceFields.removeAll()
        let now = Date().timeIntervalSince1970
        editingRule = original ?? ["id": UUID().uuidString, "revision": 0, "name": "", "kind": "token", "amount": 20_000_000,
            "model": "all", "task": "all", "tokenMetric": "total", "currency": "USD", "fx": 1,
            "period": ["type": "day", "timezone": TimeZone.current.identifier, "hour": 0, "minute": 0],
            "thresholds": [20, 10, 0], "enabled": true, "source": state["source"] as? String ?? "codex", "windowMinutes": 10080, "quotaCondition": "floor"]
        expectedRevision = Int(budgetNumber(editingRule["revision"]) ?? 0)
        let rule = editingRule, kind = rule["kind"] as? String ?? "token", period = rule["period"] as? BudgetObject ?? [:]
        let form = budgetStack(spacing: 18)
        let basics = budgetStack(spacing: 14)
        basics.addArrangedSubview(fieldRow("预算名称", textField("name", rule["name"] as? String ?? "", placeholder: "例如：日常开发")))
        basics.addArrangedSubview(fieldRow("预算口径", popup("kind", [("token", "Token 数量"), ("money", "估算金额"), ("quota", "官方额度")], selected: kind)))
        let condition = popup("quotaCondition", [("floor", "官方余量下限"), ("consume", "期间消耗上限 · 暂不可用")], selected: "floor")
        condition.itemArray.last?.isEnabled = false; condition.autoenablesItems = false
        let quotaNote = budgetStack([fieldRow("提醒条件", condition), budgetText("期间消耗需要稳定的账号与额度窗口标识，当前无法可靠区分重置及跨设备变化，暂不提供。", 11, color: .secondaryLabelColor)], spacing: 8)
        sections["quota"] = quotaNote; basics.addArrangedSubview(quotaNote)
        let amount = budgetNumber(rule["amount"]) ?? 20_000_000
        let amountField = textField("amount", String(format: "%g", kind == "token" ? amount / 1_000_000 : amount))
        let units = popup("tokenUnit", [("M", "百万 Token · M"), ("K", "千 Token · K"), ("raw", "Token")], selected: "M")
        let unitRow = fieldRow("Token 单位", units); sections["tokenUnit"] = unitRow
        let amountRow = fieldRow("每期额度", amountField); sections["amountRow"] = amountRow
        basics.addArrangedSubview(amountRow); basics.addArrangedSubview(unitRow)
        let metric = fieldRow("Token 口径", popup("tokenMetric", [("total", "总 Token（缓存与推理不重复计入）"), ("noncached", "非缓存输入 + 输出"), ("output", "仅输出（含推理）")], selected: rule["tokenMetric"] as? String ?? "total"))
        sections["tokenMetric"] = metric; basics.addArrangedSubview(metric)
        let window = fieldRow("官方窗口 · 分钟", textField("windowMinutes", String(Int(budgetNumber(rule["windowMinutes"]) ?? 10080)), placeholder: "每周 10080；5 小时 300"))
        sections["window"] = window; basics.addArrangedSubview(window)
        let money = budgetStack(spacing: 12)
        money.addArrangedSubview(fieldRow("币种", popup("currency", [("USD", "USD · 美元"), ("CNY", "CNY · 人民币")], selected: rule["currency"] as? String ?? "USD")))
        money.addArrangedSubview(fieldRow("固定汇率 USD → CNY", textField("fx", String(format: "%g", budgetNumber(rule["fx"]) ?? 1))))
        money.addArrangedSubview(budgetText("使用你填写的模型单价，不代表订阅扣费。价格与汇率固定于当前预算周期。缺失价格不会作为零元计算。", 11, color: .secondaryLabelColor))
        let table = budgetStack(spacing: 6); priceStack = table
        let priceHeader = budgetStack([budgetText("模型", 11), budgetText("输入", 11), budgetText("缓存输入", 11), budgetText("输出", 11), NSView()], horizontal: true, spacing: 8)
        priceHeader.arrangedSubviews[0].widthAnchor.constraint(equalToConstant: 170).isActive = true
        for view in priceHeader.arrangedSubviews[1...3] { view.widthAnchor.constraint(equalToConstant: 76).isActive = true }
        money.addArrangedSubview(budgetText("每百万 Token 的 USD 单价 · 不自动填入未经核实的价格", 11, .medium))
        money.addArrangedSubview(priceHeader); money.addArrangedSubview(table)
        for price in rule["prices"] as? [BudgetObject] ?? [] {
            addPriceRow(["model": price["model"] as? String ?? "", "input": numberText(price["input"]), "cachedInput": numberText(price["cachedInput"]), "output": numberText(price["output"])])
        }
        money.addArrangedSubview(BudgetActionButton("＋ 添加模型单价") { [weak self] in self?.addPriceRow([:]); self?.draftChanged() })
        sections["money"] = money; basics.addArrangedSubview(money)
        form.addArrangedSubview(BudgetCard(content: basics))

        let dates = budgetStack(spacing: 12)
        dates.addArrangedSubview(budgetText("时间计划", 14, .medium))
        dates.addArrangedSubview(fieldRow("周期", popup("period", [("day", "每日重置"), ("week", "每周重置"), ("month", "每月重置"), ("once", "自定义起止时间"), ("interval", "固定时长重复")], selected: period["type"] as? String ?? "day")))
        dates.addArrangedSubview(fieldRow("时区", textField("timezone", period["timezone"] as? String ?? TimeZone.current.identifier)))
        let reset = budgetStack([fieldRow("重置小时 · 0–23", textField("hour", String(Int(budgetNumber(period["hour"]) ?? 0)))), fieldRow("重置分钟 · 0–59", textField("minute", String(Int(budgetNumber(period["minute"]) ?? 0))))], spacing: 12)
        sections["reset"] = reset; dates.addArrangedSubview(reset)
        let weekdays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"].enumerated().map { (String($0.offset + 1), $0.element) }
        let week = fieldRow("每周重置日", popup("weekday", weekdays, selected: String(Int(budgetNumber(period["weekday"]) ?? 2))))
        sections["week"] = week; dates.addArrangedSubview(week)
        let month = fieldRow("每月日期 · 1–31", textField("day", String(Int(budgetNumber(period["day"]) ?? 1))))
        sections["month"] = month; dates.addArrangedSubview(month)
        let start = datePicker("start", budgetNumber(period["start"]) ?? now)
        let end = datePicker("end", budgetNumber(period["end"]) ?? now + 86400)
        let startRow = fieldRow("开始", start); sections["start"] = startRow; dates.addArrangedSubview(startRow)
        let endRow = fieldRow("结束", end); sections["end"] = endRow; dates.addArrangedSubview(endRow)
        let interval = fieldRow("每期小时数", textField("hours", String(format: "%g", (budgetNumber(period["seconds"]) ?? 86400) / 3600)))
        sections["interval"] = interval; dates.addArrangedSubview(interval)
        dates.addArrangedSubview(budgetText("包含开始时刻、不包含结束时刻。短月份的重置日取月末；本期已发生的记录会计入预算。官方窗口的实际重置时间由账号决定。", 11, color: .secondaryLabelColor))
        form.addArrangedSubview(BudgetCard(content: dates))

        let scope = budgetStack(spacing: 12)
        scope.addArrangedSubview(budgetText("统计范围", 14, .medium))
        scope.addArrangedSubview(fieldRow("模型", popup("model", [("all", "全部模型")], selected: rule["model"] as? String ?? "all")))
        scope.addArrangedSubview(fieldRow("任务", popup("task", [("all", "全部任务")], selected: rule["task"] as? String ?? "all")))
        scope.addArrangedSubview(budgetText("此范围独立保存，不跟随主面板或浮窗用量筛选。官方额度为账号级，不支持拆分模型与任务。", 11, color: .secondaryLabelColor))
        sections["scope"] = scope; form.addArrangedSubview(BudgetCard(content: scope))
        let notifications = budgetStack(spacing: 12)
        notifications.addArrangedSubview(budgetText("视觉提醒", 14, .medium))
        let levels = (rule["thresholds"] as? [NSNumber] ?? [20, 10, 0]).map { String(format: "%g", $0.doubleValue) }.joined(separator: ", ")
        let thresholdRow = fieldRow("剩余比例 · %", textField("thresholds", levels, placeholder: "20, 10, 0"))
        sections["thresholds"] = thresholdRow; notifications.addArrangedSubview(thresholdRow)
        notifications.addArrangedSubview(budgetText("用逗号分隔；每周期每级一次，跨过多个节点时合并为最高级。无声音、不强制展开窗口、不打断任务。", 11, color: .secondaryLabelColor))
        notifications.addArrangedSubview(check("enabled", "启用预算监测", rule["enabled"] as? Bool ?? true))
        notifications.addArrangedSubview(check("pin", "保存后在浮窗显示", false))
        if expectedRevision > 0 {
            notifications.addArrangedSubview(check("recalculateCurrent", "立即应用并重算本期", false))
            notifications.addArrangedSubview(budgetText("修改时间、范围或价格默认下期生效；勾选后本期按新规则重算，可能立即达到提醒线。", 11, color: .secondaryLabelColor))
        }
        form.addArrangedSubview(BudgetCard(content: notifications))
        let error = budgetText("", 12, color: .systemRed); errorText = error; form.addArrangedSubview(error)
        let save = BudgetActionButton("保存并启用") { [weak self] in self?.save() }; saveButton = save
        let cancel = BudgetActionButton("取消") { [weak self] in
            guard let self = self, !self.pendingSave else { return }; self.finishEditing(message: "已取消编辑，原有监测保持不变。")
        }
        controls["cancel"] = cancel
        let buttons = budgetStack([NSView(), cancel, save], horizontal: true)
        form.addArrangedSubview(buttons)
        for child in form.arrangedSubviews { child.widthAnchor.constraint(equalTo: form.widthAnchor).isActive = true }
        replaceContent(scroll(form), heading: expectedRevision == 0 ? "新建预算" : "编辑预算", subtitle: "保存后开始监测 · 草稿仅保存在本机", buttons: [])
        message.stringValue = "预算范围独立于用量页面筛选。"
        refreshChoices(); updateEditorVisibility(); actionHandler("requestChoices", [:]); actionHandler("viewing", ["id": editingRule["id"] ?? "", "editing": true]); draftChanged()
    }
    private func numberText(_ value: Any?) -> String { budgetNumber(value).map { String(format: "%g", $0) } ?? "" }
    private func textField(_ key: String, _ text: String, placeholder: String = "") -> NSTextField {
        let field = NSTextField(string: text); field.font = .systemFont(ofSize: 13); field.placeholderString = placeholder
        field.identifier = NSUserInterfaceItemIdentifier(key); field.setAccessibilityLabel(key)
        field.delegate = self; controls[key] = field; return field
    }
    private func popup(_ key: String, _ choices: [(String, String)], selected: String) -> NSPopUpButton {
        let popup = FeedbackPopUpButton(frame: .zero, pullsDown: false)
        for (id, title) in choices { popup.addItem(withTitle: title); popup.lastItem?.representedObject = id }
        select(popup, value: selected, preserveUnknown: true)
        popup.cell?.lineBreakMode = .byTruncatingTail
        popup.target = self; popup.action = #selector(controlChanged); popup.identifier = NSUserInterfaceItemIdentifier(key)
        popup.setAccessibilityLabel(key); popup.menu?.delegate = self; controls[key] = popup; return popup
    }
    private func select(_ popup: NSPopUpButton, value: String, preserveUnknown: Bool = false) {
        if let item = popup.itemArray.first(where: { $0.representedObject as? String == value }) { popup.select(item) }
        else if preserveUnknown { popup.addItem(withTitle: "\(value)（已保存）"); popup.lastItem?.representedObject = value; popup.select(popup.lastItem) }
        popup.toolTip = popup.selectedItem?.title
    }
    private func check(_ key: String, _ title: String, _ checked: Bool) -> NSButton {
        let button = FeedbackButton(checkboxWithTitle: title, target: self, action: #selector(controlChanged))
        button.state = checked ? .on : .off; button.identifier = NSUserInterfaceItemIdentifier(key); controls[key] = button; return button
    }
    private func datePicker(_ key: String, _ timestamp: Double) -> NSDatePicker {
        let field = NSDatePicker(); field.datePickerStyle = .textFieldAndStepper
        field.datePickerElements = [.yearMonthDay, .hourMinute]; field.dateValue = Date(timeIntervalSince1970: timestamp)
        field.timeZone = TimeZone(identifier: (editingRule["period"] as? BudgetObject)?["timezone"] as? String ?? "") ?? .current
        field.target = self; field.action = #selector(controlChanged); field.identifier = NSUserInterfaceItemIdentifier(key)
        controls[key] = field; return field
    }
    private func fieldRow(_ title: String, _ field: NSView) -> NSView {
        let label = budgetText(title, 12, color: .secondaryLabelColor)
        label.widthAnchor.constraint(equalToConstant: 154).isActive = true
        field.setAccessibilityLabel(title)
        let row = budgetStack([label, field], horizontal: true)
        field.setContentHuggingPriority(.defaultLow, for: .horizontal)
        field.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        // Menu titles and entered text may be very long. They must not set the
        // fitting width of the editor and cause AppKit to enlarge its window.
        field.widthAnchor.constraint(equalToConstant: 360).isActive = true
        return row
    }
    private func addPriceRow(_ values: [String: String]) {
        guard !pendingSave, let table = priceStack else { return }
        var fields: [String: NSTextField] = [:]
        for key in ["model", "input", "cachedInput", "output"] {
            let field = NSTextField(string: values[key] ?? ""); field.font = .systemFont(ofSize: 12); field.delegate = self
            field.setAccessibilityLabel("模型价格 \(key)"); field.placeholderString = key == "model" ? "模型 ID" : "未知"
            field.widthAnchor.constraint(equalToConstant: key == "model" ? 170 : 76).isActive = true; fields[key] = field
        }
        let row = budgetStack([fields["model"]!, fields["input"]!, fields["cachedInput"]!, fields["output"]!], horizontal: true, spacing: 8)
        let remove = BudgetActionButton("移除") { [weak self, weak row] in
            guard let self = self, !self.pendingSave, let row = row else { return }
            self.priceFields.removeAll { $0["model"] === fields["model"] }
            self.priceStack?.removeArrangedSubview(row); row.removeFromSuperview(); self.draftChanged()
        }
        row.addArrangedSubview(remove); table.addArrangedSubview(row); priceFields.append(fields)
    }
    private func replacePriceRows(_ rows: [[String: String]]) {
        priceStack?.arrangedSubviews.forEach { priceStack?.removeArrangedSubview($0); $0.removeFromSuperview() }
        priceFields.removeAll(); for row in rows { addPriceRow(row) }
    }
    private func refreshChoices() {
        guard trackingMenus.isEmpty else { deferredChoices = true; return }
        deferredChoices = false
        for (key, source) in [("model", "models"), ("task", "tasks")] {
            guard let popup = controls[key] as? NSPopUpButton else { continue }
            let selected = popup.selectedItem?.representedObject as? String ?? editingRule[key] as? String ?? "all"
            let choices = state[source] as? [BudgetObject] ?? []
            popup.removeAllItems(); popup.addItem(withTitle: key == "model" ? "全部模型" : "全部任务"); popup.lastItem?.representedObject = "all"
            for choice in choices {
                guard let id = choice["id"] as? String, id != "all" else { continue }
                let item = NSMenuItem(title: choice["label"] as? String ?? id, action: nil, keyEquivalent: "")
                item.representedObject = id; item.toolTip = item.title; popup.menu?.addItem(item)
            }
            select(popup, value: selected, preserveUnknown: true)
        }
    }
    @objc private func controlChanged(_ sender: NSControl) {
        if let popup = sender as? NSPopUpButton { popup.toolTip = popup.selectedItem?.title }
        if ["kind", "period", "enabled"].contains(sender.identifier?.rawValue ?? "") { updateEditorVisibility() }
        draftChanged()
    }
    private func draftChanged() { if editing { actionHandler("draftChanged", [:]) } }
    private func updateEditorVisibility() {
        let kind = selected("kind"), period = selected("period")
        let amountTitle = kind == "quota" ? "官方余量下限 · %" : "每期额度"
        ((sections["amountRow"] as? NSStackView)?.arrangedSubviews.first as? NSTextField)?.stringValue = amountTitle
        controls["amount"]?.setAccessibilityLabel(amountTitle)
        for key in ["tokenMetric", "tokenUnit"] { sections[key]?.isHidden = kind != "token" }
        sections["money"]?.isHidden = kind != "money"
        for key in ["quota", "window"] { sections[key]?.isHidden = kind != "quota" }
        sections["thresholds"]?.isHidden = kind == "quota"
        controls["model"]?.isEnabled = kind != "quota" && !pendingSave; controls["task"]?.isEnabled = kind != "quota" && !pendingSave
        sections["reset"]?.isHidden = !["day", "week", "month"].contains(period)
        sections["week"]?.isHidden = period != "week"; sections["month"]?.isHidden = period != "month"
        sections["start"]?.isHidden = !["once", "interval"].contains(period)
        sections["end"]?.isHidden = period != "once"; sections["interval"]?.isHidden = period != "interval"
        let zone = TimeZone(identifier: text("timezone")) ?? .current
        (controls["start"] as? NSDatePicker)?.timeZone = zone; (controls["end"] as? NSDatePicker)?.timeZone = zone
        saveButton?.title = (controls["enabled"] as? NSButton)?.state == .on ? "保存并启用" : "保存"
    }
    private func selected(_ key: String) -> String { (controls[key] as? NSPopUpButton)?.selectedItem?.representedObject as? String ?? "" }
    private func text(_ key: String) -> String { (controls[key] as? NSTextField)?.stringValue.trimmingCharacters(in: .whitespacesAndNewlines) ?? "" }
    private func setEditorEnabled(_ enabled: Bool) {
        for control in controls.values { control.isEnabled = enabled }
        for row in priceFields { for field in row.values { field.isEnabled = enabled } }
        saveButton?.isEnabled = enabled; saveButton?.title = enabled ? "保存" : "正在保存…"
        if enabled { updateEditorVisibility() }
    }
    private func finishEditing(message text: String) {
        editing = false; pendingSave = false; editingRule = [:]; expectedRevision = 0
        message.stringValue = text; showList(); actionHandler("draftChanged", [:])
        actionHandler("viewing", ["id": selectedBudgetID ?? "", "editing": false])
        if let deferred = deferredNavigation { deferredNavigation = nil; navigate(budgetID: deferred.0, create: deferred.1) }
    }
    private func save() {
        guard editing, !pendingSave else { return }
        window?.makeFirstResponder(nil)
        do {
            let rule = try validatedRule()
            pendingPin = (controls["pin"] as? NSButton)?.state == .on
            pendingSave = true; setEditorEnabled(false); errorText?.stringValue = ""
            actionHandler("save", ["rule": rule, "expectedRevision": expectedRevision,
                "recalculateCurrent": (controls["recalculateCurrent"] as? NSButton)?.state == .on])
        } catch { errorText?.stringValue = error.localizedDescription }
    }
    private struct Invalid: LocalizedError { let message: String; var errorDescription: String? { message } }
    private func validatedRule() throws -> BudgetObject {
        func fail(_ value: String) -> Invalid { Invalid(message: value) }
        func number(_ key: String, minimum: Double, maximum: Double = Double.greatestFiniteMagnitude, integer: Bool = false) throws -> Double {
            guard let value = Double(text(key)), value.isFinite, value >= minimum, value <= maximum, !integer || value.rounded() == value else { throw fail("请检查 \(key) 的有效范围。") }
            return value
        }
        let name = text("name"), kind = selected("kind"), type = selected("period")
        guard !name.isEmpty, name.count <= 60 else { throw fail("请输入 1–60 个字符的预算名称。") }
        guard ["token", "money", "quota"].contains(kind) else { throw fail("请选择支持的预算口径。") }
        let rawAmount = try number("amount", minimum: kind == "quota" ? 0 : Double.leastNonzeroMagnitude, maximum: kind == "quota" ? 100 : 1e15)
        let multiplier = kind == "token" ? (selected("tokenUnit") == "M" ? 1_000_000.0 : selected("tokenUnit") == "K" ? 1_000.0 : 1.0) : 1.0
        let amount = rawAmount * multiplier
        guard amount.isFinite, amount <= 9_007_199_254_740_991 else { throw fail("额度过大，请缩小数值。") }
        guard ["total", "noncached", "output"].contains(selected("tokenMetric")), ["USD", "CNY"].contains(selected("currency")) else { throw fail("统计口径或币种无效，请重新选择。") }
        guard TimeZone(identifier: text("timezone")) != nil else { throw fail("请输入有效 IANA 时区，例如 Asia/Shanghai。") }
        var period: BudgetObject = ["type": type, "timezone": text("timezone")]
        switch type {
        case "day", "week", "month":
            period["hour"] = try number("hour", minimum: 0, maximum: 23, integer: true)
            period["minute"] = try number("minute", minimum: 0, maximum: 59, integer: true)
            if type == "week" {
                guard let day = Int(selected("weekday")), (1...7).contains(day) else { throw fail("每周重置日无效。") }; period["weekday"] = day
            }
            if type == "month" { period["day"] = try number("day", minimum: 1, maximum: 31, integer: true) }
        case "once", "interval":
            let start = (controls["start"] as? NSDatePicker)?.dateValue.timeIntervalSince1970 ?? 0
            guard start.isFinite else { throw fail("开始时间无效。") }; period["start"] = floor(start / 60) * 60
            if type == "once" {
                let end = floor(((controls["end"] as? NSDatePicker)?.dateValue.timeIntervalSince1970 ?? 0) / 60) * 60
                guard end > floor(start / 60) * 60 else { throw fail("结束时间必须晚于开始时间。") }; period["end"] = end
            } else { period["seconds"] = try number("hours", minimum: 1.0 / 60, maximum: 315_576_000 / 3600) * 3600 }
        default: throw fail("请选择有效时间计划。")
        }
        var levels: [Double] = []
        if kind == "quota" {
            guard selected("quotaCondition") == "floor" else { throw fail("当前不支持可靠的期间消耗预算，请使用官方余量下限。") }
            levels = [amount]
        } else {
            let pieces = text("thresholds").replacingOccurrences(of: "，", with: ",").split(separator: ",", omittingEmptySubsequences: false)
            guard !pieces.isEmpty, pieces.count <= 10 else { throw fail("请填写 1–10 个提醒比例，用逗号分隔。") }
            for piece in pieces {
                guard let value = Double(piece.trimmingCharacters(in: .whitespaces)), value.isFinite, value >= 0, value <= 100 else { throw fail("提醒比例必须是 0–100 之间的数字。") }; levels.append(value)
            }
            guard Set(levels).count == levels.count else { throw fail("提醒比例不能重复。") }; levels.sort(by: >)
        }
        var result = editingRule
        result["name"] = name; result["kind"] = kind; result["amount"] = amount; result["period"] = period
        result["thresholds"] = levels; result["tokenMetric"] = selected("tokenMetric")
        result["model"] = kind == "quota" ? "all" : selected("model"); result["task"] = kind == "quota" ? "all" : selected("task")
        result["enabled"] = (controls["enabled"] as? NSButton)?.state == .on
        if kind == "quota" { result["quotaCondition"] = "floor"; result["windowMinutes"] = try number("windowMinutes", minimum: 1, maximum: 525600, integer: true) }
        if kind == "money" {
            result["currency"] = selected("currency"); result["fx"] = try number("fx", minimum: Double.leastNonzeroMagnitude, maximum: 1_000_000)
            var prices: [BudgetObject] = [], seen = Set<String>()
            for row in priceFields {
                let model = row["model"]?.stringValue.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                let rateText = ["input", "cachedInput", "output"].map { row[$0]?.stringValue.trimmingCharacters(in: .whitespacesAndNewlines) ?? "" }
                if model.isEmpty && rateText.allSatisfy({ $0.isEmpty }) { continue }
                guard !model.isEmpty, !seen.contains(model) else { throw fail("模型价格必须有唯一模型 ID。") }; seen.insert(model)
                var price: BudgetObject = ["model": model]
                for (index, key) in ["input", "cachedInput", "output"].enumerated() {
                    if rateText[index].isEmpty { continue }
                    guard let value = Double(rateText[index]), value.isFinite, value >= 0, value <= 1e9 else { throw fail("模型单价必须为非负数字，未知价格请留空。") }; price[key] = value
                }
                prices.append(price)
            }
            result["prices"] = prices
        }
        return result
    }
}
extension BudgetPage: NSMenuDelegate {
    func menuWillOpen(_ menu: NSMenu) { trackingMenus.insert(ObjectIdentifier(menu)) }
    func menuDidClose(_ menu: NSMenu) {
        trackingMenus.remove(ObjectIdentifier(menu))
        if deferredChoices {
            DispatchQueue.main.async { [weak self] in
                guard let self = self, self.editing else { return }; self.refreshChoices()
            }
        }
    }
}
extension BudgetPage: NSTextFieldDelegate {
    func controlTextDidChange(_ obj: Notification) {
        if let field = obj.object as? NSTextField, field.identifier?.rawValue == "timezone" { updateEditorVisibility() }
        draftChanged()
    }
}
