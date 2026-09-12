# Windows 原生用量与预算

提供用量概览、预算管理、托盘与浮窗显示，支持主题、筛选、CSV 导出和贴边收起。

## 运行

下载 Windows ZIP 并完整解压，运行 `CodexUsage.exe`，无需安装 .NET 或 Python。通过托盘菜单切换显示方式；浮窗“更多”提供主题及窗口行为设置。

用量统计读取本机 Codex 数据目录。预算为本地规则，实际账号额度以服务提供方为准。

## 构建和验证

在源码根目录运行 `python scripts/build_windows.py --dotnet <dotnet.exe>`。需 .NET SDK 10.0.401；工具下载并校验私有 Python 3.14.7。运行 `python -B test.py`，以及 `python scripts/test_windows_lifecycle.py --app build/windows-x64/CodexUsage.exe`。

打包使用 `python scripts/package_windows.py`，验包使用 `python scripts/verify_windows.py`。测试应使用隔离目录；前台交互测试会显示窗口，请选择合适时机运行。
