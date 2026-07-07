using System.Collections.Frozen;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Pipeline;

/// <summary>
///     Serializes edit/mutation tool calls so only one executes at a time.
///     Read-only tools are not affected and run concurrently.
/// </summary>
/// <remarks>
///     The MCP protocol serializes calls to the same tool but allows different tools to run
///     in parallel. Without this filter, concurrent edit tools can race — resolving symbols
///     against the same solution snapshot, then both writing, with the second overwriting
///     the first's changes.
///     <para>
///         The set of mutable tools is derived at startup by reflecting over all
///         <see cref="McpServerToolAttribute" /> methods in the assembly — any tool without
///         <c>ReadOnly = true</c> is serialized.
///     </para>
/// </remarks>
internal static class EditSerializationFilter
{
    private static readonly FrozenSet<string> MutableToolNames = DiscoverMutableToolNames();

    public static IMcpServerBuilder WithEditSerializationFilter(this IMcpServerBuilder builder)
    {
        SemaphoreSlim editGate = new(1, 1);

        return builder.WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                string toolName = context.Params.Name;

                if (!MutableToolNames.Contains(toolName))
                {
                    return await next(context, cancellationToken);
                }

                await editGate.WaitAsync(cancellationToken);
                try
                {
                    return await next(context, cancellationToken);
                }
                finally
                {
                    editGate.Release();
                }
            });
        });
    }

    /// <summary>
    ///     Scans the current assembly for <see cref="McpServerToolAttribute" /> methods
    ///     that are NOT marked <c>ReadOnly = true</c>, returning their tool names.
    /// </summary>
    private static FrozenSet<string> DiscoverMutableToolNames()
    {
        return ToolAttributeDiscovery.GetToolMethods()
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attr => attr is { ReadOnly: false, Name: not null })
            .Select(attr => attr!.Name!)
            .ToFrozenSet(StringComparer.Ordinal);
    }
}
