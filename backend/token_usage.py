#!/usr/bin/env python3
"""Read Codex rollout accounting only. No network, log writes or token estimates."""
from __future__ import annotations

import argparse
from collections import defaultdict
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import sys
from typing import Any

FIELDS = ("input_tokens", "output_tokens", "total_tokens", "cached_input_tokens",
          "reasoning_output_tokens", "cache_write_input_tokens")
CORE = FIELDS[:3]
ID = re.compile(r"^[A-Za-z0-9_-]{1,128}$")
BOUNDARY_NOTE = "截至已落盘用量；尚未落盘的本次回答尾部及后续用量不在统计内。"


def valid_id(value: Any) -> str | None:
    return value if isinstance(value, str) and ID.fullmatch(value) else None


def usage(value: Any) -> dict | None:
    if not isinstance(value, dict):
        return None
    result = {k: value.get(k) for k in FIELDS}
    if any(type(result[k]) is not int or result[k] < 0 for k in CORE):
        return None
    for k in FIELDS[3:]:
        if type(result[k]) is not int or result[k] < 0:
            result[k] = None
    if result["total_tokens"] != result["input_tokens"] + result["output_tokens"]:
        return None
    if (result["cached_input_tokens"] or 0) > result["input_tokens"]:
        return None
    if (result["reasoning_output_tokens"] or 0) > result["output_tokens"]:
        return None
    return result


def add(values: list[dict]) -> dict:
    return {k: None if any(v[k] is None for v in values) else sum(v[k] for v in values)
            for k in FIELDS}


def subtract(a: dict, b: dict) -> dict | None:
    out = {k: a[k] - b[k] if a[k] is not None and b[k] is not None else None
           for k in FIELDS}
    return usage(out)


def metric(totals: dict | None, status: str, source: str, issues=()) -> dict:
    return {"status": status, "source": source, "tokens": totals,
            "issues": sorted(set(issues))}


def combine(parts: list[dict]) -> dict:
    known = [p["tokens"] for p in parts if p["tokens"] is not None]
    issues = [i for p in parts for i in p["issues"]]
    if not known or (any(p["tokens"] is None for p in parts)
                     and all(v["total_tokens"] == 0 for v in known)):
        return metric(None, "unavailable", "aggregate", issues)
    exact = all(p["status"] == "available" for p in parts)
    return metric(add(known), "available" if exact else "partial", "aggregate", issues)


def metadata(path: Path) -> dict | None:
    """Discovery reads only the first metadata line, never other threads' messages."""
    try:
        with path.open("rb") as handle:
            first = json.loads(handle.readline())
        if first.get("type") != "session_meta":
            return None
        p = first.get("payload", {})
        tid = valid_id(p.get("id"))
        if not tid:
            return None
        source = p.get("source")
        sub = source.get("subagent", {}) if isinstance(source, dict) else {}
        spawn = sub.get("thread_spawn", {}) if isinstance(sub, dict) else {}
        parent = valid_id(spawn.get("parent_thread_id")) if isinstance(spawn, dict) else None
        return {"id": tid, "parent": parent, "fork": valid_id(p.get("forked_from_id"))}
    except (OSError, ValueError, TypeError, AttributeError):
        return None


def discover(home: Path) -> tuple[dict, int]:
    registry: dict[str, dict] = {}
    unreadable = 0
    for directory in (home / "archived_sessions", home / "sessions"):
        if not directory.is_dir():
            continue
        for path in sorted(directory.rglob("*.jsonl")):
            meta = metadata(path)
            if meta is None:
                unreadable += 1
                continue
            item = registry.setdefault(meta["id"], {**meta, "paths": []})
            item["paths"].append(path)
    return registry, unreadable


