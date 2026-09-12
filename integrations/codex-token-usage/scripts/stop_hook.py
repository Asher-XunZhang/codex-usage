"""Read-only Codex Stop hook: display recorded counters without resuming the model."""
from __future__ import annotations

import json
import os
from pathlib import Path
import sys

sys.dont_write_bytecode = True
import token_usage


def handle(event: dict) -> dict:
    if not isinstance(event, dict) or event.get("hook_event_name") != "Stop":
        return {}
    thread = token_usage.valid_id(event.get("session_id"))
    turn = token_usage.valid_id(event.get("turn_id"))
    if not thread or not turn:
        return {"systemMessage": "Token：回复后统计不可用（缺少有效任务或轮次标识）。"}
    home = Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex").expanduser()
    try:
        result = token_usage.collect(home, thread, turn)
        message = token_usage.table(result)
        message = message.replace(token_usage.BOUNDARY_NOTE, "")
        message += " 回复后已落盘快照；日志延迟时可能尚未全部入账。"
        if "current_turn" in result and not result["current_turn"]["completed"]:
            message += " 当前轮日志尚未标记结束。"
        return {"systemMessage": message}
    except Exception:
        # Hook failure must not leak a transcript or interfere with the user's task.
        return {"systemMessage": "Token：回复后统计暂不可用（本地记账读取失败）。"}


def main() -> int:
    try:
        # Stop payload may include the final response. Parse in memory, never print it.
        raw = sys.stdin.read(2_000_001)
        event = json.loads(raw) if len(raw) <= 2_000_000 else None
        output = handle(event)
    except Exception:
        output = {"systemMessage": "Token：回复后统计暂不可用（事件无法读取）。"}
    print(json.dumps(output, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8")
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
