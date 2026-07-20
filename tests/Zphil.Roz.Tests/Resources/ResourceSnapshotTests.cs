using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Resources;

namespace Zphil.Roz.Tests.Resources;

/// <summary>
///     Snapshot pin for the MCP resource surface (<see cref="RozResources" />): the registered set of
///     guide resources, each one's URI / name / title / mime-type / description, and the load-bearing
///     anchors their embedded markdown must carry. Analog of
///     <see cref="Zphil.Roz.Tests.Prompts.PromptSnapshotTests" /> — a rename, a dropped resource, or a
///     manifest-id drift trips a row here rather than surfacing only when a client reads the resource.
///     Anchor assertions target stable phrases, not the whole blob, so wording can evolve while the
///     load-bearing spec cannot silently drift.
/// </summary>
public class ResourceSnapshotTests
{
    [Fact]
    public void Resources_RegisteredSet_MatchesSnapshot()
    {
        // Act — the full set of shipped resource names, discovered exactly as the SDK's assembly scan does.
        HashSet<string> names = GetResourceMethods()
            .Select(m => m.GetCustomAttribute<McpServerResourceAttribute>()?.Name!)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.Ordinal);

        // Assert — additions/removals must update this list (and the theory rows below).
        string[] expected =
        [
            RozResources.ConfigurationGuideName, RozResources.EditingGuideName, RozResources.WorkflowsGuideName
        ];
        // ReSharper disable once ArgumentsStyleLiteral — keep `ignoreOrder:` self-documenting.
        names.ShouldBe(expected, ignoreOrder: true);
    }

    [Theory]
    [InlineData(RozResources.ConfigurationGuideUri, RozResources.ConfigurationGuideName, "Configuring roz")]
    [InlineData(RozResources.EditingGuideUri, RozResources.EditingGuideName, "Editing C# with roz")]
    [InlineData(RozResources.WorkflowsGuideUri, RozResources.WorkflowsGuideName, "roz tool routing and packaged workflows")]
    public void Resource_UriNameTitleAndMimeType_MatchSnapshot(string uri, string expectedName, string expectedTitle)
    {
        // Arrange
        MethodInfo method = GetResourceMethods()
            .Single(m => m.GetCustomAttribute<McpServerResourceAttribute>()?.UriTemplate == uri);
        McpServerResourceAttribute attr = method.GetCustomAttribute<McpServerResourceAttribute>()!;

        // Assert — a URI with no {param} is a direct resource; the client renders name/title/mime-type.
        attr.Name.ShouldBe(expectedName);
        attr.Title.ShouldBe(expectedTitle);
        attr.MimeType.ShouldBe("text/markdown");
        method.GetCustomAttribute<DescriptionAttribute>().ShouldNotBeNull().Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ConfigurationGuide_LoadsEmbeddedResource_NonTrivial()
    {
        // A rename of the .md or its manifest id would otherwise surface only when a client reads the
        // resource; this load-time assertion turns it into a test failure instead.
        RozResources.ConfigurationGuide().Length.ShouldBeGreaterThan(500);
    }

    [Fact]
    public void EditingGuide_LoadsEmbeddedResource_NonTrivial() => RozResources.EditingGuide().Length.ShouldBeGreaterThan(500);

    [Fact]
    public void WorkflowsGuide_LoadsEmbeddedResource_NonTrivial() => RozResources.WorkflowsGuide().Length.ShouldBeGreaterThan(500);

    [Fact]
    public void ConfigurationGuide_ListsEveryRegisteredEnvVar()
    {
        // Registry-driven so a newly-registered env var that never makes it into the guide fails here.
        string text = RozResources.ConfigurationGuide();
        foreach (EnvVarInfo envVar in RozEnvVars.All)
        {
            text.ShouldContain(envVar.Name);
        }
    }

    [Fact]
    public void ConfigurationGuide_CarriesToolSelectionAnchors()
    {
        string text = RozResources.ConfigurationGuide();
        text.ShouldContain("ROZ_TOOLS");
        text.ShouldContain("default");
        text.ShouldContain("navigation");
        text.ShouldContain("all,-usings"); // an exclusion example
    }

    [Fact]
    public void ConfigurationGuide_DocumentsProjectConfigFile()
    {
        // Load-bearing anchors for the .roz.json section: the file name and the precedence rule.
        // Case-sensitive (project default): match the .md exactly.
        string text = RozResources.ConfigurationGuide();
        text.ShouldContain(".roz.json");
        text.ShouldContain("environment variable always wins");
    }

    [Fact]
    public void EditingGuide_CarriesLoadBearingAnchors()
    {
        // Case-sensitive (project default): match the .md exactly.
        string text = RozResources.EditingGuide();
        foreach (string anchor in new[]
                 {
                     "DryRun", "Delta", "equivalenceKey", ".ctor", "op_Addition",
                     "this[]", "change_signature", "analyze_change_impact"
                 })
        {
            text.ShouldContain(anchor);
        }
    }

    [Fact]
    public void WorkflowsGuide_ListsEveryRegisteredPrompt()
    {
        // Registry-driven, like the env-var check above: the guide's catalog moved out of the
        // always-loaded project snippet, so a renamed or newly-added prompt that never makes it
        // into the guide fails here rather than silently going undiscoverable.
        string[] promptNames = typeof(RozResources).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerPromptTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerPromptAttribute>()?.Name!)
            .Where(n => n is not null)
            .ToArray();
        promptNames.ShouldNotBeEmpty();

        string text = RozResources.WorkflowsGuide();
        foreach (string promptName in promptNames)
        {
            text.ShouldContain(promptName);
        }
    }

    [Fact]
    public void WorkflowsGuide_CarriesRoutingAnchors()
    {
        // Case-sensitive (project default): match the .md exactly.
        string text = RozResources.WorkflowsGuide();
        foreach (string anchor in new[]
                 {
                     "referenceKinds=invocations", "find_implementations", "analyze_change_impact",
                     "get_symbols_overview", "go_to_definition",
                     // The slash-command example stays scoped to a direct install, with the
                     // plugin-prefix caveat alongside — the guide ships to plugin users too.
                     "/mcp__roz__", "plugin installs prefix the name"
                 })
        {
            text.ShouldContain(anchor);
        }
    }

    private static IEnumerable<MethodInfo> GetResourceMethods()
    {
        // Resource methods are internal static, so include NonPublic — matches the SDK's
        // WithResourcesFromAssembly scan (Public | NonPublic | Instance | Static).
        return typeof(RozResources).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerResourceTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerResourceAttribute>() is not null);
    }
}
