"""Use actual OS pipes with production event decoding and disposal."""
from tools.common.paths import macos_source
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

DRIVER = r'''
import Foundation
let pipe = Pipe()
let actual = BackendEventChannel(handle: pipe.fileHandleForReading)
var events: [[String: Any]] = [], failures = 0
actual.event = { events.append($0) }; actual.failed = { failures += 1 }; actual.start()
func pump() { RunLoop.main.run(until: Date().addingTimeInterval(0.06)) }
func write(_ data: Data) { try! pipe.fileHandleForWriting.write(contentsOf: data); pump() }
func awaitCallback(_ condition: () -> Bool) {
    let deadline = Date().addingTimeInterval(2)
    while !condition() && Date() < deadline {
        RunLoop.main.run(until: Date().addingTimeInterval(0.01))
    }
    precondition(condition(), "Expected pipe callback did not arrive before deadline")
}
let value = Data("{\"protocol\":1,\"event\":\"desktop_state\",\"refresh_completed\":7}\n".utf8)
switch CommandLine.arguments[1] {
case "partial":
    write(value.prefix(19)); precondition(events.isEmpty)
    write(value.dropFirst(19)); awaitCallback { !events.isEmpty || failures > 0 }
    precondition(events.count == 1 && events[0]["refresh_completed"] as? Int == 7 && failures == 0)
    try! pipe.fileHandleForWriting.close(); awaitCallback { failures > 0 }; precondition(failures == 1)
case "oversize":
    write(Data(repeating: 120, count: 5000)); awaitCallback { failures > 0 }; precondition(events.isEmpty && failures == 1)
case "invalid":
    write(Data("{\"protocol\":99,\"event\":\"desktop_state\"}\n".utf8)); awaitCallback { failures > 0 }; precondition(events.isEmpty && failures == 1)
case "close":
    actual.close(); pump(); precondition(failures == 0 && events.isEmpty)
default: fatalError()
}
actual.close(); pump()
print("passed")
'''


@unittest.skipUnless(shutil.which('xcrun'), 'Requires macOS Swift')
class BackendEventsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='backend event pipe '); cls.addClassCleanup(cls.directory.cleanup)
        main = Path(cls.directory.name) / 'main.swift'; main.write_text(DRIVER); cls.binary = main.with_name('events')
        result = subprocess.run(['xcrun', 'swiftc', str(macos_source('BackendEvents.swift')), str(main), '-o', str(cls.binary)], capture_output=True, text=True, timeout=90)
        if result.returncode: raise AssertionError(result.stderr)

    def test_partial_record_eof_and_bounded_protocol(self):
        for case in ['partial', 'oversize', 'invalid', 'close']:
            with self.subTest(case=case):
                result = subprocess.run([str(self.binary), case], capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn('passed', result.stdout)
