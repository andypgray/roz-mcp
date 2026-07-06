from __future__ import annotations

from roslyn_abtest.analyze import aggregate, collect_raw_values


def _base_run(**overrides: object) -> dict:
    """Minimal run dict that aggregate() can consume; override fields per-test."""
    run: dict = {
        "task": "01-feature-add",
        "arm": "arm-a",
        "rep": 1,
        "wall_seconds": 10.0,
        "total_cost_usd": 0.05,
        "num_turns": 4,
        "tool_call_count": 6,
        "diff_loc": 20,
        "usage": {
            "input_tokens": 1000,
            "output_tokens": 500,
            "cache_read_input_tokens": 200,
            "cache_creation_input_tokens": 100,
        },
        "is_error": False,
    }
    run.update(overrides)
    return run


def test_aggregate_empty_returns_empty_dict() -> None:
    assert aggregate([]) == {}


def test_aggregate_single_run_produces_one_cell_with_n_equal_to_one() -> None:
    runs = [_base_run()]
    summary = aggregate(runs)
    key = ("01-feature-add", "arm-a")
    assert list(summary.keys()) == [key]
    cell = summary[key]
    assert cell["n"] == 1
    assert cell["avg_wall_s"] == 10.0
    assert cell["avg_cost_usd"] == 0.05
    assert cell["avg_turns"] == 4
    assert cell["avg_tool_calls"] == 6
    assert cell["avg_diff_loc"] == 20
    assert cell["avg_input_tokens"] == 1000


def test_aggregate_two_reps_means_are_arithmetic() -> None:
    runs = [
        _base_run(rep=1, wall_seconds=10.0, total_cost_usd=0.10),
        _base_run(rep=2, wall_seconds=20.0, total_cost_usd=0.30),
    ]
    cell = aggregate(runs)[("01-feature-add", "arm-a")]
    assert cell["n"] == 2
    assert cell["avg_wall_s"] == 15.0
    assert cell["avg_cost_usd"] == 0.20


def test_aggregate_discovers_arbitrary_residual_keys() -> None:
    runs = [
        _base_run(rep=1, foo_residual_count=4, bar_residual_count=10),
        _base_run(rep=2, foo_residual_count=6, bar_residual_count=20),
    ]
    cell = aggregate(runs)[("01-feature-add", "arm-a")]
    residuals = cell["residual_counts"]
    assert set(residuals.keys()) == {"foo_residual_count", "bar_residual_count"}
    assert residuals["foo_residual_count"] == 5
    assert residuals["bar_residual_count"] == 15


def test_aggregate_grandfathers_legacy_rename_residual() -> None:
    runs = [
        _base_run(rep=1, rename_residual_count=3),
        _base_run(rep=2, rename_residual_count=7),
    ]
    cell = aggregate(runs)[("01-feature-add", "arm-a")]
    # Grandfather alias for legacy runs that pre-date generic verifier.
    assert cell["avg_rename_residual"] == 5


def test_aggregate_skips_none_when_computing_means() -> None:
    runs = [
        _base_run(rep=1, total_cost_usd=0.10),
        _base_run(rep=2, total_cost_usd=None),
        _base_run(rep=3, total_cost_usd=0.30),
    ]
    cell = aggregate(runs)[("01-feature-add", "arm-a")]
    # Mean of present values is 0.20, not (0.10 + 0 + 0.30) / 3 = 0.133.
    assert cell["avg_cost_usd"] == 0.20


def test_aggregate_build_pass_rate_direct_field() -> None:
    runs = [
        _base_run(rep=1, build_passed=True),
        _base_run(rep=2, build_passed=False),
    ]
    cell = aggregate(runs)[("01-feature-add", "arm-a")]
    assert cell["build_pass_rate"] == 0.5


def test_aggregate_build_pass_rate_falls_back_to_exit_codes() -> None:
    runs = [
        _base_run(rep=1, build_exit_code=0, build_expected_exit=0),
        _base_run(rep=2, build_exit_code=1, build_expected_exit=0),
    ]
    cell = aggregate(runs)[("01-feature-add", "arm-a")]
    assert cell["build_pass_rate"] == 0.5


def test_aggregate_build_pass_rate_none_when_no_build_data() -> None:
    runs = [_base_run(rep=1), _base_run(rep=2)]
    cell = aggregate(runs)[("01-feature-add", "arm-a")]
    assert cell["build_pass_rate"] is None


def test_collect_raw_values_pairs_reps_by_index() -> None:
    runs = [
        _base_run(arm="arm-a", rep=1, wall_seconds=10.0),
        _base_run(arm="arm-a", rep=2, wall_seconds=20.0),
        _base_run(arm="arm-b", rep=1, wall_seconds=11.0),
        _base_run(arm="arm-b", rep=2, wall_seconds=22.0),
    ]
    raw = collect_raw_values(runs)
    assert raw[("01-feature-add", "arm-a")][1]["wall_seconds"] == 10.0
    assert raw[("01-feature-add", "arm-a")][2]["wall_seconds"] == 20.0
    assert raw[("01-feature-add", "arm-b")][1]["wall_seconds"] == 11.0
    assert raw[("01-feature-add", "arm-b")][2]["wall_seconds"] == 22.0


def test_collect_raw_values_skips_missing_metric_without_fabricating() -> None:
    runs = [
        _base_run(arm="arm-a", rep=1, wall_seconds=10.0),
        _base_run(arm="arm-a", rep=2, wall_seconds=None),
    ]
    raw = collect_raw_values(runs)
    cell = raw[("01-feature-add", "arm-a")]
    assert "wall_seconds" in cell[1]
    assert "wall_seconds" not in cell[2]


def test_collect_raw_values_errored_rep_in_one_arm_leaves_other_arm_intact() -> None:
    # Rep 1 of arm-a is missing every metric (errored before any was set).
    # Rep 1 of arm-b should still be present.
    runs = [
        {
            "task": "01-feature-add",
            "arm": "arm-a",
            "rep": 1,
            "is_error": True,
            "usage": None,
            "wall_seconds": None,
            "total_cost_usd": None,
            "num_turns": None,
            "tool_call_count": None,
            "diff_loc": None,
        },
        _base_run(arm="arm-b", rep=1, wall_seconds=15.0),
    ]
    raw = collect_raw_values(runs)
    # arm-a rep 1 had no metric values, so it should not appear in the index.
    assert ("01-feature-add", "arm-a") not in raw
    assert raw[("01-feature-add", "arm-b")][1]["wall_seconds"] == 15.0
