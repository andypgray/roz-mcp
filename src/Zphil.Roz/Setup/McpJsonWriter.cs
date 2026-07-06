using System.Text.Json;
using System.Text.Json.Nodes;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Setup;

/// <summary>
///     Shared JSON-based MCP server config writer for Claude Code (.mcp.json),
///     Cursor (.cursor/mcp.json), and VS Code Copilot Chat (.vscode/mcp.json). Each client uses
///     the same nested structure but differs in the top-level servers key and the set of
///     per-entry fields. Merges idempotently — preserves sibling MCP servers, sibling env keys
///     the user may have added (e.g. <c>ROZ_SOLUTION_PATH</c>), and any unrelated top-level
///     fields the client may need (e.g. VS Code's <c>inputs</c> array). If the file exists but
///     cannot be parsed, aborts rather than overwriting.
/// </summary>
internal static class McpJsonWriter
{
    private static readonly JsonDocumentOptions LenientDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    internal static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    /// <summary>
    ///     Merges (or creates) an MCP server entry named <paramref name="serverName" /> under
    ///     the <paramref name="serversKey" /> object inside <paramref name="filePath" />.
    /// </summary>
    /// <param name="filePath">Absolute path to the target JSON config.</param>
    /// <param name="serversKey">
    ///     Top-level key holding the servers object — <c>mcpServers</c> for Claude/Cursor, <c>servers</c>
    ///     for VS Code.
    /// </param>
    /// <param name="serverName">Name of the server entry (e.g. <c>roz</c>).</param>
    /// <param name="toolsValue">Value for <c>ROZ_TOOLS</c> env var; <c>null</c> leaves env untouched.</param>
    /// <param name="extraEntryFields">
    ///     Optional extra fields added to the server entry (e.g. <c>{"type": "stdio"}</c> for VS
    ///     Code).
    /// </param>
    /// <param name="ct">Cancellation token forwarded to the atomic write.</param>
    /// <returns>
    ///     <c>true</c> when the file was written; <c>false</c> when an existing unparseable file caused the merge to
    ///     abort.
    /// </returns>
    internal static async Task<bool> MergeMcpServerEntryAsync(
        string filePath,
        string serversKey,
        string serverName,
        string? toolsValue,
        IReadOnlyDictionary<string, JsonNode?>? extraEntryFields = null,
        CancellationToken ct = default)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!String.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        JsonObject? root = await ReadJsonFileAsync(filePath, ct);
        if (root is null && File.Exists(filePath))
        {
            Console.WriteLine($"  Skipping {Path.GetFileName(filePath)} update to avoid data loss.");
            return false;
        }

        root ??= new JsonObject();

        if (root[serversKey] is not JsonObject servers)
        {
            servers = new JsonObject();
            root[serversKey] = servers;
        }

        if (servers[serverName] is not JsonObject serverEntry)
        {
            serverEntry = new JsonObject();
            servers[serverName] = serverEntry;
        }

        serverEntry["command"] = "roz-mcp";
        serverEntry["args"] = new JsonArray();

        if (extraEntryFields is not null)
        {
            foreach ((string key, JsonNode? value) in extraEntryFields)
            {
                serverEntry[key] = value?.DeepClone();
            }
        }

        if (toolsValue is not null)
        {
            if (serverEntry["env"] is not JsonObject env)
            {
                env = new JsonObject();
                serverEntry["env"] = env;
            }

            env[RozEnvVars.Tools.Name] = toolsValue;
            Console.WriteLine($"  Set env var: {RozEnvVars.Tools.Name}={toolsValue}");
        }

        string json = root.ToJsonString(IndentedOptions);
        // Atomic write: this method merges into an existing file (preserving sibling MCP servers
        // and user env keys), so a crash mid-write must not truncate and destroy that config.
        // Utf8NoBom keeps the output byte-identical to the prior plain File.WriteAllTextAsync.
        await AtomicFileWriter.WriteAtomicAsync(filePath, json, FileUtility.Utf8NoBom, ct);

        return true;
    }

    /// <summary>
    ///     Reads and parses a JSON file with lenient options (comments + trailing commas allowed,
    ///     BOM stripped). Returns <c>null</c> when the file does not exist or cannot be parsed —
    ///     the caller decides whether to overwrite or abort.
    /// </summary>
    internal static async Task<JsonObject?> ReadJsonFileAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string content = await File.ReadAllTextAsync(path, ct);

        if (content.Length > 0 && content[0] == '﻿')
        {
            content = content[1..];
        }

        try
        {
            return JsonNode.Parse(content, documentOptions: LenientDocumentOptions)?.AsObject();
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"  Warning: Could not parse {path}: {ex.Message}");
            return null;
        }
    }
}
