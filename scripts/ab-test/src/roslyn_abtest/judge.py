#!/usr/bin/env python3
"""LLM-judge for the analyze_change_impact + analyze_method A/B experiments and the
MCP-prompt efficacy experiment.

Offline correctness grader, deliberately separate from the load-bearing run path
in runner.py: it reads the report artifact each arm produced for a judged task,
compares it against a pre-generated reference (a Roslyn tool's own output, or a
hand-annotated oracle, eyeballed once), and scores it with a pinned-model Claude
call. Five rubrics, dispatched per task:

  - impact     — site recall + verdict accuracy (analyze_change_impact tasks and
                 the assess_impact prompt, which reuses the same oracles)
  - method     — inbound/outbound caller-graph recall + hallucinations
  - breaking   — break-class accuracy + recall + in-solution/external split
                 (check_breaking_changes prompt)
  - decompile  — factual accuracy + grounding + hallucinations (decompile_symbol)
  - audit      — fan-in / entity / god-class recall + count plausibility (02-audit)

A task's rubric is resolved from its frontmatter `rubric:`/`report:`/`reference:`
keys; the eight original impact/method tasks (which carry no such keys) fall back
to a hard-coded legacy registry so they keep grading unchanged. Tasks whose signal
is purely mechanical verifiers (fix_diagnostics, cleanup_dead_code,
tighten_accessibility) declare `rubric: none` and carry no LLM judge here.

It touches none of the experiment invariants (NO_FORMATTER_DIRECTIVE,
setting_sources, skills, the fixture cache). Run after `roslyn-abtest run`
(+ `analyze`), from a PLAIN TERMINAL — judging inside a Claude session hangs.

Usage:
    roslyn-abtest judge                      # judge the most recent results/<ts>/
    roslyn-abtest judge --timestamp 2026...  # a specific run
"""
from __future__ import annotations

import json
import os
import statistics
import tempfile
from collections import defaultdict
from pathlib import Path
from typing import NamedTuple

from claude_agent_sdk import (
    AssistantMessage,
    ClaudeAgentOptions,
    ResultMessage,
    TextBlock,
    query,
)

from .analyze import _pct, _pick_anchor, load_runs
from .paths import TASKS_DIR
from .tasks import parse_task


class _Judged(NamedTuple):
    """How to grade one task: which rubric, which report artifact, which reference oracle."""

    rubric: str       # impact | method | breaking | decompile
    report: str       # report-artifact filename the arm was asked to write
    reference: str     # reference task stem (the *.reference.md to grade against)


# The LLM-graded rubrics. A task frontmatter `rubric:` outside this set (e.g.
# `none`) carries no LLM judge — its signal is the mechanical verifiers.
_LLM_RUBRICS = frozenset({"impact", "method", "breaking", "decompile", "audit"})

# Legacy registry for the original eight judged tasks, which carry no `rubric:`
# frontmatter. New prompt tasks declare their grading in frontmatter instead
# (see _resolve_judged). 09-impact-trap / 13-method-trap are absent on purpose:
# a rename / an over-reach control has no blast-radius or call-graph to grade.
_LEGACY_JUDGED: dict[str, _Judged] = {
    "04-impact-analysis": _Judged("impact", "IMPACT_ANALYSIS.md", "04-impact-analysis"),
    "06-impact-remove": _Judged("impact", "REMOVE_IMPACT.md", "06-impact-remove"),
    "07-impact-accessibility": _Judged(
        "impact", "ACCESSIBILITY_IMPACT.md", "07-impact-accessibility"
    ),
    "08-impact-signature": _Judged("impact", "SIGNATURE_IMPACT.md", "08-impact-signature"),
    "05-explain-service": _Judged("method", "SERVICE_EXPLAINED.md", "05-explain-service"),
    "10-method-callgraph": _Judged("method", "CALL_GRAPH.md", "10-method-callgraph"),
    "11-method-interface": _Judged("method", "INBOUND.md", "11-method-interface"),
    "12-method-overloads": _Judged("method", "OVERLOADS.md", "12-method-overloads"),
}


def _resolve_judged(task_stem: str) -> _Judged | None:
    """Resolve a task's grading: frontmatter `rubric:`/`report:`/`reference:` first,
    legacy registry as fallback for the original eight. Returns None when the task
    carries no LLM rubric (mechanical-only, or simply not a judged task)."""
    task_md = TASKS_DIR / f"{task_stem}.md"
    if task_md.is_file():
        try:
            metadata, _ = parse_task(task_md)
        except ValueError:
            metadata = {}
        rubric = str(metadata.get("rubric") or "").strip().lower()
        if rubric in _LLM_RUBRICS:
            report = metadata.get("report")
            if not report:
                return None
            return _Judged(rubric, str(report), str(metadata.get("reference") or task_stem))
        if rubric == "none":
            return None
        # No `rubric:` declared (the original tasks) → fall through to legacy.
    return _LEGACY_JUDGED.get(task_stem)


# Pinned judge model. ClaudeAgentOptions exposes no temperature knob, so
# determinism rests on the pinned model plus a structured, low-variance prompt
# (a documented caveat: temp-0 would reduce, not remove, judge variance).
JUDGE_MODEL = os.environ.get("ROZ_ABTEST_JUDGE_MODEL") or "claude-opus-4-7"

# Guard against pathological context blow-up. Reports are already response-capped
# (~25 KB); references are generated with a high cap and a high-fan-in method oracle
# (e.g. 11-method-interface, ~60 KB of caller sites) must survive unclipped, so keep
# this well above both.
_MAX_DOC_CHARS = 80_000

