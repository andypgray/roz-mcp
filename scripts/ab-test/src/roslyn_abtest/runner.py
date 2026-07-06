from __future__ import annotations

import json
import re
import shutil
import subprocess
import time
from collections import Counter
from pathlib import Path

import claude_agent_sdk
from claude_agent_sdk import (
    AssistantMessage,
    ClaudeAgentOptions,
    ResultMessage,
    ToolUseBlock,
    UserMessage,
    query,
)

from claude_agent_sdk.types import SystemPromptPreset

from .bootstrap import NUGET_ORG_CONFIG, ensure_fixture
from .diffmetrics import compute_diff_hygiene
from .fixtures import Fixture, get_fixture
from .paths import PATCHES_DIR, REPO_ROOT, RESULTS_DIR
from .tasks import parse_task

PROJECT_INSTRUCTIONS_SNIPPET = (
    REPO_ROOT / "src" / "Zphil.Roz" / "project-instructions-snippet.md"
)

BASE_TOOLS = ["Read", "Edit", "Write", "Grep", "Glob", "Bash"]

NO_FORMATTER_DIRECTIVE = (
    "\n\n## Experiment constraints\n"
    "Do not invoke ReSharper, `jb cleanupcode`, `dotnet format`, or any other code "
    "formatter or linter. Submit raw edits only — formatting normalization would "
    "pollute the diff being measured."
)
FORMATTER_POLLUTANTS = ("jb ", "cleanupcode", "dotnet format", "resharper")


def resolve_injected_snippet(arm_config: dict) -> str:
    """The system-prompt snippet text an arm injects: the production snippet by default,
    a variant when `claude_md_snippet_path` is set, or "" when injection is off.

    Defaulting to the production snippet keeps arms WITHOUT the override (arm-ci-baseline,
    arm-am-on) byte-identical to before the override existed — the A/B-integrity invariant
    the analyze_method experiment's 3-arm design rests on. An arm opts into a variant
    (e.g. arm-am-routed's routing-row snippet) by setting `claude_md_snippet_path`.
    """
    if not arm_config.get("inject_claude_md_snippet"):
        return ""
    override = arm_config.get("claude_md_snippet_path")
    snippet_path = (REPO_ROOT / override) if override else PROJECT_INSTRUCTIONS_SNIPPET
    return snippet_path.read_text(encoding="utf-8")


