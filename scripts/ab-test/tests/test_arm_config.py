from __future__ import annotations

import pytest

from roslyn_abtest.arms import load_arm_configs


def test_load_arm_configs_none_returns_all_sorted() -> None:
    configs = load_arm_configs(None)
    # 3 original arms + the 2 isolating CI arms (arm-ci-baseline, arm-ci-on)
    # + the 2 analyze_method arms (arm-am-on, arm-am-routed)
    # + the prompt-efficacy arm (arm-prompt-recipe).
    assert len(configs) == 8
    names = [c["name"] for c in configs]
    # Filename-sorted is name-sorted because each file is named after its arm.
    assert names == sorted(names)


@pytest.mark.parametrize(
    "arm_name",
    [
        "arm-a-default", "arm-a-all", "arm-b-baseline",
        "arm-ci-baseline", "arm-ci-on", "arm-am-on", "arm-am-routed",
    ],
)
def test_load_arm_configs_each_arm_has_required_keys(arm_name: str) -> None:
    configs = load_arm_configs([arm_name])
    assert len(configs) == 1
    cfg = configs[0]
    for key in (
        "name", "description", "inject_claude_md_snippet",
        "mcp_servers", "extra_allowed_tools",
    ):
        assert key in cfg, f"{arm_name}: missing key {key!r}"


def test_load_arm_configs_filter_by_name_returns_single() -> None:
    configs = load_arm_configs(["arm-b-baseline"])
    assert len(configs) == 1
    assert configs[0]["name"] == "arm-b-baseline"


def test_load_arm_configs_unknown_name_exits() -> None:
    with pytest.raises(SystemExit):
        load_arm_configs(["unknown-arm"])


def test_arm_am_routed_has_snippet_override_and_am_on_does_not() -> None:
    # A/B integrity: the routed arm (R) opts into the variant snippet; the bare-add
    # arm (N) must NOT, so it stays byte-identical to arm-ci-baseline's injection.
    routed = load_arm_configs(["arm-am-routed"])[0]
    bare = load_arm_configs(["arm-am-on"])[0]
    assert routed["claude_md_snippet_path"].endswith(
        "project-instructions-snippet.analyze-method.md"
    )
    assert "claude_md_snippet_path" not in bare


def test_arm_am_arms_register_analyze_method() -> None:
    for arm_name in ("arm-am-on", "arm-am-routed"):
        cfg = load_arm_configs([arm_name])[0]
        assert cfg["mcp_servers"]["roz"]["env"]["ROZ_TOOLS"] == "default,analyze_method"
        assert "mcp__roz__analyze_method" in cfg["extra_allowed_tools"]
