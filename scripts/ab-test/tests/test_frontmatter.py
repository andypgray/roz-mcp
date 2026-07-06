from __future__ import annotations

from pathlib import Path

import pytest

from roslyn_abtest.paths import TASKS_DIR
from roslyn_abtest.tasks import parse_task


@pytest.mark.parametrize(
    "task_name",
    ["00-smoke", "01-feature-add", "02-audit", "03-refactor-rename"],
)
def test_parse_task_real_files_yield_metadata_and_body(task_name: str) -> None:
    metadata, body = parse_task(TASKS_DIR / f"{task_name}.md")
    assert isinstance(metadata, dict)
    assert metadata.get("fixture"), f"{task_name}: missing 'fixture'"
    assert metadata.get("verification"), f"{task_name}: missing 'verification'"
    assert body.strip(), f"{task_name}: body is empty"


def test_parse_task_missing_opening_marker_raises(tmp_path: Path) -> None:
    path = tmp_path / "bad.md"
    path.write_text("no frontmatter here, just body\n", encoding="utf-8")
    with pytest.raises(ValueError, match="missing frontmatter"):
        parse_task(path)


def test_parse_task_unterminated_frontmatter_raises(tmp_path: Path) -> None:
    path = tmp_path / "bad.md"
    path.write_text("---\nname: foo\nbody starts but no closing marker\n", encoding="utf-8")
    with pytest.raises(ValueError, match="unterminated"):
        parse_task(path)


def test_parse_task_empty_body_after_frontmatter_raises(tmp_path: Path) -> None:
    path = tmp_path / "bad.md"
    path.write_text("---\nname: foo\n---\n   \n", encoding="utf-8")
    with pytest.raises(ValueError, match="body is empty"):
        parse_task(path)


def test_parse_task_handles_crlf_and_utf8_bom(tmp_path: Path) -> None:
    path = tmp_path / "windows.md"
    # UTF-8 BOM + CRLF endings — what Notepad / VS produce on Windows by default.
    text = "﻿---\r\nname: smoke\r\nfixture: nopcommerce\r\n---\r\nHello body\r\n"
    path.write_bytes(text.encode("utf-8"))
    metadata, body_out = parse_task(path)
    assert metadata["name"] == "smoke"
    assert metadata["fixture"] == "nopcommerce"
    assert body_out.strip() == "Hello body"
