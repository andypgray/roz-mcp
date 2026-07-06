---
name: P4c-decompile-markdig
fixture: nopcommerce
prompt: decompile_symbol
prompt_args:
  symbol: "Markdig.Markdown.ToHtml"
  focus: "what the default pipeline enables, and whether it sanitizes or escapes raw HTML in the input"
report: DECOMPILE_MARKDIG.md
rubric: decompile
reference: P4c-decompile-markdig
verification:
  - {type: file-exists, paths: [DECOMPILE_MARKDIG.md]}
  - {type: loc-delta-max, max: 0, ignore_tracked_paths: [DECOMPILE_MARKDIG.md]}
---
Prompt-efficacy task for `/mcp__roz__decompile_symbol` (Phase 2) — the **second open-source NuGet**
symbol, picked for package variety and a security-relevant gotcha. `Markdig.Markdown.ToHtml` is in
nopCommerce's package graph (Markdig 0.42.0, via Nop.Web.Framework); source is on GitHub (xoofx/markdig),
so the recipe should prefer real source.

The rendered recipe is the brief (render bridge in runner.py); this body is documentation only and
is NOT sent to the model. Read-only.

**Oracle.** `P4c-decompile-markdig.reference.md` — the real facade body (`GetPipeline` + parse +
render) + the authoritative behavior + a FALSE-claim checklist (default pipeline is plain CommonMark
with NO advanced extensions; raw HTML is passed through unsanitized by default; static not instance;
throws on null). Graded by the `decompile` rubric.

**Failure modes probed.** The dangerous hallucination that Markdig sanitizes HTML / is XSS-safe by
default; claiming the default pipeline enables tables/footnotes; null-tolerance; explaining a generic
"markdown to HTML" mental model instead of the retrieved body.
