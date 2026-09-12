# Windows 任务监控

提供用量、预算与任务监控。可选择正在执行的任务，设置本轮或每轮提醒，查看未读消息；取消监控只停止提醒，不会停止 Codex 任务。

## 运行

下载 Windows ZIP 并完整解压，运行 `CodexUsage.exe`，无需安装 .NET 或 Python。通过托盘菜单切换显示方式；浮窗“更多”提供主题及窗口行为设置。

用量统计和任务监控读取本机 Codex 数据目录，任务监控与用量筛选独立。预算为本地规则，实际账号额度以服务提供方为准。

## 构建和验证

在源码根目录运行 `python scripts/build_windows.py --dotnet <dotnet.exe>`。需 .NET SDK 10.0.401；工具下载并校验私有 Python 3.14.7。运行 `python -B test.py`，以及 `python scripts/test_windows_lifecycle.py --app build/windows-x64/CodexUsage.exe`。

打包使用 `python scripts/package_windows.py`，验包使用 `python scripts/verify_windows.py`。测试应使用隔离目录；前台交互测试会显示窗口，请选择合适时机运行。

监控检查：`CodexUsage.exe --monitor-tests`；宿主检查：`python scripts/test_windows_monitor.py --app build/windows-x64/CodexUsage.exe`。系统通知是否显示受 Windows 通知设置、勿扰及全屏状态影响，应用内仍保留消息。
