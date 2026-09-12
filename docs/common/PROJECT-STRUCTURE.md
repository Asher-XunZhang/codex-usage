# 仓库目录介绍

本仓采用**顶层按职责、源码按平台、平台内部按功能**的单仓结构。macOS 使用 Swift/AppKit，Windows 使用 C#/WPF，共同统计逻辑使用 Python。目录调整保留现有语言、应用身份、数据位置与运行时装配方式。

## 目录导航

```text
codex-usage/
├── README.md                 项目介绍与平台入口
├── CONTRIBUTING.md           贡献者开发与验证指南
├── AGENTS.md                 agent 技能入口
├── CHANGELOG.md / LICENSE / THIRD-PARTY.md
├── build.py / package.py     macOS 旧命令兼容入口
├── test.py / pytest.ini      测试入口与发现配置
├── .github/                  CI、Issue 与 PR 模板
├── .agents/skills/            可复用的开发技能
├── src/
│   ├── backend/              公共解析、索引、本地服务与进程监测
│   ├── macos/                macOS 原生应用
│   └── windows/              Windows 原生应用及项目文件
├── tools/
│   ├── common/               根路径、下载校验、发行源码清单
│   ├── macos/                构建、打包、验包、预览与采样
│   └── windows/              构建、打包、验包及进程/监控验证
├── scripts/                  独立安装助手与旧工具命令兼容入口
├── tests/
│   ├── common/               公共行为与后端测试
│   ├── macos/                Swift/AppKit/C 测试驱动
│   ├── windows/              Windows 行为与差分测试驱动
│   └── tooling/              构建、下载、安装、打包与目录约束
├── resources/
│   ├── macos/                固定运行时清单、许可证与声明
│   └── windows/              固定运行时清单与依赖许可证
├── integrations/
│   └── codex-token-usage/    可独立复制的用量查询技能与 Stop hook
├── docs/
│   ├── README.md             文档导航
│   ├── common/               共同维护说明、两端对齐清单
│   ├── macos/                macOS 文档与图片
│   └── windows/              Windows 文档与分版本图片
├── website/                  独立的 VitePress 文档站工程
├── build/                    新构建、报告与预览；忽略提交
├── dist/                     分平台发行包；忽略提交
└── .local/                   SDK、下载缓存与本机实验；忽略提交
```

## 源码如何归属

| 位置 | 职责 |
| --- | --- |
| `src/backend/` | 保持现有 Python 模块平铺及独立脚本导入方式；目录搬移不要求安装新的 Python 包。 |
| `src/<平台>/App/` | 程序入口、宿主与窗口装配。 |
| `src/<平台>/Features/` | 按 Usage、Budgets、Floating 等功能集中界面、状态与相关逻辑；macOS 系统入口为 MenuBar，Windows 为 Tray，并有 TaskMonitor。 |
| `src/<平台>/UI/` | 多个功能共用的控件、主题和视觉反馈。 |
| `src/<平台>/Infrastructure/` | 进程、数据库、后端通信、账号查询与系统接入。 |
| `src/macos/Native/`、`Helpers/` | 应用调用的 C 桥接，以及独立额度/摘要辅助程序。 |
| `src/windows/Resources/` | 应用图标与 manifest；项目文件仍位于 `src/windows/`。 |
| `src/windows/Diagnostics/` | 现有内置自测和预览，保留在项目中编译并调用真实组件；外部 Python 驱动位于 `tests/windows/`。 |

一个功能的状态、逻辑与界面优先放在一起。只有确实被多个功能共用的内容才上移到公共目录，不预建没有内容的层级。测试间复用使用明确的模块引用；出现独立、共同使用的样本时再建立 `tests/fixtures/`，不复制生产实现作为测试替身。

正式源码不依赖 `tools/`、外部 `tests/` 或 `website/`。工具可以读取和构建源码，测试可以调用生产组件。macOS 构建用的图标生成器位于 `tools/macos/Icon.swift`，预览驱动位于 `tools/macos/preview/`。

## 工具入口与输出