JUDGE_SYSTEM_PROMPT = (
    "You are a meticulous grader for a static-analysis A/B experiment. You "
    "compare a CANDIDATE change-impact report against a trusted REFERENCE (the "
    "output of a Roslyn-powered analyzer, treated as ground truth) and score how "
    "completely and correctly the candidate reproduced the reference's impacted "
    "sites and their verdicts. You output ONLY a single JSON object, nothing else."
)

JUDGE_METHOD_SYSTEM_PROMPT = (
    "You are a meticulous grader for a static-analysis A/B experiment. You "
    "compare a CANDIDATE method-comprehension report against a trusted REFERENCE "
    "(the output of a Roslyn-powered analyzer, treated as ground truth) and score "
    "how completely and correctly the candidate reproduced the reference's inbound "
    "callers and outbound in-solution collaborators for each method. You output "
    "ONLY a single JSON object, nothing else."
)

JUDGE_BREAKING_SYSTEM_PROMPT = (
    "You are a meticulous grader for a static-analysis experiment on breaking-change "
    "detection. You compare a CANDIDATE breaking-change report against a trusted "
    "REFERENCE that lists each planted public-surface change, its verified in-solution "
    "impact, and its break class. You score how completely and correctly the candidate "
    "found the planted breaks, classified them, and split verified in-solution impact "
    "from unverifiable external surface. You output ONLY a single JSON object."
)

JUDGE_DECOMPILE_SYSTEM_PROMPT = (
    "You are a meticulous grader for an experiment on explaining external (BCL/NuGet) "
    "code. You compare a CANDIDATE explanation against a trusted REFERENCE (the symbol's "
    "real source/IL plus a checklist of plausible-but-FALSE claims). You score factual "
    "accuracy, how well claims are grounded in the actual body, and how many false claims "
    "the candidate made. You output ONLY a single JSON object."
)

JUDGE_AUDIT_SYSTEM_PROMPT = (
    "You are a meticulous grader for a codebase-audit experiment. You compare a CANDIDATE "
    "audit report against a trusted REFERENCE of three ranked tables (top service interfaces "
    "by caller fan-in, top entities by project spread, biggest god-classes by LOC) produced "
    "by a Roslyn-powered analyzer, treated as ground truth. You score how completely the "
    "candidate reproduced each ranking, whether its cited numbers are plausible, and how many "
    "items it fabricated. You output ONLY a single JSON object."
)


def _clip(text: str, limit: int = _MAX_DOC_CHARS) -> str:
    if len(text) <= limit:
        return text
    return text[:limit] + "\n... [clipped for judging]"


def _build_judge_prompt(task: str, reference: str, candidate: str) -> str:
    """Assemble the judging prompt: ground-truth reference + candidate + rubric."""
    return f"""\
You are grading a change-impact report for the experiment task `{task}`.

The REFERENCE below is the trusted ground truth: the output of a Roslyn-powered
analyzer that classified every reference site of the changed symbol with a
verdict. Treat it as correct and complete.

The CANDIDATE is a report a coding agent wrote for the same change. Grade how
well the candidate reproduced the reference.

A "site" is one impacted reference location, identified by file path plus the
member or line it occurs in. Match a candidate site to a reference site when
they point at the same location; tolerate small line-number drift and path
abbreviation. A site's "verdict" is one of: compatible, requires-update, unsafe
(an accessibility change uses only compatible/unsafe; a removal or signature
change may apply a single verdict to every site). Treat an orphaned override or
interface implementation called out by the reference as a site too.

Score:
- site_recall: (reference sites the candidate also reported) / (total reference
  sites), a float in [0,1].
- verdict_accuracy: of the matched sites, the fraction whose candidate verdict
  matches the reference verdict, a float in [0,1]. If no sites matched, use 0.
- missed_sites: short identifiers (file + member) of reference sites the
  candidate omitted.
- hallucinated_sites: candidate sites that are not in the reference.
- notes: one or two sentences on the main discrepancy, or "" if none.

Output ONLY a single JSON object, no prose and no code fence:
{{"site_recall": <float>, "verdict_accuracy": <float>, "missed_sites": \
[<string>, ...], "hallucinated_sites": [<string>, ...], "notes": <string>}}

=== REFERENCE (ground truth) ===
{_clip(reference)}

=== CANDIDATE (to grade) ===
{_clip(candidate)}
"""


