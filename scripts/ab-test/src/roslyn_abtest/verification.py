"""Pluggable per-task verification: each task's YAML frontmatter declares a list of
verifier specs; `run_one` dispatches them through this registry, merging each
verifier's emitted dict into the run's result JSON."""
from __future__ import annotations

import os
import re
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path


@dataclass
class VerifyContext:
    """Context passed to each verifier.

    `cache_dir` and `solution_path` are the active per-fixture clone paths,
    threaded in from `run_one`'s resolved `Fixture`. The verifiers that shell
    out to the clone (`build`, `token-residual`, `loc-delta-max` ignore-branch)
    read them rather than module-level constants, so each run targets its own
    fixture's clone.

    `result` is the in-progress result dict; later verifiers see earlier
    verifiers' merged output (e.g. `loc-delta-*` reads `diff_loc`).

    `diff_full` is the run's full unified diff (agent edits only — the planted
    `setup_patch` is committed before the run, so it never shows here). The
    `diff-absent`/`diff-contains` verifiers grep its added lines.
    """
    cache_dir: Path
    solution_path: Path
    result: dict
    diff_full: str = ""


Verifier = Callable[[VerifyContext, dict], dict]


_SLUG_PATTERN = re.compile(r"[^a-z0-9]+")


def _slug_for_token(token: str) -> str:
    return _SLUG_PATTERN.sub("", token.lower())


def _verify_build(ctx: VerifyContext, spec: dict) -> dict:
    from .runner import build_clone

    expected_exit = int(spec.get("expected_exit", 0))
    build_rc, build_stdout, build_stderr = build_clone(ctx.solution_path)
    # Trim the copy written to the result JSON: success keeps the ~500-char build
    # summary, failure keeps 4000 for diagnosis (the untrimmed output is dominated by
    # NU1900 warnings from an unreachable feed).
    keep = 500 if build_rc == expected_exit else 4000
    return {
        "build_exit_code": build_rc,
        "build_stdout_tail": build_stdout[-keep:],
        "build_stderr_tail": build_stderr[-keep:],
        "build_expected_exit": expected_exit,
        "build_passed": build_rc == expected_exit,
    }


# Matches an MSBuild/Roslyn diagnostic line, e.g.
#   C:\x\Foo.cs(12,13): warning CS0168: 'x' declared but never used [C:\x\Proj.csproj]
# The trailing [project] group is optional (some diagnostics omit it).
_DIAGNOSTIC_RE = re.compile(
    r"^(?P<file>.+?)\((?P<line>\d+),(?P<col>\d+)\):\s+"
    r"(?P<sev>warning|error)\s+(?P<id>[A-Za-z]+\d+):\s+"
    r"(?P<msg>.*?)(?:\s+\[(?P<proj>[^\]]+)\])?$"
)


def _added_diff_lines(diff_full: str) -> list[str]:
    """Added content lines of a unified diff (`+foo`), excluding the `+++` file header."""
    return [
        line[1:]
        for line in diff_full.splitlines()
        if line.startswith("+") and not line.startswith("+++")
    ]


def _verify_diff_absent(ctx: VerifyContext, spec: dict) -> dict:
    """Fail if any added diff line matches any regex in `patterns` (suppression /
    silent-IVT / must-not-edit detector). The planted patch is committed before the
    run, so this sees the agent's edits only."""
    patterns = list(spec.get("patterns") or [])
    if not patterns:
        raise ValueError(f"diff-absent requires a non-empty 'patterns' list; got {spec!r}")
    compiled = [(p, re.compile(p)) for p in patterns]
    added = _added_diff_lines(ctx.diff_full)
    violations = [
        {"pattern": p, "line": line.strip()[:200]}
        for line in added
        for p, rx in compiled
        if rx.search(line)
    ]
    return {
        "diff_absent_patterns": patterns,
        "diff_absent_violations": violations[:20],
        "diff_absent_violation_count": len(violations),
        "diff_absent_pass": not violations,
    }


