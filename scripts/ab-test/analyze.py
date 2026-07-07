"""Backwards-compat shim. Prefer `roslyn-abtest analyze` (after `pip install -e .`)."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent / "src"))
if len(sys.argv) == 1 or sys.argv[1] not in {"run", "analyze", "judge", "backfill", "-h", "--help"}:
    sys.argv.insert(1, "analyze")
from roslyn_abtest.cli import main
main()
