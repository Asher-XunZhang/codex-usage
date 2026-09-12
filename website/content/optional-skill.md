# 可选的逐轮 Token 技能

仓库还保留可复用的逐轮/任务 Token 统计技能和 Stop hook。它们是可选组件：**桌面 App 不依赖此技能，也不会替你修改 Codex 配置或自动启用 hook。** 只需要菜单栏、主面板或浮窗的用户无需安装它们。

## 适合什么场景

| 需求 | 使用方式 |
| --- | --- |
| 在原生窗口查看用量趋势和账户额度 | 使用桌面 App，见 [使用指南](./user-guide.md) |
| 在 Codex 任务中查看当前轮次或任务的已记账 Token | 按需运行 `token_usage.py`，或单独安装技能 |
| 在任务结束事件中提供统计提示 | 自行选择是否配置 Stop hook |

技能统计的是已记录的 Token，不是账单或订阅剩余额度，不能用 Token 数量反推费用。

## 独立运行统计脚本

取得 [v1.0.1 源码](https://github.com/Asher-XunZhang/codex-usage/tree/v1.0.1) 后，在仓库根目录运行：

```sh
python3 integrations/codex-token-usage/scripts/token_usage.py --help
python3 integrations/codex-token-usage/scripts/token_usage.py --codex-home ~/.codex --format table
```

脚本只使用 Python 标准库。请使用这台 Mac 实际可用的 Python 路径；这里的 `python3` 是示例，不是某位开发者的私有安装路径。

在 Codex 任务环境中，`CODEX_THREAD_ID` 用于确定当前任务。缺少任务身份时，不应猜测或自动改成其他任务；可先查看 `--help`，再显式指定自己希望统计的任务或轮次。

## 如何阅读结果

输出保留输入、其中缓存输入、非缓存输入、输出和总数，并标注可核实的子代理归属与快照边界。

- 输入已包含缓存输入，不能把缓存再次加到总数。
- 输出已包含推理输出，不能把推理再次相加。
- 尚未落盘或无法核实的记录，不能据此当作零消耗。
- 统计是某个时间点的快照；生成快照之后的内容不属于该快照。

更完整的统计口径与本地文件位置见 [统计与隐私](./metrics-and-privacy.md)。

## 单独安装技能

`integrations/codex-token-usage/` 是可独立复制的技能目录，包含 `SKILL.md`、`agents/openai.yaml` 和脚本。按自己所用 Codex 版本支持的技能安装方式添加，并选择本机可用的 Python 路径；具体使用说明以该目录内的 `SKILL.md` 为准。

复制时应保留完整目录结构，不要只复制说明文件。安装本技能与安装桌面 App 是两个独立操作。

## Stop hook 的职责

`integrations/codex-token-usage/scripts/stop_hook.py` 可处理合法的 Stop 事件，只返回统计提示，不恢复模型控制流，也不回显事件携带的最终回复。

hook 不会自行完成配置或建立信任；是否接入由使用者决定。复制脚本时，必须同时保留其旁边的 `token_usage.py`。桌面应用的安装、启动和退出都不构成启用该 hook 的授权。

## 页面依据

本页基于版本 [`v1.0.1`](https://github.com/Asher-XunZhang/codex-usage/tree/v1.0.1) 的 [可选技能文档](https://github.com/Asher-XunZhang/codex-usage/blob/v1.0.1/docs/SKILL.md) 与 [README](https://github.com/Asher-XunZhang/codex-usage/blob/v1.0.1/README.md)。
