"""Inspect 适配层的测试。真正的 Pigeon 与验证脚本由几行 Python 扮演，不需要 Unity，也不调用模型。

运行：tools/inspect-venv/Scripts/python.exe -m pytest eval/inspect
"""

import json
import sys
import textwrap
import time
from pathlib import Path

import anyio
import pytest
from inspect_ai import eval as inspect_eval
from inspect_ai.model import ChatMessageAssistant, ChatMessageTool, ChatMessageUser

sys.path.insert(0, str(Path(__file__).parent))
import patchparity_task as pt  # noqa: E402


def write_task(tasks: Path, task_id: str, verifier: list[str], max_turns: int = 80, wall_ms: int = 600_000) -> Path:
    d = tasks / task_id
    d.mkdir(parents=True)
    (d / "task.md").write_text(f"# {task_id} 的题面\n", encoding="utf-8")
    (d / "task.json").write_text(json.dumps({
        "version": 1, "id": task_id, "instructions": "task.md", "tags": ["patch-verify"],
        "budget": {"maxTurns": max_turns, "wallClockMs": wall_ms},
        "verifier": {"command": verifier, "timeoutMs": 60_000},
    }), encoding="utf-8")
    return d


def config(tmp: Path, pigeon: Path, thinking: str = "off") -> pt.Config:
    mcp = tmp / "mcp-launch.json"
    mcp.write_text('{"mcpServers":{"patchparity":{"command":"patchparity-mcp.exe","args":[]}}}', encoding="utf-8")
    return pt.Config(tmp / "out", pigeon, "stream-fn.mjs", mcp, "deepseek-v4-flash", thinking, sys.executable)


# ── 题目与配置 ─────────────────────────────────────────────


def test_load_samples_reads_budget_and_filters_by_prefix(tmp_path):
    write_task(tmp_path, "safe-a", ["node", "{TASK_DIR}/verify.mjs"], max_turns=80)
    write_task(tmp_path, "search-a", ["node", "{TASK_DIR}/verify.mjs"], max_turns=200, wall_ms=5_400_000)

    samples = pt.load_samples(tmp_path, only="search-")

    assert [s.id for s in samples] == ["search-a"]
    assert samples[0].input == "# search-a 的题面\n"
    assert samples[0].metadata["max_turns"] == 200
    assert samples[0].metadata["wall_clock_ms"] == 5_400_000
    assert samples[0].metadata["verifier"] == ["node", "{TASK_DIR}/verify.mjs"]


def test_tier_settings_mark_only_list_scenarios_read():
    tools = pt.tier_settings()["mcp"]["servers"]["patchparity"]["tools"]
    assert tools["list_scenarios"] == {"tier": "read"}
    assert all(tools[t] == {"tier": "write"} for t in pt.WRITE_TOOLS)


def test_agent_args_switch_off_session_search_and_pass_budget(tmp_path):
    cfg = config(tmp_path, Path("P"))
    args = pt.agent_args({"max_turns": 200, "wall_clock_ms": 5_400_000}, tmp_path / "ws", cfg)

    assert args[:3] == [sys.executable, str(Path("P") / "src" / "cli" / "index.ts"), "run"]
    assert args[args.index("--root") + 1] == str(tmp_path / "ws")
    assert args[args.index("--max-turns") + 1] == "200"
    assert args[args.index("--wall-clock") + 1] == "5400000"
    assert args[args.index("--thinking") + 1] == "off"
    # 跨题检索会话在 S6 实测中出现过（裁决 019）
    for flag in ("--no-session-search", "--trust-config", "--yolo", "--json"):
        assert flag in args


def test_agent_args_leave_thinking_to_pigeon_when_not_given(tmp_path):
    args = pt.agent_args({"max_turns": 1, "wall_clock_ms": 1}, tmp_path, config(tmp_path, Path("P"), thinking=""))
    assert "--thinking" not in args


def test_thinking_off_survives_yaml_parsing_of_task_args(tmp_path):
    # inspect eval -T thinking=off 把值按 YAML 解析成 False；不还原的话 Pigeon 会取缺省的 high
    assert pt.thinking_level(False) == "off"
    assert pt.thinking_level("high") == "high"
    assert pt.thinking_level(None) == ""
    with pytest.raises(ValueError):
        pt.thinking_level(True)
    write_task(tmp_path / "tasks", "safe-a", ["node", "{TASK_DIR}/verify.mjs"])
    mcp = tmp_path / "mcp.json"
    mcp.write_text("{}", encoding="utf-8")
    task = pt.patchparity(tasks=str(tmp_path / "tasks"), out=str(tmp_path / "o"), pigeon="P", stream_fn="s",
                          mcp_config=str(mcp), thinking=False)
    assert task.metadata["thinking"] == "off"


