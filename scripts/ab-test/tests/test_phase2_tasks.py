from __future__ import annotations

from pathlib import Path

import pytest

from roslyn_abtest.paths import PATCHES_DIR, TASKS_DIR
from roslyn_abtest.tasks import parse_task
from roslyn_abtest.verification import VERIFIERS, validate_verification_order

# The Phase-2 prompt-efficacy tasks (the four new prompts; assess/fix are Phase 1).
PHASE2_TASKS = [
    "P3-check-breaking-changes",
    "P4a-decompile-path",
    "P4b-decompile-pascalize",
    "P4c-decompile-markdig",
    "P5-cleanup-dead-code",
    "P6-tighten-accessibility",
]

_LLM_RUBRICS = {"impact", "method", "breaking", "decompile"}


@pytest.mark.parametrize("stem", PHASE2_TASKS)
def test_phase2_task_is_wellformed(stem: str) -> None:
    """Each Phase-2 task parses, declares a prompt, uses only known verifiers in a valid
    order, references an existing patch, and (if LLM-judged) an existing reference oracle."""
    path = TASKS_DIR / f"{stem}.md"
    assert path.is_file(), f"{stem}.md not found"
    metadata, body = parse_task(path)

    assert metadata.get("prompt"), f"{stem}: missing `prompt:` (it is a prompt-efficacy task)"
    assert body.strip(), f"{stem}: empty documentation body"

    specs = list(metadata.get("verification") or [])
    validate_verification_order(specs, path)  # raises if build is not first, or slug collision
    for spec in specs:
        assert spec.get("type") in VERIFIERS, f"{stem}: unknown verifier {spec.get('type')!r}"

    patch_rel = metadata.get("setup_patch")
    if patch_rel:
        patch = PATCHES_DIR / Path(patch_rel).name
        assert patch.is_file(), f"{stem}: setup_patch {patch} missing"
        # A committed planted patch is needed for the run diff to show only the agent's edits.
        assert metadata.get("setup_commit") is True, f"{stem}: setup_patch needs setup_commit: true"

    rubric = str(metadata.get("rubric") or "").strip().lower()
    if rubric in _LLM_RUBRICS:
        ref = metadata.get("reference") or stem
        ref_path = TASKS_DIR / f"{ref}.reference.md"
        assert ref_path.is_file(), f"{stem}: LLM-judged but reference {ref_path.name} missing"


def test_phase2_token_residual_tokens_are_distinct() -> None:
    """No two token-residual tokens in one task collapse to the same slug (which would
    overwrite each other's residual_count key) — covered by validate_verification_order,
    asserted here per task for a clearer failure."""
    for stem in PHASE2_TASKS:
        metadata, _ = parse_task(TASKS_DIR / f"{stem}.md")
        validate_verification_order(list(metadata.get("verification") or []), Path(f"{stem}.md"))
