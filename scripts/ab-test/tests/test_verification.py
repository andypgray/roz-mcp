from __future__ import annotations

from pathlib import Path
from types import SimpleNamespace

import pytest

from roslyn_abtest.diffmetrics import _BOM_UNICODE
from roslyn_abtest.verification import (
    VERIFIERS,
    VerifyContext,
    validate_verification_order,
)


def _ctx(
    tmp_path: Path, result: dict | None = None, diff_full: str = ""
) -> VerifyContext:
    return VerifyContext(
        cache_dir=tmp_path,
        solution_path=tmp_path / "src" / "Foo.sln",
        result=result or {},
        diff_full=diff_full,
    )


# ------------------------- validate_verification_order -------------------------


def test_validate_verification_order_empty_list_does_not_raise() -> None:
    validate_verification_order([], Path("dummy.md"))


def test_validate_verification_order_build_first_then_token_residual_ok() -> None:
    specs = [
        {"type": "build"},
        {"type": "token-residual", "token": "X"},
    ]
    validate_verification_order(specs, Path("dummy.md"))


def test_validate_verification_order_build_after_other_raises() -> None:
    specs = [
        {"type": "token-residual", "token": "X"},
        {"type": "build"},
    ]
    with pytest.raises(ValueError, match="build"):
        validate_verification_order(specs, Path("dummy.md"))


def test_validate_verification_order_slug_collision_raises() -> None:
    specs = [
        {"type": "token-residual", "token": "My-Token"},
        {"type": "token-residual", "token": "mytoken"},
    ]
    with pytest.raises(ValueError, match="slug"):
        validate_verification_order(specs, Path("dummy.md"))


def test_validate_verification_order_accessibility_slug_collision_raises() -> None:
    specs = [
        {"type": "accessibility-is", "symbolName": "Foo", "expected": "internal"},
        {"type": "accessibility-is", "symbolName": "Foo", "expected": "private"},
    ]
    with pytest.raises(ValueError, match="accessibility-is"):
        validate_verification_order(specs, Path("dummy.md"))


def test_validate_verification_order_accessibility_distinct_symbols_ok() -> None:
    specs = [
        {"type": "accessibility-is", "symbolName": "Foo", "expected": "internal"},
        {"type": "accessibility-is", "symbolName": "Bar", "expected": "private"},
    ]
    validate_verification_order(specs, Path("dummy.md"))


# ----------------------------- _verify_file_exists -----------------------------


def test_verify_file_exists_reports_present_and_missing(tmp_path: Path) -> None:
    (tmp_path / "a.cs").write_text("// a", encoding="utf-8")
    ctx = _ctx(tmp_path)
    spec = {"type": "file-exists", "paths": ["a.cs", "b.cs"]}
    out = VERIFIERS["file-exists"](ctx, spec)
    fe = out["file_exists"]
    assert fe["all_present"] is False
    assert fe["missing"] == ["b.cs"]
    assert fe["checked"] == ["a.cs", "b.cs"]


# ----------------------------- _verify_loc_delta_max ----------------------------


def test_verify_loc_delta_max_plain_branch_pass(tmp_path: Path) -> None:
    ctx = _ctx(tmp_path, {"diff_loc": 5})
    out = VERIFIERS["loc-delta-max"](ctx, {"type": "loc-delta-max", "max": 10})
    assert out["loc_delta_max_value"] == 5
    assert out["loc_delta_max_threshold"] == 10
    assert out["loc_delta_max_pass"] is True


def test_verify_loc_delta_max_plain_branch_fail(tmp_path: Path) -> None:
    ctx = _ctx(tmp_path, {"diff_loc": 5})
    out = VERIFIERS["loc-delta-max"](ctx, {"type": "loc-delta-max", "max": 3})
    assert out["loc_delta_max_pass"] is False


