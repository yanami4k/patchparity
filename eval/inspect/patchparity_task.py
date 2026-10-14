"""patchparity 的 Inspect AI 任务：用 Inspect 组织题目、重复跑、统计与展示，Agent 仍是 Pigeon（裁决 021）。

题目来自 eval/generate-tasks.mjs 生成的题目目录（task.json、task.md、verify.mjs），与 `patchparity eval` 同一份。
每个样本的每个 epoch：

1. 建一个干净的工作目录 <out>/<题号>/epoch-<n>，放进 MCP 启动定义 .mcp.json 与风险档 .pigeon/settings.json；
2. 用 `pigeon run --root <工作目录> --json` 起 Agent，题面经标准输入送入；会话检索、派 worker、联网、推送记忆、钩子一律关掉；
3. 把 Pigeon 的会话存档转成 Inspect 的消息，查看器里能逐条看到 Agent 说了什么、调了哪个工具、传了什么、拿回什么；
4. 判分器在同一目录跑题目的 verify.mjs（调 patchparity judge，从服务端台账判定），退出码即判分。

判分不采信 Agent 的说法（裁决 009）：消息只用于展示，分数只来自台账。

运行（并发须为 1：被测工程只有一份，裁决 014）：

    inspect eval eval/inspect/patchparity_task.py --model none --max-samples 1 \
        -T tasks=<题目目录> -T out=<输出目录> -T pigeon=<Pigeon 目录> -T stream_fn=<模块> -T mcp_config=<mcp-launch.json> \
        -T epochs=3 [-T only=search-] [-T thinking=off]
"""

import json
import os
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import anyio
from inspect_ai import Epochs, Task, task
from inspect_ai.dataset import Sample
from inspect_ai.log import transcript
from inspect_ai.model import (
    ChatMessage,
    ChatMessageAssistant,
    ChatMessageTool,
    ChatMessageUser,
    ContentReasoning,
    ContentText,
    ModelOutput,
)
from inspect_ai.scorer import (
    CORRECT,
    INCORRECT,
    Metric,
    SampleScore,
    Score,
    Target,
    accuracy,
    metric,
    scorer,
    stderr,
)
from inspect_ai.solver import Generate, TaskState, solver
from inspect_ai.tool import ToolCall, ToolCallError

TOOL_PREFIX = "mcp__patchparity__"

# 风险档 A（裁决 015）：list_scenarios 为 read，其余为 write，配合 --yolo
WRITE_TOOLS = ("build_package", "build_patch", "revert_patch", "run_scenario", "wait_job", "compare_runs")

# Agent 超出墙钟预算后再宽限多久才强行结束：Pigeon 自己按预算收尾，这里只兜底
AGENT_GRACE_SECONDS = 600


@dataclass(frozen=True)
class Config:
    out: Path
    pigeon: Path
    stream_fn: str
    mcp_config: Path
    model_id: str
    thinking: str
    node: str


# ── 题目 ──────────────────────────────────────────────────


def load_samples(tasks_dir: str | Path, only: str = "") -> list[Sample]:
    """每个题目目录一个样本；预算与验证命令放进样本的元数据。"""
    samples = []
    for d in sorted(Path(tasks_dir).iterdir()):
        spec_path = d / "task.json"
        if not spec_path.is_file():
            continue
        spec = json.loads(spec_path.read_text(encoding="utf-8"))
        if not spec["id"].startswith(only):
            continue
        prompt = (d / spec.get("instructions", "task.md")).read_text(encoding="utf-8")
        samples.append(
            Sample(
                id=spec["id"],
                input=prompt,
                metadata={
                    "task_dir": str(d.resolve()),
                    "tags": spec.get("tags", []),
                    "max_turns": spec["budget"]["maxTurns"],
                    "wall_clock_ms": spec["budget"]["wallClockMs"],
                    "verifier": spec["verifier"]["command"],
                    "verifier_timeout_ms": spec["verifier"].get("timeoutMs", 120_000),
                },
            )
        )
    return samples


def tier_settings() -> dict[str, Any]:
    """Pigeon 5e70ca2 设置里 mcp 一节（旧布局的 .pigeon/mcp.json 新版启动即报错）。"""
    tools: dict[str, Any] = {"list_scenarios": {"tier": "read"}}
    tools.update({t: {"tier": "write"} for t in WRITE_TOOLS})
    return {"mcp": {"servers": {"patchparity": {"defaultTier": "write", "tools": tools}}}}


