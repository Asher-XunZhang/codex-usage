"""Synthetic accounting edge cases; no real transcript bodies are copied here."""
import contextlib
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import token_usage as app


def u(inp=100, out=20, cached=40, reasoning=5):
    return dict(input_tokens=inp, output_tokens=out, total_tokens=inp+out,
                cached_input_tokens=cached, reasoning_output_tokens=reasoning,
                cache_write_input_tokens=0)


def event(kind, payload):
    return {"timestamp": "2026-09-10T01:00:00Z", "type": kind, "payload": payload}


def meta(tid, parent=None, fork=None):
    return event("session_meta", {"id": tid, "forked_from_id": fork,
                  "source": {"subagent": {"thread_spawn": {"parent_thread_id": parent}}} if parent else "vscode"})


def start(turn, root=None):
    return [event("event_msg", {"type": "task_started", "turn_id": turn}),
            event("turn_context", {"turn_id": turn, "root_turn_id": root or turn})]


def record(tid, turn, rid, value=None, root=None, turn_total=None, thread_total=None):
    value = value or u()
    return event("token_usage_record", {"thread_id": tid, "turn_id": turn,
                 "root_turn_id": root or turn, "response_id": rid, "usage": value,
                 "turn_token_usage": turn_total or value, "thread_token_usage": thread_total or value})


def legacy(value):
    return event("event_msg", {"type": "token_count", "info": {"total_token_usage": value}})


