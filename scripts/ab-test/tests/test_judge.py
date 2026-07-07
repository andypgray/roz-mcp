from __future__ import annotations

import json
from collections.abc import Callable
from pathlib import Path

import pytest

from roslyn_abtest.judge import (
    _extract_json,
    judge_breaking_report,
    judge_decompile_report,
    judge_method_report,
    judge_report,
    run_judge,
)

# Marker-gated: judge_report/run_judge import claude_agent_sdk and (unstubbed)
# would invoke a model. Every test here stubs roslyn_abtest.judge.query.
pytestmark = pytest.mark.integration


def _make_stub(reply: str) -> Callable:
    """A query() stand-in yielding one AssistantMessage(TextBlock) then a ResultMessage.

    Mirrors test_integration's stub but carries assistant text — the judge reads
    TextBlocks, not just the ResultMessage."""
    from claude_agent_sdk import AssistantMessage, ResultMessage, TextBlock

    async def _stub(*, prompt: str, options: object, **_extra: object) -> object:
        yield AssistantMessage(content=[TextBlock(text=reply)], model="judge-stub")
        yield ResultMessage(
            subtype="result",
            duration_ms=1,
            duration_api_ms=1,
            is_error=False,
            num_turns=1,
            session_id="t",
            stop_reason="end_turn",
            total_cost_usd=0.0,
            result=reply,
        )

    return _stub


_FULL_RECALL = json.dumps(
    {
        "site_recall": 1.0,
        "verdict_accuracy": 1.0,
        "missed_sites": [],
        "hallucinated_sites": [],
        "notes": "",
    }
)
_MISSED_SITE = json.dumps(
    {
        "site_recall": 0.75,
        "verdict_accuracy": 1.0,
        "missed_sites": ["OrderService.cs:GetByIdAsync"],
        "hallucinated_sites": [],
        "notes": "missed one repository call",
    }
)
_WRONG_VERDICT = json.dumps(
    {
        "site_recall": 1.0,
        "verdict_accuracy": 0.5,
        "missed_sites": [],
        "hallucinated_sites": ["Made.cs:Up"],
        "notes": "tagged an unsafe site as requires-update",
    }
)

_METHOD_FULL = json.dumps(
    {
        "inbound_recall": 1.0,
        "outbound_recall": 1.0,
        "missed_callers": [],
        "missed_callees": [],
        "hallucinated_callers": [],
        "hallucinated_callees": [],
        "notes": "",
    }
)
_METHOD_PARTIAL = json.dumps(
    {
        "inbound_recall": 0.5,
        "outbound_recall": 0.8,
        "missed_callers": ["OrderController.cs:PlaceOrder"],
        "missed_callees": [],
        "hallucinated_callers": [],
        "hallucinated_callees": ["FooService.Bar"],
        "notes": "missed half the callers; invented one callee",
    }
)


async def test_judge_report_full_recall_passes_through(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _make_stub(_FULL_RECALL))
    result = await judge_report("04-impact-analysis", "REFERENCE", "CANDIDATE")
    assert result["site_recall"] == 1.0
    assert result["verdict_accuracy"] == 1.0
    assert result["missed_sites"] == []
    assert result["hallucinated_sites"] == []


async def test_judge_report_missed_site_is_parsed(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _make_stub(_MISSED_SITE))
    result = await judge_report("06-impact-remove", "REFERENCE", "CANDIDATE")
    assert result["site_recall"] == 0.75
    assert result["missed_sites"] == ["OrderService.cs:GetByIdAsync"]


async def test_judge_report_wrong_verdict_is_parsed(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _make_stub(_WRONG_VERDICT))
    result = await judge_report("07-impact-accessibility", "REFERENCE", "CANDIDATE")
    assert result["verdict_accuracy"] == 0.5
    assert result["hallucinated_sites"] == ["Made.cs:Up"]


async def test_judge_report_tolerates_code_fence(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _make_stub(f"```json\n{_FULL_RECALL}\n```"))
    result = await judge_report("04-impact-analysis", "REFERENCE", "CANDIDATE")
    assert result["site_recall"] == 1.0


