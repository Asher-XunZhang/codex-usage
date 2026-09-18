# 安装与 Intel 故障排查

本指南面向 Intel 和 Apple Silicon Mac。安装发行版不需要额外安装 Python、Homebrew 或 Apple Command Line Tools。源码构建另见 [构建指南](BUILDING.md)。

当前发行版为 **v1.0.3**，使用 ad hoc 签名，未经过 Developer ID 签名或 Apple 公证。免费安装助手减少首次安装的手工步骤；它不能赋予 Apple 公证信任，也不能保证绕过组织管理策略。用户应先确认信任本仓库和本次发布。

## 安装助手

### 在线安装

在 macOS「终端」执行 [文档站的安装命令](https://asher-xunzhang.github.io/codex-usage/installation.html#macos)。命令从 v1.0.3 Release 下载独立的 `install.sh`，保存到临时目录，再用系统 `/bin/bash` 执行。也可先查看 [脚本源码](https://github.com/Asher-XunZhang/codex-usage/blob/main/scripts/install.sh)。

助手会：

1. 检测实际芯片并选择 Intel 或 Apple Silicon 发布包；在 Rosetta 终端中也会选择 Apple Silicon 版本。
2. 展示固定版本、来源、目标路径和隔离处理说明，要求在交互终端输入 `install` 确认。
3. 下载固定的 v1.0.3 ZIP，核对脚本中固定的 SHA-256，解压后检查 App 的签名完整性。
4. 仅处理新安装这份 App 的隔离属性，安装到 `~/Applications/Codex用量.app`，随后启动。

没有 `--yes` 或 `--force` 参数。取消确认、没有交互终端、校验失败或目标 App 已存在时，助手停止，不覆盖旧版本。不要通过 `sudo` 运行；脚本会拒绝以 root 身份安装。

SHA-256 用于核对下载内容是否与脚本预期一致；ad hoc 签名用于检查应用包内容是否完整。两者都**不能证明开发者身份，也不等于恶意软件审查**。助手仅处理本次安装的 App，不关闭 Gatekeeper，不清理其他应用或系统目录的隔离属性。Apple 对下载隔离与检查机制的说明见 [可信执行故障排查](https://developer.apple.com/forums/thread/706442)。

安装结束后，从 Finder「前往 → 个人」下的「应用程序」打开 `Codex用量.app` 即可。同一份已处理的 App 通常不需要每次启动都重新安装；重新下载或更新的副本可能重新受到首次运行检查。

### 从本地源码或 PR 分支预览

README 的在线脚本地址固定指向 v1.0.3 Release 的独立安装助手。评审尚未合并的 PR 时，在该分支仓库根目录运行：

```sh
/bin/bash scripts/install.sh --help
/bin/bash scripts/install.sh
```

仓库脚本安装其 `--help` 标明的固定发行 App，并不编译当前分支。发行源码快照中的脚本可能对应较早版本；安装 v1.0.3 请使用发行页的独立 `install.sh`。

### 离线安装

提前取得同一发行页中的独立 `install.sh` 和 [v1.0.3 发行页](https://github.com/Asher-XunZhang/codex-usage/releases/tag/v1.0.3)中与芯片匹配的完整 ZIP。把下例路径换成实际文件路径：

```sh
/bin/bash ./install.sh \
  --archive "$HOME/Downloads/codex-usage-desktop-v1.0.3-Intel.zip"
```

Apple Silicon 使用文件名以 `AppleSilicon.zip` 结尾的资产。离线模式同样校验固定摘要，只接受当前脚本对应架构的 v1.0.3 发布包；不能用于任意 ZIP、修改过的 App 或 GitHub 的 Source code 包。

| 架构 | v1.0.3 ZIP 的 SHA-256 文件 |
| --- | --- |
| Intel | [Intel.zip.sha256](https://github.com/Asher-XunZhang/codex-usage/releases/download/v1.0.3/codex-usage-desktop-v1.0.3-Intel.zip.sha256) |
| Apple Silicon | [AppleSilicon.zip.sha256](https://github.com/Asher-XunZhang/codex-usage/releases/download/v1.0.3/codex-usage-desktop-v1.0.3-AppleSilicon.zip.sha256) |

独立安装助手内置这两份最终 ZIP 的固定摘要。发行页另提供 `install.sh.sha256`，用于核对脚本文件。

### 安装参数

| 参数 | 用途 |
| --- | --- |
| `--archive /path/to/file.zip` | 使用本地发行 ZIP，不下载 App |
| `--destination /path/to/directory` | 改用当前用户可写的安装目录；须为绝对路径，参数是目录，不是 `.app` 路径 |
| `--no-open` | 安装成功后不启动 App，适合验收 |
| `--help` | 显示用法，不安装 |

独立安装验收可使用一个尚无同名 App 的目录：

```sh
/bin/bash ./install.sh \
  --destination "$HOME/Applications/CodexUsageTest" \
  --no-open
```

默认安装目录是 `~/Applications`，不是系统级 `/Applications`。不要为了写入系统目录而加 `sudo`。`--no-open` 时安装助手不会启动 GUI 或统计服务。

## 已有版本迁移与卸载

助手不自动替换旧 App，也不终止正在运行的进程。目标目录已有 `Codex用量.app` 时会停止；使用默认目录时，如 `/Applications` 已有同名 App，也会停止，避免安装两个常用副本。迁移步骤如下：

1. 在应用菜单选择「退出」，而不是仅关闭主面板；菜单栏和浮窗也应退出。
2. 用 Finder 将提示路径中的旧 App 移到一个单独的备份文件夹，或移到废纸篓；默认安装时检查 `~/Applications` 和 `/Applications` 两处。
3. 重新运行安装助手，安装完成后打开新位置的 App。默认应启动 `~/Applications/Codex用量.app`，避免混用备份副本。

安装助手不会改动 `~/.codex`，也不会修改 `~/Library/Application Support/CodexUsageDashboard/` 或应用偏好。应用启动后仍按正常逻辑读取本机 Codex 数据并维护自己的运行组件、索引和偏好。

卸载时先退出应用，再在 Finder 中移除安装的 App 即可；历史数据和支持目录会保留。不要删除 `~/.codex`，它属于 Codex 本身，可能包含账号凭据和聊天记录。

## 使用 Finder 手动安装

1. 在「 → 关于本机」查看芯片，使用 Safari 从 [正式发行页](https://github.com/Asher-XunZhang/codex-usage/releases/tag/v1.0.3)下载对应 ZIP。
2. 完整解压，移动其中的 `Codex用量.app` 到「应用程序」后双击。
3. 如提示开发者无法验证或 Apple 无法检查恶意软件，确认来源后，到「系统设置 → 隐私与安全」中点击「仍要打开」，再确认「打开」。该 App 会被保存为例外，之后通常可以直接双击启动。[Apple 官方说明](https://support.apple.com/en-us/102445)

“仍要打开”并非所有报错都会出现。若系统提示检测到恶意软件、App 已损坏，或 ZIP/签名校验失败，请停止使用该副本，从发行页重新下载并反馈；不要把所有启动错误都当作隔离问题。受组织管理的 Mac 可能需要联系管理员。

优先发送发行页链接，让接收者直接下载。Apple 记录过下载助手设置的特殊隔离标记导致已签名、公证 App 无法打开，而 Safari 下载同一文件可正常弹出确认的案例；这说明下载渠道可能影响结果，但 **Safari 下载不代表本项目已被公证或必然免除提示**。[Apple 技术讨论](https://developer.apple.com/forums/thread/767612)

## Intel 提示“应用程序无法打开”

已报告的一个 Intel/macOS **15.7.9 (24G830)** 案例中，v1.0.0 主程序为 `x86_64`、具有执行权限、App 签名完整性检查通过，但从终端运行得到 `Operation not permitted`；该用户移除这份 App 的隔离属性后确认可以启动。这支持“隔离触发的执行检查造成此次阻塞”，不能据此认定所有 Intel 启动错误都具有相同原因。

遇到类似情况，先使用安装助手从固定发行包重新安装；已有副本按上面的迁移步骤处理。助手将架构选择、完整性检查及这份 App 的隔离处理合并为一次明确确认的流程，无需逐条手工修复命令。

如果仍失败，可先收集以下只读信息。默认路径对应安装助手；若使用手动安装，将第一行改成实际 App 路径：

```sh
usage_app="$HOME/Applications/Codex用量.app"
sw_vers
uname -m
file "$usage_app/Contents/MacOS/CodexUsage"
ls -l "$usage_app/Contents/MacOS/CodexUsage"
codesign --verify --deep --strict --verbose=2 "$usage_app"
xattr -p com.apple.quarantine "$usage_app"
spctl --assess --type execute --verbose=2 "$usage_app"
```

`xattr` 提示没有该属性，表示这一层 App 未附带该属性，本身不代表应用损坏。由于当前版本未公证，`spctl` 拒绝也不等于下载损坏；请保留原始输出用于区分信任问题。`codesign` 验证通过只说明签名完整性，不代表系统必须允许启动。

| 现象 | 下一步 |
| --- | --- |
| Intel 上可执行文件显示 `arm64` | 重新下载 Intel ZIP；Intel 无法通过 Rosetta 运行 Apple Silicon App |
| SHA-256 不一致或 `codesign` 报错 | 停止安装，重新下载固定发布包；不要重新签名或强行修改该副本 |
| `Operation not permitted` | 结合隔离属性、策略检查及安装助手结果判断，不能只凭该句确定原因 |
| `Permission denied`、缺失动态库或启动后崩溃 | 保留完整错误，作为权限、依赖或运行兼容性问题排查 |
| 安装助手提示目标已存在 | 退出旧版本并按迁移说明保留备份，或选择独立验收目录 |

请在 [GitHub Issues](https://github.com/Asher-XunZhang/codex-usage/issues)中附上系统版本、芯片型号、下载的文件名、下载方式和以上输出；发送前可隐藏用户名及本地路径。不要上传 `~/.codex`、`auth.json`、聊天日志、账号令牌或整个支持目录。

## 支持范围

构建最低目标是 macOS 11；目标版本不等于完整兼容性验收。已有 Apple Silicon/macOS 26.5 实测及上述 Intel/macOS 15.7.9 启动反馈，尚未覆盖所有 Intel 机型、系统版本和首次下载场景。安装助手解决的是安装步骤与这次隔离问题的处理成本，不修复程序本身可能存在的运行时兼容性问题。产品验证边界见 [验证记录](VALIDATION.md)。

### 安装助手验收记录（2026-09-11）

在 Apple Silicon/macOS 26.5 上，用系统 `/bin/bash`、独立临时安装目录和 `--no-open` 验证，未替换日常使用的 App：

- 从 GitHub 实际下载 Apple Silicon v1.0.0，交互输入 `install` 后完成摘要校验、解压、架构及签名检查和安装。
- 使用原始 Intel v1.0.0 ZIP 完成相同的离线安装流程。此项在测试入口模拟 Intel 硬件识别，实际处理和验证的仍是 Intel 发布包。
- 两份安装后的 App 均无残留下载隔离属性，临时安装文件与锁目录已清理。
- 在真实 Rosetta 终端中，`uname -m` 返回 `x86_64` 时，助手仍正确选择 Apple Silicon 发布包。
- 两种架构的宿主和嵌套主面板，分别在空运行组件目录执行 `--check-runtime`，四次均退出 0；Python、SSL、SQLite 导入通过。Intel 的执行检查使用 Rosetta。

这些检查没有打开 GUI，也没有查询真实账号或读取聊天记录；不能替代实体 Intel/macOS 15.7.9 的完整交互验收。

新增安装器行为测试 19 项全部通过；仓库完整测试 `python3 test.py` 共 179 项，177 项通过、2 项 Windows 专用测试在 macOS 跳过，耗时 53.873 秒。维护者可运行 `python3 -m unittest discover -s tests -p test_installer.py -v` 重复合成安装包的行为测试；开发测试需要 Python，安装发行 App 不需要。
