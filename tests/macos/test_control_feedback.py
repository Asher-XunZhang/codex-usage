"""Native feedback pixels and synthetic events; no installed app or visible menu.

The production controls are compiled unchanged. A segmented-cell tracking hook
and a test-process mouse-up capture the pressed frame without physical input.
The action test uses native button keyboard/accessibility dispatch directly.
"""
from tools.common.paths import ROOT, BACKEND, macos_source
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


DRIVER = r'''import AppKit
import ObjectiveC
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
func check(_ value: @autoclosure () -> Bool, _ message: String) { precondition(value(), message) }
final class FixtureWindow: NSWindow {
    var pointer = NSPoint(x: -1000, y: -1000)
    override var mouseLocationOutsideOfEventStream: NSPoint { pointer }
}
final class Backdrop: NSView {
    var dark = false
    override func draw(_ dirtyRect: NSRect) {
        (dark ? NSColor(calibratedWhite: 0.10, alpha: 1) : NSColor(calibratedWhite: 0.97, alpha: 1)).setFill()
        bounds.intersection(dirtyRect).fill()
    }
}
final class CaptureSegmentCell: NSSegmentedCell {
    var capture: (() -> Void)?
    override func trackMouse(with event: NSEvent, in cellFrame: NSRect, of controlView: NSView, untilMouseUp flag: Bool) -> Bool {
        capture?()
        return false
    }
}
final class Counter: NSObject {
    var count = 0
    @objc func clicked(_ sender: Any?) { count += 1 }
}
struct Pixels {
    let bitmap: NSBitmapImageRep
    var bytes: [UInt8] { Array(UnsafeBufferPointer(start: bitmap.bitmapData!, count: bitmap.bytesPerRow * bitmap.pixelsHigh)) }
}
final class Fixture {
    let window: FixtureWindow
    let root = Backdrop(frame: NSRect(x: 0, y: 0, width: 250, height: 82))
    let control: NSControl
    let kind: String
    let counter = Counter()
    init(_ kind: String, dark: Bool) {
        self.kind = kind
        window = FixtureWindow(contentRect: root.frame, styleMask: .borderless, backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        root.dark = dark
        window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        if kind == "button" {
            let button = FeedbackButton(title: "新建预算", target: nil, action: nil)
            button.bezelStyle = .rounded
            control = button
        } else if kind == "popup" {
            let popup = FeedbackPopUpButton(frame: .zero, pullsDown: false)
            popup.addItems(withTitles: ["全部模型", "示例模型"])
            control = popup
        } else {
            let segmented = FeedbackSegmentedControl(frame: .zero)
            let cell = CaptureSegmentCell(textCell: "")
            cell.segmentCount = 2; cell.trackingMode = .selectOne
            cell.setLabel("用量统计", forSegment: 0); cell.setLabel("预算提醒", forSegment: 1)
            cell.setWidth(88, forSegment: 0); cell.setWidth(88, forSegment: 1)
            segmented.cell = cell; segmented.selectedSegment = 0
            control = segmented
        }
        control.frame = NSRect(x: 32, y: 24, width: 186, height: 32)
        control.target = counter; control.action = #selector(Counter.clicked(_:))
        control.appearance = window.appearance
        window.contentView = root; root.addSubview(control)
        control.updateTrackingAreas()
        check(!window.isVisible, "A feedback test must not show a window")
    }
    func event(_ type: NSEvent.EventType = .mouseMoved, at point: NSPoint = NSPoint(x: 120, y: 40)) -> NSEvent {
        NSEvent.mouseEvent(with: type, location: point, modifierFlags: [], timestamp: 0,
                          windowNumber: window.windowNumber, context: nil, eventNumber: 1, clickCount: 1, pressure: 1)!
    }
    func capture() -> Pixels {
        root.layoutSubtreeIfNeeded()
        let bitmap = root.bitmapImageRepForCachingDisplay(in: root.bounds)!
        bitmap.bitmapData!.initialize(repeating: 0, count: bitmap.bytesPerRow * bitmap.pixelsHigh)
        root.cacheDisplay(in: root.bounds, to: bitmap)
        return Pixels(bitmap: bitmap)
    }
    func hover(_ value: Bool) {
        window.pointer = value ? control.convert(NSPoint(x: control.bounds.midX, y: control.bounds.midY), to: nil) : NSPoint(x: -1000, y: -1000)
        if value { control.mouseEntered(with: event()) } else { control.mouseExited(with: event()) }
    }
    func pressed(at point: NSPoint = NSPoint(x: 120, y: 40)) -> Pixels {
        if let segmented = control as? FeedbackSegmentedControl {
            var result: Pixels?
            let cell = segmented.cell as? CaptureSegmentCell
            cell?.capture = { result = self.capture() }
            let mouseUp = event(.leftMouseUp, at: point)
            let timer = Timer(timeInterval: 0.02, repeats: false) { _ in
                if result == nil { result = self.capture() }
                NSApp.postEvent(mouseUp, atStart: true)
            }
            RunLoop.main.add(timer, forMode: .eventTracking)
            RunLoop.main.add(timer, forMode: .common)
            // Bound native tracking even if an OS version uses another mode.
            let fallback = DispatchWorkItem { NSApp.postEvent(mouseUp, atStart: true) }
            DispatchQueue.global().asyncAfter(deadline: .now() + 0.2, execute: fallback)
            segmented.mouseDown(with: event(.leftMouseDown, at: point))
            timer.invalidate(); fallback.cancel()
            cell?.capture = nil
            check(result != nil, "Native segmented tracking must reach the pressed-frame hook")
            return result!
        }
        control.cell?.isHighlighted = true
        let result = capture()
        control.cell?.isHighlighted = false
        return result
    }
    func feedbackAreas() -> [NSTrackingArea] {
        let options: NSTrackingArea.Options = [.mouseEnteredAndExited, .activeAlways, .inVisibleRect]
        return control.trackingAreas.filter { $0.options == options && ($0.owner as? NSControl) === control }
    }
    func dispose() { window.contentView = nil; window.close() }
}
func changedPixels(_ a: Pixels, _ b: Pixels, where includes: (NSPoint) -> Bool) -> Int {
    let image = a.bitmap, other = b.bitmap
    let sx = CGFloat(image.pixelsWide) / 250, sy = CGFloat(image.pixelsHigh) / 82
    var changed = 0
    for y in 0..<image.pixelsHigh {
        for x in 0..<image.pixelsWide {
            let point = NSPoint(x: (CGFloat(x) + 0.5) / sx, y: 82 - (CGFloat(y) + 0.5) / sy)
            if !includes(point) { continue }
            let offset = y * image.bytesPerRow + x * image.samplesPerPixel
            if (0..<image.samplesPerPixel).contains(where: { image.bitmapData![offset + $0] != other.bitmapData![offset + $0] }) { changed += 1 }
        }
    }
    return changed
}
func meanPixelChange(_ a: Pixels, _ b: Pixels, in rect: NSRect) -> Double {
    let image = a.bitmap, other = b.bitmap
    let sx = CGFloat(image.pixelsWide) / 250, sy = CGFloat(image.pixelsHigh) / 82
    var delta = 0, samples = 0
    for y in 0..<image.pixelsHigh {
        for x in 0..<image.pixelsWide {
            let point = NSPoint(x: (CGFloat(x) + 0.5) / sx, y: 82 - (CGFloat(y) + 0.5) / sy)
            guard rect.contains(point) else { continue }
            let offset = y * image.bytesPerRow + x * image.samplesPerPixel
            for channel in 0..<3 { delta += abs(Int(image.bitmapData![offset + channel]) - Int(other.bitmapData![offset + channel])); samples += 1 }
        }
    }
    return samples > 0 ? Double(delta) / Double(samples) : 0
}
func glowViews(in view: NSView) -> [NSView] {
    (String(describing: type(of: view)).contains("ControlGlowView") ? [view] : []) + view.subviews.flatMap { glowViews(in: $0) }
}
final class WeakGlow {
    weak var view: NSView?
    init(_ view: NSView) { self.view = view }
}
func sameNativeImplementation(_ subclass: AnyClass, _ base: AnyClass, _ selector: String) -> Bool {
    let selector = NSSelectorFromString(selector)
    guard let lhs = class_getInstanceMethod(subclass, selector), let rhs = class_getInstanceMethod(base, selector) else { return false }
    return unsafeBitCast(method_getImplementation(lhs), to: UInt.self) == unsafeBitCast(method_getImplementation(rhs), to: UInt.self)
}
func diagnosticHover(_ control: NSControl) -> String {
    guard let item = Mirror(reflecting: control).children.first(where: { $0.label?.contains("feedback") == true }) else { return "missing-storage" }
    let mirror = Mirror(reflecting: item.value)
    let value = mirror.displayStyle == .optional ? mirror.children.first!.value : item.value
    return Mirror(reflecting: value).children.first(where: { $0.label == "hovered" }).map { String(describing: $0.value) } ?? "missing-hover"
}
var output: [String: Any] = [:]
switch CommandLine.arguments[1] {
case "pixels":
    var rows: [[String: Any]] = []
    var images: [[Pixels]] = []
    for dark in [false, true] {
        for kind in ["button", "popup", "segmented"] {
            let fixture = Fixture(kind, dark: dark)
            let normal = fixture.capture()
            fixture.hover(true); let hover = fixture.capture()
            let pressed = fixture.pressed()
            let pressedActionCount = fixture.counter.count
            fixture.control.isEnabled = false; let disabled = fixture.capture()
            let frame = fixture.control.frame
            let disabledGlows = glowViews(in: fixture.root).count
            var segmentHoverStable = true, segmentSelectionStable = true, segmentClicksIndependent = true
            var segmentClicks: [[String: Int]] = []
            if let segmented = fixture.control as? FeedbackSegmentedControl {
                segmented.isEnabled = true; segmented.selectedSegment = 0
                let first = NSPoint(x: frame.minX + 44, y: frame.midY)
                let second = NSPoint(x: frame.minX + 132, y: frame.midY)
                fixture.window.pointer = first; segmented.updateTrackingAreas(); let firstHover = fixture.capture()
                fixture.window.pointer = second; segmented.updateTrackingAreas(); let secondHover = fixture.capture()
                segmentHoverStable = firstHover.bytes == secondHover.bytes
                segmentSelectionStable = segmented.selectedSegment == 0
                // The snapshot hook deliberately cancels native tracking;
                // use an untouched native cell to exercise actual selection.
                let nativeCell = NSSegmentedCell(textCell: "")
                nativeCell.segmentCount = 2; nativeCell.trackingMode = .selectOne
                nativeCell.setLabel("用量统计", forSegment: 0); nativeCell.setLabel("预算提醒", forSegment: 1)
                nativeCell.setWidth(88, forSegment: 0); nativeCell.setWidth(88, forSegment: 1)
                segmented.cell = nativeCell; segmented.selectedSegment = 0
                segmented.target = fixture.counter; segmented.action = #selector(Counter.clicked(_:))
                for (index, point) in [first, second].enumerated() {
                    fixture.window.pointer = point; segmented.updateTrackingAreas()
                    let actions = fixture.counter.count
                    var children = segmented.accessibilityChildren() ?? []
                    // Some AppKit releases expose one cell group between
                    // the control and its accessible segment buttons.
                    for _ in 0..<3 {
                        guard children.count == 1, let group = children[0] as? NSObject,
                              let descendants = group.accessibilityAttributeValue(.children) as? [Any], !descendants.isEmpty else { break }
                        children = descendants
                    }
                    if index < children.count, let child = children[index] as? NSObject {
                        child.accessibilityPerformAction(.press)
                    }
                    segmentClicks.append(["expected": index, "selected": segmented.selectedSegment, "actions": fixture.counter.count - actions, "children": children.count])
                    segmentClicksIndependent = segmentClicksIndependent && segmented.selectedSegment == index && fixture.counter.count == actions + 1
                }
            }
            rows.append(["theme": dark ? "dark" : "light", "kind": kind,
                         "hoverDiffers": normal.bytes != hover.bytes,
                         "pressedDiffers": hover.bytes != pressed.bytes,
                         "pressedActionCount": pressedActionCount,
                         "disabledDiffers": normal.bytes != disabled.bytes,
                         "hoverOutsideChanges": changedPixels(normal, hover) { !frame.contains($0) },
                         "pressedOutsideChanges": changedPixels(normal, pressed) { !frame.contains($0) },
                         "hoverInteriorChanges": changedPixels(normal, hover) { frame.insetBy(dx: 6, dy: 6).contains($0) },
                         "hoverFarChanges": changedPixels(normal, hover) { !frame.insetBy(dx: -24, dy: -24).contains($0) },
                         "pressedFarChanges": changedPixels(normal, pressed) { !frame.insetBy(dx: -24, dy: -24).contains($0) },
                         "disabledGlows": disabledGlows,
                         "segmentHoverStable": segmentHoverStable, "segmentSelectionStable": segmentSelectionStable,
                         "segmentClicksIndependent": segmentClicksIndependent, "segmentClicks": segmentClicks])
            images.append([normal, hover, pressed, disabled]); fixture.dispose()
        }
    }
    if CommandLine.arguments.count > 2 {
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1000, pixelsHigh: 640, bitsPerSample: 8, samplesPerPixel: 4,
                                      hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        NSColor(calibratedWhite: 0.94, alpha: 1).setFill(); NSRect(x: 0, y: 0, width: 1000, height: 640).fill()
        for (column, text) in ["Normal", "Hover", "Pressed", "Disabled"].enumerated() {
            (text as NSString).draw(at: NSPoint(x: column * 250 + 85, y: 605), withAttributes: [.font: NSFont.systemFont(ofSize: 15, weight: .medium), .foregroundColor: NSColor.black])
        }
        for (row, states) in images.enumerated() {
            let y = 500 - row * 90
            for (column, pixels) in states.enumerated() {
                let image = NSImage(size: NSSize(width: 250, height: 82)); image.addRepresentation(pixels.bitmap)
                image.draw(in: NSRect(x: column * 250, y: y, width: 250, height: 82))
            }
        }
        NSGraphicsContext.restoreGraphicsState()
        try bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[2]))
    }
    output["rows"] = rows
case "dense":
    var images: [[Pixels]] = []
    for dark in [false, true] {
        for kind in ["button", "popup", "segmented"] {
            let fixture = Fixture(kind, dark: dark)
            fixture.control.frame = NSRect(x: 78, y: 24, width: 94, height: 32)
            if let segmented = fixture.control as? NSSegmentedControl {
                segmented.setLabel("用量", forSegment: 0); segmented.setLabel("预算", forSegment: 1)
                segmented.setWidth(42, forSegment: 0); segmented.setWidth(42, forSegment: 1)
            }
            for (x, title) in [(CGFloat(6), "返回"), (CGFloat(176), "更多")] {
                let neighbor = NSButton(title: title, target: nil, action: nil)
                neighbor.bezelStyle = .rounded; neighbor.clipsToBounds = true
                neighbor.frame = NSRect(x: x, y: 24, width: 68, height: 32)
                fixture.root.addSubview(neighbor)
            }
            fixture.control.updateTrackingAreas()
            let normal = fixture.capture()
            fixture.hover(true); let hover = fixture.capture()
            let pressed = fixture.pressed()
            images.append([normal, hover, pressed]); fixture.dispose()
        }
    }
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1400, pixelsHigh: 1100, bitsPerSample: 8, samplesPerPixel: 4,
                                  hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
    NSColor(calibratedWhite: 0.94, alpha: 1).setFill(); NSRect(x: 0, y: 0, width: 1400, height: 1100).fill()
    ("相邻控件：光晕在边缘连续衔接" as NSString).draw(at: NSPoint(x: 24, y: 1042), withAttributes: [.font: NSFont.systemFont(ofSize: 27, weight: .semibold), .foregroundColor: NSColor.black])
    ("真实 AppKit 控件绘制 · 中间控件悬停 · 深浅主题对照" as NSString).draw(at: NSPoint(x: 24, y: 1005), withAttributes: [.font: NSFont.systemFont(ofSize: 18), .foregroundColor: NSColor.darkGray])
    for (x, text) in [(CGFloat(290), "正常"), (CGFloat(700), "悬停"), (CGFloat(1080), "右边缘局部 × 2.6")] {
        (text as NSString).draw(at: NSPoint(x: x, y: 962), withAttributes: [.font: NSFont.systemFont(ofSize: 18, weight: .medium), .foregroundColor: NSColor.black])
    }
    for (row, states) in images.enumerated() {
        let y = CGFloat(810 - row * 140)
        let label = "\(row < 3 ? "浅色" : "深色") · \(["按钮", "下拉框", "分段组"][row % 3])"
        (label as NSString).draw(at: NSPoint(x: 24, y: y + 56), withAttributes: [.font: NSFont.systemFont(ofSize: 17, weight: .medium), .foregroundColor: NSColor.black])
        for (column, pixels) in states.prefix(2).enumerated() {
            let image = NSImage(size: NSSize(width: 250, height: 82)); image.addRepresentation(pixels.bitmap)
            image.draw(in: NSRect(x: 145 + column * 410, y: Int(y), width: 400, height: 131))
        }
        let hover = NSImage(size: NSSize(width: 250, height: 82)); hover.addRepresentation(states[1].bitmap)
        hover.draw(in: NSRect(x: 1080, y: y + 1, width: 176.8, height: 124.8), from: NSRect(x: 143, y: 17, width: 68, height: 48), operation: .sourceOver, fraction: 1)
    }
    ("放大图直接裁剪悬停位图；邻居边缘渐隐受光，文字与核心区域保持原色。" as NSString).draw(at: NSPoint(x: 24, y: 45), withAttributes: [.font: NSFont.systemFont(ofSize: 17), .foregroundColor: NSColor.darkGray])
    NSGraphicsContext.restoreGraphicsState()
    try bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[2]))
case "segmented-preview":
    var rows: [[Pixels]] = []
    for dark in [false, true] {
        let fixture = Fixture("segmented", dark: dark)
        let segmented = fixture.control as! NSSegmentedControl
        fixture.window.pointer = NSPoint(x: 76, y: 40); segmented.updateTrackingAreas(); let left = fixture.capture()
        fixture.window.pointer = NSPoint(x: 164, y: 40); segmented.updateTrackingAreas(); let right = fixture.capture()
        check(left.bytes == right.bytes, "Moving between segments must keep the same whole-group halo")
        segmented.selectedSegment = 1; let selected = fixture.capture()
        rows.append([left, right, selected]); fixture.dispose()
    }
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1260, pixelsHigh: 600, bitsPerSample: 8, samplesPerPixel: 4,
                                  hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
    NSColor(calibratedWhite: 0.94, alpha: 1).setFill(); NSRect(x: 0, y: 0, width: 1260, height: 600).fill()
    ("分段控件：整组悬停，逐段选择" as NSString).draw(at: NSPoint(x: 24, y: 550), withAttributes: [.font: NSFont.systemFont(ofSize: 27, weight: .semibold), .foregroundColor: NSColor.black])
    for (column, label) in ["悬停左段 · 用量统计", "悬停右段 · 预算提醒", "右段已选中"].enumerated() {
        (label as NSString).draw(at: NSPoint(x: 25 + column * 420, y: 495), withAttributes: [.font: NSFont.systemFont(ofSize: 19, weight: .medium), .foregroundColor: NSColor.black])
    }
    for (row, states) in rows.enumerated() {
        (row == 0 ? "浅色" : "深色" as NSString).draw(at: NSPoint(x: 25, y: 447 - row * 200), withAttributes: [.font: NSFont.systemFont(ofSize: 17, weight: .medium), .foregroundColor: NSColor.black])
        for (column, pixels) in states.enumerated() {
            let image = NSImage(size: NSSize(width: 250, height: 82)); image.addRepresentation(pixels.bitmap)
            image.draw(in: NSRect(x: 20 + column * 420, y: 300 - row * 200, width: 400, height: 131))
        }
    }
    ("真实 AppKit 位图：左、右悬停图像完全一致；选中状态仍由原生分段控件呈现。" as NSString).draw(at: NSPoint(x: 24, y: 45), withAttributes: [.font: NSFont.systemFont(ofSize: 17), .foregroundColor: NSColor.darkGray])
    NSGraphicsContext.restoreGraphicsState()
    try bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: CommandLine.arguments[2]))
case "lifecycle":
    var rows: [[String: Any]] = []
    for kind in ["button", "popup", "segmented"] {
        let fixture = Fixture(kind, dark: true)
        let normal = fixture.capture()
        let trackingClippedInitially = fixture.control.bounds.contains(fixture.control.visibleRect)
        let area = fixture.feedbackAreas().first!
        let originalCount = fixture.control.trackingAreas.count
        for _ in 0..<200 { fixture.control.updateTrackingAreas() }
        let trackingClippedAfterUpdates = fixture.control.bounds.contains(fixture.control.visibleRect)
        let stable = fixture.feedbackAreas().count == 1 && fixture.feedbackAreas().first === area && fixture.control.trackingAreas.count == originalCount
        fixture.hover(true); fixture.hover(false)
        let exited = fixture.capture().bytes == normal.bytes
        fixture.hover(true); fixture.control.removeFromSuperview()
        let detached = fixture.feedbackAreas().isEmpty
        fixture.window.pointer = NSPoint(x: -1000, y: -1000)
        fixture.root.addSubview(fixture.control); fixture.control.updateTrackingAreas()
        let reattached = fixture.capture().bytes == normal.bytes && fixture.feedbackAreas().count == 1
        fixture.control.isEnabled = false
        let disabled = fixture.capture(); fixture.hover(true)
        let disabledIgnoresHover = fixture.capture().bytes == disabled.bytes
        fixture.window.pointer = NSPoint(x: -1000, y: -1000)
        fixture.control.isEnabled = true
        let enabledClean = fixture.capture().bytes == normal.bytes
        fixture.window.pointer = NSPoint(x: 120, y: 40)
        fixture.control.isEnabled = false; fixture.control.isEnabled = true
        let enabledInside = fixture.capture()
        fixture.hover(true)
        let enabledInsideRestoresHover = enabledInside.bytes != normal.bytes && enabledInside.bytes == fixture.capture().bytes
        fixture.hover(false)
        var references: [WeakGlow] = []
        var oneGlowPerHover = true, noGlowAfterExit = true, lightweightGlow = true
        var glowWantsLayer = false, glowHasLayer = false, glowTracks = false
        for _ in 0..<24 {
            autoreleasepool {
                fixture.hover(true); _ = fixture.capture()
                let views = glowViews(in: fixture.root)
                oneGlowPerHover = oneGlowPerHover && views.count == 1
                if let view = views.first {
                    references.append(WeakGlow(view))
                    glowWantsLayer = glowWantsLayer || view.wantsLayer
                    glowHasLayer = glowHasLayer || view.layer != nil
                    glowTracks = glowTracks || !view.trackingAreas.isEmpty
                    // AppKit may supply an inherited backing layer while drawing.
                    // The overlay must not request one or install input tracking.
                    lightweightGlow = lightweightGlow && !view.wantsLayer && view.trackingAreas.isEmpty
                }
                fixture.control.updateTrackingAreas()
                oneGlowPerHover = oneGlowPerHover && glowViews(in: fixture.root).count == 1
                fixture.hover(false)
                noGlowAfterExit = noGlowAfterExit && glowViews(in: fixture.root).isEmpty
            }
        }
        let glowsReleased = references.count == 24 && references.allSatisfy { $0.view == nil }
        rows.append(["kind": kind, "trackingStable": stable, "exitClears": exited,
                     "detachRemovesTracking": detached, "reattachClears": reattached,
                     "disabledIgnoresHover": disabledIgnoresHover, "enabledClean": enabledClean,
                     "enabledInsideRestoresHover": enabledInsideRestoresHover,
                     "trackingClippedInitially": trackingClippedInitially, "trackingClippedAfterUpdates": trackingClippedAfterUpdates,
                     "oneGlowPerHover": oneGlowPerHover, "noGlowAfterExit": noGlowAfterExit,
                     "glowsReleased": glowsReleased, "lightweightGlow": lightweightGlow,
                     "glowWantsLayer": glowWantsLayer, "glowHasLayer": glowHasLayer, "glowTracks": glowTracks])
        fixture.dispose()
    }
    output["rows"] = rows
case "layout":
    var rows: [[String: Any]] = []
    for kind in ["button", "popup", "segmented"] {
        let fixture = Fixture(kind, dark: true)
        let pointer = NSPoint(x: 40, y: 40)
        fixture.window.pointer = pointer
        fixture.control.mouseEntered(with: fixture.event())
        fixture.control.setFrameOrigin(NSPoint(x: 64, y: 24))
        fixture.control.updateTrackingAreas()
        let hoverBeforeCapture = diagnosticHover(fixture.control)
        let localPointer = NSStringFromPoint(fixture.control.convert(pointer, from: nil))
        let visibleRect = NSStringFromRect(fixture.control.visibleRect)
        let bounds = NSStringFromRect(fixture.control.bounds)
        let trackingClippedAfterMove = fixture.control.bounds.contains(fixture.control.visibleRect)
        let afterMoveAway = fixture.capture()
        let hoverAfterCapture = diagnosticHover(fixture.control)
        fixture.control.mouseExited(with: fixture.event())
        let noHoverAway = fixture.capture()
        fixture.control.setFrameOrigin(NSPoint(x: 32, y: 24))
        fixture.control.updateTrackingAreas()
        let trackingClippedAfterReturn = fixture.control.bounds.contains(fixture.control.visibleRect)
        let afterMoveUnderPointer = fixture.capture()
        fixture.control.mouseEntered(with: fixture.event())
        let explicitHover = fixture.capture()
        rows.append(["kind": kind, "clearsAfterMove": afterMoveAway.bytes == noHoverAway.bytes,
                     "hoverWhenMovedUnderPointer": afterMoveUnderPointer.bytes == explicitHover.bytes,
                     "hoverBeforeCapture": hoverBeforeCapture, "hoverAfterCapture": hoverAfterCapture, "localPointer": localPointer,
                     "visibleRect": visibleRect, "bounds": bounds,
                     "trackingClippedAfterMove": trackingClippedAfterMove, "trackingClippedAfterReturn": trackingClippedAfterReturn,
                     "pointerDidNotMove": fixture.window.pointer == pointer,
                     "trackingCount": fixture.feedbackAreas().count])
        fixture.dispose()
    }
    output["rows"] = rows
case "boundaries":
    var rows: [[String: Any]] = []
    for dark in [false, true] {
        for kind in ["button", "popup", "segmented"] {
            let fixture = Fixture(kind, dark: dark)
            let neighbor = NSButton(title: "+", target: nil, action: nil)
            neighbor.bezelStyle = .rounded; neighbor.clipsToBounds = true
            neighbor.frame = NSRect(x: 221, y: 26, width: 24, height: 28)
            fixture.root.addSubview(neighbor)
            let points = [NSPoint(x: 120, y: 40), NSPoint(x: 219, y: 40), NSPoint(x: 233, y: 40)]
            let beforeHits = points.map { fixture.root.hitTest($0) }
            let normal = fixture.capture()
            fixture.hover(true); let hover = fixture.capture()
            let hitTestingPreserved = points.enumerated().allSatisfy { fixture.root.hitTest($0.element) === beforeHits[$0.offset] }
            let glowNoninteractive = glowViews(in: fixture.root).allSatisfy { $0.hitTest(NSPoint(x: $0.frame.midX, y: $0.frame.midY)) == nil }
            let edgeDepth = min(8, min(neighbor.frame.width, neighbor.frame.height) / 4)
            let neighborCore = neighbor.frame.insetBy(dx: edgeDepth, dy: edgeDepth)
            let neighborCoreChanges = changedPixels(normal, hover) { neighborCore.contains($0) }
            let neighborEdgeChanges = changedPixels(normal, hover) { neighbor.frame.contains($0) && !neighborCore.contains($0) }
            // A hard rectangular exclusion would go from visible light to zero
            // at the first inner pixel. Sample either side of that boundary,
            // away from the native rounded corners and the label.
            let boundary = neighbor.frame.minX, middle = neighbor.frame.midY
            let edgeProfile = (-2...Int(edgeDepth)).map { offset in
                meanPixelChange(normal, hover, in: NSRect(x: boundary + CGFloat(offset), y: middle - 3, width: 1, height: 6))
            }
            let boundaryRatio = edgeProfile[1] > 0 ? edgeProfile[2] / edgeProfile[1] : 0
            fixture.hover(false)
            let clip = NSClipView(frame: NSRect(x: 20, y: 18, width: 200, height: 38))
            clip.drawsBackground = false; clip.clipsToBounds = true
            let document = NSView(frame: NSRect(x: 0, y: 0, width: 240, height: 160))
            neighbor.removeFromSuperview(); fixture.control.removeFromSuperview()
            fixture.root.addSubview(clip); clip.documentView = document; document.addSubview(fixture.control)
            fixture.control.frame = NSRect(x: 32, y: 24, width: 186, height: 32)
            fixture.control.updateTrackingAreas(); let clippedNormal = fixture.capture()
            let visible = fixture.control.visibleRect
            fixture.window.pointer = fixture.control.convert(NSPoint(x: visible.midX, y: visible.midY), to: nil)
            fixture.control.mouseEntered(with: fixture.event()); fixture.control.updateTrackingAreas()
            let clippedHover = fixture.capture()
            let viewport = clip.convert(clip.bounds, to: fixture.root)
            let viewportOutsideChanges = changedPixels(clippedNormal, clippedHover) { !viewport.contains($0) }
            clip.isHidden = true
            let ancestorHiddenGlows = glowViews(in: fixture.root).count
            clip.isHidden = false
            let glowsBeforeScroll = glowViews(in: fixture.root).count
            clip.scroll(to: NSPoint(x: 0, y: 90)); fixture.control.updateTrackingAreas()
            let scrollAwayGlows = glowViews(in: fixture.root).count
            rows.append(["theme": dark ? "dark" : "light", "kind": kind,
                         "neighborCoreChanges": neighborCoreChanges, "neighborEdgeChanges": neighborEdgeChanges,
                         "edgeProfile": edgeProfile, "boundaryRatio": boundaryRatio,
                         "hitTestingPreserved": hitTestingPreserved,
                         "glowNoninteractive": glowNoninteractive,
                         "viewportOutsideChanges": viewportOutsideChanges,
                         "ancestorHiddenGlows": ancestorHiddenGlows, "scrollAwayGlows": scrollAwayGlows,
                         "glowsBeforeScroll": glowsBeforeScroll,
                         "viewportHoverVisible": clippedNormal.bytes != clippedHover.bytes])
            fixture.hover(false); fixture.dispose()
        }
    }
    output["rows"] = rows
case "actions":
    let fixture = Fixture("button", dark: false)
    let button = fixture.control as! FeedbackButton
    let counter = Counter(); button.target = counter; button.action = #selector(Counter.clicked(_:))
    button.performClick(nil)
    let clickCount = counter.count
    let axResult = button.accessibilityPerformPress()
    let axCount = counter.count - clickCount
    let nativeCounter = Counter(), nativeButton = NSButton(title: "Baseline", target: nil, action: nil)
    nativeButton.target = nativeCounter; nativeButton.action = #selector(Counter.clicked(_:))
    fixture.root.addSubview(nativeButton)
    let nativeAXResult = nativeButton.accessibilityPerformPress()
    button.keyEquivalent = "r"; button.keyEquivalentModifierMask = .command
    let key = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: .command, timestamp: 0,
                              windowNumber: fixture.window.windowNumber, context: nil, characters: "r", charactersIgnoringModifiers: "r", isARepeat: false, keyCode: 15)!
    let beforeKey = counter.count
    let keyResult = button.performKeyEquivalent(with: key)
    let keyCount = counter.count - beforeKey
    button.isEnabled = false
    let beforeDisabled = counter.count
    button.performClick(nil); _ = button.accessibilityPerformPress(); _ = button.performKeyEquivalent(with: key)
    button.mouseDown(with: fixture.event(.leftMouseDown))
    let disabledCount = counter.count - beforeDisabled
    fixture.dispose()
    var native: [[String: Any]] = []
    for (kind, child, base) in [("button", FeedbackButton.self as AnyClass, NSButton.self as AnyClass),
                                ("popup", FeedbackPopUpButton.self as AnyClass, NSPopUpButton.self as AnyClass),
                                ("segmented", FeedbackSegmentedControl.self as AnyClass, NSSegmentedControl.self as AnyClass)] {
        let item = Fixture(kind, dark: false), counter = Counter()
        item.control.target = counter; item.control.action = #selector(Counter.clicked(_:)); item.control.isEnabled = false
        item.control.mouseDown(with: item.event(.leftMouseDown))
        native.append(["kind": kind, "disabledMouseActions": counter.count,
                       "keyboardInherited": sameNativeImplementation(child, base, "keyDown:"),
                       "accessibilityInherited": sameNativeImplementation(child, base, "accessibilityPerformPress")])
        item.dispose()
    }
    output = ["clickCount": clickCount, "axResult": axResult, "axCount": axCount, "keyResult": keyResult,
              "keyCount": keyCount, "disabledCount": disabledCount, "native": native,
              "nativeAXResult": nativeAXResult, "nativeAXCount": nativeCounter.count]
default: fatalError("Unknown test case")
}
let data = try JSONSerialization.data(withJSONObject: output, options: [.sortedKeys])
print(String(data: data, encoding: .utf8)!)
'''


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "Requires macOS developer tools")
class ControlFeedbackTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix="control feedback ")
        cls.addClassCleanup(cls.directory.cleanup)
        root = Path(cls.directory.name)
        main = root / "main.swift"
        main.write_text(DRIVER)
        cls.binary = root / "feedback-check"
        result = subprocess.run(
            ["xcrun", "swiftc", "-swift-version", "5", str(main),
             str(macos_source('ControlFeedback.swift')), "-o", str(cls.binary)],
            capture_output=True, text=True, timeout=90,
        )
        if result.returncode:
            raise AssertionError(result.stderr)

    def run_case(self, name, *args):
        result = subprocess.run([str(self.binary), name, *map(str, args)], capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout.strip().splitlines()[-1])

    def test_feedback_pixels_and_clipping(self):
        preview = os.environ.get("CODEX_CONTROL_FEEDBACK_PREVIEW")
        segmented_preview = os.environ.get("CODEX_CONTROL_FEEDBACK_SEGMENTED_PREVIEW")
        if segmented_preview:
            self.run_case("segmented-preview", segmented_preview)
        report = self.run_case("pixels", *([preview] if preview else []))
        for row in report["rows"]:
            with self.subTest(theme=row["theme"], kind=row["kind"]):
                for name in ["hoverDiffers", "pressedDiffers", "disabledDiffers"]:
                    self.assertTrue(row[name], row)
                self.assertGreater(row["hoverOutsideChanges"], 0, row)
                self.assertGreater(row["pressedOutsideChanges"], 0, row)
                self.assertEqual(row["hoverInteriorChanges"], 0, row)
                self.assertEqual(row["hoverFarChanges"], 0, row)
                self.assertEqual(row["pressedFarChanges"], 0, row)
                self.assertEqual(row["disabledGlows"], 0, row)
                self.assertLessEqual(row["pressedActionCount"], 1, row)
                self.assertTrue(row["segmentHoverStable"], row)
                self.assertTrue(row["segmentSelectionStable"], row)
                self.assertTrue(row["segmentClicksIndependent"], row)

    def test_tracking_and_detach_reset(self):
        for row in self.run_case("lifecycle")["rows"]:
            with self.subTest(kind=row["kind"]):
                for name, value in row.items():
                    if name not in ["kind", "glowWantsLayer", "glowHasLayer", "glowTracks"]:
                        self.assertTrue(value, row)

    def test_native_actions_keyboard_and_accessibility(self):
        report = self.run_case("actions")
        self.assertEqual(report["clickCount"], 1, report)
        self.assertEqual(report["axCount"], 1, report)
        self.assertEqual(report["keyCount"], 1, report)
        self.assertEqual(report["axResult"], report["nativeAXResult"], report)
        self.assertEqual(report["nativeAXCount"], 1, report)
        self.assertTrue(report["keyResult"], report)
        self.assertEqual(report["disabledCount"], 0, report)
        for row in report["native"]:
            with self.subTest(kind=row["kind"]):
                self.assertEqual(row["disabledMouseActions"], 0, row)
                self.assertTrue(row["keyboardInherited"], row)
                self.assertTrue(row["accessibilityInherited"], row)

    def test_stationary_pointer_rechecks_hover_after_layout_moves_control(self):
        for row in self.run_case("layout")["rows"]:
            with self.subTest(kind=row["kind"]):
                self.assertTrue(row["pointerDidNotMove"], row)
                self.assertTrue(row["clearsAfterMove"], row)
                self.assertTrue(row["hoverWhenMovedUnderPointer"], row)
                self.assertTrue(row["trackingClippedAfterMove"] and row["trackingClippedAfterReturn"], row)
                self.assertEqual(row["trackingCount"], 1, row)

    def test_outer_glow_preserves_neighbors_hit_testing_and_scroll_viewport(self):
        preview = os.environ.get("CODEX_CONTROL_FEEDBACK_DENSE_PREVIEW")
        if preview:
            self.run_case("dense", preview)
        for row in self.run_case("boundaries")["rows"]:
            with self.subTest(theme=row["theme"], kind=row["kind"]):
                self.assertGreater(row["neighborEdgeChanges"], 0, row)
                self.assertEqual(row["neighborCoreChanges"], 0, row)
                self.assertGreater(row["edgeProfile"][1], 0, row)
                self.assertGreater(row["boundaryRatio"], 0.25, row)
                self.assertTrue(row["hitTestingPreserved"], row)
                self.assertTrue(row["glowNoninteractive"], row)
                self.assertTrue(row["viewportHoverVisible"], row)
                self.assertEqual(row["viewportOutsideChanges"], 0, row)
                self.assertEqual(row["ancestorHiddenGlows"], 0, row)
                self.assertEqual(row["glowsBeforeScroll"], 1, row)
                self.assertEqual(row["scrollAwayGlows"], 0, row)
