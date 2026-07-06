from __future__ import annotations

import shutil
from pathlib import Path
from unittest.mock import patch

import pytest

from roslyn_abtest.bootstrap import NUGET_ORG_CONFIG, ensure_fixture
from roslyn_abtest.fixtures import Fixture

_REPO_URL = "https://example.test/repo.git"


def _fixture(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Fixture:
    # cache_dir reads _CACHE_ROOT at call time, so redirecting it points the
    # whole fixture under tmp_path without touching %LOCALAPPDATA%. The
    # arbitrary repo_url/sha/subdir prove ensure_fixture is fixture-generic,
    # not nopCommerce-specific.
    monkeypatch.setattr("roslyn_abtest.fixtures._CACHE_ROOT", tmp_path)
    return Fixture(
        name="test",
        sha="abc123",
        repo_url=_REPO_URL,
        cache_subdir="TestFix",
        solution_relpath="src/NopCommerce.sln",
    )


def _make_solution(fixture: Fixture) -> Path:
    solution = fixture.solution_path
    solution.parent.mkdir(parents=True, exist_ok=True)
    solution.write_text("dummy sln", encoding="utf-8")
    return solution


def test_ensure_fixture_cache_present_makes_no_subprocess_calls(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    fix = _fixture(tmp_path, monkeypatch)
    _make_solution(fix)
    with patch("roslyn_abtest.bootstrap.subprocess.run") as run_mock, \
         patch("roslyn_abtest.bootstrap.shutil.rmtree") as rmtree_mock:
        ensure_fixture(fix)
    assert run_mock.call_count == 0
    assert rmtree_mock.call_count == 0


def test_ensure_fixture_cold_cache_issues_correct_sequence(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    fix = _fixture(tmp_path, monkeypatch)
    cache_dir = fix.cache_dir
    captured_calls: list[list[str]] = []

    def fake_run(args: list[str], *_, **__) -> None:
        captured_calls.append(list(args))
        # The git fetch+checkout don't actually populate the solution; the
        # restore mock just needs to find NuGet.config on disk (asserted below).
        if args[0] == "dotnet" and args[1] == "restore":
            assert (cache_dir / "NuGet.config").exists(), (
                "NuGet.config must exist on disk BEFORE the first `dotnet restore` — "
                "otherwise the user-level config may leak in"
            )
            assert (cache_dir / "NuGet.config").read_text(encoding="utf-8") == NUGET_ORG_CONFIG

    with patch("roslyn_abtest.bootstrap.subprocess.run", side_effect=fake_run):
        ensure_fixture(fix)

    # Order check: each subprocess.run call, in capture order. The repo_url and
    # sha come straight from the (arbitrary) fixture, not a nop-specific constant.
    assert captured_calls[0][:2] == ["git", "init"]
    assert "remote" in captured_calls[1] and _REPO_URL in captured_calls[1]
    assert "fetch" in captured_calls[2] and "abc123" in captured_calls[2]
    assert "checkout" in captured_calls[3]
    assert captured_calls[4][:2] == ["dotnet", "restore"]


def test_ensure_fixture_partial_cache_removes_before_cloning(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    # cache_dir exists but the solution doesn't (interrupted clone).
    fix = _fixture(tmp_path, monkeypatch)
    cache_dir = fix.cache_dir
    cache_dir.mkdir(parents=True)
    (cache_dir / "junk").write_text("leftover", encoding="utf-8")

    events: list[str] = []
    # Capture the real rmtree BEFORE patching — `patch("...shutil.rmtree")`
    # rewrites the shared `shutil` module's attribute, so a later
    # `shutil.rmtree(...)` would re-enter our mock and infinite-recurse.
    real_rmtree = shutil.rmtree

    def fake_run(args: list[str], *_, **__) -> None:
        events.append(f"run:{args[0]}:{args[1] if len(args) > 1 else ''}")

    def fake_rmtree(path: Path) -> None:
        events.append(f"rmtree:{path}")
        real_rmtree(path)

    with patch("roslyn_abtest.bootstrap.subprocess.run", side_effect=fake_run), \
         patch("roslyn_abtest.bootstrap.shutil.rmtree", side_effect=fake_rmtree):
        ensure_fixture(fix)

    rmtree_events = [e for e in events if e.startswith("rmtree:")]
    run_events = [e for e in events if e.startswith("run:")]
    assert len(rmtree_events) == 1, f"expected exactly one rmtree; got {rmtree_events}"
    # rmtree happens BEFORE any subprocess call (git or dotnet).
    assert events.index(rmtree_events[0]) < events.index(run_events[0])
    # And the first subprocess call is `git init`.
    assert run_events[0] == "run:git:init"
