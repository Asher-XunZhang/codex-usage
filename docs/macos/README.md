# macOS 文档

本目录描述 macOS 原生实现与相关发行流程；两端差异见 [Windows → macOS 对齐清单](../common/WINDOWS-MACOS-ALIGNMENT.md)。

公开下载为 v1.0.2（Apple Silicon / Intel），在同名 Release 补充 macOS 安装包。包含预算提醒、任务监控、浮窗交互、统一设置与菜单栏详情；安装不会自动修改原有 Codex 配置。

2026-09-14 当前源码已包含任务监控、四方向展开、全区域拖动、四边隐藏、统一设置、预算诊断和事件等待优化。9 月 15 日补齐两屏接缝停靠与自动隐藏。发行包校验与仍未实测的路径见 [v1.0.2 发行说明](RELEASE-1.0.2.md)。

- [安装与 Intel 排障](INSTALL.md)
- [构建与发布](BUILDING.md)
- [架构与取舍](ARCHITECTURE.md)
- [预算与提醒](BUDGETS.md)
- [任务监控](TASK-MONITOR.md)
- [本轮特性同步验收](FEATURE-SYNC-VALIDATION.md)
- [Windows 新增能力的 macOS 适配梳理](../common/MACOS-ADAPTATION-BACKLOG.md)
- [macOS 适配分析与实现取舍](ADAPTATION-DECISIONS.md)
- [特性同步设计（已批准）](FEATURE-SYNC-PROPOSAL.md)
- [2026-09-14 本机安装与调试验证](LOCAL-INSTALL-20260914.md)
- [后端空闲等待优化与测量边界](IDLE-WAITS.md)
- [本地验证工具](DEVELOPMENT-TOOLS.md)
- [验证记录与适用范围](VALIDATION.md)

截图位于 `images/`，具体版本与数据来源以引用它们的文档为准。

[返回文档导航](../README.md) · [Windows 文档](../windows/README.md)
