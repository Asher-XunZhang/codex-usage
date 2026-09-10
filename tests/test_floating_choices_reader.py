"""Exercise the actual transient native reader without starting an app or UI."""
import json
import os
from pathlib import Path
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from test_floating_period import declaration


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class FloatingChoicesReaderTests(unittest.TestCase):
    def test_native_large_reply_replacement_cancel_and_callback_release(self):
        source = Path(__file__).parents[1] / 'Sources'
        reader = declaration((source / 'Runtime.swift').read_text(), 'final class FloatingChoicesReader')
        summary_reader = declaration((source / 'Runtime.swift').read_text(), 'final class NativeSummaryReader')
        query = declaration((source / 'Main.swift').read_text(), 'struct FloatingUsageQuery:')
        with tempfile.TemporaryDirectory(prefix='floating choices lifecycle ') as temporary:
            root = Path(temporary)
            cache = root / 'synthetic.sqlite'
            # More than a pipe buffer: termination-before-read would deadlock if
            # the reader retained the old synchronous Pipe implementation.
            tasks = [{'id': f'task-{i:04d}', 'label': '同名中文任务' * 20} for i in range(1500)]
            with sqlite3.connect(cache) as db:
                db.execute('CREATE TABLE kv(key TEXT PRIMARY KEY, value TEXT)')
                db.execute('CREATE TABLE daily(task TEXT,model TEXT,date TEXT,timestamp TEXT,input_tokens INTEGER DEFAULT 0,output_tokens INTEGER DEFAULT 0,total_tokens INTEGER DEFAULT 0,cached_input_tokens INTEGER DEFAULT 0,reasoning_output_tokens INTEGER DEFAULT 0,cache_write_input_tokens INTEGER DEFAULT 0,requests INTEGER DEFAULT 1)')
                db.execute('INSERT INTO kv VALUES (?,?)', ('snapshot', json.dumps({'generated_at': '2020-01-01', 'has_rows': True, 'models': ['model-a'], 'tasks': tasks}, ensure_ascii=False)))
                db.executemany('INSERT INTO daily(task,model,date,timestamp) VALUES (?,?,?,?)', [(t['id'], 'model-a', '2020-01-01', '2020-01-01T12:00:00') for t in tasks])
            swift = root / 'main.swift'
            swift.write_text('import Foundation\ntypealias Object = [String: Any]\nlet usageMacOSURL = Bundle.main.executableURL!.deletingLastPathComponent()\n' + query + reader + summary_reader + r'''
func check(_ condition: @autoclosure () -> Bool, _ reason: String) { precondition(condition(), reason) }
func pump(until ready: () -> Bool) {
    let deadline = Date().addingTimeInterval(6)
    while !ready() && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.01)) }
    check(ready(), "Native reader must finish promptly")
    RunLoop.main.run(until: Date().addingTimeInterval(0.03))
}
final class Marker { let id = 42 }
let query = FloatingUsageQuery(days: "all", model: "model-a", task: "all")
let cache = URL(fileURLWithPath: CommandLine.arguments[1])
let reader = FloatingChoicesReader()
var obsoleteCalls = 0, currentCalls = 0
reader.load(cache: cache, query: query, kind: "task") { choices, error in
    obsoleteCalls += 1
    check(choices == nil && error == nil, "Replaced request must be cancelled, not published")
}
var marker: Marker? = Marker()
weak var weakMarker = marker
reader.load(cache: cache, query: query, kind: "task") { [held = marker!] choices, error in
    currentCalls += 1
    check(held.id == 42 && error == nil, "Valid reader callback should complete")
    check(choices?.count == 1501, "The entire large choice response must arrive")
    let matching = choices!.filter { $0.0 == "task-0001" || $0.0 == "task-0002" }
    check(matching.count == 2 && matching[0].1 != matching[1].1, "Equal task labels must include their stable IDs")
}
marker = nil
pump { currentCalls == 1 }
check(obsoleteCalls == 1 && currentCalls == 1, "Each request completes only once across replacement")
check(weakMarker == nil, "Reader must release callback captures after delivery")
var cancelledCalls = 0
reader.load(cache: cache, query: query, kind: "task") { choices, error in
    cancelledCalls += 1; check(choices == nil && error == nil, "Cancel cannot deliver stale choices")
}
reader.cancel(); reader.cancel()
RunLoop.main.run(until: Date().addingTimeInterval(0.5))
check(cancelledCalls == 1, "Repeated cancellation remains idempotent")
var modelCalls = 0
reader.load(cache: cache, query: query, kind: "model") { choices, error in
    modelCalls += 1
    check(error == nil && choices?.map { $0.0 } == ["all", "model-a"], "The reader works after cancellation")
}
pump { modelCalls == 1 }
let summaries = NativeSummaryReader()
var staleSummaries = 0, summaryCalls = 0, missingCalls = 0
summaries.read(cache: cache, query: query, seconds: 0) { _ in staleSummaries += 1 }
summaries.read(cache: cache, query: query, seconds: 0) { result in
    summaryCalls += 1
    guard case .success(let payload) = result, let filtered = payload["filtered"] as? Object, let summary = filtered["summary"] as? Object else { fatalError("Native cached summary must succeed without an indexer") }
    check(summary["requests"] as? Int == 1500 && payload["today"] is Object, "Native reader returns both independent compact summaries")
}
pump { summaryCalls == 1 }
check(staleSummaries == 0, "Replaced native summary must not publish")
let missing = cache.deletingLastPathComponent().appendingPathComponent("absent.sqlite")
summaries.read(cache: missing, query: query, seconds: 0) { result in
    missingCalls += 1; guard case .failure = result else { fatalError("Missing cache must not fabricate usage") }
}
pump { missingCalls == 1 }
check(!FileManager.default.fileExists(atPath: missing.path), "Native summary never creates a missing database")
let leftovers = try! FileManager.default.contentsOfDirectory(atPath: FileManager.default.temporaryDirectory.path).filter { $0.hasPrefix("codex-usage-choices-") || $0.hasPrefix("codex-usage-summary-") }
check(leftovers.isEmpty, "Completed and cancelled requests remove their temporary output")
print("native reader lifecycle passed")
''')
            built = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(swift), '-o', str(root / 'check')], capture_output=True, text=True, timeout=90)
            self.assertEqual(built.returncode, 0, built.stderr)
            compiled = subprocess.run(['xcrun', 'clang', '-Os', str(source / 'Summary.c'), '-lsqlite3', '-o', str(root / 'CodexSummary')], capture_output=True, text=True, timeout=30)
            self.assertEqual(compiled.returncode, 0, compiled.stderr)
            temp = root / 'reader-temp'
            temp.mkdir()
            ran = subprocess.run([str(root / 'check'), str(cache)], env={**os.environ, 'TMPDIR': str(temp)}, capture_output=True, text=True, timeout=15)
            self.assertEqual(ran.returncode, 0, ran.stderr)
            self.assertIn('native reader lifecycle passed', ran.stdout)


if __name__ == '__main__':
    unittest.main()
