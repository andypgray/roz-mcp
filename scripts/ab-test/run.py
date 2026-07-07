"""Backwards-compat shim. Prefer `roslyn-abtest run` (after `pip install -e .`)."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))
# Inject the `run` subcommand to preserve the pre-refactor invocation, but skip
# the inject when the caller already named a subcommand or asked for top-level
# help — otherwise `--help` would only show the `run` subparser and the
# `analyze` subcommand would be undiscoverable through the shim.
if len(sys.argv) == 1 or sys.argv[1] not in {"run", "analyze", "judge", "backfill", "-h", "--help"}:
    sys.argv.insert(1, "run")
from roslyn_abtest.cli import main
main()