class AccountingTests(unittest.TestCase):
    def test_display_splits_cached_and_noncached_without_double_counting(self):
        value = app.metric(u(100, 20, 80, 15), "available", "token_usage_record")
        self.assertEqual(app.breakdown(value), ["100", "80", "20", "20", "120"])
        missing_cache = u(100, 20)
        missing_cache["cached_input_tokens"] = None
        self.assertEqual(app.breakdown(app.metric(missing_cache, "partial", "test")),
                         ["100", "不可统计", "不可统计", "20", "120"])
        self.assertEqual(app.breakdown(app.metric(None, "unavailable", "test")), ["不可统计"] * 5)

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.home = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def write(self, tid, events, tail="", archive=False, name=None):
        directory = self.home / ("archived_sessions" if archive else "sessions")
        directory.mkdir(exist_ok=True)
        path = directory / (name or f"rollout-{tid}.jsonl")
        path.write_text("".join(json.dumps(e) + "\n" for e in events) + tail, encoding="utf-8")
        return path

    def get(self, tid="root", **kwargs):
        return app.collect(self.home, tid, **kwargs)

    def test_modern_dedupe_steering_and_compaction(self):
        first, second = u(), u(200, 30, 90, 9)
        total = app.add([first, second])
        r1 = record("root", "t1", "r1", first)
        r2 = record("root", "t1", "r2", second, turn_total=total, thread_total=total)
        self.write("root", [meta("root"), *start("t1"), r1, legacy(first), r1,
                            event("response_item", {"role": "user", "content": "STEERING_SECRET"}),
                            event("compacted", {}), event("turn_context", {"turn_id": "t1"}),
                            r2, legacy(total), legacy(total)])
        result = self.get()
        self.assertEqual(result["current_turn"]["turn_id"], "t1")
        self.assertEqual(result["current_turn"]["combined"]["tokens"], total)
        self.assertEqual(result["thread_total"]["combined"]["status"], "available")
        self.assertNotIn("STEERING_SECRET", json.dumps(result))

    def test_current_turn_delta_not_cumulative(self):
        one, two = u(), u(30, 8, 10, 2)
        self.write("root", [meta("root"), *start("t1"), record("root", "t1", "r1", one),
                            *start("t2"), record("root", "t2", "r2", two, thread_total=app.add([one, two]))])
        result = self.get()
        self.assertEqual(result["current_turn"]["self"]["tokens"], two)
        self.assertEqual(result["thread_total"]["self"]["tokens"], app.add([one, two]))
        old = self.get(turn_id="t1")
        self.assertEqual(old["current_turn"]["self"]["tokens"], one)

    def test_fork_history_filtered_and_children_attributed_by_root_turn(self):
        root_old, root_now, child_old, child_now = u(), u(300), u(500), u(700)
        copied = [meta("root"), *start("old"), record("root", "old", "r-old", root_old)]
        self.write("root", [*copied, *start("new"), record("root", "new", "r-new", root_now,
                   thread_total=app.add([root_old, root_now]))])
        self.write("child", [meta("child", parent="root", fork="root"), *copied,
                   *start("c-old", "old"), record("child", "c-old", "c1", child_old, root="old"),
                   *start("c-new", "new"), record("child", "c-new", "c2", child_now, root="new",
                   thread_total=app.add([child_old, child_now]))])
        # A manual fork isn't a subagent and must not contribute.
        self.write("manual", [meta("manual", fork="root"), *start("m"), record("manual", "m", "m1", u(900))])
        result = self.get()
        self.assertEqual(result["discovery"]["associated_threads"], 1)
        self.assertEqual(result["current_turn"]["combined"]["tokens"], app.add([root_now, child_now]))
        self.assertEqual(result["thread_total"]["combined"]["tokens"], app.add([root_old, root_now, child_old, child_now]))

    def test_nested_subagents_and_archive_duplicate(self):
        for tid, parent, turn in [("root", None, "t"), ("child", "root", "c"), ("grand", "child", "g")]:
            events = [meta(tid, parent=parent), *start(turn, "t"), record(tid, turn, tid+"-r", root="t")]
            self.write(tid, events)
            if tid == "root":
                self.write(tid, events, archive=True)
        result = self.get()
        self.assertEqual(result["thread_total"]["combined"]["tokens"], app.add([u(), u(), u()]))
        self.assertEqual(result["discovery"]["associated_threads"], 2)

    def test_old_archive_cannot_override_new_live_turn(self):
        old = [meta("root"), *start("old"), record("root", "old", "r1")]
        fresh = [*start("new"), record("root", "new", "r2", thread_total=app.add([u(), u()]))]
        for e in fresh:
            e["timestamp"] = "2026-09-10T02:00:00Z"
        self.write("root", old, archive=True)
        self.write("root", old + fresh)
        result = self.get()
        self.assertEqual(result["current_turn"]["turn_id"], "new")
        self.assertEqual(result["thread_total"]["self"]["tokens"], app.add([u(), u()]))
        # Also check adverse file ordering, independent of discovery's current order.
        registry, _ = app.discover(self.home)
        registry["root"]["paths"].reverse()
        with patch.object(app, "discover", return_value=(registry, 0)):
            self.assertEqual(self.get()["current_turn"]["turn_id"], "new")

    def test_old_child_with_no_current_root_usage_contributes_zero(self):
        self.write("root", [meta("root"), *start("new"), record("root", "new", "r1")])
        self.write("child", [meta("child", parent="root"), *start("c", "old"), record("child", "c", "c1", root="old")])
        result = self.get()
        self.assertEqual(result["current_turn"]["subagents"]["tokens"], app.add([]))
        self.assertEqual(result["current_turn"]["combined"]["status"], "available")

    def test_child_missing_record_cannot_claim_zero_current_usage(self):
        self.write("root", [meta("root"), *start("new"), record("root", "new", "r1")])
        self.write("child", [meta("child", parent="root"), *start("c", "old"),
                             record("child", "c", "c1", root="old", thread_total=u(999))])
        self.assertEqual(self.get()["current_turn"]["subagents"]["status"], "unavailable")

    def test_missing_child_root_id_is_not_silently_excluded(self):
        self.write("root", [meta("root"), *start("new"), record("root", "new", "r1")])
        r = record("child", "c", "c1")
        del r["payload"]["root_turn_id"]
        self.write("child", [meta("child", parent="root"), *start("c", "new"), r])
        self.assertEqual(self.get()["current_turn"]["combined"]["status"], "partial")

    def test_tail_and_malformed_record_are_partial_not_silent(self):
        self.write("root", [meta("root"), *start("t"), record("root", "t", "r1")], tail='garbage\n{"type":')
        result = self.get()["thread_total"]["self"]
        self.assertEqual(result["status"], "partial")
        self.assertEqual(result["tokens"], u())
        self.assertIn("incomplete_tail_ignored", result["issues"])
        self.assertIn("malformed_record_ignored", result["issues"])

    def test_no_usage_is_unavailable_not_zero(self):
        self.write("root", [meta("root"), *start("t"), legacy(None)])
        result = self.get()
        self.assertIsNone(result["current_turn"]["self"]["tokens"])
        self.assertEqual(result["thread_total"]["combined"]["status"], "unavailable")
        # The zero-child subtotal cannot turn missing main usage into an exact zero.
        self.assertIn("无法可靠统计", app.short(result))
        self.assertNotIn("输入 0", app.short(result))

    def test_unknown_turn_boundary_not_inferred_from_user_message(self):
        self.write("root", [meta("root"), event("response_item", {"role": "user"}), record("root", "t", "r1")])
        result = self.get()
        self.assertEqual(result["current_turn"]["self"]["status"], "unavailable")
        self.assertEqual(result["thread_total"]["self"]["status"], "available")

    def test_new_turn_before_first_usage_does_not_reuse_prior_turn(self):
        self.write("root", [meta("root"), *start("old"), record("root", "old", "r1"), *start("new")])
        self.assertIsNone(self.get()["current_turn"]["self"]["tokens"])

    def test_cumulative_mismatch_is_partial(self):
        self.write("root", [meta("root"), *start("t"), record("root", "t", "r1", thread_total=u(999))])
        result = self.get()["thread_total"]["self"]
        self.assertEqual(result["status"], "partial")
        self.assertIn("thread_cumulative_mismatch", result["issues"])

    def test_conflicting_duplicate_not_summed(self):
        self.write("root", [meta("root"), *start("t"), record("root", "t", "r1"), record("root", "t", "r1", u(900))])
        result = self.get()["thread_total"]["self"]
        self.assertEqual(result["tokens"], u())
        self.assertEqual(result["status"], "partial")

    def test_missing_optional_subsets_are_unknown_not_zero(self):
        value = {k: v for k, v in u().items() if k in app.CORE}
        self.write("root", [meta("root"), *start("t"), record("root", "t", "r1", value)])
        result = self.get()["thread_total"]["self"]
        self.assertIsNone(result["tokens"]["cached_input_tokens"])
        self.assertEqual(result["status"], "available")

    def test_legacy_dedup_and_turn_deltas(self):
        first, second = u(), u(200, 30, 90, 9)
        summed = app.add([first, second])
        self.write("root", [meta("root"), *start("t1"), legacy(first), legacy(first),
                            *start("t2"), legacy(first), legacy(summed), legacy(summed)])
        result = self.get()
        self.assertEqual(result["current_turn"]["self"]["tokens"], second)
        self.assertEqual(result["thread_total"]["self"]["tokens"], summed)
        self.assertEqual(result["current_turn"]["self"]["status"], "available")

    def test_legacy_reset_unavailable_and_compaction_flagged(self):
        self.write("root", [meta("root"), *start("t"), legacy(u(500)), event("compacted", {}), legacy(u())])
        self.assertIsNone(self.get()["thread_total"]["self"]["tokens"])
        self.write("root", [meta("root"), *start("t"), legacy(u()), event("compacted", {})])
        self.assertEqual(self.get()["current_turn"]["self"]["status"], "partial")

    def test_legacy_fork_cannot_claim_exact_ownership(self):
        self.write("child", [meta("child", parent="root", fork="root"), *start("t"), legacy(u())])
        result = self.get("child")
        self.assertIsNone(result["thread_total"]["self"]["tokens"])

    def test_legacy_second_turn_missing_baseline_is_unavailable(self):
        self.write("root", [meta("root"), *start("old"), *start("new"), legacy(u(500))])
        result = self.get()
        self.assertIsNone(result["current_turn"]["self"]["tokens"])
        self.assertEqual(result["thread_total"]["self"]["tokens"], u(500))

    def test_missing_main_usage_with_real_child_subtotal_stays_partial(self):
        self.write("root", [meta("root"), *start("t")])
        self.write("child", [meta("child", parent="root"), *start("c", "t"), record("child", "c", "c1", root="t")])
        result = self.get()["current_turn"]["combined"]
        self.assertEqual(result["status"], "partial")
        self.assertEqual(result["tokens"], u())

    def test_explicit_transcript_and_metadata_mismatch(self):
        p = self.write("root", [meta("root"), *start("t"), record("root", "t", "r1")])
        with patch.object(app, "discover", side_effect=AssertionError("must not scan")):
            result = self.get(include_subagents=False, transcript_path=p)
        self.assertEqual(result["thread_total"]["self"]["tokens"], u())
        mismatch = app.collect(self.home, "other", include_subagents=False, transcript_path=p)
        self.assertEqual(mismatch["error"], "transcript_metadata_mismatch_or_unreadable")

    def test_env_defaults_and_no_secret_output(self):
        self.write("root", [meta("root"), *start("t"), record("root", "t", "r1"),
                            event("response_item", {"content": "sk-DO-NOT-EXPOSE"})])
        stream = io.StringIO()
        with patch.dict(os.environ, {"CODEX_HOME": str(self.home), "CODEX_THREAD_ID": "root"}), contextlib.redirect_stdout(stream):
            code = app.main([])
        self.assertEqual(code, 0)
        self.assertNotIn("sk-DO-NOT-EXPOSE", stream.getvalue())
        self.assertEqual(json.loads(stream.getvalue())["thread_id"], "root")

    def test_default_home_and_missing_thread_id(self):
        fake_user = self.home / "user"
        home = fake_user / ".codex"
        home.mkdir(parents=True)
        with patch.dict(os.environ, {"CODEX_THREAD_ID": "root"}, clear=True), patch.object(Path, "home", return_value=fake_user), patch.object(app, "collect", return_value={"current_turn": {}}) as collect:
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(app.main([]), 0)
            self.assertEqual(collect.call_args.args[0], home)
        with patch.dict(os.environ, {}, clear=True), contextlib.redirect_stdout(io.StringIO()) as stream:
            self.assertEqual(app.main([]), 2)
        self.assertEqual(json.loads(stream.getvalue())["error"], "missing_or_invalid_thread_or_turn_id")

    def test_completed_only_from_task_complete(self):
        events = [meta("root"), *start("t"), record("root", "t", "r1")]
        self.write("root", events)
        self.assertFalse(self.get(turn_id="t")["current_turn"]["completed"])
        self.write("root", events + [event("event_msg", {"type": "task_complete", "turn_id": "t"})])
        self.assertTrue(self.get()["current_turn"]["completed"])

    def test_cache_reasoning_not_added_twice(self):
        value = u(100, 20, 80, 15)
        self.write("root", [meta("root"), *start("t"), record("root", "t", "r1", value)])
        result = self.get()["thread_total"]["combined"]["tokens"]
        self.assertEqual(result["total_tokens"], 120)
        self.assertIsNone(app.usage(u(10, 5, 20, 1)))


if __name__ == "__main__":
    unittest.main()
