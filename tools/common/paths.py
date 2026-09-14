"""Repository locations shared by desktop tools; also works in source archives."""
from pathlib import Path


def repository_root(start: Path = Path(__file__)) -> Path:
    """Find a source checkout without relying on Git metadata or the working directory."""
    path = Path(start).resolve()
    for candidate in (path, *path.parents):
        if (candidate / 'src/backend/token_usage.py').is_file() and (candidate / 'test.py').is_file():
            return candidate
    raise RuntimeError(f'Cannot locate the codex-usage source tree from {start}')


ROOT = repository_root()
BACKEND = ROOT / 'src/backend'
MACOS = ROOT / 'src/macos'
WINDOWS = ROOT / 'src/windows'
MACOS_RESOURCES = ROOT / 'resources/macos'
WINDOWS_RESOURCES = ROOT / 'resources/windows'

MACOS_SOURCES = {
    'Entry.swift': 'App/Entry.swift',
    'Main.swift': 'App/Main.swift',
    'MainState.swift': 'Features/Usage/MainState.swift',
    'Settings.swift': 'Infrastructure/Settings.swift',
    'UpdateStatus.swift': 'Infrastructure/UpdateStatus.swift',
    'BudgetCore.swift': 'Features/Budgets/BudgetCore.swift',
    'BudgetHost.swift': 'Features/Budgets/BudgetHost.swift',
    'BudgetUI.swift': 'Features/Budgets/BudgetUI.swift',
    'Capsule.swift': 'Features/Floating/Capsule.swift',
    'CapsulePlacement.swift': 'Features/Floating/CapsulePlacement.swift',
    'CapsuleDocking.swift': 'Features/Floating/CapsuleDocking.swift',
    'TaskMonitorCore.swift': 'Features/TaskMonitor/TaskMonitorCore.swift',
    'TaskLogReader.swift': 'Features/TaskMonitor/TaskLogReader.swift',
    'TaskMonitorService.swift': 'Features/TaskMonitor/TaskMonitorService.swift',
    'TaskMonitorUI.swift': 'Features/TaskMonitor/TaskMonitorUI.swift',
    'TaskMonitorHost.swift': 'Features/TaskMonitor/TaskMonitorHost.swift',
    'NotificationRouter.swift': 'Infrastructure/NotificationRouter.swift',
    'CapsuleHost.swift': 'Features/Floating/CapsuleHost.swift',
    'Chart.swift': 'Features/Usage/Chart.swift',
    'StatusDetail.swift': 'Features/MenuBar/StatusDetail.swift',
    'StatusMenu.swift': 'Features/MenuBar/StatusMenu.swift',
    'ControlFeedback.swift': 'UI/ControlFeedback.swift',
    'Quota.swift': 'Infrastructure/Quota.swift',
    'BackendEvents.swift': 'Infrastructure/BackendEvents.swift',
    'Runtime.swift': 'Infrastructure/Runtime.swift',
    'Termination.swift': 'Infrastructure/Termination.swift',
    'UsageChangeMonitor.swift': 'Infrastructure/UsageChangeMonitor.swift',
    'WindowProcess.swift': 'Infrastructure/WindowProcess.swift',
    'UsageNative.c': 'Native/UsageNative.c',
    'UsageNative.h': 'Native/UsageNative.h',
    'QuotaHelper.swift': 'Helpers/QuotaHelper.swift',
    'Summary.c': 'Helpers/Summary.c',
}


def macos_source(name: str, root: Path = ROOT) -> Path:
    return root / 'src/macos' / MACOS_SOURCES[name]
