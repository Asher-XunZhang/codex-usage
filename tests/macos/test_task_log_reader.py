"""Exercise the production incremental reader against isolated JSONL files."""
from tools.common.paths import macos_source
from pathlib import Path
import base64
import json
import shutil
import subprocess
import tempfile
import unittest

HARNESS = r'''
import Foundation
let url = URL(fileURLWithPath: CommandLine.arguments[1])
let commands = try JSONSerialization.jsonObject(with: FileHandle.standardInput.readDataToEndOfFile()) as! [MonitorObject]
var reader: TaskLogReader?
var checkpoint: MonitorObject = [:]
var rows: [MonitorObject] = []
for command in commands {
    var events: [MonitorObject] = []
    let op = command["op"] as? String ?? ""
    if op == "write" || op == "append", let encoded = command["data"] as? String, let data = Data(base64Encoded: encoded) {
        if op == "write" { try data.write(to: url, options: [.atomic]) }
        else { let file = try FileHandle(forWritingTo: url); try file.seekToEnd(); try file.write(contentsOf: data); try file.close() }
    }
    if op == "open" || op == "restore" {
        reader = TaskLogReader(url: url, home: "/fixture")
        if op == "restore" { reader?.restore(checkpoint) }
    }
    if op == "read", let reader = reader {
        let limit = command["budget"] as? Int ?? 512 * 1024
        let cycles = command["cycles"] as? Int ?? 1
        for _ in 0..<cycles {
            var budget = limit
            for (turn, offline) in reader.read(budget: &budget, force: command["force"] as? Bool == true) {
                var object = turn.object; object["offline"] = offline; events.append(object)
            }
            precondition(budget >= 0, "Reader exceeded byte budget")
            if !reader.pending { break }
        }
    }
    if op == "checkpoint" { checkpoint = reader?.checkpoint ?? [:] }
    rows.append(["task": reader?.task.object ?? [:], "events": events, "pending": reader?.pending ?? false,
                 "bytes": reader?.bytesRead ?? 0, "backfillBytes": reader?.backfillBytes ?? 0, "checkpoint": checkpoint])
}
FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: rows))
'''


def line(kind, payload):
    return json.dumps(dict(type=kind, timestamp=100, payload=payload), separators=(',', ':')).encode() + b'\n'


def meta(**extra):
    return line('session_meta', dict(id='task-1', cwd='/project', **extra))


def lifecycle(kind='task_started', turn='turn-1', at=100):
    return line('event_msg', dict(type=kind, turn_id=turn, started_at=99, completed_at=at))


def write(data, op='write'):
    return dict(op=op, data=base64.b64encode(data).decode())


