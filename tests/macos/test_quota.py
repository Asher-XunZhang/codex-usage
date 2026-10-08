from tools.common.paths import ROOT, BACKEND, macos_source
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires Swift')
class QuotaTests(unittest.TestCase):
    def test_remaining_quota_unknown_zero_stale_and_reset_count(self):
        with tempfile.TemporaryDirectory(prefix='quota model ') as d:
            root = Path(d)
            main = root / 'main.swift'
            main.write_text('''import Foundation
let usageMacOSURL = Bundle.main.executableURL!.deletingLastPathComponent()
let fresh = Date().timeIntervalSince1970
let data: [String: Any] = ["updated_at":fresh,"windows":[["used_percent":80,"duration_minutes":10080,"resets_at":fresh+3600],["used_percent":0,"duration_minutes":300]],"reset_count":3]
let result=QuotaSnapshot.parse(data)
precondition(result.windows.map(\\.remaining)==[20,100])
precondition(result.capsuleCompact=="周余 20%")
precondition(result.capsuleWindow?.label=="周" && result.capsuleWindow?.remaining==20, "Liquid level must use the same limiting window as the displayed quota")
precondition(result.resetLabel=="重置卡 3 张")
precondition(!result.stale)
let missing=QuotaSnapshot.parse([:])
precondition(missing.windows.isEmpty && missing.resetCount==nil && missing.compact=="额度 —")
precondition(missing.capsuleWindow==nil, "Missing quota must not appear as empty quota")
let zero=QuotaSnapshot.parse(["updated_at":fresh,"windows":[["used_percent":100,"duration_minutes":300]],"reset_count":0])
precondition(zero.windows[0].remaining==0 && zero.resetCount==0)
let stale=QuotaSnapshot.parse(["updated_at":fresh-1000,"windows":[["used_percent":80,"duration_minutes":10080]]])
precondition(stale.stale && stale.compact.hasSuffix("*"))
let directory=URL(fileURLWithPath:NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
defer { try? FileManager.default.removeItem(at:directory) }
try JSONSerialization.data(withJSONObject:data).write(to:directory.appendingPathComponent("quota.json"))
let reader=QuotaReader(root:directory)
reader.start()
precondition(reader.snapshot.resetCount==3 && reader.snapshot.error==nil, "A fresh cached quota must not launch an unavailable helper during mode changes")
reader.setEnabled(false)
reader.refresh(force:true)
precondition(!reader.enabled && !reader.isRefreshing && reader.snapshot.resetCount==3, "Disabled refresh preserves cached data and never starts a helper")
var stopped=false;reader.stop{stopped=true}
while !stopped { RunLoop.main.run(until:Date().addingTimeInterval(0.01)) }
print("Quota read-only display checks passed")
''')
            binary = root / 'check'
            compiled = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), str(macos_source('Quota.swift')), '-o', str(binary)],capture_output=True,text=True,timeout=90)
            self.assertEqual(compiled.returncode,0,compiled.stderr)
            ran = subprocess.run([str(binary)],capture_output=True,text=True,timeout=10)
            self.assertEqual(ran.returncode,0,ran.stderr)

    def test_helper_has_only_quota_and_managed_auth_operations(self):
        source=(macos_source('QuotaHelper.swift')).read_text()
        import re
        methods=set(re.findall(r'"method":\s*"([^"]+)"',source))
        methods.update(re.findall(r'request\(\d+, "([^"]+)"', source))
        self.assertEqual(methods,{'initialize','initialized','account/rateLimits/read','account/read'})
        self.assertNotIn('consume',source)
        self.assertNotIn('auth.json',source)

