"""Check production popup geometry without creating an app or opening a menu."""
from tools.common.paths import ROOT, BACKEND, macos_source
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class StatusMenuGeometryTests(unittest.TestCase):
    def test_screen_edges_dynamic_bar_and_autohide(self):
        with tempfile.TemporaryDirectory(prefix='status menu geometry ') as directory:
            root = Path(directory)
            main = root / 'main.swift'
            main.write_text('''import AppKit
func verify(_ condition: @autoclosure () -> Bool, _ message: String) {
    precondition(condition(), message)
}
func layout(_ button: NSRect, _ screen: NSRect, _ visible: NSRect, _ width: CGFloat, _ barHeight: CGFloat) -> StatusMenuPlacement {
    StatusMenuGeometry.placement(button: button, screen: screen, visible: visible, menuWidth: width, menuBarHeight: barHeight)
}
let screen = NSRect(x: 0, y: 0, width: 1440, height: 900)
let visible = NSRect(x: 0, y: 70, width: 1440, height: 797)
let button = NSRect(x: 1310, y: 872, width: 100, height: 22)
let regular = layout(button, screen, visible, 350, 33)
verify(regular.anchor == NSPoint(x: 1086, y: 863), "Right-edge popup must fit beneath the real 33pt menu bar")
verify(regular.confinement.maxY == 863 && regular.confinement.minY == 70, "Confinement must exclude the bar and bottom dock")
let narrowerMenu = layout(button, screen, visible, 200, 33)
verify(narrowerMenu.anchor.x == 1236, "Recompute the popup position from the current menu width")

let hidden = layout(button, screen, screen, 350, 33)
verify(hidden.anchor.y == 863, "Auto-hide visibleFrame must still respect the revealed menu bar")
let belowButton = NSRect(x: 600, y: 850, width: 140, height: 22)
verify(layout(belowButton, screen, screen, 250, 22).anchor.y == 846, "Button's actual bottom edge must win over a smaller reported bar height")

let negativeScreen = NSRect(x: -1920, y: -180, width: 1920, height: 1080)
let negativeVisible = NSRect(x: -1920, y: -180, width: 1920, height: 1056)
let negativeButton = NSRect(x: -110, y: 876, width: 100, height: 24)
let secondary = layout(negativeButton, negativeScreen, negativeVisible, 420, 24)
verify(secondary.anchor == NSPoint(x: -424, y: 872), "Negative-origin secondary screen must preserve global coordinates")
verify(StatusMenuGeometry.screenIndex(button: negativeButton, screens: [screen, negativeScreen]) == 1, "Choose the button's screen rather than the main display")

let upperScreen = NSRect(x: 0, y: 900, width: 1200, height: 800)
let upperVisible = NSRect(x: 0, y: 900, width: 1200, height: 772)
let upperButton = NSRect(x: 30, y: 1672, width: 100, height: 28)
let upper = layout(upperButton, upperScreen, upperVisible, 320, 28)
verify(upper.anchor == NSPoint(x: 30, y: 1668), "Vertically stacked screens must not be normalized to the primary screen")
verify(StatusMenuGeometry.screenIndex(button: upperButton, screens: [screen, upperScreen]) == 1, "Select the upper screen independently of primary display coordinates")
verify(StatusMenuGeometry.screenIndex(button: NSRect(x: 2000, y: 3000, width: 100, height: 22), screens: [screen, upperScreen]) == nil, "A hidden offscreen button must fall back to its window's screen, not an unrelated last display")

let sideDockVisible = NSRect(x: 72, y: 0, width: 1368, height: 876)
let nearLeft = NSRect(x: 20, y: 876, width: 100, height: 24)
verify(layout(nearLeft, screen, sideDockVisible, 400, 24).anchor.x == 76, "Horizontal clamp must respect a side Dock")
let oversized = layout(button, screen, visible, 2000, 33)
verify(oversized.anchor.x == 4 && oversized.anchor.y == 863, "An oversized menu must retain a valid anchor below the bar")
verify(oversized.confinement.width == 1432, "Let AppKit constrain oversized content to available width")
print("8 popup geometry cases passed")
''')
            binary = root / 'check'
            source = macos_source('StatusMenu.swift')
            compiled = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), str(source), '-o', str(binary)], capture_output=True, text=True, timeout=90)
            self.assertEqual(compiled.returncode, 0, compiled.stderr)
            ran = subprocess.run([str(binary)], capture_output=True, text=True, timeout=10)
            self.assertEqual(ran.returncode, 0, ran.stderr)
            self.assertIn('8 popup geometry cases passed', ran.stdout)


if __name__ == '__main__':
    unittest.main()
