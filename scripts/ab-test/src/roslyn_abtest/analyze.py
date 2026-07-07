#!/usr/bin/env python3
"""Aggregate A/B test run JSONs into a summary.md.

Usage:
    python analyze.py                     # analyze the most recent results/<timestamp>/
    python analyze.py --timestamp 2026... # analyze a specific run
"""
from __future__ import annotations

import argparse
import json
import math
import re
import statistics
import sys
from collections import defaultdict
from pathlib import Path

from .paths import RESULTS_DIR
from .stats import bootstrap_ci, cohens_d, wilcoxon_signed_rank

# The name pattern cli.py stamps on every run dir (datetime ...strftime). The
# auto-default resolver restricts to these so a manually-named dir (e.g.
# `merged-impact-2026-06-02`) can't sort above the timestamps and hijack the default.
_TIMESTAMP_DIR_RE = re.compile(r"^\d{8}T\d{6}Z$")

_RAW_METRIC_KEYS = (
    "wall_seconds",
    "total_cost_usd",
    "num_turns",
    "tool_call_count",
    "diff_loc",
)


def load_runs(timestamp_dir: Path) -> list[dict]:
    """Load run JSONs from a results dir, excluding transcripts and judgments.

    `*.judgment.json` share the `*.json` glob but carry a `judge_model` key and lack run
    metrics (wall_seconds etc.) — including one would crash format_table and inflate `n`.
    Excluded by both name and the `judge_model` guard (the same guard run_judge uses),
    so re-running write_summary over an already-judged dir (as `backfill` does) is safe."""
    runs = []
    for p in sorted(timestamp_dir.glob("*.json")):
        if p.name.endswith((".transcript.json", ".judgment.json")):
            continue
        record = json.loads(p.read_text(encoding="utf-8"))
        if "judge_model" in record:
            continue
        runs.append(record)
    return runs


def _mean_present(values: list) -> float:
    """Mean of non-None values; empty list -> 0.0. Skipping None (rather than
    coercing it to 0) keeps aggregate's means consistent with collect_raw_values,
    so errored reps don't pull cost/turn averages down toward zero in one table
    while being excluded from the paired-stats table next to it."""
    filtered = [v for v in values if v is not None]
    return statistics.mean(filtered) if filtered else 0.0


def mean(values: list[float]) -> float:
    return statistics.mean(values) if values else 0.0


def _usage_mean(items: list[dict], key: str) -> float:
    return _mean_present([(i.get("usage") or {}).get(key) for i in items])


def _build_passed(r: dict) -> bool | None:
    """Per-run build verdict. Single source of truth used by both aggregate's
    pass-rate and format_table's PASS/FAIL cell so the two can't drift on the
    grandfathered-fallback rule for old JSONs missing `build_passed`."""
    if "build_passed" in r:
        return bool(r["build_passed"])
    rc = r.get("build_exit_code")
    if rc is None:
        return None
    expected = r.get("build_expected_exit", 0)
    return rc == expected


def _pick_anchor(arms: list[str]) -> str:
    """Baseline arm for delta comparisons. Prefers any arm whose name contains
    `baseline` (the project's naming convention for the no-MCP control arm);
    falls back to alphabetical first when no such arm is present."""
    baselines = [a for a in arms if "baseline" in a]
    return baselines[0] if baselines else sorted(arms)[0]


def _pct(x: float | None, y: float | None) -> str:
    if x is None or y is None:
        return "-"
    if y == 0:
        return "n/a" if x == 0 else "+inf%"
    return f"{((x - y) / y * 100):+.1f}%"


def _residual_cell(s: dict) -> str | None:
    """Compact residual summary, or None if no residual data is present.
    Renders the new generic residual_counts dict (one token-residual key per
    entry) when populated; falls back to the legacy avg_rename_residual alias
    for grandfathered runs that pre-date the generic verifier."""
    rc = s.get("residual_counts") or {}
    if rc:
        return ", ".join(
            f"{k.removesuffix('_residual_count')}={v:.1f}"
            for k, v in sorted(rc.items())
        )
    legacy = s.get("avg_rename_residual")
    if legacy is not None:
        return f"{legacy:.1f}"
    return None