def _build_method_judge_prompt(task: str, reference: str, candidate: str) -> str:
    """Assemble the method-comprehension judging prompt: reference + candidate + rubric."""
    return f"""\
You are grading a method-comprehension report for the experiment task `{task}`.

The REFERENCE below is the trusted ground truth: the output of a Roslyn-powered
tool (`analyze_method`) that, for one or more methods, lists the method
signature, its INBOUND callers, and its OUTBOUND in-solution collaborators (the
other methods/services/repositories it calls). Treat it as correct and complete.

The CANDIDATE is a report a coding agent wrote about the same method(s). Grade
how well the candidate reproduced the reference's call graph.

Definitions:
- An "inbound caller site" is one location that calls the method, identified by
  file path plus the calling member (tolerate small line drift and path abbreviation).
- An "outbound collaborator" is one in-solution service / repository / helper method /
  event publisher the method INVOKES. The reference tags each outbound entry `[method]`,
  `[constructor]`, or `[property]`: collaborators are the `[method]` / `[constructor]`
  calls (e.g. `_orderService.UpdateOrderItemAsync`, `IEventPublisher.PublishAsync`),
  deduplicated by target. Bare `[property]` / field reads on entities (e.g. `Order.Id`,
  `BaseEntity.Id`) are NOT collaborators — a candidate that omits them is NOT missing a
  collaborator. Ignore BCL/LINQ noise likewise.
- The reference may cover MULTIPLE methods. Score only methods that appear in BOTH
  the reference and the candidate. If a method is in one but not the other (e.g. the
  candidate chose a different "important" method), treat it as OUT OF SCOPE — do not
  count its sites as missed or hallucinated; note the method-set difference instead.

Score, pooled across the in-scope (shared) methods:
- inbound_recall: (reference caller sites the candidate also reported) / (total
  reference caller sites for shared methods), a float in [0,1]. If the shared methods
  have no reference caller sites, use 1.0.
- outbound_recall: (reference outbound collaborators the candidate also reported) /
  (total reference outbound collaborators for shared methods), a float in [0,1]. If
  the shared methods have no reference outbound collaborators, use 1.0.
- missed_callers: short identifiers (file + member) of reference caller sites omitted.
- missed_callees: short identifiers of reference outbound collaborators omitted.
- hallucinated_callers: candidate caller sites absent from the reference (shared methods).
- hallucinated_callees: candidate outbound collaborators absent from the reference (shared methods).
- notes: 1-2 sentences on the main discrepancy, incl. any method-set difference, or "" if none.

Output ONLY a single JSON object, no prose and no code fence:
{{"inbound_recall": <float>, "outbound_recall": <float>, "missed_callers": \
[<string>, ...], "missed_callees": [<string>, ...], "hallucinated_callers": \
[<string>, ...], "hallucinated_callees": [<string>, ...], "notes": <string>}}

=== REFERENCE (ground truth) ===
{_clip(reference)}

=== CANDIDATE (to grade) ===
{_clip(candidate)}
"""


def _build_breaking_judge_prompt(task: str, reference: str, candidate: str) -> str:
    """Assemble the breaking-change judging prompt: reference + candidate + rubric."""
    return f"""\
You are grading a breaking-change report for the experiment task `{task}`.

The REFERENCE below is the trusted ground truth: a list of the planted
public-surface changes on this branch. For each, it gives the changed
declaration, the break class a consumer would hit — **source-incompatible**
(won't recompile: removed/renamed/retyped/narrowed), **binary-incompatible**
(signature change that breaks already-compiled callers even if source compiles),
or **behavior change** (same signature, different semantics) — and the verified
in-solution impact. `internal`/`private` changes that CANNOT break external
consumers are NOT in the planted set; a candidate that flags one is over-reporting.

The CANDIDATE is a report a coding agent wrote. Grade how well it reproduced the
reference.

Score:
- recall: (planted reference changes the candidate also reported) / (total planted
  changes), a float in [0,1].
- classification_accuracy: of the matched changes, the fraction the candidate put
  in the correct break class, a float in [0,1]. If none matched, use 0.
- split_correct: a float in [0,1] — how correctly the candidate separated VERIFIED
  in-solution impact from UNVERIFIABLE external-surface impact (1.0 = split exactly
  right, 0.0 = conflated or absent).
- missed: short identifiers of planted changes the candidate omitted.
- misclassified: short identifiers of changes the candidate put in the wrong class
  (incl. any internal/private change it wrongly flagged as breaking).
- notes: 1-2 sentences on the main discrepancy, or "" if none.

Output ONLY a single JSON object, no prose and no code fence:
{{"recall": <float>, "classification_accuracy": <float>, "split_correct": <float>, \
"missed": [<string>, ...], "misclassified": [<string>, ...], "notes": <string>}}

=== REFERENCE (ground truth) ===
{_clip(reference)}

=== CANDIDATE (to grade) ===
{_clip(candidate)}
"""


def _build_decompile_judge_prompt(task: str, reference: str, candidate: str) -> str:
    """Assemble the decompile/explain judging prompt: reference + candidate + rubric."""
    return f"""\
You are grading an explanation of an external (BCL/NuGet) symbol for the
experiment task `{task}`.

The REFERENCE below is the trusted ground truth: the symbol's REAL source or
decompiled body, followed by a CHECKLIST of plausible-but-FALSE claims (things a
model might assert that are actually wrong for this symbol). Treat the body as
correct and complete, and treat every checklist item as false.

The CANDIDATE is an explanation a coding agent wrote. Grade it against the body.

Score:
- factual_accuracy: a float in [0,1] — how well the candidate's claims about the
  symbol's behavior match the real body.
- grounding: a float in [0,1] — how well the candidate's claims are supported by
  the actual body it should have retrieved (vs hand-wavy generic assertions).
- hallucinations: an integer count of checklist (false) claims the candidate
  asserted, plus any other clearly-false claims it made.
- source_first: a float in [0,1] — from the candidate's own account of its
  process, how well it preferred real source over decompilation (1.0 = read real
  source / said source was used; 0.0 = decompiled when source was available;
  use 0.5 if the report doesn't say).
- hallucinated_claims: short strings naming the false claims the candidate made.
- notes: 1-2 sentences on the main discrepancy, or "" if none.

Output ONLY a single JSON object, no prose and no code fence:
{{"factual_accuracy": <float>, "grounding": <float>, "hallucinations": <int>, \
"source_first": <float>, "hallucinated_claims": [<string>, ...], "notes": <string>}}

=== REFERENCE (ground truth) ===
{_clip(reference)}

=== CANDIDATE (to grade) ===
{_clip(candidate)}
"""


