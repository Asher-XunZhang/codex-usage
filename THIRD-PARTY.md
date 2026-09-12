# 第三方组件

本项目代码使用根目录 [LICENSE](LICENSE) 中的 MIT 许可证。随包运行组件保留各自的原始许可证。

- [CPython 独立运行组件的来源与校验值](resources/macos/THIRD-PARTY.md)
- [固定版本、官方下载地址、大小与 SHA-256](resources/macos/runtimes/manifest.json)
- [上游许可证与 notices](resources/macos/third-party-licenses/)
- [Windows Python 固定来源与校验值](resources/windows/runtimes/manifest.json)
- [Windows 通知依赖许可证](resources/windows/licenses/README.md)

发行 App 内也包含这些 notices；完整、未修改的 Python 归档中自带 Python、pip 及其依赖的原始许可证。工具仅使用 Python 标准库，无额外 pip 安装步骤。

本地完整性校验不等同于发行者身份认证。macOS 发行采用 ad hoc 签名，未经过 Developer ID 签名或 Apple 公证；Windows 便携包未做代码签名。