def aggregate(runs: list[dict]) -> dict:
    groups: dict[tuple[str, str], list[dict]] = defaultdict(list)
    for r in runs:
        groups[(r["task"], r["arm"])].append(r)

    # Generic residual-key discovery: any *_residual_count field emitted by a
    # token-residual verifier is rolled into `residual_counts`. The legacy
    # `avg_rename_residual` alias below is grandfathered from old runs only
    # (literal key, no alphabetical fallback — with two residual keys whose
    # semantics differ, picking either as "the residual" is misleading).
    all_residual_keys = sorted({
        k for r in runs for k in r if k.endswith("_residual_count")
    })

    summary: dict = {}
    for (task, arm), items in groups.items():
        legacy_residuals = [
            i["rename_residual_count"] for i in items if "rename_residual_count" in i
        ]
        build_verdicts = [v for v in (_build_passed(i) for i in items) if v is not None]
        build_pass_rate = (
            sum(1 for v in build_verdicts if v) / len(build_verdicts)
            if build_verdicts else None
        )
        residual_means = {
            key: _mean_present([i.get(key) for i in items])
            for key in all_residual_keys
            if any(key in i for i in items)
        }
        # Diff-hygiene means are None (not 0.0) for a cell whose runs pre-date the
        # metric, so format_aggregate can gate the columns on presence like Residual.
        hygiene_present = any("diff_loc_semantic" in i for i in items)
        summary[(task, arm)] = {
            "n": len(items),
            "avg_wall_s": _mean_present([i.get("wall_seconds") for i in items]),
            "avg_input_tokens": _usage_mean(items, "input_tokens"),
            "avg_output_tokens": _usage_mean(items, "output_tokens"),
            "avg_cache_read": _usage_mean(items, "cache_read_input_tokens"),
            "avg_cache_creation": _usage_mean(items, "cache_creation_input_tokens"),
            "avg_cost_usd": _mean_present([i.get("total_cost_usd") for i in items]),
            "avg_turns": _mean_present([i.get("num_turns") for i in items]),
            "avg_tool_calls": _mean_present([i.get("tool_call_count") for i in items]),
            "avg_diff_loc": _mean_present([i.get("diff_loc") for i in items]),
            "build_pass_rate": build_pass_rate,
            "error_rate": sum(1 for i in items if i.get("is_error")) / max(len(items), 1),
            "avg_rename_residual": mean(legacy_residuals) if legacy_residuals else None,
            "residual_counts": residual_means,
            "avg_diff_loc_semantic": (
                _mean_present([i.get("diff_loc_semantic") for i in items])
                if hygiene_present else None
            ),
            "avg_diff_churn_ratio": (
                _mean_present([i.get("diff_churn_ratio") for i in items])
                if hygiene_present else None
            ),
        }
    return summary


def collect_raw_values(
    runs: list[dict],
) -> dict[tuple[str, str], dict[int, dict[str, float]]]:
    """Index runs by (task, arm) -> {rep: {metric_key: value}} for paired-stat lookups.

    Rep-keyed (not positional) so that an errored rep in one arm drops only that
    pair, not the surviving reps. Skips missing metric values; never fabricates."""
    indexed: dict[tuple[str, str], dict[int, dict[str, float]]] = defaultdict(dict)
    for r in runs:
        per_metric: dict[str, float] = {}
        for mk in _RAW_METRIC_KEYS:
            value = r.get(mk)
            if value is None:
                continue
            per_metric[mk] = float(value)
        if per_metric:
            indexed[(r["task"], r["arm"])][r["rep"]] = per_metric
    return indexed


def format_table(runs: list[dict]) -> str:
    hdr = (
        "| Task | Arm | Rep | Wall(s) | Turns | In tok | Out tok | "
        "Cache rd | Cache wr | $ | Tools | Diff LOC | Sem LOC | Churn% | Build | Stop |"
    )
    sep = (
        "|------|-----|-----|---------|-------|--------|---------|"
        "----------|----------|------|-------|----------|---------|--------|-------|------|"
    )
    lines = [hdr, sep]
    for r in sorted(runs, key=lambda x: (x["task"], x["arm"], x["rep"])):
        u = r.get("usage") or {}
        passed = _build_passed(r)
        build_cell = "-" if passed is None else ("PASS" if passed else "FAIL")
        # Per-cell .get so runs that pre-date diff-hygiene render `-` rather than error.
        sem = r.get("diff_loc_semantic")
        churn = r.get("diff_churn_ratio")
        sem_cell = str(sem) if sem is not None else "-"
        churn_cell = f"{churn * 100:.0f}%" if churn is not None else "-"
        lines.append(
            f"| {r['task']} | {r['arm']} | {r['rep']} | "
            f"{r['wall_seconds']:.1f} | {r.get('num_turns') or '-'} | "
            f"{u.get('input_tokens') or 0} | {u.get('output_tokens') or 0} | "
            f"{u.get('cache_read_input_tokens') or 0} | "
            f"{u.get('cache_creation_input_tokens') or 0} | "
            f"{(r.get('total_cost_usd') or 0):.3f} | "
            f"{r['tool_call_count']} | {r['diff_loc']} | {sem_cell} | {churn_cell} | "
            f"{build_cell} | "
            f"{r.get('stop_reason') or '-'} |"
        )
    return "\n".join(lines)