以下新入口在仓库根执行；Python 使用本机可用的 `python` 或 `python3`。具体参数与平台前提见 [macOS 构建](../macos/BUILDING.md)和 [Windows 构建](../windows/README.md#构建与验证)。

| 用途 | 入口 | 默认输出 |
| --- | --- | --- |
| macOS 运行时 | `python -m tools.macos.fetch_runtime --arch arm64` | `.local/macos-runtime/` |
| macOS 构建 | `python -m tools.macos.build --arch arm64` | `build/macos/arm64/Codex用量.app` |
| macOS 打包 / 验包 | `python -m tools.macos.package` / `python -m tools.macos.verify_release` | `dist/macos/` |
| Windows 构建 | `python -m tools.windows.build` | `build/windows/x64/` |
| Windows 打包 / 验包 | `python -m tools.windows.package` / `python -m tools.windows.verify_release` | `dist/windows/` |
| 全部外部测试 | `python -B test.py` | 控制台结果；跳过原因单列 |
| 文档链接与图片 | `python -m tools.common.check_docs` | 校验仓内或解包源码中的相对引用 |
| 网站 | 在 `website/` 执行 `pnpm build`、`pnpm check` | `website/.vitepress/dist/` |

报告和预览优先放在 `build/reports/<平台>/`、`build/previews/<平台>/`；需要保留多次候选结果时加独立名称。构建及缓存目录由工具按需生成，旧产物无需随源码搬移。

旧根 `build.py`、`package.py` 与 `scripts/*.py` 保留参数、退出码及原默认输出：例如 Windows 旧构建仍输出 `build/windows-x64/`，旧打包仍输出 `dist/`。兼容层只负责转发，新增实现放入 `tools/`；混用新旧命令时应显式指定相同产物路径。`scripts/install.sh` 继续作为可独立下载的 macOS 安装助手。

根定位由 [tools/common/paths.py](../../tools/common/paths.py)集中处理，使用源码标识识别仓库，不依赖 `.git` 或当前工作目录。旧脚本可通过绝对路径从其他目录执行；模块入口从仓库根执行。发行 ZIP 的 `source/` 也具有同样的工具结构。

## 资源、发行与测试边界

受版本维护的清单在 `resources/<平台>/runtimes/manifest.json`，许可证放在对应平台资源目录。下载的运行时、.NET SDK 与包缓存放在 `.local/`；构建保留固定大小、SHA-256 与许可证校验，以及已有 App 资源的 macOS 离线复用方式。

**仓库源码布局和应用运行时布局分开维护。** Python 源码来自 `src/backend/`，Windows 产物仍使用应用旁的 `backend/`，macOS 仍使用 `Contents/Resources/backend/`。私有 Python 搜索路径、用户配置/预算/历史数据目录不随仓库目录迁移。

两端源码包使用 [tools/common/distribution.py](../../tools/common/distribution.py)的明确清单，包含重建桌面应用所需的两端源码、公共后端、工具、测试、资源、文档及可选集成；不包含网站依赖、构建缓存或用户数据。它是桌面发行源码，不是含网站工程的完整 Git 仓库镜像。完整仓库开发使用 Git 克隆。

测试通过 `test.py` 或 `pytest` 从分平台测试包发现。Windows 原生测试必须将 `CODEX_USAGE_WINDOWS_EXE` 指向本次构建；macOS 原生测试须在 macOS 运行。目录迁移需按旧新模块映射比较测试标识，不能只看总数；发行验收还要实际在解包源码中运行，避免源码清单遗漏被仓库环境掩盖。

CI 位于 `.github/workflows/`。公共后端与工程工具变更覆盖两端；平台实现、文档与网站按其依赖范围验证。新增检查不代表已经完成远端 CI 或另一平台实机验收。

## 旧路径速查

| 旧位置 | 当前实现位置 |
| --- | --- |
| `Sources/` | `src/macos/`；`Icon.swift` 在 `tools/macos/` |
| `windows/` | `src/windows/`，按 App、Features、UI、Infrastructure、Diagnostics 分组 |
| `backend/` | `src/backend/` |
| 根构建/打包实现、`scripts/*.py` | `tools/macos/`、`tools/windows/`；共同下载与清单在 `tools/common/` |
| 平铺 Python 测试 | `tests/common/`、`macos/`、`windows/`、`tooling/` |
| `resources/runtimes/manifest.json` | `resources/macos/runtimes/manifest.json` |
| `resources/runtimes/windows-manifest.json` | `resources/windows/runtimes/manifest.json` |
| `resources/third-party-licenses/`、`resources/licenses/` | `resources/macos/third-party-licenses/`、`resources/windows/licenses/` |
| `skill/` | `integrations/codex-token-usage/` |

历史验收记录与已发布版本的 URL 保留其版本含义；当前源码链接指向新位置。开发新功能从本页与平台指南入手，历史提案用于理解设计背景。

[文档导航](../README.md) · [贡献指南](../../CONTRIBUTING.md) · [Agent 入口](../../AGENTS.md)