def agent_args(metadata: dict[str, Any], workspace: Path, cfg: Config) -> list[str]:
    """起 Agent 的命令行，与 `patchparity eval` 一致。--trust-config 只对本次放行 .mcp.json 的启动定义。"""
    args = [
        cfg.node, str(cfg.pigeon / "src" / "cli" / "index.ts"), "run",
        "--root", str(workspace),
        "--stream-fn", cfg.stream_fn,
        "--provider", "deepseek",
        "--model", cfg.model_id,
        "--yolo", "--trust-config",
        "--no-session-search", "--no-spawn-workers", "--no-web", "--no-pushed-memory", "--no-hooks",
        "--max-turns", str(metadata["max_turns"]),
        "--wall-clock", str(metadata["wall_clock_ms"]),
        "--json",
    ]
    # 推理档位：不给时 5e70ca2 对 deepseek-v4-flash 取 high，S5、S6 是关着跑的（裁决 020）
    if cfg.thinking:
        args += ["--thinking", cfg.thinking]
    return args


def prepare_workspace(workspace: Path, mcp_config: Path) -> None:
    """干净的工作目录：上次中途退出留下的残局整个删掉重来。"""
    if workspace.exists():
        shutil.rmtree(workspace)
    (workspace / ".pigeon").mkdir(parents=True)
    shutil.copyfile(mcp_config, workspace / ".mcp.json")
    (workspace / ".pigeon" / "settings.json").write_text(json.dumps(tier_settings(), indent=2), encoding="utf-8")


# ── 子进程 ────────────────────────────────────────────────


@dataclass(frozen=True)
class ProcessOutcome:
    returncode: int | None
    timed_out: bool
    stdout: str
    stderr: str


def _kill_tree(pid: int) -> None:
    # 连同子进程一起结束：Agent 拉起的 MCP 服务端、Unity 不能留下来占着工程。Inspect 自带的 subprocess 超时只结束直接子进程
    if sys.platform == "win32":
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(pid)], capture_output=True, check=False)
    else:
        os.killpg(pid, 9)


