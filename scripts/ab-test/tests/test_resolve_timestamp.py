from __future__ import annotations

from pathlib import Path

import pytest

from roslyn_abtest.analyze import _resolve_timestamp_dir


def test_resolve_timestamp_dir_prefers_latest_run_over_custom_named(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    results = tmp_path / "results"
    results.mkdir()
    (results / "20260418T140806Z").mkdir()
    (results / "20260617T113722Z").mkdir()
    # A manually-named dir sorts ABOVE the timestamps ('m' > '2') — it must NOT be
    # auto-selected (the bug that sent `judge` at the wrong dir).
    (results / "merged-impact-2026-06-02").mkdir()
    monkeypatch.setattr("roslyn_abtest.analyze.RESULTS_DIR", results)
    assert _resolve_timestamp_dir(None).name == "20260617T113722Z"


def test_resolve_timestamp_dir_explicit_custom_named_is_allowed(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    results = tmp_path / "results"
    results.mkdir()
    (results / "merged-impact-2026-06-02").mkdir()
    monkeypatch.setattr("roslyn_abtest.analyze.RESULTS_DIR", results)
    # An explicit --timestamp can still target a custom-named dir.
    assert _resolve_timestamp_dir("merged-impact-2026-06-02").name == "merged-impact-2026-06-02"


def test_resolve_timestamp_dir_no_timestamp_dirs_exits(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    results = tmp_path / "results"
    results.mkdir()
    (results / "merged-impact-2026-06-02").mkdir()  # only custom-named dirs present
    monkeypatch.setattr("roslyn_abtest.analyze.RESULTS_DIR", results)
    with pytest.raises(SystemExit):
        _resolve_timestamp_dir(None)
