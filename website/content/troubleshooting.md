# 故障排查

## Intel 提示“应用程序无法打开”

一个 Intel/macOS **15.7.9 (24G830)** 案例中，主程序确认为 `x86_64`，执行权限和 App 签名完整性均正常，但终端启动报 `Operation not permitted`。用户移除该 App 的隔离属性后确认能够打开。这支持“此次启动被隔离触发的执行检查阻止”，不代表所有 Intel 启动失败都具有相同原因。

推荐先检查下载包是否匹配芯片，确认从[正式发行页](https://github.com/Asher-XunZhang/codex-usage/releases/tag/v1.0.0)下载了原始 Intel ZIP。再按照[安装指南](./installation.md)尝试系统“仍要打开”或快捷安装助手；已有 App 先按迁移步骤处理。

下载渠道也可能影响隔离标记。Apple 记录过某些下载助手生成的特殊标记阻止已经签名、公证的应用执行的案例。让接收者通过 Safari 直接下载原始发行 ZIP，有助于排除中间传输工具引入的情况，但不会使本项目自动获得公证。[Apple 技术讨论](https://developer.apple.com/forums/thread/767612)

### 只读诊断

把第一行改为实际安装位置；安装助手默认用 `$HOME/Applications`，Finder 手动安装可能是 `/Applications`。

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

| 输出或现象 | 如何理解与处理 |
| --- | --- |
| Intel 机器上的可执行文件只有 `arm64` | 安装包选错，重新下载 Intel ZIP；Rosetta 不能让 Intel 运行 arm64 App |
| `codesign` 校验失败、ZIP 摘要不符 | 停止安装并重新下载；不要直接修改或重新签名损坏的副本 |
| `codesign` 通过，`spctl` 拒绝 | 完整性与系统信任是不同检查；当前版本未公证，不代表下载一定损坏 |
| `xattr` 提示没有这个属性 | 该层文件没有此属性，本身不代表 App 损坏 |
| `Operation not permitted` | 结合隔离和策略检查判断，不能只凭此句确定唯一原因 |
| `Permission denied`、缺失动态库或启动后崩溃 | 保留完整错误，继续排查权限、依赖或运行兼容性 |

系统若明确提示检测到恶意软件，请停止使用该副本。公司管理的 Mac 可能存在额外策略，应联系管理员处理，不能依赖安装脚本越过组织策略。

## 安装助手停止了

| 提示 | 下一步 |
| --- | --- |
| 已有 App，未覆盖 | 先退出并移走旧 App，或指定独立测试目录；无需删除 Codex 数据 |
| 安装包校验失败 | 使用对应架构的原始 v1.0.0 ZIP，不能重新压缩或使用 Source code 包 |
| 没有交互终端 | 在 macOS「终端」中先下载脚本到文件，再用 `/bin/bash` 执行 |
| 不要使用 sudo | 以当前用户运行，使用 `~/Applications` 或其他可写目录 |
| 下载失败 | 检查网络，或预先下载 ZIP 后使用 `--archive` |
| 安装锁目录已存在 | 确认另一个安装已结束；若上次被强行中止，再删除提示的空锁目录重试 |

安装助手已通过 [PR #1](https://github.com/Asher-XunZhang/codex-usage/pull/1) 于 2026-09-11 合并到 `main`。它仍安装原始 v1.0.0 App；合并不代表新增 Apple 公证，也不代表所有实体 Intel 设备均已验收。

## 没有统计、数字没变或额度为“—”

1. 确认这台 Mac 已安装并使用过 Codex，应用选中的日志目录正确。
2. 检查主面板和浮窗各自的时间、模型、任务筛选，避免用不同范围进行比较。
3. 首次历史索引需要等待；手动刷新只能读取已经落盘的记录。
4. 账号额度另行查询。确认本机 Codex 登录状态；未知或旧快照不等于额度为零。

完整说明见[统计口径与隐私](./metrics-and-privacy.md)。

## 菜单栏或浮窗不见了

先检查是否选择了「仅状态栏」「仅保留胶囊」或「退出」，并区分隐藏窗口和退出整个应用。也可以确认系统是否隐藏了对应菜单栏项目。若状态没有恢复，先退出整个 App，再从确定的安装路径重新打开，避免混用两个副本。

正常情况下，普通打开/关闭/隐藏主面板不应连带退出菜单栏或浮窗；如果仍可复现，请记录操作顺序和 App 版本反馈。鼠标行为与快捷键见[使用指南](./user-guide.md)。

## 内存为何超过 10 MB

菜单栏和浮窗虽然没有闲置常驻 Python，仍有 AppKit 字体、菜单、绘图和系统缓存成本。历史负载、是否打开过主面板以及短时额度查询都会改变观察值。不要将 RSS、虚拟内存或单个时刻的读数混为同一指标；项目没有承诺所有状态低于 10 MB。详见[架构](./architecture.md)和[验证记录](./validation.md)。

## 提交可复现的问题

请提供 macOS 版本与芯片、App 版本、下载来源与文件名、复现步骤、预期/实际结果及必要的错误信息。提交前检查并隐藏个人用户名或敏感路径；不要上传聊天日志、凭据、`auth.json` 或整个数据目录。
