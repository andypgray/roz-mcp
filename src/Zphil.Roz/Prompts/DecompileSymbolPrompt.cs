using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Prompts;

/// <summary>
///     MCP prompt that decompiles and explains an external (metadata-only) symbol — a BCL or NuGet
///     type/member with no source in the solution. Read-only: it resolves the symbol, prefers real
///     package source (NuGet/GitHub) over decompilation, falls back to <c>ilspycmd</c>, and explains the
///     actual body. Encodes a prefer-source-then-decompile workflow (real package source first,
///     <c>ilspycmd</c> as the fallback) as a one-shot recipe — no new tool, no new dependency.
/// </summary>
[McpServerPromptType]
internal sealed class DecompileSymbolPrompt
{
    /// <summary>
    ///     Emits a read-only recipe that resolves <paramref name="symbol" />, obtains its real source or a
    ///     decompiled body, and explains it — optionally narrowed to <paramref name="focus" />.
    /// </summary>
    [McpServerPrompt(Name = "decompile_symbol", Title = "Decompile and explain an external symbol")]
    [Description(
        "Explain an external (BCL/NuGet) symbol that has no source in your solution — grounded in its "
        + "actual source or decompiled body, not guesswork. Read-only.")]
    public static string DecompileSymbol(
        [Description(
            "The external symbol to explain — a name (e.g. 'JsonSerializer.Serialize' or 'List<T>.Add') "
            + "or a cursor 'path:line:col' on a usage site.")]
        string symbol,
        [Description(
            "Optional focus for the explanation, in plain English — e.g. 'how it handles nulls', "
            + "'allocation behavior', 'is it thread-safe'. Omit for a general explanation.")]
        string? focus = null)
    {
        string focusNote = String.IsNullOrWhiteSpace(focus)
            ? "a general explanation of what it does and how"
            : $"narrowed to: {focus}";

        return
            $"""
             Decompile and explain `{symbol}` — an external symbol (BCL or NuGet) with no source in this
             solution — using the roz-mcp tools: prefer real package source over decompilation, fall back to
             `ilspycmd`. Read-only: you're reading and explaining, not editing. Ground every claim in the actual
             body you retrieve — no guessing from training data.

             {PromptFragments.ToolPreflight()}

             1. **Resolve & locate.** Resolve `{symbol}` (a name or `path:line:col`) with `go_to_definition`
                (or `find_symbol` for a name). Two outcomes:
                - **In-solution source** — this isn't external. Read the declaration, summarize it from the
                  real source, and stop: tell me it's local and point me at the file. No decompilation.
                - **Metadata-only** (NuGet/BCL) — `go_to_definition`/`find_symbol` auto-include its XML docs
                  and name the containing assembly and namespace. Capture the full type name and the assembly,
                  then continue.

             2. **Prefer source over decompiling.** Decompiled IL-to-C# reads worse than real source, so try
                source first. Find the owning package and version with `dotnet list <project> package` (the
                DLL is cached at `~/.nuget/packages/<pkg>/<ver>/lib/<tfm>/<assembly>.dll`). If it's
                open-source, its repository URL is on the package's nuget.org page (the `Source repository` /
                `Project website` links) or in the cached `.nuspec` (`<projectUrl>`/`<repository>`) — read the
                type's real source on GitHub. BCL types (`System.*`) are open-source too — browse them at
                https://source.dot.net (indexed by name) or in `dotnet/runtime`.

             3. **Decompile (fallback).** If real source isn't readily available, decompile from the NuGet
                cache with `ilspycmd` — but preflight it first: run `ilspycmd --version` (or `where ilspycmd` /
                `which ilspycmd`) to check it's on PATH.
                - **Present** — decompile: `ilspycmd -lv Latest -t "Namespace.TypeName" <path-to-dll>`.
                  `ilspycmd` decompiles a whole type, not a single member — for one member, decompile the type
                  and extract it. The cache holds one DLL per `lib/<tfm>`, and a decompiled body can differ
                  between target frameworks — pick the `<tfm>` the consuming project targets.
                - **Absent** — don't install it silently. Raise it to me and ask how to proceed —
                  {PromptFragments.AsMultipleChoice(false)} — between "install `ilspycmd` now, then continue"
                  (you'd run `dotnet tool install -g ilspycmd` and resume decompiling) and "skip decompiling —
                  explain from the XML docs and signature gathered in steps 1–2, and say that's all you had".
                  If I pick install, run it and decompile; if I pick skip, go straight to step 4's docs-only
                  path.

             4. **Explain**, grounded in the body you retrieved — {focusNote}. Walk the actual logic and call
                out the gotchas that bite at call sites: null handling, allocations/boxing, exceptions thrown,
                thread-safety, and any surprising edge-case behavior. If all you could get was the XML docs and
                signature (no source, no decompile), say so and keep the explanation to what those support —
                don't fill the gap with guesses.

             Start with step 1.
             """;
    }
}
