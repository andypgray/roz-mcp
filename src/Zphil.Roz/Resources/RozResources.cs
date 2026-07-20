using System.ComponentModel;
using ModelContextProtocol.Server;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Resources;

/// <summary>
///     The MCP resource surface: three on-demand guides whose bodies are embedded markdown.
///     <c>roz://guides/configuration</c> serves <c>configuration-guide.md</c> (every environment
///     variable the server reads and the <c>ROZ_TOOLS</c> selection grammar);
///     <c>roz://guides/editing</c> serves <c>editing-guide.md</c> (verify modes, the
///     <c>change_signature</c> apply gate, <c>apply_code_fix</c> equivalence keys, and the special
///     symbol names); <c>roz://guides/workflows</c> serves <c>workflows-guide.md</c> (the
///     question → tool routing map and the packaged workflow prompts, signposted from the project
///     snippet rather than the server instructions). The detail lives here, loaded on demand, rather
///     than bloating the always-loaded surfaces — the guides are the generic, public-facing twin of
///     this repo's internal <c>CLAUDE.md</c>.
/// </summary>
/// <remarks>
///     Mirrors the prompt types under <c>Prompts/</c>: the class is non-static because
///     <c>WithResourcesFromAssembly()</c> discovers it as a type, while each resource method is
///     <c>static</c>, so no instance is ever constructed. Neither URI template carries a
///     <c>{parameter}</c>, so the SDK registers them as <em>direct</em> resources (surfaced by
///     <c>resources/list</c>), not templates. A <c>string</c> return maps to a single
///     <c>TextResourceContents</c>. The assembly scan binds <c>NonPublic | Static</c> members, so
///     these <c>internal static</c> methods register.
/// </remarks>
[McpServerResourceType]
internal sealed class RozResources
{
    internal const string ConfigurationGuideUri = "roz://guides/configuration";
    internal const string ConfigurationGuideName = "roz_configuration_guide";
    internal const string EditingGuideUri = "roz://guides/editing";
    internal const string EditingGuideName = "roz_editing_guide";
    internal const string WorkflowsGuideUri = "roz://guides/workflows";
    internal const string WorkflowsGuideName = "roz_workflows_guide";

    private const string ConfigurationGuideDescription =
        "Every environment variable the roz-mcp server reads (defaults and effects), the ROZ_TOOLS "
        + "preset/category/exclusion grammar for selecting the tool surface, the .roz.json per-project "
        + "config file, and configuration troubleshooting. Read before changing roz configuration or "
        + "when a tool is missing from the registered set.";

    private const string EditingGuideDescription =
        "How to edit C# with roz's five mutating tools: the verify=None|Delta|DryRun modes, the "
        + "change_signature apply gate and its blocker classes, apply_code_fix equivalence keys, and the "
        + "special Roslyn symbol names (.ctor, op_Addition, this[], …). Read before a mutating call.";

    private const string WorkflowsGuideDescription =
        "The question → tool routing map (who calls this, what implements this, what breaks if I "
        + "change this, …) and the packaged workflow prompts. Read when choosing "
        + "between roz tools for a C# symbol question or when a request matches a packaged workflow.";

    [McpServerResource(
        UriTemplate = ConfigurationGuideUri,
        Name = ConfigurationGuideName,
        Title = "Configuring roz",
        MimeType = "text/markdown")]
    [Description(ConfigurationGuideDescription)]
    internal static string ConfigurationGuide() => EmbeddedResourceText.Load("Zphil.Roz.Resources.configuration-guide.md");

    [McpServerResource(
        UriTemplate = EditingGuideUri,
        Name = EditingGuideName,
        Title = "Editing C# with roz",
        MimeType = "text/markdown")]
    [Description(EditingGuideDescription)]
    internal static string EditingGuide() => EmbeddedResourceText.Load("Zphil.Roz.Resources.editing-guide.md");

    [McpServerResource(
        UriTemplate = WorkflowsGuideUri,
        Name = WorkflowsGuideName,
        Title = "roz tool routing and packaged workflows",
        MimeType = "text/markdown")]
    [Description(WorkflowsGuideDescription)]
    internal static string WorkflowsGuide() => EmbeddedResourceText.Load("Zphil.Roz.Resources.workflows-guide.md");
}
