from __future__ import annotations

from types import SimpleNamespace

from roslyn_abtest.fixtures import Fixture
from roslyn_abtest.mcp_client import McpStdioClient


def _fixture() -> Fixture:
    return Fixture(
        name="t",
        sha="c0ffee1234",
        repo_url="https://example/repo.git",
        cache_subdir="X",
        solution_relpath="src/X.sln",
    )


# --------------------------------- get_prompt ----------------------------------


def test_get_prompt_concatenates_text_messages_and_calls_prompts_get() -> None:
    calls: list[tuple[str, dict]] = []

    def fake_request(method: str, params: dict, timeout: float) -> dict:
        calls.append((method, params))
        return {
            "messages": [
                {"role": "user", "content": {"type": "text", "text": "step 1"}},
                {"role": "user", "content": {"type": "text", "text": "step 2"}},
                {"role": "user", "content": {"type": "image", "data": "ignore me"}},
            ]
        }

    stub = SimpleNamespace(request=fake_request)
    text = McpStdioClient.get_prompt(stub, "fix_diagnostics", {"severity": "warning"}, 60.0)

    assert text == "step 1\nstep 2"
    assert calls == [
        ("prompts/get", {"name": "fix_diagnostics", "arguments": {"severity": "warning"}})
    ]


def test_get_prompt_tolerates_no_messages() -> None:
    stub = SimpleNamespace(request=lambda *_a, **_k: {})
    assert McpStdioClient.get_prompt(stub, "x", {}, 60.0) == ""


# ------------------------------ render_task_brief ------------------------------


def test_render_task_brief_expands_sha_appends_report_and_memoizes(
    monkeypatch,
) -> None:
    from roslyn_abtest import runner

    calls: list[tuple[str, dict, str]] = []

    def fake_render(name: str, args: dict, solution_path: str) -> str:
        calls.append((name, dict(args), solution_path))
        return "RECIPE BODY"

    monkeypatch.setattr("roslyn_abtest.mcp_client.render_prompt", fake_render)
    runner._RENDER_CACHE.clear()

    fixture = _fixture()
    meta = {
        "prompt": "check_breaking_changes",
        "prompt_args": {"baseline": "$FIXTURE_SHA", "scope": "Nop.Core"},
        "report": "BREAK_REPORT.md",
    }

    brief = runner.render_task_brief(meta, fixture)

    assert "RECIPE BODY" in brief
    assert "BREAK_REPORT.md" in brief          # report directive appended
    assert calls[0][1]["baseline"] == "c0ffee1234"   # $FIXTURE_SHA expanded
    assert calls[0][2] == str(fixture.solution_path)

    # Second call with identical (prompt, args) reuses the cached render.
    runner.render_task_brief(meta, fixture)
    assert len(calls) == 1


def test_render_task_brief_no_report_directive_without_report(monkeypatch) -> None:
    from roslyn_abtest import runner

    monkeypatch.setattr(
        "roslyn_abtest.mcp_client.render_prompt", lambda *_a, **_k: "RECIPE"
    )
    runner._RENDER_CACHE.clear()
    brief = runner.render_task_brief(
        {"prompt": "decompile_symbol", "prompt_args": {"symbol": "string.Format"}}, _fixture()
    )
    assert brief == "RECIPE"
    assert "## Output" not in brief