def _build_audit_judge_prompt(task: str, reference: str, candidate: str) -> str:
    """Assemble the codebase-audit judging prompt: reference tables + candidate + rubric."""
    return f"""\
You are grading a codebase-audit report for the experiment task `{task}`.

The REFERENCE below is the trusted ground truth: three ranked tables produced by a
Roslyn-powered analyzer — (a) the top service interfaces by caller fan-in, (b) the top
entities by how many projects reference them, and (c) the biggest god-classes by lines
of code. Treat the rankings as correct. Exact rank order and exact numbers may vary
slightly; judge on whether the candidate surfaced the same items and plausible figures.

The CANDIDATE is an audit report a coding agent wrote for the same task. It was asked for
the top 5 fan-in services, top 3 most-referenced entities, and top 3 biggest god-classes —
so grade recall of the candidate's items against the HIGHEST-ranked reference items
(match a candidate item to a reference row when they name the same interface/entity/type,
tolerating an `I` prefix, namespace, or `.cs` suffix).

Score:
- fanin_recall: of the reference's top service interfaces the candidate SHOULD have found
  (its top ~5), the fraction it did, a float in [0,1].
- entity_recall: likewise for the most-referenced entities (its top ~3), a float in [0,1].
- godclass_recall: likewise for the biggest god-classes (its top ~3), a float in [0,1].
- count_plausibility: a float in [0,1] — are the candidate's cited caller counts / project
  counts / LOC figures in the right ballpark versus the reference (1.0 = consistent,
  0.0 = fabricated or wildly off; 0.5 if the candidate cites few or no numbers).
- hallucinated_items: candidate items (services/entities/god-classes) that are clearly wrong
  — not in the reference and not a plausible near-miss.
- notes: 1-2 sentences on the main discrepancy, or "" if none.

Output ONLY a single JSON object, no prose and no code fence:
{{"fanin_recall": <float>, "entity_recall": <float>, "godclass_recall": <float>, \
"count_plausibility": <float>, "hallucinated_items": [<string>, ...], "notes": <string>}}

=== REFERENCE (ground truth) ===
{_clip(reference)}

=== CANDIDATE (to grade) ===
{_clip(candidate)}
"""


def _extract_json(text: str) -> dict:
    """Pull the single JSON object out of the judge's reply, fence-tolerant."""
    cleaned = text.strip()
    if cleaned.startswith("```"):
        # Drop a leading ```json / ``` fence and the trailing fence.
        cleaned = cleaned.split("```", 2)[1] if cleaned.count("```") >= 2 else cleaned
        cleaned = cleaned.removeprefix("json").strip()
    start = cleaned.find("{")
    end = cleaned.rfind("}")
    if start == -1 or end == -1 or end < start:
        raise ValueError(f"no JSON object found in judge reply: {text[:200]!r}")
    return json.loads(cleaned[start : end + 1])


def _clamp01(value: object) -> float | None:
    try:
        return max(0.0, min(1.0, float(value)))  # type: ignore[arg-type]
    except (TypeError, ValueError):
        return None


def _as_int(value: object) -> int | None:
    try:
        return max(0, int(value))  # type: ignore[call-overload]
    except (TypeError, ValueError):
        return None


def _normalize_judgment(parsed: dict) -> dict:
    """Coerce a raw impact-judge object into the canonical scored shape."""
    return {
        "site_recall": _clamp01(parsed.get("site_recall")),
        "verdict_accuracy": _clamp01(parsed.get("verdict_accuracy")),
        "missed_sites": list(parsed.get("missed_sites") or []),
        "hallucinated_sites": list(parsed.get("hallucinated_sites") or []),
        "notes": str(parsed.get("notes") or ""),
    }


def _normalize_method_judgment(parsed: dict) -> dict:
    """Coerce a raw method-judge object into the canonical scored shape."""
    return {
        "inbound_recall": _clamp01(parsed.get("inbound_recall")),
        "outbound_recall": _clamp01(parsed.get("outbound_recall")),
        "missed_callers": list(parsed.get("missed_callers") or []),
        "missed_callees": list(parsed.get("missed_callees") or []),
        "hallucinated_callers": list(parsed.get("hallucinated_callers") or []),
        "hallucinated_callees": list(parsed.get("hallucinated_callees") or []),
        "notes": str(parsed.get("notes") or ""),
    }


def _normalize_breaking_judgment(parsed: dict) -> dict:
    """Coerce a raw breaking-change-judge object into the canonical scored shape."""
    return {
        "recall": _clamp01(parsed.get("recall")),
        "classification_accuracy": _clamp01(parsed.get("classification_accuracy")),
        "split_correct": _clamp01(parsed.get("split_correct")),
        "missed": list(parsed.get("missed") or []),
        "misclassified": list(parsed.get("misclassified") or []),
        "notes": str(parsed.get("notes") or ""),
    }


def _normalize_decompile_judgment(parsed: dict) -> dict:
    """Coerce a raw decompile-judge object into the canonical scored shape."""
    return {
        "factual_accuracy": _clamp01(parsed.get("factual_accuracy")),
        "grounding": _clamp01(parsed.get("grounding")),
        "hallucinations": _as_int(parsed.get("hallucinations")),
        "source_first": _clamp01(parsed.get("source_first")),
        "hallucinated_claims": list(parsed.get("hallucinated_claims") or []),
        "notes": str(parsed.get("notes") or ""),
    }


def _normalize_audit_judgment(parsed: dict) -> dict:
    """Coerce a raw audit-judge object into the canonical scored shape."""
    return {
        "fanin_recall": _clamp01(parsed.get("fanin_recall")),
        "entity_recall": _clamp01(parsed.get("entity_recall")),
        "godclass_recall": _clamp01(parsed.get("godclass_recall")),
        "count_plausibility": _clamp01(parsed.get("count_plausibility")),
        "hallucinated_items": list(parsed.get("hallucinated_items") or []),
        "notes": str(parsed.get("notes") or ""),
    }