def _verify_diff_contains(ctx: VerifyContext, spec: dict) -> dict:
    """Pass only if every regex in `patterns` matches at least one added diff line."""
    patterns = list(spec.get("patterns") or [])
    if not patterns:
        raise ValueError(f"diff-contains requires a non-empty 'patterns' list; got {spec!r}")
    added = _added_diff_lines(ctx.diff_full)
    missing = [p for p in patterns if not any(re.search(p, line) for line in added)]
    return {
        "diff_contains_patterns": patterns,
        "diff_contains_missing": missing,
        "diff_contains_pass": not missing,
    }


def _verify_diff_hygiene(ctx: VerifyContext, spec: dict) -> dict:
    """Fail a run that games diff-based cost metrics via whole-file rewrites or BOM strips.

    `max_churn_ratio` caps the fraction of raw +/- lines that are verbatim resurrection
    (a line removed and re-added unchanged modulo BOM/whitespace); `forbid_bom_strip`
    fails the run if any file had a leading UTF-8 BOM dropped. Both are optional — a spec
    with neither just reports the metrics. The measured diff is agent edits only (the
    planted setup_patch is committed before the run)."""
    from .diffmetrics import compute_diff_hygiene

    metrics = compute_diff_hygiene(ctx.diff_full)
    violations: list[str] = []
    if "max_churn_ratio" in spec:
        max_ratio = float(spec["max_churn_ratio"])
        if metrics["diff_churn_ratio"] > max_ratio:
            violations.append(
                f"churn_ratio {metrics['diff_churn_ratio']:.3f} > max {max_ratio}"
            )
    if spec.get("forbid_bom_strip") and metrics["diff_bom_stripped_files"] > 0:
        violations.append(
            f"{metrics['diff_bom_stripped_files']} file(s) had a UTF-8 BOM stripped"
        )
    return {
        "diff_hygiene_churn_ratio": metrics["diff_churn_ratio"],
        "diff_hygiene_semantic_loc": metrics["diff_loc_semantic"],
        "diff_hygiene_bom_stripped_files": metrics["diff_bom_stripped_files"],
        "diff_hygiene_rewritten_files": metrics["diff_rewritten_files"],
        "diff_hygiene_violations": violations,
        "diff_hygiene_pass": not violations,
    }


def _verify_diagnostics_delta(ctx: VerifyContext, spec: dict) -> dict:
    """Count remaining targeted diagnostics in the clone; pass if <= max_remaining.

    Runs its OWN clean build (`no_incremental=True`) rather than reusing the exit-code
    `build` verifier's incremental one: a skipped up-to-date compile doesn't re-emit
    its warnings, so an incremental build would under-count whatever the agent already
    compiled. Filters by `ids` (diagnostic codes), `severity`, and `scope`
    (project/file substring) — any omitted filter is a wildcard."""
    if "max_remaining" not in spec:
        raise ValueError(f"diagnostics-delta requires 'max_remaining'; got {spec!r}")
    max_remaining = int(spec["max_remaining"])
    ids = {i.upper() for i in (spec.get("ids") or [])}
    severity = spec.get("severity")
    severity = severity.strip().lower() if isinstance(severity, str) else None
    scope = spec.get("scope")

    from .runner import build_clone

    _rc, out, err = build_clone(ctx.solution_path, no_incremental=True)
    build_output = f"{out}\n{err}"

    seen: set[tuple[str, str, str]] = set()
    for line in build_output.splitlines():
        m = _DIAGNOSTIC_RE.match(line.strip())
        if not m:
            continue
        if severity and m["sev"] != severity:
            continue
        if ids and m["id"].upper() not in ids:
            continue
        if scope:
            scope_l = scope.lower()
            in_file = scope_l in (m["file"] or "").lower()
            in_proj = scope_l in (m["proj"] or "").lower()
            if not in_file and not in_proj:
                continue
        # Dedup by (id, file, line): MSBuild repeats a diagnostic once per
        # referencing project, which would otherwise inflate the count.
        seen.add((m["id"].upper(), (m["file"] or "").lower(), m["line"]))

    remaining = len(seen)
    return {
        "diagnostics_delta_remaining": remaining,
        "diagnostics_delta_max": max_remaining,
        "diagnostics_delta_ids": sorted(ids),
        "diagnostics_delta_samples": [f"{i} {f}:{ln}" for (i, f, ln) in sorted(seen)][:20],
        "diagnostics_delta_pass": remaining <= max_remaining,
    }


