---
name: P4a-decompile-path
fixture: nopcommerce
prompt: decompile_symbol
prompt_args:
  symbol: "Path.Combine"
  focus: "its handling of an absolute or rooted second argument, null or empty inputs, and whether it normalizes the path or touches the filesystem"
report: DECOMPILE_PATH.md
rubric: decompile
reference: P4a-decompile-path
verification:
  - {type: file-exists, paths: [DECOMPILE_PATH.md]}
  - {type: loc-delta-max, max: 0, ignore_tracked_paths: [DECOMPILE_PATH.md]}
---
Prompt-efficacy task for `/mcp__roz__decompile_symbol` (Phase 2) — the **BCL** symbol of the
three. `System.IO.Path.Combine` is metadata-only (no source in the solution); BCL source lives in
dotnet/runtime, but it is not a NuGet package `dotnet nuget search` can surface, so the recipe may
legitimately fall back to `ilspycmd` on `System.Private.CoreLib`. Either route should reach the same
body.

The rendered recipe is the brief (render bridge in runner.py); this body is documentation only and
is NOT sent to the model. Read-only — the recipe explains, it does not edit.

**Oracle.** `P4a-decompile-path.reference.md` — the real decompiled body + the authoritative behavior
+ a checklist of plausible-but-FALSE claims (the famous rooted-second-arg gotcha, null handling, no
normalization, no filesystem access). Graded by the `decompile` rubric: `factual_accuracy`,
`grounding` (claims tied to the actual body), `hallucinations` (false-checklist claims asserted),
`source_first` (preferred real source/IL over guessing).

**Failure modes probed.** Hallucinating that an absolute second arg is appended (the #1 trap);
claiming normalization / filesystem access / null-tolerance the method doesn't have; explaining from
training-data guesses instead of the retrieved body; editing when read-only (`loc-delta-max: 0`).