async def test_judge_report_clamps_out_of_range_scores(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(
        "roslyn_abtest.judge.query",
        _make_stub('{"site_recall": 1.5, "verdict_accuracy": -0.2}'),
    )
    result = await judge_report("08-impact-signature", "REFERENCE", "CANDIDATE")
    assert result["site_recall"] == 1.0
    assert result["verdict_accuracy"] == 0.0


async def test_judge_method_report_full_recall_passes_through(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _make_stub(_METHOD_FULL))
    result = await judge_method_report("10-method-callgraph", "REFERENCE", "CANDIDATE")
    assert result["inbound_recall"] == 1.0
    assert result["outbound_recall"] == 1.0
    assert result["missed_callers"] == []
    assert result["hallucinated_callees"] == []


async def test_judge_method_report_partial_is_parsed(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _make_stub(_METHOD_PARTIAL))
    result = await judge_method_report("11-method-interface", "REFERENCE", "CANDIDATE")
    assert result["inbound_recall"] == 0.5
    assert result["outbound_recall"] == 0.8
    assert result["missed_callers"] == ["OrderController.cs:PlaceOrder"]
    assert result["hallucinated_callees"] == ["FooService.Bar"]


async def test_judge_method_report_clamps_out_of_range(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(
        "roslyn_abtest.judge.query",
        _make_stub('{"inbound_recall": 1.4, "outbound_recall": -0.3}'),
    )
    result = await judge_method_report("12-method-overloads", "REFERENCE", "CANDIDATE")
    assert result["inbound_recall"] == 1.0
    assert result["outbound_recall"] == 0.0


_BREAKING_FULL = json.dumps(
    {
        "recall": 1.0,
        "classification_accuracy": 1.0,
        "split_correct": 1.0,
        "missed": [],
        "misclassified": [],
        "notes": "",
    }
)
_DECOMPILE = json.dumps(
    {
        "factual_accuracy": 0.9,
        "grounding": 0.8,
        "hallucinations": 2,
        "source_first": 1.0,
        "hallucinated_claims": ["claims it's thread-safe"],
        "notes": "one false thread-safety claim",
    }
)


async def test_judge_breaking_report_parses(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _make_stub(_BREAKING_FULL))
    result = await judge_breaking_report("P3-breaking", "REFERENCE", "CANDIDATE")
    assert result["recall"] == 1.0
    assert result["classification_accuracy"] == 1.0
    assert result["split_correct"] == 1.0


async def test_judge_decompile_report_parses(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _make_stub(_DECOMPILE))
    result = await judge_decompile_report("P4-decompile", "REFERENCE", "CANDIDATE")
    assert result["factual_accuracy"] == 0.9
    assert result["hallucinations"] == 2
    assert result["source_first"] == 1.0
    assert result["hallucinated_claims"] == ["claims it's thread-safe"]


async def test_judge_decompile_report_clamps_and_floors(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(
        "roslyn_abtest.judge.query",
        _make_stub('{"factual_accuracy": 1.4, "grounding": -0.1, "hallucinations": -3}'),
    )
    result = await judge_decompile_report("P4-decompile", "REFERENCE", "CANDIDATE")
    assert result["factual_accuracy"] == 1.0
    assert result["grounding"] == 0.0
    assert result["hallucinations"] == 0  # floored at 0


async def test_run_judge_dispatches_breaking_task_to_breaking_rubric(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _make_stub(_BREAKING_FULL))
    tasks_dir = tmp_path / "tasks"
    tasks_dir.mkdir()
    (tasks_dir / "P3-breaking.md").write_text(
        "---\nname: P3-breaking\nrubric: breaking\nreport: BREAK.md\nreference: P3-breaking\n"
        "---\ndocumentation\n",
        encoding="utf-8",
    )
    (tasks_dir / "P3-breaking.reference.md").write_text("REF", encoding="utf-8")
    monkeypatch.setattr("roslyn_abtest.judge.TASKS_DIR", tasks_dir)

    ts_dir = tmp_path / "results" / "testrun"
    ts_dir.mkdir(parents=True)
    run = {"task": "P3-breaking", "arm": "arm-prompt-recipe", "rep": 1, "wall_seconds": 1.0}
    (ts_dir / "P3-breaking-arm-prompt-recipe-1.json").write_text(json.dumps(run), encoding="utf-8")
    artifacts = ts_dir / "P3-breaking-arm-prompt-recipe-1.artifacts"
    artifacts.mkdir()
    (artifacts / "BREAK.md").write_text("CANDIDATE REPORT", encoding="utf-8")

    summary_path = await run_judge(ts_dir, model="judge-stub")

    judgment = json.loads(
        (ts_dir / "P3-breaking-arm-prompt-recipe-1.judgment.json").read_text(encoding="utf-8")
    )
    assert judgment["rubric"] == "breaking"
    assert judgment["recall"] == 1.0
    assert "site_recall" not in judgment
    assert "Breaking-change tasks" in summary_path.read_text(encoding="utf-8")


async def _raising_query(*, prompt: str, options: object, **_extra: object) -> object:
    """A query() stand-in that fails on iteration the way the SDK does when the CLI
    subprocess exits non-zero ("Command failed with exit code 1")."""
    raise Exception("Command failed with exit code 1 (exit code: 1)")
    yield  # unreachable; the bare yield makes this an async generator


async def test_run_judge_records_error_and_continues_when_query_fails(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _raising_query)
    tasks_dir = tmp_path / "tasks"
    tasks_dir.mkdir()
    (tasks_dir / "04-impact-analysis.reference.md").write_text("REF", encoding="utf-8")
    monkeypatch.setattr("roslyn_abtest.judge.TASKS_DIR", tasks_dir)

    ts_dir = tmp_path / "results" / "20260617T113722Z"
    ts_dir.mkdir(parents=True)
    run = {"task": "04-impact-analysis", "arm": "arm-prompt-recipe", "rep": 1, "wall_seconds": 1.0}
    (ts_dir / "04-impact-analysis-arm-prompt-recipe-1.json").write_text(
        json.dumps(run), encoding="utf-8"
    )
    artifacts = ts_dir / "04-impact-analysis-arm-prompt-recipe-1.artifacts"
    artifacts.mkdir()
    (artifacts / "IMPACT_ANALYSIS.md").write_text("CANDIDATE", encoding="utf-8")

    # Must NOT raise — the failure is recorded, not propagated.
    summary_path = await run_judge(ts_dir, model="judge-stub")

    judgment = json.loads(
        (ts_dir / "04-impact-analysis-arm-prompt-recipe-1.judgment.json").read_text(
            encoding="utf-8"
        )
    )
    assert judgment["site_recall"] is None  # null-scored, not crashed
    assert "judge query failed" in judgment["notes"]
    assert summary_path.exists()


def test_extract_json_finds_object_amid_prose() -> None:
    parsed = _extract_json('Here is my verdict:\n{"site_recall": 0.9}\nThanks!')
    assert parsed["site_recall"] == 0.9


async def test_run_judge_writes_judgment_and_summary(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _make_stub(_FULL_RECALL))
    # Reference lives under tasks/<task>.reference.md — point TASKS_DIR at a temp dir.
    tasks_dir = tmp_path / "tasks"
    tasks_dir.mkdir()
    (tasks_dir / "04-impact-analysis.reference.md").write_text("REF", encoding="utf-8")
    monkeypatch.setattr("roslyn_abtest.judge.TASKS_DIR", tasks_dir)

    ts_dir = tmp_path / "results" / "testrun"
    ts_dir.mkdir(parents=True)
    run = {"task": "04-impact-analysis", "arm": "arm-ci-on", "rep": 1, "wall_seconds": 1.0}
    (ts_dir / "04-impact-analysis-arm-ci-on-1.json").write_text(json.dumps(run), encoding="utf-8")
    artifacts = ts_dir / "04-impact-analysis-arm-ci-on-1.artifacts"
    artifacts.mkdir()
    (artifacts / "IMPACT_ANALYSIS.md").write_text("CANDIDATE REPORT", encoding="utf-8")

    summary_path = await run_judge(ts_dir, model="judge-stub")

    assert summary_path.exists()
    assert "Judge Summary" in summary_path.read_text(encoding="utf-8")
    judgment = json.loads(
        (ts_dir / "04-impact-analysis-arm-ci-on-1.judgment.json").read_text(encoding="utf-8")
    )
    assert judgment["site_recall"] == 1.0
    assert judgment["candidate_present"] is True
    assert judgment["arm"] == "arm-ci-on"


async def test_run_judge_missing_report_scores_zero(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _make_stub(_FULL_RECALL))
    tasks_dir = tmp_path / "tasks"
    tasks_dir.mkdir()
    (tasks_dir / "04-impact-analysis.reference.md").write_text("REF", encoding="utf-8")
    monkeypatch.setattr("roslyn_abtest.judge.TASKS_DIR", tasks_dir)

    ts_dir = tmp_path / "results" / "testrun"
    ts_dir.mkdir(parents=True)
    run = {"task": "04-impact-analysis", "arm": "arm-ci-baseline", "rep": 1, "wall_seconds": 1.0}
    (ts_dir / "04-impact-analysis-arm-ci-baseline-1.json").write_text(
        json.dumps(run), encoding="utf-8"
    )
    # No .artifacts dir -> report missing -> recall 0, candidate_present False.

    await run_judge(ts_dir, model="judge-stub")

    judgment = json.loads(
        (ts_dir / "04-impact-analysis-arm-ci-baseline-1.judgment.json").read_text(encoding="utf-8")
    )
    assert judgment["site_recall"] == 0.0
    assert judgment["candidate_present"] is False


async def test_run_judge_dispatches_method_task_to_method_rubric(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.query", _make_stub(_METHOD_FULL))
    tasks_dir = tmp_path / "tasks"
    tasks_dir.mkdir()
    (tasks_dir / "10-method-callgraph.reference.md").write_text("REF", encoding="utf-8")
    monkeypatch.setattr("roslyn_abtest.judge.TASKS_DIR", tasks_dir)

    ts_dir = tmp_path / "results" / "testrun"
    ts_dir.mkdir(parents=True)
    run = {"task": "10-method-callgraph", "arm": "arm-am-routed", "rep": 1, "wall_seconds": 1.0}
    (ts_dir / "10-method-callgraph-arm-am-routed-1.json").write_text(
        json.dumps(run), encoding="utf-8"
    )
    artifacts = ts_dir / "10-method-callgraph-arm-am-routed-1.artifacts"
    artifacts.mkdir()
    (artifacts / "CALL_GRAPH.md").write_text("CANDIDATE REPORT", encoding="utf-8")

    summary_path = await run_judge(ts_dir, model="judge-stub")

    judgment = json.loads(
        (ts_dir / "10-method-callgraph-arm-am-routed-1.judgment.json").read_text(encoding="utf-8")
    )
    # Method rubric keys present, impact keys absent -> dispatched to the method judge.
    assert judgment["inbound_recall"] == 1.0
    assert judgment["outbound_recall"] == 1.0
    assert "site_recall" not in judgment
    assert "Method-comprehension tasks" in summary_path.read_text(encoding="utf-8")
