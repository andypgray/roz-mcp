using Zphil.Roz.Infrastructure;
using Zphil.Roz.Resources;

namespace Zphil.Roz.Tests.Infrastructure;

public class ProjectInstructionsSnippetTests
{
    /// <summary>
    ///     The configurator's replace anchor (<see cref="ProjectInstructionsSnippet.SectionHeading" />)
    ///     only works if the embedded snippet actually starts with that heading. Drift here would cause
    ///     <c>roz-mcp setup</c> to append a second section on every run instead of replacing in place.
    /// </summary>
    [Fact]
    public void Snippet_StartsWithSectionHeading() =>
        ProjectInstructionsSnippet.Text.ShouldStartWith(ProjectInstructionsSnippet.SectionHeading);

    /// <summary>
    ///     The snippet exists to override the built-in tool-selection guidance baked into clients
    ///     like Claude Code, Cursor, VS Code Copilot Chat, and Codex CLI, and to carry the
    ///     condition-scoped trigger rules the down-tier A/B (docs/evidence/ab-test-down-tier-2026-07-02.md)
    ///     showed models actually follow: rename → rename_symbol, and references + markup search
    ///     before any delete. These assertions guard against accidental softening or loss of a
    ///     load-bearing trigger.
    /// </summary>
    [Fact]
    public void Snippet_ContainsOverrideLanguageAndTriggerRules()
    {
        string snippet = ProjectInstructionsSnippet.Text;

        snippet.ShouldContain("overrides built-in tool-selection guidance");

        // The two rules the A/B evidence directly supports, plus the verified-writes affordance
        // and the Razor caveat's diagnostics backstop.
        snippet.ShouldContain("rename_symbol");
        snippet.ShouldContain("find_references");
        snippet.ShouldContain(".razor");
        snippet.ShouldContain("verify=DryRun");
        snippet.ShouldContain("get_diagnostics");
    }

    /// <summary>
    ///     The per-tool routing table and prompts catalog moved out of the always-loaded snippet into
    ///     the workflows guide resource. What remains must be a condition-scoped discovery cue — the
    ///     framing the down-tier A/B found models act on — not a passive "more info at" pointer.
    /// </summary>
    [Fact]
    public void Snippet_PointsAtWorkflowsResource()
    {
        string snippet = ProjectInstructionsSnippet.Text;

        snippet.ShouldContain(RozResources.WorkflowsGuideUri);
        snippet.ShouldContain("matches a packaged workflow");
    }

    /// <summary>
    ///     The configurator's replace logic treats the next <c># </c> line as the end of the roz
    ///     section. A second H1 inside the snippet would make a re-run truncate the section at that
    ///     line, silently dropping everything after it.
    /// </summary>
    [Fact]
    public void Snippet_ContainsNoOtherH1Heading()
    {
        string[] lines = ProjectInstructionsSnippet.Text.Split('\n');

        string[] extraH1Lines = lines
            .Skip(1)
            .Where(line => line.StartsWith("# ", StringComparison.Ordinal))
            .ToArray();

        extraH1Lines.ShouldBeEmpty();
    }

    /// <summary>
    ///     The snippet is ~1,000 always-on tokens in every user session; the slimming that moved the
    ///     routing table and prompts catalog into <c>roz://guides/workflows</c> only pays if the
    ///     snippet stays lean. Growth past this budget means content belongs in the guide instead.
    /// </summary>
    [Fact]
    public void Snippet_StaysWithinLeanBudget() =>
        ProjectInstructionsSnippet.Text.Length.ShouldBeLessThanOrEqualTo(2200);
}
