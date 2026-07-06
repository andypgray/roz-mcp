from __future__ import annotations

from pathlib import Path

import pytest

from roslyn_abtest.fixtures import FIXTURES, get_fixture
from roslyn_abtest.manifest import read_nop_commit_sha
from roslyn_abtest.paths import TASKS_DIR
from roslyn_abtest.tasks import is_task_brief, parse_task


def test_get_fixture_unknown_name_exits() -> None:
    with pytest.raises(SystemExit):
        get_fixture("nope")


def test_nopcommerce_sha_tracks_stress_test_csproj() -> None:
    assert FIXTURES["nopcommerce"].sha == read_nop_commit_sha()


def test_nopcommerce_cache_dir_preserves_shared_layout() -> None:
    # The C# stress tests clone into ...\Zphil.Roz\nopCommerce\<sha>; the
    # harness must land char-for-char in the same place to share the clone.
    cache_dir = FIXTURES["nopcommerce"].cache_dir
    assert cache_dir.parts[-2:] == ("nopCommerce", read_nop_commit_sha())


def test_spectre_console_fixture_shape() -> None:
    fix = FIXTURES["spectre-console"]
    assert fix.cache_subdir == "Spectre.Console"
    assert fix.solution_relpath.endswith(".slnx")


@pytest.mark.parametrize("key", sorted(FIXTURES))
def test_registry_entry_name_matches_key(key: str) -> None:
    # Fixture.name feeds bootstrap status messages and result["fixture"] but never
    # sees the dict key it's stored under — guard the two against silently drifting.
    assert FIXTURES[key].name == key


def test_get_fixture_none_resolves_to_default() -> None:
    assert get_fixture(None).name == "nopcommerce"


@pytest.mark.parametrize(
    "task_path",
    sorted(p for p in TASKS_DIR.glob("*.md") if is_task_brief(p)),
    ids=lambda p: p.stem,
)
def test_every_task_declares_a_known_fixture(task_path: Path) -> None:
    metadata, _ = parse_task(task_path)
    # get_fixture applies the default when "fixture" is unset; raises SystemExit on a typo.
    get_fixture(metadata.get("fixture"))