def format_aggregate(summary: dict) -> str:
    has_hygiene = any(s.get("avg_diff_loc_semantic") is not None for s in summary.values())
    has_residual = any(_residual_cell(s) is not None for s in summary.values())
    columns = [
        "Task", "Arm", "n", "Wall(s)", "Turns", "In tok", "Out tok",
        "Cache rd", "$", "Tools", "Diff LOC", "Build %", "Err %",
    ]
    if has_hygiene:
        columns += ["Sem LOC", "Churn%"]
    if has_residual:
        columns.append("Residual")
    lines = [
        "| " + " | ".join(columns) + " |",
        "|" + "|".join(["------"] * len(columns)) + "|",
    ]
    for (task, arm), s in sorted(summary.items()):
        bpr = s.get("build_pass_rate")
        build_pct = f"{bpr * 100:.0f}%" if bpr is not None else "-"
        cells = [
            task, arm, str(s["n"]),
            f"{s['avg_wall_s']:.1f}", f"{s['avg_turns']:.1f}",
            f"{s['avg_input_tokens']:.0f}", f"{s['avg_output_tokens']:.0f}",
            f"{s['avg_cache_read']:.0f}", f"{s['avg_cost_usd']:.3f}",
            f"{s['avg_tool_calls']:.1f}", f"{s['avg_diff_loc']:.0f}",
            build_pct, f"{s['error_rate'] * 100:.0f}%",
        ]
        if has_hygiene:
            sem = s.get("avg_diff_loc_semantic")
            churn = s.get("avg_diff_churn_ratio")
            cells.append(f"{sem:.0f}" if sem is not None else "-")
            cells.append(f"{churn * 100:.0f}%" if churn is not None else "-")
        if has_residual:
            cells.append(_residual_cell(s) or "-")
        lines.append("| " + " | ".join(cells) + " |")
    return "\n".join(lines)


def format_per_task_pivot(summary: dict) -> str:
    """Per-task pivot with one mean column per present arm and one delta column
    per non-anchor present arm. Anchor is chosen per-task via `_pick_anchor`,
    matching `format_statistical_significance` so the two sections never
    disagree on the baseline. Each task's heading names its anchor so the
    reader doesn't have to infer it from column labels."""
    tasks = sorted({task for task, _ in summary})
    metrics = [
        ("Cost ($)", "avg_cost_usd", ".3f"),
        ("Wall (s)", "avg_wall_s", ".1f"),
        ("Turns", "avg_turns", ".1f"),
        ("Input tok", "avg_input_tokens", ".0f"),
        ("Output tok", "avg_output_tokens", ".0f"),
        ("Cache rd", "avg_cache_read", ".0f"),
        ("Cache wr", "avg_cache_creation", ".0f"),
        ("Tool calls", "avg_tool_calls", ".1f"),
        ("Diff LOC", "avg_diff_loc", ".0f"),
        ("Sem LOC", "avg_diff_loc_semantic", ".0f"),
        ("Churn %", "avg_diff_churn_ratio", ".0%"),
        ("Build %", "build_pass_rate", ".0%"),
    ]

    out = []
    for task in tasks:
        present = sorted(a for (t, a) in summary if t == task)
        if len(present) < 2:
            continue
        anchor = _pick_anchor(present)
        delta_arms = [a for a in present if a != anchor]
        out.append(f"\n### {task} (anchor: `{anchor}`)\n")
        headers = ["Metric", *present, *[f"{a} vs {anchor}" for a in delta_arms]]
        out.append("| " + " | ".join(headers) + " |")
        out.append("|" + "|".join(["--------"] * len(headers)) + "|")
        for label, key, fmt in metrics:
            cells = {a: summary[(task, a)].get(key) for a in present}
            cell_strs = [
                format(cells[a], fmt) if cells[a] is not None else "-" for a in present
            ]
            anchor_val = cells[anchor]
            delta_strs = [_pct(cells[a], anchor_val) for a in delta_arms]
            row = [label, *cell_strs, *delta_strs]
            out.append("| " + " | ".join(row) + " |")
    return "\n".join(out) if out else "\n(no per-task cells present)"