# Per-rubric scaffolding: the score keys, plus the system prompt / prompt builder /
# normalizer trio. Keeps _missing_judgment / _unparseable_judgment / dispatch
# table-driven instead of branching four ways in each.
_RUBRIC_SCORE_KEYS: dict[str, tuple[str, ...]] = {
    "impact": ("site_recall", "verdict_accuracy"),
    "method": ("inbound_recall", "outbound_recall"),
    "breaking": ("recall", "classification_accuracy", "split_correct"),
    "decompile": ("factual_accuracy", "grounding", "source_first", "hallucinations"),
    "audit": ("fanin_recall", "entity_recall", "godclass_recall", "count_plausibility"),
}
_RUBRIC_LIST_KEYS: dict[str, tuple[str, ...]] = {
    "impact": ("missed_sites", "hallucinated_sites"),
    "method": ("missed_callers", "missed_callees", "hallucinated_callers", "hallucinated_callees"),
    "breaking": ("missed", "misclassified"),
    "decompile": ("hallucinated_claims",),
    "audit": ("hallucinated_items",),
}


def _missing_judgment(rubric: str) -> dict:
    """The zero-score judgment used when a candidate report artifact is absent."""
    out: dict = {k: 0.0 for k in _RUBRIC_SCORE_KEYS[rubric]}
    for k in _RUBRIC_LIST_KEYS[rubric]:
        out[k] = []
    out["notes"] = "candidate report artifact not found"
    # Surface the absence in whichever "missed" list the rubric carries.
    for k in ("missed_sites", "missed_callers", "missed"):
        if k in out:
            out[k] = ["<entire report missing>"]
            break
    return out


def _unparseable_judgment(rubric: str, exc: Exception) -> dict:
    """The null-score judgment used when the judge reply won't parse."""
    out: dict = {k: None for k in _RUBRIC_SCORE_KEYS[rubric]}
    for k in _RUBRIC_LIST_KEYS[rubric]:
        out[k] = []
    out["notes"] = f"judge reply unparseable: {exc}"
    return out


_JUDGE_SCRATCH_DIR: str | None = None


def _judge_scratch_dir() -> str:
    """A scratch cwd OUTSIDE the repo for the judge's CLI subprocess, created once.

    Mirrors the run path's `cwd=<clone>` insulation so the headless CLI discovers no
    project `.claude/` from the caller's directory. See `_call_judge` for why."""
    global _JUDGE_SCRATCH_DIR
    if _JUDGE_SCRATCH_DIR is None:
        _JUDGE_SCRATCH_DIR = tempfile.mkdtemp(prefix="roslyn-abtest-judge-")
    return _JUDGE_SCRATCH_DIR


async def _call_judge(
    prompt: str, model: str, system_prompt: str = JUDGE_SYSTEM_PROMPT
) -> str:
    """Run the judge model once with no tools; return its raw text reply.

    `cwd=_judge_scratch_dir()` insulates the headless CLI from the caller's
    project config: with `setting_sources=None` the SDK emits no `--setting-sources`
    flag, so the CLI falls back to discovering `user`+`project` settings from cwd —
    and a judge launched from the repo would load project settings/skills the run
    path never sees (runs are insulated by `cwd=<clone>`, outside the repo). The
    `stderr` callback captures the CLI's stderr so a non-zero exit surfaces the real
    reason instead of a bare "Command failed with exit code 1"."""
    stderr_lines: list[str] = []
    options = ClaudeAgentOptions(
        model=model,
        system_prompt=system_prompt,
        tools=[],
        allowed_tools=[],
        mcp_servers={},
        permission_mode="bypassPermissions",
        max_turns=1,
        setting_sources=None,
        skills=None,
        cwd=_judge_scratch_dir(),
        stderr=stderr_lines.append,
    )
    parts: list[str] = []
    result_text: str | None = None
    try:
        async for msg in query(prompt=prompt, options=options):
            if isinstance(msg, AssistantMessage):
                for block in msg.content:
                    if isinstance(block, TextBlock):
                        parts.append(block.text)
            elif isinstance(msg, ResultMessage):
                result_text = msg.result
    except Exception as exc:
        tail = "\n".join(stderr_lines[-25:]).strip()
        detail = f"; CLI stderr tail:\n{tail}" if tail else " (no CLI stderr captured)"
        raise RuntimeError(f"judge query failed: {exc!r}{detail}") from exc
    return "".join(parts) or (result_text or "")


async def judge_report(
    task: str, reference: str, candidate: str, model: str = JUDGE_MODEL
) -> dict:
    """Grade one candidate impact report against its reference; raises on unparseable reply."""
    prompt = _build_judge_prompt(task, reference, candidate)
    raw = await _call_judge(prompt, model)
    return _normalize_judgment(_extract_json(raw))


async def judge_method_report(
    task: str, reference: str, candidate: str, model: str = JUDGE_MODEL
) -> dict:
    """Grade one candidate method report against its reference; raises on unparseable reply."""
    prompt = _build_method_judge_prompt(task, reference, candidate)
    raw = await _call_judge(prompt, model, JUDGE_METHOD_SYSTEM_PROMPT)
    return _normalize_method_judgment(_extract_json(raw))


async def judge_breaking_report(
    task: str, reference: str, candidate: str, model: str = JUDGE_MODEL
) -> dict:
    """Grade one candidate breaking-change report against its reference."""
    prompt = _build_breaking_judge_prompt(task, reference, candidate)
    raw = await _call_judge(prompt, model, JUDGE_BREAKING_SYSTEM_PROMPT)
    return _normalize_breaking_judgment(_extract_json(raw))


