using System.Diagnostics.CodeAnalysis;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Setup;

/// <summary>
///     TOML-based MCP server config writer for Codex CLI's <c>.codex/config.toml</c>. Mirrors
///     <see cref="McpJsonWriter" /> but emits a <c>[mcp_servers.&lt;name&gt;]</c> sub-table
///     instead of a JSON property.
/// </summary>
/// <remarks>
///     Uses <see cref="TomlSerializer" /> model-level round-trip: parses the existing file into
///     a <see cref="TomlTable" />, mutates the target sub-table, and serializes back. Sibling
///     <c>[mcp_servers.*]</c> tables and unrelated top-level sections are preserved. Top-level
///     comments and exact key positioning may be reformatted — Codex's own
///     <c>replace_mcp_servers</c> CLI has the same caveat.
/// </remarks>
internal static class CodexTomlWriter
{
    private const string ServersTableKey = "mcp_servers";

    /// <summary>
    ///     Ensures the <c>[mcp_servers.&lt;serverName&gt;]</c> table exists under
    ///     <paramref name="filePath" /> with the supplied <paramref name="command" />,
    ///     <paramref name="args" />, and <paramref name="env" /> values. Other
    ///     <c>[mcp_servers.*]</c> sub-tables and unrelated top-level sections are preserved.
    ///     If the file exists but cannot be parsed, aborts rather than overwriting.
    /// </summary>
    /// <returns>
    ///     <c>true</c> when the file was written; <c>false</c> when an existing unparseable file caused the merge to
    ///     abort.
    /// </returns>
    [UnconditionalSuppressMessage("AOT", "IL2026", Justification = "Tool is not trimmed or NativeAOT.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Tool is not trimmed or NativeAOT.")]
    internal static async Task<bool> MergeMcpServerTableAsync(
        string filePath,
        string serverName,
        string command,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env,
        CancellationToken ct = default)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!String.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        TomlTable? root = await ReadTomlFileAsync(filePath, ct);
        if (root is null && File.Exists(filePath))
        {
            Console.WriteLine($"  Skipping {Path.GetFileName(filePath)} update to avoid data loss.");
            return false;
        }

        root ??= new TomlTable();

        TomlTable servers = GetOrCreateSubTable(root, ServersTableKey);
        TomlTable serverEntry = GetOrCreateSubTable(servers, serverName);

        serverEntry["command"] = command;
        serverEntry["args"] = BuildTomlArray(args);

        if (env is not null && env.Count > 0)
        {
            TomlTable envTable = GetOrCreateSubTable(serverEntry, "env");
            foreach ((string key, string value) in env)
            {
                envTable[key] = value;
            }

            if (env.TryGetValue(RozEnvVars.Tools.Name, out string? toolsValue))
            {
                Console.WriteLine($"  Set env var: {RozEnvVars.Tools.Name}={toolsValue}");
            }
        }

        string toml = TomlSerializer.Serialize(root);
        // Atomic write: merges into an existing config (preserving sibling [mcp_servers.*] tables),
        // so a crash mid-write must not truncate it. Utf8NoBom matches the prior write's encoding.
        await AtomicFileWriter.WriteAtomicAsync(filePath, toml, FileUtility.Utf8NoBom, ct);

        return true;
    }

    private static TomlTable GetOrCreateSubTable(TomlTable parent, string key)
    {
        if (parent.TryGetValue(key, out object existing) && existing is TomlTable existingTable)
        {
            return existingTable;
        }

        var newTable = new TomlTable();
        parent[key] = newTable;
        return newTable;
    }

    private static TomlArray BuildTomlArray(IReadOnlyList<string> values)
    {
        var array = new TomlArray();
        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }

    [UnconditionalSuppressMessage("AOT", "IL2026", Justification = "Tool is not trimmed or NativeAOT.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Tool is not trimmed or NativeAOT.")]
    private static async Task<TomlTable?> ReadTomlFileAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string content = await File.ReadAllTextAsync(path, ct);

        try
        {
            // Pre-parse with SyntaxParser to surface TOML errors that Deserialize would otherwise
            // accept by producing a partially-populated model.
            DocumentSyntax doc = SyntaxParser.Parse(content, path);
            if (doc.HasErrors)
            {
                Console.WriteLine($"  Warning: Could not parse {path}: contains TOML errors.");
                return null;
            }

            return TomlSerializer.Deserialize<TomlTable>(content);
        }
        catch (TomlException ex)
        {
            Console.WriteLine($"  Warning: Could not parse {path}: {ex.Message}");
            return null;
        }
    }
}
