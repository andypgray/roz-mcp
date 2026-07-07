from __future__ import annotations

import json
from pathlib import Path

import pytest

from roslyn_abtest.arms import load_arm_configs
from roslyn_abtest.paths import TASKS_DIR
from roslyn_abtest.runner import run_one

pytestmark = pytest.mark.integration


async def _stub_query(*, prompt: str, options, **_extra) -> object:
    """Async iterator yielding a single ResultMessage — replaces claude_agent_sdk.query.
    `**_extra` absorbs forward-compat kwargs the real SDK accepts (e.g. `transport=`)."""
    from claude_agent_sdk import ResultMessage

    yield ResultMessage(
        subtype="result",
        duration_ms=10,
        duration_api_ms=5,
        is_error=False,
        num_turns=1,
        session_id="test-session",
        stop_reason="end_turn",
        total_cost_usd=0.01,
        usage={
            "input_tokens": 100,
            "output_tokens": 50,
            "cache_read_input_tokens": 0,
            "cache_creation_input_tokens": 0,
        },
    )


async def test_run_one_end_to_end_against_smoke_task(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    monkeypatch.setattr("roslyn_abtest.runner.query", _stub_query)
    # Redirect results to tmp_path so the test doesn't pollute the repo's
    # scripts/ab-test/results/ tree. `analyze.py`'s no-arg fallback picks the
    # lex-max directory name, and "testrun" > any "20260422T..." timestamp.
    monkeypatch.setattr("roslyn_abtest.runner.RESULTS_DIR", tmp_path)

    arm = load_arm_configs(["arm-b-baseline"])[0]
    task_path = TASKS_DIR / "00-smoke.md"
    # 00-smoke declares fixture: spectre-console, so prepare_clone (NOT stubbed
    # here) bootstraps Spectre.Console — ~2 min cold, instant warm — not nopCommerce.
    timestamp = "testrun"

    result = await run_one(
        arm_config=arm,
        task_path=task_path,
        rep=1,
        timestamp=timestamp,
        max_turns=80,
        max_budget_usd=10.0,
        model="claude-opus-4-7",
    )

    out_path = tmp_path / timestamp / "00-smoke-arm-b-baseline-1.json"
    assert out_path.exists(), f"missing result file {out_path}"

    written = json.loads(out_path.read_text(encoding="utf-8"))
    for key in (
        "arm", "task", "rep", "wall_seconds", "tool_call_count",
        "diff_loc", "sdk_version", "loc_delta_max_value",
    ):
        assert key in written, f"missing key {key!r} in written result"
        assert key in result, f"missing key {key!r} in returned result"

    # Guard the wrapper test against silent failure: the runner has a blanket
    # `except Exception` that coerces stub crashes into is_error=True with
    # None usage/cost. Without this assertion, a broken stub would still pass.
    assert written["is_error"] is False
    assert written["total_cost_usd"] == 0.01
    assert written["num_turns"] == 1

    assert isinstance(written["sdk_version"], str)
    assert written["sdk_version"], "sdk_version is empty"
    assert written["arm"] == "arm-b-baseline"
    assert written["task"] == "00-smoke"
    assert written["fixture"] == "spectre-console"


def test_resolve_injected_snippet_inert_for_baseline_and_bare_add() -> None:
    """A/B integrity: arms WITHOUT claude_md_snippet_path inject the production snippet
    verbatim (arm-ci-baseline / arm-am-on stay byte-identical), and a non-injecting arm
    injects nothing. This is the invariant the analyze_method 3-arm design rests on."""
    from roslyn_abtest.runner import PROJECT_INSTRUCTIONS_SNIPPET, resolve_injected_snippet

    production = PROJECT_INSTRUCTIONS_SNIPPET.read_text(encoding="utf-8")

    assert resolve_injected_snippet(load_arm_configs(["arm-ci-baseline"])[0]) == production
    assert resolve_injected_snippet(load_arm_configs(["arm-am-on"])[0]) == production
    assert resolve_injected_snippet(load_arm_configs(["arm-b-baseline"])[0]) == ""


def test_resolve_injected_snippet_routed_arm_adds_exactly_one_routing_row() -> None:
    """The routed arm (R) injects the variant snippet = production + exactly one
    analyze_method routing row; no other line changes."""
    from roslyn_abtest.runner import PROJECT_INSTRUCTIONS_SNIPPET, resolve_injected_snippet

    production = PROJECT_INSTRUCTIONS_SNIPPET.read_text(encoding="utf-8")
    variant = resolve_injected_snippet(load_arm_configs(["arm-am-routed"])[0])

    prod_lines = production.splitlines()
    variant_lines = variant.splitlines()
    added = [ln for ln in variant_lines if ln not in prod_lines]
    removed = [ln for ln in prod_lines if ln not in variant_lines]
    assert removed == []
    assert len(added) == 1
    assert "analyze_method" in added[0]
