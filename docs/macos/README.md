# macOS 文档

本目录描述 macOS 原生实现与相关发行流程；两端差异见 [Windows → macOS 对齐清单](../common/WINDOWS-MACOS-ALIGNMENT.md)。

当前 macOS 版本为 **v1.0.3**（Apple Silicon / Intel），新增单窗口弧线调色和微弧融合贴边轮廓，修复预览偏移及无任务时的侧签留白。延续预算提醒、任务监控、统一设置与菜单栏详情。

- [v1.0.3 发行说明与验证边界](RELEASE-1.0.3.md)
- [新版视图与交互图解](FLOATING-1.0.3.md)
- [安装与 Intel 排障](INSTALL.md)
- [构建与发布](BUILDING.md)
- [架构与取舍](ARCHITECTURE.md)
- [预算与提醒](BUDGETS.md)
- [任务监控](TASK-MONITOR.md)
- [折叠浮窗弧线配色](ARC-COLORS.md)
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

### 贴边浮窗

贴边时使用两端平滑接入边缘的微弧轮廓，适配上、下、左、右四个方向。无任务时只显示居中的额度；运行中、等待下一轮、待确认或存在未读消息时，按需扩展并显示状态。已结束且已读后恢复紧凑尺寸。尺寸变化沿原贴边中心展开，屏幕边角空间不足时限制在可用区域内；开启系统“减少动态效果”时直接切换。
