# 构建与发布

## 环境

需要 macOS、Apple Command Line Tools（`xcode-select --install`）和 Python 3.10+。构建工具只使用 Python 标准库。Swift/C 程序目标为 macOS 11；编译目标并不等于所有旧系统或设备都经过实测。

## 从干净克隆构建

在仓库根目录执行：

```sh
python3 test.py
python3 scripts/fetch_runtime.py --arch arm64
python3 build.py --arch arm64
```

结果位于 `build/arm64/Codex用量.app`。Intel 将两条命令中的架构改成 `x86_64`；也可选择 `universal` 生成双架构 App。按架构连续构建，避免并行写入共用的图标和模块缓存。

运行组件固定为 Astral python-build-standalone 的 CPython 3.12.14 / 20260901 发布。`resources/runtimes/manifest.json` 记录官方下载地址、大小和 SHA-256；下载先写临时文件，验证成功后才保存。已经存在但不匹配的缓存会报错，不静默覆盖。

若已有完整发行 App，可完全离线复用其资源。在发行 ZIP 的 `source/` 目录中执行：

```sh
python3 build.py --arch arm64 --runtime-source '../Codex用量.app/Contents/Resources'
```

## 测试

`python3 test.py` 使用合成日志和独立临时目录，覆盖解析、索引、刷新、信号退出、原生界面与进程生命周期。Swift/AppKit 测试需要 macOS；其它平台会跳过相应测试。桌面应用的支持平台仍为 macOS。

实际 App 的离线运行时与偏好初始化可在不打开窗口的情况下检查：

```sh
python3 - <<'PY'
from pathlib import Path
import os
import subprocess
import tempfile

app = Path('build/arm64/Codex用量.app').resolve()
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
python3 package.py
python3 scripts/verify_release.py
```

发行文件在 `dist/`。ZIP 包含 App、文档、项目许可证、第三方许可证、可构建源码以及逐文件校验清单；外部同名 `.zip.sha256` 用于校验整个下载文件。只使用明确的文件清单，不打包日志、数据库、认证、偏好、编译缓存或本机实验数据。

构建使用 ad hoc 签名。公开下载后的首次信任体验、Developer ID 签名、公证和干净设备验收是独立事项，本仓库不删除隔离属性或绕过 Gatekeeper。
