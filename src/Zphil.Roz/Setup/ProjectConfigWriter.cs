using System.Text.Json;
using System.Text.Json.Nodes;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Setup;

/// <summary>
///     Writes per-project settings into <c>.roz.json</c> during plugin-mode setup: the plugin's
///     launch command is global and carries no per-project <c>env</c> block, so <c>--tools</c>
///     lands in the config file <see cref="ProjectConfigSeeder" /> reads at server startup.
/// </summary>
internal static class ProjectConfigWriter
{
    /// <summary>
    ///     Merges <paramref name="key" /> = <paramref name="value" /> into
    ///     <c>&lt;projectRoot&gt;/.roz.json</c>, creating the file when missing and preserving
    ///     sibling keys. An existing file that cannot be rewritten losslessly — invalid JSON, a
    ///     non-object root, or comments/trailing commas (which a rewrite would strip) — is left
    ///     untouched, with manual instructions printed instead.
    /// </summary>
    internal static async Task MergeKeyAsync(
        string projectRoot,
        string key,
        string value,
        CancellationToken ct = default)
    {
        string configPath = Path.Combine(projectRoot, ProjectConfigSeeder.FileName);

        JsonObject root;
        if (File.Exists(configPath))
        {
            string content = await File.ReadAllTextAsync(configPath, ct);
            JsonObject? existing = ParseForRewrite(content);
            if (existing is null)
            {
                Console.WriteLine($"  {ProjectConfigSeeder.FileName} exists but can't be rewritten losslessly (invalid JSON, comments, or a non-object root); leaving it untouched.");
                Console.WriteLine($"  Add \"{key}\": \"{value}\" to it by hand: {configPath}");
                return;
            }

            root = existing;
        }
        else
        {
            root = new JsonObject();
        }

        root[key] = value;

        string json = root.ToJsonString(McpJsonWriter.IndentedOptions);
        // Atomic write for the same reason as the MCP config merges: the file may carry keys the
        // user added by hand, and a crash mid-write must not truncate them away.
        await AtomicFileWriter.WriteAtomicAsync(configPath, json, FileUtility.Utf8NoBom, ct);

        Console.WriteLine($"  Updated: {ProjectConfigSeeder.FileName} ({key}={value})");
        Console.WriteLine($"  Note: an environment variable named {key} still takes precedence over {ProjectConfigSeeder.FileName}.");
    }

    /// <summary>
    ///     Strict parse — no comments, no trailing commas, no duplicate keys — unlike the seeder's
    ///     lenient read, because a merge rewrites the whole file: leniently accepting a commented
    ///     file here would silently strip the comments on write. Whitespace-only counts as an empty
    ///     object, matching the seeder's empty-config semantics. A parseable non-object root
    ///     returns <c>null</c> like a parse failure — there is nothing to merge into.
    /// </summary>
    private static JsonObject? ParseForRewrite(string content)
    {
        // Strip a leading BOM: File.ReadAllText usually consumes it, but an unusual encoding may
        // leave U+FEFF behind, which the strict JSON parser rejects.
        string json = content.TrimStart((char)0xFEFF);
        if (String.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        JsonDocumentOptions strictOptions = new() { AllowDuplicateProperties = false };
        try
        {
            return JsonNode.Parse(json, documentOptions: strictOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