# find_symbol emits accessibility two ways: a bracketed `[public method]` tag in member
# listings (depth > 0), and an UNBRACKETED leading keyword in a top-level result line,
# e.g. `1. public static method string Foo()` / `1. public sealed class Bar`. The
# accessibility-is verifier queries a single symbol, so it gets the latter — match both.
_ACCESS_TAG_RE = re.compile(
    r"(?:\[|^\s*\d+\.\s+)"
    r"(public|protected internal|protected|private protected|internal|private|file)\b",
    re.MULTILINE,
)


def _parse_access_tag(symbol_text: str) -> str | None:
    """Pull the accessibility keyword from the first symbol line find_symbol emits —
    either a bracketed `[access kind]` tag or an unbracketed `N. access ... ` result line."""
    m = _ACCESS_TAG_RE.search(symbol_text)
    return m.group(1) if m else None


def query_symbol_text(
    solution_path: Path, symbol_name: str, containing_type: str | None, project: str | None
) -> str:
    """Run `find_symbol` against the clone via a short-lived server; return its raw text.

    Split out as a module function so tests stub the live MCP round-trip without a
    real workspace load. The verifier's tag parsing (`_parse_access_tag`) stays pure."""
    from .mcp_client import CALL_TIMEOUT_S, McpStdioClient, resolve_roslyn_exe

    arguments: dict = {"symbolNames": [symbol_name]}
    if containing_type:
        arguments["containingType"] = containing_type
    if project:
        arguments["project"] = project
    env = {
        **os.environ,
        "ROZ_SOLUTION_PATH": str(solution_path),
        "ROZ_TOOLS": "all",
        "ROZ_LOG_LEVEL": "Warning",
    }
    client = McpStdioClient(resolve_roslyn_exe(), env)
    try:
        client.initialize()
        return client.call_tool("find_symbol", arguments, CALL_TIMEOUT_S)
    finally:
        client.close()


def _verify_accessibility_is(ctx: VerifyContext, spec: dict) -> dict:
    """Assert a member's final accessibility equals `expected` by re-resolving it post-run."""
    symbol_name = spec.get("symbolName")
    expected = spec.get("expected")
    if not symbol_name or not isinstance(symbol_name, str):
        raise ValueError(f"accessibility-is requires a string 'symbolName'; got {spec!r}")
    if not expected or not isinstance(expected, str):
        raise ValueError(f"accessibility-is requires a string 'expected'; got {spec!r}")
    containing_type = spec.get("containingType")
    text = query_symbol_text(
        ctx.solution_path, symbol_name, containing_type, spec.get("project")
    )
    actual = _parse_access_tag(text)
    expected_norm = expected.strip().lower()
    # Slug the keys by symbol (+ containingType) so multiple accessibility-is checks in one task
    # don't overwrite each other's result — the same multi-instance safety token-residual has.
    slug = _slug_for_token(f"{containing_type}.{symbol_name}" if containing_type else symbol_name)
    return {
        f"accessibility_is_{slug}_symbol": symbol_name,
        f"accessibility_is_{slug}_expected": expected_norm,
        f"accessibility_is_{slug}_actual": actual,
        f"accessibility_is_{slug}_pass": actual == expected_norm,
    }


def _verify_token_residual(ctx: VerifyContext, spec: dict) -> dict:
    from .runner import count_token_in_src

    token = spec.get("token")
    if not token or not isinstance(token, str):
        raise ValueError(
            f"token-residual verifier requires a non-empty string 'token'; got {spec!r}"
        )
    scope = spec.get("scope", "src")
    if scope != "src":
        raise ValueError(
            f"token-residual scope={scope!r} not supported (only 'src')"
        )
    count = count_token_in_src(ctx.solution_path, token)
    slug = _slug_for_token(token)
    emitted: dict = {f"{slug}_residual_count": count}
    if "max_count" in spec:
        emitted[f"{slug}_residual_max_pass"] = count <= int(spec["max_count"])
    if "min_count" in spec:
        emitted[f"{slug}_residual_min_pass"] = count >= int(spec["min_count"])
    return emitted


