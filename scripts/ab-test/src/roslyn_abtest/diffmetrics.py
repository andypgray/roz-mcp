"""Pure diff-hygiene metrics: catch cost-metric gaming a raw +/- line count can't see.

`compute_diff_hygiene` parses a unified git diff per file, normalizes each changed line
(strip a leading UTF-8 BOM — both the clean U+FEFF form and the legacy cp1252 mojibake
form — then rstrip trailing whitespace/CR), and measures how much of the raw +/- churn is
verbatim-line resurrection (a line removed and re-added unchanged) rather than real
semantic change. A baseline that "wins" on cost by rewriting whole files (stripping BOMs,
flipping EOLs) shows near-total churn; a surgical edit shows ~none.

Known limits (same blind-spot class as the rest of the harness):
  - Untracked artifacts are invisible: an audit task that only writes AUDIT_REPORT.md
    (untracked) produces an empty tracked diff, so every field is zero — the same blind
    spot diff_loc already has.
  - A delete-and-recreate file rename does not churn-match across the two paths (its
    content is attributed to different files), so both arms inflate equally; the signal
    still survives (a whole-file rewrite churns ~99%, a surgical rename ~0%).
  - Churn detects verbatim-line resurrection (BOM/EOL/whole-file rewrite), NOT token-level
    edit distance — a reflowed line counts as fully semantic.
"""
from __future__ import annotations

from collections import Counter

# A UTF-8 BOM (bytes EF BB BF) surfaces two ways in a stored .diff depending on how run_git
# decoded git's output: the clean U+FEFF (encoding="utf-8", post-fix captures) and the legacy
# cp1252 mojibake (text=True without encoding, pre-fix captures — EF BB BF reinterpreted
# through cp1252 = the chars U+00EF U+00BB U+00BF). Detection accepts both so backfilling old
# diffs works. Kept all-ASCII in source (escape + decode) — a bare BOM is invisible/manglable.
_BOM_UNICODE = "﻿"
_BOM_MOJIBAKE = b"\xef\xbb\xbf".decode("cp1252")
_BOM_FORMS = (_BOM_UNICODE, _BOM_MOJIBAKE)

# A file is a "rewrite" when nearly all its churn is verbatim resurrection AND it is big
# enough that the pattern isn't the coincidence of a tiny hunk.
_REWRITE_CHURN_RATIO = 0.9
_REWRITE_MIN_RAW = 20


def _strip_leading_bom(text: str) -> tuple[str, bool]:
    """Return (text without a leading BOM, whether one was present) — either BOM form."""
    for form in _BOM_FORMS:
        if text.startswith(form):
            return text[len(form):], True
    return text, False


def _normalize(content: str) -> str:
    """Canonical form for churn matching: drop a leading BOM, rstrip CR + trailing space."""
    stripped, _ = _strip_leading_bom(content)
    return stripped.rstrip()


def _split_files(diff_text: str) -> list[list[str]]:
    """Split a unified diff into per-file line groups at `diff --git` boundaries.

    Lines before the first `diff --git` (a raw diff with no git header) form their own
    leading group, so no +/- content line is dropped from the raw count."""
    files: list[list[str]] = []
    current: list[str] | None = None
    for line in diff_text.splitlines():
        if line.startswith("diff --git "):
            current = []
            files.append(current)
            continue
        if current is None:
            current = []
            files.append(current)
        current.append(line)
    return files


def _is_removed(line: str) -> bool:
    """True for a removed content line (`-foo`), not the `---` file header."""
    return line.startswith("-") and not line.startswith("---")


def _is_added(line: str) -> bool:
    """True for an added content line (`+foo`), not the `+++` file header."""
    return line.startswith("+") and not line.startswith("+++")


def compute_diff_hygiene(diff_text: str) -> dict:
    """Measure verbatim-line churn and BOM strips in a unified git diff.

    Returns four fields merged into the run JSON:
      - diff_loc_semantic: raw +/- content lines MINUS churn (the real edited lines).
      - diff_churn_ratio: churn / raw, in [0,1]; 0.0 when the diff has no +/- lines.
      - diff_bom_stripped_files: files where a line's leading UTF-8 BOM was dropped.
      - diff_rewritten_files: files whose churn is ~all resurrection (>=0.9) over >=20 raw lines.
    Raw (= diff_loc_semantic + churn) equals runner.count_diff_loc(diff_text) by construction.
    """
    raw_total = 0
    churn_total = 0
    bom_stripped_files = 0
    rewritten_files = 0

    for group in _split_files(diff_text):
        removed_raw = [line[1:] for line in group if _is_removed(line)]
        added_raw = [line[1:] for line in group if _is_added(line)]
        raw_file = len(removed_raw) + len(added_raw)
        if raw_file == 0:
            continue
        raw_total += raw_file

        removed_norm = Counter(_normalize(c) for c in removed_raw)
        added_norm = Counter(_normalize(c) for c in added_raw)
        # Multiset intersection: each verbatim-resurrected line counts once as removed and
        # once as added, so the file's churn is 2x the intersection size.
        churn_file = 2 * sum((removed_norm & added_norm).values())
        churn_total += churn_file

        if raw_file >= _REWRITE_MIN_RAW and churn_file / raw_file >= _REWRITE_CHURN_RATIO:
            rewritten_files += 1

        # A BOM was stripped when a removed line carried a leading BOM whose de-BOMed
        # content reappears as an added line that has NO BOM.
        added_without_bom = set()
        for content in added_raw:
            stripped, had_bom = _strip_leading_bom(content)
            if not had_bom:
                added_without_bom.add(stripped.rstrip())
        for content in removed_raw:
            stripped, had_bom = _strip_leading_bom(content)
            if had_bom and stripped.rstrip() in added_without_bom:
                bom_stripped_files += 1
                break

    churn_ratio = round(churn_total / raw_total, 4) if raw_total else 0.0
    return {
        "diff_loc_semantic": raw_total - churn_total,
        "diff_churn_ratio": churn_ratio,
        "diff_bom_stripped_files": bom_stripped_files,
        "diff_rewritten_files": rewritten_files,
    }