def test_prepare_workspace_starts_clean(tmp_path):
    cfg = config(tmp_path, Path("P"))
    ws = tmp_path / "ws"
    ws.mkdir()
    (ws / "stale.txt").write_text("上次中途退出留下的", encoding="utf-8")

    pt.prepare_workspace(ws, cfg.mcp_config)

    assert not (ws / "stale.txt").exists()
    assert (ws / ".mcp.json").read_text(encoding="utf-8") == cfg.mcp_config.read_text(encoding="utf-8")
    assert json.loads((ws / ".pigeon" / "settings.json").read_text(encoding="utf-8")) == pt.tier_settings()


# ── 会话转换与统计 ─────────────────────────────────────────


SESSION = [
    {"kind": "header", "version": 4, "id": "sess_9"},
    {"kind": "entry", "type": "custom", "customType": "pigeon.run-start", "data": {"model": {"thinkingLevel": "off"}}},
    {"kind": "entry", "lane": "main", "type": "message", "message": {"role": "user", "content": [{"type": "text", "text": "题面"}]}},
    {"kind": "entry", "lane": "main", "type": "message", "message": {"role": "assistant", "model": "deepseek-v4-flash", "content": [
        {"type": "thinking", "thinking": "要有地面才会着地"},
        {"type": "text", "text": "试第一个场景"},
        {"type": "toolCall", "id": "c1", "name": "mcp__patchparity__run_scenario", "arguments": {"options": "ground=1"}},
    ]}},
    {"kind": "entry", "lane": "main", "type": "message", "message": {
        "role": "toolResult", "toolCallId": "c1", "toolName": "mcp__patchparity__run_scenario",
        "content": [{"type": "text", "text": "job_id=1"}]}},
    {"kind": "entry", "lane": "main", "type": "message", "message": {"role": "assistant", "content": [
        {"type": "toolCall", "id": "c2", "name": "read_file", "arguments": {"path": "x"}}]}},
    {"kind": "entry", "lane": "main", "type": "message", "message": {
        "role": "toolResult", "toolCallId": "c2", "toolName": "read_file", "isError": True,
        "content": [{"type": "text", "text": "没有这个文件"}]}},
    # 派出的 worker 在别的 lane，不算主会话
    {"kind": "entry", "lane": "worker-1", "type": "message", "message": {"role": "assistant", "content": [
        {"type": "toolCall", "id": "w1", "name": "grep", "arguments": {}}]}},
]


def write_session(ws: Path, session_id: str = "sess_9") -> Path:
    d = ws / ".pigeon" / "state" / "sessions" / "--D--pp-e-x--"
    d.mkdir(parents=True)
    p = d / f"2026-10-14T00-00-00-000Z_{session_id}.jsonl"
    p.write_text("\n".join(json.dumps(e, ensure_ascii=False) for e in SESSION) + "\n", encoding="utf-8")
    return p


def test_session_messages_keep_reasoning_tool_calls_and_errors(tmp_path):
    messages = pt.session_messages(write_session(tmp_path))

    assert [type(m) for m in messages] == [
        ChatMessageUser, ChatMessageAssistant, ChatMessageTool, ChatMessageAssistant, ChatMessageTool]
    first = messages[1]
    assert first.text == "试第一个场景"
    assert any(getattr(c, "reasoning", None) == "要有地面才会着地" for c in first.content)
    assert first.tool_calls[0].function == "mcp__patchparity__run_scenario"
    assert first.tool_calls[0].arguments == {"options": "ground=1"}
    assert messages[2].tool_call_id == "c1" and messages[2].error is None
    assert messages[4].error is not None and "没有这个文件" in messages[4].error.message


def test_count_tools_counts_main_lane_calls_only(tmp_path):
    counts = pt.count_tools(pt.session_messages(write_session(tmp_path)))
    assert counts == {"mcp__patchparity__run_scenario": 1, "read_file": 1}


def test_session_file_picks_the_named_session(tmp_path):
    write_session(tmp_path, "sess_9")
    other = write_session(tmp_path / "other", "sess_other")
    assert pt.session_file(tmp_path, "sess_9").name.endswith("_sess_9.jsonl")
    assert pt.session_file(tmp_path / "other", "sess_other") == other
    assert pt.session_file(tmp_path, "sess_missing") is None


