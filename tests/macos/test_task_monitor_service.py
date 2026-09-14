"""Real host service against generated temporary homes; no App or notifications."""
from tools.common.paths import macos_source
from pathlib import Path
import json
import shutil
import subprocess
import tempfile
import unittest

HARNESS = r'''
import Foundation
let root = URL(fileURLWithPath: CommandLine.arguments[1])
let home = root.appendingPathComponent("home"), sessions = home.appendingPathComponent("sessions")
try FileManager.default.createDirectory(at: sessions, withIntermediateDirectories: true)
let log = sessions.appendingPathComponent("rollout.jsonl")
let now = Date().timeIntervalSince1970
func record(_ kind: String, _ payload: MonitorObject) -> Data { var data = try! JSONSerialization.data(withJSONObject: ["type": kind, "timestamp": now, "payload": payload]); data.append(10); return data }
func start(_ id: String = "turn-1") -> Data { record("event_msg", ["type": "task_started", "turn_id": id, "started_at": now - 2]) }
func complete(_ id: String = "turn-1") -> Data { record("event_msg", ["type": "task_complete", "turn_id": id, "completed_at": now + 1]) }
func append(_ data: Data) { let file = try! FileHandle(forWritingTo: log); try! file.seekToEnd(); try! file.write(contentsOf: data); try! file.close() }
try (record("session_meta", ["id": "task-1", "cwd": "/project"]) + start()).write(to: log)
let service = TaskMonitorService(url: root.appendingPathComponent("desktop/monitor.json"))
var state: MonitorObject = [:], deliveries: [MonitorObject] = []
service.changed = { state = $0 }
service.deliver = { messages, _ in deliveries.append(contentsOf: messages) }
func wait(_ predicate: () -> Bool) {
    let deadline = Date().addingTimeInterval(8)
    while !predicate() && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.01)) }
    if !predicate() { FileHandle.standardError.write(Data("Timed out: \(state)\n".utf8)); exit(2) }
}
func command(_ action: String, _ payload: MonitorObject = [:]) -> MonitorObject {
    var reply: MonitorObject?
    service.command(action, payload: payload) { reply = $0 }
    wait { reply != nil }; return reply!
}
service.start(home: home, watching: true)
_ = command("snapshot", ["section": "tasks"])
wait { (state["rows"] as? [MonitorObject])?.first?["status"] as? String == "running" }
let initial = state
let added = command("add", ["mode": "once", "selections": [["id": "task-1", "turnID": "turn-1"]]])
append(complete()); service.receive(paths: [log.path])
_ = command("snapshot", ["section": "messages"])
wait { (state["summary"] as? MonitorObject)?["unread"] as? Int == 1 }
let completed = state
wait { !deliveries.isEmpty }
let messageID = (completed["rows"] as! [MonitorObject])[0]["id"] as! String
_ = command("snapshot", ["section": "messages", "messageIDs": [messageID]])
precondition((state["rows"] as? [MonitorObject])?.count == 1)
_ = command("snapshot", ["section": "messages", "messageIDs": ["missing-message"]])
precondition((state["rows"] as? [MonitorObject])?.isEmpty == true)
precondition((state["summary"] as? MonitorObject)?["unread"] as? Int == 1)
_ = command("snapshot", ["section": "messages"])
precondition((state["rows"] as? [MonitorObject])?.count == 1 && state["messageIDs"] == nil)
service.stop()
let saved = try JSONSerialization.jsonObject(with: Data(contentsOf: root.appendingPathComponent("desktop/monitor.json"))) as! MonitorObject
let restarted = TaskMonitorService(url: root.appendingPathComponent("desktop/monitor.json"))
var restored: MonitorObject = [:], offlineDelivery = false
restarted.changed = { restored = $0 }; restarted.deliver = { _, _ in offlineDelivery = true }
restarted.start(home: home, watching: false)
restarted.command("snapshot", payload: ["section": "messages"]) { _ in }
wait { (restored["rows"] as? [MonitorObject])?.first != nil }
restarted.stop()
let result: MonitorObject = ["initial": initial, "added": added, "completed": completed, "saved": saved, "restored": restored,
    "delivered": !deliveries.isEmpty, "offlineDelivery": offlineDelivery]
print(String(data: try JSONSerialization.data(withJSONObject: result), encoding: .utf8)!)
'''


@unittest.skipUnless(shutil.which('xcrun'), 'Requires macOS Swift')
class TaskMonitorServiceTests(unittest.TestCase):
    def test_discovery_live_completion_and_restart_suppression(self):
        with tempfile.TemporaryDirectory(prefix='task service fixture ') as folder:
            root = Path(folder); main = root / 'main.swift'; main.write_text(HARNESS); binary = root / 'service'
            compiled = subprocess.run(['xcrun', 'swiftc', '-O', *(str(macos_source(x)) for x in ['TaskMonitorCore.swift', 'TaskLogReader.swift', 'TaskMonitorService.swift']), str(main), '-o', str(binary)], capture_output=True, text=True, timeout=120)
            self.assertEqual(compiled.returncode, 0, compiled.stderr)
            result = subprocess.run([str(binary), folder], capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)
            report = json.loads(result.stdout.strip().splitlines()[-1])
            self.assertTrue(report['added']['ok'])
            self.assertTrue(report['delivered'], json.dumps(report, ensure_ascii=False))
            self.assertFalse(report['offlineDelivery'])
            self.assertEqual(report['completed']['summary']['active'], 0)
            self.assertEqual(report['completed']['summary']['preview'][0]['id'], 'task-1')
            self.assertNotIn('path', report['completed']['summary']['preview'][0])
            self.assertEqual(len(report['saved']['messages']), 1)
            self.assertFalse(report['saved']['messages'][0]['offline'])
            self.assertEqual(report['restored']['rows'][0]['delivery'], 'suppressed')
            self.assertEqual(report['initial']['rows'][0]['status'], 'running')