@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires Swift')
class QuotaRecoveryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix='quota protocol ')
        cls.addClassCleanup(cls.temporary.cleanup)
        root = Path(cls.temporary.name)
        source = macos_source('QuotaHelper.swift').read_text().split('// MARK: - Process entry point')[0]
        main = root / 'main.swift'
        main.write_text(source + '''
let request = QuotaRequest()
var sent: [[String: Any]] = [], result: [String: Any]?, error: String?, completions = 0
request.send = { sent.append($0) }
request.finish = { result = $0; error = $1; completions += 1 }
while let line = readLine(), let data = line.data(using: .utf8) {
    request.receive(try JSONSerialization.jsonObject(with: data) as! [String: Any])
}
let report: [String: Any] = ["sent": sent, "result": result as Any? ?? NSNull(), "error": error as Any? ?? NSNull(), "completions": completions]
print(String(data: try JSONSerialization.data(withJSONObject: report), encoding: .utf8)!)
''')
        cls.binary = root / 'protocol'
        subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), '-o', str(cls.binary)],
                       check=True, capture_output=True, text=True, timeout=90)

    def exchange(self, *responses):
        import json
        ran = subprocess.run([str(self.binary)], input='\n'.join(json.dumps(r) for r in responses),
                             capture_output=True, text=True, timeout=10, check=True)
        return json.loads(ran.stdout)

    @staticmethod
    def reply(id, result):
        return dict(id=id, result=result)

    @staticmethod
    def failure(id, message, code=-32603):
        return dict(id=id, error=dict(code=code, message=message))

    @staticmethod
    def limits(id=2):
        return dict(id=id, result=dict(rateLimits=dict(primary=dict(usedPercent=42, windowDurationMins=300))))

    def test_success_does_not_refresh_credentials(self):
        r = self.exchange(self.reply(1, {}), self.limits())
        self.assertEqual([x['method'] for x in r['sent']], ['initialized', 'account/rateLimits/read'])
        self.assertEqual(r['result']['windows'][0]['used_percent'], 42)
        self.assertIsNone(r['error'])

    def test_401_refreshes_credentials_and_retries_once(self):
        r = self.exchange(self.reply(1, {}), self.failure(2, 'failed: 401 Unauthorized'),
                          self.reply(3, dict(account=dict(type='chatgpt'))), self.limits(4))
        self.assertEqual([x['method'] for x in r['sent']],
                         ['initialized', 'account/rateLimits/read', 'account/read', 'account/rateLimits/read'])
        self.assertEqual(r['sent'][2]['params'], dict(refreshToken=True))
        self.assertEqual(r['result']['windows'][0]['used_percent'], 42)
        self.assertEqual(r['completions'], 1)

    def test_rejected_retry_stops_and_does_not_leak_server_message(self):
        r = self.exchange(self.reply(1, {}), self.failure(2, '401 Unauthorized secret-first'),
                          self.reply(3, dict(account=dict(type='chatgpt'))),
                          self.failure(4, '401 Unauthorized secret-second'), self.limits(4))
        self.assertEqual(len(r['sent']), 4)
        self.assertEqual(r['completions'], 1)
        self.assertIsNone(r['result'])
        self.assertEqual(r['error'], '登录已失效，请在 Codex 中重新登录后重试')

    def test_expired_refresh_credentials_require_login(self):
        r = self.exchange(self.reply(1, {}), self.failure(2, '401 Unauthorized'),
                          self.failure(3, 'Your refresh token has already been used. secret'))
        self.assertEqual(len(r['sent']), 3)
        self.assertIn('重新登录', r['error'])
        self.assertNotIn('secret', r['error'])

    def test_network_failure_does_not_refresh_credentials(self):
        for message, expected in [('connection refused', '网络或代理'), ('timeout', '超时'),
                                  ('403 Forbidden', '无权'), ('503 Service Unavailable', '稍后重试')]:
            with self.subTest(message=message):
                r = self.exchange(self.reply(1, {}), self.failure(2, message))
                self.assertEqual(len(r['sent']), 2)
                self.assertIn(expected, r['error'])

    def test_network_failure_during_refresh_is_not_reported_as_expired_login(self):
        r = self.exchange(self.reply(1, {}), self.failure(2, '401 Unauthorized'), self.failure(3, 'error sending request'))
        self.assertIn('网络或代理', r['error'])
        self.assertNotIn('登录已失效', r['error'])

    def test_missing_account_and_unsupported_cli_stop_before_retry(self):
        for response, expected in [(self.reply(3, dict(account=None)), '重新登录'),
                                   (self.failure(3, 'method not found', -32601), '更新 Codex')]:
            with self.subTest(response=response):
                r = self.exchange(self.reply(1, {}), self.failure(2, '401 Unauthorized'), response)
                self.assertEqual(len(r['sent']), 3)
                self.assertIn(expected, r['error'])

    def test_notifications_duplicates_and_out_of_order_results_are_ignored(self):
        r = self.exchange(self.limits(), self.reply(1, {}), self.reply(1, {}),
                          dict(id=2, method='account/rateLimits/updated', params={}),
                          self.failure(2, '401 Unauthorized'), self.limits(),
                          self.reply(3, dict(account=dict(type='chatgpt'))), self.limits(4), self.limits(4))
        self.assertEqual(len(r['sent']), 4)
        self.assertEqual(r['completions'], 1)
        self.assertIsNone(r['error'])

    def test_malformed_response_preserves_failure_semantics(self):
        r = self.exchange(self.reply(1, {}), dict(id=2))
        self.assertIsNone(r['result'])
        self.assertIn('响应无效', r['error'])

    def test_reader_preserves_actionable_error_and_recovers_on_manual_retry(self):
        import json
        with tempfile.TemporaryDirectory(prefix='quota reader ') as directory:
            root = Path(directory)
            cache = root / 'quota.json'
            cache.write_text(json.dumps(dict(updated_at=1, windows=[dict(used_percent=80, duration_minutes=300)])))
            helper = root / 'CodexQuota'
            (root / 'helper.py').write_text('''import json, pathlib, sys, time
p = pathlib.Path(sys.argv[sys.argv.index('--output') + 1])
d = json.loads(p.read_text())
if 'error' not in d:
    d['error'] = '登录已失效，请在 Codex 中重新登录后重试'
    p.write_text(json.dumps(d))
    sys.exit(1)
p.write_text(json.dumps(dict(updated_at=time.time(), windows=[dict(used_percent=42, duration_minutes=300)])))
''')
            import shlex
            helper.write_text('#!/bin/sh\nexec ' + shlex.quote(sys.executable) + ' ' + shlex.quote(str(root / 'helper.py')) + ' \"$@\"\n')
            helper.chmod(0o755)
            main = root / 'main.swift'
            main.write_text('''import Foundation
let usageMacOSURL = URL(fileURLWithPath: CommandLine.arguments[1])
let reader = QuotaReader(root: usageMacOSURL)
func waitForRefresh() {
    let deadline = Date().addingTimeInterval(10)
    while reader.isRefreshing && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.01)) }
    precondition(!reader.isRefreshing)
}
reader.start(); waitForRefresh()
precondition(reader.snapshot.error == "登录已失效，请在 Codex 中重新登录后重试")
precondition(reader.snapshot.windows[0].remaining == 20 && reader.snapshot.updated!.timeIntervalSince1970 == 1)
reader.refresh(force: true); waitForRefresh()
precondition(reader.snapshot.error == nil && reader.snapshot.windows[0].remaining == 58 && !reader.snapshot.stale)
var stopped = false; reader.stop { stopped = true }
while !stopped { RunLoop.main.run(until: Date().addingTimeInterval(0.01)) }
''')
            binary = root / 'reader'
            subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), str(macos_source('Quota.swift')),
                            '-o', str(binary)], check=True, capture_output=True, text=True, timeout=90)
            subprocess.run([str(binary), str(root)], check=True, capture_output=True, text=True, timeout=25)