class Accounting:
    def __init__(self, meta: dict):
        self.meta = meta
        self.records: dict[str, dict] = {}
        self.has_records = False
        self.record_issues: list[str] = []
        self.log_issues: list[str] = []
        self.boundaries: list[tuple[str, str, str, str]] = []
        self.contexts: dict[str, str] = {}
        self.completed: set[str] = set()
        self.legacy: list[tuple[str | None, dict]] = []
        self.legacy_missing = False
        self.compactions = 0
        self.latest_timestamp: str | None = None
        self.read()

    def read(self):
        # Ordinals are not required: response IDs deduplicate archive/live copies.
        for path in self.meta["paths"]:
            active = None
            try:
                with path.open("rb") as handle:
                    for line in handle:
                        if not line.endswith(b"\n"):
                            # Even valid JSON without a newline may be a concurrently written tail.
                            self.log_issues.append("incomplete_tail_ignored")
                            continue
                        try:
                            event = json.loads(line)
                        except (ValueError, TypeError, UnicodeError):
                            self.log_issues.append("malformed_record_ignored")
                            continue
                        if not isinstance(event, dict):
                            self.log_issues.append("malformed_record_ignored")
                            continue
                        kind, p = event.get("type"), event.get("payload", {})
                        if not isinstance(p, dict):
                            continue
                        stamp = event.get("timestamp")
                        safe_stamp = ""
                        if isinstance(stamp, str) and re.fullmatch(r"[0-9T:.Z+\-]{10,40}", stamp):
                            try:
                                parsed_stamp = datetime.fromisoformat(stamp.replace("Z", "+00:00"))
                                if parsed_stamp.tzinfo is not None:
                                    safe_stamp = parsed_stamp.astimezone(timezone.utc).isoformat()
                            except ValueError:
                                pass
                        if safe_stamp:
                            stamp = safe_stamp
                            if self.latest_timestamp is None or stamp > self.latest_timestamp:
                                self.latest_timestamp = stamp
                        if kind == "compacted":
                            self.compactions += 1
                        if kind == "turn_context":
                            turn = valid_id(p.get("turn_id"))
                            if turn:
                                active = turn
                                root = valid_id(p.get("root_turn_id")) or turn
                                self.contexts[turn] = root
                                self.boundaries.append((turn, root, "turn_context", safe_stamp))
                        if kind == "event_msg":
                            if p.get("type") == "task_started":
                                turn = valid_id(p.get("turn_id"))
                                if turn:
                                    active = turn
                                    self.boundaries.append((turn, self.contexts.get(turn, turn), "task_started", safe_stamp))
                            elif p.get("type") == "task_complete":
                                turn = valid_id(p.get("turn_id"))
                                if turn:
                                    self.completed.add(turn)
                            elif p.get("type") == "token_count":
                                info = p.get("info")
                                count = usage(info.get("total_token_usage")) if isinstance(info, dict) else None
                                if count:
                                    self.legacy.append((active, count))
                                else:
                                    self.legacy_missing = True
                        if kind != "token_usage_record" or p.get("thread_id") != self.meta["id"]:
                            continue
                        self.has_records = True
                        rid = valid_id(p.get("response_id"))
                        turn = valid_id(p.get("turn_id"))
                        root = valid_id(p.get("root_turn_id"))
                        if root is None and self.meta.get("parent"):
                            self.record_issues.append("subagent_root_turn_id_missing")
                        elif root is None:
                            root = turn
                        count = usage(p.get("usage"))
                        if not rid or not turn or not count:
                            self.record_issues.append("invalid_or_missing_usage_record")
                            continue
                        record = {"turn": turn, "root": root, "usage": count,
                                  "turn_total": usage(p.get("turn_token_usage")),
                                  "thread_total": usage(p.get("thread_token_usage"))}
                        if rid in self.records and self.records[rid] != record:
                            self.record_issues.append("conflicting_duplicate_response_id")
                        else:
                            self.records[rid] = record
            except OSError:
                self.log_issues.append("selected_log_read_failed")

    def boundary(self, requested: str | None = None) -> tuple[str | None, str | None, str]:
        if requested:
            root = self.contexts.get(requested)
            if root is None:
                root = next((r["root"] for r in self.records.values() if r["turn"] == requested), requested)
            return requested, root, "explicit_turn_id"
        if self.boundaries:
            _, (turn, root, source, _) = max(enumerate(self.boundaries), key=lambda b: (b[1][3], b[0]))
            # Context after compaction can repeat the same turn; user messages never reset it.
            return turn, self.contexts.get(turn, root), source
        if self.records:
            r = next(reversed(self.records.values()))
            return r["turn"], r["root"], "latest_accounted_turn_only"
        return None, None, "unavailable"

    def modern(self, turn: str | None = None, root: str | None = None,
               allow_empty: bool = False) -> dict:
        all_records = list(self.records.values())
        selected = [r for r in all_records if (turn is None or r["turn"] == turn)
                    and (root is None or r["root"] == root)]
        issues = list(self.record_issues) + list(self.log_issues)
        if not selected:
            if allow_empty and all_records and not issues and self.modern()["status"] == "available":
                return metric(add([]), "available", "token_usage_record")
            return metric(None, "unavailable", "token_usage_record", issues + ["no_accounted_usage"])
        # Validate every local turn. Cumulative snapshots detect missing per-response records.
        groups: dict[str, list[dict]] = defaultdict(list)
        for r in all_records:
            groups[r["turn"]].append(r)
        relevant_turns = {r["turn"] for r in selected}
        for tid in relevant_turns:
            group = groups[tid]
            snapshots = [r["turn_total"] for r in group if r["turn_total"] is not None]
            if snapshots:
                largest = max(snapshots, key=lambda u: u["total_tokens"])
                summed = add([r["usage"] for r in group])
                if any(largest[k] != summed[k] for k in CORE):
                    issues.append("turn_cumulative_mismatch")
        if turn is None and root is None:
            snapshots = [r["thread_total"] for r in all_records if r["thread_total"] is not None]
            if snapshots:
                largest = max(snapshots, key=lambda u: u["total_tokens"])
                summed = add([r["usage"] for r in all_records])
                if any(largest[k] != summed[k] for k in CORE):
                    issues.append("thread_cumulative_mismatch")
        return metric(add([r["usage"] for r in selected]),
                      "partial" if issues else "available", "token_usage_record", issues)

    def old(self, turn: str | None = None) -> dict:
        issues = list(self.log_issues)
        if self.meta.get("fork") or self.meta.get("parent"):
            return metric(None, "unavailable", "token_count", ["legacy_fork_ownership_unknown"])
        if len(self.meta["paths"]) > 1:
            return metric(None, "unavailable", "token_count", ["legacy_multiple_logs_order_unknown"])
        if not self.legacy:
            return metric(None, "unavailable", "token_count", issues + ["no_accounted_usage"])
        if turn is not None:
            first_turns = list(dict.fromkeys(b[0] for b in self.boundaries))
            before_target = []
            for tid, count in self.legacy:
                if tid == turn:
                    break
                before_target.append(count)
            if first_turns and first_turns[0] != turn and not before_target:
                return metric(None, "unavailable", "token_count", ["legacy_turn_baseline_missing"])
        previous = add([])
        deltas = []
        matched = False
        for tid, count in self.legacy:
            delta = subtract(count, previous)
            if delta is None:
                return metric(None, "unavailable", "token_count", issues + ["legacy_counter_reset_or_invalid_delta"])
            if turn is None or tid == turn:
                matched = True
                deltas.append(delta)
            previous = count
        if not matched:
            return metric(None, "unavailable", "token_count", issues + ["turn_boundary_or_usage_missing"])
        if turn is not None and self.compactions:
            # Legacy snapshots cannot attribute compaction accounting to a response/turn.
            issues.append("legacy_compaction_attribution_unverified")
        if self.legacy_missing:
            issues.append("legacy_missing_usage_event")
        return metric(add(deltas), "partial" if issues else "available", "token_count", issues)

    def total(self):
        return self.modern() if self.has_records else self.old()

    def current(self, turn, root, child=False):
        if root is None or (turn is None and not child):
            return metric(None, "unavailable", "none", ["turn_boundary_unknown"])
        if self.has_records:
            return self.modern(root=root, allow_empty=True) if child else self.modern(turn=turn)
        if child:
            return metric(None, "unavailable", "token_count", ["legacy_subagent_root_turn_unknown"])
        return self.old(turn)


