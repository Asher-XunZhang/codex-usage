"""Incremental, local-only usage index. Retains accounting fields, never message bodies."""
from __future__ import annotations

from collections import defaultdict
from contextlib import closing
from datetime import datetime, timedelta, timezone
import json
import sqlite3
from pathlib import Path
import threading
import time

from token_usage import FIELDS, CORE, usage, valid_id, metadata, subtract, add

TZ = timezone(timedelta(hours=8))


def timestamp(value):
    try:
        stamp = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if stamp.tzinfo is None or not 2000 <= stamp.year <= datetime.now(TZ).year + 1:
            return None
        return stamp.astimezone(TZ).isoformat()
    except (ValueError, TypeError, AttributeError):
        return None


class LogFile:
    def __init__(self, path):
        self.path = path
        self.meta = metadata(path)
        self.offset = 0
        self.size = 0
        self.mtime = 0
        self.model = "未标注模型"
        self.rows = {}
        self.legacy = False
        self.modern = False
        self.issues = set()
        self.pending_tail = False
        self.legacy_rows = []
        self.legacy_previous = add([])
        self.legacy_reset = False
        self.legacy_skipped = 0
        self.turn = None
        self.identity = None

    def update(self, progress=None, stop=None):
        stat = self.path.stat()
        identity = (stat.st_dev, stat.st_ino)
        if self.identity == identity and stat.st_size == self.size and stat.st_mtime_ns == self.mtime and self.meta is not None:
            return
        if (self.identity is not None and self.identity != identity) or stat.st_size < self.size or (stat.st_size == self.size and stat.st_mtime_ns != self.mtime):
            self.__init__(self.path)
        if self.meta is None:
            self.meta = metadata(self.path)
            if self.meta is None:
                self.issues.add("日志元数据不可读取")
                return
            self.issues.discard("日志元数据不可读取")
        with self.path.open("rb") as stream:
            opened = stream.fileno()
            import os
            current = os.fstat(opened)
            if (current.st_dev, current.st_ino) != identity:
                return  # Replaced during open; retry the new file on the next scan.
            stream.seek(self.offset)
            self.pending_tail = False
            lines = 0
            while True:
                lines += 1
                if lines % 256 == 0:
                    if progress:
                        progress()
                    if stop is not None and stop.is_set():
                        return
                line = stream.readline()
                if not line:
                    break
                if not line.endswith(b"\n"):
                    self.pending_tail = True
                    break
                self.offset = stream.tell()
                if not any(key in line for key in (b'"token_usage_record"', b'"turn_context"', b'"token_count"')):
                    continue
                try:
                    event = json.loads(line)
                except (ValueError, UnicodeError):
                    self.issues.add("存在损坏记账行")
                    continue
                if not isinstance(event, dict) or not isinstance(event.get("payload"), dict):
                    continue
                p = event["payload"]
                if event.get("type") == "turn_context":
                    self.turn = valid_id(p.get("turn_id"))
                    model = p.get("model")
                    if isinstance(model, str) and model.strip():
                        self.model = model[:100]
                elif event.get("type") == "event_msg" and p.get("type") == "token_count":
                    self.legacy = True
                    # These histories can never contribute legacy rows: modern records
                    # own accounting, and fork/subagent legacy ownership is ambiguous.
                    if self.modern or self.legacy_reset or self.meta.get("fork") or self.meta.get("parent"):
                        continue
                    info = p.get("info")
                    count = usage(info.get("total_token_usage")) if isinstance(info, dict) else None
                    last = usage(info.get("last_token_usage")) if isinstance(info, dict) else None
                    stamp = timestamp(event.get("timestamp"))
                    if count is None:
                        self.legacy_skipped += 1
                        continue
                    delta = subtract(count, self.legacy_previous)
                    self.legacy_previous = count
                    if delta is None:
                        self.legacy_reset = True
                        self.legacy_rows.clear()
                        continue
                    if delta["total_tokens"] == 0:
                        continue  # Repeated cumulative notification, not a new call.
                    if not last or not stamp or any(delta[k] != last[k] for k in CORE):
                        self.legacy_skipped += 1
                        continue
                    # Subset disagreements are unknown, never a fabricated cache split.
                    for key in FIELDS[3:]:
                        if delta[key] != last[key]:
                            delta[key] = None
                    self.legacy_rows.append({"thread": self.meta["id"], "turn": self.turn,
                        "response": f"legacy-{self.offset}", "model": self.model,
                        "timestamp": stamp, "date": stamp[:10], "usage": delta,
                        "thread_total": None, "source": "legacy_verified_delta"})
                elif event.get("type") == "token_usage_record" and p.get("thread_id") == self.meta["id"]:
                    self.modern = True
                    self.legacy_rows.clear()
                    rid, turn = valid_id(p.get("response_id")), valid_id(p.get("turn_id"))
                    count, stamp = usage(p.get("usage")), timestamp(event.get("timestamp"))
                    if not rid or not turn or count is None or stamp is None:
                        self.issues.add("存在缺失字段的记账记录")
                        continue
                    row = {"thread": self.meta["id"], "response": rid, "turn": turn,
                           "model": self.model, "timestamp": stamp, "date": stamp[:10],
                           "usage": count, "thread_total": usage(p.get("thread_token_usage"))}
                    old = self.rows.get(rid)
                    if old and (old["usage"] != count or old["turn"] != turn):
                        self.issues.add("存在冲突的重复记账")
                    else:
                        self.rows[rid] = row
        self.size, self.mtime, self.identity = stat.st_size, stat.st_mtime_ns, identity


