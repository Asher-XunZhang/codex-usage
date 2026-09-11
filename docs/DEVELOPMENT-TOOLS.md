# 本地验证工具

以下工具仅供 macOS 开发与检查使用。默认输出位于 Git 忽略的 `.local/`，不进入源码仓库或分发包。

## 离屏界面预览

```sh
python3 scripts/render_previews.py
python3 scripts/render_previews.py --source Sources/Capsule.swift --output .local/previews
```

需要 Apple Command Line Tools。脚本编译当前 `Capsule.swift` 与 `scripts/preview/main.swift`，生成深浅主题、额度边界、长文本和展开中间帧的 26 张 PNG，以及一张收起样式合集。全部数字、时间、模型及任务均为合成示例，不读取本机 Codex 日志、账号或应用偏好，不打开已安装应用或可见窗口。

`render-manifest.json` 记录图片尺寸与示例状态，`run-result.json` 记录源码及预览驱动的 SHA-256；编译和渲染日志保存在同一输出目录。临时源码副本、二进制及 Swift 模块缓存自动清理，包括失败路径。默认输出目录可重复使用，同名预览会被替换；比较多个版本时请指定不同 `--output`。

这套预览验证绘制布局；真实鼠标事件、窗口层级与系统菜单仍需单独验证。

README 的两张收起浮窗示意图取自上述输出。更新额度配色后，在仓库根目录执行以下命令同步图片；50% 应显示为对应主题的琥珀色：

```sh
python3 scripts/render_previews.py --output .local/previews
cp .local/previews/compact-dark-50.png docs/images/compact-dark-50.png
cp .local/previews/compact-light-50.png docs/images/compact-light-50.png
```

发布文档前检查两张图和相对链接即可；仅补充说明或替换示意图时，沿用已有代码测试结果，不需要启动已安装应用或重测无关功能。

## 已运行应用的内存采样

```sh
python3 scripts/measure_processes.py --duration 40
python3 scripts/measure_processes.py --app '/Applications/Codex用量.app' --duration 70 --output .local/performance
```

默认检查 `~/Applications/Codex用量.app`，不会启动、退出或操作应用。先手动选择需要测量的状态，再保持不操作，运行采样。`--interval` 默认 0.1 秒；每轮还需要进程枚举和读取时间，因此实际样本间隔以结果时间戳为准。

- 使用 Darwin `proc_pid_rusage` 的 `ri_phys_footprint`，单位 MiB（2²⁰ 字节），不使用 RSS 或虚拟地址空间替代。
- 每轮重新查找精确 App 路径的常驻进程与独立主面板，并递归统计当轮可见子进程。已经观察到的子进程即使重新挂到其他父进程，仍会按进程启动身份继续追踪。
- 任何采样中观察到的 GUI 进程集合/PID/启动身份变化，或原 GUI 退出、不可读取，都会使结果 `valid=false`，退出码为 2。首尾刷新间隔或模式改变也会标记无效。
- `idle_host_mib_median` 仅计算没有其他相关进程的样本；主面板一直打开时该值应为 `null`。合计中位数与 `observed_combined_peak_mib` 包括当轮可读取的主面板、Python 和额度助手等相关进程。
- 采样可能错过两个样本之间的短进程、重启与瞬时峰值。进程枚举和资源读取并非原子操作，读取失败的当轮进程会记录在样本中，并使整次结果无效。观察到的峰值不能作为完整生命周期的峰值保证。
- 工具不报告 CPU。旧实验脚本曾将 rusage CPU 字段乘以 Mach 时间基准；本迁移版不沿用未经确认的单位换算，也不据此延续旧 CPU 结论。

每次生成独立命名的 `*-performance.json` 和 `*-samples.json`，避免覆盖先前结果。文件权限为当前用户读写。结果仅保存被测 App 路径、进程编号、模式、刷新间隔及资源计数，不保存命令参数、聊天内容或凭据。测量工具自身和系统共享服务不计入 App 合计；实验负载、系统版本、冷/热启动状态仍需在比较报告中注明。
