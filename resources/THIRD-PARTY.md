# 随包运行时来源

两份 tar.gz 是 Astral `python-build-standalone` 官方 GitHub release **20260901 / CPython 3.12.14** 的完整、未修改 `install_only` 归档。

- [官方发布](https://github.com/astral-sh/python-build-standalone/releases/tag/20260901)
- [官方分发与运行说明](https://github.com/astral-sh/python-build-standalone/blob/main/docs/running.rst)
- 原始下载地址、字节数、GitHub asset SHA-256 保存在 `runtimes/manifest.json`。
- 启动器在首次解压前核对 SHA-256；归档内自带的 Python、pip 及其依赖许可证原样保留。许可证与 notices 可在解压后的 Python 目录内查阅。
- 本工具只使用 Python 标准库，没有额外 pip 安装步骤。

| 架构 | 官方 SHA-256 |
|---|---|
| aarch64-apple-darwin | 3ee3ee547cedfeb7c2b16b2b7156039f7b470bb8f857e226fd3d2eb11db83c76 |
| x86_64-apple-darwin | 2e31b23f3f1319f707d0e620b48847a0046577541d357276821f9f1b5492e0ba |

完整性校验用于发现缺失、损坏和意外修改，不等同于发行者数字签名。此 ZIP 尚未经过 Developer ID 签名或 Apple 公证。

`third-party-licenses/` 另附同一上游版本的许可证与 `python-licenses.rst`；其中也包含其他构建目标使用的许可证，不代表全部组件都链接进 macOS 运行时。
