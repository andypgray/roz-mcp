using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Zphil.Roz.Tests.Pipeline;

/// <summary>
///     Pins the ModelContextProtocol call-tool filter fold direction the concurrency design leans on:
///     the <em>first</em> filter registered on the builder is the <em>outermost</em> wrapper at request
///     time. <c>Program.cs</c> registers <c>WithEditSerializationFilter()</c> before
///     <c>WithGlobalCallToolFilter()</c>, so the edit gate wraps the external-edit reconcile — the
///     invariant the verified-write conflict-detection (B2) and shutdown-drain (N2) reasoning depends on:
///     within one edit, reconcile → compute → commit is serialized against every other edit by the outer
///     edit gate.
/// </summary>
/// <remarks>
///     The SDK folds the filter list back-to-front (verified by decompiling ModelContextProtocol.Core
///     1.4.0, <c>McpServer.BuildFilterPipeline</c>:
///     <c>for (i = filters.Count - 1; i &gt;= 0; i--) handler = filters[i](handler)</c>), so list index 0
///     becomes the outermost handler. This test registers two recording filters through the same
///     <c>AddCallToolFilter</c> surface <c>Program.cs</c> uses, reads back the real filter list the SDK
///     will fold, applies that documented fold, and asserts the first-registered body enters first. It
///     trips if <c>AddCallToolFilter</c> stops appending in call order; the fold loop below is kept in
///     lockstep with <c>BuildFilterPipeline</c> and must be re-verified against it on any SDK upgrade
///     (per the plan: if this framing breaks, re-evaluate B2/N2 before relying on the edit gate).
/// </remarks>
public sealed class CallToolFilterOrderTests
{
    [Fact]
    public async Task AddCallToolFilter_FirstRegistered_IsOutermostAtRequestTime()
    {
        // Arrange — two recording filters via the real SDK builder surface, in the relative order
        // Program.cs uses (edit-serialization first, global second).
        List<string> entryOrder = [];
        ServiceCollection services = new();
        services.AddMcpServer().WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(next => (request, ct) =>
            {
                entryOrder.Add("first-registered");
                return next(request, ct);
            });
            filters.AddCallToolFilter(next => (request, ct) =>
            {
                entryOrder.Add("second-registered");
                return next(request, ct);
            });
        });

        await using ServiceProvider provider = services.BuildServiceProvider();
        McpServerOptions options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
        IList<McpRequestFilter<CallToolRequestParams, CallToolResult>> filters =
            options.Filters.Request.CallToolFilters;
        filters.Count.ShouldBe(2);

        // Act — fold exactly as McpServer.BuildFilterPipeline does (back-to-front) so index 0 becomes
        // outermost, then invoke. The recording filters never touch the request, so null is safe.
        McpRequestHandler<CallToolRequestParams, CallToolResult> handler =
            (_, _) => ValueTask.FromResult(new CallToolResult());
        for (int i = filters.Count - 1; i >= 0; i--)
        {
            handler = filters[i](handler);
        }

        await handler(null!, CancellationToken.None);

        // Assert — the first-registered filter entered before the second: it is the outermost wrapper.
        entryOrder.ShouldBe(["first-registered", "second-registered"]);
    }
}
