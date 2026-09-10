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
var stopped=false;reader.stop{stopped=true}
while !stopped { RunLoop.main.run(until:Date().addingTimeInterval(0.01)) }
print("Quota read-only display checks passed")
''')
            binary = root / 'check'
            compiled = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), str(Path(__file__).parents[1]/'Sources/Quota.swift'), '-o', str(binary)],capture_output=True,text=True,timeout=90)
            self.assertEqual(compiled.returncode,0,compiled.stderr)
            ran = subprocess.run([str(binary)],capture_output=True,text=True,timeout=10)
            self.assertEqual(ran.returncode,0,ran.stderr)

    def test_helper_has_only_fixed_read_operations(self):
        source=(Path(__file__).parents[1]/'Sources/QuotaHelper.swift').read_text()
        import re
        methods=set(re.findall(r'"method":\s*"([^"]+)"',source))
        self.assertEqual(methods,{'initialize','initialized','account/rateLimits/read'})
        self.assertNotIn('consume',source)
        self.assertNotIn('auth.json',source)