def collect(home: Path, thread_id: str, turn_id: str | None = None,
            include_subagents: bool = True, transcript_path: Path | None = None) -> dict:
    if not valid_id(thread_id) or (turn_id is not None and not valid_id(turn_id)):
        return {"schema_version": 1, "status": "unavailable", "error": "invalid_identifier", "note": BOUNDARY_NOTE}
    registry, failed_headers = discover(Path(home)) if include_subagents or transcript_path is None else ({}, 0)
    if transcript_path is not None:
        explicit = metadata(Path(transcript_path))
        if explicit and explicit["id"] == thread_id:
            registry[thread_id] = {**explicit, "paths": [Path(transcript_path)]}
        else:
            return {"schema_version": 1, "status": "unavailable", "thread_id": thread_id,
                    "error": "transcript_metadata_mismatch_or_unreadable", "note": BOUNDARY_NOTE}
    if thread_id not in registry:
        return {"schema_version": 1, "status": "unavailable", "thread_id": thread_id,
                "error": "thread_log_not_found", "note": BOUNDARY_NOTE}
    selected = {thread_id}
    if include_subagents:
        while True:
            new = {tid for tid, m in registry.items() if m["parent"] in selected}
            if new <= selected:
                break
            selected |= new
    accounts = {tid: Accounting(registry[tid]) for tid in sorted(selected)}
    main = accounts[thread_id]
    turn, root, boundary = main.boundary(turn_id)
    current_self = main.current(turn, root)
    if boundary in ("latest_accounted_turn_only", "unavailable"):
        current_self = metric(None, "unavailable", "none", ["current_turn_boundary_unknown"])
    children = [a for tid, a in accounts.items() if tid != thread_id]
    current_children = [a.current(None, root, child=True) for a in children]
    total_children = [a.total() for a in children]
    current_sub = combine(current_children) if children else metric(add([]), "available", "none")
    total_sub = combine(total_children) if children else metric(add([]), "available", "none")
    total_self = main.total()
    if main.meta.get("parent") and children:
        # A root turn can contain several follow-up turns in a child. Their descendants
        # cannot be split between those local turns using root_turn_id alone.
        local = {r["turn"] for r in main.records.values() if r["root"] == root}
        if len(local) > 1:
            current_sub["status"] = "partial"
            current_sub["issues"].append("descendants_grouped_by_root_not_local_turn")
    return {"schema_version": 1, "thread_id": thread_id,
            "snapshot_at": datetime.now(timezone.utc).isoformat(),
            "latest_log_timestamp": main.latest_timestamp,
            "current_turn": {"turn_id": turn, "root_turn_id": root, "boundary": boundary,
                             "completed": turn in main.completed,
                             "self": current_self, "subagents": current_sub,
                             "combined": combine([current_self, current_sub])},
            "thread_total": {"self": total_self, "subagents": total_sub,
                             "combined": combine([total_self, total_sub])},
            "subagent_threads": [{"thread_id": a.meta["id"], "parent_thread_id": a.meta["parent"],
                                  "current_turn": c, "thread_total": t}
                                 for a, c, t in zip(children, current_children, total_children)],
            "discovery": {"subagents_included": include_subagents,
                          "associated_threads": len(children), "unreadable_metadata_files": failed_headers,
                          "scope": "locally_discovered_subagent_metadata"},
            "accounting": {"cached_input_is_subset_of_input": True,
                           "reasoning_output_is_subset_of_output": True,
                           "counts_are_provider_accounting_not_model_estimates": True},
            "note": BOUNDARY_NOTE}


