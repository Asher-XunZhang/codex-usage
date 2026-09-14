"""Behavioral screen geometry checks in AppKit points; no synthetic screen is a hardware claim."""
from tools.common.paths import macos_source
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


HARNESS = r'''
import Foundation
import CoreGraphics
func check(_ value: @autoclosure () -> Bool, _ reason: String) { precondition(value(), reason) }
let work = CGRect(x: -1600, y: -200, width: 1600, height: 1000)
let screen = CapsuleScreenGeometry(id: "left", frame: work, visibleFrame: work)
switch CommandLine.arguments[1] {
case "directions":
    for (x, y, right, up) in [(-1500.0, 600.0, true, false), (-1500, -100, true, true), (-100, 600, false, false), (-100, -100, false, true)] {
        let compact = CGRect(x: x, y: y, width: 76, height: 76)
        let p = CapsulePlacement.resolve(compact: compact, work: work)
        check(p.opensRight == right && p.opensUp == up, "choose each axis from actual space")
        check(p.compact == compact && work.contains(p.detail), "preserve compact anchor and visibility")
        check(right ? p.detail.minX == compact.minX : p.detail.maxX == compact.maxX, "horizontal edge remains anchored")
        check(up ? p.detail.minY == compact.minY : p.detail.maxY == compact.maxY, "vertical edge remains anchored")
        let translated = p.translated(x: 20, y: -40)
        check(translated.compact == compact.offsetBy(dx: 20, dy: -40), "drag moves compact backup too")
        check(translated.detail == p.detail.offsetBy(dx: 20, dy: -40), "drag preserves frozen direction")
    }
case "tiny":
    for size in [CGSize(width: 210, height: 200), CGSize(width: 350, height: 420), CGSize(width: 50, height: 50)] {
        let tiny = CGRect(origin: CGPoint(x: -5, y: -60), size: size)
        let p = CapsulePlacement.resolve(compact: CGRect(x: 10, y: 10, width: 76, height: 76), work: tiny)
        check(tiny.contains(p.detail) && tiny.contains(p.compact), "never put controls outside available bounds")
        check(p.detail.width <= 336 && p.detail.height <= 410, "limit detail size on small work areas")
    }
    for right in [false, true] {
        for up in [false, true] {
            let pill = CGRect(x: right ? work.minX + 24 : work.maxX - 160, y: up ? work.minY + 24 : work.maxY - 68, width: 136, height: 44)
            let p = CapsulePlacement.resolve(compact: pill, work: work)
            check(p.opensRight == right && p.opensUp == up && p.compact == pill, "new pill selects all four directions without moving its anchor")
        }
    }
case "seams":
    let neighbor = CapsuleScreenGeometry(id: "right", frame: CGRect(x: 0, y: 300, width: 1000, height: 600), visibleFrame: CGRect(x: 0, y: 300, width: 1000, height: 580))
    let connected = CGRect(x: -76, y: 400, width: 76, height: 76)
    check(CapsulePlacement.dockingEdge(compact: connected, screen: screen) == .right, "shared seam docks just like an outer edge")
    let exposed = CGRect(x: -76, y: 20, width: 76, height: 76)
    check(CapsulePlacement.dockingEdge(compact: exposed, screen: screen) == .right, "offset neighbor leaves lower edge available")
    check(CapsulePlacement.screen(for: connected, pointer: CGPoint(x: 20, y: 400), screens: [screen, neighbor])?.id == "right", "drag pointer chooses destination screen")
    let saved = CapsuleSavedPosition(compact: connected, screen: screen, edge: .right, freeOrigin: connected.origin)
    check(saved.restored(screens: [screen, neighbor])?.1 == .right, "restart retains docking at a shared seam")
    for edge in CapsuleDockEdge.allCases {
        let offset: CGPoint
        switch edge {
        case .left: offset = CGPoint(x: -work.width, y: 0)
        case .right: offset = CGPoint(x: work.width, y: 0)
        case .top: offset = CGPoint(x: 0, y: work.height)
        case .bottom: offset = CGPoint(x: 0, y: -work.height)
        }
        let adjacent = CapsuleScreenGeometry(id: "adjacent", frame: work.offsetBy(dx: offset.x, dy: offset.y), visibleFrame: work.offsetBy(dx: offset.x, dy: offset.y))
        let compact = CapsulePlacement.docked(CGRect(x: -850, y: 300, width: 76, height: 76), edge: edge, work: work)
        let panel = CapsulePlacement.resolve(compact: compact, work: work).detail
        check(CapsulePlacement.dockingEdge(compact: compact, screen: screen) == edge, "all four shared edges accept compact drops")
        check(CapsulePlacement.expandedDropEdge(panel: panel, pointer: CGPoint(x: compact.midX, y: compact.midY), screen: screen) == edge, "expanded panel accepts shared edge without relying on compact anchor distance")
        let tab = CapsulePlacement.tab(compact: compact, edge: edge, work: work)
        check(work.contains(tab) && tab.intersection(adjacent.frame).isEmpty, "shared-edge tab remains wholly on its owning screen")
        let position = CapsuleSavedPosition(compact: compact, screen: screen, edge: edge, freeOrigin: compact.origin)
        let restored = position.restored(screens: [screen, adjacent])!
        check(restored.0 == compact && restored.1 == edge, "all shared edges survive restart")
    }
case "restore":
    let compact = CGRect(x: -76, y: 200, width: 76, height: 76)
    let saved = CapsuleSavedPosition(compact: compact, screen: screen, edge: .right, freeOrigin: CGPoint(x: -900, y: 300))
    let roundtrip = try JSONDecoder().decode(CapsuleSavedPosition.self, from: JSONEncoder().encode(saved))
    check(roundtrip == saved, "versioned placement roundtrip")
    let replacement = CapsuleScreenGeometry(id: "new", frame: CGRect(x: 0, y: 0, width: 800, height: 600), visibleFrame: CGRect(x: 0, y: 50, width: 800, height: 520))
    let restored = saved.restored(screens: [replacement], size: compact.size)!
    check(restored.1 == .right && restored.0.maxX == 800 && replacement.visibleFrame.contains(restored.0), "unplugged screen relocates visible dock")
    check(abs(Double(restored.0.minY - 50) / (520 - 76) - saved.fraction) < 0.00001, "keep along-edge fraction")
    var invalid = saved; invalid.version = 99
    check(invalid.restored(screens: [screen]) == nil, "unknown preference version is not silently consumed")
case "edges":
    for edge in CapsuleDockEdge.allCases {
        let compact = CapsulePlacement.docked(CGRect(x: -850, y: 300, width: 76, height: 76), edge: edge, work: work)
        check(CapsulePlacement.dockingEdge(compact: compact, screen: screen) == edge, "all four docking edges")
        let tab = CapsulePlacement.tab(compact: compact, edge: edge, work: work)
        check(work.contains(tab), "tab avoids menu bar and Dock")
        check(tab.size == (edge == .left || edge == .right ? CGSize(width: 36, height: 76) : CGSize(width: 80, height: 32)), "side tabs are vertical; top and bottom tabs preserve readable horizontal numbers")
    }
    let pill = CGRect(x: work.minX, y: work.maxY - 44, width: 136, height: 44)
    let detail = CapsulePlacement.resolve(compact: pill, work: work)
    check(detail.detail.minX == pill.minX && detail.detail.maxY == pill.maxY, "docked anchor outranks optional margins")
default: fatalError("unknown case")
}
print("passed")
'''


@unittest.skipUnless(shutil.which('xcrun'), 'Requires Swift Foundation')
class CapsulePlacementTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='capsule placement ')
        cls.addClassCleanup(cls.directory.cleanup)
        main = Path(cls.directory.name) / 'main.swift'
        main.write_text(HARNESS)
        cls.binary = main.with_name('geometry')
        result = subprocess.run(['xcrun', 'swiftc', str(macos_source('CapsulePlacement.swift')), str(main), '-o', str(cls.binary)], capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise AssertionError(result.stderr)

    def test_geometry(self):
        for case in ['directions', 'tiny', 'seams', 'restore', 'edges']:
            with self.subTest(case=case):
                result = subprocess.run([str(self.binary), case], capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode, 0, result.stderr)
