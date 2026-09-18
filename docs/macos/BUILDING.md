# 构建与发布

本页介绍 macOS 构建。Windows x64 的独立构建、打包与验证命令见 [Windows 使用与开发说明](../windows/README.md)。两个平台共用 `src/backend/`，界面分别位于 `src/macos/` 和 `src/windows/`。

## 环境

需要 macOS、Apple Command Line Tools（`xcode-select --install`）和 Python 3.10+。构建工具只使用 Python 标准库。Swift/C 程序目标为 macOS 11；编译目标并不等于所有旧系统或设备都经过实测。

## 从干净克隆构建

在仓库根目录执行：

```sh
python3 test.py
python3 -m tools.macos.fetch_runtime --arch arm64
python3 -m tools.macos.build --arch arm64
```

结果位于 `build/macos/arm64/Codex用量.app`。Intel 将两条命令中的架构改成 `x86_64`；也可选择 `universal` 生成双架构 App。按架构连续构建，避免并行写入共用的图标和模块缓存。

运行组件固定为 Astral python-build-standalone 的 CPython 3.12.14 / 20260901 发布。`resources/macos/runtimes/manifest.json` 记录官方下载地址、大小和 SHA-256；下载先写临时文件，验证成功后才保存。已经存在但不匹配的缓存会报错，不静默覆盖。

若已有完整发行 App，可完全离线复用其资源。在发行 ZIP 的 `source/` 目录中执行：

```sh
python3 -m tools.macos.build --arch arm64 --runtime-source '../Codex用量.app/Contents/Resources'
```

## 测试

`python3 test.py` 使用合成日志和独立临时目录，覆盖解析、索引、刷新、信号退出、原生界面与进程生命周期。Swift/AppKit 测试需要 macOS；其它平台会跳过相应测试。Windows 原生回归需先构建 Windows 应用，并按 [Windows 说明](../windows/README.md)指定测试使用的可执行文件。

实际 App 的离线运行时与偏好初始化可在不打开窗口的情况下检查：

```sh
python3 - <<'PY'
from pathlib import Path
import os
import subprocess
import tempfile

app = Path('build/macos/arm64/Codex用量.app').resolve()
for executable in ['Contents/MacOS/CodexUsage',
                   'Contents/Helpers/CodexUsageMain.app/Contents/MacOS/CodexUsage']:
    with tempfile.TemporaryDirectory(prefix='codex-usage-bootstrap-') as temporary:
        environment = dict(os.environ, CODEX_USAGE_DESKTOP_BASE=temporary)
        subprocess.run([str(app / executable), '--check-runtime'], env=environment, check=True)
PY
```

以上命令会在独立临时目录解压运行组件，并自动清理测试目录。它们不运行 GUI，也不读取 Codex 聊天记录。主面板嵌套组件使用父 App 的资源，因此需要分别检查两个 bundle。Intel 本机检查时将路径中的 `arm64` 改为 `x86_64`。

## 打包

先依次构建两种架构，再执行：

```sh
python3 -m tools.macos.package
python3 -m tools.macos.verify_release
```

发行文件在 `dist/macos/`。ZIP 包含 App、文档、项目许可证、第三方许可证、可构建源码以及逐文件校验清单；外部同名 `.zip.sha256` 用于校验整个下载文件。只使用明确的文件清单，不打包日志、数据库、认证、偏好、编译缓存或本机实验数据。

构建使用 ad hoc 签名，没有 Developer ID 身份认证或 Apple 公证。应用自身不移除隔离属性；可选的 [`scripts/install.sh`](https://github.com/Asher-XunZhang/codex-usage/blob/main/scripts/install.sh) 在用户明确确认后，对固定发布包校验 SHA-256 与签名完整性，再仅移除新安装这份 App 的隔离属性。它不改变系统全局安全设置。安装流程和限制见 [安装指南](INSTALL.md)。

v1.0.3 的独立安装助手随 Release 发布，固定使用本版两种架构 ZIP 及各自的 SHA-256，不自动追踪 `latest`；既有版本的发布资产保持不变。发布新版本时，先定稿并验收两个架构的 ZIP，再根据最终资产的摘要，更新安装助手中的固定版本、资产名和预期 SHA-256，上传后核对远端摘要；不要只替换下载 URL 或将运行时从同一下载位置取回的摘要当作固定校验值。下载摘要和 ad hoc 签名完整性不等于发布者身份认证。

v1.0.2 是在已有 Windows Release 中补充 macOS 包，保留原标签与 Windows 资产。macOS 源码使用 Release 单独标注的提交与 ZIP 的 `source/` 快照；`BUILD-INFO.json` 记录编译提交和源码摘要，验证器核对实际源码字节。打包前提交源码并构建，ZIP 定稿后再用独立提交更新安装器摘要，避免自引用。

ZIP 内的 `source/` 是打包时的源码快照，其中的安装助手可能仍指向旧版本，具体以该脚本的 `--help` 输出为准。不要为了回写 ZIP 自身的摘要而再次打包、覆盖已发布的同名 ZIP：这会改变摘要、形成自引用，并使已有安装助手的固定校验失效。v1.0.1 的 `install.sh` 及 `install.sh.sha256` 在最终 ZIP 确定后生成，作为独立 Release 资产上传，并同步到 `main`；源码 tag 和已发布 ZIP 不再改写。README 的安装入口指向该独立资产。

发行验收需要分别覆盖：包结构与签名完整性、离线组件启动、真实 Intel/Apple Silicon 启动，以及在干净设备上从 Safari 等实际渠道首次下载后的信任流程。通过本机 `codesign --verify` 或 Rosetta 运行，不能代替实体 Intel 和首次下载验收。Apple 的 [可信执行故障排查](https://developer.apple.com/forums/thread/706442) 说明了隔离属性传播及使用干净环境测试的原因。

## v1.0.3

本版新建同名源码标签并发布 Apple Silicon / Intel 两个包。先提交候选源码并构建、定稿 ZIP，再单独提交安装助手固定摘要。Release 的 `install.sh` 是本版入口；ZIP 内源码保留构建时的安装助手版本，不为自引用摘要重打包。UI 文档图片可通过 `python3 -m tools.macos.render_release_views` 重新生成。