def _verify_file_exists(ctx: VerifyContext, spec: dict) -> dict:
    paths = list(spec.get("paths") or [])
    missing = [rel for rel in paths if not (ctx.cache_dir / rel).exists()]
    return {
        "file_exists": {
            "all_present": not missing,
            "missing": missing,
            "checked": paths,
        }
    }


def _verify_loc_delta_max(ctx: VerifyContext, spec: dict) -> dict:
    threshold = int(spec["max"])
    ignore = list(spec.get("ignore_tracked_paths") or [])
    if ignore:
        # Filters tracked-file diffs only. Untracked files never contribute to
        # `diff_loc` regardless — documented limitation. For 02-audit, this
        # protects against Claude editing a tracked file as a side-effect;
        # AUDIT_REPORT.md is created untracked and is already invisible to the
        # base diff_loc count.
        from .runner import count_diff_loc, run_git

        args = ["diff", "--", *[f":!{p}" for p in ignore]]
        diff_full = run_git(ctx.cache_dir, *args).stdout
        value = count_diff_loc(diff_full)
    else:
        value = int(ctx.result.get("diff_loc", 0))
    return {
        "loc_delta_max_value": value,
        "loc_delta_max_threshold": threshold,
        "loc_delta_max_pass": value <= threshold,
    }


def _verify_loc_delta_min(ctx: VerifyContext, spec: dict) -> dict:
    threshold = int(spec["min"])
    value = int(ctx.result.get("diff_loc", 0))
    return {
        "loc_delta_min_value": value,
        "loc_delta_min_threshold": threshold,
        "loc_delta_min_pass": value >= threshold,
    }


VERIFIERS: dict[str, Verifier] = {
    "build": _verify_build,
    "token-residual": _verify_token_residual,
    "file-exists": _verify_file_exists,
    "loc-delta-max": _verify_loc_delta_max,
    "loc-delta-min": _verify_loc_delta_min,
    "diff-absent": _verify_diff_absent,
    "diff-contains": _verify_diff_contains,
    "diff-hygiene": _verify_diff_hygiene,
    "diagnostics-delta": _verify_diagnostics_delta,
    "accessibility-is": _verify_accessibility_is,
}


def validate_verification_order(spec_list: list[dict], task_path: Path) -> None:
    """`build` runs first if present so other verifiers can read its results
    from `ctx.result`. Enforced at parse time, not by re-sorting — surfaces
    misorderings instead of silently fixing them. Also catches token-residual
    and accessibility-is slug collisions, which would otherwise silently overwrite
    each other's slugged result keys in the result dict."""
    types = [s.get("type") for s in spec_list]
    if "build" in types and types[0] != "build":
        raise ValueError(
            f"{task_path}: 'build' verifier must be first in `verification:` "
            f"(other verifiers may read build_exit_code from context). Got: {types}"
        )

    slugs: dict[str, str] = {}
    for spec in spec_list:
        if spec.get("type") != "token-residual":
            continue
        token = spec.get("token")
        if not isinstance(token, str) or not token:
            continue
        slug = _slug_for_token(token)
        if slug in slugs and slugs[slug] != token:
            raise ValueError(
                f"{task_path}: token-residual tokens {slugs[slug]!r} and {token!r} "
                f"both reduce to slug {slug!r} — the second would overwrite the "
                f"first's *_residual_count key. Use distinguishable tokens."
            )
        slugs[slug] = token

    # Same multi-instance guard for accessibility-is, whose result keys are slugged by
    # symbol (+ containingType): two checks that collapse to one slug would overwrite each
    # other's *_pass key (the P6 bug — three same-keyed checks left only the last recorded).
    acc_slugs: dict[str, str] = {}
    for spec in spec_list:
        if spec.get("type") != "accessibility-is":
            continue
        symbol_name = spec.get("symbolName")
        if not isinstance(symbol_name, str) or not symbol_name:
            continue
        containing_type = spec.get("containingType")
        ident = f"{containing_type}.{symbol_name}" if containing_type else symbol_name
        slug = _slug_for_token(ident)
        if slug in acc_slugs:
            raise ValueError(
                f"{task_path}: accessibility-is targets {acc_slugs[slug]!r} and {ident!r} "
                f"both reduce to slug {slug!r}; their result keys would collide. "
                f"Use one check per symbol, or disambiguate with containingType."
            )
        acc_slugs[slug] = ident
