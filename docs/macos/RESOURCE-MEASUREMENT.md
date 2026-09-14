# 本机资源采样

在仓库根目录运行（App 必须已经启动，脚本不启动或操作窗口）：

```sh
PYTHONPATH=. python3 tools/macos/measure_processes.py --duration 20 --interval 0.5
```

通过 `--app` 指定实际安装路径，`--output` 指定结果目录。默认采样用户 Applications 中的 Codex用量.app，输出独立 JSON 结果和逐样本记录。记录场景时同时注明主窗口是否打开、活动监控数量、浮窗形态、自动刷新间隔及是否正在产生日志。采样期间避免切换模式或重启 App。

| 指标 | 口径与限制 |
| --- | --- |
| 内存 | `ri_phys_footprint`，单位 MiB；中位数和离散观察峰值，不是 RSS 或生命周期最大值 |
| CPU | 同一 PID＋进程启动身份的 `ri_user_time + ri_system_time` 首末差值，按本机 Mach timebase 转换为秒；宿主百分比以一颗 CPU 核心为 100% |
| 唤醒 | 分别记录任务归因的 `ri_pkg_idle_wkups` 与 `ri_interrupt_wkups` 增量；不能相加当作整机唤醒或耗电量 |
| 磁盘 I/O | `ri_diskio_bytesread/written` 增量，不包含缓存命中的逻辑读取；零也可能来自内核未提供 I/O 记账 |
| 文件描述符 | `PROC_PIDLISTFDS` 观察数量，未读取文件内容或路径；不含 Mach ports，读取失败保留 null |
| 子进程 | 从宿主及独立主面板追踪后代，保留已经发现的重新归属进程，并用启动身份排除 PID 复用 |

CPU、I/O 和唤醒只覆盖每个进程首次到末次成功观察之间的变化；不补猜首次观察前、末次观察后及整个采样间隙中启动并退出的短进程。父进程的 `ri_child_*` 不再相加，避免与已采样后代重复。逐进程记录包含观察次数；只有一次观察的进程没有可测增量，不能理解为它没有资源消耗。

`valid` 表示进程、模式、刷新间隔和内存／计数器采样的一致性检查通过，不代表功耗验收通过。文件描述符完整性另看 `fd_samples_complete`；计数器回退会使样本无效。采样脚本自身和系统服务未计入 App 合计，但测量行为仍有观察开销。

2026-09-14 核对本机 SDK 和 Apple XNU 的 [`fill_task_rusage`／I/O 记账](https://github.com/apple-oss-distributions/xnu/blob/main/osfmk/kern/bsd_kern.c)、[`task_power_info_locked`](https://github.com/apple-oss-distributions/xnu/blob/main/osfmk/kern/task.c) 与 [`libproc` 声明](https://github.com/apple-oss-distributions/xnu/blob/main/libsyscall/wrappers/libproc/libproc.h)。CPU 时间字段使用 Mach 时间单位，不能在 Apple Silicon 上直接当纳秒。自动验证另以本机 Python `process_time` 做短时 CPU 对照，并覆盖 PID 复用、计数回退、I/O 单位及打开／关闭文件后的 FD 变化。

当前候选的实际测量见[适配验收记录](FEATURE-SYNC-VALIDATION.md)。若要比较优化收益，须另外固定版本、负载、热身、刷新周期和交互状态进行对照；单次短样本不能证明续航改善或永久没有泄漏。
