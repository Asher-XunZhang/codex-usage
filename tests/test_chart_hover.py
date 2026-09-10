"""Native chart data, hit testing and hover lifetime without showing an app window."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires macOS developer tools')
class ChartHoverTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='chart hover ')
        cls.addClassCleanup(cls.directory.cleanup)
        root = Path(cls.directory.name)
        main = root / 'main.swift'
        main.write_text('''import AppKit
func check(_ result: @autoclosure () -> Bool, _ message: String) { precondition(result(), message) }
let mode = CommandLine.arguments[1]
switch mode {
case "values":
    let big = NSNumber(value: UInt64(9_007_199_254_740_993))
    let datum = TrendDatum(["date": "2026-09-11", "input_tokens": big, "output_tokens": 7, "total_tokens": NSNumber(value: UInt64(9_007_199_254_741_000))])
    check(datum.tooltipText == "2026-09-11\\n输入：9,007,199,254,740,993 tokens\\n输出：7 tokens\\n合计：9,007,199,254,741,000 tokens", "Exact hover counts must not round integers through Double")
    let largest = TrendDatum(["input_tokens": NSNumber(value: Int64.max), "output_tokens": NSNumber(value: UInt64.max)])
    check(largest.values[0].1 == "9,223,372,036,854,775,807" && largest.values[1].1 == "18,446,744,073,709,551,615", "Signed and unsigned integer maxima must retain every digit")
    let zero = TrendDatum(["date": "2026-09-10", "input_tokens": 0, "output_tokens": 0, "total_tokens": 0])
    check(zero.values.map { $0.1 } == ["0", "0", "0"], "Known zero is a real observation")
    let unknown = TrendDatum(["input_tokens": true, "output_tokens": -1, "total_tokens": Double.nan])
    check(unknown.date == "日期未知" && unknown.values.map { $0.1 } == ["未知", "未知", "未知"], "Missing or invalid values must not masquerade as zero")
    let missingTotal = TrendDatum(["date": "2026-09-11", "input_tokens": 4, "output_tokens": 5])
    check(missingTotal.values.last!.1 == "未知", "Never invent a reported total by adding partial fields")
case "geometry":
    let bounds = NSRect(x: 10, y: 20, width: 864, height: 240)
    let area = TrendGeometry.plot(in: bounds)
    check(area == NSRect(x: 58, y: 32, width: 800, height: 200), "Respect nonzero bounds origins")
    check(TrendGeometry.index(at: NSPoint(x: 58, y: 32), in: bounds, count: 4) == 0, "First date includes plot's leading edge")
    check(TrendGeometry.index(at: NSPoint(x: 257.9, y: 100), in: bounds, count: 4) == 0, "Stay in the first date before its boundary")
    check(TrendGeometry.index(at: NSPoint(x: 258, y: 100), in: bounds, count: 4) == 1, "Date boundaries must select only one day")
    check(TrendGeometry.index(at: NSPoint(x: 857.9, y: 100), in: bounds, count: 4) == 3, "Last visible date remains reachable")
    for point in [NSPoint(x: 858, y: 100), NSPoint(x: 50, y: 100), NSPoint(x: 100, y: 232), NSPoint(x: 100, y: 31)] {
        check(TrendGeometry.index(at: point, in: bounds, count: 4) == nil, "Axes and outside points must hide the hover")
    }
    check(TrendGeometry.index(at: NSPoint(x: 100, y: 100), in: bounds, count: 0) == nil, "Empty data has no hit target")
    check(TrendGeometry.index(at: .zero, in: NSRect(x: 0, y: 0, width: 40, height: 20), count: 4) == nil, "Undersized charts must not divide by zero")
    let resized = NSRect(x: 10, y: 20, width: 1264, height: 240)
    check(TrendGeometry.index(at: NSPoint(x: 658, y: 100), in: bounds, count: 4) == 3, "Original width")
    check(TrendGeometry.index(at: NSPoint(x: 658, y: 100), in: resized, count: 4) == 2, "Resize must recompute date regions")
case "lifetime":
    let app = NSApplication.shared; app.setActivationPolicy(.prohibited)
    let chart = TrendView(frame: NSRect(x: 0, y: 0, width: 864, height: 240))
    chart.days = [["date": "2026-09-10", "input_tokens": 0, "output_tokens": 0, "total_tokens": 0], ["date": "2026-09-11", "input_tokens": 10, "output_tokens": 5, "total_tokens": 15]]
    chart.updateTrackingAreas(); let tracking = chart.trackingAreas.first!
    chart.updateTrackingAreas()
    check(chart.trackingAreas.count == 1 && chart.trackingAreas.first === tracking, "Keep one tracking area, including after layout")
    chart.updateHover(at: NSPoint(x: 600, y: 100))
    check(chart.hoveredDayIndex == 1 && chart.hoverText!.hasPrefix("2026-09-11"), "Hover must expose the selected date")
    chart.days = [["date": "2026-09-09", "input_tokens": 3, "output_tokens": 4, "total_tokens": 7]]
    check(chart.hoveredDayIndex == nil && chart.hoverText == nil, "Filtering must immediately discard the previous date's tooltip")
    chart.updateHover(at: NSPoint(x: 600, y: 100))
    check(chart.hoveredDayIndex == 0 && chart.hoverText!.contains("合计：7 tokens"), "New hits must use the replacement data")
    chart.setFrameSize(NSSize(width: 500, height: 200))
    check(chart.hoveredDayIndex == nil, "Resize must hide stale positions until a fresh pointer event")
    chart.updateHover(at: NSPoint(x: 100, y: 100))
    let event = NSEvent.mouseEvent(with: .mouseMoved, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: 0, context: nil, eventNumber: 0, clickCount: 0, pressure: 0)!
    chart.mouseExited(with: event)
    check(chart.hoveredDayIndex == nil, "Mouse exit must hide immediately")
    chart.days = []
    chart.updateHover(at: NSPoint(x: 100, y: 100))
    check(chart.hoverText == nil, "Empty filters must not retain a former tooltip")
case "tooltip-bounds":
    for visible in [NSRect(x: 0, y: 70, width: 1440, height: 797), NSRect(x: -1920, y: -180, width: 1920, height: 1056)] {
        for cursor in [NSPoint(x: visible.minX + 1, y: visible.minY + 1), NSPoint(x: visible.maxX - 1, y: visible.maxY - 1), NSPoint(x: visible.midX, y: visible.midY)] {
            let frame = TrendGeometry.tooltipFrame(size: NSSize(width: 260, height: 100), near: cursor, visible: visible)
            check(visible.contains(frame), "Tooltips must remain within the chosen screen, including negative coordinates")
            check(frame.width == 260 && frame.height == 100, "Ordinary screen edges must not clip the content")
            check(!frame.contains(cursor), "Tooltip should sit away from the pointer")
        }
    }
    let tiny = NSRect(x: -50, y: 200, width: 100, height: 70)
    let clipped = TrendGeometry.tooltipFrame(size: NSSize(width: 260, height: 100), near: NSPoint(x: 0, y: 235), visible: tiny)
    check(tiny.contains(clipped) && clipped.width == 84 && clipped.height == 54, "Impossible display sizes must remain bounded")
default: fatalError("Unknown test")
}
print(mode + " passed")
''')
        cls.binary = root / 'check'
        source = Path(__file__).parents[1] / 'Sources/Chart.swift'
        compiled = subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main), str(source), '-o', str(cls.binary)], capture_output=True, text=True, timeout=120)
        if compiled.returncode:
            raise AssertionError(compiled.stderr)

    def run_case(self, name):
        result = subprocess.run([str(self.binary), name], capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(name + ' passed', result.stdout)

    def test_exact_known_unknown_and_zero_values(self):
        self.run_case('values')

    def test_current_plot_hit_regions(self):
        self.run_case('geometry')

    def test_hover_filter_resize_and_exit_lifetime(self):
        self.run_case('lifetime')

    def test_tooltip_screen_edges(self):
        self.run_case('tooltip-bounds')


if __name__ == '__main__':
    unittest.main()
