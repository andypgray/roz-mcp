from __future__ import annotations

from pathlib import Path

import pytest

from roslyn_abtest.judge import (
    _missing_judgment,
    _normalize_audit_judgment,
    _resolve_judged,
    _unparseable_judgment,
)


def _write_task(tasks_dir: Path, stem: str, frontmatter: str) -> None:
    tasks_dir.mkdir(exist_ok=True)
    (tasks_dir / f"{stem}.md").write_text(
        f"---\nname: {stem}\n{frontmatter}---\ndocumentation body\n", encoding="utf-8"
    )


# --------------------------------- _resolve_judged ------------------------------


def test_resolve_judged_reads_frontmatter_rubric(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    tasks = tmp_path / "tasks"
    _write_task(tasks, "P9-foo", "rubric: breaking\nreport: BREAK.md\nreference: P9-foo\n")
    monkeypatch.setattr("roslyn_abtest.judge.TASKS_DIR", tasks)
    graded = _resolve_judged("P9-foo")
    assert graded is not None
    assert graded.rubric == "breaking"
    assert graded.report == "BREAK.md"
    assert graded.reference == "P9-foo"


def test_resolve_judged_reference_defaults_to_task_stem(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    tasks = tmp_path / "tasks"
    _write_task(tasks, "P1-assess", "rubric: impact\nreport: R.md\n")
    monkeypatch.setattr("roslyn_abtest.judge.TASKS_DIR", tasks)
    graded = _resolve_judged("P1-assess")
    assert graded is not None and graded.reference == "P1-assess"


def test_resolve_judged_reads_audit_rubric(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    tasks = tmp_path / "tasks"
    _write_task(tasks, "02-audit", "rubric: audit\nreport: AUDIT_REPORT.md\n")
    monkeypatch.setattr("roslyn_abtest.judge.TASKS_DIR", tasks)
    graded = _resolve_judged("02-audit")
    assert graded is not None
    assert graded.rubric == "audit"
    assert graded.report == "AUDIT_REPORT.md"
    assert graded.reference == "02-audit"  # defaults to the task stem


def test_resolve_judged_rubric_none_is_not_judged(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    tasks = tmp_path / "tasks"
    _write_task(tasks, "P2-fix", "rubric: none\n")
    monkeypatch.setattr("roslyn_abtest.judge.TASKS_DIR", tasks)
    assert _resolve_judged("P2-fix") is None


def test_resolve_judged_llm_rubric_without_report_is_not_judged(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    tasks = tmp_path / "tasks"
    _write_task(tasks, "P-bad", "rubric: impact\n")  # no report:
    monkeypatch.setattr("roslyn_abtest.judge.TASKS_DIR", tasks)
    assert _resolve_judged("P-bad") is None


def test_resolve_judged_legacy_fallback_when_no_frontmatter(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # Empty tasks dir: the original eight tasks resolve via the hard-coded legacy map.
    monkeypatch.setattr("roslyn_abtest.judge.TASKS_DIR", tmp_path / "tasks")
    impact = _resolve_judged("04-impact-analysis")
    assert impact is not None
    assert impact.rubric == "impact"
    assert impact.report == "IMPACT_ANALYSIS.md"
    method = _resolve_judged("10-method-callgraph")
    assert method is not None and method.rubric == "method"


def test_resolve_judged_unknown_task_is_none(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr("roslyn_abtest.judge.TASKS_DIR", tmp_path / "tasks")
    assert _resolve_judged("99-not-a-task") is None


# ------------------------- _missing / _unparseable judgment ---------------------


def test_missing_judgment_per_rubric_zeros_scores() -> None:
    impact = _missing_judgment("impact")
    assert impact["site_recall"] == 0.0
    assert impact["missed_sites"] == ["<entire report missing>"]

    method = _missing_judgment("method")
    assert method["inbound_recall"] == 0.0
    assert method["missed_callers"] == ["<entire report missing>"]

    breaking = _missing_judgment("breaking")
    assert breaking["recall"] == 0.0
    assert breaking["missed"] == ["<entire report missing>"]

    decompile = _missing_judgment("decompile")
    assert decompile["factual_accuracy"] == 0.0
    assert decompile["hallucinations"] == 0.0
    assert "not found" in decompile["notes"]

    audit = _missing_judgment("audit")
    assert audit["fanin_recall"] == 0.0
    assert audit["entity_recall"] == 0.0
    assert audit["godclass_recall"] == 0.0
    assert audit["hallucinated_items"] == []
    assert "not found" in audit["notes"]


def test_unparseable_judgment_nulls_scores() -> None:
    out = _unparseable_judgment("breaking", ValueError("boom"))
    assert out["recall"] is None
    assert out["classification_accuracy"] is None
    assert "unparseable" in out["notes"]

    audit = _unparseable_judgment("audit", ValueError("boom"))
    assert audit["fanin_recall"] is None
    assert audit["count_plausibility"] is None
    assert audit["hallucinated_items"] == []


# ------------------------------ _normalize_audit_judgment -----------------------


def test_normalize_audit_judgment_clamps_and_shapes() -> None:
    out = _normalize_audit_judgment(
        {
            "fanin_recall": 1.4,           # clamps to 1.0
            "entity_recall": -0.2,          # clamps to 0.0
            "godclass_recall": 0.75,
            "count_plausibility": "bad",    # non-float -> None
            "hallucinated_items": ["IMadeUpService"],
            "notes": "one miss",
        }
    )
    assert out["fanin_recall"] == 1.0
    assert out["entity_recall"] == 0.0
    assert out["godclass_recall"] == 0.75
    assert out["count_plausibility"] is None
    assert out["hallucinated_items"] == ["IMadeUpService"]
    assert out["notes"] == "one miss"
