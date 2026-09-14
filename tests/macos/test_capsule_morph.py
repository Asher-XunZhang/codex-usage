"""Continuous morph geometry and rounded-contour text safety."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from tools.common.paths import macos_source
from tests.macos.test_capsule_controller_interaction import declaration


@unittest.skipUnless(shutil.which('xcrun'), 'Requires macOS Swift')
class CapsuleMorphTests(unittest.TestCase):
    def test_staged_geometry_reversal_and_fractional_text_safety(self):
        code = 'import AppKit\n' + declaration(macos_source('Capsule.swift').read_text(), 'struct CapsuleMorph') + r'''
func near(_ a: CGFloat, _ b: CGFloat) -> Bool { abs(a-b) < 1e-7 }
let compact = NSRect(x: 400, y: 200, width: 76, height: 76)
for dx: CGFloat in [0, -260] { for dy: CGFloat in [0, -334] {
    let panel = NSRect(x: compact.minX + dx, y: compact.minY + dy, width: 336, height: 410)
    let initial = CapsuleMorph.frame(compact: compact, panel: panel, progress: 0.14)
    precondition(initial.shape == compact && initial.arcAlpha == 0 && initial.detailsAlpha == 0)
    let wide = CapsuleMorph.frame(compact: compact, panel: panel, progress: 0.76)
    precondition(near(wide.shape.width, 336) && wide.shape.height < 410 && wide.detailsAlpha == 0)
    let full = CapsuleMorph.frame(compact: compact, panel: panel, progress: 0.84)
    precondition(full.shape == panel && full.detailsAlpha == 0)
    precondition(near(CapsuleMorph.frame(compact: compact, panel: panel, progress: 0.93).detailsAlpha, 0.5))
    let envelope = compact.union(panel)
    for i in 0...1000 {
        let p = CGFloat(i)/1000, f = CapsuleMorph.frame(compact: compact, panel: panel, progress: p)
        precondition(envelope.insetBy(dx: -1e-8, dy: -1e-8).contains(f.shape))
        let reverse = CapsuleMorph.frame(compact: compact, panel: panel, progress: 1-CGFloat(1000-i)/1000)
        precondition(near(f.shape.minX, reverse.shape.minX) && near(f.shape.height, reverse.shape.height))
        if f.detailsAlpha > 0 { precondition(f.shape == panel) }
        let wanted = NSRect(x: f.shape.minX, y: f.shape.minY, width: 64, height: 30)
        let safe = CapsuleMorph.safeText(wanted, inside: f.shape, radius: f.radius)!
        precondition(safe.size == wanted.size)
        let inset = f.shape.insetBy(dx: 2, dy: 2), radius = f.radius - 2
        for x in [safe.minX, safe.maxX] { for y in [safe.minY, safe.maxY] {
            let cx = min(max(x, inset.minX+radius), inset.maxX-radius)
            let cy = min(max(y, inset.minY+radius), inset.maxY-radius)
            precondition(pow(x-cx,2)+pow(y-cy,2) <= radius*radius+1e-7)
        } }
    }
} }
for n in 0..<1000 {
    let circle = NSRect(x: -CGFloat(n)/7, y: CGFloat(n)/11, width: 76, height: 76)
    let safe = CapsuleMorph.safeText(NSRect(x: circle.minX+2, y: circle.minY+4, width: 64, height: 30), inside: circle, radius: 38)
    precondition(safe != nil)
}
precondition(CapsuleMorph.safeText(NSRect(x: 0, y: 0, width: 76, height: 30), inside: NSRect(x: 0, y: 0, width: 76, height: 76), radius: 38) == nil)
'''
        with tempfile.TemporaryDirectory(prefix='capsule morph ') as folder:
            source = Path(folder)/'main.swift'; source.write_text(code); binary = source.with_name('check')
            result = subprocess.run(['xcrun', 'swiftc', str(source), '-o', str(binary)], capture_output=True, text=True, timeout=90)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = subprocess.run([str(binary)], capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stderr)