def format_statistical_significance(
    raw: dict[tuple[str, str], dict[int, dict[str, float]]],
    primary_metric: str = "total_cost_usd",
) -> str:
    """Per-task tables of arm Mean [95% CI], plus Δ%, Wilcoxon p, and Cohen's d
    against the per-task anchor chosen via `_pick_anchor` (same picker as
    format_per_task_pivot). Pairing is by rep index so that an errored rep in
    either arm drops only that pair."""
    tasks = sorted({task for task, _ in raw})
    if not tasks:
        return "(no data for statistical tests)"

    out = []
    for task in tasks:
        arms_for_task = sorted({arm for (t, arm) in raw if t == task})
        if not arms_for_task:
            continue
        anchor = _pick_anchor(arms_for_task)
        anchor_reps = raw[(task, anchor)]

        out.append(
            f"\n### {task} (anchor: `{anchor}`, primary metric: `{primary_metric}`)\n"
        )
        out.append("| Arm | n | Mean | 95% CI | Δ% vs anchor | Wilcoxon p | Cohen's d |")
        out.append("|-----|---|------|--------|--------------|------------|-----------|")

        for arm in arms_for_task:
            arm_reps = raw[(task, arm)]
            vals = [
                reps[primary_metric]
                for reps in arm_reps.values()
                if primary_metric in reps
            ]
            n = len(vals)

            if n == 0:
                out.append(f"| `{arm}` | 0 | - | n/a | - | - | - |")
                continue

            mean_val = sum(vals) / n
            mean_str = f"{mean_val:.4f}"

            if n >= 2:
                try:
                    lo, hi = bootstrap_ci(vals)
                    ci_str = f"[{lo:.4f}, {hi:.4f}]"
                except ValueError:
                    ci_str = "n/a"
            else:
                ci_str = "n/a"

            if arm == anchor:
                out.append(f"| `{arm}` | {n} | {mean_str} | {ci_str} | — | — | — |")
                continue

            common = sorted(set(anchor_reps) & set(arm_reps))
            pairs = [
                (arm_reps[r][primary_metric], anchor_reps[r][primary_metric])
                for r in common
                if primary_metric in arm_reps[r] and primary_metric in anchor_reps[r]
            ]
            paired_n = len(pairs)

            if paired_n < 2:
                out.append(
                    f"| `{arm}` | {n} | {mean_str} | {ci_str} | "
                    f"n={paired_n} (too few for paired tests) | — | — |"
                )
                continue

            paired_arm = [p[0] for p in pairs]
            paired_anchor = [p[1] for p in pairs]
            mean_arm_paired = sum(paired_arm) / paired_n
            mean_anchor_paired = sum(paired_anchor) / paired_n
            delta_str = _pct(mean_arm_paired, mean_anchor_paired)

            try:
                _, p_value = wilcoxon_signed_rank(paired_arm, paired_anchor)
                p_str = "n/a" if math.isnan(p_value) else f"{p_value:.4f}"
            except ValueError:
                p_str = "n/a"

            try:
                d = cohens_d(paired_arm, paired_anchor)
                d_str = "n/a" if math.isnan(d) else f"{d:+.3f}"
            except ValueError:
                d_str = "n/a"

            out.append(
                f"| `{arm}` | {n} | {mean_str} | {ci_str} | "
                f"{delta_str} | {p_str} | {d_str} |"
            )

    return "\n".join(out) if out else "(no statistical data)"


def format_pollution_summary(runs: list[dict]) -> str:
    polluted = [r for r in runs if (r.get("formatter_pollution_hits") or 0) > 0]
    if not polluted:
        return "No formatter pollution detected across any run."
    lines = [f"**{len(polluted)} run(s) flagged for formatter pollution:**", ""]
    for r in sorted(polluted, key=lambda x: (x["task"], x["arm"], x["rep"])):
        samples = r.get("formatter_pollution_samples") or []
        lines.append(
            f"- `{r['task']} | {r['arm']} | rep {r['rep']}` — "
            f"{r['formatter_pollution_hits']} hit(s)"
        )
        for s in samples:
            lines.append(f"  - `{s[:120]}`")
    return "\n".join(lines)


