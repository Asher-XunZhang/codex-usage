# Agent 开发入口

本仓的开发技能记录可复用的协作方式与产品原则。当前用户要求和已确认方案优先；历史案例用于理解原则，不自动成为新需求。

## 按任务加载

只阅读与任务有关的技能，组合使用即可，不要求每次加载全部内容。纯文档、文案或局部样式修正不需要启动完整设计与跨平台审查。

| 任务 | 技能 | 主要用途 |
| --- | --- | --- |
| 实现功能、修复缺陷、重构或优化 | [codex-usage-development](.agents/skills/codex-usage-development/SKILL.md) | 控制范围、快速实现、选择验证、明确交付证据 |
| 设计或评审界面、布局、交互、动效 | [codex-usage-product-design](.agents/skills/codex-usage-product-design/SKILL.md) | 用户心智、信息分组、视觉风格、状态连续性 |
| 修改异步数据、筛选、编辑保存、持久化或恢复 | [codex-usage-state-integrity](.agents/skills/codex-usage-state-integrity/SKILL.md) | 可信数据、状态归属、并发一致性、失败恢复 |
| 两端共同开发、平台移植、共享代码改动、合入审查或打包 | [codex-usage-cross-platform](.agents/skills/codex-usage-cross-platform/SKILL.md) | 行为兼容、原生适配、资源生命周期、发行完整性 |

这些技能位于仓库级 `.agents/skills/`，也可用 `$技能名` 显式调用。原有 `integrations/codex-token-usage/` 是供使用者查询用量的产品功能，不是开发技能；不要因开发本仓而自动安装它或启用其输出约定。

## 定位实现与证据

- [仓库目录介绍](docs/common/PROJECT-STRUCTURE.md)与 [贡献指南](CONTRIBUTING.md)说明文件归属、工具入口和验证边界。
- `src/macos/`：macOS 原生界面与宿主；`src/windows/`：Windows 原生界面与宿主；`src/backend/`：共享统计与本地服务。
- [文档导航](docs/README.md)按 `macos/`、`windows/`、`common/` 分类；架构、构建和验证记录按适用平台查阅，截图跟随平台目录。
- [两端对齐清单](docs/common/WINDOWS-MACOS-ALIGNMENT.md)、[Windows 说明](docs/windows/README.md)、[已确认布局](docs/windows/LAYOUT-PROPOSAL.md)提供平台差异与设计背景。
- 文档中的版本、截图、验收结果只证明其标注的快照。开始相关工作时核对当前源码、工作区改动与实际运行产物；提案不等于实现，历史通过不等于当前通过。

## 维护这些技能

只把能改变开发决策、跨多个功能成立的经验写入技能。具体缺陷、尺寸、时序常量、版本清单和测试日志留在实现、测试或对应项目文档中。新增规则前先判断能否改进已有原则，避免持续堆积特例。