async def judge_decompile_report(
    task: str, reference: str, candidate: str, model: str = JUDGE_MODEL
) -> dict:
    """Grade one candidate decompile/explain report against its reference."""
    prompt = _build_decompile_judge_prompt(task, reference, candidate)
    raw = await _call_judge(prompt, model, JUDGE_DECOMPILE_SYSTEM_PROMPT)
    return _normalize_decompile_judgment(_extract_json(raw))


async def judge_audit_report(
    task: str, reference: str, candidate: str, model: str = JUDGE_MODEL
) -> dict:
    """Grade one candidate codebase-audit report against its reference tables."""
    prompt = _build_audit_judge_prompt(task, reference, candidate)
    raw = await _call_judge(prompt, model, JUDGE_AUDIT_SYSTEM_PROMPT)
    return _normalize_audit_judgment(_extract_json(raw))


async def _dispatch_judge(
    rubric: str, task: str, reference: str, candidate: str, model: str
) -> dict:
    """Route one (task, rubric) to its judge function."""
    if rubric == "impact":
        return await judge_report(task, reference, candidate, model)
    if rubric == "method":
        return await judge_method_report(task, reference, candidate, model)
    if rubric == "breaking":
        return await judge_breaking_report(task, reference, candidate, model)
    if rubric == "decompile":
        return await judge_decompile_report(task, reference, candidate, model)
    if rubric == "audit":
        return await judge_audit_report(task, reference, candidate, model)
    raise ValueError(f"unknown rubric {rubric!r}")


def _find_report(
    timestamp_dir: Path, task: str, arm: str, rep: int, report_name: str
) -> str | None:
    """Locate a run's report artifact (rglob tolerates a `src/`-rooted write)."""
    artifacts = timestamp_dir / f"{task}-{arm}-{rep}.artifacts"
    if not artifacts.is_dir():
        return None
    for candidate in sorted(artifacts.rglob(report_name)):
        if candidate.is_file():
            return candidate.read_text(encoding="utf-8", errors="replace")
    return None


def _mean(values: list[float | None]) -> float | None:
    present = [v for v in values if v is not None]
    return statistics.mean(present) if present else None


def _fmt(value: float | None) -> str:
    return f"{value:.3f}" if value is not None else "-"


def _format_judge_summary(judgments: list[dict]) -> str:
    """Per-arm and per-task recall/accuracy tables with a with-vs-without delta."""
    if not judgments:
        return "(no judgments produced — no references or no report artifacts found)"

    by_arm: dict[str, list[dict]] = defaultdict(list)
    by_task_arm: dict[tuple[str, str], list[dict]] = defaultdict(list)
    for j in judgments:
        by_arm[j["arm"]].append(j)
        by_task_arm[(j["task"], j["arm"])].append(j)

    arms = sorted(by_arm)
    anchor = _pick_anchor(arms)

    out = ["## Per-arm correctness (all judged tasks pooled)", ""]
    out.append("| Arm | n | Mean site-recall | Mean verdict-accuracy |")
    out.append("|-----|---|------------------|-----------------------|")
    for arm in arms:
        items = by_arm[arm]
        recall = _mean([i["site_recall"] for i in items])
        acc = _mean([i["verdict_accuracy"] for i in items])
        flag = " (anchor)" if arm == anchor else ""
        out.append(f"| `{arm}`{flag} | {len(items)} | {_fmt(recall)} | {_fmt(acc)} |")

    out += ["", "## Per-task correctness (recall / accuracy, with-vs-without delta)", ""]
    out.append(
        "| Task | Arm | n | Site-recall | Verdict-accuracy "
        "| Δ recall vs anchor | Δ accuracy vs anchor |"
    )
    out.append("|------|-----|---|-------------|------------------|--------------------|----------------------|")
    tasks = sorted({t for t, _ in by_task_arm})
    for task in tasks:
        present = sorted(a for (t, a) in by_task_arm if t == task)
        task_anchor = _pick_anchor(present)
        anchor_recall = _mean([i["site_recall"] for i in by_task_arm[(task, task_anchor)]])
        anchor_acc = _mean([i["verdict_accuracy"] for i in by_task_arm[(task, task_anchor)]])
        for arm in present:
            items = by_task_arm[(task, arm)]
            recall = _mean([i["site_recall"] for i in items])
            acc = _mean([i["verdict_accuracy"] for i in items])
            if arm == task_anchor:
                d_recall = d_acc = "—"
            else:
                d_recall = _pct(recall, anchor_recall)
                d_acc = _pct(acc, anchor_acc)
            out.append(
                f"| {task} | `{arm}` | {len(items)} | {_fmt(recall)} | "
                f"{_fmt(acc)} | {d_recall} | {d_acc} |"
            )

    out += [
        "",
        "## Caveat",
        "",
        "Each (arm, rep) is judged once by a pinned model. The SDK exposes no "
        "temperature knob, so this reduces but does not remove judge variance — "
        "for the headline tasks (04/06/07), re-judge and average if a delta is "
        "marginal. The reference is the analyze_change_impact tool's own output, "
        "eyeballed once; a wrong reference biases both arms equally.",
    ]
    return "\n".join(out)


