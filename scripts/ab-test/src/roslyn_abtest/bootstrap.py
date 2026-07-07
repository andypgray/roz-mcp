from __future__ import annotations

import shutil
import subprocess
import sys
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from .fixtures import Fixture

NUGET_ORG_CONFIG = """<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"""


def _status(msg: str) -> None:
    """Bootstrap progress to stderr — clone+restore can take 5-15 min, mustn't be silent."""
    print(f"[roslyn-abtest bootstrap] {msg}", file=sys.stderr, flush=True)


def _git(*args: str) -> None:
    subprocess.run(["git", *args], check=True)


def ensure_fixture(fixture: Fixture) -> None:
    """Clone `fixture` at its pinned sha into its cache dir if not already present.

    For the nopCommerce fixture this mirrors the AcquireNopCommerce MSBuild target in
    Zphil.Roz.StressTests.csproj byte-for-byte (same git sequence, same
    `--source https://api.nuget.org/v3/index.json` restore arg) so users running either
    the C# stress tests or the Python harness share a single clone.
    """
    cache_dir = fixture.cache_dir
    solution = fixture.solution_path
    if solution.exists():
        return

    if cache_dir.exists():
        # Interrupted clone — match MSBuild's <RemoveDir> semantics.
        _status(f"Removing partial cache at {cache_dir}")
        shutil.rmtree(cache_dir)

    _status(f"Cloning {fixture.name} at {fixture.sha} into {cache_dir}")
    cache_dir.mkdir(parents=True)

    _git("init", str(cache_dir))
    _git("-C", str(cache_dir), "remote", "add", "origin", fixture.repo_url)
    _git("-C", str(cache_dir), "fetch", "--depth", "1", "origin", fixture.sha)
    _git("-C", str(cache_dir), "checkout", "FETCH_HEAD")

    (cache_dir / "global.json").unlink(missing_ok=True)

    # Pin nuget.org-only config BEFORE restore. The existing reset_clone wrote
    # this only after restore, exposing the first-ever restore to user-level
    # NuGet config that may point at private feeds.
    (cache_dir / "NuGet.config").write_text(NUGET_ORG_CONFIG, encoding="utf-8")

    _status(f"Restoring {fixture.name} solution (this can take several minutes)")
    subprocess.run(
        ["dotnet", "restore", str(solution),
         "--source", "https://api.nuget.org/v3/index.json"],
        check=True,
    )

    _status(f"{fixture.name} ready at {cache_dir}")
