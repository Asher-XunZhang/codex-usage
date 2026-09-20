"""Settings content must not paint over native tabs when AppKit invalidates outside it."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from tools.common.paths import macos_source


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS AppKit')
class SettingsBackgroundTests(unittest.TestCase):
    def test_background_stays_inside_page_for_oversized_dirty_rect(self):
        harness = r'''import AppKit
let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
let usageMacOSURL = URL(fileURLWithPath: "/fixture-no-executable")
let controller = UpdateStatusController(embedded: true)
let view = controller.content
view.frame = NSRect(x: 0, y: 0, width: 80, height: 60)
for theme in [NSAppearance.Name.aqua, .darkAqua] {
    let image = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 120, pixelsHigh: 100,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: image)!
    NSAppearance(named: theme)!.performAsCurrentDrawingAppearance {
        NSColor.magenta.setFill(); NSRect(x: 0, y: 0, width: 120, height: 100).fill()
        let transform = NSAffineTransform(); transform.translateX(by: 20, yBy: 20); transform.concat()
        // A non-clipping ancestor may invalidate the entire window, including its tabs.
        view.draw(NSRect(x: -20, y: -20, width: 120, height: 100))
    }
    NSGraphicsContext.restoreGraphicsState()
    func isMarker(_ x: Int, _ y: Int) -> Bool {
        let c = image.colorAt(x: x, y: y)!.usingColorSpace(.deviceRGB)!
        return c.redComponent > 0.99 && c.greenComponent < 0.01 && c.blueComponent > 0.99
    }
    for y in 0..<100 { for x in 0..<120 {
        if x < 20 || x >= 100 || y < 20 || y >= 80 {
            precondition(isMarker(x, y), "Settings background erased neighboring navigation at \(x),\(y)")
        }
    } }
    precondition(!isMarker(60, 50), "Page background must still be painted")
}
print("background stays inside page")
'''
        with tempfile.TemporaryDirectory(prefix='settings-background-') as directory:
            root = Path(directory)
            main = root / 'main.swift'
            main.write_text(harness)
            binary = root / 'check'
            result = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main),
                                     str(macos_source('UpdateStatus.swift')), str(macos_source('Quota.swift')),
                                     '-o', str(binary)], capture_output=True, text=True, timeout=90)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = subprocess.run([str(binary)], capture_output=True, text=True, timeout=20)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
