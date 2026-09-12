# 参与开发

先从 [目录介绍](docs/common/PROJECT-STRUCTURE.md)定位实现；平台操作与构建分别见 [macOS](docs/macos/BUILDING.md) 和 [Windows](docs/windows/README.md)。Agent 按 [AGENTS.md](AGENTS.md)加载相关技能即可。

## 代码放在哪里

- 桌面实现放在 `src/`：`backend/` 负责共同统计，`macos/`、`windows/` 负责原生界面和系统接入。平台内按功能归属文件，多个功能确实共用的组件再放入 `UI/` 或 `Infrastructure/`。
- 构建与发行工具放在 `tools/<平台>/`；确有两端调用者的工具放在 `tools/common/`。`scripts/` 和根脚本保留旧命令兼容，不添加新的实现。
- 外部测试放在 `tests/common/`、`macos/`、`windows/` 或 `tooling/`。Windows 现有内置自测与预览仍在项目内 `Diagnostics/`，保留其编译和调用关系。
- 文档、截图按 `docs/common/`、`macos/`、`windows/` 归档；开发技能放在 `.agents/skills/`，可选产品集成放在 `integrations/`。

## 开发与验证

从主线创建功能分支，先明确预期行为和受影响平台。已有方案直接执行；需要确认的新交互先提供可评审方案。保留其他工作区改动，目录迁移与行为变更分别说明。

在仓库根运行 `python -B test.py`。检查跳过原因：Windows 原生测试需指定本次构建的 `CODEX_USAGE_WINDOWS_EXE`，Swift/AppKit 测试必须在 macOS 运行。按改动风险补充相关行为、界面或发行验证，不把编译通过当作实操通过。

新工具采用 `python -m tools.<平台>.<工具>`；构建结果进入 `build/<平台>/`，分发包进入 `dist/<平台>/`。使用临时数据做写入、恢复与故障测试；缓存和本机实验放在忽略提交的 `.local/`。

## 提交 PR

说明解决的问题、变化后的行为、受影响平台和实际验证结果。共享后端、进程协议或分发变更应核对两端调用方；UI 改动附有代表性的实图或操作证据。明确未验证的设备和平台，历史截图与旧版本测试不作为当前证明。

不要提交用户日志、认证、配置、数据库、下载缓存或编译产物。依赖变更同时维护版本、校验和许可证；发布前验证实际发行包及其解包源码。