def breakdown(m: dict) -> list[str]:
    t = m["tokens"]
    if t is None:
        return ["不可统计"] * 5
    cached = t.get("cached_input_tokens")
    noncached = t["input_tokens"] - cached if cached is not None else None
    values = [t["input_tokens"], cached, noncached, t["output_tokens"], t["total_tokens"]]
    return [f"{v:,}" if v is not None else "不可统计" for v in values]


def table(result: dict) -> str:
    if "current_turn" not in result:
        return "Token 用量：无法读取当前任务用量。" + BOUNDARY_NOTE
    rows = [("主任务本轮增量", result["current_turn"]["self"])]
    for child in result.get("subagent_threads", []):
        m = child["current_turn"]
        if m["status"] == "available" and m["tokens"] is not None and m["tokens"]["total_tokens"] == 0:
            continue
        rows.append((f"子代理 {child['thread_id'][:8]} 本轮", m))
    rows += [("本轮合计", result["current_turn"]["combined"]),
             ("任务累计（含子代理）", result["thread_total"]["combined"])]
    lines = ["| 范围 | 输入 | 其中缓存输入 | 非缓存输入 | 输出（含推理） | 总数 |",
             "| --- | ---: | ---: | ---: | ---: | ---: |"]
    for label, m in rows:
        if m["status"] == "partial":
            label += "（部分记录）"
        lines.append("| " + " | ".join([label] + breakdown(m)) + " |")
    lines += ["", "输入 = 缓存输入 + 非缓存输入；总数 = 输入 + 输出。缓存不另加，推理已含在输出中。", BOUNDARY_NOTE]
    issues = sorted({issue for _, m in rows for issue in m.get("issues", [])})
    # Fixed accounting codes only; never interpolate transcript contents.
    if issues:
        lines.append("记账诊断：" + ", ".join(i for i in issues if re.fullmatch(r"[a-z_]+", i)) + "。")
    unreadable = result.get("discovery", {}).get("unreadable_metadata_files", 0)
    if unreadable:
        lines.append(f"有 {unreadable} 份日志的元数据无法读取，关联子代理覆盖可能不完整。")
    return "\n".join(lines)