def test_scenario_attempts_count_distinct_baseline_scenarios(tmp_path):
    entries = [
        {"step": "baseline_build", "ok": True},
        {"step": "run", "run_kind": "baseline", "scenario": "default"},
        {"step": "run", "run_kind": "rebuild", "scenario": "default"},
        {"step": "run", "run_kind": "baseline", "scenario": "ground=1"},
        {"step": "run", "run_kind": "rebuild", "scenario": "ground=1"},
        {"step": "run", "run_kind": "patched", "scenario": "ground=1"},
        {"step": "run", "run_kind": "baseline", "scenario": "ground=1"},
        # 验证完后在别的场景补跑一次补丁（S6 射线一题）：不算一次场景尝试
        {"step": "run", "run_kind": "patched", "scenario": "ticks=300;jump=200"},
    ]
    (tmp_path / "patchparity-ledger.json").write_text(json.dumps({"version": 1, "entries": entries}), encoding="utf-8")

    assert pt.scenario_attempts(tmp_path) == 2
    assert pt.scenario_attempts(tmp_path / "missing") is None


def test_last_json_line():
    assert pt.last_json_line("日志\n{\"a\": 1}\n\n") == {"a": 1}
    assert pt.last_json_line("没有 JSON") is None
    assert pt.last_json_line("{坏的") is None


# ── 子进程 ─────────────────────────────────────────────────


def test_run_process_feeds_stdin_and_decodes_utf8(tmp_path):
    code = "import sys; data = sys.stdin.buffer.read().decode('utf-8'); sys.stdout.buffer.write(('收到:' + data).encode('utf-8'))"
    outcome = anyio.run(pt.run_process, [sys.executable, "-c", code], tmp_path, "题面第一行", 60)

    assert outcome.returncode == 0 and not outcome.timed_out
    assert outcome.stdout == "收到:题面第一行"


def test_run_process_kills_the_whole_tree_on_timeout(tmp_path):
    # 子进程再拉起一个孙进程，孙进程写下自己的 pid：超时后两者都不能留下
    marker = tmp_path / "grandchild.pid"
    grandchild = f"import os, time; open(r'{marker}', 'w').write(str(os.getpid())); time.sleep(60)"
    child = f"import subprocess, sys, time; subprocess.Popen([sys.executable, '-c', {grandchild!r}]); time.sleep(60)"
    started = time.monotonic()

    outcome = anyio.run(pt.run_process, [sys.executable, "-c", child], tmp_path, None, 3)

    assert outcome.timed_out and outcome.returncode is None
    assert time.monotonic() - started < 30
    pid = int(marker.read_text())
    time.sleep(1)
    assert not _alive(pid)


def _alive(pid: int) -> bool:
    import subprocess as sp
    if sys.platform == "win32":
        out = sp.run(["tasklist", "/FI", f"PID eq {pid}", "/NH"], capture_output=True, text=True).stdout
        return str(pid) in out
    import os
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


# ── 端到端：Inspect 起"Agent"、转换消息、判分、跨 epoch 合并、统计 ──


FAKE_PIGEON = textwrap.dedent('''
    # 扮演 pigeon run：读题面，写一份会话存档与台账，最后一行输出 --json 的结果
    import json, sys
    from pathlib import Path
    args = sys.argv[1:]
    root = Path(args[args.index("--root") + 1])
    prompt = sys.stdin.read()
    sessions = root / ".pigeon" / "state" / "sessions" / "--enc--"
    sessions.mkdir(parents=True)
    entries = [
        {"kind": "entry", "type": "message", "message": {"role": "user", "content": [{"type": "text", "text": prompt}]}},
        {"kind": "entry", "type": "message", "message": {"role": "assistant", "content": [
            {"type": "text", "text": "先看参数"},
            {"type": "toolCall", "id": "1", "name": "mcp__patchparity__list_scenarios", "arguments": {}}]}},
        {"kind": "entry", "type": "message", "message": {"role": "toolResult", "toolCallId": "1",
            "toolName": "mcp__patchparity__list_scenarios", "content": [{"type": "text", "text": "参数说明"}]}},
        {"kind": "entry", "type": "message", "message": {"role": "assistant", "content": [{"type": "text", "text": "完成"}]}},
    ]
    (sessions / "t_sess_fake.jsonl").write_text("\\n".join(json.dumps(e, ensure_ascii=False) for e in entries), encoding="utf-8")
    ledger = {"version": 1, "entries": [
        {"step": "run", "run_kind": "baseline", "scenario": "a"},
        {"step": "run", "run_kind": "baseline", "scenario": "b"}]}
    (root / "patchparity-ledger.json").write_text(json.dumps(ledger), encoding="utf-8")
    print("pigeon 的日志一行")
    print(json.dumps({"sessionId": "sess_fake", "status": "completed", "failure": None, "turns": 4, "toolCalls": 1,
                      "usage": {"totalTokens": 1000, "cost": {"total": 0.002}}}))
''')

