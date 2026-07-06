from __future__ import annotations

from roslyn_abtest.runner import count_diff_loc


def test_count_diff_loc_empty_string_returns_zero() -> None:
    assert count_diff_loc("") == 0


def test_count_diff_loc_counts_content_plus_and_minus_lines() -> None:
    diff = "\n".join([
        " context line",
        "+added 1",
        "+added 2",
        "+added 3",
        "-removed 1",
        "-removed 2",
        " more context",
    ])
    assert count_diff_loc(diff) == 5


def test_count_diff_loc_excludes_headers() -> None:
    diff = "\n".join([
        "--- a/file.cs",
        "+++ b/file.cs",
        "+actual addition",
        "-actual removal",
    ])
    assert count_diff_loc(diff) == 2


def test_count_diff_loc_multi_hunk_real_shape() -> None:
    diff = "\n".join([
        "diff --git a/Foo.cs b/Foo.cs",
        "index abc..def 100644",
        "--- a/Foo.cs",
        "+++ b/Foo.cs",
        "@@ -1,3 +1,4 @@",
        " using System;",
        "-namespace Old;",
        "+namespace New;",
        "+",
        " public class Foo",
        "@@ -10,2 +11,3 @@",
        " {",
        "-    private int x;",
        "+    private int y;",
        "+    private int z;",
        "}",
    ])
    # Content lines: -namespace Old, +namespace New, +blank,
    # -private int x, +private int y, +private int z = 6.
    # Headers (---, +++) and hunk markers (@@) are excluded.
    assert count_diff_loc(diff) == 6
