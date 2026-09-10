"""Production reclaim scheduler with deterministic allocator and interaction state."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from test_floating_period import declaration


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class IdleReclamationTests(unittest.TestCase):
    def test_coalesces_and_skips_unsafe_interactions(self):
        method = declaration((Path(__file__).parents[1] / 'Sources/Main.swift').read_text(), '    func trimIdleMemory()').replace('malloc_zone_pressure_relief', 'testPressureRelief')
        source = '''import Foundation
var reclaimed = 0
func testPressureRelief(_ zone: UnsafeRawPointer?, _ goal: Int) { reclaimed += 1 }
final class State { var interactionActive = false }
final class Collector { var busy = false }
final class Fixture {
    var trimWork: DispatchWorkItem?, lastMemoryTrim = -Double.infinity
    var compactMode = true, terminating = false, nativeSummaryBusy = false, statusMenuTracking = false
    var floatExpanded = false, floatMovingFrame = false, floatAnimation: NSObject?, pendingRefresh: Int?
    let capsuleState = State(), collector = Collector()
''' + method + '''
}
let value = Fixture()
value.trimIdleMemory(); let previous = value.trimWork!
value.trimIdleMemory(); precondition(previous.isCancelled)
let flags: [(Bool) -> Void] = [
    { value.compactMode = !$0 }, { value.terminating = $0 }, { value.collector.busy = $0 },
    { value.nativeSummaryBusy = $0 }, { value.pendingRefresh = $0 ? 1 : nil },
    { value.statusMenuTracking = $0 }, { value.capsuleState.interactionActive = $0 },
    { value.floatExpanded = $0 }, { value.floatAnimation = $0 ? NSObject() : nil },
    { value.floatMovingFrame = $0 }
]
for flag in flags {
    flag(true); value.trimIdleMemory(); value.trimWork!.perform()
    precondition(reclaimed == 0 && value.trimWork == nil, "Unsafe work must retire without sweeping")
    flag(false)
}
value.trimIdleMemory(); value.trimWork!.perform()
precondition(reclaimed == 1 && value.trimWork == nil && value.lastMemoryTrim.isFinite)
value.trimIdleMemory()
RunLoop.main.run(until: Date().addingTimeInterval(0.1))
precondition(reclaimed == 1, "Repeated requests must respect the quiet delay")
value.trimWork?.cancel()
print("passed")
'''
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'main.swift'
            path.write_text(source)
            binary = Path(tmp) / 'check'
            result = subprocess.run(['xcrun', 'swiftc', str(path), '-o', str(binary)], capture_output=True, text=True, timeout=90)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = subprocess.run([str(binary)], capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
