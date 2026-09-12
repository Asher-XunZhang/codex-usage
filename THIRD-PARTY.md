# 第三方组件

本项目代码使用根目录 [LICENSE](LICENSE) 中的 MIT 许可证。随包运行组件保留各自的原始许可证。

- [CPython 独立运行组件的来源与校验值](resources/THIRD-PARTY.md)
- [固定版本、官方下载地址、大小与 SHA-256](resources/runtimes/manifest.json)
- [上游许可证与 notices](resources/third-party-licenses/)

发行 App 内也包含这些 notices；完整、未修改的 Python 归档中自带 Python、pip 及其依赖的原始许可证。工具仅使用 Python 标准库，无额外 pip 安装步骤。

本地完整性校验不等同于发行者身份认证。当前发行采用 ad hoc 签名，未经过 Developer ID 签名或 Apple 公证。

## Windows

Windows 版使用 WPF/.NET 与 PSF 的 Windows embedded Python。运行时按固定版本与校验值获取，发行目录的 licenses/ 包含实际使用依赖的许可证。
