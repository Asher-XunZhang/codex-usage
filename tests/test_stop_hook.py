import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "backend"))
spec = importlib.util.spec_from_file_location("stop_hook", ROOT / "skill/scripts/stop_hook.py")
hook = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hook)


class StopHookTests(unittest.TestCase):
    def test_optional_skill_uses_the_same_accounting_parser(self):
        self.assertEqual((ROOT / "skill/scripts/token_usage.py").read_bytes(),
                         (ROOT / "backend/token_usage.py").read_bytes())

    def test_no_control_flow_or_transcript_disclosure(self):
        event = {"hook_event_name": "Stop", "session_id": "11111111-1111-1111-1111-111111111111",
                 "turn_id": "22222222-2222-2222-2222-222222222222",
                 "last_assistant_message": "SECRET RESPONSE BODY"}
        with patch.object(hook.token_usage, "collect", return_value={"current_turn": {"completed": False}}), \
             patch.object(hook.token_usage, "table", return_value="输入 10 / 输出 4。" + hook.token_usage.BOUNDARY_NOTE):
            result = hook.handle(event)
        self.assertEqual(set(result), {"systemMessage"})
        self.assertIn("输入 10 / 输出 4", result["systemMessage"])
        self.assertIn("尚未标记结束", result["systemMessage"])
        self.assertNotIn("SECRET", json.dumps(result))

    def test_other_events_do_not_read_logs(self):
        with patch.object(hook.token_usage, "collect") as collect:
            self.assertEqual(hook.handle({"hook_event_name": "PreToolUse"}), {})
            collect.assert_not_called()

    def test_missing_identity_never_selects_another_thread(self):
        with patch.object(hook.token_usage, "collect") as collect:
            result = hook.handle({"hook_event_name": "Stop"})
            self.assertIn("不可用", result["systemMessage"])
            collect.assert_not_called()

    def test_errors_are_nonblocking_and_do_not_echo_payload(self):
        output = io.StringIO()
        with patch.object(sys, "stdin", io.StringIO("SECRET invalid json")), contextlib.redirect_stdout(output):
            self.assertEqual(hook.main(), 0)
        result = json.loads(output.getvalue())
        self.assertEqual(set(result), {"systemMessage"})
        self.assertNotIn("SECRET", output.getvalue())


if __name__ == "__main__":
    unittest.main()
