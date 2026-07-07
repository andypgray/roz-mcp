---
name: P4b-decompile-pascalize
fixture: nopcommerce
prompt: decompile_symbol
prompt_args:
  symbol: "Humanizer.InflectorExtensions.Pascalize"
  focus: "exactly which characters it treats as word boundaries, and whether it changes the casing of the characters it does not capitalize"
report: DECOMPILE_PASCALIZE.md
rubric: decompile
reference: P4b-decompile-pascalize
verification:
  - {type: file-exists, paths: [DECOMPILE_PASCALIZE.md]}
  - {type: loc-delta-max, max: 0, ignore_tracked_paths: [DECOMPILE_PASCALIZE.md]}
---
Prompt-efficacy task for `/mcp__roz__decompile_symbol` (Phase 2) — the **open-source NuGet**
symbol of the three. `Humanizer.InflectorExtensions.Pascalize` is in nopCommerce's package graph
(Humanizer.Core 2.14.1, referenced by Nop.Core); its source is on GitHub (Humanizr/Humanizer), so the
recipe should PREFER real source over decompiling (high `source_first`).

The rendered recipe is the brief (render bridge in runner.py); this body is documentation only and
is NOT sent to the model. Read-only.

**Oracle.** `P4b-decompile-pascalize.reference.md` — the real one-line `Regex.Replace` body + the
authoritative behavior + a FALSE-claim checklist (it splits only on `^`/`_`/spaces not hyphens; it
does NOT lowercase the rest of a word; `ToUpper` not `ToUpperInvariant`; throws on null). Graded by
the `decompile` rubric.

**Failure modes probed.** Over-claiming that it produces strict PascalCase by lowercasing the
remainder; inventing extra separators it splits on; claiming culture-invariant casing or
null-tolerance; explaining from a generic mental model of "pascalize" instead of the retrieved body.
