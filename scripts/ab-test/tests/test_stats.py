from __future__ import annotations

import math

import pytest

from roslyn_abtest.stats import bootstrap_ci, cohens_d, paired_t_test, wilcoxon_signed_rank


def test_bootstrap_ci_returns_finite_bounds_bracketing_mean() -> None:
    values = [1.0, 2.0, 3.0, 4.0, 5.0]
    lo, hi = bootstrap_ci(values, n_boot=200)
    assert math.isfinite(lo)
    assert math.isfinite(hi)
    mean = sum(values) / len(values)
    assert lo <= mean <= hi


def test_bootstrap_ci_is_deterministic_under_default_seed() -> None:
    # Determinism depends on the seed, not n_boot — smaller iteration count
    # proves the same property in a fraction of the time.
    values = [1.0, 2.0, 3.0, 4.0, 5.0]
    assert bootstrap_ci(values, n_boot=200) == bootstrap_ci(values, n_boot=200)


def test_bootstrap_ci_single_value_raises() -> None:
    with pytest.raises(ValueError):
        bootstrap_ci([1.0])


def test_paired_t_test_length_mismatch_raises() -> None:
    with pytest.raises(ValueError):
        paired_t_test([1.0, 2.0], [1.0])


def test_paired_t_test_singleton_raises() -> None:
    with pytest.raises(ValueError):
        paired_t_test([1.0], [2.0])


def test_paired_t_test_returns_finite_pair_on_real_input() -> None:
    t, p = paired_t_test([1.0, 2.0, 3.0], [2.0, 3.0, 4.5])
    assert math.isfinite(t)
    assert math.isfinite(p)


def test_wilcoxon_signed_rank_length_mismatch_raises() -> None:
    with pytest.raises(ValueError):
        wilcoxon_signed_rank([1.0, 2.0], [1.0])


def test_wilcoxon_signed_rank_singleton_raises() -> None:
    with pytest.raises(ValueError):
        wilcoxon_signed_rank([1.0], [2.0])


def test_cohens_d_sign_is_positive_when_a_greater_than_b() -> None:
    # Diffs are varied (1.0, 1.5, 2.5) so std(diffs) > 0; mean > 0 → d > 0.
    d = cohens_d([2.0, 3.0, 4.5], [1.0, 1.5, 2.0])
    assert d > 0


def test_cohens_d_sign_flips_when_arguments_reversed() -> None:
    d_positive = cohens_d([2.0, 3.0, 4.5], [1.0, 1.5, 2.0])
    d_negative = cohens_d([1.0, 1.5, 2.0], [2.0, 3.0, 4.5])
    assert d_positive == pytest.approx(-d_negative)


def test_cohens_d_length_mismatch_raises() -> None:
    with pytest.raises(ValueError):
        cohens_d([1.0, 2.0], [1.0])


def test_cohens_d_singleton_raises() -> None:
    with pytest.raises(ValueError):
        cohens_d([1.0], [2.0])


def test_cohens_d_identical_paired_diffs_raises() -> None:
    # All pairs have the same delta (1.0), so std(diffs)=0 — undefined.
    with pytest.raises(ValueError):
        cohens_d([2.0, 3.0, 4.0], [1.0, 2.0, 3.0])
