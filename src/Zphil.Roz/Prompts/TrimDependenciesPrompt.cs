using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Prompts;

/// <summary>
///     MCP prompt that drives <c>get_unused_references</c> to trim dead <c>ProjectReference</c> /
///     <c>PackageReference</c> entries. It cross-checks each candidate — the package signal is weak by
///     design (analyzers, source generators, runtime-only and reflection-configured deps never appear in
///     source) — removes the confirmed ones via <c>dotnet remove</c>, and verifies with a build. Mutating
///     and confirmation-gated, matching the server's conservative-writes design.
/// </summary>
[McpServerPromptType]
internal sealed class TrimDependenciesPrompt
{
    /// <summary>
    ///     Emits a recipe that finds unused references of <paramref name="kind" /> (optionally scoped by
    ///     <paramref name="scope" />), cross-checks the weak candidates, and removes the confirmed ones.
    /// </summary>
    [McpServerPrompt(Name = "trim_dependencies", Title = "Trim unused dependencies")]
    [Description(
        "Find and remove unused project and package references, confirming each removal and verifying the "
        + "build still passes. Applies edits.")]
    public static string TrimDependencies(
        [Description("Optional project-name substring to scope the scan to one project; omit for the whole solution.")]
        string? scope = null,
        [Description(
            "Which references to scan: 'Projects' (default, the confident signal) finds unused "
            + "ProjectReferences; 'Packages' (weak signal — verify each) finds unused PackageReferences; "
            + "'All' reports both.")]
        [AllowedValues("Projects", "Packages", "All")]
        string kind = "Projects")
    {
        string scopeArg = String.IsNullOrWhiteSpace(scope) ? "" : $" `project={scope}`";

        return
            $"""
             Trim unused dependencies in this solution using `get_unused_references` plus the roz-mcp tools,
             then `dotnet remove`. Conservative and confirmation-gated: a dependency that *looks* unused can
             still be load-bearing, so confirm before removing and let the build be the judge.

             {PromptFragments.ToolPreflight("get_unused_references")}

             1. **Scan for unused references.** Call `get_unused_references` with `dependencyKind={kind}`{scopeArg}. Know
                the signal strength before acting on it: **Projects** is confident — it checks real
                cross-project symbol usage; **Packages** is weak — analyzers, source generators, runtime-only
                and reflection-configured packages never name a type in source, so the tool flags package hits
                as a starting point, not a verdict.

             2. **Cross-check every *package* candidate before trusting it** (project-reference hits are
                confident — if you scanned `dependencyKind=Projects` there are no package candidates, so skip to step 3).
                The package signal is weak by design: a package can be needed without ever naming a type in
                source. For each flagged package, rule that out:
                - **Used where the scan can't see** — text-search the markup (`*.razor`, `*.cshtml`) and run
                  `find_references` on the package's namespace / key types. {PromptFragments.RazorBlindSpot}.
                - **Analyzer / generator / runtime / targets package** — these legitimately have zero source
                  references. `dotnet nuget why <project> <pkg>` shows direct-vs-transitive, and the
                  `PackageReference`'s `PrivateAssets` / `IncludeAssets` / `ExcludeAssets` metadata flags
                  analyzer- and build-only packages. Keep those.
                - **DI- or reflection-registered** — wired up without a direct call; `find_references` tags the
                  DI registrations it detects across the supported containers. When in doubt, keep it.

             3. **Confirm what to remove.** {PromptFragments.AsMultipleChoice(true)}, one option per surviving
                candidate — project references pre-recommended (confident signal), each package flagged "weak
                signal — verify" with what your step-2 cross-check found. Treat this like the public-API gate:
                a dependency leaned on only transitively or at runtime can still be needed, so remove only what
                I tick.

             4. **Remove the confirmed references.** For a project:
                `dotnet remove <project> reference <path-to-referenced-csproj>`; for a package:
                `dotnet remove <project> package <id>` (or delete the `ProjectReference` / `PackageReference`
                line directly). Batch the removals per project.

             5. **Verify with a build.** The removals went through `dotnet remove`, which edits the `.csproj`
                behind the server's back — the in-memory workspace won't see it until reloaded
                (`get_workspace_info reload=true`), and the diagnostics baseline was never recaptured for it,
                so do NOT verify with `get_diagnostics` here: `dotnet build` is THE check. A removed-but-needed
                reference surfaces as a hard compile error (e.g. `CS0246`); if something no longer compiles,
                restore it and tell me what needed it. The build CANNOT catch a package needed only at
                **runtime** (reflection, config-driven, a transitive provider it re-exposes) — call that out
                and suggest a smoke run. Re-scan and loop if the graph shifted; finish with the removed/kept
                summary.

             Start with step 1.
             """;
    }
}
