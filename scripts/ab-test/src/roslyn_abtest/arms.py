from __future__ import annotations

import json
import sys
from typing import TypedDict

from .paths import CONFIGS_DIR


class ArmConfig(TypedDict, total=False):
    name: str
    description: str
    inject_claude_md_snippet: bool
    # Repo-relative path to a variant snippet to inject instead of the production
    # project-instructions-snippet.md. Absent -> the production snippet (see
    # runner.run_one). Doc-only today; no enforcement.
    claude_md_snippet_path: str
    mcp_servers: dict[str, dict]
    extra_allowed_tools: list[str]


def load_arm_configs(filter_names: list[str] | None) -> list[dict]:
    configs = [
        json.loads(p.read_text(encoding="utf-8"))
        for p in sorted(CONFIGS_DIR.glob("*.json"))
    ]
    if filter_names:
        configs = [c for c in configs if c["name"] in filter_names]
        missing = set(filter_names) - {c["name"] for c in configs}
        if missing:
            sys.exit(f"Unknown arm(s): {sorted(missing)}")
    return configs
