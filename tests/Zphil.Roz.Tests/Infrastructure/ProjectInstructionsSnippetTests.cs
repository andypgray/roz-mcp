using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Tests.Infrastructure;

public class ProjectInstructionsSnippetTests
{
    /// <summary>
    ///     The configurator's idempotency check (<see cref="ProjectInstructionsSnippet.SectionHeading" />)
    ///     only works if the embedded snippet actually starts with that heading. Drift here would cause
    ///     <c>roz-mcp setup</c> to append the snippet on every run instead of skipping.
    /// </summary>
    [Fact]
    public void Snippet_StartsWithSectionHeading() =>
        ProjectInstructionsSnippet.Text.ShouldStartWith(ProjectInstructionsSnippet.SectionHeading);

    /// <summary>
    ///     The snippet exists to override the built-in tool-selection guidance baked into clients
    ///     like Claude Code, Cursor, VS Code Copilot Chat, and Codex CLI, and to carry the
    ///     condition-scoped trigger rules the down-tier A/B (docs/research/ab-test-down-tier-2026-07-02.md)
    ///     showed models actually follow: rename → rename_symbol, and references + markup search
    ///     before any delete. These assertions guard against accidental softening or loss of a
    ///     load-bearing trigger.
    /// </summary>
    [Fact]
    public void Snippet_ContainsOverrideLanguageAndTriggerRules()
    {
        string snippet = ProjectInstructionsSnippet.Text;

        snippet.ShouldContain("overrides built-in tool-selection guidance");

        // The two rules the A/B evidence directly supports, plus the verified-writes affordance.
        snippet.ShouldContain("rename_symbol");
        snippet.ShouldContain("find_references");
        snippet.ShouldContain(".razor");
        snippet.ShouldContain("verify=DryRun");

        // Question → tool routing triggers.
        snippet.ShouldContain("referenceKinds=invocations");
        snippet.ShouldContain("find_implementations");
        snippet.ShouldContain("analyze_change_impact");
        snippet.ShouldContain("get_type_hierarchy");
        snippet.ShouldContain("get_symbols_overview");
        snippet.ShouldContain("find_symbol");
        snippet.ShouldContain("go_to_definition");
        snippet.ShouldContain("get_diagnostics");
    }
}