def run_git(cache_dir: Path, *git_args: str, check: bool = True) -> subprocess.CompletedProcess:
    # encoding="utf-8" (not bare text=True) so a UTF-8 BOM in a diffed file decodes to a
    # clean U+FEFF rather than cp1252 mojibake `ï»¿`; errors="replace" keeps any stray
    # non-UTF-8 byte from faulting the capture. Without this, stored diffs carry mojibake
    # BOMs that diffmetrics has to special-case.
    return subprocess.run(
        ["git", "-c", "core.longpaths=true", "-C", str(cache_dir), *git_args],
        check=check,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def _reset_to_sha(fixture: Fixture) -> None:
    cache_dir = fixture.cache_dir
    run_git(cache_dir, "reset", "--hard", fixture.sha)
    # -x removes gitignored files too (obj/, bin/, .vs/). Windows long-path warnings are
    # non-fatal - if some build artifacts survive they don't affect Roslyn's ability to
    # load the solution, so don't fail the run on them.
    run_git(cache_dir, "clean", "-fdx", check=False)
    # Override the user-level NuGet.config (which may point at private feeds for other
    # solutions) with a clone-local one that only uses nuget.org. Prevents NU1900 warnings
    # flooding the build output and any `dotnet build` Claude itself invokes.
    (cache_dir / "NuGet.config").write_text(NUGET_ORG_CONFIG, encoding="utf-8")


def prepare_clone(fixture: Fixture) -> None:
    """Ensure clone exists, then snap it back to <sha> with a clean working tree."""
    ensure_fixture(fixture)
    _reset_to_sha(fixture)


def _git_commit_all(cache_dir: Path, message: str) -> None:
    """Commit the whole working tree on a throwaway `abtest-setup` branch.

    Used by setup_commit so the planted patch lands in history: the run's own
    diff (working tree vs HEAD) then shows the AGENT's edits only, and
    check_breaking_changes can diff `<fixture.sha>...HEAD` against real content.
    The clone's own user.name/email may be unset, so pass them inline."""
    run_git(cache_dir, "checkout", "-B", "abtest-setup")
    run_git(cache_dir, "add", "-A")
    run_git(
        cache_dir,
        "-c", "user.name=abtest",
        "-c", "user.email=abtest@local",
        "commit", "--quiet", "-m", message,
    )


def apply_planted_setup(fixture: Fixture, task_metadata: dict, task_path: Path) -> None:
    """Apply a task's planted-condition patch to the clone (and commit it if setup_commit).

    Runs after `prepare_clone`, so the patch lands on the clean pinned snapshot.
    `setup_patch` is a path under `scripts/ab-test/patches/`."""
    patch_rel = task_metadata.get("setup_patch")
    if not patch_rel:
        return
    patch_path = PATCHES_DIR / Path(patch_rel).name
    if not patch_path.is_file():
        raise FileNotFoundError(
            f"{task_path.name}: setup_patch not found at {patch_path}"
        )
    run_git(fixture.cache_dir, "apply", "--whitespace=nowarn", str(patch_path))
    if task_metadata.get("setup_commit"):
        _git_commit_all(fixture.cache_dir, f"abtest planted setup for {task_path.stem}")


# Recipe text is identical across reps of one prompt task, so render once per
# (prompt, args) and reuse — one fewer server spawn per extra rep.
_RENDER_CACHE: dict[tuple[str, tuple[tuple[str, str], ...]], str] = {}


def _stringify_prompt_args(raw: dict, fixture: Fixture) -> dict[str, str]:
    """Coerce prompt_args to the strings MCP requires and expand the $FIXTURE_SHA token.

    $FIXTURE_SHA lets a task name the pinned commit (e.g. check_breaking_changes'
    `baseline`) without hard-coding the SHA, which the manifest owns."""
    return {key: str(value).replace("$FIXTURE_SHA", fixture.sha) for key, value in raw.items()}


def render_task_brief(task_metadata: dict, fixture: Fixture) -> str:
    """Render a prompt task's recipe via a real `prompts/get`, memoized, + report directive.

    The rendered recipe is the exact message a human typing the slash command would
    get. When the task declares a `report:` artifact, append a one-line directive
    telling the agent where to write it — the harness's grading hook, analogous to
    the naive tasks that already name their report file in the brief body."""
    prompt_name = task_metadata["prompt"]
    args = _stringify_prompt_args(dict(task_metadata.get("prompt_args") or {}), fixture)
    key = (prompt_name, tuple(sorted(args.items())))
    recipe = _RENDER_CACHE.get(key)
    if recipe is None:
        # Lazy import: mcp_client carries subprocess/threading machinery only this
        # path needs, and only when a prompt task is actually run.
        from .mcp_client import render_prompt

        recipe = render_prompt(prompt_name, args, str(fixture.solution_path))
        _RENDER_CACHE[key] = recipe
    report = task_metadata.get("report")
    if report:
        recipe += (
            f"\n\n## Output\nWhen you have finished, write your full report to the file "
            f"`{report}` at the TOP of your current working directory (not in a `src/` or "
            "other subfolder — the path must be exactly that, relative to where you started). "
            "Create that one file; do not modify any other files unless the recipe above "
            "explicitly tells you to apply edits."
        )
    return recipe


def get_clone_diff(cache_dir: Path) -> dict:
    stat = run_git(cache_dir, "diff", "--stat").stdout
    status = run_git(cache_dir, "status", "--porcelain").stdout
    full = run_git(cache_dir, "diff").stdout
    untracked = run_git(cache_dir, "ls-files", "--others", "--exclude-standard").stdout.splitlines()
    return {"stat": stat, "status": status, "full": full, "untracked": untracked}


def count_diff_loc(diff_full: str) -> int:
    return sum(
        1
        for line in diff_full.splitlines()
        if line.startswith(("+", "-")) and not line.startswith(("+++", "---"))
    )


def count_token_in_src(solution_path: Path, token: str) -> int:
    """Count case-sensitive, word-boundary token matches in .cs files under the solution dir."""
    pattern = re.compile(rf"\b{re.escape(token)}\b")
    total = 0
    src_root = solution_path.parent
    for cs_path in src_root.rglob("*.cs"):
        try:
            text = cs_path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        total += len(pattern.findall(text))
    return total


def build_clone(solution_path: Path, *, no_incremental: bool = False) -> tuple[int, str, str]:
    """Build the clone; return (exit_code, full_stdout, full_stderr).

    Returns the UNTRIMMED output so `diagnostics-delta` can count every diagnostic
    line; `_verify_build` trims the copy it writes to the result JSON (the untrimmed
    output is dominated by NU1900 warnings from an unreachable NuGet source).

    `no_incremental` forces a from-scratch compile. The exit-code `build` verifier
    leaves it off (incremental is faster and exit code is cache-stable), but
    `diagnostics-delta` turns it on: MSBuild skips up-to-date projects without
    re-running the compiler, and a skipped compile does NOT re-emit its warnings —
    so counting diagnostics off an incremental build would under-count whatever the
    agent already compiled (a false pass). Verifier builds run after the measured
    turn, so the extra time is off the experiment's wall-clock."""
    cmd = ["dotnet", "build", str(solution_path), "--nologo", "-v", "q"]
    if no_incremental:
        cmd.append("--no-incremental")
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=900)
    return result.returncode, result.stdout, result.stderr


