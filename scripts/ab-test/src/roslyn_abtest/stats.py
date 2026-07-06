"""Statistical tests on paired metric samples: bootstrap CI on the mean, paired
t-test, Wilcoxon signed-rank, paired Cohen's d. Inputs are matched per-rep value
lists from `analyze.collect_raw_values`. All functions raise `ValueError` on
length mismatch or n < 2 — no silent NaN fallback; the caller renders 'n/a'."""
from __future__ import annotations

import numpy as np
from scipy import stats  # type: ignore[import-untyped]


def bootstrap_ci(
    values: list[float], n_boot: int = 10_000, alpha: float = 0.05, seed: int = 42
) -> tuple[float, float]:
    """Percentile bootstrap CI on the mean of values."""
    if len(values) < 2:
        raise ValueError(f"bootstrap_ci needs at least 2 values; got {len(values)}")
    rng = np.random.default_rng(seed=seed)
    arr = np.asarray(values, dtype=float)
    n = len(arr)
    boot_means = np.empty(n_boot)
    for i in range(n_boot):
        boot_means[i] = rng.choice(arr, size=n, replace=True).mean()
    lo = float(np.percentile(boot_means, 100 * alpha / 2))
    hi = float(np.percentile(boot_means, 100 * (1 - alpha / 2)))
    return lo, hi


def paired_t_test(a: list[float], b: list[float]) -> tuple[float, float]:
    """Paired-sample t-test. Returns (t_statistic, p_value)."""
    if len(a) != len(b):
        raise ValueError(f"paired_t_test length mismatch: {len(a)} vs {len(b)}")
    if len(a) < 2:
        raise ValueError(f"paired_t_test needs at least 2 pairs; got {len(a)}")
    result = stats.ttest_rel(a, b)
    return float(result.statistic), float(result.pvalue)


def wilcoxon_signed_rank(a: list[float], b: list[float]) -> tuple[float, float]:
    """Wilcoxon signed-rank test on paired samples. Returns (statistic, p_value)."""
    if len(a) != len(b):
        raise ValueError(f"wilcoxon_signed_rank length mismatch: {len(a)} vs {len(b)}")
    if len(a) < 2:
        raise ValueError(f"wilcoxon_signed_rank needs at least 2 pairs; got {len(a)}")
    result = stats.wilcoxon(a, b)
    return float(result.statistic), float(result.pvalue)


def cohens_d(a: list[float], b: list[float]) -> float:
    """Paired Cohen's d effect size: mean(a-b) / std(a-b, ddof=1)."""
    if len(a) != len(b):
        raise ValueError(f"cohens_d length mismatch: {len(a)} vs {len(b)}")
    if len(a) < 2:
        raise ValueError(f"cohens_d needs at least 2 pairs; got {len(a)}")
    diffs = np.asarray(a, dtype=float) - np.asarray(b, dtype=float)
    std = float(np.std(diffs, ddof=1))
    if std == 0:
        raise ValueError(
            "cohens_d undefined: standard deviation of paired differences is zero"
        )
    return float(np.mean(diffs)) / std
