# macOS v1.0.2 发行说明

2026-09-15 在 [v1.0.2 Release](https://github.com/Asher-XunZhang/codex-usage/releases/tag/v1.0.2) 补充 Apple Silicon 与 Intel 安装包，版本为 1.0.2 / 102。Windows 既有安装包和 v1.0.2 标签保持不变。

## 新增与修正

- 任务监控页、本轮／持续提醒、未读消息与通知；关闭主面板后继续监控。
- 预算提醒、缺价诊断与官方额度窗口选择，统一设置和主题继承，菜单栏详情。
- 圆环到面板连续形变、展开面板全区域拖动、四方向展开、四边贴合隐藏。两屏接缝也可停靠，持续拖动仍自由跨屏。
- 修复主动收起与鼠标悬停冲突、移出后不收起、按钮光晕被相邻元素遮挡。
- 采用事件等待减少后端空闲轮询；Token 解析和去重保持原逻辑，不新增闲置常驻 Python 监控或预算进程。

## 安装与源码

按芯片下载 `codex-usage-desktop-v1.0.2-AppleSilicon.zip` 或 `codex-usage-desktop-v1.0.2-Intel.zip`，核对同名 `.sha256`。也可使用 Release 独立 `install.sh` 自动选包。更新前退出并移走旧 App，保留用户支持目录、偏好及 Codex 日志。[安装指南](INSTALL.md)

既有 v1.0.2 标签对应 Windows 源码；新增 macOS 包的源码提交由 Release 单独列出。每个 ZIP 包含编译时的 `source/` 快照，App 的 `Contents/Resources/BUILD-INFO.json` 记录提交、架构及源码摘要，发行验证逐文件核对。独立安装助手在 ZIP 最终摘要确定后更新；包内源码自带脚本可能仍指向旧版本，以 `--help` 为准，安装新版本请用 Release 独立脚本。不会为回写摘要而改写已发布 ZIP。

## 验证范围

两种架构分别构建并检查可执行文件、嵌套签名、固定 Python 运行时、源码和资源清单。具体执行结果在 PR、CI 与 Release 中记录；本机 Apple Silicon 的开发验收见[特性同步验证](FEATURE-SYNC-VALIDATION.md)。用户已确认收起重叠修复，以及两屏接缝贴合隐藏、持续跨屏拖动正常。

实体 Intel、完整拔屏／Spaces／休眠路径以及系统通知正文点击与冷启动跳转尚未全部实机验收。编译成功不代表这些路径均通过。本轮未给出整机功耗收益百分比。

macOS 最低构建目标为 11；采用 ad hoc 签名，未经 Developer ID 签名或 Apple 公证。签名完整性校验不等于 Apple 发布者认证。