def format_tool_histograms(runs: list[dict]) -> str:
    from collections import Counter
    groups: dict[tuple[str, str], list[dict]] = defaultdict(list)
    for r in runs:
        groups[(r["task"], r["arm"])].append(r)
    out = []
    for (task, arm), items in sorted(groups.items()):
        combined: Counter[str] = Counter()
        for i in items:
            combined.update(i.get("tool_histogram") or {})
        if not combined:
            continue
        out.append(f"\n### {task} | {arm}")
        for name, count in combined.most_common():
            out.append(f"- `{name}`: {count}")
    return "\n".join(out)


def write_summary(timestamp_dir: Path) -> Path:
    runs = load_runs(timestamp_dir)
    if not runs:
        sys.exit(f"No runs found in {timestamp_dir}")
    summary = aggregate(runs)
    raw = collect_raw_values(runs)

    md = [
        f"# A/B Test Summary — {timestamp_dir.name}",
        "",
        f"Total runs: **{len(runs)}**  ",
        f"Distinct (task, arm) cells: **{len(summary)}**",
        "",
        "## Per-run results",
        "",
        format_table(runs),
        "",
        "## Aggregates",
        "",
        format_aggregate(summary),
        "",
        "## Per-task pivot",
        "",
        format_per_task_pivot(summary),
        "",
        "## Statistical significance (paired, primary metric: total_cost_usd)",
        "",
        format_statistical_significance(raw),
        "",
        "## Formatter pollution check",
        "",
        format_pollution_summary(runs),
        "",
        "## Tool-call histograms",
        format_tool_histograms(runs),
        "",
        "## Qualitative notes (fill in by hand after reviewing transcripts and diffs)",
        "",
        "### Task 1 — Feature add",
        "- Did Arm A follow nopCommerce conventions more closely? Evidence:",
        "- Did Arm B thrash on grep/read cycles? Evidence:",
        "- Where were the idiomaticity differences most visible?",
        "",
        "### Task 2 — Audit",
        "- Which arm's rankings were more defensible? Spot-check a few.",
        "- Did Arm B reconstruct the call graph accurately?",
        "",
        "## Verdict",
        "",
        "Based on the numbers above, would the user be better off without the MCP server?",
        "- **Token delta** (Arm A - Arm B, per task): ",
        "- **Time delta** (Arm A - Arm B, per task): ",
        "- **Completion rate delta**: ",
        "- **Quality delta** (subjective, 1-5): ",
        "",
    ]
    out_path = timestamp_dir / "summary.md"
    out_path.write_text("\n".join(md), encoding="utf-8")
    return out_path


def _all_timestamp_dirs() -> list[Path]:
    """Every results/<timestamp>/ dir (name matches the strftime pattern), oldest first.

    Manually-named dirs (e.g. `merged-*`) are excluded — same restriction as the
    auto-default resolver; back-fill one of those explicitly with --timestamp."""
    if not RESULTS_DIR.exists():
        return []
    return sorted(
        (p for p in RESULTS_DIR.iterdir() if p.is_dir() and _TIMESTAMP_DIR_RE.match(p.name)),
        key=lambda p: p.name,
    )


def _resolve_timestamp_dir(timestamp: str | None) -> Path:
    if timestamp:
        path = RESULTS_DIR / timestamp
        if not path.exists():
            sys.exit(f"Results directory not found: {path}")
        return path
    candidates = _all_timestamp_dirs()
    if not candidates:
        sys.exit("No timestamped results dirs found (pass --timestamp for a custom-named one).")
    return candidates[-1]  # sorted oldest-first, so the last is the most recent


def backfill_dir(timestamp_dir: Path) -> int:
    """Recompute diff-hygiene fields for every run JSON with a sibling .diff (idempotent).

    Skips transcripts, judgments, and any record carrying a `judge_model` key — the same
    exclusion load_runs / run_judge use. Returns the count of run JSONs updated."""
    from .diffmetrics import compute_diff_hygiene

    updated = 0
    for json_path in sorted(timestamp_dir.glob("*.json")):
        if json_path.name.endswith((".transcript.json", ".judgment.json")):
            continue
        try:
            record = json.loads(json_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            continue
        if "judge_model" in record:
            continue
        diff_path = json_path.with_suffix(".diff")
        if not diff_path.is_file():
            continue
        diff_text = diff_path.read_text(encoding="utf-8", errors="replace")
        record.update(compute_diff_hygiene(diff_text))
        json_path.write_text(json.dumps(record, indent=2, default=str), encoding="utf-8")
        updated += 1
    return updated


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--timestamp")
    args = parser.parse_args()
    write_summary(_resolve_timestamp_dir(args.timestamp))


if __name__ == "__main__":
    main()
