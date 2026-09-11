# 开发、构建与本地验证

本页以 v1.0.0 App 的固定源码提交 **`53c9cf4fcfc3c549be61dbfe77facb2af1b87ca8`** 为依据，以下步骤用于重现该首次公开版本。免费安装助手已通过 [PR #1](https://github.com/Asher-XunZhang/codex-usage/pull/1) 合并；截至 2026-09-11，`main` 为 [`d6a9407`](https://github.com/Asher-XunZhang/codex-usage/tree/d6a9407a86325eaf210e7d69f58a63ba4f5eab8e)，原始 v1.0.0 App 和发行 ZIP 没有改变。普通使用者直接阅读 [安装指南](./installation.md)，无需安装开发环境。

## 环境与源码

构建需要 macOS、Apple Command Line Tools 和 Python 3.10+。构建脚本只使用 Python 标准库。Swift/C 程序的最低编译目标为 macOS 11；该目标不代表所有旧系统和设备均已实测。

如尚未安装 Command Line Tools，可执行：

```sh
xcode-select --install
```

获取并固定源码版本：

```sh
git clone https://github.com/Asher-XunZhang/codex-usage.git
cd codex-usage
git checkout --detach 53c9cf4fcfc3c549be61dbfe77facb2af1b87ca8
```

以上在新克隆中操作，不要求切换已有工作目录或丢弃本地修改。要开发新功能，可在此基础上另建自己的分支。

## 按芯片构建

先在仓库根目录运行测试：

```sh
python3 test.py
```

Intel 构建：

```sh
python3 scripts/fetch_runtime.py --arch x86_64
python3 build.py --arch x86_64
```

结果位于 `build/x86_64/Codex用量.app`。Apple Silicon 使用：

```sh
python3 scripts/fetch_runtime.py --arch arm64
python3 build.py --arch arm64
```

结果位于 `build/arm64/Codex用量.app`。构建参数也支持 `universal` 双架构 App。按架构连续构建，避免并行写入共用的图标和模块缓存。

运行组件固定为 Astral python-build-standalone 的 CPython 3.12.14 / 20260901 发布。`resources/runtimes/manifest.json` 记录下载地址、大小和 SHA-256；下载先写临时文件，验证成功后保存。存在不匹配的缓存时会报错，不静默覆盖。

发行 ZIP 内已带对应运行组件。若要复用已有完整 App 的资源，在发行 ZIP 的 `source/` 目录执行：

```sh
python3 build.py --arch x86_64 --runtime-source '../Codex用量.app/Contents/Resources'
```

Apple Silicon 将架构改为 `arm64`；资源来源也应匹配目标架构。

## 验证离线运行组件

自动化测试使用合成日志与独立临时目录，覆盖解析、索引、刷新、信号退出、原生界面和进程生命周期。Swift/AppKit 测试需要 macOS；其他平台会跳过相应测试，不意味着桌面 App 支持这些平台。

以下例子检查 Intel 构建的宿主和主面板，使用独立临时支持目录，不打开 GUI、不读取个人聊天记录：

```sh
python3 - <<'PY'
from pathlib import Path
import os
import subprocess
import tempfile

app = Path('build/x86_64/Codex用量.app').resolve()
for executable in ['Contents/MacOS/CodexUsage',
                   'Contents/Helpers/CodexUsageMain.app/Contents/MacOS/CodexUsage']:
    with tempfile.TemporaryDirectory(prefix='codex-usage-bootstrap-') as temporary:
        environment = dict(os.environ, CODEX_USAGE_DESKTOP_BASE=temporary)
        subprocess.run([str(app / executable), '--check-runtime'], env=environment, check=True)
PY
```

Apple Silicon 本机构建检查将路径中的 `x86_64` 改为 `arm64`。主面板嵌套组件使用父 App 资源，因此两个 bundle 需要分别检查。交叉编译或 Rosetta 上运行成功不能替代实体 Intel 验收。

## 离屏界面预览

```sh
python3 scripts/render_previews.py
python3 scripts/render_previews.py --source Sources/Capsule.swift --output .local/previews
```

工具需要 Command Line Tools，会编译真实绘制组件，生成深浅主题、额度边界、长文本和展开中间帧的 26 张 PNG，以及收起样式合集。全部数字、时间、模型和任务均为合成示例；不读取本机 Codex 日志、账号或偏好，不打开已安装 App 或可见窗口。

输出包括图片状态清单、源码与驱动摘要、编译和渲染日志。临时源码、二进制和模块缓存自动清理。默认 `.local/` 被 Git 忽略，同名预览会被替换；比较版本时请指定不同输出目录。离屏预览验证绘制布局，真实鼠标事件、窗口层级与系统菜单仍需交互验收。

## 测量已运行 App 的内存

先手动将 App 切到要测量的状态，保持不操作，再采样：

```sh
python3 scripts/measure_processes.py --duration 40
python3 scripts/measure_processes.py --app '/Applications/Codex用量.app' --duration 70 --output .local/performance
```

默认目标为 `~/Applications/Codex用量.app`。工具不会启动、退出或操作应用，使用 Darwin `ri_phys_footprint`，单位 MiB；不是 RSS 或虚拟地址空间。

- 合计包含当轮可读的 GUI、Python 和额度等相关进程；测量工具自身及系统共享服务不计入。
- GUI 身份变化、原 GUI 退出、进程读取失败或首尾模式与刷新间隔变化，会使结果无效；检查 `valid` 字段。
- 无辅助进程时的常驻中位数与合计峰值是不同口径；主面板一直打开时，闲置宿主中位数应为 `null`。
- 离散采样可能漏掉短进程或瞬时峰值，不能用观察峰值保证完整生命周期峰值。
- 该版本工具不报告 CPU。比较内存时应同时记录系统版本、实验负载、冷/热启动和交互顺序。

每次生成独立命名的性能和样本 JSON，不包含聊天或凭据。完整指标和限制见固定版本 [本地验证工具文档](https://github.com/Asher-XunZhang/codex-usage/blob/53c9cf4fcfc3c549be61dbfe77facb2af1b87ca8/docs/DEVELOPMENT-TOOLS.md)。

## 打包与发行边界

依次构建两种架构后执行：

```sh
python3 package.py
python3 scripts/verify_release.py
```

结果位于 `dist/`。ZIP 包含 App、文档、项目和第三方许可证、可构建源码及逐文件校验清单；外部同名 `.zip.sha256` 用于整个下载文件的校验。打包使用明确的文件清单，不包含日志、数据库、认证、偏好、编译缓存或本机实验数据。

该发布使用 ad hoc 签名，没有 Developer ID 签名或 Apple 公证。签名完整性、架构检查、运行组件启动和真实下载后的首次信任体验是不同验收项目；不能把构建通过当作任意 Mac 均可直接运行。已公开的验收结果见 [验证范围](./validation.md)。

## 固定来源

维护当前安装助手时，应以已合并的 `d6a9407` 或更新的主线为基础，不能使用上面重现原始 App 的 `53c9cf4` 检出。安装器维护顺序见[主线构建与发布说明](https://github.com/Asher-XunZhang/codex-usage/blob/d6a9407a86325eaf210e7d69f58a63ba4f5eab8e/docs/BUILDING.md)：先确定并上传新的发行 ZIP，再更新安装器中的版本与固定摘要；不要重新打包并覆盖同名 v1.0.0 ZIP。

- [v1.0.0 源码](https://github.com/Asher-XunZhang/codex-usage/tree/53c9cf4fcfc3c549be61dbfe77facb2af1b87ca8)
- [构建与发布文档](https://github.com/Asher-XunZhang/codex-usage/blob/53c9cf4fcfc3c549be61dbfe77facb2af1b87ca8/docs/BUILDING.md)
- [本地验证工具文档](https://github.com/Asher-XunZhang/codex-usage/blob/53c9cf4fcfc3c549be61dbfe77facb2af1b87ca8/docs/DEVELOPMENT-TOOLS.md)
- [版本记录](https://github.com/Asher-XunZhang/codex-usage/blob/53c9cf4fcfc3c549be61dbfe77facb2af1b87ca8/CHANGELOG.md)
