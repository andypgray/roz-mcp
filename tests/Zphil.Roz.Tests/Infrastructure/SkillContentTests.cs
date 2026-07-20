using Zphil.Roz.Resources;

namespace Zphil.Roz.Tests.Infrastructure;

/// <summary>
///     The Claude Code plugin ships <c>skills/roz-csharp-editing/SKILL.md</c> (repo root) as the
///     delivery vehicle for the same breakage-prevention rules the setup flow writes via the
///     project-instructions snippet — plugins cannot inject rules files. These tests pin the skill
///     to the snippet's load-bearing anchors so an edit to one surface must propagate to the other
///     (see <see cref="ProjectInstructionsSnippetTests" /> for the snippet side).
/// </summary>
public class SkillContentTests
{
    private static string ReadSkillText()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".claude-plugin")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException(
                $"No ancestor of '{AppContext.BaseDirectory}' contains a .claude-plugin directory; cannot locate the plugin skill.");
        }

        string skillPath = Path.Combine(dir.FullName, "skills", "roz-csharp-editing", "SKILL.md");
        if (!File.Exists(skillPath))
        {
            throw new InvalidOperationException($"Plugin skill not found at '{skillPath}'.");
        }

        return File.ReadAllText(skillPath);
    }

    /// <summary>
    ///     The same trigger rules
    ///     <see cref="ProjectInstructionsSnippetTests.Snippet_ContainsOverrideLanguageAndTriggerRules" />
    ///     pins: rename → rename_symbol, references + markup search before any delete, the
    ///     verified-writes affordance, and the Razor caveat's diagnostics backstop.
    /// </summary>
    [Fact]
    public void Skill_CarriesSnippetRuleAnchors()
    {
        string skill = ReadSkillText();

        skill.ShouldContain("rename_symbol");
        skill.ShouldContain("find_references");
        skill.ShouldContain(".razor");
        skill.ShouldContain("verify=DryRun");
        skill.ShouldContain("get_diagnostics");
    }

    /// <summary>
    ///     The skill keeps the workflows-guide discovery cue, but client-agnostically: plugin
    ///     installs prefix tool and prompt names, so the classic <c>mcp__roz__</c> literal must
    ///     not appear in plugin-shipped content.
    /// </summary>
    [Fact]
    public void Skill_PointsAtWorkflowsResource_WithoutClassicPrefixLiteral()
    {
        string skill = ReadSkillText();

        skill.ShouldContain(RozResources.WorkflowsGuideUri);
        skill.ShouldNotContain("mcp__roz__");
    }

    /// <summary>
    ///     The recommended permission block mirrors what classic setup writes (wildcard allow, the
    ///     seven write tools routed to ask), with the plugin-install tool-name prefix. Independent
    ///     hardcoded strings, per this suite's drift-catch style: when Phase 3 pins the empirical
    ///     prefix into the setup configurator, a mismatch with the skill fails here.
    /// </summary>
    [Fact]
    public void Skill_RecommendsPluginPrefixedPermissionSplit()
    {
        string skill = ReadSkillText();

        skill.ShouldContain("\"mcp__plugin_roz-mcp_roz__*\"");
        skill.ShouldContain("\"mcp__plugin_roz-mcp_roz__edit_symbol\"");
        skill.ShouldContain("\"mcp__plugin_roz-mcp_roz__rename_symbol\"");
        skill.ShouldContain("\"mcp__plugin_roz-mcp_roz__replace_content\"");
        skill.ShouldContain("\"mcp__plugin_roz-mcp_roz__apply_code_fix\"");
        skill.ShouldContain("\"mcp__plugin_roz-mcp_roz__change_signature\"");
        skill.ShouldContain("\"mcp__plugin_roz-mcp_roz__add_usings\"");
        skill.ShouldContain("\"mcp__plugin_roz-mcp_roz__remove_unused_usings\"");
    }

    /// <summary>
    ///     Claude Code resolves a skill by its frontmatter name; drift from the directory name
    ///     would silently change the skill's invocation identity.
    /// </summary>
    [Fact]
    public void Skill_FrontmatterNameMatchesDirectory()
    {
        string skill = ReadSkillText();

        skill.ShouldStartWith("---");
        skill.ShouldContain("name: roz-csharp-editing");
    }
}
