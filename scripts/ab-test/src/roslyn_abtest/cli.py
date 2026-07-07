#!/usr/bin/env python3
"""A/B test harness CLI: `roslyn-abtest run` and `roslyn-abtest analyze`."""
from __future__ import annotations

import argparse
import asyncio
import os
import random
import sys
from datetime import datetime, UTC
from pathlib import Path

from .arms import load_arm_configs
from .paths import PATCHES_DIR, RESULTS_DIR
from .runner import run_one
from .tasks import load_tasks


def _preflight_setup_patches(task_metas: list[tuple[Path, dict]]) -> None:
    """Fail the sweep early on a missing or stale planted patch — same discipline as
    the `.reference.md` oracles. Patch-file existence is always checked; `git apply
    --check` against the pinned SHA runs only when that fixture's clone is already
    cached (it isn't worth a network clone just to dry-run a patch)."""
    from .fixtures import get_fixture
    from .runner import run_git

    by_fixture: dict[str, list[tuple[Path, Path]]] = {}
    for task_path, meta in task_metas:
        rel = meta.get("setup_patch")
        if not rel:
            continue
        patch = PATCHES_DIR / Path(rel).name
        if not patch.is_file():
            sys.exit(f"{task_path.name}: setup_patch not found at {patch}")
        fixture = get_fixture(meta.get("fixture"))
        by_fixture.setdefault(fixture.name, []).append((task_path, patch))

    for fixture_name, items in by_fixture.items():
        fixture = get_fixture(fixture_name)
        if not fixture.solution_path.is_file():
            print(
                f"  (clone for {fixture_name} not cached yet — deferring "
                f"`git apply --check` to run time)",
                flush=True,
            )
            continue
        # --check doesn't modify the tree, so reset once to the clean SHA and
        # dry-run each patch against it independently.
        run_git(fixture.cache_dir, "reset", "--hard", fixture.sha)
        run_git(fixture.cache_dir, "clean", "-fdx", check=False)
        for task_path, patch in items:
            res = run_git(
                fixture.cache_dir,
                "apply", "--check", "--whitespace=nowarn", str(patch),
                check=False,
            )
            if res.returncode != 0:
                sys.exit(
                    f"{task_path.name}: setup_patch does not apply to "
                    f"{fixture.sha[:10]}:\n{res.stderr.strip()}"
                )


