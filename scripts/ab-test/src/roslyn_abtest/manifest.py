from __future__ import annotations

import re

from .paths import STRESS_TEST_CSPROJ

_SHA_RE = re.compile(r"<NopCommitSha>([0-9a-fA-F]{7,40})</NopCommitSha>")


def read_nop_commit_sha() -> str:
    """Source-of-truth lookup for the nopCommerce commit SHA.

    The C# stress test's csproj declares `<NopCommitSha>` and uses it in its
    AcquireNopCommerce MSBuild target. Mirroring it here keeps the Python
    harness and the C# stress tests in lock-step — bumping the SHA in the
    csproj auto-propagates.
    """
    text = STRESS_TEST_CSPROJ.read_text(encoding="utf-8")
    match = _SHA_RE.search(text)
    if not match:
        raise RuntimeError(
            f"Could not find <NopCommitSha> in {STRESS_TEST_CSPROJ}"
        )
    # Preserve case as-written in the csproj. MSBuild substitutes the property
    # verbatim into the cache-dir path; lowercasing here would split the cache
    # whenever the source-of-truth uses uppercase hex.
    return match.group(1)