@unittest.skipUnless(shutil.which('xcrun'), 'Requires macOS Swift')
class TaskLogReaderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory(prefix='task reader build ')
        cls.addClassCleanup(cls.build.cleanup)
        main = Path(cls.build.name) / 'main.swift'; main.write_text(HARNESS)
        cls.binary = main.with_name('reader')
        result = subprocess.run(['xcrun', 'swiftc', '-O', str(macos_source('TaskMonitorCore.swift')), str(macos_source('TaskLogReader.swift')), str(main), '-o', str(cls.binary)], capture_output=True, text=True, timeout=120)
        if result.returncode: raise AssertionError(result.stderr)

    def run_commands(self, *commands):
        with tempfile.TemporaryDirectory(prefix='task reader fixture ') as folder:
            result = subprocess.run([str(self.binary), str(Path(folder) / 'session.jsonl')], input=json.dumps(commands), capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def test_append_online_and_checkpoint_offline(self):
        rows = self.run_commands(write(meta() + lifecycle()), dict(op='open'), dict(op='read'),
            dict(op='checkpoint'), write(lifecycle('task_complete', at=101), 'append'), dict(op='restore'), dict(op='read'),
            write(lifecycle(turn='turn-2'), 'append'), dict(op='read'))
        self.assertEqual(rows[6]['task']['status'], 'completed')
        self.assertTrue(rows[6]['events'][0]['offline'])
        self.assertFalse(rows[-1]['events'][0]['offline'])
        self.assertEqual(rows[-1]['task']['turnID'], 'turn-2')

    def test_partial_lifecycle_never_finishes_until_newline(self):
        terminal = lifecycle('task_complete', at=101)
        rows = self.run_commands(write(meta() + lifecycle()), dict(op='open'), dict(op='read'),
            write(terminal[:-1], 'append'), dict(op='read'), write(b'\n', 'append'), dict(op='read'))
        self.assertEqual(rows[4]['task']['status'], 'unknown')
        self.assertFalse(rows[4]['events'])
        self.assertEqual(rows[-1]['task']['status'], 'completed')
        self.assertEqual(len(rows[-1]['events']), 1)

    def test_lifecycle_straddles_initial_tail_cut(self):
        start = lifecycle()
        padding = line('response_item', dict(content='x' * (2 * 1024 * 1024 - 128)))
        # The cut falls inside the lifecycle, so neither half is valid alone.
        data = meta() + start + padding
        self.assertGreater(len(data) - 2 * 1024 * 1024, len(meta()))
        self.assertLess(len(data) - 2 * 1024 * 1024, len(meta()) + len(start))
        row = self.run_commands(write(data), dict(op='open'), dict(op='read', cycles=30))[-1]
        self.assertEqual(row['task']['status'], 'running')
        self.assertFalse(row['pending'])
        self.assertGreater(row['backfillBytes'], 0)

    def test_giant_conversation_backfill_bounded_and_private(self):
        data = meta() + lifecycle() + line('response_item', dict(content='PRIVATE-' + 'x' * (17 * 1024 * 1024)))
        rows = self.run_commands(write(data), dict(op='open'), dict(op='read'), dict(op='read', cycles=100), dict(op='checkpoint'))
        self.assertTrue(rows[2]['pending'])
        self.assertLess(rows[2]['bytes'], 513 * 1024)
        self.assertFalse(rows[-1]['pending'])
        self.assertEqual(rows[-1]['task']['status'], 'running')
        self.assertNotIn('PRIVATE-', json.dumps(rows[-1]['checkpoint']))

    def test_rewrite_same_task_resets_prior_turn(self):
        rows = self.run_commands(write(meta() + lifecycle()), dict(op='open'), dict(op='read'),
            write(meta() + lifecycle('task_complete', turn='turn-2', at=102)), dict(op='read', force=True))
        self.assertEqual(rows[-1]['task']['turnID'], 'turn-2')
        self.assertEqual(rows[-1]['task']['status'], 'completed')
        self.assertTrue(rows[-1]['events'][0]['offline'])

    def test_nested_child_is_not_selectable(self):
        row = self.run_commands(write(meta(source=dict(subagent=dict(thread_spawn=dict(parent_thread_id='root-id')))) + lifecycle()), dict(op='open'), dict(op='read'))[-1]
        self.assertEqual(row['task']['parentID'], 'root-id')
        self.assertFalse(row['task']['selectable'])
        self.assertFalse(row['events'])

    def test_nonstandard_json_key_order_is_supported(self):
        record = json.dumps(dict(extra='x' * 1200, payload=dict(turn_id='turn-1', type='task_started'), timestamp=100, type='event_msg')).encode() + b'\n'
        row = self.run_commands(write(meta() + record), dict(op='open'), dict(op='read'))[-1]
        self.assertEqual(row['task']['status'], 'running')

    def test_idle_read_does_no_file_io(self):
        rows = self.run_commands(write(meta() + lifecycle()), dict(op='open'), dict(op='read'), dict(op='read'))
        self.assertEqual(rows[-1]['bytes'], rows[-2]['bytes'])

    def test_restore_partial_discovery_restarts_from_safe_tail(self):
        data = meta() + lifecycle() + line('response_item', dict(content='x' * (4 * 1024 * 1024)))
        rows = self.run_commands(write(data), dict(op='open'), dict(op='read'), dict(op='checkpoint'), dict(op='restore'), dict(op='read', cycles=50))
        self.assertEqual(rows[-1]['task']['status'], 'running')
        self.assertFalse(rows[-1]['pending'])
