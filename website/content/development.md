# 开发、构建与本地验证

普通使用者直接[下载发行包](./installation.md)，无需开发环境。本页的模块入口适用于整理后的当前源码和 Windows v1.0.2；历史 macOS v1.0.1 tag 仍使用该版本原有目录与命令。

## 选择源码版本

在新的目录中克隆仓库：

```sh
git clone https://github.com/Asher-XunZhang/codex-usage.git
cd codex-usage
```

- 重现 Windows 正式包：`git checkout --detach v1.0.2`。
- 开发新改动：`git switch -c codex/my-change origin/main`。
- 重现 macOS 旧正式包：`git checkout --detach v1.0.1`，使用[该 tag 的构建文档](https://github.com/Asher-XunZhang/codex-usage/blob/v1.0.1/docs/BUILDING.md)。不要在旧 tag 中调用下文的新模块路径。

v1.0.2 仅发布 Windows 包；从当前源码构建 macOS 属于开发构建，不等于已经有 macOS v1.0.2 正式包。

## 当前目录与职责

| 目录 | 内容 |
| --- | --- |
| `src/backend/` | 共享日志解析、增量索引、本地服务和父进程监测 |
| `src/macos/` | Swift / AppKit 原生应用，按 App、Features、Infrastructure、UI 等职责组织 |
| `src/windows/` | C# / WPF 项目；Features 下区分 Usage、Budgets、TaskMonitor、Floating、Tray |
| `src/windows/Diagnostics/` | 调用真实 Windows 组件的内置测试与合成预览 |
| `tools/common/`、`tools/macos/`、`tools/windows/` | 公共工具与各平台构建、打包、验证 |
| `tests/common/`、`tests/macos/`、`tests/windows/`、`tests/tooling/` | 按适用范围组织的外部测试 |
| `resources/macos/`、`resources/windows/` | 固定运行时清单、许可证与资源 |
| `integrations/codex-token-usage/` | 可选 Token 技能与 Stop hook |
| `docs/common/`、`docs/macos/`、`docs/windows/` | 工程说明、平台文档与验收记录 |
| `website/` | 本文档站，独立构建，不参与桌面运行 |

更详细的文件归属见[仓库目录介绍](https://github.com/Asher-XunZhang/codex-usage/blob/main/docs/common/PROJECT-STRUCTURE.md)与[贡献指南](https://github.com/Asher-XunZhang/codex-usage/blob/main/CONTRIBUTING.md)。

仓库源码目录与发行运行时布局分开：Windows 包仍在 EXE 旁使用 `backend/`，macOS 使用 `Contents/Resources/backend/`。用户设置、预算与 Codex 日志目录不随仓库迁移。

## Windows 构建与验证

需要 Windows、用于构建脚本的 Python 3.10+，以及 .NET SDK **10.0.401**。在仓库根目录运行：

```powershell
python -B -m tools.windows.build --dotnet 'C:\Path\To\dotnet.exe'
$env:CODEX_USAGE_WINDOWS_EXE = (Resolve-Path 'build/windows/x64/CodexUsage.exe').Path
& 'build/windows/x64/python/python.exe' -E -s -B test.py
python -B -m tools.windows.test_lifecycle --app build/windows/x64/CodexUsage.exe --output build/reports/windows/lifecycle.json
python -B -m tools.windows.package
python -B -m tools.windows.verify_release --report build/reports/windows/verification.json
```

`--dotnet` 可以省略，脚本会查找 `.local/dotnet/dotnet.exe` 和 PATH。首次构建需要联网获取固定依赖；运行时来源、大小与 SHA-256 位于 `resources/windows/runtimes/manifest.json`。产物位于 `build/windows/x64/`，ZIP 和整体 SHA-256 位于 `dist/windows/`。

原生测试必须指向本次构建的 `CODEX_USAGE_WINDOWS_EXE`；没有指定时的跳过不是通过。离线发行验证还会检查包内清单、架构、随包 Python、合成数据库和解包后源码，不能只验证工作区里能运行。

### 合成预览与不干扰检查

```powershell
& 'build/windows/x64/CodexUsage.exe' --demo
& 'build/windows/x64/CodexUsage.exe' --preview --output "$PWD/build/previews/windows/main"
python -B -m tools.windows.verify_release --app build/windows/x64 --background
```

`--demo` 使用隔离数据。后台验证会跳过可见 Popup 与真实键盘焦点等前台检查，并在报告说明；它不能替代完整交互验收。不要用真实任务、账号或聊天记录制作公开截图。

完整参数、监控与窗口验证入口见[Windows 工程指南](https://github.com/Asher-XunZhang/codex-usage/blob/main/docs/windows/README.md#构建与验证)。

## macOS 当前源码构建

需要 macOS、Apple Command Line Tools 和 Python 3.10+。如尚未安装 Command Line Tools，可运行 `xcode-select --install`。

```sh
python3 -B test.py
python3 -m tools.macos.fetch_runtime --arch arm64
python3 -m tools.macos.build --arch arm64
```

结果位于 `build/macos/arm64/Codex用量.app`。Intel 将架构改为 `x86_64`，产物位于 `build/macos/x86_64/`；构建也支持 `universal`。按架构顺序构建，避免并行写入共用缓存。运行时清单位于 `resources/macos/runtimes/manifest.json`。

完成需要的两种架构构建后，可运行：

```sh
python3 -m tools.macos.package
python3 -m tools.macos.verify_release
```

当前模块入口将发行包写入 `dist/macos/`。构建、签名完整性和 Rosetta 执行不能代替实体 Intel 与实际首次下载验收，也不会为应用增加 Developer ID 签名或 Apple 公证。

### 同步当前源码的浮窗示意图

```sh
python3 -m tools.macos.render_previews --output build/previews/macos
```

工具使用真实绘制组件和合成数据生成离屏预览。检查图片与状态清单后，再把需要的 PNG 复制到 `docs/macos/images/`，明确标注对应版本。v1.0.1 tag 中使用原来的 `scripts/render_previews.py` 与 `docs/images/`，不能混用新路径。

## 旧命令兼容与发行源码

当前根目录 `build.py`、`package.py` 与 `scripts/*.py` 保留兼容入口及原有默认输出；新增逻辑在 `tools/`。混用新旧命令时应显式对齐输出路径，不要把旧目录的产物当成本次构建。

发行 ZIP 的 `source/` 带有重建桌面应用所需源码、工具、测试、资源与文档；它不是含网站工程和依赖的完整 Git 仓库镜像。做完整仓库开发请使用 Git 克隆。已发布同名 ZIP 不应重新打包覆盖。

## 构建文档站

网站沿用 VitePress，依赖版本锁定在 `website/package.json` 和锁文件中。使用 Node.js 24 与项目指定的 pnpm 版本，在 `website/` 执行：

```sh
pnpm install --frozen-lockfile
pnpm build
pnpm check
```

静态结果位于 `website/.vitepress/dist/`，使用 GitHub Pages 的 `/codex-usage/` 子路径。链接检查覆盖页面、图片、锚点与三份正式下载入口；远端发行资产是否已上传属于独立发布检查。

网站只复制文档目录中公开的图片，不从本机用户数据生成页面。构建网站不会构建、重启或操作桌面应用。

[平台支持与差异](./platforms.md) · [验证与兼容性](./validation.md) · [当前版本记录](https://github.com/Asher-XunZhang/codex-usage/blob/main/CHANGELOG.md)
