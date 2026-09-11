# 安装与首次启动

发行 App 已包含离线运行组件，**无需另装 Python、Node.js、Homebrew 或开发工具**。使用过 Codex 才会有本机记账；账号额度查询需要本机 Codex 已登录。

**选择一种安装方式即可：** 推荐使用快捷安装助手；也可手动下载 ZIP。已有版本请先按[迁移步骤](#已有版本)处理。

## 快捷安装助手（推荐）

安装助手已通过 [PR #1](https://github.com/Asher-XunZhang/codex-usage/pull/1) 于 **2026-09-11 合并到 `main`**。以下入口固定到合并提交 [`d6a9407`](https://github.com/Asher-XunZhang/codex-usage/blob/d6a9407a86325eaf210e7d69f58a63ba4f5eab8e/scripts/install.sh)，脚本内容与合并前完成验证的版本相同。安装的 App 仍是正式的 **v1.0.0**，原始发行 ZIP 未更换；安装助手的验证边界见[验证与兼容性](./validation.md)。 安装助手不会编译当前源码，因此不包含[尚未发布的额度配色](./user-guide.md#当前源码-额度弧线配色-未发布)。

助手自动识别芯片，校验固定 ZIP 摘要、可执行文件架构及 App 签名完整性。用户在交互终端输入 `install` 后，才会处理新安装这份 App 的下载隔离属性。默认安装到 `~/Applications/Codex用量.app`，无需管理员密码；不会改变系统全局安全设置。

请先确认信任[仓库及脚本来源](https://github.com/Asher-XunZhang/codex-usage/blob/d6a9407a86325eaf210e7d69f58a63ba4f5eab8e/scripts/install.sh)，再在 macOS「终端」执行：

```sh
usage_installer_dir="$(mktemp -d "${TMPDIR:-/tmp}/codex-usage-install.XXXXXX")" &&
curl --fail --show-error --location --proto '=https' --proto-redir '=https' --tlsv1.2 \
  --connect-timeout 20 --max-time 60 --retry 2 \
  'https://raw.githubusercontent.com/Asher-XunZhang/codex-usage/d6a9407a86325eaf210e7d69f58a63ba4f5eab8e/scripts/install.sh' \
  --output "$usage_installer_dir/install.sh" &&
/bin/bash "$usage_installer_dir/install.sh"
```

脚本先保存为文件再运行，不使用管道执行。没有 `--yes` 或 `--force`；来源确认不能省略。**摘要校验和 ad hoc 签名完整性不等于发布者身份认证，也不是 Apple 公证。**

### 离线安装与参数

离线安装是另一种方式，**不用先执行上面的在线安装命令**。在可联网时，将[固定版本安装脚本](https://raw.githubusercontent.com/Asher-XunZhang/codex-usage/d6a9407a86325eaf210e7d69f58a63ba4f5eab8e/scripts/install.sh)保存为 `~/Downloads/codex-usage-install.sh`，并下载匹配芯片的原始 ZIP。准备好两个文件后，在离线 Mac 的终端执行：

```sh
/bin/bash "$HOME/Downloads/codex-usage-install.sh" \
  --archive "$HOME/Downloads/codex-usage-desktop-v1.0.0-Intel.zip"
```

Apple Silicon 应选择对应的 `AppleSilicon.zip`。脚本只接受该架构的原始 v1.0.0 ZIP，不接受重新压缩、改名后内容不符或其他版本的包。

| 参数 | 含义 |
| --- | --- |
| `--archive ZIP` | 使用已下载的发行包，仍核对固定摘要 |
| `--destination DIR` | 安装到指定绝对路径目录，须为当前用户可写 |
| `--no-open` | 安装完成后不打开 App，适合独立验收 |
| `--help` | 查看帮助 |

不要用 `sudo` 运行。新安装的这份 App 通常不需要每次启动都重复安装命令；重新下载或更新的副本可能再次触发检查。

### 已有版本

助手不会终止进程或覆盖已有 App。默认安装时，`~/Applications` 或 `/Applications` 已有同名 App 都会导致停止。

1. 在应用菜单选择「退出」，确保菜单栏、浮窗及主面板退出。
2. 用 Finder 将旧 App 移到单独的备份目录或废纸篓。
3. 再运行安装助手，完成后打开新安装位置的 App。

旧 App 的移除不要求删除 `~/.codex`、支持目录或偏好。不要同时打开两个位置的副本。

## 手动下载安装（可选）

在「 → 关于本机」确认芯片，使用 Safari 从官方发行页下载匹配的 ZIP：

| Mac 芯片 | 安装包 |
| --- | --- |
| Intel | [下载 Intel ZIP](https://github.com/Asher-XunZhang/codex-usage/releases/download/v1.0.0/codex-usage-desktop-v1.0.0-Intel.zip) |
| Apple Silicon（M 系列） | [下载 Apple Silicon ZIP](https://github.com/Asher-XunZhang/codex-usage/releases/download/v1.0.0/codex-usage-desktop-v1.0.0-AppleSilicon.zip) |

1. 完整解压 ZIP，将其中的 `Codex用量.app` 拖到「应用程序」。GitHub 的 **Source code** 包用于开发，不能直接双击运行。
2. 双击 App。首次启动会校验并解压随包运行组件，首次历史索引也可能需要等待。
3. 打开主面板查看统计，或切换到菜单栏、圆形浮窗。使用方法见[使用指南](./user-guide.md)。

最低构建目标为 macOS 11，不代表所有机型和系统版本都已验收。当前包未经 Developer ID 签名或 Apple 公证。

### 首次打开时的系统提示

如果提示开发者无法验证或 Apple 无法检查应用，确认来源后，到「系统设置 → 隐私与安全」点击「仍要打开」，再确认「打开」。macOS 会保存该 App 的例外，之后通常可直接启动。[Apple 官方说明](https://support.apple.com/en-us/102445)

“仍要打开”不一定会出现在所有错误中。若出现仅有“好”按钮的“应用程序无法打开”，请阅读 [Intel 故障排查](./troubleshooting.md)，或参考上面的安装助手流程。组织管理策略可能限制安装，需要遵循本机管理员的设置。

## 核对下载文件

发行页提供同名 `.zip.sha256`。固定 v1.0.0 摘要如下：

| 架构 | ZIP 的 SHA-256 |
| --- | --- |
| Intel | `4dbcd5b12cd4c96580b223dafdb612dc6f2a268913923e8a5f788278dba0e4d2` |
| Apple Silicon | `18bad090d4dd52a4d31177fe8cca0be9d52dc8e165650765a16cc6110b29c220` |

```sh
shasum -a 256 "$HOME/Downloads/codex-usage-desktop-v1.0.0-Intel.zip"
```

摘要不符时停止使用该副本，从正式发行页重新下载。校验说明见[验证与兼容性](./validation.md)。
