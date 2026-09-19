---
title: 平台支持与功能差异
description: 对比 Windows v1.0.3 与 macOS v1.0.3 的功能、安装包与验证边界。
---

# 平台支持与功能差异

**macOS v1.0.3 提供 Apple Silicon 和 Intel 包，Windows 当前为 v1.0.3。** 更新 App 后才能获得对应的新功能。

| 能力 | Windows v1.0.3 | macOS v1.0.3 |
| --- | --- | --- |
| 原生界面 | WPF 主面板、系统托盘与浮窗 | AppKit 主面板、菜单栏与浮窗 |
| Token、趋势、模型／任务筛选、导出 | 支持 | 支持 |
| 只读账号额度、重置时间与重置卡数量 | 支持 | 支持 |
| Token、估算金额、官方余量下限预算 | 支持 | 支持 |
| 本轮／每轮任务提醒、消息历史 | 支持 | 支持 |
| 圆环、侧签的监控状态与未读标记 | 支持 | 支持 |
| 连续形变、展开后整窗拖动、方向选择 | 支持 | 支持 |
| 四边贴合与自动隐藏 | 微弧轮廓，按任务状态调整尺寸 | 微弧轮廓，按任务状态调整尺寸 |
| 单窗口自定义弧线调色 | 单色 / 随额度双色渐变 / 重置默认 | 单色 / 随额度双色渐变 / 重置默认 |
| 两屏相连接缝 | 可停靠并自动隐藏；按松手指针选择归属屏幕 | 可停靠并自动隐藏；按松手指针选择归属屏幕 |
| 系统入口详情与命令菜单 | 托盘详情与右键菜单 | 菜单栏原生详情与菜单 |
| 统一设置、系统主题与界面覆盖 | 支持 | 支持 |
| 手动刷新 | 本地用量、账号额度分别反馈 | 本地用量、账号额度分别反馈 |

两端复用统计解析，系统集成分别实现。通知受权限、勿扰和系统策略影响；本轮结束不代表整个需求已完成。

## 运行环境

| 平台 | 环境与边界 |
| --- | --- |
| Windows x64 | 自带 .NET 与 Python；主要实测 Windows 11。保持完整解压目录。 |
| macOS Apple Silicon | 自带运行组件，最低构建目标 macOS 11；本轮本机测试为 Apple Silicon。 |
| macOS Intel | 独立 x86_64 包；已做交叉编译、架构和发行完整性验证，不等于实体 Intel 实机验收。 |

[下载安装](./installation.md) · [验证范围](./validation.md)

## 源码来源

v1.0.3 标签保留 macOS 首发源码；Windows 为同一 Release 后续补充，源码提交由发行页单独标注，并随 Windows ZIP 的 `source/` 提供，`BUILD-MANIFEST.json` 记录源码哈希。macOS 包内 `source/` 与 App 的 `Contents/Resources/BUILD-INFO.json` 可核对编译提交和源码摘要。安装器在 ZIP 定稿后独立固定摘要。旧版 v1.0.2 标签与 Windows 资产保持不变。

[开发与发布](./development.md) · [两端对齐清单](https://github.com/Asher-XunZhang/codex-usage/blob/main/docs/common/WINDOWS-MACOS-ALIGNMENT.md)
