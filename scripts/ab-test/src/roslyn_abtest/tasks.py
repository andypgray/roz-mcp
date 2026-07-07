from __future__ import annotations

import sys
from pathlib import Path

import yaml

from .paths import TASKS_DIR


def is_task_brief(path: Path) -> bool:
    """True for a runnable task brief, False for a generated `<task>.reference.md` oracle.

    Oracles (written by generate_references.py) share the `tasks/*.md` glob but carry an
    HTML-comment provenance header instead of YAML frontmatter, so they are not tasks.
    """
    return not path.name.endswith(".reference.md")


def load_tasks(name: str) -> list[Path]:
    if name == "all":
        # 00-* is reserved for smoke/plumbing tasks - opt in explicitly.
        tasks = [
            p
            for p in sorted(TASKS_DIR.glob("*.md"))
            if is_task_brief(p) and not p.stem.startswith("00-")
        ]
        if not tasks:
            sys.exit(f"No task files found under {TASKS_DIR}")
        return tasks
    path = TASKS_DIR / f"{name}.md"
    if not path.exists():
        sys.exit(f"Task not found: {path}")
    return [path]


def parse_task(path: Path) -> tuple[dict, str]:
    """Return (frontmatter_metadata, body_markdown). Tolerates CRLF endings and a
    UTF-8 BOM (both common on Windows checkouts). Missing or malformed frontmatter
    raises — a silently-returned `{}` would skip every verifier and produce a JSON
    that looks identical to a successful run."""
    text = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")
    if not text.startswith("---\n"):
        raise ValueError(
            f"{path}: missing frontmatter (file must start with `---` line)"
        )
    end = text.find("\n---\n", 4)
    if end == -1:
        raise ValueError(
            f"{path}: unterminated frontmatter (no closing `---` line)"
        )
    metadata = yaml.safe_load(text[4:end]) or {}
    body = text[end + 5 :]
    if not body.strip():
        raise ValueError(f"{path}: task body is empty after frontmatter strip")
    return metadata, body
