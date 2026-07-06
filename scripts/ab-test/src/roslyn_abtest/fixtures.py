from __future__ import annotations

import os
import sys
from dataclasses import dataclass
from pathlib import Path

from .manifest import read_nop_commit_sha

_CACHE_ROOT = Path(os.environ["LOCALAPPDATA"]) / "Zphil.Roz"


@dataclass(frozen=True)
class Fixture:
    """A pinned, git-cloneable code fixture the harness runs tasks against."""

    name: str
    sha: str
    repo_url: str
    cache_subdir: str       # second path segment under the cache root, e.g. "nopCommerce"
    solution_relpath: str   # forward-slash path from clone root, e.g. "src/Spectre.Console.slnx"

    @property
    def cache_dir(self) -> Path:
        # Reads the module global at call time so tests can monkeypatch _CACHE_ROOT.
        return _CACHE_ROOT / self.cache_subdir / self.sha

    @property
    def solution_path(self) -> Path:
        return self.cache_dir / self.solution_relpath


FIXTURES: dict[str, Fixture] = {
    "nopcommerce": Fixture(
        name="nopcommerce",
        sha=read_nop_commit_sha(),  # source-of-truth: stress-test csproj <NopCommitSha>
        repo_url="https://github.com/nopSolutions/nopCommerce.git",
        cache_subdir="nopCommerce",
        solution_relpath="src/NopCommerce.sln",
    ),
    "spectre-console": Fixture(
        name="spectre-console",
        sha="b18ccb1b00118b7fb81b8fd03fabbdd2324ad8ee",  # tag 0.55.2 (2026-04-17)
        repo_url="https://github.com/spectreconsole/spectre.console.git",
        cache_subdir="Spectre.Console",
        solution_relpath="src/Spectre.Console.slnx",
    ),
}


# Tasks that omit a `fixture:` key fall back to this. Owned here, not duplicated
# at each call site, so the cli pre-flight and runner.run_one can't drift apart.
DEFAULT_FIXTURE = "nopcommerce"


def get_fixture(name: str | None = None) -> Fixture:
    """Resolve a fixture name (None → DEFAULT_FIXTURE); exit with the valid set on miss."""
    try:
        return FIXTURES[name or DEFAULT_FIXTURE]
    except KeyError:
        sys.exit(f"Unknown fixture {name!r}. Valid fixtures: {', '.join(sorted(FIXTURES))}")