def _format_method_judge_summary(judgments: list[dict]) -> str:
    """Per-arm and per-task inbound/outbound-recall tables for the method tasks."""
    if not judgments:
        return "(no method judgments produced — no references or no report artifacts found)"

    by_arm: dict[str, list[dict]] = defaultdict(list)
    by_task_arm: dict[tuple[str, str], list[dict]] = defaultdict(list)
    for j in judgments:
        by_arm[j["arm"]].append(j)
        by_task_arm[(j["task"], j["arm"])].append(j)

    arms = sorted(by_arm)
    anchor = _pick_anchor(arms)

    out = ["## Per-arm method-comprehension correctness (all method tasks pooled)", ""]
    out.append("| Arm | n | Mean inbound-recall | Mean outbound-recall | Σ hallucinated callees |")
    out.append("|-----|---|---------------------|----------------------|------------------------|")
    for arm in arms:
        items = by_arm[arm]
        inbound = _mean([i["inbound_recall"] for i in items])
        outbound = _mean([i["outbound_recall"] for i in items])
        halluc = sum(len(i.get("hallucinated_callees") or []) for i in items)
        flag = " (anchor)" if arm == anchor else ""
        out.append(
            f"| `{arm}`{flag} | {len(items)} | {_fmt(inbound)} | {_fmt(outbound)} | {halluc} |"
        )

    out += [
        "",
        "## Per-task method correctness (inbound / outbound recall, with-vs-without delta)",
        "",
    ]
    out.append(
        "| Task | Arm | n | Inbound-recall | Outbound-recall "
        "| Δ inbound vs anchor | Δ outbound vs anchor | Σ halluc. callees |"
    )
    out.append(
        "|------|-----|---|----------------|-----------------"
        "|---------------------|----------------------|-------------------|"
    )
    tasks = sorted({t for t, _ in by_task_arm})
    for task in tasks:
        present = sorted(a for (t, a) in by_task_arm if t == task)
        task_anchor = _pick_anchor(present)
        anchor_inbound = _mean([i["inbound_recall"] for i in by_task_arm[(task, task_anchor)]])
        anchor_outbound = _mean([i["outbound_recall"] for i in by_task_arm[(task, task_anchor)]])
        for arm in present:
            items = by_task_arm[(task, arm)]
            inbound = _mean([i["inbound_recall"] for i in items])
            outbound = _mean([i["outbound_recall"] for i in items])
            halluc = sum(len(i.get("hallucinated_callees") or []) for i in items)
            if arm == task_anchor:
                d_inbound = d_outbound = "—"
            else:
                d_inbound = _pct(inbound, anchor_inbound)
                d_outbound = _pct(outbound, anchor_outbound)
            out.append(
                f"| {task} | `{arm}` | {len(items)} | {_fmt(inbound)} | "
                f"{_fmt(outbound)} | {d_inbound} | {d_outbound} | {halluc} |"
            )

    out += [
        "",
        "## Method-judge caveat",
        "",
        "Each (arm, rep) is judged once by a pinned model; recall/hallucination are "
        "approximate. Recall is scored only over methods present in BOTH the candidate "
        "and the reference, so a candidate's choice of which methods to document does not "
        "by itself tank recall. The reference is analyze_method's own output, eyeballed "
        "once; a wrong reference biases both arms equally.",
    ]
    return "\n".join(out)


def _format_metric_summary(
    judgments: list[dict], label: str, metrics: list[tuple[str, str]]
) -> str:
    """Compact per-arm and per-(task,arm) means for a rubric's score keys.

    Used for the prompt-efficacy rubrics (breaking, decompile), which run a single
    acceptance arm — the with-vs-without delta machinery of the impact/method
    summaries doesn't apply, so this just reports means and lets the SHIP/FIX gate
    read them."""
    if not judgments:
        return f"(no {label} judgments produced — no references or no report artifacts found)"

    by_arm: dict[str, list[dict]] = defaultdict(list)
    by_task_arm: dict[tuple[str, str], list[dict]] = defaultdict(list)
    for j in judgments:
        by_arm[j["arm"]].append(j)
        by_task_arm[(j["task"], j["arm"])].append(j)

    headers = " | ".join(lbl for lbl, _ in metrics)
    sep = "|".join(["------"] * (len(metrics) + 2))
    out = [f"## Per-arm {label} correctness (all {label} tasks pooled)", ""]
    out.append(f"| Arm | n | {headers} |")
    out.append(f"|{sep}|")
    for arm in sorted(by_arm):
        items = by_arm[arm]
        cells = " | ".join(_fmt(_mean([i.get(key) for i in items])) for _, key in metrics)
        out.append(f"| `{arm}` | {len(items)} | {cells} |")

    out += ["", f"## Per-task {label} correctness", ""]
    out.append(f"| Task | Arm | n | {headers} |")
    out.append(f"|------|{sep}|")
    for task, arm in sorted(by_task_arm):
        items = by_task_arm[(task, arm)]
        cells = " | ".join(_fmt(_mean([i.get(key) for i in items])) for _, key in metrics)
        out.append(f"| {task} | `{arm}` | {len(items)} | {cells} |")
    return "\n".join(out)


def _print_progress(rubric: str, task: str, arm: str, rep: int, record: dict) -> None:
    """One streaming line per judged run, showing the rubric's headline metrics."""
    headline = " ".join(f"{key}={_fmt(record.get(key))}" for key in _RUBRIC_SCORE_KEYS[rubric])
    print(f"  judged {task} | {arm} | rep {rep}: {headline}", flush=True)