def short(result: dict) -> str:
    if "current_turn" not in result:
        return "Token 用量：无法读取当前线程用量。" + BOUNDARY_NOTE
    def render(m):
        if m["tokens"] is None:
            return "无法可靠统计"
        label = "已记录部分：" if m["status"] != "available" else ""
        inp, cached, noncached, out, total = breakdown(m)
        return f"{label}输入 {inp}（缓存 {cached}，非缓存 {noncached}） / 输出（含推理） {out} / 总数 {total}"
    count = result["discovery"]["associated_threads"]
    return (f"Token：本轮 {render(result['current_turn']['combined'])}；"
            f"线程累计 {render(result['thread_total']['combined'])}"
            f"（含 {count} 个关联子代理；缓存输入、推理输出已含于各自总量）。"
            + BOUNDARY_NOTE)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--thread-id", default=os.environ.get("CODEX_THREAD_ID"))
    parser.add_argument("--turn-id")
    parser.add_argument("--transcript-path", type=Path)
    parser.add_argument("--codex-home", type=Path,
                        default=Path(os.environ["CODEX_HOME"]) if os.environ.get("CODEX_HOME") else None)
    parser.add_argument("--format", choices=("json", "short", "table"), default="json")
    parser.add_argument("--no-subagents", action="store_true")
    args = parser.parse_args(argv)
    if not valid_id(args.thread_id) or (args.turn_id and not valid_id(args.turn_id)):
        result = {"schema_version": 1, "status": "unavailable", "error": "missing_or_invalid_thread_or_turn_id",
                  "note": BOUNDARY_NOTE}
    else:
        try:
            home = (args.codex_home or Path.home() / ".codex").expanduser()
            result = collect(home, args.thread_id, args.turn_id,
                             not args.no_subagents, args.transcript_path)
        except (OSError, ValueError, RuntimeError):
            # Never print raw log content, paths, exceptions, credentials or conversation text.
            result = {"schema_version": 1, "status": "unavailable", "error": "log_access_failed",
                      "note": BOUNDARY_NOTE}
    print(table(result) if args.format == "table" else short(result) if args.format == "short" else json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if "current_turn" in result else 2


if __name__ == "__main__":
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
