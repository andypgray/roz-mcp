from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent.parent.parent
REPO_ROOT = SCRIPT_DIR.parent.parent
CONFIGS_DIR = SCRIPT_DIR / "configs"
TASKS_DIR = SCRIPT_DIR / "tasks"
PATCHES_DIR = SCRIPT_DIR / "patches"
RESULTS_DIR = SCRIPT_DIR / "results"
STRESS_TEST_CSPROJ = (
    REPO_ROOT / "tests" / "Zphil.Roz.StressTests" / "Zphil.Roz.StressTests.csproj"
)
