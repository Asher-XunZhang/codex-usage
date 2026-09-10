"""Native refresh editor actions and ownership, with window presentation disabled."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class RefreshIntervalPromptTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='refresh interval prompt ')
        cls.addClassCleanup(cls.directory.cleanup)
        root = Path(cls.directory.name)
        source = Path(__file__).parents[1] / 'Sources/RefreshInterval.swift'
        main = root / 'main.swift'
        main.write_text(r'''import AppKit
let app = NSApplication.shared
app.setActivationPolicy(.prohibited)
func check(_ value: @autoclosure () -> Bool, _ reason: String) { precondition(value(), reason) }
func drain() { autoreleasepool { RunLoop.main.run(until: Date().addingTimeInterval(0.05)) } }
func edit(_ prompt: RefreshIntervalPrompt, _ value: String) {
    prompt.field!.stringValue = value
    prompt.field!.currentEditor()?.string = value
}
func key(_ prompt: RefreshIntervalPrompt, _ code: UInt16, _ chars: String, flags: NSEvent.ModifierFlags = []) -> Bool {
    let panel = prompt.window!
    let event = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: flags, timestamp: 0,
        windowNumber: panel.windowNumber, context: nil, characters: chars, charactersIgnoringModifiers: chars,
        isARepeat: false, keyCode: code)!
    return panel.performKeyEquivalent(with: event)
}
func click(_ prompt: RefreshIntervalPrompt, _ title: String) {
    let button = prompt.window!.contentView!.subviews.compactMap { $0 as? NSButton }.first { $0.title == title }!
    check(NSApp.sendAction(button.action!, to: button.target, from: button), "Dispatch the real native button action")
}
func editor() -> RefreshIntervalPrompt {
    RefreshIntervalPrompt { panel in check(!panel.isVisible, "Tests must not show or activate a window") }
}
switch CommandLine.arguments[1] {
case "values":
    for (text, value) in [("1", 1), ("3600", 3600), (" 17\n", 17), ("005", 5)] {
        check(RefreshIntervalPreference.validatedValue(input: text) == value, "Valid integer input must be accepted")
    }
    for text in ["", " ", "0", "-1", "3601", "1.5", "1e3", "NaN", "abc", "9999999999999999999999999"] {
        check(RefreshIntervalPreference.validatedValue(input: text) == nil, "Invalid input cannot be applied")
    }
    let suite = "local.codex-usage.test-refresh-prompt." + UUID().uuidString
    let prefs = UserDefaults(suiteName: suite)!
    defer { prefs.removePersistentDomain(forName: suite) }
    check(RefreshIntervalPreference.remembered(preferences: prefs, current: 0) == 5, "New user fallback is five seconds")
    prefs.set(17, forKey: RefreshIntervalPreference.lastPositiveKey)
    check(RefreshIntervalPreference.remembered(preferences: prefs, current: 0) == 17, "Paused state restores remembered custom interval")
    check(RefreshIntervalPreference.remembered(preferences: prefs, current: 30) == 30, "Active interval wins over stale memory")
    for invalid: Any in [0, -1, 3601, "broken"] {
        prefs.set(invalid, forKey: RefreshIntervalPreference.lastPositiveKey)
        check(RefreshIntervalPreference.remembered(preferences: prefs, current: 0) == 5, "Corrupt memory must use safe fallback")
    }
case "duplicate-invalid":
    var presentations = 0, results: [Int?] = []
    let prompt = RefreshIntervalPrompt { panel in presentations += 1; check(!panel.isVisible, "No test UI") }
    prompt.completion = { results.append($0) }
    prompt.show(initialSeconds: 17)
    let original = prompt.window!
    edit(prompt, "bad")
    check(key(prompt, 36, "\r"), "Return is handled by the native panel")
    check(prompt.window === original && prompt.errorLabel?.isHidden == false && results.isEmpty, "Invalid Return keeps one window and emits no result")
    check(prompt.field?.stringValue == "bad", "Invalid text must remain editable")
    prompt.show(initialSeconds: 30)
    check(prompt.window === original && prompt.field?.stringValue == "bad" && presentations == 2, "Repeated show focuses without resetting user input")
    edit(prompt, " 23 ")
    click(prompt, "应用")
    check(results.count == 1 && results[0] == 23 && !prompt.isPresented, "Native Apply delivers the corrected value once")
    prompt.cancel(); original.close()
    check(results.count == 1, "Late cancel/close cannot emit duplicate completion")
case "keyboard-close":
    let prompt = editor(); var results: [Int?] = []
    prompt.completion = { results.append($0) }; prompt.show(initialSeconds: 0)
    check(prompt.field?.stringValue == "5", "Invalid starting values use a valid default")
    edit(prompt, "71")
    check(key(prompt, 76, "\r") && results.count == 1 && results[0] == 71, "Keypad Enter applies once")
    prompt.completion = { results.append($0) }; prompt.show(initialSeconds: 71)
    check(key(prompt, 53, "\u{1b}") && results.count == 2 && results[1] == nil, "Escape cancels without a value")
    prompt.completion = { results.append($0) }; prompt.show(initialSeconds: 71)
    click(prompt, "取消")
    check(results.count == 3 && results[2] == nil, "Cancel button follows the same one-shot completion")
    prompt.completion = { results.append($0) }; prompt.show(initialSeconds: 71)
    prompt.window!.performClose(nil)
    check(results.count == 4 && results[3] == nil && !prompt.isPresented, "Window close cancels and releases the editor")
case "release":
    final class Marker {}
    let prompt = editor()
    weak var oldWindow: NSPanel?, oldField: NSTextField?, oldError: NSTextField?, held: Marker?
    var calls = 0
    autoreleasepool {
        let marker = Marker(); held = marker
        prompt.completion = { [marker] value in check(value == 19, "Apply keeps the chosen value"); _ = marker; calls += 1 }
        prompt.show(initialSeconds: 19)
        oldWindow = prompt.window; oldField = prompt.field; oldError = prompt.errorLabel
        click(prompt, "应用")
    }
    drain()
    check(calls == 1 && !prompt.isPresented && prompt.completion == nil, "Completion and presentation state reset together")
    check(prompt.window == nil && prompt.field == nil && prompt.errorLabel == nil, "No prompt-owned control survives completion")
    check(oldWindow == nil && oldField == nil && oldError == nil && held == nil, "Released references: window=\(oldWindow == nil) field=\(oldField == nil) error=\(oldError == nil) callback=\(held == nil)")
    for _ in 0..<30 {
        autoreleasepool {
            prompt.completion = { _ in calls += 1 }; prompt.show(initialSeconds: 9)
            oldWindow = prompt.window; oldField = prompt.field; oldError = prompt.errorLabel
            edit(prompt, "bad"); _ = key(prompt, 36, "\r")
            prompt.cancel()
        }
        drain()
        check(!prompt.isPresented && prompt.field == nil && prompt.completion == nil, "Repeated presentations retain no previous editor")
        check(oldWindow == nil && oldField == nil && oldError == nil, "Invalid input followed by Cancel also releases native controls")
    }
    check(calls == 31, "Each repeated presentation completes exactly once")
case "reentrant":
    let prompt = editor(); var results: [Int?] = []
    prompt.completion = { value in
        results.append(value)
        prompt.completion = { results.append($0) }
        prompt.show(initialSeconds: 27)
    }
    prompt.show(initialSeconds: 11); let first = prompt.window!
    click(prompt, "应用")
    check(prompt.isPresented && prompt.window !== first && prompt.field?.stringValue == "27", "Completion may safely create a new independent presentation")
    first.close()
    check(prompt.isPresented && results.count == 1, "An old window close cannot cancel a replacement")
    prompt.cancel()
    check(results.count == 2 && results[0] == 11 && results[1] == nil, "Replacement has its own completion")
default: fatalError("Unknown test case")
}
print(CommandLine.arguments[1] + " passed")
''')
        cls.binary = root / 'check'
        compiled = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), str(source), '-o', str(cls.binary)],
                                  capture_output=True, text=True, timeout=120)
        if compiled.returncode:
            raise AssertionError(compiled.stderr)

    def run_case(self, name):
        result = subprocess.run([str(self.binary), name], capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(name + ' passed', result.stdout)

    def test_validation_and_remembered_interval(self): self.run_case('values')
    def test_duplicate_show_invalid_input_and_native_apply(self): self.run_case('duplicate-invalid')
    def test_return_escape_cancel_and_close_actions(self): self.run_case('keyboard-close')
    def test_controls_and_completion_captures_are_released(self): self.run_case('release')
    def test_completion_can_reopen_without_old_close_cancelling_it(self): self.run_case('reentrant')


if __name__ == '__main__':
    unittest.main()