async def main_async(args: argparse.Namespace) -> None:
    # --task accepts several stems (or 'all'); aggregate preserving order and de-duping
    # so an overlapping selection (e.g. 'all' plus a stem) runs each task only once.
    seen: set[Path] = set()
    tasks: list[Path] = []
    for name in args.task:
        for t in load_tasks(name):
            if t not in seen:
                seen.add(t)
                tasks.append(t)
    configs = load_arm_configs(args.arms)

    # Parse + validate every task up-front so YAML/frontmatter errors fail the
    # sweep before any prepare_clone or model call burns budget. Cheap: each
    # task is a small markdown file. parse_task raises on missing/malformed
    # frontmatter; validate_verification_order raises on misordered verifiers.
    from .fixtures import get_fixture
    from .tasks import parse_task
    from .verification import validate_verification_order
    task_metas: list[tuple[Path, dict]] = []
    for t in tasks:
        metadata, _ = parse_task(t)
        validate_verification_order(list(metadata.get("verification") or []), t)
        get_fixture(metadata.get("fixture"))   # get_fixture exits on an unknown name
        task_metas.append((t, metadata))
    _preflight_setup_patches(task_metas)

    plan: list[tuple[dict, Path, int]] = [
        (c, t, r)
        for t in tasks
        for c in configs
        for r in range(1, args.reps + 1)
    ]

    random.seed(args.seed)
    random.shuffle(plan)

    timestamp = datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ")
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)
    (RESULTS_DIR / timestamp).mkdir(parents=True, exist_ok=True)

    print(f"Timestamp: {timestamp}", flush=True)
    print(f"Plan: {len(plan)} runs", flush=True)
    for c, t, r in plan:
        print(f"  {t.stem} | {c['name']} | rep {r}", flush=True)

    for i, (c, t, r) in enumerate(plan, 1):
        print(f"\n=== [{i}/{len(plan)}] {t.stem} | {c['name']} | rep {r} ===", flush=True)
        try:
            res = await run_one(c, t, r, timestamp, args.max_turns, args.max_budget_usd, args.model)
            build_rc = res.get("build_exit_code")
            print(
                f"  done: wall={res['wall_seconds']}s turns={res['num_turns']} "
                f"stop={res['stop_reason']} tools={res['tool_call_count']} "
                f"build_rc={build_rc if build_rc is not None else '-'} "
                f"diff_loc={res['diff_loc']}",
                flush=True,
            )
        except Exception as exc:
            print(f"  FAILED: {exc}", flush=True)
            raise

    # Generate a summary markdown
    from .analyze import write_summary

    summary_path = write_summary(RESULTS_DIR / timestamp)
    print(f"\nSummary written to {summary_path}", flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(prog="roslyn-abtest")
    sub = parser.add_subparsers(dest="cmd", required=True)

    run_p = sub.add_parser("run", help="Run A/B test sweep")
    run_p.add_argument(
        "--task",
        nargs="+",
        default=["all"],
        help="One or more task stems (e.g. 05-explain-service 10-method-callgraph) or 'all'.",
    )
    run_p.add_argument("--arms", nargs="*", default=None, help="Restrict to specific arm names")
    run_p.add_argument("--reps", type=int, default=2, help="Reps per (task, arm)")
    run_p.add_argument("--seed", type=int, default=42, help="Random seed for run-order shuffle")
    run_p.add_argument("--max-turns", type=int, default=80, help="Max turns per run")
    run_p.add_argument("--max-budget-usd", type=float, default=10.0, help="Per-run budget cap")
    run_p.add_argument(
        "--model",
        # `or` (not the second arg to .get) so that an explicitly-empty env var
        # falls back to the hardcoded default instead of passing "" to the SDK.
        default=os.environ.get("ROZ_ABTEST_MODEL") or "claude-opus-4-7",
        help="Claude model to run each arm against. Overrides $ROZ_ABTEST_MODEL.",
    )

    analyze_p = sub.add_parser("analyze", help="Aggregate a results/<timestamp>/ into summary.md")
    analyze_p.add_argument(
        "--timestamp", help="Specific results/<timestamp>/ to analyze. Default: most recent."
    )

    judge_p = sub.add_parser("judge", help="LLM-judge correctness of impact reports for a run")
    judge_p.add_argument(
        "--timestamp", help="Specific results/<timestamp>/ to judge. Default: most recent."
    )
    judge_p.add_argument(
        "--model",
        default=os.environ.get("ROZ_ABTEST_JUDGE_MODEL") or "claude-opus-4-7",
        help="Judge model. Overrides $ROZ_ABTEST_JUDGE_MODEL.",
    )

    backfill_p = sub.add_parser(
        "backfill",
        help="Recompute diff-hygiene fields for existing runs (idempotent) and rewrite summaries.",
    )
    backfill_p.add_argument(
        "--timestamp",
        help="Specific results/<timestamp>/ to backfill. Default: every timestamped dir.",
    )

    args = parser.parse_args()
    if args.cmd == "run":
        asyncio.run(main_async(args))
    elif args.cmd == "analyze":
        from .analyze import _resolve_timestamp_dir, write_summary
        write_summary(_resolve_timestamp_dir(args.timestamp))
    elif args.cmd == "judge":
        from .analyze import _resolve_timestamp_dir
        from .judge import run_judge
        asyncio.run(run_judge(_resolve_timestamp_dir(args.timestamp), args.model))
    elif args.cmd == "backfill":
        from .analyze import (
            _all_timestamp_dirs,
            _resolve_timestamp_dir,
            backfill_dir,
            write_summary,
        )
        dirs = [_resolve_timestamp_dir(args.timestamp)] if args.timestamp else _all_timestamp_dirs()
        if not dirs:
            print("No results dirs to backfill.", flush=True)
        for d in dirs:
            # Historical sweep: one bad dir must not abort backfilling the rest. Catch
            # SystemExit too — write_summary raises it (via sys.exit) on a run-less dir
            # (e.g. a stillborn run dir), which is a skip here, not a fatal.
            try:
                count = backfill_dir(d)
                write_summary(d)
                print(f"  backfilled {count} run(s) in {d.name}", flush=True)
            except (Exception, SystemExit) as exc:  # noqa: BLE001 — surfaced, not swallowed
                print(f"  SKIPPED {d.name}: {exc}", flush=True)


if __name__ == "__main__":
    main()
