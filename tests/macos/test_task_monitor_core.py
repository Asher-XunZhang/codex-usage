"""Lifecycle and persistence behavior, without Codex processes or notifications."""
from tools.common.paths import macos_source
from pathlib import Path
import json
import shutil
import subprocess
import tempfile
import unittest


HARNESS = r'''
import Foundation
let url = URL(fileURLWithPath: CommandLine.arguments[1])
var store = TaskMonitorStore(url: url)
var tasks: [String: MonitoredTask] = [:]
let commands = try JSONSerialization.jsonObject(with: FileHandle.standardInput.readDataToEndOfFile()) as! [MonitorObject]
var results: [MonitorObject] = []
for command in commands {
    var result: MonitorObject = [:]
    let now = (command["now"] as? NSNumber)?.doubleValue ?? 100
    let id = command["id"] as? String ?? "task-a"
    switch command["op"] as? String ?? "" {
    case "task": tasks[id] = MonitoredTask(id: id, home: "/fixture", path: "/fixture/sessions/" + id + ".jsonl", parentID: command["parent"] as? String ?? "", title: id, project: "/project")
    case "event":
        if var task = tasks[id], let event = command["event"] as? MonitorObject {
            if let turn = task.consume(event) { store.observe(task, turn: turn, offline: command["offline"] as? Bool == true, now: now) }
            tasks[id] = task; store.reconcile(task, offline: command["offline"] as? Bool == true, now: now)
        }
    case "apply": result = store.apply(command["action"] as? String ?? "", payload: command["payload"] as? MonitorObject ?? [:], tasks: Array(tasks.values), home: "/fixture", now: now)
    case "save": result["saved"] = store.save()
    case "reload": store = TaskMonitorStore(url: url)
    case "block": try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: url.deletingLastPathComponent().path)
    case "unblock": try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.deletingLastPathComponent().path)
    case "repair-recovery-target":
        try FileManager.default.removeItem(at: url)
        try Data("{damaged fixture".utf8).write(to: url)
    default: break
    }
    result["tasks"] = tasks.values.map { $0.object }; result["watches"] = store.watches; result["messages"] = store.messages
    result["settings"] = store.settings; result["storeError"] = store.error; result["corrupt"] = store.corrupt
    result["recoveryBackup"] = store.recoveryBackup
    results.append(result)
}
FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: results))
'''


def event(kind, turn='turn-1', at=100, **extra):
    return dict(op='event', now=at, event=dict(type='event_msg', timestamp=at,
        payload=dict(type=kind, turn_id=turn, **extra)))


def apply(action, **payload):
    return dict(op='apply', action=action, payload=payload, now=100)


def add(mode='once', turn='turn-1', task='task-a'):
    return apply('add', mode=mode, selections=[dict(id=task, turnID=turn)])


