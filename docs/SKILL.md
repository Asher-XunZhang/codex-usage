# 可选的逐轮 Token 技能

桌面 App 不依赖此技能，也不会修改 Codex 配置来自动启用它。技能和 Stop hook 是单独保留的可复用组件。

`skill/scripts/token_usage.py` 可独立运行，仅使用 Python 标准库：

```sh
python3 skill/scripts/token_usage.py --help
python3 skill/scripts/token_usage.py --codex-home ~/.codex --format table
```

在 Codex 任务环境中，`CODEX_THREAD_ID` 用于确定当前任务；缺少身份时不要猜测其它任务。也可按照 `--help` 显式指定自己要统计的任务或轮次。输出保留输入、缓存输入、非缓存输入、输出和总数，以及可核实的子代理归属与快照边界。Token 不是账单或订阅额度。

`skill/` 是可独立复制的技能目录，包含 `SKILL.md`、`agents/openai.yaml` 和脚本。按所用 Codex 版本的技能安装方式添加，选择本机实际可用的 Python 路径。源文件没有任何开发者个人路径。

`skill/scripts/stop_hook.py` 可处理合法的 Stop 事件，只返回统计提示，不恢复模型控制流、不回显事件携带的最终回复。它不自动配置或建立 hook 信任；使用者可自行决定是否接入。复制后脚本旁的 `token_usage.py` 必须一起保留。
