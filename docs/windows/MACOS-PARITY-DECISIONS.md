# Windows 与 macOS 行为对齐决策

适用版本：Windows v1.0.3。本页说明与 macOS 对齐的行为、平台实现差异及所依据的官方 API。

## 多屏接缝：采用一致的停靠行为

允许在相邻显示器接缝停靠并自动隐藏，与 macOS 一致。Windows 原先在 `CapsulePlacement.Dock` 排除共享接缝，这是应用策略，并非系统限制。

停靠以拖动结束时选定显示器的工作区为准，侧签完整保留在该屏工作区中。显示器增减、工作区变化或 DPI 变化时重新定位；物理桌面坐标和 DIP 尺寸分别处理，不将相邻屏幕当作不可停靠区。Microsoft 的 [MonitorFromRect](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfromrect) 提供矩形相交及最近屏幕选择，[GetMonitorInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getmonitorinfoa) 提供屏幕和工作区信息。

## 侧签邻近唤回：保留局部 100 ms 检查

保留可见侧签外围 8 DIP 的邻近唤回，以及停靠期间 100 ms 指针检查。只在浮窗可见、已经停靠且启用自动隐藏时运行；隐藏应用、取消停靠、关闭自动隐藏或退出时停止。120 ms 唤回和 550 ms 隐藏延迟仍可取消，触发时重新确认指针位置、窗口归属和交互状态。

原因是 [TrackMouseEvent](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-trackmouseevent) 只通知所指定窗口客户区或非客户区的停留和离开，离开后追踪取消；[TRACKMOUSEEVENT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-trackmouseevent) 也没有订阅任意屏幕区域的能力。指针处于侧签外的其他窗口时，本窗口不会因“进入外侧 8 DIP”收到正常鼠标事件。

因此，在保持现有邻近命中行为、不给桌面添加透明输入区域、也不使用全局输入 hook 的前提下，不能仅靠本窗口鼠标事件实现等价唤回。改成只响应可见侧签可以移除周期检查，但会改变用户当前操作范围，本轮不采用。该差异不阻止数据和后端的空闲等待采用事件。

## 后端与主面板：采用事件等待

共享后端的 `--desktop-events` 同时允许 Windows 和 macOS 直接启动。stdout 只承载有界 `protocol=1` JSON 行：`desktop_ready` 握手和最新 `desktop_state`；普通日志转至 stderr。协议保留 PID、实例标识、刷新票据和最新完成状态，写出队列只保留一项握手和一项最新状态，慢读者不会无限积压或阻塞索引锁。HTTP 查询、启动状态文件和 Windows 私密退出凭据继续保持原有含义。

Windows 客户端可以用单一长寿命读取器消费 stdout，以状态事件替换正常运行时的 `/health` 定时检查，并按刷新票据等待完成。读取器限制单行长度，校验协议、实例和 PID；换目录、重启、断流或关闭时取消旧等待并拒绝迟到事件。宿主的账号、预算、任务监控和设置是另一个状态来源，必须通过宿主 IPC 推送，不能只接后端 stdout 就删除主面板全部同步。

Microsoft 的 [StreamReader.ReadLineAsync](https://learn.microsoft.com/en-us/dotnet/api/system.io.streamreader.readlineasync?view=net-10.0) 提供异步行读取；实现也可以用有界 `ReadAsync` 分帧。这里的目标是不周期查询，不宣称没有等待线程：Windows [匿名管道](https://learn.microsoft.com/en-us/windows/win32/ipc/anonymous-pipe-operations) 本身不支持 overlapped I/O，缓冲区写满会等待读者。关闭顺序必须结束子进程／管道并收回读取任务，连接失效才进入有界退避恢复。

Windows HTTP 服务同时等待监听 socket 和停止 socket，移除原 250 ms 空闲超时。Windows 的 [selectors](https://docs.python.org/3/library/selectors.html) 支持 socket、不支持普通 pipe；[socket.socketpair](https://docs.python.org/3/library/socket.html#socket.socketpair) 已支持 Windows，因此使用不可继承、非阻塞的 socket pair 发送一次停止信号。该字节不被读取，所有消费者持续看到停止。macOS 继续使用原停止 pipe；其他 Unix 保留原回退。

`ParentMonitor` 统一持有停止对象：Windows 继续用原生进程句柄和 Win32 event 无限等待父进程退出，同时用 socket 唤醒 HTTP。没有 `--parent-pid` 时仍建立 HTTP 可等待的停止对象。HTTP 循环返回、父进程等待线程结束之后才释放句柄和 socket；停止和关闭串行，迟到请求重复停止仍安全。`/api/shutdown` 仍要求实例标识和私密凭据。自动刷新截止时间、短暂交互延迟和异常恢复退避属于有明确目的的等待，不承诺全应用没有定时器。

## 验证边界

本轮 Python 回归覆盖真实 Windows 无限进程等待、无父进程 HTTP 请求与停止、停止广播、提前停止、创建失败清理、关闭与迟到停止竞争、重复生命周期句柄数、真实父进程被结束、事件握手、手动刷新票据与慢读者有界队列。macOS 原生测试保留，但在 Windows 上跳过，不能算作通过。多屏、混合 DPI、系统通知和可见交互仍以对应候选应用的实机验收为准。

[Windows 文档](README.md) · [两端能力对照](../common/WINDOWS-MACOS-ALIGNMENT.md)