@unittest.skipUnless(shutil.which('xcrun'), 'Requires macOS Swift')
class TaskMonitorCoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = tempfile.TemporaryDirectory(prefix='task monitor core ')
        cls.addClassCleanup(cls.build.cleanup)
        main = Path(cls.build.name) / 'main.swift'
        main.write_text(HARNESS)
        cls.binary = main.with_name('monitor')
        result = subprocess.run(['xcrun', 'swiftc', str(macos_source('TaskMonitorCore.swift')), str(main), '-o', str(cls.binary)], capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise AssertionError(result.stderr)

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='task monitor store ')
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / 'monitor.json'

    def run_commands(self, *commands):
        result = subprocess.run([str(self.binary), str(self.path)], input=json.dumps(commands), capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def setup_commands(self):
        return [dict(op='task'), event('task_started', at=99)]

    def test_once_duplicate_terminal_and_read_survive_restart(self):
        rows = self.run_commands(*self.setup_commands(), add(), event('task_complete', at=101), event('task_complete', at=101), dict(op='save'), dict(op='reload'))
        self.assertEqual(len(rows[-1]['messages']), 1)
        self.assertFalse(rows[-1]['watches'][0]['active'])
        self.assertEqual(rows[-1]['messages'][0]['delivery'], 'suppressed')
        self.assertFalse(rows[-1]['messages'][0]['read'])
        identifier = rows[-1]['messages'][0]['id']
        rows = self.run_commands(apply('read', ids=[identifier]), dict(op='reload'))
        self.assertTrue(rows[-1]['messages'][0]['read'])

    def test_continuous_old_terminal_never_rewinds_new_turn(self):
        rows = self.run_commands(*self.setup_commands(), add('each'), event('task_started', 'turn-2', 102), event('task_complete', 'turn-1', 103), event('task_complete', 'turn-2', 104))
        self.assertEqual(rows[-2]['tasks'][0]['turnID'], 'turn-2')
        self.assertEqual(rows[-2]['watches'][0]['status'], 'running')
        self.assertEqual(rows[-1]['watches'][0]['status'], 'idle')
        self.assertEqual(len(rows[-1]['messages']), 2)
        self.assertTrue(rows[-1]['watches'][0]['active'])

    def test_ms_seconds_and_replayed_start(self):
        rows = self.run_commands(dict(op='task'), event('task_started', at=1_800_000_000_000), event('task_complete', at=1_800_000_001), event('task_started', at=1_800_000_000))
        self.assertEqual(rows[-1]['tasks'][0]['status'], 'completed')
        self.assertEqual(rows[-1]['tasks'][0]['startedAt'], 1_800_000_000)

    def test_unidentified_or_unsupported_terminal_is_unknown(self):
        bad = event('task_complete'); del bad['event']['payload']['turn_id']
        rows = self.run_commands(*self.setup_commands(), add(), bad)
        self.assertEqual(rows[-1]['watches'][0]['status'], 'unknown')
        self.assertEqual(rows[-1]['messages'], [])
        rows = self.run_commands(*self.setup_commands(), event('turn_aborted', at=101, reason='future-new-reason'))
        self.assertEqual(rows[-1]['tasks'][0]['status'], 'unknown')

    def test_child_inherited_events_do_not_create_root_subscription(self):
        rows = self.run_commands(dict(op='task', parent='parent-id'), event('task_started'), add())
        self.assertFalse(rows[-1]['ok'])
        self.assertEqual(rows[-1]['watches'], [])

    def test_pause_records_messages_and_cancel_does_not_clear_history(self):
        rows = self.run_commands(*self.setup_commands(), add(), apply('settings', patch=dict(pausedUntil=-1)), event('task_complete', at=101), apply('stop', id='task-a'))
        self.assertEqual(rows[-1]['watches'], [])
        self.assertEqual(len(rows[-1]['messages']), 1)
        self.assertFalse(rows[-1]['messages'][0]['read'])
        self.assertEqual(rows[-1]['messages'][0]['delivery'], 'suppressed')

    def test_selection_race_preserves_selected_ended_turn(self):
        rows = self.run_commands(*self.setup_commands(), event('task_complete', at=101), event('task_started', 'turn-2', 102), add())
        self.assertTrue(rows[-1]['ok'])
        self.assertTrue(rows[-1]['items'][0]['ended'])
        self.assertEqual(rows[-1]['watches'][0]['turnID'], 'turn-1')
        self.assertEqual(rows[-1]['messages'][0]['delivery'], 'suppressed')

    def test_corrupt_store_requires_backup_and_confirmation(self):
        damaged = b'{not valid json'
        self.path.write_bytes(damaged)
        rows = self.run_commands(apply('read', all=True), apply('recover', confirm=False))
        self.assertTrue(rows[-1]['corrupt'])
        self.assertEqual(self.path.read_bytes(), damaged)
        row = self.run_commands(apply('recover', confirm=True))[-1]
        self.assertTrue(row['ok'])
        self.assertEqual(Path(row['backupPath']).read_bytes(), damaged)

    def test_conversation_content_is_not_persisted(self):
        private = dict(op='event', event=dict(type='response_item', payload=dict(content='PRIVATE-BODY-DO-NOT-STORE')))
        self.run_commands(*self.setup_commands(), add(), private, dict(op='save'))
        self.assertNotIn('PRIVATE-BODY-DO-NOT-STORE', self.path.read_text())

    def test_failed_recovery_commit_keeps_backup_and_allows_retry(self):
        # A directory can be backed up but cannot be replaced by Data.write.
        # This exercises failure after backup without touching user storage.
        self.path.mkdir()
        (self.path / 'evidence').write_text('preserve damaged input')
        rows = self.run_commands(apply('recover', confirm=True),
                                 dict(op='repair-recovery-target'),
                                 apply('recover', confirm=True), dict(op='reload'))
        failed, _, retried, reloaded = rows
        self.assertFalse(failed['ok'])
        self.assertTrue(failed['corrupt'], 'Failed recovery must remain retryable')
        self.assertTrue(failed['storeError'])
        self.assertEqual((Path(failed['recoveryBackup']) / 'evidence').read_text(),
                         'preserve damaged input')
        self.assertTrue(retried['ok'])
        self.assertIn('backupPath', retried, 'Retry must perform recovery, not report no-op')
        self.assertFalse(reloaded['corrupt'])
        self.assertEqual(reloaded['messages'], [])

    def test_failed_write_retains_uncommitted_watch_intent(self):
        import os
        if os.geteuid() == 0:
            self.skipTest('Root bypasses directory write permissions')
        rows = self.run_commands(*self.setup_commands(), dict(op='block'), add(), dict(op='unblock'), add())
        self.assertFalse(rows[-3]['ok'])
        self.assertEqual(len(rows[-3]['watches']), 1)
        self.assertTrue(rows[-1]['ok'])
        self.assertEqual(len(json.loads(self.path.read_text())['watches']), 1)
