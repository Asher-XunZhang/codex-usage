---
name: codex-token-usage
description: Report recorded Codex input and output tokens for the current turn or task, including cache and subagent accounting. Use for token questions and the user's automatic end-of-reply usage footer; do not infer billing or subscription quota from tokens.
---

# Codex token usage

Read local usage records with [scripts/token_usage.py](scripts/token_usage.py). The script makes no model or network requests and prints counters and accounting metadata, not conversation bodies.

For the automatic footer, run `python3 -B <absolute-skill-directory>/scripts/token_usage.py --format table` after substantive work, then append its Markdown table. Keep all six columns: scope, input, cached input, noncached input, output (including reasoning), and total. Show the main task's current-turn increment, subagents with recorded current-turn use (or unresolved accounting), current-turn combined total, and a separately labeled task cumulative row. Do not replace this with input/output totals alone. Resolve the script relative to this skill and use an available Python 3.10+ interpreter. Reuse skill context already read. `CODEX_THREAD_ID` selects this task; do not guess another task when it is missing. Use `--thread-id` only for a task the user identified. Use `--format json` for structured accounting and `--turn-id` for a known specific turn.

The footer is a snapshot of usage already written to disk. It cannot include text or tool results generated after the snapshot, including the remainder of the final answer. Keep that qualification. Do not repeatedly call the script to try to count its own future cost. If a counter or turn boundary is unavailable, report it as unavailable or partial, not zero; complete the user's substantive task normally. Preserve a user-required output format: put statistics in a permitted JSON field when appropriate, or omit the footer when the required schema has no place for it. A request to disable reporting overrides the automatic preference.

Input includes cached input. Output includes reasoning output. Neither subset is added again. Combined figures include only attributable subagent usage; retain the script's coverage and quality notes. Token totals describe repeated model processing, not unique text, money, or the account's remaining allowance. For actual quota, use the account usage tool.

An optional Stop hook, [scripts/stop_hook.py](scripts/stop_hook.py), displays recorded usage after a reply in the Codex event UI. Hook trust is managed by Codex; the skill never creates trust records or bypasses review. The hook does not ask the model to continue or stop other hooks. Its event is separate from the assistant's final text and can still be partial if logging is delayed. An installed hook alone is not proof it ran. When hook operation has been verified in this task and the user prefers its post-reply display, omit the duplicate manual footer unless requested.
