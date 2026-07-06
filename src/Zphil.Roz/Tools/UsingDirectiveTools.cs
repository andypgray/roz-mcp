using System.ComponentModel;
using ModelContextProtocol.Server;
using Zphil.Roz.Constants;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tools;

/// <summary>
///     MCP tools for managing using directives: add and remove unused.
/// </summary>
[McpServerToolType]
internal sealed class UsingDirectiveTools(UsingDirectiveService usingService)
{
    [McpServerTool(Name = "add_usings", Destructive = false, Idempotent = true, OpenWorld = false, Title = "Add Usings")]
    [Description("Add namespace using directives (skips duplicates; use replace_content for static/global).")]
    public async Task<string> AddUsings(
        [Description("File path (no :line:col).")]
        string filePath,
        [Description("Namespace(s) or aliases to add, e.g. [\"System.Linq\", \"Json = System.Text.Json\"]")]
        string[] usings,
        [Description(ToolDescriptions.SortUsings)]
        bool sortUsings = true,
        CancellationToken ct = default)
    {
        FileLocation loc = LocationParser.ParseFile(filePath, "add_usings");

        AddUsingsResult result = await usingService.AddUsingsAsync(loc.FilePath, usings, sortUsings, ct);
        return ResponseFormatter.Format(result);
    }

    [McpServerTool(Name = "remove_unused_usings", Destructive = true, Idempotent = true, OpenWorld = false, Title = "Remove Unused Usings")]
    [Description("Remove unused using directives.")]
    public async Task<string> RemoveUnusedUsings(
        [Description("File paths (absolute or solution-relative).")]
        string[] filePaths,
        [Description(ToolDescriptions.SortUsings)]
        bool sortUsings = true,
        CancellationToken ct = default)
    {
        RemoveUnusedUsingsResult result = await usingService.RemoveUnusedUsingsAsync(filePaths, sortUsings, ct);
        return ResponseFormatter.Format(result);
    }
}
