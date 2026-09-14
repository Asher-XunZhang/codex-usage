"""A hidden usage page must acknowledge a refreshed summary without rendering it."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from tools.common.paths import macos_source
from tests.macos.test_window_refresh_bridge import declaration


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class CompactRefreshReceiptTests(unittest.TestCase):
    def test_summary_completes_refresh_without_usage_or_floating_view(self):
        source = macos_source('Main.swift').read_text()
        methods = ''.join(declaration(source, marker) for marker in (
            '    func loadCompact()', '    func refreshPresented('))
        harness = r'''import Foundation
typealias Object = [String: Any]
func exact(_ value: Any?) -> String { String(describing: value ?? 0) }
final class Request { func resume() {} }
final class Session {
    var reply: ((Data?, URLResponse?, Error?) -> Void)?
    func dataTask(with url: URL, completionHandler: @escaping (Data?, URLResponse?, Error?) -> Void) -> Request {
        reply = completionHandler; return Request()
    }
    func complete(stamp: String, status: Int = 200) {
        let data = try! JSONSerialization.data(withJSONObject: ["meta": ["generated_at": stamp], "summary": ["total_tokens": 120]])
        let response = HTTPURLResponse(url: URL(string: "http://127.0.0.1/fixture")!, statusCode: status, httpVersion: nil, headerFields: nil)!
        let callback = reply; reply = nil; callback?(data, response, nil)
        RunLoop.main.run(until: Date().addingTimeInterval(0.02))
    }
}
final class Backend {
    let url: URL? = URL(string: "http://127.0.0.1/fixture")
    var generation = UUID()
    let root = URL(fileURLWithPath: CommandLine.arguments[1])
}
final class State { var status = "", indicator = "" }
final class Button { var toolTip: String? }
final class Item { var button: Button? = Button() }
final class Fixture {
    var compactMode = false, terminating = false
    let backend = Backend(), session = Session(), capsuleState = State()
    var summaryRequest: Request?, summaryRequestID = 0
    var todaySnapshot: Object = [:], compactStamp: String?, publishedUsageStamp: String?
    var statusItem: Item?, intervalDescription = "paused", statusText = ""
    var pendingRefresh: Int?, manualBeganAt: Date?, manualExpectedStamp: String?
    var receipts: [String] = []
    func sendHost(_ action: String) {}
    func updateStatusTitle() {}
    func fetchCompact(manual: Bool) {}
    // The helper has no floating window, and its usage page is hidden.
    func loadFloating() {}
    func reportHelperRefresh(success: Bool, message: String) {
        precondition(success)
        receipts.append(manualExpectedStamp ?? "missing timestamp")
    }
''' + methods + r'''
}
let f = Fixture()
let required = "2026-09-14T10:00:00+08:00", newer = "2026-09-14T10:00:01+08:00"
f.manualBeganAt = Date(); f.manualExpectedStamp = required
f.loadCompact(); f.session.complete(stamp: required, status: 500)
precondition(f.receipts.isEmpty && f.manualBeganAt != nil, "A failed read cannot acknowledge refresh")
f.loadCompact(); f.session.complete(stamp: "2026-09-14T09:59:59+08:00")
precondition(f.receipts.isEmpty && f.manualBeganAt != nil, "An older snapshot cannot acknowledge refresh")
f.loadCompact(); f.session.complete(stamp: newer)
precondition(f.receipts == [newer], "A matching summary must acknowledge while usage/floating are hidden, preserving its timestamp")
precondition(f.manualBeganAt == nil && f.manualExpectedStamp == nil)
f.loadCompact(); f.session.complete(stamp: newer)
precondition(f.receipts.count == 1, "Repeated publication cannot duplicate the receipt")
f.manualBeganAt = Date(); f.manualExpectedStamp = newer
f.loadCompact(); f.backend.generation = UUID(); f.session.complete(stamp: newer)
precondition(f.receipts.count == 1, "An old backend's response cannot finish a new refresh")
print("hidden-page refresh receipt passed")
'''
        with tempfile.TemporaryDirectory(prefix='summary refresh receipt ') as directory:
            root = Path(directory)
            path = root / 'main.swift'; path.write_text(harness)
            binary = root / 'check'
            build = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(path), '-o', str(binary)],
                                   capture_output=True, text=True, timeout=120)
            self.assertEqual(build.returncode, 0, build.stderr)
            result = subprocess.run([str(binary), str(root)], capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn('hidden-page refresh receipt passed', result.stdout)


if __name__ == '__main__':
    unittest.main()
