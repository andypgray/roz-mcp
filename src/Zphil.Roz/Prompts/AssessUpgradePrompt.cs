using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Prompts;

/// <summary>
///     MCP prompt that previews a NuGet package upgrade. It gauges how exposed the solution is to the
///     package via <c>find_references</c>, maps the release's breaking changes (which the agent brings —
///     the server can't enumerate them) onto your in-solution call sites, reports a risk verdict, and
///     upgrades only on confirmation. CLI-first so it works on any client; a connected NuGet MCP server can
///     supplement but isn't required. Report-first, parallel to <c>assess_impact</c>.
/// </summary>
[McpServerPromptType]
internal sealed class AssessUpgradePrompt
{
    /// <summary>
    ///     Emits a report-first recipe that gauges exposure to <paramref name="package" />, assesses the jump
    ///     to <paramref name="targetVersion" />, and upgrades only on confirmation — optionally scoped by
    ///     <paramref name="project" />. When <paramref name="package" /> is omitted, the recipe first lists
    ///     the solution's outdated packages (<c>dotnet list package --outdated</c>) and has the user pick one.
    /// </summary>
    [McpServerPrompt(Name = "assess_upgrade", Title = "Assess a package upgrade")]
    [Description(
        "Gauge how risky a NuGet package upgrade is before making it: how exposed your code is and which "
        + "call sites the release's breaking changes would hit. Omit the package to pick from the solution's "
        + "outdated ones. Report-first; upgrades only on confirmation.")]
    public static string AssessUpgrade(
        [Description(
            "The NuGet package id to assess (e.g. 'Newtonsoft.Json'). Omit it to list the solution's "
            + "outdated packages and pick one to assess.")]
        string? package = null,
        [Description("Target version to assess. Omit for the latest stable version (resolve it in step 1).")]
        string? targetVersion = null,
        [Description("Optional project-name substring to scope the assessment to one project; omit for the whole solution.")]
        string? project = null)
    {
        bool hasPackage = !String.IsNullOrWhiteSpace(package);

        string targetClause = hasPackage
            ? String.IsNullOrWhiteSpace(targetVersion) ? "the latest stable version" : $"version `{targetVersion}`"
            : "its latest stable version";

        string opening = hasPackage
            ? $"Assess upgrading `{package}` to {targetClause} *before* doing it"
            : "Assess a NuGet package upgrade *before* doing it (you didn't name one, so step 1 lists what's "
              + "upgradable and you pick)";

        string discoveryLead = hasPackage
            ? ""
            : " You didn't name a package, so first run `dotnet list package --outdated` and present the "
              + $"upgradable packages as a pick-one selection — {PromptFragments.AsMultipleChoice(false)}, "
              + "one option per package (its id, with current → latest version) — then assess the one I pick.";

        string pkgCmd = hasPackage ? package! : "<id>";

        string versionArg = String.IsNullOrWhiteSpace(targetVersion)
            ? " --version <target>"
            : $" --version {targetVersion}";
        string projectScopeNote = String.IsNullOrWhiteSpace(project)
            ? ""
            : $" Scope the `dotnet` commands to the `{project}` project and every Roslyn lookup to `project={project}`.";

        string verifyStep = PromptFragments.GetVerifyStep(
            "the upgrade",
            "If something no longer compiles, the upgrade is a breaking one — report exactly what broke before going further.",
            "the baseline you captured before upgrading");

        return
            $"""
             {opening} — a report-first preview built on the roz-mcp tools, parallel to `assess_impact`. The
             server can map the upgrade onto your code; it can't read the package's changelog — so this is
             CLI-first and honest about that seam. Touch nothing until I confirm.{projectScopeNote}

             {PromptFragments.ToolPreflight()}

             1. **Pin current → target.**{discoveryLead} `dotnet list <project> package` shows the installed
                version; `dotnet list package --outdated` (or `dotnet package search {pkgCmd} --take 1`)
                surfaces the latest/target version; the package's nuget.org page or cached `.nuspec`
                (`<projectUrl>`/`<repository>`) has the repository URL for the changelog. (If a NuGet MCP server is connected you may
                use it here instead — optional; the CLI is the baseline.) State its current → {targetClause} plainly.

             2. **Gauge exposure — the value-add.** Measure how much of the solution touches this package:
                `find_symbol` its key namespaces/types, then `find_references` (`includeTests=true`) to count
                the call sites and the projects they span — "you call this at N sites across M projects" is the
                upgrade's blast surface. {PromptFragments.RazorBlindSpot}; text-search the markup
                (`*.razor`, `*.cshtml`) for the package's types so a markup-only consumer isn't missed.

             3. **Flag the at-risk sites — honestly.** The server CANNOT enumerate what changed between the
                versions; bring the breaking changes yourself from the release notes / changelog, then use
                `find_references` (and `analyze_change_impact` where a specific API is retyped, removed, or
                resignatured) to map each one onto your in-solution sites. Be explicit about the blind spot,
                exactly as `assess_impact` is: reflection, `dynamic`, and source-generated usage aren't seen,
                and consumers outside this solution are invisible.

             4. **Report.** Lead with current → {targetClause}, then exposure (N sites across M projects), then
                the at-risk sites with the breaking change each would hit. Give a verdict: **low-risk** (no
                at-risk sites — a version bump), **mechanical** (a handful of well-understood edits), or
                **high-touch** (broad surface or semantically risky changes).

             5. **Offer — don't upgrade yet.** {PromptFragments.AsMultipleChoice(false)} — "upgrade now and fix
                the mechanical sites" versus "report only, change nothing".

             6. **If I pick upgrade:** {PromptFragments.BaselineCapture} Then
                `dotnet add <project> package {pkgCmd}{versionArg}`, apply the call-site fixes the at-risk list
                named, and verify: {verifyStep} Run the test suite too if one is present. If I pick report only,
                stop and change nothing.

             Start with step 1.
             """;
    }
}