async def _judge_with_retry(
    rubric: str, task: str, arm: str, rep: int, reference: str, candidate: str, model: str
) -> dict:
    """Dispatch one judgment; retry once when the reply parses as no JSON."""
    for attempt in (1, 2):
        try:
            return await _dispatch_judge(rubric, task, reference, candidate, model)
        except (ValueError, json.JSONDecodeError) as exc:
            # A truncated reply (paragraph-length `notes` overrunning the output cap) parses
            # as no JSON; one fresh call almost always repairs it (both 2026-07-02 incidents
            # did). A second failure records the null-scored judgment exactly as before.
            if attempt == 2:
                return _unparseable_judgment(rubric, exc)
            print(f"  retry {task} | {arm} | rep {rep}: unparseable judge reply", flush=True)
        except Exception as exc:
            # A query/transport failure (e.g. the CLI exits non-zero) must not abort the
            # whole judge run and lose the judgments already written. Record a null-scored
            # judgment carrying the captured reason, print it, continue.
            print(f"  ERROR judging {task} | {arm} | rep {rep}: {exc}", flush=True)
            return _unparseable_judgment(rubric, exc)
    raise AssertionError("unreachable: attempt 2 always returns")


async def run_judge(timestamp_dir: Path, model: str = JUDGE_MODEL) -> Path:
    """Judge every judged-task run (all four rubrics); write judgments + summary."""
    # Exclude our own *.judgment.json (they share the *.json glob and carry a
    # `judge_model` key) so a re-run is idempotent rather than judging prior judgments.
    judged_runs: list[tuple[dict, _Judged]] = []
    for r in load_runs(timestamp_dir):
        if "judge_model" in r:
            continue
        graded = _resolve_judged(r.get("task", ""))
        if graded is not None:
            judged_runs.append((r, graded))

    needed_refs = {g.reference for _, g in judged_runs}
    references: dict[str, str] = {}
    for stem in needed_refs:
        ref_path = TASKS_DIR / f"{stem}.reference.md"
        if ref_path.exists():
            references[stem] = ref_path.read_text(encoding="utf-8")
    if judged_runs and not references:
        print(
            "No <task>.reference.md files found for the judged runs — generate them "
            "first (run generate_references.py against the pinned clone). Nothing to judge.",
            flush=True,
        )

    buckets: dict[str, list[dict]] = {rubric: [] for rubric in _LLM_RUBRICS}
    for r, graded in sorted(judged_runs, key=lambda x: (x[0]["task"], x[0]["arm"], x[0]["rep"])):
        task, arm, rep = r["task"], r["arm"], r["rep"]
        if graded.reference not in references:
            print(f"  skip {task} | {arm} | rep {rep}: no reference", flush=True)
            continue

        candidate = _find_report(timestamp_dir, task, arm, rep, graded.report)
        if candidate is None:
            judgment = _missing_judgment(graded.rubric)
            present = False
        else:
            present = True
            judgment = await _judge_with_retry(
                graded.rubric, task, arm, rep, references[graded.reference], candidate, model
            )

        record = {
            "task": task,
            "arm": arm,
            "rep": rep,
            "rubric": graded.rubric,
            "candidate_present": present,
            "judge_model": model,
            **judgment,
        }
        (timestamp_dir / f"{task}-{arm}-{rep}.judgment.json").write_text(
            json.dumps(record, indent=2), encoding="utf-8"
        )
        buckets[graded.rubric].append(record)
        _print_progress(graded.rubric, task, arm, rep, record)

    total = sum(len(v) for v in buckets.values())
    md = [
        f"# Judge Summary — {timestamp_dir.name}",
        "",
        f"Judged runs: **{total}** ("
        + ", ".join(f"{len(buckets[r])} {r}" for r in sorted(_LLM_RUBRICS))
        + f")  \nJudge model: `{model}`",
        "",
        "# Impact tasks (analyze_change_impact / assess_impact)",
        "",
        _format_judge_summary(buckets["impact"]),
        "",
        "# Method-comprehension tasks (analyze_method)",
        "",
        _format_method_judge_summary(buckets["method"]),
        "",
        "# Breaking-change tasks (check_breaking_changes)",
        "",
        _format_metric_summary(
            buckets["breaking"],
            "breaking",
            [
                ("Recall", "recall"),
                ("Class-acc", "classification_accuracy"),
                ("Split", "split_correct"),
            ],
        ),
        "",
        "# Decompile tasks (decompile_symbol)",
        "",
        _format_metric_summary(
            buckets["decompile"],
            "decompile",
            [
                ("Factual", "factual_accuracy"),
                ("Grounding", "grounding"),
                ("Source-first", "source_first"),
                ("Σ hallucinations", "hallucinations"),
            ],
        ),
        "",
        "# Audit tasks (02-audit)",
        "",
        _format_metric_summary(
            buckets["audit"],
            "audit",
            [
                ("Fan-in recall", "fanin_recall"),
                ("Entity recall", "entity_recall"),
                ("God-class recall", "godclass_recall"),
                ("Count plausibility", "count_plausibility"),
            ],
        ),
        "",
    ]
    out_path = timestamp_dir / "judge-summary.md"
    out_path.write_text("\n".join(md), encoding="utf-8")
    print(f"\nJudge summary written to {out_path}", flush=True)
    return out_path


def main() -> None:
    import argparse
    import asyncio

    from .analyze import _resolve_timestamp_dir

    parser = argparse.ArgumentParser(prog="roslyn-abtest judge")
    parser.add_argument(
        "--timestamp", help="results/<timestamp>/ to judge. Default: most recent."
    )
    parser.add_argument(
        "--model",
        default=JUDGE_MODEL,
        help="Judge model. Overrides $ROZ_ABTEST_JUDGE_MODEL.",
    )
    args = parser.parse_args()
    asyncio.run(run_judge(_resolve_timestamp_dir(args.timestamp), args.model))


if __name__ == "__main__":
    main()