async def run_process(
    args: list[str], cwd: Path, stdin: str | None, timeout_seconds: float, env: dict[str, str] | None = None
) -> ProcessOutcome:
    """跑一个子进程，标准输入整段送入；超时连同进程树结束。输出一律按 UTF-8 解码。"""
    process = await anyio.open_process(
        args, cwd=cwd, env=env, start_new_session=sys.platform != "win32",
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    out = bytearray()
    err = bytearray()

    async def drain(stream: Any, sink: bytearray) -> None:
        async for chunk in stream:
            sink.extend(chunk)

    timed_out = False
    async with process:
        async with anyio.create_task_group() as tg:
            tg.start_soon(drain, process.stdout, out)
            tg.start_soon(drain, process.stderr, err)
            if stdin is not None:
                await process.stdin.send(stdin.encode("utf-8"))
            await process.stdin.aclose()
            with anyio.move_on_after(timeout_seconds) as scope:
                await process.wait()
            if scope.cancelled_caught:
                timed_out = True
                _kill_tree(process.pid)
                await process.wait()
    return ProcessOutcome(
        None if timed_out else process.returncode, timed_out,
        out.decode("utf-8", errors="replace"), err.decode("utf-8", errors="replace"),
    )


def last_json_line(text: str) -> dict[str, Any] | None:
    """Pigeon 的 --json 与 judge 都把结构化结果写在标准输出的最后一行。"""
    for line in reversed(text.splitlines()):
        line = line.strip()
        if line.startswith("{"):
            try:
                value = json.loads(line)
            except json.JSONDecodeError:
                return None
            return value if isinstance(value, dict) else None
    return None


# ── Pigeon 会话 → Inspect 消息 ────────────────────────────


def session_file(workspace: Path, session_id: str | None) -> Path | None:
    """5e70ca2 的会话存档：.pigeon/state/sessions/<按工作目录编码的子目录>/<时间>_<会话号>.jsonl。"""
    sessions = workspace / ".pigeon" / "state" / "sessions"
    if not sessions.is_dir():
        return None
    files = sorted(sessions.rglob(f"*_{session_id}.jsonl" if session_id else "*.jsonl"))
    return files[-1] if files else None


def _text(blocks: Any) -> str:
    if isinstance(blocks, str):
        return blocks
    return "\n".join(b.get("text", "") for b in blocks or [] if isinstance(b, dict) and b.get("type") == "text")


def session_messages(path: Path) -> list[ChatMessage]:
    """把主会话的消息条目转成 Inspect 的消息。思考块转成推理内容，工具调用带上入参，工具出错的返回标成错误。"""
    messages: list[ChatMessage] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        entry = json.loads(line)
        if entry.get("type") != "message" or entry.get("lane", "main") != "main":
            continue
        m = entry.get("message") or {}
        role = m.get("role")
        content = m.get("content")
        if role == "user":
            messages.append(ChatMessageUser(content=_text(content)))
        elif role == "assistant":
            parts: list[Any] = []
            calls: list[ToolCall] = []
            for b in content or []:
                kind = b.get("type")
                if kind == "thinking" and (b.get("thinking") or b.get("text")):
                    parts.append(ContentReasoning(reasoning=b.get("thinking") or b.get("text")))
                elif kind == "text" and b.get("text"):
                    parts.append(ContentText(text=b["text"]))
                elif kind == "toolCall":
                    calls.append(ToolCall(id=b.get("id", ""), function=b.get("name", ""), arguments=b.get("arguments") or {}))
            messages.append(ChatMessageAssistant(content=parts or "", tool_calls=calls or None, model=m.get("model")))
        elif role == "toolResult":
            text = _text(content)
            messages.append(
                ChatMessageTool(
                    content=text,
                    tool_call_id=m.get("toolCallId"),
                    function=m.get("toolName"),
                    error=ToolCallError("unknown", text[:500]) if m.get("isError") else None,
                )
            )
    return messages


def count_tools(messages: list[ChatMessage]) -> dict[str, int]:
    counts: dict[str, int] = {}
    for m in messages:
        if isinstance(m, ChatMessageAssistant):
            for call in m.tool_calls or []:
                counts[call.function] = counts.get(call.function, 0) + 1
    return dict(sorted(counts.items()))


def scenario_attempts(workspace: Path) -> int | None:
    """试了几个场景：台账里未打补丁的母包运行用过多少种场景。场景搜索类每试一次就是一对母包与整包重编的运行。"""
    ledger = workspace / "patchparity-ledger.json"
    if not ledger.is_file():
        return None
    entries = json.loads(ledger.read_text(encoding="utf-8")).get("entries", [])
    return len({e.get("scenario") for e in entries if e.get("step") == "run" and e.get("run_kind") == "baseline"})


# ── 求解器与判分器 ────────────────────────────────────────


@solver
def pigeon_agent(cfg: Config):
    async def solve(state: TaskState, generate: Generate) -> TaskState:
        workspace = cfg.out / str(state.sample_id) / f"epoch-{state.epoch}"
        prepare_workspace(workspace, cfg.mcp_config)
        prompt = state.input_text

        started = time.monotonic()
        env = {**os.environ, "PATCHPARITY_DEEPSEEK_MODEL": cfg.model_id}
        timeout = state.metadata["wall_clock_ms"] / 1000 + AGENT_GRACE_SECONDS
        outcome = await run_process(agent_args(state.metadata, workspace, cfg), workspace, prompt, timeout, env)
        (workspace / "agent.out.txt").write_text(outcome.stdout, encoding="utf-8")
        (workspace / "agent.err.txt").write_text(outcome.stderr, encoding="utf-8")
        result = last_json_line(outcome.stdout) or {}

        session = session_file(workspace, result.get("sessionId"))
        messages = session_messages(session) if session else []
        if messages:
            state.messages = messages
        last_text = next((m.text for m in reversed(messages) if isinstance(m, ChatMessageAssistant) and m.text), "")
        state.output = ModelOutput.from_content(model=cfg.model_id, content=last_text)

        tools = count_tools(messages)
        usage = result.get("usage") or {}
        agent = {
            "status": result.get("status") or ("killed" if outcome.timed_out else "no_result"),
            "exit": outcome.returncode,
            "timed_out": outcome.timed_out,
            "session": result.get("sessionId"),
            "failure": result.get("failure"),
            "turns": result.get("turns"),
            "tool_calls": result.get("toolCalls"),
            "tokens": usage.get("totalTokens"),
            "cost": (usage.get("cost") or {}).get("total"),
            "error": result.get("errorMessage"),
            "seconds": round(time.monotonic() - started, 1),
        }
        state.store.set("workspace", str(workspace))
        state.store.set("agent", agent)
        state.store.set("tools", tools)
        transcript().info({"agent": agent, "tools": tools, "workspace": str(workspace)}, source="pigeon")
        return state

    return solve


def _metadata_values(scores: list[SampleScore], key: str) -> list[float]:
    return [
        float(s.score.metadata[key])
        for s in scores
        if s.score.metadata and isinstance(s.score.metadata.get(key), (int, float))
    ]


def _mean(values: list[float]) -> float:
    return sum(values) / len(values) if values else float("nan")


# 下面几个指标取每个 epoch 各自的分数（不经合并）：花费、轮次、尝试次数要按每次运行平均


@metric(scores="unreduced")
def mean_turns() -> Metric:
    def compute(scores: list[SampleScore]) -> float:
        return _mean(_metadata_values(scores, "turns"))

    return compute


@metric(scores="unreduced")
def mean_cost() -> Metric:
    def compute(scores: list[SampleScore]) -> float:
        return _mean(_metadata_values(scores, "cost"))

    return compute


@metric(scores="unreduced")
def mean_attempts() -> Metric:
    def compute(scores: list[SampleScore]) -> float:
        return _mean(_metadata_values(scores, "attempts"))

    return compute


@metric(scores="unreduced")
def foreign_tool_runs() -> Metric:
    """调用过 patchparity 以外工具的运行次数：题面只允许 patchparity 的工具，Pigeon 无法从配置上禁掉内置工具。"""

    def compute(scores: list[SampleScore]) -> float:
        return float(sum(1 for s in scores if s.score.metadata and s.score.metadata.get("foreign_tools")))

    return compute


@scorer(metrics=[accuracy(), stderr(), mean_turns(), mean_cost(), mean_attempts(), foreign_tool_runs()])
def patchparity_judge():
    async def score(state: TaskState, target: Target) -> Score:
        workspace = Path(state.store.get("workspace"))
        task_dir = state.metadata["task_dir"]
        command = [part.replace("{TASK_DIR}", task_dir) for part in state.metadata["verifier"]]
        verify = await run_process(command, workspace, None, state.metadata["verifier_timeout_ms"] / 1000)
        (workspace / "verify.out.txt").write_text(verify.stdout, encoding="utf-8")
        judge = last_json_line(verify.stdout) or {}
        passed = not verify.timed_out and verify.returncode == 0

        agent = state.store.get("agent") or {}
        tools = state.store.get("tools") or {}
        foreign = [name for name in tools if not name.startswith(TOOL_PREFIX)]
        return Score(
            value=CORRECT if passed else INCORRECT,
            answer=str(judge.get("verdict", "no_result")),
            explanation=verify.stdout.strip() or verify.stderr.strip(),
            metadata={
                "verdict": judge.get("verdict"),
                "observable": judge.get("observable"),
                "scenario": judge.get("scenario"),
                "first_divergent_tick": judge.get("first_divergent_tick"),
                "attempts": scenario_attempts(workspace),
                "turns": agent.get("turns"),
                "cost": agent.get("cost"),
                "tokens": agent.get("tokens"),
                "agent_status": agent.get("status"),
                "agent_seconds": agent.get("seconds"),
                "foreign_tools": foreign,
                "tools": tools,
                "judge": judge,
            },
        )

    return score


# ── 任务 ──────────────────────────────────────────────────


def thinking_level(value: Any) -> str:
    """-T 的值按 YAML 解析，`-T thinking=off` 到这里是 False（YAML 1.1 把 off 读成布尔值）。
    当成"没给档位"会让 Pigeon 取缺省的 high，与 S5、S6 的条件不一致（裁决 020），所以这里还原成 off。"""
    if value is False:
        return "off"
    if value is None:
        return ""
    if value is True:
        raise ValueError("thinking 须为档位名（off、low、medium、high 等）")
    return str(value)


@task
def patchparity(
    tasks: str = "eval/tasks",
    out: str = "",
    pigeon: str = "",
    stream_fn: str = "",
    mcp_config: str = "",
    only: str = "",
    model_id: str = "deepseek-v4-flash",
    thinking: str = "off",
    node: str = "node",
    epochs: int = 1,
) -> Task:
    if not (out and pigeon and stream_fn and mcp_config):
        raise ValueError("须给出 -T out、pigeon、stream_fn、mcp_config")
    thinking = thinking_level(thinking)
    cfg = Config(Path(out), Path(pigeon), stream_fn, Path(mcp_config), model_id, thinking, node)
    k = int(epochs)
    samples = load_samples(tasks, only)
    if not samples:
        raise ValueError(f"{tasks} 下没有 id 以 '{only}' 开头的题")
    return Task(
        dataset=samples,
        solver=pigeon_agent(cfg),
        scorer=patchparity_judge(),
        # 每题跑 k 次：平均通过率、k 次里至少一次通过的概率（pass@k）、k 次全过的概率（pass^k）
        epochs=Epochs(k, ["mean", f"pass_at_{k}", f"pass_k_{k}"]) if k > 1 else 1,
        # 有样本出错也接着跑完其余的；出错的样本可用 inspect eval-retry 补跑
        fail_on_error=False,
        metadata={"agent": "Pigeon（pigeon run）", "model": model_id, "thinking": thinking or "（Pigeon 缺省）"},
    )
