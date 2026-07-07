<!-- HAND-AUTHORED reference for decompile_symbol P4c (do not regenerate with generate_references.py).
     Body decompiled once via `ilspycmd -t Markdig.Markdown` from markdig 0.42.0
     (lib/net9.0/Markdig.dll), the version in nopCommerce's graph. Stable.
     Ground truth + a checklist of plausible-but-FALSE claims for the decompile judge rubric. -->

# `Markdig.Markdown.ToHtml(string markdown, MarkdownPipeline? pipeline = null, MarkdownParserContext? context = null)` — ground truth (NuGet, Markdig 0.42.0)

## Decompiled body

```csharp
public static string ToHtml(string markdown, MarkdownPipeline? pipeline = null, MarkdownParserContext? context = null)
{
    if (markdown == null)
        ThrowHelper.ArgumentNullException_markdown();
    pipeline = GetPipeline(pipeline, markdown);
    return MarkdownParser.Parse(markdown, pipeline, context).ToHtml(pipeline);
}

private static MarkdownPipeline GetPipeline(MarkdownPipeline? pipeline, string markdown)
{
    if (pipeline == null)
        return DefaultPipeline;                          // plain CommonMark, no advanced extensions
    if (pipeline.SelfPipeline != null)
        return pipeline.SelfPipeline.CreatePipelineFromInput(markdown);
    return pipeline;
}
```

## What it actually does (authoritative)

- **Static** convenience method. Throws `ArgumentNullException` if `markdown` is `null`.
- When no `pipeline` is passed it uses `DefaultPipeline` — a **plain CommonMark** pipeline. Advanced
  extensions (tables, footnotes, task lists, auto-links, etc.) are **OFF by default**; you opt in by
  building a pipeline with `new MarkdownPipelineBuilder().UseAdvancedExtensions().Build()`.
- By default Markdig **passes raw/embedded HTML through to the output unchanged** — it does NOT
  sanitize or escape it. (Disabling HTML is opt-in via `DisableHtml()`; sanitizing against XSS is
  the caller's responsibility.)
- It parses the markdown to a `MarkdownDocument` and renders that document to an HTML `string`.
- If a `pipeline` configured with a `SelfPipeline` is supplied, the effective pipeline is derived
  from the input's own configuration comment.

## Checklist of plausible-but-FALSE claims (each is FALSE for this method — count any the candidate asserts as a hallucination)

1. "By default it sanitizes or escapes embedded HTML to prevent XSS." (FALSE — raw HTML is passed
   through unchanged by default.)
2. "The default pipeline enables advanced extensions such as tables, footnotes, and task lists."
   (FALSE — the default is plain CommonMark; advanced extensions are opt-in.)
3. "It returns `null` or an empty string when `markdown` is `null`." (FALSE — it throws
   `ArgumentNullException`.)
4. "It is an instance method on a `Markdown`/document object." (FALSE — it is a static method.)
5. "It caches the parsed document or the result for repeated calls." (FALSE.)
6. "It validates the markdown and throws on malformed/invalid markdown." (FALSE — CommonMark has no
   'invalid' input; it renders best-effort.)
7. "By default it adds `rel="nofollow"` / opens links safely / rewrites URLs." (FALSE — no such
   link processing in the default pipeline.)
