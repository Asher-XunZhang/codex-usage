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

## 已运行应用的内存采样

```sh
python3 scripts/measure_processes.py --scenario '收起浮窗/浅色/完成固定暖机操作后'
python3 scripts/measure_processes.py --app '/Applications/Codex用量.app' --duration 130 --interval 0.2 --scenario 'A/菜单栏/第1轮' --source-revision '构建对应的提交或源码标签' --output .local/performance
```

默认检查 `~/Applications/Codex用量.app`，不会启动、退出或操作应用。先手动选择需要测量的状态，再保持不操作，运行采样。默认持续 **130 秒**，覆盖至少两个约 60 秒的额度查询周期；`--interval` 默认 **0.2 秒**。每轮直接调用 libproc 获取 PID、父进程和可执行路径，不再反复创建 `ps` 进程，也不读取命令参数。采样仍有观察开销，并不能保证发现短于采样间隔的所有进程。

- 使用 Darwin `proc_pid_rusage` 的 `ri_phys_footprint`，单位 MiB（2²⁰ 字节），不使用 RSS 或虚拟地址空间替代。
- 每轮重新查找精确 App 路径的常驻进程与独立主面板，并递归统计当轮可见子进程。已经观察到的子进程即使重新挂到其他父进程，仍按进程启动身份追踪。可读取父进程但无法读取路径时，保留父进程关系；每个样本记录枚举失败计数。
- 任何采样中观察到的 GUI 进程集合/PID/启动身份变化，或原 GUI 退出、不可读取，都会使结果 `valid=false`，退出码为 2。首尾模式、刷新间隔、主题、显示偏好或筛选条件变化，以及 App 二进制/版本变化也标记无效。
- `host_only_mib_median` 仅计算没有其它相关进程的样本，**不证明 host 正在闲置或浮窗处于收起状态**；主面板一直打开时该值为 `null`。旧字段 `idle_host_mib_median` 暂时保留为同值的废弃别名，见 `deprecated_fields`。
- 合计中位数与 `observed_combined_peak_mib` 包括当轮可读取的主面板、Python 和额度助手等相关进程。观察到的峰值不能作为完整生命周期峰值保证，不能将各进程不同时间的峰值相加作为合计峰值。
- `actual_sample_intervals_seconds` 记录实际样本间隔的最小值、中位数、均值、P95 和最大值；`inventory_duration_seconds` 单独记录进程枚举耗时。请求间隔不等于实际间隔。
- `build_start` / `build_end` 记录四个可执行文件的 SHA-256、App 版本和构建号；`system` 记录 macOS 版本/构建、内核、采样器架构及主程序包含的二进制架构。通用二进制的架构列表不代表已经确认运行进程使用哪个架构。
- `--source-revision` 原样记录调用者提供的提交或源码标签，不自动猜测 Git HEAD，也不保证该标签对应实际二进制。`--scenario` 同样是调用者描述，工具不会据此自动操作或核实界面。
- 采样可能错过中途又恢复的偏好/界面变化、短进程、重启与瞬时峰值。进程枚举和资源读取并非原子操作，读取失败的当轮相关进程会记录在样本中，并使整次结果无效。尚未被观察到且无法读取祖先信息的子进程仍可能漏记。
- 工具不报告 CPU 或能耗，不根据内存推断交互流畅度。旧实验脚本的 CPU 换算不能直接沿用。

每次生成独立命名的 `*-performance.json` 和 `*-samples.json`，避免覆盖先前结果，权限为当前用户读写。报告包括本地应用路径、PID、模型/任务筛选值与实验标签，可能包含私人标识，**仅保存在 `.local/`，不要原样上传**。不保存命令参数、聊天内容或凭据；偏好只保留明确列出的统计/外观设置，不保存完整偏好域。

## A/B 比较的边界

固定同一设备、系统、显示器缩放、运行架构、构建配置、数据集、筛选、刷新与额度查询设置。分别定义冷菜单栏、冷收起浮窗和暖机后收起浮窗；暖机步骤须固定，例如相同次数的展开、菜单/主题操作以及主面板开关。

建议交错测量 **A1、B1、A2、B2、A3、B3**，每轮使用同样的启动、暖机和等待步骤，再运行 130 秒采样。切换候选版本与重启应用是实验人员的独立操作，测量脚本不会替换应用。每轮用不同 `--scenario` 标签；报告各轮数值与分布，不把冷启动的 A 与经过复杂操作的 B 比较。真正闲置的确认、显示缩放、交互延迟、动画和数据更新行为需另行核对。

测量工具自身和 WindowServer 等系统共享服务不计入 App 合计。需要解释增长原因时，在相同场景用 `vmmap` 或 Instruments 分类检查；分配器空闲页、活对象、字体/图形缓存与 RSS 的变化应分开解释。

## Darwin API 约定

枚举实现按 Apple `libproc.h`、`sys/proc_info.h` 与 `sys/resource.h` 定义：

- `proc_listallpids` 返回 **PID 数量**，传入缓冲区大小却以 **字节** 为单位；缓冲区恰好填满时扩容重试。
- `proc_pidinfo(PROC_PIDTBSDINFO)` 返回完整 `proc_bsdinfo` 的字节数才读取字段；短读视为不可用。
- `proc_pidpath` 返回路径字节长度，不包含终止 NUL；缓冲区使用 `PROC_PIDPATHINFO_MAXSIZE`。
- 本项目支持的 arm64/x86_64 Darwin ABI 中 `pid_t` 为 4 字节，`proc_bsdinfo` 为 136 字节，PPID 偏移 16，启动时间偏移 120，路径缓冲上限 4096，`rusage_info_v2` 为 160 字节。`tests/test_process_measurement.py` 用本机 SDK 编译小型 C 探针核对 ctypes 大小和偏移，并用 mock 覆盖缓冲扩容、短读和失败。

[Apple libproc 实现](https://github.com/apple-oss-distributions/xnu/blob/main/libsyscall/wrappers/libproc/libproc.c) · [Darwin 结构定义](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/proc_info.h)