# 扮演 verify.mjs：工作目录里有标记文件即判"不符合"，否则"符合"
FAKE_VERIFY = textwrap.dedent('''
    import json, sys
    from pathlib import Path
    fail = Path("fail.flag").exists()
    print("判定: divergent")
    print(json.dumps({"verdict": "divergent", "matches": not fail, "observable": True, "scenario": "ground=1",
                      "first_divergent_tick": 2}))
    sys.exit(1 if fail else 0)
''')


def test_end_to_end_with_epochs(tmp_path):
    pigeon = tmp_path / "pigeon"
    (pigeon / "src" / "cli").mkdir(parents=True)
    (pigeon / "src" / "cli" / "index.ts").write_text(FAKE_PIGEON, encoding="utf-8")
    tasks = tmp_path / "tasks"
    task_dir = write_task(tasks, "search-a", [sys.executable, "{TASK_DIR}/verify.py"])
    (task_dir / "verify.py").write_text(FAKE_VERIFY, encoding="utf-8")
    cfg = config(tmp_path, pigeon)

    task = pt.patchparity(
        tasks=str(tasks), out=str(cfg.out), pigeon=str(pigeon), stream_fn="x", mcp_config=str(cfg.mcp_config),
        node=sys.executable, epochs=2)
    [log] = inspect_eval(task, model="none", max_samples=1, log_dir=str(tmp_path / "logs"), display="none")

    assert log.status == "success"
    assert len(log.samples) == 2
    sample = log.samples[0]
    score = sample.scores["patchparity_judge"]
    assert score.value == "C"
    assert score.answer == "divergent"
    assert score.metadata["attempts"] == 2
    assert score.metadata["turns"] == 4
    assert score.metadata["foreign_tools"] == []
    # 查看器里能看到 Agent 的过程：题面、工具调用及入参、工具返回
    assert [m.role for m in sample.messages] == ["user", "assistant", "tool", "assistant"]
    assert sample.messages[1].tool_calls[0].function == "mcp__patchparity__list_scenarios"
    assert (cfg.out / "search-a" / "epoch-1" / "verify.out.txt").exists()
    assert (cfg.out / "search-a" / "epoch-2" / "agent.out.txt").exists()
    # 跨 epoch 合并：均值、pass@2、pass^2 各一组，另有按每次运行平均的轮次与花费
    metrics = {}
    for group in log.results.scores:
        for name, m in group.metrics.items():
            metrics[(group.reducer, name)] = m.value
    assert metrics[("mean", "accuracy")] == 1.0
    assert metrics[("pass_at_2", "accuracy")] == 1.0
    assert metrics[("pass_k_2", "accuracy")] == 1.0
    assert any(name == "mean_turns" and value == 4.0 for (_, name), value in metrics.items())
    assert any(name == "mean_cost" and abs(value - 0.002) < 1e-9 for (_, name), value in metrics.items())


def test_failed_verifier_scores_incorrect(tmp_path):
    pigeon = tmp_path / "pigeon"
    (pigeon / "src" / "cli").mkdir(parents=True)
    # 这个"Agent"什么都不做，还留下一个让验证脚本判不符合的标记
    (pigeon / "src" / "cli" / "index.ts").write_text(
        "import sys; from pathlib import Path; a = sys.argv[1:]; r = Path(a[a.index('--root') + 1]);"
        " (r / 'fail.flag').write_text('x'); sys.stdin.read()", encoding="utf-8")
    tasks = tmp_path / "tasks"
    task_dir = write_task(tasks, "safe-a", [sys.executable, "{TASK_DIR}/verify.py"])
    (task_dir / "verify.py").write_text(FAKE_VERIFY, encoding="utf-8")
    cfg = config(tmp_path, pigeon)

    task = pt.patchparity(
        tasks=str(tasks), out=str(cfg.out), pigeon=str(pigeon), stream_fn="x", mcp_config=str(cfg.mcp_config),
        node=sys.executable)
    [log] = inspect_eval(task, model="none", max_samples=1, log_dir=str(tmp_path / "logs"), display="none")

    score = log.samples[0].scores["patchparity_judge"]
    assert score.value == "I"
    # Agent 没给出 --json 结果：记作 no_result，照样判分
    assert score.metadata["agent_status"] == "no_result"
