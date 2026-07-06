# Python — A/B test harness

This file applies inside `scripts/ab-test/`. This is a Python package; the C# coding conventions used elsewhere in this repository do **not** apply here. The general tool-usage rules (dedicated tools over Bash) and git conventions still do.

Wider context: [README.md](README.md) and [pyproject.toml](pyproject.toml) cover the package surface; the canonical experimental output is [docs/evidence/ab-test-mcp-vs-no-mcp-2026-04-18.md](../../docs/evidence/ab-test-mcp-vs-no-mcp-2026-04-18.md).

## Build, test, lint

```powershell
pip install -e scripts/ab-test/[dev]        # one-time, editable install with dev extras
ruff check scripts/ab-test/                 # lint
mypy scripts/ab-test/src/roslyn_abtest      # type-check
pytest scripts/ab-test/                     # unit tests (Step 7 onwards)
pytest scripts/ab-test/ -m integration      # SDK-touching tests (marker-gated)
roslyn-abtest run --task 00-smoke --reps 1  # end-to-end smoke
```

Validation order is **ruff → mypy → pytest** — cheapest gate first, same philosophy as the C# ReSharper → build → test pipeline.

## Coding conventions

Captured from the existing code in [src/roslyn_abtest/](src/roslyn_abtest/) — not new opinions. New code matches what's there.

- `from __future__ import annotations` at the top of every module.
- Type hints on every public signature, including `-> None`.
- PEP 585 built-in generics: `dict[str, int]`, `list[Path]`, `tuple[int, str, str]`. **Never** `typing.Dict`/`List`/`Tuple` — the `__future__` import makes built-ins work as annotations on 3.11.
- `Path` from `pathlib` for filesystem paths. No string concatenation, no `os.path.join`.
- Explicit `encoding="utf-8"` on every file read and write.
- Terse triple-quoted docstrings, one sentence, present-tense imperative — `"""Ensure clone exists, then snap it back to <sha>."""`, not `"""Ensures…"""`.
- Module-level constants in `UPPER_SNAKE_CASE`.
- Private helpers prefixed with `_`.
- `argparse` for CLI. Already in use in [cli.py](src/roslyn_abtest/cli.py) — do **not** introduce Click or Typer.
- Lazy imports inside functions only to break a real cycle; comment the WHY (pattern at [runner.py:262-263](src/roslyn_abtest/runner.py#L262-L263)).
- `print(..., flush=True)` for streaming progress — the harness is non-interactive and stdout buffering hides progress of multi-minute runs.
- Ruff config (line-length 100, `target-version = "py311"`, ruleset `E,F,B,UP,SIM`) lives in [pyproject.toml](pyproject.toml). Follow what ruff enforces; don't argue with it.

## Testing conventions

- pytest. Use plain `assert` with descriptive messages — there's no idiomatic Python equivalent of Shouldly; don't recreate one.
- Test names: `test_<what>_<scenario>_<expected>` — snake_case mirror of the C# `Method_Scenario_Expected` convention.
- Test files live in `tests/` under the package root (e.g. `scripts/ab-test/tests/test_aggregate.py`).
- SDK-touching tests are marked `@pytest.mark.integration`. Default `pytest` skips them; `pytest -m integration` selects them. They boot the fixture cache or invoke `claude_agent_sdk.query`, so they're slow and require external state.
- Integration tests stub `claude_agent_sdk.query` directly with an async iterator yielding a single `ResultMessage`. Do **not** mock the SDK's HTTP layer — too fragile, too vendor-internal.

## Investigating third-party code

When you need to understand how a Python library actually behaves, prefer reading source over reasoning from training data.

- **Source (preferred):** `pip show <pkg>` and follow `Home-page` / `Project-URL` to the GitHub repo.
- **Fallback:** read the installed source under the active venv's `site-packages/<pkg>/`. `python -c "import <pkg>; print(<pkg>.__file__)"` locates the path.
- **claude-agent-sdk specifically:** it is young and pre-1.0. Prefer **context7** lookups (`mcp__plugin_context7_context7__resolve-library-id` then `query-docs`) over training data — parameter names, message types, and option shapes have churned and may have moved since the training cutoff.

## What NOT to touch

Load-bearing harness invariants. Changing any of these silently invalidates the A/B comparison.

- `NO_FORMATTER_DIRECTIVE` in [runner.py:34-39](src/roslyn_abtest/runner.py#L34-L39) stays as a code constant. Don't move it to config — it's load-bearing for the experimental claim that formatters aren't masking the comparison.
- `setting_sources=None` and `skills=None` in `ClaudeAgentOptions` are deliberate. They prevent the SDK from picking up the user's CLAUDE.md / skills and contaminating the measurement. Do not "fix."
- `results/` stays gitignored. Canonical evidence lives in [docs/evidence/ab-test-mcp-vs-no-mcp-2026-04-18.md](../../docs/evidence/ab-test-mcp-vs-no-mcp-2026-04-18.md). Don't commit run output.
- Cache path `%LOCALAPPDATA%\Zphil.Roz\nopCommerce\<sha>` is shared char-for-char with the C# stress test so users running both don't get a second clone. Don't rebase, rename, or move it.
- Windows-only. Matches `%LOCALAPPDATA%` and the .NET-dev target audience. Don't add Linux/macOS conditionals speculatively.

## Tool usage

Prefer dedicated tools over Bash. Use Read / Edit / Write / Grep / Glob for files and search; reserve Bash for `pip`, `pytest`, `ruff`, `mypy`, `dotnet`, and `git`.
