from __future__ import annotations

from roslyn_abtest.diffmetrics import _BOM_MOJIBAKE, _BOM_UNICODE, compute_diff_hygiene
from roslyn_abtest.runner import count_diff_loc


def _diff(*lines: str) -> str:
    return "\n".join(lines)


_HEADER = ("diff --git a/F.cs b/F.cs", "--- a/F.cs", "+++ b/F.cs", "@@ -1,9 +1,9 @@")


def test_compute_diff_hygiene_empty_is_all_zero() -> None:
    out = compute_diff_hygiene("")
    assert out == {
        "diff_loc_semantic": 0,
        "diff_churn_ratio": 0.0,
        "diff_bom_stripped_files": 0,
        "diff_rewritten_files": 0,
    }


def test_compute_diff_hygiene_pure_rename_hunk_has_zero_churn() -> None:
    diff = _diff(
        *_HEADER,
        "-    private readonly IAffiliateService _svc;",
        "+    private readonly IAffiliateManagementService _svc;",
        " // unchanged context line",
    )
    out = compute_diff_hygiene(diff)
    # The removed and added lines differ, so nothing is resurrected: all raw is semantic.
    assert out["diff_churn_ratio"] == 0.0
    assert out["diff_loc_semantic"] == 2
    assert out["diff_bom_stripped_files"] == 0
    assert out["diff_rewritten_files"] == 0


def _bom_rewrite(bom: str) -> str:
    # 25-line whole-file rewrite whose ONLY change is the first line losing its BOM;
    # every other line is re-added verbatim -> ~total churn + one stripped BOM.
    removed = [f"-{bom}using System;"] + [f"-    var x{i} = {i};" for i in range(24)]
    added = ["+using System;"] + [f"+    var x{i} = {i};" for i in range(24)]
    return _diff(*_HEADER, *removed, *added)


def test_compute_diff_hygiene_bom_strip_rewrite_unicode_form() -> None:
    out = compute_diff_hygiene(_bom_rewrite(_BOM_UNICODE))
    assert out["diff_churn_ratio"] >= 0.95
    assert out["diff_bom_stripped_files"] == 1
    assert out["diff_rewritten_files"] == 1
    assert out["diff_loc_semantic"] == 0


def test_compute_diff_hygiene_bom_strip_rewrite_mojibake_form() -> None:
    # Legacy diffs (captured before the run_git encoding fix) carry the cp1252 mojibake BOM.
    out = compute_diff_hygiene(_bom_rewrite(_BOM_MOJIBAKE))
    assert out["diff_churn_ratio"] >= 0.95
    assert out["diff_bom_stripped_files"] == 1


def test_compute_diff_hygiene_crlf_only_rewrite_churns_out() -> None:
    removed = [f"-    line {i}" for i in range(25)]
    added = [f"+    line {i}\r" for i in range(25)]  # CRLF: a trailing CR on each added line
    out = compute_diff_hygiene(_diff(*_HEADER, *removed, *added))
    assert out["diff_churn_ratio"] >= 0.95
    assert out["diff_bom_stripped_files"] == 0
    assert out["diff_rewritten_files"] == 1


def test_compute_diff_hygiene_trailing_whitespace_matches_after_rstrip() -> None:
    # Only trailing whitespace differs -> churn (resurrection), not semantic change.
    diff = _diff(*_HEADER, "-    return x;", "+    return x;   ")
    out = compute_diff_hygiene(diff)
    assert out["diff_churn_ratio"] == 1.0
    assert out["diff_loc_semantic"] == 0


def test_compute_diff_hygiene_mixed_partial_churn() -> None:
    # 2 removed + 2 added, one line resurrected verbatim -> churn 2, semantic 2, ratio 0.5.
    diff = _diff(*_HEADER, "-alpha", "-beta", "+alpha", "+GAMMA")
    out = compute_diff_hygiene(diff)
    assert out["diff_churn_ratio"] == 0.5
    assert out["diff_loc_semantic"] == 2
    assert out["diff_rewritten_files"] == 0  # raw 4 < 20-line rewrite floor


def test_compute_diff_hygiene_raw_equals_semantic_plus_churn_matches_count_diff_loc() -> None:
    diff = _diff(*_HEADER, " ctx", "-old", "+new", "+extra")
    out = compute_diff_hygiene(diff)
    raw = count_diff_loc(diff)
    churn = raw - out["diff_loc_semantic"]
    # The invariant the backfill relies on: raw == semantic + churn == count_diff_loc.
    assert raw == 3
    assert churn == 0  # old != new; extra is unmatched
    assert out["diff_loc_semantic"] == 3


def test_compute_diff_hygiene_two_files_sum_independently() -> None:
    # A clean rename in file A + a whole-file rewrite in file B -> per-file churn is
    # summed, not pooled: only B's 40 lines resurrect.
    b_removed = [f"-B line {i}" for i in range(20)]
    b_added = [f"+B line {i}" for i in range(20)]
    diff = _diff(
        "diff --git a/A.cs b/A.cs", "--- a/A.cs", "+++ b/A.cs", "@@ -1,1 +1,1 @@",
        "-old name", "+new name",
        "diff --git a/B.cs b/B.cs", "--- a/B.cs", "+++ b/B.cs", "@@ -1,20 +1,20 @@",
        *b_removed, *b_added,
    )
    out = compute_diff_hygiene(diff)
    # A contributes 2 raw / 0 churn; B contributes 40 raw / 40 churn.
    assert out["diff_loc_semantic"] == 2
    assert out["diff_rewritten_files"] == 1  # only B crosses the rewrite threshold
    assert out["diff_churn_ratio"] == round(40 / 42, 4)
