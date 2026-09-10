<p align="center"><img src="docs/images/icon.png" width="88" alt="Codex Usage icon"></p>

# codex-usage

**在 macOS 原生窗口、菜单栏和圆形浮窗中查看 Codex 用量。**

A native macOS companion for recorded Codex tokens and account quota. Local statistics, independent windows, no browser required.

[下载安装包](https://github.com/Asher-XunZhang/codex-usage/releases/latest) · [构建与开发](docs/BUILDING.md) · [架构](docs/ARCHITECTURE.md) · [验证范围](docs/VALIDATION.md)

## 直接使用

| Mac 芯片 | 下载文件 |
| --- | --- |
| Apple Silicon：M 系列 | [codex-usage-desktop-v1.6.0-AppleSilicon.zip](https://github.com/Asher-XunZhang/codex-usage/releases/download/v1.6.0/codex-usage-desktop-v1.6.0-AppleSilicon.zip) |
| Intel | [codex-usage-desktop-v1.6.0-Intel.zip](https://github.com/Asher-XunZhang/codex-usage/releases/download/v1.6.0/codex-usage-desktop-v1.6.0-Intel.zip) |

1. 在「 → 关于本机」确认芯片，下载匹配的 ZIP。GitHub 自动生成的 **Source code** 包用于开发，不能直接当作应用打开。
2. 完整解压，把 `Codex用量.app` 拖到「应用程序」，然后双击。无需另装 Python、Node.js、Homebrew 或开发工具；首次启动会校验并离线解压随包组件。
3. 安装并使用过 Codex 后，本工具读取这台 Mac 的本地记账；账号额度需要本机 Codex 已登录。首次历史索引可能需要等待。

当前发行使用本地临时签名，**未经过 Developer ID 签名或 Apple 公证**。互联网下载后，macOS 可能要求按 [Apple 的「仍要打开」流程](https://support.apple.com/guide/mac-help/mh40616/mac)确认来源；组织管理策略可能限制运行。构建最低目标为 macOS 11；已在 Apple Silicon/macOS 26.5 实测，实体 Intel Mac 和其它系统版本尚未逐一验证。

发给同事时，只需发送上表匹配芯片的 **一个完整 ZIP**，也可附上同名 `.zip.sha256` 校验文件。不要发送自己的 Codex 数据文件夹或支持文件夹。应用会统计同事电脑上的记录，不携带发送者的账号、历史用量或聊天。

## 三种查看方式

- **主面板**：今天、7/30/90 天或全部时间；按模型和任务筛选，查看输入、输出、缓存和调用次数；柱状图悬停显示精确数值，可搜索、排序、导出 CSV。
- **菜单栏**：快速查看今日 Token 与剩余额度。单击展开菜单，双击打开主面板；点击其它位置关闭菜单。
- **圆形浮窗**：76×76 的扁平圆环，5 点宽的绿色弧线表示剩余额度。悬停立即展开详情，移开立即开始收起；支持深色/浅色、置顶、拖动，以及独立的时间、模型、任务筛选。

<p>
  <img src="docs/images/compact-dark-50.png" width="96" alt="深色圆形浮窗，演示数据">
  <img src="docs/images/compact-light-50.png" width="96" alt="浅色圆形浮窗，演示数据">
</p>
<p>
  <img src="docs/images/expanded-dark.png" width="300" alt="深色详情，演示数据">
  <img src="docs/images/expanded-light.png" width="300" alt="浅色详情，演示数据">
</p>

以上图片由真实绘制组件生成，使用固定演示数据，不代表某个账号的额度或记录。

浮窗数字区左键打开主面板；右键或 Control-click 打开功能菜单；按住数字区移动超过 3 点时只拖动。选择菜单打开期间暂缓收起，可手动保持展开。动画遵循系统「减少动态效果」设置。

主面板关闭或隐藏时，菜单栏与浮窗继续运行；关闭浮窗不影响主面板。只有明确选择「仅状态栏」「仅保留胶囊」或「退出」才按其含义调整其它窗口。`⌘Q` 退出整个工具，`⌘H` 隐藏并释放主面板。

## 刷新与统计口径

手动刷新立即请求增量扫描，完成后更新读数。自动刷新可关闭，或选择 1/2/5/10/30/60 秒、自定义 1–3600 秒；三种界面共享刷新间隔，各自的筛选范围独立保存。账号额度另按约 60 秒只读查询。

- 只能统计 Codex **已经写入本机日志**的记账；尚未落盘、缺失或旧格式无法核实的记录会标注覆盖限制。
- 输入已经包含缓存输入；输出已经包含推理输出，不能重复相加。
- Token 数量、账户订阅额度和费用是不同口径，不能相互反推。本工具不计算账单。
- 未知额度显示「—」，旧快照带标记。主面板可显示重置卡数量，**没有兑换入口**。

默认读取当前用户的 `~/.codex`，可在应用菜单中选择其它目录。索引、运行组件和工具日志位于 `~/Library/Application Support/CodexUsageDashboard/`；偏好位于 `local.codex-usage.desktop` 域。工具不上传聊天或统计。额度查询调用本机 Codex 的只读接口，可能由 Codex 自身与官方服务通信。

## 进程与内存

菜单栏与浮窗共用稳定的常驻 GUI，主面板使用按需启动的独立 GUI。主面板关闭后，它和 Python 统计服务一起退出，完整释放主面板的进程内缓存。详见 [架构与取舍](docs/ARCHITECTURE.md)。

| 状态 | 稳定时的进程 |
| --- | --- |
| 仅菜单栏或浮窗 | 1 个 GUI；没有闲置常驻 Python |
| 打开主面板 | 2 个 GUI + 1 个 Python 统计服务 |
| 扫描或查询额度期间 | 另有短时采集/摘要/额度进程，完成后退出 |

一次 Apple Silicon 实测：冷浮窗约 13.36 MiB；两次主面板开关并操作浮窗后，收起闲置约 24.83 MiB；随后暖机菜单栏约 25.81 MiB。系统菜单、字体和图形缓存有固定成本，不能承诺所有闲置状态低于 10 MB。短时查询峰值及测量边界见 [验证记录](docs/VALIDATION.md)。

## 源码与可选技能

```sh
git clone https://github.com/Asher-XunZhang/codex-usage.git
cd codex-usage
python3 test.py
python3 scripts/fetch_runtime.py --arch arm64
python3 build.py --arch arm64
```

本机源码构建需要 Apple Command Line Tools 和 Python 3.10+。Intel 构建将 `arm64` 改为 `x86_64`；两架构的离线运行组件均固定版本并校验 SHA-256。运行时归档不放进 Git 历史，发行 ZIP 已包含对应归档。

`skill/` 另保留可选的逐轮/任务 Token 统计技能与 Stop hook；它们不由桌面应用自动安装或启用。用法见 [可选技能](docs/SKILL.md)。

## 许可证

本项目代码使用 [MIT](LICENSE) 许可证。随包 CPython 和第三方组件遵循各自的许可证，见 [运行组件来源](resources/THIRD-PARTY.md) 和 [完整 notices](resources/third-party-licenses/)。图标及示意图由本项目代码生成。

本项目为独立工具，与 OpenAI 官方无隶属关系。