def test_verify_loc_delta_max_ignore_branch_uses_filtered_git_diff(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(
        "roslyn_abtest.runner.run_git",
        lambda *args, **kw: SimpleNamespace(stdout="fake diff body"),
    )
    monkeypatch.setattr("roslyn_abtest.runner.count_diff_loc", lambda _diff: 7)
    ctx = _ctx(tmp_path, {"diff_loc": 999})  # plain-branch value must be ignored.
    spec = {
        "type": "loc-delta-max",
        "max": 10,
        "ignore_tracked_paths": ["AUDIT_REPORT.md"],
    }
    out = VERIFIERS["loc-delta-max"](ctx, spec)
    assert out["loc_delta_max_value"] == 7
    assert out["loc_delta_max_pass"] is True


# ----------------------------- _verify_loc_delta_min ----------------------------


def test_verify_loc_delta_min_pass(tmp_path: Path) -> None:
    ctx = _ctx(tmp_path, {"diff_loc": 50})
    out = VERIFIERS["loc-delta-min"](ctx, {"type": "loc-delta-min", "min": 20})
    assert out["loc_delta_min_value"] == 50
    assert out["loc_delta_min_threshold"] == 20
    assert out["loc_delta_min_pass"] is True


def test_verify_loc_delta_min_fail(tmp_path: Path) -> None:
    ctx = _ctx(tmp_path, {"diff_loc": 5})
    out = VERIFIERS["loc-delta-min"](ctx, {"type": "loc-delta-min", "min": 20})
    assert out["loc_delta_min_pass"] is False


# -------------------------------- _verify_build --------------------------------


def test_verify_build_passes_when_exit_matches_expected(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr("roslyn_abtest.runner.build_clone", lambda _sln: (0, "ok", ""))
    ctx = _ctx(tmp_path)
    out = VERIFIERS["build"](ctx, {"type": "build", "expected_exit": 0})
    assert out["build_exit_code"] == 0
    assert out["build_expected_exit"] == 0
    assert out["build_passed"] is True


def test_verify_build_fails_when_exit_does_not_match_expected(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr("roslyn_abtest.runner.build_clone", lambda _sln: (0, "ok", ""))
    ctx = _ctx(tmp_path)
    out = VERIFIERS["build"](ctx, {"type": "build", "expected_exit": 1})
    assert out["build_passed"] is False


# ---------------------------- _verify_token_residual ----------------------------


def test_verify_token_residual_emits_count_and_max_pass(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr("roslyn_abtest.runner.count_token_in_src", lambda _sln, _t: 3)
    ctx = _ctx(tmp_path)
    spec = {"type": "token-residual", "token": "Foo", "max_count": 5}
    out = VERIFIERS["token-residual"](ctx, spec)
    assert out["foo_residual_count"] == 3
    assert out["foo_residual_max_pass"] is True


def test_verify_token_residual_missing_token_raises(tmp_path: Path) -> None:
    ctx = _ctx(tmp_path)
    with pytest.raises(ValueError):
        VERIFIERS["token-residual"](ctx, {"type": "token-residual"})


def test_verify_token_residual_unsupported_scope_raises(tmp_path: Path) -> None:
    ctx = _ctx(tmp_path)
    spec = {"type": "token-residual", "token": "Foo", "scope": "dir"}
    with pytest.raises(ValueError):
        VERIFIERS["token-residual"](ctx, spec)


# ------------------------------- VERIFIERS index -------------------------------


def test_verifiers_registry_has_expected_keys() -> None:
    assert set(VERIFIERS.keys()) == {
        "build",
        "token-residual",
        "file-exists",
        "loc-delta-max",
        "loc-delta-min",
        "diff-absent",
        "diff-contains",
        "diff-hygiene",
        "diagnostics-delta",
        "accessibility-is",
    }


# ------------------------------ _verify_diff_hygiene ----------------------------


def test_verify_diff_hygiene_passes_clean_rename(tmp_path: Path) -> None:
    diff = "\n".join([
        "diff --git a/F.cs b/F.cs", "--- a/F.cs", "+++ b/F.cs", "@@ -1,1 +1,1 @@",
        "-    private readonly IAffiliateService _svc;",
        "+    private readonly IAffiliateManagementService _svc;",
    ])
    ctx = _ctx(tmp_path, diff_full=diff)
    spec = {"type": "diff-hygiene", "max_churn_ratio": 0.5, "forbid_bom_strip": True}
    out = VERIFIERS["diff-hygiene"](ctx, spec)
    assert out["diff_hygiene_pass"] is True
    assert out["diff_hygiene_churn_ratio"] == 0.0
    assert out["diff_hygiene_violations"] == []


def test_verify_diff_hygiene_fails_on_high_churn_rewrite(tmp_path: Path) -> None:
    removed = [f"-line {i}" for i in range(25)]
    added = [f"+line {i}" for i in range(25)]
    diff = "\n".join([
        "diff --git a/F.cs b/F.cs", "--- a/F.cs", "+++ b/F.cs", "@@ -1,25 +1,25 @@",
        *removed, *added,
    ])
    ctx = _ctx(tmp_path, diff_full=diff)
    out = VERIFIERS["diff-hygiene"](ctx, {"type": "diff-hygiene", "max_churn_ratio": 0.5})
    assert out["diff_hygiene_pass"] is False
    assert out["diff_hygiene_churn_ratio"] >= 0.95
    assert out["diff_hygiene_rewritten_files"] == 1


def test_verify_diff_hygiene_fails_on_bom_strip_even_within_churn_budget(tmp_path: Path) -> None:
    # churn is 1.0 but max_churn_ratio permits it; the stripped BOM alone must fail the run.
    diff = "\n".join([
        "diff --git a/F.cs b/F.cs", "--- a/F.cs", "+++ b/F.cs", "@@ -1,1 +1,1 @@",
        f"-{_BOM_UNICODE}using System;", "+using System;",
    ])
    ctx = _ctx(tmp_path, diff_full=diff)
    spec = {"type": "diff-hygiene", "max_churn_ratio": 1.0, "forbid_bom_strip": True}
    out = VERIFIERS["diff-hygiene"](ctx, spec)
    assert out["diff_hygiene_bom_stripped_files"] == 1
    assert out["diff_hygiene_pass"] is False


def test_verify_diff_hygiene_bare_spec_reports_without_failing(tmp_path: Path) -> None:
    # No thresholds -> the verifier just records metrics and always passes.
    ctx = _ctx(tmp_path, diff_full="diff --git a/F.cs b/F.cs\n@@ -1 +1 @@\n-a\n+b")
    out = VERIFIERS["diff-hygiene"](ctx, {"type": "diff-hygiene"})
    assert out["diff_hygiene_pass"] is True
    assert out["diff_hygiene_violations"] == []


# ------------------------------- _verify_diff_absent ----------------------------


def test_verify_diff_absent_passes_when_no_pattern_matches(tmp_path: Path) -> None:
    diff = "+    var x = ComputeThing();\n-    var y = old;\n+++ b/Foo.cs"
    ctx = _ctx(tmp_path, diff_full=diff)
    spec = {"type": "diff-absent", "patterns": ["#pragma warning disable", "<NoWarn"]}
    out = VERIFIERS["diff-absent"](ctx, spec)
    assert out["diff_absent_pass"] is True
    assert out["diff_absent_violation_count"] == 0


def test_verify_diff_absent_fails_on_added_suppression(tmp_path: Path) -> None:
    diff = (
        "+    #pragma warning disable CS0168\n"
        "+    int unused;\n"
        "+    #pragma warning restore CS0168"
    )
    ctx = _ctx(tmp_path, diff_full=diff)
    spec = {"type": "diff-absent", "patterns": ["#pragma warning disable", "SuppressMessage"]}
    out = VERIFIERS["diff-absent"](ctx, spec)
    assert out["diff_absent_pass"] is False
    assert out["diff_absent_violation_count"] == 1
    assert out["diff_absent_violations"][0]["pattern"] == "#pragma warning disable"


def test_verify_diff_absent_ignores_plusplusplus_header(tmp_path: Path) -> None:
    # A `+++ b/<NoWarn>.cs`-style header line must not count as an added line.
    ctx = _ctx(tmp_path, diff_full="+++ b/src/<NoWarn>Thing.cs\n context")
    out = VERIFIERS["diff-absent"](ctx, {"type": "diff-absent", "patterns": ["<NoWarn"]})
    assert out["diff_absent_pass"] is True


def test_verify_diff_absent_requires_patterns(tmp_path: Path) -> None:
    with pytest.raises(ValueError):
        VERIFIERS["diff-absent"](_ctx(tmp_path), {"type": "diff-absent"})


# ------------------------------ _verify_diff_contains ---------------------------


def test_verify_diff_contains_pass_when_all_present(tmp_path: Path) -> None:
    ctx = _ctx(tmp_path, diff_full="+    await Foo().ConfigureAwait(false);\n+    other")
    out = VERIFIERS["diff-contains"](ctx, {"type": "diff-contains", "patterns": ["ConfigureAwait"]})
    assert out["diff_contains_pass"] is True
    assert out["diff_contains_missing"] == []


def test_verify_diff_contains_fail_when_missing(tmp_path: Path) -> None:
    ctx = _ctx(tmp_path, diff_full="+    nothing relevant here")
    out = VERIFIERS["diff-contains"](ctx, {"type": "diff-contains", "patterns": ["ConfigureAwait"]})
    assert out["diff_contains_pass"] is False
    assert out["diff_contains_missing"] == ["ConfigureAwait"]


# ---------------------------- _verify_diagnostics_delta -------------------------


_BUILD_WITH_TWO_NOPCORE_WARNINGS = (
    r"C:\x\Foo.cs(12,13): warning CS0168: 'x' is declared but never used [C:\x\Nop.Core.csproj]"
    "\n"
    r"C:\x\Bar.cs(5,9): warning CS0219: 'y' is assigned but never used [C:\x\Nop.Core.csproj]"
    "\n"
    r"C:\x\Baz.cs(7,1): warning CS1591: missing XML comment [C:\x\Nop.Web.csproj]"
    "\n"
)


def _stub_build(monkeypatch: pytest.MonkeyPatch, stdout: str) -> None:
    # diagnostics-delta runs its own clean build_clone; stub it to return canned output.
    monkeypatch.setattr("roslyn_abtest.runner.build_clone", lambda *_a, **_k: (0, stdout, ""))


def test_verify_diagnostics_delta_counts_targeted_in_scope(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _stub_build(monkeypatch, _BUILD_WITH_TWO_NOPCORE_WARNINGS)
    spec = {
        "type": "diagnostics-delta",
        "severity": "warning",
        "scope": "Nop.Core",
        "ids": ["CS0168", "CS0219"],
        "max_remaining": 0,
    }
    out = VERIFIERS["diagnostics-delta"](_ctx(tmp_path), spec)
    assert out["diagnostics_delta_remaining"] == 2
    assert out["diagnostics_delta_pass"] is False


def test_verify_diagnostics_delta_passes_when_cleared(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _stub_build(monkeypatch, "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n")
    spec = {"type": "diagnostics-delta", "ids": ["CS0168"], "max_remaining": 0}
    out = VERIFIERS["diagnostics-delta"](_ctx(tmp_path), spec)
    assert out["diagnostics_delta_remaining"] == 0
    assert out["diagnostics_delta_pass"] is True


def test_verify_diagnostics_delta_dedups_repeated_lines(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    line = r"C:\x\Foo.cs(12,13): warning CS0168: 'x' [C:\x\Nop.Core.csproj]"
    _stub_build(monkeypatch, f"{line}\n{line}\n{line}\n")
    spec = {"type": "diagnostics-delta", "ids": ["CS0168"], "max_remaining": 5}
    out = VERIFIERS["diagnostics-delta"](_ctx(tmp_path), spec)
    assert out["diagnostics_delta_remaining"] == 1


def test_verify_diagnostics_delta_scope_excludes_other_projects(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _stub_build(monkeypatch, _BUILD_WITH_TWO_NOPCORE_WARNINGS)
    spec = {"type": "diagnostics-delta", "scope": "Nop.Web", "max_remaining": 0}
    out = VERIFIERS["diagnostics-delta"](_ctx(tmp_path), spec)
    # Only the CS1591 line is in Nop.Web.
    assert out["diagnostics_delta_remaining"] == 1


def test_verify_diagnostics_delta_requires_max_remaining(tmp_path: Path) -> None:
    # The max_remaining guard fires before any build, so no stub is needed.
    with pytest.raises(ValueError):
        VERIFIERS["diagnostics-delta"](_ctx(tmp_path), {"type": "diagnostics-delta"})


# ----------------------------- _verify_accessibility_is -------------------------


def test_parse_access_tag_reads_first_tag() -> None:
    from roslyn_abtest.verification import _parse_access_tag

    # Bracketed form (member listings, depth > 0).
    assert _parse_access_tag("Found 1 symbol:\n[internal method] Task Foo()") == "internal"
    assert _parse_access_tag("[public class] Bar") == "public"
    assert _parse_access_tag("[protected internal property] int P") == "protected internal"
    # Unbracketed top-level result form — what find_symbol actually returns for a single symbol.
    assert _parse_access_tag(
        'Found symbol(s) matching "Foo" (1):\n\n1. public static method string Foo()'
    ) == "public"
    assert _parse_access_tag("1. internal sealed class Bar") == "internal"
    assert _parse_access_tag("2. private method int Baz()") == "private"
    assert _parse_access_tag("no tag at all") is None


def test_verify_accessibility_is_pass(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(
        "roslyn_abtest.verification.query_symbol_text",
        lambda *_a, **_k: "1. internal method Task Foo()",
    )
    spec = {"type": "accessibility-is", "symbolName": "Foo", "expected": "Internal"}
    out = VERIFIERS["accessibility-is"](_ctx(tmp_path), spec)
    # Keys are slugged by symbol so multiple checks in one task don't collide.
    assert out["accessibility_is_foo_actual"] == "internal"
    assert out["accessibility_is_foo_pass"] is True


def test_verify_accessibility_is_keys_slugged_per_symbol_no_collision(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # Two checks on different symbols must produce independent, non-overlapping keys.
    monkeypatch.setattr(
        "roslyn_abtest.verification.query_symbol_text",
        lambda _sln, sym, *_a, **_k: f"1. internal method {sym}()",
    )
    a = VERIFIERS["accessibility-is"](
        _ctx(tmp_path), {"type": "accessibility-is", "symbolName": "Alpha", "expected": "internal"}
    )
    b = VERIFIERS["accessibility-is"](
        _ctx(tmp_path), {"type": "accessibility-is", "symbolName": "Beta", "expected": "private"}
    )
    merged = {**a, **b}
    assert merged["accessibility_is_alpha_pass"] is True
    assert merged["accessibility_is_beta_pass"] is False  # actual internal != expected private
    assert "accessibility_is_alpha_actual" in merged and "accessibility_is_beta_actual" in merged


def test_verify_accessibility_is_fail_when_still_broad(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(
        "roslyn_abtest.verification.query_symbol_text",
        lambda *_a, **_k: "1. public method Task Foo()",
    )
    spec = {"type": "accessibility-is", "symbolName": "Foo", "expected": "internal"}
    out = VERIFIERS["accessibility-is"](_ctx(tmp_path), spec)
    assert out["accessibility_is_foo_pass"] is False


def test_verify_accessibility_is_requires_symbol_and_expected(tmp_path: Path) -> None:
    ctx = _ctx(tmp_path)
    with pytest.raises(ValueError):
        VERIFIERS["accessibility-is"](ctx, {"type": "accessibility-is", "expected": "internal"})
    with pytest.raises(ValueError):
        VERIFIERS["accessibility-is"](ctx, {"type": "accessibility-is", "symbolName": "Foo"})
