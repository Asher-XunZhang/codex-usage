"""Exercise the production reader with real Swift/C processes and synthetic SQLite.

Only BudgetQueryReader is extracted: no notification center is instantiated,
permissions are never requested, and no installed application is launched.
"""
from tools.common.paths import ROOT, BACKEND, macos_source
from datetime import datetime
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from disk_index import DiskUsageIndex
from tests.common.test_token_usage import meta, start, record


HARNESS = r'''
let arguments = CommandLine.arguments
let usageMacOSURL = URL(fileURLWithPath: arguments[1], isDirectory: true)
let cache = URL(fileURLWithPath: arguments[2])
let missing = cache.deletingLastPathComponent().appendingPathComponent("missing.sqlite")
let input = try Data(contentsOf: URL(fileURLWithPath: arguments[3]))
let requests = (try JSONSerialization.jsonObject(with: input) as! Object)["requests"] as! [Object]
let mode = arguments[4]
let reader = BudgetQueryReader()
var callbacks: [Object] = []
var cancelledCallbacks = 0
var finishing = false

func finish() {
    guard !finishing else { return }
    finishing = true
    // Return through the current completion/defer before checking the outcome.
    DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) {
        reader.cancel()
        let output: Object = ["callbacks": callbacks, "cancelledCallbacks": cancelledCallbacks]
        FileHandle.standardOutput.write(try! JSONSerialization.data(withJSONObject: output))
        exit(0)
    }
}
func append(_ result: Object?, _ error: String?) {
    callbacks.append(["result": result as Any? ?? NSNull(), "error": error as Any? ?? NSNull()])
}
func readNext(_ index: Int) {
    reader.read(cache: mode == "recover" && index == 0 ? missing : cache, requests: requests) { result, error in
        append(result, error)
        if index < 2 {
            // Intentional synchronous reentry from the completion. Coordinator's
            // queued refresh takes this same path; cleanup must belong to the
            // old request, not to this newly started child and its temp files.
            readNext(index + 1)
        } else { finish() }
    }
}
if mode == "cancel" {
    reader.read(cache: cache, requests: requests) { _, _ in cancelledCallbacks += 1 }
    reader.read(cache: cache, requests: requests) { result, error in
        append(result, error); finish()
    }
} else { readNext(0) }
DispatchQueue.main.asyncAfter(deadline: .now() + 15) {
    FileHandle.standardError.write(Data("Reader harness timed out".utf8))
    reader.cancel(); exit(2)
}
RunLoop.main.run()
'''


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS Swift/C toolchain')
class BudgetHostReaderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory(prefix='budget reader build ')
        folder = Path(cls.build.name)
        source = (macos_source('BudgetHost.swift')).read_text()
        start_at = source.index('final class BudgetQueryReader {')
        end_at = source.index('/// Host-owned store', start_at)
        reader = source[start_at:end_at]
        harness = folder / 'main.swift'
        harness.write_text('import AppKit\ntypealias Object = [String: Any]\n' + reader + HARNESS)
        cls.binary = folder / 'ReaderHarness'
        for command in [
            ['xcrun', 'clang', '-Os', str(macos_source('Summary.c')), '-lsqlite3', '-o', str(folder / 'CodexSummary')],
            ['xcrun', 'swiftc', '-O', str(harness), '-o', str(cls.binary)],
        ]:
            result = subprocess.run(command, capture_output=True, text=True)
            if result.returncode:
                raise RuntimeError(result.stderr)

    @classmethod
    def tearDownClass(cls):
        cls.build.cleanup()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='budget reader fixture ')
        self.home = Path(self.temp.name)
        sessions = self.home / 'sessions'
        sessions.mkdir()
        rows = [meta('fixture-root'), *start('fixture-turn'), record('fixture-root', 'fixture-turn', 'fixture-response')]
        (sessions / 'fixture.jsonl').write_text(''.join(json.dumps(row) + '\n' for row in rows))
        self.index = DiskUsageIndex(self.home, self.home / 'index.sqlite')
        self.index.scan()
        # All callbacks must be satisfied by the index. The reader cannot rely
        # on the source logs or an incidental Python refresh.
        shutil.rmtree(sessions)
        self.request = dict(id='fixture-budget', revision=1, periodID='fixture-day',
                            start=datetime.fromisoformat('2026-09-10T00:00:00+00:00').timestamp(),
                            end=datetime.fromisoformat('2026-09-11T00:00:00+00:00').timestamp(),
                            model='all', task='all')
        self.requests = self.home / 'requests.json'
        self.requests.write_text(json.dumps({'requests': [self.request]}))

    def tearDown(self):
        self.temp.cleanup()

    def run_harness(self, mode):
        result = subprocess.run([str(self.binary), self.build.name, str(self.index.cache), str(self.requests), mode],
                                capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def assert_success(self, callback):
        self.assertIsNone(callback['error'])
        rows = callback['result']['results']
        self.assertEqual(rows[0]['id'], self.request['id'])
        self.assertEqual(rows[0]['rows'][0]['total_tokens'], 120)
        self.assertEqual(rows[0]['rows'][0]['requests'], 1)

    def test_reentrant_three_successful_queries_keep_each_new_output_alive(self):
        result = self.run_harness('chain')
        self.assertEqual(len(result['callbacks']), 3)
        for callback in result['callbacks']:
            self.assert_success(callback)
        self.assertEqual(result['cancelledCallbacks'], 0)

    def test_error_completion_can_reenter_and_recover_without_deleting_new_files(self):
        result = self.run_harness('recover')
        self.assertEqual(len(result['callbacks']), 3)
        self.assertIsNone(result['callbacks'][0]['result'])
        self.assertTrue(result['callbacks'][0]['error'])
        for callback in result['callbacks'][1:]:
            self.assert_success(callback)
        self.assertFalse((self.home / 'missing.sqlite').exists())

    def test_replacing_inflight_query_suppresses_old_callback(self):
        result = self.run_harness('cancel')
        self.assertEqual(len(result['callbacks']), 1)
        self.assert_success(result['callbacks'][0])
        self.assertEqual(result['cancelledCallbacks'], 0)


if __name__ == '__main__':
    unittest.main()