async def run_one(
    arm_config: dict,
    task_path: Path,
    rep: int,
    timestamp: str,
    max_turns: int,
    max_budget_usd: float,
    model: str,
) -> dict:
    arm_name = arm_config["name"]
    task_name = task_path.stem
    out_dir = RESULTS_DIR / timestamp
    out_dir.mkdir(parents=True, exist_ok=True)

    task_metadata, task_brief = parse_task(task_path)

    fixture = get_fixture(task_metadata.get("fixture"))
    prepare_clone(fixture)
    apply_planted_setup(fixture, task_metadata, task_path)

    mcp_servers: dict = {}
    for server_name, cfg in arm_config.get("mcp_servers", {}).items():
        mcp_cfg = dict(cfg)
        env = dict(mcp_cfg.get("env", {}))
        env.setdefault("ROZ_SOLUTION_PATH", str(fixture.solution_path))
        # Pin the log level: an ambient ROZ_LOG_LEVEL=Information leaks through the SDK's
        # env passthrough and turns per-file reload logging into a multi-hundred-MB log.
        env.setdefault("ROZ_LOG_LEVEL", "Warning")
        mcp_cfg["env"] = env
        mcp_servers[server_name] = mcp_cfg

    allowed_tools = BASE_TOOLS + list(arm_config.get("extra_allowed_tools", []))

    snippet = resolve_injected_snippet(arm_config)
    system_prompt: SystemPromptPreset = {
        "type": "preset",
        "preset": "claude_code",
        "append": snippet + NO_FORMATTER_DIRECTIVE,
    }

    options = ClaudeAgentOptions(
        model=model,
        cwd=str(fixture.cache_dir),
        tools=allowed_tools,
        allowed_tools=allowed_tools,
        mcp_servers=mcp_servers,
        system_prompt=system_prompt,
        permission_mode="bypassPermissions",
        max_turns=max_turns,
        max_budget_usd=max_budget_usd,
        setting_sources=None,
        skills=None,
    )

    # Prompt tasks declare a `prompt:` key: render its recipe (the exact slash-command
    # message) as the brief. Everything else keeps task_brief verbatim — byte-identical
    # behavior for the existing 00-13 tool tasks (the A/B-integrity invariant).
    prompt = (
        render_task_brief(task_metadata, fixture)
        if task_metadata.get("prompt")
        else task_brief
    )

    tool_calls: list[dict] = []
    transcript: list[dict] = []
    start = time.monotonic()
    usage = None
    total_cost = None
    stop_reason = None
    duration_ms = None
    duration_api_ms = None
    num_turns = None
    is_error = False
    err_detail: str | None = None

    try:
        async for msg in query(prompt=prompt, options=options):
            if isinstance(msg, AssistantMessage):
                for block in msg.content:
                    if isinstance(block, ToolUseBlock):
                        # Scan full input for pollutants BEFORE truncating the preview —
                        # otherwise a long Bash preamble can push `dotnet format` past
                        # the 300-char cap and silently evade detection.
                        input_str = str(block.input)
                        polluted = (
                            block.name == "Bash"
                            and any(p in input_str.lower() for p in FORMATTER_POLLUTANTS)
                        )
                        tool_calls.append({
                            "name": block.name,
                            "input_preview": input_str[:300],
                            "polluted": polluted,
                        })
                    elif hasattr(block, "text"):
                        transcript.append({"role": "assistant", "text": block.text})
            elif isinstance(msg, UserMessage):
                for block in getattr(msg, "content", []) or []:
                    if hasattr(block, "text"):
                        transcript.append({"role": "user", "text": str(block.text)[:2000]})
            elif isinstance(msg, ResultMessage):
                usage = msg.usage
                total_cost = msg.total_cost_usd
                stop_reason = msg.stop_reason
                duration_ms = msg.duration_ms
                duration_api_ms = msg.duration_api_ms
                num_turns = msg.num_turns
                is_error = msg.is_error
    except Exception as exc:
        err_detail = repr(exc)
        is_error = True

    wall = time.monotonic() - start
    diff = get_clone_diff(fixture.cache_dir)

    histogram = dict(Counter(tc["name"] for tc in tool_calls))
    bash_pollutants = [tc for tc in tool_calls if tc.get("polluted")]
    roslyn_tools_env = (
        arm_config.get("mcp_servers", {})
                  .get("roz-mcp", {})
                  .get("env", {})
                  .get("ROZ_TOOLS")
    )
    mcp_tools_advertised = sum(
        1 for t in allowed_tools if t.startswith("mcp__roz__")
    )

    result = {
        "arm": arm_name,
        "task": task_name,
        "rep": rep,
        "timestamp": timestamp,
        "wall_seconds": round(wall, 2),
        "duration_ms": duration_ms,
        "duration_api_ms": duration_api_ms,
        "num_turns": num_turns,
        "usage": usage,
        "total_cost_usd": total_cost,
        "stop_reason": stop_reason,
        "is_error": is_error,
        "error": err_detail,
        "tool_call_count": len(tool_calls),
        "tool_histogram": histogram,
        "diff_stat": diff["stat"],
        "diff_status": diff["status"],
        "untracked_files": diff["untracked"],
        "diff_loc": count_diff_loc(diff["full"]),
        "model": model,
        "sdk_version": claude_agent_sdk.__version__,
        "mcp_tools_advertised": mcp_tools_advertised,
        "roslyn_tools_env": roslyn_tools_env,
        "formatter_pollution_hits": len(bash_pollutants),
        "formatter_pollution_samples": [tc["input_preview"] for tc in bash_pollutants[:3]],
    }

    # Diff-hygiene sits next to diff_loc: semantic LOC net of verbatim churn, plus the
    # BOM/whole-file-rewrite gaming signals a raw +/- count can't see. The diff-hygiene
    # verifier and analyze.py's Sem LOC / Churn% columns consume these four fields.
    result.update(compute_diff_hygiene(diff["full"]))

    result["fixture"] = fixture.name

    # Lazy import: verification.py imports build_clone/count_token_in_src from
    # this module, so importing it at module load time would cycle.
    from .verification import VERIFIERS, VerifyContext, validate_verification_order

    verification_specs = list(task_metadata.get("verification") or [])
    validate_verification_order(verification_specs, task_path)
    ctx = VerifyContext(
        cache_dir=fixture.cache_dir,
        solution_path=fixture.solution_path,
        result=result,
        diff_full=diff["full"],
    )
    for spec in verification_specs:
        vtype = spec.get("type")
        if vtype not in VERIFIERS:
            raise ValueError(
                f"Unknown verifier type {vtype!r} in {task_path.name}; "
                f"valid: {sorted(VERIFIERS)}"
            )
        result.update(VERIFIERS[vtype](ctx, spec))

    (out_dir / f"{task_name}-{arm_name}-{rep}.json").write_text(
        json.dumps(result, indent=2, default=str), encoding="utf-8"
    )
    (out_dir / f"{task_name}-{arm_name}-{rep}.transcript.json").write_text(
        json.dumps({"tool_calls": tool_calls, "transcript": transcript}, indent=2, default=str),
        encoding="utf-8",
    )
    (out_dir / f"{task_name}-{arm_name}-{rep}.diff").write_text(diff["full"], encoding="utf-8")

    # Copy new/untracked files into an artifacts dir so later runs' resets don't lose them.
    # Skip NuGet.config (our own harness artifact) and anything over 256KB.
    artifacts_dir = out_dir / f"{task_name}-{arm_name}-{rep}.artifacts"
    for rel in diff["untracked"]:
        if rel == "NuGet.config":
            continue
        src = fixture.cache_dir / rel
        if not src.is_file():
            continue
        try:
            if src.stat().st_size > 256 * 1024:
                continue
            dst = artifacts_dir / rel
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(src, dst)
        except OSError:
            continue

    return result
