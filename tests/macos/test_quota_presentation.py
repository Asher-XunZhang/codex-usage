"""Host quota results rendered through the real floating view and snapshot model."""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from tools.common.paths import macos_source
from tests.macos.test_window_refresh_bridge import declaration


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'Requires AppKit')
class QuotaPresentationTests(unittest.TestCase):
    def test_host_wiring_preserves_errors_pending_and_recovery(self):
        method = declaration(macos_source('Main.swift').read_text(), '    func renderQuota(')
        fixture = r'''import AppKit
let isMainWindowProcess = false
let usageMacOSURL = Bundle.main.executableURL!.deletingLastPathComponent()
final class Dashboard { let quotaText = NSTextField(), quotaCards = NSTextField() }
final class Reader { var isRefreshing = false, enabled = true }
final class Budget { func updateQuota(_ snapshot: QuotaSnapshot) {} }
final class Host {
    let capsuleState = CapsuleState(), quotaReader = Reader()
    var dashboard: Dashboard? = Dashboard(), budgetCoordinator: Budget?
    lazy var capsule: CapsuleSurface? = CapsuleSurface(state: capsuleState)
    func updateStatusTitle() {}; func updateRefreshStatus() {}; func publishHostState() {}
''' + method + r'''}
NSApplication.shared.setActivationPolicy(.prohibited)
let host = Host()
let surface = host.capsule!
surface.setFrameSize(CapsuleSurface.large); surface.expansion = 1
let error = "未找到 Codex；请先安装并登录 Codex"
host.renderQuota(QuotaSnapshot(error: error))
precondition(host.capsuleState.quotaFraction == nil)
precondition(host.capsuleState.quotaDetail == error && host.capsuleState.quotaFailed)
precondition((surface.accessibilityValue() as? String)?.contains(error) == true)
let old = QuotaSnapshot(windows: [QuotaWindow(label: "周", remaining: 42, resetsAt: nil)], updated: Date(), error: error)
host.renderQuota(old)
precondition(host.capsuleState.quotaFraction == 0.42 && host.capsuleState.quotaStale)
precondition(host.capsuleState.quotaDetail == error && surface.toolTip!.contains(error))
host.quotaReader.isRefreshing = true; host.renderQuota(old)
host.capsuleState.indicator = "check"
precondition(host.capsuleState.refreshIndicator == "busy" && !host.capsuleState.canRefresh)
precondition(host.capsuleState.quotaDetail == "正在刷新账号额度…")
host.quotaReader.isRefreshing = false
host.renderQuota(QuotaSnapshot(windows: [QuotaWindow(label: "周", remaining: 38, resetsAt: nil)], updated: Date()))
precondition(host.capsuleState.quotaFraction == 0.38 && !host.capsuleState.quotaFailed && !host.capsuleState.quotaStale)
precondition(host.capsuleState.quotaDetail == "周余 38%" && host.capsuleState.canRefresh)
'''
        with tempfile.TemporaryDirectory(prefix='quota-presentation-') as directory:
            root = Path(directory); main = root / 'main.swift'; main.write_text(fixture)
            binary = root / 'check'
            subprocess.run(['xcrun', 'swiftc', '-swift-version', '5', str(main),
                            str(macos_source('Quota.swift')), str(macos_source('Capsule.swift')), '-o', str(binary)],
                           check=True, capture_output=True, text=True, timeout=120)
            subprocess.run([str(binary)], check=True, capture_output=True, text=True, timeout=15)
