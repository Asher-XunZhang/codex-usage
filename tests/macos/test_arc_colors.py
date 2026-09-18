"""Real AppKit color editing, persistence, and collapsed-only rendering."""
from pathlib import Path
import os
import shutil
import subprocess
import sys
import tempfile
import unittest

from tools.common.paths import macos_source
from tests.macos.test_capsule_controller_interaction import declaration


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS AppKit')
class ArcColorTests(unittest.TestCase):
    def test_embedded_preview_matches_floating_surface(self):
        harness = r'''import AppKit
let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
func descendants(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap(descendants) }
func pixels(_ surface: NSView) -> Data {
    let bitmap = surface.bitmapImageRepForCachingDisplay(in: surface.bounds)!
    surface.cacheDisplay(in: surface.bounds, to: bitmap)
    return Data(bytes: bitmap.bitmapData!, count: bitmap.bytesPerRow * bitmap.pixelsHigh)
}
for theme in CapsuleTheme.allCases { for fraction: CGFloat in [0, 0.09, 0.79, 1] {
    let editor = ArcColorEditor(style: CapsuleArcStyle(), theme: theme, fraction: fraction)
    let embedded = descendants(editor.window!.contentView!).compactMap { $0 as? CapsuleSurface }.first!
    let state = CapsuleState(); state.theme = theme; state.quotaFraction = Double(fraction)
    let standalone = CapsuleSurface(state: state)
    let panel = NSPanel(contentRect: NSRect(origin: NSPoint(x: 400, y: 300), size: CapsuleSurface.small), styleMask: .borderless, backing: .buffered, defer: false)
    panel.isReleasedWhenClosed = false; panel.contentView = standalone
    if CommandLine.arguments[1] == "shadow" {
        precondition(editor.window!.hasShadow, "Embedding a collapsed preview must preserve the editor window shadow")
        precondition(!panel.hasShadow, "The real collapsed floating panel remains shadowless")
    } else {
        let reference = pixels(standalone)
        for origin in [NSPoint(x: 50, y: 80), NSPoint(x: 480, y: 240)] {
            editor.window!.setFrameOrigin(origin)
            precondition(pixels(embedded) == reference, "Embedded percentage must match the standalone orb at every window origin, theme and digit width")
        }
    }
    editor.close(); panel.close()
} }
print("embedded preview passed")
'''
        with tempfile.TemporaryDirectory(prefix='arc-preview-layout-') as directory:
            main = Path(directory) / 'main.swift'; main.write_text(harness)
            binary = Path(directory) / 'check'
            built = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main),
                                    str(macos_source('Capsule.swift')), str(macos_source('ArcColorEditor.swift')),
                                    '-o', str(binary)], capture_output=True, text=True, timeout=120)
            self.assertEqual(built.returncode, 0, built.stderr)
            for case in ['layout', 'shadow']:
                with self.subTest(case=case):
                    checked = subprocess.run([str(binary), case], capture_output=True, text=True, timeout=30)
                    self.assertEqual(checked.returncode, 0, checked.stdout + checked.stderr)

    def test_host_single_window_and_late_close(self):
        method = declaration(macos_source('Main.swift').read_text(), '    @objc func showArcColors()')
        harness = r'''import AppKit
let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
var isMainWindowProcess = false
let suite = "arc-host-test-" + UUID().uuidString
let usagePreferences = UserDefaults(suiteName: suite)!
defer { usagePreferences.removePersistentDomain(forName: suite) }
final class Window { var isVisible = false }
final class ArcColorEditor {
    static var created = 0
    var window: Window? = Window()
    var preview: ((CapsuleArcStyle?) -> Void)?
    var save: ((CapsuleArcStyle) -> String?)?
    var closed: (() -> Void)?
    var draft: CapsuleArcStyle
    init(style: CapsuleArcStyle, theme: CapsuleTheme, fraction: CGFloat?, warning: String?) { draft = style; Self.created += 1 }
    func showWindow(_ sender: Any?) { window?.isVisible = true }
}
final class Host: NSObject {
    var arcColorEditor: ArcColorEditor?
    let capsuleState = CapsuleState()
    var terminating = false, sent: [String] = []
    func sendHost(_ action: String) { sent.append(action) }
''' + method + r'''
}
let host = Host()
isMainWindowProcess = true; host.showArcColors()
precondition(host.sent == ["arcColors"] && host.arcColorEditor == nil)
isMainWindowProcess = false; host.showArcColors()
let first = host.arcColorEditor!
first.draft.lowHex = "#FF0000"; first.draft.highHex = "#0000FF"; first.preview?(first.draft)
host.showArcColors()
precondition(ArcColorEditor.created == 1 && host.arcColorEditor === first && first.draft.lowHex == "#FF0000")
precondition(host.capsuleState.arcStylePreview == first.draft && host.capsuleState.arcStyle == CapsuleArcStyle())
precondition(first.save?(first.draft) == nil && host.capsuleState.arcStyle == first.draft)
first.preview?(nil); first.window?.isVisible = false; first.closed?()
host.showArcColors(); let replacement = host.arcColorEditor!
precondition(replacement !== first && replacement.draft == host.capsuleState.arcStyle)
RunLoop.main.run(until: Date().addingTimeInterval(0.02))
precondition(host.arcColorEditor === replacement, "An old close cannot clear a reopened editor")
host.terminating = true
precondition(replacement.save?(CapsuleArcStyle()) != nil && host.capsuleState.arcStyle == first.draft)
print("host ownership passed")
'''
        with tempfile.TemporaryDirectory(prefix='arc-host-tests-') as directory:
            main = Path(directory) / 'main.swift'; main.write_text(harness)
            binary = Path(directory) / 'check'
            built = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), str(macos_source('Capsule.swift')), '-o', str(binary)], capture_output=True, text=True, timeout=90)
            self.assertEqual(built.returncode, 0, built.stderr)
            checked = subprocess.run([str(binary)], capture_output=True, text=True, timeout=15)
            self.assertEqual(checked.returncode, 0, checked.stdout + checked.stderr)

    def test_palette_editor_and_persistence(self):
        harness = r'''import AppKit
let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
let suite = "arc-colors-test-" + UUID().uuidString
let defaults = UserDefaults(suiteName: suite)!
defer { defaults.removePersistentDomain(forName: suite) }
func equal(_ a: NSColor, _ b: NSColor) -> Bool { CapsuleArcStyle.hex(a) == CapsuleArcStyle.hex(b) }
var style = CapsuleArcStyle()
precondition(CapsuleArcStyle.load(defaults).style == style)
for theme in CapsuleTheme.allCases {
    for value in [CGFloat.nan, -1, 0, 0.05, 0.10, 0.25, 0.50, 0.75, 1, 2] {
        precondition(equal(style.color(for: value, theme: theme), CapsuleQuotaColors.color(for: value, theme: theme)))
    }
    style.mode = .solid
    for value: CGFloat in [0, 0.5, 1] { precondition(equal(style.color(for: value, theme: theme), CapsuleQuotaColors.color(for: 1, theme: theme))) }
    style.mode = .gradient
}
style.setColor(CapsuleArcStyle.parse("#FF0000")!, high: false, theme: .dark)
precondition(style.highHex == "#35DE94", "Editing a default materializes the opposite endpoint")
style.setColor(CapsuleArcStyle.parse("#0000FF")!, high: true, theme: .dark)
precondition(CapsuleArcStyle.hex(style.color(for: 0.5, theme: .dark)) == "#800080")
precondition(CapsuleArcStyle.hex(style.color(for: 0, theme: .dark)) == "#FF0000")
precondition(CapsuleArcStyle.hex(style.color(for: 1, theme: .light)) == "#0000FF")
style.mode = .solid; style.solidHex = "#123456"; style.resetColors()
precondition(style.solidHex == nil && style.lowHex == "#FF0000", "Reset only changes the selected mode")
style.mode = .gradient
precondition(style.save(defaults) == nil && CapsuleArcStyle.load(UserDefaults(suiteName: suite)!).style == style)
let malformed = Data("{broken".utf8); defaults.set(malformed, forKey: CapsuleArcStyle.preferenceKey)
precondition(CapsuleArcStyle.load(defaults).error != nil && defaults.data(forKey: CapsuleArcStyle.preferenceKey) == malformed)
var invalid = style; invalid.lowHex = "#ZZZZZZ"
precondition(invalid.save(defaults) != nil && defaults.data(forKey: CapsuleArcStyle.preferenceKey) == malformed)
precondition(style.save(defaults) == nil)

func descendants(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap(descendants) }
func control<T: NSControl>(_ editor: ArcColorEditor, _ id: String, _ type: T.Type) -> T {
    descendants(editor.window!.contentView!).first { $0.identifier?.rawValue == id } as! T
}
func click(_ editor: ArcColorEditor, _ id: String) {
    let c = control(editor, id, NSButton.self); precondition(c.isEnabled); _ = c.sendAction(c.action, to: c.target)
}
func mode(_ editor: ArcColorEditor, _ index: Int) {
    let popup = control(editor, "arcMode", NSPopUpButton.self); popup.selectItem(at: index); _ = popup.sendAction(popup.action, to: popup.target)
}
func edit(_ editor: ArcColorEditor, _ id: String, _ text: String) {
    let field = control(editor, id, NSTextField.self); field.stringValue = text
    editor.controlTextDidChange(Notification(name: NSControl.textDidChangeNotification, object: field))
}
let editor = ArcColorEditor(style: style, theme: .light, fraction: 0.5)
let quotaSlider = descendants(editor.window!.contentView!).compactMap { $0 as? NSSlider }.first { $0.accessibilityLabel() == "预览剩余额度" }!
quotaSlider.doubleValue = 79.6; _ = quotaSlider.sendAction(quotaSlider.action, to: quotaSlider.target)
let previewSurface = descendants(editor.window!.contentView!).compactMap { $0 as? CapsuleSurface }.first!
precondition(quotaSlider.doubleValue == 80 && previewSurface.state.normalizedQuota == 0.8, "Fractional slider input must not show 80% beside a 79% orb")
var previews: [CapsuleArcStyle?] = [], closes = 0, attempts = 0
editor.preview = { previews.append($0) }; editor.closed = { closes += 1 }
editor.save = { _ in attempts += 1; return "fixture write failure" }
precondition(control(editor, "arcMode", NSPopUpButton.self).numberOfItems == 2)
let windowCount = app.windows.count
click(editor, "arcHigh")
let wheel = control(editor, "arcWheel", ArcColorWheel.self)
let beforeKey = editor.draft
let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                            windowNumber: editor.window!.windowNumber, context: nil, characters: "", charactersIgnoringModifiers: "", isARepeat: false, keyCode: 123)!
wheel.keyDown(with: event)
precondition(editor.draft.lowHex == beforeKey.lowHex && editor.draft.highHex != beforeKey.highHex)
wheel.setBrightness(1)
func mouse(_ type: NSEvent.EventType, at point: NSPoint) -> NSEvent {
    NSEvent.mouseEvent(with: type, location: wheel.convert(point, to: nil), modifierFlags: [], timestamp: 0,
                      windowNumber: editor.window!.windowNumber, context: nil, eventNumber: 1, clickCount: 1, pressure: 1)!
}
wheel.mouseDown(with: mouse(.leftMouseDown, at: NSPoint(x: wheel.bounds.maxX, y: wheel.bounds.midY)))
precondition(editor.draft.highHex == "#FF0000" && editor.draft.lowHex == beforeKey.lowHex)
wheel.mouseDragged(with: mouse(.leftMouseDragged, at: NSPoint(x: 0, y: wheel.bounds.midY)))
precondition(editor.draft.highHex == "#00FFFF", "Mouse drag continuously updates the selected endpoint")
precondition(app.windows.count == windowCount, "Endpoint and wheel interactions do not create color windows")
edit(editor, "arcLowHex", "invalid")
precondition(!control(editor, "arcApply", NSButton.self).isEnabled)
edit(editor, "arcLowHex", "AA0000")
precondition(editor.draft.lowHex == "#AA0000" && control(editor, "arcApply", NSButton.self).isEnabled)
let gradient = editor.draft
mode(editor, 0)
precondition(CapsuleArcStyle.hex(editor.draft.color(for: 0.4, theme: .light)) == "#009E68")
edit(editor, "arcLowHex", "#112233")
mode(editor, 1)
precondition(editor.draft.lowHex == gradient.lowHex && editor.draft.highHex == gradient.highHex)
mode(editor, 0); precondition(editor.draft.solidHex == "#112233")
click(editor, "arcReset"); precondition(editor.draft.solidHex == nil)
editor.updateTheme(.dark)
precondition(control(editor, "arcLowHex", NSTextField.self).stringValue == "#35DE94")
mode(editor, 1); click(editor, "arcReset"); precondition(editor.draft.isBuiltinGradient)
edit(editor, "arcLowHex", "#123456"); let failedDraft = editor.draft
click(editor, "arcApply")
precondition(attempts == 1 && closes == 0 && editor.draft == failedDraft && CapsuleArcStyle.load(defaults).style == style)
editor.updateTheme(.light)
precondition(descendants(editor.window!.contentView!).compactMap { $0 as? NSTextField }.contains { $0.stringValue == "fixture write failure" }, "Appearance changes retain the failed-save message")
editor.save = { $0.save(defaults) }
click(editor, "arcApply")
precondition(closes == 1 && previews.last! == nil && CapsuleArcStyle.load(defaults).style == failedDraft)
let canceled = ArcColorEditor(style: failedDraft, theme: .dark, fraction: nil)
var canceledPreview: CapsuleArcStyle? = failedDraft
canceled.preview = { canceledPreview = $0 }; canceled.save = { _ in fatalError("Cancel must not save") }
edit(canceled, "arcLowHex", "#FFFFFF")
let escape = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0,
                             windowNumber: canceled.window!.windowNumber, context: nil, characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53)!
precondition(canceled.window!.performKeyEquivalent(with: escape), "Escape invokes the cancel button")
precondition(canceledPreview == nil && CapsuleArcStyle.load(defaults).style == failedDraft)

func pixels(_ surface: NSView) -> Data {
    let bitmap = surface.bitmapImageRepForCachingDisplay(in: surface.bounds)!
    surface.cacheDisplay(in: surface.bounds, to: bitmap)
    return Data(bytes: bitmap.bitmapData!, count: bitmap.bytesPerRow * bitmap.pixelsHigh)
}
let state = CapsuleState(); state.quotaFraction = 0.5
let surface = CapsuleSurface(state: state)
let panel = NSPanel(contentRect: surface.frame, styleMask: .borderless, backing: .buffered, defer: true)
panel.contentView = surface
let compact = pixels(surface); state.arcStyle = style
precondition(pixels(surface) != compact, "The real collapsed surface uses the custom style")
state.arcStylePreview = CapsuleArcStyle(); precondition(pixels(surface) == compact, "Live preview overrides saved style")
state.arcStylePreview = nil; precondition(pixels(surface) != compact)
panel.setContentSize(CapsuleSurface.large); surface.expansion = 1
let expanded = pixels(surface); state.arcStyle = CapsuleArcStyle()
precondition(pixels(surface) == expanded, "Expanded battery colors are outside the arc setting")
surface.expansion = 0; panel.setContentSize(CapsuleSurface.small); state.quotaFraction = nil
let unknown = pixels(surface); state.arcStyle = style
precondition(pixels(surface) == unknown, "Unavailable quota must not draw a custom arc")

if let output = ProcessInfo.processInfo.environment["CODEX_USAGE_ARC_PREVIEW_DIR"] {
    for theme in CapsuleTheme.allCases {
        let sample = ArcColorEditor(style: CapsuleArcStyle(), theme: theme, fraction: 0.68)
        let root = sample.window!.contentView!
        let image = descendants(root).first { $0.accessibilityLabel() == "折叠浮窗预览" }!
        precondition(image.accessibilityChildren()?.isEmpty == true, "Preview must not expose inactive floating actions")
        for id in ["arcApply", "arcCancel", "arcReset", "arcMode", "arcLowHex", "arcHighHex", "arcWheel"] {
            let view = descendants(root).first { $0.identifier?.rawValue == id }!
            precondition(root.bounds.contains(view.convert(view.bounds, to: root)), "Visible control bounds: " + id)
        }
        sample.window!.appearance!.performAsCurrentDrawingAppearance {
            let bitmap = root.bitmapImageRepForCachingDisplay(in: root.bounds)!
            root.cacheDisplay(in: root.bounds, to: bitmap)
            try! bitmap.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: output).appendingPathComponent("arc-editor-" + theme.rawValue + ".png"))
        }
        sample.close()
    }
}
precondition(!editor.window!.isVisible)
print("arc model, native editor, persistence, rollback and rendering passed")
'''
        with tempfile.TemporaryDirectory(prefix='arc-color-tests-') as directory:
            root = Path(directory)
            main = root / 'main.swift'; main.write_text(harness)
            binary = root / 'check'
            built = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main),
                                    str(macos_source('Capsule.swift')), str(macos_source('ArcColorEditor.swift')),
                                    '-o', str(binary)], capture_output=True, text=True, timeout=120)
            self.assertEqual(built.returncode, 0, built.stderr)
            checked = subprocess.run([str(binary)], capture_output=True, text=True, timeout=30, env=os.environ.copy())
            self.assertEqual(checked.returncode, 0, checked.stdout + checked.stderr)
            self.assertIn('rendering passed', checked.stdout)


if __name__ == '__main__':
    unittest.main()