class IndexedRow:
    """Immutable ownership view; shares accounting instead of copying a dict per row."""
    __slots__ = ("record", "task")

    def __init__(self, record, task):
        self.record = record
        self.task = task

    def __getitem__(self, key):
        return self.task if key == "task" else self.record[key]


def totals(rows):
    result = dict.fromkeys(FIELDS, 0)
    tasks = set()
    for row in rows:
        count = row["usage"]
        tasks.add(row["task"])
        for key in FIELDS:
            if result[key] is not None:
                result[key] = result[key] + count[key] if count[key] is not None else None
    cached = result["cached_input_tokens"]
    result["noncached_input_tokens"] = result["input_tokens"] - cached if cached is not None else None
    result["requests"] = len(rows)
    result["active_tasks"] = len(tasks)
    result["cache_hit_rate"] = (cached / result["input_tokens"] * 100
                                if cached is not None and result["input_tokens"] else None)
    return result


class UsageIndex:
    def __init__(self, home, refresh_seconds=30):
        self.home = Path(home)
        self.files = {}
        self.lock = threading.Lock()
        self.rows = []
        self.tasks = []
        self.models = []
        self.loading = True
        self.scanned = 0
        self.excluded = 0
        self.issues = []
        self.updated = None
        self.stop = threading.Event()
        self.coverage = {}
        self.started_monotonic = time.monotonic()
        self.last_success_monotonic = None
        self.last_progress_monotonic = self.started_monotonic
        self.scanning = False
        self.refresh_condition = threading.Condition()
        self.refresh_seconds = refresh_seconds
        self.refresh_requested = 0
        self.refresh_completed = 0
        self.refresh_error = None
        self.scan_duration_ms = None
        self.metadata_signature = None
        self.aggregate_dirty = True

    def request_refresh(self):
        with self.refresh_condition:
            self.refresh_requested += 1
            self.refresh_condition.notify_all()
            return self.refresh_requested

    def configure_refresh(self, seconds):
        if type(seconds) is not int or not 0 <= seconds <= 3600:
            raise ValueError("Refresh interval must be an integer from 0 to 3600")
        with self.refresh_condition:
            self.refresh_seconds = seconds
            self.refresh_condition.notify_all()

    def wait_for_refresh(self, ticket, timeout):
        with self.refresh_condition:
            self.refresh_condition.wait_for(lambda: self.refresh_completed >= ticket or self.stop.is_set(), timeout)
            return self.refresh_completed

    def health(self):
        # No index lock or aggregation: this endpoint must work during a scan.
        now = time.monotonic()
        last_success = self.last_success_monotonic
        age = max(0, now - (last_success if last_success is not None else self.started_monotonic))
        progressing = self.scanning and now - self.last_progress_monotonic < 120
        allowed_age = max(120, self.refresh_seconds * 2 + 10)
        deliberately_idle = self.refresh_seconds == 0 and not self.scanning
        status = ("initializing" if age < 300 or progressing else "stalled") if last_success is None else ("healthy" if age < allowed_age or progressing or deliberately_idle else "stalled")
        return {"status": status, "ready": status == "healthy", "snapshot_age_seconds": round(age, 1),
                "generated_at": self.updated, "scanning": self.scanning,
                "refresh_seconds": self.refresh_seconds, "refresh_requested": self.refresh_requested,
                "refresh_completed": self.refresh_completed, "refresh_error": self.refresh_error,
                "scan_duration_ms": self.scan_duration_ms}

    def progress(self):
        self.last_progress_monotonic = time.monotonic()

    def scan(self):
        paths = set()
        errors = set()
        for directory in (self.home / "sessions", self.home / "archived_sessions"):
            if directory.is_dir():
                paths.update(directory.rglob("*.jsonl"))
        if not paths:
            errors.add("未发现会话日志；请先在 Codex 完成一次对话，或检查 CODEX_HOME 和读取权限")
        changed = set(self.files) != paths
        for path in sorted(paths):
            if self.stop.is_set():
                return
            self.progress()
            try:
                item = self.files.setdefault(path, LogFile(path)) if path not in self.files else self.files[path]
                before = (item.size, item.mtime, item.identity, item.meta is None)
                item.update(self.progress, self.stop)
                changed |= before != (item.size, item.mtime, item.identity, item.meta is None)
            except OSError:
                changed = True
                errors.add("部分日志暂时无法读取")
            self.scanned += 1 if self.loading else 0
        self.files = {p: item for p, item in self.files.items() if p in paths}
        signatures = []
        for name in ("session_index.jsonl", "state_5.sqlite", "state_5.sqlite-wal"):
            try:
                stat = (self.home / name).stat()
                signatures.append((name, stat.st_ino, stat.st_size, stat.st_mtime_ns))
            except OSError:
                signatures.append((name, None))
        signature = tuple(signatures)
        self.aggregate_dirty |= changed or signature != self.metadata_signature
        if not self.aggregate_dirty and not self.loading:
            # No parsing, SQLite reads, or aggregate rebuild for unchanged files.
            with self.lock:
                self.updated = datetime.now(TZ).isoformat()
                self.last_success_monotonic = time.monotonic()
            return
        registry, records, legacy, modern = {}, {}, set(), set()
        by_thread = defaultdict(list)
        for item in self.files.values():
            errors.update(item.issues)
            if item.pending_tail:
                errors.add("正在写入的日志尾部将在后续刷新纳入")
            if not item.meta:
                continue
            if not item.modern and not item.legacy:
                errors.add("部分会话尚无可识别的记账记录，不能据此认定没有消耗")
            tid = item.meta["id"]
            by_thread[tid].append(item)
            registry[tid] = item.meta
            if item.legacy:
                legacy.add(tid)
            if item.modern:
                modern.add(tid)
            for rid, row in item.rows.items():
                key = (tid, rid)
                old = records.get(key)
                if old and (old["usage"] != row["usage"] or old["turn"] != row["turn"]):
                    errors.add("存在冲突的重复记账，统计可能不完整")
                elif not old or (old["model"] == "未标注模型" and row["model"] != "未标注模型"):
                    records[key] = row
        names = {}
        try:
            with (self.home / "session_index.jsonl").open(encoding="utf-8") as stream:
                for line in stream:
                    try:
                        item = json.loads(line)
                        if valid_id(item.get("id")) and isinstance(item.get("thread_name"), str):
                            names[item["id"]] = item["thread_name"][:160]
                    except (ValueError, TypeError, AttributeError):
                        continue
        except OSError:
            pass

        # The session index is incomplete; the app database owns current titles.
        # Read naming metadata only, never previews, prompts, credentials or bodies.
        archived, system_tasks = {}, set()
        database = self.home / "state_5.sqlite"
        if database.is_file():
            try:
                with closing(sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True, timeout=1)) as db:
                    columns = {r[1] for r in db.execute("PRAGMA table_info(threads)")}
                    if {"id", "title", "archived"} <= columns:
                        name_expr = "name" if "name" in columns else "NULL"
                        source_expr = "source" if "source" in columns else "NULL"
                        for tid, name, title, is_archived, source in db.execute(f"SELECT id, substr({name_expr},1,512), substr(title,1,512), archived, {source_expr} FROM threads"):
                            label = name or title
                            if valid_id(tid) and isinstance(label, str) and label.strip():
                                names[tid] = " ".join(label.split())[:160]
                            archived[tid] = bool(is_archived)
                            try:
                                source_data = json.loads(source) if source else {}
                                if isinstance(source_data, dict) and source_data.get("subagent", {}).get("other") == "guardian":
                                    system_tasks.add(tid)
                            except (ValueError, TypeError, AttributeError):
                                pass
            except sqlite3.Error:
                errors.add("任务标题数据库暂不可读，使用已有名称")

        def root(tid):
            seen = set()
            while tid in registry and registry[tid].get("parent"):
                if tid in seen:
                    errors.add("存在异常的子代理关系")
                    break
                seen.add(tid)
                tid = registry[tid]["parent"]
            return tid

        recovered, partial, reasons = set(), set(), defaultdict(int)
        for tid in legacy - modern:
            items = by_thread[tid]
            item = items[0]
            if any(x.meta.get("fork") or x.meta.get("parent") for x in items):
                reasons["分叉或子代理归属不明"] += 1
            elif len(items) != 1:
                reasons["多个日志副本的先后关系不明"] += 1
            elif item.legacy_reset:
                reasons["累计计数重置或差值无效"] += 1
            elif not item.legacy_rows:
                reasons["缺少可交叉核对的调用记录"] += 1
            else:
                recovered.add(tid)
                if item.legacy_skipped:
                    partial.add(tid)
                for row in item.legacy_rows:
                    records[(tid, row["response"])] = row
        rows = []
        owners = defaultdict(list)
        for record in records.values():
            row = IndexedRow(record, root(record["thread"]))
            rows.append(row)
            owners[row["thread"]].append(row)
        gaps = []
        for owner, owner_rows in owners.items():
            hints = [r["thread_total"] for r in owner_rows if r["thread_total"] is not None]
            if hints:
                hint = max(hints, key=lambda u: u["total_tokens"])
                if any(sum(r["usage"][k] for r in owner_rows) != hint[k] for k in CORE):
                    counted = add([r["usage"] for r in owner_rows])
                    gaps.append({"thread": owner, "label": names.get(owner) or f"任务 {owner}",
                                 "recorded_total": counted["total_tokens"],
                                 "cumulative_total": hint["total_tokens"],
                                 "difference": hint["total_tokens"] - counted["total_tokens"]})
        if gaps:
            errors.add(f"{len(gaps)} 个任务存在累计差异，详见下方全局覆盖诊断；差额未重复加入用量")
        tasks = sorted({r["task"] for r in rows})
        last_seen = {}
        task_models = defaultdict(set)
        for row in rows:
            last_seen[row["task"]] = max(last_seen.get(row["task"], ""), row["timestamp"])
            task_models[row["task"]].add(row["model"])
        system_tasks.update(t for t, models in task_models.items() if models == {"codex-auto-review"})
        for tid in system_tasks:
            names[tid] = f"后台审批检查 · {last_seen.get(tid, '')[5:16].replace('T', ' ')}"
        def task_label(tid):
            return names.get(tid) or f"未命名任务 · {last_seen.get(tid, '')[5:16].replace('T', ' ')}"
        for gap in gaps:
            gap["label"] = names.get(gap["thread"]) or "未命名任务"
        with self.lock:
            self.rows = sorted(rows, key=lambda r: r["timestamp"])
            self.tasks = [{"id": tid, "label": task_label(tid), "named": bool(names.get(tid)),
                           "archived": archived.get(tid, False), "last_used": last_seen[tid]} for tid in tasks]
            for task_item in self.tasks:
                task_item["system"] = task_item["id"] in system_tasks
            self.models = sorted({r["model"] for r in rows})
            self.scanned = len(paths)
            self.excluded = len(legacy - modern - recovered)
            self.coverage = {"legacy_threads": len(legacy-modern), "recovered_legacy_threads": len(recovered),
                             "partial_legacy_threads": len(partial), "excluded_reasons": dict(reasons),
                             "reconstructed_calls": sum(len(by_thread[t][0].legacy_rows) for t in recovered),
                             "cumulative_gaps": gaps}
            self.issues = sorted(errors)
            self.updated = datetime.now(TZ).isoformat()
            self.loading = False
            self.last_success_monotonic = time.monotonic()
            self.metadata_signature = signature
            self.aggregate_dirty = bool(errors & {"部分日志暂时无法读取", "任务标题数据库暂不可读，使用已有名称"})

    def run(self):
        first = True
        finished = time.monotonic()
        while not self.stop.is_set():
            with self.refresh_condition:
                while not first and self.refresh_requested <= self.refresh_completed and not self.stop.is_set():
                    remaining = self.refresh_seconds - (time.monotonic() - finished)
                    if self.refresh_seconds and remaining <= 0:
                        break
                    self.refresh_condition.wait(min(0.25, max(0.01, remaining)) if self.refresh_seconds else 0.25)
                if self.stop.is_set():
                    return
                # A request that arrives after this point needs the next scan. It must
                # never be acknowledged by a scan that began before the request.
                ticket = self.refresh_requested
                self.scanning = True
            first = False
            began = time.monotonic()
            error = None
            try:
                self.progress()
                self.scan()
            except Exception:
                error = "读取未完成，保留上次成功快照；可点击刷新重试"
                with self.lock:
                    self.issues = [error]
            finally:
                with self.refresh_condition:
                    finished = time.monotonic()
                    self.scan_duration_ms = round((finished - began) * 1000)
                    self.refresh_error = error
                    self.refresh_completed = ticket
                    self.scanning = False
                    self.refresh_condition.notify_all()

    def query(self, days="30", model="all", task="all", group="model", now=None, summary_only=False):
        if days not in ("1", "7", "30", "90", "all") or group not in ("model", "task"):
            raise ValueError("Invalid filters")
        now = now or datetime.now(TZ)
        today = now.astimezone(TZ).date()
        with self.lock:
            rows, tasks, models = self.rows, list(self.tasks), list(self.models)
            meta = {"application": "codex-token-usage-dashboard", "generated_at": self.updated, "time_zone": "UTC+08:00", "refresh_seconds": self.refresh_seconds,
                    "refresh_error": self.refresh_error, "scan_duration_ms": self.scan_duration_ms,
                    "loading": self.loading, "scanned_files": self.scanned,
                    "excluded_legacy_threads": self.excluded, "issues": list(self.issues),
                    "coverage": self.coverage}
        if model != "all" and model not in models:
            raise ValueError("Unknown model")
        if task != "all" and task not in {t["id"] for t in tasks}:
            raise ValueError("Unknown task")
        start = (today - timedelta(days=int(days)-1)).isoformat() if days != "all" else ""
        eligible = [r for r in rows if (not start or r["date"] >= start) and r["date"] <= today.isoformat()
                    and (model == "all" or r["model"] == model)]
        selected = [r for r in eligible if task == "all" or r["task"] == task]
        if summary_only:
            summary = totals(selected)
            if not rows:
                summary = {key: None for key in summary}
            return {"meta": {key: meta[key] for key in ("generated_at", "time_zone", "refresh_seconds", "loading", "refresh_error")},
                    "summary": summary,
                    "filters": {"selected_task": next((t for t in tasks if t["id"] == task), None)}}
        recent = {}
        for row in eligible:
            recent[row["task"]] = max(recent.get(row["task"], ""), row["timestamp"])
        task_choices = [dict(t, last_used=recent[t["id"]]) for t in tasks if t["id"] in recent]
        task_choices.sort(key=lambda t: (t["last_used"], t["id"]), reverse=True)
        dates, grouped = defaultdict(list), defaultdict(list)
        for row in selected:
            dates[row["date"]].append(row)
            grouped[row[group]].append(row)
        begin = datetime.fromisoformat(start or min(dates, default=today.isoformat())).date()
        timeline = []
        while begin <= today:
            day = begin.isoformat()
            timeline.append(dict(date=day, **totals(dates[day])))
            begin += timedelta(days=1)
        labels = {t["id"]: t["label"] for t in tasks}
        groups = [dict(id=key, label=labels.get(key, key) if group == "task" else key, **totals(items))
                  for key, items in grouped.items()]
        groups.sort(key=lambda r: r["total_tokens"], reverse=True)
        if self.excluded:
            meta["issues"].append(f"{self.excluded} 个旧格式任务尚无法安全纳入，具体原因见全局覆盖诊断")
        selected_task = next((t for t in tasks if t["id"] == task), None)
        summary = totals(selected)
        if not rows:
            summary = {key: None for key in summary}
            timeline = []
            meta["issues"].append("暂无可核实的用量记录；当前用量不可统计，不能解释为零消耗")
        return {"meta": meta, "filters": {"models": models, "tasks": task_choices, "selected_task": selected_task},
                "summary": summary, "timeline": timeline, "groups": groups}
